using System;
using System.Threading;
using System.Threading.Tasks;

namespace Kaleidoscope.Services
{
    // =====================================================================================
    // 统一读码抽象（ICodeReader，2026-10-25 自 HJVision 生产工程第二轮下沉新增）。
    // -------------------------------------------------------------------------------------
    // 【为什么要有它】库里有两族读码设备，编程模型完全不同：
    //   ① IDMVS 读码器（CodeReaderClient，同目录）：**触发式**——业务按节拍调
    //      AcquireSingleAsync/AcquireContinuousAsync，一次调用换一次读码结果
    //      （开采集→start→回码→关采集，请求-响应）；
    //   ② 基恩士 SR 扫码枪家族（ScannerService/ScannerTcpService/ScannerEipService，
    //      实现 IScanner）：**被动事件流**——Open 后枪自动连续读码，扫到一条抛一条
    //      SerialNumberScanned 事件，业务订阅事件收码。
    // 两族对上层（生产脚本/流程编排）暴露的其实是同一个业务语义："现在给我读一次码，
    // 拿到码串或 NoRead"。本接口把这个语义归一：上层只依赖 ICodeReader，
    // 换读码设备（IDMVS ↔ 基恩士 SR）不改业务代码。
    //
    // 【设计取舍】
    //   - 返回值复用 ReaderAcquireResult（IDMVS 侧既有结果类）：Success/Code/RawFrame/
    //     IsNoRead/Error 五字段对被动枪同样成立（超时=Success false + Error 文案）；
    //   - 请求参数收进 CodeReadRequest（单次重读/持续时长/读到即停）：IDMVS 实现按参数
    //     分流到既有 AcquireSingleAsync/AcquireContinuousAsync；SR 适配器
    //     （KeyenceScannerReader）把参数统一映射为"触发后等一条上报或超时"（枪端
    //     自动连打，单次/持续语义天然成立，不再软件层重试）；
    //   - 事件只留连接边沿 ConnectionChanged(bool)：两族都能给；码上报不进接口
    //     （触发式语义下码就是返回值，不需要旁路事件）。
    // =====================================================================================

    /// <summary>触发式读码请求参数（字段缺省 0/false 时由实现按各自缺省口径处理）。</summary>
    public sealed class CodeReadRequest
    {
        /// <summary>单次模式总预算 ms（开采集+触发+重读+关采集；≤0 用实现缺省，IDMVS=8000）。</summary>
        public int TimeoutMs;

        /// <summary>false=单次模式（有限次重读，IDMVS 缺省惯例）；true=持续模式（时长内循环触发）。</summary>
        public bool Continuous;

        /// <summary>持续模式时长 ms（钳制由实现负责，IDMVS 口径 [500,60000] 缺省 3000）。</summary>
        public int ContinuousMs;

        /// <summary>持续模式读到第一笔码立即返回（true，生产缺省）；false=打满时长取最后一笔。</summary>
        public bool StopOnFirstCode = true;

        /// <summary>单次模式最大触发次数（≤0 用实现缺省，IDMVS=3，钳 [1,5]）。</summary>
        public int MaxAttempts;

        /// <summary>单次模式重读间隔 ms（<0 用实现缺省，IDMVS=300）。</summary>
        public int RetryDelayMs;
    }

    /// <summary>
    /// 触发式读码统一接口：一次调用=一次完整读码（触发→拿码/NoRead→收尾），业务按节拍调。
    /// 实现方：CodeReaderClient（IDMVS，直实现）、KeyenceScannerReader（基恩士 SR 家族适配器）。
    /// 线程口径：实例方法不做并发保护，业务侧自行串行（一次只飞一枪）。
    /// </summary>
    public interface ICodeReader : IDisposable
    {
        /// <summary>设备名（日志/状态灯标识；IDMVS=配置名，SR=串口名或 IP:端口）。</summary>
        string Name { get; }

        /// <summary>当前是否已连接/已打开。</summary>
        bool IsConnected { get; }

        /// <summary>
        /// 连接状态边沿事件（true=已连接，false=断开；工作线程触发，UI 订阅方需自行 Invoke）。
        /// 实现方需做边沿去重（状态没翻转不发）。
        /// </summary>
        event EventHandler<bool> ConnectionChanged;

        /// <summary>
        /// 触发一次读码并等结果。NoRead/超时=Success false（业务判 NG），不抛异常
        /// （参数非法 ArgumentNull 除外）。返回的 ReaderAcquireResult 绝不为 null。
        /// </summary>
        Task<ReaderAcquireResult> AcquireAsync(CodeReadRequest request, CancellationToken ct);
    }
}
