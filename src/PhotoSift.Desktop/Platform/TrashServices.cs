using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PhotoSift.Core.Deletion;

namespace PhotoSift.Desktop.Platform;

public static class TrashServiceFactory
{
    public static ITrashService Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsRecycleBinService();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacTrashService();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxGioTrashService();
        }

        return new UnsupportedTrashService();
    }
}

/// <summary>
/// Moves files to the Windows Recycle Bin via SHFileOperation with FOF_ALLOWUNDO.
/// </summary>
/// <remarks>
/// Microsoft.VisualBasic's FileSystem.DeleteFile is not used: with dialogs suppressed it
/// silently deletes permanently when a file cannot be recycled (e.g. too large, or a drive
/// without a Recycle Bin). Here, only local fixed drives are allowed, and FOF_WANTNUKEWARNING
/// makes Windows warn before any permanent deletion instead of performing it silently.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsRecycleBinService : ITrashService
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_NO_CONNECTED_ELEMENTS = 0x2000;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    public Task MoveToTrashAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => MoveToRecycleBin(Path.GetFullPath(path)), cancellationToken);

    private static void MoveToRecycleBin(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal) ||
            new DriveInfo(root).DriveType != DriveType.Fixed)
        {
            throw new IOException("The Recycle Bin is only used for files on local fixed drives; the file was left in place.");
        }

        var flags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI |
                             FOF_NO_CONNECTED_ELEMENTS | FOF_WANTNUKEWARNING);
        var from = path + "\0\0";

        int result;
        bool aborted;
        if (Environment.Is64BitProcess)
        {
            var op = new ShFileOpStruct64 { wFunc = FO_DELETE, pFrom = from, fFlags = flags };
            result = SHFileOperation64(ref op);
            aborted = op.fAnyOperationsAborted;
        }
        else
        {
            var op = new ShFileOpStruct32 { wFunc = FO_DELETE, pFrom = from, fFlags = flags };
            result = SHFileOperation32(ref op);
            aborted = op.fAnyOperationsAborted;
        }

        if (aborted)
        {
            throw new IOException("The operation was canceled; the file was left in place.");
        }

        if (result != 0)
        {
            throw new IOException($"Windows could not move the file to the Recycle Bin (error 0x{result:X}).");
        }

        if (File.Exists(path))
        {
            throw new IOException("The file is still present after moving it to the Recycle Bin.");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct64
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    // On 32-bit Windows, SHFILEOPSTRUCT is declared with 1-byte packing.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    private struct ShFileOpStruct32
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

#pragma warning disable SYSLIB1054 // Struct with string fields requires runtime marshalling.
    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation64(ref ShFileOpStruct64 lpFileOp);

    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation32(ref ShFileOpStruct32 lpFileOp);
#pragma warning restore SYSLIB1054
}

/// <summary>Moves files to the macOS Trash through Finder (osascript).</summary>
public sealed class MacTrashService : ITrashService
{
    public Task MoveToTrashAsync(string path, CancellationToken cancellationToken = default) =>
        ProcessRunner.RunTrashCommandAsync(
            "osascript",
            ["-e", "on run argv", "-e", "tell application \"Finder\" to delete (POSIX file (item 1 of argv))", "-e", "end run", Path.GetFullPath(path)],
            path,
            cancellationToken);
}

/// <summary>Moves files to the freedesktop.org trash with <c>gio trash</c>.</summary>
public sealed class LinuxGioTrashService : ITrashService
{
    public Task MoveToTrashAsync(string path, CancellationToken cancellationToken = default) =>
        ProcessRunner.RunTrashCommandAsync("gio", ["trash", "--", Path.GetFullPath(path)], path, cancellationToken);
}

public sealed class UnsupportedTrashService : ITrashService
{
    public Task MoveToTrashAsync(string path, CancellationToken cancellationToken = default) =>
        throw new PlatformNotSupportedException("Moving files to the trash is not supported on this operating system.");
}

internal static class ProcessRunner
{
    public static async Task RunTrashCommandAsync(string fileName, IEnumerable<string> arguments, string path, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new IOException($"Could not start '{fileName}'.");
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var message = (await stderr.ConfigureAwait(false)).Trim();
            throw new IOException($"'{fileName}' failed with exit code {process.ExitCode}: {message}");
        }

        if (File.Exists(path))
        {
            throw new IOException("The file is still present after moving it to the trash.");
        }
    }
}
