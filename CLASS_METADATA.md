# Class Metadata Export

Extract detailed class structure metadata from Unreal Engine assets for database schema generation, API development, and understanding game object hierarchies.

## What is Class Metadata?

Class metadata includes information about UE classes that is **embedded in game files** but not the actual C++ source code:

### What You GET ✅
- **Property names and types** (e.g., `Damage: float`, `WeaponName: string`)
- **Class hierarchies** (parent classes, inheritance chains)
- **Default property values** (as set in the game)
- **Property categories and organization**
- **Data types** (primitives, arrays, structs, references)
- **Database schema suggestions** (auto-generated SQL types)

### What You DON'T GET ❌
- C++ source code or function implementations
- Game logic or algorithms
- Compiled binary code
- Private/protected implementation details

## Usage

### Basic Export

```bash
# Extract class metadata along with JSON
dotnet run -- --class-metadata --output "./output"
```

This creates additional `*_metadata.json` files alongside regular `.json` exports.

### Common Use Cases

```bash
# Items only with metadata
dotnet run -- --filter "Items/" --class-metadata --output "./items_meta"

# Weapons metadata for database schema
dotnet run -- --filter "Weapons/" --class-metadata --no-icons --output "./weapons_schema"

# All blueprints metadata (no textures)
dotnet run -- --class-metadata --no-icons --no-textures --output "./blueprints_meta"
```

## Output Format

### Standard JSON vs Metadata JSON

**Regular JSON** (`BP_Weapon.json`):
- Full object properties and values
- Includes all nested data
- Large file size
- Complete game data

**Metadata JSON** (`BP_Weapon_metadata.json`):
- Class structure and schema
- Property definitions and types
- Inheritance information
- Compact, focused on structure

### Example Output

**File:** `BP_AK47_metadata.json`

```json
[
  {
    "ClassName": "BP_Weapon_AK47",
    "PackagePath": "ConZ_Files/Items/Weapons",
    "ParentClass": "BP_Weapon_Rifle_Base",
    "ClassType": "BlueprintClass",
    "Properties": [
      {
        "Name": "WeaponDamage",
        "Type": "FloatProperty",
        "Category": "Combat",
        "DefaultValue": 45.0,
        "IsArray": false,
        "IsOptional": false
      },
      {
        "Name": "FireRate",
        "Type": "FloatProperty",
        "Category": "Combat",
        "DefaultValue": 600.0,
        "IsArray": false,
        "IsOptional": false
      },
      {
        "Name": "MagazineSize",
        "Type": "IntProperty",
        "Category": "Ammo",
        "DefaultValue": 30,
        "IsArray": false,
        "IsOptional": false
      },
      {
        "Name": "AmmoTypes",
        "Type": "ArrayProperty",
        "Category": "Ammo",
        "DefaultValue": ["762x39mm", "762x39mm_Tracer"],
        "IsArray": true,
        "IsOptional": false,
        "InnerType": "StringProperty"
      }
    ],
    "DefaultValues": {
      "WeaponDamage": 45.0,
      "FireRate": 600.0,
      "MagazineSize": 30,
      "EffectiveRange": 400.0,
      "_SchemaSummary": {
        "TableName": "Weapon_AK47",
        "ParentTable": "Weapon_Rifle_Base",
        "Columns": [
          {
            "Name": "WeaponDamage",
            "SqlType": "REAL",
            "IsNullable": false,
            "DefaultValue": "45.0"
          },
          {
            "Name": "FireRate",
            "SqlType": "REAL",
            "IsNullable": false,
            "DefaultValue": "600.0"
          },
          {
            "Name": "MagazineSize",
            "SqlType": "INTEGER",
            "IsNullable": false,
            "DefaultValue": "30"
          },
          {
            "Name": "AmmoTypes",
            "SqlType": "JSON",
            "IsNullable": false,
            "DefaultValue": null
          }
        ]
      }
    },
    "Tags": ["Weapon", "Firearm", "Automatic"],
    "ExtractedAt": "2026-01-07T10:30:00Z"
  }
]
```

## Practical Applications

### 1. Database Schema Generation

Use the `_SchemaSummary` to auto-generate SQL:

