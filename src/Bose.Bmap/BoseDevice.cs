using System.Text;
using Bose.Bmap.Protocol;

namespace Bose.Bmap;

/// <summary>音频模式（Quiet / Aware / Immersion / 自定义模式）。</summary>
public sealed record AudioMode(int Index, string? Name, byte[] RawConfig)
{
    /// <summary>
    /// 空的自定义模式槽位。实测空槽名称为 "None"，且 payload[2]（推测为图标/类型）为 0。
    /// </summary>
    public bool IsEmpty => Name == "None" && RawConfig.Length > 2 && RawConfig[2] == 0;

    public override string ToString() => Name is { Length: > 0 } ? $"[{Index}] {Name}" : $"[{Index}] (未命名)";
}

/// <summary>电池信息。不同产品返回的字段不同，因此同时保留原始 payload。</summary>
public sealed record BatteryInfo(int? Level, bool? Charging, byte[] Raw, string Source)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Level.HasValue) parts.Add($"{Level}%");
        if (Charging == true) parts.Add("充电中");
        return parts.Count > 0 ? string.Join(" ", parts) : "未知";
    }
}

public sealed record ProductInfo(string? Name, string? Firmware, string? Serial, string? BmapVersion,
    int? ProductId, int? Variant, IReadOnlyList<byte> SupportedFunctionBlocks);

/// <summary>
/// 面向业务的高层 API：电量、音频模式、设备信息等。
/// 所有 FunctionBlock/Function 编号集中定义在这里，若与实际固件不符，只需修改 <see cref="Ids"/>。
/// </summary>
public sealed class BoseDevice
{
    /// <summary>
    /// 协议编号表。来源：社区逆向（based-connect、Bose QC Ultra 相关项目与抓包），
    /// 部分条目（尤其是左右耳电量）因固件版本而异，建议用 `bosectl scan` 验证。
    /// </summary>
    public static class Ids
    {
        // ProductInfo (0x00)
        public static readonly (byte fb, byte fn) BmapVersion = (FunctionBlocks.ProductInfo, 0x01);
        public static readonly (byte fb, byte fn) AllFunctionBlocks = (FunctionBlocks.ProductInfo, 0x02);
        public static readonly (byte fb, byte fn) ProductIdVariant = (FunctionBlocks.ProductInfo, 0x03);
        public static readonly (byte fb, byte fn) FirmwareVersion = (FunctionBlocks.ProductInfo, 0x05);
        public static readonly (byte fb, byte fn) SerialNumber = (FunctionBlocks.ProductInfo, 0x07);

        // Settings (0x01)
        public static readonly (byte fb, byte fn) ProductName = (FunctionBlocks.Settings, 0x02);

        // Status (0x02)
        public static readonly (byte fb, byte fn) BatteryLevel = (FunctionBlocks.Status, 0x02);

        // AudioModes (0x1F)
        public static readonly (byte fb, byte fn) CurrentMode = (FunctionBlocks.AudioModes, 0x03);
        public static readonly (byte fb, byte fn) ModeConfig = (FunctionBlocks.AudioModes, 0x06);

        /// <summary>
        /// 左右耳 / 充电盒电量的候选位置。耳机通常会在 Status 功能块下提供分耳机电量，
        /// 具体 function 编号可能随固件变化，程序会依次尝试并报告实际命中的那一个。
        /// </summary>
        public static readonly (byte fb, byte fn)[] PerBudBatteryCandidates =
        {
            (FunctionBlocks.Status, 0x05),
            (FunctionBlocks.Status, 0x03),
            (FunctionBlocks.Status, 0x04),
            (FunctionBlocks.Status, 0x06),
        };
    }

    private readonly BmapClient _client;

    public BoseDevice(BmapClient client) => _client = client;

    public BmapClient Client => _client;

    // ---------------------------------------------------------------- 设备信息

