namespace YoloDetection
{
    /// <summary>
    /// 单个目标的检测结果。
    /// X/Y 为检测框中心点坐标，Width/Height 为框宽高（均为原图像素坐标系）。
    /// </summary>
    public class DetectionResult
    {
        /// <summary>类别 ID</summary>
        public int ClassId { get; set; }

        /// <summary>类别名称</summary>
        public string ClassName { get; set; }

        /// <summary>置信度（0-1）</summary>
        public float Confidence { get; set; }

        /// <summary>检测框中心 X</summary>
        public float X { get; set; }

        /// <summary>检测框中心 Y</summary>
        public float Y { get; set; }

        public float Width { get; set; }

        public float Height { get; set; }

        public float Left => X - Width / 2;

        public float Top => Y - Height / 2;

        public float Right => X + Width / 2;

        public float Bottom => Y + Height / 2;
    }
}
