using System;
using System.Collections.Generic;
using CommonLib.Models;

namespace CommonLib.Utils
{
    /// <summary>
    /// IO 映射表构建器：依据现场"IO 分配表"建立内部连续编号与三菱 PLC 物理地址之间的映射关系。
    /// 【来源】AgingTestSystem.Services.IoMapBuilder 原样移植，参数改为显式 int（不再依赖业务配置类）。
    ///
    /// 【IO 分配表 实际配置】
    /// - 输入(NPN)：真空负压表-1~TotalBarometers，地址 X000~X107 (八进制)
    /// - 输出(PNP)：真空电磁阀-1~N（Y000~Y107）、载台上电-1~N（Y110~Y217）
    /// - 每个气压表对应：1 输入 + 2 输出
    ///
    /// 【内部编号 vs 物理地址】
    /// 程序内部使用十进制连续编号(IoId)便于数组索引：
    ///   输入: 1 ~ TotalInputs；输出: TotalInputs+1 ~ TotalInputs+TotalOutputs
    /// 与硬件通信时需通过物理地址(PhysicalAddress)寻址。
    ///
    /// 【八进制编址说明】三菱 PLC 的 X/Y 点采用八进制编号（每位数字仅 0~7）。
    /// 例如 X007 之后是 X010（非 X008），X077 之后是 X100。本构建器使用
    /// Convert.ToString(value, 8) 将十进制转为八进制字符串。
    /// </summary>
    public static class IoMapBuilder
    {
        /// <summary>
        /// 构建完整的 IO 映射表：按"输入→真空电磁阀输出→载台上电输出→预留输入/输出"顺序生成所有 IO 点定义。
        /// 业务角度：每个气压表固定对应 1 输入 + 2 输出，"业务必需"总数由 totalBarometers 决定。
        /// 现场角度：模块数量可能多于业务使用量（如 80DI/160DO 但业务用 72DI/144DO），
        /// 多出来的通道作为"预留点"也生成出来，便于扩展或现场排查。
        /// </summary>
        /// <param name="totalBarometers">气压表总数（业务必需点数由此决定）</param>
        /// <param name="totalInputs">IO 输入通道总数（DI 通道数，≥ totalBarometers）</param>
        /// <param name="totalOutputs">IO 输出通道总数（DO 通道数，≥ totalBarometers×2）</param>
        /// <returns>所有 IO 点定义列表</returns>
        public static List<IoPointDefinition> Build(int totalBarometers, int totalInputs, int totalOutputs)
        {
            if (totalBarometers < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(totalBarometers), "气压表总数不能小于1");
            }

            if (totalInputs < totalBarometers)
            {
                throw new ArgumentOutOfRangeException(nameof(totalInputs),
                    "TotalInputs 不能小于 TotalBarometers（每个气压表至少需要 1 个输入点）");
            }

            if (totalOutputs < totalBarometers * 2)
            {
                throw new ArgumentOutOfRangeException(nameof(totalOutputs),
                    "TotalOutputs 不能小于 TotalBarometers×2（每个气压表至少需要 2 个输出点）");
            }

            var map = new List<IoPointDefinition>(totalInputs + totalOutputs);
            int ioId = 1;

            // ===== 1. 输入点: 真空负压表-N → X 地址(八进制) =====
            // 地址规律: X + octal(n-1), 即 n=1→X000, n=8→X007, n=9→X010, n=72→X107
            for (int n = 1; n <= totalBarometers; n++)
            {
                map.Add(new IoPointDefinition
                {
                    IoId = ioId++,
                    PhysicalAddress = "X" + ToOctal(n - 1),
                    DeviceName = $"真空负压表-{n}",
                    DeviceId = n,
                    Type = IoType.Input,
                    Function = IoFunction.VacuumPressure,
                    Electrical = ElectricalType.NPN,
                    LocalIndex = 1
                });
            }

            // ===== 1.1 预留输入点: X 地址继续顺延 =====
            // 例如：TotalInputs=80, TotalBarometers=72，则预留输入为 73~80，对应 X110~X117
            for (int n = totalBarometers + 1; n <= totalInputs; n++)
            {
                map.Add(new IoPointDefinition
                {
                    IoId = ioId++,
                    PhysicalAddress = "X" + ToOctal(n - 1),
                    DeviceName = $"预留输入-{n}",
                    DeviceId = n,
                    Type = IoType.Input,
                    Function = IoFunction.Unknown,
                    Electrical = ElectricalType.NPN,
                    LocalIndex = 1
                });
            }

            // ===== 2. 输出点A: 真空电磁阀-N → Y 地址(八进制) =====
            // 地址规律: Y + octal(n-1), 即 n=1→Y000, n=72→Y107
            for (int n = 1; n <= totalBarometers; n++)
            {
                map.Add(new IoPointDefinition
                {
                    IoId = ioId++,
                    PhysicalAddress = "Y" + ToOctal(n - 1),
                    DeviceName = $"真空电磁阀-{n}",
                    DeviceId = n,
                    Type = IoType.Output,
                    Function = IoFunction.VacuumValve,
                    Electrical = ElectricalType.PNP,
                    LocalIndex = 1
                });
            }

