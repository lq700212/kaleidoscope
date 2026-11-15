using System.Collections.Generic;

namespace YoloDetection
{
    /// <summary>
    /// 单个人体关键点（坐标系为原图像素坐标）。
    /// </summary>
    public class PoseKeypoint
    {
        /// <summary>关键点 X 坐标（原图像素）</summary>
        public float X { get; set; }

        /// <summary>关键点 Y 坐标（原图像素）</summary>
        public float Y { get; set; }

        /// <summary>关键点可见性置信度（0-1，低于阈值说明该点不可信）</summary>
        public float Confidence { get; set; }
    }

    /// <summary>
    /// 单个人的姿态检测结果：一个人体框 + 该框对应的 COCO 17 个关键点。
    ///
    /// 所有权契约：Persons 与 Keypoints 均为独立对象，
    /// 调用方可自由持有，修改不会影响检测器内部状态。
    /// </summary>
    public class PoseResult
    {
        /// <summary>
        /// 关联的人体检测框（与 Detect 传入的 persons 列表中的元素为同一实例，
        /// 便于订阅方把姿态归属回具体的人）。
        /// </summary>
        public DetectionResult Person { get; set; }

        /// <summary>
        /// COCO 17 关键点，顺序严格遵循 CocoKeyPointIndexes 定义。
        /// 整帧内未检出任何有效关键点时为空集合（不是 null，方便遍历）。
        /// </summary>
        public List<PoseKeypoint> Keypoints { get; } = new List<PoseKeypoint>();

        /// <summary>该人的整体检测置信度（来自人体框分数）</summary>
        public float PersonConfidence { get; set; }

        /// <summary>是否检出了有效关键点</summary>
        public bool HasKeypoints
        {
            get { return Keypoints != null && Keypoints.Count > 0; }
        }
    }

    /// <summary>
    /// COCO 17 人体关键点索引常量表（YOLO-pose 系列模型的固定输出顺序）。
    ///
    /// 索引速查（写规则判断时对照本表取点，不要硬编码魔法数字）：
    ///   0鼻 1左眼 2右眼 3左耳 4右耳
    ///   5左肩 6右肩 7左肘 8右肘 9左手腕 10右手腕
    ///   11左髋 12右髋 13左膝 14右膝 15左踝 16右踝
    /// </summary>
    public static class CocoKeyPointIndexes
    {
        public const int Nose = 0;
        public const int LeftEye = 1;
        public const int RightEye = 2;
        public const int LeftEar = 3;
        public const int RightEar = 4;
        public const int LeftShoulder = 5;
        public const int RightShoulder = 6;
        public const int LeftElbow = 7;
        public const int RightElbow = 8;
        public const int LeftWrist = 9;
        public const int RightWrist = 10;
        public const int LeftHip = 11;
        public const int RightHip = 12;
        public const int LeftKnee = 13;
        public const int RightKnee = 14;
        public const int LeftAnkle = 15;
        public const int RightAnkle = 16;

        /// <summary>COCO 姿态模型的关键点总数</summary>
        public const int TotalCount = 17;
    }
}
