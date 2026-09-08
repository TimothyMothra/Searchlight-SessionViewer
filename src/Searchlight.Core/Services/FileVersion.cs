using Searchlight.Diagnostics;

namespace Searchlight.Services;

// ASSUMPTION: local writers update last-write time or length when content changes.
// Include WAL files separately: SQLite commits need not change the main database.
internal readonly record struct FileVersion(long Ticks, long Length)
{
    public static FileVersion Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new(info.LastWriteTimeUtc.Ticks, info.Length) : default;
        }
        catch (IOException ex)
        {
            CoreLog.Write($"File version unavailable: {ex.Message}");
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            CoreLog.Write($"File version unavailable: {ex.Message}");
            throw;
        }
    }

    public static FileVersion ReadDirectory(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists ? new(info.LastWriteTimeUtc.Ticks, 0) : default;
        }
        catch (IOException ex)
        {
            CoreLog.Write($"Directory version unavailable: {ex.Message}");
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            CoreLog.Write($"Directory version unavailable: {ex.Message}");
            throw;
        }
    }
}