    public async Task<ProductInfo> GetProductInfoAsync()
    {
        string? name = await TryAsync(GetNameAsync);
        string? fw = await TryAsync(async () => Ascii((await Get(Ids.FirmwareVersion)).Payload));
        string? serial = await TryAsync(async () => Ascii((await Get(Ids.SerialNumber)).Payload));
        string? bmap = await TryAsync(async () => Ascii((await Get(Ids.BmapVersion)).Payload));

        int? pid = null, variant = null;
        var idResp = await TryAsync(() => Get(Ids.ProductIdVariant));
        if (idResp is { Payload.Length: >= 2 })
        {
            pid = (idResp.Payload[0] << 8) | idResp.Payload[1];
            if (idResp.Payload.Length >= 3) variant = idResp.Payload[2];
        }

        var fbs = await TryAsync(GetSupportedFunctionBlocksAsync) ?? Array.Empty<byte>();
        return new ProductInfo(name, fw, serial, bmap, pid, variant, fbs);
    }

    public async Task<string> GetNameAsync()
    {
        var resp = await Get(Ids.ProductName);
        // payload[0] 通常是一个标志/长度上限字节，其后是 UTF-8 名称
        var p = resp.Payload;
        int start = p.Length > 0 && p[0] < 0x20 ? 1 : 0;
        return Encoding.UTF8.GetString(p, start, p.Length - start).TrimEnd('\0');
    }

    /// <summary>
    /// 获取设备支持的功能块列表。payload 是一个位图：第 i 位置 1 表示支持功能块 i
    /// （按字节大端、位从高到低排列）。
    /// </summary>
    public async Task<IReadOnlyList<byte>> GetSupportedFunctionBlocksAsync()
    {
        var resp = await Get(Ids.AllFunctionBlocks);
        var list = new List<byte>();
        var p = resp.Payload;
        for (int byteIdx = 0; byteIdx < p.Length; byteIdx++)
        for (int bit = 0; bit < 8; bit++)
        {
            if ((p[byteIdx] & (0x80 >> bit)) != 0) list.Add((byte)(byteIdx * 8 + bit));
        }
        return list;
    }

    // ---------------------------------------------------------------- 电量

    /// <summary>读取整机（或主耳机）电量。</summary>
    public async Task<BatteryInfo> GetBatteryAsync()
    {
        var resp = await Get(Ids.BatteryLevel);
        return ParseBattery(resp.Payload, $"Status.0x{Ids.BatteryLevel.fn:X2}");
    }

    /// <summary>
    /// 读取左耳 / 右耳 / 充电盒电量。返回 null 表示该固件不支持或未找到对应功能。
    /// </summary>
    public async Task<(BatteryInfo? Left, BatteryInfo? Right, BatteryInfo? Case, BmapPacket? RawPacket)> GetPerBudBatteryAsync()
    {
        // QC Ultra Earbuds (固件 4.9.x)：Status.0x02 返回多个 4 字节组件记录
        try
        {
            var resp = await Get(Ids.BatteryLevel);
            var comps = ParseComponents(resp.Payload, "Status.0x02");
            if (comps.Count >= 2)
            {
                comps.TryGetValue(ComponentLeft, out var l);
                comps.TryGetValue(ComponentRight, out var r);
                comps.TryGetValue(ComponentCase, out var c);
                return (l, r, c, resp);
            }
        }
        catch (Exception ex) when (ex is BmapException or TimeoutException) { }

        foreach (var id in Ids.PerBudBatteryCandidates)
        {
            BmapPacket resp;
            try { resp = await Get(id); }
            catch (BmapException) { continue; }
            catch (TimeoutException) { continue; }

            var parsed = ParsePerBud(resp.Payload, $"{FunctionBlocks.Name(id.fb)}.0x{id.fn:X2}");
            if (parsed.Left != null || parsed.Right != null)
                return (parsed.Left, parsed.Right, parsed.Case, resp);
        }
        return (null, null, null, null);
    }

