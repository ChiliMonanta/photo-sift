# Test photos for manual testing

Small generated images (≈190 KB in total) that cover the MVP scenarios in [SPEC.md](../../SPEC.md). They contain no personal photos.

> **Copy this folder before testing deletion.** "Move to trash" really moves files to the OS trash. If you test it on this folder inside the repository, the files disappear from your working copy (restore them with `git checkout -- samples/test-photos`).

## How to test

1. Copy `samples/test-photos` somewhere outside the repository, e.g. to your desktop.
2. Start the app: `dotnet run --project src/PhotoSift.Desktop`
3. **Add folders…** → select the copied `test-photos` folder.
4. **Scan for duplicates**.
5. **Add master folders…** → select `test-photos/Pictures`.

## Expected result

Scan: **14 images**, **5 duplicate groups**, **0 errors**, scan complete. With `Pictures` as the master folder, **4 files** are marked for deletion:

| Group | Files | Marked with master = `Pictures` | What it tests |
| :--- | :--- | :--- | :--- |
| Beach (3 copies) | `Pictures/2024/Summer/beach.jpg`<br>`Downloads/IMG_4021.jpg`<br>`Phone backup/DCIM/PXL_20240712.jpg` | `IMG_4021.jpg`, `PXL_20240712.jpg` | Three or more copies; different names and folders |
| Sunset (3 copies) | `Pictures/2023/sunset.jpg`<br>`Pictures/2023/sunset-backup.jpg`<br>`Phone backup/DCIM/sunset (1).jpg` | `sunset (1).jpg` | Duplicates *inside* the master folder are left untouched |
| Mountain (2 copies) | `Pictures/2023/mountain.png`<br>`Phone backup/DCIM/mountain.png` | `Phone backup/DCIM/mountain.png` | PNG; same name in different folders |
| Forest (2 copies) | `Downloads/forest.jpg`<br>`Phone backup/DCIM/forest-copy.jpg` | nothing | Group without a master copy is not auto-selected |
| HEIC (2 copies) | `Downloads/IMG_0001.heic`<br>`Phone backup/DCIM/IMG_0001.heic` | nothing | Preview placeholder; detection still works |

Files that must **not** appear in any group:

| File | Why |
| :--- | :--- |
| `Pictures/2024/city.jpg` | Unique image |
| `Downloads/beach-edited.jpg` | Looks like the beach photo but is re-encoded (different bytes), so it is not an exact duplicate |
| `Downloads/beach-copy.txt` | Same bytes as `beach.jpg`, but not an image file, so it is not scanned |

The `.heic` files are not real HEIC images. They contain random bytes and exist only to test the "Preview is not available" placeholder.

## Things to try on your copy

- **Several master folders:** also add `Downloads` as a master folder. `IMG_4021.jpg` is unmarked (it is now in a master folder), and `Phone backup/DCIM/forest-copy.jpg` and `Phone backup/DCIM/IMG_0001.heic` become marked, so **5 files** are marked: `PXL_20240712.jpg`, `sunset (1).jpg`, `Phone backup/DCIM/mountain.png`, `forest-copy.jpg`, and `Phone backup/DCIM/IMG_0001.heic`. Select `Downloads` and click **Remove** to go back to the 4 files above.
- **Safe deletion:** review and confirm the 4 marked files; they should appear in the OS trash, and the report should say "4 moved to trash".
- **Changed file is skipped:** scan, then edit or replace `Downloads/IMG_4021.jpg` (e.g. copy `city.jpg` over it), then delete. It should be skipped with "contents have changed".
- **All copies selected:** mark every file in the Forest group and delete. Both should be skipped because one copy must always be kept.
- **Cancel:** start a scan and cancel it. Results are marked incomplete and deletion is disabled (easier to catch on a larger folder).
