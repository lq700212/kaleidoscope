using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Kaleidoscope.Utils;
using GxIAPINET;
using MvCamCtrl.NET;

namespace Kaleidoscope.Cameras
{
    /// <summary>
    /// GigE 相机 IP 配置执行体（静态、无状态、零弹窗，失败信息经返回值/日志）。
    ///
    /// 【干什么】软件内改 GigE 相机 IP（对标 MVS/GalaxyView 工具）：①写静态持久化
    /// （掉电保留，复位生效）；②切 DHCP；③ForceIP 救援（临时，拉通后再写永久）。
    /// 大恒用 MAC 定位，海康用 SN 重枚举定位。op：0=写静态，1=切DHCP，2=ForceIP。
    ///
    /// 【为什么这么设计】（一比一对齐 HJVision CamIpAdmin，全部真机血泪沿用）
    ///   - 校验与 SDK 执行分离：校验是纯字符串逻辑（KxCamIpUtil），无 SDK 类型引用——
    ///     GxIAPINET 是 C++/CLI 混合程序集，缺驱动的机器上 JIT 一碰含 IGXFactory 引用的
    ///     方法即 FileNotFound；拆开后校验在任何机器可测，执行缺驱动时走正常错误串。
    ///   - 海康改 IP 复位生效链（V4.4.26 实机取证）：MV_GIGE_SetIpConfig 在此 SDK/固件下
    ///     STATIC/DHCP 恒返 0x80000203 不可用；静态生效链 = 写 GevPersistent 三键→回读
    ///     确认→DeviceReset 复位→轮询验证新 IP 上线，缺复位则"写了不生效"。
    ///   - 大恒写完发 RECONNECT 复位（GX_MANUFACTURER_SPECIFIC_RECONNECT），
    ///     调用方重枚举验证。
    ///   - 同网段前置检查：目标与本机网卡不同网段时拒绝写入（写了即失联，ForceIP 救援拉通）。
    ///
    /// 【怎么接】调用方先做拦截（3D/无网络信息/空 SN）与格式校验，再调 ExecOp；
    /// 改 IP 期间相机采集中会被 SDK 独占拒绝——错误串统一带"采集中请先停采"提示；
    /// 本类全部方法绝不抛异常（失败经返回值字符串），调用方放后台线程。
    /// </summary>
    public static class KxGigEIpAdmin
    {
        /// <summary>
        /// 改 IP 统一执行体（成功回 null，失败回原因）。
        /// </summary>
        public static string ExecOp(KxCamBrand brand, string sn, string mac, int op,
            string ip, string mask, string gateway)
        {
            try
            {
                if (brand == KxCamBrand.DaHeng)
                {
                    if (op == 0) return DahengSetStaticIp(mac, ip, mask, gateway);
                    else if (op == 1) return DahengSetDhcp(mac);
                    else return DahengForceIp(mac, ip, mask, gateway);
                }
                else
                {
                    if (op == 0) return HikSetStaticIp(sn, ip, mask, gateway);
                    else if (op == 1) return HikSetDhcp(sn);
                    else return HikForceIp(sn, ip, mask, gateway);
                }
            }
            catch (Exception ex) { return "执行异常：" + ex.Message; }
        }

        /// <summary>大恒 ForceIP 救援（临时，重启丢失；采集中 SDK 拒绝→提示先停采）。</summary>
        public static string DahengForceIp(string mac, string ip, string mask, string gateway)
        {
            // 参数校验与 SDK 执行分两个方法：校验是纯字符串逻辑（KxCamIpUtil），
            // 无 GxIAPINET 类型引用——GxIAPINET 是 C++/CLI 混合程序集，缺驱动的机器上
            // JIT 一碰含 IGXFactory 引用的方法即 FileNotFound；拆开后校验在任何机器可测，
            // 执行缺驱动时走正常错误串（见各执行方法的 try-catch）。
            string err = ValidateDahengIpArgs(mac, ip, mask, gateway);
            if (err != null) return err;
            try
            {
                IGXFactory.GetInstance().GigEForceIp(mac.Trim(), ip.Trim(), mask.Trim(), gwOf(gateway));
                return null;
            }
            catch (Exception ex) { return "ForceIP 失败（采集中请先停采）：" + ex.Message; }
        }

