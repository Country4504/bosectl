using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace Bose.Bmap.Transport;

/// <summary>
/// 已配对蓝牙设备信息。
/// </summary>
public sealed record PairedDevice(string Name, string Id, ulong Address, bool IsConnected)
{
    public string AddressText => FormatAddress(Address);

    public static string FormatAddress(ulong addr) =>
        string.Join(":", Enumerable.Range(0, 6).Select(i => ((addr >> (8 * (5 - i))) & 0xFF).ToString("X2")));

    public static ulong ParseAddress(string text)
    {
        var hex = new string(text.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12) throw new FormatException($"蓝牙地址格式错误: {text}，应为 AA:BB:CC:DD:EE:FF");
        return Convert.ToUInt64(hex, 16);
    }
}

/// <summary>RFCOMM 服务描述。</summary>
public sealed record RfcommServiceInfo(Guid Uuid, string HostName, string ServiceName, RfcommDeviceService Service);

/// <summary>
/// 基于 WinRT 的经典蓝牙 RFCOMM 传输层。
/// Bose 设备通过一个 RFCOMM 通道承载 BMAP 协议。
/// </summary>
public sealed class RfcommTransport : IAsyncDisposable
{
    /// <summary>Bose 自定义的 BMAP RFCOMM 服务 UUID（社区逆向得到）。</summary>
    public static readonly Guid BoseBmapUuid = Guid.Parse("00000000-deca-fade-deca-deafdecacaff");

    /// <summary>标准串口服务 (SPP) UUID，部分老款 Bose 设备在此通道上跑 BMAP。</summary>
    public static readonly Guid SerialPortUuid = Guid.Parse("00001101-0000-1000-8000-00805f9b34fb");

    private StreamSocket? _socket;
    private DataWriter? _writer;
    private IInputStream? _input;

    public bool IsConnected => _socket != null;
    public RfcommServiceInfo? ConnectedService { get; private set; }

    // ---------------------------------------------------------------- 设备发现

    public static async Task<IReadOnlyList<PairedDevice>> ListPairedDevicesAsync()
    {
        var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        var infos = await DeviceInformation.FindAllAsync(selector);
        var result = new List<PairedDevice>();
        foreach (var info in infos)
        {
            try
            {
                using var dev = await BluetoothDevice.FromIdAsync(info.Id);
                if (dev == null) continue;
                result.Add(new PairedDevice(dev.Name, dev.DeviceId, dev.BluetoothAddress,
                    dev.ConnectionStatus == BluetoothConnectionStatus.Connected));
            }
            catch
            {
                // 某些设备可能无法打开，忽略
            }
        }
        return result;
    }

