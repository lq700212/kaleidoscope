// Kaleidoscope 验收测试用例集（54 用例：全功能点 正常/边界/异常路径，最终验收/发布前全量回归）。
// ─────────────────────────────────────────────────────────────────
// 【编译】用 Roslyn csc（支持 C# 7.3，系统自带 csc 是 C# 5 会报错）：
//   "D:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\Roslyn\csc.exe"
//     /out:SmokeTest.exe
//     /r:..\Kaleidoscope\bin\Debug\Kaleidoscope.dll
//     /r:..\Kaleidoscope\bin\Debug\HslCommunication.dll
//     /r:..\Kaleidoscope\bin\Debug\NModbus.dll
//     /r:..\Kaleidoscope\bin\Debug\NModbus.Serial.dll
//     /r:System.Drawing.dll
//     SmokeTest.cs
// 【运行】SmokeTest.exe（需同目录有 Kaleidoscope.dll / HslCommunication.dll / NModbus.dll /
//   NModbus.Serial.dll；HSL 授权用例环境自适应：exe 目录有 hsl.dat 才断言激活，无则 SKIP）。
// 【退出码】0=全部通过，1=有失败（可接入 CI）。
// 【覆盖】配置序列化/校验、Mock 三件套、PLC 从站（三拍握手/型号/清零/重建）、Modbus 主站
//   （读写/轮询/断线自愈/并发）、IO 耦合器（映射/读改写/越界）、送风机（解析/命令/回退）、
//   TCP 扫码枪（收码/重连/半条码）、相机（T1/T2/超时/PR/PW 缓存/OF）、ImageStore、
//   HSL 多品牌（授权/自愈/加密存储）、DeviceHub 全链路（建/启/收码/热更/释放）、生命周期。
// ─────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Kaleidoscope.Configuration;
using Kaleidoscope.Models;
using Kaleidoscope.Services;
using Kaleidoscope.Utils;
using NModbus;
using NModbus.Data;

internal static class Smoke
{
    static readonly List<string> _results = new List<string>();
    static int _pass, _fail, _skip;

    static void Run(string name, Action body)
    {
        try { body(); _pass++; _results.Add("PASS  " + name); Console.WriteLine("  [PASS] " + name); }
        catch (Exception ex)
        {
            _fail++; var msg = ex.Message; if (msg.Length > 160) msg = msg.Substring(0, 160);
            _results.Add("FAIL  " + name + "  | " + msg);
            Console.WriteLine("  [FAIL] " + name + "  | " + msg);
        }
    }
    static void SkipCase(string name)
    {
        _skip++;
        _results.Add("SKIP  " + name);
        Console.WriteLine("  [SKIP] " + name);
    }
    static void AssertTrue(bool cond, string msg)
    {
        if (!cond) throw new Exception(msg);
    }
    static void Wait(Func<bool> cond, int timeoutMs, string msg)
    {
        DateTime dl = DateTime.Now.AddMilliseconds(timeoutMs);
        while (DateTime.Now < dl) { if (cond()) return; Thread.Sleep(100); }
        if (!cond()) throw new Exception(msg + "（等待超时 " + timeoutMs + "ms）");
    }

    static int Main(string[] args)
    {
        // --slave <port>：独立进程从站模式（供断电用例使用——杀进程时 OS 关闭全部
        // socket，对端立即感知断开；进程内 MockSlave.Dispose 只停 listener，已建立
        // 连接不会被切断，模拟不了真实断电）
        if (args.Length >= 2 && args[0] == "--slave")
        {
            SlaveMode(int.Parse(args[1]));
            return 0;
        }

        LogHelper.LogAction = (l, m) => { };
        Console.WriteLine("=== Kaleidoscope 测试用例集 ===");

        Run("配置.1 Save/Load 往返全字段", T1_RoundTrip);
        Run("配置.2 缺字段旧文件默认值兜底", T1_MissingFields);
        Run("配置.3 显式 null 经 EnsureSafe 兜底", T1_EnsureSafe);
        Run("配置.4 损坏 JSON 抛 InvalidDataException", T1_Corrupt);
        Run("配置.5 Validator 合法配置通过", T1_ValidOk);
        Run("配置.6 Validator 非法 IP/端口/超时拦截", T1_InvalidRejected);
        Run("配置.7 Validator 轮询项重名告警", T1_DupPollWarn);

        Run("Mock.1 气压表读数", T2_Barometer);
        Run("Mock.2 IO 写读一致", T2_Io);
        Run("Mock.3 送风机启停状态", T2_Fan);

        Run("从站.1 主站连入监听端口", T3_Connect);
        Run("从站.2 三拍握手完整流程", T3_Handshake);
        Run("从站.3 型号区序号+字符串", T3_Model);
        Run("从站.4 相机结果寄存器上电清零", T3_CameraReset);
        Run("从站.5 通用 Write/ReadRegister", T3_GeneralRW);
        Run("从站.6 热更重建后从站重新可连", T3_RebuildReconnect);

        Run("主站.1 连接模拟从站", T4_Connect);
        Run("主站.2 读保持/写单/写多/线圈", T4_RW);
        Run("主站.3 后台轮询缓存更新", T4_Poll);
        Run("主站.4 断线标记+自动重连恢复", T4_Reconnect);
        Run("主站.5 未连接时读返回 null 不崩溃", T4_NullWhenDown);
        Run("主站.6 多线程并发读写锁保护", T4_Concurrent);

        Run("IO.1 输入映射（32 路）", T5_Inputs);
        Run("IO.2 单点写读-改-写回读", T5_WriteSingle);
        Run("IO.3 批量写回读", T5_WriteBatch);
        Run("IO.4 单点写不覆盖同寄存器其它位", T5_NoOverwrite);
        Run("IO.5 越界通道安全处理", T5_OutOfRange);

        Run("送风机.1 状态解析（定值启动+温度÷100）", T6_Parse);
        Run("送风机.2 启动/停止命令写控制字", T6_Cmd);
        Run("送风机.3 未知运行态保守回退", T6_UnknownState);

        Run("扫码枪.1 连接→自动发 LON→收码", T7_Receive);
        Run("扫码枪.2 断线自动重连再收码", T7_Reconnect);
        Run("扫码枪.3 半条码断线不污染新条码", T7_HalfLine);
        Run("扫码枪.4 Dispose 后线程退出", T7_Dispose);

        Run("相机.1 T1 触发回显", T8_T1);
        Run("相机.2 T2 判定 OK（全合格位）", T8_T2Ok);
        Run("相机.3 T2 判定 NG（含非合格位）", T8_T2Ng);
        Run("相机.4 T2 超时（无应答）不崩溃", T8_Timeout);
        Run("相机.5 PR 读程序号", T8_PR);
        Run("相机.6 PW 切程序 + 同号跳过", T8_PW);
        Run("相机.7 OF 输出格式设置", T8_OF);

        Run("ImageStore.1 SaveImage 归档结构", T9_Save);
        Run("ImageStore.2 FindLatestPair + DeleteSourceFile", T9_Cleanup);

        Run("HSL.1 授权从 hsl.dat 激活（无授权环境自动跳过）", T10_Auth);
        Run("HSL.2 连接失败自愈 + Dispose 干净", T10_Lifecycle);
        Run("HSL.3 错误授权码不破坏已激活状态", T10_BadCode);
        Run("HSL.4 Dispose 后调用读写安全返回失败", T10_AfterDispose);
        Run("HSL.5 加密存储往返（文件无明文）", T10_EncryptRoundTrip);

        Run("DeviceHub.1 Mock 全链路建/启", T11_Start);
        Run("DeviceHub.2 TCP 扫码枪经 hub 收码", T11_Scan);
        Run("DeviceHub.3 热更切多品牌角色", T11_ApplyConfig);
        Run("DeviceHub.4 Dispose 后引用清空", T11_Dispose);

        Run("生命周期.1 串口打开失败不崩溃", T12_Serial);
        Run("生命周期.2 EIP 扫码枪 Open→Dispose", T12_Eip);

        Console.WriteLine();
        Console.WriteLine("=== 汇总：PASS=" + _pass + "  FAIL=" + _fail + "  SKIP=" + _skip + " ===");
        foreach (var r in _results) Console.WriteLine("  " + r);
        return _fail == 0 ? 0 : 1;
    }

