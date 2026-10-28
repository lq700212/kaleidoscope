# 版本改动记录

> **本文件自 V1.0.0（Kaleidoscope 库抽取之日）起记，只记录 Kaleidoscope 仓库自身的改动。**
> 更早的 V2.14.26 及以前版本均为 CommandCenter 原项目的历史记录（窗口徽标、点位配置、
> 产品型号弹窗等界面功能，与 Kaleidoscope 库代码无关），**不随本仓库迁移**；其中通讯相关的
> 血泪背景（PLC 从站释放 V2.14.23、相机判定即写 V2.13.7、PW 同程序号跳过 V2.14.19、
> 存图清理防误删 V2.14.12 等）已沉淀在 `AGENTS.md`「已知通讯关键点」（位于仓库根），需要
> 原始完整记录时查原 CommandCenter 项目的 CHANGELOG.md。

## V1.8.3（2026-10-28）IO 耦合器原始读输入寄存器方法（宿主 PlcMesBridge 反哺）

> 宿主 PlcMesBridge 现场（苏州毅嘉 Audi cover module 固化程控）IO 表是**块状
> 布局**：10 块扩展、每块 0x20=32 个寄存器地址（16DI 占 0x10 + 16DO 占 0x10），
> 块间地址**不连续**——用库内连续读会按 16DI 依序打包假设拿错位数据。为宿主
> "逐块精确读 1 个 DI 寄存器"补原始方法（FC04 任意地址），与既有
> `ReadHoldingRegisters`（FC03 任意地址）对称，不改变既有行为。

### 改动

- `ModbusTcpIoController.ReadInputRegisters(ushort startAddress, ushort count)`：
  读输入寄存器（FC04）原始方法——与 `ReadHoldingRegisters` 同款实现路径
  （`_syncRoot` 锁串行化、失败 `MarkDisconnectedOnFailure` 断连标记 +
  OnError 边沿、未连接/未配置直接回 null）。业务用它按块地址精确读取
  （如 Audi 布局 DI@0x1000+(K-1)×0x20、DO@0x1010+(K-1)×0x20，
  每块 16DI+16DO 各占 0x10）。

### 验证与提交顺序（红线，本版即执行）

- 子模块 MSBuild 编译 0 error（net472）；宿主 PlcMesBridge 全量测试
  156/156 PASS（宿主 `IoBusTests`/`PointTableTests` 直接消费本方法链）。
- **提交顺序（红线，本版即执行）**：本改动随 kaleidoscope V1.8.3 先行
  commit/push（子模块仓库 main）；宿主主仓随后 `git add extern/kaleidoscope`
  （更新指针）并 commit/push——宿主引用的是 `bin\Debug` 产物，指针与源码
  必须保持对应。

## V1.8.2（2026-10-27）PLC 连接态记账与查询 + 五处半开连接根治（宿主现场反哺）

> 宿主 HJVision-WPF 现场两轮血泪：① 主窗/测试页 PLC 恒判"未连接"② 欧姆龙 FINS 会话槽
> 被半开连接占死。根因与修法都沉淀在库侧，宿主只做查询消费。

### 改法（真相源集中 s_lastReconnOk 记账 + 失败必关 socket）

- **`IsPlcConnectedAt(index)` 静态查询**：真相源=`s_lastReconnOk` 记账（`s_reconnLock` 保护）。
  为什么需要查询而不只靠事件：首连成功按原设计不发事件（"启动组终算已刷过 UI"的
  WinForms 口径），事件订阅方若晚于首连才订阅（如测试页打开时机）会错过初值——字典无
  记录时必须能问库实时态，否则恒判未连接（主窗灯绿、测试页红）。断线瞬间的短窗口
  （TCP 已死、首次重连失败未记账）可能短暂报 true，与原版状态灯滞后同口径；翻红由
  重连失败记账保证，不额外加探活 IO。Mock 开启恒 true。
- **首连成败记账**（`ConnPlcDevAt` 尾）：原来首连成功无人记 `s_lastReconnOk=true`（设计假设
  "启动组终算已刷过 UI"）。成败都记：成功 true（后续读通不再重复触发"恢复"边沿，保持
  日常零事件收敛），失败 false（与既有失败记账同口径）。
- **失败必关 socket（三处同口径）**：`ConnPlcDevAt` 首连失败分支 / 重连失败点 /
  `AddPlcDev` 建连失败分支——`ConnectServer` 失败（FINS 握手超时等）时 TCP 可能已建连，
  不关就留一个半开连接挂在 PLC 会话槽上。欧姆龙 FINS/TCP 会话槽有限，反复失败重试
  （启动/热更每轮 2 连试）把槽占满后，PLC 对一切新连接的握手一律不响应：能 ping 通、
  TCP 能连、FINS 通讯全断，只有重启 PLC 才能救。`ConnectClose` 幂等无害；对象本身仍留
  槽（重连链 `RunReconnect` 按 `ConnectClose+ConnectServer` 复用，不换引用）。
- **退出逐槽主动断开**（Dispose）：进程退出 OS 兜底会关 socket，但主动关时序确定——
  尤其 FINS/Modbus 这类对端有会话槽上限的设备，process 退出时对端未感知 FIN 会滞留
  连接占死 PLC 侧会话，下次启动明明 PLC 正常却永远连不上。逐槽 `(as DeviceTcpNet)?.ConnectClose()`。

### 验证与提交顺序

- 库随宿主主工程级联构建 0 error；宿主 14 批回归全 PASS；真机热更日志恒
  "保留/连上 1/1 台"零断线。
- **提交顺序（红线，本版即执行）**：本改动随 kaleidoscope V1.8.2 先行 commit/push，
  宿主主仓再记录子模块新指针推主仓——主仓先推会把别人 fetch 不到的子模块指针发上去
  （宿主 AGENTS 子模块提交顺序红线）。

## V1.8.1（2026-10-26）CodeReaderClient 连接失败/对端断开零异常化

### 改法（失败一律走状态边沿，状态机/重连节律/调用方判定全部不变）

- **背景**：宿主 HJVision-WPF 调试启动时设备不在线，读码器每槽连接超时都会从
  `ConnectAsync` 抛 `TimeoutException`（含取消 OCE），或 Socket 失败 re-throw；调用方
  `Wait()` 再转 `AggregateException`，自动重连循环每 backoff 一轮再抛一轮——VS 调试器对
  每个 first-chance 异常逐条通知，启动调试输出整片"引发的异常"刷屏、且被放大成可感启动
  卡顿。**「预期的失败路径用异常做控制流」在调试器下是噪声放大器，改为零异常等价通道。**
- ① `ConnectAsync`：连接超时/取消（原 `throw new TimeoutException|OperationCanceledException`）
  与 Socket 失败（原 catch 内 `throw;` re-throw）两条路统一改为
  `SetState(ReaderState.Error, "连接失败：" + 原文案) + EnsureReconnectLoop() + return`
  ——Error 文案与原异常 Message 一字不差（含 OCE 默认消息"操作已取消。"）；
- ② `ReconnectLoopAsync`：配套去掉 `catch` 重试体，改 `IsConnected` 条件轮询，
  backoff 2s→30s 指数退避节律原样保留；
- ③ `ReceiveLoopAsync`：`n==0`（对端正常关闭，常态断连路径）不再模拟
  `throw new SocketException(ConnectionReset)`，改 `break`（补原等价日志文案，走
  finally `OnConnectionLost` 同终点）——真实 RST/IO 异常路径仍走原 catch 通道；
- ④ `SendRawAndWaitAsync` 的指令等待超时（`tcs.TrySetException`）**保留不改**：那是
  运行期协议层反馈（已连接半死状态下发指令收不到回包），异常文案是准确失败信息
  （"等待回包超时（指令：X，…ms）"），且调用方（AcquireSingleAsync/SetAcqAsync）
  统一转 `Fail 结果`，量低频不刷屏——再动它要动公共 API 语义，一比一红线不划算。

### 验证与提交顺序

- 库 csproj 构建 0 error；宿主 12 批回归全 PASS（W9 读码器批覆盖）；宿主冒烟
  装配期读码器失败日志以状态边沿出口正常落盘、无异常刷屏。
- **提交顺序（红线，本版即执行）**：本改动随 kaleidoscope V1.8.1 先行 commit/push，
  宿主主仓 V0.9.24 再记录子模块新指针推主仓——主仓先推会把别人 fetch 不到的
  子模块指针发上去（宿主 AGENTS 子模块提交顺序红线）。

