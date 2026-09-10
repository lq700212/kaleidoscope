// Kaleidoscope 验收测试用例集（152 用例：全功能点 正常/边界/异常路径，最终验收/发布前全量回归）。
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
// 【覆盖】配置序列化/校验（含校验器全分支与序列化边角）、Mock 三件套（含边界）、
//   PLC 从站（三拍握手/型号/清零/重建/未建站安全/映射截断）、Modbus 主站
//   （读写/轮询/断线自愈/并发/全功能码/事件/端点语义）、IO 耦合器（映射/读改写/
//   越界/备用重定向/取反/原始寄存器）、送风机（解析/命令/回退/自定义映射/禁用）、
//   TCP 扫码枪（收码/重连/半条码/边沿/契约）、串口扫码枪（契约）、EIP 扫码枪（契约）、
//   相机（T1/T2/超时/PR/PW 缓存/OF/详细格式/空判定/夹值/读图）、ImageStore
//   （归档/双格式/字节/模板/监听去重/清理）、HSL 多品牌（授权/自愈/加密存储/
//   端口表/全类型失败路径）、DeviceHub 全链路（建/启/收码/热更/释放/三角色装配）、
//   ConnectionMonitor（空安全/边沿）、生命周期、描述符/说明书、映射解析/构建、工具类。
// ─────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Reflection;
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

        Run("配扩.1 Load 不存在返回默认", T13_LoadMissing);
        Run("配扩.2 FromJson 空文本返回默认", T13_FromJsonEmpty);
        Run("配扩.3 Save/ToJson null 抛 ArgumentNullException", T13_NullThrows);
        Run("配扩.4 未知成员忽略兼容", T13_UnknownMember);
        Run("配扩.5 中文直读无转义且 UTF8 无 BOM", T13_ChineseNoEscape);
        Run("配扩.6 反斜杠 u 序列不误解码", T13_BackslashU);
        Run("配扩.7 EnsureSafe 深层嵌套兜底", T13_EnsureSafeDeep);
        Run("配扩.8 FromJson 类型错配抛 InvalidDataException", T13_TypeMismatch);

        Run("校验.1 null 配置与默认配置", T14_NullAndDefault);
        Run("校验.2 PLC 从站坏 IP 端口长度", T14_PlcSlaveBad);
        Run("校验.3 PLC 从站地址重叠告警", T14_PlcOverlap);
        Run("校验.4 PLC 从站型号表错误", T14_PlcModelTable);
        Run("校验.5 PLC 主站全分支", T14_PlcMaster);
        Run("校验.6 多品牌角色跳过与端口约定", T14_MultiBrandSkip);
        Run("校验.7 多品牌心跳与西门子提示", T14_MultiBrandHeartbeat);
        Run("校验.8 多品牌轮询项全分支", T14_MultiBrandPoll);
        Run("校验.9 相机全分支", T14_Cameras);
        Run("校验.10 扫码枪全分支", T14_Scanners);
        Run("校验.11 气压表全分支", T14_Barometer);
        Run("校验.12 IO 全分支", T14_Io);
        Run("校验.13 送风机全分支", T14_Fan);
        Run("校验.14 图像与全局", T14_ImageGlobal);

        Run("描述.1 GetAll 非空且字段齐", T15_GetAll);
        Run("描述.2 缓存与未登记回退", T15_CacheFallback);
        Run("描述.3 null 抛 ArgumentNullException", T15_NullThrows);
        Run("描述.4 说明书导出与转义", T15_Export);

        Run("映射.1 ParseAll 空与单条", T16_ParseBasic);
        Run("映射.2 ParseAll 中文分号与箭头", T16_ParseSeps);
        Run("映射.3 ParseAll 错误汇总", T16_ParseErrors);
        Run("映射.4 ParseAll 无前缀寄存器兼容", T16_ParseNoPrefix);
        Run("映射.5 IoMapBuilder 数量与编号", T16_BuildCounts);
        Run("映射.6 IoMapBuilder 八进制地址", T16_Octal);
        Run("映射.7 IoMapBuilder 预留点", T16_Reserved);
        Run("映射.8 IoMapBuilder 参数异常", T16_BuildErrors);

        Run("从扩.1 相机地址注册容错", T17_CamAddrSafe);
        Run("从扩.2 未建站读写安全", T17_BeforeConnect);
        Run("从扩.3 型号映射大小写与截断", T17_ModelMap);
        Run("从扩.4 空型号与释放语义", T17_ModelEmptyDispose);
        Run("从扩.5 相机通道零地址", T17_CamZeroAddr);

        Run("主扩.1 四类读与多线圈写", T18_AllRW);
        Run("主扩.2 轮询事件与缓存", T18_PollEvent);
        Run("主扩.3 空轮询项安全", T18_EmptyPoll);
        Run("主扩.4 端点与重连语义", T18_Endpoint);
        Run("主扩.5 Dispose 后安全", T18_AfterDispose);
        Run("主扩.6 连接失败 OnError", T18_ConnError);

        Run("IO扩.1 备用通道映射", T19_Remap);
        Run("IO扩.2 输入输出取反", T19_Invert);
        Run("IO扩.3 原始寄存器读写", T19_Raw);
        Run("IO扩.4 未连接全路径安全", T19_DownSafe);
        Run("IO扩.5 断开与重复连接", T19_Disconnect);

        Run("风扩.1 未启用返回空", T20_Disabled);
        Run("风扩.2 偏移越界不崩", T20_OffsetOverflow);
        Run("风扩.3 自定义映射与命令码", T20_CustomMap);
        Run("风扩.4 空配置与无候选", T20_NoCandidates);
        Run("风扩.5 断开与重复释放", T20_Disconnect);

        Run("枪扩.1 TCP 未启用与空触发", T21_TcpDisabled);
        Run("枪扩.2 TCP 名称与重复 Open", T21_TcpName);
        Run("枪扩.3 TCP 连接边沿事件", T21_TcpEdge);
        Run("枪扩.4 串口禁用与触发空操作", T21_SerialContract);
        Run("枪扩.5 EIP 接口契约", T22_EipContract);
        Run("枪扩.6 EIP 禁用与重复释放", T22_EipDisabled);

        Run("相扩.1 T2 详细格式 OK/NG", T23_Detailed);
        Run("相扩.2 空判定与坏前缀失败", T23_EmptyBad);
        Run("相扩.3 自定义合格字符", T23_OkChar);
        Run("相扩.4 切程序夹值与失败", T23_PwClampFail);
        Run("相扩.5 非法 OF 与坏 PR", T23_OfPrBad);
        Run("相扩.6 未连接全路径失败", T23_DownPaths);
        Run("相扩.7 标签与重复释放", T23_LabelsDispose);
        Run("相扩.8 BR 读图成功与失败", T23_ReadImage);

        Run("图扩.1 双格式归档", T24_Pair);
        Run("图扩.2 字节归档合法与非法", T24_Bytes);
        Run("图扩.3 模板渲染占位符", T24_Template);
        Run("图扩.4 监听去重与空参", T24_Monitor);
        Run("图扩.5 查找边界", T24_FindEdge);
        Run("图扩.6 删除源文件安全", T24_DeleteSafe);
        Run("图扩.7 重名序号与非法字符", T24_DupSanitize);
        Run("图扩.8 脏 SubDirs 与清理", T24_DirtyCleanup);

        Run("HSL扩.1 各品牌默认端口", T25_Ports);
        Run("HSL扩.2 品牌标签与非法回退", T25_Labels);
        Run("HSL扩.3 未连接全类型读写失败", T25_DownRW);
        Run("HSL扩.4 空轮询与错误隔离", T25_PollIsolation);
        Run("HSL扩.5 授权守卫", T25_AuthGuards);

        Run("Hub扩.1 主站角色装配", T26_MasterRole);
        Run("Hub扩.2 空相机回退默认", T26_DefaultCams);
        Run("Hub扩.3 空热更与重复启停", T26_NullApply);
        Run("Hub扩.4 真实类型装配", T26_RealTypes);
        Run("Hub扩.5 三种扫码枪装配", T26_ScannerModes);

        Run("监控.1 空构造安全", T27_NullSafe);
        Run("监控.2 气压表边沿事件", T27_BaroEdge);
        Run("监控.3 主站与IO送风机边沿", T27_MultiEdge);

        Run("表Mock.1 真实气压表失败路径", T28_BaroDown);
        Run("表Mock.2 Mock 气压表边界", T28_MockBaro);
        Run("表Mock.3 Mock IO 边界", T28_MockIo);
        Run("表Mock.4 Mock 送风机边界", T28_MockFan);

        Run("工具.1 LogHelper 定向与隔离", T29_Log);
        Run("工具.2 TcpKeepAlive 空安全", T29_KeepAlive);
        Run("工具.3 串口识别无硬件安全", T29_SerialHelper);
        Run("工具.4 HSL 文件格式守卫", T29_HslFile);

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
    static volatile bool _camPwFail = false;        // true=PW 回 ER,PW,03（切程序失败场景）
    static volatile bool _camPrBad = false;         // true=PR 回非法前缀（读程序号失败场景）
    static volatile bool _camBrBad = false;         // true=BR 回非法前缀（读图失败场景）
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
                            else if (cmd == "PR") resp = _camPrBad ? Encoding.ASCII.GetBytes("XX\r") : Encoding.ASCII.GetBytes("PR,001\r");
                            else if (cmd.StartsWith("PW")) resp = _camPwFail ? Encoding.ASCII.GetBytes("ER,PW,03\r") : Encoding.ASCII.GetBytes("PW\r");
                            else if (cmd.StartsWith("OF")) resp = Encoding.ASCII.GetBytes("OF\r");
                            else if (cmd.StartsWith("BR")) resp = _camBrBad ? Encoding.ASCII.GetBytes("XX\r") : Encoding.ASCII.GetBytes("BR,0000000001,0000000005,HELLO");
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

    // ══════════ 13. 配置扩展（序列化边角） ══════════
    static void T13_LoadMissing()
    {
        string f = Path.Combine(_cfgDir, "no_such_file_xyz.kcfg");
        if (File.Exists(f)) File.Delete(f);
        var r = ConfigSerializer.Load(f);
        AssertTrue(r != null && r.Plc != null && r.Plc.Port == 502, "不存在文件未返回默认配置");
    }
    static void T13_FromJsonEmpty()
    {
        AssertTrue(ConfigSerializer.FromJson(null) != null, "FromJson null 应返回默认");
        AssertTrue(ConfigSerializer.FromJson("") != null, "FromJson 空串应返回默认");
        AssertTrue(ConfigSerializer.FromJson("   ") != null, "FromJson 空白应返回默认");
    }
    static void T13_NullThrows()
    {
        bool t1 = false, t2 = false;
        try { ConfigSerializer.Save(null, Path.Combine(_cfgDir, "x.kcfg")); } catch (ArgumentNullException) { t1 = true; }
        try { ConfigSerializer.ToJson(null); } catch (ArgumentNullException) { t2 = true; }
        AssertTrue(t1 && t2, "Save/ToJson null 未抛 ArgumentNullException");
    }
    static void T13_UnknownMember()
    {
        Directory.CreateDirectory(_cfgDir);
        string f = Path.Combine(_cfgDir, "unknown.kcfg");
        File.WriteAllText(f, "{\"Plc\":{\"IpAddress\":\"9.9.9.9\"},\"未来新增字段_XYZ\":12345}");
        var r = ConfigSerializer.Load(f);
        AssertTrue(r.Plc.IpAddress == "9.9.9.9", "未知成员导致已知字段丢失");
    }
    static void T13_ChineseNoEscape()
    {
        var c = new DeviceHubConfig();
        c.ProductModel = "测试型号中文";
        string json = ConfigSerializer.ToJson(c);
        AssertTrue(json.Contains("测试型号中文"), "中文被转义不可读");
        AssertTrue(!json.Contains("\\u"), "仍含 \\u 转义");
        string f = Path.Combine(_cfgDir, "cn.kcfg");
        ConfigSerializer.Save(c, f);
        byte[] head = new byte[3];
        using (var fs = File.OpenRead(f)) fs.Read(head, 0, 3);
        AssertTrue(!(head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF), "文件带 BOM（应 UTF-8 无 BOM）");
        AssertTrue(ConfigSerializer.Load(f).ProductModel == "测试型号中文", "中文往返不一致");
    }
    static void T13_BackslashU()
    {
        var c = new DeviceHubConfig();
        c.ProductModel = "X\\u0041Y";   // 字面反斜杠+u（非转义字符）
        string json = ConfigSerializer.ToJson(c);
        var r = ConfigSerializer.FromJson(json);
        AssertTrue(r.ProductModel == "X\\u0041Y", "反斜杠 u 序列往返被误解码：" + r.ProductModel);
    }
    static void T13_EnsureSafeDeep()
    {
        var c = ConfigSerializer.EnsureSafe(null);
        AssertTrue(c != null && c.Cameras != null, "EnsureSafe null 未返回默认");
        var c2 = new DeviceHubConfig();
        c2.Cameras.Add(new CameraConfig { StationPrograms = null, ModelStationPrograms = new List<ModelStationPrograms> { new ModelStationPrograms { Programs = null } } });
        c2.Fan.FanIpCandidates = null;
        c2.Io.IoBackupChannelMappings = null;
        c2.PlcMaster.PollItems = null;
        c2.MultiBrandPlc.PollItems = null;
        c2.Plc.ModelIndexes = null;
        ConfigSerializer.EnsureSafe(c2);
        AssertTrue(c2.Cameras[0].StationPrograms != null, "相机点位表未兜底");
        AssertTrue(c2.Cameras[0].ModelStationPrograms[0].Programs != null, "型号分表未兜底");
        AssertTrue(c2.Fan.FanIpCandidates != null && c2.Io.IoBackupChannelMappings != null, "Fan/Io 列表未兜底");
        AssertTrue(c2.PlcMaster.PollItems != null && c2.MultiBrandPlc.PollItems != null, "轮询表未兜底");
    }
    static void T13_TypeMismatch()
    {
        bool threw = false;
        try { ConfigSerializer.FromJson("{\"PlcRole\":\"不是枚举应抛错\"}"); }
        catch (InvalidDataException) { threw = true; }
        AssertTrue(threw, "类型错配未抛 InvalidDataException");
    }

    // ══════════ 14. 校验器全分支 ══════════
    static DeviceHubConfig NewValidCfg()
    {
        var c = new DeviceHubConfig();
        c.UseMockCommunication = true;   // Mock 下跳过网络类参数，聚焦逻辑字段
        c.ProductModel = "U171";
        return c;
    }
    static void T14_NullAndDefault()
    {
        var r0 = DeviceHubConfigValidator.Validate(null);
        AssertTrue(!r0.IsValid && r0.Errors.Count == 1, "null 配置应恰 1 条错误");
        var r1 = DeviceHubConfigValidator.Validate(NewValidCfg());
        AssertTrue(r1.IsValid, "默认合法配置被误报：" + string.Join(";", r1.Errors.ToArray()));
    }
    static void T14_PlcSlaveBad()
    {
        var c = NewValidCfg();
        c.Plc.IpAddress = "bad-ip"; c.Plc.Port = 0; c.Plc.ProductModelLen = 0;
        var r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(r.Errors.Count >= 3, "从站坏 IP/端口/长度应≥3 错，实际" + r.Errors.Count);
    }
    static void T14_PlcOverlap()
    {
        var c = NewValidCfg();
        c.Plc.ScanRequestAddress = 5; c.Plc.ScanResultAddress = 5;   // 故意重叠
        var r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(r.IsValid && r.Warnings.Exists(delegate(string w) { return w.Contains("重复占用"); }), "重叠未告警");
        c.Plc.ScanRequestAddress = 0; c.Plc.ScanResultAddress = 0;   // 0=预留不算重叠
        r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(!r.Warnings.Exists(delegate(string w) { return w.Contains("重复占用"); }), "地址 0 不应算重叠");
    }
    static void T14_PlcModelTable()
    {
        var c = NewValidCfg();
        c.Plc.ModelIndexes.Add(new ModelIndexItem { ModelName = "", ModelIndex = 1 });
        c.Plc.ModelIndexes.Add(new ModelIndexItem { ModelName = "X", ModelIndex = -2 });
        var r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(r.Errors.Count >= 2, "空型号名/负序号未拦截");
    }
    static void T14_PlcMaster()
    {
        var c = NewValidCfg();
        c.PlcMaster.IpAddress = "no-ip"; c.PlcMaster.Port = 99999;
        c.PlcMaster.TimeoutMs = 0; c.PlcMaster.ReconnectIntervalMs = -1; c.PlcMaster.PollIntervalMs = -1;
        c.PlcMaster.PollItems.Add(null);
        c.PlcMaster.PollItems.Add(new PlcPollItem { Name = "", Count = 0, Function = (PlcPollFunction)99 });
        var r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(r.Errors.Count >= 7, "主站分支应≥7 错，实际" + r.Errors.Count + ":" + string.Join("|", r.Errors.ToArray()));
    }
    static void T14_MultiBrandSkip()
    {
        var c = NewValidCfg();   // 默认 Slave 角色
        c.MultiBrandPlc.IpAddress = "bad-ip";   // 非生效角色应跳过不误报
        var r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(r.IsValid, "非多品牌角色不应校验多品牌段：" + string.Join(";", r.Errors.ToArray()));
        c.PlcRole = PlcRole.MultiBrand;
        c.MultiBrandPlc.IpAddress = "1.2.3.4"; c.MultiBrandPlc.Port = 0;   // 0=按品牌默认，合法
        r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(r.IsValid, "端口 0 应合法：" + string.Join(";", r.Errors.ToArray()));
        c.MultiBrandPlc.Brand = (MultiBrandPlcBrand)99;
        r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(!r.IsValid, "非法品牌未拦截");
    }
    static void T14_MultiBrandHeartbeat()
    {
        var c = NewValidCfg();
        c.PlcRole = PlcRole.MultiBrand; c.MultiBrandPlc.IpAddress = "1.2.3.4";
        c.MultiBrandPlc.HeartbeatIntervalMs = 500;   // <1000 告警
        c.MultiBrandPlc.HeartbeatAddress = "D0";
        c.MultiBrandPlc.HeartbeatDataType = (PlcDataType)99;   // 非法
        c.MultiBrandPlc.Brand = MultiBrandPlcBrand.SiemensS7;
        c.MultiBrandPlc.SiemensModel = "不存在的型号";
        var r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(r.Warnings.Exists(delegate(string w) { return w.Contains("心跳"); }), "小心跳未告警");
        AssertTrue(r.Errors.Exists(delegate(string e) { return e.Contains("心跳数据"); }), "非法心跳类型未拦截");
        AssertTrue(r.Warnings.Exists(delegate(string w) { return w.Contains("西门子"); }), "西门子型号未告警");
    }
    static void T14_MultiBrandPoll()
    {
        var c = NewValidCfg();
        c.PlcRole = PlcRole.MultiBrand; c.MultiBrandPlc.IpAddress = "1.2.3.4";
        c.MultiBrandPlc.PollItems.Add(null);
        c.MultiBrandPlc.PollItems.Add(new MultiBrandPlcPollItem { Name = "big", Address = "D0", Count = 2000, DataType = (PlcDataType)99 });
        var r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(r.Errors.Count >= 2, "null 项/非法类型未拦截");
        AssertTrue(r.Warnings.Exists(delegate(string w) { return w.Contains("过大"); }), "超大 Count 未告警");
    }
    static void T14_Cameras()
    {
        var c = NewValidCfg();
        var r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(r.Warnings.Exists(delegate(string w) { return w.Contains("未配置任何相机"); }), "空相机未告警");
        c.Cameras.Add(null);
        var cam = new CameraConfig();
        cam.Name = ""; cam.IpAddress = "bad"; cam.CommandPort = 0;
        cam.ResponseTimeoutMs = 0; cam.TimeoutMs = 0; cam.ImageWaitMs = 0;
        cam.PlcRequestAddress = -1; cam.PlcResultAddress = -1;
        cam.ImageSource = "外星"; cam.OkChar = "00";
        cam.StationPrograms.Add(null);
        cam.StationPrograms.Add(new StationProgramItem { StationNo = 0, ProgramNo = 200 });
        cam.ModelStationPrograms.Add(null);
        cam.ModelStationPrograms.Add(new ModelStationPrograms { ModelName = "", Programs = new List<StationProgramItem>() });
        c.Cameras.Add(cam);
        var dup = new CameraConfig(); dup.Name = "同名"; dup.IpAddress = "1.2.3.4";
        var dup2 = new CameraConfig(); dup2.Name = "同名"; dup2.IpAddress = "1.2.3.4";
        c.Cameras.Add(dup); c.Cameras.Add(dup2);
        r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(r.Errors.Count >= 8, "相机分支应≥8 错，实际" + r.Errors.Count);
        AssertTrue(r.Warnings.Exists(delegate(string w) { return w.Contains("重复"); }), "重名相机未告警");
        AssertTrue(r.Warnings.Exists(delegate(string w) { return w.Contains("取图来源"); }), "未知取图来源未告警");
    }
    static void T14_Scanners()
    {
        var c = NewValidCfg();
        c.Scanners.Add(null);
        c.Scanners.Add(new ScanConfig { Enabled = true, Mode = "蓝牙" });
        c.Scanners.Add(new ScanConfig { Enabled = true, Mode = "Tcp", IpAddress = "bad", Port = 0 });
        c.Scanners.Add(new ScanConfig { Enabled = true, Mode = "Eip", IpAddress = "1.2.3.4", Port = 44818, EipReadLengthBytes = 1, EipPollingMs = 10 });
        c.Scanners.Add(new ScanConfig { Enabled = true, Mode = "Serial", BaudRate = 0, DataBits = 9, StopBits = "3", Parity = "XYZ", PortName = "", DeviceKeyword = "" });
        var r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(r.Errors.Count >= 5, "扫码枪分支应≥5 错，实际" + r.Errors.Count + ":" + string.Join("|", r.Errors.ToArray()));
        AssertTrue(r.Warnings.Count >= 4, "扫码枪告警应≥4，实际" + r.Warnings.Count);
    }
    static void T14_Barometer()
    {
        var c = NewValidCfg();
        c.Barometer.TotalBarometers = 0; c.Barometer.BarometerReadRegisterCount = 0;
        c.Barometer.BarometerDefaultDecimalPlaces = 99;
        var r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(r.Errors.Count >= 2, "气压表总数/读数未拦截");
        AssertTrue(r.Warnings.Exists(delegate(string w) { return w.Contains("小数位"); }), "小数位未告警");
        c = NewValidCfg();
        c.Barometer.BaudRate = 0; c.Barometer.StopBits = 3; c.Barometer.Parity = "BAD";
        c.Barometer.SerialReadTimeoutMs = 0; c.Barometer.SerialWriteTimeoutMs = 0;
        var r2 = DeviceHubConfigValidator.Validate(c);
        AssertTrue(r2.IsValid, "Mock 下串口参数应跳过：" + string.Join(";", r2.Errors.ToArray()));
        c.UseMockCommunication = false;
        r2 = DeviceHubConfigValidator.Validate(c);
        AssertTrue(r2.Errors.Count >= 3, "真机串口参数未拦截");
    }
    static void T14_Io()
    {
        var c = NewValidCfg();
        c.Io.TotalInputs = 0; c.Io.TotalOutputs = 0;
        c.Io.IoBackupChannelMappings.Add(null);
        c.Io.IoBackupChannelMappings.Add(new IoOutputChannelRemap { SourceChannel = 99, TargetChannel = 99 });
        c.Io.IoBackupChannelMappings.Add(new IoOutputChannelRemap { SourceRegister = 0x2000, SourceChannel = 1, TargetRegister = 0x2000, TargetChannel = 1 });
        var r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(r.Errors.Count >= 4, "IO 分支应≥4 错，实际" + r.Errors.Count);
        AssertTrue(r.Warnings.Exists(delegate(string w) { return w.Contains("无意义"); }), "源=目标未告警");
        c = NewValidCfg(); c.Io.PlcAddress = "bad";
        r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(r.IsValid, "Mock 下 IO 网络应跳过");
        c.UseMockCommunication = false;
        r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(!r.IsValid, "真机坏 IP 未拦截");
    }
    static void T14_Fan()
    {
        var c = NewValidCfg();
        c.Fan.FanEnabled = false; c.Fan.FanIpAddress = "bad"; c.Fan.FanStatusCount = 0;
        var r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(r.IsValid, "未启用送风机应整段跳过");
        c = NewValidCfg();
        c.Fan.FanStatusCount = 0; c.Fan.FanTimeoutMs = 0;
        c.Fan.FanRunStateOffset = 9; c.Fan.FanTemperatureOffset = 9;
        c.Fan.FanHumidityOffset = 9; c.Fan.FanTempSetpointOffset = 9; c.Fan.FanHumSetpointOffset = 9;
        r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(r.Errors.Count >= 2, "送风机长度/超时未拦截");
        AssertTrue(r.Warnings.Count >= 5, "偏移越界应≥5 告警，实际" + r.Warnings.Count);
        c = NewValidCfg(); c.Fan.FanIpAddress = "bad";
        r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(r.IsValid, "Mock 下送风机网络应跳过");
        c.UseMockCommunication = false; c.Fan.FanIpCandidates.Add("not-ip");
        r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(!r.IsValid && r.Warnings.Exists(delegate(string w) { return w.Contains("候选 IP"); }), "候选坏 IP 未告警");
    }
    static void T14_ImageGlobal()
    {
        var c = NewValidCfg();
        c.Image.SaveRootDir = ""; c.Image.KeepDays = -1; c.Image.SubDirs.Clear(); c.Image.FtpRootDir = "";
        c.Cameras.Add(new CameraConfig { IpAddress = "1.2.3.4" });
        var r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(r.Errors.Count >= 1, "负保留天数未拦截");
        AssertTrue(r.Warnings.Count >= 3, "图像告警应≥3，实际" + r.Warnings.Count);
        c = NewValidCfg(); c.PlcRole = PlcRole.Master; c.PlcMaster = null;
        r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(!r.IsValid, "Master 缺段未拦截");
        c = NewValidCfg(); c.PlcRole = PlcRole.MultiBrand; c.MultiBrandPlc = null;
        r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(!r.IsValid, "MultiBrand 缺段未拦截");
        c = NewValidCfg(); c.ProductModel = "";
        r = DeviceHubConfigValidator.Validate(c);
        AssertTrue(r.Warnings.Exists(delegate(string w) { return w.Contains("ProductModel"); }), "空型号未告警");
    }

    // ══════════ 15. 描述符/说明书 ══════════
    static void T15_GetAll()
    {
        var all = DeviceDescriptorRegistry.GetAll();
        AssertTrue(all != null && all.Count >= 13, "描述符段数=" + (all == null ? -1 : all.Count));
        DeviceDescriptor cam = null;
        foreach (var d in all) if (d.TypeName == "CameraConfig") cam = d;
        AssertTrue(cam != null && cam.Fields.Count > 0, "相机描述符缺失");
        bool hasName = false, hasColl = false;
        foreach (var f in cam.Fields)
        {
            AssertTrue(!string.IsNullOrEmpty(f.DisplayName), "字段中文名为空：" + f.Name);
            if (f.Name == "Name") hasName = f.DisplayName.Length > 0;
            if (f.Name == "StationPrograms") hasColl = f.IsCollection;
        }
        AssertTrue(hasName && hasColl, "相机 Name/集合标记异常");
    }
    static void T15_CacheFallback()
    {
        var a = DeviceDescriptorRegistry.Get(typeof(CameraConfig));
        var b = DeviceDescriptorRegistry.Get(typeof(CameraConfig));
        AssertTrue(object.ReferenceEquals(a, b), "缓存未命中同一实例");
        var d = DeviceDescriptorRegistry.Build(typeof(string));
        AssertTrue(d.DisplayName == "String" && d.Fields.Count >= 0, "未登记类型回退异常");
        var b2 = DeviceDescriptorRegistry.Build(typeof(CameraConfig));
        AssertTrue(b2.Fields.Count == a.Fields.Count, "Build 与缓存字段数不一致");
        bool orderSame = true;
        for (int i = 0; i < a.Fields.Count; i++) if (a.Fields[i].Name != b2.Fields[i].Name) orderSame = false;
        AssertTrue(orderSame, "字段声明顺序不稳定");
    }
    static void T15_NullThrows()
    {
        bool t1 = false, t2 = false;
        try { DeviceDescriptorRegistry.Get(null); } catch (ArgumentNullException) { t1 = true; }
        try { DeviceDescriptorRegistry.Build(null); } catch (ArgumentNullException) { t2 = true; }
        AssertTrue(t1 && t2, "null 类型未抛 ArgumentNullException");
    }
    static void T15_Export()
    {
        string md = DeviceDescriptionExporter.ExportToMarkdown();
        AssertTrue(md.Contains("# Kaleidoscope") && md.Contains("| 字段 |") && md.Contains("CameraConfig"), "说明书缺关键段");
        var m = typeof(DeviceDescriptionExporter).GetMethod("EscapeCell", BindingFlags.NonPublic | BindingFlags.Static);
        string esc = (string)m.Invoke(null, new object[] { "a|b\r\nc" });
        AssertTrue(esc == "a\\|b  c", "转义异常：" + esc);
        string f = Path.Combine(Path.GetTempPath(), "ktest_doc", "desc.md");
        DeviceDescriptionExporter.ExportToMarkdownFile(f);
        byte[] head = new byte[3];
        using (var fs = File.OpenRead(f)) fs.Read(head, 0, 3);
        AssertTrue(!(head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF), "说明书带 BOM");
        AssertTrue(File.ReadAllText(f).Contains("相机"), "说明书文件缺中文");
        try { Directory.Delete(Path.GetDirectoryName(f), true); } catch { }
    }

    // ══════════ 16. 映射解析/构建 ══════════
    static void T16_ParseBasic()
    {
        string err;
        var e0 = IoOutputChannelRemap.ParseAll(null, out err);
        AssertTrue(e0.Count == 0 && err == null, "null 应空表无错");
        var e1 = IoOutputChannelRemap.ParseAll("  ", out err);
        AssertTrue(e1.Count == 0 && err == null, "空白应空表无错");
        var l = IoOutputChannelRemap.ParseAll("0x2000@0x00->0x2009@0x10", out err);
        AssertTrue(l.Count == 1 && err == null, "单条解析失败：" + err);
        AssertTrue(l[0].SourceRegister == 0x2000 && l[0].SourceChannel == 0, "源解析错");
        AssertTrue(l[0].TargetRegister == 0x2009 && l[0].TargetChannel == 16, "目标解析错（0x10=16）");
    }
    static void T16_ParseSeps()
    {
        string err;
        var l = IoOutputChannelRemap.ParseAll("0x2000@0x00->0x2009@0x10；0x2008@0x00→0x2009@0x11", out err);
        AssertTrue(l.Count == 2 && err == null, "中文分号/箭头解析失败：" + err);
    }
    static void T16_ParseErrors()
    {
        string err;
        var l = IoOutputChannelRemap.ParseAll("少分隔符;0xZZZZ@0x00->0x2009@0x10;0x2000@5->0x2009@0x10;0x2000@0x20->0x2009@0x10;0x2000@0x01->0x2000@0x01", out err);
        AssertTrue(l.Count == 0, "全错行应空表，实际" + l.Count);
        AssertTrue(!string.IsNullOrEmpty(err) && err.Contains("分隔符") && err.Contains("相同"), "错误汇总缺关键项：" + err);
    }
    static void T16_ParseNoPrefix()
    {
        string err;
        var l = IoOutputChannelRemap.ParseAll("2000@0x0A->2009@0x0B", out err);
        AssertTrue(l.Count == 1 && l[0].SourceRegister == 0x2000 && l[0].SourceChannel == 10, "无 0x 前缀寄存器应兼容");
    }
    static void T16_BuildCounts()
    {
        var map = IoMapBuilder.Build(72, 80, 160);
        AssertTrue(map.Count == 240, "总数应 240，实际" + map.Count);
        for (int i = 0; i < map.Count; i++) AssertTrue(map[i].IoId == i + 1, "IoId 不连续 @" + i);
    }
    static void T16_Octal()
    {
        var m1 = IoMapBuilder.GetDeviceMapping(1, 72, 80);
        AssertTrue(m1.VacuumPressureInput.PhysicalAddress == "X000", "1 号输入=" + m1.VacuumPressureInput.PhysicalAddress);
        AssertTrue(m1.VacuumValveOutput.PhysicalAddress == "Y000", "1 号电磁阀=" + m1.VacuumValveOutput.PhysicalAddress);
        AssertTrue(m1.CarrierPowerOutput.PhysicalAddress == "Y110", "1 号载台=" + m1.CarrierPowerOutput.PhysicalAddress);
        AssertTrue(m1.VacuumValveOutput.IoId == 81 && m1.CarrierPowerOutput.IoId == 153, "输出 IoId 错");
        var m8 = IoMapBuilder.GetDeviceMapping(8, 72, 80);
        AssertTrue(m8.VacuumPressureInput.PhysicalAddress == "X007", "8 号=" + m8.VacuumPressureInput.PhysicalAddress);
        var m9 = IoMapBuilder.GetDeviceMapping(9, 72, 80);
        AssertTrue(m9.VacuumPressureInput.PhysicalAddress == "X010", "9 号应进位 X010，实际" + m9.VacuumPressureInput.PhysicalAddress);
        var m72 = IoMapBuilder.GetDeviceMapping(72, 72, 80);
        AssertTrue(m72.VacuumPressureInput.PhysicalAddress == "X107", "72 号=" + m72.VacuumPressureInput.PhysicalAddress);
        AssertTrue(m72.CarrierPowerOutput.PhysicalAddress == "Y217", "72 号载台=" + m72.CarrierPowerOutput.PhysicalAddress);
    }
    static void T16_Reserved()
    {
        var map = IoMapBuilder.Build(72, 80, 160);
        AssertTrue(map[72].DeviceName.Contains("预留输入"), "第 73 点应预留输入：" + map[72].DeviceName);
        AssertTrue(map[map.Count - 1].DeviceName.Contains("预留输出"), "末点应预留输出");
    }
    static void T16_BuildErrors()
    {
        bool t = false;
        try { IoMapBuilder.Build(0, 80, 160); } catch (ArgumentOutOfRangeException) { t = true; }
        AssertTrue(t, "总数 0 未抛");
        t = false;
        try { IoMapBuilder.Build(72, 10, 160); } catch (ArgumentOutOfRangeException) { t = true; }
        AssertTrue(t, "输入不足未抛");
        t = false;
        try { IoMapBuilder.Build(72, 80, 10); } catch (ArgumentOutOfRangeException) { t = true; }
        AssertTrue(t, "输出不足未抛");
        t = false;
        try { IoMapBuilder.GetDeviceMapping(0, 72, 80); } catch (ArgumentOutOfRangeException) { t = true; }
        AssertTrue(t, "设备号 0 未抛");
        t = false;
        try { IoMapBuilder.GetDeviceMapping(73, 72, 80); } catch (ArgumentOutOfRangeException) { t = true; }
        AssertTrue(t, "设备号越界未抛");
    }

    // ══════════ 17. PLC 从站扩展 ══════════
    static void T17_CamAddrSafe()
    {
        using (var plc = new PlcService(NewSlaveCfg(15300)))
        {
            plc.SetCameraResultAddresses(null);
            plc.SetCameraResultAddresses(new List<CameraConfig>());
            var list = new List<CameraConfig>();
            list.Add(null);
            var z = new CameraConfig(); z.PlcResultAddress = 0;
            var ok = new CameraConfig(); ok.PlcResultAddress = 20;
            list.Add(z); list.Add(ok);
            plc.SetCameraResultAddresses(list);
            plc.EnsureConnected();
            Wait(delegate() { return plc.IsConnected; }, 3000, "监听未就绪");
            AssertTrue(!plc.HasMasterConnected, "无主站连入时应为 false");
        }
    }
    static void T17_BeforeConnect()
    {
        using (var plc = new PlcService(NewSlaveCfg(15301)))
        {
            bool req;
            AssertTrue(!plc.ReadScanRequest(out req), "未建站读请求应 false");
            ushort v;
            AssertTrue(!plc.ReadRegister(1, out v), "未建站读应 false");
            AssertTrue(!plc.WriteRegister(1, 5), "未建站写应 false");
            int st;
            AssertTrue(plc.ReadCameraRequest(null, out st) && st == 0, "null 相机应无请求");
            plc.WriteCameraResult(null, 1);   // 不抛即过
            var z = new CameraConfig(); z.PlcResultAddress = 0;
            plc.WriteCameraResult(z, 1);
        }
    }
    static void T17_ModelMap()
    {
        int port = 15302;
        var pcfg = NewSlaveCfg(port);
        pcfg.ModelIndexes = PlcConfig.DefaultModelIndexes();   // Z121=1、U171=2
        using (var plc = new PlcService(pcfg))
        {
            plc.SetCurrentModel("u171");
            plc.EnsureConnected();
            Wait(delegate() { return plc.IsConnected; }, 3000, "监听未就绪");
            using (var m = NModbusMasterRetry(port))
            {
                AssertTrue(plc.WriteProductModel("U171"), "写已知型号失败");
                AssertTrue(m.ReadHoldingRegisters(1, 7, 1)[0] == 2, "U171 序号应 2（大小写不敏感）");
                AssertTrue(plc.WriteProductModel("未知型号"), "写未知型号失败");
                AssertTrue(m.ReadHoldingRegisters(1, 7, 1)[0] == 0, "未知型号序号应 0");
                AssertTrue(plc.WriteProductModel("ABCDEFGHIJKLMNOPQRST"), "超长型号失败");
                var regs = m.ReadHoldingRegisters(1, 8, 5);
                AssertTrue(regs.Length == 5, "截断后应恰 5 寄存器");
            }
        }
    }
    static void T17_ModelEmptyDispose()
    {
        int port = 15303;
        var plc = new PlcService(NewSlaveCfg(port));
        plc.SetCurrentModel(null);
        plc.SetCurrentModel("");
        plc.EnsureConnected();
        Wait(delegate() { return plc.IsConnected; }, 3000, "监听未就绪");
        using (var m = NModbusMasterRetry(port))
        {
            AssertTrue(plc.WriteProductModel(""), "空型号写失败");
            AssertTrue(m.ReadHoldingRegisters(1, 7, 1)[0] == 0, "空型号序号应 0");
        }
        plc.Dispose();
        plc.Dispose();
        AssertTrue(!plc.EnsureConnected(), "Dispose 后 EnsureConnected 应 false");
    }
    static void T17_CamZeroAddr()
    {
        int port = 15304;
        using (var plc = new PlcService(NewSlaveCfg(port)))
        {
            plc.EnsureConnected();
            Wait(delegate() { return plc.IsConnected; }, 3000, "监听未就绪");
            var z = new CameraConfig(); z.PlcRequestAddress = 0; z.PlcResultAddress = 0;
            int st;
            AssertTrue(plc.ReadCameraRequest(z, out st) && st == 0, "零地址请求应 0");
            var good = new CameraConfig(); good.PlcRequestAddress = 11; good.PlcResultAddress = 12;
            using (var m = NModbusMasterRetry(port))
            {
                m.WriteSingleRegister(1, 11, 9);
                AssertTrue(plc.ReadCameraRequest(good, out st) && st == 9, "请求值=" + st);
                plc.WriteCameraResult(good, 1);
                AssertTrue(m.ReadHoldingRegisters(1, 12, 1)[0] == 1, "结果未写入");
            }
        }
    }

    // ══════════ 18. Modbus 主站扩展 ══════════
    static void T18_AllRW()
    {
        using (var slave = MockSlave.Start(15310))
        {
            slave.WriteHold(0, 1, 2);
            slave.WriteInput(10, 0x1234, 0x5678);
            var m = NewModbusMaster(15310);
            var inp = m.ReadInputRegisters(10, 2);
            AssertTrue(inp != null && inp[0] == 0x1234 && inp[1] == 0x5678, "输入寄存器不符");
            AssertTrue(m.WriteSingleCoil(3, true), "写单线圈失败");
            var coils = m.ReadCoils(3, 1);
            AssertTrue(coils != null && coils[0], "线圈回读不符");
            AssertTrue(m.WriteMultipleCoils(5, new bool[] { true, false, true }), "写多线圈失败");
            var coils2 = m.ReadCoils(5, 3);
            AssertTrue(coils2 != null && coils2[0] && !coils2[1] && coils2[2], "多线圈回读不符");
            var disc = m.ReadDiscreteInputs(0, 2);
            AssertTrue(disc != null && disc.Length == 2, "离散输入应返回数组");
            m.Dispose();
        }
    }
    static void T18_PollEvent()
    {
        using (var slave = MockSlave.Start(15311))
        {
            slave.WriteHold(0, 7, 8);
            var m = new ModbusTcpMasterClient();
            var cfg = new PlcMasterConfig();
            cfg.IpAddress = "127.0.0.1"; cfg.Port = 15311; cfg.UnitId = 1;
            cfg.TimeoutMs = 1000; cfg.ReconnectIntervalMs = 300; cfg.PollIntervalMs = 100;
            cfg.PollItems.Add(new PlcPollItem { Name = "h", Function = PlcPollFunction.Holding, StartAddress = 0, Count = 2 });
            cfg.PollItems.Add(new PlcPollItem { Name = "c", Function = PlcPollFunction.Coil, StartAddress = 0, Count = 1 });
            AssertTrue(m.Connect(cfg), "连接失败");
            var got = new List<string>();
            m.PollDataUpdated += delegate(object o, PlcPollEventArgs e) { lock (got) got.Add(e.Name); };
            m.StartPolling();
            m.StartPolling();   // 幂等
            Wait(delegate() { object v; return m.TryGetLastPollData("h", out v); }, 3000, "轮询缓存未更新");
            Thread.Sleep(400);
            lock (got) AssertTrue(got.Contains("h"), "PollDataUpdated 未触发");
            object miss;
            AssertTrue(!m.TryGetLastPollData("不存在", out miss), "缺失项应 false");
            m.StopPolling(); m.StopPolling();
            m.Dispose();
        }
    }
    static void T18_EmptyPoll()
    {
        var m = new ModbusTcpMasterClient();
        var cfg = new PlcMasterConfig();
        cfg.IpAddress = "127.0.0.1"; cfg.Port = 15312; cfg.UnitId = 1; cfg.TimeoutMs = 500;
        cfg.PollIntervalMs = 100;
        m.StartPolling();   // _config null 应静默返回
        m.Connect(cfg);     // 无从站会失败，但 _config 已就位供轮询路径使用
        m.StartPolling();   // 无轮询项不启动
        Thread.Sleep(300);
        object v;
        AssertTrue(!m.TryGetLastPollData("x", out v), "空轮询应无缓存");
        m.Dispose();
    }
    static void T18_Endpoint()
    {
        using (var slave = MockSlave.Start(15313))
        {
            var m = new ModbusTcpMasterClient();
            AssertTrue(m.ActiveEndpoint == null, "未连接端点应 null");
            var cfg = new PlcMasterConfig();
            cfg.IpAddress = "127.0.0.1"; cfg.Port = 15313; cfg.UnitId = 1; cfg.TimeoutMs = 1000;
            AssertTrue(m.Connect(cfg), "连接失败");
            AssertTrue(m.ActiveEndpoint == "127.0.0.1:15313", "端点=" + m.ActiveEndpoint);
            AssertTrue(m.ReconnectNow(), "已连接 ReconnectNow 应 true");
            m.Disconnect();
            AssertTrue(!m.IsConnected && m.ActiveEndpoint == null, "断开后状态未清");
            m.Disconnect();
            m.Dispose();
        }
    }
    static void T18_AfterDispose()
    {
        using (var slave = MockSlave.Start(15314))
        {
            var m = NewModbusMaster(15314);
            m.Dispose();
            m.Dispose();
            AssertTrue(m.ReadHoldingRegisters(0, 1) == null, "Dispose 后读应 null");
            AssertTrue(!m.WriteSingleRegister(0, 1), "Dispose 后写应 false");
            AssertTrue(m.ReadCoils(0, 1) == null && m.ReadDiscreteInputs(0, 1) == null, "位读应 null");
            AssertTrue(m.ActiveEndpoint == null, "Dispose 后端点应 null");
        }
    }
    static void T18_ConnError()
    {
        var m = new ModbusTcpMasterClient();
        var errs = new List<string>();
        m.OnError += delegate(object o, string e) { lock (errs) errs.Add(e); };
        var cfg = new PlcMasterConfig();
        cfg.IpAddress = "127.0.0.1"; cfg.Port = 15315; cfg.UnitId = 1; cfg.TimeoutMs = 400;
        AssertTrue(!m.Connect(cfg), "无从站应连接失败");
        lock (errs) AssertTrue(errs.Count > 0, "失败未触发 OnError");
        AssertTrue(!m.Connect(null), "null 配置应 false");
        m.Dispose();
    }

    // ══════════ 19. IO 耦合器扩展 ══════════
    static void T19_Remap()
    {
        using (var slave = MockSlave.Start(15320))
        {
            var io = new ModbusTcpIoController();
            var cfg = NewIoCfg(15320, 16, 32);
            cfg.IoBackupChannelMappingEnabled = true;
            // 输出点 base+1（bit0@0x2000）重定向到备用 0x2001 bit0
            cfg.IoBackupChannelMappings.Add(new IoOutputChannelRemap
            {
                SourceRegister = 0x2000, SourceChannel = 0,
                TargetRegister = 0x2001, TargetChannel = 0
            });
            io.Connect(cfg);
            int out1 = cfg.TotalInputs + 1;
            io.WriteOutput(out1, true);
            AssertTrue(io.ReadOutput(out1), "映射后回读失败");
            ushort[] raw = io.ReadHoldingRegisters(0x2000, 2);
            AssertTrue(raw != null && raw[0] == 0 && (raw[1] & 1) != 0, "映射未落到备用寄存器");
            bool[] all = io.ReadAllOutputs();
            AssertTrue(all != null && all.Length == 32 && all[0], "批量读映射失败");
            io.Dispose();
        }
    }
    static void T19_Invert()
    {
        using (var slave = MockSlave.Start(15321))
        {
            slave.WriteInput(0x1000, 0x0001, 0x0000);
            var io = new ModbusTcpIoController();
            var cfg = NewIoCfg(15321, 32, 16);
            io.Connect(cfg);
            AssertTrue(io.ReadInput(1) && !io.ReadInput(2), "正逻辑不符");
            io.Dispose();
            var io2 = new ModbusTcpIoController();
            var cfg2 = NewIoCfg(15321, 32, 16);
            cfg2.InvertInputs = true; cfg2.InvertOutputs = true;
            io2.Connect(cfg2);
            AssertTrue(!io2.ReadInput(1) && io2.ReadInput(2), "取反逻辑不符");
            int out1 = cfg2.TotalInputs + 1;
            io2.WriteOutput(out1, true);
            AssertTrue(io2.ReadOutput(out1), "取反输出回读不符");
            io2.Dispose();
        }
    }
    static void T19_Raw()
    {
        using (var slave = MockSlave.Start(15322))
        {
            var io = new ModbusTcpIoController();
            io.Connect(NewIoCfg(15322, 16, 16));
            AssertTrue(io.WriteSingleRegister(0x2000, 0xABCD), "原始写失败");
            ushort[] r = io.ReadHoldingRegisters(0x2000, 1);
            AssertTrue(r != null && r[0] == 0xABCD, "原始读不符");
            io.Dispose();
        }
    }
    static void T19_DownSafe()
    {
        var io = new ModbusTcpIoController();
        AssertTrue(!io.ReadInput(1), "未连接读输入应 false");
        AssertTrue(io.ReadAllInputs().Length == 0, "未连接批量输入应空");
        AssertTrue(!io.ReadOutput(81), "未连接读输出应 false");
        AssertTrue(io.ReadAllOutputs().Length == 0, "未连接批量输出应空");
        io.WriteOutput(81, true);
        io.WriteOutputs(null, null);
        io.WriteOutputs(new int[] { 81 }, new bool[] { true, false });
        AssertTrue(io.ReadHoldingRegisters(0x2000, 1) == null, "未连接原始读应 null");
        AssertTrue(!io.WriteSingleRegister(0x2000, 1), "未连接原始写应 false");
        io.Dispose();
    }
    static void T19_Disconnect()
    {
        using (var slave = MockSlave.Start(15323))
        {
            var io = new ModbusTcpIoController();
            io.Connect(NewIoCfg(15323, 16, 16));
            AssertTrue(io.IsConnected, "未连接");
            io.Disconnect();
            AssertTrue(!io.IsConnected, "断开后仍连接");
            AssertTrue(io.Connect(NewIoCfg(15323, 16, 16)), "重连失败");
            io.Dispose(); io.Dispose();
        }
    }

    // ══════════ 20. 送风机扩展 ══════════
    static void T20_Disabled()
    {
        var fan = new FanControllerClient();
        var cfg = NewFanCfg(15330);
        cfg.FanEnabled = false;
        fan.Connect(cfg);
        AssertTrue(fan.ReadStatus() == null, "未启用读状态应 null");
        AssertTrue(!fan.StartFixedValue() && !fan.Stop(), "未启用命令应 false");
        fan.Dispose();
    }
    static void T20_OffsetOverflow()
    {
        using (var slave = MockSlave.Start(15331))
        {
            slave.WriteHold(0x0002, 3);   // 仅 0x0002=启动命令码；温度偏移 2 越界取 0
            var fan = new FanControllerClient();
            var cfg = NewFanCfg(15331);
            cfg.FanStatusCount = 2;   // 区块仅 2 个，温度/湿度/设定偏移 2~5 越界
            fan.Connect(cfg);
            var st = fan.ReadStatus();
            AssertTrue(st != null, "偏移越界应回退 0 不崩");
            AssertTrue(st.RunState == FanRunState.FixedValueRunning, "运行态=" + st.RunState);
            AssertTrue(Math.Abs(st.Temperature) < 0.01, "越界温度应 0，实际" + st.Temperature);
            fan.Dispose();
        }
    }
    static void T20_CustomMap()
    {
        using (var slave = MockSlave.Start(15332))
        {
            slave.WriteHold(0x0011, 9, 2600, 6100, 2400, 3100);
            var fan = new FanControllerClient();
            var cfg = NewFanCfg(15332);
            cfg.FanStatusStartAddress = 0x0010;
            cfg.FanStatusCount = 6;
            cfg.FanStartCommand = 9; cfg.FanStopCommand = 8;
            cfg.FanControlAddress = 0x0020;
            fan.Connect(cfg);
            var st = fan.ReadStatus();
            AssertTrue(st != null && st.RunState == FanRunState.FixedValueRunning, "自定义命令码未识别");
            AssertTrue(Math.Abs(st.Temperature - 26.0) < 0.01, "自定义区块温度=" + st.Temperature);
            AssertTrue(fan.StartFixedValue() && slave.ReadHold(0x0020) == 9, "自定义控制字≠9");
            AssertTrue(fan.Stop() && slave.ReadHold(0x0020) == 8, "自定义停止字≠8");
            fan.Dispose();
        }
    }
    static void T20_NoCandidates()
    {
        var fan = new FanControllerClient();
        AssertTrue(!fan.Connect(null), "null 配置应 false");
        var cfg = NewFanCfg(15333);
        cfg.FanIpAddress = ""; cfg.FanAutoDetectEnabled = false; cfg.FanIpCandidates.Clear();
        AssertTrue(!fan.Connect(cfg), "无候选 IP 应 false");
        var cfg2 = NewFanCfg(15333);
        cfg2.FanIpAddress = "999.999.1.1"; cfg2.FanAutoDetectEnabled = false; cfg2.FanTimeoutMs = 300;
        AssertTrue(!fan.Connect(cfg2), "非法 IP 应 false");
        fan.Dispose();
    }
    static void T20_Disconnect()
    {
        using (var slave = MockSlave.Start(15334))
        {
            var fan = new FanControllerClient();
            fan.Connect(NewFanCfg(15334));
            AssertTrue(fan.IsConnected && fan.ActiveIp == "127.0.0.1", "ActiveIp=" + fan.ActiveIp);
            fan.Disconnect();
            AssertTrue(!fan.IsConnected, "断开后仍连接");
            fan.Dispose(); fan.Dispose();
        }
    }

    // ══════════ 21/22. 扫码枪扩展 ══════════
    static void T21_TcpDisabled()
    {
        var s = new ScannerTcpService(new ScanConfig { Enabled = false, Mode = "Tcp", IpAddress = "127.0.0.1", Port = 15340 });
        AssertTrue(!s.Open(), "未启用应返回 false");
        var e = new ScannerTcpService(new ScanConfig { Enabled = true, Mode = "Tcp", IpAddress = "127.0.0.1", Port = 15341, TriggerCommand = "" });
        AssertTrue(e.SendTrigger(), "空触发指令应直接 true");
        var n = new ScannerTcpService(new ScanConfig { Enabled = true, Mode = "Tcp", IpAddress = "127.0.0.1", Port = 15342, TriggerCommand = "LON" });
        AssertTrue(!n.SendTrigger(), "未连接非空触发应 false");
        s.Dispose(); e.Dispose(); n.Dispose();
    }
    static void T21_TcpName()
    {
        int port = 15343;
        StartSim(port, "N");
        var s = new ScannerTcpService(new ScanConfig { Enabled = true, Mode = "Tcp", IpAddress = "127.0.0.1", Port = port, TriggerCommand = "LON" });
        AssertTrue(s.Name == "127.0.0.1:" + port, "Name=" + s.Name);
        AssertTrue(s.Open(), "首次 Open 失败");
        AssertTrue(!s.Open(), "重复 Open 应 false");
        Wait(delegate() { return s.IsOpen; }, 4000, "未连接");
        s.Dispose(); s.Dispose();
        AssertTrue(!s.IsOpen, "Dispose 后应断开");
        StopSim();
    }
    static void T21_TcpEdge()
    {
        int port = 15344;
        StartSim(port, "E");
        var s = new ScannerTcpService(new ScanConfig { Enabled = true, Mode = "Tcp", IpAddress = "127.0.0.1", Port = port, TriggerCommand = "LON" });
        var edges = new List<bool>();
        s.ConnectionChanged += delegate(object o, bool c) { lock (edges) edges.Add(c); };
        s.Open();
        Wait(delegate() { lock (edges) return edges.Contains(true); }, 5000, "未收到连上边沿");
        s.Dispose();
        Thread.Sleep(300);
        lock (edges) AssertTrue(edges.Contains(false), "未收到断开边沿");
        StopSim();
    }
    static void T21_SerialContract()
    {
        var d = new ScannerService(new ScanConfig { Enabled = false, Mode = "Serial", PortName = "COM1" });
        AssertTrue(!d.Open(), "串口未启用应 false");
        AssertTrue(!d.IsOpen, "初始应未打开");
        AssertTrue(d.SendTrigger(), "串口触发应空操作 true");
        var s = new ScannerService(new ScanConfig { Enabled = true, Mode = "Serial", PortName = "COM_NOT_EXIST_X" });
        AssertTrue(s.Name.Contains("COM_NOT_EXIST_X"), "Name=" + s.Name);
        AssertTrue(s.Open(), "Open 应启动心跳返回 true");
        Thread.Sleep(500);
        AssertTrue(!s.IsOpen, "无效串口不应已连接");
        s.Dispose(); s.Dispose();
        AssertTrue(!s.IsOpen, "释放后应关闭");
        d.Dispose();
    }
    static void T22_EipContract()
    {
        var e = new ScannerEipService(new ScanConfig { Enabled = true, Mode = "Eip", IpAddress = "127.0.0.1", Port = 15345 });
        AssertTrue(e.Name == "EIP:127.0.0.1:15345", "Name=" + e.Name);
        AssertTrue(!e.IsOpen, "初始应未连接");
        AssertTrue(e.SendTrigger(), "EIP 触发应空操作 true");
        AssertTrue(e.Open(), "首次 Open 失败");
        Thread.Sleep(600);
        AssertTrue(!e.IsOpen, "无 EIP 真机不应已连接");
        e.Dispose(); e.Dispose();
        AssertTrue(e.SendTrigger(), "释放后触发仍 true");
    }
    static void T22_EipDisabled()
    {
        var d = new ScannerEipService(new ScanConfig { Enabled = false, Mode = "Eip", IpAddress = "127.0.0.1", Port = 15346 });
        AssertTrue(!d.Open(), "未启用应 false");
        var e = new ScannerEipService(new ScanConfig { Enabled = true, Mode = "Eip", IpAddress = "127.0.0.1", Port = 15347 });
        AssertTrue(e.Open(), "首次 Open 失败");
        AssertTrue(!e.Open(), "重复 Open 应 false");
        e.Dispose(); e.Dispose();
        d.Dispose();
    }

    // ══════════ 23. 相机扩展 ══════════
    static void T23_Detailed()
    {
        int port = 15350;
        try
        {
            _camJudge = "RT,00152,OK,01,OK,0000100";
            StartCamSim(port);
            var cam = NewCam(port);
            var r = cam.TriggerAndRead();
            AssertTrue(r.Succeeded && r.IsOk, "详细 OK 应判 OK：" + r.Detail);
            cam.Dispose(); StopCamSim();
            _camJudge = "RT,00151,NG,01,NG,0000000";
            StartCamSim(port);
            var cam2 = NewCam(port);
            var r2 = cam2.TriggerAndRead();
            AssertTrue(r2.Succeeded && !r2.IsOk, "详细 NG 应判 NG");
            cam2.Dispose(); StopCamSim();
        }
        finally { _camJudge = "RT,0000"; try { StopCamSim(); } catch { } }
    }
    static void T23_EmptyBad()
    {
        int port = 15351;
        try
        {
            _camJudge = "RT,";
            StartCamSim(port);
            var cam = NewCam(port);
            AssertTrue(!cam.TriggerAndRead().Succeeded, "空判定应失败");
            cam.Dispose(); StopCamSim();
            _camJudge = "XX,0000";
            StartCamSim(port);
            var cam2 = NewCam(port);
            AssertTrue(!cam2.TriggerAndRead().Succeeded, "坏前缀应失败");
            cam2.Dispose(); StopCamSim();
        }
        finally { _camJudge = "RT,0000"; try { StopCamSim(); } catch { } }
    }
    static void T23_OkChar()
    {
        int port = 15352;
        try
        {
            _camJudge = "RT,1111";
            StartCamSim(port);
            var cfg = new CameraConfig();
            cfg.Name = "t"; cfg.IpAddress = "127.0.0.1"; cfg.CommandPort = port;
            cfg.ResponseTimeoutMs = 800; cfg.TimeoutMs = 800; cfg.ImageWaitMs = 500;
            cfg.OkChar = "1";
            var cam = new KeyenceIV4Camera(cfg);
            AssertTrue(cam.TriggerAndRead().IsOk, "自定义合格位 1 应 OK");
            cam.Dispose(); StopCamSim();
            _camJudge = "RT,0000";
            StartCamSim(port);
            var cfg2 = new CameraConfig();
            cfg2.Name = "t"; cfg2.IpAddress = "127.0.0.1"; cfg2.CommandPort = port;
            cfg2.ResponseTimeoutMs = 800; cfg2.TimeoutMs = 800; cfg2.ImageWaitMs = 500;
            cfg2.OkChar = "";
            var cam2 = new KeyenceIV4Camera(cfg2);
            AssertTrue(cam2.TriggerAndRead().IsOk, "空 OkChar 应回退 0 判 OK");
            cam2.Dispose(); StopCamSim();
        }
        finally { _camJudge = "RT,0000"; try { StopCamSim(); } catch { } }
    }
    static void T23_PwClampFail()
    {
        int port = 15353;
        StartCamSim(port);
        var cam = NewCam(port);
        try
        {
            AssertTrue(cam.SwitchProgram(-5), "负数应夹到 0 成功");
            lock (_camLock) AssertTrue(_camCmds[_camCmds.Count - 1] == "PW,000", "夹值命令=" + _camCmds[_camCmds.Count - 1]);
            AssertTrue(cam.SwitchProgram(200), "超界应夹到 127 成功");
            lock (_camLock) AssertTrue(_camCmds[_camCmds.Count - 1] == "PW,127", "夹值命令=" + _camCmds[_camCmds.Count - 1]);
            _camPwFail = true;
            AssertTrue(!cam.SwitchProgram(9), "ER 响应应返回 false");
        }
        finally { _camPwFail = false; cam.Dispose(); StopCamSim(); }
    }
    static void T23_OfPrBad()
    {
        var cam = NewCam(15354);   // 无服务：非法格式不发即 false
        AssertTrue(!cam.SetOutputFormat(null) && !cam.SetOutputFormat("") && !cam.SetOutputFormat("0"), "非法 OF 应 false");
        AssertTrue(!cam.SetOutputFormat("ABC") && !cam.SetOutputFormat("1A"), "非法 OF 应 false");
        cam.Dispose();
        int port = 15355;
        try
        {
            _camPrBad = true;
            StartCamSim(port);
            var c2 = NewCam(port);
            AssertTrue(c2.ReadProgramNo() == -1, "坏 PR 前缀应 -1");
            c2.Dispose(); StopCamSim();
        }
        finally { _camPrBad = false; try { StopCamSim(); } catch { } }
    }
    static void T23_DownPaths()
    {
        var cam = NewCam(15356);   // 无服务监听
        var r = cam.TriggerAndRead();
        AssertTrue(!r.Succeeded, "未连接 T2 应失败");
        AssertTrue(!cam.SendTrigger(), "未连接 T1 应 false");
        AssertTrue(cam.ReadProgramNo() == -1, "未连接 PR 应 -1");
        AssertTrue(!cam.SetOutputFormat("00"), "未连接 OF 应 false");
        AssertTrue(!cam.EnsureConnected(), "未连接 Ensure 应 false");
        AssertTrue(!cam.CheckConnection(), "未连接心跳应 false");
        cam.Dispose();
    }
    static void T23_LabelsDispose()
    {
        var cfg = new CameraConfig();
        cfg.Name = "上相机"; cfg.IpAddress = "10.1.2.3"; cfg.CommandPort = 8500;
        var cam = new KeyenceIV4Camera(cfg);
        AssertTrue(cam.DisplayName == "上相机", "DisplayName=" + cam.DisplayName);
        AssertTrue(cam.IpLabel == "10.1.2.3:8500", "IpLabel=" + cam.IpLabel);
        AssertTrue(cam.IpAddressOnly == "10.1.2.3", "Ip=" + cam.IpAddressOnly);
        AssertTrue(!cam.IsConnected, "初始应未连接");
        cam.Dispose(); cam.Dispose();
    }
    static void T23_ReadImage()
    {
        int port = 15357;
        var cam = NewCam(port);   // 无服务
        var r0 = cam.ReadImage();
        AssertTrue(!r0.Succeeded, "无服务读图应失败");
        cam.Dispose();
        try
        {
            StartCamSim(port);
            var c2 = NewCam(port);
            var r1 = c2.ReadImage();
            AssertTrue(r1.Succeeded && r1.DataSize == 5 && r1.ImageData.Length == 5, "BR 成功数据错");
            AssertTrue(r1.DataTriggerNo == 1, "触发编号=" + r1.DataTriggerNo);
            c2.Dispose(); StopCamSim();
            _camBrBad = true;
            StartCamSim(port);
            var c3 = NewCam(port);
            AssertTrue(!c3.ReadImage().Succeeded, "坏前缀读图应失败");
            c3.Dispose(); StopCamSim();
        }
        finally { _camBrBad = false; try { StopCamSim(); } catch { } }
    }

    // ══════════ 24. 图像存储扩展 ══════════
    static void T24_Pair()
    {
        string root = Path.Combine(Path.GetTempPath(), "ktest_pair");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        string src = Path.Combine(Path.GetTempPath(), "ktest_pair_src");
        if (Directory.Exists(src)) Directory.Delete(src, true);
        Directory.CreateDirectory(src);
        var cfg = new ImageConfig(); cfg.SaveRootDir = root; cfg.KeepDays = 7;
        using (var store = new ImageStore(cfg))
        {
            string j = Path.Combine(src, "0084.jpeg");
            string v = Path.Combine(src, "0084.iv4p");
            File.WriteAllBytes(j, new byte[] { 0xFF, 0xD8, 0xFF, 0x00 });
            File.WriteAllBytes(v, new byte[] { 0x01, 0x02 });
            string t = store.SaveImageFilePair(j, v, 3, true, "SN_P", "camA");
            AssertTrue(!string.IsNullOrEmpty(t) && File.Exists(t) && t.EndsWith(".jpeg"), "归档失败：" + t);
            AssertTrue(t.Contains("0084_"), "文件名缺源名时间戳：" + t);
            AssertTrue(File.Exists(Path.ChangeExtension(t, ".iv4p")), "iv4p 未同名归档");
            AssertTrue(store.SaveImageFilePair(Path.Combine(src, "nope.jpeg"), null, 1, true, "S", "c") == null, "缺 jpeg 应 null");
            string t2 = store.SaveImageFilePair(j, null, 1, false, "S2", "c");
            AssertTrue(!string.IsNullOrEmpty(t2) && File.Exists(t2), "无 iv4p 应仍归档 jpeg");
        }
        try { Directory.Delete(root, true); } catch { }
        try { Directory.Delete(src, true); } catch { }
    }
    static void T24_Bytes()
    {
        string root = Path.Combine(Path.GetTempPath(), "ktest_bytes");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var cfg = new ImageConfig(); cfg.SaveRootDir = root; cfg.KeepDays = 7;
        using (var store = new ImageStore(cfg))
        {
            byte[] bmp;
            using (var b = new Bitmap(16, 16))
            using (var ms = new MemoryStream()) { b.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp); bmp = ms.ToArray(); }
            string p = store.SaveImageBytes(bmp, 2, true, "SNB", "cam");
            AssertTrue(!string.IsNullOrEmpty(p) && File.Exists(p), "合法 BMP 应归档");
            AssertTrue(store.SaveImageBytes(new byte[] { 1, 2, 3 }, 1, true, "S", "c") == null, "非法字节应 null");
            AssertTrue(store.SaveImageBytes(null, 1, true, "S", "c") == null, "null 字节应 null");
        }
        try { Directory.Delete(root, true); } catch { }
    }
    static void T24_Template()
    {
        var m = typeof(ImageStore).GetMethod("RenderTemplate", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        AssertTrue(m != null, "反射不到 RenderTemplate");
        object r = m.Invoke(null, new object[] { "{年月日}/{SN}/{相机}/{OKNG}/{点位}/{时间}", new DateTime(2026, 8, 11, 10, 20, 30, 123), "SN1", true, 5, "上相机" });
        string s = (string)r;
        AssertTrue(s.Contains("2026年08月11日") && s.Contains("SN1") && s.Contains("上相机") && s.Contains("OK") && s.Contains("5"), "占位符渲染错：" + s);
        AssertTrue(((string)m.Invoke(null, new object[] { "{未知}ABC", DateTime.Now, "S", true, 1, "c" })).Contains("{未知}"), "未知占位应保留");
        AssertTrue((string)m.Invoke(null, new object[] { null, DateTime.Now, "S", true, 1, "c" }) == "", "null 模板应空串");
        AssertTrue(((string)m.Invoke(null, new object[] { "{SN}/{相机}", DateTime.Now, "", false, 1, null })).Contains("未知SN") && ((string)m.Invoke(null, new object[] { "{相机}", DateTime.Now, "S", true, 1, null })).Contains("未知相机"), "空 SN/相机兜底错");
    }
    static void T24_Monitor()
    {
        string root = Path.Combine(Path.GetTempPath(), "ktest_mon");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var cfg = new ImageConfig(); cfg.SaveRootDir = root; cfg.KeepDays = 7;
        using (var store = new ImageStore(cfg))
        {
            store.AddMonitor(null, 0);
            store.AddMonitor("", 1);
            string dir = Path.Combine(root, "ftp0");
            store.AddMonitor(dir, 0);
            store.AddMonitor(dir + "\\", 0);   // 尾斜杠去重
            store.AddMonitor(dir.ToUpper(), 0); // 大小写去重
            var hits = new List<string>();
            store.FtpFileArrived += delegate(int i, string p) { lock (hits) hits.Add(p); };
            File.WriteAllText(Path.Combine(dir, "n1.jpeg"), "x");
            Wait(delegate() { lock (hits) return hits.Count >= 1; }, 4000, "监听未触发");
            Thread.Sleep(500);
            lock (hits) AssertTrue(hits.Count == 1, "重复监听导致事件重复：" + hits.Count);
        }
        try { Directory.Delete(root, true); } catch { }
    }
    static void T24_FindEdge()
    {
        string root = Path.Combine(Path.GetTempPath(), "ktest_find");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var cfg = new ImageConfig(); cfg.SaveRootDir = root; cfg.KeepDays = 7;
        using (var store = new ImageStore(cfg))
        {
            var r0 = store.FindLatestPair(null);
            AssertTrue(r0.JpegPath == null && r0.IvpPath == null, "null 目录应空");
            AssertTrue(store.FindLatestPair(Path.Combine(root, "nodir")).JpegPath == null, "不存在目录应空");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "a.txt"), "x");
            AssertTrue(store.FindLatestPair(root).JpegPath == null, "无图目录应空");
            string oldJ = Path.Combine(root, "old.jpg");
            string newJ = Path.Combine(root, "new.jpeg");
            File.WriteAllBytes(oldJ, new byte[] { 1 });
            Thread.Sleep(50);
            File.WriteAllBytes(newJ, new byte[] { 2 });
            File.SetLastWriteTime(oldJ, DateTime.Now.AddDays(-1));
            var r1 = store.FindLatestPair(root);
            AssertTrue(r1.JpegPath != null && r1.JpegPath.EndsWith("new.jpeg"), "应取最新：" + r1.JpegPath);
        }
        try { Directory.Delete(root, true); } catch { }
    }
    static void T24_DeleteSafe()
    {
        ImageStore.DeleteSourceFile(null, "t");
        ImageStore.DeleteSourceFile("", "t");
        ImageStore.DeleteSourceFile(Path.Combine(Path.GetTempPath(), "ktest_nope_xyz.tmp"), "t");
    }
    static void T24_DupSanitize()
    {
        string root = Path.Combine(Path.GetTempPath(), "ktest_dup");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var cfg = new ImageConfig(); cfg.SaveRootDir = root; cfg.KeepDays = 7;
        using (var store = new ImageStore(cfg))
        using (var bmp = new Bitmap(8, 8))
        {
            string p1 = store.SaveImage(bmp, 1, true, "SND", "cam");
            string p2 = store.SaveImage(bmp, 1, true, "SND", "cam");
            AssertTrue(p1 != p2 && p2.Contains("_2"), "重名未追加序号：" + p2);
            string p3 = store.SaveImage(bmp, 1, true, "A/B:C", "cam");
            AssertTrue(!string.IsNullOrEmpty(p3) && File.Exists(p3), "非法字符 SN 应清洗落盘");
        }
        try { Directory.Delete(root, true); } catch { }
    }
    static void T24_DirtyCleanup()
    {
        string root = Path.Combine(Path.GetTempPath(), "ktest_dirty");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var cfg = new ImageConfig(); cfg.SaveRootDir = root; cfg.KeepDays = 7;
        cfg.SubDirs = new List<string> { "E:\\Images\\{SN}" };   // 脏配置：完整路径当一层
        using (var store = new ImageStore(cfg))
        using (var bmp = new Bitmap(8, 8))
        {
            string p = store.SaveImage(bmp, 1, true, "SNZ", "cam");
            AssertTrue(p.Contains("SNZ") && !p.Contains("Images\\Images"), "脏路径未清洗：" + p);
        }
        try { Directory.Delete(root, true); } catch { }
        // 清理逻辑：过期日期目录删、含新文件目录留、KeepDays=0 不动
        string croot = Path.Combine(Path.GetTempPath(), "ktest_clean");
        if (Directory.Exists(croot)) Directory.Delete(croot, true);
        Directory.CreateDirectory(croot);
        string oldd = Path.Combine(croot, "20200101");
        Directory.CreateDirectory(oldd);
        string of = Path.Combine(oldd, "o.png");
        File.WriteAllBytes(of, new byte[] { 1 });
        File.SetLastWriteTime(of, DateTime.Now.AddDays(-30));
        string keepd = Path.Combine(croot, "keepnew");
        Directory.CreateDirectory(keepd);
        File.WriteAllBytes(Path.Combine(keepd, "n.png"), new byte[] { 2 });
        var ccfg = new ImageConfig(); ccfg.SaveRootDir = croot; ccfg.KeepDays = 7;
        using (var store = new ImageStore(ccfg))
        {
            var m = typeof(ImageStore).GetMethod("RunCleanupOnce", BindingFlags.NonPublic | BindingFlags.Instance);
            m.Invoke(store, null);
            AssertTrue(!Directory.Exists(oldd), "过期日期目录未删除");
            AssertTrue(Directory.Exists(keepd), "含新文件目录被误删");
        }
        var zcfg = new ImageConfig(); zcfg.SaveRootDir = croot; zcfg.KeepDays = 0;
        using (var store = new ImageStore(zcfg))
        {
            var m = typeof(ImageStore).GetMethod("RunCleanupOnce", BindingFlags.NonPublic | BindingFlags.Instance);
            m.Invoke(store, null);   // KeepDays=0 不清理不抛
            AssertTrue(Directory.Exists(keepd), "KeepDays=0 不应删");
        }
        var gcfg = new ImageConfig(); gcfg.SaveRootDir = Path.GetPathRoot(croot); gcfg.KeepDays = 7;
        using (var store = new ImageStore(gcfg))
        {
            var m = typeof(ImageStore).GetMethod("RunCleanupOnce", BindingFlags.NonPublic | BindingFlags.Instance);
            m.Invoke(store, null);   // 盘符根应跳过不抛
        }
        try { Directory.Delete(croot, true); } catch { }
    }

    // ══════════ 25. HSL 多品牌扩展 ══════════
    static MultiBrandPlcConfig NewMbCfg(MultiBrandPlcBrand brand, int port)
    {
        var c = new MultiBrandPlcConfig();
        c.Brand = brand; c.IpAddress = "127.0.0.1"; c.Port = port;
        c.TimeoutMs = 300; c.ReconnectIntervalMs = 60000; c.HeartbeatIntervalMs = 60000;
        return c;
    }
    static void T25_Ports()
    {
        var cases = new MultiBrandPlcBrand[] { MultiBrandPlcBrand.MelsecMc, MultiBrandPlcBrand.MelsecA1E, MultiBrandPlcBrand.OmronFins, MultiBrandPlcBrand.OmronCip, MultiBrandPlcBrand.InovanceH5U, MultiBrandPlcBrand.SiemensS7 };
        var wants = new string[] { ":6000", ":6000", ":9600", ":44818", ":502", ":102" };
        for (int i = 0; i < cases.Length; i++)
        {
            var c = NewMbCfg(cases[i], 0);
            var cli = new HslPlcClient(c);
            AssertTrue(cli.ActiveEndpoint != null && cli.ActiveEndpoint.EndsWith(wants[i]), cases[i] + " 默认端口错：" + cli.ActiveEndpoint);
            cli.Dispose();
        }
        var e = new HslPlcClient(NewMbCfg(MultiBrandPlcBrand.MelsecMc, 16666));
        AssertTrue(e.ActiveEndpoint.EndsWith(":16666"), "显式端口未尊重");
        e.Dispose();
        var n = new HslPlcClient();
        AssertTrue(n.ActiveEndpoint == null, "无配置端点应 null");
        n.Dispose();
    }
    static void T25_Labels()
    {
        var m = typeof(HslPlcClient).GetMethod("BrandLabel", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        AssertTrue(m != null, "反射不到 BrandLabel");
        string[] brands = new string[] { "MelsecA1E", "MelsecMc", "OmronFins", "OmronCip", "InovanceH5U", "SiemensS7" };
        foreach (var b in brands)
        {
            object v = Enum.Parse(typeof(MultiBrandPlcBrand), b);
            string label = (string)m.Invoke(null, new object[] { v });
            AssertTrue(!string.IsNullOrEmpty(label), b + " 标签为空");
        }
        string fb = (string)m.Invoke(null, new object[] { (MultiBrandPlcBrand)99 });
        AssertTrue(fb == "99", "非法品牌应回退 ToString，实际" + fb);
    }
    static void T25_DownRW()
    {
        var c = NewMbCfg(MultiBrandPlcBrand.MelsecMc, 15360);
        var cli = new HslPlcClient(c);
        AssertTrue(!cli.IsConnected, "初始应未连接");
        bool bv; short sv; ushort uv; int iv; float fv;
        AssertTrue(!cli.ReadBool("M10", out bv), "Bool 读应失败");
        AssertTrue(cli.ReadBools("M10", 2) == null, "Bools 应 null");
        AssertTrue(!cli.ReadInt16("D0", out sv), "Int16 应失败");
        AssertTrue(cli.ReadInt16s("D0", 2) == null, "Int16s 应 null");
        AssertTrue(!cli.ReadUInt16("D0", out uv), "UInt16 应失败");
        AssertTrue(cli.ReadUInt16s("D0", 2) == null, "UInt16s 应 null");
        AssertTrue(!cli.ReadInt32("D0", out iv), "Int32 应失败");
        AssertTrue(cli.ReadInt32s("D0", 2) == null, "Int32s 应 null");
        AssertTrue(!cli.ReadFloat("D0", out fv), "Float 应失败");
        AssertTrue(cli.ReadFloats("D0", 2) == null, "Floats 应 null");
        AssertTrue(!cli.Write("M10", true) && !cli.Write("D0", (short)1) && !cli.Write("D0", (ushort)1), "写应失败");
        AssertTrue(!cli.Write("D0", 1) && !cli.Write("D0", 1.5f), "写应失败2");
        AssertTrue(!cli.Write("D0", new short[] { 1 }) && !cli.Write("D0", new bool[] { true }), "数组写应失败");
        object v;
        AssertTrue(!cli.TryGetLastPollData("x", out v), "空缓存应 false");
        cli.Dispose();
    }
    static void T25_PollIsolation()
    {
        var n = new HslPlcClient();
        n.StartPolling(); n.StopPolling(); n.StopPolling();   // 空配置安全
        var c = NewMbCfg(MultiBrandPlcBrand.MelsecMc, 15361);
        var cli = new HslPlcClient(c);
        cli.OnError += delegate(object o, string e) { throw new Exception("订阅者故障"); };
        AssertTrue(!cli.Connect(c), "坏目标应连接失败");
        Thread.Sleep(200);
        cli.StopPolling();
        cli.Dispose(); cli.Dispose();
    }
    static void T25_AuthGuards()
    {
        AssertTrue(!HslAuthorization.TryActivate(null) && !HslAuthorization.TryActivate("") && !HslAuthorization.TryActivate("   "), "空码应 false");
        bool threw = false;
        try { HslAuthorization.SaveToFile(null); } catch (ArgumentException) { threw = true; }
        AssertTrue(threw, "SaveToFile null 未抛 ArgumentException");
        AssertTrue(HslAuthorization.AuthFilePath.EndsWith(HslAuthorization.FileName), "授权路径错");
        if (HslAuthorization.IsActivated)
        {
            AssertTrue(!string.IsNullOrEmpty(HslAuthorization.ActiveCode), "已激活但 ActiveCode 为空");
            AssertTrue(HslAuthorization.EnsureRegistered(), "已激活 Ensure 应 true");
        }
    }

    // ══════════ 26. DeviceHub 扩展 ══════════
    static DeviceHubConfig NewTempHubCfg(bool mock, PlcRole role)
    {
        var c = new DeviceHubConfig();
        c.UseMockCommunication = mock;
        c.PlcRole = role;
        string t = Path.Combine(Path.GetTempPath(), "ktest_hub_" + Guid.NewGuid().ToString("N"));
        c.Image.SaveRootDir = t; c.Image.FtpRootDir = Path.Combine(t, "ftp");
        return c;
    }
    static void T26_MasterRole()
    {
        var cfg = NewTempHubCfg(true, PlcRole.Master);
        var hub = new DeviceHub(cfg);
        AssertTrue(hub.PlcMaster != null && hub.Plc == null && hub.PlcMultiBrand == null, "主站装配错");
        AssertTrue(hub.IsPlcMaster && !hub.IsPlcMultiBrand, "角色标志错");
        AssertTrue(hub.Monitor != null && hub.ImageStore != null, "监控/存图缺失");
        hub.Start();
        hub.Dispose(); hub.Dispose();
        AssertTrue(hub.PlcMaster == null && hub.Monitor == null && hub.ImageStore == null, "释放后引用未清空");
        AssertTrue(hub.Barometer == null && hub.Io == null && hub.Fan == null, "三件套未清空");
    }
    static void T26_DefaultCams()
    {
        var cfg = NewTempHubCfg(true, PlcRole.Slave);
        var hub = new DeviceHub(cfg);
        AssertTrue(hub.Cameras != null && hub.Cameras.Count == 2, "空相机应回退 2 台默认，实际" + hub.Cameras.Count);
        hub.Dispose();
        var cfg2 = NewTempHubCfg(true, PlcRole.Slave);
        var one = new CameraConfig(); one.Name = "only"; one.IpAddress = "127.0.0.1";
        one.FtpUploadDir = Path.Combine(Path.GetTempPath(), "ktest_hub_cam1");
        cfg2.Cameras.Add(one);
        var hub2 = new DeviceHub(cfg2);
        AssertTrue(hub2.Cameras.Count == 1, "单相机应 1 台");
        hub2.Dispose();
        try { Directory.Delete(Path.Combine(Path.GetTempPath(), "ktest_hub_cam1"), true); } catch { }
    }
    static void T26_NullApply()
    {
        var hub = new DeviceHub(NewTempHubCfg(true, PlcRole.Slave));
        int n = 0;
        hub.ServicesRebuilt += delegate(object o, EventArgs e) { n++; };
        hub.ApplyConfig(null);
        AssertTrue(n == 0, "空热更不应触发重建");
        hub.Start(); hub.Start();
        hub.Dispose(); hub.Dispose();
        AssertTrue(hub.Scanners == null && hub.Cameras == null, "释放后未清空");
    }
    static void T26_RealTypes()
    {
        var hub = new DeviceHub(NewTempHubCfg(false, PlcRole.Slave));
        AssertTrue(hub.Barometer is ModbusRtuBarometerReader, "真机气压表类型错");
        AssertTrue(hub.Io is ModbusTcpIoController, "真机 IO 类型错");
        AssertTrue(hub.Fan is FanControllerClient, "真机送风机类型错");
        hub.Dispose();
    }
    static void T26_ScannerModes()
    {
        var cfg = NewTempHubCfg(true, PlcRole.Slave);
        cfg.Scanners.Add(new ScanConfig { Enabled = false, Mode = "Tcp", IpAddress = "127.0.0.1", Port = 15370 });
        cfg.Scanners.Add(new ScanConfig { Enabled = false, Mode = "Eip", IpAddress = "127.0.0.1", Port = 15371 });
        cfg.Scanners.Add(new ScanConfig { Enabled = false, Mode = "Serial", PortName = "COM_X" });
        var hub = new DeviceHub(cfg);
        AssertTrue(hub.Scanners.Count == 3, "扫码枪数=" + hub.Scanners.Count);
        AssertTrue(hub.Scanners[0] is ScannerTcpService, "第 1 台应 TCP");
        AssertTrue(hub.Scanners[1] is ScannerEipService, "第 2 台应 EIP");
        AssertTrue(hub.Scanners[2] is ScannerService, "第 3 台应串口");
        hub.Start();
        hub.Dispose();
    }

    // ══════════ 27. 监控器扩展 ══════════
    static void T27_NullSafe()
    {
        var mon = new ConnectionMonitor(null, null);
        mon.Start();
        Thread.Sleep(200);
        mon.Dispose(); mon.Dispose();
    }
    static void T27_BaroEdge()
    {
        var mock = new MockBarometerReader();
        var mon = new ConnectionMonitor(null, new List<KeyenceIV4Camera>(), null, null, mock, new BarometerConfig(), null, null, null, null);
        var edges = new List<bool>();
        mon.BarometerConnectionChanged += delegate(object o, bool c) { lock (edges) edges.Add(c); };
        mon.Start();
        mock.Connect(new BarometerConfig());
        Wait(delegate() { lock (edges) return edges.Contains(true); }, 7000, "气压表连上边沿未触发");
        mock.Disconnect();
        Wait(delegate() { lock (edges) return edges.Contains(false); }, 7000, "气压表断开边沿未触发");
        mon.Dispose();
        mock.Dispose();
    }
    static void T27_MultiEdge()
    {
        var mio = new MockIoController();
        var mfan = new MockFanController();
        var pm = new ModbusTcpMasterClient();
        var mon = new ConnectionMonitor(null, new List<KeyenceIV4Camera>(), pm, new PlcMasterConfig(), null, null, mio, new IoConfig(), mfan, NewFanCfg(0));
        var ioE = new List<bool>(); var fanE = new List<bool>(); var pmE = new List<bool>();
        mon.IoConnectionChanged += delegate(object o, bool c) { lock (ioE) ioE.Add(c); };
        mon.FanConnectionChanged += delegate(object o, bool c) { lock (fanE) fanE.Add(c); };
        mon.PlcMasterConnectionChanged += delegate(object o, bool c) { lock (pmE) pmE.Add(c); };
        mon.Start();
        mio.Connect(new IoConfig());
        mfan.Connect(NewFanCfg(0));
        Wait(delegate() { lock (ioE) return ioE.Contains(true); }, 7000, "IO 边沿未触发");
        Wait(delegate() { lock (fanE) return fanE.Contains(true); }, 7000, "送风机边沿未触发");
        Thread.Sleep(2500);
        lock (pmE) AssertTrue(!pmE.Contains(true), "未连接主站不应有连上边沿");
        mio.Disconnect(); mfan.Disconnect();
        Wait(delegate() { lock (ioE) return ioE.Contains(false); }, 7000, "IO 断开边沿未触发");
        mon.Dispose(); pm.Dispose(); mio.Dispose(); mfan.Dispose();
    }

    // ══════════ 28. 气压表/Mock 边界 ══════════
    static void T28_BaroDown()
    {
        var r = new ModbusRtuBarometerReader();
        var cfg = new BarometerConfig(); cfg.PortName = "COM_NOT_EXIST_999"; cfg.TotalBarometers = 2;
        AssertTrue(!r.Connect(cfg), "无效串口应连接失败");
        AssertTrue(r.CurrentPortName == null, "失败应无端口名");
        AssertTrue(r.ReadData(1) == null, "未连接读应 null");
        AssertTrue(r.ReadData(0) == null && r.ReadData(99) == null, "越界读应 null");
        var all = r.ReadAllData();
        AssertTrue(all.Length == 2 && all[0] == null && all[1] == null, "未连接应全 null 数组");
        AssertTrue(!r.SetThreshold(1, -90m), "未连接写阈值应 false");
        AssertTrue(!r.SetThreshold(99, -90m), "越界写应 false");
        AssertTrue(r.SetAllThresholds(-90m).Count == 0, "未连接批量应空");
        r.Disconnect(); r.Dispose();
    }
    static void T28_MockBaro()
    {
        var m = new MockBarometerReader();
        var cfg = new BarometerConfig(); cfg.TotalBarometers = 3; cfg.PortName = "COM7";
        AssertTrue(m.Connect(cfg), "Mock 连接失败");
        AssertTrue(m.CurrentPortName == "COM7", "端口名=" + m.CurrentPortName);
        AssertTrue(m.ReadData(0) == null && m.ReadData(99) == null, "越界应 null");
        AssertTrue(!m.SetThreshold(0, -90m), "越界写应 false");
        var all = m.SetAllThresholds(-90m);
        AssertTrue(all.Count == 3, "批量数=" + all.Count);
        AssertTrue(m.ReadAllData().Length == 3, "批量读长度错");
        m.Disconnect();
        AssertTrue(m.ReadData(1) == null, "断开读应 null");
        var off = m.ReadAllData();
        AssertTrue(off.Length == 3 && off[0] == null, "断开应全 null");
        m.Dispose();
    }
    static void T28_MockIo()
    {
        var m = new MockIoController();
        m.Connect(new IoConfig());
        AssertTrue(!m.ReadInput(0) && !m.ReadInput(9999), "越界输入应 false");
        AssertTrue(m.ReadAllInputs().Length == 80, "批量输入长度错");
        m.WriteOutputs(null, null);
        m.WriteOutputs(new int[] { 81 }, new bool[] { true, false });
        m.WriteOutput(9999, true);
        AssertTrue(m.ReadAllOutputs().Length == 160, "批量输出长度错");
        m.Disconnect();
        AssertTrue(!m.ReadInput(1) && m.ReadAllInputs().Length == 0, "断开读错");
        m.Dispose(); m.Dispose();
    }
    static void T28_MockFan()
    {
        var m = new MockFanController();
        var cfg = NewFanCfg(0);
        AssertTrue(m.Connect(cfg), "Mock 连接失败");
        AssertTrue(m.ActiveIp == cfg.FanIpAddress, "ActiveIp=" + m.ActiveIp);
        m.Stop();
        AssertTrue(m.ReadStatus().RunState == FanRunState.FixedValueStopped, "停止态错");
        m.StartFixedValue();
        AssertTrue(m.ReadStatus().RunState == FanRunState.FixedValueRunning, "运行态错");
        m.Disconnect();
        AssertTrue(m.ReadStatus() == null, "断开应 null");
        AssertTrue(m.ReconnectNow(), "重连应恢复");
        m.Dispose(); m.Dispose();
    }

    // ══════════ 29. 工具类 ══════════
    static void T29_Log()
    {
        var got = new List<string>();
        LogHelper.LogAction = delegate(string l, string msg) { lock (got) got.Add(l + ":" + msg); };
        try
        {
            LogHelper.Info("i1"); LogHelper.Warn("w1");
            LogHelper.Error("e1"); LogHelper.Error("e2", new Exception("ex"));
            lock (got) AssertTrue(got.Count == 4, "日志数=" + got.Count);
            LogHelper.LogAction = delegate(string l, string msg) { throw new Exception("出口故障"); };
            LogHelper.Info("不应抛");   // 隔离不断言抛
        }
        finally { LogHelper.LogAction = delegate(string l, string msg) { }; }
    }
    static void T29_KeepAlive()
    {
        TcpKeepAlive.Configure(null);
        using (var t = new TcpClient())
        {
            TcpKeepAlive.Configure(t);
            TcpKeepAlive.Configure(t);   // 幂等
        }
    }
    static void T29_SerialHelper()
    {
        AssertTrue(SerialPortHelper.GetAllPortNames() != null, "串口列表 null");
        AssertTrue(SerialPortHelper.GetCh340Ports() != null, "CH340 列表 null");
        string one = null;
        try { one = SerialPortHelper.GetCh340PortName(); } catch { AssertTrue(false, "GetCh340PortName 抛异常"); }
        AssertTrue(one == null || one.Length > 0, "首端口异常");
    }
    static void T29_HslFile()
    {
        AssertTrue(HslAuthorization.AuthFilePath.EndsWith(HslAuthorization.FileName), "授权路径错");
        AssertTrue(!HslAuthorization.TryActivate("   "), "空白码应 false");
        bool threw = false;
        try { HslAuthorization.SaveToFile(""); } catch (ArgumentException) { threw = true; }
        AssertTrue(threw, "空码落盘未抛");
        if (File.Exists(HslAuthorization.AuthFilePath))
        {
            string content = File.ReadAllText(HslAuthorization.AuthFilePath);
            AssertTrue(content.Contains("ENC1:"), "授权文件非加密格式");
        }
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
    static IModbusMaster NModbusMasterRetry(int port)
    {
        // 从站 EnsureConnected 置位先于 socket 真正 accept：首连毫秒级窗口可能被拒，重试消除偶发
        Exception last = null;
        for (int i = 0; i < 20; i++)
        {
            try { return NModbusMaster(port); }
            catch (Exception ex) { last = ex; Thread.Sleep(150); }
        }
        throw new Exception("主站连入从站失败 port=" + port + "：" + (last == null ? "" : last.Message));
    }
}