```python
import json

with open('BP_Weapon_AK47_metadata.json') as f:
    metadata = json.load(f)[0]

schema = metadata['DefaultValues']['_SchemaSummary']
table_name = schema['TableName']

# Generate CREATE TABLE statement
sql = f"CREATE TABLE {table_name} (\n  id INTEGER PRIMARY KEY"

for col in schema['Columns']:
    nullable = "" if col['IsNullable'] else " NOT NULL"
    default = f" DEFAULT {col['DefaultValue']}" if col['DefaultValue'] else ""
    sql += f",\n  {col['Name']} {col['SqlType']}{nullable}{default}"

sql += "\n);"
print(sql)
```

**Output:**
```sql
CREATE TABLE Weapon_AK47 (
  id INTEGER PRIMARY KEY,
  WeaponDamage REAL NOT NULL DEFAULT 45.0,
  FireRate REAL NOT NULL DEFAULT 600.0,
  MagazineSize INTEGER NOT NULL DEFAULT 30,
  AmmoTypes JSON NOT NULL
);
```

### 2. API Model Generation

Generate TypeScript interfaces:

```python
def generate_typescript(metadata):
    class_name = metadata['ClassName']
    properties = metadata['Properties']
    
    ts = f"export interface {class_name} {{\n"
    
    for prop in properties:
        ts_type = map_to_typescript(prop['Type'], prop['IsArray'])
        optional = "?" if prop['IsOptional'] else ""
        ts += f"  {prop['Name']}{optional}: {ts_type};\n"
    
    ts += "}"
    return ts

def map_to_typescript(ue_type, is_array):
    base_type = {
        'IntProperty': 'number',
        'FloatProperty': 'number',
        'BoolProperty': 'boolean',
        'StringProperty': 'string',
        'NameProperty': 'string',
    }.get(ue_type, 'any')
    
    return f"{base_type}[]" if is_array else base_type
```

### 3. Class Hierarchy Analysis

Map out inheritance chains:

```python
def build_hierarchy(metadata_files):
    classes = {}
    
    for file in metadata_files:
        with open(file) as f:
            for cls in json.load(f):
                classes[cls['ClassName']] = cls['ParentClass']
    
    # Find all descendants of a class
    def get_descendants(class_name, classes):
        return [k for k, v in classes.items() if v == class_name]
    
    # Example: Find all weapon types
    weapon_base = "BP_Weapon_Base"
    weapons = get_descendants(weapon_base, classes)
    print(f"Weapon types: {weapons}")
```

### 4. Data Validation

Validate extracted data against schema:

```python
def validate_item(item_data, metadata):
    for prop in metadata['Properties']:
        prop_name = prop['Name']
        prop_type = prop['Type']
        
        if prop_name in item_data:
            value = item_data[prop_name]
            
            # Type checking
            if prop_type == 'IntProperty' and not isinstance(value, int):
                print(f"Warning: {prop_name} should be int, got {type(value)}")
            elif prop_type == 'FloatProperty' and not isinstance(value, (int, float)):
                print(f"Warning: {prop_name} should be float, got {type(value)}")
```

### 5. Property Tracking Across Updates

Compare metadata between game versions:

```bash
# Extract current version
dotnet run -- --class-metadata --filter "Items/" --output "./v1.5_meta"

# After game update
dotnet run -- --class-metadata --filter "Items/" --output "./v1.6_meta"

# Compare (using diff or custom script)
diff -r v1.5_meta v1.6_meta
```

## Performance

- **Minimal overhead:** Metadata extraction adds ~5-10% to processing time
- **File size:** Metadata files are typically 10-50% size of full JSON
- **Memory:** No additional memory overhead during extraction

## Tips

1. **Focus extraction:** Use `--filter` to only extract relevant classes
2. **Combine with JSON:** Use both `--class-metadata` and regular JSON for complete data
3. **Track versions:** Keep metadata for each game version to track changes
4. **Automate schema generation:** Build scripts to auto-generate database schemas

## Limitations

- **Complex types:** Deeply nested structures may be simplified
- **References:** Object references exported as paths, not full objects
- **Custom types:** Game-specific types may need manual interpretation
- **Functions:** No function signatures or implementations (C++ only)

## Related Documentation

- [README.md](README.md) - Main project documentation
- [EXAMPLES.md](EXAMPLES.md) - Usage examples
- [wiki_docs/Getting-Started.md](wiki_docs/Getting-Started.md) - CUE4Parse basics
