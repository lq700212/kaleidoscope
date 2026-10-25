// =====================================================================================
// 【下沉注记】本文件 2026-10-25 自 HJVision-WPF 生产工程（第二轮仪器设备下沉）
// 原样搬入，口径与第一轮 PlcNetCommQue/TcpChannel 下沉相同：
//   ①来源 GYZVision.Core/Devices/VibFeeder.cs 的 VibFeederController 段（含 Modes 嵌套类）；
//      同文件的 FeederSpec/VibFeederTableSync（读料号 INI，业务装配件）留守主项目不随迁；
//   ②命名空间 → Kaleidoscope.Services；
//   ③演示模式出口 MockMode.IsEnabled → MockBridge.IsEnabled（7 处，宿主静态构造接线）；
//   ④日志出口 FlashLogger.RunInfo(DateTime.Now + …) → LogHelper.Info(…)（10 处）：宿主
//      FlashLogger 静态构造把 LogAction 接到 RunInfo，落盘自带时间戳，原手拼的
//      DateTime.Now 前缀是冗余已去，消息正文一字未动；
//   ⑤NModbus 引用库内已有（ModbusIpMaster），寄存器表/协议口径全保留。
//   其余算法/默认值/异常口径/注释全保留，公共 API 签名不变（主项目经
//   文件级 using 别名壳零改动引用，断点可跨工程打进本源码）。
// =====================================================================================
using Modbus.Device;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using Kaleidoscope.Utils;

namespace Kaleidoscope.Services
{
    // =====================================================================================
    // 柔性供料震动盘控制器（以太网 / MODBUS TCP，同步版本）。
    // 【对标】原版 E:\Project\HJVision\GYZVision\SubClass\VibFeeder.cs 一比一搬入（V4.4.x 口径，
    //   含 2026-10-17 纯时长制改版 + 断连自愈重连冷却），唯一结构差异=命名空间
    //   HuaJiVision → HuaJiVision.Core.Devices（FlashLogger/IniFile/MockMode 同名经 using 直用）。
    // -------------------------------------------------------------------------------------
    // 【来源】V4.2.1 MainForm.cs 的 VibratoryFeederController（类内同名），整类移植。
    // 移植改造点（V4.2.1 原版在现场能跑，但有以下硬伤，本文件逐条修掉）：
    //   ①Connect 是裸 TcpClient.Connect——不可达 IP 卡几十秒（调用线程冻死）。
    //     改为 BeginConnect + WaitOne 手动超时（每轮默认 3000ms）+ 返回 bool（见上位机通讯封装 skill §3.1）；
    //     V4.4.x 加第二轮重试（首轮失败 300ms 后再试一次，与 PLC RunReconnect 同强度）：
    //     控制器上电后 TCP 先 accept、Modbus 从站晚就绪，单轮验证读失败不判死——
    //     "Ping 通但测试连接第一次不通、等会再试就好"的来源之一；
    //   ②全部 master 操作无锁——轮询线程与关窗 Dispose 并发会互踩引用。加 _lock 串行化（§3.2）；
    //   ③EnsureConnected 失败只弹 MessageBox，完了还继续用 null master 调——必 NRE。
    //     改为返回 bool，调用方失败即返（读回 null / 写直接丢弃 + 边沿日志）；
    //   ④连接失败写 error.log 文件 + throw。改为 FlashLogger + 返回 false（调用方定夺，不抛）；
    //   ⑤Dispose 增加关窗协议：先 _disposed → 限时抢锁 → 拿不到锁外强断网（§3.4）；
    //   ⑥参数非法（未知轴名/组合越界/亮度越界）旧版弹框后继续执行（甚至对 null 解引用）。
    //     改为记日志直接返回——配置页/脚本传错值时不断线、不写错寄存器；
    //   ⑦读写失败即标记断开（TcpClient.Connected 是缓存值，不能信，§3.3）+ 边沿日志防刷屏；
    //   ⑧名字：旧版叫"振动盘"，本项目统一叫"震动盘"（类名/注释/界面一律）。
    // 【失败语义】震动盘是辅助供料（缺料由视觉 NG 计数兜底），命令丢失只记日志、不断业务线：
    //   读失败回 null，写失败吞掉（边沿记一次）。调用方（脚本）无需 try-catch 也能跑，
    //   但"测试连接"按钮这类要明确结果的场景调 TestConnection（显式 bool）。
    // 【寄存器表】柔性供料控制器以太网版（V4.2.1 实测，现场换控制器型号先核对）：
    //   0x000F 模式字（b4=触发位，StartModeWithTrigger 自动加）；0x0000 停；
    //   0x0008 平台使能（0xFF00/0x0000）；0x0009 清故障（写 0xFF00）；
    //   轴基址 0x0010 起每轴 3 字（频率/电压/时间），见 GetAxisBaseAddress；
    //   0x0031 聚集 3 字（垂直/水平/总时间）；组合 0x0060/0x0070 各 16 字（8 步×模式+时间）；
    //   灯 A=0x0006 B=0x0007（0xFF00 开）；亮度 0x0050/0x0051（6~999）；料斗 0x0005。
    // 【0x000F 协议事实——两次现场实证，别再当状态位用】
    //   0x000F 是"持续命令"寄存器（写啥就保持啥，直到下一次写覆盖），不是运行状态位：
    //   ①2026-10-04 实证：Stop 写失败后模式字残留 Scatter，盘持续振 10 分钟无人写停——
    //     连续动作永远不会自己归零；
    //   ②2026-10-17 实证：参数组1（组合动作）物理跑完约 4s 后，模式字读回仍非零——
    //     组动作也不会自动归零。曾按"归零=动作完成"轮询等完成（V4.4.226），归零确认永不
    //     触发=每次补料空烧满 8s 预算，现场实测两次震动间隔 5~6s（大头就是烧掉的预算）。
    //   结论：补料动作一律"按配置时长等待 + 写停读回验证"（RunActionOnce），
    //   绝不靠读模式字判完成。若以后厂商给出真正的运行状态位，再按协议加提前退出。
    // =====================================================================================
    public class VibFeederController : IDisposable
    {
        /// <summary>
        /// 配置支持的最大震动盘数（V4.4.12 由 2 扩到 8，与打码机台数上限对齐；现场按需增减）。
        /// 三处同值：配置页台数 NUD 上限 / 脚本 ConnectFeeders 建表循环 / ini Feeder 节数，改此处同步改三处。
        /// 脚本建表循环本就以本常量为界，扩上限后旧脚本零改动（旧配置无 Feeder3+ 键，读空即跳过）。
        /// </summary>
        public const int MaxFeeders = 8;

