using System;
using System.Collections.Generic;
using System.IO;       // 用于读写"上次连接成功端口"的磁盘缓存文件
using System.IO.Ports;
using Kaleidoscope.Models;
using Kaleidoscope.Utils;
using NModbus;
using NModbus.Serial;

namespace Kaleidoscope.Services
{
    /// <summary>
    /// 气压表通讯实现（Modbus RTU / RS485）。
    /// 【来源】AgingTestSystem.Services.ModbusRtuBarometerReader 原样移植，配置类型由 DeviceConfig
    /// 换成独立强类型 <see cref="BarometerConfig"/>（跨项目接入时只依赖本库模型，不依赖业务配置）。
    ///
    /// 适用场景：
    /// - 气压表通过 RS485 转 USB（CH340 芯片）接入工控机
    /// - 上位机作为 Modbus 主站，定时轮询 1~N 个从站地址读取压力值
    ///
    /// 设计要点（给新手看的）：
    /// 1) SerialPort 不是线程安全的：同一时刻只允许一个线程读/写串口。
    ///    因此用 _syncRoot 做互斥锁，保证 Modbus 请求不会并发。
    /// 2) Modbus RTU 每一帧都包含从站地址：这里默认用 deviceId 作为从站地址（1~72）。
    ///    现场如果不是这个规则，需要改成"固定从站地址 + 不同寄存器/偏移"。
    /// 3) 寄存器地址/单位/缩放需要现场确认：代码保留了配置项，通线后按说明书修正即可。
    /// 4) 端口"工控机记忆"：连接成功后把实际端口写入程序目录 BarometerPort.cache，
    ///    下次启动优先用缓存端口直接连（省去重新搜索）；缓存端口失效（设备被拔/换口）再自动
    ///    重新识别 CH340 —— 与送风机 FanLastIp.cache 的机制一致（见 Connect/BuildCandidatePorts）。
    ///
    /// 【串口心跳（V1.16.2 沉淀）】单台设备"无响应"（超时/Modbus 异常码）是正常离线，不误判；
    /// 只有"端口级故障"（RS485 适配器被拔出/端口被占用/端口已关闭）才把 _isConnected 置 false，
    /// 让上层（DeviceHub 的连接监控）感知并后台自动重连。判定见 <see cref="IsPortLevelFailure"/>。
    /// </summary>
    public class ModbusRtuBarometerReader : IBarometerReader
    {
        /// <summary>
        /// 串口/主站对象的互斥锁。
        /// 为什么需要锁：SerialPort 不是线程安全的；NModbus 的 Master 也不应该在多线程同时发请求；
        /// 并发读写会导致帧交叉，出现 CRC 错误、超时、甚至串口假死。
        /// 正常情况下采集在单一后台线程进行不会并发，但保留锁可防止后续扩展（如手动读某一路）造成并发问题。
        /// </summary>
        private readonly object _syncRoot = new object();

        /// <summary>全局配置（Connect 时赋值，Disconnect 时不置空——方便错误排查看配置；ReadAllData 有判空保护）。</summary>
        private BarometerConfig _config;

        /// <summary>串口对象（RS485 转 USB 后会表现为一个 COM 口）。</summary>
        private SerialPort _serialPort;

        /// <summary>Modbus 主站对象（通过 NModbus 创建，RTU 模式）。</summary>
        private IModbusMaster _master;

        /// <summary>连接状态标志。volatile：ConnectionMonitor 心跳线程在锁外读 IsConnected 决定是否重连。</summary>
        private volatile bool _isConnected;

        /// <summary>实际使用的串口名称（自动识别或配置指定的结果）。</summary>
        public string CurrentPortName { get; private set; }

        /// <summary>磁盘缓存文件名（程序 exe 所在目录）：一行文本 = 本工控机最近一次连接成功的气压表串口。</summary>
        private const string PortCacheFileName = "BarometerPort.cache";

        /// <summary>最近一次连接成功的串口名称（内存缓存，重连优先尝试它，端口没变一次就连上）。</summary>
        private string _cachedPort;

