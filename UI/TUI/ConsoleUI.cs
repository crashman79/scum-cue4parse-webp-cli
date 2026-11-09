using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CUE4ParseCLI.Core;

namespace CUE4ParseCLI.UI.TUI
{
    /// <summary>
    /// Manages a two-panel console UI with statistics bar and activity log
    /// </summary>
    public class ConsoleUI : IProgressUI
    {
        private readonly ConcurrentQueue<string> _exports = new();
        private readonly ConcurrentQueue<string> _warnings = new();
        private readonly ConcurrentQueue<string> _errors = new();
        private readonly int _maxLogEntries = 12; // Max entries per section (limited to 10-15 range to prevent UI expansion) - strictly enforced
        private readonly object _lockObject = new();
        private bool _uiEnabled = true;
        private readonly bool _supportsColor;
        private CancellationTokenSource _updateCancellation = new();
        private Task? _updateTask;
        private readonly object _updateLock = new();
        private volatile bool _updateRequested = false;
        
        // Track cursor position for in-place updates (prevents scrolling)
        private int _lastDrawnLines = 0;
        private bool _firstDraw = true;
        private bool _alternateScreenEnabled = false; // Track if alternate screen buffer is enabled
        
        // Cache disk space calculation to avoid expensive directory scans on every update
        private DateTime _lastDiskSpaceUpdate = DateTime.MinValue;
        private double _cachedDiskSpaceGB = 0;
        private const int DiskSpaceUpdateIntervalSeconds = 5; // Update disk space every 5 seconds instead of every 100ms
        
        // Total counters (not just displayed items)
        private long _totalExports = 0;
        private long _totalWarnings = 0;
        private long _totalErrors = 0;
        
        // ANSI color codes
        private const string Reset = "\u001b[0m";
        private const string JsonColor = "\u001b[36m";      // Cyan
        private const string MeshColor = "\u001b[35m";      // Magenta
        private const string IconColor = "\u001b[33m";      // Yellow
        private const string TextColor = "\u001b[32m";      // Green
        private const string SoundColor = "\u001b[34m";     // Blue
        private const string WarningColor = "\u001b[33m";   // Yellow for warnings
        private const string ErrorColor = "\u001b[31m";     // Red for errors

        // Statistics
        public long Progress { get; set; }
        public long TotalFiles { get; set; }
        public long JsonCount { get; set; }
        public long TextureSuccess { get; set; }
        public long TextureTotal { get; set; }
        public long MeshCount { get; set; }
        public long SoundCount { get; set; }
        public long ErrorCount { get; set; }
        public double Rate { get; set; }
        public TimeSpan ETA { get; set; }
        public TimeSpan Elapsed { get; set; }
        public double MemoryGB { get; set; }
        public int GcTriggers { get; set; }
        public bool ResumeMode { get; set; }
        public double DiskSpaceGB { get; set; }  // Disk space used by extraction
        public string OutputDirectory { get; set; } = "";  // Output directory for disk space calculation
        public long EstimatedTotalOutputs { get; set; }  // Dynamic estimate of total outputs
        public bool HasEstimate { get; set; }  // Whether we have enough data for estimation
        public long JsonSkipped { get; set; }
        public long TextureSkipped { get; set; }
        public long MeshSkipped { get; set; }
        public long SoundSkipped { get; set; }
        public long WebPQueued { get; set; }  // WebP conversions queued for async processing
        public long WebPCompleted { get; set; }  // WebP conversions completed
        public bool WebPAsyncEnabled { get; set; }  // Whether async WebP conversion is enabled
        public string StatusMessage { get; set; } = "Initializing...";
        public System.Threading.CancellationTokenSource? CancellationSource { get; set; }

        public void AddExport(string message)
        {
            if (!_uiEnabled) return;
            Interlocked.Increment(ref _totalExports);
            // Store full message text - exports already have color coding from AddJsonExport, AddTextureExport, etc.
            // PadOrTruncate will handle width fitting during display while preserving color codes
            _exports.Enqueue(message);
            // Strictly enforce queue limit - remove oldest items immediately if over limit
            while (_exports.Count > _maxLogEntries)
                _exports.TryDequeue(out _);
        }

