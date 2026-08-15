using System;
using Kaleidoscope.Models;

namespace Kaleidoscope.Services
{
    /// <summary>
    /// 冷却送风机控制接口（真实 / Mock 共用同一声明，业务层只依赖本接口）。
    ///
    /// 【设计说明】与 IBarometerReader / IIoController 保持同一套设计风格：
    /// 上层只依赖接口，不关心底层是"真实设备"还是"Mock 模拟"；现场接入真实送风机时
    /// 只要实现本接口即可，不动上层业务代码。
    ///
    /// 【业务说明】冷却送风机（厂商自带控制屏）的自动控温已由厂商集成好，上位机只需：
    /// 1. 定值启动：让送风机按控制屏上的设定温度运行（厂商自动控温）
    /// 2. 定值停止：停止送风机
    /// 另可周期读状态（运行中/已停止、温度、湿度）用于显示。
    ///
    /// 【线程说明】通讯可能被"采集线程"和"UI 线程（按钮点击）"同时调用，
    /// 实现类必须保证线程安全（lock 串行化对主站的访问）。
    /// </summary>
    public interface IFanController : IDisposable
    {
        /// <summary>连接状态：true=已连接，false=未连接。</summary>
        bool IsConnected { get; }

        /// <summary>当前实际连接成功的送风机 IP（自动识别结果；未连接成功时为 null）。</summary>
        string ActiveIp { get; }

        /// <summary>连接送风机控制屏（内部自动：断开旧连→按候选 IP 逐个尝试→建 Modbus 主站）。</summary>
        /// <param name="config">送风机配置</param>
        /// <returns>是否连接成功（失败通过 OnError 通知）</returns>
        bool Connect(FanConfig config);

        /// <summary>断开连接（释放 TCP 客户端与主站对象，连接状态置 false）。</summary>
        void Disconnect();

        /// <summary>
        /// 按需重连：用户操作需要送风机时调用——已连接直接返回 true，未连接立即重连一次
        /// （不等后台 10 秒节流，保证按钮响应及时）。
        /// </summary>
        /// <returns>重连后是否已连接</returns>
        bool ReconnectNow();

        /// <summary>读取送风机当前状态（状态 + 温度 + 湿度 + 设定值，一次批量读 6 个寄存器）。</summary>
        /// <returns>送风机数据；读取失败返回 null（上层据此显示"离线"）</returns>
        FanData ReadStatus();

        /// <summary>定值启动：让送风机按控制屏设定的温度运行（写入 0x0001 = 0x0003）。</summary>
        /// <returns>是否发送成功</returns>
        bool StartFixedValue();

        /// <summary>定值停止（写入 0x0001 = 0x0002）。</summary>
        /// <returns>是否发送成功</returns>
        bool Stop();

        /// <summary>通讯错误事件（连接失败、读写超时等；工作线程触发，UI 订阅方需自行 Invoke）。</summary>
        event EventHandler<string> OnError;
    }
}
