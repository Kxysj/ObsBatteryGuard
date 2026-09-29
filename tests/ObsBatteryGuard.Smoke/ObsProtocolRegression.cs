using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ObsBatteryGuard.Core;

internal static class ObsProtocolRegression
{
    public static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var profile = "测试配置";
        var mode = "Simple";
        var recording = false;
        var queueProfileEvent = false;
        var serving = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = tcp.GetStream();
            var header = new StringBuilder();
            var one = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n"))
            {
                if (header.Length > 8192 || await stream.ReadAsync(one, timeout.Token) == 0) throw new IOException("无效测试握手");
                header.Append((char)one[0]);
            }
            var key = header.ToString().Split("\r\n").Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), timeout.Token);
            using var socket = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(10));
            async Task Send(object data) => await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(data), WebSocketMessageType.Text, true, timeout.Token);
            await Send(new { op = 0, d = new { rpcVersion = 1, obsWebSocketVersion = "5.5.0" } });
            var buffer = new byte[16384];
            try
            {
                while (!timeout.IsCancellationRequested)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult received;
                    do
                    {
                        received = await socket.ReceiveAsync(buffer, timeout.Token);
                        if (received.MessageType == WebSocketMessageType.Close) return;
                        message.Write(buffer, 0, received.Count);
                    } while (!received.EndOfMessage);
                    using var doc = JsonDocument.Parse(message.ToArray());
                    var root = doc.RootElement;
                    var data = root.GetProperty("d");
                    if (root.GetProperty("op").GetInt32() == 1)
                    {
                        await Send(new { op = 2, d = new { negotiatedRpcVersion = 1 } }); continue;
                    }
                    var type = data.GetProperty("requestType").GetString();
                    object response = new { };
                    if (queueProfileEvent)
                    {
                        queueProfileEvent = false;
                        await Send(new { op = 5, d = new { eventType = "CurrentProfileChanged", eventData = new { profileName = profile } } });
                    }
                    switch (type)
                    {
                        case "GetVersion": response = new { obsVersion = "31-test" }; break;
                        case "GetProfileList": response = new { currentProfileName = profile }; break;
                        case "GetRecordDirectory": response = new { recordDirectory = @"D:\测试录像" }; break;
                        case "GetRecordStatus": response = new { outputActive = recording, outputPaused = false, outputDuration = 1000L, outputBytes = 1024L }; break;
                        case "GetProfileParameter":
                            var name = data.GetProperty("requestData").GetProperty("parameterName").GetString();
                            response = new { parameterValue = name switch { "Mode" => mode, "RecType" => "FFmpeg", _ => "mkv" } }; break;
                        case "StartRecord":
                            recording = true;
                            await Send(new { op = 5, d = new { eventType = "RecordStateChanged", eventData = new { outputState = "OBS_WEBSOCKET_OUTPUT_STARTED", outputPath = @"D:\测试录像\开始.mkv" } } }); break;
                        case "StopRecord": recording = false; response = new { outputPath = @"D:\测试录像\结束.mkv" }; break;
                    }
                    await Send(new { op = 7, d = new { requestType = type, requestId = data.GetProperty("requestId").GetString(), requestStatus = new { result = true, code = 100 }, responseData = response } });
                }
            }
            catch (WebSocketException) { }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        }, timeout.Token);
        await using var client = new ObsWebSocketClient();
        try
        {
            void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
            Check((await client.ConnectAsync("127.0.0.1", port, "", 3, timeout.Token)).Success, "模拟 OBS 连接失败");
            Check((await client.InspectFormatAsync(timeout.Token)).IsMkv, "运行配置 MKV 读取失败");
            await client.StartRecordingAsync(timeout.Token);
            Check((await client.GetSnapshotAsync(timeout.Token)).OutputPath.EndsWith("开始.mkv"), "开始响应之前的录制事件路径丢失");
            await client.StopRecordingAsync(timeout.Token);
            var stopped = await client.GetSnapshotAsync(timeout.Token);
            Check(stopped.IsConfirmedStopped && stopped.OutputPath == "" && stopped.LastCompletedOutputPath.EndsWith("结束.mkv"), "停止响应文件路径未保留或误当当前文件");
            profile = "新配置"; queueProfileEvent = true;
            Check((await client.GetSnapshotAsync(timeout.Token)).CurrentProfile == "新配置", "配置切换事件未同步");
            mode = "Advanced";
            Check(!(await client.InspectFormatAsync(timeout.Token)).IsMkv, "在线自定义 FFmpeg 被误判为 MKV");
            Console.WriteLine("PASS mock-websocket-live-format-events-profile-and-output-paths");
        }
        finally
        {
            await client.DisconnectAsync(); timeout.Cancel(); listener.Stop();
            await serving;
        }
    }
}
