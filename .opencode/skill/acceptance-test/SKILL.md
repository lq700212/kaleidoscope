---
name: acceptance-test
description: Kaleidoscope 项目专属的回归测试技能。当用户要求跑测试、回归测试、冒烟测试、验证改动没有破坏功能、发布前检查、或质疑某个功能是否正常工作时使用。提供 152 个测试用例（全功能点 正常/边界/异常路径）的一键编译运行与失败定位方法。
---

# Kaleidoscope 验收测试技能

152 个测试用例覆盖库全部功能点（正常/边界/异常路径），**一条命令完成编译+运行**——
最终验收/发布前/提交前的全量回归检查。
改动库代码后、提交前、发布前都应跑一遍；全部 PASS 才算稳定。

## 一键运行

```powershell
# 前置：先构建库（MSBuild Kaleidoscope/Kaleidoscope.csproj）
powershell -ExecutionPolicy Bypass -File .opencode/skill/smoke-test/scripts/run-smoke.ps1
```

脚本自动完成：探测 Roslyn csc → 编译 `scripts/SmokeTest.cs`（输出到 %TEMP%，不污染仓库）→
拷运行依赖 → 探测授权文件（库工程目录 / ConfigEditor 目录两处，有则拷、无则 HSL 授权用例
自动 SKIP）→ 运行并透传退出码（0=全过，1=有失败，可接 CI）。

## 结果判据

- **PASS=152 FAIL=0** = 全部稳定（本仓库基线）；
- 出现 FAIL：用例名即定位（如 `从站.4 相机结果寄存器上电清零` = PlcService 的重建清零场景），
  先复现再看对应服务的实现；
- **SKIP 是正常现象**：HSL 授权用例（HSL.1/3/5）在本机无 `hsl.dat` 时自动跳过（开源仓库不含
  授权文件）；有授权环境（ConfigEditor 跑过一次弹窗输入后）会真实执行；
- 注意汇总行 `PASS=x FAIL=0 SKIP=y` 中 FAIL 必须为 0，SKIP 不算失败。

## 用例清单（28 组 152 例：12 组原始 54 例 + 16 组扩展 98 例）

| 组 | 覆盖 |
| --- | --- |
| 配置（7） | 序列化往返、缺字段兜底、显式 null、损坏 JSON、Validator 正/反例、重名告警 |
| Mock（3） | 气压表/IO/送风机模拟实现 |
| PLC 从站（6） | 主站连入、三拍握手、型号区（未配映射写 0）、相机寄存器重建清零、通用读写、热更重建重连 |
| Modbus 主站（6） | 读写四类、轮询缓存、断线自愈（循环探测 TCP 半开）、未连接安全、多线程并发 |
| IO 耦合器（5） | 输入映射（InputRegisters 区）、单点写（编号=TotalInputs+1 起）、批量写、同寄存器不覆盖、越界安全 |
| 送风机（3） | 状态解析（÷100）、启停控制字、未知运行态回退 |
| TCP 扫码枪（4） | 收码、断线重连（模拟器需关闭活跃连接）、半条码不污染、Dispose |
| 相机（7） | T1/T2 OK/NG/超时、PR、PW 同号跳过、OF |
| ImageStore（2） | 归档结构、FindLatestPair（成对文件）+ DeleteSourceFile |
| HSL（5） | 授权（环境自适应）、断线自愈、错误码保护、Dispose 后安全、加密存储往返（文件无明文） |
| DeviceHub（4） | Mock 建启、经 hub 收码、热更切角色、Dispose 清空 |
| 生命周期（2） | 串口失败路径、EIP 生命周期 |
| 配扩（8） | 缺文件/空文本默认、null 抛参、未知成员兼容、中文直读无 BOM、反斜杠 u 保留、深层兜底、类型错配 |
| 校验（14） | 校验器全分支：从站坏参/重叠告警/型号表、主站全分支、多品牌跳过与端口约定/心跳与西门子/轮询项、相机/扫码枪/气压表/IO/送风机/图像全局 |
| 描述（4） | GetAll 非空、缓存与未登记回退、null 抛参、说明书导出与转义 |
| 映射（8） | ParseAll 空/单条/分隔符兼容/错误汇总/无前缀兼容、IoMapBuilder 数量与编号/八进制/预留/参数异常 |
| 从扩（5） | 相机地址容错、未建站安全、型号映射与截断、空型号与释放、零地址通道 |
| 主扩（6） | 全功能码读写、轮询事件、空轮询安全、端点与重连、Dispose 后安全、连接失败事件 |
| IO扩（5） | 备用重定向、取反、原始寄存器、未连接安全、断开重连 |
| 风扩（5） | 未启用、偏移越界、自定义映射、无候选、断开释放 |
| 枪扩（6） | TCP 禁用与空触发/名称与重复 Open/边沿事件、串口契约、EIP 契约与禁用 |
| 相扩（8） | 详细格式、空判定与坏前缀、自定义合格位、夹值与 ER 失败、非法 OF 与坏 PR、未连接路径、标签释放、BR 读图 |
| 图扩（8） | 双格式归档、字节归档、模板占位符、监听去重、查找边界、删除安全、重名与清洗、脏路径与清理 |
| HSL扩（5） | 各品牌默认端口、品牌标签、未连接全类型失败、空轮询与错误隔离、授权守卫 |
| Hub扩（5） | 主站装配、默认相机回退、空热更与重复启停、真实类型、三枪装配 |
| 监控（3） | 空构造、气压表边沿、主站与 IO 送风机边沿 |
| 表Mock（4） | 真实气压表失败路径、Mock 三件套边界 |
| 工具（4） | LogHelper 定向隔离、KeepAlive 空安全、串口识别无硬件安全、HSL 文件格式 |

