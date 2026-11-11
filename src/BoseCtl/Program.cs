using System.Text;
using Bose.Bmap;
using Bose.Bmap.Protocol;
using Bose.Bmap.Transport;

namespace BoseCtl;

internal static class Program
{
    private sealed class Options
    {
        public string? Device;
        public Guid? Uuid;
        public bool Verbose;
        public bool Prompt;
        public double TimeoutSec = 3;
        public List<string> Args = new();
    }

    private static async Task<int> Main(string[] argv)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Options opt;
        try { opt = ParseOptions(argv); }
        catch (Exception ex) { Error(ex.Message); return 2; }

        if (opt.Args.Count == 0 || opt.Args[0] is "help" or "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        var cmd = opt.Args[0].ToLowerInvariant();
        var rest = opt.Args.Skip(1).ToList();

        try
        {
            switch (cmd)
            {
                case "devices": return await CmdDevices();
                case "services": return await CmdServices(opt);
            }

            await using var session = await Session.OpenAsync(opt);
            var dev = session.Device;

            return cmd switch
            {
                "info" => await CmdInfo(dev),
                "battery" or "bat" => await CmdBattery(dev, opt),
                "mode" => await CmdMode(dev, rest, opt),
                "modes" => await CmdModes(dev),
                "anc" or "quiet" => await SetModeByName(dev, "quiet", opt),
                "aware" or "transparency" => await SetModeByName(dev, "aware", opt),
                "immersion" => await SetModeByName(dev, "immersion", opt),
                "status" => await CmdStatus(dev, opt),
                "raw" => await CmdRaw(dev, rest),
                "scan" => await CmdScan(dev, rest),
                "monitor" => await CmdMonitor(dev),
                _ => Unknown(cmd),
            };
        }
        catch (Exception ex)
        {
            Error(ex.Message);
            if (opt.Verbose) Console.Error.WriteLine(ex);
            else if (ex.InnerException != null) Console.Error.WriteLine($"  原因: {ex.InnerException.Message}");
            return 1;
        }
    }

    // ================================================================ 连接会话

    private sealed class Session : IAsyncDisposable
    {
        public required BmapClient Client;
        public required BoseDevice Device;

        public static async Task<Session> OpenAsync(Options opt)
        {
            var target = await RfcommTransport.FindDeviceAsync(opt.Device);
            Info($"设备: {target.Name} ({target.AddressText}){(target.IsConnected ? "" : "  [未连接]")}");

            var transport = new RfcommTransport();
            await transport.ConnectAsync(target, opt.Uuid);
            if (opt.Verbose) Info($"已连接 RFCOMM 服务 {transport.ConnectedService!.Uuid}");

            var client = new BmapClient(transport) { DefaultTimeout = TimeSpan.FromSeconds(opt.TimeoutSec) };
            if (opt.Verbose)
            {
                client.PacketSent += p => Dim($"  >> {p}");
                client.PacketReceived += p => Dim($"  << {p}");
            }
            client.Start();
            return new Session { Client = client, Device = new BoseDevice(client) };
        }

        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }

    // ================================================================ 命令实现

    private static async Task<int> CmdDevices()
    {
        var list = await RfcommTransport.ListPairedDevicesAsync();
        if (list.Count == 0) { Info("没有已配对的蓝牙设备。"); return 0; }
        Console.WriteLine($"{"名称",-32} {"地址",-18} 状态");
        foreach (var d in list.OrderByDescending(d => d.IsConnected))
            Console.WriteLine($"{d.Name,-32} {d.AddressText,-18} {(d.IsConnected ? "已连接" : "-")}");
        return 0;
    }

    private static async Task<int> CmdServices(Options opt)
    {
        var target = await RfcommTransport.FindDeviceAsync(opt.Device);
        Info($"设备: {target.Name} ({target.AddressText})");
        var services = await RfcommTransport.ListServicesAsync(target);
        foreach (var s in services)
        {
            string tag = s.Uuid == RfcommTransport.BoseBmapUuid ? "  <- Bose BMAP"
                : s.Uuid == RfcommTransport.SerialPortUuid ? "  <- SPP" : "";
            Console.WriteLine($"{s.Uuid}  {s.ServiceName}{tag}");
        }
        return 0;
    }