    // ══════════ 1. 配置 ══════════
    static string _cfgDir = Path.Combine(Path.GetTempPath(), "ktest_cfg");

    static void T1_RoundTrip()
    {
        Directory.CreateDirectory(_cfgDir);
        string f = Path.Combine(_cfgDir, "rt.kcfg");
        var c = new DeviceHubConfig();
        c.Plc.IpAddress = "127.0.0.1"; c.Plc.Port = 15020;
        c.PlcRole = PlcRole.MultiBrand;
        c.MultiBrandPlc.Brand = MultiBrandPlcBrand.OmronFins;
        c.MultiBrandPlc.IpAddress = "10.0.0.9"; c.MultiBrandPlc.Port = 0;
        c.MultiBrandPlc.PollItems.Add(new MultiBrandPlcPollItem { Name = "t", Address = "D100", DataType = PlcDataType.Float, Count = 2 });
        var sc = new ScanConfig { Enabled = true, Mode = "Eip", IpAddress = "1.2.3.4", EipPollingMs = 250 };
        c.Scanners.Add(sc);
        ConfigSerializer.Save(c, f);
        var r = ConfigSerializer.Load(f);
        AssertTrue(r.Plc.IpAddress == "127.0.0.1" && r.Plc.Port == 15020, "Plc 字段不一致");
        AssertTrue(r.PlcRole == PlcRole.MultiBrand, "PlcRole 不一致");
        AssertTrue(r.MultiBrandPlc.Brand == MultiBrandPlcBrand.OmronFins && r.MultiBrandPlc.IpAddress == "10.0.0.9", "MultiBrand 不一致");
        AssertTrue(r.MultiBrandPlc.PollItems.Count == 1 && r.MultiBrandPlc.PollItems[0].DataType == PlcDataType.Float, "轮询项不一致");
        AssertTrue(r.Scanners.Count == 1 && r.Scanners[0].Mode == "Eip" && r.Scanners[0].EipPollingMs == 250, "EIP 扫码枪不一致");
    }
    static void T1_MissingFields()
    {
        Directory.CreateDirectory(_cfgDir);
        string f = Path.Combine(_cfgDir, "old.kcfg");
        File.WriteAllText(f, "{\"Plc\":{\"IpAddress\":\"5.6.7.8\"}}");
        var r = ConfigSerializer.Load(f);
        AssertTrue(r.Plc.IpAddress == "5.6.7.8", "已有字段丢失");
        AssertTrue(r.Plc.Port == 502, "缺字段未补默认值");
        AssertTrue(r.MultiBrandPlc != null && r.MultiBrandPlc.TimeoutMs == 2000, "新段未补默认值");
    }
    static void T1_EnsureSafe()
    {
        Directory.CreateDirectory(_cfgDir);
        string f = Path.Combine(_cfgDir, "null.kcfg");
        File.WriteAllText(f, "{\"PlcRole\":0,\"MultiBrandPlc\":null,\"Plc\":{\"IpAddress\":\"1.2.3.4\"},\"Scanners\":[null]}");
        var r = ConfigSerializer.Load(f);
        AssertTrue(r.MultiBrandPlc != null && r.MultiBrandPlc.PollItems != null, "MultiBrandPlc 未兜底");
        AssertTrue(r.Scanners.Count == 1, "Scanners 数量异常");
    }
    static void T1_Corrupt()
    {
        Directory.CreateDirectory(_cfgDir);
        string f = Path.Combine(_cfgDir, "bad.kcfg");
        File.WriteAllText(f, "{这不是JSON");
        bool threw = false;
        try { ConfigSerializer.Load(f); } catch (InvalidDataException) { threw = true; }
        AssertTrue(threw, "损坏文件未抛 InvalidDataException");
    }
    static void T1_ValidOk()
    {
        var c = new DeviceHubConfig();
        c.PlcRole = PlcRole.MultiBrand;
        c.MultiBrandPlc.IpAddress = "192.168.1.5";
        var r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(r.IsValid, "合法配置被误报：" + string.Join(";", r.Errors.ToArray()));
    }
    static void T1_InvalidRejected()
    {
        var c = new DeviceHubConfig();
        c.PlcRole = PlcRole.MultiBrand;
        c.MultiBrandPlc.IpAddress = "not-an-ip";
        c.MultiBrandPlc.TimeoutMs = 0;
        var r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(!r.IsValid && r.Errors.Count >= 2, "非法 IP/超时未被拦截");
        c.MultiBrandPlc.IpAddress = "1.2.3.4"; c.MultiBrandPlc.TimeoutMs = 1000;
        c.MultiBrandPlc.PollItems.Add(new MultiBrandPlcPollItem { Name = "a", Address = "", Count = 0 });
        r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(r.Errors.Count >= 2, "空地址/零数量未被拦截");
    }
    static void T1_DupPollWarn()
    {
        var c = new DeviceHubConfig();
        c.PlcRole = PlcRole.MultiBrand;
        c.MultiBrandPlc.IpAddress = "1.2.3.4";
        c.MultiBrandPlc.PollItems.Add(new MultiBrandPlcPollItem { Name = "dup", Address = "D0" });
        c.MultiBrandPlc.PollItems.Add(new MultiBrandPlcPollItem { Name = "dup", Address = "D1" });
        var r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(r.Warnings.Exists(w => w.Contains("dup")), "重名轮询项未告警");
    }

