// =====================================================================================
// 【下沉注记】本文件 2026-10-25 自 HJVision-WPF 生产工程（第二轮仪器设备下沉）
// 原样搬入，口径与第一轮 PlcNetCommQue/TcpChannel 下沉相同：
//   ①来源 GYZVision.Core/Devices/LedControl.cs（瑞视串口光源驱动）；
//   ②命名空间 → Kaleidoscope.Services；零其它改动（自包含，日志出口仍是静态
//      LedController.LogSink 注入，宿主不接则静默，原口径不变）。
//   其余算法/默认值/异常口径/注释全保留，公共 API 签名不变（主项目经
//   文件级 using 别名壳零改动引用，断点可跨工程打进本源码）。
// =====================================================================================
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;

namespace Kaleidoscope.Services
{
    // =====================================================================================
    // 瑞视串口光源驱动（老工程 SubClass/LedControl.cs，原样搬运）。
    // -------------------------------------------------------------------------------------
    // 【改动一处】Console.WriteLine 两处换 LogSink 委托（服务内静默惯例；不接则丢弃）。
    // Jiali/Monran 从未实现（调即抛 NotSupportedException 明示，见 InitilaAndConnectToLed）。
    // 协议：S+通道字母+4 位亮度+#（如 SB0250#），回 1 字节通道字母即成功。
    // =====================================================================================
    public class LedController
    {
        /// <summary>驱动内部日志出口（不接则丢弃，绝不抛）。</summary>
        public static Action<string> LogSink;

        private static void LogLocal(string msg)
        {
            try { if (LogSink != null) LogSink(msg); } catch { }
        }

        private static readonly object _padlock = new object();

        private static LedController _instance = new LedController();

        public Dictionary<LedInfo, ILedInterface> DicLeds = new Dictionary<LedInfo, ILedInterface>();

        public static LedController GetInstance()
        {
            lock (_padlock)
            {
                if (_instance == null)
                {
                    _instance = new LedController();
                }
            }
            return _instance;
        }

        private LedController()
        {
        }

        public static string GetNameByIndex(int camIndex)
        {
            return "LED" + ((char)(65 + camIndex)).ToString();
        }

        public static int GetIndexByName(string name)
        {
            return Encoding.ASCII.GetBytes(name.Substring(name.Length - 1, 1))[0] - 65;
        }

        public void InitilaAndConnectToLed(LedInfo[] ledInfos)
        {
            foreach (var item in ledInfos)
            {
                ILedInterface led = null;
                switch (item.LedType)
                {
                    case EnumLedType.Jiali:
                        throw new NotSupportedException("光源类型 Jiali 驱动未实现，请用 RuiShi 或补驱动");
                    case EnumLedType.Monran:
                        throw new NotSupportedException("光源类型 Monran 驱动未实现，请用 RuiShi 或补驱动");
                    case EnumLedType.RuiShi:
                        led = new LedRuiShi();
                        break;
                    default:
                        break;
                }
                led.Connect(item.Mark);
                DicLeds.Add(item, led);
            }
        }

        public void UnInitialLed()
        {
            foreach (var item in DicLeds.Values)
            {
                item.Close();
                item.DisConnect();
            }
            DicLeds.Clear();
        }

        public void Close()
        {
            foreach (var item in DicLeds.Values)
                item.Close();
        }

        public ILedInterface GetLedByIndex(int index)
        {
            return DicLeds.ElementAt(index).Value;
        }
    }

    public class LedRuiShi : ILedInterface
    {
        private readonly SerialPort _com = new SerialPort();

        private readonly object _comLock = new object();

        public override void Close()
        {
            if (!IsConnect) return;
            try
            {
                for (int i = 1; i <= 6; i++)
                    if (!SetLightLess(i, 0))
                        break;
            }
            catch (Exception)
            {
            }
        }

        public override bool Connect(string connectMark)
        {
            ConnectMark = connectMark;
            _com.PortName = ConnectMark;
            _com.Parity = Parity.None;
            _com.BaudRate = 19200;
            _com.StopBits = StopBits.One;
            _com.DataBits = 8;
            try
            {
                if (_com.IsOpen)
                    _com.Close();
                _com.ReadTimeout = 500;
                _com.WriteTimeout = 500;
                _com.Open();
                IsConnect = true;
            }
            catch (Exception ex)
            {
                LedController_Log(ex.Message);
                return false;
            }
            return true;
        }

        private static void LedController_Log(string msg)
        {
            try { if (LedController.LogSink != null) LedController.LogSink(msg); } catch { }
        }

        public override void DisConnect()
        {
            _com?.Close();
            IsConnect = false;
        }

        public override bool GetLightLess(int ch, out int ledLightless)
        {
            throw new NotImplementedException();
        }

        public override bool SetLightLess(int ch, int lightless)
        {
            if (!IsConnect)
            {
                LedController_Log("No conect!");
                return false;
            }
            Char feedback = '1';
            switch (ch)
            {
                case 1: feedback = 'A'; break;
                case 2: feedback = 'B'; break;
                case 3: feedback = 'C'; break;
                case 4: feedback = 'D'; break;
                case 5: feedback = 'E'; break;
                case 6: feedback = 'F'; break;
                default: break;
            }
            string strSend = "S" + feedback.ToString() + lightless.ToString("0000") + "#";
            try
            {
                byte[] buffer = new byte[1];
                int count = 0;
                lock (_comLock)
                {
                    _com.Write(strSend);
                    count = _com.Read(buffer, 0, 1);
                }
                if (count == 0 || buffer[0] != feedback)
                    return false;
            }
            catch (Exception ex)
            {
                LedController_Log(ex.Message);
                return false;
            }
            return true;
        }
    }

    public abstract class ILedInterface
    {
        public string ConnectMark { get; protected set; }

        public bool IsConnect { get; protected set; } = false;

        /// <summary>连接光源控制器。</summary>
        public abstract bool Connect(string connectMark);

        /// <summary>断开光源控制器。</summary>
        public abstract void DisConnect();

        /// <summary>设置光源亮度。</summary>
        public abstract bool SetLightLess(int ch, int lightless);

        /// <summary>获取光源亮度。</summary>
        public abstract bool GetLightLess(int ch, out int ledLightless);

        /// <summary>频闪光源。</summary>
        public virtual void Trigger(int ch, int timeDlay)
        {
            throw new NotImplementedException();
        }

        /// <summary>关闭光源。</summary>
        public abstract void Close();
    }

    public class LedInfo
    {
        public EnumLedType LedType { get; set; }

        public string Mark { get; set; }
    }

    public enum EnumLedType
    {
        Jiali,
        Monran,
        RuiShi
    }
}
