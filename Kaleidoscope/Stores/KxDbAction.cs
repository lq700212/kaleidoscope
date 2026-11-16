using System;
using System.Data.SQLite;
using System.Threading;

namespace Kaleidoscope.Stores
{
    /// <summary>
    /// 一条写队列动作（KxSqliteStore 写线程的执行单元）。
    ///
    /// 【干什么】把"一段参数化 SQL 执行体"包装成可入队的动作对象。引擎对业务表零知识：
    /// 动作的 SQL 与参数全由宿主闭包捕获（Run 委托里想写哪张表、写几条语句都行），
    /// 引擎只负责队列调度、批量事务、独占通道与完成回执——业务壳用闭包捕获自己的
    /// 行 DTO，执行体里回写自己的结果字段，双方互不感知对方内部结构。
    ///
    /// 【为什么用委托而不是动作枚举】宿主的写入动作天然按业务语义切（建产品/记工位/
    /// 写帧/改点位……），枚举方案会让引擎跟着业务字段膨胀；委托方案下引擎只见
    /// "一段代码 + 一个完成句柄"，加业务动作=宿主加闭包，引擎零改动。
    ///
    /// 【怎么接】宿主构造：new KxDbAction { Name="工位明细", Run = cmd => { ...; return rows; },
    /// Solo=false, Done = null 或 new ManualResetEventSlim(false) }。
    /// Done 非 null 时引擎在任何路径（成功/失败/异常）都会 Set（调用方同步等待不死等）；
    /// Run 返回值仅在 Solo=true 时被引擎用于"真删了行才做空间回收"判定（见 KxSqliteStore）。
    /// </summary>
    public sealed class KxDbAction
    {
        /// <summary>动作名（单条失败日志/独占清理日志里标识这一条，如"工位明细"）。</summary>
        public string Name;

        /// <summary>
        /// 执行体：在写线程的命令对象上执行（事务已由引擎挂好，本方法不再自行开事务）。
        /// 返回"本次动作影响的行数"——仅 Solo 动作被引擎消费（&gt;0 触发增量 vacuum），
        /// 普通（批量）动作的返回值忽略。
        /// 【线程】只在唯一写线程上执行；抛异常由引擎单条隔离接住（留日志，不杀线程）。
        /// </summary>
        public Func<SQLiteCommand, int> Run;

        /// <summary>
        /// 是否走"独占事务"通道（对标老 SqliteRecordQue 的 CleanOld 语义）：
        /// 自建事务单跑、不与普通写入共享批量事务。适用大 DELETE——①共享事务体积暴涨
        /// 拖慢正常落库；②共享提交回滚会连累同批无辜写入；③同步等待方只等自己这一单。
        /// FIFO 顺序由引擎保证（批内遇到 Solo 先提交本批再独占执行）。
        /// </summary>
        public bool Solo;

        /// <summary>
        /// 完成回执句柄（可 null=调用方不同步等待）。引擎在执行体 finally 里 Set——
        /// 任何路径（含异常跳出）都通知，调用方 Wait(超时) 不会永久阻塞。
        /// </summary>
        public ManualResetEventSlim Done;

        /// <summary>
        /// 仅 Solo 动作有效：独占事务提交结果回调（committed=true=已提交成功；
        /// false=执行体抛异常或 Commit 失败已回滚）。用于修正"执行体回写了行数、
        /// 但事务最终回滚"的误报——对标老 SqliteRecordQue 独占清理防线里
        /// it.CleanResult = -1 的尽力修正语义（Done 先 Set，调用方读数存在竞态，
        /// 极端下仍可能读到旧值，属灾难路径尽力语义）。可 null=不在意。
        /// </summary>
        public Action<bool> OnCommitted;
    }
}