        /// <summary>是否已尝试从磁盘缓存恢复 _cachedPort（防止每次构建候选列表都读一次磁盘）。</summary>
        private bool _cachedPortLoadedFromDisk;

        public bool IsConnected => _isConnected;

        public event EventHandler<string> OnError;

        /// <summary>连接气压表：断开旧连 → 按"缓存端口→配置端口→CH340 识别"顺序逐个尝试第一个打开的。</summary>
        public bool Connect(BarometerConfig config)
        {
            // 【加锁对齐】与 ModbusTcpIoController/FanControllerClient 一致：Connect/Disconnect/读写
            // 全部在 _syncRoot 锁内串行化。原实现 Connect 不带锁——SetAllThresholds 断线重连时并发
            // Disconnect 关串口会干扰采集线程锁内的 ReadData（抛 ObjectDisposedException，虽能自愈
            // 但不干净）。串口 Open/建 RTU 主站是瞬时操作（无 TCP 超时阻塞），锁内安全。
            lock (_syncRoot)
            {
                _config = config;
                return ConnectInternal();
            }
        }

        /// <summary>连接实际执行部分（必须在 _syncRoot 锁内调用；逐个候选串口尝试，各候选都是瞬时 Open）。</summary>
        private bool ConnectInternal()
        {
            try
            {
                // 0) 先断开旧连接（避免重复 Open 串口导致 Access denied 或句柄泄漏）
                Disconnect();

                // 0.5) 组装候选端口列表（核心：缓存优先 + CH340 自动识别）：
                //   ① 上次连接成功的端口（磁盘缓存 BarometerPort.cache，省去每次重新搜索）
                //   ② 配置里填的端口 PortName（尊重手动配置）
                //   ③ CH340 自动识别（气压表 RS485 适配器，现场免改配置）
                // 逻辑一句话：连上就记住；记住的端口连不上，就重新去找。
                List<string> candidates = BuildCandidatePorts(_config);
                if (candidates.Count == 0)
                {
                    OnError?.Invoke(this, "气压表连接参数错误：没有可用的串口，请检查 PortName 配置");
                    return false;
                }

                // 1) 按顺序逐个尝试候选端口：第一个打开成功的即为实际使用的端口
                Exception lastError = null;
                foreach (string portName in candidates)
                {
                    try
                    {
                        // 创建串口对象并配置参数：BaudRate/DataBits/StopBits/Parity 必须与现场气压表一致；
                        // ReadTimeout/WriteTimeout 防止串口调用长期卡死。
                        _serialPort = new SerialPort(portName)
                        {
                            BaudRate = _config.BaudRate,
                            DataBits = _config.DataBits,
                            Parity = ParseParity(_config.Parity),
                            StopBits = ParseStopBits(_config.StopBits),
                            ReadTimeout = _config.SerialReadTimeoutMs,
                            WriteTimeout = _config.SerialWriteTimeoutMs
                        };

                        _serialPort.Open();
                        CurrentPortName = portName;

                        // 连接成功 → 把本次实际端口写入磁盘缓存（下次启动优先用它，不用再走 CH340 搜索）
                        SaveCachedPort(portName);

                        // 通过 NModbus 创建 RTU 主站（会在串口上组装 RTU 帧并处理 CRC 校验）
                        var factory = new ModbusFactory();
                        _master = factory.CreateRtuMaster(_serialPort);
                        _master.Transport.ReadTimeout = _config.SerialReadTimeoutMs;
                        _master.Transport.WriteTimeout = _config.SerialWriteTimeoutMs;

                        _isConnected = true;
                        return true;
                    }
                    catch (Exception ex)
                    {
                        // 本端口打开失败（端口不存在/被占用/驱动异常等）：记下原因，清理资源后继续下一个候选
                        lastError = ex;
                        try
                        {
                            if (_serialPort != null)
                            {
                                if (_serialPort.IsOpen) _serialPort.Close();
                                _serialPort.Dispose();
                            }
                        }
                        catch
                        {
                            // Close/Dispose 在"串口被拔插"场景可能抛异常，吞掉继续
                        }
                        _serialPort = null;
                        _master = null;
                    }
                }

                // 2) 所有候选端口都连不上：通知上层，并清理资源
                OnError?.Invoke(this,
                    $"气压表串口连接失败（已尝试 {candidates.Count} 个端口: {string.Join(", ", candidates)}）: {lastError?.Message}");
                Disconnect();
                return false;
            }
            catch (Exception ex)
            {
                // Connect 设计约定：不向外抛异常（避免启动阶段把主程序崩掉），通过 OnError 通知
                OnError?.Invoke(this, ex.Message);
                Disconnect();
                return false;
            }
        }

