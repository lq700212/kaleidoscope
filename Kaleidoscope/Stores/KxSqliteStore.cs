using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Text;
using System.Threading;

namespace Kaleidoscope.Stores
{
    /// <summary>
    /// KxSqliteStore —— SQLite 单写线程落库引擎（上位机记录类数据的通用底座）。
    ///
    /// 【干什么】
    ///   把"本地 SQLite 单文件库的可靠性骨架"做成引擎，业务只注入自己的表结构与
    ///   写动作，引擎统一消化全部脏活：单写线程、批量事务、掉电安全（WAL）、坏库自愈、
    ///   队列积压保险丝、独立短连接读、DbMeta 通用 kv、旧库补列迁移、增量空间回收。
    ///   业务壳（如 HJVision 的 SqliteRecordQue）保留全部业务 API 面，内部改走本引擎——
    ///   业务调用方零改动。
    ///
    /// 【为什么这么设计】（现场背景/踩过的坑，逻辑一比一对齐 HJVision SqliteRecordQue）
    ///   - 单写线程模型：所有写入经 ConcurrentQueue 由唯一后台线程串行执行，写连接
    ///     只有该线程持有；读走独立短连接（SQLite 多读单写互不阻塞）。检测线程等
    ///     实时线程绝不做磁盘 IO——全部"非阻塞入队"。
    ///   - 批量事务（上限 MaxBatchSize 条）：把"每条一次 fsync"摊薄成"每批一次"，
    ///     WAL 下机械盘单条事务百级 TPS、批量可达数千 TPS；掉电语义不退化（内存队列
    ///     本来就会丢，已提交批次完整）。
    ///   - "先 Reset 再消费"：防生产者在"消费完→Reset"间隙 Enqueue+Set 被吞信号。
    ///   - 提交防线：Commit 抛错（满盘/IO错）回滚整批，必须就地接住——抛出去会跳过
    ///     整轮 draining 直达外层 catch 后 WaitOne 空等，队列若还有超批尾巴且再无 Set，
    ///     数据无限期滞留。接住后记日志 + 补 Set 保活。
    ///   - 自愈只隔离"真损坏"（SQLite 报 Corrupt/NotADb，或文件头不是 SQLite 魔数），
    ///     坏库改名 .bad_时间戳 留证据重建（宁可丢历史也不瘫痪产线）；满盘/被占用/
    ///     权限等瞬时故障不动用户数据，直接返回失败。
    ///   - PRAGMA 顺序铁律（实测）：auto_vacuum=INCREMENTAL 必须在 journal_mode=WAL
    ///     之前——WAL 切换会初始化空库文件头，之后再设 auto_vacuum 即被锁死无效。
    ///   - Synchronous=Normal 走连接串而不是一次性 PRAGMA：synchronous 是
    ///     per-connection 设置，实测一次性 PRAGMA 跨连接读回 FULL，必须让 provider
    ///     每次 open 自动应用。
    ///   - 未就绪(IsReady=false)时写入安全降级 no-op，查询返回空，绝不抛异常——
    ///     调用方（产线主流程）绝不能因存储故障而崩。
    ///
    /// 【怎么接】
    ///   1) 构造：new KxSqliteStore(库路径, schema版本号, 建表SQL集合, 预热回调)——
    ///      建表 SQL 一律 CREATE TABLE IF NOT EXISTS（旧库升级无损）；预热回调在
    ///      连接已开、表已建、写线程未启动的时机调用（业务取号预热等），回调内抛异常
    ///      计入打开失败（走自愈判据，与原版预热在 TryOpen 内的语义一致）。
    ///   2) Register() 幂等启动（失败返回 false 留日志）；EnsureColumn 在预热回调里
    ///      做（写线程未启动、连接独占——探列+ALTER 重入无损）。
    ///   3) 写：Enqueue(new KxDbAction{...}) 非阻塞（保险丝丢弃返回 false）；
    ///      需要同步等结果的（清理/kv 写）给 KxDbAction.Done 传句柄 + Solo。
    ///   4) 读：ExecRead/ExecReadList 独立短连接（阻塞，BusyTimeout 5 秒）——
    ///      禁止 UI 线程调用，界面列表走后台线程 + 分页。
    ///   5) 退出：Close() 排干队列再关线程（幂等、可重入），数据不丢。
    ///   6) 桥接：Log 接宿主日志；IsMock 接宿主演示门（true 时 Enqueue 直接丢弃，
    ///      与保险丝丢弃同语义，演示数据不进生产库）。
    ///   部署三件套：System.Data.SQLite.dll + x64\SQLite.Interop.dll + x86\SQLite.Interop.dll
    ///   （managed DLL 引 libs 里的；native interop 按进程位数随 exe 目录 x64/x86 子目录部署）。
    /// </summary>
    public sealed class KxSqliteStore : IDisposable
    {
        // ------------------------------------------------------------------
        // 常量（可靠性参数，一比一对齐原版；调优前先读懂注释）
        // ------------------------------------------------------------------

