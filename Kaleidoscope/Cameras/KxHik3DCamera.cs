using System;
using System.Runtime.InteropServices;
using Kaleidoscope.Utils;
// 注意：Mv3dLpNet.dll 的 SWIG 包装类型（Mv3dLpSDK/MV3D_LP_*）在全局命名空间，
// 原版 CamBrands.cs 就是无 using 直用——勿凭直觉加 using 前缀（CS0246）。

namespace Kaleidoscope.Cameras
{
    /// <summary>
    /// 海康 3D 线激光相机适配器（Mv3dLpNet / MV3D_LP SDK）。
    ///
    /// 【干什么】3D 相机的打开/开始测量/软触发/取帧回调，帧以 KxCamFrame.Range16 上抛：
    /// Depth 帧 = ushort+32768 偏移重映射后的 short 线性段 + fX/Y/ZScale 尺度元数据；
    /// Profile 帧 = SDK 原始小端字节段（高度钳 5 后的 w/h）。下游图像容器构造
    /// （VisionPro CogImage16Grey/16Range + 线性变换 + Profile 承载对象保活）由宿主桥做。
    ///
    /// 【为什么这么设计】（一比一对齐 HJVision MVHaiKang3DCamSDK；适配器已知欠账如下，
    ///   沉淀一比一保留，补齐属独立任务需真机验证）
    ///   - 半成品现状：CamSetTriggerSource/CamSetExposureTime/CamSetHeartbeatTime/
    ///     ClearImageBuffer/CloseCam/CheckSN 全是空壳或恒 false——3D 只能软触发
    ///     （MV3D_LP_SoftTrigger），参数不下发，CloseCam 甚至不关设备。
    ///   - Open 直接按 SN 打开（MV3D_LP_OpenDeviceBySN）不依赖枚举表——ShareDiscoveryFrom
    ///     只同步向量引用备用。
    ///   - 3D 版 SDK 缓冲失效同款坑：UnsafeAddrOfPinnedArrayElement 只取首元素地址
    ///     并不固定数组，局部数组被 GC 回收/移动后下游图像容器攥着裸指针→数据撕裂/
    ///     随机崩溃——必须 Marshal.Copy 拷进托管数组（KxCamFrame 语义）。
    ///   - Depth 字节序/偏移：16bit 深度小端拆字节 + (short)→+32768 转 ushort 重映射。
    ///   - Profile 高度钳 5：nHeight<5 时按 5 处理（SDK 偶发小行数帧）。
    ///   - 回调内禁延时：旧版照搬轮询示例在回调里 Thread.Sleep(2000)，帧率被硬限
    ///     ≤0.5fps 且 SDK 队列积压——已删，触发节拍归上层。
    ///   - 多台 3D 并发互踩：m_stImageInfo 是类级 static 共享字段（遗留轮询代码的
    ///     残留字段，回调路径不使用），多台 3D 并发回调互相覆盖——当前单 3D 相机安全，
    ///     扩到多台前必须按设备隔离。
    ///   - Profile 帧 GC 悬垂：旧版局部 CogImage16Grey 失根被 GC 后其自管内存释放，
    ///     持 Scan0 的 Root 变悬垂指针——现版承载对象构造整体归宿主桥，保活环形槽
    ///     随桥走（见宿主桥注释）。
    ///
    /// 【怎么接】宿主壳组合持有；Open 直接按 SN（不查发现表）；采集回调经
    /// ImageDataCallBackHandle.Register（SWIG 包装类）挂接。帧经 RaiseFrame 上抛。
    /// </summary>
    public class KxHik3DCamera : KxCameraDeviceBase
    {
        /// <summary>
        /// SWIG 回调包装（SDK 要求继承 ImageDataCallBack 实现 run）：
        /// 在 SDK 回调线程里把帧抽成 KxCamFrame 后上抛，绝不抛异常/绝不耗时/绝不弹窗。
        /// </summary>
        public class ImageDataCallBackHandle : ImageDataCallBack
        {
            KxHik3DCamera mOwner;

