using System;
using System.Runtime.InteropServices;
using System.Threading;
using Kaleidoscope.Utils;
using MvCamCtrl.NET;

namespace Kaleidoscope.Cameras
{
    /// <summary>
    /// 海康 2D 相机适配器（MvCameraControl.Net / MVS，GigE+USB3 双传输层）。
    ///
    /// 【干什么】海康工业相机的生命周期/参数/采流/软硬触发/取帧回调，帧统一成
    /// KxCamFrame.Mono8（深拷托管 buffer）上抛。像素提取管线（Mono8 快路径/SDK 转换/
    /// 回落钳制直拷/坏帧占位）在适配器内完成；下游图像容器构造（VisionPro CogImage8Grey
    /// + Stride 感知拷贝）由宿主桥做。
    ///
    /// 【为什么这么设计】（一比一对齐 HJVision MVHaiKangCamSDK，全部真机血泪沿用）
    ///   - SN 尾随 '\0'：chSerialNumber 是 ByValTStr 定长字符串。发现器一律 TrimEnd('\0')；
    ///     但 CheckSN 沿用老语义直接 == 比对定长串（改任何一边都可能破坏既有可运行组合），
    ///     SN 下拉一律走发现器（已 Trim），Open 前 SetCamSN 传入的是下拉值。
    ///   - CreateDevice↔OpenDevice↔DestroyDevice 严格配对：Open 失败必须 Destroy +
    ///     置 null（旧版泄漏句柄且后续误判相机可用）；CloseCam 补 Destroy 与 Create 配对。
    ///   - GigE 丢包重发默认关：Cognex 自家驱动默认开、海康 SDK 默认关，丢包行保持
    ///     0=黑条纹；Open 末尾、StartGrab 之前 MV_GIGE_SetResend_NET(1,10,50)
    ///     （对标 MVS 默认），失败只记日志不阻断。
    ///   - 像素格式血案（斜条纹）：相机 UserSet 掉电保持 Mono10/12 Packed 而设置没写上
    ///     时，按 Mono8 直拷逐行错位=斜条纹。EnsureMono8：下发 PixelFormat=Mono8 +
    ///     回读确认 + 失败留痕；非 Mono8 帧走 SDK MV_CC_ConvertPixelType_NET 转换，
    ///     失败回落钳制直拷。
    ///   - 回调线程纪律：转换函数在 SDK 回调线程跑，抛异常即崩进程，永不让异常冒泡；
    ///     SDK 异常/掉线回调同样不可耗时/弹窗；掉线经 RegisterExceptionCallBack 上报
    ///     0x00008000，委托必须保字段防 GC。
    ///   - 回调委托数组容量：静态 MvImageCallback[10] 必须与宿主相机枚举台数一致
    ///     （旧版开 5 个是笔误，第 6 台即 IndexOutOfRange）。
    ///   - 帧长钳制：Mono8 快路径按 nFrameLen 钳制拷贝（无脑拷 w*h 短帧越界读；
    ///     unmanaged 越界读 AccessViolation catch 接不住）；帧长 0 直接坏帧处理；
    ///     丢包 nLostPacket>0 必记日志。
    ///   - 软触发静默失败治理：未 StartGrab 时 TriggerSoftwareExecute 返非 0 无帧，
    ///     全链路零报错；现返回码非 0 即记 10 秒节流日志 + 探活断点（rc=0 也留痕，
    ///     区分"命令没发出去"与"命令发了帧没回来"）。
    ///   - GCHandle 纪律：像素转换的 pinned 句柄 finally Free（旧版 Alloc 丢弃返回值
    ///     永不 Free 泄漏）。
    ///   - 坏帧永不 null：坏帧头/超大尺寸/零帧长给 KxCamFrame.BlankMono8()（64x48 全 0
    ///     显式清零，VisionPro 自管内存不保证零初始化、实测残留 143 垃圾值）——
    ///     转换链"永不返回 null、永不抛"。
    ///
    /// 【怎么接】宿主壳组合持有；Open 前必须 CamNetInit（或 ShareDiscoveryFrom）+
    /// SetCamSN；诊断面 LastFrameDiag/QueryPixelFormat/EnsureMono8 供快测页/排查用，
    /// 全部永不抛。帧经 AcqImageCallbackFunction 注册（RegisterImageCallBackEx）。
    /// </summary>
    public class KxHik2DCamera : KxCameraDeviceBase
    {
        MyCamera.MV_CC_DEVICE_INFO_LIST m_pDeviceList;
        private MyCamera _camera;
        private int OpenCurrentNumber;
        /// <summary>扫描出的网络中相机数目（只读）。</summary>
        public int CamCountInNet { get { return _CamNumCount; } }
        private DateTime _mFilteringTime = new DateTime();
        private bool _CamSoftTrgEnable;
        private int _CamNumCount;

