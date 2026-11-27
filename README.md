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
