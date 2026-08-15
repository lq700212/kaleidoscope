using System;
using System.Net.Sockets;
using Kaleidoscope.Models;
using NModbus;

namespace Kaleidoscope.Services
{
    /// <summary>
    /// IO 控制器通讯实现（Modbus TCP）。
    /// 【来源】AgingTestSystem.Services.ModbusTcpIoController 原样移植，配置类型换成独立强类型
    /// <see cref="IoConfig"/>。新增了原始寄存器读/写两个方法（ReadHoldingRegisters/WriteSingleRegister）。
    ///
    /// 适用场景：
    /// - GX-CL140 或类似的 Modbus TCP IO 耦合器
    /// - 上位机作为 Modbus TCP Client（主站），周期性读取 DI/DO，并按业务写 DO
    ///
    /// 对新手的关键说明：
    /// 1) Modbus TCP 是"请求-响应"模型：设备不会主动推送变化，上位机必须轮询读取。
    /// 2) 这里把 DI/DO 视为"16 点打包成 1 个寄存器"的常见实现：
    ///    - 第 1~16 点 → 第 1 个寄存器的 bit0~bit15；第 17~32 点 → 第 2 个寄存器的 bit0~bit15
    /// 3) 寄存器区与位序已依据现场 ModbusTCPTest 实测结果固化（GX-CL140）：
    ///    - DI（输入）：起始 0x1000，读 Input Registers（功能码 0x04），位序从右往左第 1 位为第 1 路
    ///    - DO（输出）：起始 0x2000，读 Holding Registers（功能码 0x03）、写单寄存器（功能码 0x06），位序同 DI
    ///    - 现场 5 个 DQ50P-S（每个 32 路）对应 10 个寄存器：0x2000~0x2009
    /// 4) 单点输出采用"读-改-写"方式修改某一位，不误伤同寄存器其它通道。
    ///
    /// 【断连判定（V1.16.1 沉淀）】读/写请求抛出"连接层异常"（Socket 异常/IO 异常/超时）说明耦合器已断开
    /// （Modbus 异常响应不算断开——那说明设备在线、只是报功能码错误）。一旦判定断开就把 _isConnected 置 false，
    /// 让 DeviceHub 的连接监控能感知并自动重连，状态灯如实显示"未连接"。
    /// </summary>
    public class ModbusTcpIoController : IIoController
    {
        /// <summary>TCP/主站对象的互斥锁：采集线程与 UI 线程可能同时访问 _master，统一串行化。</summary>
        private readonly object _syncRoot = new object();

        /// <summary>全局配置（Connect 时赋值）。</summary>
        private IoConfig _config;

        /// <summary>TCP 客户端（负责网络连接）。</summary>
        private TcpClient _client;

        /// <summary>Modbus 主站（负责组包/解包、发起请求）。</summary>
        private IModbusMaster _master;

        /// <summary>连接状态。volatile：ConnectionMonitor 心跳线程在锁外读 IsConnected 决定是否重连。</summary>
        private volatile bool _isConnected;

        public bool IsConnected => _isConnected;

        public event EventHandler<string> OnError;

        /// <summary>
        /// 连接层错误判定：读/写请求抛出"连接层异常"（Socket 异常/IO 异常/超时）说明耦合器已断开，
        /// 把 _isConnected 置 false 并释放坏连接（Modbus 异常响应不算断开）。
        /// 【断连即释放】这里顺手 Close 坏 TcpClient，不必等 Monitor 下一轮重连（≤5s）才关——
        /// 坏 socket 多挂几秒会让后续读请求白等一个超时。本方法在 _syncRoot 锁内被调用（各读写方法
        /// 的 catch 都在锁外完成 Modbus 调用后进入），释放与读写串行化，无并发风险。
        /// </summary>
        private void MarkDisconnectedOnFailure(Exception ex)
        {
            if (ex is SocketException || ex is System.IO.IOException || ex is TimeoutException)
            {
                _isConnected = false;
                try { if (_client != null) _client.Close(); } catch { }
                try { _client?.Dispose(); } catch { }
                _client = null;
                _master = null;
            }
        }