        /// <summary>
        /// Create a human-friendly summary from a warning/error message
        /// </summary>
        private string SummarizeMessage(string message, int maxLength = 40)
        {
            if (string.IsNullOrEmpty(message)) return "";
            
            // Extract key information: filename, error type, etc.
            // Patterns to look for:
            // - "Error exporting T_TextureName: Detex decompression failed"
            // - "Texture skipped (null reference) | T_TextureName"
            // - "Skipping BC6H texture: T_TextureName"
            // - "Error exporting sound SoundName: Exception message"
            
            // Try to extract filename/asset name
            var filenameMatch = System.Text.RegularExpressions.Regex.Match(message, @"(?:exporting|skipped|Skipping)\s+([A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)?)");
            var filename = filenameMatch.Success && filenameMatch.Groups.Count > 1 ? filenameMatch.Groups[1].Value : null;
            
            // Extract error/warning type
            string summary;
            if (message.Contains("Detex") || message.Contains("not initialized"))
            {
                summary = !string.IsNullOrEmpty(filename) ? $"Detex: {filename}" : "Detex unsupported";
            }
            else if (message.Contains("null reference") || message.Contains("Object reference"))
            {
                summary = !string.IsNullOrEmpty(filename) ? $"Null ref: {filename}" : "Null reference";
            }
            else if (message.Contains("BC6H"))
            {
                summary = !string.IsNullOrEmpty(filename) ? $"BC6H: {filename}" : "BC6H skipped";
            }
            else if (message.Contains("Error exporting"))
            {
                var parts = message.Split(':');
                var errorType = parts.Length > 1 ? parts[parts.Length - 1].Trim() : "Export failed";
                if (string.IsNullOrEmpty(errorType)) errorType = "Export failed";
                
                summary = !string.IsNullOrEmpty(filename) ? $"{filename}: {errorType}" : errorType;
                // Truncate error type if too long
                if (summary.Length > maxLength && !string.IsNullOrEmpty(filename))
                {
                    var maxErrorLen = Math.Max(10, maxLength - filename.Length - 2);
                    if (errorType.Length > maxErrorLen)
                    {
                        errorType = errorType.Substring(0, maxErrorLen - 3) + "...";
                    }
                    summary = $"{filename}: {errorType}";
                }
            }
            else if (message.Contains("skipped"))
            {
                summary = !string.IsNullOrEmpty(filename) ? $"Skipped: {filename}" : "Skipped";
            }
            else
            {
                // Generic truncation
                summary = message.Length > maxLength ? message.Substring(0, maxLength - 3) + "..." : message;
            }
            
            return TruncateMessage(summary, maxLength);
        }
        
        public void AddWarning(string message)
        {
            if (!_uiEnabled) return;
            Interlocked.Increment(ref _totalWarnings);
            // Store full message text (not summarized) - user wants full text display
            // Add color coding for warnings (yellow) to differentiate from other outputs
            var coloredMessage = _supportsColor 
                ? $"{WarningColor}{message}{Reset}"
                : message;
            _warnings.Enqueue(coloredMessage);
            // Keep warnings longer - don't remove old ones as aggressively
            // This ensures warnings persist even when exports scroll by
            while (_warnings.Count > _maxLogEntries * 2)
                _warnings.TryDequeue(out _);
        }

        public void AddError(string message)
        {
            if (!_uiEnabled) return;
            Interlocked.Increment(ref _totalErrors);
            // Store full message text (not summarized) - user wants full text display
            // Add color coding for errors (red) to differentiate from other outputs
            var coloredMessage = _supportsColor 
                ? $"{ErrorColor}{message}{Reset}"
                : message;
            _errors.Enqueue(coloredMessage);
            // Keep errors longer - don't remove old ones as aggressively
            // This ensures errors persist even when exports scroll by
            while (_errors.Count > _maxLogEntries * 2)
                _errors.TryDequeue(out _);
        }

        public ConsoleUI()
        {
            _startTime = DateTime.Now;
            // Detect color support: check if we're in a terminal that supports colors
            // Check environment variable and if output is not redirected
            _supportsColor = !Console.IsOutputRedirected &&
                            Environment.GetEnvironmentVariable("TERM")?.Contains("color") == true &&
                            Environment.GetEnvironmentVariable("NO_COLOR") == null;
            
            // Enable alternate screen buffer (like Vim) to prevent flicker and scrollback conflicts
            // This creates a separate buffer for our full-screen UI, preventing terminal scrollback interference
            EnableAlternateScreen();
            
            // Start asynchronous UI update task
            StartAsyncUpdate();
        }
        
