# PhotoSift – Project Specification

## 1. Project Overview
**Project name:** PhotoSift  
**Purpose:** A cross-platform desktop application that identifies and manages exact photo duplicates in selected local folders. Google Drive support is planned for a later release.  
**Target platforms:** Windows, macOS, and Linux.

---

## 2. Core Features (MVP)

### A. Data Sources
* **Local file management:** Users can select one or more local root folders to scan, including subfolders.
* **File formats:** Scan image files only: JPEG, PNG, WebP, HEIC/HEIF, and RAW formats (e.g., CR2, NEF, ARW). Non-image files are excluded.

### B. Duplicate Detection (Scanning)
The app scans the selected local folders, including subfolders, and groups exact duplicates using MD5 hashes. Files with identical contents are considered duplicates regardless of filename or folder. A group can contain two or more copies; re-encoded or otherwise modified images are not considered exact duplicates.

### C. Review and Management
* **Duplicate group review:** Show every file in a group, including groups with more than two copies. Show filenames and paths so copies can be identified even when they are in different folders or have different names.
* **Thumbnails:** Show one thumbnail per duplicate group for visual confirmation. Load it only when the group is displayed, reuse it for the group's files, and limit concurrent requests to keep the interface responsive. If a thumbnail cannot be generated, show a placeholder; this must not prevent duplicate detection.
* **Choose files to delete:** Users can manually mark one or more files in each group for deletion. At least one copy in each group must stay unmarked: the last unmarked copy cannot be marked and is labeled as kept.
* **Master folders:** Users can select one or more local folders as masters. In groups with a copy in any master folder, keep the copies in the master folders and automatically mark all copies outside every master folder for deletion, regardless of filename. Users do not need to select each file to delete. If a group has no copy in a master folder, nothing is selected automatically. Copies within master folders, including duplicates spread across several master folders, are left untouched by this rule. Adding, removing, or clearing master folders reapplies the rule: marks the rule set earlier that no longer apply are removed, while marks the user set manually are never changed. Proposed deletions can be reviewed and must be confirmed before execution.
* **Safe deletion:**
  * Move local files to the operating system's trash; never permanently delete them.
  * Show the files and paths selected for deletion and require explicit confirmation.
  * Before moving a file to the trash, verify that it still exists and has the same hash as during the scan. If it has changed or cannot be verified, skip it and report the reason.
  * At least one copy in every group is always kept: if every copy in a group is selected, or none of the kept copies still matches the scanned hash, the group's selected files are skipped and reported.
  * Symbolic links and junctions are not followed or scanned (they are reported as notes), so a file cannot appear as a duplicate of itself. Overlapping selected folders are scanned only once.
  * On Windows, files are moved to the Recycle Bin only on local fixed drives; files on network or removable drives are skipped and reported, because Windows may delete them permanently.

---

## 3. Technology Stack (.NET Ecosystem)

| Component | Choice | Rationale |
| :--- | :--- | :--- |
| **Target framework** | **.NET 10 (net10.0)** | Cross-platform with high performance. |
| **UI framework** | **Avalonia UI** + `CommunityToolkit.Mvvm` | A truly cross-platform GUI for Linux, macOS, and Windows. |
| **Local database** | `Microsoft.Data.Sqlite` or **LiteDB** | Caches the file index and MD5 hashes between scans. |
| **OS trash** | Platform-specific calls | `SHFileOperation` with `FOF_ALLOWUNDO` and `FOF_WANTNUKEWARNING` (Windows), `gio trash` (Linux), `osascript` (macOS). `Microsoft.VisualBasic.FileIO` is not used because it silently deletes permanently when a file cannot be recycled. |

---

## 4. Project Structure (Clean Architecture)

```text
PhotoSift/
├── .devcontainer/
│   ├── devcontainer.json
│   └── Dockerfile
├── src/
│   ├── PhotoSift.Core/          # Business logic, hashing, SQLite
│   ├── PhotoSift.Desktop/       # Avalonia UI (views, view models, platform-specific code)
│   └── PhotoSift.Tests/         # xUnit tests for hashing and comparison logic
├── PhotoSift.sln
└── SPEC.md
```

---

## 5. Development Environment: VS Code Dev Container

