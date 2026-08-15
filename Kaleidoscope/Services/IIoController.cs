using System;
using Kaleidoscope.Models;

namespace Kaleidoscope.Services
{
    /// <summary>
    /// IO 控制器接口（真实 / Mock 共用同一声明，业务层只依赖本接口）。
    ///
    /// 【内部编号 vs 物理地址】
    /// 方法参数(inputId/outputId)使用"内部十进制连续编号"：
    ///   输入：1 ~ TotalInputs（如 1 ~ 72）
    ///   输出：TotalInputs+1 ~ TotalInputs+TotalOutputs（如 73 ~ 216）
    /// 与硬件通信时通过 <see cref="IoMapBuilder"/> 转换为物理地址（三菱八进制 X/Y）。
    ///
    /// 【IO 点编号规则】（依据现场 IO 分配表）
    /// - 输入点(NPN, X 地址)：内部编号 n → 物理地址 X+octal(n-1)，1→X000、8→X007、9→X010、72→X107
    /// - 输出点(PNP, Y 地址)：
    ///   真空电磁阀(内部 TotalInputs+1~+TotalBarometers)：n→Y+octal(n-1)
    ///   载台上电(内部 TotalInputs+TotalBarometers+1~+2×TotalBarometers)：n→Y+octal(TotalBarometers+n-1)
    /// 每个气压表对应：1 输入(真空负压表) + 2 输出(真空电磁阀 + 载台上电)
    ///
    /// 【接入要点】由 DeviceHub 创建并持有，业务层通过 hub.Io 属性拿到实例：
    /// 采集轮询读 <see cref="ReadAllInputs"/> / <see cref="ReadAllOutputs"/>；控制输出调
    /// <see cref="WriteOutput"/>（内部读-改-写单点，备用通道烧毁可经配置重定向）。
    /// </summary>
    public interface IIoController : IDisposable
    {
        /// <summary>连接状态：true=已连接，false=未连接（连接层异常会自动置 false 等待自动重连）。</summary>
        bool IsConnected { get; }

        /// <summary>连接 IO 耦合器设备（内部自动：断开旧连→BeginConnect 手动超时→建 Modbus 主站）。</summary>
        /// <param name="config">IO 耦合器配置</param>
        /// <returns>是否连接成功（失败通过 OnError 通知）</returns>
        bool Connect(IoConfig config);

        /// <summary>断开连接（释放 TCP 客户端与主站对象，连接状态置 false）。</summary>
        void Disconnect();

        /// <summary>读取单个输入点状态（失败返回 false，不抛异常；连接层异常自动标记断开）。</summary>
        /// <param name="inputId">输入点内部编号（1 ~ TotalInputs）</param>
        /// <returns>输入点状态：true=导通(NPN 传感器拉低电平)，false=断开</returns>
        bool ReadInput(int inputId);

        /// <summary>批量读取所有输入点状态（一次批量读寄存器，减少网络往返）。</summary>
        /// <returns>输入点状态数组，索引 0 对应输入点 1（X000）；未连接/异常返回空数组</returns>
        bool[] ReadAllInputs();

        /// <summary>写入单个输出点状态（内部读-改-写只改 1 bit，不误伤同寄存器其它通道）。</summary>
        /// <param name="outputId">输出点内部编号（TotalInputs+1 ~ TotalInputs+TotalOutputs）</param>
        /// <param name="state">输出状态：true=导通(PNP 输出 +24V 驱动继电器)，false=断开</param>
        void WriteOutput(int outputId, bool state);

        /// <summary>批量写入输出点状态（逐台调用 WriteOutput，编号与状态一一对应）。</summary>
        /// <param name="outputIds">输出点编号数组</param>
        /// <param name="states">输出状态数组，与 outputIds 一一对应</param>
        void WriteOutputs(int[] outputIds, bool[] states);

        /// <summary>读取单个输出点状态（用于回读确认）。</summary>
        /// <param name="outputId">输出点内部编号（TotalInputs+1 ~ TotalInputs+TotalOutputs）</param>
        /// <returns>输出点状态</returns>
        bool ReadOutput(int outputId);

        /// <summary>批量读取所有输出点状态。</summary>
        /// <returns>输出点状态数组；未连接/异常返回空数组</returns>
        bool[] ReadAllOutputs();

        /// <summary>通讯错误事件（连接失败、读写失败等；工作线程触发，UI 订阅方需自行 Invoke）。</summary>
        event EventHandler<string> OnError;
    }
}