        /// <summary>
        /// Starts asynchronous UI update task that runs independently of extraction
        /// </summary>
        private void StartAsyncUpdate()
        {
            _updateTask = Task.Run(async () =>
            {
                while (!_updateCancellation.Token.IsCancellationRequested)
                {
                    try
                    {
                        // Update UI at ~10 FPS (every 100ms) - reduced from 50ms to avoid GC pressure
                        // Slower updates are fine and reduce memory allocations from frequent updates
                        await Task.Delay(100, _updateCancellation.Token);
                        
                        // Only update if requested OR if enabled (prevents unnecessary updates during GC)
                        // The update can be expensive (disk space, queue enumeration), so be selective
                        if (_uiEnabled && (_updateRequested || true)) // Always update if enabled, but slower
                        {
                            UpdateInternal();
                            _updateRequested = false; // Clear flag if it was set
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        // Expected when cancellation is requested
                        break;
                    }
                    catch
                    {
                        // Ignore errors in update task - don't let exceptions break the update loop
                        // Reset state to prevent full redraw issues
                        _firstDraw = true;
                        _lastDrawnLines = 0;
                    }
                }
            }, _updateCancellation.Token);
        }
        
        /// <summary>
        /// Request UI update (non-blocking, triggers async update)
        /// </summary>
        public void RequestUpdate()
        {
            if (!_uiEnabled) return;
            _updateRequested = true;
        }
        
        /// <summary>
        /// Calculate disk space used by output directory
        /// This is an expensive operation (full directory scan) so it's cached and only updated periodically
        /// Skip calculation during GC pauses to avoid adding more memory pressure
        /// </summary>
        private double CalculateDiskSpace()
        {
            if (string.IsNullOrEmpty(OutputDirectory) || !Directory.Exists(OutputDirectory))
                return 0;
            
            // Skip disk space calculation if we're under memory pressure (GC might be running)
            // This prevents adding more work during GC pauses
            var memoryGB = GC.GetTotalMemory(false) / (1024.0 * 1024 * 1024);
            if (memoryGB > 6.0) // Skip if memory is high (likely GC pressure)
            {
                return _cachedDiskSpaceGB; // Return cached value instead
            }
            
            try
            {
                long totalBytes = 0;
                int fileCount = 0;
                const int MAX_FILES_TO_SCAN = 50000; // Limit scan to prevent excessive time/memory
                
                // Use EnumerateFiles instead of GetFiles to avoid loading all file paths into memory at once
                // This is more memory-efficient for large directories
                var files = Directory.EnumerateFiles(OutputDirectory, "*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    fileCount++;
                    if (fileCount > MAX_FILES_TO_SCAN)
                    {
                        // If we hit the limit, estimate based on average file size
                        // This prevents extremely long scans on huge directories
                        var avgSize = fileCount > 0 ? totalBytes / fileCount : 0;
                        var estimatedTotal = (long)(avgSize * fileCount * 1.1); // 10% fudge factor
                        return estimatedTotal / (1024.0 * 1024 * 1024);
                    }
                    
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        if (fileInfo.Exists)
                            totalBytes += fileInfo.Length;
                    }
                    catch
                    {
                        // Skip files we can't access
                    }
                }
                return totalBytes / (1024.0 * 1024 * 1024); // Convert to GB
            }
            catch
            {
                return _cachedDiskSpaceGB; // Return cached value on error
            }
        }
        
        /// <summary>
        /// Internal update method (called by async task)
        /// </summary>
        private void UpdateInternal()
        {
            if (!_uiEnabled) return;
            
            try
            {
                // Update elapsed time based on actual elapsed time from start (always update)
                var actualElapsed = DateTime.Now - _startTime;
                if (Elapsed == TimeSpan.Zero || actualElapsed > Elapsed)
                {
                    Elapsed = actualElapsed;
                }
                
                // Update disk space only periodically (expensive operation - full directory scan)
                // Cache the result to avoid blocking UI updates
                // Also skip during high GC activity to prevent adding memory pressure
                var now = DateTime.Now;
                var memoryGB = GC.GetTotalMemory(false) / (1024.0 * 1024 * 1024);
                var shouldUpdateDiskSpace = (now - _lastDiskSpaceUpdate).TotalSeconds >= DiskSpaceUpdateIntervalSeconds;
                
                // Skip disk space update if memory is high (likely GC activity) - avoid adding pressure
                if (shouldUpdateDiskSpace && memoryGB < 7.0)
                {
                    _cachedDiskSpaceGB = CalculateDiskSpace();
                    _lastDiskSpaceUpdate = now;
                }
                DiskSpaceGB = _cachedDiskSpaceGB;
                
                lock (_lockObject)
                {
                    // Check if we're in a real TTY (interactive terminal)
                    bool isInteractive = !Console.IsOutputRedirected;
                    
                    if (isInteractive)
                    {
                        // Use cursor positioning for in-place updates (prevents scrolling and visible redraw)
                        // With alternate screen buffer, we can do incremental updates without full clears
                        if (!_firstDraw && _lastDrawnLines > 0)
                        {
                            try
                            {
                                // Move cursor up to the start of our display area (incremental update)
                                Console.Write($"\u001b[{_lastDrawnLines}A"); // Move up N lines to where we started
                                // Don't clear entire screen - just update in place (prevents visible redraw)
                                // The new content will overwrite the old content line by line
                            }
                            catch
                            {
                                // If cursor operations fail (e.g., during GC pause), reset to first draw
                                _firstDraw = true;
                                _lastDrawnLines = 0;
                            }
                        }
                        else
                        {
                            // First draw - clear entire screen using alternate screen buffer
                            // Since we're using alternate screen, we can clear the entire buffer once
                            try
                            {
                                Console.Write("\u001b[H"); // Move to home position (top-left)
                                Console.Write("\u001b[2J"); // Clear entire screen (only on first draw)
                                _firstDraw = false;
                            }
                            catch
                            {
                                // If console operations fail, disable in-place updates
                                _firstDraw = true;
                                _lastDrawnLines = 0;
                            }
                        }
                    }
                    else
                    {
                        // For non-interactive (piped/redirected) output, add separator
                        Console.WriteLine("\n" + new string('─', 80));
                    }
                    
                    // Draw statistics bar
                    var width = GetConsoleWidth();
                    var statsBar = BuildStatsBar();
                    
                    // Write each line of stats bar, padding to full width to clear leftover characters
                    var statsLines = statsBar.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    var statsLineCount = 0;
                    foreach (var line in statsLines)
                    {
                        // Always pad each line to full width to clear leftover characters
                        var paddedLine = PadLineToWidth(line.TrimEnd('\r', '\n'), width);
                        Console.WriteLine(paddedLine);
                        statsLineCount++;
                    }
                    // Ensure at least one line is written (for line counting)
                    if (statsLineCount == 0)
                    {
                        Console.WriteLine(new string(' ', width));
                        statsLineCount = 1;
                    }
                    
                    var statsLinesCount = statsLineCount; // Use for line counting later
                    
                    // Draw activity log (now three separate horizontal windows)
                    var activityLines = DrawActivityLog();
                    
                    // Track total lines drawn for next update
                    var newTotalLines = statsLinesCount + activityLines;
                    
                    // If new content is shorter, clear remaining old lines (only clear what's needed)
                    if (isInteractive && newTotalLines < _lastDrawnLines)
                    {
                        // We drew fewer lines than before, clear the remaining old lines below
                        var linesToClear = _lastDrawnLines - newTotalLines;
                        for (int i = 0; i < linesToClear; i++)
                        {
                            Console.Write("\u001b[1B"); // Move down one line
                            Console.Write("\u001b[0G"); // Start of line
                            Console.Write("\u001b[2K"); // Clear entire line
                        }
                        // Move back up to where we finished drawing
                        Console.Write($"\u001b[{linesToClear}A");
                    }
                    else if (isInteractive && newTotalLines > _lastDrawnLines)
                    {
                        // New content is longer - we've already written it, just track the new line count
                        // No need to clear anything, the new lines are already written
                    }
                    
                    _lastDrawnLines = newTotalLines;
                }
            }
            catch
            {
                // If console operations fail (e.g., redirected output), disable UI
                _uiEnabled = false;
            }
        }
        