    /// <summary>
    /// Status.0x02 组件记录中的组件 ID。
    /// 实测 QC Ultra Earbuds（固件 4.9.32）返回: 5A FF FF 02 | 14 FF FF 01 | 14 FF FF 04
    /// 每条记录为 [电量%, ?, ?, 组件ID]，0xFF 表示无数据。
    /// 与 Bose App 对照（左 90 / 右 30）确认：0x02 = 左耳，0x01 = 右耳。
    /// 0x04 推测为充电盒，但 App 本身不显示盒电量，该值可能是缓存值，仅供参考。
    /// </summary>
    public const byte ComponentLeft = 0x02;
    public const byte ComponentRight = 0x01;
    public const byte ComponentCase = 0x04;

    public static Dictionary<byte, BatteryInfo> ParseComponents(byte[] p, string source)
    {
        var result = new Dictionary<byte, BatteryInfo>();
        if (p.Length < 8 || p.Length % 4 != 0) return result;
        for (int i = 0; i < p.Length; i += 4)
        {
            byte level = p[i], id = p[i + 3];
            var rec = p.AsSpan(i, 4).ToArray();
            result[id] = new BatteryInfo(level <= 100 ? level : null, null, rec, source);
        }
        return result;
    }

    /// <summary>
    /// 分耳机电量的启发式解析。常见格式为若干个 (组件ID, 电量, 状态) 三元组，
    /// 或固定顺序的 [左, 右, 盒] 电量字节。电量 0xFF 表示不在线（如耳机放回盒中且盒盖关闭）。
    /// </summary>
    internal static (BatteryInfo? Left, BatteryInfo? Right, BatteryInfo? Case) ParsePerBud(byte[] p, string source)
    {
        BatteryInfo? Make(byte level, byte? flags = null) =>
            level <= 100 ? new BatteryInfo(level, flags.HasValue ? (flags.Value & 0x01) != 0 : null, new[] { level }, source) : null;

        // 格式 A：三元组 [id, level, flags] * n，id: 0=左 1=右 2=盒（或 1/2/3）
        if (p.Length >= 6 && p.Length % 3 == 0)
        {
            var triples = Enumerable.Range(0, p.Length / 3).Select(i => (id: p[i * 3], lvl: p[i * 3 + 1], fl: p[i * 3 + 2])).ToList();
            var ids = triples.Select(t => t.id).ToList();
            bool zeroBased = ids.All(x => x <= 2) && ids.Distinct().Count() == ids.Count;
            bool oneBased = ids.All(x => x is >= 1 and <= 3) && ids.Distinct().Count() == ids.Count;
            if (zeroBased || oneBased)
            {
                int off = zeroBased ? 0 : 1;
                BatteryInfo? l = null, r = null, c = null;
                foreach (var t in triples)
                {
                    var b = Make(t.lvl, t.fl);
                    switch (t.id - off) { case 0: l = b; break; case 1: r = b; break; case 2: c = b; break; }
                }
                return (l, r, c);
            }
        }

        // 格式 B：成对 [level, flags] * n，顺序 左、右、盒
        if (p.Length is 4 or 6)
        {
            var l = Make(p[0], p[1]);
            var r = Make(p[2], p[3]);
            var c = p.Length == 6 ? Make(p[4], p[5]) : null;
            if (l != null || r != null) return (l, r, c);
        }

        // 格式 C：纯电量字节 [左, 右, (盒)]
        if (p.Length is 2 or 3)
        {
            return (Make(p[0]), Make(p[1]), p.Length == 3 ? Make(p[2]) : null);
        }

        return (null, null, null);
    }

    internal static BatteryInfo ParseBattery(byte[] p, string source)
    {
        if (p.Length == 0) return new BatteryInfo(null, null, p, source);
        int? level = p[0] <= 100 ? p[0] : null;
        bool? charging = null;
        // 旧款耳机在第 4 字节给出充电状态；多组件格式（长度为 4 的倍数且 ≥8）中第 4 字节是组件 ID，不能这样解释
        if (p.Length is >= 4 and < 8) charging = p[3] != 0;
        return new BatteryInfo(level, charging, p, source);
    }

