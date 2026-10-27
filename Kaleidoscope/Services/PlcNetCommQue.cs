using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Collections.Concurrent;
using HslCommunication;
using HslCommunication.Profinet.Melsec;
using HslCommunication.Profinet.Omron;
using HslCommunication.Core;
using HslCommunication.Core.Device;
using HslCommunication.Profinet.Freedom;
using HslCommunication.Profinet.Inovance;
using HslCommunication.Profinet.Siemens;
using Kaleidoscope.Utils;

namespace Kaleidoscope.Services
{
    // =====================================================================================
    // PLC / 通用 TCP 通讯队列（自 HJVision 生产工程下沉进库，行为与原实现一字不差）。
    // -------------------------------------------------------------------------------------
    // 【干什么】把「多品牌 PLC 读写 + 裸 TCP 外发」统一收进一个常驻消费线程串行执行：
    //   业务层只管把请求 Enqueue 进队列，消费线程按到达顺序逐个执行，天然规避多线程
    //   共用一条 PLC 连接的竞态；读请求必回填结果槽并 Set 句柄，发起方永不死等。
    //
    // 【为什么是队列而不是锁】PLC 一次读写往返 10~200ms，若用锁串行化，UI/脚本线程
    //   会被排队卡住；队列把"发起"与"执行"解耦，发起方立即返回（异步写）或只等自己
    //   那一条（同步读写带超时）。这与库内 HslPlcClient 的锁范式是两种取舍：
    //   队列适合"高频、多来源、要求严格顺序"的生产节拍，锁适合"低频、单来源"的配置期操作。
    //
    // 【槽位语义】DevID = 设备下标（配置表行号），槽位数组按下标对齐；停用/删除只置 null
    //   占位不断号（删中间行会让后继 DevID 错位）。结果槽 CamName 是 int（0-9 相机，
    //   10 气密，11 测试页，12 心跳/同步垃圾槽）——库里不引入相机概念，纯整数槽位。
    //
    // 【自动重连】读写失败只调 RequestReconnect（内部去重 + 5 秒退避），执行体跑在消费
    //   线程（与读写天然串行，不加锁）；心跳探针失败走旁路跳过退避（5 秒一轮天然限流，
    //   否则"固定周期触发 + 按完成时间退避 + 单次执行超周期"三者凑成活锁，链路永久无人救）。
    //
    // 【Mock】演示模式的短路判断走 Utils/MockBridge（宿主注入），库本身不依赖任何
    //   业务工程；未注入时 MockBridge 恒为"未启用"，即真实通讯。
    //
    // 【线程模型】单消费线程（Highest 优先级 + 前台线程，关闭靠置旗 + 唤醒，禁 Abort）；
    //   事件（PlcConnChanged）在消费线程触发，UI 订阅方自行 Invoke 回 UI 线程。
    //
    // 【依赖】HslCommunication（多品牌 PLC 协议）+ 本库 TcpChannel（裸 TCP/EIP 外发）。
    // =====================================================================================
    public class PlcNetCommQue
    {
        private readonly ConcurrentQueue<FlashPlcWRDataInfo> _quePLC;
        private readonly ManualResetEvent _mre;
        private volatile bool _mShut;
        Thread t;

        List<IReadWriteNet> _PlcDevices = new List<IReadWriteNet>();
        List<TcpChannel> _TcpDevices = new List<TcpChannel>();

        /// <summary>
        /// PLC 连接变化事件（消费线程里重连/重建出结果即触发，参数=设备下标 + 是否在线）。
        /// 触发线程是队列消费线程，订阅方涉 UI 必须封送回刷。
        /// </summary>
        public static event Action<int, bool> PlcConnChanged;

        private static readonly HashSet<int> s_reconnecting = new HashSet<int>();
        private static readonly Dictionary<int, DateTime> s_lastReconn = new Dictionary<int, DateTime>();
        private static readonly object s_reconnLock = new object();
        /// <summary>重连退避间隔（秒）：一次执行后 N 秒内不再对同设备重连。</summary>
        private const int ReconnectCooldownSec = 5;
        private static readonly Dictionary<int, bool> s_lastReconnOk = new Dictionary<int, bool>();
        private static readonly Dictionary<int, int> s_reconnFailStreak = new Dictionary<int, int>();
        /// <summary>连续重连失败补记一笔的间隔（次）：12 次 × 5 秒退避 ≈ 60 秒一条。</summary>
        private const int ReconnectFailLogEvery = 12;
        private static readonly Dictionary<int, int> s_heartbeatFailStreak = new Dictionary<int, int>();
        /// <summary>心跳连续失败补警告的间隔（次）：12 次 × 5 秒心跳 ≈ 60 秒一条（另有时间兜底）。</summary>
        private const int HeartbeatWarnEvery = 12;
        private static readonly Dictionary<int, DateTime> s_lastHeartbeatWarn = new Dictionary<int, DateTime>();
        /// <summary>"应用层读不通"警告最小间隔（秒）：次数与时间双门限都过才记一笔。</summary>
        private const int HeartbeatWarnMinIntervalSec = 60;

        private static PlcNetCommQue _plcNetCommQue = new PlcNetCommQue();

        /// <summary>读结果槽（下标=槽号，0-9 相机，10 气密，11 测试页，12 心跳/同步垃圾槽）。</summary>
        public static List<string> mReadResultData { get { return _mReadResultData; } }

        public List<TcpChannel> TcpDevices
        {
            get { return _TcpDevices; }
            set { _TcpDevices = value; }
        }

        // 槽位分配见 mReadResultData 注释。扩容不影响既有 0-9 语义。
        private static List<string> _mReadResultData = new List<string>(new string[13]);

        /// <summary>
        /// PLC 心跳读结果槽位：12（结果无人消费，只借槽走失败驱动链）。
        /// 脚本同步读写借用本槽当垃圾结果槽（结果走各自 SyncBox），心跳警告靠显式标记区分。
        /// </summary>
        public const int HeartbeatSlotIndex = 12;

        /// <summary>
        /// PLC 心跳读取地址（纯函数）：各品牌通用无害单字 D0（只读不写）；
        /// 欧姆龙 CIP 按标签读写、无通用地址，回 null 表跳过；
        /// 西门子 S7 无 D0，读 M0（只读 MW0 无副作用）。
        /// </summary>
        public static string HeartbeatAddress(PlcType type)
        {
            if (type == PlcType.OmronCipNet) return null;
            if (type == PlcType.SiemensS7Net) return "M0";
            return "D0";
        }

        private PlcNetCommQue()
        {
            _quePLC = new ConcurrentQueue<FlashPlcWRDataInfo>();
            _mre = new ManualResetEvent(false);
        }

        /// <summary>单例。</summary>
        public static PlcNetCommQue Instance()
        {
            return _plcNetCommQue;
        }

        /// <summary>启动消费线程（初始化调一次，防重入；Close 后重调可复活）。</summary>
        public void Register()
        {
            if (t != null && t.IsAlive) return;
            _mShut = false;

            t = new Thread(new ThreadStart(WriteReadPlc));
            t.Priority = ThreadPriority.Highest;
            t.IsBackground = false;
            t.Start();
        }

        public bool ConnNewPLCDev(PlcType DveType, string PlcIP, int PlcPort)
        {
            IReadWriteNet dev = BuildPlcDevice(DveType, PlcIP, PlcPort);
            if (dev == null) return false;
            bool ret = false;
            // 无论连上与否都 Add（DevID=列表下标，跳过会后继错位）。
            try
            {
                DeviceTcpNet nd = dev as DeviceTcpNet;
                if (nd != null) ret = nd.ConnectServer().IsSuccess;
            }
            catch { ret = false; }
            // 失败对象立即关 socket（与 ConnPlcDevAt 首连失败分支同口径，2026-10-27 现场
            // 血泪）：半开连接占 PLC 会话槽；ConnectClose 幂等无害，对象本身仍 Add 占槽。
            if (!ret)
            {
                try { DeviceTcpNet ndFail = dev as DeviceTcpNet; if (ndFail != null) ndFail.ConnectClose(); }
                catch { }
            }
            _PlcDevices.Add(dev);
            return ret;
        }

        /// <summary>
        /// 预占 PLC 槽位（启动并行建连用：先按台数占 null 槽，后续按下标原子赋值，
        /// DevID 与配置表下标恒对齐）。必须在 Register() 之前调完。
        /// </summary>
        public void PreparePlcSlots(int count)
        {
            lock (_PlcDevices)
            {
                try
                {
                    _PlcDevices.Clear();
                    for (int i = 0; i < count; i++) _PlcDevices.Add(null);
                }
                catch (Exception ex)
                {
                    try { LogHelper.Info(" PLC槽位预占失败：" + ex.Message); }
                    catch { }
                }
            }
        }

        /// <summary>
        /// 只增不减补齐槽位（动态加 PLC 用：已有槽位保留不断连，不足尾部追加 null。
        /// 严禁用 Clear 重建——那会丢在线连接，全组闪断）。
        /// </summary>
        public void EnsurePlcSlots(int count)
        {
            lock (_PlcDevices)
            {
                try
                {
                    if (count < 0) count = 0;
                    while (_PlcDevices.Count < count) _PlcDevices.Add(null);
                }
                catch (Exception ex)
                {
                    try { LogHelper.Info(" PLC槽位补齐失败：" + ex.Message); }
                    catch { }
                }
            }
        }