    /// <summary>
    /// 根据名称片段或 MAC 地址查找设备；为空时自动选择名称中含 "Bose" 的已连接设备。
    /// </summary>
    public static async Task<PairedDevice> FindDeviceAsync(string? nameOrAddress)
    {
        var devices = await ListPairedDevicesAsync();
        if (devices.Count == 0) throw new InvalidOperationException("没有找到任何已配对的蓝牙设备。");

        if (!string.IsNullOrWhiteSpace(nameOrAddress))
        {
            if (nameOrAddress.Count(c => c == ':' || c == '-') == 5)
            {
                var addr = PairedDevice.ParseAddress(nameOrAddress);
                return devices.FirstOrDefault(d => d.Address == addr)
                       ?? throw new InvalidOperationException($"未找到地址为 {nameOrAddress} 的已配对设备。");
            }
            var matches = devices.Where(d => d.Name.Contains(nameOrAddress, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) throw new InvalidOperationException($"未找到名称包含 \"{nameOrAddress}\" 的已配对设备。");
            return matches.OrderByDescending(d => d.IsConnected).First();
        }

        var bose = devices.Where(d => d.Name.Contains("Bose", StringComparison.OrdinalIgnoreCase) ||
                                      d.Name.Contains("QC", StringComparison.OrdinalIgnoreCase) ||
                                      d.Name.Contains("Ultra", StringComparison.OrdinalIgnoreCase))
                          .OrderByDescending(d => d.IsConnected).ToList();
        if (bose.Count == 0)
            throw new InvalidOperationException("未找到名称中包含 Bose/QC/Ultra 的已配对设备，请用 --device 指定名称或地址。");
        return bose[0];
    }

    public static async Task<IReadOnlyList<RfcommServiceInfo>> ListServicesAsync(PairedDevice device)
    {
        using var dev = await BluetoothDevice.FromBluetoothAddressAsync(device.Address)
                        ?? throw new InvalidOperationException("无法打开蓝牙设备。");
        var res = await dev.GetRfcommServicesAsync(BluetoothCacheMode.Uncached);
        if (res.Error != BluetoothError.Success)
            res = await dev.GetRfcommServicesAsync(BluetoothCacheMode.Cached);
        if (res.Error != BluetoothError.Success)
            throw new InvalidOperationException($"读取 RFCOMM 服务失败: {res.Error}");

        return res.Services.Select(s => new RfcommServiceInfo(
            s.ServiceId.Uuid,
            s.ConnectionHostName.CanonicalName,
            s.ConnectionServiceName,
            s)).ToList();
    }

    // ---------------------------------------------------------------- 连接

    /// <summary>
    /// 连接到设备上的 BMAP 服务。优先使用 <paramref name="serviceUuid"/>，
    /// 否则依次尝试 Bose 自定义 UUID 与 SPP UUID。
    /// </summary>
    public async Task ConnectAsync(PairedDevice device, Guid? serviceUuid = null, CancellationToken ct = default)
    {
        var services = await ListServicesAsync(device);
        var candidates = new List<Guid>();
        if (serviceUuid.HasValue) candidates.Add(serviceUuid.Value);
        candidates.Add(BoseBmapUuid);
        candidates.Add(SerialPortUuid);

        Exception? last = null;
        foreach (var uuid in candidates.Distinct())
        {
            var svc = services.FirstOrDefault(s => s.Uuid == uuid);
            if (svc == null) continue;
            try
            {
                var socket = new StreamSocket();
                await socket.ConnectAsync(svc.Service.ConnectionHostName, svc.Service.ConnectionServiceName,
                    SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication).AsTask(ct);
                _socket = socket;
                _writer = new DataWriter(socket.OutputStream);
                _input = socket.InputStream;
                ConnectedService = svc;
                return;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        var available = string.Join(", ", services.Select(s => s.Uuid));
        throw new InvalidOperationException(
            $"无法连接 BMAP 服务。设备提供的 RFCOMM 服务: [{available}]。" +
            "可使用 `bosectl services` 查看后通过 --uuid 指定。", last);
    }

    public async Task WriteAsync(byte[] data, CancellationToken ct = default)
    {
        if (_writer == null) throw new InvalidOperationException("尚未连接。");
        _writer.WriteBytes(data);
        await _writer.StoreAsync().AsTask(ct);
        await _writer.FlushAsync().AsTask(ct);
    }

    /// <summary>读取一段数据（至少 1 字节）。连接关闭时返回空数组。</summary>
    public async Task<byte[]> ReadAsync(CancellationToken ct = default)
    {
        if (_input == null) throw new InvalidOperationException("尚未连接。");
        var buffer = new Windows.Storage.Streams.Buffer(1024);
        var result = await _input.ReadAsync(buffer, buffer.Capacity, InputStreamOptions.Partial).AsTask(ct);
        return result.Length == 0 ? Array.Empty<byte>() : result.ToArray();
    }

    public ValueTask DisposeAsync()
    {
        try { _writer?.DetachStream(); } catch { }
        _writer?.Dispose();
        _socket?.Dispose();
        _writer = null;
        _socket = null;
        _input = null;
        return ValueTask.CompletedTask;
    }
}