        /// <summary>显示名（"震动盘1/2"，日志与配置页用；不是 SN，与相机 SN 无关）。</summary>
        public readonly string Name;
        public readonly string IpAddress;
        public readonly int Port;
        public readonly byte SlaveId;

        private TcpClient _tcp;
        private ModbusIpMaster _master;
        private readonly object _lock = new object();   // 连接/读写/释放同一把锁（见文件头②）
        private volatile bool _disposed;
        private bool _connected;                        // 真实连接标志（读写成功置 true，失败置 false；不信 Connected 缓存）
        private bool _failLogged;                       // 失败日志边沿（连上清零，防刷屏）
        /// <summary>
        /// 断连自愈重连失败后的冷却毫秒：冷却内的读写直接失败、不再硬试重连（见 EnsureMasterLocked【冷却】）。
        /// 取值 5000ms 的依据：ST5 轮内振散一轮（启动写+停止验证读）约 2~4s，冷却盖住一轮即可——
        /// 控制器恢复上线最多晚一个冷却期被发现，而离线期单触发的硬试重连开销从每笔 2.3s 降为整触发一次。
        /// </summary>
        private const int ReconnectCooldownMs = 5000;
        /// <summary>上次自动重连失败时刻（Environment.TickCount 毫秒）。初始 int.MinValue=极远以前，冷却早已过；
        /// TickCount 约 24.9 天环绕一次，比较走 unchecked 减法取差值，天然免疫环绕。</summary>
        private long _lastReconnFailAt = int.MinValue;

        /// <summary>连接边沿事件（true=连上，false=断开；后台线程触发，UI 订阅方自行 BeginInvoke）。</summary>
        public event Action<VibFeederController, bool> ConnectionChanged;