    // ---------------------------------------------------------------- 音频模式（降噪/通透）

    /// <summary>当前音频模式的索引。</summary>
    public async Task<int> GetCurrentModeIndexAsync()
    {
        var resp = await Get(Ids.CurrentMode);
        if (resp.Payload.Length == 0) throw new BmapException("当前模式响应为空。");
        return resp.Payload[0];
    }

    /// <summary>切换到指定索引的音频模式。</summary>
    /// <param name="voicePrompt">是否让耳机播报模式名称。</param>
    public async Task SetModeAsync(int index, bool voicePrompt = false)
    {
        var (fb, fn) = Ids.CurrentMode;
        var payload = new[] { (byte)index, (byte)(voicePrompt ? 1 : 0) };
        try
        {
            await _client.StartAsync(fb, fn, payload);
        }
        catch (BmapException ex) when (ex.ErrorCode == 0x05)
        {
            // 部分固件不接受 START，退回到 SETGET
            await _client.SetGetAsync(fb, fn, payload);
        }
    }

    /// <summary>读取单个模式槽位的配置（含名称）。</summary>
    public async Task<AudioMode> GetModeAsync(int index)
    {
        var resp = await Get(Ids.ModeConfig, (byte)index);
        return new AudioMode(index, ExtractModeName(resp.Payload), resp.Payload);
    }

    /// <summary>枚举所有可用的音频模式槽位。</summary>
    public async Task<IReadOnlyList<AudioMode>> GetModesAsync(int maxSlots = 12)
    {
        var list = new List<AudioMode>();
        int consecutiveFailures = 0;
        for (int i = 0; i < maxSlots; i++)
        {
            try
            {
                var m = await GetModeAsync(i);
                list.Add(m);
                consecutiveFailures = 0;
            }
            catch (Exception ex) when (ex is BmapException or TimeoutException)
            {
                if (++consecutiveFailures >= 2) break;
            }
        }
        return list;
    }

    /// <summary>
    /// 从模式配置 payload 中提取名称：找最长的一段可打印 UTF-8 文本。
    /// 预设模式（Quiet/Aware/Immersion）在部分固件中名称为空，此时按惯例补全。
    /// </summary>
    internal static string? ExtractModeName(byte[] p)
    {
        string best = "";
        int i = 0;
        while (i < p.Length)
        {
            if (p[i] >= 0x20 && p[i] != 0x7F)
            {
                int start = i;
                while (i < p.Length && p[i] >= 0x20 && p[i] != 0x7F) i++;
                string s;
                try { s = Encoding.UTF8.GetString(p, start, i - start); } catch { s = ""; }
                if (s.Length > best.Length) best = s;
            }
            else i++;
        }
        return best.Length >= 2 ? best.Trim() : null;
    }

    /// <summary>预设模式的惯用名称 → 索引（QC Ultra 系列）。仅在设备未返回名称时作为兜底。</summary>
    public static readonly IReadOnlyDictionary<string, int> PresetAliases = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["quiet"] = 0, ["anc"] = 0, ["nc"] = 0, ["安静"] = 0, ["降噪"] = 0,
        ["aware"] = 1, ["transparency"] = 1, ["通透"] = 1, ["感知"] = 1,
        ["immersion"] = 2, ["immersive"] = 2, ["沉浸"] = 2,
    };

    // ---------------------------------------------------------------- 工具

    private Task<BmapPacket> Get((byte fb, byte fn) id, params byte[] payload) => _client.GetAsync(id.fb, id.fn, payload);

    private static string Ascii(byte[] p) => Encoding.UTF8.GetString(p).TrimEnd('\0').Trim();

    private static async Task<T?> TryAsync<T>(Func<Task<T>> f)
    {
        try { return await f(); }
        catch (Exception ex) when (ex is BmapException or TimeoutException) { return default; }
    }
}