        /// <summary>
        /// Synchronous update (for backward compatibility, but now just requests async update)
        /// </summary>
        public void Update()
        {
            RequestUpdate();
        }
        
        /// <summary>
        /// Enable alternate screen buffer (like Vim) to prevent flicker and scrollback conflicts
        /// This creates a separate buffer that doesn't interfere with terminal scrollback
        /// </summary>
        private void EnableAlternateScreen()
        {
            if (Console.IsOutputRedirected) return; // Don't use alternate screen if output is redirected
            
            try
            {
                // Enable alternate screen buffer - same technique Vim uses
                // ESC[?1049h is the standard way to switch to alternate screen
                // This prevents our in-place updates from conflicting with scrollback
                Console.Write("\u001b[?1049h");
                // Hide cursor during updates to prevent flicker
                Console.Write("\u001b[?25l");
                _alternateScreenEnabled = true;
            }
            catch
            {
                // If alternate screen isn't supported, continue without it
                _alternateScreenEnabled = false;
            }
        }
        
        /// <summary>
        /// Disable alternate screen buffer and restore normal terminal state
        /// </summary>
        private void DisableAlternateScreen()
        {
            if (!_alternateScreenEnabled) return;
            
            try
            {
                // Show cursor again
                Console.Write("\u001b[?25h");
                // Switch back to normal screen buffer (restores scrollback)
                Console.Write("\u001b[?1049l");
                _alternateScreenEnabled = false;
            }
            catch
            {
                // Ignore errors when disabling
            }
        }
        
        /// <summary>
        /// Stop async update task and cleanup
        /// </summary>
        public void Stop()
        {
            try
            {
                _updateCancellation?.Cancel();
                _updateTask?.Wait(TimeSpan.FromSeconds(1)); // Wait up to 1 second for graceful shutdown
                
                // Restore normal terminal state before exiting
                DisableAlternateScreen();
            }
            catch
            {
                // Ignore errors during shutdown, but still try to restore terminal
                try { DisableAlternateScreen(); } catch { }
            }
        }
        
        private int GetConsoleWidth()
        {
            try
            {
                return Console.WindowWidth > 0 ? Console.WindowWidth : 120;
            }
            catch
            {
                return 120; // Default width if can't determine
            }
        }