## V1.8.0（2026-10-25）自 HJVision 生产工程下沉仪器设备全家 + 读码器统一抽象

> 第二轮「生产工程反哺」：把 HJVision 的九类仪器设备通讯服务**原样下沉**进库
> （耐压/气密/震动盘/激光打码/光源/IDMVS 读码器 + 设备服务基类 + 两件连带工具件），
> 出口替换口径与 V1.7.0 完全一致（MockMode→MockBridge、FlashLogger→LogHelper），
> 主工程照旧「类型别名壳 + 调用点零改动」。同时按 HJVision 侧明确的三点决策之三，
> 新增 **ICodeReader 统一读码抽象**：IDMVS（触发式）与库内基恩士 SR 家族（被动事件流）
> 归一到同一个「一次调用换一次读码结果」的接口，换读码设备不改业务代码。

### 新增（自 HJVision 下沉，行为一字不差）

- **`Services/DeviceServiceBase.cs`**：设备服务基类（volatile 停止旗 / LogSink 日志注入 /
  时间节流日志 / 限时 Join / MockOk 演示短路 / 泛型配置层）。MockOk 内部改走
  MockBridge.IsEnabled，宿主注入后演示模式自动生效。
- **`Services/HipottestService.cs`**：耐压绝缘/微电阻测试服务（固纬 GPT-9500 + GOM-80x，
  RS-232 SCPI：GPT 完成边沿状态机 + COMP? 二道确认、GOM 指纹变化判批、判定码全表、
  高压哨兵、Mock 演示批、ReadBatchOnce 按需读、经现有连接测试）。
- **`Services/CodeReaderClient.cs`**：IDMVS 读码器 TCP 客户端（三命令协议、换行切帧 +
  80ms 静默兜底、指令串行、单次/持续双触发模式、指数退避自动重连、SynchronizationContext
  事件回投、ReaderFrameParser 纯函数切帧器）。
- **`Services/LedControl.cs`**：瑞视串口光源驱动（S+通道字母+4 位亮度+# 协议，六通道）。
- **`Services/AirtightService.cs`**：气密测试服务（IO 经 PLC / Modbus 直连希立双分支，
  结果邮箱 + 序号认领、独立/统一启动、凑批等待、上位机启动检查握手、完成信号延时通知
  队列、双四通道"先 1 后 2"完成边沿、泄漏状态、实时快照；PLC 分支走静态委托注入）。
- **`Services/VibFeederController.cs`**：柔性供料震动盘控制器（以太网 MODBUS TCP 同步版，
  两轮重连、断连自愈 + 5 秒冷却、纯时长制补料 RunActionOnce、停必验证 StopVerified、
  寄存器表全覆盖；日志的 DateTime.Now 前缀去掉——宿主日志自带时间戳，正文未动）。
- **`Services/LaserMarkService.cs`**：激光打码机通讯服务（TCP 监听/串口多口双传输、
  三工作模式、完成信号关键词+后缀判定、串口名动态识别 + 缓存、MarkDoneHub 静态中继）。
- **`Services/MarkContentHub.cs`**：打码内容交接中心（来源三选一/无货策略三选一，随
  LaserMarkService 连带下沉）。
- **`Utils/SerialPortNameResolver.cs`**：串口名动态识别器（缓存→配置→WMI 关键词→全口
  兜底四层候选组装，随 LaserMarkService 连带下沉，与 SerialPortHelper 同层）。

### 新增（本轮新设计）

- **`Services/ICodeReader.cs`**：统一读码抽象（接口 + CodeReadRequest 请求参数）。
  语义是业务视角的「现在给我读一次码」：IDMVS 与基恩士 SR 两族对上层暴露的同一个
  业务动作。返回值复用 IDMVS 侧既有 ReaderAcquireResult（Success/Code/RawFrame/IsNoRead/Error）。
- **`Services/KeyenceScannerReader.cs`**：基恩士 SR 家族（IScanner 被动事件流）→
  ICodeReader 适配器。SendTrigger 后等下一条 SerialNumberScanned 或超时；单飞行者口径
  与 IDMVS 一致；只订阅事件不拥有连接（枪的 Open/Dispose 归宿主管）。
- **CodeReaderClient 实现 ICodeReader**（追加实现，既有公共面一行未动）：
  ConnectionChanged(bool) 由四态 StateChanged 边沿去重转发；AcquireAsync 按请求参数
  分流到既有 AcquireSingleAsync/AcquireContinuousAsync。

### 依赖

- 新增 libs/NModbus4.dll（命名空间 `Modbus.Device`，与既有 NModbus 3.0.83 的
  `NModbus` 命名空间不同、可并存）：VibFeederController 原工程即引 NModbus4
  （ModbusIpMaster.CreateIp），按「原样下沉」红线不改协议层代码，把该 DLL 随迁入库。

## V1.7.0（2026-10-25）自 HJVision 生产工程下沉 PLC/TCP 通讯队列

> 首个「生产工程反哺」版本：华际视觉检测系统（HJVision-WPF）把现场多轮迭代验证过的
> PLC/通用 TCP 通讯队列与裸 TCP 通道**原样下沉**进库（行为与原实现一字不差，仅
> FlashLogger→LogHelper、MockMode→MockBridge 两类出口替换），主工程经 ProjectReference
> + 文件级 using 别名**调用点零改动**接入。库从此补上「高频、多来源、要求严格顺序」的
> **队列范式**——与既有 HslPlcClient 的锁范式互补（队列适合生产节拍读写，锁适合配置期
> 低频操作），PlcNetCommQue 文件头有两者取舍的完整论述。

### 新增

- **`Services/PlcNetCommQue.cs`**：多品牌 PLC（HSL 三菱 MC/A1E、欧姆龙 FINS/CIP、西门子
  S7、汇川 H5U、自由口）+ 裸 TCP/EIP 外发**统一读写队列**——常驻消费线程按到达顺序串行
  执行（天然规避多线程共用一条 PLC 连接的竞态）、读请求结果槽回填 + 句柄唤醒（发起方
  永不死等）、读写失败即断连标记、去重 + 5 秒退避重连、心跳真探针旁路退避（防活锁）、
  热更 Rebuild 保槽位、脚本同步读写 API（PlcSyncRead*/Write* 全家族）。
- **`Services/TcpChannel.cs`**：通用 TCP / EtherNet-IP 双模式通道（自 HJVision 的
  TCP_Client 改名下沉）——裸 Socket 按行切帧（CR/LF/CRLF 全兼容）/ EIP 走
  AllenBradleyNet 周期读 Assembly、收发断线三事件、BeginConnect 强制超时、
  **禁自重连红线**（断线只抛事件，重连策略交上层统一管，防多业务入口各自重连打满对端）。
- **`Utils/MockBridge.cs`**：宿主演示模式桥——库内短路判断不再依赖任何业务工程的
  Mock 实现，宿主注入 IsEnabled/FakePlcRead/FakePlcWrite 三个委托；未注入恒为
  「未启用」= 真实通讯。配套日志桥走既有 `LogHelper.LogAction`（HJVision 侧已接
  FlashLogger，库日志与主程序并成一条流）。

### 接入（HJVision-WPF 侧，供其它工程抄）

- 引用方式：SDK 风格主工程直接 `ProjectReference` 本库 csproj（断点可跨工程打进库源码），
  库 DLL 随主工程落盘；
- 旧名兼容：主工程在原命名空间留「文件级 using 别名」壳文件（如
  `using PlcNetCommQue = Kaleidoscope.Services.PlcNetCommQue;`），全项目几十处调用点
  一字不改；
- 桥接线：宿主在自身 MockMode/日志类的静态构造里把三委托/LogAction 接上，任何进程
  （主程序/回归探针/测试宿主）碰到宿主类即自动完成接线。

## V1.6.2（2026-09-10）全面测试 54→152 例 + 修四处真实缺陷

> 全面代码审查 + 测试盲区清零：验收用例从 54 例扩到 152 例（新增 16 组 98 例，
> 覆盖校验器全分支/序列化边角/描述符/映射解析/各服务失败路径与释放语义）。
> 首轮新用例即抓出 3 处库真实缺陷 + 1 处校验器崩溃，全部已修并回归全绿。
> 另补 `.gitignore` 运行期缓存规则（送风机/气压表 IP 记忆文件）。

### 修复（均为新用例抓出的真实问题）

- **`DeviceHubConfigValidator` 相机 null 项崩溃**：`ValidateCameras` 先取 `cam.Name`
  组 label 再判 null——`Cameras` 含 null 项时抛 NullReferenceException，
  校验器本身崩溃。改为先判空返回错误再组装 label。
