namespace ObsBatteryGuard.Core;

public interface IObsClient : IAsyncDisposable
{
    bool IsConnected { get; }
    string RecordDirectory { get; }
    Task<ObsRequestResult> ConnectAsync(string host, int port, string password, int timeoutSeconds, CancellationToken token);
    Task<ObsSnapshot> GetSnapshotAsync(CancellationToken token);
    Task<ObsRequestResult> StartRecordingAsync(CancellationToken token);
    Task<ObsRequestResult> StopRecordingAsync(CancellationToken token);
    Task<ObsRequestResult> SplitRecordingAsync(CancellationToken token);
    Task<string> GetRecordDirectoryAsync(CancellationToken token);
    Task DisconnectAsync();
    Task<ObsFormatInfo> InspectFormatAsync(CancellationToken token) => Task.FromResult(
        new ObsFormatInfo(false, "", "", "", "", false, "当前连接不支持读取运行中的 OBS 格式"));
}