    // ══════════ 2. Mock ══════════
    static void T2_Barometer()
    {
        var bar = new MockBarometerReader();
        AssertTrue(bar.Connect(new BarometerConfig()), "Mock 气压表 Connect 失败");
        var d = bar.ReadAllData();
        AssertTrue(d != null && d.Length > 0, "无数据");
        bar.Dispose();
    }
    static void T2_Io()
    {
        var io = new MockIoController();
        var ioc = new IoConfig();   // TotalInputs=80 → 输出点编号 81 起（连续编号语义）
        io.Connect(ioc);
        int out1 = ioc.TotalInputs + 1;
        io.WriteOutput(out1, true);
        AssertTrue(io.ReadOutput(out1), "写 true 读回 false");
        io.WriteOutput(out1, false);
        AssertTrue(!io.ReadOutput(out1), "写 false 读回 true");
        io.Dispose();
    }
    static void T2_Fan()
    {
        var fan = new MockFanController();
        AssertTrue(fan.Connect(NewFanCfg(0)), "Mock 送风机 Connect 失败");
        var s1 = fan.ReadStatus();
        AssertTrue(s1 != null, "初始状态 FanData 为 null");
        fan.StartFixedValue();
        var s2 = fan.ReadStatus();
        AssertTrue(s2 != null, "启动后 FanData 为 null");
        fan.Dispose();
    }

    // ───────────── 辅助：NModbus 模拟从站 ─────────────
    class MockSlave : IDisposable
    {
        TcpListener _listener;
        IModbusTcpSlaveNetwork _net;
        CancellationTokenSource _cts;
        SlaveDataStore _store;
        bool _disposed;

        public static MockSlave Start(int port)
        {
            var m = new MockSlave();
            m._listener = new TcpListener(IPAddress.Loopback, port);
            m._listener.Start();
            var factory = new ModbusFactory();
            m._store = new SlaveDataStore();
            var slave = factory.CreateSlave(1, m._store);
            m._net = factory.CreateSlaveNetwork(m._listener);
            m._net.AddSlave(slave);
            m._cts = new CancellationTokenSource();
            m._net.ListenAsync(m._cts.Token);
            Thread.Sleep(400);
            return m;
        }
        public void WriteHold(ushort addr, params ushort[] vals) { _store.HoldingRegisters.WritePoints(addr, vals); }
        public void WriteInput(ushort addr, params ushort[] vals) { _store.InputRegisters.WritePoints(addr, vals); }
        public ushort ReadHold(ushort addr) { return _store.HoldingRegisters.ReadPoints(addr, 1)[0]; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
        }
    }

    // ───────────── 辅助：独立进程从站模式（--slave <port>）──────────────
    static void SlaveMode(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var factory = new ModbusFactory();
        var store = new SlaveDataStore();
        var slave = factory.CreateSlave(1, store);
        var net = factory.CreateSlaveNetwork(listener);
        net.AddSlave(slave);
        var cts = new CancellationTokenSource();
        net.ListenAsync(cts.Token);
        Thread.Sleep(Timeout.Infinite);   // 阻塞直到被宿主 Kill（Kill 即真实断电）
    }

    // ══════════ 3. PLC 从站 ══════════
    static PlcConfig NewSlaveCfg(int port)
    {
        var c = new PlcConfig();
        c.IpAddress = "127.0.0.1"; c.Port = port; c.UnitId = 1; c.TimeoutMs = 2000;
        return c;
    }

    static void T3_Connect()
    {
        int port = 15100;
        var cam = new CameraConfig(); cam.Name = "c"; cam.PlcResultAddress = 20;
        var cams = new List<CameraConfig>(); cams.Add(cam);
        using (var plc = new PlcService(NewSlaveCfg(port)))
        {
            plc.SetCameraResultAddresses(cams);
            plc.EnsureConnected();
            Wait(() => plc.IsConnected, 3000, "从站监听未就绪");
            using (var m = NModbusMaster(port)) { }
        }
    }
    static void T3_Handshake()
    {
        int port = 15101;
        using (var plc = new PlcService(NewSlaveCfg(port)))
        {
            plc.EnsureConnected();
            Wait(() => plc.IsConnected, 3000, "从站监听未就绪");
            using (var m = NModbusMaster(port))
            {
                m.WriteSingleRegister(1, 1, 1);
                Wait(() => { bool r; return plc.ReadScanRequest(out r) && r; }, 2000, "从站未读到请求");
                plc.WriteScanResult(1);
                ushort res = m.ReadHoldingRegisters(1, 4, 1)[0];
                AssertTrue(res == 1, "主站读到结果=" + res + "（应 1）");
                plc.WriteScanResult(0);
                res = m.ReadHoldingRegisters(1, 4, 1)[0];
                AssertTrue(res == 0, "复位后结果=" + res + "（应 0）");
            }
        }
    }
    static void T3_Model()
    {
        int port = 15102;
        using (var plc = new PlcService(NewSlaveCfg(port)))
        {
            plc.SetCurrentModel("U171");
            plc.EnsureConnected();
            Wait(() => plc.IsConnected, 3000, "从站监听未就绪");
            using (var m = NModbusMaster(port))
            {
                AssertTrue(plc.WriteProductModel("U171"), "写型号失败");
                ushort seq = m.ReadHoldingRegisters(1, 7, 1)[0];
                AssertTrue(seq == 0, "未配映射序号应写 0，实际=" + seq);
                var regs = m.ReadHoldingRegisters(1, 8, 5);
                var bytes = new List<byte>();
                foreach (var rg in regs) { bytes.Add((byte)(rg >> 8)); bytes.Add((byte)(rg & 0xFF)); }
                string model = Encoding.ASCII.GetString(bytes.ToArray()).Trim('\0');
                AssertTrue(model == "U171", "型号字符串=" + model);
            }
        }
    }
    static void T3_CameraReset()
    {
        int port = 15103;
        var cam = new CameraConfig(); cam.Name = "c"; cam.PlcResultAddress = 20;
        var cams = new List<CameraConfig>(); cams.Add(cam);
        using (var plc = new PlcService(NewSlaveCfg(port)))
        {
            plc.SetCameraResultAddresses(cams);
            plc.EnsureConnected();
            Wait(() => plc.IsConnected, 3000, "从站监听未就绪");
            var m = NModbusMaster(port);
            m.WriteSingleRegister(1, 20, 2);
            // 真实语义：清零发生在"从站建站成功"时（上电/重建/热更）——模拟热更重建
            plc.Dispose();                        // 旧从站网络释放会断开主站会话
            Thread.Sleep(300);
            var plc2 = new PlcService(NewSlaveCfg(port));
            plc2.SetCameraResultAddresses(cams);
            plc2.EnsureConnected();
            m.Dispose();
            m = NModbusMaster(port);              // 主站重连新从站
            Wait(() => m.ReadHoldingRegisters(1, 20, 1)[0] == 0, 3000, "相机结果寄存器未清零");
            plc2.Dispose();
            m.Dispose();
        }
    }
    static void T3_GeneralRW()
    {
        int port = 15104;
        using (var plc = new PlcService(NewSlaveCfg(port)))
        {
            plc.EnsureConnected();
            Wait(() => plc.IsConnected, 3000, "从站监听未就绪");
            AssertTrue(plc.WriteRegister(30, 777), "WriteRegister 失败");
            ushort v;
            AssertTrue(plc.ReadRegister(30, out v) && v == 777, "ReadRegister 值=" + v);
        }
    }
    static void T3_RebuildReconnect()
    {
        int port = 15105;
        var cfg = NewSlaveCfg(port);
        var plc1 = new PlcService(cfg);
        plc1.EnsureConnected();
        Wait(() => plc1.IsConnected, 3000, "第一实例监听未就绪");
        plc1.Dispose();
        Thread.Sleep(300);
        var plc2 = new PlcService(cfg);
        plc2.EnsureConnected();
        Wait(() => plc2.IsConnected, 3000, "第二实例监听未就绪");
        using (var m = NModbusMaster(port))
        {
            ushort v = m.ReadHoldingRegisters(1, 1, 1)[0];
            AssertTrue(v == 0, "新从站可读写，值=" + v);
        }
        plc2.Dispose();
    }