        /// <summary>大恒改 IP 三参校验（纯函数，无 SDK 引用，探针/无驱动机直测）。</summary>
        private static string ValidateDahengIpArgs(string mac, string ip, string mask, string gateway)
        {
            if (string.IsNullOrWhiteSpace(mac)) return "该相机无 MAC 信息（可能未枚举到，重按刷新）";
            if (!KxCamIpUtil.IsValidIpv4(ip)) return "IP 非法：" + ip;
            if (!KxCamIpUtil.IsValidIpv4(mask)) return "掩码非法：" + mask;
            string gw = (gateway ?? "").Trim();
            if (gw.Length == 0) gw = "0.0.0.0";
            if (!KxCamIpUtil.IsValidIpv4(gw)) return "网关非法：" + gateway;
            return null;
        }

        private static string gwOf(string gateway)
        {
            string gw = (gateway ?? "").Trim();
            return gw.Length == 0 ? "0.0.0.0" : gw;
        }

        /// <summary>
        /// 大恒写静态 IP（持久化，掉电保留，复位生效）。UserID 原样带回；
        /// 写完发 RECONNECT 复位，调用方重枚举验证。
        /// 校验（纯逻辑）与执行（碰 IGXFactory）分离，原因见 DahengForceIp。
        /// </summary>
        public static string DahengSetStaticIp(string mac, string ip, string mask, string gateway)
        {
            string err = ValidateDahengIpArgs(mac, ip, mask, gateway);
            if (err != null) return err;
            if (!KxCamIpUtil.IsSameSubnetWithHost(ip, mask))
                return "目标与本机网卡不同网段，写了即失联（先改网卡网段，或用 ForceIP 救援拉通）";
            try
            {
                return DahengSetStaticIpExec(mac.Trim(), ip.Trim(), mask.Trim(), gwOf(gateway));
            }
            catch (Exception ex) { return "写入失败（采集中请先停采）：" + ex.Message; }
        }

        /// <summary>大恒写静态执行体（唯一碰 IGXFactory 的方法，缺驱动时抛由上层转错误串）。</summary>
        private static string DahengSetStaticIpExec(string mac, string ip, string mask, string gw)
        {
            string userId = "";
            try
            {
                List<IGXDeviceInfo> devs = new List<IGXDeviceInfo>();
                IGXFactory.GetInstance().UpdateAllDeviceList(200, devs);
                if (devs != null)
                {
                    foreach (IGXDeviceInfo d in devs)
                    {
                        if (d == null) continue;
                        string m = "";
                        try { m = (d.GetMAC() ?? "").Trim(); } catch { }
                        if (string.Equals(m, mac, StringComparison.OrdinalIgnoreCase))
                        {
                            try { userId = (d.GetUserID() ?? "").Trim(); } catch { }
                            break;
                        }
                    }
                }
            }
            catch { }
            IGXFactory.GetInstance().GigEIpConfiguration(mac,
                GX_IP_CONFIGURE_MODE.IP_CONFIGURE_STATIC_IP, ip, mask, gw, userId);
            try { IGXFactory.GetInstance().GigEResetDevice(mac, GX_RESET_DEVICE_MODE.GX_MANUFACTURER_SPECIFIC_RECONNECT); }
            catch (Exception exRe) { return "IP 已写入但复位失败，断电重启相机后验证：" + exRe.Message; }
            return null;
        }

        /// <summary>大恒切 DHCP（掉电保留模式位，重启后自动获取；校验与执行分离，原因同上）。</summary>
        public static string DahengSetDhcp(string mac)
        {
            if (string.IsNullOrWhiteSpace(mac)) return "该相机无 MAC 信息（可能未枚举到，重按刷新）";
            try
            {
                return DahengSetDhcpExec(mac.Trim());
            }
            catch (Exception ex) { return "切换失败（采集中请先停采）：" + ex.Message; }
        }