            public ImageDataCallBackHandle(KxHik3DCamera owner)
            {
                mOwner = owner;
            }

            public override void run(MV3D_LP_IMAGE_DATA m_stImageInfo)
            {
                try
                {
                    if (m_stImageInfo.enImageType == Mv3dLpSDK.ImageType_Depth)
                    {
                        // Depth：小端拆字节 → +32768 重映射 → short 线性段；尺度元数据随帧带走
                        // （下游构造"1/Scale"线性变换，语义与老版 Cog3DTransformLinear 一致）。
                        byte[] ImageBuffer = new byte[(int)m_stImageInfo.nDataLen];
                        Marshal.Copy(m_stImageInfo.pData, ImageBuffer, 0, (int)m_stImageInfo.nDataLen);

                        int nPixels = (int)m_stImageInfo.nDataLen / 2;
                        ushort[] ImageBufferDepth = new ushort[nPixels];
                        for (int i = 0; i < nPixels; i++)
                        {
                            short temp = (short)((ImageBuffer[i * 2] & 0xff) | (ImageBuffer[i * 2 + 1] & 0xff) << 8);
                            ImageBufferDepth[i] = (ushort)(temp + 32768);
                        }

                        int mWidth3D = (Int32)m_stImageInfo.nWidth;
                        int mHeight3D = (Int32)m_stImageInfo.nHeight;
                        short[] copySrcDepth = new short[nPixels];
                        Buffer.BlockCopy(ImageBufferDepth, 0, copySrcDepth, 0, nPixels * 2);

                        KxCamFrame frame = KxCamFrame.RangeDepth(mWidth3D, mHeight3D, copySrcDepth,
                            m_stImageInfo.fXScale, m_stImageInfo.fYScale, m_stImageInfo.fZScale);
                        mOwner.RaiseFrame((int)mOwner.CamIndex, frame);
                    }
                    else if (m_stImageInfo.enImageType == Mv3dLpSDK.ImageType_Profile)
                    {
                        // Profile：高度钳 5 → 原始小端段拷进托管数组（按 nDataLen 截短防越界）。
                        // 承载对象（CogImage16Grey/Root/RangeRoot）与保活环形槽由宿主桥负责。
                        if (m_stImageInfo.nHeight < 5)
                        {
                            m_stImageInfo.nHeight = 5;
                        }

                        int mWidthP = (Int32)m_stImageInfo.nWidth;
                        int mHeightP = (Int32)m_stImageInfo.nHeight;
                        int nBytesP = mWidthP * mHeightP * 2;

                        byte[] profileBytes = new byte[nBytesP];
                        Marshal.Copy(m_stImageInfo.pData, profileBytes, 0, Math.Min(nBytesP, (int)m_stImageInfo.nDataLen));

                        KxCamFrame frame = KxCamFrame.RangeProfile(mWidthP, mHeightP, profileBytes);
                        mOwner.RaiseFrame((int)mOwner.CamIndex, frame);
                    }
                }
                catch
                {
                    // SDK 回调线程纪律：任何异常都不冒泡（抛出即崩进程）
                }
                return;
            }
        }

        public static ImageDataCallBackHandle[] pImageDataCallBack = new ImageDataCallBackHandle[10];
        private static readonly object Lock = new object();

        // —— 以下三个静态字段是遗留轮询取图路径的残留（WPF 版已删轮询死循环，字段随壳保留）——
        // 多台 3D 并发回调互踩（类级共享）：当前单 3D 相机安全，扩多台前必须按设备隔离。
        static MV3D_LP_IMAGE_DATA m_stImageInfo = new MV3D_LP_IMAGE_DATA();
        static UInt32 m_MaxImageSize = 1024 * 1024 * 30;
        static byte[] m_pcDataBuf = new byte[m_MaxImageSize];

        UInt32 m_nDevNum = 0;
        IntPtr m_DevHandle = IntPtr.Zero;
        MV3D_LP_DEVICE_INFO_VECTOR m_stVector;