        private string BuildStatsBar()
        {
            var percent = TotalFiles > 0 ? (Progress * 100.0 / TotalFiles) : 0;
            var width = GetConsoleWidth();
            
            var sb = new StringBuilder();
            
            // Title line - simple, no borders
            var title = "EXTRACTION PROGRESS" + (ResumeMode ? " [RESUME MODE]" : "");
            // Note: Lines will be padded by caller, so just write the content
            sb.AppendLine(title);
            sb.AppendLine(new string('-', Math.Min(title.Length, width)));
            
            // Line 1: Progress | Rate | ETA
            var progressBox = $"Progress: {Progress:N0}/{TotalFiles:N0} ({percent:F1}%)";
            var rateBox = $"Rate: {Rate:F1}/s";
            var etaBox = $"ETA: {ETA:hh\\:mm\\:ss}";
            sb.AppendLine($"{progressBox}  |  {rateBox}  |  {etaBox}");
            
            // Line 2: Elapsed | Memory | Disk | GC | WebP Queue (if active)
            var elapsedBox = $"Elapsed: {Elapsed:hh\\:mm\\:ss}";
            var memoryBox = $"Memory: {MemoryGB:F2} GB";
            var diskBox = $"Disk: {DiskSpaceGB:F2} GB";
            var gcBox = $"GC: {GcTriggers}";
            
            // Show WebP counter if async conversion is enabled (even if 0/0 initially)
            if (WebPAsyncEnabled)
            {
                var webpBox = $"WebP: {WebPCompleted:N0}/{WebPQueued:N0}";
                sb.AppendLine($"{elapsedBox}  |  {memoryBox}  |  {diskBox}  |  {gcBox}  |  {webpBox}");
            }
            else
            {
                sb.AppendLine($"{elapsedBox}  |  {memoryBox}  |  {diskBox}  |  {gcBox}");
            }
            
            // Line 3: Outputs and all counters
            var totalOutputs = JsonCount + TextureSuccess + MeshCount + SoundCount;
            var outputsDisplay = HasEstimate 
                ? $"Outputs: {totalOutputs:N0}/{EstimatedTotalOutputs:N0}"
                : $"Outputs: {totalOutputs:N0}";
            
            // Update elapsed time based on actual elapsed time from start
            if (Elapsed == TimeSpan.Zero && Progress == 0 && TotalFiles == 0)
            {
                // During initialization/scan, show time from start
                Elapsed = DateTime.Now - _startTime;
            }
            
            // Build compact counter displays
            var jsonDisplay = ResumeMode && JsonSkipped > 0 
                ? $"J:{JsonCount:N0}(S:{JsonSkipped:N0})"
                : $"J:{JsonCount:N0}";
            var textureDisplay = ResumeMode && TextureSkipped > 0
                ? $"T:{TextureSuccess:N0}/{TextureTotal:N0}(S:{TextureSkipped:N0})"
                : $"T:{TextureSuccess:N0}/{TextureTotal:N0}";
            var meshDisplay = ResumeMode && MeshSkipped > 0
                ? $"M:{MeshCount:N0}(S:{MeshSkipped:N0})"
                : $"M:{MeshCount:N0}";
            var soundDisplay = ResumeMode && SoundSkipped > 0
                ? $"S:{SoundCount:N0}(S:{SoundSkipped:N0})"
                : $"S:{SoundCount:N0}";
            var errorDisplay = $"E:{ErrorCount:N0}";
            
            sb.AppendLine($"{outputsDisplay}  |  {jsonDisplay}  |  {textureDisplay}  |  {meshDisplay}  |  {soundDisplay}  |  {errorDisplay}");
            
            return sb.ToString();
        }

