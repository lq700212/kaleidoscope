---
name: acceptance-test
description: Kaleidoscope 项目专属的回归测试技能。当用户要求跑测试、回归测试、冒烟测试、验证改动没有破坏功能、发布前检查、或质疑某个功能是否正常工作时使用。提供 54 个测试用例（全功能点 正常/边界/异常路径）的一键编译运行与失败定位方法。
---

# Kaleidoscope 验收测试技能

54 个测试用例覆盖库全部功能点（正常/边界/异常路径），**一条命令完成编译+运行**——
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

- **PASS=54 FAIL=0** = 全部稳定（本仓库基线）；
- 出现 FAIL：用例名即定位（如 `从站.4 相机结果寄存器上电清零` = PlcService 的重建清零场景），
  先复现再看对应服务的实现；
- **SKIP 是正常现象**：HSL 授权用例（HSL.1/3/5）在本机无 `hsl.dat` 时自动跳过（开源仓库不含
  授权文件）；有授权环境（ConfigEditor 跑过一次弹窗输入后）会真实执行；
- 注意汇总行 `PASS=x FAIL=0 SKIP=y` 中 FAIL 必须为 0，SKIP 不算失败。

## 用例清单（12 组 54 例）

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

## 扩展用例

1. 在 `scripts/SmokeTest.cs` 对应分组加 `static void Tn_Xxx()`，内部用 `AssertTrue(cond, "失败说明")` 断言；
2. 在 `Main` 对应分组注册 `Run("组.序号 名称", Tn_Xxx);`；
3. 模拟设备复用现成基建：
   - `MockSlave`（NModbus 3.x 从站）：`WriteHold`（功能码 03）/`WriteInput`（功能码 04）预置数据；
   - `StartSim/StopSim`（TCP 扫码枪）：可控制应答码、**Stop 会关闭全部活跃连接**（触发客户端断线重连）；
   - `StartCamSim`（相机指令应答）：`_camJudge` 切判定、`_camSilent` 模拟超时；
4. 改完跑一遍，全 PASS 后随项目提交（用例是正式资产）。

## 已知边界（无真机环境）

- 串口类设备（气压表 RTU/串口扫码枪）：只测失败路径与生命周期，协议层需真机验证；
- EIP 扫码枪 CIP 协议层：需真机，测试覆盖生命周期与断线自愈；
- 相机 FTP 取图链路：需真机 FTP，测试覆盖指令层（T1/T2/PR/PW/OF）。

## 编译注意（历史坑）

- 必须用 **Roslyn csc**（VS 的 MSBuild/Current/Bin/Roslyn/csc.exe）——系统自带 csc 是 C# 5，
  不支持 out var/字符串插值会报一堆语法错；
- **.ps1 必须 UTF-8 带 BOM**（本目录脚本已带），否则 PowerShell 5.1 按 ANSI 解析中文乱码；
- NModbus 3.x 从站 API：`SlaveDataStore`（无参构造）+ `factory.CreateSlave(unitId, store)` +
  `network.AddSlave(slave)` + `network.ListenAsync(token)`——与老版本 `DataStoreFactory` API 不同。
