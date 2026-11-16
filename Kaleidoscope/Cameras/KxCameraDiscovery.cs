using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Kaleidoscope.Utils;

namespace Kaleidoscope.Cameras
{
    /// <summary>
    /// 相机发现器（静态、无状态、零弹窗）：三品牌"只发现不打开"统一入口。
    ///
    /// 【干什么】枚举本机网络/USB 上的大恒、海康 2D、海康 3D 相机，产出 KxCamFoundInfo
    /// 列表（品牌+SN+型号+传输层+网络信息），供配置页 SN 下拉、改 IP 页回显使用。
    /// 另带按 SN 反查品牌/整条记录的两个查询助手。
    ///
    /// 【为什么这么设计】（一比一对齐 HJVision CamSDK.CamDiscovery）
    ///   - 只发现不打开：与采集互斥安全（只广播收设备信息，不 Open）。
    ///   - 任一品牌异常只记日志返回空（每品牌独立 try-catch，一个挂了不影响另外两个）：
    ///     三家 SDK 各自依赖本机驱动，缺哪家的驱动就枚不到哪家的，绝不让单品牌故障
    ///     冲掉其它品牌甚至抛出炸调用方。
    ///   - 故意不调 IGXFactory.Init：大恒工厂 Init/Uninit 是进程级配对（打开计数
    ///     由 KxDaHengCamera 维护），发现器多调一次 Init 会打破配对。
    ///   - SN 一律 TrimEnd('\0')：海康/3D 的 SN 是定长字符串，原串带尾随 '\0'，
    ///     不 Trim 则与配置 SN 的 ==/Ordinal 比对恒不等（血泪）。
    ///
    /// 【怎么接】大恒约 200ms、海康广播 1–2s——调用方必须放后台线程，禁 UI 线程直调。
    /// 缺 SDK 驱动的机器上对应品牌枚举抛 FileNotFound 类异常，被各品牌 try-catch
    /// 接住记日志（本类不引用驱动专属执行体之外的路径，枚举本身必然触碰 SDK 类型，
    /// 调用方若担心缺驱动崩溃，放后台线程即可）。
    /// </summary>
    public static class KxCameraDiscovery
    {
        /// <summary>枚举本机三品牌全部相机（每品牌独立 try-catch，一个挂了不影响另外两个）。</summary>
        public static List<KxCamFoundInfo> EnumAllCameras()
        {
            List<KxCamFoundInfo> all = new List<KxCamFoundInfo>();
            try { all.AddRange(EnumDaHeng()); }
            catch (Exception ex) { try { LogHelper.Info(DateTime.Now + " 相机发现(大恒)异常：" + ex.Message); } catch { } }
            try { all.AddRange(EnumHik()); }
            catch (Exception ex) { try { LogHelper.Info(DateTime.Now + " 相机发现(海康)异常：" + ex.Message); } catch { } }
            try { all.AddRange(EnumHik3D()); }
            catch (Exception ex) { try { LogHelper.Info(DateTime.Now + " 相机发现(3D)异常：" + ex.Message); } catch { } }
            return all;
        }

        /// <summary>大恒枚举（GxIAPINET UpdateAllDeviceList 约 200ms；无 SN 的设备条目必须过滤，否则"配了也打不开"）。</summary>
        public static List<KxCamFoundInfo> EnumDaHeng()
        {
            List<KxCamFoundInfo> list = new List<KxCamFoundInfo>();
            List<GxIAPINET.IGXDeviceInfo> devs = new List<GxIAPINET.IGXDeviceInfo>();
            GxIAPINET.IGXFactory.GetInstance().UpdateAllDeviceList(200, devs);
            foreach (GxIAPINET.IGXDeviceInfo d in devs)
            {
                if (d == null) continue;
                string sn = "";
                string model = "";
                try { sn = (d.GetSN() ?? "").Trim(); } catch { }
                try { model = (d.GetModelName() ?? "").Trim(); } catch { }
                if (sn.Length == 0) continue;
                KxCamFoundInfo f = new KxCamFoundInfo();
                f.Brand = KxCamBrand.DaHeng;
                f.SN = sn;
                f.Model = model;
                f.Transport = "GxIAPINET";
                try { f.Ip = (d.GetIP() ?? "").Trim(); } catch { }
                try { f.Mask = (d.GetSubnetMask() ?? "").Trim(); } catch { }
                try { f.Gateway = (d.GetGateway() ?? "").Trim(); } catch { }
                try { f.Mac = (d.GetMAC() ?? "").Trim(); } catch { }
                list.Add(f);
            }
            return list;
        }

