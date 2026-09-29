using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ObsBatteryGuard.Core;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string DataDirectory { get; }
    public string SettingsPath { get; }
    public string LogDirectory { get; }
    public string LastLoadError { get; private set; } = string.Empty;
    private AppSettings? _lastGood;

    public SettingsStore(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ObsBatteryGuard");
        SettingsPath = Path.Combine(DataDirectory, "settings.json");
        LogDirectory = Path.Combine(DataDirectory, "logs");
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    public AppSettings Load()
    {
        LastLoadError = string.Empty;
        try
        {
            if (!File.Exists(SettingsPath))
            {
                var defaults = new AppSettings();
                Save(defaults);
                return defaults;
            }

            var json = File.ReadAllText(SettingsPath, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? throw new JsonException("配置为空");
            if (settings.SchemaVersion < 2)
            {
                settings.SchemaVersion = 2;
                settings.ObsStatusPollMilliseconds = 300;
                settings.StatusRefreshMilliseconds = 500;
                Save(settings);
            }
            settings.Normalize();
            _lastGood = Clone(settings);
            return settings;
        }
        catch (Exception ex)
        {
            LastLoadError = "配置读取失败，原文件未覆盖：" + ex.Message;
            return _lastGood is not null ? Clone(_lastGood) : new AppSettings
            {
                AutoConnectObs = false, AutoLaunchObs = false, AutoStartRecordingWhenObsConnects = false,
                AutoStartGuardianWithWindows = false, BatteryProtectionEnabled = false,
                AfterStopAction = PostRecordingAction.None, EmergencyAlwaysShutdown = false
            };
        }
    }

    private static AppSettings Clone(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions)!;

    public void Save(AppSettings settings)
    {
        settings.Normalize();
        Directory.CreateDirectory(DataDirectory);
        var temp = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(true);
        }
        File.Move(temp, SettingsPath, true);
        _lastGood = Clone(settings);
    }
}

public static class CredentialProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ObsBatteryGuard.v1.local-only");

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        try
        {
            var data = Encoding.UTF8.GetBytes(value);
            return Convert.ToBase64String(ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return string.Empty;
        }
    }

    public static string Unprotect(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        try
        {
            var data = Convert.FromBase64String(value);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return string.Empty;
        }
    }
}