        /// <summary>
        /// 写队列积压保险丝（条数）：写入失败（如磁盘满）时队列会无限堆积拖垮进程，
        /// 超过此上限直接丢弃新入队数据并限流记日志。
        /// </summary>
        public const int MaxQueBacklog = 10000;

        /// <summary>Close 排干队列的等待上限（毫秒）：正常队列为空时秒回，此处只防卡死。</summary>
        public const int CloseJoinTimeoutMs = 5000;

        /// <summary>
        /// 单事务批量合并上限（条）：写线程每轮最多把这么多条队列动作合进一个事务提交。
        /// 作用是把"每条一次 fsync"摊薄成"每批一次 fsync"。取 200 是折中：再大对吞吐
        /// 提升有限，掉电时未提交批次最多丢 200 条（内存队列掉电本来就全丢，语义不退化）。
        /// </summary>
        public const int MaxBatchSize = 200;

        // ------------------------------------------------------------------
        // 桥接（宿主注入）
        // ------------------------------------------------------------------

        /// <summary>日志出口（库默认走 Kaleidoscope.Utils.LogHelper；宿主可整体替换）。</summary>
        public Action<string> Log = Utils.LogHelper.Info;

        /// <summary>演示模式门（true 时全部写入动作入队前直接丢弃，生产库零演示行）。</summary>
        public Func<bool> IsMock = delegate { return false; };

        // ------------------------------------------------------------------
        // 单例状态
        // ------------------------------------------------------------------

        private readonly string _dbFilePath;                       // 库文件全路径
        private readonly int _schemaVersion;                       // 建表结构版本号（写入 DbMeta 供现场排查）
        private readonly IEnumerable<string> _createTableSqls;     // 建表 SQL 集合（IF NOT EXISTS）
        private readonly Action<KxSqliteStore> _afterPrepare;      // 预热回调（连接已开/表已建/写线程未启动）

        private readonly ConcurrentQueue<KxDbAction> _que = new ConcurrentQueue<KxDbAction>();
        private readonly ManualResetEvent _mre = new ManualResetEvent(false);
        private Thread _threadWorker;                              // 唯一写线程（Register 防重入保护）
        private volatile bool _mShut;                              // volatile：跨线程读写的停止标志
        private volatile bool _ready;                              // 库是否就绪（决定写入 API 生效或降级 no-op）

        private SQLiteConnection _writeConn;                       // 写连接：只有写线程使用，随 Register 打开、Close 释放
        private readonly object _stateLock = new object();         // 保护 Register/Close 状态迁移互斥

        /// <summary>记录被保险丝丢弃的条数（仅诊断用）。</summary>
        private long _droppedCount;

        /// <summary>构造引擎（只存参数，不碰磁盘；真正开库在 Register）。</summary>
        /// <param name="dbFilePath">库文件全路径（目录不存在时 Register 自动创建）。</param>
        /// <param name="schemaVersion">当前建表结构版本号（改表结构时 +1，写入 DbMeta 表便于现场排查）。</param>
        /// <param name="createTableSqls">建表 SQL 集合（CREATE TABLE/INDEX IF NOT EXISTS；旧库升级无损）。</param>
        /// <param name="afterPrepare">预热回调（可 null）：连接已开、表已建、写线程未启动的时机调用；
        /// 业务在此做取号预热/EnsureColumn 等。回调抛异常计入打开失败（走自愈判据）。</param>
        public KxSqliteStore(string dbFilePath, int schemaVersion,
            IEnumerable<string> createTableSqls, Action<KxSqliteStore> afterPrepare = null)
        {
            _dbFilePath = dbFilePath ?? throw new ArgumentNullException("dbFilePath");
            _schemaVersion = schemaVersion;
            _createTableSqls = createTableSqls ?? new string[0];
            _afterPrepare = afterPrepare;
        }

