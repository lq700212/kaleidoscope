# 版本改动记录

> **本文件自 V1.0.0（Kaleidoscope 库抽取之日）起记，只记录 Kaleidoscope 仓库自身的改动。**
> 更早的 V2.14.26 及以前版本均为 CommandCenter 原项目的历史记录（窗口徽标、点位配置、
> 产品型号弹窗等界面功能，与 Kaleidoscope 库代码无关），**不随本仓库迁移**；其中通讯相关的
> 血泪背景（PLC 从站释放 V2.14.23、相机判定即写 V2.13.7、PW 同程序号跳过 V2.14.19、
> 存图清理防误删 V2.14.12 等）已沉淀在 `AGENTS.md`「已知通讯关键点」（位于仓库根），需要
> 原始完整记录时查原 CommandCenter 项目的 CHANGELOG.md。

## V2.0.0（2026-11-18）Kaleidoscope.UI 首建：WPF 复用控件库（宿主 CureLine V0.0.109 反哺）

> 对应宿主 CureLine V0.0.109：UI 资产下沉调研（两程序 26 个弹窗手抄骨架 /
> 按钮/输入族 / 主题键板三处重复盘点）后首建 WPF 控件库。SDK 风格
> net8.0-windows + UseWPF 单目标（与主库 Kaleidoscope net472 并行的独立工程，
> 各自版本线；HJVision-WPF 等 net472 宿主接入需后续加多目标，见 UI README 路线图）。

### 新增

- `Kaleidoscope.UI/Views/ChromeDialogWindow.cs`：**圆角自绘弹窗基类**——WindowStyle=None
  + WindowChrome（CaptionHeight/Resizeable DP，缩放边 0/6）+ 透明分层 + 根 Grid Margin=14
  （投影留白）+ 圆角 10 卡片 Border（WinBg/Border/Fx.WinShadow）+ 品牌标题条（HeaderGrad
  + TopbarBorder 底边 + 顶角圆 10）+ 标题绑 Window.Title + ✕ 关闭钮（悬停红换白字，
  chrome 命中豁免）。子类 XAML 根内容自动进骨架内容槽（OnContentChanged 转移，重入闸防递归）。
  零 StaticResource 依赖（全 SetResourceReference 语义键）；按宿主 CureLine 现行 26 窗口径
  逐像素一致（无 ContentClip、无隐式样式注入——HJVision SunnyDialog 两坑绕行）。
- `Kaleidoscope.UI/Themes/ChromeStyles.xaml`：圆角窗骨架 keyed 样式 6 键（Fx.WinShadow +
  WinTitleBar/WinTitleText/WinTitleClose/WinSysBtn/WinSysClose）——与基类同口径，供不继承
  基类的窗（主窗/定制标题条窗）手抄用；像素口径来源=CureLine 两程序逐字相同值。
- `Kaleidoscope.UI/Themes/FreshStyles.xaml`：按钮/输入族 keyed 10 键（GhostButton/
  PrimaryGhostButton/FreshButton/FreshPrimaryButton/FreshDangerButton/FreshSuccessButton/
  FreshInput/FreshPwdInput/FreshCombo/FreshComboItem）——零硬编码色全 DynamicResource；
  【隐式样式绝不下沉】纪律入注释；FreshPwdInput Padding 库缺省取通用值 10,0,8,0，
  宿主像素契约走本地覆盖通道。
- `Kaleidoscope.UI/Themes/DefaultLight|Dark.xaml`：缺省键板 36 语义键（值取宿主
  CureStation Light/Dark 一字未动）——新宿主开箱即用；键名=契约（缺键=DynamicResource
  静默空白），键值可换皮。
- `Kaleidoscope.UI/Converters/UiConverters.cs`：InverseBoolConverter（互斥 Radio 单向显示）
  + StringEqualsConverter（MultiBinding 徽标比对）——语义零改动提炼。
