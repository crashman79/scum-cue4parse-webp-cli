using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using CUE4ParseCLI.Core;
using CUE4ParseCLI;

namespace CUE4ParseCLI.UI.GUI
{
    /// <summary>
    /// True GUI using Avalonia UI - native windows, buttons, text fields, etc.
    /// </summary>
    public class AvaloniaGuiUI : IProgressUI
    {
        internal MainWindow? _mainWindow;
        internal static AvaloniaGuiUI? _instance;
        private App? _app;
        private Task? _uiThread;
        private readonly ConcurrentQueue<string> _exports = new();
        private readonly ConcurrentQueue<string> _warnings = new();
        private readonly ConcurrentQueue<string> _errors = new();
        private readonly int _maxListItems = 50;
        
        private long _totalExports = 0;
        private long _totalWarnings = 0;
        private long _totalErrors = 0;
        
        // Statistics properties
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
        public double DiskSpaceGB { get; set; }
        public int GcTriggers { get; set; }
        public bool ResumeMode { get; set; }
        public long EstimatedTotalOutputs { get; set; }
        public bool HasEstimate { get; set; }
        public long JsonSkipped { get; set; }
        public long TextureSkipped { get; set; }
        public long MeshSkipped { get; set; }
        public long SoundSkipped { get; set; }
        public string OutputDirectory { get; set; } = "";
        public string StatusMessage { get; set; } = "Initializing...";
        public CancellationTokenSource? CancellationSource { get; set; }
        public long WebPQueued { get; set; }
        public long WebPCompleted { get; set; }
        public bool WebPAsyncEnabled { get; set; }
        
        private DateTime _lastDiskSpaceUpdate = DateTime.MinValue;
        private double _cachedDiskSpaceGB = 0;
        private const int DiskSpaceUpdateIntervalSeconds = 5;
        
        public AvaloniaGuiUI()
        {
            _instance = this;
            
            // Start Avalonia app on a dedicated thread (Avalonia requires its own thread)
            _uiThread = Task.Run(() =>
            {
                try
                {
                    // Build and start Avalonia app FIRST (this initializes the platform)
                    // The window will be created in OnFrameworkInitializationCompleted
                    // Note: Not using .UseSkia() to avoid SkiaSharp native library issues in single-file apps
                    AppBuilder.Configure<App>()
                        .UsePlatformDetect()
                        .AfterSetup(builder =>
                        {
                            // Platform is now initialized, safe to create window
                            Dispatcher.UIThread.Post(() =>
                            {
                                try
                                {
                                    _mainWindow = new MainWindow(this);
                                    App.MainWindowInstance = _mainWindow;
                                    
                                    // Set as main window if lifetime is available
                                    if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                                    {
                                        desktop.MainWindow = _mainWindow;
                                        _mainWindow.Show();
                                    }
                                }
                                catch (Exception winEx)
                                {
                                    Console.Error.WriteLine($"❌ Failed to create window: {winEx.Message}");
                                }
                            }, DispatcherPriority.Normal);
                        })
                        .StartWithClassicDesktopLifetime(Array.Empty<string>());
                }
                catch (Exception ex)
                {
                    // Log error to stderr so it's visible even if GUI fails
                    Console.Error.WriteLine($"❌ Avalonia GUI initialization failed: {ex.Message}");
                    Console.Error.WriteLine($"Stack trace: {ex.StackTrace}");
                    if (ex.InnerException != null)
                    {
                        Console.Error.WriteLine($"Inner exception: {ex.InnerException.Message}");
                    }
                }
            });
            
            // Give UI thread time to initialize and show window
            // Increase wait time to ensure window appears
            Thread.Sleep(2000);
            
            // Verify window was created
            if (_mainWindow == null)
            {
                Console.Error.WriteLine("⚠️ Warning: GUI window was not created. Falling back to console output.");
            }
        }
        