        /// <summary>连接 IO 设备（断开旧连 → BeginConnect 手动超时 → 建 Modbus 主站）。</summary>
        public bool Connect(IoConfig config)
        {
            _config = config;
            // 用 _syncRoot 串行化"连接/断开/读写"，防止后台自动重连与采集线程并发访问 _client/_master
            // 导致空引用（C# lock 可重入，内部再调 Disconnect 不冲突）。
            lock (_syncRoot)
            {
                return ConnectInternal();
            }
        }

        /// <summary>连接的实际执行部分（必须在 _syncRoot 锁内调用）。</summary>
        private bool ConnectInternal()
        {
            try
            {
                // 0) 先断开旧连接（如果之前连接过）
                Disconnect();

                // 1) 建立 TCP 连接（带手动超时）：
                //    【修复】TcpClient.Connect 是同步阻塞的，且不受 SendTimeout/ReceiveTimeout 约束，
                //    IP 填错时系统默认要等约 20 秒才超时——会卡住界面。这里用 BeginConnect + WaitOne
                //    实现"手动超时"：TcpReceiveTimeoutMs 内连不上立即放弃并给出明确提示。
                _client = new TcpClient();
                _client.SendTimeout = _config.TcpSendTimeoutMs;
                _client.ReceiveTimeout = _config.TcpReceiveTimeoutMs;

                IAsyncResult connectResult = _client.BeginConnect(_config.PlcAddress, _config.PlcPort, null, null);
                if (!connectResult.AsyncWaitHandle.WaitOne(_config.TcpReceiveTimeoutMs))
                {
                    _client.Close();
                    _client.Dispose();
                    _client = null;
                    OnError?.Invoke(this,
                        $"耦合器 {_config.PlcAddress}:{_config.PlcPort} 连接超时（{_config.TcpReceiveTimeoutMs}ms），请检查 IP/网线");
                    return false;
                }
                _client.EndConnect(connectResult);

                // 2) 创建 Modbus 主站（Master）
                var factory = new ModbusFactory();
                _master = factory.CreateMaster(_client);
                _master.Transport.ReadTimeout = _config.TcpReceiveTimeoutMs;
                _master.Transport.WriteTimeout = _config.TcpSendTimeoutMs;

                // 3) 标记连接成功
                _isConnected = true;
                return true;
            }
            catch (Exception ex)
            {
                // Connect 的设计约定：不向外抛异常，统一用 OnError 通知上层
                OnError?.Invoke(this, ex.Message);
                Disconnect();
                return false;
            }
        }

        /// <summary>断开连接（释放 TCP 客户端与主站；锁串行化与 Connect/读写一致）。</summary>
        public void Disconnect()
        {
            lock (_syncRoot)
            {
                _isConnected = false;
                try
                {
                    if (_client != null)
                    {
                        _client.Close();
                    }
                }
                catch
                {
                }
                finally
                {
                    _client = null;
                    _master = null;
                }
            }
        }

        /// <summary>读取单个输入点状态。</summary>
        public bool ReadInput(int inputId)
        {
            if (!_isConnected || _config == null || _master == null)
            {
                OnError?.Invoke(this, "设备未连接");
                return false;
            }

            if (inputId < 1 || inputId > _config.TotalInputs)
            {
                OnError?.Invoke(this, $"无效的输入点编号: {inputId}");
                return false;
            }

            try
            {
                // inputId 是"内部连续编号"（1~TotalInputs），先换算为 0 基下标 bitIndex，
                // 再计算寄存器地址与位序（每 16 路一个寄存器）。
                int bitIndex = inputId - 1;
                ushort regAddress = (ushort)(_config.IoInputRegisterStartAddress + (bitIndex / 16));
                int bit = bitIndex % 16;

                ushort value;
                lock (_syncRoot)
                {
                    // 读取 1 个输入寄存器（Input Register，功能码 0x04）
                    ushort[] regs = _master.ReadInputRegisters(_config.IoUnitId, regAddress, 1);
                    if (regs == null || regs.Length < 1) return false;
                    value = regs[0];
                }

                // 位运算取某一位：(1 << bit) 生成掩码；value & mask != 0 表示该 bit 为 1。
                // 【已现场确认】bit0 对应"第 1 路输入"，bit15 对应"第 16 路输入"。
                // InvertInputs 兼容少数现场"低有效/高有效"逻辑与寄存器 bit 值不一致：
                // false：bit=1 认为输入 ON（默认）；true：逻辑取反（bit=0 当成 ON）。
                bool rawState = (value & (1 << bit)) != 0;
                return _config.InvertInputs ? !rawState : rawState;
            }
            catch (Exception ex)
            {
                // 连接层异常（超时/断网）→ 标记断开，让上层感知并自动重连
                MarkDisconnectedOnFailure(ex);
                OnError?.Invoke(this, ex.Message);
                return false;
            }
        }