        public override void CamNetInit()
        {
            int nRet;
            nRet = MyCamera.MV_CC_EnumDevices_NET(MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE, ref m_pDeviceList);
            if (MyCamera.MV_OK != nRet)
            {
                NoteNoDevice("枚举相机错误");
                return;
            }
            _CamNumCount = (int)m_pDeviceList.nDeviceNum;
        }

        /// <summary>复用同品牌首台的发现列表（SDK 原生指针结构浅拷贝，同一打开阶段内只读）。</summary>
        public override void ShareDiscoveryFrom(KxCameraDeviceBase src)
        {
            KxHik2DCamera mSrc = src as KxHik2DCamera;
            if (mSrc == null)
                throw new InvalidOperationException("海康发现列表复用失败（来源非同品牌）");
            m_pDeviceList = mSrc.m_pDeviceList;
            _CamNumCount = mSrc._CamNumCount;
        }

        public override bool Open()
        {
            int nRet;
            if (CheckSN(CamSN))
            {
                try
                {
                    if (_camera != null)
                    {
                        _camera.MV_CC_CloseDevice_NET();
                        _camera = null;
                    }
                    MyCamera.MV_CC_DEVICE_INFO device =
                        (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(m_pDeviceList.pDeviceInfo[OpenCurrentNumber],
                                                                       typeof(MyCamera.MV_CC_DEVICE_INFO));

                    _camera = new MyCamera();
                    nRet = _camera.MV_CC_CreateDevice_NET(ref device);
                    if (MyCamera.MV_OK != nRet)
                    {
                        _camera = null;
                        NoteNoDevice("打开相机失败");
                        return false;
                    }
                    nRet = _camera.MV_CC_OpenDevice_NET();
                    if (MyCamera.MV_OK != nRet)
                    {
                        _camera.MV_CC_DestroyDevice_NET();
                        _camera = null;
                        NoteNoDevice("打开相机失败");
                        return false;
                    }

                    try
                    {
                        mExceptionCallback = new MyCamera.cbExceptiondelegate(MvExceptionCallback);
                        _camera.MV_CC_RegisterExceptionCallBack_NET(mExceptionCallback, new IntPtr(CamIndex));
                    }
                    catch { }

                    // 使能 GigE 丢包重发（对标 MVS 默认：使能=1，单帧最多重发 10% 包数，单次等待 50ms；
                    // 失败只记日志不阻断）。
                    try
                    {
                        int rcResend = _camera.MV_GIGE_SetResend_NET(1, 10, 50);
                        if (rcResend != 0)
                        {
                            try { LogHelper.Info(DateTime.Now + " 海康重发使能返回 rc=" + rcResend + "（相机会按无重发旧模式工作，条纹风险仍在，查固件/驱动版本）。"); }
                            catch { }
                        }
                    }
                    catch (Exception exResend)
                    {
                        try { LogHelper.Info(DateTime.Now + " 海康重发使能异常（保持无重发旧模式）：" + exResend.Message); }
                        catch { }
                    }
                }
                catch (Exception e)
                {
                    return false;
                }
                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>检查该 SN 是否在当前网络中（GigE/USB 双传输比对，注意定长串尾随 '\0'）。</summary>
        public bool CheckSN(string CamSN)
        {
            for (int i = 0; i < m_pDeviceList.nDeviceNum; i++)
            {
                MyCamera.MV_CC_DEVICE_INFO device = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(m_pDeviceList.pDeviceInfo[i], typeof(MyCamera.MV_CC_DEVICE_INFO));
                if (device.nTLayerType == MyCamera.MV_GIGE_DEVICE)
                {
                    IntPtr buffer = Marshal.UnsafeAddrOfPinnedArrayElement(device.SpecialInfo.stGigEInfo, 0);
                    MyCamera.MV_GIGE_DEVICE_INFO gigeInfo = (MyCamera.MV_GIGE_DEVICE_INFO)Marshal.PtrToStructure(buffer, typeof(MyCamera.MV_GIGE_DEVICE_INFO));
                    // 注意：此处与 KxCameraDiscovery 不同——老版直接 == 比对定长串（含尾随 '\0'），
                    // 配置 SN 无 '\0' 时恒不等。沿用老语义（改任何一边都可能破坏既有可运行组合），
                    // SN 下拉一律走 KxCameraDiscovery（已 Trim），Open 前 SetCamSN 传入的是下拉值。
                    if (gigeInfo.chSerialNumber == CamSN)
                    {
                        OpenCurrentNumber = i;
                        return true;
                    }
                }
                else if (device.nTLayerType == MyCamera.MV_USB_DEVICE)
                {
                    IntPtr buffer = Marshal.UnsafeAddrOfPinnedArrayElement(device.SpecialInfo.stUsb3VInfo, 0);
                    MyCamera.MV_USB3_DEVICE_INFO usbInfo = (MyCamera.MV_USB3_DEVICE_INFO)Marshal.PtrToStructure(buffer, typeof(MyCamera.MV_USB3_DEVICE_INFO));

                    if (usbInfo.chSerialNumber == CamSN)
                    {
                        OpenCurrentNumber = i;
                        return true;
                    }
                }
            }
            return false;
        }

        public override void SetCamSN(string mark)
        {
            CamSN = mark;
        }

        public override void SetCamIndex(int camIndex)
        {
            CamIndex = camIndex;
        }

        /// <summary>
        /// 手动触发一次（返回码非 0 即记日志，10 秒节流；异常不上抛）。
        /// 【采流武装兜底，2026-10-27 现场血泪】软触发只在采流运行中出帧——流停了
        /// TriggerSoftware rc=0 也静默无帧（"关实时后手动触发磁贴没图"根因之一）。
        /// 软触发前先补 StartGrabbing：流没跑时武装成功（本拍即出帧）；已跑时重复
        /// 启动返回非零（海康对运行中的流再 Start 报错，无害，静默忽略防误报刷屏）。
        /// 武装真异常（句柄坏等）由 rcSrc/rcTrg 失败日志与"无帧排查"链路呈现。
        /// </summary>
        public override void GrabOne()
        {
            if (_camera == null) return;
            try
            {
                try { _camera.MV_CC_StartGrabbing_NET(); } catch { }   // 武装兜底：已跑报错=正常，静默
                int rcSrc = _camera.MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                int rcTrg = _camera.MV_CC_TriggerSoftwareExecute_NET();
                if (rcSrc != 0 || rcTrg != 0)
                {
                    WarnGrab("软触发下发失败rc（触发源/执行）=" + rcSrc + "/" + rcTrg
                        + "（相机未开抓取时多半因此：查取像回调是否注册、StartGrab 是否执行）。");
                }
                else
                {
                    // 探活断点（2026-10-27）：rc=0 也留痕（同节流），区分"命令没发出去"
                    // 与"命令发了帧没回来"——后者才是回收/路由环节的问题。
                    WarnGrab("软触发执行 rc=0 已下发（帧未进托管则查注册/采流）");
                }
            }
            catch (Exception exGrab)
            {
                WarnGrab("软触发异常：" + exGrab.Message);
            }
        }

        private int mGrabWarnTick = 0;

        private void WarnGrab(string msg)
        {
            try
            {
                int now = Environment.TickCount;
                if (now - mGrabWarnTick < 10000) return;
                mGrabWarnTick = now;
                LogHelper.Info(DateTime.Now + " 海康软触发异常：" + msg);
            }
            catch { }
        }

        public override void StartGrab()
        {
            if (_camera == null) return;
            // 采流启动返回码必查（2026-10-27 断点增强：旧版静默吞掉，采流没开时软触发
            // rc=0 却永远无帧，现场无法分辨"没采流"还是"回调断了"。每次装配/开采才调，
            // 不是每帧调用，失败日志不会刷屏）。
            int rcStart = _camera.MV_CC_StartGrabbing_NET();
            if (rcStart != 0)
            {
                try { LogHelper.Info(DateTime.Now + " 海康采流启动失败 Cam" + (CamIndex + 1) + " rc=" + rcStart + "(0x" + rcStart.ToString("X8") + ")"); }
                catch { }
            }
        }

        public override void StopGrab()
        {
            if (_camera == null) return;
            _camera.MV_CC_StopGrabbing_NET();
        }

        public override void UnregisterImageCallback()
        {
        }

        /// <summary>回调委托数组容量与宿主相机枚举台数一致（10 台；旧版开 5 个是笔误，第 6 台即越界）。</summary>
        public static MyCamera.cbOutputExdelegate[] MvImageCallback = new MyCamera.cbOutputExdelegate[10];

        private int mPixWarnTick = 0;

        /// <summary>SDK 异常回调委托引用（保字段防 GC 回收导致回调失效）。</summary>
        private MyCamera.cbExceptiondelegate mExceptionCallback;

        /// <summary>
        /// 海康 SDK 异常消息回调：仅处理设备掉线(0x00008000)，转 RaiseDeviceOffline。
        /// 运行在 SDK 内部线程，不可做耗时/弹窗操作。
        /// </summary>
        private void MvExceptionCallback(uint nMsgType, IntPtr pUser)
        {
            try
            {
                if (nMsgType == 0x00008000)
                {
                    RaiseDeviceOffline(CamIndex);
                }
            }
            catch { }
        }

        public override void AcqImageCallbackFunction()
        {
            if (_camera == null) return;
            MvImageCallback[CamIndex] = new MyCamera.cbOutputExdelegate(CaptureCallback);
            // 注册返回码必查（2026-10-27 断点增强：旧版吞 rc，注册失败则帧永远不进托管，
            // 现场只见"软触发 rc=0 无帧"无从查起。装配/兜底注册才调，一次性动作不刷屏）。
            int rcReg = _camera.MV_CC_RegisterImageCallBackEx_NET(MvImageCallback[CamIndex], new IntPtr(CamIndex));
            if (rcReg != 0)
            {
                try { LogHelper.Info(DateTime.Now + " 海康取像回调注册失败 Cam" + (CamIndex + 1) + " rc=" + rcReg + "(0x" + rcReg.ToString("X8") + ")——帧将无人接收"); }
                catch { }
            }
        }

        private long _mDroppedFrameCount = 0;

        /// <summary>帧进托管回调的探活计数（按 CamIndex 槽；首帧+每 100 帧打一条，2026-10-27 排查断点）。</summary>
        private static long[] s_frameProbeCount = new long[10];

        public void CaptureCallback(IntPtr pData, ref MyCamera.MV_FRAME_OUT_INFO_EX pFrameInfo, IntPtr mThisCamID)
        {
            if (DateTime.Now.Subtract(_mFilteringTime).TotalMilliseconds < 10)
            {
                if (System.Threading.Interlocked.Increment(ref _mDroppedFrameCount) % 100 == 1)
                {
                    LogHelper.Info(DateTime.Now + string.Format(" 相机[{0}]帧间隔<10ms被过滤，已累计{1}帧", (int)mThisCamID, _mDroppedFrameCount));
                }
                return;
            }
            _mFilteringTime = DateTime.Now;
            // 探活断点（2026-10-27 手动触发无帧排查）：有效帧进了托管回调才计数——
            // "帧已进入托管回调"日志出现=SDK→托管桥通；不出现=断在 SDK 侧（注册/采流/触发模式）。
            int probeIdx = (int)mThisCamID;
            if (probeIdx >= 0 && probeIdx < 10)
            {
                long c = System.Threading.Interlocked.Increment(ref s_frameProbeCount[probeIdx]);
                if (c == 1 || c % 100 == 0)
                {
                    try { LogHelper.Info(DateTime.Now + " 海康帧已进入托管回调 Cam" + (probeIdx + 1) + " 有效帧累计=" + c); }
                    catch { }
                }
            }

            KxCamFrame frame = ExtractFrame(pData, ref pFrameInfo);
            RaiseFrame((int)mThisCamID, frame);
        }

        public override void CamSetHeartbeatTime(long beatTime)
        {
        }

        public override void CamSetTriggerSource(string mTriggerSource)
        {
            if (_camera == null) return;
            try
            {
                _camera.MV_CC_SetEnumValue_NET("AcquisitionMode", 2);
                if (mTriggerSource == "Line0")
                {
                    _camera.MV_CC_SetEnumValue_NET("TriggerMode", 1);
                    _camera.MV_CC_SetEnumValue_NET("TriggerSource", 1);
                }
                else
                {
                    string detMono8 = "";
                    try { detMono8 = EnsureMono8(); }
                    catch { detMono8 = "格式确认异常"; }
                    try { LogHelper.Info(DateTime.Now + " 海康触发源切Software，" + detMono8); }
                    catch { }
                    // 触发模式/触发源下发返回码必查（2026-10-27 断点增强：dfg1/dfg2 旧版
                    // 丢弃 rc，TriggerMode 没写上时软触发 rc=0 却静默无效=无帧，现场无从查起。
                    // 仅失败时打日志，成功不打防刷屏）。
                    int dfg1 = _camera.MV_CC_SetEnumValue_NET("TriggerMode", 1);
                    int dfg2 = _camera.MV_CC_SetEnumValue_NET("TriggerSource", 0);
                    if (dfg1 != 0 || dfg2 != 0)
                    {
                        try { LogHelper.Info(DateTime.Now + " 海康触发模式/源下发失败 Cam" + (CamIndex + 1) + " rc=" + dfg1 + "/" + dfg2 + "（软触发将静默无效，查采流态/句柄）"); }
                        catch { }
                    }
                }
                _CamSoftTrgEnable = (mTriggerSource == "Software");
            }
            catch
            {
            }
        }

        public override void CamSetExposureTime(double mExposureTime, double mGainValue)
        {
            if (_camera == null) return;
            int grgrg = 0;
            try
            {
                // 新固件特征名优先、旧名回落，下发后回读验真，结果记日志。
                float expUs = (float)mExposureTime * 1000;
                float gainRaw = (float)mGainValue;
                int rcE = _camera.MV_CC_SetFloatValue_NET("ExposureTime", expUs);
                string expKey = "ExposureTime";
                if (rcE != 0)
                {
                    rcE = _camera.MV_CC_SetFloatValue_NET("ExposureTimeAbs", expUs);
                    expKey = "ExposureTimeAbs";
                }
                int rcG = _camera.MV_CC_SetFloatValue_NET("Gain", gainRaw);
                string gainKey = "Gain";
                if (rcG != 0)
                {
                    rcG = _camera.MV_CC_SetFloatValue_NET("GainRaw", gainRaw);
                    gainKey = "GainRaw";
                }
                grgrg = rcE;
                float backE = -1, backG = -1;
                try
                {
                    MyCamera.MVCC_FLOATVALUE fv = new MyCamera.MVCC_FLOATVALUE();
                    if (_camera.MV_CC_GetFloatValue_NET(expKey, ref fv) == 0) backE = fv.fCurValue;
                    MyCamera.MVCC_FLOATVALUE gv = new MyCamera.MVCC_FLOATVALUE();
                    if (_camera.MV_CC_GetFloatValue_NET(gainKey, ref gv) == 0) backG = gv.fCurValue;
                }
                catch { }
                try
                {
                    LogHelper.Info(DateTime.Now + string.Format(
                        " 海康曝光增益下发：曝光{0}ms({1}us) rc={2} 回读={3}us，增益{4} rc={5} 回读={6}",
                        mExposureTime, expUs, rcE, backE, mGainValue, rcG, backG));
                }
                catch { }
            }
            catch
            {
            }
        }

        public override void ClearImageBuffer()
        {
            if (_camera != null)
            {
                _camera.MV_CC_ClearImageBuffer_NET();
            }
        }

        /// <summary>关闭相机（CreateDevice↔DestroyDevice 配对释放句柄）。</summary>
        public override void CloseCam()
        {
            ClearImageBuffer();
            try
            {
                if (_camera != null)
                {
                    _camera.MV_CC_CloseDevice_NET();
                    _camera.MV_CC_DestroyDevice_NET();
                    _camera = null;
                }
            }
            catch
            {
            }
            GC.Collect();
        }

        /// <summary>Mono8 像素格式枚举值（快路径判据）。</summary>
        private const uint PixMono8 = 0x01080001;

        private string mLastFrameDiag = "无帧";

        /// <summary>读末帧诊断（"无帧"或"Mono8,len=…,lost=…"；永不抛）。</summary>
        public string LastFrameDiag()
        {
            try { return string.IsNullOrEmpty(mLastFrameDiag) ? "无帧" : mLastFrameDiag; }
            catch { return "无帧"; }
        }

        /// <summary>像素格式值→短名（日志用；未知值给十六进制，不抛）。</summary>
        public static string PixFormatName(uint v)
        {
            try
            {
                switch (v)
                {
                    case 0x01080001: return "Mono8";
                    case 0x01080002: return "Mono8_Signed";
                    case 0x01100003: return "Mono10";
                    case 0x01100005: return "Mono12";
                    case 0x01100007: return "Mono16";
                    case 0x01100025: return "Mono14";
                    case 0x010C0004: return "Mono10_Packed";
                    case 0x010C0006: return "Mono12_Packed";
                    case 0x01080008: return "BayerGR8";
                    case 0x01080009: return "BayerRG8";
                    case 0x0108000A: return "BayerGB8";
                    case 0x0108000B: return "BayerBG8";
                    default: return "未知(0x" + v.ToString("X8") + ")";
                }
            }
            catch { return "未知格式"; }
        }

        /// <summary>
        /// 强制 Mono8 并回读确认（防 UserSet 掉电保持非 Mono8 致直拷错位条纹）。
        /// 返回说明串（调用方记日志/显示），永不抛。
        /// </summary>
        public string EnsureMono8()
        {
            try
            {
                if (_camera == null) return "相机未打开，格式未确认。";
                int rcSet = _camera.MV_CC_SetEnumValue_NET("PixelFormat", PixMono8);
                string read = QueryPixelFormat();
                bool ok = false;
                try { ok = read.StartsWith("Mono8"); } catch { }
                return "PixelFormat 下发 rc=" + rcSet + "，回读" + read
                    + (ok ? "（已是 Mono8）。" : "（非 Mono8！条纹多半因此来，查相机 UserSet 掉电保持）。");
            }
            catch (Exception ex) { return "格式确认异常：" + ex.Message; }
        }

        /// <summary>读当前 PixelFormat（永不抛）。</summary>
        public string QueryPixelFormat()
        {
            try
            {
                if (_camera == null) return "未打开";
                MyCamera.MVCC_ENUMVALUE ev = new MyCamera.MVCC_ENUMVALUE();
                int rc = _camera.MV_CC_GetEnumValue_NET("PixelFormat", ref ev);
                if (rc != 0) return "读失败rc=" + rc;
                return PixFormatName(ev.nCurValue) + "(0x" + ev.nCurValue.ToString("X8") + ")";
            }
            catch (Exception ex) { return "读异常：" + ex.Message; }
        }

        private void WarnFrame(string msg)
        {
            try
            {
                int now = Environment.TickCount;
                if (now - mPixWarnTick < 10000) return;
                mPixWarnTick = now;
                LogHelper.Info(DateTime.Now + " 海康帧异常：" + msg);
            }
            catch { }
        }

        /// <summary>
        /// 非 Mono8 帧经 SDK 转 Mono8（无句柄/超大帧/转失败回 null，调用方回落钳制直拷，永不抛）。
        /// </summary>
        private byte[] ConvertToMono8(IntPtr pData, ref MyCamera.MV_FRAME_OUT_INFO_EX info)
        {
            GCHandle gh = default(GCHandle);
            try
            {
                if (_camera == null || pData == IntPtr.Zero) return null;
                int w = (int)info.nWidth, h = (int)info.nHeight;
                if (w <= 0 || h <= 0 || w > 16384 || h > 16384) return null;
                long want = (long)w * h;
                if (want <= 0 || want > 100L * 1024 * 1024) return null;
                if (info.nFrameLen == 0) return null;
                byte[] dst = new byte[want];
                gh = GCHandle.Alloc(dst, GCHandleType.Pinned);
                MyCamera.MV_PIXEL_CONVERT_PARAM cp = new MyCamera.MV_PIXEL_CONVERT_PARAM();
                cp.nWidth = (ushort)w;
                cp.nHeight = (ushort)h;
                cp.enSrcPixelType = info.enPixelType;
                cp.pSrcData = pData;
                cp.nSrcDataLen = info.nFrameLen;
                cp.enDstPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8;
                cp.pDstBuffer = gh.AddrOfPinnedObject();
                cp.nDstBufferSize = (uint)want;
                cp.nRes = new uint[4];
                int rc = _camera.MV_CC_ConvertPixelType_NET(ref cp);
                if (rc != 0) return null;
                return dst;
            }
            catch { return null; }
            finally { try { if (gh.IsAllocated) gh.Free(); } catch { } }
        }

        /// <summary>
        /// SDK 帧抽成 KxCamFrame（Mono8 管线：快路径钳拷→SDK 像素转换→回落直拷→坏帧占位）。
        /// 转换永不返回 null、永不抛（SDK 回调线程抛异常即崩进程）；
        /// 行跨距≠宽的填充对齐属下游图像容器（宿主桥按目标容器 Stride 处理）。
        /// </summary>
        public KxCamFrame ExtractFrame(IntPtr pData, ref MyCamera.MV_FRAME_OUT_INFO_EX info)
        {
            try
            {
                int w = (int)info.nWidth, h = (int)info.nHeight;
                uint pix = 0;
                try { pix = (uint)info.enPixelType; } catch { }
                try
                {
                    mLastFrameDiag = PixFormatName(pix) + ",len=" + info.nFrameLen
                        + ",lost=" + info.nLostPacket;
                }
                catch { }
                try
                {
                    if (info.nLostPacket != 0)
                        WarnFrame("丢包 " + info.nLostPacket + " 个（" + w + "x" + h + " " + PixFormatName(pix) + "，丢包行显示黑条纹，查网线/交换机端口/巨帧；已开重发仍丢说明链路质量差）。");
                }
                catch { }
                if (pData == IntPtr.Zero || w <= 0 || h <= 0 || w > 16384 || h > 16384)
                {
                    WarnFrame("坏帧头（空指针或尺寸 " + w + "x" + h + "），给占位黑图。");
                    return KxCamFrame.BlankMono8();
                }
                if (info.nFrameLen == 0)
                {
                    WarnFrame("帧长为 0（" + w + "x" + h + " " + PixFormatName(pix) + "），按坏帧处理，给占位黑图。");
                    return KxCamFrame.BlankMono8();
                }
                byte[] pixels = null;
                if (pix == PixMono8)
                {
                    int want = w * h;
                    int have = (int)info.nFrameLen;
                    int take = (have > 0) ? Math.Min(want, have) : want;
                    pixels = new byte[want];
                    Marshal.Copy(pData, pixels, 0, take);
                    if (take != want)
                        WarnFrame("Mono8 帧长对不上 want=" + want + " have=" + have + "（只拷" + take + "字节，尾部补黑）。");
                }
                else
                {
                    pixels = ConvertToMono8(pData, ref info);
                    if (pixels == null)
                    {
                        WarnFrame("非 Mono8 帧（" + PixFormatName(pix) + "）SDK 转换失败，回落直拷（可能条纹）。");
                        int want = w * h;
                        int have = (int)info.nFrameLen;
                        int take = (have > 0) ? Math.Min(want, have) : want;
                        pixels = new byte[want];
                        Marshal.Copy(pData, pixels, 0, take);
                    }
                }
                return KxCamFrame.Mono8(w, h, pixels);
            }
            catch (Exception ex)
            {
                try { WarnFrame("转换兜底：" + ex.Message); } catch { }
                return KxCamFrame.BlankMono8();
            }
        }
    }
}
