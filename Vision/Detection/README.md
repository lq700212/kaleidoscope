# Kaleidoscope.Vision.Detection — YOLO 视觉检测核心库

> 自 YoloDetector 项目（RTSP 实时人员检测）沉淀的**通用视觉检测核心**：ONNX 推理检测器、
> 帧源、检测域抽象接口、高性能 Mat↔SKBitmap 互转、可视化绘制、ROI 框选与 Zoom 坐标换算、
> 日志门面。命名空间保持 `YoloDetection`（沉淀时原样保留，宿主接入零 using 改动）。

## 能力清单

| 分类 | 类型 | 说明 |
| --- | --- | --- |
| 检测抽象 | `IYoloDetector` / `IPoseDetector` | 检测器契约（`Initialize(modelPath)` + `Detect(Mat)`，非线程安全需串行化调用） |
| 检测器 | `YoloV26Detector` | YOLO ONNX 推理：letterbox 预处理、双格式输出解析、NMS、TargetClassIds 过滤 |
| 检测器 | `YoloPoseDetector` | YOLO-pose 姿态推理：逐人裁剪扩边→17 关键点还原（输出布局/动态维自动兼容） |
| 帧源 | `IFrameSource` / `RtspFrameCapturer` | RTSP/文件拉流线程（线程私有 VideoCapture、断流两层自愈：连续失败重连 + 心跳看门狗强杀重建） |
| 管道契约 | `IDetectionPipeline` | 检测管道契约（Start/Stop/ProcessFrame/GetLatestDetections + 快照事件） |
| 后处理 | `IDetectionResultProcessor` | 结果过滤契约 + Default/SizeFilter/Composite 三实现 |
| 可视化 | `IDetectionVisualizer` / `VisualizerFactory` / `YoloBuiltinVisualizer` | Skia 红框（跨平台）与 OpenCV 绿框两种方案 |
| 结果 | `DetectionResult` / `PoseResult` / `YoloClasses` | 检测框（像素坐标）/ COCO 17 关键点 / COCO 类别表 |
| 互转 | `MatExtensions` | Mat↔SKBitmap 高性能互转（Bgra8888 与 BGRA 对齐，SIMD CvtColor + 整块内存拷贝，1080P 约 5ms） |
| ROI | `RoiSelectionState` / `ZoomMapping` / `EsdRoiRect` | 拖拽框选状态机（纯逻辑零 UI 依赖）与 Zoom(letterbox) 坐标换算（纯函数） |
| 日志 | `LogManager` | 分级日志门面：宿主经 `Initialize(..., outputSink, uiSink)` 委托注入，未注入退化为 Debug.WriteLine |

**注意：管道实现类（Monitor 生产者-消费者、单槽位缓冲、快照事件、有界停止）留在源宿主
YoloDetector 中**（它带静电触摸/门监测业务旁路，与业务耦合）。本库只提供 `IDetectionPipeline`
契约，复用方可参考源宿主实现或按自己的节奏实现（约定见接口注释：Mat 所有权归订阅者、
事件传不可变快照、停止走有界协议）。

## 接入方式（三步）

1. **引用**：整目录复制进解决方案加 ProjectReference，或直接引用编译产物
   `Kaleidoscope.Detection.dll`（多目标 net472 / netstandard2.0，能力一致）。
2. **native 落位**：`libs/native/` 的运行库（onnxruntime / OpenCvSharpExtern / ffmpeg 的
   Windows .dll 与 Linux .so）经 csproj `None + Link 平铺` 规则随 ProjectReference 自动流转
   到宿主输出目录（运行时 DllImport 按 exe 目录解析）；若用 DLL 引用方式，需自行把
   native 文件复制到 exe 同目录。
3. **模型与日志注入**：ONNX 模型路径由调用方传入 `Initialize(modelPath)`（模型文件不入库）；
   日志经 `LogManager.Initialize` 委托注入宿主日志通道。

## 构建命令

```powershell
# SDK 风格多目标工程，dotnet 构建（与仓库其余 MSBuild 老式工程并存）：
dotnet build Vision/Detection/Kaleidoscope.Detection.csproj -v q
# 也可用 VS MSBuild（需安装 .NET SDK 工作负载）
```

- 依赖在 `libs/` 本地引用，无需 NuGet restore，离线可编译。
- 成功标准：0 error 0 warning（net472 与 netstandard2.0 两目标）。

## 关键契约（接入前必读）

- **Mat 所有权**：`IFrameSource.FrameReady` 与 `IDetectionPipeline.FrameProcessed` 事件传出的
  Mat 归订阅者所有，用完必须 Dispose（详见各接口 XML 注释）。
- **事件传不可变快照**：列表一律副本传出，禁止把内部集合引用泄漏给订阅者。
- **检测器非线程安全**：`Detect(Mat)` 必须单线程串行调用（管道在独立检测线程调用）。
- **配置经参数注入**：本库不读任何配置文件，阈值/ROI/日志开关全部由调用方注入。
- **模型与运行配置不入库**：ONNX 模型（大二进制）与现场运行配置（阈值/IP/ROI 标定）
  归宿主维护，本库只收参数。

## 来源与同步

- 来源：YoloDetector 项目（v2.10.4 时点）`Detection\` 类库的通用核心部分；宿主侧保留
  业务分析器（静电触摸 EsdContactAnalyzer / 门监测 DoorMonitorAnalyzer）与管道实现。
- 命名空间刻意保持 `YoloDetection` 未改名：源宿主以 git submodule 引用本库，类型无缝
  解析，两侧代码零 using 改动。改本库公共 API 前先评估对源宿主的影响。