- **`DeviceHubConfigValidator` 从站地址重叠判死自己**：重叠检测用 Dictionary
  初始化器装四个地址——两地址相等时初始化器抛"相同键"异常，本想告警重叠
  反而崩溃。改为先装成对列表再逐个判重（0=预留跳过语义不变）。
- **`ConnectionMonitor.Dispose` 非幂等**：重复释放时 `_timer.Change` 抛
  "无法访问已释放的对象"。补 `_disposed` 早退（与全库"重复 Dispose 安全"约定对齐）。
- **Dispose 后连接复活（与 HslPlcClient 契约对齐）**：`ModbusTcpMasterClient`
  的 `EnsureConnected`/`ReconnectNow`/`Connect` 缺 `_disposed` 拦截——Dispose 后
  一次读写即悄悄重连成功（HSL 侧 V1.6.0 已拦截，行为不一致）；`FanControllerClient`/
  `ModbusRtuBarometerReader`/`ModbusTcpIoController` 的 `Connect` 与
  `ScannerService.Open` 同理补拦截。DeviceHub 热更只建新实例，无复用旧实例
  流程，不受影响。
- **测试侧**：`T17_ModelMap` 曾误删 `EnsureConnected` 致主站被拒（已补回）；
  新增 `NModbusMasterRetry` 覆盖从站监听线程启动的毫秒级 accept 窗口。

### 测试（16 组 98 例，详见技能 SKILL.md 用例清单）

- 配扩 8（缺文件/空文本/未知成员/中文无 BOM/反斜杠 u/深层兜底/类型错配）、
  校验 14（校验器此前仅多品牌 3 例，现全段全分支）、描述 4、映射 8
  （ParseAll/八进制/预留/异常）、从扩 5、主扩 6、IO扩 5、风扩 5、枪扩 6、
  相扩 8（含详细格式/BR 读图，模拟器加 `_camPwFail/_camPrBad/_camBrBad` 开关）、
  图扩 8（含脏 SubDirs/清理日期与 KeepDays=0/盘符根护栏）、HSL扩 5
  （品牌端口表/全类型失败路径）、Hub扩 5（三角色装配）、监控 3（空安全/边沿）、
  表Mock 4、工具 4。
- 无真机仍盲区（技能已知边界）：串口协议层、EIP CIP 协议去重（仅契约）、相机 FTP 链路。

### 文档同步

- `AGENTS.md`：测试基线 54→152；`.gitignore` 补 `FanLastIp.cache`/
  `BarometerPort.cache`（`git check-ignore` 三文件均命中）。
- `README.md` 仓库结构测试行；技能 `SKILL.md` 用例清单/基线/模拟器说明；
  `run-smoke.ps1` 概要行；`SmokeTest.cs` 头注释。
- 验证：MSBuild 三工程（库/Demo/编辑器）通过；`PASS=152 FAIL=0 SKIP=0`。

## V1.6.1（2026-09-05）ui-layout-debug 并入全局 winforms-ui-debug

- 删除 `.opencode/skill/ui-layout-debug/`（矩形枚举与真实截图方法论 + 四脚本原型已
  合并进全局技能 `winforms-ui-debug` §十与 `scripts/`，业务默认值已参数化）；
  `AGENTS.md` 脚本路径引用与技能说明改指全局版本；`README.md` 目录树同步删除该行。
- **为什么这么改**：四项目 UI 调试 skill 合并为同一个全局版本，防分叉；本项目构建
  命令与对照窗体已收录进全局 skill 附录 A 项目档案。
- 验证：无代码改动；全局 skill 内四脚本 `py_compile`/BOM/参数三态已验证。
- 附带机制：AGENTS 加"改构建输出同步改全局 skill 附录 A"一行（防表漂移）。

## V1.6.0（2026-08-24）集成 HslCommunication：多品牌 PLC + EtherNet/IP 扫码枪

### 新增

- **集成商业通讯库 HslCommunication（12.6.0.0）供选择**（`libs/HslCommunication.dll` 本地引用，
  离线可编译；**商业授权，授权码需自行购买**）：
  - **多品牌 PLC 主站**：`PlcRole.MultiBrand` + `HslPlcClient`——三菱 MC/A1E、欧姆龙 FINS/CIP、
    西门子 S7、汇川 H5U；一个 `IReadWriteNet` 接口吃掉所有品牌（新增品牌只加工厂 case），
    地址填品牌语法（三菱 D100/欧姆龙 W10/西门子 DB1.DBW0），端口 0=按品牌默认；
    锁串行化 + 断连边沿 + 节流重连 + 自带心跳自愈 + 可选轮询项（与 Modbus 主站事件同构）；
  - **EtherNet/IP 扫码枪**：`ScanConfig.Mode="Eip"` + `ScannerEipService`——上位机周期读
    Assembly 取条码，无需触发指令；保持型数据自动去重（码变化或出现过空才上报，
    读到空清缓存，"同码→空→同码"可再次触发）。
- **授权使用**：授权码放 exe 同目录 `hsl.dat` 文件（UTF-8，`#` 开头为注释行，码单独一行，
  文件已被 .gitignore 忽略）；未配置时配置编辑器切到多品牌 PLC / EIP 扫码枪节点会弹窗
  引导输入，验签通过自动保存；运行中放入文件无需重启自动生效。未授权不阻断通讯（受限流）。
  授权注册统一在 `Utils/HslAuthorization.cs`（加载/验签/落盘），服务层连接前幂等调用。
- **ConfigEditor**：设备树 PLC 分组重组（ModBus-TCP → 主站/从站；胡工科技通讯库 → 主站），
  节点悬停 ToolTip 补充协议说明；七个多品牌品牌预设 + EIP 扫码枪预设；HSL 授权状态区
  与输入弹窗（`HslAuthDialog`）；Demo 新增多品牌 PLC 直连测试区。
- **调试工具固化为项目专属技能**：`.opencode/skill/ui-layout-debug/`（子窗口矩形枚举、
  置顶真实截图，Python/PowerShell 双版本）。

### 修复

- **ConfigEditor 布局**：主窗体/rightPanel 的 Dock 添加顺序（Fill 必须最先 Add）——
  原顺序下 split 占满全客户区，树/属性网格被状态条静默遮挡 30px，预设条控件只露顶边；
  预设条/工具条/状态条/rightPanel 换原生 Panel；预设条显示时强制重排（Visible 切换布局时序）。
- `ConfigSerializer.EnsureSafe` 补 `MultiBrandPlc` 段与轮询项列表兜底。
- `HslPlcClient`：Dispose 后 in-flight 回调复活连接/Timer 的竞态（ConnectInternal/EnsureConnected
  加废弃拦截）、StartKeepAlive 双 Timer 窗口加锁、ReconnectNow 锁内二次确认；
  事件统一走 RaiseSafe 隔离（订阅者异常不外抛，防 Timer 回调未捕获异常崩进程）。
- `ScannerEipService`：Worker 循环整圈异常兜底（订阅方异常不再杀死收码线程）。
- `HslAuthorization`：未激活时每次重读授权文件（运行中补放文件无需重启），告警日志 30s 节流；
  TryActivate/SaveToFile 加锁防并发。



## V1.5.0（2026-08-16）ConfigEditor 界面 SunnyUI 小清新重构

### 改动范围

- **ConfigEditor 界面从原生 WinForms 换成 SunnyUI（3.9.8，net472）小清新风格**：
  - 窗体改继承 `UIForm`（无系统边框、圆角阴影、可拉伸），全局主题 `UIStyles.SetStyle(UIStyle.LayuiGreen)` 浅绿；
  - 工具栏/状态栏/品牌预设条改 `UIPanel` 容器，按钮统一 `UIButton`：**文本水平垂直居中**、
    圆角、悬浮提示、**宽度按文本实测（TextRenderer）保证内容完整显示**；
  - 工具条/品牌预设条内**同排控件按容器中线上下垂直对齐**（按钮 Y=(52-32)/2=10、预设条 label/combo/按钮同中线）；
  - 设备树换 `UITreeView`（浅绿选中高亮），属性网格仍为原生 `PropertyGrid`（置于白色 UIPanel）。
- **依赖**：`ConfigEditor/libs/` 新增 `SunnyUI.dll` + `SunnyUI.Common.dll`（net472，离线 HintPath 引用，
  csproj 构建后自动拷输出目录）。**坑**：SunnyUI.dll 运行时依赖 SunnyUI.Common.dll，两个都要带；
  拷贝时勿把 net8.0 版 Common.dll 覆盖 net472 版（会 `System.Runtime 8.0` 加载崩溃；net472 版 168448 字节）。
