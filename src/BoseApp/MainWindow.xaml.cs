using System.Runtime.InteropServices;
using Bose.Bmap;
using BoseApp.Tray;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace BoseApp;

public sealed partial class MainWindow : Window
{
    private readonly EarbudsService _service = new();
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _pollTimer;
    private readonly DispatcherQueueTimer _reconnectTimer;
    private readonly DispatcherQueueTimer _modePollTimer;
    private readonly DispatcherQueueTimer _pushDebounce;
    private bool _switching;
    private bool _polling;

    private readonly List<(int Index, ToggleButton Button)> _modeButtons = new();
    private int _currentMode = -1;
    private bool _suppressToggle;
    private bool _busy;
    private bool _userDisconnected;

    // 托盘
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly TrayIcon _tray;
    private IReadOnlyList<AudioMode> _modes = Array.Empty<AudioMode>();
    private BatteryInfo? _left, _right;
    private string _statusText = "未连接";
    private bool _exiting;
    private readonly HashSet<string> _lowBatteryNotified = new();

    public MainWindow()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        SystemBackdrop = new MicaBackdrop();
        ResizeForDpi(460, 760);
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "BoseApp.ico")); } catch { }

        // 设备会主动推送部分变化，这里再加一个低频轮询兜底
        _pollTimer = _dispatcher.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromSeconds(60);
        _pollTimer.Tick += async (_, _) => await RefreshAsync(silent: true);

        _reconnectTimer = _dispatcher.CreateTimer();
        _reconnectTimer.Interval = TimeSpan.FromSeconds(10);
        _reconnectTimer.IsRepeating = false;
        _reconnectTimer.Tick += async (_, _) => await ConnectAsync(silent: true);

        _service.ConnectionChanged += connected => _dispatcher.TryEnqueue(() => OnConnectionChanged(connected));
        _service.BatteryChanged += (l, r, c) => _dispatcher.TryEnqueue(() => ShowBattery(l, r, c));
        _service.ModeChanged += idx => _dispatcher.TryEnqueue(() => ShowCurrentMode(idx));
        // 收到任何设备主动推送（如按耳机按键）时，稍等片刻再刷新一次，合并连续推送
        _service.DevicePushed += _ => _dispatcher.TryEnqueue(() => { _pushDebounce.Stop(); _pushDebounce.Start(); });

        _pushDebounce = _dispatcher.CreateTimer();
        _pushDebounce.Interval = TimeSpan.FromMilliseconds(400);
        _pushDebounce.IsRepeating = false;
        _pushDebounce.Tick += async (_, _) => await RefreshAsync(silent: true);

        // 模式轮询：只发一个很小的 GET 请求，保证按耳机按键切换后 2 秒内同步，
        // 即使设备在当前固件下不推送模式变化也能工作
        _modePollTimer = _dispatcher.CreateTimer();
        _modePollTimer.Interval = TimeSpan.FromSeconds(2);
        _modePollTimer.Tick += async (_, _) => await PollModeAsync();

        BuildPresetButtons();
        PromptCheck.IsChecked = _settings.VoicePrompt;
        PromptCheck.Click += (_, _) => { _settings.VoicePrompt = PromptCheck.IsChecked == true; _settings.Save(); };

        // ---- 托盘图标
        _tray = new TrayIcon("Bose Earbuds");
        _tray.LeftClick += ToggleWindow;
        _tray.ShowRequested += ShowWindow;
        _tray.ThemeChanged += UpdateTray;
        _tray.MenuProvider = BuildTrayMenu;
        UpdateTray();

        // 点关闭按钮时隐藏到托盘，而不是退出
        AppWindow.Closing += (_, e) =>
        {
            if (_exiting || !_settings.CloseToTray) return;
            e.Cancel = true;
            AppWindow.Hide();
        };
        Closed += async (_, _) =>
        {
            _tray.Dispose();
            await _service.DisposeAsync();
        };

        // 不依赖 Loaded：以 --tray 启动时窗口不会显示，Loaded 不会触发
        _dispatcher.TryEnqueue(async () => await ConnectAsync(silent: false));
    }

    // ================================================================ 连接

    private async Task ConnectAsync(bool silent)
    {
        if (_busy || _service.IsConnected) return;
        SetBusy(true, "正在连接…");
        try
        {
            await _service.ConnectAsync();
            _userDisconnected = false;
            HideError();
            await LoadAllAsync();
            _pollTimer.Start();
            _modePollTimer.Start();
        }
        catch (Exception ex)
        {
            SetStatus(false, "未连接");
            if (!silent) ShowError("连接失败", ex);
            ScheduleReconnect();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnConnectionChanged(bool connected)
    {
        SetStatus(connected, connected ? "已连接" : "连接已断开");
        if (!connected)
        {
            _pollTimer.Stop();
            _modePollTimer.Stop();
            ScheduleReconnect();
        }
    }

    private void ScheduleReconnect()
    {
        if (!_userDisconnected) _reconnectTimer.Start();
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (_service.IsConnected) await DisconnectAsync();
        else
        {
            _userDisconnected = false;
            await ConnectAsync(silent: false);
        }
    }

    private async Task DisconnectAsync()
    {
        _userDisconnected = true;
        _pollTimer.Stop();
        _modePollTimer.Stop();
        _reconnectTimer.Stop();
        await _service.DisposeAsync();
        SetStatus(false, "已断开");
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (!_service.IsConnected) await ConnectAsync(silent: false);
        else await RefreshAsync(silent: false);
    }

    // ================================================================ 数据加载

    private async Task LoadAllAsync()
    {
        var dev = _service.Device!;
        var target = _service.Target!;
        DeviceNameText.Text = target.Name;
        AddressText.Text = target.AddressText;
        SetStatus(true, "已连接");

        try
        {
            var info = await dev.GetProductInfoAsync();
            if (!string.IsNullOrEmpty(info.Name)) DeviceNameText.Text = info.Name;
            FirmwareText.Text = info.Firmware ?? "--";
            SerialText.Text = info.Serial ?? "--";
            BmapText.Text = info.BmapVersion ?? "--";
        }
        catch { /* 设备信息不是必须的 */ }

        try
        {
            var modes = await dev.GetModesAsync();
            _modes = modes;
            BuildCustomModeButtons(modes);
        }
        catch (Exception ex) { ShowError("读取模式列表失败", ex); }

        await RefreshAsync(silent: false);
    }

    private async Task RefreshAsync(bool silent)
    {
        var dev = _service.Device;
        if (dev == null || !_service.IsConnected) return;
        try
        {
            var (l, r, c, _) = await dev.GetPerBudBatteryAsync();
            ShowBattery(l, r, c);
            ShowCurrentMode(await dev.GetCurrentModeIndexAsync());
            UpdatedText.Text = $"更新于 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            if (!silent) ShowError("刷新失败", ex);
        }
    }

    // ================================================================ 电量显示

    private void ShowBattery(BatteryInfo? left, BatteryInfo? right, BatteryInfo? @case)
    {
        ShowBud(left, LeftText, LeftBar, LeftIcon);
        ShowBud(right, RightText, RightBar, RightIcon);
        CaseText.Text = @case?.Level is int c ? $"充电盒: {c}%（参考值）" : "充电盒: --";
        UpdatedText.Text = $"更新于 {DateTime.Now:HH:mm:ss}";
        _left = left;
        _right = right;
        CheckLowBattery("左耳", left);
        CheckLowBattery("右耳", right);
        UpdateTray();
    }

    /// <summary>电量跌破阈值时弹一次托盘通知；电量回升（充电）后重置。</summary>
    private void CheckLowBattery(string name, BatteryInfo? b)
    {
        if (b?.Level is not int level) return;
        if (level > _settings.LowBatteryThreshold) { _lowBatteryNotified.Remove(name); return; }
        if (!_settings.LowBatteryAlert || !_lowBatteryNotified.Add(name)) return;
        _tray.ShowBalloon("耳机电量低", $"{name}电量仅剩 {level}%，请及时充电。", warning: true);
    }

    private static void ShowBud(BatteryInfo? b, TextBlock text, ProgressBar bar, FontIcon icon)
    {
        if (b?.Level is int level)
        {
            text.Text = $"{level}%";
            bar.Value = level;
            bar.ShowError = level <= 20;
            icon.Glyph = BatteryGlyph(level);
        }
        else
        {
            text.Text = "不在线";
            bar.Value = 0;
            bar.ShowError = false;
            icon.Glyph = "\uE996"; // BatteryUnknown
        }
    }

    /// <summary>Segoe Fluent Icons: Battery0..Battery9 = E850..E859, Battery10 = E83F。</summary>
    private static string BatteryGlyph(int level)
    {
        if (level >= 95) return "\uE83F";
        int step = Math.Clamp(level / 10, 0, 9);
        return ((char)(0xE850 + step)).ToString();
    }

    // ================================================================ 模式

    private static readonly (int Index, string Title, string Sub, string Glyph)[] Presets =
    {
        (0, "Quiet", "降噪", "\uE7F6"),
        (1, "Aware", "通透", "\uE7B3"),
        (2, "Immersion", "沉浸", "\uE8D6"),
    };

    private void BuildPresetButtons()
    {
        foreach (var (idx, title, sub, glyph) in Presets)
        {
            var content = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 22 });
            content.Children.Add(new TextBlock { Text = sub, HorizontalAlignment = HorizontalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            content.Children.Add(new TextBlock
            {
                Text = title,
                HorizontalAlignment = HorizontalAlignment.Center,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Opacity = 0.7,
            });

            var btn = CreateModeButton(idx, content);
            btn.Padding = new Thickness(8, 14, 8, 14);
            Grid.SetColumn(btn, idx);
            PresetGrid.Children.Add(btn);
        }
    }

    private void BuildCustomModeButtons(IReadOnlyList<AudioMode> modes)
    {
        CustomPanel.Children.Clear();
        _modeButtons.RemoveAll(m => m.Index > 2);

        var custom = modes.Where(m => m.Index > 2 && !m.IsEmpty).ToList();
        CustomHeader.Visibility = custom.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var m in custom)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = m.Name ?? $"模式 {m.Index}" });
            var idxText = new TextBlock { Text = $"#{m.Index}", Opacity = 0.5 };
            Grid.SetColumn(idxText, 1);
            row.Children.Add(idxText);

            var btn = CreateModeButton(m.Index, row);
            btn.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            btn.Padding = new Thickness(14, 10, 14, 10);
            CustomPanel.Children.Add(btn);
        }
        ShowCurrentMode(_currentMode);
    }

    private ToggleButton CreateModeButton(int index, object content)
    {
        var btn = new ToggleButton
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Tag = index,
        };
        btn.Click += OnModeClick;
        _modeButtons.Add((index, btn));
        return btn;
    }

    private async void OnModeClick(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        var btn = (ToggleButton)sender;
        await SwitchModeAsync((int)btn.Tag);
    }

    private async Task SwitchModeAsync(int idx)
    {
        var dev = _service.Device;

        if (dev == null || !_service.IsConnected)
        {
            ShowCurrentMode(_currentMode);
            ShowError("尚未连接耳机", null);
            return;
        }

        int previous = _currentMode;
        ShowCurrentMode(idx); // 乐观更新
        _switching = true;
        try
        {
            await dev.SetModeAsync(idx, _settings.VoicePrompt);
            ShowCurrentMode(await dev.GetCurrentModeIndexAsync());
        }
        catch (Exception ex)
        {
            ShowCurrentMode(previous);
            ShowError("切换模式失败", ex);
            if (!AppWindow.IsVisible) _tray.ShowBalloon("切换模式失败", ex.Message, warning: true);
        }
        finally
        {
            _switching = false;
        }
    }

    /// <summary>轻量轮询当前模式；仅在变化时更新界面。</summary>
    private async Task PollModeAsync()
    {
        var dev = _service.Device;
        if (_polling || _switching || _busy || dev == null || !_service.IsConnected) return;
        _polling = true;
        try
        {
            int idx = await dev.GetCurrentModeIndexAsync();
            if (!_switching && idx != _currentMode) ShowCurrentMode(idx);
        }
        catch { /* 轮询失败忽略，断线由 Disconnected 事件处理 */ }
        finally
        {
            _polling = false;
        }
    }

    private void ShowCurrentMode(int index)
    {
        _currentMode = index;
        _suppressToggle = true;
        foreach (var (i, b) in _modeButtons) b.IsChecked = i == index;
        _suppressToggle = false;
        UpdateTray();
    }

    // ================================================================ 托盘

    private void UpdateTray()
    {
        if (_tray == null) return; // 构造期间尚未创建
        bool connected = _service.IsConnected;

        var state = new TrayIconRenderer.State(_left?.Level, _right?.Level, connected, TrayIcon.IsTaskbarLight());
        _tray.SetIcon(TrayIconRenderer.Render(state, TrayIcon.RecommendedIconSize()));

        string tip;
        if (connected)
        {
            static string Lv(BatteryInfo? b) => b?.Level is int v ? $"{v}%" : "--";
            tip = $"{DeviceNameText.Text}\n左 {Lv(_left)}  ·  右 {Lv(_right)}\n模式: {ModeName(_currentMode)}";
        }
        else
        {
            tip = $"Bose Earbuds\n{_statusText}";
        }
        _tray.SetTooltip(tip);
    }

    private string ModeName(int index)
    {
        var preset = Presets.FirstOrDefault(p => p.Index == index);
        if (preset.Title != null) return $"{preset.Sub} {preset.Title}";
        return _modes.FirstOrDefault(m => m.Index == index)?.Name ?? (index >= 0 ? $"#{index}" : "--");
    }

    private IReadOnlyList<TrayMenuItem> BuildTrayMenu()
    {
        bool connected = _service.IsConnected;
        var items = new List<TrayMenuItem>
        {
            new(AppWindow.IsVisible ? "隐藏窗口" : "打开主窗口", ToggleWindow, IsDefault: true),
            TrayMenuItem.Separator,
        };

        static string Lv(BatteryInfo? b) => b?.Level is int v ? $"{v}%" : "--";
        items.Add(connected
            ? new TrayMenuItem($"左耳 {Lv(_left)}    右耳 {Lv(_right)}", Enabled: false)
            : new TrayMenuItem(_statusText, Enabled: false));
        items.Add(TrayMenuItem.Separator);

        foreach (var p in Presets)
            items.Add(new($"{p.Sub} ({p.Title})", () => _ = SwitchModeAsync(p.Index),
                Checked: p.Index == _currentMode, Enabled: connected, Radio: true));

        var custom = _modes.Where(m => m.Index > 2 && !m.IsEmpty).ToList();
        if (custom.Count > 0)
        {
            items.Add(TrayMenuItem.Separator);
            foreach (var m in custom)
            {
                int idx = m.Index;
                items.Add(new(m.Name ?? $"模式 {idx}", () => _ = SwitchModeAsync(idx),
                    Checked: idx == _currentMode, Enabled: connected, Radio: true));
            }
        }

        items.Add(TrayMenuItem.Separator);
        items.Add(new("刷新", () => { if (connected) _ = RefreshAsync(silent: false); else _ = ConnectAsync(silent: false); }, Enabled: !_busy));
        items.Add(connected
            ? new TrayMenuItem("断开连接", () => _ = DisconnectAsync())
            : new TrayMenuItem("连接", () => { _userDisconnected = false; _ = ConnectAsync(silent: false); }, Enabled: !_busy));
        items.Add(TrayMenuItem.Separator);

        items.Add(new("切换时语音播报", () =>
        {
            _settings.VoicePrompt = !_settings.VoicePrompt;
            PromptCheck.IsChecked = _settings.VoicePrompt;
            _settings.Save();
        }, Checked: _settings.VoicePrompt));
        items.Add(new("低电量提醒", () => { _settings.LowBatteryAlert = !_settings.LowBatteryAlert; _settings.Save(); },
            Checked: _settings.LowBatteryAlert));
        items.Add(new("关闭窗口时最小化到托盘", () => { _settings.CloseToTray = !_settings.CloseToTray; _settings.Save(); },
            Checked: _settings.CloseToTray));
        items.Add(new("开机自动启动", ToggleAutoStart, Checked: SafeAutoStart()));
        items.Add(TrayMenuItem.Separator);
        items.Add(new("退出", ExitApp));
        return items;
    }

    private static bool SafeAutoStart()
    {
        try { return AppSettings.AutoStart; } catch { return false; }
    }

    private void ToggleAutoStart()
    {
        try { AppSettings.AutoStart = !AppSettings.AutoStart; }
        catch (Exception ex) { _tray.ShowBalloon("设置开机启动失败", ex.Message, warning: true); }
    }

    private void ToggleWindow()
    {
        if (AppWindow.IsVisible && IsForeground()) AppWindow.Hide();
        else ShowWindow();
    }

    private void ShowWindow()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } p)
            p.Restore();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    private bool IsForeground() => GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(this);

    private void ExitApp()
    {
        _exiting = true;
        _tray.Dispose();
        Close();
        Application.Current.Exit();
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);

    // ================================================================ UI 辅助

    private void SetStatus(bool connected, string text)
    {
        StatusText.Text = text;
        StatusDot.Fill = (Brush)Application.Current.Resources[connected ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush"];
        ConnectButton.Content = connected ? "断开" : "连接";
        _statusText = text;
        UpdateTray();
    }

    private void SetBusy(bool busy, string? text = null)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        ConnectButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        if (text != null) StatusText.Text = text;
    }

    private void ShowError(string title, Exception? ex)
    {
        ErrorBar.Title = title;
        ErrorBar.Message = ex == null ? "" : ex.InnerException != null ? $"{ex.Message}\n{ex.InnerException.Message}" : ex.Message;
        ErrorBar.IsOpen = true;
    }

    private void HideError() => ErrorBar.IsOpen = false;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void ResizeForDpi(int width, int height)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(width * scale), (int)(height * scale)));
    }
}