        public void AddExport(string message)
        {
            Interlocked.Increment(ref _totalExports);
            _exports.Enqueue(message);
            while (_exports.Count > _maxListItems)
                _exports.TryDequeue(out _);
            UpdateUI();
        }
        
        public void AddWarning(string message)
        {
            Interlocked.Increment(ref _totalWarnings);
            _warnings.Enqueue(message);
            while (_warnings.Count > _maxListItems)
                _warnings.TryDequeue(out _);
            UpdateUI();
        }
        
        public void AddError(string message)
        {
            Interlocked.Increment(ref _totalErrors);
            _errors.Enqueue(message);
            while (_errors.Count > _maxListItems)
                _errors.TryDequeue(out _);
            UpdateUI();
        }
        
        public void AddJsonExport(string fileName) => AddExport($"JSON: {fileName}");
        public void AddTextureExport(string fileName) => AddExport($"Texture: {fileName}");
        public void AddMeshExport(string fileName) => AddExport($"Mesh: {fileName}");
        public void AddSoundExport(string fileName) => AddExport($"Sound: {fileName}");
        public void AddIconExport(string fileName) => AddExport($"Icon: {fileName}");
        
        private void UpdateUI()
        {
            if (_mainWindow != null)
            {
                Dispatcher.UIThread.Post(() => _mainWindow.UpdateDisplay(), DispatcherPriority.Background);
            }
        }
        
        public void Run()
        {
            // Wait for UI thread to complete (blocks until window closed)
            _uiThread?.Wait();
        }
        
        public void Stop()
        {
            Dispatcher.UIThread.Post(() => _mainWindow?.Close(), DispatcherPriority.Normal);
        }
        
        public void RequestUpdate()
        {
            UpdateUI();
        }
        
        public void Update()
        {
            UpdateUI();
        }
        
        public void Dispose()
        {
            Stop();
            // Avalonia will shutdown when window closes
        }
        
        // Expose data for UI binding
        public string[] GetExports() => _exports.ToArray().Reverse().Take(_maxListItems).ToArray();
        public string[] GetWarnings() => _warnings.ToArray().Reverse().Take(_maxListItems).ToArray();
        public string[] GetErrors() => _errors.ToArray().Reverse().Take(_maxListItems).ToArray();
        
        private double CalculateDiskSpace()
        {
            if (string.IsNullOrEmpty(OutputDirectory) || !Directory.Exists(OutputDirectory))
                return 0;
            
            try
            {
                long totalBytes = 0;
                var files = Directory.EnumerateFiles(OutputDirectory, "*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        if (fileInfo.Exists)
                            totalBytes += fileInfo.Length;
                    }
                    catch { }
                }
                return totalBytes / (1024.0 * 1024 * 1024);
            }
            catch
            {
                return 0;
            }
        }
        
