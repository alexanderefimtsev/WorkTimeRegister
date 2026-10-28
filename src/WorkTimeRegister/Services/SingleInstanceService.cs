using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace WorkTimeRegister.Services;

public sealed class SingleInstanceService : IDisposable
{
    private readonly FileStream lease;
    private readonly CancellationTokenSource cancellation = new();
    private readonly string pipeName;
    private readonly Task listener;

    private SingleInstanceService(FileStream lease, string pipeName)
    {
        this.lease = lease;
        this.pipeName = pipeName;
        listener = Task.Run(ListenAsync);
    }

    public Func<Task<bool>>? ActivateRequested { get; set; }
    public Func<Task<bool>>? ExitRequested { get; set; }

    private static string Identity(string dataPath) =>
        "WorkTimeRegister-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataPath))))[..24];

    public static SingleInstanceService? TryAcquire(string dataPath)
    {
        var identity = Identity(dataPath);
        var lockPath = Path.Combine(Path.GetTempPath(), identity + ".lock");
        try
        {
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new SingleInstanceService(stream, identity);
        }
        catch (IOException) { return null; }
    }

    public static async Task<bool> SendAsync(string dataPath, bool exit, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(exit ? 15 : 5));
        try
        {
            await using var client = new NamedPipeClientStream(".", Identity(dataPath), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(timeout.Token);
            await client.WriteAsync(new byte[] { exit ? (byte)2 : (byte)1 }, timeout.Token);
            var response = new byte[1];
            return await client.ReadAsync(response, timeout.Token) == 1 && response[0] == 1;
        }
        catch (IOException) { return false; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    private async Task ListenAsync()
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellation.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var request = new byte[1];
                if (await server.ReadAsync(request, timeout.Token) != 1)
                    continue;
                var handler = request[0] switch { 1 => ActivateRequested, 2 => ExitRequested, _ => null };
                var success = handler is not null && await handler();
                await server.WriteAsync(new byte[] { success ? (byte)1 : (byte)0 }, timeout.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                Trace.TraceError($"Single-instance notification failed: {exception}");
                try { await Task.Delay(100, cancellation.Token); }
                catch (OperationCanceledException) { }
            }
        }
    }

    public void Dispose()
    {
        cancellation.Cancel();
        lease.Dispose();
        // The lock file stays in place to avoid unlink/recreate races between processes.
    }
}