### `.devcontainer/Dockerfile`
```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0

# Install dependencies required to run Avalonia UI and X11 on Linux
RUN apt-get update && export DEBIAN_FRONTEND=noninteractive \
    && apt-get -y install --no-install-recommends \
    libx11-dev \
    libice-dev \
    libsm-dev \
    libfontconfig1-dev \
    libfreetype6-dev \
    libgl1-mesa-dev \
    x11-apps \
    && apt-get clean -y && rm -rf /var/lib/apt/lists/*

# Install Avalonia templates for dotnet new
RUN dotnet new install Avalonia.Templates
```

### `.devcontainer/devcontainer.json`
```json
{
  "name": "PhotoSift .NET & Avalonia",
  "build": {
    "dockerfile": "Dockerfile"
  },
  "runArgs": [
    "-e", "DISPLAY=${localEnv:DISPLAY}",
    "-v", "/tmp/.X11-unix:/tmp/.X11-unix"
  ],
  "customizations": {
    "vscode": {
      "extensions": [
        "ms-dotnettools.csdevkit",
        "AvaloniaTeam.vscode-avalonia",
        "qwtel.sqlite-viewer"
      ]
    }
  },
  "postCreateCommand": "dotnet restore"
}
```

---

## 6. Key Technical Flows

### Asynchronous Scanning (`System.Threading.Channels`)
1. **Producer:** Enumerates supported image files in the selected local folders and their subfolders.
2. **Consumer (worker pool):** Computes hashes for local files in parallel and saves the results to SQLite.

---

## 7. Non-Functional Requirements
* **Performance:** Asynchronous/multithreaded scanning so the UI never freezes, with a clear progress indicator. Users can cancel a scan.
* **Privacy:** Hashing is performed locally on the user's computer. No image data is sent to third-party servers.
* **Error handling:** Report unreadable, inaccessible, or unsupported files with their paths and reasons, and continue scanning other files. If a scan is canceled or any file cannot be scanned, mark the results incomplete; users can review them, but deletion is disabled until a complete scan has finished.
* **Distribution:** Releases are published as GitHub Releases with self-contained, single-file builds (no .NET installation required) for Windows x64 (`.zip`), macOS Apple Silicon and Intel (`PhotoSift.app` in a `.zip`), and Linux x64 (`.tar.gz`), plus a `SHA256SUMS.txt`. Pushing a `vX.Y.Z` tag (or `vX.Y.Z-suffix` for a pre-release) runs the tests on all three platforms and creates a draft release that a maintainer publishes manually. Builds are not code-signed for 0.x releases; the release notes explain how to open the app despite OS warnings. The app shows its version in the window title.

---

## 8. Google Drive (Post-MVP, Planned)

Anyone who downloads PhotoSift can connect **their own Google Drive** as an alternative or addition to local folders. Delivered in stages:

1. **File sources (done):** Core scans, verifies, and trashes files through file sources (`IFileSource`). The local file system is the first source; every file has a source id, a unique location (an absolute path, or a URI such as `gdrive://<account>/<fileId>` for other sources), and a display path. Files from different sources are compared with each other.
2. **Read-only Drive (first Drive release; implemented on Windows):**
   * **Scope:** only the user's own **My Drive**. Shared drives and files shared with the user are not scanned.
   * **Access:** OAuth 2.0. New sign-ins ask for the `drive` scope, because uploading (section 10) adds files to existing folders; scanning stays read-only. Sign-ins made with `drive.readonly` (before uploading existed) still scan; the user clicks **Allow uploads…** to connect again with the broader scope. Scanning never downloads photo contents from Drive, only metadata and small thumbnails. Sign-ins made with the old `drive.metadata.readonly` scope are not used; the user is asked to connect again, and the old access is revoked.
   * **Scanning:** Drive folders are chosen in an in-app Drive folder browser and can be combined with local folders. Hashes come from Drive's `md5Checksum` metadata, so no files are downloaded; local and Drive copies with the same MD5 are grouped together. Files without a checksum and shortcuts are skipped and reported as notes; folders that cannot be read are errors that make the scan incomplete. Folders outside My Drive are errors. Drive folders can be master folders. Drive files have locations `gdrive://<permissionId>/file/<fileId>` and folders `gdrive://<permissionId>/folder/<folderId>`, where `permissionId` identifies the Google account; display paths look like `My Drive/Photos/IMG_1.jpg`.
   * **Deletion:** Drive files cannot be moved to the trash in this release; they can be kept copies when deleting local duplicates. Selecting a Drive file for deletion is prevented, and any that reach deletion are skipped with the reason.
   * **Thumbnails:** a local copy the app can decode is used first, so Drive is not contacted. Otherwise the group shows the small thumbnail Drive generates (which also covers HEIC and RAW): the link comes with the scan's file listing (no extra requests), the image is requested at display size (about 192 px), at most two downloads run at once, and downloaded thumbnails are cached in memory by content hash for the session. An expired link is replaced once per file. The access token is only sent to Google hosts over HTTPS. If Google refuses thumbnails for several files in a row (e.g. because of the scope), no more are requested and placeholders are shown.
