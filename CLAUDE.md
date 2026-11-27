# Claude Code Instructions

## Language
- Write all code, comments, documentation, commit messages, and user-facing responses in English.

## Project requirements
- Read `SPEC.md` before implementing features. It is the source of truth for product requirements.
- Keep the first release focused on scanning selected local folders for exact image duplicates. Google Drive and event-based photo organization are post-MVP.
- Follow the .NET 10, Avalonia UI, and MVVM technology choices in `SPEC.md`.

## Safety and reliability
- Never permanently delete user files. Deletion must move files to the operating system's trash and require explicit user confirmation.
- Revalidate a file against its scanned hash before moving it to the trash. Skip changed, missing, or unreadable files and report why.
- Do not allow deletion from canceled or incomplete scans.
- Test file operations only with temporary/generated test images, never with a user's personal photo library.
- Surface errors; do not silently ignore failures or report success when an operation failed.

## Development workflow
- Keep changes focused and consistent with the existing architecture and conventions.
- Add or update focused tests for behavior changes.
- Run the relevant build and tests after code changes, and report the commands and results.
- Update `SPEC.md` when product requirements change.
