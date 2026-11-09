# Examples & Configuration

## Game-Specific Configurations

### SCUM
```csharp
string pakFolder = "/path/to/SCUM/Paks";
```
**Recommended filter:** `--filter "Items/"`

### Fortnite
```csharp
string pakFolder = "C:/Program Files/Epic Games/Fortnite/FortniteGame/Content/Paks";
```
**Recommended filter:** `--filter "Athena/Items/"`

### ARK: Survival Evolved
```csharp
string pakFolder = "C:/Program Files (x86)/Steam/steamapps/common/ARK/ShooterGame/Content/Paks";
```
**Recommended filter:** `--filter "PrimalItemResource"`

### PUBG
```csharp
string pakFolder = "C:/Program Files (x86)/Steam/steamapps/common/PUBG/TslGame/Content/Paks";
```
**Recommended filter:** `--filter "Item/Weapon/"`

## Usage Examples

### Extract Everything (Default WebP Quality 85)
```bash
dotnet run -- --output "./full_export"
```
**Output:** All JSON, icons, and textures in WebP format at quality 85

### Extract with High-Quality PNG
```bash
dotnet run -- --format png --quality 100 --output "./png_export"
```
**Output:** Lossless PNG files (largest file size, best quality)

### Extract with Maximum Compression
```bash
dotnet run -- --format webp --quality 60 --output "./compressed"
```
**Output:** Highly compressed WebP files (smallest size, slight quality loss)

### Extract as JPEG for Compatibility
```bash
dotnet run -- --format jpeg --quality 90 --output "./jpeg_export"
```
**Output:** JPEG files (widely supported, lossy compression)

### Extract Only Weapons
```bash
dotnet run -- --filter "Weapons" --format webp --quality 85 --output "./weapons"
```
**Output:** Only weapon-related assets

### Extract Only JSON (No Images)
```bash
dotnet run -- --no-icons --output "./json_data"
```
**Output:** JSON files only, no image conversion

### High-Quality Archival Export
```bash
dotnet run -- --format webp --quality 100 --output "./archive"
```
**Output:** Lossless WebP (50-70% smaller than PNG, no quality loss)

### Multiple Filters (requires code modification)
```csharp
// In Program.cs, modify file filtering logic:
var files = allFiles
    .Where(f => 
        f.Contains("/Weapons/") || 
        f.Contains("/Armor/") || 
        f.Contains("/Consumables/")
    )
    .ToList();
```

## Export Format & Quality Guide

### Format Comparison

| Format | Lossless | Compression | Compatibility | Best For |
|--------|----------|-------------|---------------|----------|
| **WebP** | ✅ Yes (Q=100) | Excellent (50-99%) | Modern browsers/apps | General purpose, web |
| **PNG** | ✅ Always | Good (40-70%) | Universal | Archives, editing |
| **JPEG** | ❌ No | Very Good (70-95%) | Universal | Photos, web legacy |

### Quality Level Guide

#### Quality 100 (Maximum)
```bash
dotnet run -- --quality 100 --format webp
```
- **Compression:** 50-70%
- **Use case:** Archival, no quality loss acceptable
- **File size:** Largest
- **WebP Note:** Lossless mode at Q=100

#### Quality 90-95 (High)
```bash
dotnet run -- --quality 95 --format webp
```
- **Compression:** 70-85%
- **Use case:** High-quality assets for modding
- **File size:** Large
- **Visual difference:** Barely perceptible

#### Quality 85 (Recommended - Default)
```bash
dotnet run -- --quality 85 --format webp
```
- **Compression:** 85-99%
- **Use case:** General purpose, excellent quality
- **File size:** Medium
- **Visual difference:** Imperceptible in most cases

#### Quality 75 (Balanced)
```bash
dotnet run -- --quality 75 --format webp
```
- **Compression:** 90-99%
- **Use case:** Web delivery, mobile apps
- **File size:** Small
- **Visual difference:** Minor in detailed textures

#### Quality 60-70 (Aggressive)
```bash
dotnet run -- --quality 60 --format webp
```
- **Compression:** 95-99%
- **Use case:** Thumbnails, previews
- **File size:** Very small
- **Visual difference:** Noticeable on close inspection

