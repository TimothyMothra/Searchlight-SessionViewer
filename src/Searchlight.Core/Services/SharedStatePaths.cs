using System.Diagnostics;
using System.Text;

namespace Searchlight.Services;

/// <summary>Channel-independent storage outside MSIX-virtualized AppData.</summary>
public sealed class SharedStatePaths
{
    public static SharedStatePaths Default { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".searchlight"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Searchlight"));

    public string RootDirectory { get; }
    public string SettingsFile => Path.Combine(RootDirectory, "settings.json");
    public string NotesDirectory => Path.Combine(RootDirectory, "notes");
    private readonly string _legacyDirectory;

    public SharedStatePaths(string rootDirectory, string legacyDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        _legacyDirectory = Path.GetFullPath(legacyDirectory);
    }

    /// <summary>Copies the legacy snapshot once; never moves or overwrites user files.</summary>
    public void MigrateLegacy()
    {
        using var gate = SharedStateIO.Lock(RootDirectory);
        string marker = Path.Combine(RootDirectory, ".legacy-copy-v1");
        if (File.Exists(marker)) return;

        // ASSUMPTION: legacy installations are no longer editing during migration.
        // A completion marker prevents a deliberately deleted shared note from
        // being resurrected on restart. Interrupted copies can safely be retried.
        CopyIfAbsent(Path.Combine(_legacyDirectory, "settings.json"), SettingsFile);
        string legacyNotes = Path.Combine(_legacyDirectory, "notes");
        using (SharedStateIO.Lock(NotesDirectory))
        {
            if (SharedStateIO.DirectoryExists(legacyNotes))
                foreach (string source in Directory.EnumerateFiles(legacyNotes, "*.md"))
                    CopyIfAbsent(source, Path.Combine(NotesDirectory, Path.GetFileName(source)));
        }
        SharedStateIO.WriteAtomic(marker, "Legacy settings and notes copied; originals retained.", overwrite: false);
    }

    private static void CopyIfAbsent(string source, string destination)
    {
        if (File.Exists(destination)) return;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(source); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        SharedStateIO.WriteAtomic(destination, bytes, overwrite: false);
    }
}

/// <summary>Cooperating processes lock a stable sidecar, not the replaceable data file.</summary>
internal static class SharedStateIO
{
    internal static FileStream Lock(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, ".state.lock");
        var wait = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (wait.Elapsed < TimeSpan.FromSeconds(3))
            {
                Thread.Sleep(25);
            }
        }
    }

    internal static string? ReadOptional(string path)
    {
        try
        {
            // Shared files are UTF-8, optionally BOM-prefixed. Never repair bad
            // bytes with replacement characters and then persist the damaged text.
            byte[] bytes = File.ReadAllBytes(path);
            int offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
            return new UTF8Encoding(false, true).GetString(bytes.AsSpan(offset));
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal static bool DirectoryExists(string path)
    {
        try
        {
            if (!File.GetAttributes(path).HasFlag(FileAttributes.Directory))
                throw new IOException($"Expected a directory at {path}.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static void WriteAtomic(string path, string text, bool overwrite = true) =>
        WriteAtomic(path, new UTF8Encoding(false, true).GetBytes(text), overwrite);

    internal static void WriteAtomic(string path, byte[] bytes, bool overwrite = true)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string pending = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.pending");
        try
        {
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // ASSUMPTION: local filesystems provide atomic same-directory rename.
            File.Move(pending, path, overwrite);
        }
        finally
        {
            if (File.Exists(pending)) File.Delete(pending);
        }
    }
}
