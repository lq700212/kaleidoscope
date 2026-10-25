// =====================================================================================
// 【下沉注记】本文件 2026-10-25 自 HJVision-WPF 生产工程（第二轮仪器设备下沉）
// 原样搬入，口径与第一轮 PlcNetCommQue/TcpChannel 下沉相同：
//   ①命名空间 HuaJiVision.Core.Devices → Kaleidoscope.Services；
//   ②演示模式出口 MockMode.IsEnabled → MockBridge.IsEnabled（宿主 MockMode 静态构造
//      接线 IsEnabledProvider，行为不变；库自身不依赖业务工程的 MockMode）；
//   ③日志出口仍是静态 LogSink 注入（宿主不接则静默，原口径不变）。
//   其余算法/默认值/异常口径/注释全保留，公共 API 签名不变（主项目经
//   文件级 using 别名壳零改动引用，断点可跨工程打进本源码）。
// =====================================================================================
using System;
using System.Threading;
using Kaleidoscope.Utils;

namespace Kaleidoscope.Services
{
    // =====================================================================================
    // 设备服务基类（老工程 SubClass/DeviceFramework/DeviceServiceBase.cs，原样搬运）。
    // -------------------------------------------------------------------------------------
    // 薄收编：volatile 停止旗 mDisposed / LogSink 日志注入（非泛型静态，全设备共用一个注入点，
    // 放泛型类上会按 TConfig 分裂）/ 时间节流日志 / 限时 Join / IsConnected 边沿字段 /
    // MockOk 演示短路。线程结构与连接事件签名各异，留在子类。
    // 跨项目红线：禁引业务符号；需读写 PLC 的服务由宿主经委托注入读写动作。
    // =====================================================================================
    public abstract class DeviceServiceBase : IDisposable
    {
        /// <summary>
        /// 日志出口（宿主注入；null 时 Log() 静默丢弃）。非泛型静态：全部设备共用一个注入点。
        /// </summary>
        public static Action<string> LogSink;

        protected void Log(string msg)
        {
            try { if (LogSink != null) LogSink(FormatLog(msg)); } catch { }
        }

        /// <summary>日志格式化钩子（子类覆写拼设备前缀；默认透传；覆写内禁抛异常）。</summary>
        protected virtual string FormatLog(string msg)
        {
            return msg;
        }

        /// <summary>停止旗：Dispose 置位后后台线程见旗即退；对外动作入口见旗即拒。</summary>
        protected volatile bool mDisposed;

        /// <summary>服务是否已 Dispose（热更/退出竞态下先查它再调动作）。</summary>
        public bool IsDisposed { get { return mDisposed; } }

        /// <summary>连接状态（子类维护边沿后对外抛各自事件）。</summary>
        protected volatile bool mConnected;

        /// <summary>当前通讯是否正常（判据由子类维护）。</summary>
        public bool IsConnected { get { return mConnected; } }

        /// <summary>启动服务（后台线程/监听；异常只记日志不外抛，不挡主程序启动）。</summary>
        public abstract void Start();

        /// <summary>停止并释放（置旗→关句柄唤醒→限时 Join；必须可重复调）。</summary>
        public abstract void Dispose();

        /// <summary>
        /// 连通性测试（配置页"测试通讯"用）：调用方后台线程里调，内部允许有界阻塞；
        /// 失败时 info 给"现场能照着排查"的一句话，不抛异常。
        /// </summary>
        public abstract bool TestConnection(out string info);

        /// <summary>当前状态一句话摘要（绝不抛异常）。</summary>
        public abstract string GetDebugInfo();

        /// <summary>
        /// Mock 演示短路：TestConnection/TestViaExistingConnection 入口先调它，
        /// 命中即 info 填演示文案回 true，不碰硬件。
        /// </summary>
        public static bool MockOk(out string info, string deviceName)
        {
            if (MockBridge.IsEnabled) { info = deviceName + " Mock已连通（演示数据，不接硬件）"; return true; }
            info = null;
            return false;
        }

        /// <summary>限时 Join（线程 null 安全 + 异常吞掉，不把关窗/热更线程拖死）。</summary>
        protected static void JoinQuietly(Thread thread, int timeoutMs)
        {
            try { if (thread != null) thread.Join(timeoutMs); } catch { }
        }

        private DateTime mLastThrottledLog = DateTime.MinValue;

        /// <summary>时间节流日志（同类高频异常 gapSeconds 秒内只记一次，防日志爆炸）。</summary>
        protected void LogThrottled(string msg, int gapSeconds)
        {
            try
            {
                if ((DateTime.Now - mLastThrottledLog).TotalSeconds < gapSeconds) return;
                mLastThrottledLog = DateTime.Now;
                Log(msg);
            }
            catch { }
        }
    }

    /// <summary>设备服务基类——泛型配置层（仅追加"配置持有"，用法见老注释）。</summary>
    public abstract class DeviceServiceBase<TConfig> : DeviceServiceBase where TConfig : new()
    {
        /// <summary>本服务配置（构造 null 兜底 new，实例永不因空配置崩构造）。</summary>
        protected readonly TConfig mCfg;

        /// <summary>本实例配置（只读引用；改配置走重建服务=热更）。</summary>
        public TConfig Config { get { return mCfg; } }

        protected DeviceServiceBase(TConfig cfg)
        {
            mCfg = cfg == null ? new TConfig() : cfg;
        }
    }
}
