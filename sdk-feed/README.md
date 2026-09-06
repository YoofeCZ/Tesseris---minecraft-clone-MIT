# Voxelity mod SDK feed

This directory is the repository-local NuGet feed for standalone mod development.
It contains the public mod contracts and build SDK only; it does not contain the
game, renderer, or shaders.

`examples/TotalConversionMod/NuGet.config` uses this feed automatically. From that
example directory, `dotnet restore` followed by `dotnet build -c Release` therefore
works without building Voxelity itself. External mod projects should use the same
packages from the published Voxelity feed, or copy/configure this directory as a
NuGet source.

Current packages:

- `Voxelity.ModApi` 2.0.0
- `Voxelity.Loader.Abstractions` 2.0.0
- `Voxelity.ModSdk` 2.0.1
