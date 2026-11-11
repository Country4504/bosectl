# Bose QC Ultra Earbuds for Windows

A Windows companion for Bose QC Ultra Earbuds. Check battery levels, view device information, and switch listening modes over Bluetooth.

## Recommended: BoseApp (WinUI 3)

**BoseApp is the easiest way to use this project.** Its graphical interface is designed for everyday use: connect to your earbuds, check left and right battery levels, and switch between Quiet, Aware, Immersion, or your saved custom modes with a click.

### Download and run

To create a ready-to-run app folder on a Windows PC with the .NET 8 SDK, clone or download this repository, open PowerShell in the project folder, and run:

```powershell
.\publish.ps1
```

Then launch `dist\BoseApp-win-x64\BoseApp.exe`. You can copy the whole `BoseApp-win-x64` folder to another compatible Windows PC; the published app is self-contained and does not require a separate .NET or Windows App SDK installation.

To build a single-executable version instead:

```powershell
.\publish.ps1 -SingleFile
```

The app is placed in `dist\BoseApp-win-x64-single`. Its first launch may take longer while it extracts files to a temporary folder. To publish for an ARM64 PC, add `-Arch arm64` to either command. To include the command-line utility in the same output folder, add `-WithCli`.

### Use BoseApp

1. Pair your earbuds in Windows Bluetooth settings and make sure they are available to connect.
2. Start `BoseApp.exe`. The app attempts to connect automatically.
3. View battery and connection status, then select a listening mode. Modes saved in the Bose app also appear when supported by the connected earbuds.

The app stays available in the notification area (system tray). Left-click its tray icon to show or hide the window. Right-click for mode switching, refresh, connect/disconnect, voice prompts, low-battery alerts, startup options, and exit. Closing the window hides it to the tray; choose **Exit** in the tray menu to quit. Settings are stored in `%LOCALAPPDATA%\BoseApp\settings.json`.

Battery levels refresh periodically, and the app retries the connection after a disconnect. The charging-case level is a protocol reference value and is not displayed by the Bose app.

### Run from source

To develop or run the app directly, install the .NET 8 SDK, then use:

```powershell
dotnet run --project src\BoseApp -p:Platform=x64
```

Alternatively, open `BoseCtl.sln` in Visual Studio, select `BoseApp` as the startup project, and choose the `x64` platform.

## Command-line tool (advanced)

`bosectl` is available for terminal users, scripts, and troubleshooting. Most users should use BoseApp above.

### Run

```powershell
# Build the solution
dotnet build -c Release

# Run a command directly
dotnet run --project src\BoseCtl -- status

# Show built-in help
dotnet run --project src\BoseCtl -- --help
```

To publish a `win-x64` CLI executable that uses the .NET 8 runtime installed on the target PC:

```powershell
dotnet publish src\BoseCtl -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

The executable is written to `src\BoseCtl\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish`.

### Common commands

| Command | Description |
| --- | --- |
| `bosectl devices` | List paired Bluetooth devices |
| `bosectl status` | Show earbud battery levels and current mode |
| `bosectl battery` | Show left, right, and charging-case reference levels |
| `bosectl mode` | Show the current listening mode |
| `bosectl modes` | List available modes, including custom modes |
| `bosectl mode quiet` | Switch to Quiet (noise cancellation) |
| `bosectl mode aware` | Switch to Aware (transparency) |
| `bosectl mode immersion` | Switch to Immersion |
| `bosectl mode 3` | Switch by mode index, for example a custom mode |
| `bosectl mode "Commute"` | Switch by custom mode name |
| `bosectl info` | Show device name, firmware, serial number, and supported function blocks |

Shortcuts include `bosectl anc`, `bosectl quiet`, `bosectl aware`, `bosectl transparency`, and `bosectl immersion`. `battery` can also be written as `bat`.

### Device selection and options

```powershell
bosectl -d "Bose QC Ultra Earbuds" status
bosectl --device 00:11:22:33:44:55 mode quiet
bosectl -p mode aware
bosectl -t 5 status
bosectl -v status
```

| Option | Description |
| --- | --- |
| `-d`, `--device <name or MAC>` | Select a paired device |
| `-u`, `--uuid <UUID>` | Select an RFCOMM service UUID |
| `-p`, `--prompt` | Request a voice prompt when changing modes |
| `-t`, `--timeout <seconds>` | Set request timeout (default: 3 seconds) |
| `-v`, `--verbose` | Print raw BMAP packets sent and received |
| `-h`, `--help` | Show help |

### Diagnostics

```powershell
bosectl services                         # List RFCOMM services
bosectl scan status audiomodes           # Scan selected function blocks
bosectl scan                             # Scan supported function blocks
bosectl raw 0x1f 0x03 get                # Send a raw BMAP GET request
bosectl monitor                          # Watch device messages; press Ctrl+C to stop
```

These commands are intended for troubleshooting and development. `scan` accepts function-block names such as `status`, `audiomodes`, and `settings`, or numeric/hexadecimal IDs. `raw` uses the format `bosectl raw <function-block> <function> [get|set|setget|start|0..15] [hex-payload]`.

## Requirements and troubleshooting

- Windows 10 version 2004 (build 19041) or later
- .NET 8 SDK to build from source or create a release package
- Earbuds paired in Windows Bluetooth settings
- Classic Bluetooth RFCOMM support on the PC

The CLI selects a paired device whose name contains Bose, QC, or Ultra by default. Use `-d` to specify a device when more than one matches. If the earbuds do not appear, pair them in Windows settings and make sure they are available to connect. If separate left/right battery levels are unavailable on your firmware, try `bosectl scan status` to inspect the device response.

## Project layout

| Path | Description |
| --- | --- |
| `src/BoseApp` | WinUI 3 graphical application (recommended) |
| `src/BoseCtl` | Command-line utility |
| `src/Bose.Bmap` | BMAP protocol, RFCOMM transport, and device API |
| `publish.ps1` | Script to publish the self-contained graphical app |

Community Discussion: linux.do