        // ------------------------------------------------------------------
        // 对外只读状态
        // ------------------------------------------------------------------

        /// <summary>库是否已就绪（Register 成功且线程运行中）。未就绪时写入 API 安全 no-op。</summary>
        public bool IsReady { get { return _ready; } }

        /// <summary>当前库文件全路径（构造传入）。</summary>
        public string DbFilePath { get { return _dbFilePath; } }

        /// <summary>
        /// 统一连接串（写连接与全部读短连接共用）：Synchronous=Normal（WAL 下与 FULL
        /// 同样保证事务原子性，每次提交少一次 fsync；必须走连接串让 provider 每次 open
        /// 自动应用——一次性 PRAGMA 只对当前连接有效、重连即丢）；BusyTimeout 防
        /// "database is locked"直接抛错。
        /// </summary>
        public string ConnString
        {
            get { return "Data Source=" + _dbFilePath + ";Version=3;BusyTimeout=5000;Synchronous=Normal;"; }
        }

        /// <summary>当前写队列积压条数（业务预热防撞用：队列非空说明有"已发号未落库"残留）。</summary>
        public int BacklogCount { get { return _que.Count; } }

        /// <summary>被保险丝丢弃的累计条数（仅诊断用）。</summary>
        public long DroppedCount { get { return Interlocked.Read(ref _droppedCount); } }

        // ==================================================================
        // 初始化 / 关闭（幂等状态机）
        // ==================================================================

        /// <summary>
        /// 启动存储服务（幂等）：建目录建库建表 + 启动唯一写线程。
        /// 已就绪时重复调用直接返回 true；Close 之后再次调用可重启（测试/热复位用）。
        /// 任何失败都不抛异常——返回 false 并留日志，调用方（启动链路）绝不能因存储故障而崩。
        /// </summary>
        public bool Register()
        {
            lock (_stateLock)
            {
                // 幂等：线程活着且未要求关闭，说明已在服务中
                if (_threadWorker != null && _threadWorker.IsAlive && !_mShut) return _ready;

                _mShut = false;

                // 打开/建库（含损坏自愈：坏库改名重建）
                if (!OpenAndPrepareDb())
                {
                    _ready = false;
                    Log(DateTime.Now + " SQLite存储启动失败，结果记录功能停用：" + _dbFilePath);
                    return false;
                }

                // 启动唯一写线程（前台线程：退出流程有显式 Close + 环境退出兜底）
                _threadWorker = new Thread(new ThreadStart(WriteThreadSub));
                _threadWorker.IsBackground = false;
                _threadWorker.Priority = ThreadPriority.BelowNormal;   // 落库优先级低于检测/通讯
                _threadWorker.Start();

                _ready = true;
                Log(DateTime.Now + " SQLite存储就绪：" + _dbFilePath);
                return true;
            }
        }

        /// <summary>
        /// 关闭存储服务（幂等、可重入）：停止接收新数据 → 排干写队列 → 关线程关连接。
        /// 必须先 Set 唤醒阻塞在 WaitOne 的写线程，否则前台线程永不退出、进程残留挂死
        /// （V4.3.x 血泪）。Close 返回即保证队列已排干（或超时留日志），数据不丢。
        /// 超时后队列残留有意不清空（新入队已被 _ready 关闸拦下，残留全是已发号未落库的
        /// 旧数据）：重 Register 时宿主预热取 max(内存,库)，残留号不会被复用，残留条目
        /// 由新写线程继续落库。
        /// </summary>
        public void Close()
        {
            lock (_stateLock)
            {
                _ready = false;          // 先关闸：后续写入 API 全部降级 no-op
                _mShut = true;
                try { _mre.Set(); } catch { }

                var worker = _threadWorker;
                if (worker != null && worker.IsAlive)
                {
                    // 排干等待：正常队列空时线程秒退；超时兜底不卡退出流程。
                    if (!worker.Join(CloseJoinTimeoutMs))
                        Log(DateTime.Now + " SQLite存储关闭超时(>" + CloseJoinTimeoutMs + "ms)，剩余数据可能未落库：" + _que.Count);
                }
                _threadWorker = null;

                try { if (_writeConn != null) { _writeConn.Dispose(); } } catch { }
                _writeConn = null;
            }
        }

