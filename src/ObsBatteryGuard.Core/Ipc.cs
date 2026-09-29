using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ObsBatteryGuard.Core;

public static class IpcProtocol
{
    public const string PipeName = "ObsBatteryGuard.v1";
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

public sealed class GuardianIpcServer
{
    private readonly GuardianEngine _engine;
    private readonly DurableLogger _logger;
    private readonly string _pipeName;
    private readonly Action? _requestShutdown;

    public GuardianIpcServer(GuardianEngine engine, DurableLogger logger, string? pipeName = null, Action? requestShutdown = null)
    {
        _engine = engine;
        _logger = logger;
        _pipeName = pipeName ?? IpcProtocol.PipeName;
        _requestShutdown = requestShutdown;
    }

    public async Task RunAsync(CancellationToken token)
    {
        // Dedicated listeners keep status queries responsive while a command is waiting for OBS.
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => RunListenerAsync(token)));
    }

    private async Task RunListenerAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    4,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token);
                await ProcessClientAsync(pipe, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error("ipc", "本机通信异常", new { ex.Message });
                try { await Task.Delay(500, token); } catch { }
            }
        }
    }

    private async Task ProcessClientAsync(Stream pipe, CancellationToken token)
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        readTimeout.CancelAfter(TimeSpan.FromSeconds(3));
        var line = await reader.ReadLineAsync(readTimeout.Token);
        if (string.IsNullOrWhiteSpace(line)) return;
        IpcResponse response;
        var exitAccepted = false;
        try
        {
            var request = JsonSerializer.Deserialize<IpcRequest>(line, IpcProtocol.JsonOptions) ?? new IpcRequest();
            if (request.Command.Trim().Equals("exit_application_confirmed", StringComparison.OrdinalIgnoreCase))
            {
                if (_requestShutdown is null || request.Arguments is null ||
                    !request.Arguments.TryGetValue("processId", out var expected) || expected != Environment.ProcessId.ToString())
                    response = IpcResponse.Fail("退出请求没有匹配的后台进程身份，未执行退出", _engine.GetStatus());
                else
                {
                    response = _engine.RequestApplicationExit();
                    exitAccepted = response.Success;
                }
            }
            else if (request.Command.Trim().Equals("shutdown_guard", StringComparison.OrdinalIgnoreCase))
            {
                response = IpcResponse.Fail("请通过软件关闭确认流程退出，不接受旧的自动结束指令", _engine.GetStatus());
            }
            else
            {
                response = await _engine.HandleCommandAsync(request, token);
            }
        }
        catch (Exception ex)
        {
            response = IpcResponse.Fail("处理命令失败：" + ex.Message, _engine.GetStatus());
        }
        using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        writeTimeout.CancelAfter(TimeSpan.FromSeconds(3));
        try { await writer.WriteLineAsync(JsonSerializer.Serialize(response, IpcProtocol.JsonOptions).AsMemory(), writeTimeout.Token); }
        finally { if (exitAccepted) _requestShutdown?.Invoke(); }
    }
}

public static class GuardianIpcClient
{
    public static async Task<IpcResponse> SendAsync(string command, int timeoutMilliseconds = 5000, CancellationToken cancellationToken = default, string? pipeName = null, Dictionary<string, string>? arguments = null)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutMilliseconds);
            await using var pipe = new NamedPipeClientStream(".", pipeName ?? IpcProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.WriteThrough);
            await pipe.ConnectAsync(timeout.Token);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            var request = new IpcRequest { Command = command, Arguments = arguments };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, IpcProtocol.JsonOptions).AsMemory(), timeout.Token);
            var responseLine = await reader.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(responseLine)) return IpcResponse.Fail("后台守护没有返回数据");
            return JsonSerializer.Deserialize<IpcResponse>(responseLine, IpcProtocol.JsonOptions) ?? IpcResponse.Fail("后台守护返回了无效数据");
        }
        catch (OperationCanceledException)
        {
            return IpcResponse.Fail("连接后台守护超时");
        }
        catch (Exception ex)
        {
            return IpcResponse.Fail("后台守护未运行：" + ex.Message);
        }
    }
}