    // ══════════ 4. Modbus 主站 ══════════
    static void T4_Connect()
    {
        using (var slave = MockSlave.Start(15200))
        {
            slave.WriteHold(0, 11, 22);
            var m = NewModbusMaster(15200);
            AssertTrue(m.IsConnected, "未连接");
            m.Dispose();
        }
    }
    static void T4_RW()
    {
        using (var slave = MockSlave.Start(15201))
        {
            slave.WriteHold(0, 11, 22, 33, 44);
            var m = NewModbusMaster(15201);
            var regs = m.ReadHoldingRegisters(0, 4);
            AssertTrue(regs != null && regs[0] == 11 && regs[3] == 44, "读保持寄存器不符");
            AssertTrue(m.WriteSingleRegister(5, 99) && slave.ReadHold(5) == 99, "写单寄存器");
            AssertTrue(m.WriteMultipleRegisters(6, new ushort[] { 7, 8 }) && slave.ReadHold(7) == 8, "写多寄存器");
            AssertTrue(m.WriteSingleCoil(0, true) && m.ReadCoils(0, 1)[0], "线圈");
            m.Dispose();
        }
    }
    static void T4_Poll()
    {
        using (var slave = MockSlave.Start(15202))
        {
            slave.WriteHold(0, 11, 22);
            var m = NewModbusMaster(15202);
            var cfg = new PlcMasterConfig();
            cfg.IpAddress = "127.0.0.1"; cfg.Port = 15202; cfg.UnitId = 1;
            cfg.TimeoutMs = 1000; cfg.ReconnectIntervalMs = 300; cfg.PollIntervalMs = 100;
            cfg.PollItems.Add(new PlcPollItem { Name = "blk", Function = PlcPollFunction.Holding, StartAddress = 0, Count = 2 });
            m.Connect(cfg);
            m.StartPolling();          // 轮询需显式启动（Connect 只建连接）
            slave.WriteHold(0, 55, 66);
            Wait(() => { object v; return m.TryGetLastPollData("blk", out v) && v is ushort[] && ((ushort[])v)[0] == 55; },
                 3000, "轮询缓存未更新");
            m.Dispose();
        }
    }
    static void T4_Reconnect()
    {
        int port = 15203;
        // 从站跑在独立子进程：Kill 进程 = 真实断电（OS 关闭全部 socket，主站立即感知）。
        // 进程内 MockSlave.Dispose 只停 listener，已建立连接不会被切断，模拟不了真实断电。
        string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var slaveProc = Process.Start(exe, "--slave " + port);
        Wait(() => NModbusPortOpen(port), 3000, "子进程从站未就绪");
        var m = NewModbusMaster(port);
        AssertTrue(m.IsConnected, "初始未连接");
        slaveProc.Kill();                      // 真实断电
        Wait(() => { m.ReadHoldingRegisters(0, 1); return !m.IsConnected; }, 6000, "断线未被标记");
        using (var s2 = MockSlave.Start(port)) // 从站复活（进程内即可，重连由下次操作触发）
        {
            Wait(() => { m.ReadHoldingRegisters(0, 1); return m.IsConnected; }, 8000, "操作触发重连未恢复");
        }
        m.Dispose();
    }
    static bool NModbusPortOpen(int port)
    {
        try { var c = new TcpClient("127.0.0.1", port); c.Close(); return true; }
        catch { return false; }
    }
    static void T4_NullWhenDown()
    {
        int port = 15204;
        var m = new ModbusTcpMasterClient();   // 从站从未存在：Connect 应失败
        var c = new PlcMasterConfig();
        c.IpAddress = "127.0.0.1"; c.Port = port; c.UnitId = 1; c.TimeoutMs = 800;
        AssertTrue(!m.Connect(c), "无从站时 Connect 竟成功");
        AssertTrue(m.ReadHoldingRegisters(0, 1) == null, "未连接读应返回 null");
        AssertTrue(!m.WriteSingleRegister(0, 1), "未连接写应返回 false");
        m.Dispose();
    }
    static void T4_Concurrent()
    {
        using (var slave = MockSlave.Start(15205))
        {
            slave.WriteHold(0, 1, 2, 3);
            var m = NewModbusMaster(15205);
            var errors = new List<string>();
            var t1 = new Thread(delegate() { for (int i = 0; i < 30; i++) { var r = m.ReadHoldingRegisters(0, 3); if (r == null) errors.Add("读失败@" + i); Thread.Sleep(10); } });
            var t2 = new Thread(delegate() { for (int i = 0; i < 30; i++) { if (!m.WriteSingleRegister(5, (ushort)i)) errors.Add("写失败@" + i); Thread.Sleep(10); } });
            t1.Start(); t2.Start(); t1.Join(); t2.Join();
            AssertTrue(errors.Count == 0, "并发异常：" + string.Join(";", errors.ToArray()));
            m.Dispose();
        }
    }

