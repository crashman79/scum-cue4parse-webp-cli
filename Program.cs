using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Sounds;
using Newtonsoft.Json;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using SkiaSharp;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using CUE4ParseCLI.Core;
using CUE4ParseCLI.UI.GUI;
using CUE4ParseCLI.UI.TUI;

namespace CUE4ParseCLI
{
    // Path mapping data structure
    public class PathMapping
    {
        public string OriginalPath { get; set; } = "";
        public string MirrorPath { get; set; } = "";
        public string OrganizedPath { get; set; } = "";
        public string AssetType { get; set; } = "";
        public string FileName { get; set; } = "";
    }
    
    
    class Program
    {
        // SCUM Configuration
        private static string? SCUM_PAK_DIR = null; // Must be provided via --input or --pak-dir
        // The AES key is not distributed with this repository. Set SCUM_AES_KEY
        // to the key for the game build you are extracting.
        private static string GetScumAesKey()
        {
            var key = Environment.GetEnvironmentVariable("SCUM_AES_KEY");
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException(
                    "SCUM_AES_KEY is not set. Export it with your own key for the game build you are extracting.");
            }
            return key;
        }

        // Default output directory. Override with SCUM_OUTPUT_DIR.
        private static string GetOutputDir()
        {
            return Environment.GetEnvironmentVariable("SCUM_OUTPUT_DIR")
                ?? Path.Combine(AppContext.BaseDirectory, "Output", "Exports");
        }
        
        // Helper method to update progress UI (consolidates GUI and TUI updates)
        private static void UpdateProgressUI(long progress, long totalFiles, long jsonCount, long textureSuccess, long textureTotal, 
            long meshCount, long soundCount, long errorCount, long jsonSkipped, long textureSkipped, long meshSkipped, long soundSkipped,
            double rate, TimeSpan eta, TimeSpan elapsed, double memoryGB, string statusMessage, long webpQueued = 0, long webpCompleted = 0)
        {
            if (_progressUI == null) return;
            
            _progressUI.Progress = progress;
            _progressUI.TotalFiles = totalFiles;
            _progressUI.JsonCount = jsonCount;
            _progressUI.TextureSuccess = textureSuccess;
            _progressUI.TextureTotal = textureTotal;
            _progressUI.MeshCount = meshCount;
            _progressUI.SoundCount = soundCount;
            _progressUI.ErrorCount = errorCount;
            _progressUI.JsonSkipped = jsonSkipped;
            _progressUI.TextureSkipped = textureSkipped;
            _progressUI.MeshSkipped = meshSkipped;
            _progressUI.SoundSkipped = soundSkipped;
            _progressUI.Rate = rate;
            _progressUI.ETA = eta;
            _progressUI.Elapsed = elapsed;
            _progressUI.MemoryGB = memoryGB;
            _progressUI.StatusMessage = statusMessage;
            
            if (_webpConversionTask != null && _progressUI.WebPAsyncEnabled)
            {
                _progressUI.WebPQueued = webpQueued;
                _progressUI.WebPCompleted = webpCompleted;
            }
            
            _progressUI.RequestUpdate();
        }
        
        // Performance optimization: Cache created directories to avoid repeated filesystem calls
        // Using ConcurrentDictionary as a thread-safe set (value is ignored)
        private static readonly ConcurrentDictionary<string, byte> CreatedDirectories = new();
        
        // Memory pressure threshold (set during initialization)
        private static long MemoryThreshold = 0;
        
        // Texture processing semaphore: Limit concurrent texture processing to prevent memory explosion
        // With parallel threads, allowing all to process textures simultaneously can cause memory spikes
        // Limiting concurrent textures prevents OOM while allowing JSON processing to continue
        // Reduced to 2 for better memory control on low-RAM systems (was 3 for high-RAM systems)
        private static readonly SemaphoreSlim TextureProcessingSemaphore = new SemaphoreSlim(2, 2);
        
        // Timeout for texture processing semaphore to prevent deadlocks (60 seconds)
        // If all slots are held by stuck threads, new threads will timeout and skip texture processing
        private const int TextureProcessingTimeoutSeconds = 60;
        
        // Async WebP conversion: Extract-then-convert approach for better memory efficiency
        // Phase 1: Extract textures as PNG (fast, frees memory immediately)
        // Phase 2: Convert PNG → WebP in background (queue stores file paths, not bitmaps)
        // This eliminates SKBitmap memory accumulation and allows higher parallelism
        // Queue stores: (pngPath, webpPath, quality, verifyQuality) - minimal memory footprint
        private static readonly ConcurrentQueue<(string pngPath, string webpPath, int quality, bool verifyQuality)> WebPConversionQueue = new();
        private static readonly SemaphoreSlim WebPConversionSemaphore = new SemaphoreSlim(Environment.ProcessorCount, Environment.ProcessorCount);
        private static Task? _webpConversionTask = null;
        private static CancellationTokenSource _webpConversionCancellation = new();
        private static long _webpConversionQueued = 0;
        private static long _webpConversionCompleted = 0;
        // No queue size limit needed - file paths are lightweight (few bytes each vs MB for bitmaps)
        
        // WebP retry queue: WebP conversions that fail or timeout are queued for retry
        // Queue stores: (pngPath, webpPath, quality, verifyQuality, retryCount)
        // Retry count prevents infinite loops on problematic conversions
        private static readonly ConcurrentQueue<(string pngPath, string webpPath, int quality, bool verifyQuality, int retryCount)> WebPRetryQueue = new();
        private static long _webpRetryQueued = 0;
        private const int MAX_WEBP_RETRIES = 3; // Limit retries to prevent infinite loops
        
        // Texture retry queue: Textures that timeout waiting for semaphore are queued for retry
        // Since textures are written to disk immediately (PNG), we can retry them later without memory pressure
        // Queue stores: (filePath, textureName, textureOutputDir, format, quality, verifyQuality, retryCount)
        // Retry count prevents infinite loops on problematic textures
        private static readonly ConcurrentQueue<(string filePath, string textureName, string textureOutputDir, string format, int quality, bool verifyQuality, int retryCount)> TextureRetryQueue = new();
        private static long _textureRetryQueued = 0;
        private const int MAX_TEXTURE_RETRIES = 3; // Limit retries to prevent infinite loops
        
        // Mesh processing semaphore: Limit concurrent mesh processing to prevent memory spikes
        // Meshes reload packages and create Exporter objects, which can use significant memory
        // Limit to 2 concurrent meshes to coordinate with WebP queue processing
        private static readonly SemaphoreSlim MeshProcessingSemaphore = new SemaphoreSlim(2, 2);
        
        // Mesh extraction queue: Defer mesh extraction until end of processing
        // Meshes are slow (200-1000ms) and can hang, so queue them and process at the end
        // This allows fast assets (JSON/textures) to complete first, then process all meshes together
        // Queue stores metadata only (file path, mesh name, output dir) - packages are reloaded at the end
        private static readonly ConcurrentQueue<(string filePath, string meshName, string meshOutputDir, bool mirrorStructure)> MeshExtractionQueue = new();
        private static long _meshQueued = 0;
        
        // Mesh retry queue: Meshes that timeout during export are queued for retry
        // Queue stores: (filePath, meshName, meshOutputDir, mirrorStructure, retryCount)
        // Retry count prevents infinite loops on problematic meshes
        private static readonly ConcurrentQueue<(string filePath, string meshName, string meshOutputDir, bool mirrorStructure, int retryCount)> MeshRetryQueue = new();
        private static long _meshRetryQueued = 0;
        private const int MAX_MESH_RETRIES = 3; // Limit retries to prevent infinite loops
        
        // Timeout for package loading to prevent hangs on corrupted/problematic files (30 seconds)
        private const int PackageLoadTimeoutSeconds = 30;
        
        // Timeout for mesh export to prevent hangs on problematic meshes (60 seconds)
        // Some meshes can cause TryWriteToDir to hang indefinitely
        private const int MeshExportTimeoutSeconds = 60;
        
        // Path mapping collection (for generating mapping file)
        private static readonly ConcurrentBag<PathMapping> PathMappings = new();
        
        // Workload model for ETA estimation (built from header scan)
        
        // Log file writers for exports and warnings/errors
        private static StreamWriter? _successLogWriter;
        private static StreamWriter? _warningsErrorsLogWriter;
        
        // UI abstraction - shared across GUI and TUI versions
        private static IProgressUI? _progressUI;
        private static bool _useUI = true; // Human-readable UI is default
        private static bool _useGui = false; // GUI mode (Avalonia)
        private static bool _machineReadable = false; // Automation-friendly output
        private static CancellationTokenSource? _extractionCancellation;
        
        // Public entry point - can be called from other Main methods (ProgramTUI, ProgramGUI)
        public static void Run(string[] args)
        {
            MainInternal(args);
        }
        
        // Main entry point (used when this is the StartupObject)
        static void Main(string[] args)
        {
            MainInternal(args);
        }
        
        // Internal implementation shared by all entry points
        static void MainInternal(string[] args)
        {
            // Set up global exception handler to catch unhandled exceptions
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                Log.Error($"Unhandled exception: {ex?.Message ?? "Unknown error"}");
                Log.Error($"Exception type: {ex?.GetType().Name ?? "Unknown"}");
                if (ex?.StackTrace != null)
                {
                    Log.Error($"Stack trace: {ex.StackTrace}");
                }
                Environment.ExitCode = 247; // Set exit code for unhandled exceptions
            };
            
            // Ensure invariant globalization mode (fallback for older builds or if not set in csproj)
            // This prevents ICU library dependency issues on systems without ICU installed
            // Invariant mode is fine for file extraction (no culture-specific formatting needed)
            Environment.SetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", "true");
            
            // Show help if requested (must be first - before any initialization)
            if (args.Contains("--help") || args.Contains("-h"))
            {
                ShowHelp();
                return;
            }
            
            // Check for explicit UI mode flags
            bool explicitGui = args.Contains("--gui");
            bool explicitTui = args.Contains("--tui");
            bool explicitNoUi = args.Contains("--no-ui");
            bool explicitMachineReadable = args.Contains("--machine-readable") || args.Contains("--automation");
            
