namespace Weave.Tools.Connectors;

internal static class FileSystemTextFile
{
    public static async Task<bool> IsBinaryFileAsync(string fullPath, CancellationToken ct)
    {
        var buffer = new byte[8192];
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
        var bytesRead = await stream.ReadAsync(buffer, ct);
        for (var index = 0; index < bytesRead; index++)
        {
            if (buffer[index] == 0x00)
                return true;
        }

        return false;
    }

    public static async Task<bool> CanReadAsTextAsync(string fullPath, CancellationToken ct)
    {
        try
        { return !await IsBinaryFileAsync(fullPath, ct); }
        catch
        { return false; }
    }
}
