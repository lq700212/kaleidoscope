using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace YoloDetection
{
    /// <summary>
    /// YOLO-pose ONNX 姿态检测器（IPoseDetector 默认实现，适配 yolo11n/yolov8n-pose 等模型）。
    ///
    /// 工作方式：
    ///   对上游传入的每个人体框，向四周扩边后从整帧裁剪出小图，
    ///   letterbox 到模型输入尺寸推理，解析出该人的 17 个 COCO 关键点，
    ///   再把坐标还原回原图像素空间。
    ///   （单帧最多推理 MaxPersonsPerFrame 人，按人体框置信度取前 N，
    ///     防止拥挤画面把检测线程拖垮；未参与推理的人返回空关键点）
    ///
    /// 模型输出格式（自动兼容两种布局）：
    ///   [1, 5+3*K, N] 或 [1, N, 5+3*K]，其中 K 为关键点数（COCO=17 → 特征维 56）：
    ///   行 0~3: cx,cy,w,h（模型输入空间像素）；行 4: 该框分数；
    ///   之后每 3 个一组: kx, ky, kpt_conf（kx/ky 同为输入空间像素）。
    ///
    /// 性能说明：
    ///   预处理与 YoloV26Detector 同款——Marshal.Copy 整块拷贝 + 单层循环填 CHW，
    ///   避免逐像素 P/Invoke；单人裁剪图推理耗时远小于全帧推理。
    ///
    /// 注意：实例不是线程安全的；同一实例的 Detect 必须串行调用（管道内部已保证）。
    /// </summary>
    public class YoloPoseDetector : IPoseDetector
    {
        private InferenceSession _session;
        private int _inputWidth = 640;
        private int _inputHeight = 640;

        // 输出特征维长度 = 5 + 3*关键点数；Initialize 时从模型元数据探测，
        // 元数据全是动态维(-1)时延迟到首次推理再确定
        private int? _featureDim;
        private bool _disposed;

        // 复用缓冲（与 YoloV26Detector 同一思想：实例串行调用，逐帧 new 大数组
        // 会造成 LOH/GC 压力；姿态是"每人每帧"一次预处理，多人场景收益更大）。
        // _cropRawBytes/_cropPixels 按模型输入尺寸分配；_candidateVec 按特征维分配。
        private byte[] _cropRawBytes;
        private float[] _cropPixels;
        private float[] _candidateVec;

        /// <summary>
        /// 单帧最多做姿态推理的人数上限。工厂场景通常 1~5 人；
        /// 超出时丢弃置信度最低的（返回空关键点），保证最坏情况耗时有上界。
        /// </summary>
        public int MaxPersonsPerFrame { get; set; } = 8;

        /// <summary>
        /// 人体框扩边比例（相对框宽高）。裁剪过紧会导致贴边的肩/腕关键点丢失。
        ///
        /// v2.8 由 0.15 提升到 0.30：实测 YOLO 人体框收紧在躯干，工人伸手指向
        /// 静电杆/茶杯时手臂常伸出框外 20%~40%，扩边 15% 时手腕根本不在裁剪图里，
        /// 姿态推理看不到手 → 手腕关键点置信度低被过滤 → 触摸判定失效
        /// （表现为"人手摸了 ROI 但框不变绿、无日志"）。
        /// v2.9 由 0.30 提升到 0.35：人在画面边缘/贴近摄像头时手臂出框比例更大。
        /// 不建议超过 0.5：裁剪图里混入第二个人后 top1 候选可能取错目标。
        /// </summary>
        public float CropExpandRatio { get; set; } = 0.35f;

        public bool IsInitialized => _session != null;

        public float PersonConfidenceThreshold { get; set; } = 0.30f;

        public void Initialize(string modelPath)
        {
            // 与 YoloV26Detector 相同的防御顺序：先抛语义准确的 ObjectDisposedException
            ThrowIfDisposed();

            if (string.IsNullOrEmpty(modelPath))
                throw new ArgumentNullException(nameof(modelPath));
            if (!System.IO.File.Exists(modelPath))
                throw new System.IO.FileNotFoundException("姿态模型文件不存在", modelPath);

            var sessionOptions = new SessionOptions
            {
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_EXTENDED
            };

            try
            {
                _session = new InferenceSession(modelPath, sessionOptions);

                var inputMeta = _session.InputMetadata.Values.First();
                int[] inputShape = inputMeta.Dimensions.ToArray();
                if (inputShape.Length >= 4)
                {
                    _inputHeight = inputShape[2];
                    _inputWidth = inputShape[3];
                }

                _featureDim = ProbeFeatureDim();
                if (_featureDim.HasValue)
                {
                    LogManager.YoloLog($"[Pose] 模型加载成功, 输入={_inputWidth}x{_inputHeight}, 特征维={_featureDim.Value}");
                }
                else
                {
                    // 输出维度全动态时只能等首帧真实输出再判定
                    LogManager.YoloLog("[Pose] 模型加载成功, 输出维度为动态, 将在首次推理时确定特征维");
                }
            }
            catch (Exception ex)
            {
                _session?.Dispose();
                _session = null;
                throw new InvalidOperationException("姿态模型初始化失败", ex);
            }
        }

        public List<PoseResult> Detect(Mat frame, List<DetectionResult> persons)
        {
            ThrowIfDisposed();
            if (!IsInitialized) throw new InvalidOperationException("姿态检测器尚未初始化");
            if (frame == null || frame.Empty())
            {
                return BuildEmptyResults(persons);
            }

            try
            {
                var results = new List<PoseResult>();
                if (persons == null || persons.Count == 0)
                {
                    return results;
                }

                // 按人体置信度取前 N 个参与推理，其余直接给空结果（保持一一对应）
                var ordered = persons.OrderByDescending(p => p.Confidence).ToList();
                var inferenceSet = new HashSet<DetectionResult>(
                    ordered.Take(MaxPersonsPerFrame));

                foreach (var person in persons)
                {
                    var result = new PoseResult
                    {
                        Person = person,
                        PersonConfidence = person.Confidence
                    };

                    if (inferenceSet.Contains(person))
                    {
                        DetectSinglePerson(frame, person, result.Keypoints);
                    }

                    results.Add(result);
                }

                return results;
            }
            catch (Exception ex)
            {
                LogManager.GeneralLog($"[Pose] 姿态检测异常: {ex.Message}");
                return BuildEmptyResults(persons);
            }
        }

        // ==================== 单人推理 ====================

        /// <summary>对单个人体裁剪小图推理，把还原后的关键点写入 output。</summary>
        private void DetectSinglePerson(Mat frame, DetectionResult person, List<PoseKeypoint> output)
        {
            // 1. 扩边裁剪：限制在画面内；宽高至少 8px，防止退化矩形让 Resize 抛异常
            Rect crop = ExpandRect(person, frame.Cols, frame.Rows);

            using (Mat cropMat = new Mat(frame, crop))
            {
                // 2. letterbox 预处理（与整帧检测同一套张量布局约定）
                var preprocess = PreprocessCrop(cropMat);

                // 3. 推理并解析 top1 候选（单人裁剪图内基本只有一个目标，取最高分即可）
                float bestScore = -1f;
                float[] bestVec = null;

                string inputName = _session.InputNames.First();
                using (var outputs = _session.Run(new[]
                {
                    NamedOnnxValue.CreateFromTensor(inputName, preprocess.Tensor)
                }))
                {
                    var tensor = outputs.First().Value as Tensor<float>;
                    if (tensor == null)
                    {
                        return;
                    }

                    EnsureFeatureDim(tensor);
                    CollectBestCandidate(tensor, ref bestScore, ref bestVec);
                }

                if (bestVec == null || bestScore < PersonConfidenceThreshold)
                {
                    return;
                }

                // 4. 坐标还原：模型输入空间 → 裁剪图空间(减pad、除scale) → 原图空间(+crop偏移)
                // 注意：bestVec 引用的是实例复用缓冲 _candidateVec，必须在本方法内
                // 立即消费完毕（下面的还原循环）——绝不能存下来跨帧/跨人使用，
                // 否则下一次推理会把它覆盖。本方法即是这么做的，时序安全。
                float scale = preprocess.Scale;
                float padX = preprocess.PadX;
                float padY = preprocess.PadY;

                for (int k = 0; k < CocoKeyPointIndexes.TotalCount; k++)
                {
                    int baseIdx = 5 + k * 3;
                    float kx = bestVec[baseIdx];
                    float ky = bestVec[baseIdx + 1];
                    float kc = bestVec[baseIdx + 2];

                    output.Add(new PoseKeypoint
                    {
                        X = (kx - padX) / scale + crop.X,
                        Y = (ky - padY) / scale + crop.Y,
                        Confidence = kc
                    });
                }
            }
        }

        /// <summary>
        /// 在输出张量中找分数最高的候选，把其完整特征行(5+3*K 个值)拷入 bestVec。
        /// 兼容 [1,C,N]（ultralytics 导出默认）与 [1,N,C] 两种布局。
        ///
        /// 性能：沿用主检测器的整块展平快路（Tensor 索引器每次调用分配 params 数组，
        /// 8400 候选逐个扫描开销显著）；胜者特征行写入实例复用缓冲 _candidateVec，
        /// 不再每候选 new 一个数组（原来是每帧每人最多 8400 次分配）。
        /// </summary>
        private void CollectBestCandidate(Tensor<float> tensor, ref float bestScore, ref float[] bestVec)
        {
            int featureDim = _featureDim.Value;
            int[] shape = tensor.Dimensions.ToArray();

            bool transposed;               // true: [1,C,N] 取 t[0,c,i]
            int numCandidates;
            if (shape.Length == 3 && shape[1] == featureDim)
            {
                transposed = true;
                numCandidates = shape[2];
            }
            else if (shape.Length == 3 && shape[2] == featureDim)
            {
                transposed = false;
                numCandidates = shape[1];
            }
            else
            {
                LogManager.GeneralLog($"[Pose] 输出形状 {string.Join(",", shape)} 与特征维 {featureDim} 不符，跳过本帧");
                return;
            }

            float[] flat = TryFlattenPose(tensor, numCandidates, featureDim);

            if (_candidateVec == null || _candidateVec.Length != featureDim)
            {
                _candidateVec = new float[featureDim];
            }

            for (int i = 0; i < numCandidates; i++)
            {
                float score = PoseVal(tensor, flat, transposed, numCandidates, featureDim, 4, i);
                if (score <= bestScore || score < PersonConfidenceThreshold)
                {
                    continue;
                }

                for (int c = 0; c < featureDim; c++)
                {
                    _candidateVec[c] = PoseVal(tensor, flat, transposed, numCandidates, featureDim, c, i);
                }

                bestScore = score;
                bestVec = _candidateVec;
            }
        }

        /// <summary>姿态输出张量展平（与主检测器 TryFlatten 同一原理，行主序连续才成功）</summary>
        private float[] TryFlattenPose(Tensor<float> tensor, int numCandidates, int featureDim)
        {
            try
            {
                DenseTensor<float> dense = tensor as DenseTensor<float>;
                if (dense == null)
                {
                    return null;
                }
                var span = dense.Buffer.Span;
                if (span.Length != numCandidates * featureDim)
                {
                    return null;
                }
                // 候选扫描是只读比对，不必拷入复用缓冲：直接 ToArray 一次拷贝即可。
                // （主检测器需要复用是因为逐元素 TryAdd；这里拷贝一次的代价远小于
                // 8400 次索引器调用，且避免实例缓冲被跨帧复写导致胜者数据污染。）
                return span.ToArray();
            }
            catch
            {
                return null;
            }
        }

        private static float PoseVal(Tensor<float> t, float[] flat,
            bool transposed, int numCandidates, int featureDim, int dim, int i)
        {
            if (flat != null)
            {
                return transposed ? flat[dim * numCandidates + i] : flat[i * featureDim + dim];
            }
            return transposed ? t[0, dim, i] : t[0, i, dim];
        }

        // ==================== 预处理 ====================

        private struct CropPreprocessResult
        {
            public DenseTensor<float> Tensor;
            public float Scale;
            public float PadX, PadY;
        }

        /// <summary>
        /// BGR 裁剪图 → CHW RGB 归一化张量（letterbox 居中 + 黑边填充）。
        /// 实现与 YoloV26Detector.PreprocessMat 同一套高性能套路
        /// （整块 Marshal.Copy + 单层循环填通道），刻意不抽公共类以避免触碰已验证代码。
        /// </summary>
        private CropPreprocessResult PreprocessCrop(Mat mat)
        {
            int width = mat.Cols;
            int height = mat.Rows;

            float scale = Math.Min((float)_inputWidth / width, (float)_inputHeight / height);
            float scaledWidth = width * scale;
            float scaledHeight = height * scale;
            float padX = (_inputWidth - scaledWidth) / 2;
            float padY = (_inputHeight - scaledHeight) / 2;

            using (var canvas = new Mat(_inputHeight, _inputWidth, MatType.CV_8UC3, new Scalar(0, 0, 0)))
            using (var scaled = new Mat())
            {
                Cv2.Resize(mat, scaled, new Size((int)scaledWidth, (int)scaledHeight));

                var roi = new Rect((int)padX, (int)padY, scaled.Cols, scaled.Rows);
                // 子头必须释放（同 YoloV26Detector.PreprocessMat 的泄漏修复）
                using (var roiView = canvas[roi])
                {
                    scaled.CopyTo(roiView);
                }

                int pixelCount = _inputHeight * _inputWidth;
                int byteCount = pixelCount * 3;
                if (_cropRawBytes == null || _cropRawBytes.Length < byteCount)
                {
                    _cropRawBytes = new byte[byteCount];
                }
                if (_cropPixels == null || _cropPixels.Length != pixelCount * 3)
                {
                    _cropPixels = new float[pixelCount * 3];
                }
                byte[] rawBytes = _cropRawBytes;
                float[] pixels = _cropPixels;

                Mat continuous = canvas.IsContinuous() ? canvas : canvas.Clone();
                try
                {
                    System.Runtime.InteropServices.Marshal.Copy(continuous.Data, rawBytes, 0, byteCount);
                }
                finally
                {
                    if (!ReferenceEquals(continuous, canvas))
                    {
                        continuous.Dispose();
                    }
                }

                // BGR(HWC) → RGB(CHW) + /255 归一化（pixels 为实例复用缓冲）
                int rOffset = 0;
                int gOffset = pixelCount;
                int bOffset = pixelCount * 2;

                for (int i = 0; i < pixelCount; i++)
                {
                    int byteIdx = i * 3;
                    pixels[rOffset + i] = rawBytes[byteIdx + 2] / 255.0f; // R
                    pixels[gOffset + i] = rawBytes[byteIdx + 1] / 255.0f; // G
                    pixels[bOffset + i] = rawBytes[byteIdx] / 255.0f;     // B
                }

                return new CropPreprocessResult
                {
                    Tensor = new DenseTensor<float>(pixels, new[] { 1, 3, _inputHeight, _inputWidth }),
                    Scale = scale,
                    PadX = padX,
                    PadY = padY
                };
            }
        }

        // ==================== 辅助 ====================

        /// <summary>人体框四周扩边后与画面求交的裁剪矩形。</summary>
        private Rect ExpandRect(DetectionResult person, int frameWidth, int frameHeight)
        {
            float expandW = person.Width * CropExpandRatio;
            float expandH = person.Height * CropExpandRatio;

            float left = Math.Max(0, person.Left - expandW);
            float top = Math.Max(0, person.Top - expandH);
            float right = Math.Min(frameWidth, person.Right + expandW);
            float bottom = Math.Min(frameHeight, person.Bottom + expandH);

            int w = Math.Max(8, (int)(right - left));
            int h = Math.Max(8, (int)(bottom - top));
            // 右/下越界时往回收，保证 rect 完整落在画面内
            int x = Math.Min((int)left, Math.Max(0, frameWidth - w));
            int y = Math.Min((int)top, Math.Max(0, frameHeight - h));
            return new Rect(x, y, w, h);
        }

        /// <summary>
        /// 从输出元数据探测特征维（= 5+3*K）：非动态维度中满足 (d-5)%3==0 且 d>=8 的最小者。
        /// 探测不到（全动态维）返回 null，由首次真实推理补判。
        /// </summary>
        private int? ProbeFeatureDim()
        {
            var outputMeta = _session.OutputMetadata.Values.FirstOrDefault();
            if (outputMeta == null)
            {
                return null;
            }

            int? candidate = null;
            foreach (int d in outputMeta.Dimensions)
            {
                if (d > 8 && (d - 5) % 3 == 0 && (candidate == null || d < candidate.Value))
                {
                    candidate = d;
                }
            }
            return candidate;
        }

        /// <summary>首次推理时若特征维仍未定，用真实输出的形状补判。</summary>
        private void EnsureFeatureDim(Tensor<float> tensor)
        {
            if (_featureDim.HasValue)
            {
                return;
            }

            int[] shape = tensor.Dimensions.ToArray();
            if (shape.Length == 3)
            {
                // 两个候选维里选满足 (d-5)%3==0 的那个
                foreach (int d in new[] { shape[1], shape[2] })
                {
                    if (d > 8 && (d - 5) % 3 == 0)
                    {
                        _featureDim = d;
                        LogManager.YoloLog($"[Pose] 特征维确定为 {d}（关键点 {(d - 5) / 3} 个）");
                        return;
                    }
                }
            }

            throw new InvalidOperationException(
                $"无法从输出形状 [{string.Join(",", shape)}] 判定姿态特征维，请确认加载的是 YOLO-pose 系列模型");
        }

        private static List<PoseResult> BuildEmptyResults(List<DetectionResult> persons)
        {
            var results = new List<PoseResult>();
            if (persons != null)
            {
                foreach (var p in persons)
                {
                    results.Add(new PoseResult { Person = p, PersonConfidence = p.Confidence });
                }
            }
            return results;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(YoloPoseDetector));
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                if (_session != null)
                {
                    _session.Dispose();
                    _session = null;
                }
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }
    }
}
