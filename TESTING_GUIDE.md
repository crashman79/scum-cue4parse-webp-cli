# Testing GUI and TUI

This guide explains how to test the GUI and TUI versions separately.

## Quick Test Commands

### Test GUI Version
```bash
# Force GUI mode
dotnet run -- --gui --output /path/to/output [other args]

# Or build and run
dotnet build
./bin/Debug/net8.0/CUE4ParseCLI --gui --output /path/to/output
```

### Test TUI Version
```bash
# TUI is default for interactive terminals, but you can force it
dotnet run -- --output /path/to/output [other args]

# Or explicitly disable GUI
dotnet run -- --no-ui --output /path/to/output
```

## Using Separate Entry Points

If you want to use the dedicated entry points (`ProgramGUI.cs` and `ProgramTUI.cs`), you need to set the `StartupObject` in the project file.

### Method 1: Edit csproj directly

**For GUI testing:**
```xml
<PropertyGroup>
  <StartupObject>CUE4ParseCLI.ProgramGUI</StartupObject>
</PropertyGroup>
```

**For TUI testing:**
```xml
<PropertyGroup>
  <StartupObject>CUE4ParseCLI.ProgramTUI</StartupObject>
</PropertyGroup>
```

### Method 2: Use MSBuild properties (recommended for testing)

```bash
# Build with GUI entry point
dotnet build -p:StartupObject=CUE4ParseCLI.ProgramGUI

# Build with TUI entry point  
dotnet build -p:StartupObject=CUE4ParseCLI.ProgramTUI
```

## Current Project Structure

```
Core/              → Shared code (IProgressUI, utilities)
UI/GUI/            → GUI code (AvaloniaGuiUI)
UI/TUI/            → TUI code (ConsoleUI)
Program.cs         → Main extraction logic (uses IProgressUI)
ProgramGUI.cs      → GUI entry point
ProgramTUI.cs      → TUI entry point
```

## Auto-Detection Behavior

By default, `Program.cs` auto-detects the UI mode:
- **GUI**: If `DISPLAY` or `WAYLAND_DISPLAY` is set (X11/Wayland available)
- **TUI**: If interactive terminal (not redirected)
- **Machine-readable**: If output is redirected (automation)

You can override with flags:
- `--gui` - Force GUI mode
- `--no-ui` or `--machine-readable` - Disable UI (machine-readable output)
