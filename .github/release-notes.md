## Download

| Platform | File |
|---|---|
| Windows (x64) | `PhotoSift-<version>-win-x64.zip` |
| macOS, Apple Silicon (M1 and later) | `PhotoSift-<version>-osx-arm64.zip` |
| macOS, Intel | `PhotoSift-<version>-osx-x64.zip` |
| Linux (x64) | `PhotoSift-<version>-linux-x64.tar.gz` |

No .NET installation is needed. `SHA256SUMS.txt` lists checksums for every file.

## Installing

These builds are **not code-signed**, so your operating system will warn you the first time you open the app.

- **Windows:** Unzip and run `PhotoSift.exe`. If SmartScreen shows "Windows protected your PC", choose **More info → Run anyway**.
- **macOS:** Unzip and move `PhotoSift.app` to Applications. The first time, macOS blocks the app: open **System Settings → Privacy & Security** and click **Open Anyway** next to PhotoSift. (Alternatively run `xattr -dr com.apple.quarantine /Applications/PhotoSift.app` in Terminal.)
- **Linux:** Extract with `tar -xzf PhotoSift-<version>-linux-x64.tar.gz` and run `./PhotoSift`. Moving files to the trash requires `gio` (part of GLib, installed on most desktops).

Deleted duplicates are always moved to the trash or Recycle Bin, never removed permanently.
