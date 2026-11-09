# CUE4Parse WebP CLI

A command-line extractor for Unreal Engine `.pak` files, built on
[CUE4Parse](https://github.com/FabianFG/CUE4Parse). It exports JSON, textures
and meshes, converts textures to WebP, and is designed for automated extraction
rather than a manual GUI workflow.

This is the public version of the extractor used for the SCUM DB fan project.
The SCUM DB database and site are separate and are not part of this repository.

## What is different from upstream

- WebP output instead of large PNG texture dumps.
- Faster texture conversion and memory handling for large PAK sets.
- Deterministic, collision-safe icon paths.
- BC7 texture support for pile and icon textures.
- `SCUM_EXTRACTION_MEMORY_GB` to cap extractor memory on smaller hosts.
- AES key is read from the environment, not committed to the repository.
- Optional upstream debug patch in `patches/` for landscape conversion.

## Requirements

- .NET 8 SDK
- The CUE4Parse source tree as a sibling directory:
  ```
  asset-extraction/
    CUE4Parse/
    CUE4Parse-WebP-CLI/
  ```
- Your own copy of the game files and the AES key for the build you are
  extracting. Keys and game files are not included.

## Build

```sh
cd CUE4Parse-WebP-CLI
dotnet build -c Release
```

The project references `../CUE4Parse/CUE4Parse/CUE4Parse.csproj` and
`../CUE4Parse/CUE4Parse-Conversion/CUE4Parse-Conversion.csproj`.

## AES key

The extractor refuses to run without `SCUM_AES_KEY`. The key is build-specific
and is not distributed with this repository. Set it in your shell:

```sh
export SCUM_AES_KEY="0xYOUR_KEY_HERE"
export SCUM_OUTPUT_DIR="$PWD/out"
```

On Windows PowerShell:

```powershell
$env:SCUM_AES_KEY = "0xYOUR_KEY_HERE"
$env:SCUM_OUTPUT_DIR = "$PWD\out"
```

See `docs/OBTAINING-KEYS.md` for how to supply a key and what to do when it does
not match your game build. Do not commit keys, `.env` files or extracted game
assets.

## Run

```sh
# Show options
dotnet run --project CUE4ParseCLI.csproj -- --help

# Example: extract with a path filter
dotnet run --project CUE4ParseCLI.csproj -- \
  --input /path/to/Game/Content/Paks \
  --filter "Items/" \
  --output "$SCUM_OUTPUT_DIR"
```

The GUI and TUI are selected automatically, or with `--gui`, `--tui` or
`--no-ui`.

## Output

The extractor writes JSON, WebP textures and mesh files under the output
directory. It does not modify the game files. Treat extracted assets as
third-party game content and respect the rights holder's terms.

## License

MIT. See `LICENSE`.

CUE4Parse is a separate project with its own license and maintainers. This
repository only contains the CLI and the extraction changes built on top of it.