        public VibFeederController(string name, string ipAddress, int port, byte slaveId)
        {
            Name = string.IsNullOrEmpty(name) ? "震动盘" : name;
            IpAddress = ipAddress;
            Port = port;
            SlaveId = slaveId;
        }

        public bool IsConnected
        {
            get { lock (_lock) { return _connected && _tcp != null && _tcp.Connected; } }
        }

        private void SetConnected(bool value)
        {
            // 调用方已持锁（本类内部约定，避免锁重入啰嗦）。
            if (_connected == value) return;   // 边沿才动作（见文件头⑦）
            _connected = value;
            if (value) _failLogged = false;
            try { LogHelper.Info(" [" + Name + "] " + (value ? "已连接" : "通讯中断")); } catch { }
            try
            {
                Action<VibFeederController, bool> h = ConnectionChanged;
                if (h != null) h(this, value);
            }
            catch { }   // 订阅方异常不许污染通讯层
        }

        private void LogOnce(string msg)
        {
            // 失败日志只记一次（边沿），连上后清零（见文件头⑦）。
            if (_failLogged) return;
            _failLogged = true;
            try { LogHelper.Info(" [" + Name + "] " + msg); } catch { }
        }

        /// <summary>
        /// 连接（同步阻塞≤timeoutMs/轮，默认 3000ms，最多两轮）。幂等：已连直接 true。
        /// 返回 false=不可用（超时/拒绝/验证读失败），内部清理干净可下次重连，不抛异常。
        /// 注意两轮含义：调用方传的仍是"每轮"超时（测试页"3 秒/台"口径变为"最长约 6 秒/台"，
        /// 见 mFormVibFeeder 提示文案）；调用方一律放后台线程，绝不在 UI 线程调（见文件头①）。
        /// </summary>
        public bool Connect(int timeoutMs = 3000)
        {
            lock (_lock)
            {
                if (_disposed) return false;
                // Mock演示：免建连直接判通（测试页试连/脚本补料同一入口全覆盖，不碰网口）。
                if (MockBridge.IsEnabled) { SetConnected(true); return true; }
                try
                {
                    if (_tcp != null && _tcp.Connected && _master != null && _connected) return true;
                    // 最多两轮：首轮失败 300ms 后再试一次（从站晚就绪场景，见文件头①）。
                    for (int mAttempt = 0; mAttempt < 2; mAttempt++)
                    {
                        if (mAttempt > 0)
                        {
                            try { Thread.Sleep(300); }
                            catch { /* 原因：关窗竞态下 Sleep 被中断，直接进第二轮，不挡建连 */ }
                            if (_disposed) return false;
                        }
                        CleanupLocked();
                        _tcp = new TcpClient();
                        // 手动超时连接（无回调式，见文件头①；回调线程 EndConnect 是 NRE 根因，禁用）。
                        IAsyncResult ar = _tcp.BeginConnect(IpAddress, Port, null, null);
                        if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                        {
                            LogOnce("连接超时（第" + (mAttempt + 1) + "轮，" + timeoutMs + "ms，" + IpAddress + ":" + Port + "）");
                            CleanupLocked();
                            continue;
                        }
                        if (_tcp.Client == null)
                        {
                            // 并发 Dispose 已释放 socket（见文件头⑤的锁外强断网），绝不能再 EndConnect。
                            LogOnce("连接被并发释放，放弃收尾");
                            CleanupLocked();
                            continue;
                        }
                        try { _tcp.EndConnect(ar); }
                        catch (Exception ex)
                        {
                            LogOnce("连接失败（第" + (mAttempt + 1) + "轮）：" + ex.Message);
                            CleanupLocked();
                            continue;
                        }
                        _master = ModbusIpMaster.CreateIp(_tcp);
                        _master.Transport.ReadTimeout = 3000;
                        _master.Transport.WriteTimeout = 3000;
                        // 验证读：状态寄存器 0x0001（V4.2.1 同款，通了才算连上）。
                        try { _master.ReadHoldingRegisters(SlaveId, 0x0001, 1); }
                        catch (Exception ex)
                        {
                            LogOnce("验证读失败（第" + (mAttempt + 1) + "轮，从站 " + SlaveId + " 无应答）：" + ex.Message);
                            CleanupLocked();
                            continue;
                        }
                        SetConnected(true);
                        try { LogHelper.Info(" [" + Name + "] 连接成功（" + IpAddress + ":" + Port + " 从站" + SlaveId + "）"); } catch { }
                        return true;
                    }
                    return false;
                }
                catch (Exception ex)
                {
                    LogOnce("连接异常：" + ex.Message);
                    try { CleanupLocked(); } catch { }
                    return false;
                }
            }
        }