        /// <summary>IDisposable 别名 Close（using/容器释放语义）。</summary>
        public void Dispose() { Close(); }

        // ==================================================================
        // 打开 + 自愈 + 建表 + 迁移
        // ==================================================================

        /// <summary>
        /// 打开连接 + PRAGMA + 建表 + 预热回调。含损坏自愈：
        /// 首次失败且文件存在 → 改名 .bad_时间戳 留证据 → 再重试全新建库。
        /// 只隔离"真损坏"（见 ShouldIsolateDbFile）；满盘/被占用/权限等瞬时故障
        /// 不动用户数据，直接返回失败。
        /// </summary>
        private bool OpenAndPrepareDb()
        {
            Exception firstEx = null;
            if (TryOpenAndPrepareDb(out firstEx)) return true;

            // 第二次尝试前先隔离疑似损坏的旧库文件（全新路径直接失败多为目录/权限问题，重试无意义）
            try
            {
                if (File.Exists(_dbFilePath) && ShouldIsolateDbFile(firstEx))
                {
                    string badPath = _dbFilePath + ".bad_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    File.Move(_dbFilePath, badPath);
                    Log(DateTime.Now + " SQLite库文件异常已隔离：" + badPath);
                    // 连带清理可能存在的 WAL 残留（-wal/-shm），避免新库被旧 WAL 污染
                    foreach (string ext in new[] { "-wal", "-shm" })
                    {
                        string p = _dbFilePath + ext;
                        if (File.Exists(p)) File.Delete(p);
                    }
                    Exception retryEx = null;
                    return TryOpenAndPrepareDb(out retryEx);
                }
            }
            catch (Exception exMove)
            {
                Log(DateTime.Now + " SQLite库文件隔离失败：" + exMove.Message);
            }
            return false;
        }

        /// <summary>
        /// 自愈判据：只有"库文件本身已损坏"才值得隔离重建。两条任一成立即隔离：
        /// ①首次异常链里有 SQLite Corrupt/NotADb；②文件头不是 SQLite 魔数
        /// （垃圾字节/空文件/半截文件）。文件头正常（满盘/占用/权限等瞬时故障）
        /// 一律不隔离——保住用户数据。
        /// </summary>
        private bool ShouldIsolateDbFile(Exception firstEx)
        {
            for (Exception e = firstEx; e != null; e = e.InnerException)
            {
                SQLiteException sq = e as SQLiteException;
                if (sq != null
                    && (sq.ResultCode == SQLiteErrorCode.Corrupt
                        || sq.ResultCode == SQLiteErrorCode.NotADb))
                    return true;
            }
            try
            {
                byte[] want = Encoding.ASCII.GetBytes("SQLite format 3\0");
                using (FileStream fs = new FileStream(
                    _dbFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length < 16) return true;
                    byte[] magic = new byte[16];
                    if (fs.Read(magic, 0, 16) < 16) return true;
                    for (int i = 0; i < 16; i++)
                        if (magic[i] != want[i]) return true;
                    return false;
                }
            }
            catch { return false; }   // 头都读不到（被独占锁定/无权限），宁可不隔离
        }

