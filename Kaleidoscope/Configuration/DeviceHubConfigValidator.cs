using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Kaleidoscope.Models;

namespace Kaleidoscope.Configuration
{
    /// <summary>
    /// 设备配置（<see cref="DeviceHubConfig"/>）的校验结果。
    /// 错误（Errors）必须修：不修会导致运行时连接失败/崩溃；警告（Warnings）建议修：不修也能跑，但可能有隐患。
    /// 供配置编辑器"保存前拦截"和业务项目"启动前自检"共用。
    /// </summary>
    public class ConfigValidationResult
    {
        /// <summary>错误列表（必须修复；为空才能保存/应用配置）</summary>
        public List<string> Errors { get; private set; }

        /// <summary>警告列表（建议修复，不阻塞保存）</summary>
        public List<string> Warnings { get; private set; }

        /// <summary>是否通过校验（Errors 为空即 true）</summary>
        public bool IsValid { get { return Errors.Count == 0; } }

        /// <summary>构造：初始化空列表</summary>
        public ConfigValidationResult()
        {
            Errors = new List<string>();
            Warnings = new List<string>();
        }
    }

    /// <summary>
    /// 设备配置（<see cref="DeviceHubConfig"/>）校验器：逐设备段检查 IP/端口/寄存器地址/
    /// 必填项等，返回错误与警告列表。
    ///
    /// 【为什么要有它】
    /// 配置交给 DeviceHub.ApplyConfig 后，坏的配置（IP 写错、端口越界、地址重叠）会让运行时
    /// 各种连接异常/寄存器错写，且难排查。本类在【保存前】把明显的问题挡在门外；
    /// 业务项目启动前调一次，也能把历史配置文件的问题提前暴露给使用者。
    ///
    /// 【调用方式】
    ///   var r = DeviceHubConfigValidator.Validate(cfg);
    ///   if (!r.IsValid) { /* 把 r.Errors 逐条提示给用户，不允许保存 */ }
    ///
    /// 【校验口径】
    /// - IP 用 IPAddress.TryParse（"0.0.0.0"=监听所有网卡是合法值）；
    /// - 端口范围 1~65535；超时/轮询周期须 &gt; 0；
    /// - 寄存器地址本身是 ushort（0~65535 天然合法），重点查【逻辑越界】：偏移超区块、
    ///   PLC 地址相互重叠等；
    /// - UseMockCommunication=true 时气压表/IO/送风机是 Mock，网络类参数不生效，
    ///   只校验逻辑字段（总路数、寄存器偏移等），避免误报噪音。
    /// </summary>
    public static class DeviceHubConfigValidator
    {
        /// <summary>
        /// 校验一份完整设备配置。
        /// </summary>
        /// <param name="config">要校验的配置（null 直接返回一条错误）</param>
        /// <returns>校验结果（Errors=必须修复，Warnings=建议修复）</returns>
        public static ConfigValidationResult Validate(DeviceHubConfig config)
        {
            var r = new ConfigValidationResult();
            if (config == null)
            {
                r.Errors.Add("配置对象为 null，无法校验");
                return r;
            }

            ValidatePlc(config, r);
            ValidatePlcMaster(config, r);
            ValidateMultiBrandPlc(config, r);
            ValidateCameras(config, r);
            ValidateScanners(config, r);
            ValidateBarometer(config, r);
            ValidateIo(config, r);
            ValidateFan(config, r);
            ValidateImage(config, r);
            ValidateGlobal(config, r);
            return r;
        }

        // ═══════════════ 各设备段 ═══════════════

