# CommonLib Demo（通讯/存图测试台）

> 现场**界面还没做好**时，用这个 WinForms 小工具手动验证 CommonLib 全链路：
> PLC 读写 / 相机触发判定取图存图 / 扫码枪收码 / 存图归档。
> 它本身就是"按 CommonLib 标准接入方式写的最小界面"，新界面可以直接照着抄接入骨架。

## 一、怎么跑

> 以下命令均在**仓库根**（`E:\Project\CommonLib`）执行。

1. **先构建 CommonLib**（Demo 引用它的 bin 输出）：
   ```powershell
   & "D:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe" `
     CommonLib/CommonLib.csproj /p:Configuration=Debug /p:Platform=AnyCPU /t:Build /nologo /v:m /m
   ```
2. **构建并运行 Demo**（构建后自动把 CommonLib.dll / NModbus.dll / Newtonsoft.Json.dll 拷到输出目录）：
   ```powershell
   MSBuild 构建 Demo/CommonLibDemo.csproj
   # 或直接跑：Demo\bin\Debug\CommonLibDemo.exe
   ```

> 首次启动无配置 → 用库默认值（PLC 监听 0.0.0.0:502、两台默认相机 19.87.6.213/.212、
> 扫码枪 TCP 19.87.6.100:9004、存图根目录 E:\Images）。现场改 IP 后点"保存配置"，
> 存到 `Config/demo.json`，下次自动加载。

## 二、界面功能速查

| 区域 | 功能 | 用途 |
| --- | --- | --- |
| **配置** | 型号下拉 + PLC IP/端口；应用配置（热更）/保存/加载 | 现场联调参数，改完点"应用配置"免重启生效 |
| **相机** | 相机下拉、T1 仅触发、**T2 触发+判定+取图存图**、读当前程序、切 P001 | 验证相机通讯 + 判定 + FTP 取图归档全链路 |
| **扫码枪** | 扫码枪下拉、发送触发指令、最近条码大字显示 | 验证收码 + 触发指令（TCP 需 LON） |
| **PLC 读写** | 读扫码/相机请求、写扫码/相机结果、写型号、任意寄存器读写 | 验证"请求-结果-复位"三拍握手与型号区 |
| **存图测试** | 生成测试图并存图 | **不依赖相机**，单独验证 ImageStore 归档链路 |
| **右侧** | 连接状态灯、图片预览、日志 | 实时观察设备连接与操作日志 |

## 三、验证清单（前期联调用）

1. **存图链路**（最先做，不依赖设备）：点"生成测试图并存图" → 日志出现"存图成功" +
   右侧显示路径 → 打开 `E:\Images\年月日\...\OK\` 看文件。
2. **扫码枪收码**：扫码枪连上（状态灯绿）→ 放条码到枪下 → 大字区实时显示条码。
3. **PLC 通讯**：右侧 PLC 灯绿（主站连入）→ 点"读扫码请求"能看到 PLC 写入的请求值 →
   点"写扫码结果 OK/NG" → PLC 梯形图能读到。
4. **相机**：相机灯绿 → 点"T2 触发+判定+取图存图" → 判定显示 OK/NG、右侧闪图、
   日志给出归档路径 → FTP 源图被删。

## 四、红线提示（Demo 已遵守，新界面照抄）

- 所有网络/文件 IO 在后台线程，完成后 `SafeInvoke` 回 UI——UI 线程绝不做设备读写。
- 所有连接由 `DeviceHub` 统一管理，Demo 不新建任何 TcpClient/串口。
- 图片预览走后台解码缩略图，不用全尺寸大图直接赋值 PictureBox。
- 关窗 `hub.Dispose()` 自动按固定顺序释放全部服务。

## 五、配置文件说明

`Config/demo.json`（Newtonsoft.Json 序列化，含 CommonLib 全部强类型配置）：
- `productModel`：当前型号（PLC 建站成功写入型号区）
- `devices.plc`：从站监听 IP/端口 + 寄存器地址
- `devices.cameras`：相机列表（IP/端口/FTP 目录/PLC 通道/点位程序表）
- `devices.scanners`：扫码枪列表（TCP/串口参数 + 触发指令）
- `devices.image`：存图根目录/目录层级/保留天数

现场换相机 IP 只改 `devices.cameras` 里的 `ipAddress`，或直接改界面上"配置"区后保存。