- **修复 SunnyUI 3.9.8 两处渲染 bug**（现场截图逐像素排查定位）：
  1. `UIButton` 开启 `ShowTips=true` 时会在按钮上画一块**红色渐变盖住文字**
     （按钮 FillColor/ForeColor 属性值全对、渲染却出 RGB(255,0,0)）——移除 ShowTips/TipsText，
     `MakeButton` 的 `tip` 参数保留，待升级 SunnyUI 后恢复悬浮提示；
  2. `UIForm` 标题栏左上角**红色小三角**：`UIForm.OnPaint` 用 WinForms `ShowIcon`（默认 true）
     判断是否 `DrawIcon` 标题栏图标，会把 SunnyUI 内置红色图标画出来（与 `ShowTitleIcon` 无关，
     设 Icon=null 仍画）——MainForm 补 `ShowIcon = false` 干净移除。
- **文本截断修复**：
  1. 工具条按钮（打开/导出说明书）文本显示不全——`MakeButton` 原用**窗体默认字体**（9pt）
     `TextRenderer.MeasureText` 定宽，而 UIButton 实际渲染用**自身默认字体（12pt）**，测宽偏窄约 1/3；
     改为用 `b.Font` 实测 + 左右各 20px 留白（打开 79 / 导出说明书 127，文字均完整显示）；
     按用户要求按钮文案去掉"…"省略号（打开…→打开、导出说明书…→导出说明书）；
  2. 左侧设备树区域过窄（SplitterDistance=240 且被 SplitContainer 创建期 clamp 到 ~119），
     PLC 从站"从站（监听 502，三拍握手）"、扫码枪"（串口/TCP）"括号内文本被裁剪——
     树区固定 300px，并在窗体 `Shown`（尺寸已定）后再设一次 `SplitterDistance=300`
     （**坑**：SplitContainer 创建时 Width 仍为默认 150，此时设 SplitterDistance 会被
     clamp 到 150-6-25≈119，Dock 拉伸后因 FixedPanel=Panel1 保持该值不变）。
- PLC 树节点文案按现场习惯保留中文：从站（监听 502，三拍握手）/ 主站（主动读写 + 轮询）——
  （曾一度改"PLC Slave/PLC Master"与配置枚举对齐，现场反馈界面仍以中文为宜，已改回；
  配置枚举 `PlcRole { Slave, Master }` 与代码字段命名不受影响。）
- 文档同步：根 `README.md`、`ConfigEditor/README.md`（界面小节 + 结构 libs 说明）。

### 验证

- MSBuild Debug/AnyCPU 构建 ConfigEditor 通过。
- 启动冒烟：带 .kcfg / 无参数均稳定存活 10s 无崩溃；窗体构造成功（UIForm）。
- 布局坐标反射验证：工具栏 7 按钮全 `Y=10/H=32` 垂直居中、宽度按**按钮自身 12pt 字体**实测
  （打开 79 / 导出说明书 127 / 添加设备 111 等不截断）；预设条 label Y=11（中线 23 对称居中）；
  左侧树区 `SplitterDistance=300`（Panel1 300px / Panel2 790px），树文字右缘距面板边界 35px 余量。
- 截图逐像素扫描：**主窗体 1100x780 内红色像素 count=0**（两处红块均消除）；
  按钮行采样 新建/保存=Accent 绿、打开…=白字、校验/导出=浅绿、添加设备=disabled 深灰；
  按钮文字完整（打开…文字右端距按钮右缘 24px、导出说明书… 41px 余量）。

## V1.4.1（2026-08-16）修补：README 快速接入代码块 + 描述符构建防御

> 自审修补（无功能变化）：修掉两处不影响运行的小问题。

### 改动范围

- **`README.md`**：修复「快速接入（四步）」格式——V1.2.4 起该节被「配置读写」小节拆散，
  ①②③④ 只剩 ① 在代码块里、②③④ 变成裸文本；现已把四步合并回一个完整代码块，
  「配置读写」「设备自文档化」等补充小节移到代码块之后。
- **`Configuration/DeviceDescriptorRegistry.cs`**：`Build` 读取默认值时经 `ReadDefaultValue`
  包 try/catch（某字段 getter 异常返回 "—"，不拖垮整段描述符构建）。

### 验证

- MSBuild Debug/AnyCPU 构建 Kaleidoscope + ConfigEditor 通过。
- 冒烟回归：描述符 13 段/126 字段、说明书导出、编辑器启动（正常 .kcfg / 含 null 相机元素
  / 损坏 JSON 三种场景均不崩）。

## V1.4.0（2026-08-16）设备自文档化：DeviceDescriptor 描述符 + 说明书导出

> 配置"可视化编辑器"规划的第三步：把第二步的 `System.ComponentModel` 元数据（V1.3.0）收敛成
> **可编程访问的设备描述符**。从此"字段说明=文档"落成真：配置模型自带语义，说明书/智能渲染/
> 日志自描述全部由一个入口生成，库新增字段自动跟上、不靠人肉维护文档。

### 改动范围

- **新增 `Configuration/DeviceDescriptor.cs`**：描述符模型——`DeviceDescriptor`（设备中文名 +
  字段清单）+ `DeviceFieldDescriptor`（属性名/中文名/说明/分组/友好类型名/默认值文本/是否集合）。
- **新增 `Configuration/DeviceDescriptorRegistry.cs`**：注册表 + 反射构建——
  - 静态构造登记"配置模型类型 → 设备中文名"一行一个（含 4 个集合子表项，共 13 段）；
  - `Build(Type)` 反射读取 `[DisplayName]/[Description]/[Category]` + 类型信息 + 默认值样本
    （new 模型后读属性，即字段初始化器默认配置），字段按 MetadataToken 声明顺序稳定输出；
  - `Get(Type)` 单段（缓存 + 锁）、`GetAll()` 全部（按登记顺序）；
  - 友好类型名：`List<CameraConfig>` 这种泛型、嵌套对象显示 `（对象）Xxx`、集合显示 `（集合）List<...>`。
- **新增 `Configuration/DeviceDescriptionExporter.cs`**：`ExportToMarkdown()` /
  `ExportToMarkdownFile(path)`（UTF-8 无 BOM）——把全部设备段的字段表（中文名/类型/默认值/说明）
  导出成 Markdown 说明书；单元格对 `|`/换行转义，防破坏表格。
- **ConfigEditor 集成**：工具栏新增「导出说明书…」按钮（`ExportToMarkdownFile` + 保存对话框）。
- **csproj**：新增 3 个 Compile（零新依赖，描述符纯反射 + 现有特性）。

### 为什么这么改

- 通用库定位的最后一环：设备参数文档（现场交接/对参数）不再手写，而是模型元数据的免费副产品；
  新增配置模型只需在 Registry 加一行中文名登记，字段说明/说明书自动出现。
- 边界明确：描述符是"字段语义"元数据，校验规则仍手写在 `DeviceHubConfigValidator`
  （寄存器重叠/地址越界等跨字段逻辑无法从元数据自动推导），两者职责互补。

### 验证

- MSBuild Debug/AnyCPU 构建 Kaleidoscope + ConfigEditor 通过（无 error）。
- PowerShell 冒烟（加载 bin 输出 dll）：`GetAll()` 返回 13 段、总字段 126；`CameraConfig` 22 字段、
  首字段 CameraId（声明顺序正确）；Markdown 导出含中文、嵌套对象显示 `（对象）Xxx`、集合显示
  `（集合）List<...>`；导出文件生成成功。ConfigEditor 启动冒烟存活不崩。

### 文档同步

- `ConfigEditor/README.md`：工具栏表格 + 导出说明书说明 + 结构行更新。
- `README.md`：仓库结构 `Configuration/` 补描述符三件；「配置读写」后新增「设备自文档化」小节。
- `AGENTS.md`：代码约定元数据条目补"自动说明书"；架构约束新增「设备自文档化（V1.4.0）」。
- `使用说明.md`：四开头引导补说明书导出一句。
- `CHANGELOG.md`：本版本（V1.4.0）。

## V1.3.0（2026-08-16）可视化配置编辑器（ConfigEditor）+ 配置模型元数据