    private static async Task<int> CmdInfo(BoseDevice dev)
    {
        var info = await dev.GetProductInfoAsync();
        Console.WriteLine($"名称        : {info.Name ?? "?"}");
        Console.WriteLine($"固件版本    : {info.Firmware ?? "?"}");
        Console.WriteLine($"序列号      : {info.Serial ?? "?"}");
        Console.WriteLine($"BMAP 版本   : {info.BmapVersion ?? "?"}");
        if (info.ProductId.HasValue)
            Console.WriteLine($"产品 ID     : 0x{info.ProductId:X4}  variant={info.Variant}");
        if (info.SupportedFunctionBlocks.Count > 0)
            Console.WriteLine($"功能块      : {string.Join(", ", info.SupportedFunctionBlocks.Select(FunctionBlocks.Name))}");
        return 0;
    }

    private static async Task<int> CmdBattery(BoseDevice dev, Options opt)
    {
        var (left, right, @case, raw) = await dev.GetPerBudBatteryAsync();
        if (left != null || right != null)
        {
            Console.WriteLine($"左耳  : {Fmt(left)}");
            Console.WriteLine($"右耳  : {Fmt(right)}");
            Console.WriteLine($"充电盒: {Fmt(@case)}  (参考值，Bose App 不显示盒电量)");
            if (opt.Verbose && raw != null)
            {
                Dim($"(来源 {raw})");
                foreach (var (id, b) in BoseDevice.ParseComponents(raw.Payload, ""))
                    Dim($"  组件 0x{id:X2}: {Hex.Format(b.Raw)}");
            }
            if (raw is { FunctionBlock: FunctionBlocks.Status, Function: 0x02 }) return 0;
        }
        else
        {
            Warn("未能读取分耳机电量（固件可能使用了不同的功能号，可运行 `bosectl scan status` 排查）。");
        }

        try
        {
            var total = await dev.GetBatteryAsync();
            Console.WriteLine($"整机  : {total}");
            if (opt.Verbose) Dim($"(原始 {Hex.Format(total.Raw)})");
        }
        catch (Exception ex) when (ex is BmapException or TimeoutException)
        {
            if (left == null && right == null) throw;
        }
        return 0;

        static string Fmt(BatteryInfo? b) => b == null ? "不在线" : b.ToString();
    }

    private static async Task<int> CmdStatus(BoseDevice dev, Options opt)
    {
        await CmdBattery(dev, opt);
        await PrintCurrentMode(dev);
        return 0;
    }

    private static async Task<int> CmdModes(BoseDevice dev)
    {
        var modes = (await dev.GetModesAsync()).Where(m => !m.IsEmpty).ToList();
        int current = -1;
        try { current = await dev.GetCurrentModeIndexAsync(); } catch { }
        if (modes.Count == 0) { Warn("没有读取到模式列表。"); return 1; }
        foreach (var m in modes)
            Console.WriteLine($"{(m.Index == current ? "*" : " ")} {m.Index,2}  {DisplayName(m)}");
        return 0;
    }

    private static async Task<int> CmdMode(BoseDevice dev, List<string> args, Options opt)
    {
        if (args.Count == 0 || args[0] == "get")
        {
            await PrintCurrentMode(dev);
            return 0;
        }

        var target = args[0] == "set" ? args.ElementAtOrDefault(1) : args[0];
        if (target == null) { Error("用法: bosectl mode set <名称|索引>"); return 2; }
        return await SetModeByName(dev, target, opt);
    }

    private static async Task PrintCurrentMode(BoseDevice dev)
    {
        int idx = await dev.GetCurrentModeIndexAsync();
        string name;
        try { name = DisplayName(await dev.GetModeAsync(idx)); }
        catch { name = PresetName(idx) ?? "?"; }
        Console.WriteLine($"当前模式: [{idx}] {name}");
    }

