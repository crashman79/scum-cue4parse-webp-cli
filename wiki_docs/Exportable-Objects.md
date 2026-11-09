# Exportable Objects

CUE4Parse can export various Unreal Engine asset types to common file formats. This allows you to extract textures, sounds, meshes, and animations from game files.

---

## Overview

CUE4Parse supports exporting:
- **Textures** → PNG, TGA, DDS
- **Sounds** → OGG, MP3, WAV, OPUS, FLAC
- **Static Meshes** → glTF, UEFormat, OBJ, FBX
- **Skeletal Meshes** → glTF, UEFormat, ActorX
- **Animations** → glTF, UEFormat, ActorX
- **Materials** → JSON representation

## Basic Export Workflow

### 1. Load the Object

```csharp
var provider = new DefaultFileProvider(directory, SearchOption.TopDirectoryOnly, true, versions);
provider.Initialize();

var obj = provider.LoadObject("Game/Content/Path/Asset.ObjectName");
```

### 2. Cast to Specific Type and Export

```csharp
switch (obj)
{
    case UTexture2D texture:
        ExportTexture(texture);
        break;
    
    case USoundWave sound:
        ExportSound(sound);
        break;
    
    case UStaticMesh mesh:
        ExportStaticMesh(mesh);
        break;
    
    case USkeletalMesh skelMesh:
        ExportSkeletalMesh(skelMesh);
        break;
    
    case UAnimSequence anim:
        ExportAnimation(anim);
        break;
}
```

## Exporting Textures

### Simple Texture Export

```csharp
using CUE4Parse_Conversion.Textures;

var texture = provider.LoadObject<UTexture2D>("Path/To/Texture");

// Decode texture to bitmap
var bitmap = texture.Decode(ETexturePlatform.DesktopMobile);

// Encode to PNG
var pngData = bitmap.Encode(ETextureFormat.Png, false, out var extension);

// Save to file
File.WriteAllBytes($"{texture.Name}.png", pngData);
```

### Supported Texture Formats

```csharp
// PNG (most common)
var data = bitmap.Encode(ETextureFormat.Png, false, out var ext);

// TGA
var data = bitmap.Encode(ETextureFormat.Tga, false, out var ext);

// DDS
var data = bitmap.Encode(ETextureFormat.Dds, false, out var ext);
```

### Texture Arrays

```csharp
if (texture is UTexture2DArray textureArray)
{
    var bitmaps = textureArray.DecodeTextureArray(ETexturePlatform.DesktopMobile);
    
    for (int i = 0; i < bitmaps.Length; i++)
    {
        var data = bitmaps[i].Encode(ETextureFormat.Png, false, out var ext);
        File.WriteAllBytes($"{texture.Name}_{i}.png", data);
    }
}
```

### Cubemaps

```csharp
if (texture is UTextureCube cubemap)
{
    var bitmap = cubemap.Decode(ETexturePlatform.DesktopMobile);
    
    // Convert to panorama
    var panorama = bitmap.ToPanorama();
    
    var data = panorama.Encode(ETextureFormat.Png, false, out var ext);
    File.WriteAllBytes($"{texture.Name}_panorama.png", data);
}
```

## Exporting Sounds

### Basic Sound Export

```csharp
using CUE4Parse.UE4.Assets.Exports.Sound;

var sound = provider.LoadObject<USoundWave>("Path/To/Sound");

// Decode sound
sound.Decode(true, out var format, out var audioData);

// Save to file
if (audioData != null)
{
    File.WriteAllBytes($"{sound.Name}.{format.ToLower()}", audioData);
}
```

### Supported Audio Formats

The format is determined by the game's source audio:
- **OGG Vorbis** - Most common
- **OPUS** - Modern compression
- **MP3** - Older games
- **WAV** - Uncompressed
- **FLAC** - Lossless compression

### Wwise Audio

```csharp
using CUE4Parse.UE4.Assets.Exports.Wwise;

if (obj is UAkMediaAssetData wwiseAsset)
{
    wwiseAsset.Decode(true, out var format, out var audioData);
    File.WriteAllBytes($"{wwiseAsset.Name}.{format.ToLower()}", audioData);
}
```

## Exporting Meshes and Animations

### Using the Conversion Library

```csharp
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Animations;
using CUE4Parse_Conversion.UEFormat.Enums;

// Configure export options
var options = new ExporterOptions
{
    LodFormat = ELodFormat.FirstLod,
    MeshFormat = EMeshFormat.UEFormat,
    AnimFormat = EAnimFormat.UEFormat,
    MaterialFormat = EMaterialFormat.AllLayersNoRef,
    Platform = ETexturePlatform.DesktopMobile,
    SocketFormat = ESocketFormat.Bone,
    ExportMorphTargets = true,
    ExportMaterials = false
};
```

### Static Mesh Export

```csharp
var staticMesh = provider.LoadObject<UStaticMesh>("Path/To/StaticMesh");

var exporter = new CUE4Parse_Conversion.Exporter(staticMesh, options);

if (exporter.TryWriteToDir(new DirectoryInfo("./exports"), out var label, out var savedFilePath))
{
    Console.WriteLine($"Exported to: {savedFilePath}");
}
```

### Skeletal Mesh Export

```csharp
var skeletalMesh = provider.LoadObject<USkeletalMesh>("Path/To/SkeletalMesh");

var exporter = new CUE4Parse_Conversion.Exporter(skeletalMesh, options);

if (exporter.TryWriteToDir(new DirectoryInfo("./exports"), out var label, out var savedFilePath))
{
    Console.WriteLine($"Exported to: {savedFilePath}");
}
```

