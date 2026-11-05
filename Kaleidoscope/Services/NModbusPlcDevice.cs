using System;
using System.Net.Sockets;
using HslCommunication;
using NModbus;

namespace Kaleidoscope.Services
{
    // =====================================================================================
    // 通用 Modbus TCP PLC 设备（NModbusPlcDevice，宿主 V0.9.55 / 原版 V4.4.359~360 同步轮新增）
    // —— IKxPlcDevice 的 NModbus 实现，把第三方协议栈接进 PLC 队列的"底层库切换"落点。
    // -------------------------------------------------------------------------------------
    // 【为什么有本类】原 PLC 槽位只能装 HSL 家族设备（BuildPlcDevice 全 new HSL 对象），
    //   现场出现"非六品牌的纯 Modbus TCP 设备"（第三方控制器/远程 IO/仪表）时无从接入。
    //   本类用库内既有 NModbus 3.0.83（气密/震动盘同款依赖，零新增引用）实现队列设备
    //   接口 IKxPlcDevice：BuildPlcDevice 的 ModbusTcp 分支建本类，整条队列（心跳/握手/
    //   脚本同步读写/重连/热更）零改动直接可用——用户在 PLC 表协议下拉选 ModbusTcp，
    //   界面操作与其它品牌完全一致（同一张表、同一个心跳勾选+起始地址框），底层换库无感。
    // 【对位范式】连接管理照抄库内 ModbusTcpMasterClient（BeginConnect+WaitOne 手动超时
    //   防 IP 填错卡 20s；锁串行化；断连标记）——那是"上位机作主站"的独立客户端，本类是
    //   同一范式对 IKxPlcDevice 接口的适配实现。
    // -------------------------------------------------------------------------------------
    // 【地址约定】纯数字字符串，0 基（"0"=第一个保持寄存器，与 HSL ModbusTcpNet 同口径）：
    //   字读写（Int16/UInt16/Int32/UInt32/Int64/UInt64/Float/Double）→ 保持寄存器（FC03/06/16）；
    //   位读写（Bool）→ 线圈（FC01/05/15）。地址非法/越界按失败返回，不静默回 0。
    // 【字序】多字类型按 ABCD（大端字序+大端字节，与 HSL ModbusTcpNet 缺省 DataFormat 一致）：
    //   Int32 = regs[0] 高字 | regs[1] 低字；Float/Int64/UInt64/Double 同理逐字节大端。
    // 【从站号】构造传入（ini [PLC_ParameterN] PlcSlaveId，缺省 1），0~247，越界回 1。
    // 【线程】对象内部锁串行化连接与全部读写（队列消费线程单线程调用 + 启动并行建连
    //   各槽各实例无共享 + 重连链复用本对象 ConnectClose+ConnectServer，三路都在锁内排队）。
    // =====================================================================================
    public class NModbusPlcDevice : IKxPlcDevice
    {
        /// <summary>连接/读写互斥锁（本对象全部成员访问都过它，见类头【线程】）。</summary>
        private readonly object _lock = new object();

        private readonly string _ip;
        private readonly int _port;
        /// <summary>Modbus 从站地址（UnitId，0~247，构造时钳界）。</summary>
        private readonly byte _slaveId;

        private TcpClient _client;
        private IModbusMaster _master;
        /// <summary>连接态标记（读写前判它，断连即失败——重连由队列失败驱动链负责）。</summary>
        private bool _connected;

        /// <summary>建连/读写超时毫秒（对齐 HSL 侧 BuildPlcDevice 的 ConnectTimeOut=2000 口径）。</summary>
        private const int TimeoutMs = 2000;
        /// <summary>Modbus TCP 单帧读寄存器协议硬上限（与库 ReadBlockLengthMax=125 同源口径）。</summary>
        private const ushort FrameRegsMax = 125;

        public NModbusPlcDevice(string ip, int port, int slaveId)
        {
            _ip = (ip ?? "").Trim();
            _port = port;
            _slaveId = (slaveId >= 0 && slaveId <= 247) ? (byte)slaveId : (byte)1;
        }

