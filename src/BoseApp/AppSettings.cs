using System.Text.Json;
using Microsoft.Win32;

namespace BoseApp;

/// <summary>
/// 用户设置，保存在 %LOCALAPPDATA%\BoseApp\settings.json（非打包应用无法使用 ApplicationData）。
/// </summary>
public sealed class AppSettings
{
    public bool VoicePrompt { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool LowBatteryAlert { get; set; } = true;
    public int LowBatteryThreshold { get; set; } = 20;

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BoseApp");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // ---------------------------------------------------------------- 开机启动（HKCU\...\Run）

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "BoseEarbuds";

    public static bool AutoStart
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
            {
                var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法获取程序路径");
                key.SetValue(RunValue, $"\"{exe}\" --tray");
            }
            else
            {
                key.DeleteValue(RunValue, throwOnMissingValue: false);
            }
        }
    }
}
