using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using Kaleidoscope.Utils;
using GxIAPINET;

namespace Kaleidoscope.Cameras
{
    /// <summary>
    /// 大恒相机适配器（GxIAPINET / Galaxy SDK）。
    ///
    /// 【干什么】大恒 GigE 相机的生命周期/参数/采流/软硬触发/取帧回调，帧以
    /// KxCamFrame.Mono8（深拷托管 buffer）上抛。像素转换在适配器内完成
    /// （ConvertToRaw8→byte[]），下游图像容器构造（VisionPro CogImage8Grey 深拷）
    /// 由宿主桥做。
    ///
    /// 【为什么这么设计】（一比一对齐 HJVision DaHengCamSDK，全部真机血泪沿用）
    ///   - 工厂 Init/Uninit 进程级配对：IGXFactory.Init/Uninit 是进程级资源，
    ///     构造即 Init（sDaHengOpenCount++）、CloseCam 减到 0（最后一台）才 Uninit——
    ///     旧版每台 CloseCam 都 Uninit，多台大恒时第一台关闭即端掉其余设备环境。
    ///     KxCameraDiscovery 故意不调 Init（多调一次打破配对）。
    ///   - SDK 缓冲回调后即被复用：ConvertToRaw8 返回的指针在回调返回后被下一帧覆盖，
    ///     旧版直接包下游图像异步使用会"花屏/图文不符"——必须 Marshal.Copy 深拷进
    ///     托管数组（KxCamFrame.Pixels8 语义）。
    ///   - Open 失败禁调 CloseCam（配对计数红线）：Open 抛异常时设备从未计入
    ///     sDaHengOpenCount，此时 CloseCam 会把计数减成负数/提前 Uninit。
    ///   - 离线回调接口兜底：本版本 GxIAPINET 的 IDeviceOfflineCallback 是 internal
    ///     （官方新版才公开），掉线检测走兜底：SDK 关键调用抛异常→ReportOfflineIfDead
    ///     上报，10 秒节流。
    ///   - GevSCPSPacketSize 最优包大小：GigE 设备 Open 后查 SDK 支持即设
    ///     GetOptimalPacketSize（提升带宽利用率，防丢包）。
    ///   - 帧防抖：帧间隔 <10ms 的帧丢弃（SDK 偶发同帧重投），累计 100 帧记一条
    ///     可感知不刷屏。
    ///
    /// 【怎么接】宿主壳组合持有；Open 前必须 CamNetInit（或 ShareDiscoveryFrom）+
    /// SetCamSN；回调经 AcqImageCallbackFunction 注册（RegisterCaptureCallback）。
    /// 注意 CloseCam 内含 GC.Collect()（原版沿用：大恒 SDK 原生资源回收依赖终结器）。
    /// </summary>
    public class KxDaHengCamera : KxCameraDeviceBase
    {
        private IGXFeatureControl _CamFeatureControl;
        private IGXDevice _Camera;
        private IGXStream _CamStream;
        private bool _CamSoftTrgEnable;

        /// <summary>扫描出的网络中相机数目（只读）。</summary>
        public int CamCountInNet { get { return _CamNumCount; } }

        private int _CamNumCount;
        private List<IGXDeviceInfo> _listcamerainfo;
        private DateTime _mFilteringTime = new DateTime();

        /// <summary>
        /// 当前已成功打开的大恒相机数量（进程级计数）。
        /// IGXFactory.Init/Uninit 进程级配对：Open 成功 +1，CloseCam -1，
        /// 减到 0（最后一台）才 Uninit（旧版每台都 Uninit，多台时第一台关闭即端掉其余）。
        /// </summary>
        private static int sDaHengOpenCount = 0;

        private DateTime _mLastOfflineReportTime = DateTime.MinValue;

        /// <summary>
        /// SDK 关键调用抛异常时调：大概率设备掉线，上报掉线事件（10 秒节流）。
        /// </summary>
        private void ReportOfflineIfDead(Exception ex)
        {
            try
            {
                if ((DateTime.Now - _mLastOfflineReportTime).TotalSeconds < 10) return;
                _mLastOfflineReportTime = DateTime.Now;
                LogHelper.Info(DateTime.Now + string.Format(" 相机[{0}]SDK调用异常，疑似掉线：{1}", CamIndex, ex.Message));
                RaiseDeviceOffline(CamIndex);
            }
            catch { }
        }

        /// <summary>SDK 构造即 GxIAPINET 初始化（使用前必须 Init）。</summary>
        public KxDaHengCamera()
        {
            IGXFactory.GetInstance().Init();
        }