        // ---------------- 连接生命周期 ----------------

        /// <summary>
        /// 建连（幂等：先关旧连再建新连）。TCP 用 BeginConnect+WaitOne 手动超时
        /// （TcpClient.Connect 同步阻塞不受 Timeout 约束，IP 填错默认等 ~20s，见
        /// ModbusTcpMasterClient.ConnectInternal 同款处理）；主站收发超时同 2 秒，
        /// 重试次数 0（同步读写外层有自己的超时与重连节奏，不重复重试拖帧）。
        /// </summary>
        public OperateResult ConnectServer()
        {
            try
            {
                lock (_lock)
                {
                    ConnectCloseCore();   // 幂等重进：残留半开连接先清
                    TcpClient client = new TcpClient();
                    client.SendTimeout = TimeoutMs;
                    client.ReceiveTimeout = TimeoutMs;
                    IAsyncResult ar = client.BeginConnect(_ip, _port, null, null);
                    if (ar == null || !ar.AsyncWaitHandle.WaitOne(TimeoutMs) || !client.Connected)
                    {
                        try { client.Close(); } catch { }
                        try { client.Dispose(); } catch { }
                        return new OperateResult("连接超时(" + TimeoutMs + "ms)：" + _ip + ":" + _port);
                    }
                    try { client.EndConnect(ar); }
                    catch (Exception ex)
                    {
                        try { client.Close(); } catch { }
                        try { client.Dispose(); } catch { }
                        return new OperateResult("连接失败：" + ex.Message);
                    }
                    IModbusMaster master = new ModbusFactory().CreateMaster(client);
                    master.Transport.ReadTimeout = TimeoutMs;
                    master.Transport.WriteTimeout = TimeoutMs;
                    master.Transport.Retries = 0;
                    _client = client;
                    _master = master;
                    _connected = true;
                    return OperateResult.CreateSuccessResult();
                }
            }
            catch (Exception ex) { return new OperateResult("连接异常：" + ex.Message); }
        }

        /// <summary>关连（幂等无害；HSL 同签名回 OperateResult，重连链复用本对象时先关后连）。</summary>
        public OperateResult ConnectClose()
        {
            try { lock (_lock) { ConnectCloseCore(); } }
            catch { }
            return OperateResult.CreateSuccessResult();
        }

        /// <summary>关连本体（锁内调；释放主站与 TCP，置断连标记）。</summary>
        private void ConnectCloseCore()
        {
            _connected = false;
            try { if (_master != null) _master.Dispose(); } catch { }
            try { if (_client != null) _client.Close(); } catch { }
            try { if (_client != null) _client.Dispose(); } catch { }
            _master = null;
            _client = null;
        }

        /// <summary>读写前置检查：已连接且主站在位才继续（锁内调）。失败给明确消息不静默。</summary>
        private bool EnsureConnected(out string why)
        {
            why = "";
            if (_connected && _master != null && _client != null && _client.Connected) return true;
            _connected = false;
            why = "未连接（等待自动重连）";
            return false;
        }

        // ---------------- 地址解析（纯数字 0 基，非法按失败处理） ----------------

        /// <summary>
        /// 地址解析：纯数字 0..65535 → ushort 偏移；杂串/空/越界回 false（调用方给
        /// "地址格式错误"失败——地址填错走重连链+日志，不静默回 0 误业务）。
        /// </summary>
        private static bool ParseAddr(string address, out ushort addr)
        {
            addr = 0;
            try
            {
                string t = (address ?? "").Trim();
                int v;
                if (t.Length == 0 || t.Length > 5 || !int.TryParse(t, out v)) return false;
                if (v < 0 || v > 65535) return false;
                addr = (ushort)v;
                return true;
            }
            catch { return false; }
        }

        // ---------------- 读（保持寄存器 FC03 / 线圈 FC01） ----------------

