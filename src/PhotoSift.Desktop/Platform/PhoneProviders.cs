using System.Runtime.CompilerServices;
using PhotoSift.Core.Import;
#if WINDOWS
using System.Runtime.Versioning;
using MediaDevices;
#endif

namespace PhotoSift.Desktop.Platform;

public static class PhoneProviderFactory
{
    public static IPhoneProvider Create()
    {
#if WINDOWS
        return new WindowsPhoneProvider();
#else
        return new UnsupportedPhoneProvider();
#endif
    }
}

public sealed class UnsupportedPhoneProvider : IPhoneProvider
{
    public string? UnavailableReason =>
        "Importing directly from phones is only available on Windows for now. You can import from a folder instead.";

    public Task<IReadOnlyList<PhoneInfo>> GetPhonesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PhoneInfo>>([]);

    public Task<IImportSource> OpenAsync(PhoneInfo phone, CancellationToken cancellationToken) =>
        throw new PlatformNotSupportedException(UnavailableReason);
}

#if WINDOWS
/// <summary>
/// Android phones (in "File transfer" mode) and iPhones (unlocked, "Trust this computer") through
/// Windows Portable Devices. Access is read-only.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPhoneProvider : IPhoneProvider
{
    public string? UnavailableReason => null;

    public Task<IReadOnlyList<PhoneInfo>> GetPhonesAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var phones = new List<PhoneInfo>();
        foreach (var device in (MediaDeviceManager.Instance.GetDevices() ?? []))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (device)
            {
                try
                {
                    device.Connect(MediaDeviceAccess.GenericRead, MediaDeviceShare.Read, false);
                    var name = FirstNonEmpty(device.FriendlyName, device.Model, device.Description) ?? "Unknown device";
                    phones.Add(new PhoneInfo(device.DeviceId, name, FirstNonEmpty($"{device.Manufacturer} {device.Model}".Trim(), device.Description) ?? string.Empty));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    phones.Add(new PhoneInfo(device.DeviceId, FirstNonEmpty(device.FriendlyName, device.Description) ?? "Unknown device",
                        "Could not be opened. Unlock the phone and allow access to its files."));
                }
                finally
                {
                    if (device.IsConnected)
                    {
                        device.Disconnect();
                    }
                }
            }
        }

        return (IReadOnlyList<PhoneInfo>)phones;
    }, cancellationToken);

    public Task<IImportSource> OpenAsync(PhoneInfo phone, CancellationToken cancellationToken) => Task.Run<IImportSource>(() =>
    {
        var device = (MediaDeviceManager.Instance.GetDevices() ?? []).FirstOrDefault(d => d.DeviceId == phone.DeviceId)
                     ?? throw new IOException($"{phone.Name} is no longer connected.");
        try
        {
            device.Connect(MediaDeviceAccess.GenericRead, MediaDeviceShare.Read, false);
            return new WindowsPhoneSource(device, phone.Name);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }, cancellationToken);

    internal static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

/// <summary>
/// The camera roll of one connected phone: the DCIM, Pictures and Movies folders of each storage
/// (e.g. "Internal shared storage", "SD card", or an iPhone's "Internal Storage").
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsPhoneSource(MediaDevice device, string name) : IImportSource
{
    private static readonly string[] MediaFolders = ["DCIM", "Pictures", "Movies"];

    // Manufacturer, model and serial number identify the phone across connections and USB ports.
    public string Id { get; } = "phone:" + string.Join("|",
        device.Manufacturer, device.Model, WindowsPhoneProvider.FirstNonEmpty(device.SerialNumber, device.DeviceId));

    public string DisplayName { get; } = name;

    public async IAsyncEnumerable<ImportItem> EnumerateAsync(IProgress<int>? found, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Device calls block, so the listing runs on a worker thread and is handed over in one piece.
        var items = await Task.Run(() => List(found, cancellationToken), cancellationToken).ConfigureAwait(false);
        foreach (var item in items)
        {
            yield return item;
        }
    }

    public async Task<Stream> OpenReadAsync(ImportItem item, CancellationToken cancellationToken)
    {
        var file = await Task.Run(() => device.GetFileInfo(item.Key), cancellationToken).ConfigureAwait(false);
        return await file.OpenReadAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        try
        {
            if (device.IsConnected)
            {
                device.Disconnect();
            }
        }
        finally
        {
            device.Dispose();
        }
    }

    private List<ImportItem> List(IProgress<int>? found, CancellationToken cancellationToken)
    {
        var items = new List<ImportItem>();
        var root = device.GetRootDirectory();
        foreach (var storage in root.EnumerateDirectories())
        {
            foreach (var folder in storage.EnumerateDirectories()
                         .Where(d => MediaFolders.Contains(d.Name, StringComparer.OrdinalIgnoreCase)))
            {
                var pending = new Stack<MediaDirectoryInfo>([folder]);
                while (pending.TryPop(out var directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var entry in directory.EnumerateFileSystemInfos())
                    {
                        if (entry is MediaDirectoryInfo subdirectory)
                        {
                            pending.Push(subdirectory);
                        }
                        else if (entry is MediaFileInfo file && MediaFormats.IsSupported(file.Name))
                        {
                            items.Add(new ImportItem(
                                file.FullName,
                                file.FullName.TrimStart('\\').Replace('\\', '/'),
                                (long)file.Length,
                                ToUtc(file.LastWriteTime ?? file.DateAuthored ?? file.CreationTime)));
                            found?.Report(items.Count);
                        }
                    }
                }
            }
        }

        return items;
    }

    private static DateTime? ToUtc(DateTime? time) => time switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } utc => utc,
        { } local => local.ToUniversalTime(),
    };
}
#endif