            // ===== 3. 输出点B: 载台上电-N → Y 地址(八进制) =====
            // 地址规律: Y + octal(totalBarometers + n - 1)
            // 即从 totalBarometers 的八进制地址开始(72→110)：n=1→Y110, n=8→Y117, n=9→Y120, n=72→Y217
            for (int n = 1; n <= totalBarometers; n++)
            {
                map.Add(new IoPointDefinition
                {
                    IoId = ioId++,
                    PhysicalAddress = "Y" + ToOctal(totalBarometers + n - 1),
                    DeviceName = $"载台上电-{n}",
                    DeviceId = n,
                    Type = IoType.Output,
                    Function = IoFunction.CarrierPower,
                    Electrical = ElectricalType.PNP,
                    LocalIndex = 2
                });
            }

            // ===== 3.1 预留输出点: Y 地址继续顺延 =====
            // 例如：TotalOutputs=160, TotalBarometers=72，则预留输出为 145~160，对应 Y220~Y237
            int usedOutputs = totalBarometers * 2;
            for (int n = usedOutputs + 1; n <= totalOutputs; n++)
            {
                map.Add(new IoPointDefinition
                {
                    IoId = ioId++,
                    PhysicalAddress = "Y" + ToOctal(n - 1),
                    DeviceName = $"预留输出-{n}",
                    DeviceId = n,
                    Type = IoType.Output,
                    Function = IoFunction.Unknown,
                    Electrical = ElectricalType.PNP,
                    LocalIndex = 1
                });
            }

            return map;
        }

        /// <summary>
        /// 获取指定气压表的 IO 点映射（1 输入 + 2 输出），供业务层按设备号直接定位三个 IO 点。
        /// </summary>
        /// <param name="deviceId">气压表编号（1 ~ totalBarometers）</param>
        /// <param name="totalBarometers">气压表总数</param>
        /// <param name="totalInputs">IO 输入通道总数（用于计算内部输出编号起点）</param>
        /// <returns>该设备的 IO 点映射集合</returns>
        /// <exception cref="ArgumentOutOfRangeException">deviceId 越界或参数非法时抛出</exception>
        public static DeviceIoMapping GetDeviceMapping(int deviceId, int totalBarometers, int totalInputs)
        {
            if (totalBarometers < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(totalBarometers), "气压表总数不能小于1");
            }
            if (totalInputs < totalBarometers)
            {
                throw new ArgumentOutOfRangeException(nameof(totalInputs),
                    "TotalInputs 不能小于 totalBarometers（每个气压表至少需要 1 个输入点）");
            }
            if (deviceId < 1 || deviceId > totalBarometers)
            {
                throw new ArgumentOutOfRangeException(nameof(deviceId),
                    $"设备编号 {deviceId} 超出合法范围 [1, {totalBarometers}]");
            }

            return new DeviceIoMapping
            {
                // 输入: 真空负压表, X + octal(deviceId-1)
                VacuumPressureInput = new IoPointDefinition
                {
                    IoId = deviceId,
                    PhysicalAddress = "X" + ToOctal(deviceId - 1),
                    DeviceName = $"真空负压表-{deviceId}",
                    DeviceId = deviceId,
                    Type = IoType.Input,
                    Function = IoFunction.VacuumPressure,
                    Electrical = ElectricalType.NPN,
                    LocalIndex = 1
                },
                // 输出1: 真空电磁阀, Y + octal(deviceId-1)
                VacuumValveOutput = new IoPointDefinition
                {
                    IoId = totalInputs + deviceId,
                    PhysicalAddress = "Y" + ToOctal(deviceId - 1),
                    DeviceName = $"真空电磁阀-{deviceId}",
                    DeviceId = deviceId,
                    Type = IoType.Output,
                    Function = IoFunction.VacuumValve,
                    Electrical = ElectricalType.PNP,
                    LocalIndex = 1
                },
                // 输出2: 载台上电, Y + octal(totalBarometers + deviceId - 1)
                CarrierPowerOutput = new IoPointDefinition
                {
                    IoId = totalInputs + totalBarometers + deviceId,
                    PhysicalAddress = "Y" + ToOctal(totalBarometers + deviceId - 1),
                    DeviceName = $"载台上电-{deviceId}",
                    DeviceId = deviceId,
                    Type = IoType.Output,
                    Function = IoFunction.CarrierPower,
                    Electrical = ElectricalType.PNP,
                    LocalIndex = 2
                }
            };
        }

        /// <summary>
        /// 将十进制数值转换为三菱 PLC 八进制地址字符串（3 位，前补零）。
        /// 例如: 0→"000", 7→"007", 8→"010", 71→"107", 72→"110", 143→"217"。
        /// </summary>
        /// <param name="decimalValue">十进制数值</param>
        /// <returns>3 位八进制字符串</returns>
        private static string ToOctal(int decimalValue)
        {
            // Convert.ToString(value, 8) 将十进制转为八进制字符串（如 0→"0", 8→"10", 72→"110"）
            // PadLeft(3, '0') 确保至少 3 位，不足前补零（如 "0"→"000", "10"→"010"）
            return System.Convert.ToString(decimalValue, 8).PadLeft(3, '0');
        }
    }
}