        public OperateResult<bool> ReadBool(string address)
        {
            ushort addr;
            if (!ParseAddr(address, out addr)) return Fail<bool>("地址格式错误（ModbusTCP 需纯数字 0~65535）：" + address);
            try
            {
                lock (_lock)
                {
                    string why;
                    if (!EnsureConnected(out why)) return Fail<bool>(why);
                    return OperateResult.CreateSuccessResult<bool>(_master.ReadCoils(_slaveId, addr, 1)[0]);
                }
            }
            catch (Exception ex) { MarkDown(); return Fail<bool>("读失败：" + ex.Message); }
        }

        public OperateResult<short> ReadInt16(string address)
        {
            ushort addr;
            if (!ParseAddr(address, out addr)) return Fail<short>("地址格式错误（ModbusTCP 需纯数字 0~65535）：" + address);
            try
            {
                lock (_lock)
                {
                    string why;
                    if (!EnsureConnected(out why)) return Fail<short>(why);
                    ushort[] regs = _master.ReadHoldingRegisters(_slaveId, addr, 1);
                    return OperateResult.CreateSuccessResult<short>((short)regs[0]);
                }
            }
            catch (Exception ex) { MarkDown(); return Fail<short>("读失败：" + ex.Message); }
        }

        public OperateResult<short[]> ReadInt16(string address, ushort length)
        {
            ushort addr;
            if (!ParseAddr(address, out addr)) return Fail<short[]>("地址格式错误（ModbusTCP 需纯数字 0~65535）：" + address);
            if (length < 1) length = 1;
            if (length > FrameRegsMax) length = FrameRegsMax;   // 协议硬上限再钳一道（调用方 125 口径同源）
            try
            {
                lock (_lock)
                {
                    string why;
                    if (!EnsureConnected(out why)) return Fail<short[]>(why);
                    ushort[] regs = _master.ReadHoldingRegisters(_slaveId, addr, length);
                    short[] vals = new short[regs.Length];
                    for (int i = 0; i < regs.Length; i++) vals[i] = (short)regs[i];
                    return OperateResult.CreateSuccessResult<short[]>(vals);
                }
            }
            catch (Exception ex) { MarkDown(); return Fail<short[]>("读失败：" + ex.Message); }
        }

        public OperateResult<ushort> ReadUInt16(string address)
        {
            ushort addr;
            if (!ParseAddr(address, out addr)) return Fail<ushort>("地址格式错误（ModbusTCP 需纯数字 0~65535）：" + address);
            try
            {
                lock (_lock)
                {
                    string why;
                    if (!EnsureConnected(out why)) return Fail<ushort>(why);
                    return OperateResult.CreateSuccessResult<ushort>(_master.ReadHoldingRegisters(_slaveId, addr, 1)[0]);
                }
            }
            catch (Exception ex) { MarkDown(); return Fail<ushort>("读失败：" + ex.Message); }
        }

        public OperateResult<int> ReadInt32(string address)
        {
            ushort addr;
            if (!ParseAddr(address, out addr)) return Fail<int>("地址格式错误（ModbusTCP 需纯数字 0~65535）：" + address);
            try
            {
                lock (_lock)
                {
                    string why;
                    if (!EnsureConnected(out why)) return Fail<int>(why);
                    ushort[] regs = _master.ReadHoldingRegisters(_slaveId, addr, 2);
                    return OperateResult.CreateSuccessResult<int>((int)Regs2ToU32(regs));
                }
            }
            catch (Exception ex) { MarkDown(); return Fail<int>("读失败：" + ex.Message); }
        }

        public OperateResult<uint> ReadUInt32(string address)
        {
            ushort addr;
            if (!ParseAddr(address, out addr)) return Fail<uint>("地址格式错误（ModbusTCP 需纯数字 0~65535）：" + address);
            try
            {
                lock (_lock)
                {
                    string why;
                    if (!EnsureConnected(out why)) return Fail<uint>(why);
                    ushort[] regs = _master.ReadHoldingRegisters(_slaveId, addr, 2);
                    return OperateResult.CreateSuccessResult<uint>(Regs2ToU32(regs));
                }
            }
            catch (Exception ex) { MarkDown(); return Fail<uint>("读失败：" + ex.Message); }
        }