3. **Upload (implemented on Windows):** see section 10.
4. **Drive trash (later release):** moving Drive files to the **Drive trash** (`files.update` with `trashed: true`, never `files.delete`), after revalidating each file's metadata and `md5Checksum` against the scan. Files the user cannot trash are skipped and reported. Uses the `drive` scope that uploading already needs.

**Sign-in and credentials:**
* OAuth for installed apps: system browser (never an embedded web view), loopback redirect on `127.0.0.1` with a random port, PKCE with `S256`, and a random `state` that the redirect must match (other requests to the port are answered and ignored). The app checks that the Drive scope was granted.
* The OAuth client ID and client secret are embedded in released builds. For desktop apps they are not confidential (RFC 8252 "public client"); security comes from the user's consent and PKCE. They are kept out of the repository: tag (release) builds receive them from GitHub Actions secrets, while manual and pull request builds are built without them; developers use `dotnet user-secrets` (Debug builds only). The environment variables `PHOTOSIFT_GOOGLE_CLIENT_ID` and `PHOTOSIFT_GOOGLE_CLIENT_SECRET` override the built-in client, so users can supply their own (an in-app setting may come later). Without a client, Drive cannot be connected and the app says why. Setting up a client is documented in [docs/google-oauth-setup.md](docs/google-oauth-setup.md).
* The refresh token is stored only in the OS's secure storage, never in a plain file; access tokens are kept in memory only. Windows uses DPAPI (current user). macOS Keychain and Linux libsecret are planned; until then, Drive cannot be connected on those platforms, and the app says so rather than falling back to an unprotected file.
* One Google account is connected at a time. **Disconnect** deletes the saved sign-in and revokes PhotoSift's access at Google; if revoking fails, the local sign-in is still deleted and the user is told how to remove access in their Google account. An expired or revoked sign-in is reported, and the user connects again.
* The Drive connection is shown at the top of the **Find duplicates** and **Upload to Google Drive** tabs. Connecting and disconnecting are disabled while a scan, deletion, or upload runs.
* Google requires verification for public apps that use restricted Drive scopes (privacy policy and home page on a verified domain, a demo video, and a scope justification). Until verified, the app runs in Testing mode with listed test users.

**Safety:** if a scanned local folder is inside a Google Drive for desktop folder (e.g. `G:\My Drive`, `~/Library/CloudStorage/GoogleDrive-*`) and Drive is also scanned through the API, the app warns that the same file may appear twice and that deleting the local copy also deletes it from Drive.

**Not covered:** Google Photos (a separate service, not visible through the Drive API).

---

## 9. Import from Phone (Post-MVP)

Copies photos and videos from a phone to the computer, in its own **"Import from phone"** tab. The tabs follow the usual workflow: **Import from phone**, **Find duplicates**, **Sort photos**, **Upload to Google Drive**.

