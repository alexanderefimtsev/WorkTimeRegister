using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace WorkTimeRegister.Services;

[UnsupportedOSPlatform("windows")]
internal sealed class UnixInstanceSocket : IDisposable
{
    private readonly Socket listener;
    private readonly string path;

    public UnixInstanceSocket(string identity)
    {
        path = SocketPath(identity);
        EnsureDirectory();
        File.Delete(path);
        listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
        }
        catch
        {
            listener.Dispose();
            throw;
        }
    }

    private static string SocketDirectory()
    {
        var user = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))))[..12];
        return Path.Combine("/tmp", "wtr-" + user);
    }

    internal static string LockPath(string identity)
    {
        EnsureDirectory();
        return Path.Combine(SocketDirectory(), "lease-" + identity[^24..]);
    }

    private static void EnsureDirectory()
    {
        var directory = SocketDirectory();
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var info = new DirectoryInfo(directory);
        var mode = File.GetUnixFileMode(directory);
        if (info.LinkTarget is not null ||
            (mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                     UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0 ||
            (mode & (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)) !=
            (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute))
            throw new IOException("Каталог канала связи имеет небезопасные права доступа.");
    }

    private static string SocketPath(string identity) => Path.Combine(SocketDirectory(), "ipc-" + identity[^24..]);

    public static async Task<bool> SendAsync(string identity, bool exit, CancellationToken token)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath(identity)), token);
        await socket.SendAsync(new byte[] { exit ? (byte)2 : (byte)1 }, SocketFlags.None, token);
        var answer = new byte[1];
        return await socket.ReceiveAsync(answer, SocketFlags.None, token) == 1 && answer[0] == 1;
    }

    public async Task ServeOnceAsync(Func<byte, Task<bool>> handle, CancellationToken token)
    {
        using var client = await listener.AcceptAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var request = new byte[1];
        if (await client.ReceiveAsync(request, SocketFlags.None, timeout.Token) != 1)
            return;
        var success = await handle(request[0]);
        await client.SendAsync(new byte[] { success ? (byte)1 : (byte)0 }, SocketFlags.None, timeout.Token);
    }

    public void Dispose()
    {
        listener.Dispose();
        File.Delete(path);
    }
}
