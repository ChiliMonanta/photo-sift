# PhotoSift

Cross-platform desktop app (.NET 10, Avalonia) that finds exact duplicate photos in local folders and safely moves selected copies to the OS trash. See [SPEC.md](SPEC.md) for requirements.

## Run

Requires the .NET 10 SDK. From the repository root:

```sh
dotnet run --project src/PhotoSift.Desktop
```

(Plain `dotnet run` only works from inside `src/PhotoSift.Desktop`; the repository root contains the solution, not a runnable project.)

## Build and test

```sh
dotnet build PhotoSift.sln
dotnet test PhotoSift.sln
```

## Download

Prebuilt apps for Windows, macOS, and Linux are on the [Releases page](https://github.com/ChiliMonanta/photo-sift/releases). They are not code-signed yet; each release explains how to open the app the first time.

## Making a release

1. Make sure `main` is green and contains everything for the release.
2. Tag and push (use a suffix such as `-rc.1` for a pre-release):
   ```sh
   git tag v0.1.0
   git push origin v0.1.0
   ```
3. The [Release workflow](.github/workflows/release.yml) runs the tests, builds all platforms, and creates a **draft** release with the packages, checksums, and generated notes.
4. Review the draft on GitHub and click **Publish release**.

To try the packaging without releasing, run the Release workflow manually from the Actions tab; the packages are attached to the run as artifacts.

## Google Drive (in development)

Google Drive support needs a Google OAuth client. The credentials are not in this repository; to build with Drive support (for example in a fork), create your own client by following [docs/google-oauth-setup.md](docs/google-oauth-setup.md).
