using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace YoloDetection
{
    /// <summary>
    /// YOLO V26 ONNX 检测器（IYoloDetector 默认实现）。
    ///
    /// 职责边界：
    ///   - 本类只负责"预处理 → 推理 → 坐标还原 + NMS"，产出原始候选结果
    ///   - 边界裁剪/尺寸过滤等业务规则由 IDetectionResultProcessor 负责
    ///   - 目标类别通过 TargetClassIds 配置（默认仅检测 person）
    ///
    /// 性能说明：
    ///   预处理使用 Marshal.Copy 整块拷贝像素 + 单层循环填充 CHW 张量，
    ///   避免 Mat.Get&lt;Vec3b&gt; 的百万次 P/Invoke 调用（640x640 约 30~80ms → 1~3ms）。
    ///
    /// 注意：实例不是线程安全的；同一实例的 Detect 必须串行调用（管道内部已保证）。
    /// </summary>
    public class YoloV26Detector : IYoloDetector
    {
        private InferenceSession _session;
        private int _inputWidth = 640;
        private int _inputHeight = 640;
        private bool _disposed;

        // 预处理复用缓冲（实例单线程串行调用契约，见类注释）：
        // 640x640 输入下 rawBytes 约 1.2MB、pixels 约 4.9MB，逐帧 new 会给 GC
        // 造成持续大对象压力（85KB 以上进 LOH，LOH 只在昂贵的 Gen2 回收时整理）。
        // 缓冲按输入尺寸分配、尺寸变化时重建；复用不改变任何推理数值结果。
        private byte[] _preprocessRawBytes;
        private float[] _preprocessPixels;

        public bool IsInitialized => _session != null;

        public float ConfidenceThreshold { get; set; } = 0.35f;

        public float NmsThreshold { get; set; } = 0.5f;

        /// <summary>要保留的目标类别集合（COCO classId），默认仅 person(0)</summary>
        public HashSet<int> TargetClassIds { get; } = new HashSet<int> { 0 };

        public void Initialize(string modelPath)
        {
            // 已释放检查必须在最前：Dispose 后调用本方法必须抛语义准确的
            // ObjectDisposedException，而不是被后续校验抢先把异常类型误导成
            // ArgumentNullException/FileNotFoundException（与 Detect 保持同一模式）
            ThrowIfDisposed();

            if (string.IsNullOrEmpty(modelPath))
                throw new ArgumentNullException(nameof(modelPath));
            if (!System.IO.File.Exists(modelPath))
                throw new System.IO.FileNotFoundException("YOLO模型文件不存在", modelPath);

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

                LogManager.YoloLog($"[YOLO] 模型加载成功, 输入={_inputWidth}x{_inputHeight}");
            }
            catch (Exception ex)
            {
                _session?.Dispose();
                _session = null;
                throw new InvalidOperationException("YOLO模型初始化失败", ex);
            }
        }

        public List<DetectionResult> Detect(Mat mat)
        {
            // 已释放检查必须在前：Dispose 后 _session 为空，若先走 IsInitialized
            // 会抛出语义不准的"尚未初始化"，而非 ObjectDisposedException
            ThrowIfDisposed();
            if (!IsInitialized) throw new InvalidOperationException("YOLO检测器尚未初始化");
            if (mat == null || mat.Empty()) return new List<DetectionResult>();

            try
            {
                int width = mat.Cols;
                int height = mat.Rows;

                var preprocess = PreprocessMat(mat);

                return RunInference(preprocess.Tensor,
                    preprocess.ScaleX, preprocess.ScaleY, preprocess.PadX, preprocess.PadY,
                    width, height);
            }
            catch (Exception ex)
            {
                LogManager.GeneralLog($"[YOLO] 检测异常: {ex.Message}");
                if (ex.InnerException != null)
                {
                    LogManager.GeneralLog($"[YOLO] 内部异常: {ex.InnerException.Message}");
                }
                return new List<DetectionResult>();
            }
        }

        // ==================== 推理与后处理 ====================

        /// <summary>
        /// NMS 前的原始候选上限（病态保护）。
        /// 正常阈值下单帧候选远小于此值（行为与原来完全一致）；极端低阈值+拥挤画面
        /// 可能产出数千候选使 O(n^2) 的 NMS 卡帧，超限时按置信度截断（只影响病态场景）。
        /// </summary>
        public int MaxRawCandidates { get; set; } = 2000;

        /// <summary>执行推理并做坐标还原 + NMS。outputs 在本方法内负责释放。</summary>
        private List<DetectionResult> RunInference(Tensor<float> inputTensor,
            float scaleX, float scaleY, float padX, float padY,
            int origWidth, int origHeight)
        {
            var results = new List<DetectionResult>();

            string inputName = _session.InputNames.First();
            using (var outputs = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, inputTensor) }))
            {
                var outputTensor = outputs.First().Value as Tensor<float>;
                if (outputTensor == null)
                {
                    return results;
                }

                ParseOutput(outputTensor, results, scaleX, scaleY, padX, padY, origWidth, origHeight);
            }

            // 病态保护：候选超限时先按置信度截断再 NMS（正常场景走不到这里）。
            // 注意截断前先降序，保证留下的是置信度最高的候选。
            if (results.Count > MaxRawCandidates && MaxRawCandidates > 0)
            {
                results = results.OrderByDescending(d => d.Confidence).Take(MaxRawCandidates).ToList();
            }

            // NMS 去重 + 置信度排序 + 最多保留 5 个目标
            var filtered = ApplyNms(results);
            return filtered.OrderByDescending(d => d.Confidence).Take(5).ToList();
        }

        private void ParseOutput(Tensor<float> outputTensor, List<DetectionResult> results,
            float scaleX, float scaleY, float padX, float padY,
            int origWidth, int origHeight)
        {
            int[] shapeArr = outputTensor.Dimensions.ToArray();

            int numCandidates, vecLen;
            bool transposed = false;
            if (shapeArr.Length == 3)
            {
                // 方向判定必须双向校验：旧逻辑只看 shape[1]<=7，当候选数 N 本身很小
                //（如 [1,5,84]：5 个候选的 84 维原始预测）会被误判为转置布局，
                // 索引越界后被 Detect 吞掉变空结果（现场表现为偶发整帧漏检）。
                // 真实模型的两种布局在此规则下与旧逻辑结论完全一致：
                //   NMS 输出 [1,D,N]（D<=7，N 巨大）→ 转置分支；
                //   原始预测 [1,N,D]（N 巨大或 D>=8）→ 非转置分支。
                int dim1 = shapeArr[1], dim2 = shapeArr[2];
                bool dim1IsVec = dim1 >= 6; // 可作为特征维（NMS 需>=6，原始预测需>=8）
                bool dim2IsVec = dim2 >= 6;
                if (dim1IsVec && !dim2IsVec)
                {
                    vecLen = dim1; numCandidates = dim2; transposed = true;  // [1,D,N]
                }
                else if (dim2IsVec && !dim1IsVec)
                {
                    numCandidates = dim1; vecLen = dim2; transposed = false; // [1,N,D]
                }
                else if (dim1 <= 7 && dim2 > 7)
                {
                    vecLen = dim1; numCandidates = dim2; transposed = true;  // 典型 NMS 输出
                }
                else if (dim1 > 0 && dim2 > 0)
                {
                    numCandidates = dim1; vecLen = dim2; transposed = false; // 默认原始预测
                }
                else
                {
                    return;
                }
            }
            else if (shapeArr.Length == 2)
            {
                numCandidates = shapeArr[0];
                vecLen = shapeArr[1];
                if (vecLen < 6 || numCandidates <= 0)
                {
                    return; // 非法形状：拒绝而非越界崩溃
                }
            }
            else
            {
                return;
            }

            bool isNmsOutput = vecLen <= 7; // 6=角点+conf+cls, 7=角点+conf+cls+?

            // 非法特征维直接拒绝：NMS 输出至少 6 维，原始预测至少 8 维。
            // 旧实现无此检查（如 [1,8400,5]），GetVal 下标越界后被 Detect 吞掉变空结果；
            // 现显式返回空列表——同样空结果，但路径明确、可单测锁定。
            if (vecLen < 6)
            {
                return;
            }

            // 快路：输出张量一次性整块拷入托管数组，后续直接下标访问。
            // 背景：Tensor 索引器 t[0,i,d] 每次调用都 new 一个 params int[]（托管分配）
            // 还要做边界检查+步长换算——8400 候选 x 84 维就是每帧 70 万次调用、上百万
            // 小对象，Gen0 压力巨大。整块拷贝一次（约 2.8MB，0.3ms）后直接算偏移，
            // 与索引器逐个取值在数值上完全一致（行主序连续布局，见 TryFlatten 注释）。
            // 拷贝失败/布局不符时回退索引器（flat=null），结果不变只是慢一点。
            float[] flat = TryFlatten(outputTensor, numCandidates, vecLen);

            for (int i = 0; i < numCandidates; i++)
            {
                if (isNmsOutput)
                {
                    // === NMS 输出: [x1,y1,x2,y2,conf,cls]（模型输入空间角点格式）===
                    float x1 = GetVal(outputTensor, flat, transposed, numCandidates, vecLen, 0, i);
                    float y1 = GetVal(outputTensor, flat, transposed, numCandidates, vecLen, 1, i);
                    float x2 = GetVal(outputTensor, flat, transposed, numCandidates, vecLen, 2, i);
                    float y2 = GetVal(outputTensor, flat, transposed, numCandidates, vecLen, 3, i);
                    float confidence = GetVal(outputTensor, flat, transposed, numCandidates, vecLen, 4, i);
                    int classId = (int)GetVal(outputTensor, flat, transposed, numCandidates, vecLen, 5, i);

                    // 模型输入空间(带padding的640x640) → 原图空间
                    x1 = (x1 - padX) * scaleX;
                    x2 = (x2 - padX) * scaleX;
                    y1 = (y1 - padY) * scaleY;
                    y2 = (y2 - padY) * scaleY;

                    TryAdd(results, confidence, classId,
                        (x1 + x2) / 2f, (y1 + y2) / 2f, x2 - x1, y2 - y1,
                        origWidth, origHeight);
                }
                else
                {
                    // === 原始预测输出: [cx,cy,w,h,obj_conf,cls_0..cls_n] ===
                    float cx = GetVal(outputTensor, flat, transposed, numCandidates, vecLen, 0, i);
                    float cy = GetVal(outputTensor, flat, transposed, numCandidates, vecLen, 1, i);
                    float bw = GetVal(outputTensor, flat, transposed, numCandidates, vecLen, 2, i);
                    float bh = GetVal(outputTensor, flat, transposed, numCandidates, vecLen, 3, i);
                    float confidence = GetVal(outputTensor, flat, transposed, numCandidates, vecLen, 4, i);

                    float maxScore = float.MinValue;
                    int classId = -1;
                    for (int c = 5; c < vecLen; c++)
                    {
                        float score = GetVal(outputTensor, flat, transposed, numCandidates, vecLen, c, i);
                        if (score > maxScore)
                        {
                            maxScore = score;
                            classId = c - 5;
                        }
                    }
                    confidence *= maxScore;

                    // 归一化坐标 → 模型输入空间
                    if (cx < 2f && cy < 2f && bw < 2f && bh < 2f)
                    {
                        cx *= _inputWidth; cy *= _inputHeight;
                        bw *= _inputWidth; bh *= _inputHeight;
                    }

                    // 模型输入空间 → 原图空间
                    TryAdd(results, confidence, classId,
                        (cx - padX) * scaleX, (cy - padY) * scaleY,
                        bw * scaleX, bh * scaleY,
                        origWidth, origHeight);
                }
            }
        }

        /// <summary>
        /// 输出张量整块展平（行主序）。仅当元素总数恰好等于 numCandidates*vecLen
        ///（batch=1 且连续）时成功；否则返回 null 由调用方回退索引器。
        /// DenseTensor 行主序连续时：非转置 [1,N,D] 的 (0,i,d) 在偏移 i*D+d；
        /// 转置 [1,D,N] 的 (0,d,i) 在偏移 d*N+i——与 GetVal 的换算一致。
        /// </summary>
        private float[] TryFlatten(Tensor<float> tensor, int numCandidates, int vecLen)
        {
            try
            {
                DenseTensor<float> dense = tensor as DenseTensor<float>;
                if (dense == null)
                {
                    return null;
                }
                var span = dense.Buffer.Span;
                long expect = (long)numCandidates * vecLen;
                if (span.Length != expect)
                {
                    return null;
                }
                if (_outputFlat == null || _outputFlat.Length < span.Length)
                {
                    _outputFlat = new float[span.Length];
                }
                span.CopyTo(new System.Span<float>(_outputFlat, 0, span.Length));
                return _outputFlat;
            }
            catch
            {
                return null;
            }
        }

        // 输出展平复用缓冲（与预处理缓冲同一复用思想；单线程串行调用，无需同步）
        private float[] _outputFlat;

        private static float GetVal(Tensor<float> t, float[] flat,
            bool transposed, int numCandidates, int vecLen, int dim, int i)
        {
            if (flat != null)
            {
                return transposed ? flat[dim * numCandidates + i] : flat[i * vecLen + dim];
            }
            return transposed ? t[0, dim, i] : t[0, i, dim];
        }

        /// <summary>
        /// 类别过滤 + 置信度过滤 + 边界裁剪后加入候选列表。
        /// 注意：这里不做尺寸过滤（由 ResultProcessor 统一处理），但会丢弃完全出界的框。
        /// </summary>
        private void TryAdd(List<DetectionResult> results, float confidence, int classId,
            float cx, float cy, float w, float h, int origWidth, int origHeight)
        {
            if (!TargetClassIds.Contains(classId))
                return;

            if (confidence < ConfidenceThreshold)
                return;

            // 裁剪到画面边界；完全出界才丢弃（人贴近镜头时框部分可见是正常情况）
            float left = Math.Max(0, cx - w / 2);
            float top = Math.Max(0, cy - h / 2);
            float right = Math.Min(origWidth, cx + w / 2);
            float bottom = Math.Min(origHeight, cy + h / 2);

            float cw = right - left;
            float ch = bottom - top;
            if (cw <= 0 || ch <= 0)
                return;

            results.Add(new DetectionResult
            {
                ClassId = classId,
                ClassName = GetClassName(classId),
                Confidence = confidence,
                X = left + cw / 2,
                Y = top + ch / 2,
                Width = cw,
                Height = ch
            });
        }

        private static string GetClassName(int classId)
        {
            string name;
            return YoloClasses.CocoClasses.TryGetValue(classId, out name) ? name : "class_" + classId;
        }

        private List<DetectionResult> ApplyNms(List<DetectionResult> detections)
        {
            if (detections == null || detections.Count <= 1)
                return detections ?? new List<DetectionResult>();

            var sorted = detections.OrderByDescending(d => d.Confidence).ToList();
            var result = new List<DetectionResult>();

            while (sorted.Count > 0)
            {
                var current = sorted[0];
                sorted.RemoveAt(0);
                result.Add(current);
                sorted.RemoveAll(d => ComputeIoU(current, d) >= NmsThreshold);
            }

            return result;
        }

        private static float ComputeIoU(DetectionResult a, DetectionResult b)
        {
            float ol = Math.Max(a.Left, b.Left), ot = Math.Max(a.Top, b.Top);
            float or = Math.Min(a.Right, b.Right), ob = Math.Min(a.Bottom, b.Bottom);
            float ow = Math.Max(0, or - ol), oh = Math.Max(0, ob - ot);
            float overlap = ow * oh;
            float areaA = a.Width * a.Height, areaB = b.Width * b.Height;
            return overlap / (areaA + areaB - overlap + 1e-6f);
        }

        // ==================== 预处理 ====================

        private struct PreprocessResult
        {
            public DenseTensor<float> Tensor;
            public float ScaleX, ScaleY, PadX, PadY;
        }

        /// <summary>
        /// BGR Mat → CHW 归一化张量（letterbox 缩放居中 + 黑边填充）。
        /// 返回的张量由调用方释放（Detect 中经 TensorScope 管理）。
        /// </summary>
        private PreprocessResult PreprocessMat(Mat mat)
        {
            int width = mat.Cols;
            int height = mat.Rows;

            // 计算缩放比例（保持宽高比）
            float scale = Math.Min((float)_inputWidth / width, (float)_inputHeight / height);
            float scaledWidth = width * scale;
            float scaledHeight = height * scale;
            float padX = (_inputWidth - scaledWidth) / 2;
            float padY = (_inputHeight - scaledHeight) / 2;

            using (var resizedMat = new Mat(_inputHeight, _inputWidth, MatType.CV_8UC3, new Scalar(0, 0, 0)))
            using (var scaledMat = new Mat())
            {
                Cv2.Resize(mat, scaledMat, new Size((int)scaledWidth, (int)scaledHeight));

                var roi = new Rect((int)padX, (int)padY, scaledMat.Cols, scaledMat.Rows);
                // 注意：mat[roi] 返回的是共享底层数据的子头（新的 native 句柄），
                // 必须 Dispose，否则每帧泄漏一个句柄，长期运行句柄数持续上涨。
                using (var roiView = resizedMat[roi])
                {
                    scaledMat.CopyTo(roiView);
                }

                // 整块拷贝像素数据（单次 P/Invoke），替代逐像素访问。
                // 缓冲复用：rawBytes/pixels 按输入尺寸只分配一次（见字段注释）。
                int pixelCount = _inputHeight * _inputWidth;
                int byteCount = pixelCount * 3;
                if (_preprocessRawBytes == null || _preprocessRawBytes.Length < byteCount)
                {
                    _preprocessRawBytes = new byte[byteCount];
                }
                // pixels 必须精确匹配（DenseTensor 构造校验数组长度==维度乘积；
                // 换模型导致输入尺寸变化时重建，否则抛长度异常）。
                // rawBytes 允许偏大（Marshal.Copy 按 byteCount 取数）。
                if (_preprocessPixels == null || _preprocessPixels.Length != pixelCount * 3)
                {
                    _preprocessPixels = new float[pixelCount * 3];
                }
                byte[] rawBytes = _preprocessRawBytes;
                float[] pixels = _preprocessPixels;

                Mat continuous = resizedMat.IsContinuous() ? resizedMat : resizedMat.Clone();
                try
                {
                    System.Runtime.InteropServices.Marshal.Copy(continuous.Data, rawBytes, 0, byteCount);
                }
                finally
                {
                    if (!ReferenceEquals(continuous, resizedMat))
                    {
                        continuous.Dispose();
                    }
                }

                // BGR(HWC) → RGB(CHW) + 归一化
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

                // DenseTensor 包装复用数组（构造不拷贝，按引用持有）——推理是同步阻塞
                // 调用，Run 返回后缓冲才可复用，时序安全。若将来改为异步推理必须重审此处。
                var tensor = new DenseTensor<float>(pixels, new[] { 1, 3, _inputHeight, _inputWidth });

                return new PreprocessResult
                {
                    Tensor = tensor,
                    // X/Y 方向分别计算缩放因子（scale 只保证一个方向填满，不能统一用 1/scale）
                    ScaleX = width / scaledWidth,
                    ScaleY = height / scaledHeight,
                    PadX = padX,
                    PadY = padY
                };
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(YoloV26Detector));
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
