# bosectl — Bose QC Ultra 耳机 Windows 工具

在 Windows 上通过经典蓝牙 RFCOMM 与 Bose QC Ultra 耳机通信（未对其他型号做测试），支持查看电量、读取设备信息和切换音频模式。项目提供两种独立的使用方式：**纯命令行工具**和 **WinUI 3 图形界面**。两者共用 `src/Bose.Bmap` 协议库。

## 项目结构

| 路径              | 用途                         |
| --------------- | -------------------------- |
| `src/Bose.Bmap` | BMAP 协议、RFCOMM 传输和耳机控制 API |
| `src/BoseCtl`   | 纯命令行程序 `bosectl`           |
| `src/BoseApp`   | WinUI 3 桌面程序 `BoseApp`     |
| `publish.ps1`   | 发布图形界面程序的 PowerShell 脚本    |

## 环境要求

- Windows 10 2004（版本 2004，内部版本 19041）或更高版本
- 开发和从源码运行需要 .NET 8 SDK
- 耳机需先在 Windows 蓝牙设置中完成配对

程序默认自动查找名称中含 Bose、QC 或 Ultra 的已配对设备；如有多台设备，可以通过命令行参数指定目标。

---

## 一、纯命令行

命令行版适合在终端、脚本或自动化任务中使用。它不依赖图形界面，可读取设备状态、切换模式，也提供 BMAP 调试命令。

### 运行

在项目根目录打开 PowerShell：

```powershell
# 编译整个解决方案
dotnet build -c Release

# 直接运行命令
dotnet run --project src\BoseCtl -- status

# 查看内置帮助
dotnet run --project src\BoseCtl -- --help
```

发布 CLI：

```powershell
# 生成 win-x64 单文件程序（依赖目标电脑已安装 .NET 8）
dotnet publish src\BoseCtl -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

发布产物位于 `src\BoseCtl\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish`。发布目录中的 `bosectl.exe` 可从 PowerShell 或命令提示符调用。

### 常用命令

以下示例假设已将 `bosectl.exe` 所在目录加入 `PATH`；也可以在项目中用 `dotnet run --project src\BoseCtl -- <命令>` 执行。

| 命令                       | 说明                  |
| ------------------------ | ------------------- |
| `bosectl devices`        | 列出已配对的蓝牙设备及连接状态     |
| `bosectl status`         | 查看左右耳电量、充电盒参考值和当前模式 |
| `bosectl battery`        | 查看耳机电量              |
| `bosectl mode`           | 查看当前模式              |
| `bosectl modes`          | 列出耳机支持的模式，包括自定义模式   |
| `bosectl mode quiet`     | 切换到 Quiet 降噪模式      |
| `bosectl mode aware`     | 切换到 Aware 通透模式      |
| `bosectl mode immersion` | 切换到 Immersion 沉浸模式  |
| `bosectl mode 3`         | 按索引切换模式（例如自定义模式）    |
| `bosectl mode "通勤"`      | 按自定义模式名称切换          |
| `bosectl info`           | 查看设备名、固件、序列号和支持的功能块 |

模式也可通过快捷命令切换：`bosectl anc` 或 `bosectl quiet`、`bosectl aware` 或 `bosectl transparency`、`bosectl immersion`。`battery` 也可简写为 `bat`。自定义模式可以按索引或名称选择。

### 设备选择与选项

```powershell
bosectl -d "Bose QC Ultra Earbuds" status
bosectl --device 00:11:22:33:44:55 mode quiet
bosectl -p mode aware
bosectl -t 5 status
bosectl -v status
```

| 选项                        | 说明                |
| ------------------------- | ----------------- |
| `-d`、`--device <名称或 MAC>` | 指定已配对的目标设备        |
| `-u`、`--uuid <UUID>`      | 指定 RFCOMM 服务 UUID |
| `-p`、`--prompt`           | 切换模式时请求耳机语音播报     |
| `-t`、`--timeout <秒>`      | 设置请求超时，默认 3 秒     |
| `-v`、`--verbose`          | 输出收发的原始 BMAP 数据包  |
| `-h`、`--help`             | 显示帮助              |

### 调试命令

这些命令用于检查 RFCOMM 服务、观察设备消息或排查 BMAP 功能号：

```powershell
bosectl services                         # 列出设备的 RFCOMM 服务
bosectl scan status audiomodes           # 扫描指定功能块
bosectl scan                             # 扫描设备支持的功能块
bosectl raw 0x1f 0x03 get                 # 发送原始 BMAP GET 请求
bosectl monitor                          # 持续显示设备推送的消息，Ctrl+C 退出
```

`scan` 可接受功能块名称（如 `status`、`audiomodes`、`settings`）或数字/十六进制编号。`raw` 的格式为 `bosectl raw <功能块> <功能号> [get|set|setget|start|0..15] [十六进制数据]`。这些接口偏向开发和调试用途。

---

## 二、WinUI 3 图形界面

`src/BoseApp` 是基于 WinUI 3 的桌面程序，适合日常查看状态和快速切换模式。启动后会尝试自动连接耳机，显示设备信息、左右耳电量和当前模式，并提供 Quiet、Aware、Immersion 及耳机中保存的自定义模式按钮。

### 从源码运行

```powershell
dotnet run --project src\BoseApp -p:Platform=x64
```

也可以在 Visual Studio 中打开 `BoseCtl.sln`，将 `BoseApp` 设为启动项目，并选择 `x64` 平台。应用以非打包方式运行。

### 发布和分发

在项目根目录运行 `publish.ps1`：

```powershell
.\publish.ps1                 # 文件夹版，推荐
.\publish.ps1 -SingleFile     # 单文件版
.\publish.ps1 -WithCli        # 同目录附带 bosectl.exe
.\publish.ps1 -Arch arm64     # 为 ARM64 设备发布
```

文件夹版输出到 `dist\BoseApp-win-x64`，单文件版输出到 `dist\BoseApp-win-x64-single`。脚本会生成对应 ZIP 包。发布为自包含程序，目标电脑无需单独安装 .NET 或 Windows App SDK。文件夹版启动较快；单文件版首次启动会解压到临时目录，因此会稍慢。

### 窗口、托盘和设置

- 连接成功后，电量约每 60 秒刷新一次，当前模式约每 2 秒检查一次；断线后应用会自动尝试重连。
- 通知区域图标用左右电池条显示耳机电量，低电量时变红；悬停可查看设备名、电量和模式。
- 左键单击托盘图标可显示或隐藏主窗口。右键菜单提供模式切换、刷新、连接/断开、语音播报、低电量提醒、开机启动及退出等操作。
- 点击窗口关闭按钮默认隐藏到托盘；从托盘菜单选择“退出”才会结束程序。重复启动时会唤起已运行的窗口。
- 设置保存在 `%LOCALAPPDATA%\BoseApp\settings.json`。开机启动选项通过当前用户的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 注册，并以 `--tray` 参数启动。

## 蓝牙连接说明

电脑需要能够通过经典蓝牙 RFCOMM 访问已配对的耳机。若设备未出现在列表中，请先在 Windows 蓝牙设置中配对，并确认耳机处于可连接状态。读取不到左右耳分项电量时，可以尝试 `bosectl scan status` 查看设备固件响应；充电盒电量为协议读取的参考值，Bose App 不显示该项。
