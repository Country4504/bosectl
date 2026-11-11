using Bose.Bmap;
using Bose.Bmap.Protocol;
using Bose.Bmap.Transport;

namespace BoseApp;

/// <summary>
/// 管理与耳机的连接：连接 / 断开 / 自动重连，并把设备推送的电量与模式变化转成事件。
/// 事件在后台线程触发，UI 需要自行切回 DispatcherQueue。
/// </summary>
public sealed class EarbudsService : IAsyncDisposable
{
    private BmapClient? _client;
    private BoseDevice? _device;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    public PairedDevice? Target { get; private set; }
    public bool IsConnected => _client?.IsConnected == true;
    public BoseDevice? Device => _device;

    public event Action<bool>? ConnectionChanged;
    public event Action<BatteryInfo?, BatteryInfo?, BatteryInfo?>? BatteryChanged;
    public event Action<int>? ModeChanged;

    public async Task ConnectAsync(string? deviceHint = null)
    {
        await _connectLock.WaitAsync();
        try
        {
            if (IsConnected) return;
            await CloseAsync();

            Target = await RfcommTransport.FindDeviceAsync(deviceHint);
            var transport = new RfcommTransport();
            await transport.ConnectAsync(Target);

            var client = new BmapClient(transport);
            client.PacketReceived += OnPacket;
            client.UnsolicitedPacketReceived += OnUnsolicited;
            client.Disconnected += () => ConnectionChanged?.Invoke(false);
            client.Start();

            _client = client;
            _device = new BoseDevice(client);
            ConnectionChanged?.Invoke(true);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// 设备主动推送的任何消息（无论具体格式）。在耳机上按键切换模式时，
    /// 设备推送的功能号可能不是 AudioModes.0x03，UI 收到后会做一次去抖刷新兜底。
    /// </summary>
    public event Action<BmapPacket>? DevicePushed;

    private void OnUnsolicited(BmapPacket p) => DevicePushed?.Invoke(p);

    /// <summary>设备推送或请求响应都会经过这里，统一转为高层事件。</summary>
    private void OnPacket(BmapPacket p)
    {
        if (p.Operator != BmapOperator.Status) return;

        if (p.FunctionBlock == BoseDevice.Ids.BatteryLevel.fb && p.Function == BoseDevice.Ids.BatteryLevel.fn)
        {
            var comps = BoseDevice.ParseComponents(p.Payload, "push");
            if (comps.Count > 0)
            {
                comps.TryGetValue(BoseDevice.ComponentLeft, out var l);
                comps.TryGetValue(BoseDevice.ComponentRight, out var r);
                comps.TryGetValue(BoseDevice.ComponentCase, out var c);
                BatteryChanged?.Invoke(l, r, c);
            }
        }
        else if (p.FunctionBlock == BoseDevice.Ids.CurrentMode.fb && p.Function == BoseDevice.Ids.CurrentMode.fn
                 && p.Payload.Length > 0)
        {
            ModeChanged?.Invoke(p.Payload[0]);
        }
    }

    private async Task CloseAsync()
    {
        if (_client != null)
        {
            _client.PacketReceived -= OnPacket;
            await _client.DisposeAsync();
        }
        _client = null;
        _device = null;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
    }
}
