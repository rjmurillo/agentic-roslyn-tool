using System;
using System.IO;

namespace AgenticRoslynTool.Tests;

/// <summary>
/// Makes a file exist but refuse to be read, so a test can exercise the read-failure path.
/// </summary>
/// <remarks>
/// The mechanism has to differ by platform. Windows enforces <see cref="FileShare.None"/>, so
/// holding the handle is enough. Unix file locks are advisory and ignored by a plain read, so
/// there the permission bits are the only lever. Either can legitimately fail to apply: a Unix
/// process running as root reads a mode-000 file regardless. <see cref="Applied"/> reports that
/// so a test can opt out rather than assert against a condition it never created.
/// </remarks>
internal sealed class ReadBlock : IDisposable
{
    private readonly string _path;
    private readonly FileStream? _handle;
    private readonly UnixFileMode _originalMode;

    private ReadBlock(string path, FileStream? handle, UnixFileMode originalMode, bool applied)
    {
        _path = path;
        _handle = handle;
        _originalMode = originalMode;
        Applied = applied;
    }

    /// <summary>True when the file is genuinely unreadable now.</summary>
    public bool Applied { get; }

    public static ReadBlock Apply(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return new ReadBlock(path, handle, default, applied: true);
        }

        var original = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        return new ReadBlock(path, null, original, applied: !CanRead(path));
    }

    private static bool CanRead(string path)
    {
        try
        {
            using var probe = File.OpenRead(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _handle?.Dispose();
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_path, _originalMode);
        }
    }
}
