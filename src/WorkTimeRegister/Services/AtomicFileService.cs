namespace WorkTimeRegister.Services;

public sealed class AtomicFileService
{
    public async Task<bool> WriteAsync(string path, Func<Stream, Task<bool>> write)
    {
        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination)!;
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 81920, FileOptions.Asynchronous))
            {
                if (!await write(stream))
                    return false;
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: true);
            return true;
        }
        finally
        {
            // Cleanup must not hide the original save error.
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