        /// <summary>
        /// 组装本次连接要尝试的候选端口列表（端口识别核心）。
        /// 顺序（越靠前越优先）：① 磁盘缓存端口（工控机记忆）② 配置端口（尊重手动配置）③ CH340 自动识别。
        /// 自动过滤空字符串与重复项（避免同一个端口试两次）。
        /// </summary>
        /// <param name="config">设备配置</param>
        /// <returns>候选端口列表（可能为空，表示配置里没端口且没识别到 CH340）</returns>
        private List<string> BuildCandidatePorts(BarometerConfig config)
        {
            var list = new List<string>();

            // 局部函数：把"还没加过"的端口追加进候选列表（去重）
            void AddCandidate(string port)
            {
                if (string.IsNullOrWhiteSpace(port)) return;
                port = port.Trim();
                foreach (string x in list)
                {
                    if (string.Equals(x, port, StringComparison.OrdinalIgnoreCase)) return;
                }
                list.Add(port);
            }

            // ① 上次连接成功的端口（磁盘缓存恢复/本次会话内存）——优先直接连
            if (!_cachedPortLoadedFromDisk)
            {
                _cachedPortLoadedFromDisk = true;
                _cachedPort = _cachedPort ?? LoadCachedPort();
            }
            AddCandidate(_cachedPort);

            // ② 配置端口始终尝试
            AddCandidate(config.PortName);

            // ③ CH340 自动识别（缓存/配置都失效时重新搜索，找到新端口后会覆盖缓存）
            List<string> ch340Ports = SerialPortHelper.GetCh340Ports();
            foreach (string ch in ch340Ports)
            {
                AddCandidate(ch);
            }

            return list;
        }

        /// <summary>
        /// 读取磁盘缓存的上次连接成功的气压表串口。
        /// 【失效判定】缓存端口必须"当前系统里仍然存在"才有效（设备被拔/换 USB 口/换电脑 → 缓存端口已不在
        /// 系统串口列表 → 返回 null，上层继续尝试配置端口和 CH340 搜索，找到新端口后覆盖缓存）。
        /// 文件不存在/内容非法/读失败 → 一律返回 null（不阻塞连接）。
        /// </summary>
        /// <returns>缓存端口名称；无有效缓存返回 null</returns>
        private string LoadCachedPort()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PortCacheFileName);
                if (!File.Exists(path)) return null;

                string content = File.ReadAllText(path).Trim();
                if (string.IsNullOrWhiteSpace(content)) return null;