        /// <summary>海康 2D 枚举（GigE+USB3 双传输层；GigE 带网络信息，USB 无）。</summary>
        public static List<KxCamFoundInfo> EnumHik()
        {
            List<KxCamFoundInfo> list = new List<KxCamFoundInfo>();
            MvCamCtrl.NET.MyCamera.MV_CC_DEVICE_INFO_LIST devList = new MvCamCtrl.NET.MyCamera.MV_CC_DEVICE_INFO_LIST();
            int nRet = MvCamCtrl.NET.MyCamera.MV_CC_EnumDevices_NET(
                MvCamCtrl.NET.MyCamera.MV_GIGE_DEVICE | MvCamCtrl.NET.MyCamera.MV_USB_DEVICE, ref devList);
            if (MvCamCtrl.NET.MyCamera.MV_OK != nRet) return list;
            for (int i = 0; i < (int)devList.nDeviceNum; i++)
            {
                MvCamCtrl.NET.MyCamera.MV_CC_DEVICE_INFO device = (MvCamCtrl.NET.MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(
                    devList.pDeviceInfo[i], typeof(MvCamCtrl.NET.MyCamera.MV_CC_DEVICE_INFO));
                if (device.nTLayerType == MvCamCtrl.NET.MyCamera.MV_GIGE_DEVICE)
                {
                    IntPtr buffer = Marshal.UnsafeAddrOfPinnedArrayElement(device.SpecialInfo.stGigEInfo, 0);
                    MvCamCtrl.NET.MyCamera.MV_GIGE_DEVICE_INFO g = (MvCamCtrl.NET.MyCamera.MV_GIGE_DEVICE_INFO)Marshal.PtrToStructure(
                        buffer, typeof(MvCamCtrl.NET.MyCamera.MV_GIGE_DEVICE_INFO));
                    string sn = (g.chSerialNumber ?? "").TrimEnd('\0').Trim();
                    if (sn.Length == 0) continue;
                    KxCamFoundInfo f = new KxCamFoundInfo();
                    f.Brand = KxCamBrand.Hik2D;
                    f.SN = sn;
                    try { f.Model = (g.chModelName ?? "").TrimEnd('\0').Trim(); } catch { }
                    f.Transport = "GigE";
                    try { f.Ip = KxCamIpUtil.GigEUintToString(g.nCurrentIp); } catch { }
                    try { f.Mask = KxCamIpUtil.GigEUintToString(g.nCurrentSubNetMask); } catch { }
                    try { f.Gateway = KxCamIpUtil.GigEUintToString(g.nDefultGateWay); } catch { }
                    list.Add(f);
                }
                else if (device.nTLayerType == MvCamCtrl.NET.MyCamera.MV_USB_DEVICE)
                {
                    IntPtr buffer = Marshal.UnsafeAddrOfPinnedArrayElement(device.SpecialInfo.stUsb3VInfo, 0);
                    MvCamCtrl.NET.MyCamera.MV_USB3_DEVICE_INFO u = (MvCamCtrl.NET.MyCamera.MV_USB3_DEVICE_INFO)Marshal.PtrToStructure(
                        buffer, typeof(MvCamCtrl.NET.MyCamera.MV_USB3_DEVICE_INFO));
                    string sn = (u.chSerialNumber ?? "").TrimEnd('\0').Trim();
                    if (sn.Length == 0) continue;
                    KxCamFoundInfo f = new KxCamFoundInfo();
                    f.Brand = KxCamBrand.Hik2D;
                    f.SN = sn;
                    f.Transport = "USB3";
                    list.Add(f);
                }
            }
            return list;
        }

        /// <summary>海康 3D 枚举（MV3D_LP_GetDeviceNumber/GetDeviceList 两段式；SN 尾随 '\0' 同款处理）。</summary>
        public static List<KxCamFoundInfo> EnumHik3D()
        {
            List<KxCamFoundInfo> list = new List<KxCamFoundInfo>();
            UInt32 devNum = 0;
            if ((int)Mv3dLpSDK.MV3D_LP_GetDeviceNumber(ref devNum) != (int)Mv3dLpSDK.MV3D_LP_OK) return list;
            if (devNum == 0) return list;
            MV3D_LP_DEVICE_INFO_VECTOR vec = new MV3D_LP_DEVICE_INFO_VECTOR((int)devNum);
            for (UInt32 i = 0; i < devNum; i++) vec.Add(new MV3D_LP_DEVICE_INFO());
            UInt32 gotNum = devNum;
            if ((int)Mv3dLpSDK.MV3D_LP_GetDeviceList(vec[0], devNum, ref gotNum) != (int)Mv3dLpSDK.MV3D_LP_OK) return list;
            for (int i = 0; i < (int)gotNum; i++)
            {
                string sn = (vec[i].chSerialNumber ?? "").TrimEnd('\0').Trim();
                if (sn.Length == 0) continue;
                KxCamFoundInfo f = new KxCamFoundInfo();
                f.Brand = KxCamBrand.Hik3D;
                f.SN = sn;
                try { f.Model = (vec[i].chModelName ?? "").TrimEnd('\0').Trim(); } catch { }
                try { f.Transport = (vec[i].chCurrentIp ?? "").TrimEnd('\0').Trim(); } catch { }
                list.Add(f);
            }
            return list;
        }

        /// <summary>
        /// 按 SN 反查品牌（SN 下拉选中后自动填品牌用；离线手输查不到属正常）。
        /// Trim 后 Ordinal 精确相等（SN 即身份，不做大小写模糊）。
        /// </summary>
        public static KxCamBrand? MatchBrandBySN(string sn, List<KxCamFoundInfo> found)
        {
            if (string.IsNullOrWhiteSpace(sn) || found == null) return null;
            string key = sn.Trim();
            foreach (KxCamFoundInfo f in found)
            {
                if (f != null && string.Equals((f.SN ?? "").Trim(), key, StringComparison.Ordinal))
                    return f.Brand;
            }
            return null;
        }

        /// <summary>按 SN 反查整条发现记录（改 IP 页回显用；找不到回 null）。</summary>
        public static KxCamFoundInfo FindBySN(string sn, List<KxCamFoundInfo> found)
        {
            if (string.IsNullOrWhiteSpace(sn) || found == null) return null;
            string key = sn.Trim();
            foreach (KxCamFoundInfo f in found)
            {
                if (f != null && string.Equals((f.SN ?? "").Trim(), key, StringComparison.Ordinal))
                    return f;
            }
            return null;
        }
    }
}