        private static string DahengSetDhcpExec(string mac)
        {
            string userId = "";
            try
            {
                List<IGXDeviceInfo> devs = new List<IGXDeviceInfo>();
                IGXFactory.GetInstance().UpdateAllDeviceList(200, devs);
                if (devs != null)
                {
                    foreach (IGXDeviceInfo d in devs)
                    {
                        if (d == null) continue;
                        string m = "";
                        try { m = (d.GetMAC() ?? "").Trim(); } catch { }
                        if (string.Equals(m, mac, StringComparison.OrdinalIgnoreCase))
                        {
                            try { userId = (d.GetUserID() ?? "").Trim(); } catch { }
                            break;
                        }
                    }
                }
            }
            catch { }
            IGXFactory.GetInstance().GigEIpConfiguration(mac,
                GX_IP_CONFIGURE_MODE.IP_CONFIGURE_DHCP, "0.0.0.0", "0.0.0.0", "0.0.0.0", userId);
            try { IGXFactory.GetInstance().GigEResetDevice(mac, GX_RESET_DEVICE_MODE.GX_MANUFACTURER_SPECIFIC_RECONNECT); }
            catch { }
            return null;
        }

        /// <summary>
        /// 按 SN 重枚举找海康设备信息（每次现枚举现找，不缓存句柄，枚举顺序会变）。
        /// </summary>
        private static bool FindHikDeviceBySN(string sn, out MyCamera.MV_CC_DEVICE_INFO info)
        {
            info = new MyCamera.MV_CC_DEVICE_INFO();
            try
            {
                MyCamera.MV_CC_DEVICE_INFO_LIST devList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
                int nRet = MyCamera.MV_CC_EnumDevices_NET(
                    MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE, ref devList);
                if (MyCamera.MV_OK != nRet) return false;
                for (int i = 0; i < (int)devList.nDeviceNum; i++)
                {
                    MyCamera.MV_CC_DEVICE_INFO device = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(
                        devList.pDeviceInfo[i], typeof(MyCamera.MV_CC_DEVICE_INFO));
                    if (device.nTLayerType != MyCamera.MV_GIGE_DEVICE) continue;
                    IntPtr buffer = Marshal.UnsafeAddrOfPinnedArrayElement(device.SpecialInfo.stGigEInfo, 0);
                    MyCamera.MV_GIGE_DEVICE_INFO g = (MyCamera.MV_GIGE_DEVICE_INFO)Marshal.PtrToStructure(
                        buffer, typeof(MyCamera.MV_GIGE_DEVICE_INFO));
                    string cur = (g.chSerialNumber ?? "").TrimEnd('\0').Trim();
                    if (string.Equals(cur, sn.Trim(), StringComparison.Ordinal))
                    {
                        info = device;
                        return true;
                    }
                }
            }
            catch { return false; }
            return false;
        }

        /// <summary>海康 ForceIP 救援（临时，重启丢失；只 Create 不 Open，用完即 Destroy）。</summary>
        public static string HikForceIp(string sn, string ip, string mask, string gateway)
        {
            if (string.IsNullOrWhiteSpace(sn)) return "SN 为空";
            UInt32 nip, nmask, ngw;
            if (!KxCamIpUtil.TryParseIpUint(ip, out nip)) return "IP 非法：" + ip;
            if (!KxCamIpUtil.TryParseIpUint(mask, out nmask)) return "掩码非法：" + mask;
            string gw = (gateway ?? "").Trim();
            if (gw.Length == 0) gw = "0.0.0.0";
            if (!KxCamIpUtil.TryParseIpUint(gw, out ngw)) return "网关非法：" + gateway;
            MyCamera.MV_CC_DEVICE_INFO info;
            if (!FindHikDeviceBySN(sn, out info)) return "枚举不到该相机（重按刷新；采集中被独占时也可能找不到）";
            MyCamera cam = null;
            try
            {
                cam = new MyCamera();
                int nRet = cam.MV_CC_CreateDevice_NET(ref info);
                if (MyCamera.MV_OK != nRet) return "创建设备失败（0x" + nRet.ToString("X8") + "，采集中请先停采）";
                nRet = cam.MV_GIGE_ForceIpEx_NET(nip, nmask, ngw);
                if (MyCamera.MV_OK != nRet) return "ForceIP 失败（0x" + nRet.ToString("X8") + "）";
                return null;
            }
            catch (Exception ex) { return "ForceIP 异常：" + ex.Message; }
            finally { try { if (cam != null) cam.MV_CC_DestroyDevice_NET(); } catch { } }
        }

