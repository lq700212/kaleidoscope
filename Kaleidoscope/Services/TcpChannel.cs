using System;
using System.Linq;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using HslCommunication;
using HslCommunication.Profinet.AllenBradley;
using Kaleidoscope.Utils;

namespace Kaleidoscope.Services
{
    // =====================================================================================
    // 通用 TCP / EtherNet-IP 通道（自 HJVision 生产工程下沉进库，行为与原实现一字不差）。
    // -------------------------------------------------------------------------------------
    // 【干什么】两种模式共用一个类，上层按 Protocol 切换：
    //   - "TCP"（默认）：裸 Socket 收码，按行切帧（CR / LF / CRLF 全兼容），
    //     典型场景是基恩士 SR 等"连上就推一行文本"的无协议扫码枪、机器人、网关；
    //   - "EIP"：走 AllenBradleyNet（CIP over EtherNet/IP）周期读 Assembly 取码，
    //     适用 EtherNet/IP 适配器型扫码枪（保持型数据需去重，见 EipPolling 注释）。
    //
    // 【为什么要这一层】库内已有的 ScannerTcpService 是"扫码枪专用"（自带触发指令、
    //   自动重连、条码事件语义）。而生产现场还有大量"只要一条能发能收的 TCP 长连接"
    //   的场景（机器人下发、网关透传、测试页裸发），语义更底层：本类只负责
    //   "连上 / 发一行 / 收一行 / 报断线"，重连策略交上层。
    //
    // 【重连红线】本类禁自重连：断线只抛 isconnect(false)，由主程序统一的重连任务恢复。
    //   原因是一台设备可能被多个业务入口共用，各自重连会打出多条连接把对端打满。
    //
    // 【线程模型】Connect() 建连后起一个后台轮询线程（IsBackground）阻塞收码；
    //   Dispose 走"置旗 + Join(300) 自然退出"，绝不 Thread.Abort（防撕断订阅方）。
    //   Connect 用 BeginConnect + WaitOne 强制超时，不可达 IP 不会拖十几秒。
    //
    // 【收码缓冲】按行切帧，一次读进多条时也能连续弹出（不依赖 Available==0）；
    //   无终止符膨胀到 8KB 记日志并丢弃——这是扫码枪终止符配错时的现场防御。
    // =====================================================================================
    public delegate void TcpChannelEventHandler(TcpChannelEventArgs param);
    public delegate void TcpChannelConnectHandler(int tcpID, bool sender);
    public delegate void TcpChannelDisconnectHandler(object sender);
    public delegate void TcpChannelRecvHandler(object sender);

    public class TcpChannelEventArgs : EventArgs
    {
        public string Message;
        public Socket client;
        /// <summary>发出本条码的设备号（= TcpChannel.TcpID；-1=未知）。发送侧直接带号，免反查。</summary>
        public int TcpID = -1;
        public TcpChannelEventArgs(string message, Socket Client)
        {
            Message = message;
            client = Client;
        }
    }

    public class TcpChannel : IDisposable
    {
        public event TcpChannelEventHandler clientevent;
        public event TcpChannelConnectHandler isconnect;

        IPAddress IP;
        IPEndPoint Host;
        public Socket ClientSocket;
        public Thread thpolling;

