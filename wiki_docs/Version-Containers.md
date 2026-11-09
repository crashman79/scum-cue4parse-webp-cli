# Version Containers

Version containers tell CUE4Parse which Unreal Engine version and platform to use when parsing game files. This is crucial for correctly interpreting binary data structures that vary between engine versions.

---

## What is a VersionContainer?

A `VersionContainer` specifies:
- The **game/engine version** (e.g., UE4.27, UE5.3)
- The **texture platform** (Desktop, Mobile, Console)
- Optional **custom versions** for specific engine modifications

```csharp
using CUE4Parse.UE4.Versions;

var versions = new VersionContainer(EGame.GAME_UE5_3);
```

## Common Engine Versions

CUE4Parse supports a wide range of Unreal Engine versions through the `EGame` enum:

### Unreal Engine 5
```csharp
EGame.GAME_UE5_0      // Unreal Engine 5.0
EGame.GAME_UE5_1      // Unreal Engine 5.1
EGame.GAME_UE5_2      // Unreal Engine 5.2
EGame.GAME_UE5_3      // Unreal Engine 5.3
EGame.GAME_UE5_4      // Unreal Engine 5.4
EGame.GAME_UE5_5      // Unreal Engine 5.5
EGame.GAME_UE5_6      // Unreal Engine 5.6 (Latest)
```

### Unreal Engine 4
```csharp
EGame.GAME_UE4_27     // Unreal Engine 4.27
EGame.GAME_UE4_26     // Unreal Engine 4.26
EGame.GAME_UE4_25     // Unreal Engine 4.25
// ... and earlier versions
EGame.GAME_UE4_LATEST // Alias for latest UE4 version
```

## Texture Platforms

The platform affects how textures are decoded:

```csharp
using CUE4Parse_Conversion.Textures;

// Desktop/PC textures (most common)
var version = new VersionContainer(
    EGame.GAME_UE5_3, 
    ETexturePlatform.DesktopMobile);

// Mobile platform textures
var versionMobile = new VersionContainer(
    EGame.GAME_UE5_3, 
    ETexturePlatform.Mobile);
```

Available platforms:
- `ETexturePlatform.DesktopMobile` - PC and mobile (most common)
- `ETexturePlatform.Mobile` - Mobile-specific
- `ETexturePlatform.PlayStation` - PlayStation consoles
- `ETexturePlatform.Xbox` - Xbox consoles
- `ETexturePlatform.Nintendo` - Nintendo Switch

## Using with File Providers

Version containers are passed when creating a file provider:

```csharp
var provider = new DefaultFileProvider(
    directory: "D:\\Games\\YourGame\\Content\\Paks",
    searchOption: SearchOption.TopDirectoryOnly,
    isCaseInsensitive: true,
    versions: new VersionContainer(EGame.GAME_UE5_3));
```

## Determining the Right Version

### Method 1: Game Documentation
Check the game's documentation or community resources for the engine version.

### Method 2: Trial and Error
Start with the latest version and work backwards if you encounter parsing errors:
```csharp
// Try latest first
var versions = new VersionContainer(EGame.GAME_UE5_6);

// If that fails, try earlier versions
// var versions = new VersionContainer(EGame.GAME_UE5_3);
// var versions = new VersionContainer(EGame.GAME_UE4_27);
```

### Method 3: Engine Version File
Some games include version information in their files. Look for files like:
- `Engine/Binaries/.version`
- Version info in `.uplugin` or `.uproject` files

## Version-Specific Behavior

Different engine versions may have:
- Different asset serialization formats
- Changed property names or types
- New or removed asset classes
- Different compression methods

Using the correct version ensures:
✅ Proper deserialization of assets  
✅ Correct property names and values  
✅ Accurate texture decoding  
✅ Proper handling of custom data types

## Example: Complete Setup

```csharp
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures;

// For a UE5.3 PC game
var provider = new DefaultFileProvider(
    "D:\\Games\\Fortnite\\FortniteGame\\Content\\Paks",
    SearchOption.TopDirectoryOnly,
    true,
    new VersionContainer(
        game: EGame.GAME_UE5_3,
        platform: ETexturePlatform.DesktopMobile));

provider.Initialize();
```

## Custom Versions

For games with heavily modified engines, you may need custom version GUIDs. This is an advanced topic typically only needed for specific games with known engine modifications.

```csharp
// Advanced: Custom engine versions (rarely needed)
var versions = new VersionContainer(EGame.GAME_UE5_3);
// Custom version configuration would go here if needed
```

## Best Practices

1. **Always specify a version** - Don't rely on defaults
2. **Match the game's actual engine version** - Incorrect versions cause parsing errors
3. **Use DesktopMobile for PC games** - It's the most compatible texture platform
4. **Update versions when games update** - Engine upgrades may require version changes
5. **Consult game-specific documentation** - Some games have known quirks

## Related Topics

- [File Providers](File-Providers) - Using versions with different provider types
- [Exportable Objects](Exportable-Objects) - Version affects export options
- [AES Encryption Keys](AES-Encryption-Keys) - Often needed alongside version specification