> 配置"可视化编辑器"规划的第一步（V1.2.4 已铺好 ConfigSerializer/Validator 地基）之后，
> 本版本把第二步交付：一个**独立工具项目 `ConfigEditor/`**（不进库、不启停设备、只产 `.kcfg`），
> 靠 `Models` 的 `System.ComponentModel` 元数据自动渲染参数界面。从此配设备不必手写 JSON，
> 库新增配置字段界面自动出现、编辑器代码一行不用改。

### 改动范围

- **`Models/*.cs` 全部补 `System.ComponentModel` 元数据**（可视化编辑器地基）：
  - 每个公开属性带 `[DisplayName(中文名)]` + `[Description(说明)]`；顶层聚合配置
    `DeviceHubConfig` 按业务语义加 `[Category]` 分组（通讯/型号/模拟/图像等）。
  - 覆盖：`PlcConfig`/`PlcMasterConfig`（含 `PlcPollItem`）/`CameraConfig`（含
    `StationProgramItem`/`ModelStationPrograms`）/`ScanConfig`/`BarometerConfig`/
    `IoConfig`/`IoOutputChannelRemap`/`FanConfig`/`ImageConfig`/`DeviceHubConfig`。
  - 行为零变化（纯特性）；后续新增字段须同步补特性（已列入 AGENTS.md 代码约定红线）。
- **新增独立工具工程 `ConfigEditor/`**（`KaleidoscopeConfigEditor.csproj`，net472，
  引用 Kaleidoscope bin 输出，与 Demo 同策略拷 NModbus.dll）：
  - `MainForm`：左侧设备树（全局/PLC 从站/主站/相机 N/扫码枪 N/气压表/IO/送风机/图像存储，
    相机与扫码枪支持增删）+ 右侧 PropertyGrid（按 Category 分组、中文名、带说明）+ 工具栏
    （新建/打开/保存/校验）+ 状态栏；关窗/打开/新建时未保存改动给确认。
  - 保存前走 `DeviceHubConfigValidator.Validate`：Errors 阻止保存并弹窗逐条列出、
    Warnings 确认后仍可保存；读写走 `ConfigSerializer`（.kcfg，UTF-8 无 BOM）。
  - `BrandPresets.cs`：内置品牌预设（按设备类型一键填充该品牌默认参数）——汇川 PLC 从站/主站、
    基恩士 IV4 相机、基恩士 SR(TCP)/Honeywell Xenon 1902(串口) 扫码枪、通用气压表(0x0001/0x0010)、
    三菱 GX-CL140 IO 耦合器、送风机现场实测映射 + 另一厂商示例映射；预设只收敛参数差异，
    协议差异仍需改库。品牌列表在数据层追加，编辑器代码不用改。
  - `Program` 支持命令行传 .kcfg 启动即打开。

### 为什么这么改

- 通用库定位：换新客户做新界面时，配置环节也不该重写表单。元数据 + PropertyGrid 让
  "配置界面"变成库模型的免费副产品；新增字段不再需要同步改编辑器。
- 编辑器保持独立工具（不引运行时代码、不启停设备），库的分层架构（DeviceHub 门面）不被污染；
  产出文件经 `ConfigSerializer.Load` + `ApplyConfig` 接入，与手写 .kcfg 完全等价。

### 验证

- MSBuild Debug/AnyCPU 构建 ConfigEditor 通过（无 error），构建后自动拷 Kaleidoscope.dll/NModbus.dll。
- 启动冒烟：无参数启动存活 4s 不崩；命令行传含 2 相机 + 2 扫码枪的 .kcfg 启动存活不崩
  （验证 Load → 树重建 → 属性网格绑定链路）。GUI 深度交互以人工验证为主。

### 文档同步

- `ConfigEditor/README.md`：编辑器用途/构建/操作/产出接入（新增）。
- `README.md`：技术栈序列化条目补元数据说明；仓库结构加 `ConfigEditor/`；新增
  「⭐ 可视化配置编辑器」小节。
- `AGENTS.md`：技术栈序列化条目补元数据；代码约定新增「配置模型元数据（V1.3.0 起）」；
  架构约束新增「ConfigEditor」条目；构建命令补编辑器命令。
- `CHANGELOG.md`：本版本（V1.3.0）。

## V1.2.4（2026-08-16）库内置配置持久化 + 校验（ConfigSerializer/Validator）

> 配置"可视化编辑器"规划的第一步地基：把设备配置（`DeviceHubConfig`）的**读写与校验**收进库内，
> 业务项目不再自己写 JSON 反序列化（旧 Demo 用 Newtonsoft 手写一套 Load/Save 已弃用），
> 也为后续独立的配置编辑器（第二步）提供统一的存取与校验入口。

### 改动范围

- **新增 `Configuration/ConfigSerializer.cs`**（命名空间 `Kaleidoscope.Configuration`）：
  - `Save/Load/ToJson/FromJson/EnsureSafe`：`DeviceHubConfig ⇄ .kcfg` JSON 文件；
  - 序列化用 .NET 内置 `DataContractJsonSerializer`（net472 自带，.NET Core 3.0+ 同名可用，
    **零第三方依赖**，遵守"不引 Newtonsoft"红线）；
  - 写盘 UTF-8 无 BOM、带缩进、`\uXXXX` 转义已解码回真实字符（中文直读、可手工编辑）；
  - **版本兼容**：缺字段自动补默认值、未知成员自动忽略；显式 null 会覆盖默认值，
    故读回后经 `EnsureSafe` 兜底（null 嵌套对象/列表替换为默认实例，防 NRE）；
  - `Load` 文件不存在返回默认配置（不抛），存在但损坏抛 `InvalidDataException`。
- **新增 `Configuration/DeviceHubConfigValidator.cs`**：`Validate(DeviceHubConfig)` 返回
  错误（必须修）+ 警告（建议修）；覆盖 PLC 从站（IP/端口/型号序号/地址重叠）、PLC 主站
  （IP/端口/轮询项）、相机（IP/端口/超时/点位程序号 0~127/取图来源）、扫码枪（Tcp/Serial
  各自参数）、气压表（总数/读寄存器数 1~125/串口参数）、IO 耦合器（总路数/备用通道 0~31）、
  送风机（状态区块偏移越界/启用段）、图像（保留天数/目录）、全局（PLC 角色/型号）；
  `UseMockCommunication=true` 时跳过网络类参数校验，避免误报。
- **csproj**：新增 `System.Runtime.Serialization`、`System.Xml` 引用 + 两个 Compile。
- **Demo 接入（示范标准接法）**：`DemoConfig` 拆两层——设备配置走 `ConfigSerializer`
  存 `Config/devices.kcfg`，界面记忆仍写 `Config/demo.json`；旧版内嵌 Devices 的 demo.json
  首次运行自动迁移成 devices.kcfg（现场参数不丢）。`MainForm` 对外接口不变。

### 为什么这么改

- 通用库定位：换新客户做新界面时底层服务一行不改。配置读写收进库内后，新界面
  `ConfigSerializer.Load(path)` 一行拿回强类型配置直接 `ApplyConfig`，不再各写各的序列化。
- 为配置编辑器（第二步）铺路：编辑器与业务项目共用同一套序列化/校验，职责单一。

### 验证

- MSBuild Debug/AnyCPU 构建 Kaleidoscope + KaleidoscopeDemo 通过（无 error）。
- PowerShell 冒烟（加载 bin 输出 dll）：Save/Load 往返（含中文/列表/点位表）、配置文件中无
  `\u` 转义且含中文、未知成员+缺字段容错、null 嵌套兜底、校验器默认配置无错误/坏配置有错误，
  全部通过。

### 文档同步

- `README.md`：技术栈"Newtonsoft.Json 不需要"更新为"内置 ConfigSerializer"；仓库结构加
  `Configuration/`；快速接入新增"配置读写（库内置）"小节。
- `AGENTS.md`：技术栈序列化条目更新；代码约定新增"配置持久化约定（V1.2.4 起）"；
  命名空间清单加 `Kaleidoscope.Configuration`。
- `CHANGELOG.md`：本版本（V1.2.4）。

## V1.2.3（2026-08-16）通讯寄存器地址全面配置化（送风机/气压表去写死）

> 通用性专项检查：全库通讯模块中，PLC 主站/从站、IO 耦合器、相机、扫码枪的地址早已配置化；
> 唯独**送风机（FanControllerClient）的寄存器映射与命令码**、**气压表（ModbusRtuBarometerReader）
> 的阈值寄存器地址与读寄存器数**仍写死在代码里。本次把这两处全部改为配置驱动（默认值
> 严格对齐现场实测，行为零变化），换厂商/换仪表只改配置不改库。