        private int DrawActivityLog()
        {
            // Avoid ToArray() to prevent allocations during GC pauses
            // Collect only the items we need (last N) without creating full arrays
            var width = GetConsoleWidth();
            int linesDrawn = 0;
            
            // Collect items efficiently: iterate once, keep only last N items
            // This avoids full array allocation and is more GC-friendly
            var exportList = CollectLastItems(_exports, _maxLogEntries);
            var warningList = CollectLastItems(_warnings, _maxLogEntries);
            var errorList = CollectLastItems(_errors, _maxLogEntries);
            
            // Display status message between stats and activity logs
            if (!string.IsNullOrEmpty(StatusMessage))
            {
                var statusLine = $"Status: {StatusMessage}";
                if (statusLine.Length > width)
                {
                    statusLine = statusLine.Substring(0, width - 3) + "...";
                }
                Console.WriteLine(PadLineToWidth(statusLine, width)); // Padded status line
                linesDrawn++;
                var separatorLine = new string('-', Math.Min(statusLine.Length, width));
                Console.WriteLine(PadLineToWidth(separatorLine, width)); // Padded separator
                linesDrawn++;
            }
            
            // Calculate column width: account for borders and spacing properly
            // Format: ║ {content} ║ {content} ║ {content} ║
            // Each column has: ║ (1) + space (1) + content + space (1) = 3 chars per column for borders
            // Plus 2 separator columns (║ + space) = 2 more chars
            // So we have: width = 3*colWidth + 2 separators + 2 end borders = 3*colWidth + 4
            // Therefore: colWidth = (width - 4) / 3
            // But we need to account for the actual content padding: each content has " {content} " = 2 spaces
            // Actually, looking at the format: "║ {content} ║ {content} ║ {content} ║"
            // The borders are: ║ (1 char), separator is ║ (1 char)
            // Content padding: " {content} " = 2 spaces total per column
            // Total: width = 1 (left border) + (colWidth + 2) + 1 (separator) + (colWidth + 2) + 1 (separator) + (colWidth + 2) + 1 (right border)
            // = 1 + colWidth + 2 + 1 + colWidth + 2 + 1 + colWidth + 2 + 1
            // = 3*colWidth + 10
            // So: colWidth = (width - 10) / 3
            // But we want to fill the space, so let's use the full width minus borders
            // Format: ║ content ║ content ║ content ║
            // Each ║ is 1 char, each space is 1 char, so:
            // Left: ║ (1) + space (1) = 2
            // Col1: content + space (1) = colWidth + 1
            // Sep1: ║ (1) + space (1) = 2
            // Col2: content + space (1) = colWidth + 1
            // Sep2: ║ (1) + space (1) = 2
            // Col3: content + space (1) = colWidth + 1
            // Right: ║ (1) = 1
            // Total: 2 + (colWidth + 1) + 2 + (colWidth + 1) + 2 + (colWidth + 1) + 1
            // = 3*colWidth + 10
            // So: colWidth = (width - 10) / 3
            
            // Calculate column width to fill full width properly
            // Format: ║ {content} ║ {content} ║ {content} ║
            // Each column structure: "║ " (2 chars) + content + " " (1 char)
            // Between columns: "║ " (2 chars)
            // Total overhead: "║ " (2) + " ║ " (3) + " ║ " (3) + " ║" (2) = 10 chars
            // So: width = 3*contentWidth + 10, therefore: contentWidth = (width - 10) / 3
            var totalExports = Interlocked.Read(ref _totalExports);
            var totalWarnings = Interlocked.Read(ref _totalWarnings);
            var totalErrors = Interlocked.Read(ref _totalErrors);
            
            // Display three separate horizontal windows stacked vertically
            // Each takes full width, making it flexible to terminal resizing
            
            // EXPORTS section
            var exportHeader = ResumeMode && (JsonSkipped + TextureSkipped + MeshSkipped + SoundSkipped) > 0
                ? $"EXPORTS ({totalExports,3}) SKIPPED ({JsonSkipped + TextureSkipped + MeshSkipped + SoundSkipped,3})"
                : $"EXPORTS ({totalExports,3})";
            Console.WriteLine(PadLineToWidth(exportHeader, width)); // Padded header
            linesDrawn++;
            var exportSeparator = new string('-', Math.Min(exportHeader.Length, width));
            Console.WriteLine(PadLineToWidth(exportSeparator, width)); // Padded separator
            linesDrawn++;
            
            // Show exports (newest first) - no spacing between rows, just one line per entry
            foreach (var export in exportList)
            {
                // PadOrTruncate handles full width display and preserves color codes
                var exportLine = PadOrTruncate(export, width);
                Console.WriteLine(exportLine);
                linesDrawn++;
            }
            
            // If no exports, show placeholder
            if (exportList.Count == 0)
            {
                Console.WriteLine(PadLineToWidth("(no exports yet)", width)); // Padded placeholder
                linesDrawn++;
            }
            
            // WARNINGS section
            var warningHeader = $"WARNINGS ({totalWarnings,3})";
            Console.WriteLine(PadLineToWidth(warningHeader, width)); // Padded header
            linesDrawn++;
            var warningSeparator = new string('-', Math.Min(warningHeader.Length, width));
            Console.WriteLine(PadLineToWidth(warningSeparator, width)); // Padded separator
            linesDrawn++;
            
            // Show warnings (newest first) - no spacing between rows, just one line per entry
            foreach (var warning in warningList)
            {
                // PadOrTruncate handles full width display and preserves color codes
                var warningLine = PadOrTruncate(warning, width);
                Console.WriteLine(warningLine);
                linesDrawn++;
            }
            
            // If no warnings, show placeholder
            if (warningList.Count == 0)
            {
                Console.WriteLine(PadLineToWidth("(no warnings)", width)); // Padded placeholder
                linesDrawn++;
            }
            
            // ERRORS section
            var errorHeader = $"ERRORS ({totalErrors,3})";
            Console.WriteLine(PadLineToWidth(errorHeader, width)); // Padded header
            linesDrawn++;
            var errorSeparator = new string('-', Math.Min(errorHeader.Length, width));
            Console.WriteLine(PadLineToWidth(errorSeparator, width)); // Padded separator
            linesDrawn++;
            
            // Show errors (newest first) - no spacing between rows, just one line per entry
            foreach (var error in errorList)
            {
                // PadOrTruncate handles full width display and preserves color codes
                var errorLine = PadOrTruncate(error, width);
                Console.WriteLine(errorLine);
                linesDrawn++;
            }
            
            // If no errors, show placeholder
            if (errorList.Count == 0)
            {
                Console.WriteLine(PadLineToWidth("(no errors)", width)); // Padded placeholder
                linesDrawn++;
            }
            
            return linesDrawn;
        }
        