- `Kaleidoscope.UI/README.md`：调研结论沉淀（候选清单/技术障碍/三波路线图/net472 多目标
  评估）+ 两步接入指南 + 键名契约清单 + 下沉纪律（口径来源明确的才收、隐式不做、
  零硬编码色）。

### 接入（宿主侧事实）

- 宿主 CureLine：两程序 App.xaml 合并库字典（宿主键板供语义键值，缺省键板不并入），
  本地已下沉键删除（CureStation 866→462 行 / CimGateway 766→344 行；FreshPwdInput 与
  隐式族按契约保留）；灰度迁移 3 窗（MesPostConfigWindow / ScannerConfigWindow /
  MesDebugWindow 可缩放变体）——其余 23 窗第二波。键契约测试在宿主
  （KaleidoscopeUiContractTests：库引用语义键 ⊆ 两程序键板、缺省键板 Light/Dark 全等、
  库样式键清单锁）。
## V1.12.0（2026-11-17）双域扩容：Stores 落库引擎 + Cameras 相机层（HJVision V0.9.68 反哺）

- **新增 `Stores` 命名空间（SQLite 单写线程落库引擎）**：`KxSqliteStore`（Register/Close
  幂等状态机 + 单写线程批量事务 + WAL + 坏库自愈 `.bad_时间戳` 隔离 + 积压保险丝 +
  独立短连接读 + DbMeta 固有 kv + EnsureColumn 旧库补列迁移 + 增量空间回收）+
  `KxDbAction`（委托型写动作：引擎对业务表零知识，宿主闭包捕获自己的行 DTO）。
  逻辑一比一对齐 HJVision `SqliteRecordQue` 骨架与全部血泪注释（PRAGMA 顺序铁律
  auto_vacuum 先于 WAL/提交防线/自愈只隔离真损坏等）；**Solo 独占通道的完成回执
  收敛为确定性语义**——Done 在 OnCommitted 之后才 Set，同步等待方读数无竞态
  （老版为尽力语义）。宿主 SqliteRecordQue 改壳后公开 API 零改动。
- **新增 `Cameras` 命名空间（三品牌工业相机层）**：`KxCamFrame`（中性帧 DTO：Mono8/
  Range16 + 尺度元数据，**解 ICogImage 焊点**——库零 VisionPro 依赖，换图像库宿主
  直接消费）+ `KxCameraDeviceBase`（抽象基类：生命周期/触发/回调/掉线/NoteNoDevice
  静默开关）+ `KxCameraDiscovery`（三品牌只发现不打开）+ `KxGigEIpAdmin`（改 IP
  执行体：校验与 SDK 执行分离/海康 GevPersistent 三键+DeviceReset 复位生效链/
  同网段前置检查）+ 三适配器 `KxDaHengCamera`（Init/Uninit 进程级配对计数/深拷
  防花屏/最优包大小/帧防抖）、`KxHik2DCamera`（Create/Destroy 配对/丢包重发使能/
  EnsureMono8 验真/特征名新旧回落/软触发武装兜底/探活断点）、`KxHik3DCamera`
  （Depth +32768 偏移重映射/Profile 高度钳 5/半成品空壳一比一保留并注明欠账）。
  宿主 `CameraAbstract` 组合壳 + `CogFrameBridge` 桥（KxCamFrame→ICogImage：Stride
  感知拷贝/占位黑图/3D 尺度变换/Profile 承载对象保活环形槽），存量调用点零改动。
- **依赖增量**：`libs/` 新增 System.Data.SQLite.dll（1.0.119）+ x64/x86 SQLite.Interop.dll
  （native 三件套，消费项目按进程位数部署）+ GxIAPINET.dll / MvCameraControl.Net.dll /
  Mv3dLpNet.dll（三相机 wrapper，均 AMD64；**原生运行库硬约束**：海康 MVS 客户端/
  大恒 Galaxy 驱动/Mv3dLP 运行库须现场安装，缺驱动机器上"校验与 SDK 执行分离"
  保证枚举/校验路径可用，SDK 执行路径走正常错误串不崩进程）。