### 改动范围

- **`Models/FanConfig.cs` 新增寄存器映射配置段**（默认值=现场实测，向后兼容）：
  - 读状态区块：`FanStatusStartAddress`（0x0000）/ `FanStatusCount`（6）；
  - 字段偏移（相对区块起始）：`FanRunStateOffset`（1）/ `FanTemperatureOffset`（2）/
    `FanHumidityOffset`（3）/ `FanTempSetpointOffset`（4）/ `FanHumSetpointOffset`（5）；
  - 控制：`FanControlAddress`（0x0001）/ `FanStartCommand`（0x0003）/ `FanStopCommand`（0x0002）。
- **`Services/FanControllerClient.cs`**：`ReadStatus` 按配置区块批量读 + 按偏移取字段（偏移越界
  对应字段取 0，不崩）；状态解析**优先按配置命令码识别定值启停**（`==FanStartCommand`→定值启动、
  `==FanStopCommand`→定值停止），再回退 `FanRunState` 枚举强转——不同厂商命令码不同也能正确识别；
  `StartFixedValue`/`Stop`/`WriteCommand` 改读 `FanStartCommand`/`FanStopCommand`/`FanControlAddress`。
- **`Models/BarometerConfig.cs` 新增**：`BarometerThresholdRegisterAddress`（0x0010，原写死 const）、
  `BarometerReadRegisterCount`（2，读压力一次连续读的寄存器数）。
- **`Services/ModbusRtuBarometerReader.cs`**：删除 `private const ThresholdRegisterAddress = 0x0010`，
  `SetThreshold` 改写配置阈值地址；`ReadData` 读数量改用配置（运行时夹到 1~125，数量 1 也能工作，
  换算仍只信任第 1 个寄存器）。
- **注释同步**：`IFanController`/`IBarometerReader`/`FanData`/`BarometerConfig`/`FanConfig`
  类头与方法注释中"写死 0x0010 / 0x0001 / 0x0003"等描述改为"默认值 + 可配"口径。

### 为什么这么改

- 通用库定位：换新客户做新界面时底层服务一行不改。送风机/气压表若遇寄存器映射不同的设备，
  旧实现必须改库代码（写死 0x0000~0x0005、0x0010），违背通用库目标。全部配置化后，
  这些设备的协议差异都收敛到配置层。
- 默认值严格等于旧行为：不改任何配置的项目编译运行结果与旧版完全一致（无破坏性变更）。

### 验证

- MSBuild Debug/AnyCPU 构建 Kaleidoscope 通过（`Kaleidoscope -> ...\bin\Debug\Kaleidoscope.dll`，无 error）。

### 文档同步

- `README.md`：仓库结构与对接要点补充送风机/气压表"寄存器地址/命令码可配"说明。
- `AGENTS.md`：「已知通讯关键点」送风机/气压表两段更新为"映射已配置化"口径；新增
  "PLC 从站不依赖主站轮询节奏（V1.2.3 审查结论）"关键点。
- `使用说明.md`：FAQ 新增 Q1c——PLC 轮询时间改大/改小均兼容 + 两条业务层边界
  （协调器等复位超时须 > PLC 最慢轮询周期；梯形图发下一拍前须确认结果已回 0）。
- `CHANGELOG.md`：本版本（V1.2.3）。

## V1.2.2（2026-08-15）库改名 Kaleidoscope

> 库名/程序集/命名空间/源码目录由 `CommonLib` 统一更名为 `Kaleidoscope`（万花筒）。
> 改名是全量替换（`CommonLib` → `Kaleidoscope`），不涉及任何行为变化，但**属破坏性变更**：
> 引用方需把 `using CommonLib.*` 改为 `using Kaleidoscope.*`、引用 `Kaleidoscope.dll`。

### 改动范围

- **目录/工程**：`CommonLib/` 目录 → `Kaleidoscope/`；`CommonLib.csproj` → `Kaleidoscope.csproj`，
  `<RootNamespace>`/`<AssemblyName>` 改 `Kaleidoscope`，程序集输出 `Kaleidoscope.dll`。
- **命名空间**：`CommonLib.Models` / `CommonLib.Services` / `CommonLib.Utils` →
  `Kaleidoscope.Models` / `Kaleidoscope.Services` / `Kaleidoscope.Utils`，全部 .cs 的
  `namespace`/`using` 同步替换（文件内容零逻辑改动，仅改名）。
- **Demo**：`CommonLibDemo.csproj` → `KaleidoscopeDemo.csproj`，程序集 `KaleidoscopeDemo.exe`；
  引用 `..\Kaleidoscope\bin\Debug\Kaleidoscope.dll`，构建后拷贝目标同步。
- **文档**：`README.md` / `使用说明.md` / `AGENTS.md` / `Demo/README.md` / 本文档品牌名统一为
  Kaleidoscope；`docs/通讯接入.md` 不含库名，未动。

### 为什么这么改

- 库定位为"换客户做新界面的通用设备通讯/图片存储库"，`CommonLib` 名字过泛且与具体业务
  无关联；更名 `Kaleidoscope`（万花筒，寓意多设备、多协议、多场景的多样性）作为独立品牌。

### 验证

- 构建 `Kaleidoscope/Kaleidoscope.csproj` → `Kaleidoscope -> ...\bin\Debug\Kaleidoscope.dll`（无 error）。
- 构建 `Demo/KaleidoscopeDemo.csproj` → `KaleidoscopeDemo.exe`，输出目录含 `Kaleidoscope.dll`/
  `NModbus.dll`/`NModbus.Serial.dll`/`Newtonsoft.Json.dll`。
- 全库 `git grep CommonLib` 无残留。

### 文档同步

- 本版本（V1.2.2）；仓库结构/构建命令/命名空间约定等已在 README/使用说明/AGENTS.md 中随改名同步。

## V1.2.1（2026-08-15）通讯连接稳定性与鲁棒性加固

> 对全库 11 个通讯/工具类做一次连接稳定性审计后的加固：修复 3 处真实竞态/卡顿点，
> 统一连接状态 volatile 可见性，补齐"断连即释放坏连接 / 重连单飞 / 热更失败兜底 /
> 超长响应丢弃连接"等防御，日志写入改常驻流。

### 改动范围

- **`ModbusTcpMasterClient`**：① `Connect` 的 `_config` 赋值移入锁内——消除轮询线程"新配置
  + 旧连接"的竞态窗口；② 轮询结果缓存 `_lastPollData` 改 `ConcurrentDictionary`——`TryGetLastPollData`
  不再借网络锁，UI 高频取数不被 2s 超时读卡住；③ `_isConnected` 改 volatile；④ 通讯失败
  `MarkDisconnectedOnFailure` 断连标记的同时主动 Close 坏 TcpClient（不必等 Monitor 下轮重连）。
- **`ModbusTcpIoController`**：`_isConnected` 改 volatile；`MarkDisconnectedOnFailure` 断连即释放坏连接。
- **`ModbusRtuBarometerReader`**：`Connect`/`Disconnect` 加 `_syncRoot` 锁对齐全库——消除
  `SetAllThresholds` 断线重连并发 `Disconnect` 干扰采集线程锁内 `ReadData` 的无锁竞态；
  `_isConnected` 改 volatile。
- **`FanControllerClient`**：`Connect` 重构为**锁外逐个候选建连 + 成功一次性锁内 commit**——设备
  离线时不再把 `_syncRoot` 占住 候选数×FanTimeoutMs；`ReconnectNow()` 改后台异步执行（UI 按钮
  不再卡死）；`ReadStatus`/`WriteCommand` 自愈重连移出锁；断连即释放坏连接；`_isConnected`/
  `_activeIp`/`_activeIpLoadedFromDisk` 改 volatile。
- **`ScannerService`**：心跳 `CheckConnectionAlive` 的 WMI 搜索/系统串口列表查询移出 `_lock`
  （避免阻塞 `DataReceived` 收码）；`_wasConnected` 改 volatile；`Dispose` 去掉已过时的
  `Thread.Abort` 兜底（改等待心跳线程自然退出 + 告警）。
- **`KeyenceIV4Camera`**：`SendCommandAndReadLine` 响应超长（>1024 字符）时丢弃连接走重连——
  防残留字节污染下一次读取；`IsConnected` 改 volatile 字段。
- **`ConnectionMonitor`**：相机重连加**单飞标志**（`_cameraReconnecting`，防重连 Task 叠加成
  连接风暴）；构造 `cameras` 判空防御。
