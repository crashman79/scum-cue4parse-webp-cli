# Mapping Files

Mapping files (.usmap) provide property name mappings for Unreal Engine packages, enabling CUE4Parse to deserialize objects with correct property names even when the game was compiled without reflection data.

---

## What are Mapping Files?

Unreal Engine games can be compiled with or without reflection data. When games are shipped without reflection (common for optimized builds):
- Property names are stripped and replaced with hashes
- CUE4Parse can only see hashed property identifiers
- Object deserialization shows generic property names

**Mapping files solve this** by providing a mapping from hashes back to original property names.

## File Format: .usmap

The `.usmap` format is a binary file containing:
- Property name mappings
- Enum value mappings
- Struct definitions
- Class hierarchies

These files are typically generated from:
- Game executable analysis
- Community-maintained mappings
- Official sources for modding-friendly games

## Using Mapping Files

### Basic Setup

```csharp
using CUE4Parse.MappingsProvider;

var provider = new DefaultFileProvider(
    directory: "D:\\Games\\YourGame\\Content\\Paks",
    searchOption: SearchOption.TopDirectoryOnly,
    isCaseInsensitive: true,
    versions: new VersionContainer(EGame.GAME_UE5_3));

// Load mappings from .usmap file
provider.MappingsContainer = new FileUsmapTypeMappingsProvider("path/to/mappings.usmap");

provider.Initialize();
```

### Complete Example

```csharp
using System.IO;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;

var provider = new DefaultFileProvider(
    "D:\\Games\\Fortnite\\FortniteGame\\Content\\Paks",
    SearchOption.TopDirectoryOnly,
    true,
    new VersionContainer(EGame.GAME_UE5_3));

// Apply mappings BEFORE initialization
provider.MappingsContainer = new FileUsmapTypeMappingsProvider(
    "./++Fortnite+Release-33.20-CL-39082670-Windows_oo.usmap");

provider.Initialize();
provider.SubmitKey(new FGuid(), new FAesKey("0xYOUR_KEY"));
provider.PostMount();

// Now objects will have proper property names
var obj = provider.LoadObject("FortniteGame/Content/Items/Weapons/Rifle");
var json = JsonConvert.SerializeObject(obj, Formatting.Indented);
Console.WriteLine(json);  // Shows actual property names!
```

## Benefits of Using Mappings

### Without Mappings

```json
{
  "Prop_0x12345678": "SomeValue",
  "Prop_0xABCDEF01": 100,
  "Prop_0x9876543": true
}
```

### With Mappings

```json
{
  "WeaponName": "Assault Rifle",
  "Damage": 100,
  "IsAutomatic": true
}
```

## Finding Mapping Files

### Sources:

1. **Community Repositories**
   - GitHub repositories for popular games
   - Modding community Discord servers
   - Game-specific forums

2. **Mapping Generation Tools**
   - UsmapDumper
   - Custom tools for specific games
   - Automated extraction from game executables

3. **Per-Version Mappings**
   - Mappings are version-specific
   - Each game update may require new mappings
   - Naming convention: `GameName+Release-Version-CL-Changelist.usmap`

Example naming:
```
++Fortnite+Release-33.20-CL-39082670-Windows_oo.usmap
++DeadByDaylight+Release-8.4.0-CL-12345678-Windows.usmap
```

## Mapping File Locations

Typical storage locations:

```
project/
├── mappings/
│   ├── game_v1.0.0.usmap
│   ├── game_v1.1.0.usmap
│   └── game_v1.2.0.usmap
├── Program.cs
└── ...
```

Load relative to your application:

```csharp
var mappingPath = Path.Combine(
    AppDomain.CurrentDomain.BaseDirectory,
    "mappings",
    "game_v1.2.0.usmap");

provider.MappingsContainer = new FileUsmapTypeMappingsProvider(mappingPath);
```

## Custom Mapping Providers

You can implement custom mapping providers:

```csharp
using CUE4Parse.MappingsProvider;

public class CustomMappingsProvider : AbstractMappingsProvider
{
    public CustomMappingsProvider()
    {
        // Load mappings from database, API, etc.
    }
    
    // Implement required methods
    public override void Reload()
    {
        // Reload mappings logic
    }
}

// Use custom provider
provider.MappingsContainer = new CustomMappingsProvider();
```

## Multiple Mapping Files

Some projects may need multiple mapping files for different game modules:

```csharp
// For now, CUE4Parse supports one mapping container per provider
// If you need multiple mappings, you may need to:
// 1. Merge mapping files externally
// 2. Use separate providers for different modules
// 3. Implement a custom MappingsProvider that combines sources
```

## Checking if Mappings are Loaded

```csharp
if (provider.MappingsContainer != null)
{
    Console.WriteLine("Mappings are loaded");
}
else
{
    Console.WriteLine("No mappings loaded - property names may be hashed");
}
```

## Version Matching

**Critical:** Mapping files must match your game version!

```csharp
// Example: Fortnite v33.20
var gameVersion = "33.20";
var mappingFile = $"++Fortnite+Release-{gameVersion}-CL-39082670-Windows_oo.usmap";

if (File.Exists(mappingFile))
{
    provider.MappingsContainer = new FileUsmapTypeMappingsProvider(mappingFile);
}
else
{
    Console.WriteLine($"Warning: No mappings found for version {gameVersion}");
    Console.WriteLine("Objects will have hashed property names");
}
```

## Troubleshooting

### Properties still show as hashes
- Verify mapping file matches game version exactly
- Check if mapping file was loaded before `Initialize()`
- Confirm .usmap file is not corrupted

### "Failed to load mappings" error
- Check file path is correct
- Verify .usmap file format is valid
- Ensure you have read permissions

### Partial mapping coverage
- Some properties mapped, others not
- Mapping file may be incomplete for this asset type
- Game may have added new properties in updates

### Performance with large mapping files
- Mapping files can be several MB
- Loading happens once at startup
- Consider caching deserialized objects if you reload often

## When Mappings Aren't Needed

You may not need mappings if:
- Game ships with reflection data (rare)
- You only need to extract binary data (textures, sounds)
- You're working with specific asset types that don't depend on property names
- The game is very old and well-documented

## Creating Your Own Mappings

Advanced users can create mappings by:

1. **Analyzing the game executable**
   - Extract RTTI information
   - Map property hashes to names
   - Generate .usmap format

2. **Using community tools**
   - UsmapDumper for Unreal Engine games
   - Game-specific dumping tools
   - Automated scripts for common engines

3. **Manual mapping**
   - Identify properties through game behavior
   - Build partial mappings for specific assets
   - Document findings for community use

## Best Practices

1. **Version control your mappings**
   - Keep mappings for each game version
   - Document which mapping works with which game build

2. **Fallback handling**
   ```csharp
   if (File.Exists(mappingPath))
   {
       provider.MappingsContainer = new FileUsmapTypeMappingsProvider(mappingPath);
   }
   else
   {
       Console.WriteLine("Warning: Running without mappings");
   }
   ```

3. **Mapping file validation**
   ```csharp
   try
   {
       provider.MappingsContainer = new FileUsmapTypeMappingsProvider(mappingPath);
       provider.MappingsContainer.Reload();  // Validate it loads
   }
   catch (Exception ex)
   {
       Console.WriteLine($"Failed to load mappings: {ex.Message}");
       provider.MappingsContainer = null;
   }
   ```

4. **Update with game patches**
   - Game updates may invalidate old mappings
   - Check community sources for updated mappings
   - Re-extract mappings when game updates

## Complete Example with Error Handling

```csharp
using System;
using System.IO;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Versions;

public class MappingExample
{
    public static void Main()
    {
        var pakDirectory = "D:\\Games\\YourGame\\Content\\Paks";
        var mappingPath = "./mappings/game_v1.0.0.usmap";
        
        var provider = new DefaultFileProvider(
            pakDirectory,
            SearchOption.TopDirectoryOnly,
            true,
            new VersionContainer(EGame.GAME_UE5_3));
        
        // Try to load mappings
        if (File.Exists(mappingPath))
        {
            try
            {
                provider.MappingsContainer = new FileUsmapTypeMappingsProvider(mappingPath);
                Console.WriteLine($"✓ Loaded mappings from {Path.GetFileName(mappingPath)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Failed to load mappings: {ex.Message}");
                Console.WriteLine("  Continuing without mappings...");
            }
        }
        else
        {
            Console.WriteLine($"⚠ Mapping file not found: {mappingPath}");
            Console.WriteLine("  Objects will have hashed property names");
        }
        
        provider.Initialize();
        
        // Now use the provider...
    }
}
```

## Related Topics

- [Getting Started](Getting-Started) - Basic object loading
- [File Providers](File-Providers) - Setting up providers with mappings
- [Version Containers](Version-Containers) - Matching versions with mappings
- [Custom Object Type](Custom-Object-Type) - Advanced property handling