        public OperateResult<long> ReadInt64(string address)
        {
            ushort addr;
            if (!ParseAddr(address, out addr)) return Fail<long>("地址格式错误（ModbusTCP 需纯数字 0~65535）：" + address);
            try
            {
                lock (_lock)
                {
                    string why;
                    if (!EnsureConnected(out why)) return Fail<long>(why);
                    ushort[] regs = _master.ReadHoldingRegisters(_slaveId, addr, 4);
                    return OperateResult.CreateSuccessResult<long>((long)Regs4ToU64(regs));
                }
            }
            catch (Exception ex) { MarkDown(); return Fail<long>("读失败：" + ex.Message); }
        }

        public OperateResult<ulong> ReadUInt64(string address)
        {
            ushort addr;
            if (!ParseAddr(address, out addr)) return Fail<ulong>("地址格式错误（ModbusTCP 需纯数字 0~65535）：" + address);
            try
            {
                lock (_lock)
                {
                    string why;
                    if (!EnsureConnected(out why)) return Fail<ulong>(why);
                    ushort[] regs = _master.ReadHoldingRegisters(_slaveId, addr, 4);
                    return OperateResult.CreateSuccessResult<ulong>(Regs4ToU64(regs));
                }
            }
            catch (Exception ex) { MarkDown(); return Fail<ulong>("读失败：" + ex.Message); }
        }

        public OperateResult<float> ReadFloat(string address)
        {
            ushort addr;
            if (!ParseAddr(address, out addr)) return Fail<float>("地址格式错误（ModbusTCP 需纯数字 0~65535）：" + address);
            try
            {
                lock (_lock)
                {
                    string why;
                    if (!EnsureConnected(out why)) return Fail<float>(why);
                    ushort[] regs = _master.ReadHoldingRegisters(_slaveId, addr, 2);
                    return OperateResult.CreateSuccessResult<float>(ToFloat(Regs2ToU32(regs)));
                }
            }
            catch (Exception ex) { MarkDown(); return Fail<float>("读失败：" + ex.Message); }
        }

        public OperateResult<double> ReadDouble(string address)
        {
            ushort addr;
            if (!ParseAddr(address, out addr)) return Fail<double>("地址格式错误（ModbusTCP 需纯数字 0~65535）：" + address);
            try
            {
                lock (_lock)
                {
                    string why;
                    if (!EnsureConnected(out why)) return Fail<double>(why);
                    ushort[] regs = _master.ReadHoldingRegisters(_slaveId, addr, 4);
                    return OperateResult.CreateSuccessResult<double>(ToDouble(Regs4ToU64(regs)));
                }
            }
            catch (Exception ex) { MarkDown(); return Fail<double>("读失败：" + ex.Message); }
        }

        // ---------------- 写（寄存器 FC06/16 / 线圈 FC05/15） ----------------

        public OperateResult Write(string address, bool[] value)
        {
            if (value == null || value.Length == 0) return new OperateResult("写数据为空");
            ushort addr;
            if (!ParseAddr(address, out addr)) return new OperateResult("地址格式错误（ModbusTCP 需纯数字 0~65535）：" + address);
            try
            {
                lock (_lock)
                {
                    string why;
                    if (!EnsureConnected(out why)) return new OperateResult(why);
                    if (value.Length == 1) _master.WriteSingleCoil(_slaveId, addr, value[0]);
                    else _master.WriteMultipleCoils(_slaveId, addr, value);
                    return OperateResult.CreateSuccessResult();
                }
            }
            catch (Exception ex) { MarkDown(); return new OperateResult("写失败：" + ex.Message); }
        }

