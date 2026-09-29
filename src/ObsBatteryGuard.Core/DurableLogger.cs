using System.Text;
using System.Text.Json;

namespace ObsBatteryGuard.Core;

public sealed class DurableLogger : IDisposable
{
    private readonly object _sync = new();
    private FileStream? _structuredStream;
    private StreamWriter? _structuredWriter;
    private FileStream? _humanStream;
    private StreamWriter? _humanWriter;
    private readonly string _directory;
    private readonly Func<DateTimeOffset> _now;
    private DateOnly _logDate;
    private bool _disposed;
    private readonly Queue<string> _emergencyLines = new();
    public string LastWriteError { get; private set; } = string.Empty;
    public IReadOnlyList<string> EmergencyLines { get { lock (_sync) return _emergencyLines.ToArray(); } }

    public string CurrentLogPath { get; private set; } = string.Empty;
    public string StructuredLogPath { get; private set; } = string.Empty;
    public event Action<string>? LineWritten;

    public DurableLogger(string directory, Func<DateTimeOffset>? now = null)
    {
        _directory = directory;
        _now = now ?? (() => DateTimeOffset.Now);
        OpenLogs();
    }

    private void OpenLogs()
    {
        _logDate = DateOnly.FromDateTime(_now().LocalDateTime);
        CurrentLogPath = Path.Combine(_directory, $"guard-{_logDate:yyyy-MM-dd}.log");
        StructuredLogPath = Path.Combine(_directory, $"guard-{_logDate:yyyy-MM-dd}.jsonl");
        try
        {
        Directory.CreateDirectory(_directory);
        MigrateExistingStructuredLog(StructuredLogPath, CurrentLogPath);

        _structuredStream = new FileStream(StructuredLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.WriteThrough);
        _structuredWriter = new StreamWriter(_structuredStream, new UTF8Encoding(false)) { AutoFlush = true };
        var newHumanLog = !File.Exists(CurrentLogPath) || new FileInfo(CurrentLogPath).Length == 0;
        _humanStream = new FileStream(CurrentLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.WriteThrough);
        _humanWriter = new StreamWriter(_humanStream, new UTF8Encoding(false)) { AutoFlush = true };
        if (newHumanLog)
        {
            _humanWriter.WriteLine("OBS 电池安全录制－易读运行日志");
            _humanWriter.WriteLine("说明：时间 | 级别 | 模块 | 事件；缩进内容为完整详细信息。");
            _humanWriter.WriteLine(new string('=', 88));
            _humanWriter.Flush();
            _humanStream.Flush(true);
        }
        }
        catch (Exception ex) { LastWriteError = "日志初始化失败：" + ex.Message; }
    }

    public void Info(string category, string message, object? data = null) => Write("INFO", category, message, data);
    public void Warning(string category, string message, object? data = null) => Write("WARN", category, message, data);
    public void Error(string category, string message, object? data = null) => Write("ERROR", category, message, data);
    public void Critical(string category, string message, object? data = null) => Write("CRITICAL", category, message, data);

    public void Write(string level, string category, string message, object? data = null)
    {
        var entry = new
        {
            timestamp = _now(),
            level,
            category,
            message,
            data
        };
        lock (_sync)
        {
            if (_disposed) return;
            if (_logDate != DateOnly.FromDateTime(_now().LocalDateTime))
            {
                CloseStreams();
                OpenLogs();
            }
            var summary = $"{_now():O} | {level} | {category} | {message}";
            try
            {
                if (_structuredWriter is null || _structuredStream is null) throw new IOException("结构化日志不可用");
                _structuredWriter.WriteLine(JsonSerializer.Serialize(entry));
                _structuredWriter.Flush();
                _structuredStream.Flush(true);
            }
            catch (Exception ex) { RememberFailure(summary, ex); }
            try
            {
                if (_humanWriter is null || _humanStream is null) throw new IOException("易读日志不可用");
                _humanWriter.Write(FormatHumanEntry(_now(), level, category, message, data));
                _humanWriter.Flush();
                _humanStream.Flush(true);
            }
            catch (Exception ex) { RememberFailure(summary, ex); }
        }
        if (LineWritten is { } handlers)
            foreach (Action<string> handler in handlers.GetInvocationList())
                try { handler($"{DateTime.Now:HH:mm:ss}  [{level}] {message}"); }
                catch (Exception ex) { lock (_sync) RememberFailure(message, ex); }
    }

    private void RememberFailure(string line, Exception ex)
    {
        LastWriteError = "日志写入异常（保护继续运行）：" + ex.Message;
        while (_emergencyLines.Count >= 200) _emergencyLines.Dequeue();
        _emergencyLines.Enqueue(line);
    }