            // Auto-detect environment
            bool hasDisplay = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) ||
                             !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
            bool isInteractiveTerminal = !Console.IsOutputRedirected && Console.IsInputRedirected == false;
            
            // Default behavior:
            // - If explicit flags provided, use them (--tui or --gui override auto-detection)
            // - If X11/Wayland available and no explicit flags, use GUI (true windowing system)
            // - If interactive terminal and no display, use TUI (text-based but visual)
            // - Machine-readable only when explicitly requested (for automation)
            
            if (explicitTui)
            {
                // Force TUI mode (terminal UI) - overrides auto-detection
                _useGui = false;
                _machineReadable = false;
                _useUI = true;
            }
            else if (explicitGui)
            {
                _useGui = true;
                _machineReadable = false;
                _useUI = false;
            }
            else if (explicitNoUi || explicitMachineReadable)
            {
                _useGui = false;
                _machineReadable = true;
                _useUI = false;
            }
            else if (hasDisplay)
            {
                // Auto-enable GUI when X11/Wayland is available (true windowing system)
                _useGui = true;
                _machineReadable = false;
                _useUI = false;
            }
            else if (isInteractiveTerminal)
            {
                // Default: TUI for interactive terminals (text-based but visual)
                _useGui = false;
                _machineReadable = false;
                _useUI = true;
            }
            else
            {
                // Non-interactive (piped/redirected): machine-readable for automation
                _useGui = false;
                _machineReadable = true;
                _useUI = false;
            }
            
            // Setup logging - write to file for detailed logs, console for UI
            var logConfig = new LoggerConfiguration();
            
            // Always log to file for detailed tracking
            var logFile = $"extraction_{DateTime.Now:yyyyMMdd_HHmmss}.log";
            logConfig.WriteTo.File(logFile, outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");
            
            // Only write to console if UI is disabled AND not using GUI, otherwise UI will handle display
            if (!_useUI && !_useGui)
            {
                // Filter out known harmless CUE4Parse warnings (missing native packages are expected)
                logConfig.WriteTo.Console(
                    theme: AnsiConsoleTheme.Literate,
                    restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information,
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}"
                );
            }
            
            // Suppress CUE4Parse's internal "Missing native package" errors (they're expected and non-fatal)
            // These come from CUE4Parse library logging, not our code
            // The textures/meshes can still be extracted even with missing material references
            Log.Logger = logConfig
                .MinimumLevel.Information()
                .Filter.ByExcluding(logEvent => 
                    logEvent.Level == Serilog.Events.LogEventLevel.Error && 
                    logEvent.MessageTemplate.Text.Contains("Missing native package"))
                .CreateLogger();
            
            // Parse PAK directory (input) - required, no default
            int inputIndex = Array.IndexOf(args, "--input");
            int pakDirIndex = Array.IndexOf(args, "--pak-dir");
            if (inputIndex >= 0 && inputIndex < args.Length - 1)
            {
                SCUM_PAK_DIR = args[inputIndex + 1];
            }
            else if (pakDirIndex >= 0 && pakDirIndex < args.Length - 1)
            {
                SCUM_PAK_DIR = args[pakDirIndex + 1];
            }
            
            // Validate that PAK directory was provided
            if (string.IsNullOrEmpty(SCUM_PAK_DIR))
            {
                Console.WriteLine("ERROR: PAK directory is required. Use --input <path> or --pak-dir <path>");
                Console.WriteLine("Example: CUE4ParseCLI --input /path/to/SCUM/Content/Paks --output ./exports");
                return;
            }
            
            // Parse output directory first (needed for UI initialization)
            string outputDir = GetOutputDir();
            int outputIndex = Array.IndexOf(args, "--output");
            if (outputIndex >= 0 && outputIndex < args.Length - 1)
            {
                outputDir = args[outputIndex + 1];
            }
            
            // Check for search mode early (before UI initialization)
            int searchIndex = Array.IndexOf(args, "--search");
            bool isSearchMode = searchIndex >= 0 && searchIndex < args.Length - 1;
            bool searchAndExtract = isSearchMode && args.Contains("--extract");
            
            if (isSearchMode && outputIndex < 0 && !searchAndExtract)
            {
                outputDir = Path.GetTempPath(); // Use temp directory for logs if not specified (only for search-only)
            }
            
            // Disable UI for search-only mode (cleaner output), but keep enabled for search-and-extract
            if (isSearchMode && !searchAndExtract)
            {
                _useUI = false;
                _machineReadable = true;
            }
            
            // Log mode information (to file, console will show UI or traditional logging)
            if (_machineReadable)
            {
                Log.Information("Mode: Machine-readable output (automation-friendly)");
            }
            else if (_useGui)
            {
                Log.Information("Mode: GUI (Avalonia) - Auto-detected X11/Wayland display");
            }
            else
            {
                Log.Information("Mode: Human-readable console UI - Use --gui for GUI or --machine-readable for automation");
            }
            
            // Initialize UI based on mode
            if (_useGui)
            {
                // Use Avalonia for true GUI (native windows, buttons, text fields)
                _progressUI = new AvaloniaGuiUI();
                _progressUI.OutputDirectory = outputDir;
            }
            else if (_useUI)
            {
                _progressUI = new ConsoleUI();
                _progressUI.OutputDirectory = outputDir;
                // Don't clear console - let TUI handle cursor positioning
            }

            // Calculate memory limits based on available system RAM
            // Adaptive limits: Conservative for low-RAM systems (<16GB), more lenient for high-RAM systems
            // This prevents OOM kills on systems with limited RAM while allowing better performance on high-RAM systems
            var gcInfo = GC.GetGCMemoryInfo();
            long totalMemoryBytes = gcInfo.TotalAvailableMemoryBytes;
            long systemMemoryGB = totalMemoryBytes / (1024L * 1024 * 1024);
            
            long maxMemoryBytes;
            string memoryLimitDesc;
            // Explicit memory target override (bypasses .NET's RAM detection, which can mis-report
            // on some hosts - e.g. reporting 4GB on a 12GB box). Value is GB; used directly as the cap.
            var memEnv = Environment.GetEnvironmentVariable("SCUM_EXTRACTION_MEMORY_GB");
            if (!string.IsNullOrWhiteSpace(memEnv) && double.TryParse(memEnv, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var memGb) && memGb > 0)
            {
                maxMemoryBytes = (long)(memGb * 1024L * 1024 * 1024);
                memoryLimitDesc = $"explicit {memGb:F1}GB cap (SCUM_EXTRACTION_MEMORY_GB)";
                Log.Information($"Memory target override SCUM_EXTRACTION_MEMORY_GB={memGb:F1} GB applied (detected {systemMemoryGB:F1} GB)");
            }
            else if (systemMemoryGB <= 16)
            {
                // Dedicated extraction host (<=16GB but ample RAM): allow up to 8GB (50% of RAM) so
                // large PAK sets extract faster without aggressive-GC stalls on a 12GB host.
                maxMemoryBytes = Math.Min(totalMemoryBytes / 2, 8L * 1024 * 1024 * 1024);
                memoryLimitDesc = "50% of RAM or 8GB max (dedicated extraction host)";
            }
            else
            {
                // High-RAM systems (>16GB): Use 50% of RAM or 8GB max (more lenient for performance)
                maxMemoryBytes = Math.Min(totalMemoryBytes / 2, 8L * 1024 * 1024 * 1024);
                memoryLimitDesc = "50% of RAM or 8GB max";
            }
            
            // Configure GC for sustained low latency (better for long-running processes)
            // This provides smoother processing with fewer long pauses compared to Batch mode
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            
            // Memory pressure threshold (70% of limit) - trigger GC when exceeded
            // More conservative (70% vs 85%) to provide larger safety margin and prevent OOM kills
            // On 8GB system with 2GB limit: threshold = 1.4GB (30% safety margin)
            MemoryThreshold = (long)(maxMemoryBytes * 0.70);
            
            // Log memory configuration
            Log.Information($"System RAM: {systemMemoryGB:F1} GB");
            Log.Information($"Memory limit: {maxMemoryBytes / (1024.0 * 1024 * 1024):F1} GB ({memoryLimitDesc})");
            Log.Information($"GC Mode: SustainedLowLatency (optimized for long-running processes)");
            Log.Information($"Memory threshold: {MemoryThreshold / (1024.0 * 1024 * 1024):F1} GB (GC will trigger at 70% of limit)");
            Log.Information($"Texture processing: Limited to 2 concurrent textures (prevents memory spikes on low-RAM systems)");

            // Parse command-line arguments
            bool extractTextures = args.Contains("--textures") || args.Contains("-t");
            bool extractJson = !args.Contains("--no-json");
            bool extractIcons = !args.Contains("--no-icons"); // Extract textures from asset properties (default: true)
            
            // Output directory already parsed above for UI initialization
            
            // Parse filter parameter (path substring to match)
            string filter = null;
            int filterIndex = Array.IndexOf(args, "--filter");
            if (filterIndex >= 0 && filterIndex < args.Length - 1)
            {
                filter = args[filterIndex + 1];
            }
            
            // Parse compression quality (0-100, default: 85)
            int quality = 85;
            int qualityIndex = Array.IndexOf(args, "--quality");
            if (qualityIndex >= 0 && qualityIndex < args.Length - 1)
            {
                if (int.TryParse(args[qualityIndex + 1], out int parsedQuality))
                {
                    quality = Math.Clamp(parsedQuality, 0, 100);
                }
                else
                {
                    Log.Warning($"Invalid quality value, using default: {quality}");
                }
            }
            
            // Parse export format (webp, png, jpeg, default: webp)
            string format = "webp";
            int formatIndex = Array.IndexOf(args, "--format");
            if (formatIndex >= 0 && formatIndex < args.Length - 1)
            {
                string requestedFormat = args[formatIndex + 1].ToLower();
                if (requestedFormat == "webp" || requestedFormat == "png" || requestedFormat == "jpeg" || requestedFormat == "jpg")
                {
                    format = requestedFormat == "jpg" ? "jpeg" : requestedFormat;
                }
                else
                {
                    Log.Warning($"Invalid format '{requestedFormat}', using default: {format}");
                }
            }
            
            // Parse verify flag (--verify: perform quality verification on first 10 images)
            bool verifyQuality = args.Contains("--verify");
            // Note: verifyCount is now a long field declared with other counters for thread safety
            
            // Parse class metadata flag (--class-metadata: export class structure metadata)
            bool exportClassMetadata = args.Contains("--class-metadata");
            
            // Parse icons-unfiltered flag (--icons-unfiltered: extract all icons regardless of filter)
            // Keep --no-filter-icons for backward compatibility
            bool noFilterIcons = args.Contains("--no-filter-icons") || args.Contains("--icons-unfiltered");
            
            // Parse mirror-structure flag (--mirror-structure: keep game folder structure instead of organizing by type)
            bool mirrorStructure = args.Contains("--mirror-structure");
            
            // Parse hybrid-structure flag (--hybrid-structure: organize by type within each major folder)
            bool hybridStructure = args.Contains("--hybrid-structure");
            
            // Parse smart-hybrid-structure flag (--smart-hybrid: preserve 2-3 levels of hierarchy)
            bool smartHybridStructure = args.Contains("--smart-hybrid");
            
            // If smart-hybrid is specified, it overrides hybrid
            if (smartHybridStructure) hybridStructure = false;
            
            bool organizeByType = !mirrorStructure && !hybridStructure && !smartHybridStructure; // Default: organize by type
            
            // Parse extract-all flag (--extract-all or --all: extract all asset types)
            bool extractAll = args.Contains("--extract-all") || args.Contains("--all");
            
            // Parse individual extraction flags (default to true, use --no-* to disable)
            bool extractMeshes = !args.Contains("--no-meshes");
            bool extractSounds = !args.Contains("--no-sounds");
            
            // Parse map-structure flag (--map-structure: generate structure mapping file without extraction)
            bool mapStructure = args.Contains("--map-structure");
            
            // Parse search flag (--search <pattern>: search for files in PAKs without extraction)
            string? searchPattern = null;
            if (isSearchMode)
            {
                searchPattern = args[searchIndex + 1];
            }
            
            // For search-and-extract mode, output directory is required
            if (isSearchMode && searchAndExtract && outputIndex < 0)
            {
                Log.Error("Output directory (--output) is required when using --search --extract");
                Console.WriteLine("ERROR: Output directory (--output) is required when using --search --extract");
                Console.WriteLine("Usage: CUE4ParseCLI --search <pattern> --extract --output <path> [other options]");
                return;
            }
            
            // Parse resume flag (--resume or --skip-existing: skip files that already exist)
            // --fast-resume: Skip validation, just check file existence (faster, less CPU/RAM)
            bool resume = args.Contains("--resume") || args.Contains("--skip-existing");
            bool fastResume = args.Contains("--fast-resume") || args.Contains("--skip-validation");
            if (fastResume) resume = true; // Fast resume implies regular resume
            _fastResumeMode = fastResume; // Set global flag for validation functions

            Log.Information("=== CUE4Parse CLI - SCUM Extractor ===");
            Log.Information($"PAK Directory: {SCUM_PAK_DIR}"); // Validated above, non-null at this point
            Log.Information($"Output Directory: {outputDir}");
            Log.Information($"Extract JSON: {extractJson}");
            Log.Information($"Extract Textures: {extractTextures}");
            Log.Information($"Extract Icons: {extractIcons}");
            Log.Information($"Export Format: {format.ToUpper()}");
            Log.Information($"Compression Quality: {quality}");
            if (verifyQuality)
            {
                Log.Information("Quality Verification: Enabled (first 10 images)");
            }
            if (exportClassMetadata)
            {
                Log.Information("Class Metadata Export: Enabled");
            }
            if (smartHybridStructure)
            {
                Log.Information("Output Organization: Smart Hybrid (preserve 2-3 levels, type subfolders) [RECOMMENDED]");
            }
            else if (hybridStructure)
            {
                Log.Information("Output Organization: Hybrid (type subfolders within first-level folder)");
            }
            else if (mirrorStructure)
            {
                Log.Information("Output Organization: Mirror game structure");
            }
            else
            {
                Log.Information("Output Organization: By asset type (default)");
            }
            if (extractAll)
            {
                Log.Information("Extract All Asset Types: Enabled (DataTables, Sounds, Meshes, etc.)");
            }
            if (filter != null)
            {
                Log.Information($"Path Filter: {filter}");
                if (noFilterIcons)
                {
                    Log.Information("  Filter applies to: Items only (icons/textures unfiltered)");
                }
                else
                {
                    Log.Information("  Filter applies to: Items, icons, and textures");
                }
            }
            if (mapStructure)
            {
                Log.Information("Mode: Generate structure mapping (no extraction)");
            }
            if (searchPattern != null)
            {
                if (searchAndExtract)
                {
                    Log.Information($"Mode: Search and extract (pattern: '{searchPattern}')");
                }
                else
                {
                    Log.Information($"Mode: Search mode (pattern: '{searchPattern}')");
                }
            }
            if (resume)
            {
                if (fastResume)
                {
                    Log.Information("Resume Mode: Fast (skipping validation, checking file existence only)");
                }
                else
                {
                    Log.Information("Resume Mode: Enabled (validating and skipping existing files)");
                }
                if (_progressUI != null)
                {
                    _progressUI!.ResumeMode = true;
                }
            }
            
            // Check if pak directory exists
            if (!Directory.Exists(SCUM_PAK_DIR!))
            {
                Log.Error($"PAK directory not found: {SCUM_PAK_DIR}");
                return;
            }

            // For search-only mode, skip output directory setup and log file initialization
            // But for search-and-extract mode, we need output directory setup
            if (!isSearchMode || (isSearchMode && searchAndExtract))
            {
                // Create output directory if it doesn't exist
                EnsureDirectory(outputDir);
                
                // Initialize log file writers in output directory root
                var successLogPath = Path.Combine(outputDir, "successful_exports.log");
                var warningsErrorsLogPath = Path.Combine(outputDir, "warnings_errors.log");
                _successLogWriter = new StreamWriter(successLogPath, append: false) { AutoFlush = true };
                _warningsErrorsLogWriter = new StreamWriter(warningsErrorsLogPath, append: false) { AutoFlush = true };
                Log.Information($"Export logs: {successLogPath}");
                Log.Information($"Warnings/Errors log: {warningsErrorsLogPath}");
            }
            
            // Clear path mappings collection for this run
            while (PathMappings.TryTake(out _)) { }
            
            try
            {
                // Initialize the file provider for SCUM (UE4.27)
                Log.Information("Initializing file provider...");
                var provider = new DefaultFileProvider(
                    new DirectoryInfo(SCUM_PAK_DIR!), 
                    SearchOption.TopDirectoryOnly, 
                    new VersionContainer(EGame.GAME_UE4_27)
                );

                // Initialize (scan PAK files) - This only indexes the PAK file tables, doesn't load contents
                // Similar to FModel's fast loading approach: only reads VFS index, not package contents
                Log.Information("Scanning PAK files...");
                provider.Initialize();
                Log.Information($"Found {provider.UnloadedVfs.Count} PAK files");

                // Snapshot the unloaded VFS readers IMMEDIATELY after Initialize() and BEFORE SubmitKey
                // or any access to provider.Files: SubmitKey / Files access lazily mounts every PAK,
                // which empties provider.UnloadedVfs. The snapshot drives pak_manifest.json.
                var pakReaders = provider.UnloadedVfs.ToList();

                // Submit AES key
                Log.Information("Submitting AES encryption key...");
                provider.SubmitKey(new FGuid(), new FAesKey(GetScumAesKey()));

                // Get file count (fast - just count keys without materializing the list)
                var totalFileCount = provider.Files.Count;
                Log.Information($"Total files indexed in PAKs: {totalFileCount}");

                // OPTIMIZATION: Build PAK location index to group files by archive
                // This allows us to process files from the same PAK together, reducing random I/O
                // Files from the same PAK are typically stored sequentially, so grouping improves disk read efficiency
                Log.Information("Building PAK location index for optimal I/O grouping...");
                var pakLocationIndex = BuildPakLocationIndex(provider, totalFileCount);

                // Phase 0: emit PAK manifests (pak_manifest.json + pak_asset_index.json) into the output root.
                EmitPakManifests(provider, pakReaders, outputDir, pakLocationIndex);
                Log.Information($"Indexed {pakLocationIndex.Count} unique PAK files");
                
                // If search mode, search for files directly on provider.Files.Keys (FModel-style: lazy enumeration)
                if (searchPattern != null)
                {
                    if (searchAndExtract)
                    {
                        // Search and extract matching files (search directly on provider.Files.Keys, FModel-style)
                        SearchAndExtractFiles(provider, searchPattern, totalFileCount, outputDir, extractJson, extractTextures, 
                            extractIcons, extractMeshes, extractSounds, extractAll, format, quality, verifyQuality, 
                            exportClassMetadata, smartHybridStructure, hybridStructure, mirrorStructure, resume, 
                            noFilterIcons, filter);
                        Log.Information("Search and extract complete!");
                        return;
                    }
                    else
                    {
                        // Search only (display results) - search directly without materializing full list
                        SearchFiles(provider, searchPattern, totalFileCount);
                        Log.Information("Search complete!");
                        return;
                    }
                }
                
                // For normal extraction, build file list efficiently using lazy enumeration (FModel-style)
                // Don't materialize all files - only query and materialize what we need
                Log.Information("Building file list for extraction...");
                Log.Information($"Total files indexed in PAKs: {totalFileCount:N0}");
                
                // If map-structure mode, generate mapping file and exit
                if (mapStructure)
                {
                    // For map-structure, we still need to materialize all files (used for parallel processing)
                    var allFiles = provider.Files.Keys.ToList();
                    GenerateStructureMap(provider, allFiles, outputDir, filter, extractAll);
                    Log.Information("Structure mapping complete!");
                    return;
                }

                // FModel-style: Build filesToExtract using lazy enumeration - only materialize final result
                // This is much faster than materializing all files first, especially for large PAK files
                // Query provider.Files.Keys directly without materializing intermediate lists
                var uniqueFiles = new HashSet<string>(); // Use HashSet to avoid duplicates efficiently
                
                // Build base query (lazy - nothing materialized yet)
                IEnumerable<string> baseQuery = provider.Files.Keys;
                
                // Apply base filter based on extractAll flag
                if (extractAll)
                {
                    // Extract ALL .uasset and .umap files
                    baseQuery = baseQuery.Where(f => f.EndsWith(".uasset") || f.EndsWith(".umap"));
                }
                else
                {
                    // Default: only item blueprints
                    baseQuery = baseQuery.Where(f => f.Contains("/Items/") && f.EndsWith(".uasset"));
                }
                
                // Apply path filter if specified (still lazy)
                if (filter != null)
                {
                    baseQuery = baseQuery.Where(f => f.Contains(filter, StringComparison.OrdinalIgnoreCase));
                }
                
                // Materialize base files (item blueprints or all assets) - first materialization
                var itemFiles = baseQuery.ToList();
                foreach (var file in itemFiles)
                {
                    uniqueFiles.Add(file);
                }
                Log.Information($"Base asset files: {itemFiles.Count:N0}");
                
                // Include icon files if needed (query directly, lazy enumeration, avoid duplicates)
                if (extractIcons || extractJson)
                {
                    var iconQuery = provider.Files.Keys
                        .Where(f => f.Contains("/ICO_") && f.EndsWith(".uasset"));
                    
                    if (filter != null && !noFilterIcons)
                    {
                        iconQuery = iconQuery.Where(f => f.Contains(filter, StringComparison.OrdinalIgnoreCase));
                    }
                    
                    // Only add icons that aren't already in the base set (avoid duplicates)
                    var iconCount = 0;
                    foreach (var iconFile in iconQuery)
                    {
                        if (uniqueFiles.Add(iconFile)) // HashSet.Add returns true if added (not duplicate)
                        {
                            iconCount++;
                        }
                    }
                    Log.Information($"Icon files: {iconCount:N0} added");
                }
                
                // Include texture files if needed (query directly, lazy enumeration, avoid duplicates)
                if (extractTextures)
                {
                    var textureQuery = provider.Files.Keys
                        .Where(f => f.Contains("/Textures/") && f.EndsWith(".uasset"));
                    
                    if (filter != null)
                    {
                        textureQuery = textureQuery.Where(f => f.Contains(filter, StringComparison.OrdinalIgnoreCase));
                    }
                    
                    // Only add textures that aren't already in the set (avoid duplicates)
                    var addedTextureCount = 0;
                    foreach (var textureFile in textureQuery)
                    {
                        if (uniqueFiles.Add(textureFile)) // HashSet.Add returns true if added (not duplicate)
                        {
                            addedTextureCount++;
                        }
                    }
                    Log.Information($"Texture files: {addedTextureCount:N0} added");
                }
                
                // Convert to List for parallel processing (final materialization)
                var filesToExtract = uniqueFiles.ToList();
                uniqueFiles = null; // Clear HashSet to free memory (GC will handle it)
                
                // OPTIMIZATION: Sort files using PAK location index to improve I/O locality
                // Group files from the same PAK together, then sort within each group
                // DEPRIORITIZE MESHES: Files likely to contain meshes are sorted to the end
                // This processes fast assets (JSON/textures) first, then slower mesh conversions
                // This reduces random disk seeks during extraction, significantly improving speed for large datasets
                // NOTE: File reordering does NOT affect output paths - path calculation logic is independent of processing order
                // Mirror mode still maps 1:1, hybrid mode still flattens correctly, regardless of file order
                Log.Information("Sorting files by PAK location and deprioritizing meshes for optimal processing order...");
                
                // Helper function to determine if a file path is likely to contain meshes (based on path heuristics)
                static bool IsLikelyMeshFile(string filePath)
                {
                    var pathUpper = filePath.ToUpperInvariant();
                    // Check for common mesh-related path patterns (already uppercase, so case-sensitive Contains is fine)
                    return pathUpper.Contains("/MESH/") ||
                           pathUpper.Contains("/STATICMESH") ||
                           pathUpper.Contains("/SKELETALMESH") ||
                           pathUpper.Contains("/MODEL") ||
                           pathUpper.Contains("/SK_") ||  // Skeletal mesh prefix in path
                           (pathUpper.Contains("SM_") &&  // Static mesh prefix (check context to avoid false positives)
                            (pathUpper.Contains("/ITEMS/") ||
                             pathUpper.Contains("/CHARACTERS/") ||
                             pathUpper.Contains("/MODELS/")));
                }
                
                if (pakLocationIndex != null && pakLocationIndex.Count > 0)
                {
                    // Sort by: 1) PAK identifier, 2) Mesh likelihood (meshes last), 3) Path within each PAK
                    filesToExtract.Sort((a, b) =>
                    {
                        var pakA = pakLocationIndex.TryGetValue(a, out var pakAVal) ? pakAVal : "unknown";
                        var pakB = pakLocationIndex.TryGetValue(b, out var pakBVal) ? pakBVal : "unknown";
                        var pakCompare = string.Compare(pakA, pakB, StringComparison.OrdinalIgnoreCase);
                        if (pakCompare != 0) return pakCompare;
                        
                        // Within same PAK, deprioritize mesh files
                        var isMeshA = IsLikelyMeshFile(a);
                        var isMeshB = IsLikelyMeshFile(b);
                        if (isMeshA != isMeshB)
                        {
                            return isMeshA ? 1 : -1; // Mesh files come after non-mesh files
                        }
                        
                        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
                    });
                    var meshFileCount = filesToExtract.Count(IsLikelyMeshFile);
                    Log.Information($"Total unique files to extract: {filesToExtract.Count:N0} (sorted by PAK location, {meshFileCount:N0} likely mesh files deprioritized)");
                }
                else
                {
                    // Fallback: Sort by mesh likelihood first, then by path (meshes last)
                    filesToExtract.Sort((a, b) =>
                    {
                        var isMeshA = IsLikelyMeshFile(a);
                        var isMeshB = IsLikelyMeshFile(b);
                        if (isMeshA != isMeshB)
                        {
                            return isMeshA ? 1 : -1; // Mesh files come after non-mesh files
                        }
                        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
                    });
                    var meshFileCount = filesToExtract.Count(IsLikelyMeshFile);
                    Log.Information($"Total unique files to extract: {filesToExtract.Count:N0} (sorted by path, {meshFileCount:N0} likely mesh files deprioritized)");
                }
                
                // Start async WebP conversion background task if WebP format is requested
                if (format.Equals("webp", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Information("Starting async WebP conversion pipeline (extract-then-convert: PNG → WebP)...");
                    _webpConversionTask = Task.Run(() => ProcessWebPConversionQueue(_webpConversionCancellation.Token));
                    
                    // Enable WebP counter in UI
                    if (_progressUI != null)
                    {
                        _progressUI.WebPAsyncEnabled = true;
                    }
                }
                
                // Determine thread count based on available memory and CPU cores for parallel processing
                // For I/O-bound operations (PAK file reading), we can use more threads than CPU cores
                var memoryInfo = GC.GetGCMemoryInfo();
                long totalMemoryGB = memoryInfo.TotalAvailableMemoryBytes / (1024L * 1024 * 1024);
                int processorCount = Environment.ProcessorCount;
                
                // More aggressive threading for I/O-bound extraction work:
                // - High memory (32GB+): Use all CPU cores (I/O can overlap with CPU work)
                // - Medium memory (16-32GB): Use 75% of cores
                // - Low memory (<16GB): Use 50% of cores (conservative for memory pressure)
                int maxThreads = totalMemoryGB >= 32 
                    ? processorCount  // Use all cores - PAK reading is I/O-bound, CPU can process while waiting
                    : totalMemoryGB >= 16 
                        ? Math.Max(4, (int)(processorCount * 0.75))  // 75% of cores
                        : Math.Max(4, processorCount / 2);  // 50% of cores (conservative)
                
                Log.Information($"System memory: {totalMemoryGB} GB, CPU cores: {processorCount}, Using: {maxThreads} threads for parallel processing");
                
                // Workload modeling removed - was taking 7-10 minutes and not providing accurate ETAs
                // Using simple rate-based ETA calculations instead
                
                if (_progressUI != null)
                {
                    _progressUI.StatusMessage = "Starting extraction...";
                }
                
                Log.Information($"Starting extraction...");
                Log.Information($"This may take several minutes...");
                Log.Information($"Phase 2: Single-pass parallel processing (JSON + Textures in one pass)");

                // Thread-safe counters (using long for Interlocked.Read compatibility)
                long successCount = 0;
                long errorCount = 0;
                long skippedCount = 0;
                long textureCount = 0;
                long textureSuccessCount = 0;
                long textureFailCount = 0;
                long textureSkippedCount = 0;
                long soundCount = 0;
                long soundSkippedCount = 0;
                long meshCount = 0;
                long meshSkippedCount = 0;
                long progressCount = 0;
                long verifyCount = 0; // Track verification count across threads
                
                // Track new vs re-extracted files
                long newFilesExtracted = 0; // Files that didn't exist before
                long reExtractedFiles = 0; // Files that existed but failed validation
                
                // Track failure reasons (thread-safe dictionaries)
                var textureFailureReasons = new ConcurrentDictionary<string, long>();
                var meshFailureReasons = new ConcurrentDictionary<string, long>();
                var soundFailureReasons = new ConcurrentDictionary<string, long>();
                
                // Track file types extracted
                var extractedFileTypes = new ConcurrentDictionary<string, long>();
                var startTime = DateTime.Now;
                
                // Initialize GUI elapsed time from start
                if (_progressUI != null)
                {
                    _progressUI.StatusMessage = "Starting extraction...";
                    if (_useGui && _progressUI is AvaloniaGuiUI gui)
                    {
                        gui.Elapsed = TimeSpan.Zero;
                    }
                    _progressUI.TotalFiles = filesToExtract.Count;
                }
                
                // Dynamic estimation: Track total outputs to estimate final count
                const int ESTIMATION_SAMPLE_SIZE = 2000; // Start estimating after 2000 files
                long totalOutputsCreated = 0; // JSON + textures + meshes + sounds
                long estimatedTotalOutputs = 0;
                bool hasEstimate = false;

                // Parallel processing options
                var parallelOptions = new ParallelOptions 
                { 
                    MaxDegreeOfParallelism = maxThreads
                };

                // Phase 2: Single-pass parallel processing - extract JSON, textures, meshes, and sounds in ONE pass
                // This eliminates double file loading and provides 2-3x speedup
                
                // Create cancellation token for GUI mode (allows stopping extraction when GUI is closed)
                var cancellationToken = _extractionCancellation?.Token ?? CancellationToken.None;
                var parallelOptionsWithCancel = new ParallelOptions 
                { 
                    MaxDegreeOfParallelism = maxThreads,
                    CancellationToken = cancellationToken
                };
                
                // Run GUI in background if enabled
                Task? guiTask = null;
                if (_useGui && _progressUI != null)
                {
                    guiTask = Task.Run(() =>
                    {
                        try
                        {
                            if (_progressUI is AvaloniaGuiUI avaloniaUI)
                            {
                                avaloniaUI.Run();
                            }
                        }
                        catch { }
                    });
                }
                
                Parallel.ForEach(filesToExtract, parallelOptionsWithCancel, file =>
                {
                    var localProgress = System.Threading.Interlocked.Increment(ref progressCount);
                    
                    // Log progress periodically to help identify stalls
                    if (localProgress % 1000 == 0)
                    {
                        var elapsed = DateTime.Now - startTime;
                        var rate = localProgress / elapsed.TotalSeconds;
                        Log.Information($"Progress: {localProgress:N0}/{filesToExtract.Count:N0} ({100.0 * localProgress / filesToExtract.Count:F1}%) | Rate: {rate:F1}/s | Currently processing: {Path.GetFileName(file)}");
                    }
                    
                    try
                    {
                        // Use TryLoadPackage with timeout protection to prevent hangs
                        // Some corrupted/problematic files can cause TryLoadPackage to hang indefinitely
                        // Wrap in a task with timeout to prevent the entire extraction from stalling
                        IPackage? package = null;
                        bool packageLoaded = false;
                        
                        try
                        {
                            // Wrap TryLoadPackage in a task with timeout to prevent hangs
                            var loadTask = Task.Run(() =>
                            {
                                IPackage? pkg = null;
                                bool loaded = provider.TryLoadPackage(file, out pkg);
                                return (pkg, loaded);
                            });
                            
                            if (loadTask.Wait(TimeSpan.FromSeconds(PackageLoadTimeoutSeconds)))
                            {
                                (package, packageLoaded) = loadTask.Result;
                            }
                            else
                            {
                                // Timeout - log and skip this file to prevent stall
                                var timeoutMsg = $"⏱️ Timeout loading package (>{PackageLoadTimeoutSeconds}s): {Path.GetFileName(file)}";
                                Log.Warning(timeoutMsg);
                                _warningsErrorsLogWriter?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | TIMEOUT | {file} | Package load exceeded {PackageLoadTimeoutSeconds}s timeout");
                                System.Threading.Interlocked.Increment(ref errorCount);
                                if (_progressUI != null)
                                {
                                    _progressUI.AddWarning($"Timeout: {Path.GetFileName(file)}");
                                }
                                // Skip this file - return from lambda to continue with next file
                                return;
                            }
                        }
                        catch (Exception loadEx)
                        {
                            // If task itself fails, log and continue
                            var loadErrorMsg = $"Error loading package {Path.GetFileName(file)}: {loadEx.Message}";
                            Log.Warning(loadErrorMsg);
                            System.Threading.Interlocked.Increment(ref errorCount);
                            // Skip this file - return from lambda to continue with next file
                            return;
                        }
                        
                        if (packageLoaded && package != null)
                        {
                            // Get all exports from the package
                            var exports = package.GetExports();
                            
                            if (exports != null && exports.Any())
                            {
                                // Create output path matching FModel structure
                                var relativePath = file.Replace("/Game/", "").Replace(".uasset", "");
                                
                                // Determine output path based on organization mode
                                string outputPath;
                                string fileOutputDir;
                                var fileName = Path.GetFileName(relativePath);
                                string assetType; // Declare outside if/else blocks
                                
                                if (smartHybridStructure)
                                {
                                    // Smart Hybrid: preserve 2-3 levels of hierarchy, then organize by type
                                    assetType = DetermineAssetType(exports, fileName, relativePath);
                                    outputPath = BuildSmartHybridPath(outputDir, relativePath, assetType, fileName);
                                    fileOutputDir = Path.GetDirectoryName(outputPath);
                                }
                                else if (hybridStructure)
                                {
                                    // Hybrid: organize by type within each major folder (first level only)
                                    assetType = DetermineAssetType(exports, fileName, relativePath);
                                    var dirPath = Path.GetDirectoryName(relativePath)?.Replace("\\", "/") ?? "";
                                    
                                    // Remove common SCUM path prefixes to get to actual content folders
                                    var cleanPath = dirPath;
                                    if (cleanPath.StartsWith("SCUM/Content/ConZ_Files/"))
                                        cleanPath = cleanPath.Substring("SCUM/Content/ConZ_Files/".Length);
                                    else if (cleanPath.StartsWith("SCUM/Content/"))
                                        cleanPath = cleanPath.Substring("SCUM/Content/".Length);
                                    else if (cleanPath.StartsWith("SCUM/"))
                                        cleanPath = cleanPath.Substring("SCUM/".Length);
                                    
                                    // Get the first major folder (e.g., "Items", "Characters", "Weapons")
                                    var pathParts = cleanPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                                    if (pathParts.Length > 0)
                                    {
                                        var majorFolder = pathParts[0];
                                        // Create path: outputDir/MajorFolder/AssetType/filename
                                        outputPath = Path.Combine(outputDir, majorFolder, assetType, fileName);
                                    }
                                    else
                                    {
                                        // No folder hierarchy, just use type
                                        outputPath = Path.Combine(outputDir, assetType, fileName);
                                    }
                                    fileOutputDir = Path.GetDirectoryName(outputPath);
                                }
                                else if (organizeByType)
                                {
                                    // Organize by asset type
                                    assetType = DetermineAssetType(exports, fileName, relativePath);
                                    outputPath = Path.Combine(outputDir, assetType, fileName);
                                    fileOutputDir = Path.GetDirectoryName(outputPath);
                                }
                                else
                                {
                                    // Mirror game structure
                                    assetType = "Unknown"; // Not used in mirror mode, but initialize to avoid compiler error
                                    outputPath = Path.Combine(outputDir, relativePath);
                                    fileOutputDir = Path.GetDirectoryName(outputPath);
                                }
                                
                                if (!string.IsNullOrEmpty(fileOutputDir))
                                {
                                    EnsureDirectory(fileOutputDir);
                                }

                                // Strategy 1: Check ALL outputs at once in resume mode
                                // If all outputs exist and are valid, skip entire package (no extraction work)
                                // BUT: Still collect path mappings even for skipped packages (for complete mapping file)
                                bool skipEntirePackage = false;
                                if (resume)
                                {
                                    bool allOutputsExist = true;
                                    
                                    // Check JSON
                                    if (extractJson)
                                    {
                                        var jsonPath = outputPath + ".json";
                                        if (!IsValidJsonFile(jsonPath))
                                        {
                                            allOutputsExist = false;
                                        }
                                    }
                                    
                                    // Check textures/icons (quick check - just file exists, full validation happens later if needed)
                                    if (allOutputsExist && (extractIcons || extractTextures))
                                    {
                                        foreach (var export in exports)
                                        {
                                            if (export is UTexture texture)
                                            {
                                                // Determine texture output directory
                                                // IMPORTANT: In smart-hybrid mode, textures are saved to a Textures subdirectory
                                                // even when extractIcons is true, so we need to use the same path calculation
                                                string textureOutputDir;
                                                if (extractIcons && smartHybridStructure)
                                                {
                                                    // Icons in smart-hybrid mode: use BuildSmartHybridPath with "Textures" asset type
                                                    // This matches where ExportTexture actually saves the files
                                                    var textureAssetType = DetermineAssetType(new[] { export }, texture.Name, relativePath);
                                                    textureOutputDir = Path.GetDirectoryName(BuildSmartHybridPath(outputDir, relativePath, textureAssetType, texture.Name));
                                                }
                                                else if (extractIcons)
                                                {
                                                    // Icons in non-smart-hybrid mode: saved to fileOutputDir (same as JSON)
                                                    textureOutputDir = fileOutputDir;
                                                }
                                                else if (extractTextures)
                                                {
                                                    // Textures use organization mode path
                                                    if (smartHybridStructure)
                                                    {
                                                        var textureAssetType = DetermineAssetType(new[] { export }, texture.Name, relativePath);
                                                        textureOutputDir = Path.GetDirectoryName(BuildSmartHybridPath(outputDir, relativePath, textureAssetType, texture.Name));
                                                    }
                                                    else if (hybridStructure)
                                                    {
                                                        var textureAssetType = DetermineAssetType(new[] { export }, texture.Name, relativePath);
                                                        var dirPath = Path.GetDirectoryName(relativePath)?.Replace("\\", "/") ?? "";
                                                        var cleanPath = dirPath;
                                                        if (cleanPath.StartsWith("SCUM/Content/ConZ_Files/"))
                                                            cleanPath = cleanPath.Substring("SCUM/Content/ConZ_Files/".Length);
                                                        else if (cleanPath.StartsWith("SCUM/Content/"))
                                                            cleanPath = cleanPath.Substring("SCUM/Content/".Length);
                                                        else if (cleanPath.StartsWith("SCUM/"))
                                                            cleanPath = cleanPath.Substring("SCUM/".Length);
                                                        var pathParts = cleanPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                                                        if (pathParts.Length > 0)
                                                        {
                                                            textureOutputDir = Path.Combine(outputDir, pathParts[0], textureAssetType);
                                                        }
                                                        else
                                                        {
                                                            textureOutputDir = Path.Combine(outputDir, textureAssetType);
                                                        }
                                                    }
                                                    else if (organizeByType)
                                                    {
                                                        var textureAssetType = DetermineAssetType(new[] { export }, texture.Name, relativePath);
                                                        textureOutputDir = Path.Combine(outputDir, textureAssetType);
                                                    }
                                                    else
                                                    {
                                                        textureOutputDir = fileOutputDir;
                                                    }
                                                }
                                                else
                                                {
                                                    textureOutputDir = fileOutputDir; // Fallback
                                                }
                                                
                                                // Collision-safe: distinct packages sharing a texture name must not overwrite each other
                                                textureOutputDir = ResolveCollisionSafeTextureDir(texture.Name, relativePath, textureOutputDir);
                                                
                                                if (!string.IsNullOrEmpty(textureOutputDir))
                                                {
                                                    var texturePath = Path.Combine(textureOutputDir, texture.Name + $".{format}");
                                                    if (!IsValidImageFile(texturePath, format))
                                                    {
                                                        allOutputsExist = false;
                                                        break; // Early exit if any missing
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    
                                    // Check meshes
                                            if (allOutputsExist && extractAll && extractMeshes)
                                            {
                                                foreach (var export in exports)
                                                {
                                                    if (export is UStaticMesh || export is USkeletalMesh)
                                                    {
                                                        // Skip problematic mesh types during resume check (same filters as extraction)
                                                        var meshName = export.Name;
                                                        var meshNameLower = meshName.ToLower();
                                                        if (meshNameLower.Contains("landscape") || 
                                                            meshNameLower.Contains("terrain") ||
                                                            meshNameLower.Contains("foliage") ||
                                                            meshNameLower.Contains("navmesh") ||
                                                            meshNameLower.Contains("spline") ||
                                                            meshNameLower.Contains("world") ||
                                                            meshNameLower.Contains("environment") ||
                                                            meshNameLower.Contains("level") ||
                                                            meshNameLower.Contains("map_") ||
                                                            meshNameLower.StartsWith("lm_") ||
                                                            meshNameLower.Contains("collision") ||
                                                            meshNameLower.Contains("instanced") ||
                                                            relativePath.Contains("/World/") ||
                                                            relativePath.Contains("/Landscape/") ||
                                                            relativePath.Contains("/Environment/") ||
                                                            relativePath.Contains("/Foliage/"))
                                                        {
                                                            continue; // Skip world meshes during resume check
                                                        }
                                                        
                                                        // Determine mesh output directory
                                                        string meshOutputDir;
                                                        if (smartHybridStructure)
                                                        {
                                                            var meshAssetType = DetermineAssetType(new[] { export }, export.Name, relativePath);
                                                            meshOutputDir = Path.GetDirectoryName(BuildSmartHybridPath(outputDir, relativePath, meshAssetType, export.Name));
                                                        }
                                                        else if (hybridStructure)
                                                        {
                                                            var meshAssetType = DetermineAssetType(new[] { export }, export.Name, relativePath);
                                                    var dirPath = Path.GetDirectoryName(relativePath)?.Replace("\\", "/") ?? "";
                                                    var cleanPath = dirPath;
                                                    if (cleanPath.StartsWith("SCUM/Content/ConZ_Files/"))
                                                        cleanPath = cleanPath.Substring("SCUM/Content/ConZ_Files/".Length);
                                                    else if (cleanPath.StartsWith("SCUM/Content/"))
                                                        cleanPath = cleanPath.Substring("SCUM/Content/".Length);
                                                    else if (cleanPath.StartsWith("SCUM/"))
                                                        cleanPath = cleanPath.Substring("SCUM/".Length);
                                                    var pathParts = cleanPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                                                    if (pathParts.Length > 0)
                                                    {
                                                        meshOutputDir = Path.Combine(outputDir, pathParts[0], meshAssetType);
                                                    }
                                                    else
                                                    {
                                                        meshOutputDir = Path.Combine(outputDir, meshAssetType);
                                                    }
                                                }
                                                else if (organizeByType)
                                                {
                                                    var meshAssetType = DetermineAssetType(new[] { export }, export.Name, relativePath);
                                                    meshOutputDir = Path.Combine(outputDir, meshAssetType);
                                                }
                                                else
                                                {
                                                    meshOutputDir = fileOutputDir;
                                                }
                                                
                                                var meshPath = Path.Combine(meshOutputDir, export.Name + ".glb");
                                                if (!IsValidMeshFile(meshPath))
                                                {
                                                    allOutputsExist = false;
                                                    break; // Early exit if any missing
                                                }
                                            }
                                        }
                                    }
                                    
                                    // Check sounds (check common formats without expensive decode)
                                    if (allOutputsExist && extractAll && extractSounds)
                                    {
                                        foreach (var export in exports)
                                        {
                                            if (export is USoundWave soundWave)
                                            {
                                                // Try common formats first (avoid expensive decode if possible)
                                                var soundName = soundWave.Name;
                                                var commonFormats = new[] { "ogg", "wav" };
                                                bool soundExists = false;
                                                
                                                foreach (var fmt in commonFormats)
                                                {
                                                    var soundPath = Path.Combine(fileOutputDir, $"{soundName}.{fmt}");
                                                    if (IsValidSoundFile(soundPath))
                                                    {
                                                        soundExists = true;
                                                        break;
                                                    }
                                                }
                                                
                                                // If not found in common formats, decode to get actual format (expensive but necessary)
                                                if (!soundExists)
                                                {
                                                    string soundFormat;
                                                    byte[] dummy;
                                                    soundWave.Decode(true, out soundFormat, out dummy);
                                                    var soundPath = Path.Combine(fileOutputDir, $"{soundName}.{soundFormat.ToLower()}");
                                                    if (!IsValidSoundFile(soundPath))
                                                    {
                                                        allOutputsExist = false;
                                                        break; // Early exit if any missing
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    
                                    // If ALL outputs exist and are valid, skip entire package
                                    if (allOutputsExist)
                                    {
                                        skipEntirePackage = true;
                                        
                                        // Count what we're skipping for logging
                                        int jsonSkip = extractJson ? 1 : 0;
                                        int textureSkipCount = 0;
                                        int meshSkipCount = 0;
                                        int soundSkipCount = 0;
                                        
                                        if (extractIcons || extractTextures)
                                        {
                                            foreach (var export in exports)
                                            {
                                                if (export is UTexture)
                                                {
                                                    textureSkipCount++;
                                                    System.Threading.Interlocked.Increment(ref textureCount); // Count for progress
                                                }
                                            }
                                        }
                                        
                                        if (extractAll && extractMeshes)
                                        {
                                            foreach (var export in exports)
                                            {
                                                if (export is UStaticMesh || export is USkeletalMesh)
                                                {
                                                    meshSkipCount++;
                                                    System.Threading.Interlocked.Increment(ref meshCount); // Count for progress
                                                }
                                            }
                                        }
                                        
                                        if (extractAll && extractSounds)
                                        {
                                            foreach (var export in exports)
                                            {
                                                if (export is USoundWave)
                                                {
                                                    soundSkipCount++;
                                                    System.Threading.Interlocked.Increment(ref soundCount); // Count for progress
                                                }
                                            }
                                        }
                                        
                                        // Increment all skip counters
                                        if (extractJson)
                                        {
                                            System.Threading.Interlocked.Increment(ref skippedCount);
                                        }
                                        System.Threading.Interlocked.Add(ref textureSkippedCount, textureSkipCount);
                                        System.Threading.Interlocked.Add(ref meshSkippedCount, meshSkipCount);
                                        System.Threading.Interlocked.Add(ref soundSkippedCount, soundSkipCount);
                                        
                                        // Log skip details (every 100th skip to avoid spam, or first 10)
                                        var totalSkipped = System.Threading.Interlocked.Read(ref skippedCount) + 
                                                         System.Threading.Interlocked.Read(ref textureSkippedCount) +
                                                         System.Threading.Interlocked.Read(ref meshSkippedCount) +
                                                         System.Threading.Interlocked.Read(ref soundSkippedCount);
                                        if (totalSkipped <= 10 || totalSkipped % 100 == 0)
                                        {
                                            var skipDetails = new List<string>();
                                            if (jsonSkip > 0) skipDetails.Add($"{jsonSkip} JSON");
                                            if (textureSkipCount > 0) skipDetails.Add($"{textureSkipCount} texture(s)");
                                            if (meshSkipCount > 0) skipDetails.Add($"{meshSkipCount} mesh(es)");
                                            if (soundSkipCount > 0) skipDetails.Add($"{soundSkipCount} sound(s)");
                                            
                                            var mode = fastResume ? "fast-resume" : "resume";
                                            Log.Information($"⏭️ [{mode}] Skipping package (all outputs exist): {Path.GetFileName(file)} - {string.Join(", ", skipDetails)}");
                                        }
                                        
                                        // Still collect path mapping even when skipping (for complete mapping file)
                                        if (extractJson && !string.IsNullOrEmpty(outputPath) && !mirrorStructure)
                                        {
                                            var relativeJsonPath = Path.GetRelativePath(outputDir, outputPath + ".json");
                                            var mirrorPath = Path.Combine(outputDir, relativePath) + ".json";
                                            var mapping = new PathMapping
                                            {
                                                OriginalPath = file,
                                                MirrorPath = Path.GetRelativePath(outputDir, mirrorPath),
                                                OrganizedPath = relativeJsonPath,
                                                AssetType = assetType,
                                                FileName = fileName + ".json"
                                            };
                                            PathMappings.Add(mapping);
                                        }
                                        
                                        // Skip to next file - no extraction work needed!
                                        // Use return to exit lambda early (can't use continue in lambda)
                                        return;
                                    }
                                }

                                // Export JSON if enabled (only if not skipping entire package)
                                if (extractJson)
                                {
                                    var jsonPath = outputPath + ".json";
                                    
                                    // Skip if file exists, is valid, and resume mode is enabled (individual check for partial skips)
                                    if (resume && IsValidJsonFile(jsonPath))
                                    {
                                        System.Threading.Interlocked.Increment(ref skippedCount);
                                        // Log individual skips occasionally (first 10, then every 1000th)
                                        var skipCount = System.Threading.Interlocked.Read(ref skippedCount);
                                        if (skipCount <= 10 || skipCount % 1000 == 0)
                                        {
                                            var mode = fastResume ? "fast-resume" : "resume";
                                            Log.Debug($"⏭️ [{mode}] Skipping JSON (exists): {Path.GetFileName(jsonPath)}");
                                        }
                                    }
                                    else
                                    {
                                        // Serialize to JSON using Newtonsoft.Json (CUE4Parse uses this)
                                        var json = JsonConvert.SerializeObject(exports, Formatting.Indented);
                                        
                                        // Atomic write to prevent partial files
                                        AtomicWriteText(jsonPath, json);
                                        System.Threading.Interlocked.Increment(ref successCount);
                                        
                                        // Write to success log
                                        var relativeJsonPath = Path.GetRelativePath(outputDir, jsonPath);
                                        _successLogWriter?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | JSON | {relativeJsonPath}");
                                        
                                        // Collect path mapping (unless mirror mode - redundant)
                                        if (!mirrorStructure)
                                        {
                                            var mirrorPath = Path.Combine(outputDir, relativePath) + ".json";
                                            var mapping = new PathMapping
                                            {
                                                OriginalPath = file,
                                                MirrorPath = Path.GetRelativePath(outputDir, mirrorPath),
                                                OrganizedPath = relativeJsonPath,
                                                AssetType = assetType,
                                                FileName = fileName + ".json"
                                            };
                                            PathMappings.Add(mapping);
                                        }
                                        
                                        // Add to UI activity log (sample exports, not all to avoid spam)
                                        if (localProgress % 10 == 0)
                                        {
                                            var jsonFileName = Path.GetFileName(relativePath);
                                            if (_useGui && _progressUI != null)
                                                _progressUI.AddJsonExport(jsonFileName);
                                            else if (_progressUI != null)
                                                _progressUI.AddJsonExport(jsonFileName);
                                        }
                                    }
                                }
                                
                                // Export class metadata if enabled
                                if (exportClassMetadata)
                                {
                                    var classMetadataList = new List<ClassMetadataInfo>();
                                    
                                    foreach (var export in exports)
                                    {
                                        var metadata = ClassMetadataExtractor.ExtractClassMetadata(export);
                                        if (metadata != null)
                                        {
                                            classMetadataList.Add(metadata);
                                            
                                            // Also generate database schema summary
                                            var schemaSummary = ClassMetadataExtractor.ExtractSchemaSummary(metadata);
                                            metadata.DefaultValues["_SchemaSummary"] = schemaSummary;
                                        }
                                    }
                                    
                                    if (classMetadataList.Any())
                                    {
                                        var metadataJson = JsonConvert.SerializeObject(classMetadataList, Formatting.Indented);
                                        AtomicWriteText(outputPath + "_metadata.json", metadataJson);
                                    }
                                }

                                // Extract textures from exports (icons, images embedded in assets) - Phase 2: Same pass!
                                if (extractIcons && !string.IsNullOrEmpty(fileOutputDir))
                                {
                                    foreach (var export in exports)
                                    {
                                        if (export is UTexture texture)
                                        {
                                            // Count texture for progress tracking (fixes X/0 display bug)
                                            System.Threading.Interlocked.Increment(ref textureCount);
                                            
                                            // Limit concurrent texture processing to prevent memory explosion
                                            // Log if we're waiting (indicates semaphore contention)
                                            if (TextureProcessingSemaphore.CurrentCount == 0 && textureDebugCount < 5)
                                            {
                                                Log.Debug($"⏳ Waiting for texture processing slot (semaphore full, processing large texture?)");
                                            }
                                            // Determine where texture should be saved (must match skip check logic)
                                            string textureOutputDirForExtract;
                                            if (smartHybridStructure)
                                            {
                                                // Icons in smart-hybrid mode: use BuildSmartHybridPath with "Textures" asset type
                                                // This matches where textures are saved (even if they were extracted with --textures previously)
                                                var textureAssetType = DetermineAssetType(new[] { export }, texture.Name, relativePath);
                                                textureOutputDirForExtract = Path.GetDirectoryName(BuildSmartHybridPath(outputDir, relativePath, textureAssetType, texture.Name));
                                            }
                                            else
                                            {
                                                // Icons in non-smart-hybrid mode: saved to fileOutputDir (same as JSON)
                                                    textureOutputDirForExtract = fileOutputDir;
                                                }
                                                
                                                // Collision-safe: distinct packages sharing a texture name must not overwrite each other
                                                textureOutputDirForExtract = ResolveCollisionSafeTextureDir(texture.Name, relativePath, textureOutputDirForExtract);
                                                
                                                // Resume check for icons/textures from asset properties
                                            // Resume check: For WebP format, check both WebP and PNG files
                                            // If PNG exists, queue it for WebP conversion instead of re-extracting
                                            var iconTexturePath = Path.Combine(textureOutputDirForExtract, texture.Name + $".{format}");
                                            if (resume)
                                            {
                                                if (IsValidImageFile(iconTexturePath, format))
                                                {
                                                    // WebP already exists, skip
                                                    System.Threading.Interlocked.Increment(ref textureSkippedCount);
                                                    // Log individual skips occasionally (first 10, then every 1000th)
                                                    var skipCount = System.Threading.Interlocked.Read(ref textureSkippedCount);
                                                    if (skipCount <= 10 || skipCount % 1000 == 0)
                                                    {
                                                        var mode = fastResume ? "fast-resume" : "resume";
                                                        Log.Debug($"⏭️ [{mode}] Skipping texture (exists): {texture.Name}.{format}");
                                                    }
                                                    continue;
                                                }
                                                else if (format.Equals("webp", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    // Check if PNG exists (from previous extraction or interrupted conversion)
                                                    var pngPath = Path.Combine(textureOutputDirForExtract, texture.Name + ".png");
                                                    if (File.Exists(pngPath) && IsValidImageFile(pngPath, "png"))
                                                    {
                                                        // PNG exists, queue for WebP conversion
                                                        // Use false for verifyQuality in resume mode (already validated)
                                                        WebPConversionQueue.Enqueue((pngPath, iconTexturePath, quality, false));
                                                        Interlocked.Increment(ref _webpConversionQueued);
                                                        System.Threading.Interlocked.Increment(ref textureSkippedCount);
                                                        var mode = fastResume ? "fast-resume" : "resume";
                                                        Log.Debug($"⏭️ [{mode}] Skipping texture extraction, queued PNG→WebP: {texture.Name}");
                                                        continue; // Skip extraction, will convert PNG → WebP
                                                    }
                                                }
                                            }
                                            
                                            // Use async wait with timeout to prevent deadlocks
                                            // If all semaphore slots are held by stuck threads, timeout and skip this texture
                                            // Note: Parallel.ForEach doesn't support async lambdas, so we use WaitAsync and wait on it
                                            var semaphoreAcquired = TextureProcessingSemaphore.WaitAsync(TimeSpan.FromSeconds(TextureProcessingTimeoutSeconds)).Result;
                                            if (semaphoreAcquired)
                                            {
                                                try
                                                {
                                                    var currentVerifyCount = System.Threading.Interlocked.Read(ref verifyCount);
                                                    bool shouldVerify = verifyQuality && currentVerifyCount < 10;
                                                    
                                                    // Log large textures that might take time
                                                    if (textureDebugCount < 10)
                                                    {
                                                        Log.Debug($"🎨 Processing texture: {texture.Name} (Format: {texture.Format})");
                                                    }
                                                    
                                                    // Check if file existed before extraction
                                                    var texturePath = Path.Combine(textureOutputDirForExtract, texture.Name + $".{format}");
                                                    bool textureExisted = File.Exists(texturePath);
                                                    
                                                    // Export to the correct directory (matching skip check logic)
                                                    string? failureReason = null;
                                                    bool success = ExportTextureWithReason(texture, textureOutputDirForExtract, texture.Name, format, quality, shouldVerify, out failureReason);
                                                    
                                                    if (success)
                                                    {
                                                        System.Threading.Interlocked.Increment(ref textureSuccessCount);
                                                        if (shouldVerify)
                                                            System.Threading.Interlocked.Increment(ref verifyCount);
                                                        
                                                        // Track new vs re-extracted
                                                        if (textureExisted)
                                                        {
                                                            System.Threading.Interlocked.Increment(ref reExtractedFiles);
                                                            Log.Debug($"🔄 Re-extracted texture (validation failed): {texture.Name}.{format}");
                                                        }
                                                        else
                                                        {
                                                            System.Threading.Interlocked.Increment(ref newFilesExtracted);
                                                        }
                                                        
                                                        // Track file type
                                                        extractedFileTypes.AddOrUpdate($"Texture.{format.ToUpper()}", 1, (key, oldValue) => oldValue + 1);
                                                    }
                                                    else
                                                    {
                                                        System.Threading.Interlocked.Increment(ref textureFailCount);
                                                        if (!string.IsNullOrEmpty(failureReason))
                                                        {
                                                            textureFailureReasons.AddOrUpdate(failureReason, 1, (key, oldValue) => oldValue + 1);
                                                        }
                                                    }
                                                }
                                                finally
                                                {
                                                    TextureProcessingSemaphore.Release();
                                                }
                                            }
                                            else
                                            {
                                                // Timeout waiting for semaphore - queue for retry instead of failing
                                                // Since textures are written to disk immediately, we can retry later without memory pressure
                                                var retryMsg = $"⏱️ Texture processing timeout (>{TextureProcessingTimeoutSeconds}s wait): {texture.Name} - queued for retry";
                                                Log.Information(retryMsg);
                                                _warningsErrorsLogWriter?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | RETRY | Texture queued for retry: {texture.Name}");
                                                
                                                // Queue for retry (will be processed at the end)
                                                var currentVerifyCount = System.Threading.Interlocked.Read(ref verifyCount);
                                                bool shouldVerify = verifyQuality && currentVerifyCount < 10;
                                                TextureRetryQueue.Enqueue((file, texture.Name, textureOutputDirForExtract, format, quality, shouldVerify, 0));
                                                Interlocked.Increment(ref _textureRetryQueued);
                                                
                                                if (_progressUI != null)
                                                {
                                                    _progressUI.AddWarning($"Texture queued for retry: {texture.Name}");
                                                }
                                            }
                                        }
                                    }
                                }
                                
                                // Extract textures if --textures flag is set (Phase 2: Same pass, no double loading!)
                                if (extractTextures && !string.IsNullOrEmpty(fileOutputDir))
                                {
                                    foreach (var export in exports)
                                    {
                                        if (export is UTexture texture)
                                        {
                                            System.Threading.Interlocked.Increment(ref textureCount);
                                            
                                            // Determine texture output directory (reuse path building logic)
                                            string textureOutputDir;
                                            if (smartHybridStructure)
                                            {
                                                var textureAssetType = DetermineAssetType(new[] { export }, texture.Name, relativePath);
                                                textureOutputDir = Path.GetDirectoryName(BuildSmartHybridPath(outputDir, relativePath, textureAssetType, texture.Name));
                                            }
                                            else if (hybridStructure)
                                            {
                                                var textureAssetType = DetermineAssetType(new[] { export }, texture.Name, relativePath);
                                                var dirPath = Path.GetDirectoryName(relativePath)?.Replace("\\", "/") ?? "";
                                                var cleanPath = dirPath;
                                                if (cleanPath.StartsWith("SCUM/Content/ConZ_Files/"))
                                                    cleanPath = cleanPath.Substring("SCUM/Content/ConZ_Files/".Length);
                                                else if (cleanPath.StartsWith("SCUM/Content/"))
                                                    cleanPath = cleanPath.Substring("SCUM/Content/".Length);
                                                else if (cleanPath.StartsWith("SCUM/"))
                                                    cleanPath = cleanPath.Substring("SCUM/".Length);
                                                var pathParts = cleanPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                                                if (pathParts.Length > 0)
                                                {
                                                    textureOutputDir = Path.Combine(outputDir, pathParts[0], textureAssetType);
                                                }
                                                else
                                                {
                                                    textureOutputDir = Path.Combine(outputDir, textureAssetType);
                                                }
                                            }
                                            else if (organizeByType)
                                            {
                                                var textureAssetType = DetermineAssetType(new[] { export }, texture.Name, relativePath);
                                                textureOutputDir = Path.Combine(outputDir, textureAssetType);
                                            }
                                                else
                                                {
                                                    textureOutputDir = fileOutputDir;
                                                }
                                                
                                                // Collision-safe: distinct packages sharing a texture name must not overwrite each other
                                                textureOutputDir = ResolveCollisionSafeTextureDir(texture.Name, relativePath, textureOutputDir);
                                                
                                                if (!string.IsNullOrEmpty(textureOutputDir))
                                            {
                                                var texturePath = Path.Combine(textureOutputDir, texture.Name + $".{format}");
                                                
                                                // Skip if file exists, is valid, and resume mode is enabled
                                                // Resume check: For WebP format, check both WebP and PNG files
                                                // If PNG exists, queue it for WebP conversion instead of re-extracting
                                                if (resume)
                                                {
                                                    if (IsValidImageFile(texturePath, format))
                                                    {
                                                        // WebP already exists, skip
                                                        System.Threading.Interlocked.Increment(ref textureSkippedCount);
                                                        // Log individual skips occasionally (first 10, then every 1000th)
                                                        var skipCount = System.Threading.Interlocked.Read(ref textureSkippedCount);
                                                        if (skipCount <= 10 || skipCount % 1000 == 0)
                                                        {
                                                            var mode = fastResume ? "fast-resume" : "resume";
                                                            Log.Debug($"⏭️ [{mode}] Skipping texture (exists): {texture.Name}.{format}");
                                                        }
                                                        continue;
                                                    }
                                                    else if (format.Equals("webp", StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        // Check if PNG exists (from previous extraction or interrupted conversion)
                                                        var pngPath = Path.Combine(textureOutputDir, texture.Name + ".png");
                                                        if (File.Exists(pngPath) && IsValidImageFile(pngPath, "png"))
                                                        {
                                                            // PNG exists, queue for WebP conversion
                                                            // Use false for verifyQuality in resume mode (already validated)
                                                            WebPConversionQueue.Enqueue((pngPath, texturePath, quality, false));
                                                            Interlocked.Increment(ref _webpConversionQueued);
                                                            System.Threading.Interlocked.Increment(ref textureSkippedCount);
                                                            var mode = fastResume ? "fast-resume" : "resume";
                                                            Log.Debug($"⏭️ [{mode}] Skipping texture extraction, queued PNG→WebP: {texture.Name}");
                                                            continue; // Skip extraction, will convert PNG → WebP
                                                        }
                                                    }
                                                    else
                                                    {
                                                        // Log why we're re-extracting (first few only, and only for specific problematic textures)
                                                        var textureName = texture.Name;
                                                        var problematicTextures = new[] { 
                                                            "T_ChristmasDecorations_Merged_N", 
                                                            "T_RockLarge_M", 
                                                            "T_SM_Vaccine_01_OP", 
                                                            "T_SM_Cocktail_01_OP_M" 
                                                        };
                                                        if (problematicTextures.Any(t => textureName.Contains(t)))
                                                        {
                                                            Log.Warning($"🔄 Re-extracting texture (validation failed): {textureName}.{format}");
                                                            Log.Warning($"   Expected path: {texturePath}");
                                                            Log.Warning($"   File exists: {File.Exists(texturePath)}");
                                                            if (File.Exists(texturePath))
                                                            {
                                                                var fi = new FileInfo(texturePath);
                                                                Log.Warning($"   File size: {fi.Length} bytes");
                                                            }
                                                        }
                                                    }
                                                }
                                                
                                                // Limit concurrent texture processing to prevent memory explosion
                                                // Log if we're waiting (indicates semaphore contention)
                                                if (TextureProcessingSemaphore.CurrentCount == 0 && textureDebugCount < 5)
                                                {
                                                    Log.Debug($"⏳ Waiting for texture processing slot (semaphore full, processing large texture?)");
                                                }
                                                // Use async wait with timeout to prevent deadlocks
                                                var semaphoreAcquired = TextureProcessingSemaphore.WaitAsync(TimeSpan.FromSeconds(TextureProcessingTimeoutSeconds)).Result;
                                                if (semaphoreAcquired)
                                                {
                                                    try
                                                    {
                                                        // Export immediately and dispose (FModel pattern)
                                                        var currentVerifyCount = System.Threading.Interlocked.Read(ref verifyCount);
                                                        bool shouldVerify = verifyQuality && currentVerifyCount < 10;

                                                        // Log large textures that might take time
                                                        if (textureDebugCount < 10)
                                                        {
                                                            Log.Debug($"🎨 Processing texture: {texture.Name} (Format: {texture.Format})");
                                                        }

                                                        var texturePath2 = Path.Combine(textureOutputDir, texture.Name + $".{format}");
                                                        bool textureExisted2 = File.Exists(texturePath2);
                                                        string? failureReason2 = null;
                                                        bool success = ExportTextureWithReason(texture, textureOutputDir, texture.Name, format, quality, shouldVerify, out failureReason2);
                                                        
                                                        if (success)
                                                        {
                                                            // Track new vs re-extracted
                                                            if (textureExisted2)
                                                            {
                                                                System.Threading.Interlocked.Increment(ref reExtractedFiles);
                                                                Log.Debug($"🔄 Re-extracted texture (validation failed): {texture.Name}.{format}");
                                                            }
                                                            else
                                                            {
                                                                System.Threading.Interlocked.Increment(ref newFilesExtracted);
                                                            }
                                                            
                                                            // Track file type
                                                            extractedFileTypes.AddOrUpdate($"Texture.{format.ToUpper()}", 1, (key, oldValue) => oldValue + 1);
                                                        }
                                                        else if (!string.IsNullOrEmpty(failureReason2))
                                                        {
                                                            textureFailureReasons.AddOrUpdate(failureReason2, 1, (key, oldValue) => oldValue + 1);
                                                        }

                                                        if (success)
                                                        {
                                                            System.Threading.Interlocked.Increment(ref textureSuccessCount);
                                                            if (shouldVerify)
                                                                System.Threading.Interlocked.Increment(ref verifyCount);
                                                        
                                                            // Collect path mapping for textures (unless mirror mode)
                                                            if (!mirrorStructure)
                                                            {
                                                                var textureAssetType = texture.Name.StartsWith("ICO_", StringComparison.OrdinalIgnoreCase) ? "Icons" : "Textures";
                                                                var textureOutputPath = Path.Combine(textureOutputDir, $"{texture.Name}.{format}");
                                                            var textureMirrorPath = Path.Combine(outputDir, relativePath.Replace("/Game/", "").Replace(".uasset", ""), textureAssetType, $"{texture.Name}.{format}");
                                                            var mapping = new PathMapping
                                                            {
                                                                OriginalPath = file,
                                                                MirrorPath = Path.GetRelativePath(outputDir, textureMirrorPath),
                                                                OrganizedPath = Path.GetRelativePath(outputDir, textureOutputPath),
                                                                AssetType = textureAssetType,
                                                                FileName = $"{texture.Name}.{format}"
                                                            };
                                                            PathMappings.Add(mapping);
                                                            }
                                                        }
                                                        else
                                                        {
                                                            System.Threading.Interlocked.Increment(ref textureFailCount);
                                                        }
                                                    }
                                                    finally
                                                    {
                                                        TextureProcessingSemaphore.Release();
                                                    }
                                                }
                                                else
                                                {
                                                    // Timeout waiting for semaphore - queue for retry instead of failing
                                                    // Since textures are written to disk immediately, we can retry later without memory pressure
                                                    var retryMsg = $"⏱️ Texture processing timeout (>{TextureProcessingTimeoutSeconds}s wait): {texture.Name} - queued for retry";
                                                    Log.Information(retryMsg);
                                                    _warningsErrorsLogWriter?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | RETRY | Texture queued for retry: {texture.Name}");
                                                    
                                                    // Queue for retry (will be processed at the end)
                                                    var currentVerifyCount = System.Threading.Interlocked.Read(ref verifyCount);
                                                    bool shouldVerify = verifyQuality && currentVerifyCount < 10;
                                                    TextureRetryQueue.Enqueue((file, texture.Name, textureOutputDir, format, quality, shouldVerify, 0));
                                                    Interlocked.Increment(ref _textureRetryQueued);
                                                    
                                                    if (_progressUI != null)
                                                    {
                                                        _progressUI.AddWarning($"Texture queued for retry: {texture.Name}");
                                                    }
                                                }
                                            } // Close: if (!string.IsNullOrEmpty(textureOutputDir))
                                        } // Close: if (export is UTexture texture)
                                    } // Close: foreach (var export in exports)
                                } // Close: if (extractTextures && !string.IsNullOrEmpty(fileOutputDir))
                                
                                // Extract additional asset types if --extract-all is enabled
                                // Only extract interactive items (Characters, Weapons, Vehicles, Items, Clothing, Attachments, Buildables, UI)
                                // Skip non-essential assets (sounds, animations, physics, world objects) for database purposes
                                if (extractAll && !string.IsNullOrEmpty(fileOutputDir))
                                {
                                    // Check if this package should be skipped (non-essential assets)
                                    var packageAssetType = DetermineAssetType(exports, fileName, relativePath);
                                    bool shouldSkipPackage = packageAssetType.EndsWith("_Skip") || 
                                                             packageAssetType == "World_Skip" ||
                                                             packageAssetType == "Sounds_Skip" ||
                                                             packageAssetType == "Animations_Skip" ||
                                                             packageAssetType == "Physics_Skip" ||
                                                             packageAssetType == "VFX_Skip";
                                    
                                    foreach (var export in exports)
                                    {
                                        try
                                        {
                                            // Export meshes (only for interactive items, not world objects/scenery)
                                            if ((export is UStaticMesh || export is USkeletalMesh) && extractMeshes && !shouldSkipPackage)
                                            {
                                                Interlocked.Increment(ref meshCount);
                                                // Determine mesh output directory based on organization mode
                                                string meshOutputDir;
                                                if (smartHybridStructure)
                                                {
                                                    var meshAssetType = DetermineAssetType(new[] { export }, export.Name, relativePath);
                                                    // Skip if this specific mesh is from a skipped asset type
                                                    if (meshAssetType.EndsWith("_Skip")) continue;
                                                    meshOutputDir = Path.GetDirectoryName(BuildSmartHybridPath(outputDir, relativePath, meshAssetType, export.Name));
                                                }
                                                else if (hybridStructure)
                                                {
                                                    var meshAssetType = DetermineAssetType(new[] { export }, export.Name, relativePath);
                                                    var dirPath = Path.GetDirectoryName(relativePath)?.Replace("\\", "/") ?? "";
                                                    var cleanPath = dirPath;
                                                    if (cleanPath.StartsWith("SCUM/Content/ConZ_Files/"))
                                                        cleanPath = cleanPath.Substring("SCUM/Content/ConZ_Files/".Length);
                                                    else if (cleanPath.StartsWith("SCUM/Content/"))
                                                        cleanPath = cleanPath.Substring("SCUM/Content/".Length);
                                                    else if (cleanPath.StartsWith("SCUM/"))
                                                        cleanPath = cleanPath.Substring("SCUM/".Length);
                                                    var pathParts = cleanPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                                                    if (pathParts.Length > 0)
                                                    {
                                                        meshOutputDir = Path.Combine(outputDir, pathParts[0], meshAssetType);
                                                    }
                                                    else
                                                    {
                                                        meshOutputDir = Path.Combine(outputDir, meshAssetType);
                                                    }
                                                }
                                                else if (organizeByType)
                                                {
                                                    var meshAssetType = DetermineAssetType(new[] { export }, export.Name, relativePath);
                                                    meshOutputDir = Path.Combine(outputDir, meshAssetType);
                                                }
                                                else
                                                {
                                                    meshOutputDir = fileOutputDir;
                                                }
                                                
                                                var meshPath = Path.Combine(meshOutputDir, export.Name + ".glb");
                                                if (resume && IsValidMeshFile(meshPath))
                                                {
                                                    System.Threading.Interlocked.Increment(ref meshSkippedCount);
                                                    // Log individual skips occasionally (first 10, then every 1000th)
                                                    var skipCount = System.Threading.Interlocked.Read(ref meshSkippedCount);
                                                    if (skipCount <= 10 || skipCount % 1000 == 0)
                                                    {
                                                        var mode = fastResume ? "fast-resume" : "resume";
                                                        Log.Debug($"⏭️ [{mode}] Skipping mesh (exists): {export.Name}.glb");
                                                    }
                                                }
                                                else
                                                {
                                                    // Queue mesh for deferred extraction at the end
                                                    // This allows fast assets (JSON/textures) to complete first
                                                    // Meshes are slow (200-1000ms) and can hang, so process them all together at the end
                                                    MeshExtractionQueue.Enqueue((file, export.Name, meshOutputDir, mirrorStructure));
                                                    Interlocked.Increment(ref _meshQueued);
                                                    // Note: meshCount is incremented when mesh is actually extracted (in ProcessMeshQueue)
                                                }
                                            }
                                            // Export sounds (skip for database purposes - not useful for documenting player-item relationships)
                                            // Sounds are marked as Sounds_Skip in asset type detection
                                            if (export is USoundWave soundWave && extractSounds && !shouldSkipPackage)
                                            {
                                                // Check individual asset type - skip if it's a sounds asset
                                                var soundAssetType = DetermineAssetType(new[] { export }, soundWave.Name, relativePath);
                                                if (soundAssetType.EndsWith("_Skip") || soundAssetType == "Sounds_Skip") continue;
                                                
                                                Interlocked.Increment(ref soundCount);
                                                
                                                // Determine sound format and path for resume check
                                                string format;
                                                byte[] dummy;
                                                soundWave.Decode(true, out format, out dummy);
                                                var soundPath = Path.Combine(fileOutputDir, $"{soundWave.Name}.{format.ToLower()}");
                                                
                                                if (resume && IsValidSoundFile(soundPath))
                                                {
                                                    System.Threading.Interlocked.Increment(ref soundSkippedCount);
                                                    // Log individual skips occasionally (first 10, then every 1000th)
                                                    var skipCount = System.Threading.Interlocked.Read(ref soundSkippedCount);
                                                    if (skipCount <= 10 || skipCount % 1000 == 0)
                                                    {
                                                        var mode = fastResume ? "fast-resume" : "resume";
                                                        Log.Debug($"⏭️ [{mode}] Skipping sound (exists): {soundWave.Name}.{format}");
                                                    }
                                                }
                                                else
                                                {
                                                    ExportSound(soundWave, fileOutputDir, resume);
                                                }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            // Log but don't fail the whole export
                                            if (progressCount < 20)
                                            {
                                                Log.Debug($"Failed to export {export.Name}: {ex.Message}");
                                            }
                                        }
                                    }
                                }

                                // Progress indicator: more frequent updates early on and in resume mode
                                // Update every 10 files for first 100, then every 50 files, to show counters quickly
                                bool shouldUpdate = localProgress <= 100 && localProgress % 10 == 0 ||
                                                   localProgress > 100 && localProgress % 50 == 0 ||
                                                   localProgress % 100 == 0; // Always update at 100 boundary
                                
                                if (shouldUpdate)
                                {
                                    var elapsed = DateTime.Now - startTime;
                                    var currentSuccess = System.Threading.Interlocked.Read(ref successCount);
                                    var currentSkipped = System.Threading.Interlocked.Read(ref skippedCount);
                                    var currentTextureSuccess = System.Threading.Interlocked.Read(ref textureSuccessCount);
                                    var currentTextureCount = System.Threading.Interlocked.Read(ref textureCount);
                                    var currentMeshCount = System.Threading.Interlocked.Read(ref meshCount);
                                    var currentSoundCount = System.Threading.Interlocked.Read(ref soundCount);
                                    var currentErrors = System.Threading.Interlocked.Read(ref errorCount);
                                    var currentJsonSkipped = System.Threading.Interlocked.Read(ref skippedCount);
                                    var currentTextureSkipped = System.Threading.Interlocked.Read(ref textureSkippedCount);
                                    var currentMeshSkipped = System.Threading.Interlocked.Read(ref meshSkippedCount);
                                    var currentSoundSkipped = System.Threading.Interlocked.Read(ref soundSkippedCount);
                                    
                                    var rate = localProgress / elapsed.TotalSeconds;
                                    
                                    // Simple rate-based ETA calculation
                                    double remainingSeconds = 0;
                                    if (rate > 0)
                                    {
                                        remainingSeconds = (filesToExtract.Count - localProgress) / rate;
                                    }
                                    
                                    var remaining = remainingSeconds;
                                    var memoryGB = GC.GetTotalMemory(false) / (1024.0 * 1024 * 1024);
                                    
                                    // Update UI (consolidated)
                                    if (_progressUI != null)
                                    {
                                        var webpQueued = _webpConversionTask != null ? System.Threading.Interlocked.Read(ref _webpConversionQueued) : 0;
                                        var webpCompleted = _webpConversionTask != null ? System.Threading.Interlocked.Read(ref _webpConversionCompleted) : 0;
                                        var uiStatusMsg = _useGui 
                                            ? $"Extracting: {localProgress:N0}/{filesToExtract.Count:N0} files"
                                            : $"Extracting files... {localProgress:N0}/{filesToExtract.Count:N0} ({100.0 * localProgress / filesToExtract.Count:F1}%)";
                                        
                                        UpdateProgressUI(localProgress, filesToExtract.Count, currentSuccess, currentTextureSuccess, 
                                            currentTextureCount, currentMeshCount, currentSoundCount, currentErrors,
                                            currentJsonSkipped, currentTextureSkipped, currentMeshSkipped, currentSoundSkipped,
                                            rate, TimeSpan.FromSeconds(remaining), elapsed, memoryGB, uiStatusMsg, webpQueued, webpCompleted);
                                    }
                                    
                                    // Machine-readable log: Clean format for scripts/bots
                                    // Format: Progress: X/Y | JSON: A | Textures: B/C | Meshes: D | Sounds: E | Outputs: F | Est: G | Skipped: H (JSON:valid:I, Textures:valid:J, Meshes:valid:K, Sounds:valid:L) | Rate: M | ETA: N
                                    // In resume mode, also show skip vs process ratio
                                    var totalOutputs = currentSuccess + currentTextureSuccess + currentMeshCount + currentSoundCount;
                                    var estimateStr = hasEstimate ? $" | Est: {estimatedTotalOutputs:N0}" : "";
                                    
                                    // Build skipped string with reasons
                                    var skippedParts = new List<string>();
                                    if (resume && currentJsonSkipped > 0) skippedParts.Add($"JSON:valid:{currentJsonSkipped}");
                                    if (resume && currentTextureSkipped > 0) skippedParts.Add($"Textures:valid:{currentTextureSkipped}");
                                    if (resume && currentMeshSkipped > 0) skippedParts.Add($"Meshes:valid:{currentMeshSkipped}");
                                    if (resume && currentSoundSkipped > 0) skippedParts.Add($"Sounds:valid:{currentSoundSkipped}");
                                    var skippedStr = skippedParts.Count > 0 ? $" | Skipped: {string.Join(", ", skippedParts)}" : "";
                                    
                                    // Add skip vs process ratio in resume mode
                                    string skipRatioStr = "";
                                    if (resume && (currentJsonSkipped > 0 || currentTextureSkipped > 0 || currentMeshSkipped > 0 || currentSoundSkipped > 0))
                                    {
                                        var totalProcessed = currentSuccess + currentTextureSuccess + currentMeshCount + currentSoundCount;
                                        var totalSkipped = currentJsonSkipped + currentTextureSkipped + currentMeshSkipped + currentSoundSkipped;
                                        var total = totalProcessed + totalSkipped;
                                        if (total > 0)
                                        {
                                            var skipPercent = (totalSkipped * 100.0 / total);
                                            skipRatioStr = $" | Skip: {skipPercent:F1}% ({totalSkipped:N0}/{total:N0})";
                                        }
                                    }
                                    
                                    // Add new vs re-extracted info in progress (every 1000 files)
                                    string extractionTypeStr = "";
                                    if (localProgress % 1000 == 0)
                                    {
                                        var newCount = System.Threading.Interlocked.Read(ref newFilesExtracted);
                                        var reExtractedCount = System.Threading.Interlocked.Read(ref reExtractedFiles);
                                        if (newCount > 0 || reExtractedCount > 0)
                                        {
                                            extractionTypeStr = $" | New: {newCount:N0}, Re-extracted: {reExtractedCount:N0}";
                                        }
                                    }
                                    
                                    var statusMsg = $"Progress: {localProgress}/{filesToExtract.Count} | JSON: {currentSuccess} | Textures: {currentTextureSuccess}/{currentTextureCount} | Meshes: {currentMeshCount} | Sounds: {currentSoundCount} | Outputs: {totalOutputs}{estimateStr}{skippedStr}{skipRatioStr}{extractionTypeStr} | Rate: {rate:F1}/s | ETA: {TimeSpan.FromSeconds(remaining):hh\\:mm\\:ss}";
                                    Log.Information(statusMsg);
                                }
                                
                                // Check memory pressure periodically (every 50 files for better control on low-RAM systems)
                                // More frequent checks prevent OOM kills on systems with limited RAM
                                if (localProgress % 50 == 0)
                                {
                                    CheckMemoryPressure();
                                }
                            } // Close: if (exports != null && exports.Any())
                        } // Close: if (packageLoaded && package != null)
                        else
                        {
                            System.Threading.Interlocked.Increment(ref errorCount);
                        }
                    } // Close: try block
                    catch (OperationCanceledException)
                    {
                        // Extraction was cancelled (e.g., GUI closed)
                        throw; // Re-throw to stop Parallel.ForEach
                    }
                    catch (Exception ex)
                    {
                        // Check for cancellation before logging
                        cancellationToken.ThrowIfCancellationRequested();
                        
                        var currentErrors = System.Threading.Interlocked.Increment(ref errorCount);
                        
                        // Always log the file that failed to help identify stalls
                        var warningMsg = $"✗ Failed: {Path.GetFileName(file)} - {ex.Message}";
                        if (currentErrors <= 20) // Log first 20 errors with details
                        {
                            Log.Warning(warningMsg);
                            _warningsErrorsLogWriter?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | WARNING | {file} | {ex.Message}");
                        }
                        else if (currentErrors == 21) // Log once when we hit the limit
                        {
                            Log.Warning("Additional errors suppressed (too many). Check warnings_errors.log for full list.");
                        }
                        
                        // If this is a timeout or hang-related error, log it specially
                        if (ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) || 
                            ex.Message.Contains("hang", StringComparison.OrdinalIgnoreCase) ||
                            ex is TimeoutException)
                        {
                            Log.Error($"⚠️ Potential hang detected on file: {file} - {ex.Message}");
                        }
                    }
                });

                var totalElapsed = DateTime.Now - startTime;
                
                // Wait for async WebP conversion queue to drain (if enabled)
                if (_webpConversionTask != null && !_webpConversionCancellation.Token.IsCancellationRequested)
                {
                    Log.Information("Waiting for async WebP conversion queue to drain...");
                    var queueStart = DateTime.Now;
                    
                    // Wait for queue to empty and all conversions to complete (synchronous wait)
                    while (!WebPConversionQueue.IsEmpty || _webpConversionCompleted < _webpConversionQueued)
                    {
                        Thread.Sleep(100);
                        if ((DateTime.Now - queueStart).TotalSeconds > 300) // 5 minute timeout
                        {
                            Log.Warning("WebP conversion queue timeout - some conversions may be incomplete");
                            break;
                        }
                    }
                    
                    // Signal cancellation and wait for task to complete
                    _webpConversionCancellation.Cancel();
                    try
                    {
                        _webpConversionTask.Wait(TimeSpan.FromSeconds(60)); // Wait up to 60 seconds
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"Error waiting for WebP conversion task: {ex.Message}");
                    }
                    
                    var queueElapsed = DateTime.Now - queueStart;
                    Log.Information($"WebP conversion queue drained: {_webpConversionCompleted:N0} conversions completed in {queueElapsed.TotalSeconds:F1}s");
                    
                    // Process queued WebP retries (conversions that failed or timed out)
                    if (_webpRetryQueued > 0)
                    {
                        Log.Information("=== Processing WebP Retries ===");
                        Log.Information($"Processing {_webpRetryQueued:N0} queued WebP retries...");
                        var webpRetryStart = DateTime.Now;
                        
                        // Process WebP retry queue (re-queue items back to main queue for processing)
                        var webpRetryProcessed = 0L;
                        var webpRetryFailed = 0L;
                        while (WebPRetryQueue.TryDequeue(out var retryItem))
                        {
                            var (pngPath, webpPath, retryQuality, retryVerifyQuality, retryCount) = retryItem;
                            // Add null-safety checks
                            if (string.IsNullOrEmpty(pngPath) || string.IsNullOrEmpty(webpPath))
                            {
                                Log.Warning($"WebP retry item has null paths - skipping");
                                webpRetryFailed++;
                                continue;
                            }
                            
                            if (retryCount < MAX_WEBP_RETRIES)
                            {
                                // Re-queue to main queue for retry
                                WebPConversionQueue.Enqueue((pngPath, webpPath, retryQuality, retryVerifyQuality));
                                Interlocked.Increment(ref _webpConversionQueued);
                                webpRetryProcessed++;
                            }
                            else
                            {
                                Log.Warning($"WebP conversion exceeded max retries: {Path.GetFileName(pngPath)} → {Path.GetFileName(webpPath)}");
                                webpRetryFailed++;
                            }
                        }
                        
                        if (webpRetryProcessed > 0)
                        {
                            Log.Information($"Re-queued {webpRetryProcessed} WebP conversions for retry");
                            // Restart WebP conversion task if needed and wait for retries to complete
                            if (_webpConversionTask == null || _webpConversionTask.IsCompleted)
                            {
                                _webpConversionCancellation = new CancellationTokenSource();
                                _webpConversionTask = Task.Run(() => ProcessWebPConversionQueue(_webpConversionCancellation.Token));
                            }
                            
                            var retryWaitStart = DateTime.Now;
                            while (!WebPConversionQueue.IsEmpty && (DateTime.Now - retryWaitStart).TotalSeconds < 300)
                            {
                                Thread.Sleep(100);
                            }
                            
                            // Wait for queue to drain again
                            queueStart = DateTime.Now;
                            while (!WebPConversionQueue.IsEmpty || _webpConversionCompleted < _webpConversionQueued)
                            {
                                Thread.Sleep(100);
                                if ((DateTime.Now - queueStart).TotalSeconds > 300)
                                {
                                    Log.Warning("WebP retry queue timeout - some conversions may be incomplete");
                                    break;
                                }
                            }
                        }
                        
                        var webpRetryElapsed = DateTime.Now - webpRetryStart;
                        Log.Information($"WebP retry processing complete: {webpRetryProcessed} retried, {webpRetryFailed} failed in {webpRetryElapsed.TotalSeconds:F1}s");
                    }
                }
                
                // Process queued texture retries (textures that timed out waiting for semaphore)
                if ((extractTextures || extractIcons) && _textureRetryQueued > 0)
                {
                    Log.Information("=== Processing Texture Retries ===");
                    Log.Information($"Processing {_textureRetryQueued:N0} queued texture retries...");
                    var retryQueueStart = DateTime.Now;
                    
                    var (retrySuccess, retryFailed, retryVerifyDelta) = ProcessTextureRetryQueue(provider, format, quality, verifyQuality);
                    System.Threading.Interlocked.Add(ref textureSuccessCount, retrySuccess);
                    System.Threading.Interlocked.Add(ref textureFailCount, retryFailed);
                    System.Threading.Interlocked.Add(ref verifyCount, retryVerifyDelta);
                    
                    var retryQueueElapsed = DateTime.Now - retryQueueStart;
                    Log.Information($"Texture retry queue processing complete: {retrySuccess} successful, {retryFailed} failed in {retryQueueElapsed.TotalSeconds:F1}s");
                }
                
                // Process queued meshes at the end (after all other assets are done)
                if (extractMeshes && extractAll && _meshQueued > 0)
                {
                    Log.Information("=== Processing Queued Meshes ===");
                    Log.Information($"Processing {_meshQueued:N0} queued meshes...");
                    var meshQueueStart = DateTime.Now;
                    
                    var (meshCountDelta, meshSkippedDelta, errorCountDelta) = ProcessMeshQueue(provider, resume, mirrorStructure);
                    meshCount += meshCountDelta;
                    meshSkippedCount += meshSkippedDelta;
                    errorCount += errorCountDelta;
                    
                    var meshQueueElapsed = DateTime.Now - meshQueueStart;
                    var finalMeshQueued = Interlocked.Read(ref _meshQueued);
                    Log.Information($"Mesh queue processing complete: {finalMeshQueued:N0} meshes processed in {meshQueueElapsed.TotalSeconds:F1}s");
                }
                
                // Process queued mesh retries (meshes that timed out during export)
                if (extractMeshes && extractAll && _meshRetryQueued > 0)
                {
                    Log.Information("=== Processing Mesh Retries ===");
                    Log.Information($"Processing {_meshRetryQueued:N0} queued mesh retries...");
                    var meshRetryStart = DateTime.Now;
                    
                    var (meshRetryCount, meshRetrySkipped, meshRetryErrors) = ProcessMeshRetryQueue(provider, resume, mirrorStructure);
                    meshCount += meshRetryCount;
                    meshSkippedCount += meshRetrySkipped;
                    errorCount += meshRetryErrors;
                    
                    var meshRetryElapsed = DateTime.Now - meshRetryStart;
                    Log.Information($"Mesh retry processing complete: {meshRetryCount} successful, {meshRetryErrors} failed in {meshRetryElapsed.TotalSeconds:F1}s");
                }
                
                Log.Information("=== Export Complete ===");
                Log.Information($"Phase 2: Single-pass parallel processing completed");
                
                // Final memory check and summary
                var finalMemory = GC.GetTotalMemory(false) / (1024.0 * 1024 * 1024);
                Log.Information($"Final memory usage: {finalMemory:F2} GB");
                
                if (extractJson)
                {
                    var finalSuccess = System.Threading.Interlocked.Read(ref successCount);
                    var finalSkipped = System.Threading.Interlocked.Read(ref skippedCount);
                    var finalErrors = System.Threading.Interlocked.Read(ref errorCount);
                    
                    if (resume && finalSkipped > 0)
                    {
                        Log.Information($"JSON: {finalSuccess} extracted, {finalSkipped} skipped, {finalErrors} errors");
                        if (finalSuccess + finalErrors > 0)
                        {
                            Log.Information($"Success rate: {(finalSuccess * 100.0 / (finalSuccess + finalErrors)):F1}%");
                        }
                    }
                    else
                    {
                        Log.Information($"JSON: {finalSuccess} successful, {finalErrors} errors");
                        if (finalSuccess + finalErrors > 0)
                        {
                            Log.Information($"Success rate: {(finalSuccess * 100.0 / (finalSuccess + finalErrors)):F1}%");
                        }
                    }
                }
                
                if (extractTextures)
                {
                    var finalTextureSuccess = System.Threading.Interlocked.Read(ref textureSuccessCount);
                    var finalTextureFail = System.Threading.Interlocked.Read(ref textureFailCount);
                    var finalTextureSkipped = System.Threading.Interlocked.Read(ref textureSkippedCount);
                    var finalTextureCount = System.Threading.Interlocked.Read(ref textureCount);
                    
                    Log.Information($"Textures: {finalTextureSuccess} successful, {finalTextureFail} errors (out of {finalTextureCount} found)");
                    if (finalTextureCount > 0)
                    {
                        Log.Information($"Texture success rate: {(finalTextureSuccess * 100.0 / finalTextureCount):F1}%");
                    }
                    if (resume && finalTextureSkipped > 0)
                    {
                        Log.Information($"Textures skipped (resume): {finalTextureSkipped}");
                    }
                    
                    // Show failure reasons
                    if (textureFailureReasons.Count > 0)
                    {
                        Log.Information($"Texture failure reasons:");
                        foreach (var reason in textureFailureReasons.OrderByDescending(kvp => kvp.Value))
                        {
                            Log.Information($"  {reason.Key}: {reason.Value:N0}");
                        }
                    }
                }
                
                if (extractAll)
                {
                    var finalSound = System.Threading.Interlocked.Read(ref soundCount);
                    var finalSoundSkipped = System.Threading.Interlocked.Read(ref soundSkippedCount);
                    var finalMesh = System.Threading.Interlocked.Read(ref meshCount);
                    var finalMeshSkipped = System.Threading.Interlocked.Read(ref meshSkippedCount);
                    Log.Information($"Sounds: {finalSound} detected" + (resume && finalSoundSkipped > 0 ? $", {finalSoundSkipped} skipped (resume)" : ""));
                    Log.Information($"Meshes: {finalMesh} detected" + (resume && finalMeshSkipped > 0 ? $", {finalMeshSkipped} skipped (resume)" : ""));
                    
                    // Show mesh failure reasons
                    if (meshFailureReasons.Count > 0)
                    {
                        Log.Information($"Mesh failure reasons:");
                        foreach (var reason in meshFailureReasons.OrderByDescending(kvp => kvp.Value))
                        {
                            Log.Information($"  {reason.Key}: {reason.Value:N0}");
                        }
                    }
                    
                    // Show sound failure reasons
                    if (soundFailureReasons.Count > 0)
                    {
                        Log.Information($"Sound failure reasons:");
                        foreach (var reason in soundFailureReasons.OrderByDescending(kvp => kvp.Value))
                        {
                            Log.Information($"  {reason.Key}: {reason.Value:N0}");
                        }
                    }
                }
                
                Log.Information($"Time elapsed: {totalElapsed:hh\\:mm\\:ss}");
                var finalProgress = System.Threading.Interlocked.Read(ref progressCount);
                if (totalElapsed.TotalSeconds > 0)
                {
                    Log.Information($"Average rate: {finalProgress / totalElapsed.TotalSeconds:F1} items/sec");
                }
                
                // Check if extraction completed successfully (resume mode summary)
                if (resume)
                {
                    var resumeJsonSkipped = System.Threading.Interlocked.Read(ref skippedCount);
                    var resumeTextureSkipped = System.Threading.Interlocked.Read(ref textureSkippedCount);
                    var resumeMeshSkipped = System.Threading.Interlocked.Read(ref meshSkippedCount);
                    var resumeSoundSkipped = System.Threading.Interlocked.Read(ref soundSkippedCount);
                    var totalSkipped = resumeJsonSkipped + resumeTextureSkipped + resumeMeshSkipped + resumeSoundSkipped;
                    
                    Log.Information($"Resume mode summary:");
                    Log.Information($"  Files processed: {finalProgress:N0}/{filesToExtract.Count:N0}");
                    Log.Information($"  Skipped (valid): {totalSkipped:N0} (JSON: {resumeJsonSkipped}, Textures: {resumeTextureSkipped}, Meshes: {resumeMeshSkipped}, Sounds: {resumeSoundSkipped})");
                    
                    // Calculate total outputs created in THIS run (newly extracted files only)
                    var newlyExtracted = System.Threading.Interlocked.Read(ref successCount) + 
                                        System.Threading.Interlocked.Read(ref textureSuccessCount) + 
                                        System.Threading.Interlocked.Read(ref meshCount) + 
                                        System.Threading.Interlocked.Read(ref soundCount);
                    
                    // Get new vs re-extracted counts
                    var newFiles = System.Threading.Interlocked.Read(ref newFilesExtracted);
                    var reExtracted = System.Threading.Interlocked.Read(ref reExtractedFiles);
                    
                    Log.Information($"=== Detailed Extraction Summary ===");
                    Log.Information($"Files processed: {finalProgress:N0}/{filesToExtract.Count:N0}");
                    Log.Information($"New files extracted: {newFiles:N0}");
                    Log.Information($"Re-extracted files (validation failed): {reExtracted:N0}");
                    Log.Information($"Skipped (valid): {totalSkipped:N0}");
                    
                    // Show extracted file types
                    if (extractedFileTypes.Count > 0)
                    {
                        Log.Information($"Extracted file types:");
                        foreach (var fileType in extractedFileTypes.OrderByDescending(kvp => kvp.Value))
                        {
                            Log.Information($"  {fileType.Key}: {fileType.Value:N0}");
                        }
                    }
                    
                    if (reExtracted == 0 && finalProgress >= filesToExtract.Count)
                    {
                        Log.Information($"✅ Extraction complete! All files were already extracted and valid.");
                    }
                    else if (reExtracted > 0)
                    {
                        Log.Warning($"⚠️  {reExtracted:N0} files were re-extracted (validation failed or missing)");
                        Log.Warning($"   {newFiles:N0} new files extracted, {totalSkipped:N0} skipped (valid)");
                    }
                }
                
                Log.Information($"Output directory: {outputDir}");
                
                // Generate path mapping file (unless mirror mode - redundant)
                // Note: Path mappings are collected even for skipped packages in resume mode
                if (!mirrorStructure && PathMappings.Any())
                {
                    GeneratePathMappingFile(outputDir, smartHybridStructure, hybridStructure, organizeByType);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Fatal error: {ex.Message}");
                Log.Error(ex.StackTrace ?? "No stack trace available");
                Log.Error($"Exception type: {ex.GetType().Name}");
                if (ex.InnerException != null)
                {
                    Log.Error($"Inner exception: {ex.InnerException.Message}");
                }
                Environment.ExitCode = 1; // Set exit code for fatal errors
            }
            finally
            {
                try
                {
                    // Ensure WebP conversion task completes
                    if (_webpConversionTask != null && !_webpConversionTask.IsCompleted)
                    {
                        Log.Information("Waiting for WebP conversion task to complete...");
                        _webpConversionCancellation?.Cancel();
                        try
                        {
                            _webpConversionTask.Wait(TimeSpan.FromSeconds(30));
                        }
                        catch (Exception ex)
                        {
                            Log.Warning($"Error waiting for WebP conversion task: {ex.Message}");
                        }
                    }
                    
                    // Close log file writers
                    _successLogWriter?.Close();
                    _warningsErrorsLogWriter?.Close();
                    
                    // Stop UI
                    _progressUI?.Dispose();
                    
                    // Check if extraction was cancelled
                    if (_extractionCancellation?.IsCancellationRequested == true)
                    {
                        Log.Warning("⚠️ Extraction cancelled by user (GUI closed)");
                        Environment.ExitCode = 130; // SIGINT exit code
                    }
                    else
                    {
                        // Log final completion message
                        Log.Information("=== Program Exiting ===");
                        if (Environment.ExitCode == 0)
                        {
                            Log.Information("✅ Program completed successfully");
                        }
                        else
                        {
                            Log.Warning($"⚠️ Program exiting with code: {Environment.ExitCode}");
                        }
                    }
                }
                catch (Exception finalEx)
                {
                    Log.Error($"Error in finally block: {finalEx.Message}");
                    Environment.ExitCode = 1;
                }
            }
        }

        /// <summary>
        /// Export a UTexture to file using FModel/CUE4Parse.Example pattern
        /// </summary>
        /// <param name="texture">The texture to export</param>
        /// <param name="outputDir">Output directory path</param>
        /// <param name="fileName">Base file name (without extension)</param>
        /// <param name="format">Export format (webp, png, jpeg)</param>
        /// <param name="quality">Compression quality (0-100)</param>
        /// <param name="verifyQuality">Whether to verify image quality after encoding</param>
        /// <returns>True if export succeeded, false otherwise</returns>
        /// <summary>
        /// Decode a PF_BC7 texture to RGBA using the managed BCnEncoder.NET decoder.
        /// The native Detex build bundled with this tool cannot decode BC7, so we bypass it.
        /// </summary>
        private static CTexture[]? DecodeBc7Texture(UTexture texture)
        {
            try
            {
                var platformData = texture.PlatformData;
                if (platformData.Mips == null || platformData.Mips.Length == 0)
                    return null;
                var mip = platformData.Mips[0];
                if (mip?.BulkData?.Data is not { } raw || raw.Length == 0)
                    return null;
                int w = mip.SizeX, h = mip.SizeY;
                if (w <= 0 || h <= 0)
                    return null;

                var decoder = new BcDecoder();
                ColorRgba32[] colors = decoder.DecodeRaw(raw, w, h, CompressionFormat.Bc7);
                if (colors == null || colors.Length < w * h)
                    return null;

                var rgba = new byte[w * h * 4];
                for (int p = 0; p < w * h; p++)
                {
                    rgba[p * 4]     = colors[p].r;
                    rgba[p * 4 + 1] = colors[p].g;
                    rgba[p * 4 + 2] = colors[p].b;
                    rgba[p * 4 + 3] = colors[p].a;
                }

                if (textureDebugCount < 10)
                    Log.Debug($"✅ BC7 managed decode: {texture.Name} ({w}x{h})");

                return new[] { new CTexture(w, h, EPixelFormat.PF_R8G8B8A8, rgba) };
            }
            catch (Exception ex)
            {
                Log.Warning($"⚠ BC7 managed decode failed for {texture.Name}: {ex.Message}");
                _warningsErrorsLogWriter?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | WARNING | BC7 decode failed | {texture.Name} | {ex.Message}");
                return null;
            }
        }

        static int textureDebugCount = 0;  // Track debug messages
        static bool ExportTextureWithReason(UTexture texture, string outputDir, string fileName, string format, int quality, bool verifyQuality, out string? failureReason)
        {
            failureReason = null;
            bool result = ExportTexture(texture, outputDir, fileName, format, quality, verifyQuality);
            if (!result)
            {
                // Determine failure reason
                if (texture.Format == EPixelFormat.PF_BC6H)
                    failureReason = "BC6H format (unsupported)";
                else if (texture == null)
                    failureReason = "Null texture reference";
                else
                    failureReason = "Decode/encode error";
            }
            return result;
        }
        
        static bool ExportTexture(UTexture texture, string outputDir, string fileName, string format = "webp", int quality = 85, bool verifyQuality = false)
        {
            try
            {
                // Log first few export attempts
                if (textureDebugCount < 10)
                {
                    Log.Information($"🎨 Exporting texture: {texture.Name} (Type: {texture.GetType().Name}, Format: {texture.Format})");
                    textureDebugCount++;
                }
                
                // FModel skips BC6H format (causes crashes)
                if (texture.Format == EPixelFormat.PF_BC6H)
                {
                    if (textureDebugCount < 5)
                    {
                        var skipMsg = $"⏭ Skipping BC6H texture: {texture.Name}";
                        Log.Warning(skipMsg);
                        _warningsErrorsLogWriter?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | WARNING | BC6H texture skipped | {texture.Name}");
                    }
                    return false;
                }
                
                // Check for null texture reference (prevents NullReferenceException)
                if (texture == null)
                {
                    if (textureDebugCount < 5)
                    {
                        Log.Debug($"⚠ Skipping null texture reference");
                    }
                    return false;
                }
                
                // Prepare array to hold decoded textures
                CTexture[]? bitmaps = null;

                // BC7 textures: the bundled native Detex build cannot decode PF_BC7, so no image
                // would otherwise be produced. Decode the raw BC7 blocks in managed code via BCnEncoder.NET.
                var decodeStart = DateTime.Now;
                if (texture.Format == EPixelFormat.PF_BC7)
                {
                    bitmaps = DecodeBc7Texture(texture);
                }

                if (bitmaps == null)
                {
                // Handle different texture types (FModel pattern)
                // Large/complex textures take longer to decode
                try
                {
                    if (texture is UTexture2DArray textureArray)
                    {
                        // 2D texture arrays (multiple layers) - can be slow
                        if (textureDebugCount < 5)
                        {
                            Log.Debug($"📦 Decoding texture array: {texture.Name} (may take time)");
                        }
                        var decoded = textureArray.DecodeTextureArray(ETexturePlatform.DesktopMobile);
                        if (decoded == null)
                        {
                            Log.Debug($"⚠ DecodeTextureArray returned null for {texture.Name}");
                            return false;
                        }
                        bitmaps = decoded;
                    }
                    else if (texture is UTextureCube textureCube)
                    {
                        // Cubemaps (6 faces) - convert to panorama (FModel pattern) - can be slow
                        if (textureDebugCount < 5)
                        {
                            Log.Debug($"📦 Decoding cubemap: {texture.Name} (may take time)");
                        }
                        var decoded = textureCube.Decode(ETexturePlatform.DesktopMobile);
                        if (decoded == null)
                        {
                            Log.Debug($"⚠ Decode returned null for cubemap {texture.Name}");
                            return false;
                        }
                        bitmaps = new[] { decoded.ToPanorama() };
                    }
                    else
                    {
                        // Regular UTexture2D (most common)
                        var decoded = texture.Decode(ETexturePlatform.DesktopMobile);
                        if (decoded == null)
                        {
                            Log.Debug($"⚠ Decode returned null for {texture.Name}");
                            return false;
                        }
                        bitmaps = new[] { decoded };
                    }
                }
                catch (NullReferenceException)
                {
                    // Handle null reference errors gracefully (known issue with some texture formats)
                    if (textureDebugCount < 10)
                    {
                        var skipMsg = $"⏭ Skipping texture with null reference: {texture.Name} (Format: {texture.Format})";
                        Log.Debug(skipMsg);
                        _warningsErrorsLogWriter?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | WARNING | Texture skipped (null reference) | {texture.Name} | Format: {texture.Format}");
                    }
                    return false;
                }
                catch (Exception decodeEx) when (decodeEx.Message.Contains("Detex") || decodeEx.Message.Contains("not initialized"))
                {
                    // Handle Detex decompression errors gracefully (known unsupported formats)
                    if (textureDebugCount < 10)
                    {
                        var skipMsg = $"⏭ Skipping texture (Detex unsupported): {texture.Name} (Format: {texture.Format})";
                        Log.Debug(skipMsg);
                        _warningsErrorsLogWriter?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | WARNING | Texture skipped (Detex unsupported) | {texture.Name} | Format: {texture.Format}");
                    }
                    return false;
                }
                } // end if (bitmaps == null)

                if (bitmaps == null) return false;
                var decodeDuration = (DateTime.Now - decodeStart).TotalMilliseconds;
                if (decodeDuration > 500 && textureDebugCount < 10)
                {
                    Log.Debug($"⏱ Decode took {decodeDuration:F0}ms for {texture.Name} (large texture)");
                }
                
                // Export each bitmap (array textures may have multiple layers)
                int successCount = 0;
                for (int i = 0; i < bitmaps.Length; i++)
                {
                    var bitmap = bitmaps[i];
                    if (bitmap == null) continue;
                    
                    // Convert to SKBitmap for encoding
                    using var skBitmap = bitmap.ToSkBitmap();
                    
                    // Encode to requested format with specified quality
                    byte[] encodedBytes;
                    string extension;
                    
                    switch (format.ToLower())
                    {
                        case "png":
                            // PNG is lossless, quality affects compression level (0-100)
                            encodedBytes = skBitmap.Encode(SKEncodedImageFormat.Png, quality).ToArray();
                            extension = "png";
                            break;
                        
                        case "jpeg":
                            // JPEG is lossy, quality affects compression (0-100)
                            encodedBytes = skBitmap.Encode(SKEncodedImageFormat.Jpeg, quality).ToArray();
                            extension = "jpg";
                            break;
                        
                        case "webp":
                        default:
                            // EXTRACT-THEN-CONVERT: Extract as PNG first, then convert to WebP in background
                            // This eliminates SKBitmap memory accumulation and allows higher parallelism
                            if (_webpConversionTask != null && !_webpConversionCancellation.Token.IsCancellationRequested)
                            {
                                // Extract as PNG first (fast, lossless, frees memory immediately)
                                var pngFileName = bitmaps.Length > 1 
                                    ? $"{fileName}_{i}.png" 
                                    : $"{fileName}.png";
                                var webpFileName = bitmaps.Length > 1 
                                    ? $"{fileName}_{i}.webp" 
                                    : $"{fileName}.webp";
                                var pngPath = Path.Combine(outputDir, pngFileName);
                                var webpPath = Path.Combine(outputDir, webpFileName);
                                
                                // Ensure directory exists
                                EnsureDirectory(outputDir);
                                
                                // Encode to PNG (fast, lossless)
                                var pngBytes = skBitmap.Encode(SKEncodedImageFormat.Png, 100).ToArray();
                                
                                // Write PNG immediately (frees memory)
                                AtomicWriteBytes(pngPath, pngBytes);
                                
                                // Queue PNG path for WebP conversion (minimal memory footprint)
                                WebPConversionQueue.Enqueue((pngPath, webpPath, quality, verifyQuality));
                                Interlocked.Increment(ref _webpConversionQueued);
                                
                                // SKBitmap will be disposed by using statement - memory freed immediately
                                // Continue to next bitmap (WebP conversion happens in background)
                                successCount++; // Count as success since PNG is written
                                continue; // Continue to next bitmap in array
                            }
                            
                            // Fallback: synchronous WebP encoding (if async not available or disabled)
                            // WebP can be lossy or lossless depending on quality (0-100)
                            // Quality 100 = lossless, < 100 = lossy with better compression
                            encodedBytes = skBitmap.Encode(SKEncodedImageFormat.Webp, quality).ToArray();
                            extension = "webp";
                            break;
                    }
                    
                    // For array textures, append layer index
                    var outputFileName = bitmaps.Length > 1 
                        ? $"{fileName}_{i}.{extension}" 
                        : $"{fileName}.{extension}";
                    var outputPath = Path.Combine(outputDir, outputFileName);
                    
                    // Ensure directory exists before writing
                    EnsureDirectory(outputDir);
                    
                    // Atomic write to prevent partial files
                    AtomicWriteBytes(outputPath, encodedBytes);
                    
                    // Perform quality verification if requested
                    if (verifyQuality && textureDebugCount < 10)
                    {
                        try
                        {
                            // Load the saved file back
                            using var savedBitmap = SKBitmap.Decode(outputPath);
                            if (savedBitmap != null)
                            {
                                // Compare original vs saved
                                var report = ImageComparison.CompareImages(skBitmap, savedBitmap, format, quality);
                                Log.Information($"🔍 Quality Verification: {outputFileName}");
                                Log.Information($"   SSIM: {report.SSIM:F4} {(report.IsVisuallyIdentical ? "✓" : "✗")} | " +
                                              $"PSNR: {(double.IsPositiveInfinity(report.PSNR) ? "∞" : $"{report.PSNR:F1}dB")} | " +
                                              $"Result: {(report.IsLossless ? "Lossless" : report.IsNearLossless ? "Near-lossless" : report.IsVisuallyIdentical ? "Visually identical" : "Lossy")}");
                            }
                        }
                        catch (Exception vex)
                        {
                            var verifyWarning = $"   Verification failed: {vex.Message}";
                            Log.Warning(verifyWarning);
                            _warningsErrorsLogWriter?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | WARNING | Quality verification failed | {vex.Message}");
                        }
                    }
                    else if (textureDebugCount < 20)
                    {
                        var uncompressedSize = bitmap.Width * bitmap.Height * 4;
                        var reduction = 100.0 - (encodedBytes.Length * 100.0 / uncompressedSize);
                        Log.Information($"✅ Exported: {outputFileName} ({bitmap.Width}x{bitmap.Height}, {reduction:F0}% smaller)");
                    }
                    
                    // Write to success log (mapping will be collected at call site where we have context)
                    var relativeTexturePath = Path.GetRelativePath(outputDir, outputPath);
                    var textureType = fileName.StartsWith("ICO_", StringComparison.OrdinalIgnoreCase) ? "ICON" : "TEXTURE";
                    _successLogWriter?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {textureType} | {relativeTexturePath}");
                    
                    // Add to UI activity log (standardized format)
                    if (_progressUI != null)
                    {
                        var textureFileName = Path.GetFileNameWithoutExtension(outputFileName);
                        var textureExtension = Path.GetExtension(outputFileName);
                        if (textureFileName.StartsWith("ICO_", StringComparison.OrdinalIgnoreCase))
                        {
                            _progressUI.AddIconExport(textureFileName + textureExtension);
                        }
                        else
                        {
                            _progressUI.AddTextureExport(textureFileName + textureExtension);
                        }
                    }
                    
                    successCount++;
                }
                
                // Clear array reference to help GC
                Array.Clear(bitmaps, 0, bitmaps.Length);
                bitmaps = null!;
                
                return successCount > 0;
            }
            catch (NullReferenceException)
            {
                // Handle null reference errors gracefully (known issue with some texture formats)
                if (textureDebugCount < 10)
                {
                    var skipMsg = $"⏭ Skipping texture (null reference): {texture.Name}";
                    Log.Debug(skipMsg);
                    _warningsErrorsLogWriter?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | WARNING | Texture skipped (null reference) | {texture.Name}");
                }
                return false;
            }
            catch (Exception ex) when (ex.Message.Contains("Detex") || ex.Message.Contains("not initialized") || 
                                       (ex.InnerException != null && (ex.InnerException.Message.Contains("Detex") || ex.InnerException.Message.Contains("not initialized"))))
            {
                // Handle Detex decompression errors gracefully (known unsupported formats)
                if (textureDebugCount < 10)
                {
                    var skipMsg = $"⏭ Skipping texture (Detex unsupported): {texture.Name}";
                    Log.Debug(skipMsg);
                    _warningsErrorsLogWriter?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | WARNING | Texture skipped (Detex unsupported) | {texture.Name}");
                }
                return false;
            }
            catch (Exception ex)
            {
                // Log unexpected errors (not null reference or Detex issues)
                if (textureDebugCount < 20)
                {
                    var errorMsg = $"Error exporting {texture.Name}: {ex.Message}";
                    Log.Error($"❌ {errorMsg}");
                    _warningsErrorsLogWriter?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | ERROR | {errorMsg}");
                    if (_progressUI != null)
                        _progressUI.AddError(errorMsg);
                    if (ex.InnerException != null)
                    {
                        Log.Error($"   Inner: {ex.InnerException.Message}");
                    }
                }
                return false;
            }
        }
        
        /// <summary>
        /// Determine asset type category for organizing output
        /// Focuses on interactive items (Characters, Weapons, Vehicles, Items, Clothing, Attachments, Buildables)
        /// Uses path-based detection, ExportType matching, and type checking
        /// </summary>
        static string DetermineAssetType(IEnumerable<UObject> exports, string fileName = null, string relativePath = null)
        {
            if (!exports.Any()) return "Unknown";
            
            var firstExport = exports.First();
            var exportType = firstExport.ExportType.ToLower();
            
            // Path-based detection (most reliable for categorizing interactive items)
            if (!string.IsNullOrEmpty(relativePath))
            {
                var pathLower = relativePath.ToLower();
                
                // Interactive items - what players interact with
                if (pathLower.Contains("/characters/") || pathLower.Contains("/prisoner/"))
                    return "Characters";
                if (pathLower.Contains("/weapons/") || pathLower.Contains("/weapon/"))
                    return "Weapons";
                if (pathLower.Contains("/vehicles/") || pathLower.Contains("/vehicle/"))
                    return "Vehicles";
                if (pathLower.Contains("/items/"))
                    return "Items";
                if (pathLower.Contains("/clothes/") || pathLower.Contains("/clothing/") || pathLower.Contains("/male/") || pathLower.Contains("/female/"))
                    return "Clothing";
                if (pathLower.Contains("/attachment/") || pathLower.Contains("/attachments/"))
                    return "Attachments";
                if (pathLower.Contains("/buildings/") || pathLower.Contains("/building/") || pathLower.Contains("/structures/") || pathLower.Contains("/crafting/"))
                    return "Buildables";
            }
            
            // Type checking for specific interactive object types (before ExportType matching)
            foreach (var export in exports)
            {
                // Check parent class for blueprints to determine category
                try
                {
                    if (export.Class != null)
                    {
                        var className = export.Class.Name.ToString().ToLower();
                        if (className.Contains("character") && !className.Contains("animation"))
                            return "Characters";
                        if (className.Contains("weapon"))
                            return "Weapons";
                        if (className.Contains("vehicle"))
                            return "Vehicles";
                        if (className.Contains("item") && !className.Contains("data"))
                            return "Items";
                        if (className.Contains("clothing") || className.Contains("costume"))
                            return "Clothing";
                        if (className.Contains("attachment"))
                            return "Attachments";
                        if (className.Contains("building") || className.Contains("structure") || className.Contains("craftable"))
                            return "Buildables";
                    }
                }
                catch { /* Ignore errors accessing class info */ }
            }
            
            // ExportType matching for interactive items
            if (exportType.Contains("character") && !exportType.Contains("animation"))
                return "Characters";
            if (exportType.Contains("weapon"))
                return "Weapons";
            if (exportType.Contains("vehicle"))
                return "Vehicles";
            if (exportType.Contains("item") && !exportType.Contains("data"))
                return "Items";
            if (exportType.Contains("clothing") || exportType.Contains("costume"))
                return "Clothing";
            if (exportType.Contains("attachment"))
                return "Attachments";
            if (exportType.Contains("building") || exportType.Contains("structure") || exportType.Contains("craftable"))
                return "Buildables";
            
            // Check file name patterns
            if (!string.IsNullOrEmpty(fileName))
            {
                var fileNameLower = fileName.ToLower();
                if (fileNameLower.StartsWith("bp_") || fileNameLower.StartsWith("bp"))
                {
                    // Blueprint - try to determine type from name
                    if (fileNameLower.Contains("character") || fileNameLower.Contains("prisoner"))
                        return "Characters";
                    if (fileNameLower.Contains("weapon"))
                        return "Weapons";
                    if (fileNameLower.Contains("vehicle"))
                        return "Vehicles";
                    if (fileNameLower.Contains("item"))
                        return "Items";
                    if (fileNameLower.Contains("clothing") || fileNameLower.Contains("costume") || fileNameLower.Contains("clothes"))
                        return "Clothing";
                    if (fileNameLower.Contains("attachment"))
                        return "Attachments";
                    if (fileNameLower.Contains("building") || fileNameLower.Contains("structure") || fileNameLower.Contains("craftable"))
                        return "Buildables";
                    // Generic blueprint if no match
                    return "Blueprints";
                }
            }
            
            // Prioritize blueprints (after interactive item checks)
            if (exportType.Contains("blueprint")) return "Blueprints";
            
            // Visual assets for 3D previews (meshes, textures)
            if (exportType.Contains("texture")) return "Textures";
            if (exportType.Contains("staticmesh")) return "StaticMeshes";
            if (exportType.Contains("skeletalmesh")) return "SkeletalMeshes";
            if (exportType.Contains("material")) return "Materials";
            
            // UI elements (for vehicle UI, inventory UI, etc.)
            if (exportType.Contains("widget")) return "UI";
            
            // Type checking for visual assets
            foreach (var export in exports)
            {
                if (export is UTexture) return "Textures";
                if (export is UStaticMesh) return "StaticMeshes";
                if (export is USkeletalMesh) return "SkeletalMeshes";
            }
            
            // Data tables (may contain item data, loot tables, etc.)
            if (exportType.Contains("datatable")) return "DataTables";
            if (exportType.Contains("stringtable")) return "StringTables";
            if (exportType.Contains("curvetable")) return "CurveTables";
            
            // Skip non-essential assets (sounds, animations, physics, etc.)
            // These are not useful for documenting player-item relationships
            if (exportType.Contains("soundwave") || exportType.Contains("soundcue")) return "Sounds_Skip";
            if (exportType.Contains("animsequence") || exportType.Contains("animation") || exportType.Contains("animblueprint")) return "Animations_Skip";
            if (exportType.Contains("physicsasset") || exportType.Contains("bodysetup")) return "Physics_Skip";
            if (exportType.Contains("skeleton")) return "Skeletons_Skip";
            if (exportType.Contains("particle") || exportType.Contains("niagara")) return "VFX_Skip";
            if (exportType.Contains("landscape") || exportType.Contains("foliage")) return "World_Skip";
            if (exportType.Contains("navmesh") || exportType.Contains("spline")) return "World_Skip";
            
            // Generic mesh detection (fallback) - use firstExport
            if (firstExport.ExportType.Contains("Mesh", StringComparison.OrdinalIgnoreCase))
            {
                return "Meshes";
            }
            
            return "Other";
        }
        
        /// <summary>
        /// Build output path using smart-hybrid structure (preserves 2-3 levels of hierarchy)
        /// </summary>
        // --- Collision-safe texture naming --------------------------------------------
        // Two distinct UE packages can contain assets with the same name (e.g. the devs
        // named the 7.62x39mm pile texture "ICO_Cal_7_62x54mmR_Pile", identical to the
        // 7.62x54mmR pile). When the output directory is shared (flat / by-type modes),
        // exports collide. We collect every owner package's slug for a (name, outputDir)
        // key and deterministically let the lexicographically SMALLEST slug keep the flat
        // dir; all others are diverted into a per-slug subfolder so no file is lost and the
        // outcome is stable regardless of parallel extraction order.
        private static readonly ConcurrentDictionary<string, HashSet<string>> _textureNameSlugs = new();
        private static readonly ConcurrentDictionary<string, object> _textureNameLock = new();

        static string ResolveCollisionSafeTextureDir(string baseName, string relativePath, string intendedDir)
        {
            if (string.IsNullOrEmpty(intendedDir) || string.IsNullOrEmpty(relativePath))
                return intendedDir;
            var slug = TextureOwnerSlug(relativePath);
            if (string.IsNullOrEmpty(slug))
                return intendedDir;
            // Key by name + intended dir so only genuine same-destination collisions divert.
            var key = baseName.ToLowerInvariant() + "||" + intendedDir.ToLowerInvariant();
            var lockObj = _textureNameLock.GetOrAdd(key, _ => new object());
            lock (lockObj)
            {
                var set = _textureNameSlugs.GetOrAdd(key, _ => new HashSet<string>());
                set.Add(slug);
                // Deterministic flat owner: smallest slug (ordinal). The same slug always
                // maps to the same destination, so the result is stable across runs.
                var flatOwner = set.Min()!;
                var result = slug == flatOwner ? intendedDir : Path.Combine(intendedDir, slug);
                return result;
            }
        }

        static string TextureOwnerSlug(string relativePath)
        {
            var dir = Path.GetDirectoryName(relativePath?.Replace('\\', '/') ?? "");
            if (string.IsNullOrEmpty(dir))
                return "";
            var parts = dir.Split('/', StringSplitOptions.RemoveEmptyEntries);
            // Use the last two directory components as a stable, human-readable package id.
            var take = parts.Length >= 2 ? parts.Skip(parts.Length - 2).ToArray() : parts;
            var slug = string.Join("_", take);
            foreach (var c in Path.GetInvalidPathChars())
                slug = slug.Replace(c.ToString(), "_");
            return slug;
        }

        static string BuildSmartHybridPath(string outputDir, string relativePath, string assetType, string fileName)
        {
            var dirPath = Path.GetDirectoryName(relativePath)?.Replace("\\", "/") ?? "";
            
            // Remove common SCUM path prefixes to get to actual content folders
            var cleanPath = dirPath;
            if (cleanPath.StartsWith("SCUM/Content/ConZ_Files/"))
                cleanPath = cleanPath.Substring("SCUM/Content/ConZ_Files/".Length);
            else if (cleanPath.StartsWith("SCUM/Content/"))
                cleanPath = cleanPath.Substring("SCUM/Content/".Length);
            else if (cleanPath.StartsWith("SCUM/"))
                cleanPath = cleanPath.Substring("SCUM/".Length);
            
            // Preserve 2-3 levels of hierarchy (e.g., "Items/Weapons" or "Items/Food/Medical")
            var pathParts = cleanPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (pathParts.Length > 0)
            {
                // Preserve up to 2-3 levels, but cap at 3 to keep it flat
                var preservedLevels = Math.Min(pathParts.Length, 3);
                var categoryPath = string.Join("/", pathParts.Take(preservedLevels));
                
                // Create path: outputDir/CategoryPath/AssetType/filename
                return Path.Combine(outputDir, categoryPath, assetType, fileName);
            }
            else
            {
                // No folder hierarchy, just use type
                return Path.Combine(outputDir, assetType, fileName);
            }
        }
        
        /// <summary>
        /// Ensure directory exists (with caching to avoid repeated filesystem calls)
        /// Performance optimization: Reduces filesystem overhead by caching created directories
        /// </summary>
        static void EnsureDirectory(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            
            // Normalize path (use forward slashes, remove trailing separators)
            var normalizedPath = Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
            
            // Check cache first (fast path)
            if (CreatedDirectories.ContainsKey(normalizedPath))
                return;
            
            // Create directory and all parent directories
            Directory.CreateDirectory(normalizedPath);
            
            // Add to cache (thread-safe)
            CreatedDirectories.TryAdd(normalizedPath, 0);
        }
        
        /// <summary>
        /// Check memory pressure and trigger GC if needed
        /// Performance optimization: Prevents OOM by proactively managing memory
        /// </summary>
        static void CheckMemoryPressure()
        {
            if (MemoryThreshold == 0) return; // Not initialized yet
            
            var currentMemory = GC.GetTotalMemory(false);
            // More aggressive: Also check if we're approaching system memory limits
            // Dedicated extraction host: force aggressive GC only above the 8GB hard limit.
            var memoryLimitBytes = 8L * 1024 * 1024 * 1024; // 8GB hard limit (dedicated extraction host)
            bool exceedsLimit = currentMemory > memoryLimitBytes;
            bool exceedsThreshold = currentMemory > MemoryThreshold;
            
            if (exceedsThreshold || exceedsLimit)
            {
                // Collect diagnostic information before GC to understand what's driving memory usage
                var beforeGC = currentMemory / (1024.0 * 1024 * 1024);
                var webpQueued = _webpConversionTask != null ? Interlocked.Read(ref _webpConversionQueued) : 0;
                var webpCompleted = _webpConversionTask != null ? Interlocked.Read(ref _webpConversionCompleted) : 0;
                var webpQueueSize = webpQueued - webpCompleted;
                var textureSemaphoreAvailable = TextureProcessingSemaphore.CurrentCount;
                var textureSemaphoreInUse = 2 - textureSemaphoreAvailable; // 2 is the semaphore limit
                
                // Estimate memory per queued bitmap (rough estimate: 4 bytes per pixel for RGBA)
                // This is approximate - actual size depends on texture dimensions
                var estimatedWebPQueueMemoryMB = webpQueueSize * 2.0; // Rough estimate: 2MB per queued texture (conservative)
                
                var gcStart = DateTime.Now;
                // More aggressive GC for low-RAM systems
                // If memory exceeds limit (2GB), do full blocking GC
                if (exceedsLimit)
                {
                    // CRITICAL: Memory exceeded hard limit - force aggressive blocking GC
                    Log.Warning($"⚠️ Memory exceeded 8GB limit ({currentMemory / (1024.0 * 1024 * 1024):F2} GB) - forcing aggressive GC");
                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(1, GCCollectionMode.Forced, true, true); // Also collect gen 1
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                    GC.WaitForPendingFinalizers();
                }
                else
                {
                    // Standard GC (threshold exceeded but under limit)
                    GC.Collect(2, GCCollectionMode.Forced, false, true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Forced, false, true);
                }
                var gcDuration = (DateTime.Now - gcStart).TotalMilliseconds;

                var afterGC = GC.GetTotalMemory(false) / (1024.0 * 1024.0 * 1024);
                var freed = beforeGC - afterGC;
                
                // Build detailed diagnostic message
                var gcMsg = $"Memory pressure: {beforeGC:F2} GB -> {afterGC:F2} GB (freed {freed:F2} GB via GC)";
                if (gcDuration > 100)
                {
                    gcMsg += $" - GC pause: {gcDuration:F0}ms";
                }
                
                // Add memory component breakdown
                var diagnostics = new List<string>();
                if (webpQueueSize > 0)
                {
                    diagnostics.Add($"WebP queue: {webpQueueSize} bitmaps (~{estimatedWebPQueueMemoryMB:F1} MB)");
                }
                if (textureSemaphoreInUse > 0)
                {
                    diagnostics.Add($"Active textures: {textureSemaphoreInUse}/2 (semaphore slots in use)");
                }
                if (diagnostics.Count > 0)
                {
                    gcMsg += $" | Components: {string.Join(", ", diagnostics)}";
                }
                
                Log.Information($"⚠️ {gcMsg}");
                if (_useGui && _progressUI != null)
                {
                    _progressUI.GcTriggers++;
                    _progressUI.AddWarning(gcMsg);
                }
                else if (_progressUI != null)
                {
                    _progressUI.GcTriggers++;
                    _progressUI.AddWarning(gcMsg);
                }
            }
        }
        
        /// <summary>
        /// Show help information
        /// </summary>
        static void ShowHelp()
        {
            Console.WriteLine("CUE4Parse WebP CLI - SCUM Game Asset Extractor");
            Console.WriteLine();
            Console.WriteLine("USAGE:");
            Console.WriteLine("  CUE4ParseCLI [OPTIONS]");
            Console.WriteLine();
            Console.WriteLine("REQUIRED:");
            Console.WriteLine("  --input <path>           PAK directory path (required)");
            Console.WriteLine("  --pak-dir <path>         Alias for --input (required)");
            Console.WriteLine("  --output <path>          Output directory for extracted files (optional for --search)");
            Console.WriteLine();
            Console.WriteLine("ORGANIZATION MODES (choose one):");
            Console.WriteLine("  --smart-hybrid          Smart hybrid: preserve 2-3 levels (Items/Weapons/Blueprints/) [RECOMMENDED]");
            Console.WriteLine("  --hybrid-structure       Hybrid: type within first-level folder (Items/Blueprints/)");
            Console.WriteLine("  --mirror-structure       Mirror: exact game folder structure");
            Console.WriteLine("  (default)               Flat: organize by type only (Blueprints/, Textures/)");
            Console.WriteLine();
            Console.WriteLine("WHAT TO EXTRACT (all default to enabled):");
            Console.WriteLine("  --all                    Extract all asset types (meshes, sounds, etc.)");
            Console.WriteLine("  --no-json                Skip JSON blueprint extraction");
            Console.WriteLine("  --no-icons               Skip icon extraction");
            Console.WriteLine("  --no-textures            Skip texture extraction");
            Console.WriteLine("  --no-meshes              Skip 3D mesh extraction");
            Console.WriteLine();
            Console.WriteLine("FILTERING:");
            Console.WriteLine("  --filter <pattern>       Filter by path pattern (e.g., \"Items/Weapons\")");
            Console.WriteLine("  --icons-unfiltered      Extract all icons regardless of filter");
            Console.WriteLine();
            Console.WriteLine("OUTPUT FORMAT:");
            Console.WriteLine("  --format <webp|png|jpeg>  Image format (default: webp)");
            Console.WriteLine("  --quality <0-100>       Compression quality (default: 85)");
            Console.WriteLine();
            Console.WriteLine("FEATURES:");
            Console.WriteLine("  --resume                 Skip existing valid files (resume interrupted extraction)");
            Console.WriteLine("  --fast-resume            Fast resume: Skip validation, just check file existence (less CPU/RAM)");
            Console.WriteLine("  --verify                 Verify image quality (first 10 images)");
            Console.WriteLine("  --class-metadata         Export class structure metadata");
            Console.WriteLine("  --map-structure          Generate structure mapping only (no extraction)");
            Console.WriteLine("  --search <pattern>        Search for files in PAKs (display results only)");
            Console.WriteLine("  --search <pattern> --extract  Search and extract matching files (requires --output)");
            Console.WriteLine();
            Console.WriteLine("OUTPUT MODE:");
            Console.WriteLine("  (default)                Human-readable console UI with statistics and activity log");
            Console.WriteLine("  --gui                    GUI mode (Terminal.Gui) - Interactive graphical interface");
            Console.WriteLine("  --machine-readable       Automation-friendly: traditional console logging");
            Console.WriteLine("  --automation             Alias for --machine-readable");
            Console.WriteLine("  --no-ui                  Alias for --machine-readable (backward compatibility)");
            Console.WriteLine();
            Console.WriteLine("OTHER:");
            Console.WriteLine("  --help, -h               Show this help message");
            Console.WriteLine();
            Console.WriteLine("EXAMPLES:");
            Console.WriteLine("  # Smart hybrid structure (recommended)");
            Console.WriteLine("  CUE4ParseCLI --output ./exports --smart-hybrid --all");
            Console.WriteLine();
            Console.WriteLine("  # Extract only items with smart hybrid");
            Console.WriteLine("  CUE4ParseCLI --output ./exports --smart-hybrid --filter \"Items/\" --all");
            Console.WriteLine();
            Console.WriteLine("  # Extract only blueprints and textures");
            Console.WriteLine("  CUE4ParseCLI --output ./exports --smart-hybrid --no-meshes --no-sounds");
            Console.WriteLine();
            Console.WriteLine("  # Resume interrupted extraction");
            Console.WriteLine("  CUE4ParseCLI --output ./exports --smart-hybrid --all --resume");
            Console.WriteLine();
            Console.WriteLine("  # Search for files in PAKs (can run while extraction is running)");
            Console.WriteLine("  CUE4ParseCLI --search \"Items/Weapons\"");
            Console.WriteLine("  CUE4ParseCLI --search \"T_Icon\"");
            Console.WriteLine();
            Console.WriteLine("  # Search and extract matching files");
            Console.WriteLine("  CUE4ParseCLI --search \"Items/Weapons\" --extract --output ./exports --smart-hybrid");
            Console.WriteLine("  CUE4ParseCLI --search \"AK47\" --extract --output ./exports --all");
        }
        
        /// <summary>
        /// Export sound file (OGG, WAV, etc.)
        /// </summary>
        static bool ExportSound(USoundWave sound, string outputDir, bool checkResume = false)
        {
            try
            {
                // Decode sound to get format and data
                string format;
                byte[] audioData;
                sound.Decode(true, out format, out audioData);
                
                if (audioData != null && audioData.Length > 0)
                {
                    var outputPath = Path.Combine(outputDir, $"{sound.Name}.{format.ToLower()}");
                    EnsureDirectory(outputDir);
                    
                    // Note: Resume check is done at call site to properly track skipped count
                    // This function is called only when file needs to be exported
                    
                    // Atomic write to prevent partial files
                    AtomicWriteBytes(outputPath, audioData);
                    var soundMsg = $"Exported sound: {sound.Name}.{format.ToLower()} ({audioData.Length} bytes)";
                    Log.Information($"✅ {soundMsg}");
                    if (_useGui && _progressUI != null)
                        _progressUI.AddSoundExport($"{sound.Name}.{format.ToLower()}");
                    else if (_progressUI != null)
                        _progressUI.AddSoundExport($"{sound.Name}.{format.ToLower()}");
                    return true;
                }
                else
                {
                    Log.Debug("⚠️ Sound {Name} decoded but no audio data", sound.Name);
                }
            }
            catch (Exception ex)
            {
                var soundErrorMsg = $"Error exporting sound {sound.Name}: {ex.Message}";
                Log.Error(ex, "❌ {SoundErrorMsg}", soundErrorMsg);
                _warningsErrorsLogWriter?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | ERROR | {soundErrorMsg}");
                if (_useGui && _progressUI != null)
                    _progressUI.AddError(soundErrorMsg);
                else if (_progressUI != null)
                    _progressUI.AddError(soundErrorMsg);
            }
            return false;
        }
        
        /// <summary>
        /// Export 3D mesh (glTF format for player visualization)
        /// In mirror mode, preserves full game path structure
        /// In other modes, ensures flat path structure (no deep nesting)
        /// </summary>
        static bool ExportMesh(UObject meshExport, string outputDir, string meshName, bool checkResume = false, bool mirrorMode = false)
        {
            try
            {
                EnsureDirectory(outputDir);
                
                var expectedPath = Path.Combine(outputDir, meshName + ".glb");
                
                // Note: Resume check is done at call site to properly track skipped count
                // This function is called only when file needs to be exported
                
                // Use CUE4Parse-Conversion to export as glTF (best for web/viewers)
                var exporter = new Exporter(meshExport, new ExporterOptions
                {
                    LodFormat = ELodFormat.FirstLod,
                    MeshFormat = EMeshFormat.Gltf2, // glTF 2.0 - best for web viewers
                    ExportMaterials = false, // Skip materials to keep files smaller
                    Platform = ETexturePlatform.DesktopMobile
                });
                
                var outputDirInfo = new DirectoryInfo(outputDir);
                if (exporter.TryWriteToDir(outputDirInfo, out var label, out var savedFilePath))
                {
                    if (!string.IsNullOrEmpty(savedFilePath) && File.Exists(savedFilePath))
                    {
                        // In mirror mode, preserve the full path structure from the exporter
                        if (mirrorMode)
                        {
                            // File is already in the correct location with full path structure
                            var meshMsg = $"Exported mesh: {meshExport.Name}";
                            Log.Information($"✅ {meshMsg}");
                            if (_useGui && _progressUI != null)
                                _progressUI.AddMeshExport($"{meshExport.Name}.glb");
                            else if (_progressUI != null)
                                _progressUI.AddMeshExport($"{meshExport.Name}.glb");
                        }
                        // In other modes, flatten paths if the exporter created deep nesting
                        else if (savedFilePath != expectedPath && savedFilePath.Contains("SCUM/Content/"))
                        {
                            try
                            {
                                // Ensure target directory exists
                                EnsureDirectory(outputDir);
                                
                                // Move file to flat location (atomic - File.Move is atomic on most filesystems)
                                if (File.Exists(expectedPath))
                                {
                                    File.Delete(expectedPath); // Remove old file if exists
                                }
                                File.Move(savedFilePath, expectedPath, overwrite: true);
                                
                                // Clean up empty directories from deep nesting
                                var oldDir = Path.GetDirectoryName(savedFilePath);
                                while (!string.IsNullOrEmpty(oldDir) && oldDir != outputDir && Directory.Exists(oldDir))
                                {
                                    try
                                    {
                                        if (!Directory.EnumerateFileSystemEntries(oldDir).Any())
                                        {
                                            Directory.Delete(oldDir);
                                            oldDir = Path.GetDirectoryName(oldDir);
                                        }
                                        else
                                        {
                                            break;
                                        }
                                    }
                                    catch
                                    {
                                        break; // Stop if we can't delete
                                    }
                                }
                                
                                var meshMsg = $"Exported mesh: {meshExport.Name}";
                                Log.Information($"✅ {meshMsg}");
                                if (_useGui && _progressUI != null)
                                    _progressUI.AddMeshExport($"{meshExport.Name}.glb");
                                else if (_progressUI != null)
                                    _progressUI.AddMeshExport($"{meshExport.Name}.glb");
                            }
                            catch (Exception moveEx)
                            {
                                var warningMsg = $"Could not flatten mesh path for {meshExport.Name}: {moveEx.Message}";
                                Log.Warning($"⚠️ {warningMsg}");
                                if (_useGui && _progressUI != null)
                                    _progressUI.AddWarning(warningMsg);
                                else if (_progressUI != null)
                                    _progressUI.AddWarning(warningMsg);
                                // File is still exported, just in wrong location
                            }
                        }
                        else
                        {
                            // File is already in the expected location
                            var meshMsg = $"Exported mesh: {meshExport.Name}";
                            Log.Information($"✅ {meshMsg}");
                            if (_progressUI != null)
                                _progressUI.AddMeshExport($"{meshExport.Name}.glb");
                        }
                    }
                    else
                    {
                        Log.Information("✅ Exported mesh: {Name}", meshExport.Name);
                    }
                    return true;
                }
                else
                {
                    Log.Debug("⚠️ Mesh {Name} export attempt failed", meshExport.Name);
                }
            }
            catch (Exception ex)
            {
                var meshErrorMsg = $"Error exporting mesh {meshExport.Name}: {ex.Message}";
                Log.Error(ex, "❌ {MeshErrorMsg}", meshErrorMsg);
                _warningsErrorsLogWriter?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | ERROR | {meshErrorMsg}");
                if (_useGui && _progressUI != null)
                    _progressUI.AddError(meshErrorMsg);
                else if (_progressUI != null)
                    _progressUI.AddError(meshErrorMsg);
            }
            return false;
        }
        
        /// <summary>
        /// <summary>
        /// Generate path mapping file (JSON and CSV) showing original vs mirror vs organized paths
        /// </summary>
        static void GeneratePathMappingFile(string outputDir, bool smartHybrid, bool hybrid, bool organizeByType)
        {
            try
            {
                var mode = smartHybrid ? "smart-hybrid" : hybrid ? "hybrid" : organizeByType ? "by-type" : "unknown";
                var jsonPath = Path.Combine(outputDir, "path_mapping.json");
                var csvPath = Path.Combine(outputDir, "path_mapping.csv");
                
                Log.Information($"Generating path mapping file: {jsonPath}");
                
                var mappingData = new
                {
                    GeneratedAt = DateTime.UtcNow,
                    OrganizationMode = mode,
                    OutputDirectory = outputDir,
                    TotalMappings = PathMappings.Count,
                    Mappings = PathMappings.OrderBy(m => m.OriginalPath).Select(m => new
                    {
                        m.OriginalPath,
                        m.MirrorPath,
                        m.OrganizedPath,
                        m.AssetType,
                        m.FileName
                    }).ToArray()
                };
                
                var json = JsonConvert.SerializeObject(mappingData, Formatting.Indented);
                                        AtomicWriteText(jsonPath, json);
                
                // Also generate CSV
                using var writer = new StreamWriter(csvPath);
                writer.WriteLine("Original Path,Mirror Path,Organized Path,Asset Type,File Name");
                foreach (var mapping in PathMappings.OrderBy(m => m.OriginalPath))
                {
                    writer.WriteLine($"\"{mapping.OriginalPath}\",\"{mapping.MirrorPath}\",\"{mapping.OrganizedPath}\",\"{mapping.AssetType}\",\"{mapping.FileName}\"");
                }
                
                Log.Information($"Path mapping files generated: {jsonPath}, {csvPath}");
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to generate path mapping file: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Search for files in PAK archives matching a pattern (FModel-style: fast, lazy enumeration)
        /// Searches directly on provider.Files.Keys without materializing the full list first
        /// </summary>
        static void SearchFiles(DefaultFileProvider provider, string searchPattern, int totalFileCount)
        {
            Log.Information($"Searching for files matching pattern: '{searchPattern}'");
            Log.Information($"Searching through {totalFileCount:N0} indexed files...");
            
            // FModel-style: Search directly on provider.Files.Keys (lazy enumeration, no intermediate list)
            // This is much faster than materializing all files into a list first
            var matches = provider.Files.Keys
                .Where(f => f.Contains(searchPattern, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f)
                .ToList(); // Only materialize matching results
            
            Log.Information($"Found {matches.Count} matching files");
            
            if (matches.Count == 0)
            {
                Console.WriteLine($"No files found matching pattern: '{searchPattern}'");
                return;
            }
            
            // Group by file type for better organization
            var byType = matches
                .GroupBy(f => Path.GetExtension(f).ToLower())
                .OrderByDescending(g => g.Count())
                .ToList();
            
            Console.WriteLine();
            Console.WriteLine($"=== Search Results: {matches.Count} files found ===");
            Console.WriteLine();
            
            // Show summary by file type
            Console.WriteLine("Summary by file type:");
            foreach (var group in byType)
            {
                var ext = string.IsNullOrEmpty(group.Key) ? "(no extension)" : group.Key;
                Console.WriteLine($"  {ext}: {group.Count()} files");
            }
            
            Console.WriteLine();
            Console.WriteLine("=== All Matching Files ===");
            Console.WriteLine();
            
            // Display all matches (limit to first 1000 to avoid overwhelming output)
            int displayLimit = 1000;
            int displayed = 0;
            
            foreach (var file in matches)
            {
                Console.WriteLine(file);
                displayed++;
                
                if (displayed >= displayLimit)
                {
                    Console.WriteLine();
                    Console.WriteLine($"... and {matches.Count - displayLimit} more files (showing first {displayLimit})");
                    Console.WriteLine($"Use a more specific search pattern to narrow results.");
                    break;
                }
            }
            
            Console.WriteLine();
            Console.WriteLine($"Total: {matches.Count} files");
        }
        
        /// <summary>
        /// Search for files matching a pattern and extract them (FModel-style: fast, lazy enumeration)
        /// Searches directly on provider.Files.Keys without materializing the full list first
        /// This approach matches FModel's efficient loading: only indexes PAK files initially, searches lazily
        /// </summary>
        static void SearchAndExtractFiles(DefaultFileProvider provider, string searchPattern, int totalFileCount,
            string outputDir, bool extractJson, bool extractTextures, bool extractIcons, bool extractMeshes,
            bool extractSounds, bool extractAll, string format, int quality, bool verifyQuality,
            bool exportClassMetadata, bool smartHybridStructure, bool hybridStructure, bool mirrorStructure,
            bool resume, bool noFilterIcons, string? filter)
        {
            Log.Information($"Searching for files matching pattern: '{searchPattern}'");
            Log.Information($"Searching through {totalFileCount:N0} indexed files...");
            
            // FModel-style: Search directly on provider.Files.Keys (lazy enumeration, no intermediate list)
            // This is much faster than materializing all files into a list first
            var allMatches = provider.Files.Keys
                .Where(f => f.Contains(searchPattern, StringComparison.OrdinalIgnoreCase))
                .ToList(); // Only materialize matching results
            
            Log.Information($"Found {allMatches.Count} total files matching pattern");
            
            // Filter to extractable files (.uasset and .umap files)
            var extractableMatches = allMatches
                .Where(f => f.EndsWith(".uasset") || f.EndsWith(".umap"))
                .OrderBy(f => f)
                .ToList();
            
            // Also include related texture files if textures are enabled
            var filesToExtract = new List<string>(extractableMatches);
            
            // OPTIMIZATION: Sort files to improve I/O locality (same as main extraction)
            filesToExtract.Sort(StringComparer.OrdinalIgnoreCase);
            
            if (extractTextures || extractIcons)
            {
                // Find texture files that match the pattern or are related to matched assets
                var textureMatches = allMatches
                    .Where(f => (f.Contains("/Textures/") || f.Contains("/ICO_")) && f.EndsWith(".uasset"))
                    .ToList();
                
                filesToExtract.AddRange(textureMatches);
                Log.Information($"Including {textureMatches.Count} matching texture/icon files");
            }
            
            Log.Information($"Found {extractableMatches.Count} extractable asset files matching pattern");
            
            if (filesToExtract.Count == 0)
            {
                Console.WriteLine($"No extractable files found matching pattern: '{searchPattern}'");
                Console.WriteLine($"Total matches: {allMatches.Count} (none are .uasset/.umap files)");
                
                // Show what file types were found
                if (allMatches.Count > 0)
                {
                    var byFileType = allMatches
                        .GroupBy(f => Path.GetExtension(f).ToLower())
                        .OrderByDescending(g => g.Count())
                        .Take(5);
                    
                    Console.WriteLine("\nFile types found (top 5):");
                    foreach (var group in byFileType)
                    {
                        var ext = string.IsNullOrEmpty(group.Key) ? "(no extension)" : group.Key;
                        Console.WriteLine($"  {ext}: {group.Count()} files");
                    }
                }
                return;
            }
            
            // Show what will be extracted
            Console.WriteLine();
            Console.WriteLine($"=== Search and Extract: {filesToExtract.Count} files to extract ===");
            Console.WriteLine($"Pattern: '{searchPattern}'");
            Console.WriteLine($"Output directory: {outputDir}");
            Console.WriteLine();
            
            // Group by file type for summary
            var byType = filesToExtract
                .GroupBy(f => 
                {
                    if (f.Contains("/Textures/")) return "Textures";
                    if (f.Contains("/ICO_")) return "Icons";
                    if (f.Contains("/Items/")) return "Items";
                    return "Other";
                })
                .OrderByDescending(g => g.Count())
                .ToList();
            
            Console.WriteLine("Files to extract by category:");
            foreach (var group in byType)
            {
                Console.WriteLine($"  {group.Key}: {group.Count()} files");
            }
            Console.WriteLine();
            
            // Continue with normal extraction flow using the search results
            // We'll use the existing extraction logic but with our filtered file list
            Log.Information($"Starting extraction of {filesToExtract.Count} files...");
            Log.Information($"This may take several minutes...");
            
            // Determine thread count based on available memory and CPU cores for parallel processing
            // For I/O-bound operations (PAK file reading), we can use more threads than CPU cores
            var memoryInfo = GC.GetGCMemoryInfo();
            long totalMemoryGB = memoryInfo.TotalAvailableMemoryBytes / (1024L * 1024 * 1024);
            int processorCount = Environment.ProcessorCount;
            
            // More aggressive threading for I/O-bound extraction work:
            // - High memory (32GB+): Use all CPU cores (I/O can overlap with CPU work)
            // - Medium memory (16-32GB): Use 75% of cores
            // - Low memory (<16GB): Use 50% of cores (conservative for memory pressure)
            int maxThreads = totalMemoryGB >= 32 
                ? processorCount  // Use all cores - PAK reading is I/O-bound, CPU can process while waiting
                : totalMemoryGB >= 16 
                    ? Math.Max(4, (int)(processorCount * 0.75))  // 75% of cores
                    : Math.Max(4, processorCount / 2);  // 50% of cores (conservative)
            
            Log.Information($"System memory: {totalMemoryGB} GB, CPU cores: {processorCount}, Using: {maxThreads} threads for parallel processing");
            
            // Start async WebP conversion background task if WebP format is requested
            if (format.Equals("webp", StringComparison.OrdinalIgnoreCase))
            {
                Log.Information("Starting async WebP conversion pipeline (extract-then-convert: PNG → WebP)...");
                _webpConversionTask = Task.Run(() => ProcessWebPConversionQueue(_webpConversionCancellation.Token));
                
                // Enable WebP counter in UI
                if (_progressUI != null)
                {
                    _progressUI.WebPAsyncEnabled = true;
                }
            }
            
            // Workload modeling removed - was taking 7-10 minutes and not providing accurate ETAs
            // Using simple rate-based ETA calculations instead
            
            Log.Information($"Extraction mode: Search results (pattern: '{searchPattern}')");

            // Thread-safe counters (using long for Interlocked.Read compatibility)
            long successCount = 0;
            long errorCount = 0;
            long skippedCount = 0;
            long textureCount = 0;
            long textureSuccessCount = 0;
            long textureFailCount = 0;
            long textureSkippedCount = 0;
            long soundCount = 0;
            long soundSkippedCount = 0;
            long meshCount = 0;
            long meshSkippedCount = 0;
            long progressCount = 0;
            long verifyCount = 0;
            var startTime = DateTime.Now;
            
            // Dynamic estimation
            const int ESTIMATION_SAMPLE_SIZE = 2000;
            long totalOutputsCreated = 0;
            long estimatedTotalOutputs = 0;
            bool hasEstimate = false;

            // Parallel processing options
            var cancellationToken = _extractionCancellation?.Token ?? CancellationToken.None;
            var parallelOptionsWithCancel = new ParallelOptions 
            { 
                MaxDegreeOfParallelism = maxThreads,
                CancellationToken = cancellationToken
            };
            
            // Initialize UI counters
            if (_progressUI != null)
            {
                _progressUI.TotalFiles = filesToExtract.Count;
                _progressUI.Progress = 0;
            }
            
            // Reuse the extraction logic from the main flow
            // This is a simplified version that processes the search results
            Parallel.ForEach(filesToExtract, parallelOptionsWithCancel, file =>
            {
                var localProgress = System.Threading.Interlocked.Increment(ref progressCount);
                try
                {
                    // Use TryLoadPackage instead of LoadPackage (safer)
                    if (provider.TryLoadPackage(file, out var package))
                    {
                        // Get all exports from the package
                        var exports = package.GetExports();
                        
                        if (exports != null && exports.Any())
                        {
                            // Create output path matching FModel structure
                            var relativePath = file.Replace("/Game/", "").Replace(".uasset", "");
                            
                            // Determine output path based on organization mode
                            string outputPath;
                            string fileOutputDir;
                            var fileName = Path.GetFileName(relativePath);
                            string assetType;
                            
                            bool organizeByType = !mirrorStructure && !hybridStructure && !smartHybridStructure;
                            
                            if (smartHybridStructure)
                            {
                                assetType = DetermineAssetType(exports, fileName, relativePath);
                                outputPath = BuildSmartHybridPath(outputDir, relativePath, assetType, fileName);
                                fileOutputDir = Path.GetDirectoryName(outputPath);
                            }
                            else if (hybridStructure)
                            {
                                assetType = DetermineAssetType(exports, fileName, relativePath);
                                var dirPath = Path.GetDirectoryName(relativePath)?.Replace("\\", "/") ?? "";
                                
                                var cleanPath = dirPath;
                                if (cleanPath.StartsWith("SCUM/Content/ConZ_Files/"))
                                    cleanPath = cleanPath.Substring("SCUM/Content/ConZ_Files/".Length);
                                else if (cleanPath.StartsWith("SCUM/Content/"))
                                    cleanPath = cleanPath.Substring("SCUM/Content/".Length);
                                else if (cleanPath.StartsWith("SCUM/"))
                                    cleanPath = cleanPath.Substring("SCUM/".Length);
                                
                                var pathParts = cleanPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                                if (pathParts.Length > 0)
                                {
                                    var majorFolder = pathParts[0];
                                    outputPath = Path.Combine(outputDir, majorFolder, assetType, fileName);
                                }
                                else
                                {
                                    outputPath = Path.Combine(outputDir, assetType, fileName);
                                }
                                fileOutputDir = Path.GetDirectoryName(outputPath);
                            }
                            else if (organizeByType)
                            {
                                assetType = DetermineAssetType(exports, fileName, relativePath);
                                outputPath = Path.Combine(outputDir, assetType, fileName);
                                fileOutputDir = Path.GetDirectoryName(outputPath);
                            }
                            else
                            {
                                assetType = "Unknown";
                                outputPath = Path.Combine(outputDir, relativePath);
                                fileOutputDir = Path.GetDirectoryName(outputPath);
                            }
                            
                            // Extract based on flags (reuse existing extraction methods)
                            // JSON extraction
                            if (extractJson)
                            {
                                var jsonPath = outputPath + ".json";
                                EnsureDirectory(fileOutputDir);
                                
                                if (!resume || !IsValidJsonFile(jsonPath))
                                {
                                    var json = JsonConvert.SerializeObject(exports, Formatting.Indented);
                                    AtomicWriteText(jsonPath, json);
                                    System.Threading.Interlocked.Increment(ref successCount);
                                    if (_progressUI != null)
                                    {
                                        _progressUI.AddJsonExport(fileName);
                                        _progressUI.JsonCount = System.Threading.Interlocked.Read(ref successCount);
                                    }
                                    Log.Debug($"Exported JSON: {jsonPath}");
                                }
                                else
                                {
                                    System.Threading.Interlocked.Increment(ref skippedCount);
                                    if (_progressUI != null)
                                    {
                                        _progressUI.JsonSkipped = System.Threading.Interlocked.Read(ref skippedCount);
                                    }
                                }
                            }
                            
                            // Texture/Icon extraction (reuse existing texture extraction logic)
                            if (extractTextures || extractIcons)
                            {
                                foreach (var export in exports)
                                {
                                    if (export is UTexture2D texture)
                                    {
                                        var textureFileName = texture.Name;
                                        var textureOutputPath = Path.Combine(fileOutputDir, $"{textureFileName}.{format}");
                                        EnsureDirectory(fileOutputDir);
                                        
                                        System.Threading.Interlocked.Increment(ref textureCount);
                                        
                                        // Resume check: For WebP format, check both WebP and PNG files
                                        if (resume)
                                        {
                                            if (File.Exists(textureOutputPath) && IsValidImageFile(textureOutputPath, format))
                                            {
                                                // WebP already exists, skip
                                                System.Threading.Interlocked.Increment(ref textureSkippedCount);
                                                continue;
                                            }
                                            else if (format.Equals("webp", StringComparison.OrdinalIgnoreCase))
                                            {
                                                // Check if PNG exists (from previous extraction or interrupted conversion)
                                                var pngPath = Path.Combine(fileOutputDir, $"{textureFileName}.png");
                                                if (File.Exists(pngPath) && IsValidImageFile(pngPath, "png"))
                                                {
                                                    // PNG exists, queue for WebP conversion
                                                    WebPConversionQueue.Enqueue((pngPath, textureOutputPath, quality, false));
                                                    Interlocked.Increment(ref _webpConversionQueued);
                                                    System.Threading.Interlocked.Increment(ref textureSkippedCount);
                                                    continue; // Skip extraction, will convert PNG → WebP
                                                }
                                            }
                                        }
                                        
                                        try
                                        {
                                            var decoded = texture.Decode(ETexturePlatform.DesktopMobile);
                                            if (decoded != null)
                                            {
                                                using var skBitmap = decoded.ToSkBitmap();
                                                var imageFormat = format.ToLower() == "webp" ? SKEncodedImageFormat.Webp :
                                                                  format.ToLower() == "png" ? SKEncodedImageFormat.Png :
                                                                  SKEncodedImageFormat.Jpeg;
                                                var encodedBytes = skBitmap.Encode(imageFormat, quality).ToArray();
                                                if (encodedBytes != null && encodedBytes.Length > 0)
                                                {
                                                    AtomicWriteBytes(textureOutputPath, encodedBytes);
                                                    System.Threading.Interlocked.Increment(ref textureSuccessCount);
                                                    if (_progressUI != null)
                                                    {
                                                        if (extractIcons && textureFileName.StartsWith("ICO_"))
                                                            _progressUI.AddIconExport(textureFileName);
                                                        else
                                                            _progressUI.AddTextureExport(textureFileName);
                                                        _progressUI.TextureSuccess = System.Threading.Interlocked.Read(ref textureSuccessCount);
                                                        _progressUI.TextureTotal = System.Threading.Interlocked.Read(ref textureCount);
                                                    }
                                                }
                                            }
                                        }
                                        catch (Exception texEx)
                                        {
                                            System.Threading.Interlocked.Increment(ref textureFailCount);
                                            var errorMsg = $"Error exporting texture {textureFileName}: {texEx.Message}";
                                            Log.Warning(errorMsg);
                                            if (_warningsErrorsLogWriter != null)
                                                _warningsErrorsLogWriter.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {errorMsg}");
                                        }
                                    }
                                }
                            }
                            
                            // Mesh extraction
                            if (extractMeshes)
                            {
                                foreach (var export in exports)
                                {
                                    if (export is USkeletalMesh skeletalMesh || export is UStaticMesh staticMesh)
                                    {
                                        var meshFileName = export.Name;
                                        var meshOutputPath = Path.Combine(fileOutputDir, $"{meshFileName}.obj");
                                        EnsureDirectory(fileOutputDir);
                                        
                                        if (resume && File.Exists(meshOutputPath) && new FileInfo(meshOutputPath).Length > 100)
                                        {
                                            System.Threading.Interlocked.Increment(ref meshSkippedCount);
                                            continue;
                                        }
                                        
                                        try
                                        {
                                            bool exported = false;
                                            if (export is USkeletalMesh skMesh)
                                            {
                                                var meshPath = Path.Combine(fileOutputDir, $"{meshFileName}.glb");
                                                if (resume && File.Exists(meshPath) && new FileInfo(meshPath).Length > 100)
                                                {
                                                    System.Threading.Interlocked.Increment(ref meshSkippedCount);
                                                    continue;
                                                }
                                                exported = ExportMesh(skMesh, fileOutputDir, meshFileName, resume, mirrorStructure);
                                            }
                                            else if (export is UStaticMesh stMesh)
                                            {
                                                var meshPath = Path.Combine(fileOutputDir, $"{meshFileName}.glb");
                                                if (resume && File.Exists(meshPath) && new FileInfo(meshPath).Length > 100)
                                                {
                                                    System.Threading.Interlocked.Increment(ref meshSkippedCount);
                                                    continue;
                                                }
                                                
                                                // Wrap mesh export in timeout to prevent stalls
                                                var meshExportTask = Task.Run(() => 
                                                {
                                                    try
                                                    {
                                                        return ExportMesh(stMesh, fileOutputDir, meshFileName, resume, mirrorStructure);
                                                    }
                                                    catch
                                                    {
                                                        return false;
                                                    }
                                                });
                                                
                                                if (meshExportTask.Wait(TimeSpan.FromSeconds(MeshExportTimeoutSeconds)))
                                                {
                                                    exported = meshExportTask.Result;
                                                }
                                                else
                                                {
                                                    // Timeout - log and skip to prevent stall
                                                    var timeoutMsg = $"⏱️ Mesh export timeout (>{MeshExportTimeoutSeconds}s): {meshFileName} - skipping to prevent stall";
                                                    Log.Warning(timeoutMsg);
                                                    _warningsErrorsLogWriter?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | TIMEOUT | {timeoutMsg}");
                                                    System.Threading.Interlocked.Increment(ref errorCount);
                                                    if (_progressUI != null)
                                                    {
                                                        _progressUI.AddWarning($"Mesh timeout: {meshFileName}");
                                                    }
                                                    exported = false;
                                                }
                                            }
                                            
                                            if (exported)
                                            {
                                                System.Threading.Interlocked.Increment(ref meshCount);
                                                if (_progressUI != null)
                                                {
                                                    _progressUI.AddMeshExport(meshFileName);
                                                    _progressUI.MeshCount = System.Threading.Interlocked.Read(ref meshCount);
                                                }
                                            }
                                        }
                                        catch (Exception meshEx)
                                        {
                                            System.Threading.Interlocked.Increment(ref errorCount);
                                            var errorMsg = $"Error exporting mesh {meshFileName}: {meshEx.Message}";
                                            Log.Warning(errorMsg);
                                        }
                                    }
                                }
                            }
                            
                            // Sound extraction
                            if (extractSounds)
                            {
                                foreach (var export in exports)
                                {
                                    if (export is USoundWave sound)
                                    {
                                        var soundFileName = sound.Name;
                                        var soundOutputDir = Path.Combine(fileOutputDir, "Sounds");
                                        EnsureDirectory(soundOutputDir);
                                        
                                        // Check if sound file already exists (resume mode)
                                        // ExportSound will determine the actual format, so we check common formats
                                        var soundPathOgg = Path.Combine(soundOutputDir, $"{soundFileName}.ogg");
                                        var soundPathWav = Path.Combine(soundOutputDir, $"{soundFileName}.wav");
                                        if (resume && ((File.Exists(soundPathOgg) && new FileInfo(soundPathOgg).Length > 100) ||
                                                       (File.Exists(soundPathWav) && new FileInfo(soundPathWav).Length > 100)))
                                        {
                                            System.Threading.Interlocked.Increment(ref soundSkippedCount);
                                            continue;
                                        }
                                        
                                        if (ExportSound(sound, soundOutputDir, resume))
                                        {
                                            System.Threading.Interlocked.Increment(ref soundCount);
                                            if (_progressUI != null)
                                            {
                                                _progressUI.SoundCount = System.Threading.Interlocked.Read(ref soundCount);
                                            }
                                        }
                                        else
                                        {
                                            System.Threading.Interlocked.Increment(ref errorCount);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    
                    // Update progress
                    var currentProgress = System.Threading.Interlocked.Read(ref progressCount);
                    var elapsed = DateTime.Now - startTime;
                    
                    if (_progressUI != null)
                    {
                        _progressUI.Progress = currentProgress;
                        _progressUI.Elapsed = elapsed;
                        
                        // Update WebP conversion queue counters
                        if (_webpConversionTask != null)
                        {
                            _progressUI.WebPQueued = System.Threading.Interlocked.Read(ref _webpConversionQueued);
                            _progressUI.WebPCompleted = System.Threading.Interlocked.Read(ref _webpConversionCompleted);
                        }

                        if (currentProgress > 0 && currentProgress % 10 == 0)
                        {
                            var rate = currentProgress / elapsed.TotalSeconds;
                            _progressUI.Rate = rate;
                            
                            if (filesToExtract.Count > 0)
                            {
                                double remainingSeconds = 0;
                            // Simple rate-based ETA calculation
                            if (rate > 0)
                            {
                                remainingSeconds = (filesToExtract.Count - currentProgress) / rate;
                            }
                                
                                var eta = TimeSpan.FromSeconds(remainingSeconds);
                                _progressUI.ETA = eta;
                            }
                            
                            _progressUI.RequestUpdate();
                        }
                    }
                    
                    // Update GUI if enabled
                    if (_progressUI != null && currentProgress > 0 && currentProgress % 10 == 0)
                    {
                        var rate = currentProgress / elapsed.TotalSeconds;
                        _progressUI.Progress = currentProgress;
                        _progressUI.TotalFiles = filesToExtract.Count;
                        _progressUI.Elapsed = elapsed;
                        _progressUI.Rate = rate;
                        
                        if (filesToExtract.Count > 0)
                        {
                            double remainingSeconds = 0;
                            // Simple rate-based ETA calculation
                            if (rate > 0)
                            {
                                remainingSeconds = (filesToExtract.Count - currentProgress) / rate;
                            }
                            
                            _progressUI.ETA = TimeSpan.FromSeconds(remainingSeconds);
                        }
                        
                        _progressUI.StatusMessage = $"Extracting: {currentProgress:N0}/{filesToExtract.Count:N0} files";
                    }
                }
                catch (Exception ex)
                {
                    System.Threading.Interlocked.Increment(ref errorCount);
                    var errorMsg = $"Error processing {file}: {ex.Message}";
                    Log.Warning(errorMsg);
                    if (_warningsErrorsLogWriter != null)
                        _warningsErrorsLogWriter.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {errorMsg}");
                }
                
                // Check memory pressure periodically (every 50 files for better control)
                if (localProgress % 50 == 0)
                {
                    CheckMemoryPressure();
                }
            });
            
            // Final summary
            var finalSuccess = System.Threading.Interlocked.Read(ref successCount);
            var finalSkipped = System.Threading.Interlocked.Read(ref skippedCount);
            var finalErrors = System.Threading.Interlocked.Read(ref errorCount);
            var finalTextureSuccess = System.Threading.Interlocked.Read(ref textureSuccessCount);
            var finalTextureFail = System.Threading.Interlocked.Read(ref textureFailCount);
            var finalTextureSkipped = System.Threading.Interlocked.Read(ref textureSkippedCount);
            var finalTextureCount = System.Threading.Interlocked.Read(ref textureCount);
            var finalSound = System.Threading.Interlocked.Read(ref soundCount);
            var finalSoundSkipped = System.Threading.Interlocked.Read(ref soundSkippedCount);
            var finalMesh = System.Threading.Interlocked.Read(ref meshCount);
            var finalMeshSkipped = System.Threading.Interlocked.Read(ref meshSkippedCount);
            var finalProgress = System.Threading.Interlocked.Read(ref progressCount);
            
            var totalTime = DateTime.Now - startTime;
            
            Console.WriteLine();
            Console.WriteLine("=== Extraction Complete ===");
            Console.WriteLine($"Files processed: {finalProgress}/{filesToExtract.Count}");
            Console.WriteLine($"JSON files: {finalSuccess} exported, {finalSkipped} skipped");
            Console.WriteLine($"Textures: {finalTextureSuccess} exported, {finalTextureFail} failed, {finalTextureSkipped} skipped");
            Console.WriteLine($"Meshes: {finalMesh} exported, {finalMeshSkipped} skipped");
            Console.WriteLine($"Sounds: {finalSound} exported, {finalSoundSkipped} skipped");
            Console.WriteLine($"Errors: {finalErrors}");
            Console.WriteLine($"Total time: {totalTime:hh\\:mm\\:ss}");
            
            Log.Information($"Search and extract complete: {finalProgress} files processed in {totalTime:hh\\:mm\\:ss}");
        }
        
        /// <summary>
        /// Generate a complete structure mapping file showing mirror vs organized structure
        /// </summary>
        static void GenerateStructureMap(DefaultFileProvider provider, List<string> allFiles, string outputDir, string filter, bool extractAll)
        {
            Log.Information("Generating structure map...");
            
            // Filter files to process
            List<string> filesToMap;
            
            if (extractAll)
            {
                filesToMap = allFiles
                    .Where(f => f.EndsWith(".uasset") || f.EndsWith(".umap"))
                    .ToList();
            }
            else
            {
                filesToMap = allFiles
                    .Where(f => f.Contains("/Items/") && f.EndsWith(".uasset"))
                    .ToList();
            }
            
            // Apply filter if specified
            if (filter != null)
            {
                filesToMap = filesToMap
                    .Where(f => f.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                Log.Information($"Filtered to {filesToMap.Count} files matching: {filter}");
            }
            
            Log.Information($"Processing {filesToMap.Count} files...");
            
            // Create mapping data
            var mappings = new ConcurrentBag<(string original, string mirror, string organized, string type)>();
            int processed = 0;
            
            Parallel.ForEach(filesToMap, new ParallelOptions { MaxDegreeOfParallelism = 4 }, file =>
            {
                try
                {
                    if (provider.TryLoadPackage(file, out var package))
                    {
                        var exports = package.GetExports();
                        if (exports != null && exports.Any())
                        {
                            var relativePath = file.Replace("/Game/", "").Replace(".uasset", "");
                            var fileName = Path.GetFileName(relativePath);
                            var assetType = DetermineAssetType(exports, fileName, relativePath);
                            
                            // Mirror structure path (relative)
                            var mirrorPath = relativePath;
                            
                            // Organized structure path (relative)
                            var organizedPath = $"{assetType}/{fileName}";
                            
                            mappings.Add((file, mirrorPath, organizedPath, assetType));
                        }
                    }
                    
                    var count = System.Threading.Interlocked.Increment(ref processed);
                    if (count % 100 == 0)
                    {
                        Log.Information($"Progress: {count}/{filesToMap.Count} files processed");
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug($"Failed to process {file}: {ex.Message}");
                }
            });
            
            // Write mapping to file
            var mapFilePath = Path.Combine(outputDir, "structure_map.txt");
            var csvFilePath = Path.Combine(outputDir, "structure_map.csv");
            
            Log.Information($"Writing structure map to: {mapFilePath}");
            
            using (var writer = new StreamWriter(mapFilePath))
            {
                writer.WriteLine("=== CUE4Parse Structure Mapping ===");
                writer.WriteLine($"Generated: {DateTime.Now}");
                writer.WriteLine($"Total Files: {mappings.Count}");
                writer.WriteLine();
                writer.WriteLine("This file shows the mapping between two extraction modes:");
                writer.WriteLine("  1. Mirror Structure: Preserves the original game folder hierarchy");
                writer.WriteLine("  2. Organized by Type: Groups assets by their type (Textures, Blueprints, etc.)");
                writer.WriteLine();
                writer.WriteLine(new string('=', 120));
                writer.WriteLine();
                
                var sortedMappings = mappings.OrderBy(m => m.type).ThenBy(m => m.original).ToList();
                
                foreach (var mapping in sortedMappings)
                {
                    writer.WriteLine($"Original Path:    {mapping.original}");
                    writer.WriteLine($"Mirror Structure: {mapping.mirror}");
                    writer.WriteLine($"Organized (Type): {mapping.organized}");
                    writer.WriteLine($"Asset Type:       {mapping.type}");
                    writer.WriteLine();
                }
            }
            
            // Also write CSV for easy import
            Log.Information($"Writing CSV map to: {csvFilePath}");
            
            using (var writer = new StreamWriter(csvFilePath))
            {
                writer.WriteLine("Original Path,Mirror Structure,Organized Structure,Asset Type");
                
                var sortedMappings = mappings.OrderBy(m => m.type).ThenBy(m => m.original).ToList();
                
                foreach (var mapping in sortedMappings)
                {
                    writer.WriteLine($"\"{mapping.original}\",\"{mapping.mirror}\",\"{mapping.organized}\",\"{mapping.type}\"");
                }
            }
            
            // Print summary by type
            var typeGroups = mappings.GroupBy(m => m.type).OrderBy(g => g.Key);
            Log.Information("\n=== Summary by Asset Type ===");
            foreach (var group in typeGroups)
            {
                Log.Information($"  {group.Key}: {group.Count()} files");
            }
        }
        
        /// <summary>
        /// Atomic write for text files (prevents partial writes on crash)
        /// </summary>
        static void AtomicWriteText(string filePath, string content)
        {
            var tempPath = filePath + ".tmp";
            try
            {
                File.WriteAllText(tempPath, content);
                File.Move(tempPath, filePath, overwrite: true);
            }
            catch
            {
                // Clean up temp file on error
                try { File.Delete(tempPath); } catch { }
                throw;
            }
        }
        
        /// <summary>
        /// Atomic write for binary files (prevents partial writes on crash)
        /// </summary>
        static void AtomicWriteBytes(string filePath, byte[] data)
        {
            var tempPath = filePath + ".tmp";
            try
            {
                File.WriteAllBytes(tempPath, data);
                File.Move(tempPath, filePath, overwrite: true);
            }
            catch
            {
                // Clean up temp file on error
                try { File.Delete(tempPath); } catch { }
                throw;
            }
        }
        
        /// <summary>
        /// Background task to process WebP conversion queue asynchronously
        /// This overlaps CPU-intensive WebP encoding with package processing for better throughput
        /// </summary>
        static async Task ProcessWebPConversionQueue(CancellationToken cancellationToken)
        {
            Log.Information("WebP conversion background task started (extract-then-convert: PNG → WebP)");
            
            while (!cancellationToken.IsCancellationRequested || !WebPConversionQueue.IsEmpty || !WebPRetryQueue.IsEmpty)
            {
                // Try main queue first, then retry queue
                bool dequeued = false;
                (string pngPath, string webpPath, int quality, bool verifyQuality, int retryCount) item = default;
                
                if (WebPConversionQueue.TryDequeue(out var mainItem))
                {
                    item = (mainItem.pngPath, mainItem.webpPath, mainItem.quality, mainItem.verifyQuality, 0);
                    dequeued = true;
                }
                else if (WebPRetryQueue.TryDequeue(out var retryItem))
                {
                    item = retryItem;
                    dequeued = true;
                }
                
                if (dequeued)
                {
                    var (pngPath, webpPath, quality, verifyQuality, retryCount) = item;
                    
                    try
                    {
                        // Wait for semaphore slot with timeout (limit concurrent conversions)
                        var semaphoreAcquired = await WebPConversionSemaphore.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
                        if (!semaphoreAcquired)
                        {
                            // Timeout waiting for semaphore - queue for retry
                            if (retryCount < MAX_WEBP_RETRIES)
                            {
                                WebPRetryQueue.Enqueue((pngPath, webpPath, quality, verifyQuality, retryCount + 1));
                                Interlocked.Increment(ref _webpRetryQueued);
                                Log.Information($"WebP conversion timeout waiting for semaphore: {Path.GetFileName(pngPath)} - queued for retry");
                                Interlocked.Increment(ref _webpConversionCompleted); // Count as processed (will retry later)
                                continue;
                            }
                            else
                            {
                                Log.Warning($"WebP conversion exceeded max retries waiting for semaphore: {Path.GetFileName(pngPath)}");
                                Interlocked.Increment(ref _webpConversionCompleted);
                                continue;
                            }
                        }
                        
                        try
                        {
                            // Read PNG from disk
                            if (!File.Exists(pngPath))
                            {
                                Log.Warning($"PNG file not found for conversion: {Path.GetFileName(pngPath)}");
                                Interlocked.Increment(ref _webpConversionCompleted);
                                continue;
                            }
                            
                            SKBitmap? pngBitmap = null;
                            try
                            {
                                pngBitmap = SKBitmap.Decode(pngPath);
                                if (pngBitmap == null)
                                {
                                    Log.Warning($"Failed to decode PNG (null bitmap): {Path.GetFileName(pngPath)}");
                                    Interlocked.Increment(ref _webpConversionCompleted);
                                    continue;
                                }
                            }
                            catch (Exception decodeEx)
                            {
                                // PNG file is corrupted or invalid - skip conversion
                                Log.Warning($"Failed to decode PNG (corrupted/invalid): {Path.GetFileName(pngPath)} - {decodeEx.Message}");
                                Interlocked.Increment(ref _webpConversionCompleted);
                                continue;
                            }
                            
                            using (pngBitmap)
                            {
                                // Encode to WebP (CPU-intensive, 50-500ms) with timeout protection
                                byte[]? encodedBytes = null;
                                try
                                {
                                    var encodeTask = Task.Run(() => pngBitmap.Encode(SKEncodedImageFormat.Webp, quality).ToArray());
                                    
                                    if (encodeTask.Wait(TimeSpan.FromSeconds(60)))
                                    {
                                        encodedBytes = encodeTask.Result;
                                    }
                                    else
                                    {
                                        // Encoding timeout - queue for retry
                                        if (retryCount < MAX_WEBP_RETRIES)
                                        {
                                            WebPRetryQueue.Enqueue((pngPath, webpPath, quality, verifyQuality, retryCount + 1));
                                            Interlocked.Increment(ref _webpRetryQueued);
                                            Log.Information($"WebP encoding timeout: {Path.GetFileName(pngPath)} - queued for retry");
                                            Interlocked.Increment(ref _webpConversionCompleted);
                                            continue;
                                        }
                                        else
                                        {
                                            Log.Warning($"WebP encoding exceeded max retries: {Path.GetFileName(pngPath)}");
                                            Interlocked.Increment(ref _webpConversionCompleted);
                                            continue;
                                        }
                                    }
                                }
                                catch (Exception encodeEx)
                                {
                                    // Encoding failed - queue for retry if under limit
                                    if (retryCount < MAX_WEBP_RETRIES)
                                    {
                                        WebPRetryQueue.Enqueue((pngPath, webpPath, quality, verifyQuality, retryCount + 1));
                                        Interlocked.Increment(ref _webpRetryQueued);
                                        Log.Information($"WebP encoding failed: {Path.GetFileName(pngPath)} - {encodeEx.Message} - queued for retry");
                                        Interlocked.Increment(ref _webpConversionCompleted);
                                        continue;
                                    }
                                    else
                                    {
                                        Log.Warning($"WebP encoding exceeded max retries: {Path.GetFileName(pngPath)} - {encodeEx.Message}");
                                        Interlocked.Increment(ref _webpConversionCompleted);
                                        continue;
                                    }
                                }
                                
                                if (encodedBytes == null || encodedBytes.Length == 0)
                                {
                                    Log.Warning($"WebP encoding produced no data: {Path.GetFileName(pngPath)}");
                                    Interlocked.Increment(ref _webpConversionCompleted);
                                    continue;
                                }
                                
                                // Atomic write to prevent partial files
                                AtomicWriteBytes(webpPath, encodedBytes);
                                
                                // Quality verification if requested
                                if (verifyQuality && textureDebugCount < 10)
                                {
                                    try
                                    {
                                        using var savedBitmap = SKBitmap.Decode(webpPath);
                                        if (savedBitmap != null)
                                        {
                                            var report = ImageComparison.CompareImages(pngBitmap, savedBitmap, "webp", quality);
                                            Log.Information($"🔍 Async Quality Verification: {Path.GetFileName(webpPath)}");
                                            Log.Information($"   SSIM: {report.SSIM:F4} {(report.IsVisuallyIdentical ? "✓" : "✗")} | " +
                                                          $"PSNR: {(double.IsPositiveInfinity(report.PSNR) ? "∞" : $"{report.PSNR:F1}dB")}");
                                        }
                                    }
                                    catch
                                    {
                                        // Ignore verification errors
                                    }
                                }
                                
                                // Delete PNG after successful WebP conversion (save disk space)
                                // PNG is only needed as intermediate format, WebP is the final output
                                try
                                {
                                    File.Delete(pngPath);
                                }
                                catch
                                {
                                    // Ignore deletion errors (file might be in use, or user wants to keep PNGs)
                                }
                                
                                Interlocked.Increment(ref _webpConversionCompleted);
                            }
                        }
                        finally
                        {
                            WebPConversionSemaphore.Release();
                        }
                    }
                    catch (Exception ex)
                    {
                        // Conversion failed - queue for retry if under limit
                        // Add null-safety checks to prevent crashes
                        var pngFileName = !string.IsNullOrEmpty(pngPath) ? Path.GetFileName(pngPath) : "unknown";
                        var webpFileName = !string.IsNullOrEmpty(webpPath) ? Path.GetFileName(webpPath) : "unknown";
                        
                        if (retryCount < MAX_WEBP_RETRIES && !string.IsNullOrEmpty(pngPath) && !string.IsNullOrEmpty(webpPath))
                        {
                            WebPRetryQueue.Enqueue((pngPath, webpPath, quality, verifyQuality, retryCount + 1));
                            Interlocked.Increment(ref _webpRetryQueued);
                            Log.Information($"WebP conversion failed: {pngFileName} → {webpFileName} - {ex.Message} - queued for retry");
                        }
                        else
                        {
                            Log.Warning($"WebP conversion exceeded max retries: {pngFileName} → {webpFileName} - {ex.Message}");
                        }
                        // Still increment counter to prevent queue from hanging
                        Interlocked.Increment(ref _webpConversionCompleted);
                    }
                }
                else
                {
                    // Queue is empty, wait a bit before checking again
                    await Task.Delay(10, cancellationToken);
                }
            }
            
            Log.Information($"WebP conversion background task completed: {_webpConversionCompleted:N0} conversions processed");
        }
        
        /// <summary>
        /// Process queued texture retries at the end of extraction
        /// Textures that timed out waiting for semaphore are retried here
        /// Since textures are written to disk immediately, we can retry without memory pressure
        /// </summary>
        static (long successCount, long failCount, long verifyCountDelta) ProcessTextureRetryQueue(DefaultFileProvider provider, 
            string format, int quality, bool verifyQuality)
        {
            var processed = 0L;
            var failed = 0L;
            var localSuccess = 0L;
            var localFailed = 0L;
            var verifyCountDelta = 0L;
            
            // Group retries by file path to avoid reloading the same package multiple times
            var retriesByFile = new Dictionary<string, List<(string textureName, string textureOutputDir, string format, int quality, bool verifyQuality, int retryCount)>>();
            
            // Collect all queued retries and group by file
            while (TextureRetryQueue.TryDequeue(out var item))
            {
                var (filePath, textureName, textureOutputDir, fmt, qual, verify, retryCount) = item;
                if (!retriesByFile.ContainsKey(filePath))
                {
                    retriesByFile[filePath] = new List<(string, string, string, int, bool, int)>();
                }
                retriesByFile[filePath].Add((textureName, textureOutputDir, fmt, qual, verify, retryCount));
            }
            
            Log.Information($"Processing {retriesByFile.Count:N0} files with {_textureRetryQueued:N0} total texture retries...");
            
            // Process retries file by file (reload each package once)
            var parallelOptions = new ParallelOptions 
            { 
                MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount / 2) // Use fewer threads for retries
            };
            
            Parallel.ForEach(retriesByFile, parallelOptions, kvp =>
            {
                var filePath = kvp.Key;
                var retries = kvp.Value;
                
                try
                {
                    // Reload package to get texture exports
                    IPackage? package = null;
                    var loadTask = Task.Run(() => 
                    {
                        IPackage? pkg = null;
                        var success = provider.TryLoadPackage(filePath, out pkg);
                        package = pkg;
                        return success;
                    });
                    if (loadTask.Wait(TimeSpan.FromSeconds(PackageLoadTimeoutSeconds)) && loadTask.Result && package != null)
                    {
                        var exports = package.GetExports();
                        
                        // Retry each texture from this package
                        foreach (var (textureName, textureOutputDir, fmt, qual, verify, retryCount) in retries)
                        {
                            try
                            {
                                // Check retry limit to prevent infinite loops
                                if (retryCount >= MAX_TEXTURE_RETRIES)
                                {
                                    var failMsg = $"Texture {textureName} exceeded max retries ({MAX_TEXTURE_RETRIES}) - giving up";
                                    Log.Warning(failMsg);
                                    _warningsErrorsLogWriter?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | FAILED | {failMsg}");
                                    Interlocked.Increment(ref localFailed);
                                    Interlocked.Increment(ref failed);
                                    continue;
                                }
                                
                                // Find the texture export by name
                                var textureExport = exports.FirstOrDefault(e => 
                                    e is UTexture && 
                                    e.Name.Equals(textureName, StringComparison.OrdinalIgnoreCase)) as UTexture;
                                
                                if (textureExport == null)
                                {
                                    Log.Debug($"Texture {textureName} not found in {filePath}");
                                    Interlocked.Increment(ref localFailed);
                                    Interlocked.Increment(ref failed);
                                    continue;
                                }
                                
                                // Try to acquire semaphore (no timeout this time - we'll wait)
                                // If still can't get it, increment retry count and re-queue
                                if (!TextureProcessingSemaphore.Wait(TimeSpan.FromSeconds(TextureProcessingTimeoutSeconds)))
                                {
                                    // Still can't get semaphore - increment retry count and re-queue
                                    var newRetryCount = retryCount + 1;
                                    if (newRetryCount < MAX_TEXTURE_RETRIES)
                                    {
                                        TextureRetryQueue.Enqueue((filePath, textureName, textureOutputDir, fmt, qual, verify, newRetryCount));
                                        Log.Information($"Texture {textureName} still waiting for semaphore - retry {newRetryCount}/{MAX_TEXTURE_RETRIES}");
                                    }
                                    else
                                    {
                                        var failMsg = $"Texture {textureName} exceeded max retries ({MAX_TEXTURE_RETRIES}) - giving up";
                                        Log.Warning(failMsg);
                                        _warningsErrorsLogWriter?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | FAILED | {failMsg}");
                                        Interlocked.Increment(ref localFailed);
                                        Interlocked.Increment(ref failed);
                                    }
                                    continue;
                                }
                                
                                try
                                {
                                    // Extract texture (verify count is managed by caller)
                                    if (ExportTexture(textureExport, textureOutputDir, textureName, fmt, qual, verify))
                                    {
                                        Interlocked.Increment(ref localSuccess);
                                        Interlocked.Increment(ref processed);
                                        if (verify)
                                            Interlocked.Increment(ref verifyCountDelta);
                                        
                                        Log.Information($"✅ Retry successful: {textureName}");
                                    }
                                    else
                                    {
                                        Interlocked.Increment(ref localFailed);
                                        Interlocked.Increment(ref failed);
                                    }
                                }
                                finally
                                {
                                    TextureProcessingSemaphore.Release();
                                }
                            }
                            catch (Exception ex)
                            {
                                var errorMsg = $"Error retrying texture {textureName} from {filePath}: {ex.Message}";
                                Log.Warning(errorMsg);
                                Interlocked.Increment(ref localFailed);
                                Interlocked.Increment(ref failed);
                            }
                        }
                    }
                    else
                    {
                        Log.Warning($"Failed to reload package for texture retry: {filePath}");
                        // Re-queue with incremented retry count
                        foreach (var (textureName, textureOutputDir, fmt, qual, verify, retryCount) in retries)
                        {
                            var newRetryCount = retryCount + 1;
                            if (newRetryCount < MAX_TEXTURE_RETRIES)
                            {
                                TextureRetryQueue.Enqueue((filePath, textureName, textureOutputDir, fmt, qual, verify, newRetryCount));
                            }
                            else
                            {
                                Interlocked.Increment(ref localFailed);
                                Interlocked.Increment(ref failed);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"Error processing texture retries from {filePath}: {ex.Message}");
                    Interlocked.Increment(ref localFailed);
                    Interlocked.Increment(ref failed);
                }
            });
            
            Log.Information($"Texture retry processing: {localSuccess} successful, {localFailed} failed");
            
            // Final check: If any items were re-queued during processing (exceeded retry limit), mark them as failed
            // This prevents infinite loops by ensuring we eventually process or fail all items
            var stillRemaining = TextureRetryQueue.Count;
            if (stillRemaining > 0)
            {
                Log.Warning($"Texture retry queue still has {stillRemaining} items after processing - marking as failed");
                // Mark remaining as failed (these exceeded max retries or couldn't be processed)
                while (TextureRetryQueue.TryDequeue(out var remainingItem))
                {
                    var (filePath, textureName, _, _, _, _, retryCount) = remainingItem;
                    var failMsg = $"Texture {textureName} from {filePath} failed after {retryCount} retries - giving up";
                    Log.Warning(failMsg);
                    _warningsErrorsLogWriter?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | FAILED | {failMsg}");
                    Interlocked.Increment(ref localFailed);
                }
            }
            
            return (localSuccess, localFailed, verifyCountDelta);
        }
        
        /// <summary>
        /// Process queued meshes at the end of extraction
        /// Reloads packages that contain meshes and extracts them
        /// This allows fast assets (JSON/textures) to complete first, then process slow meshes together
        /// </summary>
        static (long meshCount, long meshSkippedCount, long errorCount) ProcessMeshQueue(DefaultFileProvider provider, bool resume, bool mirrorStructure)
        {
            var processed = 0L;
            var failed = 0L;
            var skipped = 0L;
            var localMeshCount = 0L;
            var localMeshSkippedCount = 0L;
            var localErrorCount = 0L;
            
            // Group meshes by file path to avoid reloading the same package multiple times
            var meshesByFile = new Dictionary<string, List<(string meshName, string meshOutputDir, bool mirrorStructure)>>();
            
            // Collect all queued meshes and group by file
            while (MeshExtractionQueue.TryDequeue(out var item))
            {
                var (filePath, meshName, meshOutputDir, mirror) = item;
                if (!meshesByFile.ContainsKey(filePath))
                {
                    meshesByFile[filePath] = new List<(string, string, bool)>();
                }
                meshesByFile[filePath].Add((meshName, meshOutputDir, mirror));
            }
            
            Log.Information($"Processing {meshesByFile.Count:N0} files with {_meshQueued:N0} total meshes...");
            
            // Process meshes file by file (reload each package once)
            var parallelOptions = new ParallelOptions 
            { 
                MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount / 2) // Use fewer threads for mesh processing
            };
            
            Parallel.ForEach(meshesByFile, parallelOptions, kvp =>
            {
                var filePath = kvp.Key;
                var meshes = kvp.Value;
                
                try
                {
                    // Reload package to get mesh exports
                    IPackage? package = null;
                    var loadTask = Task.Run(() => provider.TryLoadPackage(filePath, out package));
                    if (loadTask.Wait(TimeSpan.FromSeconds(PackageLoadTimeoutSeconds)) && loadTask.Result && package != null)
                    {
                        
                        var exports = package.GetExports();
                        
                        // Extract each mesh from this package
                        foreach (var (meshName, meshOutputDir, mirror) in meshes)
                        {
                            try
                            {
                                // Find the mesh export by name
                                var meshExport = exports.FirstOrDefault(e => 
                                    (e is UStaticMesh || e is USkeletalMesh) && 
                                    e.Name.Equals(meshName, StringComparison.OrdinalIgnoreCase));
                                
                                if (meshExport == null)
                                {
                                    Log.Debug($"Mesh {meshName} not found in {filePath}");
                                    Interlocked.Increment(ref failed);
                                    continue;
                                }
                                
                                var meshPath = Path.Combine(meshOutputDir, meshName + ".glb");
                                if (resume && IsValidMeshFile(meshPath))
                                {
                                    Interlocked.Increment(ref localMeshSkippedCount);
                                    Interlocked.Increment(ref skipped);
                                }
                                else
                                {
                                    // Try to acquire semaphore with timeout
                                    var semaphoreAcquired = MeshProcessingSemaphore.Wait(TimeSpan.FromSeconds(MeshExportTimeoutSeconds));
                                    if (!semaphoreAcquired)
                                    {
                                        // Timeout waiting for semaphore - queue for retry
                                        MeshRetryQueue.Enqueue((filePath, meshName, meshOutputDir, mirror, 0));
                                        Interlocked.Increment(ref _meshRetryQueued);
                                        Log.Information($"⏱️ Mesh semaphore timeout: {meshName} - queued for retry");
                                        continue;
                                    }
                                    
                                    try
                                    {
                                        // Export mesh with timeout protection
                                        var meshExportTask = Task.Run(() => 
                                        {
                                            try
                                            {
                                                return ExportMesh(meshExport, meshOutputDir, meshName, resume, mirror);
                                            }
                                            catch
                                            {
                                                return false;
                                            }
                                        });
                                        
                                        if (meshExportTask.Wait(TimeSpan.FromSeconds(MeshExportTimeoutSeconds)))
                                        {
                                            if (meshExportTask.Result)
                                            {
                                                Interlocked.Increment(ref localMeshCount);
                                                Interlocked.Increment(ref processed);
                                            }
                                            else
                                            {
                                                Interlocked.Increment(ref failed);
                                            }
                                        }
                                        else
                                        {
                                            // Export timeout - queue for retry
                                            var timeoutMsg = $"⏱️ Mesh export timeout (>{MeshExportTimeoutSeconds}s): {meshName} - queued for retry";
                                            Log.Information(timeoutMsg);
                                            _warningsErrorsLogWriter?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | RETRY | {timeoutMsg}");
                                            MeshRetryQueue.Enqueue((filePath, meshName, meshOutputDir, mirror, 0));
                                            Interlocked.Increment(ref _meshRetryQueued);
                                            if (_progressUI != null)
                                            {
                                                _progressUI.AddWarning($"Mesh queued for retry: {meshName}");
                                            }
                                        }
                                    }
                                    finally
                                    {
                                        MeshProcessingSemaphore.Release();
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                var errorMsg = $"Error processing mesh {meshName} from {filePath}: {ex.Message}";
                                Log.Warning(errorMsg);
                                Interlocked.Increment(ref failed);
                            }
                        }
                    }
                    else
                    {
                        Log.Warning($"Failed to reload package for mesh extraction: {filePath}");
                        Interlocked.Increment(ref failed);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"Error processing meshes from {filePath}: {ex.Message}");
                    Interlocked.Increment(ref failed);
                }
            });
            
            Log.Information($"Mesh queue processing: {processed} successful, {skipped} skipped, {failed} failed");
            return (localMeshCount, localMeshSkippedCount, localErrorCount);
        }
        
        /// <summary>
        /// Process queued mesh retries at the end of extraction
        /// Meshes that timed out during export are retried here
        /// Uses semaphore to limit concurrent processing and prevent memory spikes
        /// </summary>
        static (long meshCount, long meshSkippedCount, long errorCount) ProcessMeshRetryQueue(DefaultFileProvider provider, bool resume, bool mirrorStructure)
        {
            var processed = 0L;
            var failed = 0L;
            var skipped = 0L;
            var localMeshCount = 0L;
            var localMeshSkippedCount = 0L;
            var localErrorCount = 0L;
            
            // Group retries by file path to avoid reloading the same package multiple times
            var retriesByFile = new Dictionary<string, List<(string meshName, string meshOutputDir, bool mirrorStructure, int retryCount)>>();
            
            // Collect all queued retries and group by file
            while (MeshRetryQueue.TryDequeue(out var item))
            {
                var (filePath, meshName, meshOutputDir, mirror, retryCount) = item;
                if (!retriesByFile.ContainsKey(filePath))
                {
                    retriesByFile[filePath] = new List<(string, string, bool, int)>();
                }
                retriesByFile[filePath].Add((meshName, meshOutputDir, mirror, retryCount));
            }
            
            Log.Information($"Processing {retriesByFile.Count:N0} files with {_meshRetryQueued:N0} total mesh retries...");
            
            // Process retries file by file (reload each package once)
            var parallelOptions = new ParallelOptions 
            { 
                MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 4) // Use even fewer threads for retries
            };
            
            Parallel.ForEach(retriesByFile, parallelOptions, kvp =>
            {
                var filePath = kvp.Key;
                var retries = kvp.Value;
                
                try
                {
                    // Reload package to get mesh exports
                    IPackage? package = null;
                    var loadTask = Task.Run(() => 
                    {
                        IPackage? pkg = null;
                        var success = provider.TryLoadPackage(filePath, out pkg);
                        package = pkg;
                        return success;
                    });
                    if (loadTask.Wait(TimeSpan.FromSeconds(PackageLoadTimeoutSeconds)) && loadTask.Result && package != null)
                    {
                        var exports = package.GetExports();
                        
                        // Retry each mesh from this package
                        foreach (var (meshName, meshOutputDir, mirror, retryCount) in retries)
                        {
                            try
                            {
                                // Check retry limit to prevent infinite loops
                                if (retryCount >= MAX_MESH_RETRIES)
                                {
                                    var failMsg = $"Mesh {meshName} exceeded max retries ({MAX_MESH_RETRIES}) - giving up";
                                    Log.Warning(failMsg);
                                    _warningsErrorsLogWriter?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | FAILED | {failMsg}");
                                    Interlocked.Increment(ref localErrorCount);
                                    Interlocked.Increment(ref failed);
                                    continue;
                                }
                                
                                // Find the mesh export by name
                                var meshExport = exports.FirstOrDefault(e => 
                                    (e is UStaticMesh || e is USkeletalMesh) && 
                                    e.Name.Equals(meshName, StringComparison.OrdinalIgnoreCase));
                                
                                if (meshExport == null)
                                {
                                    Log.Debug($"Mesh {meshName} not found in {filePath}");
                                    Interlocked.Increment(ref failed);
                                    continue;
                                }
                                
                                var meshPath = Path.Combine(meshOutputDir, meshName + ".glb");
                                if (resume && IsValidMeshFile(meshPath))
                                {
                                    Interlocked.Increment(ref localMeshSkippedCount);
                                    Interlocked.Increment(ref skipped);
                                    continue;
                                }
                                
                                // Try to acquire semaphore (with timeout)
                                if (!MeshProcessingSemaphore.Wait(TimeSpan.FromSeconds(MeshExportTimeoutSeconds)))
                                {
                                    // Still can't get semaphore - increment retry count and re-queue
                                    var newRetryCount = retryCount + 1;
                                    if (newRetryCount < MAX_MESH_RETRIES)
                                    {
                                        MeshRetryQueue.Enqueue((filePath, meshName, meshOutputDir, mirror, newRetryCount));
                                        Log.Information($"Mesh {meshName} still waiting for semaphore - retry {newRetryCount}/{MAX_MESH_RETRIES}");
                                    }
                                    else
                                    {
                                        var failMsg = $"Mesh {meshName} exceeded max retries ({MAX_MESH_RETRIES}) - giving up";
                                        Log.Warning(failMsg);
                                        _warningsErrorsLogWriter?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | FAILED | {failMsg}");
                                        Interlocked.Increment(ref localErrorCount);
                                        Interlocked.Increment(ref failed);
                                    }
                                    continue;
                                }
                                
                                try
                                {
                                    // Export mesh with timeout protection
                                    var meshExportTask = Task.Run(() => 
                                    {
                                        try
                                        {
                                            return ExportMesh(meshExport, meshOutputDir, meshName, resume, mirror);
                                        }
                                        catch
                                        {
                                            return false;
                                        }
                                    });
                                    
                                    if (meshExportTask.Wait(TimeSpan.FromSeconds(MeshExportTimeoutSeconds)))
                                    {
                                        if (meshExportTask.Result)
                                        {
                                            Interlocked.Increment(ref localMeshCount);
                                            Interlocked.Increment(ref processed);
                                            Log.Information($"✅ Mesh retry successful: {meshName}");
                                        }
                                        else
                                        {
                                            // Export failed - increment retry count and re-queue
                                            var newRetryCount = retryCount + 1;
                                            if (newRetryCount < MAX_MESH_RETRIES)
                                            {
                                                MeshRetryQueue.Enqueue((filePath, meshName, meshOutputDir, mirror, newRetryCount));
                                                Log.Information($"Mesh {meshName} export failed - retry {newRetryCount}/{MAX_MESH_RETRIES}");
                                            }
                                            else
                                            {
                                                Interlocked.Increment(ref failed);
                                            }
                                        }
                                    }
                                    else
                                    {
                                        // Export timeout - increment retry count and re-queue
                                        var newRetryCount = retryCount + 1;
                                        if (newRetryCount < MAX_MESH_RETRIES)
                                        {
                                            MeshRetryQueue.Enqueue((filePath, meshName, meshOutputDir, mirror, newRetryCount));
                                            Log.Information($"Mesh {meshName} export timeout - retry {newRetryCount}/{MAX_MESH_RETRIES}");
                                        }
                                        else
                                        {
                                            var timeoutMsg = $"⏱️ Mesh export exceeded max retries ({MAX_MESH_RETRIES}): {meshName} - giving up";
                                            Log.Warning(timeoutMsg);
                                            _warningsErrorsLogWriter?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | FAILED | {timeoutMsg}");
                                            Interlocked.Increment(ref localErrorCount);
                                            Interlocked.Increment(ref failed);
                                            if (_progressUI != null)
                                            {
                                                _progressUI.AddWarning($"Mesh failed: {meshName}");
                                            }
                                        }
                                    }
                                }
                                finally
                                {
                                    MeshProcessingSemaphore.Release();
                                }
                            }
                            catch (Exception ex)
                            {
                                var errorMsg = $"Error retrying mesh {meshName} from {filePath}: {ex.Message}";
                                Log.Warning(errorMsg);
                                Interlocked.Increment(ref failed);
                            }
                        }
                    }
                    else
                    {
                        Log.Warning($"Failed to reload package for mesh retry: {filePath}");
                        // Re-queue with incremented retry count
                        foreach (var (meshName, meshOutputDir, mirror, retryCount) in retries)
                        {
                            var newRetryCount = retryCount + 1;
                            if (newRetryCount < MAX_MESH_RETRIES)
                            {
                                MeshRetryQueue.Enqueue((filePath, meshName, meshOutputDir, mirror, newRetryCount));
                            }
                            else
                            {
                                Interlocked.Increment(ref localErrorCount);
                                Interlocked.Increment(ref failed);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"Error processing mesh retries from {filePath}: {ex.Message}");
                    Interlocked.Increment(ref failed);
                }
            });
            
            Log.Information($"Mesh retry processing: {processed} successful, {skipped} skipped, {failed} failed");
            
            // Final check: If any items were re-queued during processing, mark them as failed
            var stillRemaining = MeshRetryQueue.Count;
            if (stillRemaining > 0)
            {
                Log.Warning($"Mesh retry queue still has {stillRemaining} items after processing - marking as failed");
                while (MeshRetryQueue.TryDequeue(out var remainingItem))
                {
                    var (filePath, meshName, _, _, retryCount) = remainingItem;
                    var failMsg = $"Mesh {meshName} from {filePath} failed after {retryCount} retries - giving up";
                    Log.Warning(failMsg);
                    _warningsErrorsLogWriter?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | FAILED | {failMsg}");
                    Interlocked.Increment(ref localErrorCount);
                    Interlocked.Increment(ref failed);
                }
            }
            
            return (localMeshCount, localMeshSkippedCount, localErrorCount);
        }
        
        /// <summary>
        /// Build a PAK location index mapping files to their archive locations
        /// This helps group files from the same PAK together for better I/O patterns
        /// NOTE: This is a heuristic based on path patterns - CUE4Parse doesn't expose actual PAK file mappings
        /// For true batch PAK reading, we would need to use reflection to access CUE4Parse internals
        /// </summary>
        /// <summary>
        /// Derives a stable, short PAK slug from a PAK/VFS reader name or path.
        /// e.g. "pakchunk0-WindowsNoEditor.pak" -> "pakchunk0-WindowsNoEditor".
        /// </summary>
        static string PakSlugFromName(string pakPath)
        {
            return Path.GetFileNameWithoutExtension(pakPath);
        }

        /// <summary>
        /// Phase 0: emit PAK manifests into the extraction output (staging) root.
        /// - pak_manifest.json: ordered list of mounted paks (pakSlug, pakFile, mountOrder).
        /// - pak_asset_index.json: ConZ-relative asset path -> owning pak slug.
        /// These are consumed by the scum-db index_extraction pipeline (source_pak backfill).
        /// </summary>
        static void EmitPakManifests(DefaultFileProvider provider, IReadOnlyCollection<IAesVfsReader> pakReaders, string outputDir, Dictionary<string, string> pakLocationIndex)
        {
            try
            {
                Directory.CreateDirectory(outputDir);

                var manifest = new List<object>();
                var order = 0;
                foreach (var reader in pakReaders)
                {
                    var name = reader.Name;
                    manifest.Add(new
                    {
                        pakSlug = PakSlugFromName(name),
                        pakFile = Path.GetFileName(name),
                        mountOrder = order++
                    });
                }

                File.WriteAllText(
                    Path.Combine(outputDir, "pak_manifest.json"),
                    JsonConvert.SerializeObject(manifest, Formatting.Indented));

                var assetIndex = new Dictionary<string, string>(pakLocationIndex, StringComparer.OrdinalIgnoreCase);
                File.WriteAllText(
                    Path.Combine(outputDir, "pak_asset_index.json"),
                    JsonConvert.SerializeObject(assetIndex, Formatting.Indented));

                Log.Information($"Emitted pak_manifest.json ({manifest.Count} paks) and pak_asset_index.json ({assetIndex.Count} assets) to {outputDir}");
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to emit pak manifests: {ex.Message}");
            }
        }

        static Dictionary<string, string> BuildPakLocationIndex(DefaultFileProvider provider, int totalFileCount)
        {
            var index = new Dictionary<string, string>();
            var indexed = 0;
            
            try
            {
                // Try to access GameFile entries which may contain archive information
                // CUE4Parse's DefaultFileProvider.Files contains GameFile entries with archive info
                foreach (var kvp in provider.Files)
                {
                    var filePath = kvp.Key;
                    var gameFile = kvp.Value;

                    // The owning PAK is available directly on the GameFile (VfsEntry.Vfs.Name) -
                    // no need for path-based heuristics. Each asset maps to a single canonical PAK.
                    string pakIdentifier = "unknown";
                    if (gameFile is VfsEntry vfsEntry && vfsEntry.Vfs != null)
                    {
                        pakIdentifier = PakSlugFromName(vfsEntry.Vfs.Name);
                    }

                    index[filePath] = pakIdentifier;

                    indexed++;
                    if (indexed % 10000 == 0)
                    {
                        Log.Information($"Indexing PAK locations: {indexed:N0}/{totalFileCount:N0} ({100.0 * indexed / totalFileCount:F1}%)");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to build full PAK location index: {ex.Message}. Using path-based grouping instead.");
            }
            
            return index;
        }
        
        // Fast resume mode flag (set at start, checked during validation)
        private static bool _fastResumeMode = false;
        
        static bool IsValidJsonFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return false;
                
                var fileInfo = new FileInfo(filePath);
                
                // Check if file is empty
                if (fileInfo.Length == 0)
                    return false;
                
                // Check if file is suspiciously small (less than 10 bytes)
                if (fileInfo.Length < 10)
                    return false;
                
                // Fast resume mode: Skip validation, just check existence and size
                if (_fastResumeMode)
                    return true;
                
                // Quick validation: Read first 1KB to check JSON structure (much faster than reading entire file)
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var buffer = new byte[Math.Min(1024, fileInfo.Length)];
                var bytesRead = fs.Read(buffer, 0, buffer.Length);
                
                if (bytesRead == 0)
                    return false;
                
                // Check for basic JSON structure (starts with { or [)
                var start = Encoding.UTF8.GetString(buffer, 0, bytesRead).TrimStart();
                if (!start.StartsWith("{") && !start.StartsWith("["))
                    return false;
                
                // Quick check passed - file appears to be valid JSON
                // Full parse is expensive, so we skip it for resume validation
                // If file is corrupt, it will be caught during actual extraction
                return true;
            }
            catch
            {
                // If any error occurs (parsing, file access, etc.), consider invalid
                return false;
            }
        }
        
        /// <summary>
        /// Validates if an image file is complete and valid
        /// </summary>
        static bool IsValidImageFile(string filePath, string format)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    // Log first few failures for debugging
                    if (!_fastResumeMode && textureDebugCount < 20)
                    {
                        Log.Debug($"❌ Validation failed: File does not exist: {Path.GetFileName(filePath)}");
                        textureDebugCount++;
                    }
                    return false;
                }
                
                var fileInfo = new FileInfo(filePath);
                
                // Check if file is empty
                if (fileInfo.Length == 0)
                {
                    if (!_fastResumeMode && textureDebugCount < 20)
                    {
                        Log.Debug($"❌ Validation failed: File is empty: {Path.GetFileName(filePath)}");
                        textureDebugCount++;
                    }
                    return false;
                }
                
                // Check if file is suspiciously small (less than 20 bytes - minimum valid WebP header size)
                // Note: Very small textures (4x4, 16x16) can be < 100 bytes when compressed, so 20 bytes is a safer minimum
                if (fileInfo.Length < 20)
                {
                    if (!_fastResumeMode && textureDebugCount < 20)
                    {
                        Log.Debug($"❌ Validation failed: File too small ({fileInfo.Length} bytes): {Path.GetFileName(filePath)}");
                        textureDebugCount++;
                    }
                    return false;
                }
                
                // Fast resume mode: Skip header validation, just check existence and size
                if (_fastResumeMode)
                    return true;
                
                // Quick validation: Check file header magic numbers (much faster than full decode)
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var header = new byte[12];
                var bytesRead = fs.Read(header, 0, 12);
                
                if (bytesRead < 12)
                {
                    if (textureDebugCount < 20)
                    {
                        Log.Debug($"❌ Validation failed: Cannot read header ({bytesRead} bytes): {Path.GetFileName(filePath)}");
                        textureDebugCount++;
                    }
                    return false;
                }
                
                // Check format-specific magic numbers
                bool validHeader = false;
                if (format.Equals("webp", StringComparison.OrdinalIgnoreCase))
                {
                    // WebP: RIFF...WEBP
                    validHeader = header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
                                  header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50;
                }
                else if (format.Equals("png", StringComparison.OrdinalIgnoreCase))
                {
                    // PNG: 89 50 4E 47 0D 0A 1A 0A
                    validHeader = header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47;
                }
                else if (format.Equals("jpg", StringComparison.OrdinalIgnoreCase) || format.Equals("jpeg", StringComparison.OrdinalIgnoreCase))
                {
                    // JPEG: FF D8 FF
                    validHeader = header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
                }
                else
                {
                    // Unknown format - do full validation (fallback)
                    fs.Position = 0;
                    using (var codec = SKCodec.Create(fs))
                    {
                        if (codec == null)
                            return false;
                        if (codec.Info.Width <= 0 || codec.Info.Height <= 0)
                            return false;
                    }
                    return true;
                }
                
                return validHeader;
            }
            catch
            {
                // If any error occurs (parsing, file access, decode error), consider invalid
                return false;
            }
        }
        
        /// <summary>
        /// Validates if a mesh file (GLB/GLTF) is complete and valid
        /// </summary>
        static bool IsValidMeshFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return false;
                
                var fileInfo = new FileInfo(filePath);
                
                // Check if file is empty
                if (fileInfo.Length == 0)
                    return false;
                
                // Fast resume mode: Skip validation, just check existence and size
                if (_fastResumeMode)
                    return true;
                
                // Check if file is empty
                if (fileInfo.Length == 0)
                    return false;
                
                // Check if file is suspiciously small (less than 100 bytes for a mesh)
                if (fileInfo.Length < 100)
                    return false;
                
                // GLB files start with "glTF" magic number (4 bytes) followed by version (4 bytes) and length (4 bytes)
                // Total header is 12 bytes
                using (var fileStream = File.OpenRead(filePath))
                {
                    if (fileStream.Length < 12)
                        return false;
                    
                    var buffer = new byte[12];
                    fileStream.Read(buffer, 0, 12);
                    
                    // Check GLB magic number
                    if (buffer[0] != 0x67 || buffer[1] != 0x6C || buffer[2] != 0x54 || buffer[3] != 0x46) // "glTF"
                        return false;
                    
                    // Check version (should be 2 for glTF 2.0)
                    var version = BitConverter.ToUInt32(buffer, 4);
                    if (version != 2)
                        return false;
                    
                    // Check that file length matches declared length
                    var declaredLength = BitConverter.ToUInt32(buffer, 8);
                    if (declaredLength != fileStream.Length)
                        return false;
                }
                
                return true;
            }
            catch
            {
                // If any error occurs, consider invalid
                return false;
            }
        }
        
        /// <summary>
        /// Validates if a sound file is complete and valid
        /// </summary>
        static bool IsValidSoundFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return false;
                
                var fileInfo = new FileInfo(filePath);
                
                // Check if file is empty
                if (fileInfo.Length == 0)
                    return false;
                
                // Fast resume mode: Skip validation, just check existence and size
                if (_fastResumeMode)
                    return true;
                
                // Check if file is suspiciously small (less than 100 bytes for audio)
                if (fileInfo.Length < 100)
                    return false;
                
                // For WAV files, check RIFF header
                // For OGG files, check OggS header
                using (var fileStream = File.OpenRead(filePath))
                {
                    if (fileStream.Length < 4)
                        return false;
                    
                    var header = new byte[4];
                    fileStream.Read(header, 0, 4);
                    
                    // Check for WAV (RIFF) or OGG (OggS) header
                    bool isValidFormat = 
                        (header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46) || // "RIFF" (WAV)
                        (header[0] == 0x4F && header[1] == 0x67 && header[2] == 0x67 && header[3] == 0x53);  // "OggS" (OGG)
                    
                    if (!isValidFormat)
                        return false;
                }
                
                return true;
            }
            catch
            {
                // If any error occurs, consider invalid
                return false;
            }
        }
    }
}