        /// <summary>
        /// 海康写静态 IP（Open→写 GevPersistent 三键→回读确认→复位生效→轮询验证上线）。
        /// 注意：MV_GIGE_SetIpConfig 在此 SDK/固件下恒返 0x80000203 不可用，只做 best-effort；
        /// 真正生效靠持久化 + DeviceReset 复位。
        /// </summary>
        public static string HikSetStaticIp(string sn, string ip, string mask, string gateway)
        {
            if (string.IsNullOrWhiteSpace(sn)) return "SN 为空";
            UInt32 nip, nmask, ngw;
            if (!KxCamIpUtil.TryParseIpUint(ip, out nip)) return "IP 非法：" + ip;
            if (!KxCamIpUtil.TryParseIpUint(mask, out nmask)) return "掩码非法：" + mask;
            string gw = (gateway ?? "").Trim();
            if (gw.Length == 0) gw = "0.0.0.0";
            if (!KxCamIpUtil.TryParseIpUint(gw, out ngw)) return "网关非法：" + gateway;
            if (!KxCamIpUtil.IsSameSubnetWithHost(ip, mask))
                return "目标与本机网卡不同网段，写了即失联（先改网卡网段，或用 ForceIP 救援拉通）";
            MyCamera.MV_CC_DEVICE_INFO info;
            if (!FindHikDeviceBySN(sn, out info)) return "枚举不到该相机（重按刷新；采集中被独占时也可能找不到）";
            MyCamera cam = null;
            try
            {
                cam = new MyCamera();
                int nRet = cam.MV_CC_CreateDevice_NET(ref info);
                if (MyCamera.MV_OK != nRet) return "创建设备失败（0x" + nRet.ToString("X8") + "，采集中请先停采）";
                nRet = cam.MV_CC_OpenDevice_NET();
                if (MyCamera.MV_OK != nRet)
                    return "打开设备失败（0x" + nRet.ToString("X8") + "，该相机正在采集中请先停采）";
                try
                {
                    nRet = cam.MV_CC_SetIntValueEx_NET("GevPersistentIPAddress", (long)nip);
                    if (MyCamera.MV_OK != nRet) return "写持久 IP 失败（0x" + nRet.ToString("X8") + "，老固件键名可能不支持，记下码联系排查）";
                    nRet = cam.MV_CC_SetIntValueEx_NET("GevPersistentSubnetMask", (long)nmask);
                    if (MyCamera.MV_OK != nRet) return "写持久掩码失败（0x" + nRet.ToString("X8") + "）";
                    nRet = cam.MV_CC_SetIntValueEx_NET("GevPersistentDefaultGateway", (long)ngw);
                    if (MyCamera.MV_OK != nRet) return "写持久网关失败（0x" + nRet.ToString("X8") + "）";
                    try
                    {
                        MyCamera.MVCC_INTVALUE back = new MyCamera.MVCC_INTVALUE();
                        int rBack = cam.MV_CC_GetIntValue_NET("GevPersistentIPAddress", ref back);
                        if (MyCamera.MV_OK != rBack || (UInt32)back.nCurValue != nip)
                            return "持久 IP 回读不一致（读回 0x" + (MyCamera.MV_OK == rBack ? ((UInt32)back.nCurValue).ToString("X8") : rBack.ToString("X8")) + "），未复位，请重试";
                    }
                    catch (Exception exBack) { return "持久 IP 回读异常（未复位）：" + exBack.Message; }
                    try
                    {
                        int rMode = cam.MV_GIGE_SetIpConfig_NET(MyCamera.MV_IP_CFG_STATIC);
                        if (MyCamera.MV_OK != rMode)
                        {
                            try { LogHelper.Info(DateTime.Now + " 相机改IP：SetIpConfig(STATIC) 返回 0x" + rMode.ToString("X8") + "（已知恒败，已忽略，靠复位生效）：" + sn); }
                            catch { }
                        }
                    }
                    catch (Exception exMode)
                    {
                        try { LogHelper.Info(DateTime.Now + " 相机改IP：SetIpConfig(STATIC) 异常已忽略：" + sn + "，" + exMode.Message); }
                        catch { }
                    }
                    try
                    {
                        int rReset = cam.MV_CC_SetCommandValue_NET("DeviceReset");
                        if (MyCamera.MV_OK != rReset)
                            return "IP 已写入但复位失败（0x" + rReset.ToString("X8") + "），断电重启相机后验证";
                    }
                    catch (Exception exReset) { return "IP 已写入但复位异常，断电重启相机后验证：" + exReset.Message; }
                }
                finally { try { cam.MV_CC_CloseDevice_NET(); } catch { } }
            }
            catch (Exception ex) { return "写入异常：" + ex.Message; }
            finally { try { if (cam != null) cam.MV_CC_DestroyDevice_NET(); } catch { } }
            return WaitHikIpOnline(sn, ip.Trim());
        }

