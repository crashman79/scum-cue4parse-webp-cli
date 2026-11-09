# Getting Started

This guide will walk you through the basic usage of CUE4Parse to load and parse Unreal Engine packages.

---

## Basic Workflow

The typical workflow for using CUE4Parse involves:
1. Creating a file provider
2. Initializing the provider
3. Loading packages or individual objects
4. Processing the loaded data

## Creating a File Provider

First, create a file provider pointing to your game's archive directory:

```csharp
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;

var provider = new DefaultFileProvider(
    directory: "D:\\Games\\YourGame\\Content\\Paks",
    searchOption: SearchOption.TopDirectoryOnly,
    isCaseInsensitive: true,
    versions: new VersionContainer(EGame.GAME_UE5_3));
```

## Initializing the Provider

Before you can load any packages, you must initialize the provider to scan for files:

```csharp
provider.Initialize();
```

This will scan the archive directory for supported file extensions (`.pak`, `.utoc`, `.ucas`, `.uasset`, `.umap`).

## Adding Encryption Keys (if needed)

Many games encrypt their archives. If your game requires encryption keys:

```csharp
using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.Objects.Core.Misc;

provider.SubmitKey(new FGuid(), new FAesKey("0xYOUR_AES_KEY_HERE"));
```

## Loading All Exports from a Package

To load all exports from a package as a JSON-serializable collection:

```csharp
using Newtonsoft.Json;

var allExports = provider.LoadPackage("GameName/Content/Path/To/Asset").GetExports();
var json = JsonConvert.SerializeObject(allExports, Formatting.Indented);
Console.WriteLine(json);
```

## Loading a Specific Object

To load a specific named export from a package:

```csharp
var objectPath = "GameName/Content/Path/To/Asset.ObjectName";
var obj = provider.LoadObject(objectPath);
var json = JsonConvert.SerializeObject(obj, Formatting.Indented);
```

Note: The path format is `PackagePath.ExportName`

## Working with Different Asset Types

Once you've loaded an object, you can cast it to its specific type:

```csharp
switch (obj)
{
    case UTexture2D texture:
        // Process texture
        var bitmap = texture.Decode(ETexturePlatform.DesktopMobile);
        break;
    
    case USoundWave sound:
        // Process sound
        sound.Decode(true, out var format, out var data);
        break;
    
    case UStaticMesh mesh:
        // Process static mesh
        break;
    
    case USkeletalMesh skelMesh:
        // Process skeletal mesh
        break;
    
    default:
        Console.WriteLine($"Loaded {obj.GetType().Name}");
        break;
}
```

## Localization

To load localized strings:

```csharp
provider.LoadLocalization(ELanguage.English);
```

## Full Example

Here's a complete minimal example:

```csharp
using System;
using System.IO;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;

public class Example
{
    public static void Main()
    {
        // Create provider
        var provider = new DefaultFileProvider(
            "D:\\Games\\YourGame\\Content\\Paks",
            SearchOption.TopDirectoryOnly,
            true,
            new VersionContainer(EGame.GAME_UE5_3));
        
        // Initialize
        provider.Initialize();
        
        // Add encryption key if needed
        provider.SubmitKey(new FGuid(), new FAesKey("0xYOUR_KEY"));
        
        // Load an object
        var obj = provider.LoadObject("Game/Content/Path/Asset.ObjectName");
        
        // Serialize to JSON
        var json = JsonConvert.SerializeObject(obj, Formatting.Indented);
        Console.WriteLine(json);
    }
}
```

## Next Steps

- Learn about different [File Providers](File-Providers) for different scenarios
- Understand [Version Containers](Version-Containers) for game-specific parsing
- Explore [Exportable Objects](Exportable-Objects) to export assets to common formats
- Set up [Mapping Files](Mapping-Files) for better property names