        /// <summary>校验 PLC 从站配置（PlcConfig：监听 IP/端口/寄存器地址/型号序号表）</summary>
        private static void ValidatePlc(DeviceHubConfig config, ConfigValidationResult r)
        {
            PlcConfig plc = config.Plc;
            if (plc == null) return; // EnsureSafe 之后不会为 null，防御性判断

            CheckIp(plc.IpAddress, "PLC 从站监听 IP", r);
            CheckPort(plc.Port, "PLC 从站监听端口", r);
            if (plc.ProductModelLen < 1)
                r.Errors.Add("PLC 从站：ProductModelLen（型号寄存器数）至少为 1");

            // PLC 从站四个寄存器地址若互相重叠，读写会串位，必须提醒。
            // 注意不能用 Dictionary 初始化器直接装（地址重复时初始化器抛"相同键"异常，
            // 校验器本身崩溃）——先装成对列表，再逐个与 owner 比对判重。
            var addrs = new[]
            {
                new { Addr = plc.ScanRequestAddress, Field = "ScanRequestAddress" },
                new { Addr = plc.ScanResultAddress, Field = "ScanResultAddress" },
                new { Addr = plc.ProductModelIndexAddress, Field = "ProductModelIndexAddress" },
                new { Addr = plc.ProductModelAddress, Field = "ProductModelAddress" },
            };
            var owner = new Dictionary<int, string>();
            foreach (var kv in addrs)
            {
                if (kv.Addr == 0) continue; // 0 在 PLC 从站语义里=未配置/预留，不算重叠
                if (owner.ContainsKey(kv.Addr))
                    r.Warnings.Add($"PLC 从站：寄存器地址 {kv.Addr} 被字段 [{owner[kv.Addr]}] 与 [{kv.Field}] 重复占用（读写会互相覆盖，请核对梯形图）");
                else
                    owner[kv.Addr] = kv.Field;
            }

            if (plc.ModelIndexes != null)
            {
                foreach (var item in plc.ModelIndexes)
                {
                    if (item == null) continue;
                    if (string.IsNullOrWhiteSpace(item.ModelName))
                        r.Errors.Add("PLC 从站：型号序号表里存在空型号名");
                    if (item.ModelIndex < 0)
                        r.Errors.Add($"PLC 从站：型号 [{item.ModelName}] 的序号不能为负（{item.ModelIndex}）");
                }
            }
        }

        /// <summary>校验 PLC 主站配置（PlcMasterConfig：目标 IP/端口/轮询项）</summary>
        private static void ValidatePlcMaster(DeviceHubConfig config, ConfigValidationResult r)
        {
            PlcMasterConfig m = config.PlcMaster;
            if (m == null) return;

            CheckIp(m.IpAddress, "PLC 主站目标 IP", r);
            CheckPort(m.Port, "PLC 主站端口", r);
            if (m.TimeoutMs <= 0) r.Errors.Add("PLC 主站：超时 TimeoutMs 必须大于 0");
            if (m.ReconnectIntervalMs < 0) r.Errors.Add("PLC 主站：重连间隔 ReconnectIntervalMs 不能为负");
            if (m.PollIntervalMs < 0) r.Errors.Add("PLC 主站：轮询周期 PollIntervalMs 不能为负");

            if (m.PollItems != null)
            {
                for (int i = 0; i < m.PollItems.Count; i++)
                {
                    PlcPollItem item = m.PollItems[i];
                    if (item == null) { r.Errors.Add($"PLC 主站：轮询项 #{i + 1} 为 null"); continue; }
                    if (string.IsNullOrWhiteSpace(item.Name))
                        r.Errors.Add($"PLC 主站：轮询项 #{i + 1} 缺少名称（Name）");
                    if (item.Count < 1)
                        r.Errors.Add($"PLC 主站：轮询项 [{item.Name}] 的读取数量 Count 至少为 1");
                    if (!System.Enum.IsDefined(typeof(PlcPollFunction), item.Function))
                        r.Errors.Add($"PLC 主站：轮询项 [{item.Name}] 的功能码 {item.Function} 非法");
                }
            }
        }