        public OperateResult Write(string address, short[] value)
        {
            ushort addr;
            ushort[] regs;
            if (!PrepRegs(address, value, out addr, out regs)) return new OperateResult("参数/地址错误：" + address);
            try
            {
                lock (_lock)
                {
                    string why;
                    if (!EnsureConnected(out why)) return new OperateResult(why);
                    if (regs.Length == 1) _master.WriteSingleRegister(_slaveId, addr, regs[0]);
                    else _master.WriteMultipleRegisters(_slaveId, addr, regs);
                    return OperateResult.CreateSuccessResult();
                }
            }
            catch (Exception ex) { MarkDown(); return new OperateResult("写失败：" + ex.Message); }
        }

        public OperateResult Write(string address, ushort[] value)
        {
            ushort addr;
            if (value == null || value.Length == 0) return new OperateResult("写数据为空");
            if (!ParseAddr(address, out addr)) return new OperateResult("地址格式错误（ModbusTCP 需纯数字 0~65535）：" + address);
            try
            {
                lock (_lock)
                {
                    string why;
                    if (!EnsureConnected(out why)) return new OperateResult(why);
                    if (value.Length == 1) _master.WriteSingleRegister(_slaveId, addr, value[0]);
                    else _master.WriteMultipleRegisters(_slaveId, addr, value);
                    return OperateResult.CreateSuccessResult();
                }
            }
            catch (Exception ex) { MarkDown(); return new OperateResult("写失败：" + ex.Message); }
        }

        public OperateResult Write(string address, int[] value) { return WriteWords2(address, value); }
        public OperateResult Write(string address, uint[] value) { return WriteWords2(address, value); }
        public OperateResult Write(string address, float[] value) { return WriteWords2(address, value); }
        public OperateResult Write(string address, long[] value) { return WriteWords4(address, value); }
        public OperateResult Write(string address, ulong[] value) { return WriteWords4(address, value); }
        public OperateResult Write(string address, double[] value) { return WriteWords4(address, value); }

        // ---------------- 内部小工具 ----------------

        /// <summary>两个寄存器拼 32 位（ABCD：regs[0] 高字，对齐 HSL ModbusTcpNet 缺省字序）。</summary>
        private static uint Regs2ToU32(ushort[] regs)
        {
            return ((uint)regs[0] << 16) | regs[1];
        }

        /// <summary>四个寄存器拼 64 位（ABCDEFGH 逐字节大端）。</summary>
        private static ulong Regs4ToU64(ushort[] regs)
        {
            return ((ulong)regs[0] << 48) | ((ulong)regs[1] << 32) | ((ulong)regs[2] << 16) | regs[3];
        }

        /// <summary>32 位位型转 float（同一比特模式重解释）。</summary>
        private static float ToFloat(uint bits)
        {
            byte[] b = BitConverter.GetBytes(bits);
            return BitConverter.ToSingle(b, 0);
        }

        /// <summary>64 位位型转 double（同一比特模式重解释）。</summary>
        private static double ToDouble(ulong bits)
        {
            byte[] b = BitConverter.GetBytes(bits);
            return BitConverter.ToDouble(b, 0);
        }

        /// <summary>
        /// 64 位值拆 4 寄存器（ABCDEFGH 大端：高字在前）——BitConverter 小端字节序反转后拼。
        /// 长整/双精度写共用（int/uint/float 走 32 位版）。
        /// </summary>
        private static ushort[] U64ToRegs4(ulong v)
        {
            byte[] le = BitConverter.GetBytes(v);                 // 小端：le[0]=最低字节
            byte[] be = new byte[8];                              // 大端：be[0]=最高字节
            for (int i = 0; i < 8; i++) be[i] = le[7 - i];
            ushort[] regs = new ushort[4];
            for (int i = 0; i < 4; i++) regs[i] = (ushort)((be[2 * i] << 8) | be[2 * i + 1]);
            return regs;
        }

        /// <summary>32 位值拆 2 寄存器（ABCD：高字在前）。有符号整型先转同宽无符号再拆，比特模式不变。</summary>
        private static ushort[] U32ToRegs2(uint v)
        {
            return new ushort[] { (ushort)(v >> 16), (ushort)v };
        }

