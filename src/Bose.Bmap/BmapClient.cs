using System.Collections.Concurrent;
using Bose.Bmap.Protocol;
using Bose.Bmap.Transport;

namespace Bose.Bmap;

public sealed class BmapException : Exception
{
    public byte ErrorCode { get; }
    public BmapPacket? Packet { get; }

    public BmapException(string message, byte errorCode = 0, BmapPacket? packet = null) : base(message)
    {
        ErrorCode = errorCode;
        Packet = packet;
    }
}

/// <summary>
/// BMAP 请求/响应客户端：后台读取循环 + 按 (FunctionBlock, Function) 匹配响应。
/// 设备主动推送的 STATUS 包通过 <see cref="PacketReceived"/> 事件分发。
/// </summary>
public sealed class BmapClient : IAsyncDisposable
{
    private readonly RfcommTransport _transport;
    private readonly ConcurrentDictionary<(byte fb, byte fn), TaskCompletionSource<BmapPacket>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<byte> _rxBuffer = new();
    private Task? _readLoop;

    /// <summary>每收到一个数据包触发（包括请求的响应和设备主动推送）。</summary>
    public event Action<BmapPacket>? PacketReceived;

    /// <summary>发送数据包时触发（用于调试输出）。</summary>
    public event Action<BmapPacket>? PacketSent;

    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(3);

    public BmapClient(RfcommTransport transport)
    {
        _transport = transport;
    }

    public void Start()
    {
        _readLoop ??= Task.Run(ReadLoopAsync);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var chunk = await _transport.ReadAsync(_cts.Token);
                if (chunk.Length == 0) break;
                _rxBuffer.AddRange(chunk);
                DrainBuffer();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            foreach (var tcs in _pending.Values) tcs.TrySetException(ex);
        }
        finally
        {
            foreach (var tcs in _pending.Values)
                tcs.TrySetException(new BmapException("连接已关闭。"));
            IsConnected = false;
            if (!_cts.IsCancellationRequested)
            {
                try { Disconnected?.Invoke(); } catch { }
            }
        }
    }

    /// <summary>读取循环是否仍在运行（连接未断开）。</summary>
    public bool IsConnected { get; private set; } = true;

    /// <summary>连接被设备或系统断开时触发（主动 Dispose 不触发）。</summary>
    public event Action? Disconnected;

    // 同步方法中处理 Span（async 方法里不能使用 ref struct 局部变量）
    private void DrainBuffer()
    {
        while (true)
        {
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_rxBuffer);
            int used = BmapPacket.TryDecode(span, out var packet);
            if (used == 0 || packet == null) break;
            _rxBuffer.RemoveRange(0, used);
            Dispatch(packet);
        }
    }

    private void Dispatch(BmapPacket packet)
    {
        try { PacketReceived?.Invoke(packet); } catch { /* 忽略订阅者异常 */ }

        if (packet.Operator == BmapOperator.Processing) return; // 设备表示"处理中"，继续等最终结果

        if (_pending.TryRemove((packet.FunctionBlock, packet.Function), out var tcs))
            tcs.TrySetResult(packet);
        else
        {
            try { UnsolicitedPacketReceived?.Invoke(packet); } catch { }
        }
    }

    /// <summary>设备主动推送、且没有对应请求在等待的数据包（例如按耳机按键切换模式）。</summary>
    public event Action<BmapPacket>? UnsolicitedPacketReceived;

    /// <summary>发送请求并等待对应的响应。</summary>
    public async Task<BmapPacket> RequestAsync(BmapPacket request, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            var key = (request.FunctionBlock, request.Function);
            var tcs = new TaskCompletionSource<BmapPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[key] = tcs;

            PacketSent?.Invoke(request);
            await _transport.WriteAsync(request.Encode(), ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout ?? DefaultTimeout);
            await using (timeoutCts.Token.Register(() => tcs.TrySetCanceled()))
            {
                try
                {
                    var resp = await tcs.Task;
                    if (resp.Operator == BmapOperator.Error)
                    {
                        byte code = resp.Payload.Length > 0 ? resp.Payload[0] : (byte)0;
                        throw new BmapException(
                            $"{FunctionBlocks.Name(request.FunctionBlock)}.0x{request.Function:X2} 返回错误: {BmapErrors.Describe(code)}",
                            code, resp);
                    }
                    return resp;
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"等待 {FunctionBlocks.Name(request.FunctionBlock)}.0x{request.Function:X2} 响应超时。");
                }
                finally
                {
                    _pending.TryRemove(key, out _);
                }
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>只发送，不等待响应。</summary>
    public async Task SendAsync(BmapPacket packet, CancellationToken ct = default)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            PacketSent?.Invoke(packet);
            await _transport.WriteAsync(packet.Encode(), ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public Task<BmapPacket> GetAsync(byte fb, byte fn, params byte[] payload) =>
        RequestAsync(new BmapPacket(fb, fn, BmapOperator.Get, payload));

    public Task<BmapPacket> SetGetAsync(byte fb, byte fn, params byte[] payload) =>
        RequestAsync(new BmapPacket(fb, fn, BmapOperator.SetGet, payload));

    public Task<BmapPacket> StartAsync(byte fb, byte fn, params byte[] payload) =>
        RequestAsync(new BmapPacket(fb, fn, BmapOperator.Start, payload));

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _transport.DisposeAsync();
        if (_readLoop != null)
        {
            try { await _readLoop.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }
        _cts.Dispose();
    }
}
