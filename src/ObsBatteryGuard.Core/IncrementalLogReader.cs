using System.Text;

namespace ObsBatteryGuard.Core;

/// <summary>Bounded UTF-8 tail reader. Incomplete final lines wait for the next read.</summary>
public sealed class IncrementalLogReader
{
    private const int MaximumReadBytes = 256 * 1024;
    private readonly Queue<string> _lines = new();
    private string _path = string.Empty;
    private long _position;
    private bool _initialized;
    public string Read(string path, bool reset = false)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (reset || !_initialized || path != _path || stream.Length < _position)
        {
            _lines.Clear();
            _position = Math.Max(0, stream.Length - MaximumReadBytes);
            _path = path;
            _initialized = true;
            reset = true;
        }
        var skipFirst = reset && _position > 0;
        if (stream.Length - _position > MaximumReadBytes)
        {
            _position = stream.Length - MaximumReadBytes;
            _lines.Clear();
            skipFirst = true;
        }
        stream.Position = _position;
        var bytes = new byte[(int)Math.Min(MaximumReadBytes, stream.Length - _position)];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = stream.Read(bytes, count, bytes.Length - count);
            if (read == 0) break;
            count += read;
        }
        if (count == 0) return string.Join(Environment.NewLine, _lines);
        var end = Array.LastIndexOf(bytes, (byte)'\n', count - 1, count);
        if (end < 0)
        {
            if (count == MaximumReadBytes) _position += count;
            return string.Join(Environment.NewLine, _lines);
        }
        var start = skipFirst ? Array.IndexOf(bytes, (byte)'\n', 0, end + 1) + 1 : 0;
        var text = Encoding.UTF8.GetString(bytes, start, end + 1 - start).TrimStart('\uFEFF');
        foreach (var line in text.Split('\n').SkipLast(1))
        {
            _lines.Enqueue(line.TrimEnd('\r'));
            while (_lines.Count > 1200) _lines.Dequeue();
        }
        _position += end + 1;
        return string.Join(Environment.NewLine, _lines);
    }

    public static string Filter(string text, string search, bool problemsOnly)
    {
        var entries = new List<string>();
        var current = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (line.Length >= 10 && DateOnly.TryParseExact(line[..10], "yyyy-MM-dd", out _) && current.Length > 0)
            {
                entries.Add(current.ToString()); current.Clear();
            }
            current.AppendLine(line.TrimEnd('\r'));
        }
        if (current.Length > 0) entries.Add(current.ToString());
        return string.Join("", entries.Where(entry =>
            (string.IsNullOrWhiteSpace(search) || entry.Contains(search, StringComparison.OrdinalIgnoreCase)) &&
            (!problemsOnly || entry.Contains("警告") || entry.Contains("错误") || entry.Contains("严重") || entry.Contains("WARN") || entry.Contains("ERROR") || entry.Contains("CRITICAL"))));
    }
}
