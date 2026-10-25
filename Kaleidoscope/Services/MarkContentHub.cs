// =====================================================================================
// 【下沉注记】本文件 2026-10-25 自 HJVision-WPF 生产工程（第二轮仪器设备下沉）
// 原样搬入，口径与第一轮 PlcNetCommQue/TcpChannel 下沉相同：
//   ①来源 GYZVision.Core/Model/MarkContentHub.cs（打码内容交接中心+两枚举），零其它改动；
//   ②随 LaserMarkService 连带下沉（其消费闭环在库内自洽），主项目经别名壳零改动引用。
//   其余算法/默认值/异常口径/注释全保留，公共 API 签名不变（主项目经
//   文件级 using 别名壳零改动引用，断点可跨工程打进本源码）。
// =====================================================================================
using System;
using System.Collections.Generic;

namespace Kaleidoscope.Services
{
    // =====================================================================================
    // 打码内容交接中心（老工程 SubClass/MarkContentHub.cs，原样搬运，仅命名空间改变）。
    // -------------------------------------------------------------------------------------
    // 内容来源三选一（Vision=视觉组装入队 / Plc=外部透传 / Fixed=固定模板试打）；
    // 无货策略三选一（Alarm=回错误串默认 / Repeat=重发上条 / Empty=回空行）。
    // 按设备 ID 分仓，生产侧与消费侧跨线程 lock 交接；TryTake 绝不抛异常。
    // =====================================================================================
    public enum MarkContentSource
    {
        Vision,
        Plc,
        Fixed
    }

    public enum MarkEmptyPolicy
    {
        Alarm,
        Repeat,
        Empty
    }

    public static class MarkContentHub
    {
        /// <summary>单个设备的仓：队列 + 策略 + 上一条（复刻用），锁保护。</summary>
        private class DeviceBin
        {
            public readonly object Lock = new object();
            public readonly Queue<string> Queue = new Queue<string>();
            public MarkContentSource Source = MarkContentSource.Plc;
            public MarkEmptyPolicy Policy = MarkEmptyPolicy.Alarm;
            public string ErrorText = "ERROR:NO_CONTENT";
            public string FixedText = "TEST";
            public string LastReplied;
        }

        private static readonly object s_binsLock = new object();
        private static readonly Dictionary<int, DeviceBin> s_bins = new Dictionary<int, DeviceBin>();

        private static DeviceBin GetBin(int deviceId)
        {
            lock (s_binsLock)
            {
                DeviceBin bin;
                if (!s_bins.TryGetValue(deviceId, out bin))
                {
                    bin = new DeviceBin();
                    s_bins[deviceId] = bin;
                }
                return bin;
            }
        }

        /// <summary>配置设备仓（来源/策略/错误串/固定串；改配置重调即生效，无需重启消费线程）。</summary>
        public static void Configure(int deviceId, MarkContentSource source, MarkEmptyPolicy policy,
            string errorText, string fixedText)
        {
            DeviceBin bin = GetBin(deviceId);
            lock (bin.Lock)
            {
                bin.Source = source;
                bin.Policy = policy;
                if (!string.IsNullOrEmpty(errorText)) bin.ErrorText = errorText;
                if (fixedText != null) bin.FixedText = fixedText;
            }
        }

        /// <summary>视觉组装的内容入队（与 PublishExternal 同一个队列）。</summary>
        public static void EnqueueVision(int deviceId, string content)
        {
            PublishExternal(deviceId, content);
        }

        /// <summary>外部内容发布（空串丢弃，空内容走 EmptyPolicy 不占位）。</summary>
        public static void PublishExternal(int deviceId, string content)
        {
            if (string.IsNullOrEmpty(content)) return;
            DeviceBin bin = GetBin(deviceId);
            lock (bin.Lock) { bin.Queue.Enqueue(content); }
        }

        /// <summary>
        /// 取一条内容应答下位机。Fixed 直接回固定串；有货取货（一问一条，取走即删）；
        /// 无货按策略处理。isError=true 表示回的是错误串/复刻旧值（调用方记告警）。
        /// 绝不抛异常。
        /// </summary>
        public static void TryTake(int deviceId, out string content, out bool isError)
        {
            try
            {
                DeviceBin bin = GetBin(deviceId);
                lock (bin.Lock)
                {
                    if (bin.Source == MarkContentSource.Fixed)
                    {
                        content = bin.FixedText;
                        isError = false;
                        return;
                    }
                    if (bin.Queue.Count > 0)
                    {
                        content = bin.Queue.Dequeue();
                        bin.LastReplied = content;
                        isError = false;
                        return;
                    }
                    switch (bin.Policy)
                    {
                        case MarkEmptyPolicy.Repeat:
                            if (!string.IsNullOrEmpty(bin.LastReplied))
                            {
                                content = bin.LastReplied;
                                isError = true;
                            }
                            else
                            {
                                content = bin.ErrorText;
                                isError = true;
                            }
                            break;
                        case MarkEmptyPolicy.Empty:
                            content = "";
                            isError = false;
                            break;
                        default:
                            content = bin.ErrorText;
                            isError = true;
                            break;
                    }
                }
            }
            catch
            {
                content = "ERROR:HUB_FAULT";
                isError = true;
            }
        }

        /// <summary>当前排队深度（状态页/日志用）。</summary>
        public static int QueuedCount(int deviceId)
        {
            DeviceBin bin = GetBin(deviceId);
            lock (bin.Lock) { return bin.Queue.Count; }
        }

        /// <summary>解析内容来源（大小写不敏感，非法回 fallback）。</summary>
        public static MarkContentSource ParseSource(string name, MarkContentSource fallback)
        {
            if (string.IsNullOrWhiteSpace(name)) return fallback;
            string n = name.Trim().ToUpperInvariant();
            if (n == "VISION") return MarkContentSource.Vision;
            if (n == "FIXED") return MarkContentSource.Fixed;
            if (n == "PLC") return MarkContentSource.Plc;
            return fallback;
        }

        /// <summary>解析空仓策略（大小写不敏感，非法回 fallback）。</summary>
        public static MarkEmptyPolicy ParsePolicy(string name, MarkEmptyPolicy fallback)
        {
            if (string.IsNullOrWhiteSpace(name)) return fallback;
            string n = name.Trim().ToUpperInvariant();
            if (n == "ALARM") return MarkEmptyPolicy.Alarm;
            if (n == "REPEAT") return MarkEmptyPolicy.Repeat;
            if (n == "EMPTY") return MarkEmptyPolicy.Empty;
            return fallback;
        }

        /// <summary>清空测试隔离用（探针/单测清理指定仓，生产代码不调）。</summary>
        public static void ClearForTest(int deviceId)
        {
            lock (s_binsLock) { s_bins.Remove(deviceId); }
        }
    }
}