* **Sources:** Android phones (unlocked, USB mode "File transfer") and iPhones (unlocked, "Trust this computer") through Windows Portable Devices; on other platforms phones are not available yet. Any folder can also be imported from (e.g. a memory card, or a phone the OS mounts as a folder). From phones, the `DCIM`, `Pictures`, and `Movies` folders of each storage are imported.
* **Files:** the image formats PhotoSift scans, plus common video formats (MP4, MOV, 3GP, MKV, and others). Other files are ignored.
* **Copy only:** nothing on the phone is ever changed or deleted.
* **Destination:** a folder the user chooses (remembered; default: the user's Pictures folder). The phone's folder structure is kept below a folder named after the phone, e.g. `Pictures\Pixel 8\Internal shared storage\DCIM\Camera\IMG_1.jpg`. Copies get the source file's modification date. Names are made valid for the file system, and paths from the phone can never point outside the destination.
* **Check before copying:** the app lists the files and shows how many are new (and their size) and how many were imported before. It refuses to start if the destination drive does not have room for the new files plus a margin, or if the destination is inside the folder being imported from.
* **Smart copying:**
  * Files imported before (same path on the source and same size) are skipped without reading them again, also when their copies were moved or deleted since, so organizing photos does not bring them back.
  * A different file with the same name at the destination is kept, and the new copy gets a numbered name (`IMG_1 (2).jpg`). An identical file already there (same MD5) is not copied twice.
  * Imported files are added to the hash cache, so a duplicate scan of them does not hash them again.
* **Safe and resumable:**
  * An import journal (SQLite, in the user's local app data) records each file before its copy starts and when it is done.
  * Each file is written to a `<name>.photosift-partial` file first, flushed to disk, and checked: the size must match the source, and the data on disk must have the same MD5 as the data read. Only then is it renamed to its real name. A broken or incomplete file therefore never gets a real name.
  * If an import is canceled, the app closes or crashes, the computer loses power, or the phone is disconnected, the next check finds the file whose copy was cut off, tells the user, removes its partial file, and copies it again. Clicking **Start copy** again continues with the files that are left.
  * A file that fails (e.g. it arrives shorter than reported) is discarded, reported with the reason, and tried again next time. After three failures in a row, or when the destination drive is full, the import stops and says why.
* **Progress:** files left out of the total, bytes copied, current file, speed, and estimated time left, with a Cancel button. Afterwards: how many files were copied, already on the computer, and failed, with the reason for each failure.
* **iPhone:** with "Transfer to Mac or PC: Automatic", iOS converts photos while copying, so their size differs from what the phone reports and they fail the check. The app tells users to choose "Keep Originals".

---

## 10. Upload to Google Drive (Post-MVP)

Uploads photos and videos from a folder on the computer to a Google Drive folder, in the **"Upload to Google Drive"** tab. The intended workflow is: import from the phone, remove duplicates and unwanted photos, then upload what is left. Files land on the computer first because cleaning up is easier there, the local copy is a backup, and a phone import and an upload never fail together.

* **Folders:** the user chooses a local folder and a Drive folder in My Drive (with the Drive folder browser); both are remembered. The files keep their local folder structure below a folder named after the local folder, e.g. `My Drive/Photos/Pixel 8/DCIM/Camera/IMG_1.jpg`. Folders are created as needed; an existing folder with the same name is reused.
* **Files:** the same photo and video formats as phone import. Empty files are reported and not uploaded.
* **Add only:** existing Drive files are never changed or deleted, and local files are only read. The only exception is PhotoSift's own upload that fails verification (below), which goes to the Drive trash, where it can be restored.
* **Check before uploading:** the app reads the Drive folder (with all subfolders) and the local folder, and shows how many files are new (and their size), how many are already in Google Drive, and how many were uploaded before.
* **Smart uploading:**
  * Files whose contents (MD5) are already anywhere in the Drive folder are skipped, wherever they are and whatever their name, so photos already uploaded another way are not uploaded twice.
  * Files uploaded before from this folder to this Drive folder, and unchanged since (same size and modification date), are skipped without hashing them again.
  * Identical local files are uploaded once.
  * A different file with the same name in the Drive folder is kept, and the upload gets a numbered name (`IMG_1 (2).jpg`).
  * Local hashes use the hash cache.
* **Safe and resumable:**
  * Files are sent with Google's resumable uploads, in chunks of 4 MiB. A file appears in Drive only after all its bytes arrived, so an interrupted upload never leaves a broken file in Drive.
  * An upload journal (SQLite, in the user's local app data) records each upload session before the first byte is sent, and each finished file. If an upload is canceled, the app closes, or the network drops, the next run asks Google how much of the file arrived and continues from there. An expired session (after about a week) starts the file again.
  * After a network or server error, the app waits (1, 2, 4… seconds), asks Google how much arrived, and continues; after five retries the file fails and is tried again next time. After three failed files in a row the upload stops and asks the user to check the connection. A full Google storage stops the upload with that reason.
  * Each uploaded file is verified: Drive's MD5 must match the local file's MD5 from the check. A local file that changed after the check is not uploaded. If Drive's copy does not match, it is moved to the Drive trash and reported, and the file is tried again next time.
  * Uploaded files get the local file's modification date in Drive.
* **Progress:** files left out of the total, bytes uploaded, current file, speed, and estimated time left, with a Cancel button. Afterwards: how many files were uploaded and failed, with the reason for each failure.
* **Security:** the access token is only sent to `https://www.googleapis.com/upload/drive/...` session addresses; any other session address from the server is refused.
* **Not covered:** Google Photos (its API no longer lets apps see a user's whole library, so duplicates could not be checked), shared drives, and deleting from Drive.

---

## 11. Sort Photos (Post-MVP)

Sorting photos into folders, in the **"Sort photos"** tab (after **Find duplicates**). The first version is manual; automatic sorting (e.g. by date, place, or event) builds on it later.

* **Photo folder:** the user chooses a local folder (remembered). The tab shows its folders as a tree, with the number of photos and videos directly in each folder. Hidden and system folders and symbolic links are skipped. **Refresh** reads the folders again.
* **Photos:** selecting a folder shows its photos and videos (the same formats as phone import) as a grid of previews. Previews load only for the photos shown, from local files; formats the app cannot decode, and videos, show a placeholder with the file name. Photos are selected by clicking them; **Select all** and **Select none** help.
* **New folder:** a subfolder of the selected folder, with a name that is valid on every OS (no `< > : " / \ | ? *`, no leading or trailing space or trailing period, no reserved Windows names). An existing folder is never replaced. The new folder is chosen as the move target.
* **Move:** the selected photos are moved to the folder chosen under **Move selected to**.
  * Files are only renamed (moved within the same drive), never copied and deleted, so a move cannot lose data. Moves to another drive are refused.
  * Nothing is overwritten: a file with a taken name gets a numbered name (`IMG_1 (2).jpg`).
  * Missing files, files already in the target folder, and files that cannot be moved are skipped and listed under Problems with the reason.
  * Cached hashes follow the moved files, so the next duplicate scan does not hash them again.
* **Undo last move:** moves the photos of the last move back where they were, unless another file has taken that place (then it is reported and nothing is replaced).
* **Not yet:** drag and drop, renaming and deleting folders, sorting Google Drive folders, and moving sidecar files (e.g. `.xmp`, `.aae`) with their photos.

---

## 12. Future Features (Post-MVP)
* Scheduled background scanning.
* EXIF handling (the ability to copy metadata from a duplicate before deleting it).
* Support for additional cloud services (OneDrive, Dropbox).
* **Event-based photo organization:** The user enters a folder name (e.g., "Vacation" or "Party") and selects a reference photo. The app suggests related photos, which the user can review and select; the user can also add photos manually. After confirmation, the app creates the named folder and moves the selected photos into it. This feature supports both local folders and Google Drive.

---

## 13. MVP Acceptance Criteria
* Only supported image files in the selected local folders and subfolders are scanned; non-image files are excluded.
* Files with identical contents are grouped together regardless of filename or folder. Groups contain all copies, including groups with three or more files; modified or re-encoded images are not grouped as exact duplicates.
* A master-folder selection keeps copies in any of the selected master folders and marks matching copies outside all of them for deletion. If no copy exists in a master folder, no file in that group is selected automatically.
* A user can review the selected files and paths, and no file is moved to the trash without explicit confirmation.
* Before deletion, each selected file is revalidated against its scanned hash. Changed, missing, or unreadable files are not moved to the trash and are reported.
* A scan can be canceled. Canceled scans and scans with files that could not be read are labeled incomplete; their results can be reviewed, but cannot be used to delete files.
* Unreadable or inaccessible files are reported with their paths and reasons, without stopping the scan of other files.
* If a thumbnail cannot be generated for an image, a placeholder is shown and duplicate detection still works.
