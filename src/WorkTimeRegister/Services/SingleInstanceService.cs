using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace WorkTimeRegister.Services;

public sealed class SingleInstanceService : IDisposable
{
    private readonly FileStream lease;
    private readonly FileStream? previousLease;
    private readonly FileStream? legacyLease;
    private readonly CancellationTokenSource cancellation = new();
    private readonly string pipeName;
    private readonly UnixInstanceSocket? unixSocket;
    private readonly Task listener;

    private SingleInstanceService(FileStream lease, FileStream? previousLease, FileStream? legacyLease, string pipeName)
    {
        this.lease = lease;
        this.previousLease = previousLease;
        this.legacyLease = legacyLease;
        this.pipeName = pipeName;
        if (OperatingSystem.IsWindows())
        {
            // Detect pipe creation errors before time tracking starts.
            using var probe = CreatePipeServer();
        }
        else
            unixSocket = new UnixInstanceSocket(pipeName);
        listener = Task.Run(ListenAsync);
    }

    public Func<Task<bool>>? ActivateRequested { get; set; }
    public Func<Task<bool>>? ExitRequested { get; set; }

    private static string Identity(string dataPath) =>
        "WorkTimeRegister-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataPath))))[..24];

    private NamedPipeServerStream CreatePipeServer() => new(pipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static string LockPath(string identity) => OperatingSystem.IsWindows()
        ? LegacyLockPath(identity) : UnixInstanceSocket.LockPath(identity);

    private static string LegacyLockPath(string identity) => Path.Combine(Path.GetTempPath(), identity + ".lock");

    public static bool HasActiveLease(string dataPath)
    {
        try
        {
            using var probe = new FileStream(LockPath(Identity(dataPath)), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            if (!OperatingSystem.IsWindows())
            {
                using var previousProbe = new FileStream(UnixInstanceSocket.PreviousLockPath(Identity(dataPath)),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                using var legacyProbe = new FileStream(LegacyLockPath(Identity(dataPath)), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    public static async Task<bool> RequestShutdownAsync(string dataPath, CancellationToken cancellationToken = default)
    {
        if (!HasActiveLease(dataPath))
            return true;
        await SendAsync(dataPath, exit: true, cancellationToken);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (!HasActiveLease(dataPath))
                return true;
            await Task.Delay(100, cancellationToken);
        }
        return !HasActiveLease(dataPath);
    }

    public static SingleInstanceService? TryAcquire(string dataPath)
    {
        var identity = Identity(dataPath);
        string lockPath;
        string? previousLockPath;
        try
        {
            lockPath = LockPath(identity);
            previousLockPath = OperatingSystem.IsWindows() ? null : UnixInstanceSocket.PreviousLockPath(identity);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new IOException("Нет доступа к каталогу блокировки приложения.", exception);
        }
        FileStream stream;
        FileStream? previous = null;
        FileStream? legacy = null;
        try
        {
            stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    previous = new FileStream(previousLockPath!, FileMode.OpenOrCreate,
                        FileAccess.ReadWrite, FileShare.None);
                    legacy = new FileStream(LegacyLockPath(identity), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch
                {
                    previous?.Dispose();
                    stream.Dispose();
                    throw;
                }
            }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException exception)
        {
            throw new IOException("Нет доступа к файлу блокировки приложения.", exception);
        }
        try { return new SingleInstanceService(stream, previous, legacy, identity); }
        catch (Exception exception)
        {
            legacy?.Dispose();
            previous?.Dispose();
            stream.Dispose();
            throw new IOException("Не удалось запустить канал управления приложением. Проверьте доступ к временным файлам.", exception);
        }
    }

    public static async Task<bool> SendAsync(string dataPath, bool exit, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(exit ? 15 : 5));
        try
        {
            if (!OperatingSystem.IsWindows())
                return await UnixInstanceSocket.SendAsync(Identity(dataPath), exit, timeout.Token);
            await using var client = new NamedPipeClientStream(".", Identity(dataPath), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(timeout.Token);
            await client.WriteAsync(new byte[] { exit ? (byte)2 : (byte)1 }, timeout.Token);
            var response = new byte[1];
            return await client.ReadAsync(response, timeout.Token) == 1 && response[0] == 1;
        }
        catch (IOException) { return false; }
        catch (SocketException) { return false; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    public static async Task<bool> ActivateExistingAsync(string dataPath, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(TimeSpan.FromMilliseconds(500));
            try
            {
                if (await SendAsync(dataPath, exit: false, attempt.Token))
                    return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            if (!HasActiveLease(dataPath))
                return false;
            await Task.Delay(100, cancellationToken);
        }
        return false;
    }

    private async Task ListenAsync()
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                if (!OperatingSystem.IsWindows() && unixSocket is not null)
                {
                    await unixSocket.ServeOnceAsync(HandleRequestAsync, cancellation.Token);
                    continue;
                }
                await using var server = CreatePipeServer();
                await server.WaitForConnectionAsync(cancellation.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var request = new byte[1];
                if (await server.ReadAsync(request, timeout.Token) != 1)
                    continue;
                var success = await HandleRequestAsync(request[0]);
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

    private async Task<bool> HandleRequestAsync(byte request)
    {
        var handler = request switch { 1 => ActivateRequested, 2 => ExitRequested, _ => null };
        return handler is not null && await handler();
    }

    public void Dispose()
    {
        cancellation.Cancel();
        try
        {
            if (!OperatingSystem.IsWindows())
                unixSocket?.Dispose();
        }
        finally
        {
            legacyLease?.Dispose();
            previousLease?.Dispose();
            lease.Dispose();
        }
        // The lock file stays in place to avoid unlink/recreate races between processes.
    }
}