        public void UpdateDiskSpace()
        {
            var now = DateTime.Now;
            if ((now - _lastDiskSpaceUpdate).TotalSeconds >= DiskSpaceUpdateIntervalSeconds)
            {
                _cachedDiskSpaceGB = CalculateDiskSpace();
                _lastDiskSpaceUpdate = now;
            }
            DiskSpaceGB = _cachedDiskSpaceGB;
        }
    }
    
    // Avalonia App class
    public class App : Application
    {
        public static MainWindow? MainWindowInstance { get; set; }
        
        public override void Initialize()
        {
            // No XAML for now - we'll build UI in code
        }
        
        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                // Window is created in AfterSetup callback, just ensure it's set here
                if (MainWindowInstance != null)
                {
                    desktop.MainWindow = MainWindowInstance;
                }
            }
            
            base.OnFrameworkInitializationCompleted();
        }
    }
    
    // Main Window with proper GUI controls
    public class MainWindow : Window
    {
        private readonly AvaloniaGuiUI _guiUI;
        
        // Configuration controls
        private TextBox? _outputDirTextBox;
        private Button? _outputDirButton;
        private CheckBox? _extractJsonCheckBox;
        private CheckBox? _extractTexturesCheckBox;
        private CheckBox? _extractIconsCheckBox;
        private CheckBox? _extractMeshesCheckBox;
        private CheckBox? _extractSoundsCheckBox;
        private CheckBox? _resumeCheckBox;
        private ComboBox? _formatComboBox;
        private Slider? _qualitySlider;
        private TextBlock? _qualityValueLabel;
        private TextBox? _filterTextBox;
        private ComboBox? _organizationComboBox;
        private Button? _startButton;
        
        // Progress display controls
        private Panel? _configPanel;
        private Panel? _progressPanel;
        private TextBlock? _progressLabel;
        private TextBlock? _rateLabel;
        private TextBlock? _etaLabel;
        private TextBlock? _elapsedLabel;
        private TextBlock? _memoryLabel;
        private TextBlock? _diskLabel;
        private TextBlock? _gcLabel;
        private TextBlock? _outputsLabel;
        private TextBlock? _countersLabel;
        private TextBlock? _statusLabel;
        private ListBox? _exportsList;
        private ListBox? _warningsList;
        private ListBox? _errorsList;
        private Button? _cancelButton;
        private ProgressBar? _progressBar;
        private DispatcherTimer? _updateTimer;
        
        private bool _extractionRunning = false;
        
        public MainWindow(AvaloniaGuiUI guiUI)
        {
            _guiUI = guiUI;
            Title = "CUE4Parse WebP CLI";
            Width = 1000;
            Height = 700;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            
            BuildUI();
            ShowConfigurationPanel();
            
            // Update timer (every 100ms) - only active during extraction
            _updateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _updateTimer.Tick += (s, e) => UpdateDisplay();
            
            // Handle window closing
            Closing += (s, e) =>
            {
                _guiUI.CancellationSource?.Cancel();
            };
        }
        
        private void BuildUI()
        {
            // Create main container with tab or panel switching
            var mainContainer = new Grid();
            
            // Configuration panel
            _configPanel = BuildConfigurationPanel();
            _configPanel.IsVisible = true;
            
            // Progress panel
            _progressPanel = BuildProgressPanel();
            _progressPanel.IsVisible = false;
            
            mainContainer.Children.Add(_configPanel);
            mainContainer.Children.Add(_progressPanel);
            
            Content = mainContainer;
        }
        
        private Panel BuildConfigurationPanel()
        {
            var panel = new StackPanel
            {
                Margin = new Thickness(15),
                Spacing = 15
            };
            
            // Title
            panel.Children.Add(new TextBlock
            {
                Text = "Extraction Configuration",
                FontSize = 18,
                FontWeight = Avalonia.Media.FontWeight.Bold,
                Margin = new Thickness(0, 0, 0, 10)
            });
            
            // Output Directory
            var outputDirPanel = new StackPanel { Spacing = 5 };
            outputDirPanel.Children.Add(new TextBlock { Text = "Output Directory:", FontWeight = Avalonia.Media.FontWeight.SemiBold });
            var outputDirRow = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 10
            };
            _outputDirTextBox = new TextBox
            {
                Text = _guiUI.OutputDirectory ?? Environment.GetEnvironmentVariable("SCUM_OUTPUT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "Output", "Exports"),
                Width = 600,
                Watermark = "Select output directory..."
            };
            _outputDirButton = new Button
            {
                Content = "Browse...",
                Width = 100
            };
            _outputDirButton.Click += async (s, e) =>
            {
                var folders = await this.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Select Output Directory",
                    AllowMultiple = false
                });
                if (folders.Count > 0)
                {
                    var folder = folders[0];
                    _outputDirTextBox.Text = folder.Path.LocalPath;
                }
            };
            outputDirRow.Children.Add(_outputDirTextBox);
            outputDirRow.Children.Add(_outputDirButton);
            outputDirPanel.Children.Add(outputDirRow);
            panel.Children.Add(outputDirPanel);
            
            // Extraction Options
            var extractPanel = new Border
            {
                BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.Gray),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = "Asset Types to Extract", FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 8) },
                        (_extractJsonCheckBox = new CheckBox { Content = "JSON Blueprints", IsChecked = true }),
                        (_extractTexturesCheckBox = new CheckBox { Content = "Textures", IsChecked = true }),
                        (_extractIconsCheckBox = new CheckBox { Content = "Icons", IsChecked = true }),
                        (_extractMeshesCheckBox = new CheckBox { Content = "Meshes (GLB)", IsChecked = true }),
                        (_extractSoundsCheckBox = new CheckBox { Content = "Sounds", IsChecked = false })
                    }
                }
            };
            panel.Children.Add(extractPanel);
            
            // Format and Quality
            var formatPanel = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("1* 1*"),
                RowDefinitions = new RowDefinitions("Auto Auto"),
                Margin = new Thickness(0, 5, 0, 5)
            };
            
            // Format
            var formatLabel = new TextBlock { Text = "Image Format:", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Grid.SetRow(formatLabel, 0);
            Grid.SetColumn(formatLabel, 0);
            formatPanel.Children.Add(formatLabel);
            
            _formatComboBox = new ComboBox
            {
                ItemsSource = new[] { "WebP", "PNG", "JPEG" },
                SelectedIndex = 0,
                Margin = new Thickness(10, 0, 0, 0)
            };
            Grid.SetRow(_formatComboBox, 0);
            Grid.SetColumn(_formatComboBox, 1);
            formatPanel.Children.Add(_formatComboBox);
            
            // Quality
            var qualityLabel = new TextBlock { Text = "Quality:", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Grid.SetRow(qualityLabel, 1);
            Grid.SetColumn(qualityLabel, 0);
            formatPanel.Children.Add(qualityLabel);
            
            var qualityPanel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 10,
                Margin = new Thickness(10, 5, 0, 0)
            };
            _qualitySlider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = 85,
                Width = 200,
                TickFrequency = 10,
                IsSnapToTickEnabled = true
            };
            _qualityValueLabel = new TextBlock
            {
                Text = "85",
                Width = 40,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            _qualitySlider.ValueChanged += (s, e) =>
            {
                _qualityValueLabel.Text = ((int)e.NewValue).ToString();
            };
            qualityPanel.Children.Add(_qualitySlider);
            qualityPanel.Children.Add(_qualityValueLabel);
            Grid.SetRow(qualityPanel, 1);
            Grid.SetColumn(qualityPanel, 1);
            formatPanel.Children.Add(qualityPanel);
            
            panel.Children.Add(formatPanel);
            
            // Filter
            var filterPanel = new StackPanel { Spacing = 5 };
            filterPanel.Children.Add(new TextBlock { Text = "Path Filter (optional):", FontWeight = Avalonia.Media.FontWeight.SemiBold });
            _filterTextBox = new TextBox
            {
                Watermark = "e.g., Items/Weapons to filter paths"
            };
            filterPanel.Children.Add(_filterTextBox);
            panel.Children.Add(filterPanel);
            
            // Organization Mode
            var orgPanel = new StackPanel { Spacing = 5 };
            orgPanel.Children.Add(new TextBlock { Text = "Organization Mode:", FontWeight = Avalonia.Media.FontWeight.SemiBold });
            _organizationComboBox = new ComboBox
            {
                ItemsSource = new[]
                {
                    "By Type (default) - Organize into Blueprints/, Textures/, etc.",
                    "Hybrid - Type folders within game folders",
                    "Mirror - Preserve exact game structure"
                },
                SelectedIndex = 0
            };
            orgPanel.Children.Add(_organizationComboBox);
            panel.Children.Add(orgPanel);
            
            // Options
            var optionsPanel = new StackPanel { Spacing = 5, Margin = new Thickness(0, 10, 0, 0) };
            _resumeCheckBox = new CheckBox { Content = "Resume Mode (skip existing valid files)", IsChecked = false };
            optionsPanel.Children.Add(_resumeCheckBox);
            panel.Children.Add(optionsPanel);
            
            // Start Button
            _startButton = new Button
            {
                Content = "Start Extraction",
                Width = 200,
                Height = 40,
                FontSize = 14,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Margin = new Thickness(0, 20, 0, 0),
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0, 120, 215)),
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.White)
            };
            _startButton.Click += OnStartExtraction;
            panel.Children.Add(_startButton);
            
            return panel;
        }
        
        private Panel BuildProgressPanel()
        {
            var mainPanel = new DockPanel
            {
                Margin = new Thickness(10)
            };
            
            // Statistics panel
            var statsPanel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Vertical,
                Spacing = 5
            };
            
            var statsBorder = new Border
            {
                BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.Gray),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 10),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = "Statistics", FontSize = 14, FontWeight = Avalonia.Media.FontWeight.Bold, Margin = new Thickness(0, 0, 0, 5) },
                        statsPanel
                    }
                }
            };
            
            _progressLabel = new TextBlock { FontSize = 14, FontWeight = Avalonia.Media.FontWeight.Bold };
            _rateLabel = new TextBlock { FontSize = 12 };
            _etaLabel = new TextBlock { FontSize = 12 };
            _elapsedLabel = new TextBlock { FontSize = 12 };
            _memoryLabel = new TextBlock { FontSize = 12 };
            _diskLabel = new TextBlock { FontSize = 12 };
            _gcLabel = new TextBlock { FontSize = 12 };
            _outputsLabel = new TextBlock { FontSize = 12 };
            _countersLabel = new TextBlock { FontSize = 12 };
            _statusLabel = new TextBlock { FontSize = 12, Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.Blue) };
            
            statsPanel.Children.Add(_progressLabel);
            statsPanel.Children.Add(_rateLabel);
            statsPanel.Children.Add(_etaLabel);
            statsPanel.Children.Add(_elapsedLabel);
            statsPanel.Children.Add(_memoryLabel);
            statsPanel.Children.Add(_diskLabel);
            statsPanel.Children.Add(_gcLabel);
            statsPanel.Children.Add(_outputsLabel);
            statsPanel.Children.Add(_countersLabel);
            statsPanel.Children.Add(_statusLabel);
            
            _progressBar = new ProgressBar
            {
                Height = 25,
                Margin = new Thickness(0, 10, 0, 0)
            };
            statsPanel.Children.Add(_progressBar);
            
            DockPanel.SetDock(statsBorder, Dock.Top);
            mainPanel.Children.Add(statsBorder);
            
            // Activity logs
            var activityPanel = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("1* 1* 1*"),
                Margin = new Thickness(0, 10, 0, 0)
            };
            
            _exportsList = new ListBox { Height = 350, SelectionMode = SelectionMode.Single };
            _warningsList = new ListBox { Height = 350, SelectionMode = SelectionMode.Single };
            _errorsList = new ListBox { Height = 350, SelectionMode = SelectionMode.Single };
            
            var exportsBorder = new Border
            {
                BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.Gray),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(5),
                Margin = new Thickness(0, 0, 5, 0),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = "Exports", FontSize = 12, FontWeight = Avalonia.Media.FontWeight.Bold, Margin = new Thickness(0, 0, 0, 5) },
                        _exportsList
                    }
                }
            };
            
            var warningsBorder = new Border
            {
                BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.Gray),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(5),
                Margin = new Thickness(5, 0, 5, 0),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = "Warnings", FontSize = 12, FontWeight = Avalonia.Media.FontWeight.Bold, Margin = new Thickness(0, 0, 0, 5), Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.Orange) },
                        _warningsList
                    }
                }
            };
            
            var errorsBorder = new Border
            {
                BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.Gray),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(5),
                Margin = new Thickness(5, 0, 0, 0),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = "Errors", FontSize = 12, FontWeight = Avalonia.Media.FontWeight.Bold, Margin = new Thickness(0, 0, 0, 5), Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.Red) },
                        _errorsList
                    }
                }
            };
            
            Grid.SetColumn(exportsBorder, 0);
            Grid.SetColumn(warningsBorder, 1);
            Grid.SetColumn(errorsBorder, 2);
            activityPanel.Children.Add(exportsBorder);
            activityPanel.Children.Add(warningsBorder);
            activityPanel.Children.Add(errorsBorder);
            
            mainPanel.Children.Add(activityPanel);
            
            // Cancel button
            var buttonPanel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            
            _cancelButton = new Button
            {
                Content = "Cancel Extraction",
                Width = 150,
                Height = 35,
                FontSize = 14
            };
            _cancelButton.Click += (s, e) =>
            {
                _guiUI.CancellationSource?.Cancel();
                _extractionRunning = false;
                ShowConfigurationPanel();
            };
            
            buttonPanel.Children.Add(_cancelButton);
            DockPanel.SetDock(buttonPanel, Dock.Bottom);
            mainPanel.Children.Add(buttonPanel);
            
            return mainPanel;
        }
        
        private void ShowConfigurationPanel()
        {
            _configPanel!.IsVisible = true;
            _progressPanel!.IsVisible = false;
            _updateTimer?.Stop();
            Title = "CUE4Parse WebP CLI - Configuration";
        }
        
        private void ShowProgressPanel()
        {
            _configPanel!.IsVisible = false;
            _progressPanel!.IsVisible = true;
            _updateTimer?.Start();
            Title = "CUE4Parse WebP CLI - Extraction Progress";
        }
        
        private async void OnStartExtraction(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            // Validate output directory
            var outputDir = _outputDirTextBox!.Text?.Trim();
            if (string.IsNullOrEmpty(outputDir))
            {
                // Show error message using a simple window
                var errorWindow = new Window
                {
                    Title = "Error",
                    Width = 350,
                    Height = 120,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    ShowInTaskbar = false
                };
                
                var okButton = new Button
                {
                    Content = "OK",
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Width = 80
                };
                okButton.Click += (s, ev) => errorWindow.Close();
                
                errorWindow.Content = new StackPanel
                {
                    Margin = new Thickness(20),
                    Children =
                    {
                        new TextBlock { Text = "Please select an output directory.", Margin = new Thickness(0, 0, 0, 15) },
                        okButton
                    }
                };
                
                await errorWindow.ShowDialog(this);
                return;
            }
            
            // Build command-line args from GUI controls
            var args = new List<string>();
            
            args.Add("--output");
            args.Add(outputDir);
            
            if (_extractTexturesCheckBox!.IsChecked == true)
                args.Add("--textures");
            if (_extractJsonCheckBox!.IsChecked == false)
                args.Add("--no-json");
            if (_extractIconsCheckBox!.IsChecked == false)
                args.Add("--no-icons");
            if (_extractMeshesCheckBox!.IsChecked == false)
                args.Add("--no-meshes");
            if (_extractSoundsCheckBox!.IsChecked == false)
                args.Add("--no-sounds");
            
            if (_formatComboBox!.SelectedIndex >= 0)
            {
                args.Add("--format");
                var format = _formatComboBox.SelectedItem?.ToString()?.ToLower() ?? "webp";
                args.Add(format);
            }
            
            args.Add("--quality");
            args.Add(((int)(_qualitySlider!.Value)).ToString());
            
            if (!string.IsNullOrWhiteSpace(_filterTextBox!.Text))
            {
                args.Add("--filter");
                args.Add(_filterTextBox.Text.Trim());
            }
            
            if (_organizationComboBox!.SelectedIndex == 1)
                args.Add("--hybrid-structure");
            else if (_organizationComboBox.SelectedIndex == 2)
                args.Add("--mirror-structure");
            
            if (_resumeCheckBox!.IsChecked == true)
                args.Add("--resume");
            
            // Switch to progress view
            ShowProgressPanel();
            _extractionRunning = true;
            
            // Start extraction on background thread
            _guiUI.CancellationSource = new CancellationTokenSource();
            _guiUI.OutputDirectory = outputDir;
            
            Task.Run(() =>
            {
                try
                {
                    // Call Program.Run with args built from GUI
                    Program.Run(args.ToArray());
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _guiUI.AddError($"Extraction failed: {ex.Message}");
                    });
                }
                finally
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _extractionRunning = false;
                        // Optionally switch back to config panel when done
                    });
                }
            });
        }
        
        public void UpdateDisplay()
        {
            if (_guiUI == null) return;
            
            _guiUI.UpdateDiskSpace();
            
            var percent = _guiUI.TotalFiles > 0 ? (_guiUI.Progress * 100.0 / _guiUI.TotalFiles) : 0;
            
            _progressLabel!.Text = $"Progress: {_guiUI.Progress:N0}/{_guiUI.TotalFiles:N0} ({percent:F1}%)";
            _rateLabel!.Text = $"Rate: {_guiUI.Rate:F1} files/sec";
            _etaLabel!.Text = $"ETA: {_guiUI.ETA:hh\\:mm\\:ss}";
            _elapsedLabel!.Text = $"Elapsed: {_guiUI.Elapsed:hh\\:mm\\:ss}";
            _memoryLabel!.Text = $"Memory: {_guiUI.MemoryGB:F2} GB";
            _diskLabel!.Text = $"Disk: {_guiUI.DiskSpaceGB:F2} GB";
            _gcLabel!.Text = $"GC Triggers: {_guiUI.GcTriggers}";
            
            var totalOutputs = _guiUI.JsonCount + _guiUI.TextureSuccess + _guiUI.MeshCount + _guiUI.SoundCount;
            var outputsDisplay = _guiUI.HasEstimate 
                ? $"Outputs: {totalOutputs:N0}/{_guiUI.EstimatedTotalOutputs:N0}"
                : $"Outputs: {totalOutputs:N0}";
            _outputsLabel!.Text = outputsDisplay;
            
            var jsonDisplay = _guiUI.ResumeMode && _guiUI.JsonSkipped > 0 
                ? $"JSON: {_guiUI.JsonCount:N0} (Skipped: {_guiUI.JsonSkipped:N0})"
                : $"JSON: {_guiUI.JsonCount:N0}";
            var textureDisplay = _guiUI.ResumeMode && _guiUI.TextureSkipped > 0
                ? $"Textures: {_guiUI.TextureSuccess:N0}/{_guiUI.TextureTotal:N0} (Skipped: {_guiUI.TextureSkipped:N0})"
                : $"Textures: {_guiUI.TextureSuccess:N0}/{_guiUI.TextureTotal:N0}";
            var meshDisplay = _guiUI.ResumeMode && _guiUI.MeshSkipped > 0
                ? $"Meshes: {_guiUI.MeshCount:N0} (Skipped: {_guiUI.MeshSkipped:N0})"
                : $"Meshes: {_guiUI.MeshCount:N0}";
            var soundDisplay = _guiUI.ResumeMode && _guiUI.SoundSkipped > 0
                ? $"Sounds: {_guiUI.SoundCount:N0} (Skipped: {_guiUI.SoundSkipped:N0})"
                : $"Sounds: {_guiUI.SoundCount:N0}";
            var errorDisplay = $"Errors: {_guiUI.ErrorCount:N0}";
            
            _countersLabel!.Text = $"{jsonDisplay} | {textureDisplay} | {meshDisplay} | {soundDisplay} | {errorDisplay}";
            _statusLabel!.Text = $"Status: {_guiUI.StatusMessage}";
            
            // Update progress bar
            if (_guiUI.TotalFiles > 0)
            {
                _progressBar!.Maximum = _guiUI.TotalFiles;
                _progressBar.Value = _guiUI.Progress;
            }
            
            // Update lists (use ItemsSource for data binding)
            _exportsList!.ItemsSource = _guiUI.GetExports();
            _warningsList!.ItemsSource = _guiUI.GetWarnings();
            _errorsList!.ItemsSource = _guiUI.GetErrors();
        }
    }
}