    private static async Task<int> SetModeByName(BoseDevice dev, string target, Options opt)
    {
        int index;
        if (int.TryParse(target, out var n)) index = n;
        else if (BoseDevice.PresetAliases.TryGetValue(target, out var preset))
            index = preset; // 预设模式（Quiet/Aware/Immersion 及中文别名）索引固定，无需枚举
        else
        {
            // 先在设备返回的名称里匹配（支持自定义模式），找不到再用预设别名
            var modes = (await dev.GetModesAsync()).Where(m => !m.IsEmpty).ToList();
            var hit = modes.FirstOrDefault(m => m.Name != null && m.Name.Equals(target, StringComparison.OrdinalIgnoreCase))
                   ?? modes.FirstOrDefault(m => m.Name != null && m.Name.Contains(target, StringComparison.OrdinalIgnoreCase));
            if (hit != null) index = hit.Index;
            else if (BoseDevice.PresetAliases.TryGetValue(target, out var p)) index = p;
            else
            {
                Error($"未找到模式 \"{target}\"。可用模式：");
                foreach (var m in modes) Console.Error.WriteLine($"  {m.Index,2}  {DisplayName(m)}");
                return 2;
            }
        }

        await dev.SetModeAsync(index, opt.Prompt);
        await Task.Delay(300);
        await PrintCurrentMode(dev);
        return 0;
    }

    /// <summary>发送任意 BMAP 包：raw &lt;fb&gt; &lt;fn&gt; [op] [payload-hex]</summary>
    private static async Task<int> CmdRaw(BoseDevice dev, List<string> args)
    {
        if (args.Count < 2)
        {
            Error("用法: bosectl raw <fb> <fn> [get|set|setget|start|0..15] [payload-hex]");
            return 2;
        }
        byte fb = ParseByte(args[0]);
        byte fn = ParseByte(args[1]);
        var op = args.Count > 2 ? ParseOperator(args[2]) : BmapOperator.Get;
        var payload = args.Count > 3 ? Hex.Parse(string.Join("", args.Skip(3))) : Array.Empty<byte>();

        var req = new BmapPacket(fb, fn, op, payload);
        Console.WriteLine($">> {req}");
        try
        {
            var resp = await dev.Client.RequestAsync(req);
            Console.WriteLine($"<< {resp}");
        }
        catch (BmapException ex) when (ex.Packet != null)
        {
            Console.WriteLine($"<< {ex.Packet}");
            Warn(ex.Message);
        }
        return 0;
    }

    /// <summary>对某个（或全部）功能块的 0x00..0x1F 执行 GET，用于逆向/验证功能号。</summary>
    private static async Task<int> CmdScan(BoseDevice dev, List<string> args)
    {
        IEnumerable<byte> blocks;
        if (args.Count == 0)
        {
            blocks = await dev.GetSupportedFunctionBlocksAsync();
            Info($"扫描设备支持的功能块: {string.Join(", ", blocks.Select(FunctionBlocks.Name))}");
        }
        else
        {
            blocks = args.Select(a => a.ToLowerInvariant() switch
            {
                "productinfo" => FunctionBlocks.ProductInfo,
                "settings" => FunctionBlocks.Settings,
                "status" => FunctionBlocks.Status,
                "audiomodes" or "modes" => FunctionBlocks.AudioModes,
                "device" or "devicemanagement" => FunctionBlocks.DeviceManagement,
                "audio" or "audiomanagement" => FunctionBlocks.AudioManagement,
                "control" => FunctionBlocks.Control,
                _ => ParseByte(a),
            }).ToList();
        }

        dev.Client.DefaultTimeout = TimeSpan.FromMilliseconds(800);
        foreach (var fb in blocks)
        {
            Console.WriteLine($"--- {FunctionBlocks.Name(fb)} (0x{fb:X2}) ---");
            for (byte fn = 0; fn < 0x20; fn++)
            {
                try
                {
                    var resp = await dev.Client.GetAsync(fb, fn);
                    Console.WriteLine($"  0x{fn:X2}: {Hex.Format(resp.Payload)}{Annot(resp.Payload)}");
                }
                catch (BmapException ex) when (ex.ErrorCode is 0x04 or 0x05)
                {
                    // 不支持，不输出
                }
                catch (BmapException ex)
                {
                    Console.WriteLine($"  0x{fn:X2}: ERROR {BmapErrors.Describe(ex.ErrorCode)}");
                }
                catch (TimeoutException)
                {
                    Console.WriteLine($"  0x{fn:X2}: (超时)");
                }
            }
        }
        return 0;

        static string Annot(byte[] p)
        {
            var s = Hex.Printable(p);
            return s.Length > 0 ? $"   \"{s}\"" : "";
        }
    }