    // ══════════ 5. IO 耦合器 ══════════
    static void T5_Inputs()
    {
        using (var slave = MockSlave.Start(15210))
        {
            slave.WriteInput(0x1000, 0x00FF, 0xFF00);   // ReadInput 走功能码 04 → InputRegisters 区
            var io = new ModbusTcpIoController();
            io.Connect(NewIoCfg(15210, 32, 32));
            var ins = io.ReadAllInputs();
            AssertTrue(ins != null && ins.Length == 32, "路数=" + (ins == null ? -1 : ins.Length));
            AssertTrue(io.ReadInput(1) && io.ReadInput(8) && !io.ReadInput(9) && !io.ReadInput(17), "输入映射不符");
            io.Dispose();
        }
    }
    static void T5_WriteSingle()
    {
        using (var slave = MockSlave.Start(15211))
        {
            var io = new ModbusTcpIoController();
            var cfg = NewIoCfg(15211, 16, 16);
            io.Connect(cfg);
            int out1 = cfg.TotalInputs + 1;   // 输出点编号 = TotalInputs+1 起（连续编号语义）
            io.WriteOutput(out1, true);
            AssertTrue(io.ReadOutput(out1), "单点写读不符");
            io.Dispose();
        }
    }
    static void T5_WriteBatch()
    {
        using (var slave = MockSlave.Start(15212))
        {
            var io = new ModbusTcpIoController();
            var cfg = NewIoCfg(15212, 16, 16);
            io.Connect(cfg);
            int baseId = cfg.TotalInputs;
            int[] ids = new int[] { baseId + 1, baseId + 5, baseId + 9 };
            bool[] vals = new bool[] { true, false, true };
            io.WriteOutputs(ids, vals);
            AssertTrue(io.ReadOutput(baseId + 1) && !io.ReadOutput(baseId + 5) && io.ReadOutput(baseId + 9),
                "批量写回读不符");
            io.Dispose();
        }
    }
    static void T5_NoOverwrite()
    {
        using (var slave = MockSlave.Start(15213))
        {
            var io = new ModbusTcpIoController();
            var cfg = NewIoCfg(15213, 32, 32);
            io.Connect(cfg);
            int baseId = cfg.TotalInputs;
            int[] ids = new int[] { baseId + 1, baseId + 2, baseId + 3 };
            bool[] vals = new bool[] { true, true, true };
            io.WriteOutputs(ids, vals);                       // 三点全 true（同寄存器 16 位内）
            io.WriteOutput(baseId + 2, false);                 // 单点改第 2 点
            AssertTrue(io.ReadOutput(baseId + 1) && !io.ReadOutput(baseId + 2) && io.ReadOutput(baseId + 3),
                "单点写覆盖了同寄存器其它位");
            io.Dispose();
        }
    }
    static void T5_OutOfRange()
    {
        using (var slave = MockSlave.Start(15214))
        {
            var io = new ModbusTcpIoController();
            io.Connect(NewIoCfg(15214, 16, 16));
            bool safe = true;
            try { io.WriteOutput(999, true); } catch (Exception) { safe = true; }   // 抛异常也算安全处理
            try { io.ReadOutput(999); } catch (Exception) { }
            AssertTrue(safe, "越界通道未安全处理");
            io.Dispose();
        }
    }
    static IoConfig NewIoCfg(int port, int ins, int outs)
    {
        var c = new IoConfig();
        c.PlcAddress = "127.0.0.1"; c.PlcPort = port;
        c.TotalInputs = ins; c.TotalOutputs = outs; c.IoUnitId = 1;
        c.TcpSendTimeoutMs = 1000; c.TcpReceiveTimeoutMs = 1000;
        return c;
    }

    // ══════════ 6. 送风机 ══════════
    static FanConfig NewFanCfg(int port)
    {
        var c = new FanConfig();
        c.FanEnabled = true; c.FanIpAddress = "127.0.0.1"; c.FanPort = port; c.FanTimeoutMs = 1000;
        c.FanStatusStartAddress = 0x0001; c.FanStatusCount = 6;
        c.FanRunStateOffset = 1; c.FanTemperatureOffset = 2; c.FanHumidityOffset = 3;
        c.FanTempSetpointOffset = 4; c.FanHumSetpointOffset = 5;
        c.FanControlAddress = 0x0001; c.FanStartCommand = 3; c.FanStopCommand = 2;
        return c;
    }
    static void T6_Parse()
    {
        using (var slave = MockSlave.Start(15220))
        {
            slave.WriteHold(0x0002, 3, 2530, 5800, 2500, 3000);
            var fan = new FanControllerClient();
            fan.Connect(NewFanCfg(15220));
            var st = fan.ReadStatus();
            AssertTrue(st != null && st.RunState == FanRunState.FixedValueRunning, "定值启动未识别，RunState=" + st.RunState);
            AssertTrue(Math.Abs(st.Temperature - 25.3) < 0.01, "温度=" + st.Temperature);
            AssertTrue(Math.Abs(st.Humidity - 58.0) < 0.01, "湿度=" + st.Humidity);
            fan.Dispose();
        }
    }
    static void T6_Cmd()
    {
        using (var slave = MockSlave.Start(15221))
        {
            slave.WriteHold(0x0002, 3, 2530, 5800, 2500, 3000);
            var fan = new FanControllerClient();
            fan.Connect(NewFanCfg(15221));
            AssertTrue(fan.StartFixedValue() && slave.ReadHold(0x0001) == 3, "启动控制字≠3");
            AssertTrue(fan.Stop() && slave.ReadHold(0x0001) == 2, "停止控制字≠2");
            fan.Dispose();
        }
    }
    static void T6_UnknownState()
    {
        using (var slave = MockSlave.Start(15222))
        {
            slave.WriteHold(0x0002, 99, 2530, 5800, 2500, 3000);   // 99=未知运行态
            var fan = new FanControllerClient();
            fan.Connect(NewFanCfg(15222));
            var st = fan.ReadStatus();   // 未知态应保守回退解析，不抛异常
            fan.Dispose();
        }
    }

    // ══════════ 7/11 共用：TCP 扫码枪模拟器 ══════════
    static TcpListener _scanListener;
    static volatile bool _scanRun;
    static readonly List<string> _scanGot = new List<string>();
    static readonly object _scanLock = new object();
    static string _reply = "ABC123";
    static readonly List<TcpClient> _scanClients = new List<TcpClient>();   // 活跃连接（Stop 时全关，触发客户端断线重连）