                // 校验缓存端口是否还在系统串口列表里（防止缓存写坏后一直连错端口）
                string[] systemPorts = SerialPortHelper.GetAllPortNames();
                foreach (string sp in systemPorts)
                {
                    if (string.Equals(sp, content, StringComparison.OrdinalIgnoreCase)) return content;
                }
                return null;
            }
            catch
            {
                return null;   // 读缓存失败不阻塞连接
            }
        }

        /// <summary>把"本次连接成功的气压表串口"写入磁盘缓存；写失败忽略（无写权限/磁盘只读等）。</summary>
        private void SaveCachedPort(string port)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PortCacheFileName);
                File.WriteAllText(path, port);
            }
            catch
            {
                // 写缓存失败忽略，下次仍回落配置列表
            }
        }

        /// <summary>断开连接：先置状态 false（上层避免继续发请求），再释放串口与主站。</summary>
        public void Disconnect()
        {
            lock (_syncRoot)
            {
                _isConnected = false;

                try
                {
                    if (_serialPort != null)
                    {
                        // Close/Dispose 可能"串口被拔插"抛异常，try/catch 吞掉
                        if (_serialPort.IsOpen)
                        {
                            _serialPort.Close();
                        }
                        _serialPort.Dispose();
                    }
                }
                catch
                {
                }
                finally
                {
                    _serialPort = null;
                    _master = null;
                }
            }
        }

        /// <summary>读取单个气压表数据（失败返回 null，不抛异常；端口级故障自动标记断开）。</summary>
        public BarometerData ReadData(int deviceId)
        {
            if (!_isConnected || _config == null || _master == null)
            {
                OnError?.Invoke(this, "设备未连接");
                return null;
            }

            if (deviceId < 1 || deviceId > _config.TotalBarometers)
            {
                OnError?.Invoke(this, $"设备编号 {deviceId} 超出合法范围 [1, {_config.TotalBarometers}]");
                return null;
            }

            try
            {
                // 一次连续读 BarometerReadRegisterCount 个输入寄存器（功能码 0x04）：
                // startAddress=压力寄存器（配置，默认 0x0001）；数量可配（默认 2 = 0x0001 压力原始值
                // + 0x0002 小数位数）。注意：0x0002 现场实测不可靠，换算不用它（固定用配置小数位），
                // 仍按配置数量读是为了与 Demo 的读取块保持一致（部分仪表需成块读才回数据）。
                // 防御：数量夹到 1~125（Modbus 单帧上限），数量为 0/异常配置时至少读 1 个。
                ushort[] registers;
                lock (_syncRoot)
                {
                    int readCount = Math.Max(1, Math.Min(125, (int)_config.BarometerReadRegisterCount));
                    registers = _master.ReadInputRegisters((byte)deviceId, _config.BarometerPressureRegisterAddress, (ushort)readCount);
                }
                if (registers == null || registers.Length < 1) return null;   // 换算只用第 1 个寄存器（压力值）

                // 寄存器值 → 压力值转换（以 Demo 为准）：
                // - 压力原始值按有符号 short 解释（0xFFFE → -2，支持负压）
                // - 小数位固定用配置默认值（BarometerDefaultDecimalPlaces=1），不再读设备 0x0002 ——
                //   现场实测该寄存器不可靠（72 台中 46 台返回 0，但仪表实际按 1 位小数显示），
                //   按 0 位小数换算会把压力显示错 10 倍。与阈值写入保持同一套固定小数位，和仪表显示完全一致。
                // - 实际压力 = 有符号原始值 / 10^小数位，再乘以可选缩放系数 BarometerPressureScale
                short rawSigned = (short)registers[0];
                int decimalPos = _config.BarometerDefaultDecimalPlaces;
                decimal pressureKPa = rawSigned / (decimal)Math.Pow(10, decimalPos);
                pressureKPa *= _config.BarometerPressureScale;

                var data = new BarometerData
                {
                    DeviceId = deviceId,
                    VacuumPressure = pressureKPa,
                    CollectTime = DateTime.Now
                };

                // 在"采集层"做一次最基础的报警判断，用于 UI 先显示 Fault（红色）。
                // 真正的联动输出（关阀/断电）由业务层统一处理，避免通讯类里写业务逻辑。
                bool alarm = IsAlarm(pressureKPa);
                data.Status = alarm ? DeviceStatus.Fault : DeviceStatus.Idle;
                return data;
            }
            catch (Exception ex)
            {
                // 读失败不抛异常，继续让其它设备有机会读取
                OnError?.Invoke(this, $"设备{deviceId}读取失败: {ex.Message}");

                // 【串口心跳】若异常是"端口级"故障（RS485 适配器被拔出/端口被占用/端口已关闭等），
                // 说明整条串口已断开：把 _isConnected 置 false，让上层感知并后台自动重连。
                // 【重要】单台设备"无响应"（超时/Modbus 异常码）属于正常离线，不是端口断开，避免误判。
                if (IsPortLevelFailure(ex))
                {
                    _isConnected = false;
                }
                return null;
            }
        }

        /// <summary>
        /// 判断异常是否为"串口级"故障（串口心跳核心）：
        /// 把"单台设备无响应"（正常，设备离线/换表）和"整条串口断开"（RS485 适配器被拔/USB 口被拔/端口被占用）区分开。
        /// - 单台无响应 → NModbus 抛超时类异常，串口本身健康，不能标记断开；
        /// - 串口级故障 → 访问被拒绝/对象已释放/IO 关闭类异常，整条总线不可用。
        /// 判定依据：1) UnauthorizedAccessException 端口访问被拒绝；2) ObjectDisposedException 串口已被释放；
        /// 3) IOException 消息带"端口/信号量/关闭/不存在/port/semaphore"等关键字
        /// （NModbus 的 SlaveException 消息是"功能码/异常码"，不含这些关键字，不会被误判）。
        /// </summary>
        private static bool IsPortLevelFailure(Exception ex)
        {
            if (ex is UnauthorizedAccessException || ex is ObjectDisposedException)
            {
                return true;
            }

            if (ex is System.IO.IOException ioEx)
            {
                string msg = ioEx.Message ?? "";
                if (ioEx.InnerException != null) msg += " " + ioEx.InnerException.Message;
                msg = msg.ToLowerInvariant();

                return msg.Contains("closed") || msg.Contains("close") ||
                       msg.Contains("port") || msg.Contains("semaphore") ||
                       msg.Contains("abort") ||
                       msg.Contains("端口") || msg.Contains("信号量") ||
                       msg.Contains("不存在") || msg.Contains("关闭") ||
                       msg.Contains("终止") || msg.Contains("中止");
            }

            return false;
        }

        /// <summary>
        /// 批量读取所有气压表数据。
        /// 设计约定：永远返回数组（即便失败也返回空数组/全 null 数组），避免上层空引用异常。
        /// 【串口心跳】串口断开时返回"全 null 数组"而不是空数组：让业务层的逐台循环仍然能累加失败次数、
        /// 触发"通讯故障"联动（关阀+断电）的安全兜底——避免整条串口掉线时测试中的设备无人监管。
        /// </summary>
        public BarometerData[] ReadAllData()
        {
            if (_config == null)
            {
                OnError?.Invoke(this, "未连接，请先调用 Connect 方法");
                return new BarometerData[0];
            }

            if (!_isConnected)
            {
                OnError?.Invoke(this, "设备未连接（串口已断开，等待自动重连）");
                return new BarometerData[_config.TotalBarometers];
            }

            var data = new BarometerData[_config.TotalBarometers];
            for (int i = 0; i < _config.TotalBarometers; i++)
            {
                data[i] = ReadData(i + 1);
            }
            return data;
        }

        /// <summary>
        /// 写入单台气压表的设备阈值（Holding Register，功能码 0x06；寄存器地址用配置
        /// BarometerThresholdRegisterAddress，默认 0x0010）。
        /// 与 Demo 保持一致：1. 小数位固定用配置（默认 1，与 Demo 硬编码 1 一致）；
        /// 2. 寄存器值 = round(阈值 × 10^小数位)，负数按补码写（设备按有符号 short 解释）；
        /// 3. 写 WriteSingleRegister(slaveId=deviceId, 阈值寄存器地址, 寄存器值)。
        /// 【单位提醒】thresholdValue 是"设备单位"（与压力读数同单位同小数位），
        /// 不是软件报警阈值 AlarmPressureThresholdKPa（kPa）。写前务必确认设备单位。
        /// </summary>
        public bool SetThreshold(int deviceId, decimal thresholdValue)
        {
            if (!_isConnected || _config == null || _master == null)
            {
                OnError?.Invoke(this, "设备未连接");
                return false;
            }

            if (deviceId < 1 || deviceId > _config.TotalBarometers)
            {
                OnError?.Invoke(this, $"设备编号 {deviceId} 超出合法范围 [1, {_config.TotalBarometers}]");
                return false;
            }

            try
            {
                lock (_syncRoot)
                {
                    // 小数位固定用配置默认值（不再读设备 0x0002，该寄存器不可靠，会算出错误寄存器值）
                    int decimalPos = _config.BarometerDefaultDecimalPlaces;

                    // 阈值 → 寄存器值：round(阈值 × 10^小数位)。有符号 short 范围 -32768~32767，
                    // 越界说明单位/位数配错，提醒后返回 false。
                    int multiplier = (int)Math.Pow(10, decimalPos);
                    long scaled = (long)Math.Round(thresholdValue * multiplier);
                    if (scaled < short.MinValue || scaled > short.MaxValue)
                    {
                        OnError?.Invoke(this, $"设备{deviceId}阈值 {thresholdValue}×10^{decimalPos}={scaled} 超出寄存器范围，请确认单位/小数位");
                        return false;
                    }

                    _master.WriteSingleRegister((byte)deviceId, _config.BarometerThresholdRegisterAddress, (ushort)scaled);
                    return true;
                }
            }
            catch (Exception ex)
            {
                // 写入失败不抛异常（与 ReadData 约定一致），通过 OnError 通知上层
                OnError?.Invoke(this, $"设备{deviceId}写阈值失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 批量写入所有气压表的设备阈值：逐台调用 SetThreshold，单台失败不影响其它台。
        /// 未连接时返回空字典（让上层给出"未连接任何气压表，请先检查通讯连接"的明确提示）。
        /// 【性能】72 台连写 + 坏设备会阻塞较久（每台坏设备约一个读超时），调用方应在后台线程执行。
        /// 【与 Demo 对齐】每写一台后延时 50ms（参考 ModbusRtuBarometerTest BatchSetThreshold
        /// writeDelayMs=50），让 RS485 总线安静一下，避免 72 台连写帧间隔过密丢帧。
        /// </summary>
        public Dictionary<int, bool> SetAllThresholds(decimal thresholdValue)
        {
            var result = new Dictionary<int, bool>();
            if (_config == null)
            {
                OnError?.Invoke(this, "未连接，请先调用 Connect 方法");
                return result;
            }

            // 串口未连接时，先尝试重连一次；再连不上才返回空字典（批量写期间已暂停采集定时器，
            // 不会与采集线程并发访问串口）。
            if (!_isConnected || _master == null)
            {
                Connect(_config);
            }

            if (!_isConnected || _master == null)
            {
                OnError?.Invoke(this, "气压表未连接，无法批量写阈值（请先检查串口/驱动）");
                return result;
            }

            for (int i = 1; i <= _config.TotalBarometers; i++)
            {
                result[i] = SetThreshold(i, thresholdValue);
                System.Threading.Thread.Sleep(50);
            }
            return result;
        }

        /// <summary>基础报警判定：按配置的比较方向判断压力是否越限（越限返回 true=标记 Fault）。</summary>
        private bool IsAlarm(decimal pressureKPa)
        {
            if (_config.AlarmWhenPressureHigherThanThreshold)
            {
                return pressureKPa > _config.AlarmPressureThresholdKPa;
            }

            return pressureKPa < _config.AlarmPressureThresholdKPa;
        }

        /// <summary>把配置校验位字符串解析为 Parity 枚举（None/Odd/Even/Mark/Space，大小写不敏感）。</summary>
        private Parity ParseParity(string parity)
        {
            if (string.IsNullOrWhiteSpace(parity)) return Parity.None;
            if (Enum.TryParse(parity, true, out Parity parsed)) return parsed;
            return Parity.None;
        }

        /// <summary>把配置停止位数值解析为 StopBits 枚举（1/2/15=1.5 位）。</summary>
        private StopBits ParseStopBits(int stopBits)
        {
            switch (stopBits)
            {
                case 1:
                    return StopBits.One;
                case 2:
                    return StopBits.Two;
                case 15:   // 项目约定：15 表示 1.5 停止位
                    return StopBits.OnePointFive;
                default:
                    return StopBits.One;
            }
        }

        /// <summary>释放资源（关闭串口连接）。</summary>
        public void Dispose()
        {
            Disconnect();
        }
    }
}
