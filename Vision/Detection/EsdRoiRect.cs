namespace YoloDetection
{
    /// <summary>
    /// 静电杆 ROI 像素矩形（浮点）。
    /// 刻意不用 OpenCvSharp.Rect（整型）：归一化坐标换算与容差判定全程浮点，
    /// 避免取整误差让贴着杆沿的手腕点忽进忽出。
    /// </summary>
    public struct EsdRoiRect
    {
        /// <summary>左上角 X（像素）</summary>
        public float X;

        /// <summary>左上角 Y（像素）</summary>
        public float Y;

        /// <summary>宽（像素）</summary>
        public float W;

        /// <summary>高（像素）</summary>
        public float H;

        /// <summary>点是否在矩形外扩 margin 后的范围内（margin 为容差像素）</summary>
        public bool Contains(float px, float py, float margin)
        {
            return px >= X - margin && px <= X + W + margin &&
                   py >= Y - margin && py <= Y + H + margin;
        }
    }
}