        /// <summary>
        /// 裁掉尾部多余槽位（动态删 PLC 用：只允许删末尾，中间行停用不断号。
        /// 被裁槽先 ConnectClose，残留读写由消费侧空槽速败处理）。
        /// </summary>
        public void TruncatePlcSlots(int count)
        {
            lock (_PlcDevices)
            {
                try
                {
                    if (count < 0) count = 0;
                    while (_PlcDevices.Count > count)
                    {
                        int tail = _PlcDevices.Count - 1;
                        try
                        {
                            DeviceTcpNet nd = _PlcDevices[tail] as DeviceTcpNet;
                            if (nd != null) nd.ConnectClose();
                        }
                        catch { }
                        _PlcDevices.RemoveAt(tail);
                    }
                }
                catch (Exception ex)
                {
                    try { LogHelper.Info(" PLC槽位裁剪失败：" + ex.Message); }
                    catch { }
                }
            }
        }

        /// <summary>查看单个槽位当前对象（热更"目标未变则保留"用：null=槽空需建连）。</summary>
        public object PeekPlcSlot(int index)
        {
            lock (_PlcDevices)
            {
                try
                {
                    if (index < 0 || index >= _PlcDevices.Count) return null;
                    return _PlcDevices[index];
                }
                catch { return null; }
            }
        }

        /// <summary>断开并清空单个槽位（动态停用/改目标用：不断号，只置 null）。</summary>
        public void DisconnectPlcSlotAt(int index)
        {
            lock (_PlcDevices)
            {
                try
                {
                    if (index < 0 || index >= _PlcDevices.Count) return;
                    try
                    {
                        DeviceTcpNet nd = _PlcDevices[index] as DeviceTcpNet;
                        if (nd != null) nd.ConnectClose();
                    }
                    catch { }
                    _PlcDevices[index] = null;
                }
                catch (Exception ex)
                {
                    try { LogHelper.Info(" PLC槽位" + index + "断开失败：" + ex.Message); }
                    catch { }
                }
            }
        }

        /// <summary>
        /// 按下标建连 PLC（启动并行用：不 Add 改按 index 赋值；首连失败就地再试一次，
        /// 间隔 500ms。PLC 网口协议栈晚就绪时单次试即判死是"Ping 通但未连接"来源之一）。
        /// siemensSeries：西门子系列名（缺省 null 即 S1500）。
        /// </summary>
        public bool ConnPlcDevAt(int index, PlcType DveType, string PlcIP, int PlcPort, string siemensSeries = null)
        {
            // Mock演示：免建连直接判通（演示机无 PLC，建连重试只会拖慢启动）。
            if (MockBridge.IsEnabled)
            {
                try { LogHelper.Info(" [Mock演示] PLC" + (index + 1) + "免建连已连通（" + PlcIP + ":" + PlcPort + "，不接硬件）。"); }
                catch { }
                return true;
            }
            IReadWriteNet dev = null;
            try { dev = BuildPlcDevice(DveType, PlcIP, PlcPort, siemensSeries); }
            catch (Exception exBuild)
            {
                try { LogHelper.Info(" PLC设备" + index + "建对象失败：" + exBuild.Message); }
                catch { }
                return false;
            }
            if (dev == null) return false;
            bool ret = false;
            try
            {
                DeviceTcpNet nd = dev as DeviceTcpNet;
                if (nd != null)
                {
                    for (int r = 0; r < 2 && !ret; r++)
                    {
                        try { ret = nd.ConnectServer().IsSuccess; }
                        catch { ret = false; }
                        if (!ret && r == 0)
                        {
                            try { Thread.Sleep(500); }
                            catch { }
                        }
                    }
                }
            }
            catch { ret = false; }
            if (!ret)
            {
                // 离线种子：首连失败记"已知离线"，心跳/业务读通后的成功边沿才认得这是"恢复"。
                try { lock (s_reconnLock) { s_lastReconnOk[index] = false; } }
                catch { }
                // 失败对象立即关 socket（2026-10-27 现场血泪）：ConnectServer 失败（FINS 握手
                // 超时等）时 TCP 可能已经建连，不关就留一个半开连接挂在 PLC 上——欧姆龙
                // FINS/TCP 会话槽有限，反复失败重试（启动/热更每轮 2 连试）把槽占满后，PLC
                // 对一切新连接的握手帧一律不响应：能 ping 通、TCP 能连、FINS 通讯全断，
                // 只有重启 PLC 才能救。ConnectClose 幂等，无害；对象本身仍留槽（重连链
                // RunReconnect 按"ConnectClose+ConnectServer"复用，不换引用）。
                try { DeviceTcpNet ndFail = dev as DeviceTcpNet; if (ndFail != null) ndFail.ConnectClose(); }
                catch { }
            }
            lock (_PlcDevices)
            {
                try
                {
                    if (index >= 0 && index < _PlcDevices.Count) _PlcDevices[index] = dev;
                    else
                    {
                        try { LogHelper.Info(" PLC设备" + index + "槽位越界（槽数" + _PlcDevices.Count + "），连接结果丢弃"); }
                        catch { }
                        return false;
                    }
                }
                catch (Exception exSlot)
                {
                    try { LogHelper.Info(" PLC设备" + index + "槽位赋值失败：" + exSlot.Message); }
                    catch { }
                    return false;
                }
            }
            // 首连结果记账（2026-10-27 现场血泪）：原来首连成功无人记 s_lastReconnOk=true
            // （设计假设"启动组终算已刷过 UI"——WinForms 口径，WPF 测试页查库拿不到 true）。
            // 成败都记：成功=true（后续读通不再重复触发"恢复"边沿，保持日常零事件收敛），
            // 失败=false（与既有失败记账同口径）。
            try { lock (s_reconnLock) { s_lastReconnOk[index] = ret; } } catch { }
            return ret;
        }