- **`DeviceHub`**：`ApplyConfig` 热更加失败兜底——`BuildServices` 异常时尽力释放半成品
  （防 FileSystemWatcher 句柄泄漏）、记 ERROR、仍触发 `ServicesRebuilt` 让上层感知设备层异常。
- **`PlcService`**：`IsConnected`/`HasMasterConnected` 改 volatile 字段（Monitor/UI 锁外读）。
- **`ScannerTcpService`**：`_connected` 改 volatile。
- **`Utils/LogHelper`**：写文件改**常驻 StreamWriter + 跨天滚动**（`AutoFlush=true` 即时落盘、
  UTF-8 无 BOM），相机触发等高频日志不再每次 Open/Close 文件。

### 为什么这么改

- 审计发现的中等风险点集中在"锁粒度/阻塞热点"（送风机锁内多候选建连、串口扫码枪锁内 WMI、
  主站缓存借网络锁）与"可见性/竞态"（`_config` 锁外赋值、连接状态非 volatile、气压表 Connect
  无锁）。逐项对齐后，连接建立、数据读写、缓存读取各走各的锁，UI 线程不受设备离线拖累。

### 验证

- MSBuild Debug/AnyCPU 构建 Kaleidoscope 通过（`Kaleidoscope -> ...\bin\Debug\Kaleidoscope.dll`，无 error）。
- 未新增外部依赖（`ConcurrentDictionary`/`StreamWriter` 均 .NET Framework 4.7.2 内置）。

### 文档同步

- `README.md`：日志出口小节修正默认行为（写 Logs 文件，常驻流+跨天滚动）；热更小节补失败兜底；
  送风机对接要点补 `ReconnectNow` 异步说明。
- `CHANGELOG.md`：本版本（V1.2.1）。

## V1.2.0（2026-08-15）PLC 主站/从站两模式（通用 Modbus TCP 主站 + 自动轮询）

> 需求：有些项目上位机要作 **Modbus TCP 主站**主动读写 PLC（而非从站模式被动等 PLC 来读写）。
> 新增 `ModbusTcpMasterClient`（通用主站）+ `PlcMasterConfig` + `DeviceHubConfig.PlcRole` 角色开关，
> DeviceHub 按角色装配两套服务，业务层只认 `hub.Plc`（从站）或 `hub.PlcMaster`（主站）。

### 改动范围

- **新增 `Models/PlcMasterConfig.cs`**：主站连接与轮询配置（`IpAddress`/`Port`/`UnitId`/
  `TimeoutMs`/`ReconnectIntervalMs`/`PollIntervalMs`/`PollItems`）；寄存器地址一律填
  Modbus 协议地址（0x0000 起），与从站的 DataStore 索引约定区分。
- **新增 `Services/ModbusTcpMasterClient.cs`**：通用 Modbus TCP 主站（范式对齐
  `ModbusTcpIoController`：BeginConnect 手动超时 + `_syncRoot` 锁串行化 + 断连边沿标记），
  新增自动轮询（`StartPolling`/`StopPolling`，`PollDataUpdated` 事件 + `GetLastPollData` 缓存）、
  重连节流（`EnsureConnected` 内部自愈 + `ReconnectNow`）、通用读写 API（
  `ReadHoldingRegisters`/`ReadInputRegisters`/`ReadCoils`/`ReadDiscreteInputs`/
  `WriteSingleRegister`/`WriteMultipleRegisters`/`WriteSingleCoil`/`WriteMultipleCoils`）。
- **`DeviceHubConfig` 新增 `PlcRole` 枚举（Slave/Master）** + `PlcMaster` 配置段；默认 `Slave` 兼容老项目。
- **`DeviceHub` 扩展**：新增 `PlcMaster`/`IsPlcMaster` 属性；`BuildServices` 按 `PlcRole` 建
  `PlcService`（从站）或 `ModbusTcpMasterClient`（主站，主站模式 `Plc` 为 null、跳过
  `SetCurrentModel`/`SetCameraResultAddresses`）；`Start()` 自动启动主站轮询；
  `DisposeServices` 释放 `PlcMaster`；聚合事件两模式都归 `HubDeviceKind.Plc`。
- **`ConnectionMonitor` 扩展**：构造新增可选 `plcMaster`/`plcMasterConfig` 参数（从站/主站二选一，
  `plc` 可 null）；Tick 内新增 PLC 主站边沿检测 + 5s 节流后台重连 +
  `PlcMasterConnectionChanged` 边沿事件。
- **csproj**：新增 `PlcMasterConfig.cs`、`ModbusTcpMasterClient.cs` 两个 Compile 项。
- **适配 NModbus 3.0.83 API**：读离散输入用 `IModbusMaster.ReadInputs`（该版本无
  `ReadDiscreteInputs` 方法，接口方法名见反射清单）。

### 为什么这么改

- 库内已有"上位机作从站"的 `PlcService`，缺"上位机作主站"角色。Modbus 同一协议两种角色，
  底层都是 NModbus，补一个通用主站类 + 角色开关，DeviceHub 按 `PlcRole` 装配，业务层接入
  代码量不变（只是属性换 `hub.PlcMaster`）。

### 验证

- MSBuild Debug/AnyCPU 构建 Kaleidoscope 通过（`Kaleidoscope -> ...\bin\Debug\Kaleidoscope.dll`，无 error）。

### 文档同步

- `README.md`：仓库结构补 `PlcMasterConfig.cs`/`ModbusTcpMasterClient.cs`；快速接入补
  "PLC 主站/从站两模式"小节；对接要点 PLC 条补主站角色说明。
- `CHANGELOG.md`：本版本（V1.2.0）。

## V1.1.0（2026-08-15）新增 Aging 三设备通讯（气压表 Modbus RTU / IO 耦合器 Modbus TCP / 送风机 Modbus TCP + Mock）

> 需求：把 AgingTestSystem（老化测试台）的通讯逻辑作为主干吸收进 Kaleidoscope，目标是——
> **换新客户做新界面时只写页面和业务编排，通讯接入代码一行不写**。DeviceHub 门面保留（薄壳），
> 内部通讯服务换成 Aging 更健壮实现（接口化 + Mock 三件套 + 自动识别 + 心跳静默重连）。

### 改动范围

- **新增 Models（独立强类型配置，不引入 Aging 的 DeviceConfig）**：
  - `BarometerConfig`（PortName 留空自动识别、BaudRate=19200、小数位=1、报警阈值 -95kPa）、
    `IoConfig`（DI 0x1000/DO 0x2000、备用通道映射 `IoBackupChannelMappings`）、
    `FanConfig`（端口 50000、IP 自动识别候选 `FanIpCandidates`）；
  - 数据模型：`BarometerData`（+`DeviceStatus` 枚举）、`FanData`（+`FanRunState` 枚举
    Unknown=-1/ProgramStopped/ProgramRunning/FixedValueStopped/FixedValueRunning）、
    `IoStatus`（+`IoType`/`IoFunction`/`ElectricalType`）、`IoPointDefinition`（+`DeviceIoMapping`）、
    `IoOutputChannelRemap`（`ParseAll` 支持 `;`/`；` 与 `->`/`→`、0x 前缀、通道 0~31）；
  - `DeviceHubConfig` 新增 `Barometer`/`Io`/`Fan` 三段 + **`UseMockCommunication`** 开关。
- **新增 Services 接口**（统一放 `Kaleidoscope.Services`，强类型配置签名 + `OnError` + `IDisposable`）：
  `IBarometerReader`（读压力/写阈值）、`IIoController`（读 DI/写 DO）、`IFanController`（定值启动/停止/读状态）。
- **新增 Services 实现（移植 Aging 健壮性）**：
  - `ModbusRtuBarometerReader`：CH340 WMI 自动识别 + `BarometerPort.cache` 端口记忆、
    单台离线/整线断开区分、未连接返回全 null 数组、SetAllThresholds 50ms 间隔；
  - `ModbusTcpIoController`：BeginConnect 手动超时、读-改-写单点输出、`MapOutputChannel` 备用通道映射、
    额外暴露原始寄存器方法；
  - `FanControllerClient`：`FanLastIp.cache` IP 记忆、10s 重连节流、`ReconnectNow`。
- **新增 Mock 三件套**（`UseMockCommunication=true` 时用，不接设备跑通 UI）：
  `MockBarometerReader`（85% 良好/15% 报警演示）、`MockIoController`（随机输入 + 翻转模拟）、
  `MockFanController`（温度波动模拟）。