        /// <summary>批量读取所有输入点状态（一次批量读寄存器，减少网络往返）。</summary>
        public bool[] ReadAllInputs()
        {
            if (!_isConnected || _config == null || _master == null)
            {
                OnError?.Invoke(this, "设备未连接");
                return new bool[0];
            }

            try
            {
                // 例如：80 路输入 → 需要 5 个寄存器（(80+15)/16 = 5）
                int regCount = (_config.TotalInputs + 15) / 16;
                ushort[] regs;
                lock (_syncRoot)
                {
                    regs = _master.ReadInputRegisters(_config.IoUnitId, _config.IoInputRegisterStartAddress, (ushort)regCount);
                }

                if (regs == null) return new bool[0];

                var result = new bool[_config.TotalInputs];
                for (int i = 0; i < _config.TotalInputs; i++)
                {
                    // i 是 0 基通道下标：regIndex=落在哪个寄存器；bit=寄存器内 bit 位
                    int regIndex = i / 16;
                    int bit = i % 16;
                    if (regIndex >= regs.Length) break;
                    bool rawState = (regs[regIndex] & (1 << bit)) != 0;
                    result[i] = _config.InvertInputs ? !rawState : rawState;
                }

                return result;
            }
            catch (Exception ex)
            {
                MarkDisconnectedOnFailure(ex);
                OnError?.Invoke(this, ex.Message);
                return new bool[0];
            }
        }

        /// <summary>
        /// 【备用通道映射】把物理通道 (regAddress, bit) 重定向到备用通道。
        /// 现场某个 DQ 通道烧毁/电压不足后，把该通道信号改写到备用通道。业务侧输出点编号
        /// （outputId）完全不变，只是把"物理寄存器+bit"换了位置。总开关关闭时原样返回。
        /// </summary>
        /// <param name="regAddress">输入源寄存器地址；输出映射后的寄存器地址（可能被改写）</param>
        /// <param name="bit">输入源通道号（0~15）；输出映射后的通道号（可能被改写）</param>
        private void MapOutputChannel(ref ushort regAddress, ref int bit)
        {
            if (!_config.IoBackupChannelMappingEnabled || _config.IoBackupChannelMappings == null)
                return;

            foreach (var remap in _config.IoBackupChannelMappings)
            {
                if (remap.SourceRegister == regAddress && remap.SourceChannel == bit)
                {
                    regAddress = remap.TargetRegister;
                    bit = remap.TargetChannel;
                    return; // 一个源通道只会被映射一次
                }
            }
        }