- **验证**：库冒烟 173 全绿（FAIL=0 SKIP=3 基线不变）；宿主侧全量回归 22 批全绿
  （含新增 W22 探针 46 项：引擎直测 18 + 壳兼容 6 + 相机桥 12 + IP/发现/壳转发 10，
  含坏库自愈/坏 SQL 单条隔离/Solo 提交回执回滚修正/Stride 感知逐行拷像素逐位断言
  等负路径）；真机冒烟启动/登录/相机装配（4 台 SN 品牌识别）/掉线状态/Mock 桥/
  优雅退出全链正常。

## V1.11.0（2026-11-16）视觉检测核心沉库：YOLO 检测域入库（源自 YoloDetector 项目）

- **新增 `Vision/Detection/` 核心库**（Kaleidoscope.Detection.dll，多目标 net472/netstandard2.0，
  dotnet build 构建）：自 YoloDetector 项目 Detection 类库沉淀的通用视觉检测核心 19 个源文件——
  YOLO ONNX 检测器（YoloV26Detector：letterbox 预处理/双格式输出解析/NMS）+ 姿态检测器
  （YoloPoseDetector：逐人裁剪扩边→17 关键点）、RTSP 帧源（RtspFrameCapturer：断流两层自愈），
  检测域接口六件套（IYoloDetector/IPoseDetector/IDetectionPipeline/IFrameSource/
  IDetectionVisualizer/IDetectionResultProcessor 及后处理三实现），Mat↔SKBitmap 高性能互转
  （MatExtensions），可视化双方案（Skia 红框/OpenCV 绿框），ROI 框选状态机 + Zoom 坐标换算
  （RoiSelectionState/ZoomMapping/EsdRoiRect），分级日志门面（LogManager）。
- **命名空间保持 `YoloDetection` 不改**：源宿主（YoloDetector）以 git submodule 引用本库，
  同名命名空间类型无缝解析，两侧代码零 using 改动；接入指南见 `Vision/Detection/README.md`。
- **依赖 vendor 入库（约 201MB）**：托管 DLL（OpenCvSharp/OnnxRuntime/SkiaSharp/System.*）
  + native 运行库（Windows .dll + Linux .so），克隆即编译、运行全离线；与主库 MSBuild 老式
  工程并存，Vision 子库用 `dotnet build Vision/Detection/Kaleidoscope.Detection.csproj`。
- **管道实现留宿主**：YoloDetectionService（Monitor 生产者-消费者管道，带静电触摸/门监测
  业务旁路）留在源宿主业务层，本库只出 `IDetectionPipeline` 契约——依赖方向是管道→业务
  分析器，与"业务留宿主"的拆分一致，复用方按接口注释自行实现或参考源宿主。
- 验收：独立编译双目标 0 error 0 warning；源宿主 submodule 引用改造后全量回归全绿
  （记录见 YoloDetector 仓库 CHANGELOG）。

## V1.10.1（2026-11-15）使用说明 6.7 补双宿主回接先例（CureLine V0.0.106 回接落地）

- **纯文档变更，零代码/零 API 变化**（用例基线 173 不变）：`使用说明.md` 6.7 节补
  "回接先例（2026-11-15，双宿主对位）"——HJVision 侧 LineFlow 直消费发号器
  （`EntityTracker<uint>` 薄壳化，宿主 V0.9.66）+ CureLine 侧政策源四字段收口
  `PolicySource<T>`、灯写账本换 `DedupEffectSink` 并补 Attach/断连对齐清账口
  （宿主 V0.0.106，三期原语回接）。
- 沉淀三条对接纪律进使用说明：①宿主锁 → 库内部锁单向嵌套绝不反向；②带参查询
  （Func<r,l,…>）与函数桩（缺省=活时钟等）形态不对口 PolicySource 勿硬套；
  ③已有健壮轮询骨架（停机静默阀/重连节流）的宿主总线可保留骨架只对位契约，
  新信号源一律继承库基类 PollingSignalSource。