- **新增 Utils**：`SerialPortHelper`（CH340 双重校验 WMI 识别 + 全串口枚举）、
  `IoMapBuilder`（Build/GetDeviceMapping/ToOctal 三菱八进制映射）。
- **扫码枪增强（ScannerService 重写，保留 IScanner 接口）**：WMI 按 `DeviceKeyword` 自动识别串口、
  3s 心跳断连检测（双信号判定 + 周期"关-重搜-重开"兜底）、后台静默重连、边沿日志、`DebugLog` 开关；
  去掉 WinForms/NativeWindow（纯库定位）。`ScanConfig` 新增 `DataBits`/`DeviceKeyword`/`DebugLog`，
  `PortName` 默认 `""`（走自动识别）。
- **ConnectionMonitor 扩展**：注入三类主站设备，Tick 内做状态边沿检测 + 节流（5s）后台重连，
  新增 `BarometerConnectionChanged`/`IoConnectionChanged`/`FanConnectionChanged` 边沿事件。
- **DeviceHub 扩展**：`HubDeviceKind` 增 Barometer/Io/Fan；公开 `Barometer`/`Io`/`Fan` 服务实例属性；
  `BuildServices` 按 `UseMockCommunication` 建真实/Mock；释放顺序扩为 监控→PLC→气压表/IO/送风机→扫码枪→相机→图像存储；
  聚合转发三类设备连接状态事件。
- **csproj**：新增 19 个 Compile 项、`System.Management` 引用、`libs/NModbus.Serial.dll` HintPath 引用（RTU 串口传输）。

### 为什么这么改

- Aging 的三类主站设备通讯（气压表/IO/送风机）比 Kaleidoscope 现有实现更健壮：接口化、自动识别、
  Mock 可跑通 UI、心跳静默重连。按用户诉求"后续只写页面"，把这些能力并入 DeviceHub 门面，
  业务层只需 `hub.Barometer`/`hub.Io`/`hub.Fan` 三属性，接入代码量降为接近零。
- 保留 DeviceHub 分层（业务层不建连接）与热更能力（新设备服务 Dispose 干净、支持重建）。

### 验证

- MSBuild Debug/AnyCPU 构建 Kaleidoscope 通过（`Kaleidoscope -> ...\bin\Debug\Kaleidoscope.dll`，无 error）。
- MSBuild Debug/AnyCPU 构建 Demo 测试台通过（`KaleidoscopeDemo -> ...\bin\Debug\KaleidoscopeDemo.exe`）。

### 文档同步

- `README.md`：仓库结构/接入步骤/停建顺序/对接要点补充三类新设备与 UseMockCommunication。
- `AGENTS.md`：新增三类设备的"已知通讯关键点"与分层图说明。
- `使用说明.md`：同步配置项与接入示例（详见该文件）。
- `CHANGELOG.md`：本版本（V1.1.0）。

## V1.0.0（2026-08-15）新增 Kaleidoscope 通用设备通讯库（PLC/相机/扫码枪/图片存储抽取封装）

> 需求：把 CommandCenter 里四类底层通讯/存储服务（汇川 PLC Modbus TCP 从站、基恩士 IV4 相机、
> 基恩士 SR 扫码枪、图片 FTP 归档与定期清理）抽取成独立类库 `Kaleidoscope/`，目标是——
> **换新客户、做新界面时底层服务一行不改，只写 UI 和业务编排**；且所有通讯必须支持热更
> （与当前项目一致，改配置免重启）。

### 改动范围

- **新增 `Kaleidoscope/` 独立类库**（.NET Framework 4.7.2，LangVersion 7.3，不依赖 NuGet，离线可编译）：
  - `Kaleidoscope.csproj` + `libs/NModbus.dll`（本地引用）；
  - `Models/`：`PlcConfig`、`CameraConfig`（含点位→程序号/型号分表模型 + `DefaultCameras()` 默认相机）、
    `ScanConfig`、`ImageConfig`、`DeviceHubConfig`（四类配置 + 型号的聚合载体，DeviceHub 唯一入参）；
  - `Services/`：`PlcService`、`KeyenceIV4Camera`、`IScanner`/`ScannerService`（串口）/
    `ScannerTcpService`（TCP，连上自动发触发指令）、`ImageStore`（FTP 监听 + 双格式归档 + 定期清理）、
    `ConnectionMonitor`（心跳 + 断连自动重连 + 边沿日志）；
  - `Utils/`：`LogHelper`（可替换出口）、`TcpKeepAlive`。
- **★ `Services/DeviceHub.cs`（设备聚合门面，核心封装）**：把原 MainForm 手写的
  "建服务 + 启动 + 事件聚合 + 热更 + 释放"全链路编排收进库内，新界面只需四个固定方法：
  `new DeviceHub(config)` → `Start()` → `ApplyConfig(newCfg)`（热更）→ `Dispose()`；
  对外只暴露聚合事件（`SerialNumberScanned`/`DeviceConnectionChanged`/`FtpFileArrived`/
  `ServicesRebuilt`）与各服务实例（`Plc`/`Cameras`/`Scanners`/`ImageStore`）。
- **抽取过程中的适配**（原代码只读，不动 CommandCenter）：
  - `IScanner` 接口新增 `Name` 属性（串口返回串口名、TCP 返回 IP:端口，供连接指示灯/日志标识）；
  - `CameraConfig` 补 `DefaultCameras()` 静态方法（现场默认两台相机，改现场 IP 只改这一处）；
  - 命名空间统一为 `Kaleidoscope.Models`/`Kaleidoscope.Services`/`Kaleidoscope.Utils`。
- **新增 `README.md`**（接入四步 + 热更说明 + 通用红线）与 **`AGENTS.md`**
  （本库维护约定：分层架构、热更约束、**注释详实是第一红线**、通讯关键点、构建命令）。
- **新增 `使用说明.md`**（完整接入手册：配置逐项说明表、四步接入骨架、业务层
  协调器写法（PLC 三拍 / 相机判定即写 / 扫码枪 / 图片显示）、热更、事件清单与线程模型、
  日志出口、FAQ 排查、红线汇总）。
- **新增 `Demo/` WinForms 测试台**（.NET Framework 4.7.2）：现场界面未完成前，
  用它手动验证全链路——配置区（型号/PLC IP/端口，应用配置热更/保存/加载）、相机
  （T1 仅触发 / T2 触发+判定+取图存图 / 读程序 / 切 P001）、扫码枪（发触发指令 + 收码大字）、
  PLC 读写（读请求/写结果/写型号/任意寄存器读写）、**存图测试（不依赖相机，生成测试图
  验证 ImageStore 归档链路）**、右侧连接状态灯 + 图片预览 + 日志。Demo 严格走 DeviceHub
  四步标准接入、后台线程 IO + SafeInvoke 回 UI，是"标准接入方式的最小界面模板"，
  新界面可直接抄它的 MainForm 接入骨架。`DemoConfig.cs` 用 Newtonsoft.Json 持久化
  `Config/demo.json`（含 Kaleidoscope 全部强类型配置）。

### 为什么这么改

- CommandCenter 的设备编排（`BuildServices`/`ApplyRuntimeConfig`/`FormClosing` 释放、扫码枪按
  `Mode` 选实现、每相机 FTP 目录监听、存图定期清理等）全部手写在 `MainForm` 里，新客户接新界面
  就得重新抄一遍、还容易漏掉红线（UI 禁 IO、热更顺序、ImageStore 释放归属等）。DeviceHub 把
  "设备活着"这件事彻底封装，业务项目只写"业务流程 + UI"，底层通讯行为与坑（超时/重连/热更/并发
  混图）全部复用库内已验证实现。
- 所有服务保持惰性连接 + 自动重连 + Dispose 干净（限时抢锁 + 锁外强断网 + NModbus
  `_network?.Dispose()`），天然支持热更。

### 验证

- MSBuild Debug/AnyCPU 构建 Kaleidoscope 通过（`Kaleidoscope -> ...\bin\Debug\Kaleidoscope.dll`）。
- 原 CommandCenter 工程未改动，仍按原样构建（两工程独立）。

### 文档同步

- `README.md`：新建（接入范式/热更/红线/配置项），补 Demo 测试台一节。
- `使用说明.md`：新建（完整接入手册，README 的详细版）。
- `AGENTS.md`：新建（库级维护约定，含注释详实红线 + 跨 .NET 版本兼容性评估）。
- `Demo/README.md`：新建（Demo 使用说明/验证清单/配置说明）。
- `CHANGELOG.md`：本版本（V1.0.0）。