        /// <summary>写入单个输出点状态（读-改-写只改 1 bit，不误伤同寄存器其它通道）。</summary>
        public void WriteOutput(int outputId, bool state)
        {
            if (!_isConnected || _config == null || _master == null)
            {
                OnError?.Invoke(this, "设备未连接");
                return;
            }

            // outputId 是"内部连续编号"（默认 73~216）；outputStart = TotalInputs + 1
            int outputStart = _config.TotalInputs + 1;
            int outputEnd = _config.TotalInputs + _config.TotalOutputs;

            if (outputId < outputStart || outputId > outputEnd)
            {
                OnError?.Invoke(this, $"无效的输出点编号: {outputId}（合法范围 {outputStart}-{outputEnd}）");
                return;
            }

            try
            {
                bool outputState = _config.InvertOutputs ? !state : state;
                int bitIndex = outputId - outputStart;
                ushort regAddress = (ushort)(_config.IoOutputRegisterStartAddress + (bitIndex / 16));
                int bit = bitIndex % 16;

                // 【备用通道映射】烧毁通道 → 备用通道（开关关闭时原样不动）
                MapOutputChannel(ref regAddress, ref bit);

                lock (_syncRoot)
                {
                    // 读-改-写：先把所在寄存器的 16bit 状态读出来，再只修改其中 1 个 bit，
                    // 避免误伤同一个寄存器里其它通道的状态。
                    ushort[] currentRegs = _master.ReadHoldingRegisters(_config.IoUnitId, regAddress, 1);
                    ushort current = (currentRegs != null && currentRegs.Length > 0) ? currentRegs[0] : (ushort)0;

                    ushort mask = (ushort)(1 << bit);
                    ushort newValue = outputState ? (ushort)(current | mask) : (ushort)(current & ~mask);

                    // 写单寄存器（功能码 0x06），写入的 16bit 中只有一个 bit 被改变。
                    // 【已现场确认】GX-CL140 + DQ50P-S：DO 区域可用 Holding Register 写入（0x06）控制通道。
                    _master.WriteSingleRegister(_config.IoUnitId, regAddress, newValue);
                }
            }
            catch (Exception ex)
            {
                MarkDisconnectedOnFailure(ex);
                OnError?.Invoke(this, ex.Message);
            }
        }

        /// <summary>批量写入输出点状态（逐台调用 WriteOutput）。</summary>
        public void WriteOutputs(int[] outputIds, bool[] states)
        {
            if (!_isConnected)
            {
                OnError?.Invoke(this, "设备未连接");
                return;
            }

            if (outputIds == null || states == null)
            {
                OnError?.Invoke(this, "参数不能为空");
                return;
            }

            if (outputIds.Length != states.Length)
            {
                OnError?.Invoke(this, "输出点编号和状态数量不一致");
                return;
            }

            for (int i = 0; i < outputIds.Length; i++)
            {
                WriteOutput(outputIds[i], states[i]);
            }
        }

        /// <summary>读取单个输出点状态（用于回读确认）。</summary>
        public bool ReadOutput(int outputId)
        {
            if (!_isConnected || _config == null || _master == null)
            {
                OnError?.Invoke(this, "设备未连接");
                return false;
            }

            int outputStart = _config.TotalInputs + 1;
            int outputEnd = _config.TotalInputs + _config.TotalOutputs;

            if (outputId < outputStart || outputId > outputEnd)
            {
                OnError?.Invoke(this, $"无效的输出点编号: {outputId}（合法范围 {outputStart}-{outputEnd}）");
                return false;
            }

            try
            {
                int bitIndex = outputId - outputStart;
                ushort regAddress = (ushort)(_config.IoOutputRegisterStartAddress + (bitIndex / 16));
                int bit = bitIndex % 16;

                // 【备用通道映射】读取也要跟随映射后的物理位置，否则读回的是烧毁通道的旧值/空值。
                MapOutputChannel(ref regAddress, ref bit);

                ushort value;
                lock (_syncRoot)
                {
                    ushort[] regs = _master.ReadHoldingRegisters(_config.IoUnitId, regAddress, 1);
                    if (regs == null || regs.Length < 1) return false;
                    value = regs[0];
                }

                bool rawState = (value & (1 << bit)) != 0;
                return _config.InvertOutputs ? !rawState : rawState;
            }
            catch (Exception ex)
            {
                MarkDisconnectedOnFailure(ex);
                OnError?.Invoke(this, ex.Message);
                return false;
            }
        }