    public static IReadOnlyList<string> ReadRecentHumanLines(string path, int maximum = 300)
    {
        if (!File.Exists(path)) return Array.Empty<string>();
        var lines = ReadTailWithSharedWrite(path, maximum);
        if (!Path.GetExtension(path).Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
            return lines;
        var result = new List<string>();
        foreach (var line in lines)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var timestamp = root.GetProperty("timestamp").GetDateTimeOffset().ToLocalTime();
                var level = root.GetProperty("level").GetString();
                var message = root.GetProperty("message").GetString();
                result.Add($"{timestamp:yyyy-MM-dd HH:mm:ss}  [{level}] {message}");
            }
            catch
            {
                result.Add("[不完整日志行] " + line);
            }
        }
        return result;
    }

    private static IReadOnlyList<string> ReadTailWithSharedWrite(string path, int maximum)
    {
        maximum = Math.Max(1, maximum);
        var queue = new Queue<string>(maximum);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        while (reader.ReadLine() is { } line)
        {
            if (queue.Count >= maximum) queue.Dequeue();
            queue.Enqueue(line);
        }
        return queue.ToArray();
    }

    private static string FormatHumanEntry(DateTimeOffset timestamp, string level, string category, string message, object? data)
    {
        var builder = new StringBuilder();
        builder.Append(timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
        builder.Append(" | ").Append(MapLevel(level).PadRight(4));
        builder.Append(" | ").Append(MapCategory(category).PadRight(8));
        builder.Append(" | ").AppendLine(message);
        if (data is not null)
        {
            try
            {
                var element = JsonSerializer.SerializeToElement(data);
                AppendHumanDetails(builder, element, string.Empty, 0);
            }
            catch
            {
                builder.Append("    详细信息：").AppendLine(data.ToString());
            }
        }
        return builder.ToString();
    }

    private static void AppendHumanDetails(StringBuilder builder, JsonElement element, string prefix, int depth)
    {
        if (depth > 5)
        {
            builder.Append("    ").Append(prefix).Append(" = ").AppendLine(element.GetRawText());
            return;
        }
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var name = string.IsNullOrWhiteSpace(prefix) ? property.Name : prefix + "." + property.Name;
                AppendHumanDetails(builder, property.Value, name, depth + 1);
            }
            return;
        }
        builder.Append("    ").Append(prefix).Append(" = ").AppendLine(FormatElementValue(element));
    }

    private static string FormatElementValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.True => "是",
        JsonValueKind.False => "否",
        JsonValueKind.Null => "无",
        JsonValueKind.Undefined => "未知",
        _ => element.GetRawText()
    };

    private static string MapLevel(string level) => level.ToUpperInvariant() switch
    {
        "INFO" => "信息",
        "WARN" => "警告",
        "ERROR" => "错误",
        "CRITICAL" => "紧急",
        _ => level
    };

    private static string MapCategory(string category) => category.ToLowerInvariant() switch
    {
        "lifecycle" => "生命周期",
        "obs" => "OBS",
        "battery" => "电池",
        "recording" => "录像",
        "safety" => "安全保护",
        "power" => "电源操作",
        "storage" => "磁盘",
        "settings" => "参数配置",
        "startup" => "开机启动",
        "diagnostic" => "系统自检",
        "ipc" => "本机通信",
        "configuration" => "OBS配置",
        "guardian" => "后台守护",
        "recovery" => "异常恢复",
        _ => category
    };

    private static void MigrateExistingStructuredLog(string structuredPath, string humanPath)
    {
        if (File.Exists(humanPath) || !File.Exists(structuredPath)) return;
        try
        {
            using var input = new FileStream(structuredPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(input, Encoding.UTF8, true);
            using var output = new FileStream(humanPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.WriteThrough);
            using var writer = new StreamWriter(output, new UTF8Encoding(false));
            writer.WriteLine("OBS 电池安全录制－易读运行日志（包含由旧版 JSONL 自动转换的历史）");
            writer.WriteLine(new string('=', 88));
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var timestamp = root.GetProperty("timestamp").GetDateTimeOffset();
                    var level = root.GetProperty("level").GetString() ?? "INFO";
                    var category = root.GetProperty("category").GetString() ?? "general";
                    var message = root.GetProperty("message").GetString() ?? string.Empty;
                    object? details = root.TryGetProperty("data", out var data) && data.ValueKind is not JsonValueKind.Null
                        ? data.Clone()
                        : null;
                    writer.Write(FormatHumanEntry(timestamp, level, category, message, details));
                }
                catch
                {
                    writer.WriteLine("[旧版日志中存在一条不完整记录，已跳过]");
                }
            }
            writer.Flush();
            output.Flush(true);
        }
        catch
        {
            try { if (File.Exists(humanPath)) File.Delete(humanPath); } catch { }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            CloseStreams();
        }
    }

    private void CloseStreams()
    {
        foreach (var resource in new IDisposable?[] { _structuredWriter, _structuredStream, _humanWriter, _humanStream })
            try { resource?.Dispose(); } catch (Exception ex) { LastWriteError = ex.Message; }
        _structuredWriter = _humanWriter = null;
        _structuredStream = _humanStream = null;
    }
}