        public override void CamNetInit()
        {
            _listcamerainfo = new List<IGXDeviceInfo>();
            IGXFactory.GetInstance().UpdateAllDeviceList(200, _listcamerainfo);
            _CamNumCount = _listcamerainfo.Count;
            if (_listcamerainfo.Count <= 0)
            {
                NoteNoDevice("网络中未发现相机!");
                return;
            }
        }

        /// <summary>复用同品牌首台的发现列表（浅拷贝引用，只读快照）。</summary>
        public override void ShareDiscoveryFrom(KxCameraDeviceBase src)
        {
            KxDaHengCamera mSrc = src as KxDaHengCamera;
            if (mSrc == null || mSrc._listcamerainfo == null)
                throw new InvalidOperationException("大恒发现列表复用失败（来源非同品牌或为空）");
            _listcamerainfo = mSrc._listcamerainfo;
            _CamNumCount = mSrc._CamNumCount;
        }

        public override bool Open()
        {
            if (CheckSN(CamSN))
            {
                try
                {
                    if (_Camera != null)
                    {
                        _Camera.Close();
                        _Camera = null;
                    }
                    _Camera = IGXFactory.GetInstance().OpenDeviceBySN(CamSN, GX_ACCESS_MODE.GX_ACCESS_EXCLUSIVE);
                    _CamFeatureControl = _Camera.GetRemoteFeatureControl();
                    if (null != _Camera) { _CamStream = _Camera.OpenStream(0); }

                    GX_DEVICE_CLASS_LIST objDeviceClass = _Camera.GetDeviceInfo().GetDeviceClass();
                    if (GX_DEVICE_CLASS_LIST.GX_DEVICE_CLASS_GEV == objDeviceClass)
                    {
                        if (true == _CamFeatureControl.IsImplemented("GevSCPSPacketSize"))
                        {
                            uint nPacketSize = _CamStream.GetOptimalPacketSize();
                            _CamFeatureControl.GetIntFeature("GevSCPSPacketSize").SetValue(nPacketSize);
                        }
                    }
                    sDaHengOpenCount++;
                }
                catch (Exception)
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

        /// <summary>检查该 SN 是否在当前网络中（不在走 NoteNoDevice 提示）。</summary>
        public bool CheckSN(string CamSN)
        {
            foreach (IGXDeviceInfo item in _listcamerainfo)
            {
                if (item.GetSN() == CamSN)
                {
                    return true;
                }
            }
            NoteNoDevice("打开失败-网络中未发现此相机：" + CamSN);
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

        /// <summary>手动触发一次（未打开直接跳过；SDK 异常上报疑似掉线，不向调用方抛）。</summary>
        public override void GrabOne()
        {
            if (_CamFeatureControl == null) return;
            try
            {
                _CamFeatureControl.GetCommandFeature("TriggerSoftware").Execute();
            }
            catch (Exception exGrab)
            {
                ReportOfflineIfDead(exGrab);
            }
        }

        public override void StartGrab()
        {
            if (_CamStream == null) return;
            _CamStream.StartGrab();
        }

        public override void StopGrab()
        {
            if (_CamStream == null) return;
            _CamStream.StopGrab();
        }

        public override void UnregisterImageCallback()
        {
            if (_CamStream == null) return;
            _CamStream.UnregisterCaptureCallback();
        }

        public override void AcqImageCallbackFunction()
        {
            _CamStream.RegisterCaptureCallback(CamIndex, DaHengCaptureCallback);
        }

        /// <summary>帧间隔<10ms 的帧防抖丢弃（累计 100 帧记一条，可感知不刷屏）。</summary>
        private long _mDroppedFrameCount = 0;

        public void DaHengCaptureCallback(object camIndexObj, IFrameData objIFrameData)
        {
            int mThisCamID = (int)camIndexObj;

            if (DateTime.Now.Subtract(_mFilteringTime).TotalMilliseconds < 10)
            {
                if (System.Threading.Interlocked.Increment(ref _mDroppedFrameCount) % 100 == 1)
                {
                    LogHelper.Info(DateTime.Now + string.Format(" 相机[{0}]帧间隔<10ms被过滤，已累计{1}帧", mThisCamID, _mDroppedFrameCount));
                }
                return;
            }
            _mFilteringTime = DateTime.Now;

            KxCamFrame frame = ExtractMono8Frame(objIFrameData);
            RaiseFrame(mThisCamID, frame);
        }

        /// <summary>设置相机的心跳超时时间（未打开直接跳过）。</summary>
        public override void CamSetHeartbeatTime(long beatTime)
        {
            if (_CamFeatureControl == null) return;
            _CamFeatureControl.GetIntFeature("GevHeartbeatTimeout").SetValue(beatTime);
        }

        public override void CamSetTriggerSource(string mTriggerSource)
        {
            if (_CamFeatureControl == null) return;
            try
            {
                _CamFeatureControl.GetEnumFeature("TriggerMode").SetValue("On");
                _CamFeatureControl.GetEnumFeature("TriggerSource").SetValue(mTriggerSource);
                _CamSoftTrgEnable = (mTriggerSource == "Software");
            }
            catch (Exception exTrg)
            {
                ReportOfflineIfDead(exTrg);
            }
        }

        public override void CamSetExposureTime(double mExposureTime, double mGainValue)
        {
            if (_CamFeatureControl == null) return;
            try
            {
                _CamFeatureControl.GetFloatFeature("ExposureTime").SetValue(mExposureTime * 1000);
                _CamFeatureControl.GetFloatFeature("Gain").SetValue(mGainValue);
            }
            catch
            {
            }
        }

        public override void ClearImageBuffer()
        {
            if (_CamStream != null) _CamStream.FlushQueue();
        }

        /// <summary>关闭相机（最后一台才 Uninit 进程级 SDK 资源）。</summary>
        public override void CloseCam()
        {
            ClearImageBuffer();
            try
            {
                if (_CamStream != null) _CamStream.Close();
                if (_Camera != null) _Camera.Close();
                _CamStream = null;
                _Camera = null;
                _CamFeatureControl = null;
            }
            catch
            {
            }
            if (sDaHengOpenCount > 0) sDaHengOpenCount--;
            if (sDaHengOpenCount == 0)
            {
                IGXFactory.GetInstance().Uninit();
            }
            GC.Collect();
        }

        /// <summary>
        /// FrameData 抽成 Mono8 中性帧（SDK 缓冲回调后被复用，必须深拷进托管数组——
        /// ConvertToRaw8→byte[]；下游图像容器构造由宿主桥做）。
        /// </summary>
        public static KxCamFrame ExtractMono8Frame(IFrameData objIFrameData)
        {
            int width = (int)objIFrameData.GetWidth();
            int height = (int)objIFrameData.GetHeight();

            IntPtr pRaw8Buffer = objIFrameData.ConvertToRaw8(GX_VALID_BIT_LIST.GX_BIT_0_7);
            byte[] pixelData = new byte[width * height];
            Marshal.Copy(pRaw8Buffer, pixelData, 0, pixelData.Length);

            return KxCamFrame.Mono8(width, height, pixelData);
        }

        /// <summary>大恒图像转标准 8 位 BMP（逐行拷贝，宽非 4 倍数时 Stride 对齐安全）。</summary>
        public Bitmap DaHengImageToBitmap(IFrameData objIFrameData)
        {
            int width = (int)objIFrameData.GetWidth();
            int height = (int)objIFrameData.GetHeight();

            Bitmap TmpBitmap = new Bitmap(width, height, PixelFormat.Format8bppIndexed);

            ColorPalette colorPalette = TmpBitmap.Palette;
            for (int i = 0; i < 256; i++)
            {
                colorPalette.Entries[i] = Color.FromArgb(i, i, i);
            }
            TmpBitmap.Palette = colorPalette;

            IntPtr pRaw8Buffer = IntPtr.Zero;
            pRaw8Buffer = objIFrameData.ConvertToRaw8(GX_VALID_BIT_LIST.GX_BIT_0_7);

            byte[] PixelDataBuffer = new byte[width * height];
            Marshal.Copy(pRaw8Buffer, PixelDataBuffer, 0, PixelDataBuffer.Length);

            BitmapData bmpData = TmpBitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadWrite, TmpBitmap.PixelFormat);
            IntPtr ptrBmp = bmpData.Scan0;

            int stride = bmpData.Stride;
            for (int row = 0; row < height; row++)
            {
                Marshal.Copy(PixelDataBuffer, row * width, new IntPtr(ptrBmp.ToInt64() + row * stride), width);
            }

            TmpBitmap.UnlockBits(bmpData);

            return TmpBitmap;
        }

        private void CreateBitmap(out Bitmap bitmap, int nWidth, int nHeight)
        {
            bitmap = new Bitmap(nWidth, nHeight, PixelFormat.Format8bppIndexed);

            ColorPalette colorPalette = bitmap.Palette;
            for (int i = 0; i < 256; i++)
            {
                colorPalette.Entries[i] = Color.FromArgb(i, i, i);
            }
            bitmap.Palette = colorPalette;
        }
    }
}
