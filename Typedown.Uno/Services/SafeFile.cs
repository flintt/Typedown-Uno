using System.Text;

namespace Typedown.Uno.Services;

/// <summary>Atomic text write: temp file in the same directory, flushed, then replaced over the target (ported from Typedown).</summary>
public static class SafeFile
{
    public static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static async Task WriteAllTextAtomicAsync(string path, string text, Encoding? encoding = null)
    {
        encoding ??= Utf8NoBom;
        var directory = Path.GetDirectoryName(path);
        var tempPath = Path.Combine(string.IsNullOrEmpty(directory) ? "." : directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                var bytes = encoding.GetBytes(text);
                await stream.WriteAsync(bytes);
                stream.Flush(flushToDisk: true);
            }
            try
            {
                if (File.Exists(path))
                    File.Replace(tempPath, path, null, ignoreMetadataErrors: true);
                else
                    File.Move(tempPath, path);
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
            {
                File.Copy(tempPath, path, overwrite: true);
            }
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    /// <summary>Stable 64-bit content hash (FNV-1a over UTF-16 code units), used for saved/changed comparisons.</summary>
    public static ulong Hash(string? text)
    {
        const ulong offset = 14695981039346656037UL, prime = 1099511628211UL;
        var hash = offset;
        if (text == null) return hash;
        foreach (var c in text)
        {
            hash ^= (byte)c; hash *= prime;
            hash ^= (byte)(c >> 8); hash *= prime;
        }
        return hash;
    }
}
