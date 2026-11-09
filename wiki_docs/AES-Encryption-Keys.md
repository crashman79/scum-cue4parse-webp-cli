# AES Encryption Keys

Many modern Unreal Engine games encrypt their archives to protect game assets. CUE4Parse supports AES encryption, but you need to provide the appropriate encryption keys.

---

## What are AES Keys?

AES (Advanced Encryption Standard) keys are cryptographic keys used to encrypt and decrypt game archives. Games use these keys to:
- Protect intellectual property
- Prevent unauthorized data mining
- Secure unreleased content
- Protect online game integrity

## Key Format

AES keys in CUE4Parse are provided as hexadecimal strings, typically prefixed with `0x`:

```
0x1234567890ABCDEF1234567890ABCDEF1234567890ABCDEF1234567890ABCDEF
```

Keys are usually **256-bit (64 hex characters)** but can also be **128-bit (32 hex characters)**.

## Submitting Keys to a Provider

Use `SubmitKey()` to add encryption keys to your file provider:

```csharp
using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.Objects.Core.Misc;

provider.SubmitKey(new FGuid(), new FAesKey("0xYOUR_KEY_HERE"));
```

### Basic Example

```csharp
var provider = new DefaultFileProvider(
    "D:\\Games\\YourGame\\Content\\Paks",
    SearchOption.TopDirectoryOnly,
    true,
    new VersionContainer(EGame.GAME_UE5_3));

provider.Initialize();

// Submit the main encryption key
provider.SubmitKey(new FGuid(), new FAesKey("0x1234..."));
```

## Multiple Keys

Some games use different encryption keys for different archives. You can submit multiple keys:

```csharp
// Main archive key
provider.SubmitKey(new FGuid(), new FAesKey("0xKEY1..."));

// DLC or patch key
provider.SubmitKey(
    new FGuid("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE"), 
    new FAesKey("0xKEY2..."));

// Another optional key
provider.SubmitKey(
    new FGuid("11111111-2222-3333-4444-555555555555"),
    new FAesKey("0xKEY3..."));
```

## Key Management Best Practices

### 1. Store Keys Securely

**Never hardcode keys in public repositories!**

```csharp
// ❌ BAD: Hardcoded key in source
var key = "0x1234567890ABCDEF...";

// ✅ GOOD: Load from environment variable
var key = Environment.GetEnvironmentVariable("GAME_AES_KEY");

// ✅ GOOD: Load from configuration file
var key = Configuration["EncryptionKeys:MainKey"];

// ✅ GOOD: Load from secure vault
var key = KeyVault.GetSecret("game-aes-key");
```

### 2. Use Configuration Files

```json
// appsettings.json (not committed to repo)
{
  "EncryptionKeys": {
    "MainKey": "0x1234567890ABCDEF...",
    "DLCKey": "0xABCDEF1234567890..."
  }
}
```

```csharp
var mainKey = configuration["EncryptionKeys:MainKey"];
provider.SubmitKey(new FGuid(), new FAesKey(mainKey));
```

### 3. Environment Variables

```bash
# .env file (not committed to repo)
GAME_AES_KEY=0x1234567890ABCDEF...
```

```csharp
var key = Environment.GetEnvironmentVariable("GAME_AES_KEY");
provider.SubmitKey(new FGuid(), new FAesKey(key));
```

## Finding Encryption Keys

> [!WARNING]
> We do not provide encryption keys. You must obtain them legally through:

### Legal Methods:
1. **Official sources** - Developers may publish keys for modding
2. **Game files** - Some games store keys in executable or configuration files
3. **Community resources** - Modding communities may share keys for games that officially support modding
4. **Reverse engineering** - Legal in many jurisdictions for interoperability purposes (check your local laws)

### Common Key Locations:
- Game executable (`.exe`, `.dll`)
- Configuration files (`.ini`, `.cfg`)
- Online services (may be retrieved at runtime)
- Previous versions of the game

## PostMount for Encrypted Archives

After submitting keys, call `PostMount()` to fully decrypt and mount the archives:

```csharp
provider.Initialize();
provider.SubmitKey(new FGuid(), new FAesKey(key));
provider.PostMount();  // Important for encrypted archives!
```

This ensures all encrypted pak files are properly decrypted and accessible.

## Handling Missing Keys

If you try to access encrypted content without the proper key:

```csharp
try
{
    var package = provider.LoadPackage("Path/To/Encrypted/Asset");
}
catch (Exception ex)
{
    // May throw exceptions related to missing encryption keys
    Console.WriteLine($"Failed to load: {ex.Message}");
    // Check if it's an encryption error and prompt for key
}
```

## Complete Example

```csharp
using System;
using System.IO;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;

public class EncryptedGameLoader
{
    public static void Main()
    {
        // Load key from environment
        var aesKey = Environment.GetEnvironmentVariable("GAME_AES_KEY");
        
        if (string.IsNullOrEmpty(aesKey))
        {
            Console.WriteLine("Error: GAME_AES_KEY environment variable not set");
            return;
        }
        
        // Create and initialize provider
        var provider = new DefaultFileProvider(
            "D:\\Games\\YourGame\\Content\\Paks",
            SearchOption.TopDirectoryOnly,
            true,
            new VersionContainer(EGame.GAME_UE5_3));
        
        provider.Initialize();
        
        // Submit encryption key
        provider.SubmitKey(new FGuid(), new FAesKey(aesKey));
        
        // Mount encrypted archives
        provider.PostMount();
        
        // Now you can load encrypted packages
        var package = provider.LoadPackage("Game/Content/Encrypted/Asset");
        Console.WriteLine($"Successfully loaded {package.Name}");
    }
}
```

## Dynamic Keys

Some games use dynamic keys or key encryption. In these cases, you may need to:

1. Decrypt the key at runtime
2. Retrieve keys from game servers
3. Compute keys based on game-specific algorithms

This is an advanced topic beyond the scope of this guide.

## Troubleshooting

### "Cannot decrypt" errors
- Verify your key is correct (check hex format)
- Ensure you're using `0x` prefix
- Check if you need multiple keys for different archives
- Confirm the game version matches your key

### Keys not working after game updates
- Games may change keys with updates
- Check community resources for updated keys
- You may need to extract new keys from the updated game

### Partial decryption
- Some games use per-archive keys
- Submit all known keys before calling `PostMount()`
- Check if specific DLC or patches have separate keys

## Related Topics

- [File Providers](File-Providers) - Using encryption with different provider types
- [Mounting Archives](Mounting-Archives) - Understanding the mounting process
- [Getting Started](Getting-Started) - Basic usage including encryption