        /// <summary>
        /// 校验多品牌 PLC 配置（MultiBrandPlcConfig：品牌/IP/端口/轮询项/西门子型号）。
        /// 仅 PlcRole=MultiBrand 时做完整校验（其它角色下整段配置不生效，不制造误报）。
        /// </summary>
        private static void ValidateMultiBrandPlc(DeviceHubConfig config, ConfigValidationResult r)
        {
            MultiBrandPlcConfig m = config.MultiBrandPlc;
            if (m == null) return;

            // 非多品牌角色：整段不生效，跳过（与"主站角色不校验从站地址重叠"同口径）
            if (config.PlcRole != PlcRole.MultiBrand) return;

            if (!System.Enum.IsDefined(typeof(MultiBrandPlcBrand), m.Brand))
                r.Errors.Add($"PLC（多品牌）：品牌 {m.Brand} 非法");

            CheckIp(m.IpAddress, "PLC（多品牌）", r);
            // 端口 0=按品牌默认端口（合法约定），其余须在 1~65535
            if (m.Port != 0)
                CheckPort(m.Port, "PLC（多品牌）端口", r);

            if (m.TimeoutMs <= 0) r.Errors.Add("PLC（多品牌）：超时 TimeoutMs 必须大于 0");
            if (m.ReconnectIntervalMs < 0) r.Errors.Add("PLC（多品牌）：重连间隔 ReconnectIntervalMs 不能为负");
            if (m.HeartbeatIntervalMs < 0) r.Errors.Add("PLC（多品牌）：心跳周期 HeartbeatIntervalMs 不能为负");
            if (m.PollIntervalMs < 0) r.Errors.Add("PLC（多品牌）：轮询周期 PollIntervalMs 不能为负");
            if (m.HeartbeatIntervalMs > 0 && m.HeartbeatIntervalMs < 1000)
                r.Warnings.Add("PLC（多品牌）：心跳周期小于 1000ms 运行时会按 1s 下限处理，建议直接配 ≥1000");
            if (!string.IsNullOrWhiteSpace(m.HeartbeatAddress)
                && !System.Enum.IsDefined(typeof(PlcDataType), m.HeartbeatDataType))
                r.Errors.Add($"PLC（多品牌）：心跳数据类型 {m.HeartbeatDataType} 非法");

            // 西门子型号字符串提示（运行时非法值回退 S1200，不阻断）
            if (m.Brand == MultiBrandPlcBrand.SiemensS7)
            {
                var valid = new[] { "S1200", "S1500", "S300", "S400", "S200Smart", "S200" };
                bool ok = false;
                foreach (var v in valid)
                    if (string.Equals(v, m.SiemensModel?.Trim(), System.StringComparison.OrdinalIgnoreCase)) { ok = true; break; }
                if (!ok)
                    r.Warnings.Add($"PLC（多品牌）：西门子型号 [{m.SiemensModel}] 无法识别（可选 S1200/S1500/S300/S400/S200Smart/S200），运行时将回退 S1200");
            }

            if (m.PollItems != null)
            {
                for (int i = 0; i < m.PollItems.Count; i++)
                {
                    MultiBrandPlcPollItem item = m.PollItems[i];
                    if (item == null) { r.Errors.Add($"PLC（多品牌）：轮询项 #{i + 1} 为 null"); continue; }
                    if (string.IsNullOrWhiteSpace(item.Name))
                        r.Errors.Add($"PLC（多品牌）：轮询项 #{i + 1} 缺少名称（Name）");
                    if (string.IsNullOrWhiteSpace(item.Address))
                        r.Errors.Add($"PLC（多品牌）：轮询项 [{item.Name}] 缺少起始地址（Address，填品牌地址语法如 D100）");
                    if (item.Count < 1)
                        r.Errors.Add($"PLC（多品牌）：轮询项 [{item.Name}] 的读取数量 Count 至少为 1");
                    if (item.Count > 1000)
                        r.Warnings.Add($"PLC（多品牌）：轮询项 [{item.Name}] 的读取数量 Count={item.Count} 过大" +
                                       "（多数协议单帧上限远小于此，过大会导致每次轮询都失败并反复断线重连，建议 ≤1000）");
                    if (!System.Enum.IsDefined(typeof(PlcDataType), item.DataType))
                        r.Errors.Add($"PLC（多品牌）：轮询项 [{item.Name}] 的数据类型 {item.DataType} 非法");
                }
            }

            // 轮询项名称重复会让结果缓存/事件互相覆盖，必须提醒
            if (m.PollItems != null)
            {
                var seen = new HashSet<string>(System.StringComparer.Ordinal);
                foreach (var item in m.PollItems)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.Name)) continue;
                    if (!seen.Add(item.Name))
                        r.Warnings.Add($"PLC（多品牌）：轮询项名称 [{item.Name}] 重复（缓存与事件将互相覆盖，请改名区分）");
                }
            }
        }

        /// <summary>校验相机配置列表（每台：IP/端口/超时/点位映射/结果通道）</summary>
        private static void ValidateCameras(DeviceHubConfig config, ConfigValidationResult r)
        {
            if (config.Cameras == null || config.Cameras.Count == 0)
            {
                r.Warnings.Add("相机：未配置任何相机，运行时将回退用库默认相机（DefaultCameras）");
                return;
            }

            var nameSet = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < config.Cameras.Count; i++)
            {
                CameraConfig cam = config.Cameras[i];
                if (cam == null) { r.Errors.Add("相机列表里存在 null 项"); continue; }
                string label = "相机 #" + (i + 1) + (string.IsNullOrEmpty(cam.Name) ? "" : $"（{cam.Name}）");

                if (string.IsNullOrWhiteSpace(cam.Name))
                    r.Warnings.Add($"{label}：名称（Name）为空，界面与日志不便区分");
                else if (!nameSet.Add(cam.Name))
                    r.Warnings.Add($"相机：名称 [{cam.Name}] 重复（多台相机同名列会混淆，请改名）");

                CheckIp(cam.IpAddress, label + " 的 IP", r);
                CheckPort(cam.CommandPort, label + " 的指令端口", r);
                if (cam.ResponseTimeoutMs <= 0) r.Errors.Add($"{label}：响应超时 ResponseTimeoutMs 必须大于 0");
                if (cam.TimeoutMs <= 0) r.Errors.Add($"{label}：收发超时 TimeoutMs 必须大于 0");
                if (cam.ImageWaitMs <= 0) r.Errors.Add($"{label}：等图超时 ImageWaitMs 必须大于 0");
                if (cam.PlcRequestAddress < 0) r.Errors.Add($"{label}：拍照请求地址不能为负");
                if (cam.PlcResultAddress < 0) r.Errors.Add($"{label}：拍照结果地址不能为负");

                // 取图来源：非 Ftp/Tcp 一律按 Ftp 兜底，提醒配置者
                if (!string.Equals(cam.ImageSource, "Ftp", System.StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(cam.ImageSource, "Tcp", System.StringComparison.OrdinalIgnoreCase))
                    r.Warnings.Add($"{label}：取图来源 [{cam.ImageSource}] 未知，将按 Ftp 处理");

                if (!string.IsNullOrEmpty(cam.OkChar) && cam.OkChar.Length != 1)
                    r.Warnings.Add($"{label}：判定合格字符 OkChar 应为 1 个字符，当前 [{cam.OkChar}]");

                // 点位→程序号映射检查（程序号 0~127，点位 ≥ 1）
                CheckStationTable(label + " 默认点位表", cam.StationPrograms, r);
                if (cam.ModelStationPrograms != null)
                {
                    foreach (var m in cam.ModelStationPrograms)
                    {
                        if (m == null) { r.Errors.Add($"{label}：型号程序表里存在 null 项"); continue; }
                        if (string.IsNullOrWhiteSpace(m.ModelName))
                            r.Warnings.Add($"{label}：存在空型号名的程序分表（匹配不到任何型号，等于无效表）");
                        CheckStationTable(label + $" 型号[{m.ModelName}]点位表", m.Programs, r);
                    }
                }
            }
        }

        /// <summary>校验一张"点位→程序号"表：点位 ≥ 1、程序号 0~127</summary>
        private static void CheckStationTable(string tableLabel, List<StationProgramItem> items, ConfigValidationResult r)
        {
            if (items == null) return;
            for (int i = 0; i < items.Count; i++)
            {
                StationProgramItem item = items[i];
                if (item == null) { r.Errors.Add($"{tableLabel}：第 {i + 1} 项为 null"); continue; }
                if (item.StationNo < 1)
                    r.Errors.Add($"{tableLabel}：点位 {item.StationNo} 非法（点位号从 1 起）");
                if (item.ProgramNo < 0 || item.ProgramNo > 127)
                    r.Errors.Add($"{tableLabel}：点位 {item.StationNo} 的程序号 {item.ProgramNo} 越界（合法 0~127）");
            }
        }

        /// <summary>校验扫码枪配置列表（每台：通讯方式/IP 或串口参数）</summary>
        private static void ValidateScanners(DeviceHubConfig config, ConfigValidationResult r)
        {
            if (config.Scanners == null || config.Scanners.Count == 0) return;

            for (int i = 0; i < config.Scanners.Count; i++)
            {
                ScanConfig s = config.Scanners[i];
                string label = "扫码枪 #" + (i + 1);
                if (s == null) { r.Errors.Add("扫码枪列表里存在 null 项"); continue; }

                bool tcp = string.Equals(s.Mode, "Tcp", System.StringComparison.OrdinalIgnoreCase);
                bool serial = string.Equals(s.Mode, "Serial", System.StringComparison.OrdinalIgnoreCase);
                bool eip = string.Equals(s.Mode, "Eip", System.StringComparison.OrdinalIgnoreCase);
                if (!tcp && !serial && !eip)
                    r.Errors.Add($"{label}：通讯方式 Mode [{s.Mode}] 非法（仅支持 Tcp / Serial / Eip）");

                if (tcp || eip)
                {
                    CheckIp(s.IpAddress, label + " 的 IP", r);
                    CheckPort(s.Port, label + " 的端口", r);
                    if (eip)
                    {
                        // EIP 专属参数：读取字节数/轮询周期有下限（服务内部也会兜底，这里提前暴露）
                        if (s.EipReadLengthBytes < 2 || s.EipReadLengthBytes > 500)
                            r.Warnings.Add($"{label}：EIP 读取字节数 [{s.EipReadLengthBytes}] 异常（正常为条码最大长度，如 64）");
                        if (s.EipPollingMs < 50)
                            r.Warnings.Add($"{label}：EIP 轮询周期 [{s.EipPollingMs}]ms 过小（运行时按 50ms 下限处理），建议 ≥200");
                    }
                }
                else if (serial)
                {
                    if (s.BaudRate <= 0) r.Errors.Add($"{label}：波特率 BaudRate 必须大于 0");
                    if (s.DataBits < 5 || s.DataBits > 8) r.Errors.Add($"{label}：数据位 DataBits 应为 5~8");
                    if (s.StopBits != "1" && s.StopBits != "15" && s.StopBits != "2")
                        r.Warnings.Add($"{label}：停止位 [{s.StopBits}] 应为 1/15/2（运行时按无效值处理可能通讯异常）");
                    if (!IsValidParity(s.Parity))
                        r.Warnings.Add($"{label}：校验位 [{s.Parity}] 不是标准枚举名（None/Odd/Even/Mark/Space）");
                    // 串口自动识别：端口留空时靠 DeviceKeyword 找设备，没关键词则识别不到
                    if (string.IsNullOrWhiteSpace(s.PortName) && string.IsNullOrWhiteSpace(s.DeviceKeyword))
                        r.Warnings.Add($"{label}：串口名与设备关键词都为空，自动识别将找不到端口，请至少填一项");
                }
            }
        }

        /// <summary>校验气压表配置（Modbus RTU：总数/串口/寄存器读取数/小数位）</summary>
        private static void ValidateBarometer(DeviceHubConfig config, ConfigValidationResult r)
        {
            BarometerConfig b = config.Barometer;
            if (b == null) return;

            if (b.TotalBarometers < 1)
                r.Errors.Add("气压表：总数 TotalBarometers 至少为 1");
            if (b.BarometerReadRegisterCount < 1 || b.BarometerReadRegisterCount > 125)
                r.Errors.Add($"气压表：单次读寄存器数 BarometerReadRegisterCount 应为 1~125（Modbus 单帧上限），当前 {b.BarometerReadRegisterCount}");
            if (b.BarometerDefaultDecimalPlaces < 0 || b.BarometerDefaultDecimalPlaces > 10)
                r.Warnings.Add($"气压表：小数位 BarometerDefaultDecimalPlaces={b.BarometerDefaultDecimalPlaces} 异常（正常 0~10）");

            if (config.UseMockCommunication) return; // Mock 模式不接串口，串口参数不校验

            if (b.BaudRate <= 0) r.Errors.Add("气压表：波特率 BaudRate 必须大于 0");
            if (b.DataBits < 5 || b.DataBits > 8) r.Errors.Add("气压表：数据位 DataBits 应为 5~8");
            if (b.StopBits != 1 && b.StopBits != 15 && b.StopBits != 2)
                r.Warnings.Add($"气压表：停止位 StopBits={b.StopBits} 应为 1/15/2");
            if (!IsValidParity(b.Parity))
                r.Warnings.Add($"气压表：校验位 [{b.Parity}] 不是标准枚举名（None/Odd/Even/Mark/Space）");
            if (b.SerialReadTimeoutMs <= 0) r.Errors.Add("气压表：读超时必须大于 0");
            if (b.SerialWriteTimeoutMs <= 0) r.Errors.Add("气压表：写超时必须大于 0");
        }

        /// <summary>校验 IO 耦合器配置（Modbus TCP：总数/IP/备用通道映射）</summary>
        private static void ValidateIo(DeviceHubConfig config, ConfigValidationResult r)
        {
            IoConfig io = config.Io;
            if (io == null) return;

            if (io.TotalInputs < 1) r.Errors.Add("IO 耦合器：输入通道总数 TotalInputs 至少为 1");
            if (io.TotalOutputs < 1) r.Errors.Add("IO 耦合器：输出通道总数 TotalOutputs 至少为 1");

            if (io.IoBackupChannelMappings != null)
            {
                for (int i = 0; i < io.IoBackupChannelMappings.Count; i++)
                {
                    IoOutputChannelRemap m = io.IoBackupChannelMappings[i];
                    if (m == null) { r.Errors.Add($"IO 耦合器：备用通道映射第 {i + 1} 项为 null"); continue; }
                    if (m.SourceChannel < 0 || m.SourceChannel > 31)
                        r.Errors.Add($"IO 耦合器：备用映射源通道 {m.SourceChannel} 越界（0~31）");
                    if (m.TargetChannel < 0 || m.TargetChannel > 31)
                        r.Errors.Add($"IO 耦合器：备用映射目标通道 {m.TargetChannel} 越界（0~31）");
                    if (m.SourceRegister == m.TargetRegister && m.SourceChannel == m.TargetChannel)
                        r.Warnings.Add($"IO 耦合器：备用映射第 {i + 1} 项源=目标（{m.SourceRegister}@{m.SourceChannel}），无意义");
                }
            }

            if (config.UseMockCommunication) return; // Mock 不建 TCP，跳过网络校验

            CheckIp(io.PlcAddress, "IO 耦合器 IP", r);
            CheckPort(io.PlcPort, "IO 耦合器端口", r);
        }

        /// <summary>校验送风机配置（Modbus TCP：启用开关/IP/端口/状态区块偏移）</summary>
        private static void ValidateFan(DeviceHubConfig config, ConfigValidationResult r)
        {
            FanConfig f = config.Fan;
            if (f == null) return;

            if (!f.FanEnabled) return; // 未启用则整段不生效

            if (f.FanStatusCount < 1)
                r.Errors.Add("送风机：读状态区块长度 FanStatusCount 至少为 1");
            if (f.FanTimeoutMs <= 0) r.Errors.Add("送风机：超时 FanTimeoutMs 必须大于 0");

            // 字段偏移必须落在状态区块内，否则该字段恒为 0
            CheckOffset(f.FanRunStateOffset, f.FanStatusCount, "运行状态", r);
            CheckOffset(f.FanTemperatureOffset, f.FanStatusCount, "温度", r);
            CheckOffset(f.FanHumidityOffset, f.FanStatusCount, "湿度", r);
            CheckOffset(f.FanTempSetpointOffset, f.FanStatusCount, "温度设定", r);
            CheckOffset(f.FanHumSetpointOffset, f.FanStatusCount, "湿度设定", r);

            if (config.UseMockCommunication) return; // Mock 不建 TCP

            CheckIp(f.FanIpAddress, "送风机 IP", r);
            CheckPort(f.FanPort, "送风机端口", r);
            if (f.FanAutoDetectEnabled && f.FanIpCandidates != null)
            {
                for (int i = 0; i < f.FanIpCandidates.Count; i++)
                {
                    string ip = f.FanIpCandidates[i];
                    if (string.IsNullOrWhiteSpace(ip)) continue;
                    if (!IsValidIp(ip))
                        r.Warnings.Add($"送风机：候选 IP #{i + 1} [{ip}] 格式非法（自动识别时会跳过它）");
                }
            }
        }

        /// <summary>校验图像存储配置（存图根目录/保留天数）</summary>
        private static void ValidateImage(DeviceHubConfig config, ConfigValidationResult r)
        {
            ImageConfig img = config.Image;
            if (img == null) return;

            if (string.IsNullOrWhiteSpace(img.SaveRootDir))
                r.Warnings.Add("图像存储：存图根目录 SaveRootDir 为空，图像将无法归档（默认会尝试 E:\\Images 语义）");
            if (img.KeepDays < 0)
                r.Errors.Add($"图像存储：保留天数 KeepDays 不能为负（当前 {img.KeepDays}，0=不自动清理）");
            if (img.SubDirs == null || img.SubDirs.Count == 0)
                r.Warnings.Add("图像存储：目录层级 SubDirs 为空，所有图会直接存根目录下，注意防文件名冲突");
            if (string.IsNullOrWhiteSpace(img.FtpRootDir) && config.Cameras != null)
                r.Warnings.Add("图像存储：FTP 兜底目录 FtpRootDir 为空，相机未单独配 FtpUploadDir 时将无处落图");
        }

        /// <summary>校验跨设备的全局配置（PLC 角色/型号/Mock）</summary>
        private static void ValidateGlobal(DeviceHubConfig config, ConfigValidationResult r)
        {
            // PLC 角色选从站但主站配置被填了轮询项之类不冲突，无需强校验；
            // 但各角色"配置段必须存在"是明确语义，缺了就报错。
            if (config.PlcRole == PlcRole.Master && config.PlcMaster == null)
                r.Errors.Add("全局：PLC 角色为 Master（主站）但未配置 PlcMaster 参数");
            if (config.PlcRole == PlcRole.MultiBrand && config.MultiBrandPlc == null)
                r.Errors.Add("全局：PLC 角色为 MultiBrand（多品牌）但未配置 MultiBrandPlc 参数");

            // 从站模式写型号要 ProductModel 非空，否则 PLC 建站不写型号（有型号需求时才算问题）
            if (config.PlcRole == PlcRole.Slave && string.IsNullOrWhiteSpace(config.ProductModel))
                r.Warnings.Add("全局：产品型号 ProductModel 为空，PLC 从站建站后将不写入型号区（若现场不用型号可忽略）");
        }

        // ═══════════════ 公共小工具 ═══════════════

        /// <summary>校验 IP 字符串：IPAddress.TryParse 能解析即合法（"0.0.0.0" 合法）</summary>
        private static bool IsValidIp(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return false;
            IPAddress addr;
            return IPAddress.TryParse(ip.Trim(), out addr);
        }

        /// <summary>校验 IP：非法则追加错误</summary>
        private static void CheckIp(string ip, string what, ConfigValidationResult r)
        {
            if (!IsValidIp(ip))
                r.Errors.Add($"{what} 的 IP 地址 [{ip}] 格式非法");
        }

        /// <summary>校验端口：1~65535</summary>
        private static void CheckPort(int port, string what, ConfigValidationResult r)
        {
            if (port < 1 || port > 65535)
                r.Errors.Add($"{what} 的端口 {port} 越界（合法 1~65535）");
        }

        /// <summary>校验字段偏移落在状态区块内（否则该字段恒读 0）</summary>
        private static void CheckOffset(ushort offset, ushort blockLen, string fieldName, ConfigValidationResult r)
        {
            if (offset >= blockLen)
                r.Warnings.Add($"送风机：字段 [{fieldName}] 的偏移 {offset} 超出状态区块长度 {blockLen}，该字段将恒为 0");
        }

        /// <summary>校验串口校验位枚举名（大小写不敏感）</summary>
        private static bool IsValidParity(string parity)
        {
            if (string.IsNullOrWhiteSpace(parity)) return false;
            return parity.Equals("None", System.StringComparison.OrdinalIgnoreCase)
                || parity.Equals("Odd", System.StringComparison.OrdinalIgnoreCase)
                || parity.Equals("Even", System.StringComparison.OrdinalIgnoreCase)
                || parity.Equals("Mark", System.StringComparison.OrdinalIgnoreCase)
                || parity.Equals("Space", System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