        /// <summary>断开（锁内清理；与后台读写串行，互不踩引用）。</summary>
        public void Disconnect()
        {
            lock (_lock) { CleanupLocked(); }
        }

        /// <summary>
        /// 关窗协议（见文件头⑤）：先 _disposed（后台不再重建）→ 限时抢锁清理 →
        /// 拿不到锁就锁外强断网（持锁任务的 WaitOne 会立刻结束，其 catch 自会收敛放锁）。
        /// </summary>
        public void Dispose()
        {
            _disposed = true;
            if (Monitor.TryEnter(_lock, TimeSpan.FromMilliseconds(300)))
            {
                try { CleanupLocked(); }
                finally { Monitor.Exit(_lock); }
            }
            else
            {
                try { LogHelper.Info(" [" + Name + "] Dispose 未拿到锁，改走锁外强断网"); } catch { }
                try { if (_tcp != null) _tcp.Close(); } catch { }
            }
        }

        private void CleanupLocked()
        {
            // 调用方已持锁。顺序：先断事件语义（置 false），再释放资源。
            SetConnected(false);
            try { if (_master != null) _master.Dispose(); } catch { }
            _master = null;
            try { if (_tcp != null) _tcp.Close(); } catch { }
            _tcp = null;
        }

        #region 基础读写（同步，失败安全语义见文件头⑧）

        public ushort[] ReadHoldingRegisters(ushort startAddress, ushort count)
        {
            lock (_lock)
            {
                // Mock演示：回全零假值（状态寄存器 0x0001 验证读能过，不断测试页结论）。
                if (MockBridge.IsEnabled) { SetConnected(true); return new ushort[count]; }
                if (_disposed) return null;
                if (_master == null && !EnsureMasterLocked()) return null;   // 断连自愈：先重连再读
                try
                {
                    ushort[] v = _master.ReadHoldingRegisters(SlaveId, startAddress, count);
                    SetConnected(true);
                    return v;
                }
                catch (Exception ex)
                {
                    MarkDownLocked(ex);
                    return null;
                }
            }
        }

        public bool WriteSingleRegister(ushort address, ushort value)
        {
            lock (_lock)
            {
                if (MockBridge.IsEnabled) { SetConnected(true); return true; }
                if (_disposed) return false;
                if (_master == null && !EnsureMasterLocked()) return false;   // 断连自愈：先重连再写
                try
                {
                    _master.WriteSingleRegister(SlaveId, address, value);
                    SetConnected(true);
                    return true;
                }
                catch (Exception ex) { MarkDownLocked(ex); return false; }
            }
        }

        public bool WriteMultipleRegisters(ushort startAddress, ushort[] values)
        {
            lock (_lock)
            {
                if (MockBridge.IsEnabled) { SetConnected(true); return true; }
                if (values == null) return false;
                if (_disposed) return false;
                if (_master == null && !EnsureMasterLocked()) return false;   // 断连自愈：先重连再写
                try
                {
                    _master.WriteMultipleRegisters(SlaveId, startAddress, values);
                    SetConnected(true);
                    return true;
                }
                catch (Exception ex) { MarkDownLocked(ex); return false; }
            }
        }

        public bool[] ReadCoils(ushort startAddress, ushort count)
        {
            lock (_lock)
            {
                if (MockBridge.IsEnabled) { SetConnected(true); return new bool[count]; }
                if (_disposed) return null;
                if (_master == null && !EnsureMasterLocked()) return null;   // 断连自愈：先重连再读
                try
                {
                    bool[] v = _master.ReadCoils(SlaveId, startAddress, count);
                    SetConnected(true);
                    return v;
                }
                catch (Exception ex)
                {
                    MarkDownLocked(ex);
                    return null;
                }
            }
        }