        /// <summary>写前统一准备：地址解析 + 值数组转寄存器序列（空值/非法地址 false）。</summary>
        private static bool PrepRegs(string address, short[] value, out ushort addr, out ushort[] regs)
        {
            addr = 0;
            regs = null;
            if (value == null || value.Length == 0) return false;
            if (!ParseAddr(address, out addr)) return false;
            regs = new ushort[value.Length];
            for (int i = 0; i < value.Length; i++) regs[i] = (ushort)value[i];
            return true;
        }

        /// <summary>2 寄存器/值 家族统一写（int/uint/float：值→32 位→拆高低字→连续寄存器）。</summary>
        private OperateResult WriteWords2(string address, System.Collections.IEnumerable values)
        {
            if (values == null) return new OperateResult("写数据为空");
            ushort addr;
            if (!ParseAddr(address, out addr)) return new OperateResult("地址格式错误（ModbusTCP 需纯数字 0~65535）：" + address);
            System.Collections.Generic.List<ushort> regs = new System.Collections.Generic.List<ushort>();
            foreach (object o in values)
            {
                if (o is int) regs.AddRange(U32ToRegs2((uint)(int)o));
                else if (o is uint) regs.AddRange(U32ToRegs2((uint)o));
                else if (o is float) regs.AddRange(U32ToRegs2(ToBits((float)o)));
                else return new OperateResult("写数据类型不符");
            }
            try
            {
                lock (_lock)
                {
                    string why;
                    if (!EnsureConnected(out why)) return new OperateResult(why);
                    if (regs.Count == 1) _master.WriteSingleRegister(_slaveId, addr, regs[0]);
                    else _master.WriteMultipleRegisters(_slaveId, addr, regs.ToArray());
                    return OperateResult.CreateSuccessResult();
                }
            }
            catch (Exception ex) { MarkDown(); return new OperateResult("写失败：" + ex.Message); }
        }

        /// <summary>4 寄存器/值 家族统一写（long/ulong/double：值→64 位→拆 4 字→连续寄存器）。</summary>
        private OperateResult WriteWords4(string address, System.Collections.IEnumerable values)
        {
            if (values == null) return new OperateResult("写数据为空");
            ushort addr;
            if (!ParseAddr(address, out addr)) return new OperateResult("地址格式错误（ModbusTCP 需纯数字 0~65535）：" + address);
            System.Collections.Generic.List<ushort> regs = new System.Collections.Generic.List<ushort>();
            foreach (object o in values)
            {
                if (o is long) regs.AddRange(U64ToRegs4((ulong)(long)o));
                else if (o is ulong) regs.AddRange(U64ToRegs4((ulong)o));
                else if (o is double) regs.AddRange(U64ToRegs4(ToBits((double)o)));
                else return new OperateResult("写数据类型不符");
            }
            try
            {
                lock (_lock)
                {
                    string why;
                    if (!EnsureConnected(out why)) return new OperateResult(why);
                    if (regs.Count == 1) _master.WriteSingleRegister(_slaveId, addr, regs[0]);
                    else _master.WriteMultipleRegisters(_slaveId, addr, regs.ToArray());
                    return OperateResult.CreateSuccessResult();
                }
            }
            catch (Exception ex) { MarkDown(); return new OperateResult("写失败：" + ex.Message); }
        }

        /// <summary>float→同宽 32 位位型（比特重解释）。</summary>
        private static uint ToBits(float v) { return BitConverter.ToUInt32(BitConverter.GetBytes(v), 0); }
        /// <summary>double→同宽 64 位位型（比特重解释）。</summary>
        private static ulong ToBits(double v) { return BitConverter.ToUInt64(BitConverter.GetBytes(v), 0); }

        /// <summary>读写异常即断连标记（下轮读写先失败，队列失败驱动链自动进重连）。</summary>
        private void MarkDown() { _connected = false; }

        /// <summary>失败结果统一组包（Content 给类型缺省值——值类型 0/引用类型空，队列侧 .ToString() 安全）。</summary>
        private static OperateResult<T> Fail<T>(string msg)
        {
            return OperateResult.CreateFailedResult<T>(new OperateResult(msg));
        }
    }
}