        private bool TryOpenAndPrepareDb(out Exception lastEx)
        {
            lastEx = null;
            try
            {
                string dir = Path.GetDirectoryName(_dbFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                // BusyTimeout：写锁被读占用时的等待毫秒数（防 "database is locked" 直接抛错）
                _writeConn = new SQLiteConnection(ConnString);
                _writeConn.Open();

                // 空间回收：新库开增量自动 vacuum，清理删出空闲页后用 incremental_vacuum(N)
                // 分批归还给操作系统。只对新建库生效，存量老库保持原模式、行为不变。
                // 顺序铁律（实测）：必须在 journal_mode=WAL 之前——WAL 切换会初始化
                // 空库文件头，之后再设 auto_vacuum 即被锁死无效（新库读回 0=NONE）。
                ExecNonQuery("PRAGMA auto_vacuum=INCREMENTAL;");

                // WAL（Write-Ahead Logging）模式：读不阻塞写、掉电恢复更安全。
                // 若个别文件系统不支持 WAL，SQLite 会自动回退默认模式，不会失败，故不校验返回值。
                // （journal_mode 持久化在库文件头，一次设置长期有效，读连接自动继承。）
                ExecNonQuery("PRAGMA journal_mode=WAL;");

                foreach (string sql in _createTableSqls) ExecNonQuery(sql);

                // DbMeta 引擎固有 kv 表 + 结构版本号（现场排查用；IF NOT EXISTS/INSERT OR REPLACE 重入无损）
                ExecNonQuery("CREATE TABLE IF NOT EXISTS DbMeta (Key TEXT PRIMARY KEY, Value TEXT NOT NULL)");
                ExecNonQuery("INSERT OR REPLACE INTO DbMeta(Key, Value) VALUES('SchemaVersion','" + _schemaVersion + "')");

                // 预热回调：连接已开、表已建、写线程未启动——业务做取号预热/EnsureColumn 等。
                // 回调异常计入打开失败（与原版预热在 TryOpen try 内的语义一致，走自愈判据）。
                if (_afterPrepare != null) _afterPrepare(this);

                return true;
            }
            catch (Exception ex)
            {
                lastEx = ex;   // 带给 ShouldIsolateDbFile 做"真损坏"判据
                Log(DateTime.Now + " SQLite打开/建库失败(" + _dbFilePath + ")：" + ex.Message);
                try { if (_writeConn != null) _writeConn.Dispose(); } catch { }
                _writeConn = null;
                return false;
            }
        }

        /// <summary>
        /// 旧库补列迁移：PRAGMA table_info 先探列再 ALTER（CREATE TABLE IF NOT EXISTS
        /// 对"已存在的旧表"不加新列，必须显式 ALTER）。重入无损（列已存在则跳过）。
        /// 只在预热回调里调（写连接已开、写线程未启动，连接独占期），不判空连接。
        /// </summary>
        /// <param name="table">表名</param>
        /// <param name="column">要补的列名</param>
        /// <param name="alterDdl">ALTER TABLE 语句（如 "ALTER TABLE T ADD COLUMN C TEXT NOT NULL DEFAULT ''"）</param>
        /// <returns>true=列已存在或补列成功；false=探列/补列失败（留日志）</returns>
        public bool EnsureColumn(string table, string column, string alterDdl)
        {
            if (_writeConn == null) return false;
            try
            {
                if (TableHasColumn(table, column)) return true;
                ExecNonQuery(alterDdl);
                return true;
            }
            catch (Exception ex)
            {
                Log(DateTime.Now + " SQLite补列失败(" + table + "." + column + ")：" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 探表列存在性：PRAGMA table_info 逐行比对列名（大小写不敏感，SQLite 列名
        /// 语义本就不分大小写）。只在写连接已开时调用，不判空。
        /// </summary>
        private bool TableHasColumn(string table, string column)
        {
            using (SQLiteCommand cmd = _writeConn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(" + table + ")";
                using (SQLiteDataReader dr = cmd.ExecuteReader())
                {
                    while (dr.Read())
                    {
                        // table_info 第 2 列(name) 即列名
                        if (!dr.IsDBNull(1) && string.Equals(dr.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            return false;
        }

        /// <summary>短命令执行（PRAGMA/建表/INSERT OR REPLACE 用，不进事务）。连接未开抛异常。</summary>
        public int ExecNonQuery(string sql)
        {
            using (SQLiteCommand cmd = _writeConn.CreateCommand())
            {
                cmd.CommandText = sql;
                return cmd.ExecuteNonQuery();
            }
        }

        // ==================================================================
        // 写入 API（业务线程调用；非阻塞、未就绪安全降级）
        // ==================================================================

        /// <summary>
        /// 入队一条写动作（非阻塞，唯一写线程异步落库）。含积压保险丝与限流日志。
        /// 未就绪/演示模式/队列超限一律直接丢弃返回 false（调用方按"未写入"分支走，
        /// 与保险丝丢弃同语义，绝不抛异常）。
        /// </summary>
        public bool Enqueue(KxDbAction action)
        {
            if (!_ready || action == null || action.Run == null) return false;
            if (IsMock()) return false;   // 演示数据不进生产库
            if (_que.Count >= MaxQueBacklog)
            {
                long n = Interlocked.Increment(ref _droppedCount);
                // 限流留痕：只记第 1 条和每 1000 条，防磁盘满场景日志风暴
                if (n == 1 || n % 1000 == 0)
                    Log(DateTime.Now + " SQLite写队列积压超限(" + MaxQueBacklog + ")，已累计丢弃 " + n + " 条记录！");
                return false;
            }
            _que.Enqueue(action);
            _mre.Set();                              // 通知写线程
            return true;
        }

        // ==================================================================
        // 写线程（唯一写者：串行执行队列中的全部落库动作）
        // ==================================================================

        private void WriteThreadSub()
        {
            while (true)
            {
                try
                {
                    _mre.WaitOne();                       // 阻塞等待信号
                    _mre.Reset();                         // 先 Reset 再消费（防信号被吞）

                    // 批量消费：每次把队列里已积压的条目（上限 MaxBatchSize）合进一个事务提交，
                    // fsync 从"每条一次"摊薄成"每批一次"，吞吐提升一个数量级；
                    // 单批提交失败只丢这一批并留日志，线程与后续批次不受影响。
                    KxDbAction it;
                    while (_que.TryDequeue(out it))
                    {
                        // 独占通道：Solo 自建事务单跑，不进下面的批量——大 DELETE 不与普通写入共享事务。
                        if (it.Solo)
                        {
                            ExecuteSoloGuarded(it);
                            continue;
                        }
                        using (SQLiteTransaction tran = _writeConn.BeginTransaction())
                        using (SQLiteCommand cmd = _writeConn.CreateCommand())
                        {
                            cmd.Transaction = tran;
                            int batchCount = 0;
                            bool pendingSolo = false;      // 批内截获的独占项（已出队、尚未执行）
                            do
                            {
                                // 内层：单条隔离——一条失败只留日志，绝不冲出循环杀线程；
                                // SQLite 单条语句失败不会中止整个事务，后续条目可继续执行。
                                try { RunAction(cmd, it); }
                                catch (Exception exItem)
                                {
                                    Log(DateTime.Now + " SQLite单条落库失败(" + SafeName(it) + ")：" + exItem.Message);
                                }
                                batchCount++;
                                if (batchCount >= MaxBatchSize) break;
                                if (!_que.TryDequeue(out it)) break;
                                // 批内遇到独占项：先提交本批再独占执行，FIFO 顺序不变
                                if (it.Solo) { pendingSolo = true; break; }
                            }
                            while (true);
                            // 提交防线：Commit 抛错（满盘/IO错）会回滚整批已出队条目，
                            // 此处必须就地接住——抛出去会跳过整轮 draining，直达外层 catch 后
                            // WaitOne 空等（队列若还有超批尾巴且再无 Set，数据无限期滞留）。
                            // 接住后记批量日志 + 补 Set 保活，循环自然继续排剩余队列。
                            try { tran.Commit(); }
                            catch (Exception exCommit)
                            {
                                Log(DateTime.Now + " SQLite批量提交失败(本批" + batchCount + "条已回滚，后续继续)：" + exCommit.Message);
                                try { _mre.Set(); } catch { }
                            }
                            if (pendingSolo) ExecuteSoloGuarded(it);
                        }
                    }

                    if (_mShut) break;                    // 关闭请求：此时队列已排干，可安全退出
                }
                catch (Exception exLoop)
                {
                    // 外层兜底：任何意外都不允许终止唯一写线程（前台线程未捕获异常=崩进程）
                    Log(DateTime.Now + " SQLite写线程循环异常：" + exLoop.Message);
                }
            }
            // 线程退出前释放写连接（Close 的 Join 等到这一步，之后才 Dispose——此处先自行释放，Close 的 Dispose 双保险判空）
            try { if (_writeConn != null) { _writeConn.Dispose(); _writeConn = null; } } catch { }
        }

        /// <summary>执行体调用 + 完成回执：Done 非 null 时任何路径（含异常）都 Set，调用方同步等待不死等。</summary>
        private void RunAction(SQLiteCommand cmd, KxDbAction it)
        {
            try
            {
                it.Run(cmd);
            }
            finally
            {
                if (it.Done != null)
                {
                    try { it.Done.Set(); } catch { }
                }
            }
        }

        /// <summary>日志安全取动作名（null 防呆）。</summary>
        private static string SafeName(KxDbAction it)
        {
            return string.IsNullOrEmpty(it.Name) ? "(未命名动作)" : it.Name;
        }

        /// <summary>
        /// 独占事务动作：Solo 的两次语义——①自建事务单跑（大 DELETE 不与普通写入共享
        /// 批量事务：共享事务体积暴涨拖慢落库/共享提交回滚连累无辜写入/等待方只等自己
        /// 这一单）；②真删了行（Run 返回 &gt;0 且提交成功）才做增量空间回收。
        /// FIFO 顺序由调用处保证（先提交本批再单跑）。
        /// 提交失败（执行体抛异常/Commit 抛错）时事务由 using 自动回滚，异常传出由
        /// ExecuteSoloGuarded 接住；OnCommitted(false) 修正宿主结果字段防误报。
        /// 【回执顺序】Done 在"提交结果回调（OnCommitted）执行完"之后才 Set——同步等待方
        /// 唤醒时读到的一定是最终结果（行数/提交状态），消除"唤醒在先、修正在后"的读数
        /// 竞态（老版 SqliteRecordQue 此处为尽力语义，本引擎把它收敛成确定性语义）。
        /// </summary>
        private void ExecuteSolo(KxDbAction it)
        {
            int rows = -1;
            bool committed = false;
            try
            {
                using (SQLiteTransaction tran = _writeConn.BeginTransaction())
                using (SQLiteCommand cmd = _writeConn.CreateCommand())
                {
                    cmd.Transaction = tran;
                    try
                    {
                        rows = it.Run(cmd);
                        tran.Commit();
                        committed = true;
                    }
                    finally
                    {
                        // 提交结果回调（失败=已回滚）：宿主据此把"执行体回写的行数"修正为失败，
                        // 防"报删了 N 行实际已回滚"的误报（本回调先于 Done.Set 执行）
                        if (it.OnCommitted != null) { try { it.OnCommitted(committed); } catch { } }
                    }
                    // 真删了行才做空间回收（rows 是 DELETE 的行数；-1=失败/回滚）
                    if (committed && rows > 0)
                    {
                        try { VacuumFreelist(); }
                        catch (Exception exVac) { Log(DateTime.Now + " SQLite空间回收失败(不影响清理结果)：" + exVac.Message); }
                    }
                }
            }
            finally
            {
                // 完成回执任何路径都 Set（含执行体异常/Commit 失败），调用方 Wait(超时) 不死等；
                // Set 时机=最终结果已定（OnCommitted 已跑完），等待方读数无竞态
                if (it.Done != null) { try { it.Done.Set(); } catch { } }
            }
        }

        /// <summary>独占动作的防线外壳：失败只记日志，绝不抛到写线程外层循环（防 WaitOne 空等）。</summary>
        private void ExecuteSoloGuarded(KxDbAction it)
        {
            try { ExecuteSolo(it); }
            catch (Exception ex)
            {
                // 提交失败时 Done 已 Set（RunActionWithResult.finally），调用方不会死等
                Log(DateTime.Now + " SQLite独占动作失败(" + SafeName(it) + ")：" + ex.Message);
            }
        }

        /// <summary>
        /// 空间回收：把清理刚删出的空闲页分批归还操作系统。
        /// 上限 2000 页（默认页 4KB ≈ 8MB/次）：有界、快、不长期占写锁；
        /// 存量老库 auto_vacuum=NONE 时此 pragma 是无害空操作，新库才真回收。
        /// 全量 VACUUM 故意不用——它重写整个文件，写线程停顿数秒，风险大于收益；
        /// 日常删/插本来就会复用空闲页，文件稳定在保留窗口水位，不会无限涨。
        /// </summary>
        private void VacuumFreelist()
        {
            using (SQLiteCommand cmd = _writeConn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA incremental_vacuum(2000);";
                cmd.ExecuteNonQuery();
            }
        }

        // ==================================================================
        // 查询 API（独立短连接：与写线程互不阻塞；异常返回空，不抛出）
        // 线程告诫：全部同步阻塞，写锁忙等上限 5 秒（BusyTimeout）——
        // 禁止在 UI 线程调用，界面列表走后台线程 + 分页，
        // 否则大清理期间 UI 最长卡 5 秒。
        // ==================================================================

        /// <summary>
        /// 通用只读执行器：独立短连接 + 全量 try-catch（查询失败留日志返回，不抛出，
        /// 不拖垮调用方）。给 COUNT/明细表/聚合等一切读查询复用的短连接模式。
        /// </summary>
        public void ExecRead(Action<SQLiteCommand> run)
        {
            try
            {
                using (SQLiteConnection conn = new SQLiteConnection(ConnString))
                {
                    conn.Open();
                    using (SQLiteCommand cmd = conn.CreateCommand())
                    {
                        run(cmd);
                    }
                }
            }
            catch (Exception ex)
            {
                Log(DateTime.Now + " SQLite查询失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 带返回值的只读执行器：ExecRead 的泛型版（行映射由 run 委托自己完成）。
        /// 查询失败/未就绪返回空集合（run 内异常同样被接住），不抛出。
        /// </summary>
        public List<T> ExecReadList<T>(Func<SQLiteCommand, List<T>> run)
        {
            List<T> list = new List<T>();
            try
            {
                using (SQLiteConnection conn = new SQLiteConnection(ConnString))
                {
                    conn.Open();
                    using (SQLiteCommand cmd = conn.CreateCommand())
                    {
                        List<T> r = run(cmd);
                        if (r != null) return r;
                    }
                }
            }
            catch (Exception ex)
            {
                Log(DateTime.Now + " SQLite查询失败：" + ex.Message);
            }
            return list;
        }

        // ==================================================================
        // DbMeta 通用 kv（引擎固有）：低频元数据的公开读写口
        // ==================================================================

        /// <summary>
        /// 写一条 DbMeta 通用 kv（水位数/安装标记等低频元数据）。
        /// 【线程模型】写走唯一写线程（入队 + ManualResetEventSlim 回执同步等待）——
        /// 调用线程会阻塞至写完（上限 3 秒），超时/未就绪返回 false。
        /// 水位类低频调用（启动+每小时一次）允许在 UI 线程等，勿拿本口写高频数据。
        /// 【回读自检】写线程内同连接 SELECT 回读，不一致（满盘/只读文件系统）返回 false。
        /// </summary>
        public bool SetMeta(string key, string value)
        {
            if (string.IsNullOrEmpty(key) || !_ready) return false;

            bool result = false;   // 写线程执行成功才回写 true
            var done = new ManualResetEventSlim(false);
            var action = new KxDbAction
            {
                Name = "SetMeta(" + key + ")",
                Solo = false,
                Done = done,
                Run = delegate(SQLiteCommand cmd)
                {
                    // INSERT OR REPLACE 幂等覆盖；写后同连接回读自检
                    cmd.CommandText = "INSERT OR REPLACE INTO DbMeta(Key, Value) VALUES(@k, @v)";
                    cmd.Parameters.AddWithValue("@k", key);
                    cmd.Parameters.AddWithValue("@v", value ?? "");
                    cmd.ExecuteNonQuery();
                    cmd.Parameters.Clear();
                    cmd.CommandText = "SELECT Value FROM DbMeta WHERE Key=@k";
                    cmd.Parameters.AddWithValue("@k", key);
                    object back = cmd.ExecuteScalar();
                    result = Convert.ToString(back) == (value ?? "");
                    return 1;
                },
            };
            if (!Enqueue(action)) return false;   // Mock 演示丢弃 / 队列积压保险丝丢弃

            if (!done.Wait(3000))
            {
                Log(DateTime.Now + " SQLite SetMeta 等待超时(>3000ms)，键=" + key);
                return false;
            }
            return result;
        }

        /// <summary>
        /// 读一条 DbMeta 通用 kv。短连接直查（与写线程互不干扰，WAL 模式）；
        /// 键不存在/未就绪/异常一律返回 null（调用方按"该存储无值"处理）。
        /// </summary>
        public string GetMeta(string key)
        {
            if (string.IsNullOrEmpty(key) || !_ready) return null;
            string result = null;
            try
            {
                using (SQLiteConnection conn = new SQLiteConnection(ConnString))
                {
                    conn.Open();
                    using (SQLiteCommand cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT Value FROM DbMeta WHERE Key=@k";
                        cmd.Parameters.AddWithValue("@k", key);
                        object v = cmd.ExecuteScalar();
                        if (v != null) result = Convert.ToString(v);
                    }
                }
            }
            catch (Exception ex)
            {
                // 查询失败按"无值"处理（水位类调用方多有另两处兜底），不拖垮调用方
                Log(DateTime.Now + " SQLite GetMeta 失败(键=" + key + ")：" + ex.Message);
            }
            return result;
        }
    }
}