        /// <summary>
        /// 等海康相机以目标 IP 上线（≤30 秒）。targetIp 为空=SN 重现且带有效 IP 即算上线（DHCP 用）。
        /// </summary>
        private static string WaitHikIpOnline(string sn, string targetIp)
        {
            string want = (targetIp ?? "").Trim();
            bool anyIp = (want.Length == 0);
            string lastSeen = "";
            DateTime deadline = DateTime.Now.AddSeconds(30);
            while (DateTime.Now < deadline)
            {
                try
                {
                    List<KxCamFoundInfo> found = KxCameraDiscovery.EnumAllCameras();
                    KxCamFoundInfo f = KxCameraDiscovery.FindBySN(sn, found);
                    if (f != null)
                    {
                        lastSeen = (f.Ip ?? "").Trim();
                        if (anyIp ? KxCamIpUtil.IsValidIpv4(lastSeen)
                                  : string.Equals(lastSeen, want, StringComparison.Ordinal)) return null;
                    }
                    else lastSeen = "";
                }
                catch { }
                try { System.Threading.Thread.Sleep(1500); }
                catch { break; }
            }
            string seen = lastSeen.Length > 0 ? lastSeen : "未枚举到";
            if (anyIp) return "已切 DHCP 但相机暂未上线（最后看到 " + seen + "；无 DHCP 服务器时约 1 分钟后回落 169.254 链路地址，再用 ForceIP 救援拉回）";
            return "IP 已写入但新 IP 未上线（最后看到 " + seen + "，相机可能还在重启，断电重启后重按刷新验证）";
        }

        /// <summary>
        /// 海康切 DHCP（Open→SetIpConfig(DHCP)→复位→等重现）。
        /// 注意该 SDK/固件下模式切换恒返 0x80000203，失败如实报错；静态组网请用写静态 IP。
        /// </summary>
        public static string HikSetDhcp(string sn)
        {
            if (string.IsNullOrWhiteSpace(sn)) return "SN 为空";
            MyCamera.MV_CC_DEVICE_INFO info;
            if (!FindHikDeviceBySN(sn, out info)) return "枚举不到该相机（重按刷新；采集中被独占时也可能找不到）";
            MyCamera cam = null;
            try
            {
                cam = new MyCamera();
                int nRet = cam.MV_CC_CreateDevice_NET(ref info);
                if (MyCamera.MV_OK != nRet) return "创建设备失败（0x" + nRet.ToString("X8") + "，采集中请先停采）";
                nRet = cam.MV_CC_OpenDevice_NET();
                if (MyCamera.MV_OK != nRet)
                    return "打开设备失败（0x" + nRet.ToString("X8") + "，该相机正在采集中请先停采）";
                try
                {
                    nRet = cam.MV_GIGE_SetIpConfig_NET(MyCamera.MV_IP_CFG_DHCP);
                    if (MyCamera.MV_OK != nRet) return "切换 DHCP 失败（0x" + nRet.ToString("X8") + "；静态组网请用写入静态IP）";
                    try
                    {
                        int rReset = cam.MV_CC_SetCommandValue_NET("DeviceReset");
                        if (MyCamera.MV_OK != rReset)
                            return "已切 DHCP 但复位失败（0x" + rReset.ToString("X8") + "），断电重启相机后验证";
                    }
                    catch (Exception exReset) { return "已切 DHCP 但复位异常，断电重启相机后验证：" + exReset.Message; }
                }
                finally { try { cam.MV_CC_CloseDevice_NET(); } catch { } }
            }
            catch (Exception ex) { return "切换异常：" + ex.Message; }
            finally { try { if (cam != null) cam.MV_CC_DestroyDevice_NET(); } catch { } }
            return WaitHikIpOnline(sn, null);
        }
    }
}
