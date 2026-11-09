# Mounting Archives

Mounting is the process of registering archives with a file provider so that CUE4Parse can read and access the packages contained within them.

---

## What is Mounting?

When you "mount" an archive:
1. The provider scans for supported file types (`.pak`, `.utoc`, `.ucas`)
2. Reads the archive's file table/index
3. Maps virtual paths to physical file locations
4. Makes packages accessible for loading

## Automatic Mounting

Most file providers automatically mount archives during initialization:

```csharp
var provider = new DefaultFileProvider(
    "D:\\Games\\YourGame\\Content\\Paks",
    SearchOption.TopDirectoryOnly,
    true,
    new VersionContainer(EGame.GAME_UE5_3));

// This automatically mounts all archives in the directory
provider.Initialize();
```

### What Initialize() Does:
1. Scans the specified directory for supported files
2. Reads archive headers and file tables
3. Registers all found archives with the provider
4. Makes packages ready for access

## Search Options

Control how the provider searches for archives:

```csharp
// Only search the specified directory
var provider = new DefaultFileProvider(
    directory,
    SearchOption.TopDirectoryOnly,  // Don't search subdirectories
    true,
    versions);

// Search all subdirectories recursively
var provider = new DefaultFileProvider(
    directory,
    SearchOption.AllDirectories,  // Search recursively
    true,
    versions);
```

## Multiple Directories

Some games store archives in multiple locations:

```csharp
var provider = new DefaultFileProvider(
    directory: new DirectoryInfo("D:\\Games\\YourGame\\Content\\Paks"),
    extraDirectories: new DirectoryInfo[]
    {
        // Local AppData for user-specific content
        new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YourGame", "Saved", "Paks")),
        
        // Additional DLC directory
        new("D:\\Games\\YourGame\\DLC\\Paks")
    },
    searchOption: SearchOption.TopDirectoryOnly,
    isCaseInsensitive: true,
    versions: new VersionContainer(EGame.GAME_UE5_3));

provider.Initialize();
```

## Manual Mounting (Advanced)

For `StreamedFileProvider`, you must manually mount archives:

```csharp
var provider = new StreamedFileProvider(
    liveGame: "YourGame",
    isCaseInsensitive: true,
    versions: new VersionContainer(EGame.GAME_UE5_3));

// Manually register each archive
foreach (var archive in manifestInfo.Archives)
{
    provider.RegisterVfs(
        file: archive.GetFullName(),
        stream: new[] { archive.GetStream() });
}
```

This is useful for:
- Remote file systems
- Custom storage backends
- Streaming from HTTP/CDN
- Non-standard archive locations

## PostMount for Encrypted Archives

When working with encrypted archives, call `PostMount()` after submitting encryption keys:

```csharp
// Initialize and scan archives
provider.Initialize();

// Submit encryption keys
provider.SubmitKey(new FGuid(), new FAesKey("0xKEY..."));

// Finalize mounting with decryption
provider.PostMount();
```

**Why PostMount() is needed:**
- Some archive metadata is encrypted
- File tables may require decryption
- Ensures all archives are fully accessible

## Mounting Order and Priority

Archives are typically mounted in the order they're discovered. Later mounts can override earlier ones:

```csharp
// Base game archives mount first
provider.Initialize();

// Patch archives mounted later take precedence
// (if they contain files with the same virtual path)
```

This mimics Unreal Engine's own pak mounting behavior where:
- Higher-numbered paks override lower ones
- Patches override base content
- DLC may override base game assets

## Checking Mounted Files

After mounting, you can inspect what files are available:

```csharp
provider.Initialize();

// Get all mounted files
var allFiles = provider.Files;
Console.WriteLine($"Mounted {allFiles.Count} files");

// Check for a specific file
if (provider.TryFindGameFile("Game/Content/Path/Asset.uasset", out var gameFile))
{
    Console.WriteLine($"Found: {gameFile.Path}");
}
else
{
    Console.WriteLine("File not found in mounted archives");
}
```

## Virtual Paths

Mounted archives use virtual paths that match Unreal Engine's content structure:

```
Physical: D:\Games\YourGame\Content\Paks\pakchunk0-Windows.pak
Virtual:  Game/Content/Characters/Hero/Hero.uasset
```

The provider maps these virtual paths to physical locations in the mounted archives.

## Unmounting (Not Directly Supported)

CUE4Parse doesn't provide direct unmounting. To change mounted archives:
1. Create a new provider instance
2. Dispose of the old provider
3. Initialize the new provider with different archives

```csharp
// Instead of unmounting, create a new provider
using (var provider1 = new DefaultFileProvider(...))
{
    provider1.Initialize();
    // Use provider1
} // Disposed here

using (var provider2 = new DefaultFileProvider(...))
{
    provider2.Initialize();
    // Use provider2 with different archives
}
```

## Common Mounting Patterns

### Pattern 1: Simple Game with Encrypted Paks

```csharp
var provider = new DefaultFileProvider(pakDirectory, SearchOption.TopDirectoryOnly, true, versions);
provider.Initialize();
provider.SubmitKey(new FGuid(), new FAesKey(key));
provider.PostMount();
```

### Pattern 2: Game with Multiple Directories

```csharp
var provider = new DefaultFileProvider(
    new DirectoryInfo(mainPakDir),
    new[] { new DirectoryInfo(dlcDir), new DirectoryInfo(userPakDir) },
    SearchOption.TopDirectoryOnly,
    true,
    versions);
provider.Initialize();
```

### Pattern 3: Mobile APK Files

```csharp
var provider = new ApkFileProvider(
    "D:\\Games\\Mobile\\game.apk",
    true,
    versions);
provider.Initialize();
```

### Pattern 4: Custom Streaming Source

```csharp
var provider = new StreamedFileProvider("Game", true, versions);

// Download or retrieve archive streams
foreach (var archiveInfo in GetArchiveList())
{
    using var stream = DownloadArchive(archiveInfo.Url);
    provider.RegisterVfs(archiveInfo.Name, new[] { stream });
}
```

## Performance Considerations

### Lazy Loading
- Archives are mounted but not fully loaded into memory
- Files are loaded on-demand when accessed
- This keeps memory usage low even with large archives

### Caching
```csharp
// CUE4Parse caches loaded packages by default
var package1 = provider.LoadPackage("Path/To/Asset");
var package2 = provider.LoadPackage("Path/To/Asset"); // Returns cached instance
```

### Large Archive Sets
- Initialization time increases with more archives
- Consider mounting only needed archives
- Use `SearchOption.TopDirectoryOnly` when possible

## Troubleshooting

### "No files found" after Initialize()
- Check directory path is correct
- Verify archives exist (`.pak`, `.utoc`, `.ucas` files)
- Ensure SearchOption matches your directory structure
- Check file permissions

### Files not accessible after mounting
- Verify encryption keys are submitted before PostMount()
- Check if archives are corrupted
- Ensure game version matches archive format

### Duplicate file warnings
- Multiple archives contain the same file
- This is normal for patches/DLC
- Later-mounted archives take precedence

## Related Topics

- [File Providers](File-Providers) - Different provider types and their mounting behavior
- [AES Encryption Keys](AES-Encryption-Keys) - Mounting encrypted archives
- [Virtual Paths](Virtual-Paths) - Understanding mounted file paths