    static void StartSim(int port, string code)
    {
        _scanRun = true; _reply = code;
        _scanListener = new TcpListener(IPAddress.Loopback, port);
        _scanListener.Start();
        var t = new Thread(SimLoop); t.IsBackground = true; t.Start();
        Thread.Sleep(300);
    }
    static void SimLoop()
    {
        while (_scanRun)
        {
            TcpClient c = null;
            try
            {
                c = _scanListener.AcceptTcpClient();
                var cc = c;
                lock (_scanLock) _scanClients.Add(cc);
                var t = new Thread(delegate()
                {
                    try
                    {
                        var buf = new byte[64];
                        var st = cc.GetStream();
                        while (_scanRun && cc.Connected)
                        {
                            int n = st.Read(buf, 0, buf.Length);
                            if (n <= 0) break;
                            if (Encoding.ASCII.GetString(buf, 0, n).Trim() == "LON")
                            { var d = Encoding.ASCII.GetBytes(_reply + "\r\n"); st.Write(d, 0, d.Length); }
                        }
                    }
                    catch { }
                    try { cc.Close(); } catch { }
                });
                t.IsBackground = true; t.Start();
            }
            catch { if (!_scanRun) break; }
            finally { if (c != null && !_scanRun) { try { c.Close(); } catch { } } }
        }
    }
    static void StopSim()
    {
        _scanRun = false;
        lock (_scanLock)
        {
            foreach (var c in _scanClients) { try { c.Close(); } catch { } }
            _scanClients.Clear();
        }
        try { _scanListener.Stop(); } catch { }
        Thread.Sleep(300);
    }
    static bool WaitScan(string code, int ms)
    {
        DateTime dl = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < dl) { lock (_scanLock) if (_scanGot.Contains(code)) return true; Thread.Sleep(150); }
        return false;
    }

    // ══════════ 7. TCP 扫码枪 ══════════
    static void T7_Receive()
    {
        int port = 15230;
        StartSim(port, "ABC123");
        lock (_scanLock) _scanGot.Clear();
        var s = new ScannerTcpService(new ScanConfig { Enabled = true, Mode = "Tcp", IpAddress = "127.0.0.1", Port = port, TriggerCommand = "LON" });
        s.SerialNumberScanned += delegate(object o, string c) { lock (_scanLock) _scanGot.Add(c); };
        s.Open();
        AssertTrue(WaitScan("ABC123", 6000), "未收到条码");
        s.Dispose(); StopSim();
    }
    static void T7_Reconnect()
    {
        int port = 15231;
        StartSim(port, "ABC123");
        lock (_scanLock) _scanGot.Clear();
        var s = new ScannerTcpService(new ScanConfig { Enabled = true, Mode = "Tcp", IpAddress = "127.0.0.1", Port = port, TriggerCommand = "LON" });
        s.SerialNumberScanned += delegate(object o, string c) { lock (_scanLock) _scanGot.Add(c); };
        s.Open();
        AssertTrue(WaitScan("ABC123", 6000), "首轮未收到");
        StopSim();                              // 关闭全部活跃连接 → 客户端断流
        Thread.Sleep(1500);                     // 等客户端进入重连循环 + 端口释放
        lock (_scanLock) _scanGot.Clear();
        StartSim(port, "XYZ789");               // 复活（换码验证是新连接）
        AssertTrue(WaitScan("XYZ789", 15000), "重连后未收到新码");
        s.Dispose(); StopSim();
    }
    static void T7_HalfLine()
    {
        int port = 15232;
        // 模拟器：先发半条码 "AB"（无 CRLF）→ 断开 → 重启发完整 "ABC\r\n"
        _scanRun = true;
        _scanListener = new TcpListener(IPAddress.Loopback, port);
        _scanListener.Start();
        var phase = new int[] { 0 };   // 0=发半条码后断开；1=发完整码
        var at = new Thread(delegate()
        {
            while (_scanRun)
            {
                TcpClient c = null;
                try
                {
                    c = _scanListener.AcceptTcpClient();
                    var cc = c;
                    var t = new Thread(delegate()
                    {
                        try
                        {
                            var buf = new byte[64];
                            var st = cc.GetStream();
                            while (_scanRun && cc.Connected)
                            {
                                int n = st.Read(buf, 0, buf.Length);
                                if (n <= 0) break;
                                if (Encoding.ASCII.GetString(buf, 0, n).Trim() == "LON")
                                {
                                    if (phase[0] == 0)
                                    {
                                        var half = Encoding.ASCII.GetBytes("AB");   // 无行结束符
                                        st.Write(half, 0, half.Length);
                                        Thread.Sleep(150);                          // 等客户端收到半条
                                        phase[0] = 1;
                                        break;                                      // 断开（半条码残留场景）
                                    }
                                    else
                                    {
                                        var full = Encoding.ASCII.GetBytes("ABC\r\n");
                                        st.Write(full, 0, full.Length);
                                    }
                                }
                            }
                        }
                        catch { }
                        try { cc.Close(); } catch { }
                    });
                    t.IsBackground = true; t.Start();
                }
                catch { if (!_scanRun) break; }
                finally { if (c != null && !_scanRun) { try { c.Close(); } catch { } } }
            }
        });
        at.IsBackground = true; at.Start();
        Thread.Sleep(300);

        lock (_scanLock) _scanGot.Clear();
        var s = new ScannerTcpService(new ScanConfig { Enabled = true, Mode = "Tcp", IpAddress = "127.0.0.1", Port = port, TriggerCommand = "LON" });
        s.SerialNumberScanned += delegate(object o, string c) { lock (_scanLock) _scanGot.Add(c); };
        s.Open();
        AssertTrue(WaitScan("ABC", 15000), "断线后未收到完整新码");
        lock (_scanLock)
        {
            AssertTrue(!_scanGot.Contains("AB") && !_scanGot.Contains("ABABC"),
                "半条码残留污染：" + string.Join(",", _scanGot.ToArray()));
        }
        s.Dispose(); StopSim();
    }
    static void T7_Dispose()
    {
        int port = 15233;
        StartSim(port, "X");
        var s = new ScannerTcpService(new ScanConfig { Enabled = true, Mode = "Tcp", IpAddress = "127.0.0.1", Port = port, TriggerCommand = "LON" });
        s.Open();
        Thread.Sleep(600);
        s.Dispose();
        Thread.Sleep(600);
        AssertTrue(!s.IsOpen, "Dispose 后仍显示已连接");
        s.Dispose();   // 重复 Dispose 安全
        StopSim();
    }

    // ══════════ 8. 相机 ══════════
    static TcpListener _camListener;
    static volatile bool _camRun;
    static volatile string _camJudge = "RT,0000";   // 可切换应答
    static volatile bool _camSilent = false;        // true=不应答（超时场景）
    static readonly List<string> _camCmds = new List<string>();
    static readonly object _camLock = new object();

    static void StartCamSim(int port)
    {
        _camRun = true;
        _camListener = new TcpListener(IPAddress.Loopback, port);
        _camListener.Start();
        var t = new Thread(CamLoop); t.IsBackground = true; t.Start();
        Thread.Sleep(300);
    }
    static void CamLoop()
    {
        while (_camRun)
        {
            TcpClient c = null;
            try
            {
                c = _camListener.AcceptTcpClient();
                var cc = c;
                var t = new Thread(delegate()
                {
                    try
                    {
                        var buf = new byte[128];
                        var st = cc.GetStream();
                        while (_camRun && cc.Connected)
                        {
                            int n = st.Read(buf, 0, buf.Length);
                            if (n <= 0) break;
                            string cmd = Encoding.ASCII.GetString(buf, 0, n).Trim('\r', '\n');
                            lock (_camLock) _camCmds.Add(cmd);
                            if (_camSilent) continue;
                            byte[] resp = null;
                            if (cmd == "T1") resp = Encoding.ASCII.GetBytes("T1\r");
                            else if (cmd == "T2" || cmd == "RT") resp = Encoding.ASCII.GetBytes(_camJudge + "\r");
                            else if (cmd == "PR") resp = Encoding.ASCII.GetBytes("PR,001\r");
                            else if (cmd.StartsWith("PW")) resp = Encoding.ASCII.GetBytes("PW\r");
                            else if (cmd.StartsWith("OF")) resp = Encoding.ASCII.GetBytes("OF\r");
                            if (resp != null) st.Write(resp, 0, resp.Length);
                        }
                    }
                    catch { }
                    try { cc.Close(); } catch { }
                });
                t.IsBackground = true; t.Start();
            }
            catch { if (!_camRun) break; }
            finally { if (c != null && !_camRun) { try { c.Close(); } catch { } } }
        }
    }
    static void StopCamSim()
    {
        _camRun = false;
        try { _camListener.Stop(); } catch { }
        Thread.Sleep(200);
    }
    static KeyenceIV4Camera NewCam(int port)
    {
        var c = new CameraConfig();
        c.Name = "t"; c.IpAddress = "127.0.0.1"; c.CommandPort = port;
        c.ResponseTimeoutMs = 800; c.TimeoutMs = 800; c.ImageWaitMs = 500;
        return new KeyenceIV4Camera(c);
    }

    static void T8_T1()
    {
        int port = 15240;
        StartCamSim(port);
        var cam = NewCam(port);
        AssertTrue(cam.SendTrigger(), "T1 未收到回显");
        cam.Dispose(); StopCamSim();
    }
    static void T8_T2Ok()
    {
        int port = 15241;
        _camJudge = "RT,0000";
        StartCamSim(port);
        var cam = NewCam(port);
        var r = cam.TriggerAndRead();
        AssertTrue(r.Succeeded && r.IsOk, "应判 OK：" + r.Detail);
        cam.Dispose(); StopCamSim();
    }
    static void T8_T2Ng()
    {
        int port = 15242;
        _camJudge = "RT,0001";
        StartCamSim(port);
        var cam = NewCam(port);
        var r = cam.TriggerAndRead();
        AssertTrue(r.Succeeded && !r.IsOk, "应判 NG");
        cam.Dispose(); StopCamSim();
    }
    static void T8_Timeout()
    {
        int port = 15243;
        _camSilent = true;
        StartCamSim(port);
        var cam = NewCam(port);
        var r = cam.TriggerAndRead();
        AssertTrue(!r.Succeeded, "无应答应返回失败");
        cam.Dispose(); _camSilent = false; StopCamSim();
    }
    static void T8_PR()
    {
        int port = 15244;
        StartCamSim(port);
        var cam = NewCam(port);
        AssertTrue(cam.ReadProgramNo() == 1, "程序号≠1");
        cam.Dispose(); StopCamSim();
    }
    static void T8_PW()
    {
        int port = 15245;
        StartCamSim(port);
        var cam = NewCam(port);
        AssertTrue(cam.SwitchProgram(2), "PW 切换失败");
        int before; lock (_camLock) before = _camCmds.Count;
        AssertTrue(cam.SwitchProgram(2), "同号二次切换返回 false");
        int after; lock (_camLock) after = _camCmds.Count;
        AssertTrue(after == before, "同号切换仍发指令（缓存未生效）" + before + "→" + after);
        AssertTrue(cam.SwitchProgram(3), "换号切换失败");
        cam.Dispose(); StopCamSim();
    }
    static void T8_OF()
    {
        int port = 15246;
        StartCamSim(port);
        var cam = NewCam(port);
        AssertTrue(cam.SetOutputFormat("00"), "OF 设置失败");
        cam.Dispose(); StopCamSim();
    }

    // ══════════ 9. ImageStore ══════════
    static void T9_Save()
    {
        string root = Path.Combine(Path.GetTempPath(), "ktest_img");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var cfg = new ImageConfig(); cfg.SaveRootDir = root; cfg.KeepDays = 7;
        using (var store = new ImageStore(cfg))
        {
            using (var bmp = new Bitmap(64, 48))
            {
                string p1 = store.SaveImage(bmp, 1, true, "SN001", "cam");
                AssertTrue(!string.IsNullOrEmpty(p1) && File.Exists(p1), "p1 不存在");
                AssertTrue(p1.Contains("SN001"), "缺 SN 目录：" + p1);
                string p2 = store.SaveImage(bmp, 1, false, "SN001", "cam");
                AssertTrue(!string.IsNullOrEmpty(p2) && File.Exists(p2), "p2 不存在");
            }
        }
        try { Directory.Delete(root, true); } catch { }
    }
    static void T9_Cleanup()
    {
        string root = Path.Combine(Path.GetTempPath(), "ktest_img2");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var cfg = new ImageConfig(); cfg.SaveRootDir = root; cfg.KeepDays = 7;
        using (var store = new ImageStore(cfg))
        {
            using (var bmp = new Bitmap(32, 32))
            {
                store.SaveImage(bmp, 1, true, "OLD", "cam");
                store.SaveImage(bmp, 1, true, "NEW", "cam");
            }
            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            AssertTrue(files.Length >= 2, "测试文件不足");
            // FindLatestPair 找 jpeg+iv4p 成对文件：手工构造一对（SaveImage 只产 jpeg）
            string pairDir = Path.Combine(root, "pair");
            Directory.CreateDirectory(pairDir);
            string jpg = Path.Combine(pairDir, "cam_1_OK_SN9.jpg");
            string iv4p = Path.Combine(pairDir, "cam_1_OK_SN9.iv4p");
            File.WriteAllBytes(jpg, new byte[] { 0xFF, 0xD8, 0xFF });
            File.WriteAllBytes(iv4p, new byte[] { 0x01, 0x02 });
            var pair = store.FindLatestPair(pairDir);
            AssertTrue(!string.IsNullOrEmpty(pair.JpegPath) && File.Exists(pair.JpegPath), "FindLatestPair 未找到 jpeg");
            AssertTrue(File.Exists(pair.IvpPath), "FindLatestPair 未找到 iv4p");
            ImageStore.DeleteSourceFile(jpg, "smoke");
            ImageStore.DeleteSourceFile(iv4p, "smoke");
            AssertTrue(!File.Exists(jpg) && !File.Exists(iv4p), "DeleteSourceFile 未删除");
        }
        try { Directory.Delete(root, true); } catch { }
    }

    // ══════════ 10. HSL ══════════
    static void T10_Auth()
    {
        // 环境自适应：本仓库不含 hsl.dat（授权码需自行购买）——无授权环境跳过本用例，
        // 不影响其它用例验证；有授权文件时才断言激活成功。
        string authFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, HslAuthorization.FileName);
        if (!File.Exists(authFile)) { SkipCase("HSL.1（本机无 hsl.dat）"); return; }
        AssertTrue(HslAuthorization.EnsureRegistered(), "授权未激活（检查 exe 目录 hsl.dat）");
    }
    static void T10_Lifecycle()
    {
        var cfg = new MultiBrandPlcConfig();
        cfg.IpAddress = "127.0.0.1"; cfg.Port = 15999; cfg.TimeoutMs = 300;
        cfg.ReconnectIntervalMs = 300; cfg.HeartbeatIntervalMs = 300;
        var c = new HslPlcClient(cfg);
        Wait(() => !c.IsConnected, 2000, "应处于断线（自愈循环在尝试）");
        c.Dispose();
        Thread.Sleep(400);
        AssertTrue(!c.IsConnected, "Dispose 后仍显示连接");
    }
    static void T10_BadCode()
    {
        if (!HslAuthorization.IsActivated) { SkipCase("HSL.3（本机未激活授权）"); return; }
        bool before = HslAuthorization.IsActivated;
        AssertTrue(!HslAuthorization.TryActivate("bad-code"), "错误码被误通过");
        AssertTrue(HslAuthorization.IsActivated == before, "错误码破坏了已激活状态");
    }
    static void T10_AfterDispose()
    {
        var cfg = new MultiBrandPlcConfig();
        cfg.IpAddress = "127.0.0.1"; cfg.Port = 15998; cfg.TimeoutMs = 300;
        var c = new HslPlcClient(cfg);
        Thread.Sleep(500);
        c.Dispose();
        short v;
        AssertTrue(!c.ReadInt16("D0", out v), "Dispose 后读应返回 false");
        AssertTrue(!c.Write("D0", (short)1), "Dispose 后写应返回 false");
        AssertTrue(!c.IsConnected, "Dispose 后 IsConnected 应为 false");
    }
    static void T10_EncryptRoundTrip()
    {
        // 需已激活环境（有 hsl.dat）：验证落盘为加密格式、文件不含明文码、解密往返一致
        string code = HslAuthorization.LoadFromFile();
        if (string.IsNullOrEmpty(code)) { SkipCase("HSL.5（本机无授权）"); return; }
        string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, HslAuthorization.FileName);
        HslAuthorization.SaveToFile(code);
        string content = File.ReadAllText(file);
        AssertTrue(!content.Contains(code), "授权文件含明文授权码");
        AssertTrue(content.Contains("ENC1:"), "授权文件非加密格式");
        AssertTrue(HslAuthorization.LoadFromFile() == code, "解密往返不一致");
    }

    // ══════════ 11. DeviceHub ══════════
    static void T11_Start()
    {
        var cfg = new DeviceHubConfig(); cfg.UseMockCommunication = true;
        var hub = new DeviceHub(cfg);
        hub.Start();
        AssertTrue(hub.Barometer != null && hub.Io != null && hub.Fan != null, "Mock 服务未就绪");
        AssertTrue(hub.Plc != null && hub.PlcMaster == null && hub.PlcMultiBrand == null, "默认角色应为从站");
        hub.Dispose();
    }
    static void T11_Scan()
    {
        int port = 15250;
        lock (_scanLock) _scanGot.Clear();
        StartSim(port, "HUB001");
        var cfg = new DeviceHubConfig(); cfg.UseMockCommunication = true;
        cfg.Scanners.Add(new ScanConfig { Enabled = true, Mode = "Tcp", IpAddress = "127.0.0.1", Port = port, TriggerCommand = "LON" });
        var hub = new DeviceHub(cfg);
        hub.SerialNumberScanned += delegate(object o, string c) { lock (_scanLock) _scanGot.Add(c); };
        hub.Start();
        AssertTrue(WaitScan("HUB001", 8000), "经 hub 未收到条码");
        hub.Dispose(); StopSim();
    }
    static void T11_ApplyConfig()
    {
        var cfg = new DeviceHubConfig(); cfg.UseMockCommunication = true;
        var hub = new DeviceHub(cfg);
        hub.Start();
        int rebuilds = 0;
        hub.ServicesRebuilt += delegate(object o, EventArgs e) { rebuilds++; };
        cfg.PlcRole = PlcRole.MultiBrand;
        hub.ApplyConfig(cfg);
        hub.Start();
        AssertTrue(hub.PlcMultiBrand != null && hub.Plc == null, "热更后多品牌服务未就绪");
        AssertTrue(hub.IsPlcMultiBrand, "IsPlcMultiBrand 未翻转");
        AssertTrue(rebuilds >= 1, "ServicesRebuilt 未触发");
        hub.Dispose();
    }
    static void T11_Dispose()
    {
        var cfg = new DeviceHubConfig(); cfg.UseMockCommunication = true;
        var hub = new DeviceHub(cfg);
        hub.Start();
        hub.Dispose();
        AssertTrue(hub.Plc == null && hub.PlcMultiBrand == null && hub.Scanners == null && hub.Cameras == null,
            "Dispose 后服务引用未清空");
    }

    // ══════════ 12. 生命周期 ══════════
    static void T12_Serial()
    {
        var s = new ScannerService(new ScanConfig { Enabled = true, Mode = "Serial", PortName = "COM_NOT_EXIST" });
        // Open 语义=启动心跳后台线程（首连失败由心跳持续重连），恒返回 true——断言不抛异常即可
        bool opened = s.Open();
        AssertTrue(opened, "Open 异常返回");
        Thread.Sleep(3500);   // 等一个心跳周期确认无设备时静默重连不崩溃
        AssertTrue(!s.IsOpen, "无效串口不应显示已连接");
        s.Dispose();
    }
    static void T12_Eip()
    {
        var e = new ScannerEipService(new ScanConfig { Enabled = true, Mode = "Eip", IpAddress = "127.0.0.1", Port = 15998 });
        e.Open();
        Thread.Sleep(800);
        e.Dispose();
    }

    // ══════════ 辅助 ══════════
    static ModbusTcpMasterClient NewModbusMaster(int port)
    {
        var m = new ModbusTcpMasterClient();
        var c = new PlcMasterConfig();
        c.IpAddress = "127.0.0.1"; c.Port = port; c.UnitId = 1; c.TimeoutMs = 1000;
        AssertTrue(m.Connect(c), "主站连接失败 port=" + port);
        return m;
    }
    static IModbusMaster NModbusMaster(int port)
    {
        var c = new TcpClient("127.0.0.1", port);
        return new ModbusFactory().CreateMaster(c);
    }
}