        /// <summary>
        /// Pad a line to full terminal width with whitespace, preserving ANSI color codes
        /// This ensures clean redraws when line lengths change, clearing leftover characters
        /// </summary>
        private string PadLineToWidth(string line, int width)
        {
            if (string.IsNullOrEmpty(line)) return new string(' ', width);
            
            // Calculate actual text length (excluding ANSI color codes)
            var textWithoutCodes = System.Text.RegularExpressions.Regex.Replace(line, @"\u001b\[[0-9;]+m", "");
            textWithoutCodes = System.Text.RegularExpressions.Regex.Replace(textWithoutCodes, @"\u001b\[0m", "");
            
            var paddingNeeded = width - textWithoutCodes.Length;
            
            if (paddingNeeded <= 0)
            {
                // Line is already at or exceeds width, return as-is
                return line;
            }
            
            // Pad with whitespace, preserving color codes
            if (line.Contains(Reset) || line.Contains("\u001b["))
            {
                // If text ends with Reset, insert padding before Reset, otherwise append
                if (line.EndsWith(Reset))
                {
                    return line.Substring(0, line.Length - Reset.Length) + new string(' ', paddingNeeded) + Reset;
                }
                else
                {
                    return line + new string(' ', paddingNeeded);
                }
            }
            else
            {
                // No color codes, simple padding
                return line + new string(' ', paddingNeeded);
            }
        }

        private string PadOrTruncate(string text, int width)
        {
            if (string.IsNullOrEmpty(text)) return new string(' ', width);
            
            // Calculate actual text length (excluding ANSI color codes)
            var textWithoutCodes = System.Text.RegularExpressions.Regex.Replace(text, @"\u001b\[[0-9;]+m", "");
            textWithoutCodes = System.Text.RegularExpressions.Regex.Replace(textWithoutCodes, @"\u001b\[0m", "");
            
            string result;
            
            // Only truncate if text significantly exceeds terminal width (allow some overflow for readability)
            // This preserves full text display while preventing extremely long lines from breaking layout
            if (textWithoutCodes.Length > width + 50) // Allow 50 chars overflow before truncating
            {
                // Truncate, but preserve color codes if present
                if (text.Contains(Reset) || text.Contains("\u001b["))
                {
                    // Has color codes, preserve them when truncating
                    var colorMatch = System.Text.RegularExpressions.Regex.Match(text, @"^(\u001b\[[0-9;]+m)");
                    var colorCode = colorMatch.Success ? colorMatch.Groups[1].Value : "";
                    
                    // Truncate to width, preserving color
                    var truncatedText = textWithoutCodes.Substring(0, width - 3) + "...";
                    result = !string.IsNullOrEmpty(colorCode) 
                        ? $"{colorCode}{truncatedText}{Reset}"
                        : truncatedText;
                }
                else
                {
                    // No color codes, simple truncation
                    result = textWithoutCodes.Substring(0, width - 3) + "...";
                }
            }
            else
            {
                // Text fits (or slightly overflows) - return full text to preserve complete messages
                result = text;
            }
            
            // Pad to full width with whitespace to clear any leftover characters from previous draws
            // This ensures clean redraws when line lengths change
            var resultWithoutCodes = System.Text.RegularExpressions.Regex.Replace(result, @"\u001b\[[0-9;]+m", "");
            resultWithoutCodes = System.Text.RegularExpressions.Regex.Replace(resultWithoutCodes, @"\u001b\[0m", "");
            var paddingNeeded = width - resultWithoutCodes.Length;
            
            if (paddingNeeded > 0)
            {
                // Preserve color codes, add padding whitespace at the end
                if (result.Contains(Reset) || result.Contains("\u001b["))
                {
                    // If text ends with Reset, insert padding before Reset, otherwise append
                    if (result.EndsWith(Reset))
                    {
                        result = result.Substring(0, result.Length - Reset.Length) + new string(' ', paddingNeeded) + Reset;
                    }
                    else
                    {
                        result = result + new string(' ', paddingNeeded);
                    }
                }
                else
                {
                    // No color codes, simple padding
                    result = result + new string(' ', paddingNeeded);
                }
            }
            
            return result;
        }