        public bool WriteSingleCoil(ushort address, bool value)
        {
            lock (_lock)
            {
                if (MockBridge.IsEnabled) { SetConnected(true); return true; }
                if (_disposed) return false;
                if (_master == null && !EnsureMasterLocked()) return false;   // 断连自愈：先重连再写
                try
                {
                    _master.WriteSingleCoil(SlaveId, address, value);
                    SetConnected(true);
                    return true;
                }
                catch (Exception ex) { MarkDownLocked(ex); return false; }
            }
        }

        private void MarkDownLocked(Exception ex)
        {
            // 读写失败即断连（见文件头⑦）；是否"真断"由下次读写/重连验证，UI 只看边沿事件。
            SetConnected(false);
            try { CleanupLocked(); } catch { }
            LogOnce("读写失败已断开：" + ex.GetType().Name + " " + ex.Message);
        }

        /// <summary>
        /// 断连自愈：master 为空（上次读写失败已被 MarkDownLocked 清掉）时当场重连一次，连上继续本次读写。
        /// 【为什么】没有这一步，运行期一旦断连，所有读写直接 false/null 直到重启软件都没有恢复手段——
        /// 2026-10-04 现场实证：振散 Stop 写失败后 0x000F 模式字残留 Scatter，震动盘持续振动
        /// 再也没有人能写停（连软件重启都不写停），震了 10 分钟靠人工断电。
        /// 【边界】只在 master==null 时触发（正常路径零开销，连一次多余判断都没有）；
        /// Connect 幂等（已连直接 true）且 Monitor 锁可重入（本方法只在持锁调用链里用）；
        /// 重连失败不清任何状态（下次读写自然再试）；已 Dispose 返回 false 不再重建。
        /// 【冷却】重连失败后 ReconnectCooldownMs 内直接 return false、不再硬试 Connect。
        /// 为什么要有冷却：控制器离线时（典型=软件/控制器重启后的就绪窗口，PLC 往往先就绪、
        /// ST5 触发先进来），一次触发要打十几笔读写（开灯2写+读回+每轮启动/停止+关灯+兜底停盘），
        /// 每笔都硬试 Connect(1000) 两轮≈2.3s 的话单触发被拖到 1 分钟以上——15 站是串行轮询，
        /// 其他站触发全被排队到超龄放弃。冷却后整触发只首笔硬试一次，其余秒失败，总耗时
        /// 回落到与"写超时失败"同级（约十几秒，与修复前持平）。代价=控制器恢复上线最多晚
        /// 一个冷却期被发现，可接受。显式 Connect（测试页/建表/延迟补试）不走本方法，不受冷却影响。
        /// </summary>
        private bool EnsureMasterLocked()
        {
            if (_disposed) return false;
            if (_master != null) return true;
            // 冷却判断：距上次重连失败不足冷却期则放弃本次重连（调用方按普通读写失败处理）。
            int sinceLastFail = unchecked(Environment.TickCount - (int)_lastReconnFailAt);
            if (sinceLastFail >= 0 && sinceLastFail < ReconnectCooldownMs) return false;
            // 短超时 1000ms（两轮≈2.3s）：调用方是生产节拍线程（拍照循环/关灯/停盘），别按默认 3s×2 拖节拍。
            if (!Connect(1000))
            {
                _lastReconnFailAt = Environment.TickCount;   // 失败起算冷却，见方法注释【冷却】
                return false;
            }
            return true;
        }

        #endregion

        #region 高级控制（同步；失败吞掉记日志，见文件头⑧）

        /// <summary>启动指定的运动模式（直接写入模式寄存器 0x000F，已包含触发位语义见下）。</summary>
        public void StartMode(ushort modeValue)
        {
            WriteSingleRegister(0x000F, modeValue);
        }

        /// <summary>启动运动模式（自动加上触发位 b4；脚本主调这个，不要调 StartMode）。</summary>
        public void StartModeWithTrigger(ushort modeWithoutTrigger)
        {
            ushort value = (ushort)(modeWithoutTrigger | 0x0010);
            StartMode(value);
        }

        public void Stop()
        {
            WriteSingleRegister(0x000F, 0x0000);
        }

