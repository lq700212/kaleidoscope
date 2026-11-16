using System;
using Kaleidoscope.Utils;

namespace Kaleidoscope.Cameras
{
    /// <summary>
    /// 工业相机抽象基类（三品牌统一采集口：生命周期/参数/启停/触发/回调/掉线）。
    ///
    /// 【干什么】定义三品牌（大恒/海康 2D/海康 3D）适配器的统一抽象面：打开关闭、
    /// 采流启停、软硬触发、曝光增益、心跳、回调注册/注销、掉线事件、取帧事件。
    /// 帧以 KxCamFrame（中性 DTO）上抛——本基类零下游图像库类型，宿主用自己的桥
    /// 把 KxCamFrame 转成自家图像（VisionPro/Halcon/OpenCV…）。
    ///
    /// 【为什么这么设计】（一比一对齐 HJVision CameraAbstract；结构差异见下）
    ///   - 相机编号用 int（CamIndex，0-based）：宿主的业务枚举（如 CamId Cam1..Cam10）
    ///     自行映射，库不带业务语义。
    ///   - 帧事件用 KxCamFrame 而非宿主图像类型：采集骨架（枚举/打开/触发/回调/掉线/
    ///     参数）本身与图像库无关，焊死宿主图像类型会让库被迫引 VisionPro（商业授权
    ///     GAC 组件）——违背"换新项目底层一行不改"的库定位。
    ///   - 设备缺失提示统一出口 NoteNoDevice：启动期模态框躲 splash 后=软件"卡住不动"
    ///     （N 台相机=N 个框，UI 线程被框 60 秒即调试器超时血泪）——QuietNoDeviceUi
    ///     静默期只记日志；AlertSink 委托由宿主接真弹窗（不接则静默丢弃）。
    ///   - 同品牌枚举复用 ShareDiscoveryFrom：MV_CC_EnumDevices_NET 每次全网广播
    ///     秒级、大恒 UpdateAllDeviceList 约 200ms，N 台同品牌 N 次扫描不可接受——
    ///     首台正常枚举、后继浅拷贝发现列表（原生指针结构体，同一打开阶段内只读共享
    ///     安全）；拷贝失败调用方回落重枚举。
    ///
    /// 【怎么接】宿主组合壳持有本基类实例（壳保留宿主自己的事件签名/业务枚举并转发）；
    /// 三品牌适配器实现全部抽象方法。回调线程纪律（三品牌同款，实现类必须遵守）：
    /// SDK 回调线程里转换/上抛绝不抛异常、绝不弹窗、绝不长耗时——抛异常即崩进程。
    /// 事件在 SDK 内部线程触发，UI 订阅方自行封送。
    /// </summary>
    public abstract class KxCameraDeviceBase
    {
        /// <summary>相机唯一 SN。</summary>
        public string CamSN { get; protected set; }

        /// <summary>相机编号（0-based int；宿主业务枚举自行映射）。</summary>
        public int CamIndex;

        /// <summary>启动期静默开关：true=设备缺失只记日志，不走 AlertSink。主窗体构造期置 true 后不再复位（两处调用方各有自己的汇总提示，子 SDK 再扰即重复）。</summary>
        public static bool QuietNoDeviceUi = false;

        /// <summary>设备缺失提示出口（宿主接真弹窗，测试接内存；不接则静默丢弃）。静默期只记日志，不进这里。</summary>
        public static Action<string> AlertSink;

        /// <summary>构造（SN 空串起步，SetCamSN 装配期填）。</summary>
        protected KxCameraDeviceBase()
        {
            CamSN = "";
        }

        /// <summary>
        /// 设备缺失提示统一出口：静默期记日志，否则走 AlertSink。
        /// 失败状态由返回 false + 状态表承载，调用方照常能感知。
        /// </summary>
        protected static void NoteNoDevice(string msg)
        {
            try
            {
                if (QuietNoDeviceUi) { try { LogHelper.Info(DateTime.Now + " " + msg); } catch { } }
                else
                {
                    try { if (AlertSink != null) AlertSink(msg); } catch { }
                }
            }
            catch { }
        }