        public override void CamNetInit()
        {
            System.GC.Collect();
            m_nDevNum = 0;

            int nRet = Mv3dLpSDK.MV3D_LP_GetDeviceNumber(ref m_nDevNum);
            m_stVector = new MV3D_LP_DEVICE_INFO_VECTOR((int)m_nDevNum);
            if (m_nDevNum == 0)
            {
                return;
            }

            for (UInt32 i = 0; i < m_nDevNum; i++)
            {
                m_stVector.Add(new MV3D_LP_DEVICE_INFO());
            }
            nRet = Mv3dLpSDK.MV3D_LP_GetDeviceList(m_stVector[0], m_nDevNum, ref m_nDevNum);
            if ((int)Mv3dLpSDK.MV3D_LP_OK != nRet)
            {
                NoteNoDevice("3D相机枚举设备失败！！");
                return;
            }

            for (int i = 0; i < m_nDevNum; i++)
            {
                string strSerialNumber = m_stVector[i].chSerialNumber;
                strSerialNumber = strSerialNumber.TrimEnd('\0');
                string strModelName = m_stVector[i].chModelName;
                strModelName = strModelName.TrimEnd('\0');
                string strCurrentIp = m_stVector[i].chCurrentIp;
                strCurrentIp = strCurrentIp.TrimEnd('\0');
            }
        }

        public override bool Open()
        {
            int nRet = Mv3dLpSDK.MV3D_LP_OpenDeviceBySN(ref m_DevHandle, CamSN);
            if ((int)Mv3dLpSDK.MV3D_LP_OK != nRet)
            {
                NoteNoDevice("3D打开设备失败 ");
                return false;
            }
            else
            {
                return true;
            }
        }

        /// <summary>3D 的 Open 直接按 SN 打开、不读枚举表，这里同步向量引用备用。</summary>
        public override void ShareDiscoveryFrom(KxCameraDeviceBase src)
        {
            KxHik3DCamera mSrc = src as KxHik3DCamera;
            if (mSrc == null)
                throw new InvalidOperationException("3D发现列表复用失败（来源非同品牌）");
            m_stVector = mSrc.m_stVector;
            m_nDevNum = mSrc.m_nDevNum;
        }

        /// <summary>检查 SN 是否存在（3D 分支恒 false：Open 直接按 SN 开，不走枚举表）。</summary>
        public bool CheckSN(string CamSN)
        {
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

        /// <summary>手动触发一次（3D 软触发；失败走 AlertSink，不抛异常）。</summary>
        public override void GrabOne()
        {
            int nRet = Mv3dLpSDK.MV3D_LP_SoftTrigger(m_DevHandle);
            if ((int)Mv3dLpSDK.MV3D_LP_OK != nRet)
            {
                try { if (AlertSink != null) AlertSink("3D开始测量失败!"); } catch { }
                return;
            }
        }

        public override void StartGrab()
        {
            int nRet = Mv3dLpSDK.MV3D_LP_StartMeasure(m_DevHandle);
        }

        public override void StopGrab()
        {
        }

        public override void UnregisterImageCallback()
        {
        }

        public override void AcqImageCallbackFunction()
        {
            pImageDataCallBack[CamIndex] = new ImageDataCallBackHandle(this);
            pImageDataCallBack[CamIndex].Register(m_DevHandle);
        }

        public override void CamSetHeartbeatTime(long beatTime)
        {
        }

        /// <summary>空壳（适配器欠账：3D 触发源配置不下发，只支持软触发，见类头）。</summary>
        public override void CamSetTriggerSource(string mTriggerSource)
        {
            try
            {
            }
            catch
            {
            }
        }

        /// <summary>空壳（适配器欠账：3D 曝光增益不下发，见类头）。</summary>
        public override void CamSetExposureTime(double mExposureTime, double mGainValue)
        {
            try
            {
            }
            catch
            {
            }
        }

        public override void ClearImageBuffer()
        {
        }

        /// <summary>空壳（适配器欠账：不关设备，见类头；CloseCam 语义与原版一致保留）。</summary>
        public override void CloseCam()
        {
            ClearImageBuffer();
            try
            {
            }
            catch
            {
            }
            GC.Collect();
        }
    }
}