        // —— 补料动作共享语义（2026-10-17 收敛：生产脚本 VibrateOnce / 配置页"补料试振"按钮 /
        //      主窗震动联动循环三处唯一口；同日再改版：等完成的"归零轮询"整体删除，改纯时长制，
        //      根因见文件头【0x000F 协议事实】——模式字在组动作跑完后不会自动归零，归零确认
        //      永不触发，每次补料空烧满 8s 预算，现场实测两次震动间隔 5~6s 全烧在这里）。

        /// <summary>
        /// 补料动作一次全流程（唯一口）：写触发动作 → 按"动作时长"等待动作跑完 →
        /// 停止并读回验证 → 歇位"震动间隔"。
        /// 生产脚本（ST5 轮内补料）/ 配置页"补料试振"按钮 / 主窗震动联动循环都调它，
        /// 杜绝三处三套实现分叉。
        /// 【为什么是纯时长制，不等"完成信号"】0x000F 模式字是持续命令寄存器，组动作跑完
        /// 也保持非零（见文件头【0x000F 协议事实】两次实证）——协议层面没有可用的完成信号，
        /// 轮询归零只会空烧预算（V4.4.226~228 的 8s 预算版实测每次补料白等约 5s）。
        /// 时长语义对两类动作都成立：连续动作（振散等）=震多久（旧硬编码 1s 同款行为）；
        /// 参数组1/2=厂商在控制器端调好的组序列总时长+余量（组序列到点自己停，这里只是
        /// 等它跑完再写停验证；厂商改组序列时长后现场把本值跟着调大即可，序列再长也不会
        /// 被截断——V4.4.222 的 1s 硬窗截断组动作、V4.4.226 的归零轮询空烧预算，两个极端
        /// 都不再有）。
        /// 【actionMs 动作时长参数】触发后等多久再停（毫秒），每台可配（料号 Comm
        /// [震动盘] FeederN_ActionMs，配置页"动作时长"数值框 100–10000，缺省按触发动作
        /// 分档：参数组=5000〔厂商组1 现场实测约 4s+余量〕，连续动作=1000〔旧硬编码行为〕，
        /// 见 VibFeederTableSync.DefaultActionMsFor）。传值入口三处同源：脚本
        /// GetFeederActionMs / 配置页当前台 ActionText / 主窗 GetFeederActionMs，
        /// 最终都经 VibFeederTableSync.ClampActionMs 钳到 [100,10000]。
        /// 【settleMs 歇位参数（V4.4.x 震动间隔开放）】停写停验证完再歇位多久，
        /// 每台可配（FeederN_Settle，配置页"震动间隔"0–5000 缺省 500）。
        /// 传值入口三处同源：脚本 GetFeederSettleMs / 配置页当前台 SettleText / 主窗
        /// GetFeederSettleMs，最终都经 VibFeederTableSync.ClampSettleMs 钳到 [0,5000]。
        /// 0=不歇位（两次震动真实间隔下限=停验证+拍照+脚本固定 50ms 间隔）。
        /// 【两次震动停顿构成（2026-10-18 现场对账公式，解释"设 500 体感停顿远超 500"）】
        /// 用户体感的"两次震动之间"静止 = (动作时长 − 厂商序列实际自停时长) + 停验证(约2ms)
        ///   + 歇位 + 下一轮拍照取数(约0.3s) + 轮间固定 Sleep(50)。配的歇位只是其中一段；
        /// 参数组场景"动作时长"若填比序列实际长很多（如组档缺省 5000 vs 序列约 4s），序列
        /// 停下后多出的纯空等会把停顿拖大——现场按厂商序列实测+0.1~0.3s 填最优（组1≈4100~4300）。
        /// 【日志】每次动作一行（模式/时长/停验证结果/歇位），现场掐表对不上时先看这行。
        /// 【返回】true=读回确认停住（0x000F 归零）；false=停不住/异常（调用方应把问题暴露，
        /// 绝不静默——停不住=盘持续振动，2026-10-04 曾实证震 10 分钟靠人工断电）。
        /// </summary>
        public bool RunActionOnce(ushort modeWithoutTrigger, int actionMs, int settleMs)
        {
            if (_disposed) return false;
            if (actionMs < 0) actionMs = 0;   // 防御：Thread.Sleep 负数抛 ArgumentOutOfRange，钳 0=触发即停
            if (settleMs < 0) settleMs = 0;   // 同上
            try
            {
                StartModeWithTrigger(modeWithoutTrigger);   // 写失败内部吞掉并断连标记，自愈见 EnsureMasterLocked
                int waitMs = actionMs;
                // Mock 演示不真等（无硬件，等再久也是空转）：钳到 300ms 保持演示节拍轻快，
                // 与旧归零确认版在 Mock 下约 300ms 完成的体感一致。
                if (MockBridge.IsEnabled && waitMs > 300) waitMs = 300;
                // 分段计时（2026-10-18 现场"两次震动间隔远超'震动间隔'配置值"排查加）：
                // 配置的 SettleMs 只是动作末尾的歇位一段，两次震动的真实间隔还包含"等足动作
                // 时长"（参数组场景厂商序列常先自己停，剩出现纯空等尾差）和"停验证"的写+读回；
                // 旧日志只打配置值，现场对账时看不出时间去哪了——这里把三段实际耗时全打出来。
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();   // 全程基准
                if (waitMs > 0) Thread.Sleep(waitMs);   // 等动作跑完（连续动作=震多久；参数组=组序列时长+余量）
                long actSpend = sw.ElapsedMilliseconds;   // 动作段实际耗时（=震多久；参数组则含序列跑完后到本段的空等尾差）
                bool stopped = StopVerified();
                long stopSpend = sw.ElapsedMilliseconds - actSpend;   // 停验证段实际耗时（一次写停+一次读回，预期几十ms；陡涨=超时重试在烧时间）
                if (settleMs > 0) Thread.Sleep(settleMs);   // 歇位（等料落稳再拍下一轮），每台可配见方法头
                long settleSpend = sw.ElapsedMilliseconds - actSpend - stopSpend;   // 歇位段实际耗时（与配置值同源，夜间 Timer 漂移±15ms 属正常）
                try
                {
                    LogHelper.Info(" [" + Name + "] 补料动作：模式" + modeWithoutTrigger
                        + " 等" + actSpend + "ms（配" + actionMs + "）"
                        + "→停" + stopSpend + "ms" + (stopped ? "已确认" : "【未确认】")
                        + "→歇" + settleSpend + "ms（配" + settleMs + "）"
                        + " 全程" + sw.ElapsedMilliseconds + "ms");
                }
                catch { /* 原因：日志线程自身异常则放弃，动作结果已由返回值表达 */ }
                return stopped;
            }
            catch (Exception ex)
            {
                try { LogHelper.Info(" [" + Name + "] 补料动作异常已隔离：" + ex.Message); } catch { }
                return false;
            }
        }