        /// <summary>批量读取所有输出点状态（一次读到全部物理通道，含备用通道映射目标）。</summary>
        public bool[] ReadAllOutputs()
        {
            if (!_isConnected || _config == null || _master == null)
            {
                OnError?.Invoke(this, "设备未连接");
                return new bool[0];
            }

            try
            {
                // 例如：160 路输出 → 需要 10 个寄存器（(160+15)/16 = 10）
                int regCount = (_config.TotalOutputs + 15) / 16;

                // 【备用通道映射】映射目标可能落在业务输出范围之外（如 0x2009 只用于备用），
                // 把批量读取范围扩到能覆盖所有映射目标，保证一次读到全部物理通道的真实状态。
                if (_config.IoBackupChannelMappingEnabled && _config.IoBackupChannelMappings != null)
                {
                    foreach (var remap in _config.IoBackupChannelMappings)
                    {
                        int need = (remap.TargetRegister - _config.IoOutputRegisterStartAddress) + 1;
                        if (need > regCount) regCount = need;
                    }
                }

                ushort[] regs;
                lock (_syncRoot)
                {
                    regs = _master.ReadHoldingRegisters(_config.IoUnitId, _config.IoOutputRegisterStartAddress, (ushort)regCount);
                }

                if (regs == null) return new bool[0];

                var result = new bool[_config.TotalOutputs];
                for (int i = 0; i < _config.TotalOutputs; i++)
                {
                    int regIndex = i / 16;
                    int bit = i % 16;
                    ushort regAddress = (ushort)(_config.IoOutputRegisterStartAddress + regIndex);

                    // 【备用通道映射】按通道重定向后，从已读到的块里取目标寄存器的对应位
                    MapOutputChannel(ref regAddress, ref bit);
                    int mappedRegIndex = regAddress - _config.IoOutputRegisterStartAddress;
                    if (mappedRegIndex < 0 || mappedRegIndex >= regs.Length) break;

                    bool rawState = (regs[mappedRegIndex] & (1 << bit)) != 0;
                    result[i] = _config.InvertOutputs ? !rawState : rawState;
                }

                return result;
            }
            catch (Exception ex)
            {
                MarkDisconnectedOnFailure(ex);
                OnError?.Invoke(this, ex.Message);
                return new bool[0];
            }
        }

        /// <summary>
        /// 读取连续多个保持寄存器（功能码 0x03）—— 共享连接上的"原始寄存器读"。
        /// 供业务通讯测试复用主程序**同一条** Modbus TCP 连接读写 DO 原始寄存器（0x2000~0x2009），
        /// 不再自建第二条连接。内部用 _syncRoot 串行化，多线程并发安全。
        /// </summary>
        /// <param name="startAddress">起始寄存器地址</param>
        /// <param name="count">寄存器数量（1~125）</param>
        /// <returns>寄存器值数组；未连接/异常返回 null</returns>
        public ushort[] ReadHoldingRegisters(ushort startAddress, ushort count)
        {
            if (!_isConnected || _config == null || _master == null)
            {
                OnError?.Invoke(this, "设备未连接");
                return null;
            }

            try
            {
                lock (_syncRoot)
                {
                    return _master.ReadHoldingRegisters(_config.IoUnitId, startAddress, count);
                }
            }
            catch (Exception ex)
            {
                MarkDisconnectedOnFailure(ex);
                OnError?.Invoke(this, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 写单个保持寄存器（功能码 0x06）—— 共享连接上的"原始寄存器写"。
        /// 供业务通讯测试复用主程序同一条连接写 DO 原始寄存器（含读-改-写的最终结果）。
        /// </summary>
        /// <param name="address">寄存器地址</param>
        /// <param name="value">要写入的值</param>
        /// <returns>true=写成功；false=未连接/异常</returns>
        public bool WriteSingleRegister(ushort address, ushort value)
        {
            if (!_isConnected || _config == null || _master == null)
            {
                OnError?.Invoke(this, "设备未连接");
                return false;
            }

            try
            {
                lock (_syncRoot)
                {
                    _master.WriteSingleRegister(_config.IoUnitId, address, value);
                }
                return true;
            }
            catch (Exception ex)
            {
                MarkDisconnectedOnFailure(ex);
                OnError?.Invoke(this, ex.Message);
                return false;
            }
        }

        /// <summary>释放资源（断开连接）。</summary>
        public void Dispose()
        {
            Disconnect();
        }
    }
}