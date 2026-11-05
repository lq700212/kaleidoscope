using System;
using HslCommunication;
using HslCommunication.Core.Device;

namespace Kaleidoscope.Services
{
    // =====================================================================================
    // HSL 设备适配包装（HslPlcDeviceWrap，宿主 V0.9.55 / 原版 V4.4.359~360 同步轮新增）
    // —— IKxPlcDevice 的 HSL 家族桥接实现，HSL 设备进 PLC 队列槽位的唯一收敛点。
    // -------------------------------------------------------------------------------------
    // 【为什么要有本包装（而不是让 HSL 设备直接上转型）】HSL 六品牌（MelsecMcNet 等）的
    //   公有方法与接口逐成员签名完全一致（对 HslCommunication.dll 反射逐个核对过：
    //   ReadBool(string)→OperateResult<bool>、Write(string, short[])→OperateResult、
    //   ConnectServer()/ConnectClose()→OperateResult……），逐成员转发编译也全过；但
    //   `IKxPlcDevice d = melsecMcNet` 的隐式接口判定在 C# 里不成立（HSL 家族的
    //   IReadWriteNet 实现形态所致——接口映射对基类链公有成员的隐式匹配规则不认，
    //   两种编译器一致报 CS0266，实测 2026-11-05）。既然隐式判定走不通，就逐成员
    //   显式桥接：一个转发类全收，HSL 设备照样进同一队列。
    // 【收敛点意义】BuildPlcDevice 的六个 HSL 分支统一 return new HslPlcDeviceWrap(对象)，
    //   槽位里 IKxPlcDevice 与 NModbusPlcDevice 平起平坐——队列/心跳/脚本全链路对
    //   "设备底层是 HSL 还是 NModbus"零感知（底层库切换）。
    // 【生命周期】ConnectServer/ConnectClose 直转发 HSL（重连链"复用旧对象先关后连"
    //   的语义与 HSL 直连时代一字不差）；读写直转发（字节序/地址解析全在 HSL 侧，
    //   各品牌行为与改造前完全一致）。
    // =====================================================================================
    public class HslPlcDeviceWrap : IKxPlcDevice
    {
        /// <summary>被包装的 HSL 设备对象（BuildPlcDevice 建的 DeviceTcpNet 家族实例）。</summary>
        private readonly DeviceTcpNet _d;

        public HslPlcDeviceWrap(DeviceTcpNet device)
        {
            _d = device;
        }

        // ---------------- 连接生命周期（直转发，重连链语义不变） ----------------

        public OperateResult ConnectServer() { return _d.ConnectServer(); }
        public OperateResult ConnectClose() { return _d.ConnectClose(); }

        // ---------------- 读（单值 + 连续字块） ----------------

        public OperateResult<bool> ReadBool(string address) { return _d.ReadBool(address); }
        public OperateResult<short> ReadInt16(string address) { return _d.ReadInt16(address); }
        public OperateResult<short[]> ReadInt16(string address, ushort length) { return _d.ReadInt16(address, length); }
        public OperateResult<ushort> ReadUInt16(string address) { return _d.ReadUInt16(address); }
        public OperateResult<int> ReadInt32(string address) { return _d.ReadInt32(address); }
        public OperateResult<uint> ReadUInt32(string address) { return _d.ReadUInt32(address); }
        public OperateResult<long> ReadInt64(string address) { return _d.ReadInt64(address); }
        public OperateResult<ulong> ReadUInt64(string address) { return _d.ReadUInt64(address); }
        public OperateResult<float> ReadFloat(string address) { return _d.ReadFloat(address); }
        public OperateResult<double> ReadDouble(string address) { return _d.ReadDouble(address); }

        // ---------------- 写（数组形） ----------------

        public OperateResult Write(string address, bool[] value) { return _d.Write(address, value); }
        public OperateResult Write(string address, short[] value) { return _d.Write(address, value); }
        public OperateResult Write(string address, ushort[] value) { return _d.Write(address, value); }
        public OperateResult Write(string address, int[] value) { return _d.Write(address, value); }
        public OperateResult Write(string address, uint[] value) { return _d.Write(address, value); }
        public OperateResult Write(string address, long[] value) { return _d.Write(address, value); }
        public OperateResult Write(string address, ulong[] value) { return _d.Write(address, value); }
        public OperateResult Write(string address, float[] value) { return _d.Write(address, value); }
        public OperateResult Write(string address, double[] value) { return _d.Write(address, value); }
    }
}
