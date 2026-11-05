using System;
using HslCommunication;

namespace Kaleidoscope.Services
{
    // =====================================================================================
    // PLC 设备抽象（IKxPlcDevice，宿主 V0.9.55 / 原版 V4.4.359~360 同步轮新增）——
    // 队列与设备端之间的窄接口，让 PLC 槽位不绑死任何一家协议栈实现。
    // -------------------------------------------------------------------------------------
    // 【为什么需要本接口】整个 PLC 队列（PlcNetCommQue）的读写链路只对设备对象提三类诉求：
    //   ① 连接生命周期（ConnectServer/ConnectClose，重连链/热更重建/退出关闭都要用）；
    //   ② 十种单值读 + 连续字块读（RunPlcRead 按 DataTypeWR 分派）；
    //   ③ 九种数组写（RunPlcWrite 按 DataTypeWR 分派，队列恒用数组重载）。
    // 原实现把设备对象按 HSL 的 IReadWriteNet 装进槽位、连接生命周期靠 `as DeviceTcpNet`
    // 探测——等于把"底层库"焊死在 HSL 家族上（IReadWriteNet 全接口 100+ 成员含 Async/泛型，
    // 第三方库全实现一遍是纯死样板；as DeviceTcpNet 对非 HSL 对象又恒 null，连接链直接失明）。
    // 本接口只收窄到队列真正用到的方法面：HSL 设备（DeviceTcpNet 家族）本来就以同名同签名
    // 公有方法实现了全部成员，无需任何包装直接上转型即可进槽；第三方实现（如 NModbus 的
    // NModbusPlcDevice）实现同一接口即可进同一队列，心跳/握手/脚本同步读写全链路无感。
    // -------------------------------------------------------------------------------------
    // 【签名为什么长这样】逐成员对齐 HSL 公有方法签名（2026-11-05 对 00_ExeBuild 的
    // HslCommunication.dll 反射核对）：ConnectServer()/ConnectClose() 都返回 OperateResult
    // （不是 void——返回类型不一致会静默破坏接口隐式匹配，BuildPlcDevice 的上转型在运行时
    // 才炸），读写返回 HslCommunication.OperateResult/OperateResult<T>。这样"接口隐式匹配"
    // 由编译期保证：BuildPlcDevice 返回类型是 IKxPlcDevice，HSL 对象 return 即编译校验，
    // 签名对不上当场 CS 错误，不会漏到现场。
    // -------------------------------------------------------------------------------------
    // 【怎么加第三家底层库】新建类实现本接口（连接生命周期自管 + 读写按自家协议栈组包，
    // 失败统一回 new OperateResult(消息) / OperateResult.CreateFailedResult<T>），在
    // BuildPlcDevice 加一个 case 即接入整条队列——心跳、握手字批量快照、脚本同步读写、
    // 重连链、热更重建全部自动可用，业务与脚本零改动。
    // =====================================================================================
    public interface IKxPlcDevice
    {
        /// <summary>建立连接（成功 IsSuccess=true；幂等——重复调用先关旧连再建新连）。</summary>
        OperateResult ConnectServer();

        /// <summary>关闭连接（幂等无害，未连接/已关闭直接成功；HSL 同签名回 OperateResult）。</summary>
        OperateResult ConnectClose();

        // ---------------- 读（单值 + 连续字块）----------------

        OperateResult<bool> ReadBool(string address);
        OperateResult<short> ReadInt16(string address);
        /// <summary>连续字块读（触发字批量扫描/握手字批量快照用；长度调用方已钳 1–125）。</summary>
        OperateResult<short[]> ReadInt16(string address, ushort length);
        OperateResult<ushort> ReadUInt16(string address);
        OperateResult<int> ReadInt32(string address);
        OperateResult<uint> ReadUInt32(string address);
        OperateResult<long> ReadInt64(string address);
        OperateResult<ulong> ReadUInt64(string address);
        OperateResult<float> ReadFloat(string address);
        OperateResult<double> ReadDouble(string address);

        // ---------------- 写（数组形，队列恒用数组重载）----------------

        OperateResult Write(string address, bool[] value);
        OperateResult Write(string address, short[] value);
        OperateResult Write(string address, ushort[] value);
        OperateResult Write(string address, int[] value);
        OperateResult Write(string address, uint[] value);
        OperateResult Write(string address, long[] value);
        OperateResult Write(string address, ulong[] value);
        OperateResult Write(string address, float[] value);
        OperateResult Write(string address, double[] value);
    }
}
