# Kaleidoscope 设备配置编辑器（ConfigEditor）

> Kaleidoscope 的可视化配置工具：**不写一行配置代码**，用界面把 `DeviceHubConfig` 配出来，
> 保存成 `.kcfg` 文件，业务项目一行 `ConfigSerializer.Load(path)` 就能用。
> 本工具**只产配置、不改库、不启停设备**——运行设备仍是业务项目 + `DeviceHub` 的职责。

## 它解决什么

旧做法配一台设备要么手写 JSON（记不住字段），要么写配置表单（每加字段同步改一次表单）。
本编辑器靠 **PropertyGrid + Models 元数据**自动渲染：`Models/*.cs` 里的每个字段都带
`DisplayName`（中文名）/`Description`（说明）/`Category`（分组），属性网格自动按中文分组显示，
**库新增配置字段时编辑器代码一行不用改**，界面自动出现新字段。

## 怎么构建

```powershell
# 先构建库，再构建编辑器（会自动拷 Kaleidoscope.dll / NModbus.dll / SunnyUI.dll / SunnyUI.Common.dll 到输出目录）
& "D:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe" `
  Kaleidoscope/Kaleidoscope.csproj /p:Configuration=Debug /p:Platform=AnyCPU /t:Build /nologo /v:m /m
& "D:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe" `
  ConfigEditor/KaleidoscopeConfigEditor.csproj /p:Configuration=Debug /p:Platform=AnyCPU /t:Build /nologo /v:m /m
```

运行：`ConfigEditor\bin\Debug\KaleidoscopeConfigEditor.exe`（可拖拽/命令行传入 .kcfg 直接打开）。

## 界面与操作

> 界面用 **SunnyUI**（3.9.8，net472）做小清新风格：无边框圆角窗体 + LayuiGreen 浅绿主题；
> 按钮统一 UIButton（文本水平垂直居中、圆角、悬浮提示、宽度按文本实测保证内容完整显示）；
> 同一行按钮/控件按行中线上下居中对齐。主题色在 `Program.cs` 入口 `UIStyles.SetStyle` 一次设置。

| 区域 | 作用 |
| --- | --- |
| 左侧设备树 | 全局设置 / PLC 从站 / PLC 主站 / 相机（可多台，支持增删）/ 扫码枪（可多台，支持增删）/ 气压表 / IO 耦合器 / 送风机 / 图像存储 |
| 右侧属性网格 | 选中设备后的全部参数，中文名 + 说明；列表类字段（型号序号表/点位表/轮询项/备用通道映射）双击值即可集合编辑 |
| 底部品牌预设 | 按设备类型选品牌 → 「应用预设」一键填充该品牌默认参数（如基恩士 IV4、Honeywell Xenon 1902、三菱 GX-CL140），之后可继续微调 |
| 工具栏 | 新建 / 打开… / 保存 / 校验 / 导出说明书… / 添加设备 / 删除设备 |

**保存前自动校验**（`DeviceHubConfigValidator`）：错误（IP/端口/寄存器越界等）**阻止保存**，
警告（建议修）确认后仍可保存。也可随时点「校验」单独查看。

**导出说明书…**：把全部设备段的字段说明（中文名/类型/默认值/说明，来自 Models 元数据）
一键导出成 Markdown 文档，现场对参数、交接文档直接用；库加字段后重新导出自动跟上。

## 产出如何用

```csharp
var cfg = ConfigSerializer.Load("devices.kcfg");  // 编辑器保存的文件
hub.ApplyConfig(cfg);                             // 转手给 DeviceHub，热更/启动均可
```

- 配置文件的格式、版本兼容、兜底规则见库内 `Configuration/ConfigSerializer.cs`。
- 品牌预设只收敛"参数差异"；真正的"协议差异"仍需改库（见仓库根 `AGENTS.md`「已知通讯关键点」）。

## 结构

```
ConfigEditor/
├── KaleidoscopeConfigEditor.csproj   # WinForms 工程（net472，引用 Kaleidoscope bin 输出 + SunnyUI）
├── Program.cs                        # 入口（支持命令行传 .kcfg；设置 SunnyUI 全局小清新主题）
├── MainForm.cs                       # 主窗体：设备树 + 属性网格 + 品牌预设 + 校验保存 + 导出说明书
├── BrandPresets.cs                   # 内置品牌预设库（按设备类型返回默认参数模板）
└── libs/                         # 离线引用的第三方库
    ├── NModbus.dll                # 运行时依赖（同 Demo 的拷贝策略）
    ├── SunnyUI.dll                # SunnyUI 界面库（net472）
    └── SunnyUI.Common.dll         # SunnyUI 基础库（SunnyUI.dll 运行时依赖它，两个都要）
```