    /// <summary>持续打印设备推送的消息（按按键、摘戴耳机、切换模式时可以观察变化）。</summary>
    private static async Task<int> CmdMonitor(BoseDevice dev)
    {
        Info("正在监听设备消息，按 Ctrl+C 退出……");
        dev.Client.PacketReceived += p => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {p}");
        var tcs = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; tcs.TrySetResult(); };

        // 一些设备只有在被请求过之后才会推送对应状态，这里先触发一次查询
        try { await dev.GetCurrentModeIndexAsync(); } catch { }
        try { await dev.GetBatteryAsync(); } catch { }

        await tcs.Task;
        return 0;
    }

    // ================================================================ 辅助

    private static string DisplayName(AudioMode m) => m.Name ?? PresetName(m.Index) ?? "(未命名)";

    private static string? PresetName(int idx) => idx switch
    {
        0 => "Quiet (降噪)",
        1 => "Aware (通透)",
        2 => "Immersion (沉浸)",
        _ => null,
    };

    private static byte ParseByte(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return Convert.ToByte(s[2..], 16);
        return byte.Parse(s);
    }

    private static BmapOperator ParseOperator(string s) => s.ToLowerInvariant() switch
    {
        "set" => BmapOperator.Set,
        "get" => BmapOperator.Get,
        "setget" => BmapOperator.SetGet,
        "start" => BmapOperator.Start,
        _ => (BmapOperator)ParseByte(s),
    };

    private static Options ParseOptions(string[] argv)
    {
        var o = new Options();
        for (int i = 0; i < argv.Length; i++)
        {
            string a = argv[i];
            string Next() => i + 1 < argv.Length ? argv[++i] : throw new ArgumentException($"{a} 需要一个参数");
            switch (a)
            {
                case "-d": case "--device": o.Device = Next(); break;
                case "-u": case "--uuid": o.Uuid = Guid.Parse(Next()); break;
                case "-v": case "--verbose": o.Verbose = true; break;
                case "-p": case "--prompt": o.Prompt = true; break;
                case "-t": case "--timeout": o.TimeoutSec = double.Parse(Next()); break;
                default: o.Args.Add(a); break;
            }
        }
        return o;
    }

    private static int Unknown(string cmd)
    {
        Error($"未知命令: {cmd}");
        PrintHelp();
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            bosectl - Bose QC Ultra Earbuds 命令行控制工具 (BMAP over RFCOMM)

            用法: bosectl [选项] <命令> [参数]

            常用命令:
              devices                   列出已配对的蓝牙设备
              status                    显示电量 + 当前模式
              battery                   显示左耳 / 右耳 / 充电盒电量
              mode                      显示当前模式
              mode <名称|索引>          切换模式，如: mode quiet / mode aware / mode 2
              modes                     列出全部模式（含自定义模式）
              anc | quiet               切换到降噪 (Quiet)
              aware | transparency      切换到通透 (Aware)
              immersion                 切换到沉浸 (Immersion)
              info                      设备名称、固件、序列号、支持的功能块

            调试/逆向:
              services                  列出设备的 RFCOMM 服务 UUID
              scan [功能块...]          对功能块执行 GET 扫描，如: scan status audiomodes
              raw <fb> <fn> [op] [hex]  发送原始 BMAP 包，如: raw 0x1f 0x03 get
              monitor                   持续打印设备推送消息

            选项:
              -d, --device <名称|MAC>   指定设备（默认自动选择名字含 Bose/QC/Ultra 的设备）
              -u, --uuid <UUID>         指定 RFCOMM 服务 UUID
              -p, --prompt              切换模式时让耳机语音播报
              -t, --timeout <秒>        请求超时（默认 3）
              -v, --verbose             打印收发的原始数据包
            """);
    }

    private static void Info(string s) { Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine(s); Console.ResetColor(); }
    private static void Warn(string s) { Console.ForegroundColor = ConsoleColor.Yellow; Console.Error.WriteLine(s); Console.ResetColor(); }
    private static void Error(string s) { Console.ForegroundColor = ConsoleColor.Red; Console.Error.WriteLine("错误: " + s); Console.ResetColor(); }
    private static void Dim(string s) { Console.ForegroundColor = ConsoleColor.DarkGray; Console.WriteLine(s); Console.ResetColor(); }
}