        private bool mark = false;
        private object ob = new object();
        /// <summary>已释放标志（热更替换旧客户端时重连任务见旗即退）。</summary>
        private volatile bool mDisposed = false;
        /// <summary>是否已释放。</summary>
        public bool IsDisposed { get { return mDisposed; } }
        private char[] HexDigits = { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', 'A', 'B', 'C', 'D', 'E', 'F', 'a', 'b', 'c', 'd', 'e', 'f' };

        public int TcpID { get; set; }

        /// <summary>
        /// TCP 建连手动超时（毫秒，默认 3000）。BeginConnect + WaitOne，不可达 IP 最多等
        /// ConnectTimeoutMs 即返回，不再由 OS 重传拖十几秒卡 UI。
        /// </summary>
        public int ConnectTimeoutMs = 3000;

        private static void CloseQuietly(Socket s)
        {
            try { if (s != null) s.Close(); }
            catch { }
        }

        #region EIP(EtherNet/IP) 模式支持

        /// <summary>通讯协议："TCP"=裸 TCP（默认）；"EIP"=EtherNet/IP（扫码枪作适配器，上位机作扫描器）。</summary>
        public string Protocol { get; set; }

        /// <summary>EIP 读取地址：扫码枪 Assembly 实例号（常见默认 "4"）。</summary>
        public string EipAddress { get; set; }

        /// <summary>EIP 单次读取字节数（条码最大长度）。</summary>
        public int EipReadLength { get; set; }

        /// <summary>EIP 轮询周期（毫秒）。</summary>
        public int EipPollingMs { get; set; }

        private AllenBradleyNet mEipClient;
        private volatile bool mEipOnline;
        private string mLastBarcode = "";

        /// <summary>是否工作在 EIP 模式（Protocol=="EIP"，其余一律按原 TCP 方式）。</summary>
        public bool IsEipMode
        {
            get { return string.Equals(Protocol, "EIP", StringComparison.OrdinalIgnoreCase); }
        }

        #endregion

        /// <summary>构造函数（协议默认 TCP）。</summary>
        public TcpChannel(string ip, string port)
        {
            IP = IPAddress.Parse(ip);
            Host = new IPEndPoint(IP, Convert.ToInt32(port));
            Protocol = "TCP";
            EipAddress = "4";
            EipReadLength = 64;
            EipPollingMs = 200;
        }

        public bool Connect()
        {
            if (IsEipMode) return ConnectEip();
            try
            {
                if (ClientSocket != null)
                {
                    CloseQuietly(ClientSocket);
                    ClientSocket = null;
                }

                ClientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                IAsyncResult mConnAr = null;
                try { mConnAr = ClientSocket.BeginConnect(Host, null, null); }
                catch { mConnAr = null; }
                if (mConnAr == null)
                {
                    CloseQuietly(ClientSocket);
                    ClientSocket = null;
                    return false;
                }
                bool mLinked = false;
                try { mLinked = mConnAr.AsyncWaitHandle.WaitOne(Math.Max(500, ConnectTimeoutMs)); }
                catch { mLinked = false; }
                finally { try { mConnAr.AsyncWaitHandle.Close(); } catch { } }
                if (!mLinked)
                {
                    CloseQuietly(ClientSocket);
                    ClientSocket = null;
                    return false;
                }
                try { ClientSocket.EndConnect(mConnAr); }
                catch
                {
                    CloseQuietly(ClientSocket);
                    ClientSocket = null;
                    return false;
                }

                if (IsConnected())
                {
                    mark = true;
                    // 只有当前没有存活的轮询线程时才新建（防断线重连后线程倍增瓜分数）。
                    if (thpolling == null || !thpolling.IsAlive)
                    {
                        thpolling = new Thread(Polling);
                        thpolling.IsBackground = true;
                        thpolling.Start();
                    }
                    return true;
                }
                else
                {
                    return false;
                }
            }
            catch
            {
                CloseQuietly(ClientSocket);
                ClientSocket = null;
                return false;
            }
        }

        /// <summary>
        /// EIP 模式连接：AllenBradleyNet(CIP over EtherNet/IP) 连扫码枪，成功后起轮询线程。
        /// Slot=0（扫码枪无机架槽位概念）。
        /// </summary>
        private bool ConnectEip()
        {
            try
            {
                if (mEipClient == null)
                {
                    mEipClient = new AllenBradleyNet(IP.ToString(), Host.Port);
                    mEipClient.ConnectTimeOut = 2000;
                    mEipClient.Slot = 0;
                    mEipClient.ReceiveTimeOut = 2000;
                }
                OperateResult connRet = mEipClient.ConnectServer();
                if (connRet.IsSuccess)
                {
                    mEipOnline = true;
                    if (thpolling == null || !thpolling.IsAlive)
                    {
                        thpolling = new Thread(EipPolling);
                        thpolling.IsBackground = true;
                        mark = true;
                        thpolling.Start();
                    }
                    return true;
                }
                mEipOnline = false;
                return false;
            }
            catch
            {
                mEipOnline = false;
                return false;
            }
        }

        /// <summary>
        /// EIP 收码轮询：周期读 Assembly → ASCII → clientevent。与 TCP 共用事件链路。
        /// 去重：保持型数据仅条码变化或中间出现过空才触发；读失败置 mEipOnline=false
        /// 并 isconnect(false)，由主程序重连任务恢复。
        /// </summary>
        private void EipPolling()
        {
            while (mark)
            {
                try
                {
                    OperateResult<byte[]> readRet = mEipClient.Read(EipAddress, (ushort)EipReadLength);
                    if (readRet.IsSuccess)
                    {
                        if (!mEipOnline)
                        {
                            mEipOnline = true;
                            if (isconnect != null) isconnect(TcpID, true);
                        }
                        string code = Encoding.ASCII.GetString(readRet.Content).Trim('\0', '\r', '\n', ' ', '\t');
                        if (code.Length > 0)
                        {
                            if (code != mLastBarcode)
                            {
                                mLastBarcode = code;
                                TcpChannelEventArgs args = new TcpChannelEventArgs(code, null);
                                args.TcpID = TcpID;
                                if (clientevent != null) { clientevent(args); }
                            }
                        }
                        else
                        {
                            mLastBarcode = "";
                        }
                    }
                    else
                    {
                        if (mEipOnline)
                        {
                            mEipOnline = false;
                            LogHelper.Info(" EIP设备" + TcpID + "通讯失败：" + readRet.Message);
                            if (isconnect != null) isconnect(TcpID, false);
                        }
                    }
                    Thread.Sleep(Math.Max(50, EipPollingMs));
                }
                catch
                {
                    Thread.Sleep(Math.Max(50, EipPollingMs));
                }
            }
        }

        private void Polling()
        {
            while (mark)
            {
                try
                {
                    string data = Recieve();
                    if (data != "")
                    {
                        TcpChannelEventArgs args = new TcpChannelEventArgs(data, ClientSocket);
                        args.TcpID = TcpID;
                        if (clientevent != null) { clientevent(args); }
                    }

                    if (isconnect != null)
                    {
                        if (IsConnected())
                        {
                        }
                        else
                        {
                            isconnect(TcpID, false);
                            return;
                        }
                    }
                    Thread.Sleep(100);
                }
                catch
                {
                }
            }
        }

        /// <summary>接收缓冲：无帧界定粘包时按行切分，一次只返回一条完整条码。仅 lock(ob) 内访问。</summary>
        private string mRxLineBuffer = "";

        private string TryPopLineFromBuffer(out bool hasMore)
        {
            hasMore = false;
            if (mRxLineBuffer.Length == 0) return null;

            int lf = mRxLineBuffer.IndexOf('\n');
            int cr = mRxLineBuffer.IndexOf('\r');

            if (lf < 0 && cr < 0) { hasMore = true; return null; }

            int cut;
            int skipLen;
            if (lf >= 0 && (cr < 0 || lf < cr))
            {
                cut = lf; skipLen = 1;
            }
            else
            {
                cut = cr;
                skipLen = (cut + 1 < mRxLineBuffer.Length && mRxLineBuffer[cut + 1] == '\n') ? 2 : 1;
            }

            string oneCode = mRxLineBuffer.Substring(0, cut).TrimEnd('\r', '\n', ' ');
            mRxLineBuffer = mRxLineBuffer.Substring(cut + skipLen);
            hasMore = mRxLineBuffer.IndexOf('\n') >= 0 || mRxLineBuffer.IndexOf('\r') >= 0;
            return oneCode;
        }

        /// <summary>接收缓冲当前字节数（只读诊断口：无终止符攒缓冲时"有N字节未成行"提示用）。</summary>
        public int RxBufferedBytes
        {
            get
            {
                lock (ob)
                {
                    try { return mRxLineBuffer == null ? 0 : mRxLineBuffer.Length; }
                    catch { return 0; }
                }
            }
        }

        /// <summary>只看不取：接收缓冲快照，不清空。</summary>
        public string PeekBufferedRaw()
        {
            lock (ob)
            {
                try { return mRxLineBuffer ?? ""; }
                catch { return ""; }
            }
        }

        /// <summary>取出并清空未成行缓冲（测试页"看原始缓冲"用；生产成帧逻辑零改动）。</summary>
        public string DrainBufferedRaw()
        {
            lock (ob)
            {
                try
                {
                    if (string.IsNullOrEmpty(mRxLineBuffer)) return "";
                    string raw = mRxLineBuffer;
                    mRxLineBuffer = "";
                    return raw;
                }
                catch { return ""; }
            }
        }

        public string Recieve()
        {
            lock (ob)
            {
                try
                {
                    // 先看缓冲里是否有完整行（上次一次读进多条时 Available==0 也能出第二枪）。
                    bool bufferedMore;
                    string buffered = TryPopLineFromBuffer(out bufferedMore);
                    if (buffered != null) return buffered;

                    if (IsConnected())
                    {
                        if (ClientSocket.Available > 0)
                        {
                            WaitReadAll(300);
                            byte[] data = new byte[ClientSocket.Available];
                            ClientSocket.Receive(data);
                            mRxLineBuffer += Encoding.ASCII.GetString(data);

                            string oneCode = TryPopLineFromBuffer(out bufferedMore);
                            if (oneCode != null)
                            {
                                if (mRxLineBuffer.Length > 8192)
                                {
                                    LogHelper.Info(string.Format(" 扫码设备[{0}]接收缓冲超8KB且未见终止符(CR/LF)，丢弃{1}字节——请检查扫码枪终止符配置",
                                        TcpID, mRxLineBuffer.Length));
                                    mRxLineBuffer = "";
                                }
                                return oneCode;
                            }
                            if (mRxLineBuffer.Length > 8192)
                            {
                                LogHelper.Info(string.Format(" 扫码设备[{0}]接收缓冲超8KB且未见终止符(CR/LF)，清空{1}字节——请检查扫码枪终止符配置",
                                    TcpID, mRxLineBuffer.Length));
                                mRxLineBuffer = "";
                            }
                            return "";
                        }
                        else
                        {
                            return "";
                        }
                    }
                    else
                    {
                        // 断线重连统一由主程序重连任务负责，本类禁自重连。
                        return "";
                    }
                }
                catch
                {
                    return "";
                }
            }
        }

        public bool IsConnected()
        {
            if (IsEipMode) return mEipOnline;
            lock (ob)
            {
                try
                {
                    if (ClientSocket != null && (!(ClientSocket.Poll(1000, SelectMode.SelectRead) && ClientSocket.Available == 0)) && ClientSocket.Connected)
                        return true;
                    else
                        return false;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// 裸发数据（不追加换行；EIP 不支持）。生产一律走 Send，只有测试页"裸发"调这里。
        /// </summary>
        public bool SendRaw(string str)
        {
            if (IsEipMode) return false;
            lock (ob)
            {
                try
                {
                    if (IsConnected())
                    {
                        byte[] data = Encoding.ASCII.GetBytes(str ?? "");
                        ClientSocket.Send(data);
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>发送数据（追加 \r\n；EIP 模式返回 false）。</summary>
        public bool Send(string str)
        {
            if (IsEipMode) return false;
            lock (ob)
            {
                try
                {
                    if (IsConnected())
                    {
                        byte[] data = Encoding.ASCII.GetBytes(str + "\r\n");
                        ClientSocket.Send(data);
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
            }
        }

        private bool CharInArray(char aChar, char[] charArray)
        {
            return (Array.Exists<char>(charArray, delegate (char a) { return a == aChar; }));
        }

        /// <summary>十六进制字符串转字节数组（过滤非 hex 字符）。</summary>
        private byte[] HexStringToByteArray(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char aChar in s)
            {
                if (CharInArray(aChar, HexDigits))
                    sb.Append(aChar);
            }
            s = sb.ToString();
            int bufferlength;
            if ((s.Length % 2) == 1)
                bufferlength = s.Length / 2 + 1;
            else bufferlength = s.Length / 2;
            byte[] buffer = new byte[bufferlength];
            for (int i = 0; i < bufferlength - 1; i++)
                buffer[i] = (byte)Convert.ToByte(s.Substring(2 * i, 2), 16);
            if (bufferlength > 0)
                buffer[bufferlength - 1] = (byte)Convert.ToByte(s.Substring(2 * (bufferlength - 1), (s.Length % 2 == 1 ? 1 : 2)), 16);
            return buffer;
        }

        /// <summary>字节数组转十六进制字符串。</summary>
        private string ByteArrayToHexString(byte[] data)
        {
            StringBuilder sb = new StringBuilder(data.Length * 3);
            foreach (byte b in data)
                sb.Append(Convert.ToString(b, 16).PadLeft(2, '0').PadRight(3, ' '));
            return sb.ToString().ToUpper();
        }

        /// <summary>等数据到齐（稳定无新增即返，最长 time 毫秒）。</summary>
        public void WaitReadAll(long time)
        {
            int prenum = ClientSocket.Available;
            int nownum = 0;
            for (int i = 0; i < time / 10; i++)
            {
                Thread.Sleep(10);
                nownum = ClientSocket.Available;
                if (prenum == nownum)
                    break;
                else
                {
                    prenum = nownum;
                }
            }
        }

        public void Dispose()
        {
            mark = false;
            mDisposed = true;
            if (IsEipMode)
            {
                try { if (mEipClient != null) mEipClient.ConnectClose(); } catch { }
                mEipOnline = false;
                return;
            }
            // 置旗 + Join 自然退出，不 Abort（防撕断订阅方）。
            if (thpolling != null)
            {
                try { thpolling.Join(300); } catch { }
                thpolling = null;
            }
            if (ClientSocket != null)
            {
                if (ClientSocket.Connected && (!ClientSocket.Poll(500, SelectMode.SelectRead)))
                {
                    try
                    {
                        ClientSocket.Shutdown(SocketShutdown.Both);
                        ClientSocket.Disconnect(true);
                    }
                    catch
                    {
                    }
                }
                ClientSocket.Close();
                ClientSocket = null;
            }
        }
    }
}
