using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace Kaleidoscope.Flowing
{
    // =====================================================================================
    // 业务流编排 · 处理线程组调度器（上位机多组常驻轮询线程的统一管理口，V1.9.0 沉库）
    // -------------------------------------------------------------------------------------
    // 【干什么】把上位机业务流（各站轮询、扫描循环、设备巡检）按"硬件独占资源"拆成多组，
    //   每组一个专属命名后台线程按固定周期跑"一圈动作"；本类统一管理组生命周期：
    //   命名/防重入/独占资源冲突 fail-fast/异常隔离与节流记账/心跳统计/健康报告。
    //   来源：宿主 HJVision 业务流调度器（原 SubClass/LineFlow/LineFlowGroupRunner.cs，
    //   2026-11-14 V4.4.401 建立、V4.4.402 复用封装）一比一下沉，语义零改动——
    //   只改命名空间与类名（去 Line 前缀，库内通用件不带宿主业务前缀）。
    // -------------------------------------------------------------------------------------
    // 【为什么是专属线程不是 ThreadPool】组循环是长驻 while，会永久占死池 worker；上位机
    //   主程序大量使用 Task.Run 与 Timer（采集链常在其中），池 worker 被占满会拖累全线
    //   异步任务。本类=专属命名线程 + 统一管理器：管控语义（命名/防重/生命周期/心跳/
    //   异常统计/健康报告）与线程池一致，但不踩池饥饿的坑。
    // 【为什么按组拆】现场实证（2026-11-14 视觉产线 16 点档）：全部站挤一条处理线程串行
    //   轮询时，长耗时站（震动盘供料不顺"补料5.5s→拍照→NG"多轮循环最长 5 轮≈27s）会把
    //   同线程排队的其它站拖到触发超龄被扫描线程放弃——该站检测从未执行，MES 收到
    //   全 0/NG 空数据、无存图无报表记录（五件产品实证）。治本=按"硬件独占资源"拆组：
    //   同相机/同 ToolBlock/同读码器的站必须同组串行（拆开并发会串台），不同组的站硬件
    //   互不相干、并发互不拖延——长耗时站从此只堵自己那组。
    // 【资源标签=手写脚本的自然防线】生成器路径可以做"同相机跨组校验"，但手写脚本（别的
    //   项目只写 RunScript.cs 的场景）以前写错组没有任何拦——要等现场串台/帧序错乱才炸。
    //   现在每组声明它占用的硬件，调度器启动时检测"同一资源串出现在两个存活组"立即抛
    //   InvalidOperationException（fail-fast：配置错误启动即暴露，绝不带病上线静默串台）。
    //   归一规则=Trim+忽略大小写——CAM3 与 cam3 必是同一台硬件，防手滑漏检；同组内重复
    //   标签自动去重无害。标签粒度自定：相机号/ToolBlock 槽/读码器枪号/震动盘都行，凡
    //   "两组都直接占用的硬件"必须同串标进同一组参数里。
    // -------------------------------------------------------------------------------------
    // 【怎么接】三步：
    //   ①（可选）启动前注入日志口：FlowGroupRunner.LogSink = 自己的日志方法;——不注入走
    //      Debug.WriteLine（调试输出窗口可见，纯库形态不注入也能跑）；宿主把调度日志
    //      导进自己的运行日志即可与业务日志同流。
    //   ②按组调用 StartGroup(组名, 一圈动作, 周期ms, 独占资源标签...)——每组"一圈动作"=
    //      组内各站按轮询序逐个 Poll；调度器内部包 while 循环+异常隔离+心跳打点，调用方
    //      只给一圈动作与周期。同一圈整体一个 try：某站异常中断本圈后续站，下一圈继续。
    //   ③诊断与收尾：HealthReport() 一行一组（存活/最近圈龄/圈数/异常数/独占资源），
    //      "最近圈龄"异常变大=组内某站卡住或线程死锁，是排查第一入口；StopAll() 全停
    //      （尽力 Join，停止即释放资源标签，可重新 StartGroup）。
    //   线程=命名后台线程"流组-组名"，伴生进程存活（宿主退出 OS 自动回收）。
    // 【怎么改】新增管控语义（暂停/恢复/动态周期）在 GroupLoop 上加字段+对应静态口；
    //   锁纪律：组表读写只在 mGate 锁内，线程启动在锁外（锁内起线程可能死锁）；
    //   冲突检测消息含后果提示与修复指引（现场写错组时一眼看懂怎么改），改文案勿删提示。
    // =====================================================================================
    public static class FlowGroupRunner
    {
        /// <summary>
        /// 日志口（宿主项目注入；缺省 Debug 输出）。库形态唯一的环境差口：
        /// 宿主注入自己的日志门面让调度日志与业务日志同流；不注入也能跑（异常/启动
        /// 记录走 Debug 可见）。多线程并发调用，宿主注入的实现自行保证线程安全。
        /// </summary>
        public static Action<string> LogSink = delegate(string m) { Debug.WriteLine("[流组调度] " + m); };

        /// <summary>单个组的运行体：一圈动作+周期+线程+独占资源+心跳/异常统计（外部只经 StartGroup 创建）。</summary>
        private sealed class GroupLoop
        {
            public string Name;              // 组名（线程名与日志前缀，如"装配组"）
            public Action Round;             // 一圈动作（组内各站 Poll 调用，顺序=组内轮询序）
            public int PeriodMs;             // 每圈间隔
            public Thread Thread;            // 本组专属线程（命名后台线程）
            public volatile bool StopReq;    // 停止请求（StopAll 置位，循环每圈检查）
            public long LastRoundTick = Environment.TickCount;   // 最近一圈完成时刻（心跳）
            public long RoundCount;          // 累计圈数
            public long ErrCount;            // 累计异常次数
            public int ErrStreak;            // 当前连续异常轮数（恢复归零）
            public string[] Resources;       // 本组独占资源标签（已 Trim+去重，冲突检测只比存活组）
        }

        private static readonly string[] EmptyResources = new string[0];   // 无标签组的缺省（不上冲突检测）
        private static readonly object mGate = new object();       // 组表读写闸
        private static readonly List<GroupLoop> mGroups = new List<GroupLoop>();

        /// <summary>
        /// 启动一个处理组（同名组已存活则跳过并记日志——防重入）。
        /// 参数 round=一圈动作（组内各站按轮询序逐个调用，整体一个 try："单轮异常隔离"——
        /// 某站异常中断本圈同组后续站，下一圈继续）；periodMs=每圈间隔毫秒；
        /// exclusiveResources=本组独占硬件标签（可变参数，可省——省=不参与冲突检测）。
        /// 【冲突 fail-fast】同一资源标签出现在两个存活组→抛 InvalidOperationException：
        /// 同相机/同 ToolBlock/同读码器的站必须同组串行，拆两组=组间并发同时驱动同一硬件
        /// （帧序错乱/取错帧/数据串台）。已停组不参与比对（StopAll 后线程退出即自然释放，
        /// 可重新 StartGroup——重启/测试场景无需手工清表）。
        /// 线程=命名后台线程"流组-组名"，伴生进程存活。
        /// </summary>
        public static void StartGroup(string name, Action round, int periodMs, params string[] exclusiveResources)
        {
            if (string.IsNullOrEmpty(name)) name = "未命名组";
            if (round == null) throw new ArgumentNullException("round");
            if (periodMs < 10) periodMs = 10;   // 防手滑传 0/负数把 CPU 打满
            string[] res = NormalizeResources(exclusiveResources);
            GroupLoop g;
            lock (mGate)
            {
                // ① 防重入：同名组存活→跳过（先于冲突检测——同名重入带同样的标签不该误报冲突）。
                foreach (GroupLoop old in mGroups)
                {
                    if (old.Name != name) continue;
                    if (old.Thread != null && old.Thread.IsAlive)
                    {
                        LogSink("[流组调度] 组「" + name + "」已在运行，本次启动请求跳过（防重入）");
                        return;
                    }
                }
                // ② 独占资源冲突检测（只比存活组）：同资源跨组=启动即抛，fail-fast 不带病上线。
                for (int i = 0; i < mGroups.Count; i++)
                {
                    GroupLoop old = mGroups[i];
                    if (old.Thread == null || !old.Thread.IsAlive) continue;   // 已停组：资源已释放，不拦
                    if (res.Length == 0 || old.Resources.Length == 0) continue;
                    for (int a = 0; a < res.Length; a++)
                    {
                        for (int b = 0; b < old.Resources.Length; b++)
                        {
                            if (!string.Equals(res[a], old.Resources[b], StringComparison.OrdinalIgnoreCase)) continue;
                            throw new InvalidOperationException(
                                "处理组独占资源冲突：硬件「" + res[a] + "」同时被组「" + old.Name + "」（已在运行）与「"
                                + name + "」（本次启动）声明——同相机/同 ToolBlock/同读码器的站必须同组串行，"
                                + "拆进两个组=组间并发同时驱动同一硬件（帧序错乱/取错帧/数据串台）。"
                                + "请把两组合并成一个组，或改掉误标的资源标签（StartGroup 第 4 个参数，同串=同硬件）。"
                                + "【后果提示】本异常抛出时，已启动的组继续正常跑，排在冲突组之后的组没有启动、"
                                + "不会消费触发——那些站的触发会被超龄回执（DONE=3）跳站，表现为下游收全 0/NG、"
                                + "无存图；修正资源标签后重启软件才生效（防重入闸不会在重跑脚本时补启动缺失的组）。");
                        }
                    }
                }
                g = new GroupLoop { Name = name, Round = round, PeriodMs = periodMs, Resources = res };
                mGroups.Add(g);
            }
            g.Thread = new Thread(() => LoopBody(g))
            {
                IsBackground = true,
                Name = "流组-" + name,
            };
            g.Thread.Start();
            LogSink("[流组调度] 组「" + name + "」已启动（周期 " + periodMs + "ms，线程名 流组-" + name
                + (res.Length > 0 ? "，独占 " + string.Join(" ", res) : "") + "）");
        }

        /// <summary>
        /// 资源标签归一化：Trim 去空白 + 组内去重（同组重复标无害）+ 忽略大小写比对（比对在冲突检测处做，
        /// 这里只做清理）。全空/全 null → 返回空表（=不参与冲突检测，等同旧签名）。
        /// 忽略大小写的理由：资源标签指物理硬件，CAM3 与 cam3 必是同一台——宽松归一防手滑漏检；
        /// 故意用大小写区分两个不同硬件的场景不存在（硬件名不会只差大小写）。
        /// </summary>
        private static string[] NormalizeResources(string[] raw)
        {
            if (raw == null || raw.Length == 0) return EmptyResources;
            List<string> norm = new List<string>();
            for (int i = 0; i < raw.Length; i++)
            {
                string t = (raw[i] ?? "").Trim();
                if (t.Length == 0) continue;
                bool dup = false;
                for (int k = 0; k < norm.Count; k++)
                {
                    if (string.Equals(norm[k], t, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                }
                if (!dup) norm.Add(t);
            }
            return norm.Count > 0 ? norm.ToArray() : EmptyResources;
        }

        /// <summary>
        /// 组循环体：while+异常隔离+心跳。异常节流=首条+每 20 条记一条（防系统性故障刷爆日志）；
        /// 连续异常恢复不单独记（ErrStreak 归零即体现）。异常绝不冲出循环——单站/单圈故障
        /// 不拖垮其它组的轮询。
        /// </summary>
        private static void LoopBody(GroupLoop g)
        {
            while (!g.StopReq)
            {
                try
                {
                    g.Round();
                    if (g.ErrStreak > 0) g.ErrStreak = 0;
                }
                catch (Exception ex)
                {
                    g.ErrCount++;
                    g.ErrStreak++;
                    if (g.ErrStreak == 1 || g.ErrStreak % 20 == 0)
                        LogSink("[流组调度] 组「" + g.Name + "」圈异常第 " + g.ErrCount
                            + " 次（连续 " + g.ErrStreak + " 圈）：" + ex.Message);
                }
                g.RoundCount++;
                g.LastRoundTick = Environment.TickCount;
                Thread.Sleep(g.PeriodMs);
            }
        }

        /// <summary>
        /// 请求全部组停止（尽力 Join 500ms；宿主退出/脚本重跑场景收尾用）。
        /// 停止即自然释放本组资源标签（冲突检测只比存活组），无需手工清表。
        /// </summary>
        public static void StopAll()
        {
            lock (mGate)
            {
                foreach (GroupLoop g in mGroups)
                {
                    try
                    {
                        g.StopReq = true;
                        if (g.Thread != null && g.Thread.IsAlive && g.Thread.Join(500)) continue;
                    }
                    catch { /* 原因：停止是尽力而为，单组失败不影响其它组 */ }
                }
            }
        }

        /// <summary>
        /// 健康报告（一行一组）：存活/最近圈龄/总圈数/异常数/独占资源。给探针、诊断日志、
        /// 开发者面板用；"最近圈龄"异常变大=组内某站卡住（如仪器长流程）或线程死锁，是排查第一入口。
        /// </summary>
        public static string HealthReport()
        {
            StringBuilder sb = new StringBuilder();
            long now = Environment.TickCount;
            lock (mGate)
            {
                foreach (GroupLoop g in mGroups)
                {
                    bool alive = g.Thread != null && g.Thread.IsAlive;
                    sb.Append(alive ? "存活" : "已停").Append(' ')
                      .Append(g.Name).Append(" 最近圈龄 ").Append(now - g.LastRoundTick).Append("ms")
                      .Append(" 共 ").Append(g.RoundCount).Append(" 圈")
                      .Append(" 异常 ").Append(g.ErrCount).Append(" 次");
                    if (g.ErrStreak > 0) sb.Append("（连续 ").Append(g.ErrStreak).Append("）");
                    if (g.Resources.Length > 0) sb.Append(" 独占[").Append(string.Join(" ", g.Resources)).Append(']');
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }
    }
}