        /// <summary>
        /// 解析西门子系列名（纯函数）：大小写不敏感；空/非法回落 S1500。
        /// </summary>
        public static SiemensPLCS ParseSiemensSeries(string series)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(series))
                    return (SiemensPLCS)Enum.Parse(typeof(SiemensPLCS), series.Trim(), true);
            }
            catch { }
            return SiemensPLCS.S1500;
        }

        /// <summary>
        /// 按配置建 PLC 客户端对象（只建不连）。品牌分支只收敛在这里，加新品牌改这一处。
        /// ConnectTimeOut 一律 2 秒（HSL 默认约 10 秒，不在线时启动期每台卡 10 秒）。
        /// </summary>
        private static IReadWriteNet BuildPlcDevice(PlcType DveType, string PlcIP, int PlcPort, string siemensSeries = null)
        {
            switch (DveType)
            {
                case PlcType.MelsecA1ENet:
                    MelsecA1ENet mPlcA1E = new MelsecA1ENet(PlcIP, PlcPort);
                    mPlcA1E.ConnectTimeOut = 2000;
                    return mPlcA1E;
                case PlcType.MelsecMcNet:
                    MelsecMcNet mPlcMc = new MelsecMcNet(PlcIP, PlcPort);
                    mPlcMc.ConnectTimeOut = 2000;
                    return mPlcMc;
                case PlcType.InovanceTcpNet:
                    InovanceTcpNet mPlcInovanceAMTcp = new InovanceTcpNet(PlcIP, PlcPort);
                    mPlcInovanceAMTcp.Series = InovanceSeries.H5U;
                    mPlcInovanceAMTcp.ConnectTimeOut = 2000;
                    return mPlcInovanceAMTcp;
                case PlcType.OmronFinsNet:
                    OmronFinsNet mPlcOmronFins = new OmronFinsNet(PlcIP, PlcPort);
                    mPlcOmronFins.ConnectTimeOut = 2000;
                    mPlcOmronFins.PlcType = OmronPlcType.CSCJ;
                    return mPlcOmronFins;
                case PlcType.OmronCipNet:
                    OmronCipNet mPlcOmronCip = new OmronCipNet(PlcIP, PlcPort);
                    mPlcOmronCip.ConnectTimeOut = 2000;
                    mPlcOmronCip.Slot = 0;
                    return mPlcOmronCip;
                case PlcType.SiemensS7Net:
                    // S7 协议端口固定 102（构造无端口参数，ini 照填 102）。
                    SiemensS7Net mPlcSiemens = new SiemensS7Net(ParseSiemensSeries(siemensSeries), PlcIP);
                    mPlcSiemens.ConnectTimeOut = 2000;
                    return mPlcSiemens;
                default:
                    return null;
            }
        }

        /// <summary>
        /// 关闭通讯消费线程（置旗 + 唤醒，线程醒来自然退出；禁 Abort，禁撕 HSL 读写中状态）。
        /// </summary>
        public void FlashPlcNetThreadClose()
        {
            try
            {
                _mShut = true;
                _mre.Set();
            }
            catch
            {
            }
            // 退出时逐槽主动断开（2026-10-27 现场血泪）：进程退出虽然 OS 兜底会关 socket，
            // 但主动关时序确定——尤其 FINS/Modbus 这类对端有会话槽上限的设备，滞留连接
            // 会占死 PLC 侧会话，下次启动明明 PLC 正常却永远连不上。
            try
            {
                lock (_PlcDevices)
                {
                    for (int i = 0; i < _PlcDevices.Count; i++)
                    {
                        try { (_PlcDevices[i] as DeviceTcpNet)?.ConnectClose(); }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private void WriteReadPlc()
        {
            while (!_mShut)
            {
                try
                {
                    _mre.WaitOne();
                    if (_mShut) break;

                    // 先 Reset 再消费（防"消费完→Reset"间隙的 Set 被吞，等待方永久死等）。
                    _mre.Reset();

                    FlashPlcWRDataInfo plcInfo;
                    while (_quePLC.Count > 0 && _quePLC.TryDequeue(out plcInfo))
                    {
                        // 单条异常只跳过本条，绝不冲出循环杀死消费线程；读请求必 Set 句柄。
                        try
                        {
                            switch (plcInfo.PlcWRswitch)
                            {
                                case PlcWRswitchStr.Write:
                                    RunPlcWrite(plcInfo);
                                    break;
                                case PlcWRswitchStr.Read:
                                    RunPlcRead(plcInfo);
                                    break;
                                case PlcWRswitchStr.TcpSend:
                                    RunTcpSend(plcInfo);
                                    break;
                                case PlcWRswitchStr.Reconnect:
                                    RunReconnect(plcInfo);
                                    break;
                                case PlcWRswitchStr.Rebuild:
                                    RunRebuild(plcInfo);
                                    break;
                                default:
                                    break;
                            }
                        }
                        catch (Exception exItem)
                        {
                            LogHelper.Info(" PLC/TCP队列单条处理异常(" + plcInfo.PlcWRswitch + " " + plcInfo.PlcAddress + ")：" + exItem.Message);
                            TrySetWaitHandle(plcInfo);
                        }
                    }
                }
                catch (Exception exLoop)
                {
                    LogHelper.Info(" PLC通讯线程循环异常：" + exLoop.Message);
                }
            }
        }

        /// <summary>安全 Set 等待句柄（异常路径兜底，防发起方永久死等）。</summary>
        private static void TrySetWaitHandle(FlashPlcWRDataInfo info)
        {
            try { if (info != null && info.ThreadWaitHandle != null) info.ThreadWaitHandle.Set(); } catch { }
        }

        private void EnqueuePlcInfo(FlashPlcWRDataInfo PlcObj)
        {
            _quePLC.Enqueue(PlcObj);
            _mre.Set();
        }

        /// <summary>
        /// Mock 演示读服务（5 个异步读入口共用）：往结果槽填内存值并唤醒等待方，
        /// 与 RunPlcRead 的"填槽+Set"同语义，调用方零感知。越界槽直接 Set 唤醒。
        /// </summary>
        private static void ServeMockRead(int slot, int devID, string address, string kind, AutoResetEvent wh)
        {
            try
            {
                string val = MockBridge.FakePlcRead(devID, address, kind);
                try
                {
                    if (slot >= 0 && slot < _mReadResultData.Count) _mReadResultData[slot] = val;
                }
                catch { }
                try { if (wh != null) wh.Set(); } catch { }
            }
            catch { try { if (wh != null) wh.Set(); } catch { } }
        }

        #region 向PLC中写数据

        public static void PlcNetWriteM_bool(int ccdName, string PlcAddress, bool[] PlcData, int devID)
        {
            // Mock：fire-and-forget 写只进内存字典（读回校验能对上），不碰硬件。
            if (MockBridge.IsEnabled) { MockBridge.FakePlcWrite(devID, PlcAddress, PlcData == null ? "" : string.Join(",", Array.ConvertAll(PlcData, delegate (bool x) { return x.ToString(); }))); return; }
            Instance().EnqueuePlcInfo(new FlashPlcWRDataInfo
            {
                CamName = ccdName,
                PlcWRswitch = PlcWRswitchStr.Write,
                PlcAddress = PlcAddress,
                PlcDataTypeWR = DataTypeWR.IsBool,
                WriteDataBool = PlcData,
                DevID = devID,
            });
        }

        public static void PlcNetWriteD_double(int ccdName, string PlcAddress, double[] PlcData, int devID)
        {
            if (MockBridge.IsEnabled) { MockBridge.FakePlcWrite(devID, PlcAddress, PlcData == null ? "" : string.Join(",", Array.ConvertAll(PlcData, delegate (double x) { return x.ToString("G17", System.Globalization.CultureInfo.InvariantCulture); }))); return; }
            Instance().EnqueuePlcInfo(new FlashPlcWRDataInfo
            {
                CamName = ccdName,
                PlcWRswitch = PlcWRswitchStr.Write,
                PlcAddress = PlcAddress,
                PlcDataTypeWR = DataTypeWR.IsDouble,
                WriteDataDouble = PlcData,
                DevID = devID,
            });
        }

        public static void PlcNetWriteD_float(int ccdName, string PlcAddress, float[] PlcData, int devID)
        {
            if (MockBridge.IsEnabled) { MockBridge.FakePlcWrite(devID, PlcAddress, PlcData == null ? "" : string.Join(",", Array.ConvertAll(PlcData, delegate (float x) { return x.ToString("G9", System.Globalization.CultureInfo.InvariantCulture); }))); return; }
            Instance().EnqueuePlcInfo(new FlashPlcWRDataInfo
            {
                CamName = ccdName,
                PlcWRswitch = PlcWRswitchStr.Write,
                PlcAddress = PlcAddress,
                PlcDataTypeWR = DataTypeWR.IsFloat,
                WriteDataFloat = PlcData,
                DevID = devID,
            });
        }

        public static void PlcNetWriteD_Int(int ccdName, string PlcAddress, int[] PlcData, int devID)
        {
            if (MockBridge.IsEnabled) { MockBridge.FakePlcWrite(devID, PlcAddress, PlcData == null ? "" : string.Join(",", Array.ConvertAll(PlcData, delegate (int x) { return x.ToString(); }))); return; }
            Instance().EnqueuePlcInfo(new FlashPlcWRDataInfo
            {
                CamName = ccdName,
                PlcWRswitch = PlcWRswitchStr.Write,
                PlcAddress = PlcAddress,
                PlcDataTypeWR = DataTypeWR.IsInt32,
                WriteDataInt32 = PlcData,
                DevID = devID,
            });
        }

        /// <summary>
        /// 队列化写 UInt32 无符号整字（请求ID/回显ID 类 UDINT，超 21 亿不溢出；
        /// 与 Int32 同字序，位 identical）。
        /// </summary>
        public static void PlcNetWriteD_UInt32(int ccdName, string PlcAddress, uint[] PlcData, int devID)
        {
            if (MockBridge.IsEnabled) { MockBridge.FakePlcWrite(devID, PlcAddress, PlcData == null ? "" : string.Join(",", Array.ConvertAll(PlcData, delegate (uint x) { return x.ToString(); }))); return; }
            Instance().EnqueuePlcInfo(new FlashPlcWRDataInfo
            {
                CamName = ccdName,
                PlcWRswitch = PlcWRswitchStr.Write,
                PlcAddress = PlcAddress,
                PlcDataTypeWR = DataTypeWR.IsUInt32,
                WriteDataUInt32 = PlcData,
                DevID = devID,
            });
        }

        public static void PlcNetWriteD_bool(int ccdName, string PlcAddress, bool[] PlcData, int devID)
        {
            if (MockBridge.IsEnabled) { MockBridge.FakePlcWrite(devID, PlcAddress, PlcData == null ? "" : string.Join(",", Array.ConvertAll(PlcData, delegate (bool x) { return x.ToString(); }))); return; }
            Instance().EnqueuePlcInfo(new FlashPlcWRDataInfo
            {
                CamName = ccdName,
                PlcWRswitch = PlcWRswitchStr.Write,
                PlcAddress = PlcAddress,
                // bool 写入必须标 IsBool（旧版误标 IsInt32 会走 Int32 分支取 null 数组抛异常）。
                PlcDataTypeWR = DataTypeWR.IsBool,
                WriteDataBool = PlcData,
                DevID = devID,
            });
        }

        /// <summary>
        /// 队列化写 Int16 短字（单字 short 专用壳；多字 ASCII 字串走 PlcNetWriteS_String）。
        /// </summary>
        public static void PlcNetWriteD_Int16(int ccdName, string PlcAddress, short[] PlcData, int devID)
        {
            if (MockBridge.IsEnabled) { MockBridge.FakePlcWrite(devID, PlcAddress, PlcData == null ? "" : string.Join(",", Array.ConvertAll(PlcData, delegate (short x) { return x.ToString(); }))); return; }
            Instance().EnqueuePlcInfo(new FlashPlcWRDataInfo
            {
                CamName = ccdName,
                PlcWRswitch = PlcWRswitchStr.Write,
                PlcAddress = PlcAddress,
                PlcDataTypeWR = DataTypeWR.IsInt16,
                WriteDataInt16 = PlcData,
                DevID = devID,
            });
        }

        /// <summary>
        /// 把 ASCII 字符串打包成 PLC 字数组（2字符/字，高字节=先一个字符，不足补空格）。
        /// 非 ASCII（中文/全角）按 '?' 替换，不静默乱码。maxChars 必须>0 偶数（奇数自动+1）。
        /// 返回 short 数组，长度恒 maxChars/2，可直接写入。
        /// </summary>
        public static short[] PackAsciiToWords(string text, int maxChars)
        {
            if (maxChars <= 0) maxChars = 2;
            if ((maxChars & 1) == 1) maxChars++;
            if (text == null) text = "";
            if (text.Length > maxChars) text = text.Substring(0, maxChars);
            int wordCount = maxChars / 2;
            short[] words = new short[wordCount];
            for (int i = 0; i < wordCount; i++)
            {
                char hi = (i * 2 < text.Length) ? text[i * 2] : ' ';
                char lo = (i * 2 + 1 < text.Length) ? text[i * 2 + 1] : ' ';
                if (hi > 0x7F) hi = '?';
                if (lo > 0x7F) lo = '?';
                words[i] = (short)(((hi & 0xFF) << 8) | (lo & 0xFF));
            }
            return words;
        }

        /// <summary>
        /// 队列化写 ASCII 字串（内容区用）：打包后走 Int16 分支，与其它字写入共用队列。
        /// fire-and-forget（完成语义由完成字承担，不要拿本方法当完成信号）。
        /// </summary>
        public static void PlcNetWriteS_String(int ccdName, string plcAddress, string text, int maxChars, int devID)
        {
            if (MockBridge.IsEnabled) { MockBridge.FakePlcWrite(devID, plcAddress, text); return; }
            Instance().EnqueuePlcInfo(new FlashPlcWRDataInfo
            {
                CamName = ccdName,
                PlcWRswitch = PlcWRswitchStr.Write,
                PlcAddress = plcAddress,
                PlcDataTypeWR = DataTypeWR.IsInt16,
                WriteDataInt16 = PackAsciiToWords(text, maxChars),
                DevID = devID,
            });
        }

        #endregion

        #region 读取PLC寄存器数据

        /// <summary>
        /// 队列化读 Int16（触发字/心跳字主用）。isHeartbeat=true 仅心跳线程的真探针传
        /// （失败计入"应用层读不通"警告；业务读传 false，靠调用方日志+重连链）。
        /// </summary>
        public static void PlcNetReadD_int(int ccdName, string PlcAddress, AutoResetEvent mThreadWaitHandle, int devID, bool isHeartbeat = false)
        {
            if (MockBridge.IsEnabled) { ServeMockRead(ccdName, devID, PlcAddress, DataTypeWR.IsInt16.ToString(), mThreadWaitHandle); return; }
            Instance().EnqueuePlcInfo(new FlashPlcWRDataInfo
            {
                CamName = ccdName,
                ThreadWaitHandle = mThreadWaitHandle,
                PlcWRswitch = PlcWRswitchStr.Read,
                PlcAddress = PlcAddress,
                PlcDataTypeWR = DataTypeWR.IsInt16,
                DevID = devID,
                IsHeartbeatProbe = isHeartbeat,
            });
        }

        /// <summary>队列化读 Int32 整字。</summary>
        public static void PlcNetReadD_int32(int ccdName, string PlcAddress, AutoResetEvent mThreadWaitHandle, int devID)
        {
            if (MockBridge.IsEnabled) { ServeMockRead(ccdName, devID, PlcAddress, DataTypeWR.IsInt32.ToString(), mThreadWaitHandle); return; }
            Instance().EnqueuePlcInfo(new FlashPlcWRDataInfo
            {
                CamName = ccdName,
                ThreadWaitHandle = mThreadWaitHandle,
                PlcWRswitch = PlcWRswitchStr.Read,
                PlcAddress = PlcAddress,
                PlcDataTypeWR = DataTypeWR.IsInt32,
                DevID = devID,
            });
        }

        /// <summary>队列化读 UInt32 无符号整字（UDINT 直读无符号，超 21 亿不溢出）。</summary>
        public static void PlcNetReadD_uint32(int ccdName, string PlcAddress, AutoResetEvent mThreadWaitHandle, int devID)
        {
            if (MockBridge.IsEnabled) { ServeMockRead(ccdName, devID, PlcAddress, DataTypeWR.IsUInt32.ToString(), mThreadWaitHandle); return; }
            Instance().EnqueuePlcInfo(new FlashPlcWRDataInfo
            {
                CamName = ccdName,
                ThreadWaitHandle = mThreadWaitHandle,
                PlcWRswitch = PlcWRswitchStr.Read,
                PlcAddress = PlcAddress,
                PlcDataTypeWR = DataTypeWR.IsUInt32,
                DevID = devID,
            });
        }

        public static void PlcNetReadD_float(int ccdName, string PlcAddress, AutoResetEvent mThreadWaitHandle, int devID)
        {
            if (MockBridge.IsEnabled) { ServeMockRead(ccdName, devID, PlcAddress, DataTypeWR.IsFloat.ToString(), mThreadWaitHandle); return; }
            Instance().EnqueuePlcInfo(new FlashPlcWRDataInfo
            {
                CamName = ccdName,
                ThreadWaitHandle = mThreadWaitHandle,
                PlcWRswitch = PlcWRswitchStr.Read,
                PlcAddress = PlcAddress,
                PlcDataTypeWR = DataTypeWR.IsFloat,
                DevID = devID,
            });
        }

        public static void PlcNetReadM_bool(int ccdName, string PlcAddress, AutoResetEvent mThreadWaitHandle, int devID)
        {
            if (MockBridge.IsEnabled) { ServeMockRead(ccdName, devID, PlcAddress, DataTypeWR.IsBool.ToString(), mThreadWaitHandle); return; }
            Instance().EnqueuePlcInfo(new FlashPlcWRDataInfo
            {
                CamName = ccdName,
                ThreadWaitHandle = mThreadWaitHandle,
                PlcWRswitch = PlcWRswitchStr.Read,
                PlcAddress = PlcAddress,
                PlcDataTypeWR = DataTypeWR.IsBool,
                DevID = devID,
            });
        }

        #endregion

        #region 脚本同步读写（生产脚本经这里走队列，不再自建 HSL 连接）

        // 脚本 PLC 固定走 12 号槽当垃圾结果槽（结果走各自 SyncBox，12 槽无人消费）。
        // 禁止在消费线程上调（死锁，内部判死回 false）；脚本跑自建线程上调。
        // 超时按失败回（重连链已在消费侧触发，下轮自动恢复，不用调用方重试）。

        /// <summary>消费线程是否活着（同步帮手前置条件）。</summary>
        public static bool IsConsumerAlive()
        {
            try
            {
                PlcNetCommQue q = Instance();
                return q != null && !q._mShut && q.t != null && q.t.IsAlive;
            }
            catch { return false; }
        }

        /// <summary>等消费线程就绪（最多 timeoutMs，100ms 轮询；超时回 false）。</summary>
        public static bool WaitForConsumer(int timeoutMs)
        {
            try
            {
                if (timeoutMs < 0) timeoutMs = 0;
                if (timeoutMs > 30000) timeoutMs = 30000;
                int waited = 0;
                while (waited < timeoutMs)
                {
                    if (IsConsumerAlive()) return true;
                    try { Thread.Sleep(100); } catch { break; }
                    waited += 100;
                }
                return IsConsumerAlive();
            }
            catch { return false; }
        }

        /// <summary>
        /// 同步读原串（本区核心）：入队→等句柄→拆盒。回 true 且 data 非 "Error" 才算读到数。
        /// </summary>
        private static bool SyncReadRaw(int devID, string address, DataTypeWR type, int timeoutMs, out string data)
        {
            data = "Error";
            try
            {
                if (devID < 0 || string.IsNullOrEmpty(address)) return false;
                if (MockBridge.IsEnabled) { data = MockBridge.FakePlcRead(devID, address, type.ToString()); return true; }
                if (timeoutMs < 500) timeoutMs = 500;
                if (timeoutMs > 30000) timeoutMs = 30000;
                try
                {
                    PlcNetCommQue q0 = Instance();
                    if (q0 != null && q0.t != null && ReferenceEquals(Thread.CurrentThread, q0.t))
                    {
                        try { LogHelper.Info(" PLC同步读拒绝：在消费线程上重入（dev" + devID + " " + address + "），防死锁直接失败"); }
                        catch { }
                        return false;
                    }
                }
                catch { }
                if (!IsConsumerAlive() && !WaitForConsumer(15000))
                {
                    try { LogHelper.Info(" PLC同步读失败：消费线程未就绪（dev" + devID + " " + address + "）"); }
                    catch { }
                    return false;
                }
                PlcSyncBox box = new PlcSyncBox();
                AutoResetEvent wh = new AutoResetEvent(false);
                try
                {
                    Instance().EnqueuePlcInfo(new FlashPlcWRDataInfo
                    {
                        CamName = HeartbeatSlotIndex,
                        ThreadWaitHandle = wh,
                        SyncBox = box,
                        PlcWRswitch = PlcWRswitchStr.Read,
                        PlcAddress = address,
                        PlcDataTypeWR = type,
                        DevID = devID,
                    });
                }
                catch { return false; }
                bool signaled = false;
                try { signaled = wh.WaitOne(timeoutMs); }
                catch { signaled = false; }
                finally { try { wh.Close(); } catch { } }
                if (!signaled) return false;
                if (box == null || !box.Success) return false;
                data = box.Data == null ? "Error" : box.Data;
                return !string.Equals(data, "Error", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>同步读 Int32（失败回 false，调用方本轮跳过）。</summary>
        public static bool PlcSyncReadInt32(int devID, string address, out int value, int timeoutMs = 5000)
        {
            value = 0;
            try
            {
                string raw;
                if (!SyncReadRaw(devID, address, DataTypeWR.IsInt32, timeoutMs, out raw)) return false;
                return int.TryParse((raw ?? "").Trim(), out value);
            }
            catch { value = 0; return false; }
        }

        /// <summary>同步读 Int16（失败回 false）。</summary>
        public static bool PlcSyncReadInt16(int devID, string address, out short value, int timeoutMs = 5000)
        {
            value = 0;
            try
            {
                string raw;
                if (!SyncReadRaw(devID, address, DataTypeWR.IsInt16, timeoutMs, out raw)) return false;
                return short.TryParse((raw ?? "").Trim(), out value);
            }
            catch { value = 0; return false; }
        }

        /// <summary>同步读 UInt32（失败回 false）。</summary>
        public static bool PlcSyncReadUInt32(int devID, string address, out uint value, int timeoutMs = 5000)
        {
            value = 0;
            try
            {
                string raw;
                if (!SyncReadRaw(devID, address, DataTypeWR.IsUInt32, timeoutMs, out raw)) return false;
                return uint.TryParse((raw ?? "").Trim(), out value);
            }
            catch { value = 0; return false; }
        }

        /// <summary>同步读 Float（失败回 false）。</summary>
        public static bool PlcSyncReadFloat(int devID, string address, out float value, int timeoutMs = 5000)
        {
            value = 0;
            try
            {
                string raw;
                if (!SyncReadRaw(devID, address, DataTypeWR.IsFloat, timeoutMs, out raw)) return false;
                return float.TryParse((raw ?? "").Trim(), out value);
            }
            catch { value = 0; return false; }
        }

        /// <summary>同步读 Bool（HSL 回 "True"/"False"，兼容 "1"/"0"；失败回 false）。</summary>
        public static bool PlcSyncReadBool(int devID, string address, out bool value, int timeoutMs = 5000)
        {
            value = false;
            try
            {
                string raw;
                if (!SyncReadRaw(devID, address, DataTypeWR.IsBool, timeoutMs, out raw)) return false;
                string s = (raw ?? "").Trim();
                if (s == "1") { value = true; return true; }
                if (s == "0") { value = false; return true; }
                return bool.TryParse(s, out value);
            }
            catch { value = false; return false; }
        }

        private static bool SyncWriteOnce(int devID, string address, DataTypeWR type,
            Action<FlashPlcWRDataInfo> fill, int timeoutMs)
        {
            // 六个同步写壳的公共后半段（入队→等句柄→拆盒），前半段的 Mock 短路各壳自管
            // （顺序与老版一致：先参数校验，再 Mock，再重入/消费线程等待）。
            // fill 只填 payload 字段（WriteDataInt32 等），其余字段这里统一装。
            try
            {
                if (devID < 0 || string.IsNullOrEmpty(address)) return false;
                if (timeoutMs < 500) timeoutMs = 500;
                if (timeoutMs > 30000) timeoutMs = 30000;
                try
                {
                    PlcNetCommQue q0 = Instance();
                    if (q0 != null && q0.t != null && ReferenceEquals(Thread.CurrentThread, q0.t)) return false;
                }
                catch { }
                if (!IsConsumerAlive() && !WaitForConsumer(15000)) return false;
                PlcSyncBox box = new PlcSyncBox();
                AutoResetEvent wh = new AutoResetEvent(false);
                try
                {
                    FlashPlcWRDataInfo item = new FlashPlcWRDataInfo
                    {
                        CamName = HeartbeatSlotIndex,
                        ThreadWaitHandle = wh,
                        SyncBox = box,
                        PlcWRswitch = PlcWRswitchStr.Write,
                        PlcAddress = address,
                        PlcDataTypeWR = type,
                        DevID = devID,
                    };
                    fill(item);
                    Instance().EnqueuePlcInfo(item);
                }
                catch { return false; }
                bool signaled = false;
                try { signaled = wh.WaitOne(timeoutMs); }
                catch { signaled = false; }
                finally { try { wh.Close(); } catch { } }
                if (!signaled) return false;
                return box != null && box.Success;
            }
            catch { return false; }
        }

        /// <summary>同步写 Int32（握手写主用：必须确认落盘，丢了 PLC 会一直等）。</summary>
        public static bool PlcSyncWriteInt32(int devID, string address, int value, int timeoutMs = 5000)
        {
            if (devID < 0 || string.IsNullOrEmpty(address)) return false;
            if (MockBridge.IsEnabled) { MockBridge.FakePlcWrite(devID, address, value.ToString()); return true; }
            return SyncWriteOnce(devID, address, DataTypeWR.IsInt32,
                delegate(FlashPlcWRDataInfo item) { item.WriteDataInt32 = new int[] { value }; }, timeoutMs);
        }

        /// <summary>同步写 Bool（语义同上）。</summary>
        public static bool PlcSyncWriteBool(int devID, string address, bool value, int timeoutMs = 5000)
        {
            if (devID < 0 || string.IsNullOrEmpty(address)) return false;
            if (MockBridge.IsEnabled) { MockBridge.FakePlcWrite(devID, address, value.ToString()); return true; }
            return SyncWriteOnce(devID, address, DataTypeWR.IsBool,
                delegate(FlashPlcWRDataInfo item) { item.WriteDataBool = new bool[] { value }; }, timeoutMs);
        }

        /// <summary>同步写 Int16（单字宽握手用，占 1 字，误用 Int32 会连带写掉下一字）。</summary>
        public static bool PlcSyncWriteInt16(int devID, string address, short value, int timeoutMs = 5000)
        {
            if (devID < 0 || string.IsNullOrEmpty(address)) return false;
            if (MockBridge.IsEnabled) { MockBridge.FakePlcWrite(devID, address, value.ToString()); return true; }
            return SyncWriteOnce(devID, address, DataTypeWR.IsInt16,
                delegate(FlashPlcWRDataInfo item) { item.WriteDataInt16 = new short[] { value }; }, timeoutMs);
        }

        /// <summary>同步写 UInt32（请求ID/回显ID 类 UDINT，占 2 字，超 21 亿不溢出）。</summary>
        public static bool PlcSyncWriteUInt32(int devID, string address, uint value, int timeoutMs = 5000)
        {
            if (devID < 0 || string.IsNullOrEmpty(address)) return false;
            if (MockBridge.IsEnabled) { MockBridge.FakePlcWrite(devID, address, value.ToString()); return true; }
            return SyncWriteOnce(devID, address, DataTypeWR.IsUInt32,
                delegate(FlashPlcWRDataInfo item) { item.WriteDataUInt32 = new uint[] { value }; }, timeoutMs);
        }

        /// <summary>同步写 Float（占 2 字；误用 Int32 写浮点地址会字序错乱）。</summary>
        public static bool PlcSyncWriteFloat(int devID, string address, float value, int timeoutMs = 5000)
        {
            if (devID < 0 || string.IsNullOrEmpty(address)) return false;
            if (MockBridge.IsEnabled) { MockBridge.FakePlcWrite(devID, address, value.ToString("G9", System.Globalization.CultureInfo.InvariantCulture)); return true; }
            return SyncWriteOnce(devID, address, DataTypeWR.IsFloat,
                delegate(FlashPlcWRDataInfo item) { item.WriteDataFloat = new float[] { value }; }, timeoutMs);
        }

        /// <summary>同步写 Int16 数组（内容区定长打包字串用，调用方先 PackAsciiToWords）。</summary>
        public static bool PlcSyncWriteWords(int devID, string address, short[] words, int timeoutMs = 5000)
        {
            try
            {
                if (devID < 0 || string.IsNullOrEmpty(address) || words == null || words.Length == 0) return false;
                if (MockBridge.IsEnabled) { MockBridge.FakePlcWrite(devID, address, string.Join(",", Array.ConvertAll(words, delegate (short x) { return x.ToString(); }))); return true; }
                if (timeoutMs < 500) timeoutMs = 500;
                if (timeoutMs > 30000) timeoutMs = 30000;
                try
                {
                    PlcNetCommQue q0 = Instance();
                    if (q0 != null && q0.t != null && ReferenceEquals(Thread.CurrentThread, q0.t)) return false;
                }
                catch { }
                if (!IsConsumerAlive() && !WaitForConsumer(15000)) return false;
                PlcSyncBox box = new PlcSyncBox();
                AutoResetEvent wh = new AutoResetEvent(false);
                try
                {
                    Instance().EnqueuePlcInfo(new FlashPlcWRDataInfo
                    {
                        CamName = HeartbeatSlotIndex,
                        ThreadWaitHandle = wh,
                        SyncBox = box,
                        PlcWRswitch = PlcWRswitchStr.Write,
                        PlcAddress = address,
                        PlcDataTypeWR = DataTypeWR.IsInt16,
                        WriteDataInt16 = words,
                        DevID = devID,
                    });
                }
                catch { return false; }
                bool signaled = false;
                try { signaled = wh.WaitOne(timeoutMs); }
                catch { signaled = false; }
                finally { try { wh.Close(); } catch { } }
                if (!signaled) return false;
                return box != null && box.Success;
            }
            catch { return false; }
        }

        /// <summary>
        /// 链路探测（验真连通，不自建连接）。probeAddress：该品牌心跳地址
        /// （西门子 "M0"，其余 "D0"，与 HeartbeatAddress 同值）。
        /// </summary>
        public static bool PlcProbeLink(int devID, string probeAddress, int timeoutMs = 5000)
        {
            try
            {
                string raw;
                return SyncReadRaw(devID, probeAddress, DataTypeWR.IsInt16, timeoutMs, out raw);
            }
            catch { return false; }
        }

        #endregion

        #region 用自由协议TCP发送数据--Robot

        public static void TcpNetSendMsg(int ccdName, string msg, int devID)
        {
            // Mock：机器人/TCP 外发直接跳过（不接硬件），记一笔；判定与显示不受影响。
            if (MockBridge.IsEnabled) { try { LogHelper.Info(" [Mock演示] TCP发送已跳过（Dev" + devID + "）：" + msg); } catch { } return; }
            Instance().EnqueuePlcInfo(new FlashPlcWRDataInfo
            {
                PlcWRswitch = PlcWRswitchStr.TcpSend,
                CamName = ccdName,
                TcpData = msg,
                DevID = devID,
            });
        }

        /// <summary>
        /// 请求 PLC 自动重连（读写失败处调）。去重 + 退避：已在排队/执行中的丢弃；
        /// 上次执行 5 秒内的丢弃。心跳探针失败走旁路（5 秒一轮天然限流，不受退避卡）。
        /// </summary>
        public static void RequestReconnect(int devID)
        {
            RequestReconnect(devID, false);
        }

        /// <summary>心跳专用的重连请求（跳过 5 秒时间退避，去重仍保留；只许心跳真探针调）。</summary>
        public static void RequestReconnectBypassCooldown(int devID)
        {
            RequestReconnect(devID, true);
        }

        private static void RequestReconnect(int devID, bool bypassCooldown)
        {
            try
            {
                if (devID < 0) return;
                lock (s_reconnLock)
                {
                    if (s_reconnecting.Contains(devID)) return;
                    if (!bypassCooldown)
                    {
                        DateTime last;
                        if (s_lastReconn.TryGetValue(devID, out last)
                            && (DateTime.Now - last).TotalSeconds < ReconnectCooldownSec) return;
                    }
                    s_reconnecting.Add(devID);
                }
                Instance().EnqueuePlcInfo(new FlashPlcWRDataInfo
                {
                    PlcWRswitch = PlcWRswitchStr.Reconnect,
                    DevID = devID,
                });
            }
            catch { }
        }

        /// <summary>
        /// 请求 PLC 单槽 surgical 重建（设置页/测试页按需调）：先连通新对象再换旧引用，
        /// 热更失败不断现有连接。行数增减不管（只管同槽换目标）。
        /// </summary>
        public static void RequestRebuild(int devID, PlcType type, string ip, int port, string newPlcSeries = null)
        {
            try
            {
                if (devID < 0 || string.IsNullOrEmpty(ip) || port <= 0) return;
                Instance().EnqueuePlcInfo(new FlashPlcWRDataInfo
                {
                    PlcWRswitch = PlcWRswitchStr.Rebuild,
                    DevID = devID,
                    NewPlcType = type,
                    NewPlcIP = ip,
                    NewPlcPort = port,
                    NewPlcSeries = newPlcSeries,
                });
            }
            catch { }
        }

        /// <summary>
        /// 查询某 PLC 槽当前应用层连接态（设置/测试页兜底用，2026-10-27 现场血泪）。
        /// 真相源=s_lastReconnOk 记账（首连成败 ConnPlcDevAt 记、日常读通记 true、
        /// 重连成败/热更重建记——断线后心跳触发重连，失败即记 false 翻红）。
        /// 为什么需要查询而不只靠事件：首连成功按原设计不发事件（"启动组终算已刷过 UI"
        /// 的 WinForms 口径），事件订阅方若晚于首连才订阅（如测试页打开时机）会错过
        /// 初值——字典无记录时必须能问库实时态，否则恒判未连接（主窗灯绿、测试页红）。
        /// 断线瞬间的短窗口（TCP 已死、首次重连失败未记账）可能短暂报 true，与原版
        /// 状态灯滞后同口径；翻红由重连失败记账保证，不额外加探活 IO。
        /// </summary>
        /// <param name="index">槽下标（设备表行号）。</param>
        /// <returns>true=最近记账为已连接；越界/无记录/Mock 关闭下失败一律 false。</returns>
        public static bool IsPlcConnectedAt(int index)
        {
            try
            {
                if (MockBridge.IsEnabled) return true;
                lock (s_reconnLock)
                {
                    bool ok;
                    if (s_lastReconnOk.TryGetValue(index, out ok)) return ok;
                    return false;
                }
            }
            catch { return false; }
        }

        #endregion

        private void RunPlcWrite(FlashPlcWRDataInfo PlcObj)
        {
            int _DevID = PlcObj.DevID;

            // 空槽速败：停用/删除后残留请求直接丢弃，不进重连；
            // 同步写必须先填盒+放行句柄，否则同步线程死等。
            try
            {
                if (_DevID < 0 || _DevID >= _PlcDevices.Count || _PlcDevices[_DevID] == null)
                {
                    try
                    {
                        if (PlcObj.SyncBox != null) PlcObj.SyncBox.Success = false;
                    }
                    catch { }
                    TrySetWaitHandle(PlcObj);
                    return;
                }
            }
            catch { try { TrySetWaitHandle(PlcObj); } catch { } return; }

            OperateResult WriteStatus = new OperateResult();
            switch (PlcObj.PlcDataTypeWR)
            {
                case DataTypeWR.IsDouble:
                    WriteStatus = _PlcDevices[_DevID].Write(PlcObj.PlcAddress, PlcObj.WriteDataDouble);
                    break;
                case DataTypeWR.IsFloat:
                    WriteStatus = _PlcDevices[_DevID].Write(PlcObj.PlcAddress, PlcObj.WriteDataFloat);
                    break;
                case DataTypeWR.IsBool:
                    WriteStatus = _PlcDevices[_DevID].Write(PlcObj.PlcAddress, PlcObj.WriteDataBool);
                    break;
                case DataTypeWR.IsInt16:
                    WriteStatus = _PlcDevices[_DevID].Write(PlcObj.PlcAddress, PlcObj.WriteDataInt16);
                    break;
                case DataTypeWR.IsInt32:
                    WriteStatus = _PlcDevices[_DevID].Write(PlcObj.PlcAddress, PlcObj.WriteDataInt32);
                    break;
                case DataTypeWR.IsUInt32:
                    WriteStatus = _PlcDevices[_DevID].Write(PlcObj.PlcAddress, PlcObj.WriteDataUInt32);
                    break;
                case DataTypeWR.IsInt64:
                    WriteStatus = _PlcDevices[_DevID].Write(PlcObj.PlcAddress, PlcObj.WriteDataInt64);
                    break;
                case DataTypeWR.IsShort:
                    WriteStatus = _PlcDevices[_DevID].Write(PlcObj.PlcAddress, PlcObj.WriteDataShort);
                    break;
                case DataTypeWR.IsUshort:
                    WriteStatus = _PlcDevices[_DevID].Write(PlcObj.PlcAddress, PlcObj.WriteDataUShort);
                    break;
                case DataTypeWR.IsUlong:
                    WriteStatus = _PlcDevices[_DevID].Write(PlcObj.PlcAddress, PlcObj.WriteDataULong);
                    break;
                default:
                    break;
            }
            // 写失败=疑似断线：触发自动重连（旧版拿了 WriteStatus 不用，失败静默到人工重启）。
            if (WriteStatus != null && !WriteStatus.IsSuccess) RequestReconnect(_DevID);
            try
            {
                if (PlcObj.SyncBox != null) PlcObj.SyncBox.Success = (WriteStatus != null && WriteStatus.IsSuccess);
            }
            catch { }
            TrySetWaitHandle(PlcObj);
        }

        private void RunPlcRead(FlashPlcWRDataInfo PlcObj)
        {
            bool ReadStatus = false;
            string data = "0";

            int _DevID = PlcObj.DevID;

            // 空槽速败（结果槽填 "Error"，盒填失败，句柄必 Set）。
            try
            {
                if (_DevID < 0 || _DevID >= _PlcDevices.Count || _PlcDevices[_DevID] == null)
                {
                    _mReadResultData[PlcObj.CamName] = "Error";
                    try
                    {
                        if (PlcObj.SyncBox != null) { PlcObj.SyncBox.Success = false; PlcObj.SyncBox.Data = "Error"; }
                    }
                    catch { }
                    TrySetWaitHandle(PlcObj);
                    return;
                }
            }
            catch { try { TrySetWaitHandle(PlcObj); } catch { } return; }

            switch (PlcObj.PlcDataTypeWR)
            {
                case DataTypeWR.IsDouble:
                    OperateResult<double> PlcReadStatus1 = _PlcDevices[_DevID].ReadDouble(PlcObj.PlcAddress);
                    ReadStatus = PlcReadStatus1.IsSuccess;
                    data = PlcReadStatus1.Content.ToString();
                    break;
                case DataTypeWR.IsFloat:
                    OperateResult<float> PlcReadStatus2 = _PlcDevices[_DevID].ReadFloat(PlcObj.PlcAddress);
                    ReadStatus = PlcReadStatus2.IsSuccess;
                    data = PlcReadStatus2.Content.ToString();
                    break;
                case DataTypeWR.IsBool:
                    OperateResult<bool> PlcReadStatus3 = _PlcDevices[_DevID].ReadBool(PlcObj.PlcAddress);
                    ReadStatus = PlcReadStatus3.IsSuccess;
                    data = PlcReadStatus3.Content.ToString();
                    break;
                case DataTypeWR.IsInt16:
                    OperateResult<short> PlcReadStatus41 = _PlcDevices[_DevID].ReadInt16(PlcObj.PlcAddress);
                    ReadStatus = PlcReadStatus41.IsSuccess;
                    data = PlcReadStatus41.Content.ToString();
                    break;
                case DataTypeWR.IsInt32:
                    OperateResult<int> PlcReadStatus4 = _PlcDevices[_DevID].ReadInt32(PlcObj.PlcAddress);
                    ReadStatus = PlcReadStatus4.IsSuccess;
                    data = PlcReadStatus4.Content.ToString();
                    break;
                case DataTypeWR.IsUInt32:
                    OperateResult<uint> PlcReadStatus9 = _PlcDevices[_DevID].ReadUInt32(PlcObj.PlcAddress);
                    ReadStatus = PlcReadStatus9.IsSuccess;
                    data = PlcReadStatus9.Content.ToString();
                    break;
                case DataTypeWR.IsInt64:
                    OperateResult<Int64> PlcReadStatus5 = _PlcDevices[_DevID].ReadInt64(PlcObj.PlcAddress);
                    ReadStatus = PlcReadStatus5.IsSuccess;
                    data = PlcReadStatus5.Content.ToString();
                    break;
                case DataTypeWR.IsShort:
                    OperateResult<short> PlcReadStatus6 = _PlcDevices[_DevID].ReadInt16(PlcObj.PlcAddress);
                    ReadStatus = PlcReadStatus6.IsSuccess;
                    data = PlcReadStatus6.Content.ToString();
                    break;
                case DataTypeWR.IsUshort:
                    OperateResult<ushort> PlcReadStatus7 = _PlcDevices[_DevID].ReadUInt16(PlcObj.PlcAddress);
                    ReadStatus = PlcReadStatus7.IsSuccess;
                    data = PlcReadStatus7.Content.ToString();
                    break;
                case DataTypeWR.IsUlong:
                    OperateResult<ulong> PlcReadStatus8 = _PlcDevices[_DevID].ReadUInt64(PlcObj.PlcAddress);
                    ReadStatus = PlcReadStatus8.IsSuccess;
                    data = PlcReadStatus8.Content.ToString();
                    break;
                default:
                    break;
            }
            if (ReadStatus)
            {
                _mReadResultData[PlcObj.CamName] = data;
                // 任一读通都证明应用层是通的：清"假连上"计数。
                NoteHeartbeatResult(_DevID, true, true);
                // 成功边沿回刷（首连/重连失败后读通即"恢复"，触发 PlcConnChanged 翻绿；
                // 日常成功期零事件零日志；首连即成功的不触发，启动组终算已刷过 UI）。
                bool mFireOnline = false;
                try
                {
                    lock (s_reconnLock)
                    {
                        bool mWas;
                        if (s_lastReconnOk.TryGetValue(_DevID, out mWas) && !mWas)
                        {
                            s_lastReconnOk[_DevID] = true;
                            mFireOnline = true;
                        }
                    }
                }
                catch { mFireOnline = false; }
                if (mFireOnline)
                {
                    try { if (PlcConnChanged != null) PlcConnChanged(_DevID, true); }
                    catch { }
                    try { LogHelper.Info(" PLC设备" + _DevID + "链路恢复在线（读通回刷）"); }
                    catch { }
                }
            }
            else
            {
                _mReadResultData[PlcObj.CamName] = "Error";
                // 真心跳探针失败才记账（脚本同步读写借用 12 号槽但标记 false，不计入）。
                NoteHeartbeatResult(_DevID, PlcObj.IsHeartbeatProbe, false);
                // 心跳探针失败走旁路（5 秒一轮天然限流）；业务/脚本失败走退避版。
                if (PlcObj.IsHeartbeatProbe) RequestReconnect(_DevID, true);
                else RequestReconnect(_DevID);
            }
            // 同步读回执：失败时盒里显式填 "Error"（与结果槽语义对齐；失败初值 "0" 不能外流）。
            try
            {
                if (PlcObj.SyncBox != null) { PlcObj.SyncBox.Success = ReadStatus; PlcObj.SyncBox.Data = ReadStatus ? data : "Error"; }
            }
            catch { }
            TrySetWaitHandle(PlcObj);
        }

        private void RunTcpSend(FlashPlcWRDataInfo tcpObj)
        {
            // DevID 越界防御：配置删除/变更后残留请求直接丢弃。
            if (tcpObj.DevID < 0 || tcpObj.DevID >= TcpDevices.Count) return;
            if (TcpDevices[tcpObj.DevID].IsConnected()) TcpDevices[tcpObj.DevID].Send(tcpObj.TcpData);
        }

        /// <summary>
        /// PLC 自动重连执行（消费线程串行：复用旧对象 ConnectClose + ConnectServer，不换引用；
        /// 最多试 2 次，单次重连最多堵队列约 4s。成败都触发 PlcConnChanged + 边沿/限流日志）。
        /// </summary>
        private void RunReconnect(FlashPlcWRDataInfo info)
        {
            int id = info.DevID;
            bool ok = false;
            try
            {
                if (id >= 0 && id < _PlcDevices.Count && _PlcDevices[id] != null)
                {
                    DeviceTcpNet nd = _PlcDevices[id] as DeviceTcpNet;
                    if (nd != null)
                    {
                        try { nd.ConnectClose(); } catch { }
                        for (int r = 0; r < 2 && !ok; r++)
                        {
                            try { ok = nd.ConnectServer().IsSuccess; }
                            catch { ok = false; }
                            if (!ok) Thread.Sleep(500);
                        }
                        // 重连失败也关 socket（2026-10-27 现场血泪同源补漏）：ConnectServer
                        // 失败（FINS 握手超时等）时 TCP 可能已建连，不关就留半开连接挂占
                        // PLC 会话槽——心跳旁路重连每轮都再试，失败一轮多一条，几小时就能
                        // 占死 PLC 侧全部会话槽（能 ping 通、TCP 能连、FINS 通讯全断）。
                        // 与 ConnPlcDevAt 首连失败分支同口径；成功时保持连接，此处不碰。
                        if (!ok) { try { nd.ConnectClose(); } catch { } }
                    }
                }
            }
            catch { ok = false; }
            finally
            {
                bool logIt = false;
                string logMsg = null;
                lock (s_reconnLock)
                {
                    s_reconnecting.Remove(id);
                    s_lastReconn[id] = DateTime.Now;
                    if (ok)
                    {
                        bool wasOk = false;
                        try { s_lastReconnOk.TryGetValue(id, out wasOk); } catch { wasOk = false; }
                        try { s_lastReconnOk[id] = true; } catch { }
                        try { s_reconnFailStreak[id] = 0; } catch { }
                        if (!wasOk) { logIt = true; logMsg = DateTime.Now + " PLC设备" + id + "重连成功"; }
                    }
                    else
                    {
                        int streak = 0;
                        try { s_reconnFailStreak.TryGetValue(id, out streak); } catch { streak = 0; }
                        streak++;
                        try { s_reconnFailStreak[id] = streak; } catch { }
                        try { s_lastReconnOk[id] = false; } catch { }
                        if (streak == 1 || streak % ReconnectFailLogEvery == 0)
                        {
                            logIt = true;
                            logMsg = DateTime.Now + " PLC设备" + id + "重连失败（已连续"
                                + streak + "次，" + ReconnectCooldownSec + "秒后允许再次重连）";
                        }
                    }
                }
                try { if (PlcConnChanged != null) PlcConnChanged(id, ok); } catch { }
                try { if (logIt && logMsg != null) LogHelper.Info(logMsg); } catch { }
            }
        }

        /// <summary>
        /// 心跳读成败记账（"假连上"告警：TCP 建连成功但地址读不通时满阈值补警告，约 60 秒一条）。
        /// isHeartbeat 必须传显式标记（脚本同步读写借用 12 号槽但标记 false，不计）。
        /// ok=true 即清零；警告只在"上次重连成功"时打（真断网走重连失败日志，不重复）。
        /// 次数（12 次）+ 时间（60 秒）双门限都过才记一笔。
        /// </summary>
        private static void NoteHeartbeatResult(int devID, bool isHeartbeat, bool ok)
        {
            try
            {
                if (!isHeartbeat || devID < 0) return;
                bool warnIt = false;
                int streak = 0;
                lock (s_reconnLock)
                {
                    if (ok) { try { s_heartbeatFailStreak[devID] = 0; } catch { } return; }
                    try { s_heartbeatFailStreak.TryGetValue(devID, out streak); } catch { streak = 0; }
                    streak++;
                    if (streak < HeartbeatWarnEvery)
                    {
                        try { s_heartbeatFailStreak[devID] = streak; } catch { }
                        return;
                    }
                    try { s_heartbeatFailStreak[devID] = 0; } catch { }
                    try { s_lastReconnOk.TryGetValue(devID, out warnIt); } catch { warnIt = false; }
                    if (!warnIt) return;
                    try
                    {
                        DateTime lastWarn;
                        if (s_lastHeartbeatWarn.TryGetValue(devID, out lastWarn)
                            && (DateTime.Now - lastWarn).TotalSeconds < HeartbeatWarnMinIntervalSec)
                            return;
                        s_lastHeartbeatWarn[devID] = DateTime.Now;
                    }
                    catch { return; }
                }
                if (!warnIt) return;
                try
                {
                    LogHelper.Info(" PLC设备" + devID + "应用层读取连续失败"
                        + HeartbeatWarnEvery + "次（约60秒）：TCP建连正常但心跳地址读不通——"
                        + "请检查PLC是否RUN、网线/交换机、心跳地址是否被改写或PLC程序停机；"
                        + "重连成功提示已收敛为边沿，此警告约60秒一条");
                }
                catch { }
            }
            catch { }
        }

        /// <summary>
        /// PLC 单槽重建执行（消费线程串行）：先拿新配置建新对象并连通，
        /// 连通了才 Close 旧对象并替换——热更失败不断现有连接。
        /// </summary>
        private void RunRebuild(FlashPlcWRDataInfo info)
        {
            int id = info.DevID;
            bool ok = false;
            try
            {
                if (id < 0 || id >= _PlcDevices.Count) return;
                IReadWriteNet dev = null;
                try { dev = BuildPlcDevice(info.NewPlcType, info.NewPlcIP, info.NewPlcPort, info.NewPlcSeries); } catch { dev = null; }
                if (dev == null) return;
                DeviceTcpNet nd = dev as DeviceTcpNet;
                if (nd == null) return;
                try { ok = nd.ConnectServer().IsSuccess; } catch { ok = false; }
                if (ok)
                {
                    try
                    {
                        DeviceTcpNet old = _PlcDevices[id] as DeviceTcpNet;
                        if (old != null) old.ConnectClose();
                    }
                    catch { }
                    _PlcDevices[id] = dev;
                }
                else
                {
                    try { nd.ConnectClose(); } catch { }
                }
            }
            finally
            {
                // 字典与事件同值同步（热更失败刚把 UI 置红，字典不同步则下次读通认不出边沿）。
                try { lock (s_reconnLock) { s_lastReconnOk[id] = ok; } }
                catch { }
                try { if (PlcConnChanged != null) PlcConnChanged(id, ok); } catch { }
                try { LogHelper.Info(" PLC设备" + id + (ok ? "热更重建成功" : "热更重建失败（保持旧连接，请检查IP/端口/类型）")); } catch { }
            }
        }
    }

    public enum PlcType
    {
        MelsecA1ENet,
        MelsecMcNet,
        OmronFinsNet,
        InovanceTcpNet,
        // 欧姆龙 EIP(CIP) 方式（NJ/NX 系列）。注意 INI 按枚举名存储，新值只能加在末尾。
        OmronCipNet,
        // 西门子 S7（S1200/S1500 等，系列见 PlcDevInfo.SiemensSeries，缺省 S1500；S7 端口固定 102）。
        SiemensS7Net,
    }

    public enum PlcWRswitchStr
    {
        Write,
        Read,
        TcpSend,
        /// <summary>PLC 自动重连（复用旧连接对象 ConnectClose+ConnectServer，见 RunReconnect）。</summary>
        Reconnect,
        /// <summary>PLC 停机热更重建（换新 Type/IP/Port 建新对象，见 RunRebuild）。</summary>
        Rebuild,
    }

    public enum DataTypeWR
    {
        IsDouble,
        IsFloat,
        IsBool,
        IsInt16,
        IsInt32,
        IsInt64,
        IsShort,
        IsUshort,
        IsUlong,
        IsLong,
        /// <summary>无符号 32 位（请求ID/回显ID 类 UDINT；占 2 字，与 Int32 同字序）。</summary>
        IsUInt32
    }

    public class PlcDevInfo
    {
        /// <summary>通讯类型。</summary>
        public PlcType PlcType { get; set; }
        /// <summary>
        /// 西门子系列（仅 PlcType=SiemensS7Net 时有效；空/非法回落 S1500。
        /// 料号 Comm 里 PlcSeries 键，其它品牌忽略）。
        /// </summary>
        public string SiemensSeries { get; set; }
        /// <summary>PLC IP 地址。</summary>
        public string PlcIP { get; set; }
        /// <summary>连接端口。</summary>
        public int PlcPort { get; set; }
        /// <summary>设备 ID 标号。</summary>
        public int PlcDevID { get; set; }
        public int PlcCommPulse { get; set; }
        /// <summary>
        /// 是否启用（动态删语义：停用=不断号占位，槽位 null，读写速败；
        /// 真删只允许删末尾。缺省 true）。
        /// </summary>
        public bool Enable { get; set; }
        /// <summary>连接状态。</summary>
        public bool IsConnect { get; set; }
    }

    public class TcpDevInfo
    {
        public string TcpIP { get; set; }
        public string TcpPort { get; set; }
        public int TcpDevID { get; set; }
        /// <summary>是否启用（同 PlcDevInfo.Enable：停用不断号，客户端 null 占位）。</summary>
        public bool Enable { get; set; }
        public bool IsConnect { get; set; }
        /// <summary>通讯协议："TCP"=裸 TCP / "EIP"=EtherNet/IP。INI 按字符串存储，缺省 TCP。</summary>
        public string Protocol { get; set; }
        /// <summary>EIP 读取地址：扫码枪 Assembly 实例号（常见默认 "4"）。</summary>
        public string EipAddress { get; set; }
        /// <summary>EIP 单次读取字节数（默认 64）。</summary>
        public int EipReadLength { get; set; }
    }

    public class FlashPlcWRDataInfo
    {
        public int DevID { get; set; }
        public int CamName { get; set; }
        /// <summary>是否心跳线程的真探针（缺省 false）。只有它为 true 的读失败才计入
        /// "应用层读不通"警告的次数（脚本同步读写借用 12 号槽但标记 false，不计入）。</summary>
        public bool IsHeartbeatProbe { get; set; }
        public PlcWRswitchStr PlcWRswitch { get; set; }
        public DataTypeWR PlcDataTypeWR { get; set; }
        public AutoResetEvent ThreadWaitHandle { get; set; }
        /// <summary>同步读写结果盒（消费线程填，等待线程只读；WaitOne 提供内存屏障）。</summary>
        public PlcSyncBox SyncBox { get; set; }
        public string PlcAddress { get; set; }
        public float[] WriteDataFloat { get; set; }
        public double[] WriteDataDouble { get; set; }
        public bool[] WriteDataBool { get; set; }
        public short[] WriteDataInt16 { get; set; }
        public int[] WriteDataInt32 { get; set; }
        public Int64[] WriteDataInt64 { get; set; }
        /// <summary>无符号 32 位写数据（ID 字回写用，走 IsUInt32 分支）。</summary>
        public uint[] WriteDataUInt32 { get; set; }
        public short[] WriteDataShort { get; set; }
        public ushort[] WriteDataUShort { get; set; }
        public ulong[] WriteDataULong { get; set; }
        public ushort DataLength { get; set; }
        public string TcpData { get; set; }
        /// <summary>热更目标类型（仅 Rebuild 项用）。</summary>
        public PlcType NewPlcType { get; set; }
        /// <summary>热更目标 IP（仅 Rebuild 项用）。</summary>
        public string NewPlcIP { get; set; }
        /// <summary>热更目标端口（仅 Rebuild 项用）。</summary>
        public int NewPlcPort { get; set; }
        /// <summary>热更目标西门子系列（仅 Rebuild 且 Type=SiemensS7Net 时用；空即 S1500）。</summary>
        public string NewPlcSeries { get; set; }
    }

    /// <summary>
    /// 脚本同步读写结果盒（Success=成败；Data=读回字符串，失败时为 "Error"）。
    /// </summary>
    public sealed class PlcSyncBox
    {
        public volatile bool Success;
        public volatile string Data;
    }

    public class FlashTcpDataInfo
    {
        public int DevID { get; set; }
        public int CamName { get; set; }
        public string Data { get; set; }
    }
}
