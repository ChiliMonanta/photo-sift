namespace PhotoSift.Core;

/// <summary>Small remembered settings, such as the last chosen folders.</summary>
public interface ISettingsStore
{
    string? GetSetting(string key);

    void SetSetting(string key, string value);
}