        // ==================================================================
        // 抽象面（三品牌各自实现；宿主壳逐成员转发）
        // ==================================================================

        /// <summary>枚举网络中的相机列表（建立本实例的发现快照；同品牌首台调用）。</summary>
        public abstract void CamNetInit();

        /// <summary>
        /// 复用同品牌首台实例的枚举结果（首台正常枚举，后继同品牌浅拷贝发现列表；
        /// 拷贝失败调用方回落重枚举）。默认空实现（3D 的 Open 不依赖枚举表）。
        /// </summary>
        public virtual void ShareDiscoveryFrom(KxCameraDeviceBase src) { }

        /// <summary>打开相机（成功返回 true；失败经 NoteNoDevice 提示，不抛异常）。</summary>
        public abstract bool Open();

        /// <summary>设置相机的心跳超时时间。</summary>
        public abstract void CamSetHeartbeatTime(long beatTime);

        /// <summary>设置触发源（软触发"Software"，硬触发"Line0"）。</summary>
        public abstract void CamSetTriggerSource(string mTriggerSource);

        /// <summary>设置相机曝光增益参数（曝光毫秒 + 增益）。</summary>
        public abstract void CamSetExposureTime(double mExposureTime, double mGainValue);

        /// <summary>开启抓图（采流启动；返回码检查纪律见各实现）。</summary>
        public abstract void StartGrab();

        /// <summary>停止抓图。</summary>
        public abstract void StopGrab();

        /// <summary>手动触发一次（软触发；未打开直接跳过，失败记日志不向调用方抛）。</summary>
        public abstract void GrabOne();

        /// <summary>清理所有相机当前 buffer 内的图像。</summary>
        public abstract void ClearImageBuffer();

        /// <summary>关闭相机（流关闭，设备对象关闭，反初始化，释放资源；资源配对纪律见各实现）。</summary>
        public abstract void CloseCam();

        /// <summary>设置相机唯一标志（SN）。</summary>
        public abstract void SetCamSN(string mark);

        /// <summary>设置相机编号（0-based int）。</summary>
        public abstract void SetCamIndex(int camIndex);

        /// <summary>注册取像回调（注册返回码必查——注册失败则帧永远不进托管，现场只见"软触发 rc=0 无帧"无从查起）。</summary>
        public abstract void AcqImageCallbackFunction();

        /// <summary>注销取像回调。</summary>
        public abstract void UnregisterImageCallback();

        // ==================================================================
        // 事件（SDK 内部线程触发；UI 订阅方自行封送）
        // ==================================================================

        /// <summary>采集帧事件（参数=相机编号 0-based，帧=中性 DTO；坏帧给占位黑图，永不 null 永不抛）。</summary>
        public event Action<int, KxCamFrame> FrameReady;

        /// <summary>设备掉线事件（参数=相机编号 0-based）。SDK 检测到掉线后上报，宿主更新状态。</summary>
        public event Action<int> DeviceOfflineEvent;

        /// <summary>返回 FrameReady 是否已绑定（防重复订阅）。</summary>
        public bool FrameReadyHave()
        {
            return FrameReady != null;
        }

        /// <summary>返回 DeviceOfflineEvent 是否已绑定（防重复订阅）。</summary>
        public bool DeviceOfflineEventHave()
        {
            return DeviceOfflineEvent != null;
        }

        /// <summary>由各品牌 SDK 子类在其帧回调里调用：转发给订阅者（SDK 回调线程上触发）。</summary>
        protected void RaiseFrame(int camIndex, KxCamFrame frame)
        {
            var h = FrameReady;
            if (h != null) h(camIndex, frame);
        }

        /// <summary>由各品牌 SDK 子类在其掉线回调中调用，转发给订阅者。</summary>
        protected void RaiseDeviceOffline(int camIndex)
        {
            var h = DeviceOfflineEvent;
            if (h != null) h(camIndex);
        }
    }
}