        /// <summary>
        /// 确保停止并读回验证（模式寄存器 0x000F 归零才算停；失败重试一轮，两轮都没确认停住
        /// 返回 false 由调用方告警暴露）。【为什么】Stop 写失败会被内部静默吞掉且 0x000F 可能
        /// 残留动作模式=盘持续振动且现场没人再写停止（2026-10-04 实证震 10 分钟）——本方法把
        /// "写停+读回验证+重试"收进控制器类，生产脚本 finally 兜底与补料动作共用同一语义。
        /// 【自愈分工】重连归通讯层（断连后下次读写已自动重连，见 EnsureMasterLocked），本方法
        /// 只管重试+验证。【代价】正常路径=一次写+一次读（几十毫秒）；故障路径最坏≈10s（写超时
        /// 3s+重连 2.3s+再写 3s+再读 3s），只在真故障时发生且换来"停必验证"的确定性。写 0 对
        /// 已停的盘是幂等命令，多写无副作用。
        /// </summary>
        public bool StopVerified()
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Stop();   // 写失败内部断连标记，下次读写前自动重连
                ushort[] v = ReadHoldingRegisters(0x000F, 1);
                if (v != null && v.Length >= 1 && v[0] == 0x0000) return true;
            }
            return false;
        }

        public void SetPlatformEnable(bool enable)
        {
            ushort value = enable ? (ushort)0xFF00 : (ushort)0x0000;
            WriteSingleRegister(0x0008, value);
        }

        public void ClearError()
        {
            WriteSingleRegister(0x0009, 0xFF00);
        }

        /// <summary>设置单轴参数（频率、电压、时间）。轴名非法记日志返回（旧版弹框后继续跑直接 NRE）。</summary>
        public void SetAxisParameters(string axis, ushort frequency, ushort voltage, ushort time)
        {
            ushort? baseAddr = GetAxisBaseAddress(axis);
            if (!baseAddr.HasValue)
            {
                try { LogHelper.Info(" [" + Name + "] 未知的轴名称: " + axis); } catch { }
                return;
            }
            ushort[] values = { frequency, voltage, time };
            WriteMultipleRegisters(baseAddr.Value, values);
        }

        private static ushort? GetAxisBaseAddress(string axis)
        {
            switch (axis)
            {
                case "上": return 0x0010;
                case "下": return 0x0013;
                case "左": return 0x0016;
                case "右": return 0x0019;
                case "左上": return 0x001C;
                case "右上": return 0x001F;
                case "左下": return 0x0022;
                case "右下": return 0x0025;
                case "垂直中": return 0x0028;
                case "水平中": return 0x002B;
                case "振散": return 0x002E;
                case "翻面": return 0x0034;
                default: return null;
            }
        }

        public void SetGatherParameters(ushort verticalTime, ushort horizontalTime, ushort totalTime)
        {
            ushort[] values = { verticalTime, horizontalTime, totalTime };
            WriteMultipleRegisters(0x0031, values);
        }

        /// <summary>设置组合步骤（1/2 组各 8 步×模式+时间）。参数非法记日志返回（旧版弹框后照写全零）。</summary>
        public void SetCombinationSteps(int groupIndex, Tuple<ushort, ushort>[] steps)
        {
            // 注：V4.2.1 原版用 C# 7 元组数组；本工程编译器版本混杂，改用 Tuple（语义一致）。
            if (groupIndex != 1 && groupIndex != 2)
            {
                try { LogHelper.Info(" [" + Name + "] 组合索引必须为1或2"); } catch { }
                return;
            }
            if (steps == null || steps.Length > 8)
            {
                try { LogHelper.Info(" [" + Name + "] 组合步骤最多8个"); } catch { }
                return;
            }
            ushort startAddr = (groupIndex == 1) ? (ushort)0x0060 : (ushort)0x0070;
            ushort[] data = new ushort[16];
            for (int i = 0; i < steps.Length; i++)
            {
                data[i * 2] = steps[i].Item1;
                data[i * 2 + 1] = steps[i].Item2;
            }
            WriteMultipleRegisters(startAddr, data);
        }

        public void SetLight(char channel, bool enable)
        {
            ushort addr = (channel == 'A') ? (ushort)0x0006 : (ushort)0x0007;
            ushort value = enable ? (ushort)0xFF00 : (ushort)0x0000;
            WriteSingleRegister(addr, value);
        }

        /// <summary>设置灯亮度（6~999）。越界记日志返回（旧版弹框后照写非法值）。</summary>
        public void SetLightBrightness(char channel, ushort brightness)
        {
            if (brightness < 6 || brightness > 999)
            {
                try { LogHelper.Info(" [" + Name + "] 亮度范围6~999"); } catch { }
                return;
            }
            ushort addr = (channel == 'A') ? (ushort)0x0050 : (ushort)0x0051;
            WriteSingleRegister(addr, brightness);
        }

        public void SetHopper(bool enable)
        {
            ushort value = enable ? (ushort)0xFF00 : (ushort)0x0000;
            WriteSingleRegister(0x0005, value);
        }

        // 料仓电压/频率地址不明确，暂不实现，保留通用方法
        // 用户如需设置，可自行使用 WriteSingleRegister 操作相应地址

        #endregion

        #region 模式常量（不含触发位，StartModeWithTrigger 自动加 b4）

        public static class Modes
        {
            public const ushort Up = 0;
            public const ushort Down = 1;
            public const ushort Left = 2;
            public const ushort Right = 3;
            public const ushort UpLeft = 4;
            public const ushort UpRight = 5;
            public const ushort DownLeft = 6;
            public const ushort Gather = 7;
            public const ushort DownRight = 8;
            public const ushort VerticalCenter = 9;
            public const ushort HorizontalCenter = 10;
            public const ushort Scatter = 11;    // 振散（脚本补料主用）
            public const ushort Flip = 12;       // 翻面
            public const ushort Combination2 = 13;
            public const ushort Combination1 = 14;
        }

        #endregion
    }
}
