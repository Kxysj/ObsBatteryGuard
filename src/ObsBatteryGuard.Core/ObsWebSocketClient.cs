using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ObsBatteryGuard.Core;

public sealed record ObsRequestResult(bool Success, string Message, JsonElement? Data = null, int StatusCode = 0);

public sealed class ObsWebSocketClient : IObsClient
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClientWebSocket? _socket;
    private string _version = string.Empty;
    private string _profile = string.Empty;
    private string _recordDirectory = string.Empty;
    private string _outputPath = string.Empty;
    private string _lastCompletedPath = string.Empty;
    private DateTimeOffset _nextDirectoryRefresh = DateTimeOffset.MinValue;
    private int _requestTimeoutSeconds = 8;

    public bool IsConnected => _socket?.State == WebSocketState.Open;
    public string RecordDirectory => _recordDirectory;

    public async Task<ObsRequestResult> ConnectAsync(string host, int port, string password, int timeoutSeconds, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected) return new ObsRequestResult(true, "OBS 已连接");
            await DisconnectInternalAsync();
            _profile = _recordDirectory = _outputPath = _lastCompletedPath = string.Empty;
            _socket = new ClientWebSocket();
            _requestTimeoutSeconds = Math.Clamp(timeoutSeconds, 2, 30);
            _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(2, timeoutSeconds)));
            await _socket.ConnectAsync(new Uri($"ws://{host}:{port}"), timeout.Token);

            using var hello = await ReceiveDocumentAsync(timeout.Token);
            if (GetOp(hello.RootElement) != 0)
                throw new InvalidOperationException("OBS WebSocket 握手格式不正确");

            var helloData = hello.RootElement.GetProperty("d");
            string? authentication = null;
            if (helloData.TryGetProperty("authentication", out var authData))
            {
                if (string.IsNullOrEmpty(password))
                    throw new InvalidOperationException("OBS WebSocket 已启用密码，请在软件设置中填写密码");
                authentication = CreateAuthentication(
                    password,
                    authData.GetProperty("salt").GetString() ?? string.Empty,
                    authData.GetProperty("challenge").GetString() ?? string.Empty);
            }

            object identify = authentication is null
                ? new { op = 1, d = new { rpcVersion = 1, eventSubscriptions = 68 } }
                : new { op = 1, d = new { rpcVersion = 1, authentication, eventSubscriptions = 68 } };
            await SendJsonAsync(identify, timeout.Token);

            using var identified = await ReceiveDocumentAsync(timeout.Token);
            if (GetOp(identified.RootElement) != 2)
                throw new InvalidOperationException("OBS WebSocket 身份验证失败");

            var versionResult = await SendRequestCoreAsync("GetVersion", null, timeout.Token);
            if (versionResult.Success && versionResult.Data is { } versionData && versionData.TryGetProperty("obsVersion", out var obsVersion))
                _version = obsVersion.GetString() ?? string.Empty;

            var profileResult = await SendRequestCoreAsync("GetProfileList", null, timeout.Token);
            if (profileResult.Success && profileResult.Data is { } profileData && profileData.TryGetProperty("currentProfileName", out var profile))
                _profile = profile.GetString() ?? string.Empty;

            var directoryResult = await SendRequestCoreAsync("GetRecordDirectory", null, timeout.Token);
            if (directoryResult.Success && directoryResult.Data is { } directoryData)
                _recordDirectory = TryGetString(directoryData, "recordDirectory");
            _nextDirectoryRefresh = DateTimeOffset.Now.AddSeconds(30);

            return new ObsRequestResult(true, $"已连接 OBS {_version}".Trim());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisconnectInternalAsync();
            throw;
        }
        catch (Exception ex)
        {
            await DisconnectInternalAsync();
            return new ObsRequestResult(false, FriendlyConnectionError(ex));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ObsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected)
            return new ObsSnapshot(false, false, false, TimeSpan.Zero, 0, string.Empty, _version, _profile, "OBS 未连接", _recordDirectory);

        var result = await SendRequestAsync("GetRecordStatus", null, cancellationToken);
        if (!result.Success || result.Data is not { } data)
            return new ObsSnapshot(IsConnected, false, false, TimeSpan.Zero, 0, string.Empty, _version, _profile, result.Message, _recordDirectory);
        if (!data.TryGetProperty("outputActive", out var outputActive) ||
            outputActive.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return new ObsSnapshot(IsConnected, false, false, TimeSpan.Zero, 0, string.Empty, _version, _profile, "OBS 未返回有效录像状态", _recordDirectory);

        if (DateTimeOffset.Now >= _nextDirectoryRefresh)
        {
            await GetRecordDirectoryAsync(cancellationToken);
            _nextDirectoryRefresh = DateTimeOffset.Now.AddSeconds(30);
        }

        var active = TryGetBoolean(data, "outputActive");
        var paused = TryGetBoolean(data, "outputPaused");
        var durationMs = TryGetInt64(data, "outputDuration");
        var bytes = TryGetInt64(data, "outputBytes");
        var path = TryGetString(data, "outputPath");
        if (!string.IsNullOrWhiteSpace(path)) _outputPath = path;
        return new ObsSnapshot(IsConnected, active, paused, TimeSpan.FromMilliseconds(Math.Max(0, durationMs)), bytes, active ? _outputPath : string.Empty, _version, _profile, string.Empty, _recordDirectory)
        { LastCompletedOutputPath = _lastCompletedPath };
    }

    public Task<ObsRequestResult> StartRecordingAsync(CancellationToken token) => SendRequestAsync("StartRecord", null, token);
    public Task<ObsRequestResult> StopRecordingAsync(CancellationToken token) => SendRequestAsync("StopRecord", null, token);
    public Task<ObsRequestResult> SplitRecordingAsync(CancellationToken token) => SendRequestAsync("SplitRecordFile", null, token);

    public async Task<string> GetRecordDirectoryAsync(CancellationToken token)
    {
        var result = await SendRequestAsync("GetRecordDirectory", null, token);
        if (result.Success && result.Data is { } data)
        {
            var directory = TryGetString(data, "recordDirectory");
            _recordDirectory = directory;
        }
        else _recordDirectory = string.Empty;
        return _recordDirectory;
    }

    public async Task<ObsFormatInfo> InspectFormatAsync(CancellationToken token)
    {
        // Read the live profile, not a possibly stale basic.ini on disk.
        var profiles = await SendRequestAsync("GetProfileList", null, token);
        if (profiles.Success && profiles.Data is { } profileData) _profile = TryGetString(profileData, "currentProfileName");
        var inspectedProfile = _profile;
        if (!profiles.Success || string.IsNullOrWhiteSpace(inspectedProfile)) return new(false, "", "", "", "", false, "无法确认 OBS 当前配置名称");
        async Task<string?> Read(string category, string parameter)
        {
            var result = await SendRequestAsync("GetProfileParameter", new { parameterCategory = category, parameterName = parameter }, token);
            if (!result.Success || result.Data is not { } data) return null;
            return data.TryGetProperty("parameterValue", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()
                : data.TryGetProperty("defaultParameterValue", out var fallback) && fallback.ValueKind == JsonValueKind.String ? fallback.GetString() : null;
        }
        var mode = await Read("Output", "Mode");
        if (string.IsNullOrWhiteSpace(mode)) return new(false, _profile, "", "", "", false, "无法读取 OBS 当前输出模式，不能验证 MKV");
        var section = mode.Equals("Advanced", StringComparison.OrdinalIgnoreCase) ? "AdvOut" : "SimpleOutput";
        if (section == "AdvOut")
        {
            var type = await Read("AdvOut", "RecType");
            if (!string.Equals(type, "Standard", StringComparison.OrdinalIgnoreCase))
                return new(false, _profile, "", mode, type ?? "未知", false, "自定义 FFmpeg 或未知输出类型，需要在 OBS 中检查实际格式");
        }
        var format = await Read(section, "RecFormat2");
        if (string.IsNullOrWhiteSpace(format)) format = await Read(section, "RecFormat");
        var finalProfile = await SendRequestAsync("GetProfileList", null, token);
        if (!finalProfile.Success || finalProfile.Data is not { } finalData || TryGetString(finalData, "currentProfileName") != inspectedProfile)
            return new(false, inspectedProfile, "", mode, "未知", false, "检查期间 OBS 配置发生变化或连接异常，请重试");
        var verified = string.Equals(format, "mkv", StringComparison.OrdinalIgnoreCase);
        return new(!string.IsNullOrWhiteSpace(format), _profile, "", mode, format ?? "未知", verified,
            $"OBS 运行配置 [{_profile}]：{mode} / {format ?? "未知"}（通过 WebSocket 读取）");
    }

    public async Task<ObsRequestResult> SendRequestAsync(string requestType, object? requestData, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_requestTimeoutSeconds));
        var acquired = false;
        try
        {
            await _gate.WaitAsync(timeout.Token);
            acquired = true;
            return await SendRequestCoreAsync(requestType, requestData, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (acquired) await DisconnectInternalAsync();
            return new ObsRequestResult(false, $"OBS 请求超时：{requestType}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (acquired) await DisconnectInternalAsync();
            throw;
        }
        catch (Exception ex)
        {
            if (acquired) await DisconnectInternalAsync();
            return new ObsRequestResult(false, ex.Message);
        }
        finally
        {
            if (acquired) _gate.Release();
        }
    }

    private async Task<ObsRequestResult> SendRequestCoreAsync(string requestType, object? requestData, CancellationToken cancellationToken)
    {
        if (!IsConnected) return new ObsRequestResult(false, "OBS 未连接");
        var requestId = Guid.NewGuid().ToString("N");
        object request = requestData is null
            ? new { op = 6, d = new { requestType, requestId } }
            : new { op = 6, d = new { requestType, requestId, requestData } };
        if (requestType == "StartRecord") _outputPath = string.Empty;
        await SendJsonAsync(request, cancellationToken);

        while (true)
        {
            using var response = await ReceiveDocumentAsync(cancellationToken);
            var root = response.RootElement;
            if (GetOp(root) == 5)
            {
                HandleEvent(root.GetProperty("d"));
                continue;
            }
            if (GetOp(root) != 7) continue;
            var data = root.GetProperty("d");
            if (!string.Equals(TryGetString(data, "requestId"), requestId, StringComparison.Ordinal)) continue;
            var status = data.GetProperty("requestStatus");
            var success = TryGetBoolean(status, "result");
            var statusCode = TryGetInt32(status, "code");
            var message = success ? "操作成功" : FormatRequestFailure(requestType, statusCode, TryGetString(status, "comment"));
            JsonElement? resultData = data.TryGetProperty("responseData", out var responseData) ? responseData.Clone() : null;
            if (success && requestType == "StopRecord" && resultData is { } stoppedData)
            {
                var path = TryGetString(stoppedData, "outputPath");
                if (!string.IsNullOrWhiteSpace(path)) _lastCompletedPath = path;
                _outputPath = string.Empty;
            }
            return new ObsRequestResult(success, message, resultData, statusCode);
        }
    }

    private void HandleEvent(JsonElement envelope)
    {
        var type = TryGetString(envelope, "eventType");
        if (!envelope.TryGetProperty("eventData", out var data)) return;
        if (type == "CurrentProfileChanged")
        {
            _profile = TryGetString(data, "profileName");
            _recordDirectory = string.Empty;
            _nextDirectoryRefresh = DateTimeOffset.MinValue;
        }
        else if (type == "RecordStateChanged")
        {
            var path = TryGetString(data, "outputPath");
            var state = TryGetString(data, "outputState");
            if (state == "OBS_WEBSOCKET_OUTPUT_STOPPED")
            {
                if (!string.IsNullOrWhiteSpace(path)) _lastCompletedPath = path;
                _outputPath = string.Empty;
            }
            else if (state == "OBS_WEBSOCKET_OUTPUT_STARTED") _outputPath = path;
        }
        else if (type == "RecordFileChanged")
        {
            var next = TryGetString(data, "newOutputPath");
            if (!string.IsNullOrWhiteSpace(next))
            {
                if (!string.IsNullOrWhiteSpace(_outputPath)) _lastCompletedPath = _outputPath;
                _outputPath = next;
            }
        }
    }

    private async Task SendJsonAsync(object value, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value);
        await _socket!.SendAsync(payload, WebSocketMessageType.Text, true, token);
    }

    private async Task<JsonDocument> ReceiveDocumentAsync(CancellationToken token)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var result = await _socket!.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("OBS WebSocket 已断开");
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        return JsonDocument.Parse(stream.ToArray());
    }

    private static int GetOp(JsonElement root) => root.TryGetProperty("op", out var op) ? op.GetInt32() : -1;
    private static bool TryGetBoolean(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static long TryGetInt64(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : 0;
    private static int TryGetInt32(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static string TryGetString(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    private static string FormatRequestFailure(string requestType, int statusCode, string comment)
    {
        var detail = comment.Contains("file splitting is enabled", StringComparison.OrdinalIgnoreCase)
            ? "OBS 输出设置未启用原生文件分割"
            : !string.IsNullOrWhiteSpace(comment)
                ? comment.Trim()
                : statusCode switch
                {
                    500 => "OBS 无法识别此请求",
                    501 => "当前 OBS 配置不支持此请求",
                    502 => "OBS 资源类型无效",
                    503 => "OBS 资源种类无效",
                    504 => "OBS 找不到请求的资源",
                    505 => "请求参数无效",
                    506 => "请求参数类型无效",
                    507 => "请求参数超出范围",
                    508 => "请求参数不能为空",
                    600 => "OBS 输出当前正在运行",
                    601 => "OBS 输出当前没有运行",
                    602 => "OBS 输出已暂停",
                    603 => "OBS 输出没有暂停",
                    604 => "OBS 输出未启用",
                    605 => "OBS 演播室模式转场正在进行",
                    _ => "OBS 拒绝了请求"
                };
        var codeText = statusCode > 0 ? $"，错误码 {statusCode}" : string.Empty;
        return $"{detail}（请求 {requestType}{codeText}）";
    }

    private static string CreateAuthentication(string password, string salt, string challenge)
    {
        var secretBytes = SHA256.HashData(Encoding.UTF8.GetBytes(password + salt));
        var secret = Convert.ToBase64String(secretBytes);
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    private static string FriendlyConnectionError(Exception ex)
    {
        if (ex is OperationCanceledException) return "连接 OBS 超时";
        if (ex is WebSocketException) return "无法连接 OBS，请确认 OBS 已启动且 WebSocket 已启用";
        return ex.Message;
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try { await DisconnectInternalAsync(); }
        finally { _gate.Release(); }
    }

    private Task DisconnectInternalAsync()
    {
        // Failure/cancellation cleanup must not await a close handshake with an unresponsive OBS.
        _socket?.Abort();
        _socket?.Dispose();
        _socket = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _gate.Dispose();
    }
}