## 扩展用例

1. 在 `scripts/SmokeTest.cs` 对应分组加 `static void Tn_Xxx()`，内部用 `AssertTrue(cond, "失败说明")` 断言；
2. 在 `Main` 对应分组注册 `Run("组.序号 名称", Tn_Xxx);`；
3. 模拟设备复用现成基建：
   - `MockSlave`（NModbus 3.x 从站）：`WriteHold`（功能码 03）/`WriteInput`（功能码 04）预置数据；
   - `StartSim/StopSim`（TCP 扫码枪）：可控制应答码、**Stop 会关闭全部活跃连接**（触发客户端断线重连）；
   - `StartCamSim`（相机指令应答）：`_camJudge` 切判定、`_camSilent` 模拟超时、
     `_camPwFail` 模拟 PW 失败、`_camPrBad` 模拟坏 PR、`_camBrBad` 模拟坏 BR；
   - `NModbusMasterRetry(port)`：从站刚 EnsureConnected 的毫秒级 accept 窗口可能拒连，新从站用例用它代替直连；
4. 改完跑一遍，全 PASS 后随项目提交（用例是正式资产）。

## 已知边界（无真机环境）

- 串口类设备（气压表 RTU/串口扫码枪）：只测失败路径与生命周期，协议层需真机验证；
- EIP 扫码枪 CIP 协议层：需真机，测试覆盖接口契约（名称/触发空操作/启停/释放）与生命周期；
- 相机 FTP 取图链路：需真机 FTP，测试覆盖指令层（T1/T2/PR/PW/OF/BR）与判定解析全分支。

## 编译注意（历史坑）

- 必须用 **Roslyn csc**（VS 的 MSBuild/Current/Bin/Roslyn/csc.exe）——系统自带 csc 是 C# 5，
  不支持 out var/字符串插值会报一堆语法错；
- **.ps1 必须 UTF-8 带 BOM**（本目录脚本已带），否则 PowerShell 5.1 按 ANSI 解析中文乱码；
- NModbus 3.x 从站 API：`SlaveDataStore`（无参构造）+ `factory.CreateSlave(unitId, store)` +
  `network.AddSlave(slave)` + `network.ListenAsync(token)`——与老版本 `DataStoreFactory` API 不同。