## Batch Processing Script

### Linux/macOS
```bash
#!/bin/bash
# extract_all_categories.sh

categories=("Weapons" "Armor" "Consumables" "Materials" "Creatures")

for category in "${categories[@]}"; do
    echo "Extracting $category..."
    dotnet run -- --filter "$category" --output "./output/$category"
done

echo "All categories extracted!"
```

### Windows (PowerShell)
```powershell
# extract_all_categories.ps1

$categories = @("Weapons", "Armor", "Consumables", "Materials", "Creatures")

foreach ($category in $categories) {
    Write-Host "Extracting $category..."
    dotnet run -- --filter $category --output "./output/$category"
}

Write-Host "All categories extracted!"
```

## Integration Examples

### Python Data Pipeline
```python
import subprocess
import json
from pathlib import Path

# Run extraction
subprocess.run([
    "dotnet", "run", "--",
    "--filter", "Items/Weapons",
    "--output", "./extracted"
])

# Process JSON files
for json_file in Path("extracted").rglob("*.json"):
    with open(json_file) as f:
        data = json.load(f)
        # Process game data
        print(f"Found item: {data.get('Name', 'Unknown')}")
```

### Node.js Asset Manager
```javascript
const { exec } = require('child_process');
const fs = require('fs');
const path = require('path');

// Run extraction
exec('dotnet run -- --output ./extracted', (error, stdout, stderr) => {
    if (error) {
        console.error(`Error: ${error}`);
        return;
    }
    
    // Find all icons
    const icons = [];
    function findIcons(dir) {
        fs.readdirSync(dir).forEach(file => {
            const fullPath = path.join(dir, file);
            if (fs.statSync(fullPath).isDirectory()) {
                findIcons(fullPath);
            } else if (file.endsWith('.webp')) {
                icons.push(fullPath);
            }
        });
    }
    
    findIcons('./extracted');
    console.log(`Found ${icons.length} icons`);
});
```

## Automated Update Workflow

### Git-Based Asset Tracking
```bash
#!/bin/bash
# update_assets.sh - Track asset changes over time

DATE=$(date +%Y%m%d)
OUTPUT_DIR="./extractions/$DATE"

# Extract assets
dotnet run -- --output "$OUTPUT_DIR"

# Initialize git repo if needed
cd extractions
git init 2>/dev/null
git add "$DATE"
git commit -m "Asset extraction: $DATE"

# Compare with previous extraction
PREV_DIR=$(ls -d */ | sort -r | sed -n 2p)
if [ -n "$PREV_DIR" ]; then
    echo "Changes since $PREV_DIR:"
    diff -qr "$PREV_DIR" "$DATE" | head -20
fi
```

## Performance Tuning

### Parallel Processing (Future Enhancement)
```csharp
// Potential parallel processing implementation
Parallel.ForEach(filesToExtract, new ParallelOptions { MaxDegreeOfParallelism = 4 }, file => {
    ProcessFile(file);
});
```

### Memory Optimization
```csharp
// Process files in batches to reduce memory usage
const int BATCH_SIZE = 1000;
for (int i = 0; i < filesToExtract.Count; i += BATCH_SIZE) {
    var batch = filesToExtract.Skip(i).Take(BATCH_SIZE);
    ProcessBatch(batch);
    GC.Collect(); // Force garbage collection between batches
}
```

## Output File Statistics

### Generate Report
```bash
#!/bin/bash
# stats.sh - Generate extraction statistics

OUTPUT_DIR="./output"

echo "=== Extraction Statistics ==="
echo "Total files:     $(find $OUTPUT_DIR -type f | wc -l)"
echo "JSON files:      $(find $OUTPUT_DIR -name '*.json' | wc -l)"
echo "WebP images:     $(find $OUTPUT_DIR -name '*.webp' | wc -l)"
echo "Total size:      $(du -sh $OUTPUT_DIR | cut -f1)"
echo ""
echo "Top 10 largest files:"
find $OUTPUT_DIR -type f -exec ls -lh {} \; | sort -k5 -hr | head -10 | awk '{print $5 "\t" $9}'
```