### Animation Export

```csharp
var animation = provider.LoadObject<UAnimSequence>("Path/To/Animation");

var exporter = new CUE4Parse_Conversion.Exporter(animation, options);

if (exporter.TryWriteToDir(new DirectoryInfo("./exports"), out var label, out var savedFilePath))
{
    Console.WriteLine($"Exported to: {savedFilePath}");
}
```

## Export Format Options

### Mesh Formats

```csharp
// UEFormat (JSON-based, preserves all data)
options.MeshFormat = EMeshFormat.UEFormat;

// glTF 2.0 (widely supported 3D format)
options.MeshFormat = EMeshFormat.GLTF2;

// ActorX (PSK/PSA for UE3-style imports)
options.MeshFormat = EMeshFormat.ActorX;
```

### LOD (Level of Detail) Options

```csharp
// Export only the first (highest quality) LOD
options.LodFormat = ELodFormat.FirstLod;

// Export all LODs
options.LodFormat = ELodFormat.AllLods;
```

### Material Options

```csharp
// Export all material layers without references
options.MaterialFormat = EMaterialFormat.AllLayersNoRef;

// Export all material layers with references
options.MaterialFormat = EMaterialFormat.AllLayers;

// Export only first material layer
options.MaterialFormat = EMaterialFormat.FirstLayer;
```

### Socket Format

```csharp
// Export sockets as bones
options.SocketFormat = ESocketFormat.Bone;

// Export sockets as meshes
options.SocketFormat = ESocketFormat.Mesh;
```

## Batch Exporting

### Export All Textures

```csharp
var files = provider.Files.Values
    .Where(f => f.Path.EndsWith(".uasset"));

foreach (var file in files)
{
    if (provider.TryLoadObject(file.Path, out var obj))
    {
        if (obj is UTexture2D texture)
        {
            try
            {
                var bitmap = texture.Decode(ETexturePlatform.DesktopMobile);
                var data = bitmap.Encode(ETextureFormat.Png, false, out _);
                
                var outputPath = Path.Combine("exports", $"{texture.Name}.png");
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                File.WriteAllBytes(outputPath, data);
                
                Console.WriteLine($"Exported: {texture.Name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to export {texture.Name}: {ex.Message}");
            }
        }
    }
}
```

### Parallel Processing

```csharp
using System.Threading.Tasks;

var packages = provider.Files.Values
    .Where(f => f.Path.EndsWith(".uasset"))
    .ToArray();

Parallel.ForEach(packages, package =>
{
    try
    {
        if (provider.TryLoadPackage(package, out var pkg))
        {
            foreach (var export in pkg.GetExports())
            {
                // Export logic here
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error processing {package.Name}: {ex.Message}");
    }
});
```

## Advanced: Material Export

Materials are complex and typically exported as JSON:

```csharp
using Newtonsoft.Json;

var material = provider.LoadObject<UMaterialInterface>("Path/To/Material");
var json = JsonConvert.SerializeObject(material, Formatting.Indented);
File.WriteAllText($"{material.Name}.json", json);
```

## Compression Options

Control output file compression:

```csharp
// No compression (faster, larger files)
options.CompressionFormat = EFileCompressionFormat.None;

// ZIP compression
options.CompressionFormat = EFileCompressionFormat.Zip;
```

## Complete Export Example

```csharp
using System;
using System.IO;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.UEFormat.Enums;

public class ExportExample
{
    public static void Main()
    {
        // Setup provider
        var provider = new DefaultFileProvider(
            "D:\\Games\\YourGame\\Content\\Paks",
            SearchOption.TopDirectoryOnly,
            true,
            new VersionContainer(EGame.GAME_UE5_3));
        
        provider.Initialize();
        provider.SubmitKey(new FGuid(), new FAesKey("0xKEY..."));
        provider.PostMount();
        
        // Configure export options
        var options = new ExporterOptions
        {
            LodFormat = ELodFormat.FirstLod,
            MeshFormat = EMeshFormat.UEFormat,
            TextureFormat = ETextureFormat.Png,
            Platform = ETexturePlatform.DesktopMobile
        };
        
        // Load and export an object
        var obj = provider.LoadObject("Game/Content/Characters/Hero/Mesh");
        
        var exporter = new CUE4Parse_Conversion.Exporter(obj, options);
        
        if (exporter.TryWriteToDir(
            new DirectoryInfo("./exports"),
            out var label,
            out var savedFilePath))
        {
            Console.WriteLine($"Successfully exported to: {savedFilePath}");
        }
        else
        {
            Console.WriteLine("Export failed");
        }
    }
}
```

## Troubleshooting

### Failed to decode texture
- Check platform setting matches game (usually DesktopMobile for PC)
- Verify texture format is supported
- Some textures may use custom formats

### Mesh export fails
- Ensure mesh has valid LODs
- Check if skeleton is loaded for skeletal meshes
- Verify export options are appropriate

### Missing materials in exports
- Set `ExportMaterials = true` in options
- Materials are exported as references or JSON
- Complex materials may not fully export

## Related Topics

- [Getting Started](Getting-Started) - Basic object loading
- [File Providers](File-Providers) - Loading objects for export
- [Version Containers](Version-Containers) - Platform settings affect exports
