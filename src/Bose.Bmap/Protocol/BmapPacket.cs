namespace Bose.Bmap.Protocol;

/// <summary>
/// BMAP (Bose Messaging And Protocol) 操作符，位于包头第 3 个字节的低 4 位。
/// </summary>
public enum BmapOperator : byte
{
    Set = 0x0,
    Get = 0x1,
    SetGet = 0x2,
    Status = 0x3,
    Error = 0x4,
    Start = 0x5,
    Result = 0x6,
    Processing = 0x7,
}

/// <summary>
/// 已知的 Function Block（功能块）。数值来自社区逆向资料（based-connect 等），
/// 不同产品支持的集合不同，可用 `bosectl probe` 查看设备实际支持的功能块。
/// </summary>
public static class FunctionBlocks
{
    public const byte ProductInfo = 0x00;
    public const byte Settings = 0x01;
    public const byte Status = 0x02;
    public const byte FirmwareUpdate = 0x03;
    public const byte DeviceManagement = 0x04;
    public const byte AudioManagement = 0x05;
    public const byte CallManagement = 0x06;
    public const byte Control = 0x07;
    public const byte Debug = 0x08;
    public const byte Notification = 0x09;
    public const byte HearingAssistance = 0x0C;
    public const byte DataCollection = 0x0D;
    public const byte HeartRate = 0x0E;
    public const byte VoicePersonalAssistant = 0x10;
    public const byte AudioModes = 0x1F;

    public static string Name(byte fb) => fb switch
    {
        ProductInfo => "ProductInfo",
        Settings => "Settings",
        Status => "Status",
        FirmwareUpdate => "FirmwareUpdate",
        DeviceManagement => "DeviceManagement",
        AudioManagement => "AudioManagement",
        CallManagement => "CallManagement",
        Control => "Control",
        Debug => "Debug",
        Notification => "Notification",
        HearingAssistance => "HearingAssistance",
        DataCollection => "DataCollection",
        HeartRate => "HeartRate",
        VoicePersonalAssistant => "VPA",
        AudioModes => "AudioModes",
        _ => $"FB_0x{fb:X2}",
    };
}

/// <summary>
/// 一个 BMAP 数据包：
/// <code>
/// [0] FunctionBlock
/// [1] Function
/// [2] bit7-6 DeviceId | bit5-4 Port | bit3-0 Operator
/// [3] PayloadLength
/// [4..] Payload
/// </code>
/// </summary>
public sealed record BmapPacket(
    byte FunctionBlock,
    byte Function,
    BmapOperator Operator,
    byte[] Payload,
    byte DeviceId = 0,
    byte Port = 0)
{
    public const int HeaderLength = 4;

    public byte[] Encode()
    {
        if (Payload.Length > 255) throw new ArgumentException("BMAP payload 不能超过 255 字节");
        var buf = new byte[HeaderLength + Payload.Length];
        buf[0] = FunctionBlock;
        buf[1] = Function;
        buf[2] = (byte)(((DeviceId & 0x3) << 6) | ((Port & 0x3) << 4) | ((byte)Operator & 0x0F));
        buf[3] = (byte)Payload.Length;
        Payload.CopyTo(buf, HeaderLength);
        return buf;
    }

    /// <summary>
    /// 尝试从缓冲区头部解析一个完整的数据包。成功时返回消耗的字节数，否则返回 0（数据不完整）。
    /// </summary>
    public static int TryDecode(ReadOnlySpan<byte> buffer, out BmapPacket? packet)
    {
        packet = null;
        if (buffer.Length < HeaderLength) return 0;
        int len = buffer[3];
        if (buffer.Length < HeaderLength + len) return 0;

        byte b2 = buffer[2];
        packet = new BmapPacket(
            buffer[0],
            buffer[1],
            (BmapOperator)(b2 & 0x0F),
            buffer.Slice(HeaderLength, len).ToArray(),
            (byte)((b2 >> 6) & 0x3),
            (byte)((b2 >> 4) & 0x3));
        return HeaderLength + len;
    }

    public bool IsResponseFor(byte fb, byte fn) => FunctionBlock == fb && Function == fn &&
        Operator is BmapOperator.Status or BmapOperator.Result or BmapOperator.Error or BmapOperator.Processing;

    public override string ToString()
    {
        var hex = Hex.Format(Encode());
        var ascii = Hex.Printable(Payload);
        return $"{FunctionBlocks.Name(FunctionBlock)}.0x{Function:X2} {Operator,-10} len={Payload.Length,-3} [{hex}]" +
               (ascii.Length > 0 ? $"  \"{ascii}\"" : "");
    }
}

/// <summary>BMAP 错误码（ERROR 操作符响应的 payload[0]）。</summary>
public static class BmapErrors
{
    public static string Describe(byte code) => code switch
    {
        0x01 => "Length (长度错误)",
        0x02 => "Chksum",
        0x03 => "FblockNotSupp (功能块不支持)",
        0x04 => "FuncNotSupp (功能不支持)",
        0x05 => "OpNotSupp (操作符不支持)",
        0x06 => "InvalidData (数据无效)",
        0x07 => "DataUnavailable (数据不可用)",
        0x08 => "Runtime",
        0x09 => "Timeout",
        0x0A => "InvalidState (状态无效)",
        0x0B => "DeviceNotFound",
        0x0C => "Busy",
        0x0D => "NoConnection",
        0x0E => "InsecureConnection",
        0x0F => "InsecureRequest",
        0x14 => "OpNotSuppInState",
        _ => $"Unknown(0x{code:X2})",
    };
}

public static class Hex
{
    public static string Format(ReadOnlySpan<byte> data) => Convert.ToHexString(data)
        .Chunk(2).Select(c => new string(c)).Aggregate("", (a, b) => a.Length == 0 ? b : a + " " + b);

    public static byte[] Parse(string text)
    {
        var clean = new string(text.Where(Uri.IsHexDigit).ToArray());
        if (clean.Length % 2 != 0) throw new FormatException($"十六进制字符串长度必须为偶数: {text}");
        return Convert.FromHexString(clean);
    }

    /// <summary>提取 payload 中可打印的 ASCII/UTF-8 片段，便于逆向时观察名称等字段。</summary>
    public static string Printable(ReadOnlySpan<byte> data)
    {
        int printable = 0;
        foreach (var b in data) if (b >= 0x20 && b < 0x7F) printable++;
        if (printable < 3) return "";
        var chars = new char[data.Length];
        for (int i = 0; i < data.Length; i++) chars[i] = data[i] is >= 0x20 and < 0x7F ? (char)data[i] : '.';
        return new string(chars);
    }
}