        private string TruncateMessage(string message, int maxLength)
        {
            if (string.IsNullOrEmpty(message)) return "";
            
            // Message is already in format "TYPE: filename" (with or without color codes)
            // Just truncate the filename part if needed
            string result = message;
            
            // If message has color codes, extract the actual text length
            var textWithoutCodes = System.Text.RegularExpressions.Regex.Replace(message, @"\u001b\[[0-9;]+m", "");
            textWithoutCodes = System.Text.RegularExpressions.Regex.Replace(textWithoutCodes, @"\u001b\[0m", "");
            
            if (textWithoutCodes.Length > maxLength)
            {
                // Extract type and filename
                var match = System.Text.RegularExpressions.Regex.Match(message, @"([A-Z]+):\s*(.+)");
                if (match.Success)
                {
                    var type = match.Groups[1].Value;
                    var fileName = match.Groups[2].Value;
                    
                    // Remove color codes from filename to get actual length
                    var cleanFileName = System.Text.RegularExpressions.Regex.Replace(fileName, @"\u001b\[[0-9;]+m", "");
                    cleanFileName = System.Text.RegularExpressions.Regex.Replace(cleanFileName, @"\u001b\[0m", "");
                    
                    if (cleanFileName.Length > maxLength - type.Length - 2) // Account for "TYPE: " prefix
                    {
                        var maxFileNameLen = maxLength - type.Length - 7; // "TYPE: " = 6 chars + 1 space
                        var truncated = cleanFileName.Substring(0, Math.Max(0, maxFileNameLen - 3)) + "...";
                        
                        // Reconstruct with color if original had it
                        if (_supportsColor && message.Contains(Reset))
                        {
                            result = $"{GetColorForType(type)}{type}:{Reset} {truncated}";
                        }
                        else
                        {
                            result = $"{type}: {truncated}";
                        }
                    }
                }
                else
                {
                    // Fallback: just truncate
                    result = textWithoutCodes.Substring(0, maxLength - 3) + "...";
                }
            }
            
            return result;
        }
        
        private string GetColorForType(string type)
        {
            return type.ToUpper() switch
            {
                "JSON" => JsonColor,
                "MESH" => MeshColor,
                "ICON" => IconColor,
                "TEXT" => TextColor,
                "TEXTURE" => TextColor,
                "SOUND" => SoundColor,
                _ => ""
            };
        }

        private DateTime _startTime = DateTime.Now;
        
        /// <summary>
        /// Collect the last N items from a ConcurrentQueue without creating a full array
        /// This is more memory-efficient and GC-friendly than ToArray()
        /// Uses a sliding window to avoid loading all items into memory
        /// </summary>
        private List<string> CollectLastItems(ConcurrentQueue<string> queue, int count)
        {
            if (count <= 0) return new List<string>();
            
            // Use a sliding window: only keep the last (count * 2) items in memory
            // This prevents loading entire queue into memory, especially important during GC
            var slidingWindow = new List<string>(Math.Min(count * 2, 100)); // Reasonable cap
            
            foreach (var item in queue)
            {
                slidingWindow.Add(item);
                // Maintain sliding window size - remove oldest when window exceeds limit
                // This prevents memory growth during long extractions
                if (slidingWindow.Count > count * 2)
                {
                    slidingWindow.RemoveAt(0); // Remove oldest item
                }
            }
            
            // Return last N items in reverse order (newest first) - strictly limit to count
            var result = new List<string>(Math.Min(count, slidingWindow.Count));
            var startIndex = Math.Max(0, slidingWindow.Count - count);
            var itemsToTake = Math.Min(count, slidingWindow.Count - startIndex);
            for (int i = slidingWindow.Count - 1; i >= startIndex && result.Count < count; i--)
            {
                result.Add(slidingWindow[i]);
            }
            
            return result;
        }
        
        // Standardized export format methods with color support
        public void AddJsonExport(string fileName)
        {
            var message = _supportsColor 
                ? $"{JsonColor}JSON:{Reset} {fileName}"
                : $"JSON: {fileName}";
            AddExport(message);
        }
        
        public void AddMeshExport(string fileName)
        {
            var message = _supportsColor 
                ? $"{MeshColor}MESH:{Reset} {fileName}"
                : $"MESH: {fileName}";
            AddExport(message);
        }
        
        public void AddIconExport(string fileName)
        {
            var message = _supportsColor 
                ? $"{IconColor}ICON:{Reset} {fileName}"
                : $"ICON: {fileName}";
            AddExport(message);
        }
        
        public void AddTextureExport(string fileName)
        {
            var message = _supportsColor 
                ? $"{TextColor}TEXT:{Reset} {fileName}"
                : $"TEXT: {fileName}";
            AddExport(message);
        }
        
        public void AddSoundExport(string fileName)
        {
            var message = _supportsColor 
                ? $"{SoundColor}SOUND:{Reset} {fileName}"
                : $"SOUND: {fileName}";
            AddExport(message);
        }

        public void Disable()
        {
            _uiEnabled = false;
            Console.Clear();
        }

        public void Enable()
        {
            _uiEnabled = true;
        }
        
        public void Dispose()
        {
            Stop(); // This will also restore the terminal (disable alternate screen)
            _updateCancellation?.Dispose();
            
            // Ensure alternate screen is disabled even if Stop() had issues
            try
            {
                DisableAlternateScreen();
            }
            catch
            {
                // Ignore errors during disposal
            }
        }
    }
}
