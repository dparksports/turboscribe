using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using System.ComponentModel;

namespace LongAudioApp;

// Simple model for the transcript file list
public class TranscriptFileInfo
{
    public string FullPath { get; set; } = "";
    public string FileName => Path.GetFileName(FullPath);
    public string FolderPath => Path.GetDirectoryName(FullPath) ?? "";
    public long CharCount { get; set; }
    public string SizeLabel => CharCount > 0 ? $"{CharCount:N0} chars" : "empty";

    /// <summary>The user's scanned media directory — set from DirectoryBox.Text so transcripts can find media files.</summary>
    public static string? MediaDirectory { get; set; }

    /// <summary>Derive the source media file from the transcript filename or embedded Source: header.</summary>
    public string? SourceMediaPath
    {
        get
        {
            // 1. Check for embedded "Source: " line in transcript header
            try
            {
                using var reader = new StreamReader(FullPath);
                for (int i = 0; i < 5 && !reader.EndOfStream; i++)
                {
                    var line = reader.ReadLine();
                    if (line != null && line.StartsWith("Source: "))
                    {
                        var path = line[8..].Trim();
                        if (File.Exists(path)) return path;
                    }
                }
            }
            catch { }

            // 2. Fallback: derive from filename — check same directory first
            var name = Path.GetFileNameWithoutExtension(FullPath);
            var idx = name.IndexOf("_transcript");
            if (idx < 0) return null;
            var baseName = name[..idx];

            string[] exts = [".mp4", ".mp3", ".wav", ".m4a", ".aac", ".ogg", ".flac", ".mkv", ".avi", ".mov", ".webm", ".wma", ".m4v", ".3gp", ".ts", ".mpg", ".mpeg"];
            
            // Check same directory as transcript
            var dir = FolderPath;
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, baseName + ext);
                if (File.Exists(candidate)) return candidate;
            }

            // 3. Check the user's scanned media directory (transcripts may be in AppData)
            if (!string.IsNullOrEmpty(MediaDirectory) && Directory.Exists(MediaDirectory))
            {
                foreach (var ext in exts)
                {
                    try
                    {
                        var matches = Directory.GetFiles(MediaDirectory, baseName + ext, SearchOption.AllDirectories);
                        if (matches.Length > 0) return matches[0];
                    }
                    catch { }
                }
            }
            return null;
        }
    }

    public DateTime LastModified { get; set; }

    public void ReadSize()
    {
        try 
        { 
            var fi = new FileInfo(FullPath);
            CharCount = fi.Length;
            LastModified = fi.LastWriteTime;
        }
        catch { CharCount = 0; LastModified = DateTime.MinValue; }
    }
}

// Model for the media-centric file list
public class MediaFileInfo
{
    public string FullPath { get; set; } = "";
    public string FileName => Path.GetFileName(FullPath);
    public string BaseName => Path.GetFileNameWithoutExtension(FullPath);
    public bool HasVoice { get; set; }
    public bool HasTranscript { get; set; }
    public List<string> TranscriptModels { get; set; } = new();
    public string ModelBadges => string.Join(", ", TranscriptModels);
    public double DurationSec { get; set; }
    public double VoiceDurationSec { get; set; }
    public int TranscriptLength { get; set; }
    public string TranscriptLengthLabel => TranscriptLength > 0 ? (TranscriptLength >= 1000 ? $"{TranscriptLength / 1000.0:F1}k" : $"{TranscriptLength}") : "—";
    public string VoiceDurationLabel => VoiceDurationSec > 0 ? (VoiceDurationSec >= 60 ? $"{VoiceDurationSec / 60:F1}m" : $"{VoiceDurationSec:F0}s") : "—";
    public string VoiceIcon => HasVoice ? "✅" : "—";
    public string TranscribedIcon => HasTranscript ? "✅" : "—";
    /// <summary>Best transcript path for this media file (latest/largest)</summary>
    public string? BestTranscriptPath { get; set; }
    public DateTime LastModified { get; set; }

    // Per-model-variant icons for individual columns
    public bool HasModel(string name) =>
        TranscriptModels.Any(m => m.Equals(name, StringComparison.OrdinalIgnoreCase));

    public string MediumEnIcon => HasModel("medium.en") ? "✅" : "";
    public string SmallEnIcon => HasModel("small.en") ? "✅" : "";
    public string LargeV1Icon => HasModel("large-v1") ? "✅" : "";
    public string LargeV2Icon => HasModel("large-v2") ? "✅" : "";
    public string LargeV3Icon => HasModel("large-v3") ? "✅" : "";
    public string TurboIcon => HasModel("turbo") ? "✅" : "";
    public string MediumIcon => HasModel("medium") ? "✅" : "";
    public string SmallIcon => HasModel("small") ? "✅" : "";
    public string BaseEnIcon => HasModel("base.en") ? "✅" : "";
    public string BaseIcon => HasModel("base") ? "✅" : "";
    public string TinyEnIcon => HasModel("tiny.en") ? "✅" : "";
    public string TinyIcon => HasModel("tiny") ? "✅" : "";

    // LLM meeting detection confidence (0-100, -1 = not scanned)
    public int LlmConfidence { get; set; } = -1;
    public string LlmConfidenceLabel => LlmConfidence >= 0 ? $"{LlmConfidence}" : "";
}

public partial class MainWindow : Window
{
    private PythonRunner _runner = null!;
    private PythonRunner _timestampRunner = null!;
    private ScanReport? _report;
    private readonly string _scriptDir;
    private readonly string _reportPath;
    private string? _selectedTranscriptPath;
    // Media player state
    private DispatcherTimer? _playerTimer;
    private bool _isUserScrubbing;      // true while user drags the seek slider
    private bool _isSyncingTranscript;   // true while timer updates transcript selection (prevents feedback loop)
    private bool _isPlayerPlaying;
    private List<TranscriptLine> _currentTranscriptLines = new();
    private bool _isLoadingMediaFile;  // prevents ModelSelector_SelectionChanged from firing during file load
    private bool _gpuHardwareAvailable;
    private bool _cudaPytorchReady;

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _runner?.Dispose();
        _timestampRunner?.Dispose();
        _gpuTimer?.Stop();
    }

    public MainWindow()
    {
        InitializeComponent();

        // Priority 1: Same directory as exe (published/release builds)
        _scriptDir = AppDomain.CurrentDomain.BaseDirectory;
        if (!File.Exists(Path.Combine(_scriptDir, "fast_engine.py")))
        {
            // Priority 2: Dev build (4 levels up from bin/Debug/net8.0-windows/)
            _scriptDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..\\..\\..\\.."));
        }
        if (!File.Exists(Path.Combine(_scriptDir, "fast_engine.py")))
        {
            // Priority 3: One level up
            _scriptDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
        }
        if (!File.Exists(Path.Combine(_scriptDir, "fast_engine.py")))
        {
            MessageBox.Show(
                $"Could not find 'fast_engine.py' in any expected location.\n\n" +
                $"Searched:\n" +
                $"  • {AppDomain.CurrentDomain.BaseDirectory}\n" +
                $"  • {Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..\\..\\..\\.."))}\n" +
                $"  • {Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."))}\n\n" +
                $"Scan and transcribe will not work.",
                "Missing Script", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LongAudioApp");
        Directory.CreateDirectory(appDataDir);
        _reportPath = Path.Combine(appDataDir, "voice_scan_results.json");
        
        _runner = new PythonRunner(_scriptDir);
        _timestampRunner = new PythonRunner(_scriptDir);

        WireUpRunner();
        SetupTranscriptContextMenu();

        // Settings Init (lightweight — small JSON + UI property sets)
        AppVersionLabel.Text = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";
        LoadAppSettings();

        // Initialize UI from settings
        AnalyticsCheck.IsChecked = _appSettings.AnalyticsEnabled;
        AnalyticsService.IsEnabled = _appSettings.AnalyticsEnabled;
        NoVadCheck.IsChecked = _appSettings.NoVadEnabled;
        if (!string.IsNullOrEmpty(_appSettings.LastDirectory))
            DirectoryBox.Text = _appSettings.LastDirectory;
        foreach (ComboBoxItem item in GpuRefreshCombo.Items)
        {
            if (item.Tag?.ToString() == _appSettings.GpuRefreshIntervalSeconds.ToString())
            {
                item.IsSelected = true;
                break;
            }
        }
        GpuRefreshCombo.SelectedValue = _appSettings.GpuRefreshIntervalSeconds.ToString();
        StartEngineCheck.IsChecked = _appSettings.StartEngineOnLaunch;
        SkipExistingCheck.IsChecked = _appSettings.SkipExistingFiles;
        EnglishOnlyCheck.IsChecked = _appSettings.EnglishOnly;
        ApplyEnglishOnlyFilter();
        foreach (ComboBoxItem item in VadSensitivityCombo.Items)
        {
            if (item.Tag?.ToString() == _appSettings.VadSensitivity.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                item.IsSelected = true;
                break;
            }
        }
        
        foreach (ComboBoxItem item in DeviceCombo.Items)
        {
            if (item.Tag?.ToString() == _appSettings.DevicePreference)
            {
                item.IsSelected = true;
                break;
            }
        }
        _runner.DevicePreference = _appSettings.DevicePreference;

        // Restore Whisper model selection
        foreach (ComboBoxItem item in WhisperModelCombo.Items)
        {
            if (item.Tag?.ToString() == _appSettings.WhisperModel)
            {
                WhisperModelCombo.SelectedItem = item;
                break;
            }
        }

        // Restore window position/size
        RestoreWindowPosition();
        Closing += MainWindow_Closing;

        // Populate drive list
        RefreshDrives();
        RefreshBatchDrives();

        // Defer heavy I/O and subprocess work until after window renders
        ContentRendered += OnContentRendered;
    }

    private void RestoreWindowPosition()
    {
        if (_appSettings.WindowWidth > 0 && _appSettings.WindowHeight > 0)
        {
            Width = _appSettings.WindowWidth;
            Height = _appSettings.WindowHeight;
        }
        if (!double.IsNaN(_appSettings.WindowLeft) && !double.IsNaN(_appSettings.WindowTop))
        {
            // Simple bounds check using virtual screen dimensions
            var left = _appSettings.WindowLeft;
            var top = _appSettings.WindowTop;
            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualWidth = SystemParameters.VirtualScreenWidth;
            var virtualHeight = SystemParameters.VirtualScreenHeight;

            if (left >= virtualLeft - 50 && left < virtualLeft + virtualWidth &&
                top >= virtualTop - 50 && top < virtualTop + virtualHeight)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }
        if (_appSettings.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _appSettings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _appSettings.WindowWidth = Width;
            _appSettings.WindowHeight = Height;
            _appSettings.WindowLeft = Left;
            _appSettings.WindowTop = Top;
        }
        SaveAppSettings();
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered; // One-shot

    // Set the media directory so transcripts can find their source media files
    TranscriptFileInfo.MediaDirectory = DirectoryBox.Text;

        // GPU detection (spawns nvidia-smi subprocess)
        DetectGpu();

        // Check if CUDA PyTorch is installed (one-time)
        _ = CheckCudaPytorchAsync();

        // Load existing scan results + refresh transcript lists (disk I/O + directory scans)
        TryLoadExistingResults();

        // Start GPU monitoring timer
        _gpuTimer = new System.Windows.Threading.DispatcherTimer();
        UpdateGpuTimer();
        _gpuTimer.Tick += (s, args) => DetectGpu();
        if (_appSettings.GpuRefreshIntervalSeconds > 0) _gpuTimer.Start();

        // Start Engine Zombie process check
        StartZombieCheckTimer();

        // Auto-start engine if enabled
        if (_appSettings.StartEngineOnLaunch)
        {
            _ = _runner.StartServerAsync();
        }

        // Check timestamp venv status is now done via button, not on startup
    }

    private DispatcherTimer _gpuTimer;
    private DispatcherTimer? _engineCheckTimer; // Check for zombies/status
    private AppSettings _appSettings = new();
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LongAudioApp", "app_settings.json");

    private void StartZombieCheckTimer()
    {
        _engineCheckTimer = new DispatcherTimer();
        _engineCheckTimer.Interval = TimeSpan.FromSeconds(2); // Faster check for responsiveness
        _engineCheckTimer.Tick += (s, e) => CheckEngineStatus();
        _engineCheckTimer.Start();
        CheckEngineStatus(); // Initial check
    }

    private void CheckEngineStatus()
    {
        // Check managed server status
        bool isManagedRunning = _runner.IsServerRunning;
        
        // Check raw process count (for zombies)
        var processes = Process.GetProcessesByName("fast_engine");
        var count = processes.Length;

        if (isManagedRunning)
        {
             EngineStatusLabel.Text = $"Active (Server Mode)";
             EngineStatusLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4ADE80")); // Green
             EngineToggleBtn.Content = "🛑 Stop Engine";
        }
        else if (count > 0)
        {
            EngineStatusLabel.Text = $"Running ({count} unmanaged)";
            EngineStatusLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FBBF24")); // Amber
            EngineToggleBtn.Content = "⚡ Start Engine";
        }
        else
        {
            EngineStatusLabel.Text = "Idle";
            EngineStatusLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CA3AF")); // Gray
            EngineToggleBtn.Content = "⚡ Start Engine";
        }
    }

    private async void EngineToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        EngineToggleBtn.IsEnabled = false;
        try
        {
            if (_runner.IsServerRunning)
            {
                await _runner.StopServerAsync();
            }
            else
            {
                await _runner.StartServerAsync();
            }
            CheckEngineStatus();
        }
        finally
        {
            EngineToggleBtn.IsEnabled = true;
        }
    }

    private void StartEngineCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_appSettings.StartEngineOnLaunch != (StartEngineCheck.IsChecked ?? false))
        {
            _appSettings.StartEngineOnLaunch = StartEngineCheck.IsChecked ?? false;
            SaveAppSettings();
        }
    }

    private void SkipExistingCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_appSettings.SkipExistingFiles != (SkipExistingCheck.IsChecked ?? false))
        {
            _appSettings.SkipExistingFiles = SkipExistingCheck.IsChecked ?? false;
            SaveAppSettings();
        }
    }

    // ===== DRIVE SELECTOR =====

    private void RefreshDrives()
    {
        DriveList.Items.Clear();
        
        // Add system drives
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                
                var emoji = drive.DriveType switch
                {
                    DriveType.Fixed => "💾",
                    DriveType.Removable => "🔌",
                    DriveType.Network => "🌐",
                    DriveType.CDRom => "💿",
                    _ => "📁"
                };
                
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "" : $" ({drive.VolumeLabel})";
                var freeGB = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
                var totalGB = drive.TotalSize / (1024.0 * 1024 * 1024);
                var displayText = $"{emoji} {drive.Name.TrimEnd('\\')}{label}  [{freeGB:F0}/{totalGB:F0} GB]";

                var cb = new CheckBox
                {
                    Content = displayText,
                    Tag = drive.RootDirectory.FullName,
                    IsChecked = _appSettings.SelectedDrives.Contains(drive.RootDirectory.FullName, StringComparer.OrdinalIgnoreCase),
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CBD5E1")),
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 12, 0),
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                cb.Checked += DriveCheckbox_Changed;
                cb.Unchecked += DriveCheckbox_Changed;
                DriveList.Items.Add(cb);
            }
            catch { /* Drive not accessible */ }
        }
        
        // Add persisted custom folders
        foreach (var folder in _appSettings.CustomFolders)
        {
            if (!Directory.Exists(folder)) continue;
            
            var displayText = $"📂 {Path.GetFileName(folder) ?? folder}";
            var cb = new CheckBox
            {
                Content = displayText,
                Tag = folder,
                IsChecked = _appSettings.SelectedDrives.Contains(folder, StringComparer.OrdinalIgnoreCase),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CBD5E1")),
                FontSize = 12,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = folder
            };
            cb.Checked += DriveCheckbox_Changed;
            cb.Unchecked += DriveCheckbox_Changed;
            DriveList.Items.Add(cb);
        }
    }

    private void DriveCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Tag is string path)
        {
            if (cb.IsChecked == true)
            {
                if (!_appSettings.SelectedDrives.Contains(path, StringComparer.OrdinalIgnoreCase))
                    _appSettings.SelectedDrives.Add(path);
            }
            else
            {
                _appSettings.SelectedDrives.RemoveAll(s => s.Equals(path, StringComparison.OrdinalIgnoreCase));
            }
            SaveAppSettings();
        }
    }

    private List<string> GetSelectedDirectories()
    {
        var dirs = new List<string>();
        foreach (var item in DriveList.Items)
        {
            if (item is CheckBox cb && cb.IsChecked == true && cb.Tag is string path)
            {
                if (Directory.Exists(path))
                    dirs.Add(NormalizePath(path));
            }
        }
        return dirs;
    }

    private void RefreshDrivesBtn_Click(object sender, RoutedEventArgs e)
    {
        RefreshDrives();
    }

    private void KillEngineBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _runner.Dispose(); // Kills managed
            
            var count = 0;
            foreach (var proc in Process.GetProcessesByName("fast_engine"))
            {
                proc.Kill();
                count++;
            }
            MessageBox.Show($"Terminated {count} background processes.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            CheckEngineStatus();
        }
        catch (Exception ex)
        {
             MessageBox.Show($"Failed to kill processes: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void InstallLibsBtn_Click(object sender, RoutedEventArgs e)
    {
        InstallLibsBtn.IsEnabled = false;
        InstallLogBox.Text = "Starting installation process...\n";
        
        string basePython = "python";
        var embeddedPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin", "python", "python.exe");
        if (File.Exists(embeddedPath)) basePython = embeddedPath;
        
        var venvPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fast_engine_venv");
        var venvPython = Path.Combine(venvPath, "Scripts", "python.exe");

        try
        {
            // 1. Create Venv using base python
            if (!File.Exists(venvPython))
            {
                var baseInstaller = new PipInstaller(basePython);
                InstallLogBox.AppendText($"Creating venv at {venvPath}...\n");
                await baseInstaller.CreateVenvAsync(venvPath, log => Dispatcher.Invoke(() => InstallLogBox.AppendText(log + "\n")));
            }
            
            // 2. Install Libs using VENV python
            if (File.Exists(venvPython))
            {
                var venvInstaller = new PipInstaller(venvPython);
                
                if (!venvInstaller.IsPipInstalled())
                {
                    InstallLogBox.AppendText("Pip not found in venv. Installing pip...\n");
                    await venvInstaller.InstallPipAsync(log => Dispatcher.Invoke(() => InstallLogBox.AppendText(log + "\n")));
                }
                
                InstallLogBox.AppendText("Installing libraries into venv...\n");
                InstallErrorSummary.Visibility = Visibility.Collapsed;
                
                var failures = await venvInstaller.InstallLibrariesAsync(log => Dispatcher.Invoke(() => 
                {
                    InstallLogBox.AppendText(log + "\n");
                    InstallLogBox.ScrollToEnd();
                }));

                // Show error summary above the log if any packages failed
                if (failures.Count > 0)
                {
                    InstallErrorSummary.Text = "⚠ Failed packages:\n• " + string.Join("\n• ", failures);
                    InstallErrorSummary.Visibility = Visibility.Visible;
                }

                // 3. Restart Engine to pick up new venv
                if (_runner != null)
                {
                    InstallLogBox.AppendText("Reloading engine...\n");
                    _runner.Dispose();
                    _runner = new PythonRunner(_scriptDir);
                    WireUpRunner();
                    if (_appSettings.StartEngineOnLaunch) _ = _runner.StartServerAsync();
                }
                
                if (failures.Count > 0)
                    MessageBox.Show($"Installation completed with {failures.Count} error(s). Check the error summary for details.", "Partial Success", MessageBoxButton.OK, MessageBoxImage.Warning);
                else
                    MessageBox.Show("Installation complete! The engine has been updated to use the new environment.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                throw new Exception("Venv python executable not found after creation.");
            }
        }
        catch (Exception ex)
        {
            InstallLogBox.AppendText($"[ERROR] {ex.Message}\n");
            MessageBox.Show($"Installation failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            InstallLibsBtn.IsEnabled = true;
        }
    }

    private void PopOpenLogBtn_Click(object sender, RoutedEventArgs e)
    {
        var logWindow = new Window
        {
            Title = "AI Libraries Install Log",
            Width = 900,
            Height = 600,
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0F172A")),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this
        };

        var textBox = new TextBox
        {
            Text = InstallLogBox.Text,
            IsReadOnly = true,
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0F172A")),
            Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#94A3B8")),
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono,Consolas,Courier New"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(12),
            BorderThickness = new Thickness(0)
        };

        logWindow.Content = textBox;
        logWindow.Show();
    }

    private void LoadAppSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                _appSettings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { }
    }

    private void SaveAppSettings()
    {
        try
        {
            var json = JsonSerializer.Serialize(_appSettings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch { }
    }

    public class AppSettings
    {
        public bool AnalyticsEnabled { get; set; } = true;
        public int GpuRefreshIntervalSeconds { get; set; } = 3;
        public bool StartEngineOnLaunch { get; set; } = false;
        public bool SkipExistingFiles { get; set; } = false;
        public string DevicePreference { get; set; } = "cuda"; // "auto", "cuda", or "cpu"
        public bool TurboModeEnabled { get; set; } = true;
        public bool EnglishOnly { get; set; } = false;
        public bool NoVadEnabled { get; set; } = true;
        public string LastDirectory { get; set; } = "";
        public List<string> SelectedDrives { get; set; } = new();
        public List<string> CustomFolders { get; set; } = new();
        public string WhisperModel { get; set; } = "large-v1";
        // Window position/size persistence
        public double WindowWidth { get; set; } = 0;
        public double WindowHeight { get; set; } = 0;
        public double WindowLeft { get; set; } = double.NaN;
        public double WindowTop { get; set; } = double.NaN;
        public bool WindowMaximized { get; set; } = false;
        public double VadSensitivity { get; set; } = 0.5; // 0.0-1.0, lower = more sensitive
    }

    private void GpuRefreshCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GpuRefreshCombo.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int seconds))
        {
            _appSettings.GpuRefreshIntervalSeconds = seconds;
            SaveAppSettings();
            UpdateGpuTimer();
        }
    }

    private void VadSensitivityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VadSensitivityCombo?.SelectedItem is ComboBoxItem item && double.TryParse(item.Tag?.ToString(), System.Globalization.CultureInfo.InvariantCulture, out double threshold))
        {
            _appSettings.VadSensitivity = threshold;
            SaveAppSettings();
        }
    }

    private void UpdateGpuTimer()
    {
        if (_gpuTimer == null) return;
        
        _gpuTimer.Stop();
        if (_appSettings.GpuRefreshIntervalSeconds > 0)
        {
            _gpuTimer.Interval = TimeSpan.FromSeconds(_appSettings.GpuRefreshIntervalSeconds);
            _gpuTimer.Start();
            // Trigger immediate update when switching modes
            DetectGpu();
        }
    }

    private void GpuLabel_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        DetectGpu();
    }

    private void AnalyticsCheck_Click(object sender, RoutedEventArgs e)
    {
        _appSettings.AnalyticsEnabled = AnalyticsCheck.IsChecked ?? true;
        AnalyticsService.IsEnabled = _appSettings.AnalyticsEnabled;
        SaveAppSettings();
        AnalyticsService.TrackEvent("analytics_opt_changed", new { enabled = AnalyticsService.IsEnabled });
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_runner == null) return; // Guard: fires during XAML init before constructor completes
        if (DeviceCombo.SelectedItem is ComboBoxItem item && item.Tag != null)
        {
            var device = item.Tag.ToString() ?? "auto";
            _appSettings.DevicePreference = device;
            _runner.DevicePreference = device;
            SaveAppSettings();
        }
    }

    private int _silentCount = 0;
    private readonly List<string> _silentFiles = new();

    private void WireUpRunner()
    {
        _runner.OutputReceived += line => Dispatcher.BeginInvoke(() =>
        {
            AppendLog(line);

            if (line.Contains("[SILENT]"))
            {
                _silentCount++;
                var path = line.Replace("[SILENT]", "").Trim();
                if (!string.IsNullOrEmpty(path)) _silentFiles.Add(path);
            }

            // When a transcript file is saved, refresh the media file list
            if (line.Contains("[SAVED]"))
            {
                RefreshMediaFileList();
            }

            // Parse semantic search results
            if (line.StartsWith("[SEARCH_RESULTS] "))
            {
                try
                {
                    var jsonStr = line["[SEARCH_RESULTS] ".Length..];
                    var items = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, System.Text.Json.JsonElement>>>(jsonStr);
                    if (items != null)
                    {
                        var results = items.Select(r => new SearchResultItem
                        {
                            FileName = r.ContainsKey("file") ? r["file"].GetString() ?? "" : "",
                            FullPath = r.ContainsKey("full_path") ? r["full_path"].GetString() ?? "" : "",
                            Score = r.ContainsKey("score") ? r["score"].GetDouble() : 0,
                            MatchSnippet = r.ContainsKey("snippet") ? r["snippet"].GetString() ?? "" : ""
                        }).ToList();
                        SearchResultsGrid.ItemsSource = results;
                        SearchStatusLabel.Text = $"Found {results.Count} semantic matches";
                    }
                }
                catch { }
            }

            // Parse analysis results
            if (line.StartsWith("[ANALYSIS_RESULT] "))
            {
                try
                {
                    var jsonStr = line["[ANALYSIS_RESULT] ".Length..];
                    var doc = System.Text.Json.JsonDocument.Parse(jsonStr);
                    var resultText = doc.RootElement.GetProperty("result").GetString() ?? "";
                    var analysisType = doc.RootElement.GetProperty("type").GetString() ?? "";
                    AnalysisOutputBox.Text = resultText;
                    AnalysisStatusLabel.Text = $"{analysisType} complete";
                }
                catch { }
            }

            // Parse meeting detection report
            if (line.StartsWith("[DETECTION_REPORT] "))
            {
                try
                {
                    var jsonStr = line["[DETECTION_REPORT] ".Length..];
                    var results = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(jsonStr);
                    var sb = new System.Text.StringBuilder();
                    int meetings = 0, total = 0;
                    foreach (var item in results.EnumerateArray())
                    {
                        total++;
                        var hasMeeting = item.GetProperty("has_meeting").GetBoolean();
                        var confidence = item.GetProperty("confidence").GetInt32();
                        var reason = item.GetProperty("reason").GetString() ?? "";
                        var filePath = item.GetProperty("file").GetString() ?? "";
                        var file = System.IO.Path.GetFileName(filePath);
                        var icon = hasMeeting ? "✅" : "❌";
                        if (hasMeeting) meetings++;
                        sb.AppendLine($"{icon} {file}  ({confidence})  {reason}");

                        // Populate LlmConfidence on matching media items
                        // Transcript files are named like "basename_transcript_model.txt"
                        // Media files share the same basename
                        if (_allMediaFiles != null)
                        {
                            foreach (var mf in _allMediaFiles)
                            {
                                if (file.StartsWith(mf.BaseName, StringComparison.OrdinalIgnoreCase))
                                {
                                    // Use the highest confidence from any transcript for this media file
                                    var score = hasMeeting ? confidence : 0;
                                    if (score > mf.LlmConfidence) mf.LlmConfidence = score;
                                    break;
                                }
                            }
                        }
                    }
                    sb.Insert(0, $"=== Meeting Detection: {meetings}/{total} meetings found ===\n\n");
                    AnalysisOutputBox.Text = sb.ToString();
                    AnalysisStatusLabel.Text = $"Detection complete — {meetings}/{total} meetings";
                    FindMeetingsBtn.IsEnabled = true;

                    // Refresh the media list to show updated LLM scores
                    ApplyMediaFileView();
                }
                catch { FindMeetingsBtn.IsEnabled = true; }
            }

            // Show per-file detection progress
            if (line.Contains("[MEETING_DETECTED]") || line.Contains("[NO_MEETING]"))
            {
                AnalysisStatusLabel.Text = line.Trim();
            }
            if (line.StartsWith("[DETECT]"))
            {
                AnalysisStatusLabel.Text = line.Trim();
            }
        });

        _runner.ProgressUpdated += (current, total, filename) => Dispatcher.BeginInvoke(() =>
        {
            if (_isScanRunning)
            {
                StatusBar.Text = $"Scanning {current}/{total} files...";
            }
            else
            {
                TranscribeProgress.Maximum = total;
                TranscribeProgress.Value = current;
                TranscribeStatusLabel.Text = $"[{current}/{total}] {Path.GetFileName(filename)} (Silent: {_silentCount})";
                StatusBar.Text = $"Transcribing {current}/{total} files...";
            }
        });

        _runner.VoiceDetected += msg => Dispatcher.BeginInvoke(() =>
        {
            AppendLog(msg);
        });

        _runner.ErrorOccurred += err => Dispatcher.BeginInvoke(() =>
        {
            AppendLog($"[ERROR] {err}");
        });

        _runner.RunningChanged += running => Dispatcher.BeginInvoke(() =>
        {
            TranscribeAllBtn.IsEnabled = !running;
            FindMeetingsBtn.IsEnabled = !running;
            BrowseBtn.IsEnabled = !running;

            CancelTranscribeBtn.IsEnabled = running;

            if (!running)
            {
                if (_isScanRunning)
                {
                    _isScanRunning = false;
                    StatusBar.Text = "Scan complete — loading results...";
                    TryLoadExistingResults();
                }
                else
                {
                    TranscribeStatusLabel.Text = $"Transcription complete. ({_silentCount} silent files)";
                    StatusBar.Text = $"Transcription complete. ({_silentCount} silent files)";
                    RefreshMediaFileList();
                }
            }
        });
    }

    private bool _isScanRunning;
    private List<MediaFileInfo> _allMediaFiles = new();
    private List<TranscriptFileInfo> _allTranscripts = new();
    private volatile bool _batchCancelled;

    private bool _isCheckingGpu = false;

    private async void DetectGpu()
    {
        if (_isCheckingGpu) return;
        _isCheckingGpu = true;

        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=name,utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            var proc = Process.Start(psi);
            if (proc != null)
            {
                var output = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();

                if (string.IsNullOrWhiteSpace(output))
                {
                    _gpuHardwareAvailable = false;
                    GpuLabel.Text = "GPU: Not available (using CPU)";
                    GpuLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94A3B8"));
                    UpdateTurboStatus();
                    return;
                }

                _gpuHardwareAvailable = true;
                var parts = output.Split('\n')[0].Split(',');
                if (parts.Length >= 4)
                {
                    var name = parts[0].Trim();
                    var util = parts[1].Trim();
                    var memUsed = parts[2].Trim();
                    var memTotal = parts[3].Trim();
                    GpuLabel.Text = $"GPU: {name} | Load: {util}% | VRAM: {memUsed}/{memTotal} MiB";
                    GpuLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#22C55E"));
                }
                else
                {
                    GpuLabel.Text = $"GPU: {parts[0].Trim()}";
                }
                UpdateTurboStatus();
            }
        }
        catch
        {
            _gpuHardwareAvailable = false;
            GpuLabel.Text = "GPU: Not detected (using CPU)";
            GpuLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94A3B8"));
            UpdateTurboStatus();
        }
        finally
        {
            _isCheckingGpu = false;
        }
    }

    private async Task CheckCudaPytorchAsync()
    {
        try
        {
            // Find venv python
            var venvPython = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fast_engine_venv", "Scripts", "python.exe");
            if (!File.Exists(venvPython))
            {
                _cudaPytorchReady = false;
                UpdateTurboStatus();
                return;
            }

            var psi = new ProcessStartInfo(venvPython, "-c \"import torch; print(torch.cuda.is_available())\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var proc = Process.Start(psi);
            if (proc != null)
            {
                var output = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();
                _cudaPytorchReady = output.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            _cudaPytorchReady = false;
        }
        Dispatcher.Invoke(UpdateTurboStatus);
    }

    private void UpdateTurboStatus()
    {
        if (!_gpuHardwareAvailable)
        {
            TurboModeCheck.IsChecked = false;
            TurboModeCheck.IsEnabled = false;
            TurboStatusLabel.Text = "❌ No GPU detected";
            TurboStatusLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94A3B8"));
            _runner.DevicePreference = "cpu";
        }
        else if (!_cudaPytorchReady)
        {
            TurboModeCheck.IsChecked = false;
            TurboModeCheck.IsEnabled = true;
            TurboStatusLabel.Text = "⚠ GPU available — install AI libraries to enable";
            TurboStatusLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EAB308"));
            _runner.DevicePreference = "cpu";
        }
        else
        {
            TurboModeCheck.IsEnabled = true;
            TurboModeCheck.IsChecked = _appSettings.TurboModeEnabled;
            TurboStatusLabel.Text = _appSettings.TurboModeEnabled ? "✅ GPU Active" : "CPU mode";
            TurboStatusLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                _appSettings.TurboModeEnabled ? "#22C55E" : "#94A3B8"));
            _runner.DevicePreference = _appSettings.TurboModeEnabled ? "cuda" : "cpu";
        }
    }

    private async void TurboModeCheck_Click(object sender, RoutedEventArgs e)
    {
        var isChecked = TurboModeCheck.IsChecked == true;

        if (isChecked && !_cudaPytorchReady)
        {
            // Prompt to install
            var result = MessageBox.Show(
                "CUDA-enabled AI libraries are not installed. Would you like to install them now?\n\n" +
                "This will download and install PyTorch with CUDA support (~2 GB).\n" +
                "An internet connection is required.",
                "Install AI Libraries",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                // Switch to Log tab and trigger install
                var logTab = MainTabControl.Items.Cast<System.Windows.Controls.TabItem>()
                    .FirstOrDefault(t => t.Header?.ToString()?.Contains("Log") == true);
                if (logTab != null) logTab.IsSelected = true;

                InstallLibsBtn_Click(sender, e);

                // After install completes, re-check CUDA
                await CheckCudaPytorchAsync();
            }
            else
            {
                TurboModeCheck.IsChecked = false;
            }
            return;
        }

        _appSettings.TurboModeEnabled = isChecked;
        _runner.DevicePreference = isChecked ? "cuda" : "cpu";
        _appSettings.DevicePreference = isChecked ? "cuda" : "cpu";
        SaveAppSettings();
        UpdateTurboStatus();

        // Sync DeviceCombo in Settings
        foreach (ComboBoxItem item in DeviceCombo.Items)
        {
            if (item.Tag?.ToString() == (isChecked ? "cuda" : "cpu"))
            {
                DeviceCombo.SelectedItem = item;
                break;
            }
        }
    }

    private void TryLoadExistingResults()
    {
        if (!File.Exists(_reportPath)) return;

        try
        {
            var json = File.ReadAllText(_reportPath);
            _report = JsonSerializer.Deserialize<ScanReport>(json);
            if (_report != null)
            {
                var voiceCount = _report.Results.Count(r => r.Error == null && r.Blocks.Count > 0);
                var blockCount = _report.Results.Where(r => r.Error == null).Sum(r => r.Blocks.Count);
                TranscribeCountLabel.Text = $"{voiceCount} files with voice ({blockCount} blocks)";
                StatusBar.Text = $"Loaded scan results from {_report.ScanDate}";
                // Refresh the media file list
                RefreshMediaFileList();
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] Failed to load results: {ex.Message}");
        }
    }

    private void RefreshMediaFileList()
    {
        _allMediaFiles = BuildMediaFileList();
        ApplyMediaFileView();

        // Also keep _allTranscripts for analysis batch compatibility
        _allTranscripts.Clear();
        foreach (var mf in _allMediaFiles)
        {
            if (mf.BestTranscriptPath != null)
            {
                var ti = new TranscriptFileInfo { FullPath = mf.BestTranscriptPath };
                ti.ReadSize();
                _allTranscripts.Add(ti);
            }
        }

        TranscribeStatusLabel.Text = _allMediaFiles.Count > 0
            ? $"Found {_allMediaFiles.Count} media files ({_allMediaFiles.Count(m => m.HasVoice)} with voice, {_allMediaFiles.Count(m => m.HasTranscript)} transcribed)"
            : "No media files found — set directory and scan";
    }

    private static readonly string[] ModelSuffixes = { "tiny", "tiny.en", "base", "base.en", "small", "small.en", "medium", "medium.en", "large", "large-v1", "large-v2", "large-v3", "turbo" };
    private static readonly string[] MediaExtensions = { ".mp4", ".mp3", ".wav", ".m4a", ".aac", ".ogg", ".flac", ".mkv", ".avi", ".mov", ".webm", ".wma", ".m4v", ".3gp", ".ts", ".mpg", ".mpeg" };

    private List<MediaFileInfo> BuildMediaFileList()
    {
        var mediaFiles = new Dictionary<string, MediaFileInfo>(StringComparer.OrdinalIgnoreCase);
        var transcriptDir = _runner.TranscriptDirectory;

        // 1. Add media files from scan report
        if (_report != null)
        {
            foreach (var result in _report.Results)
            {
                var mf = new MediaFileInfo
                {
                    FullPath = result.File,
                    HasVoice = result.Error == null && result.Blocks.Count > 0,
                    DurationSec = result.DurationSec,
                    VoiceDurationSec = result.SpeechDurationSec > 0 
                        ? result.SpeechDurationSec 
                        : result.Blocks.Sum(b => b.End - b.Start),
                    LastModified = File.Exists(result.File) ? new FileInfo(result.File).LastWriteTime : DateTime.MinValue
                };
                mediaFiles[result.File] = mf;
            }
        }

        // 2. Scan for transcript files and cross-reference with media files
        var transcriptFiles = new List<string>();

        // AppData transcripts
        if (Directory.Exists(transcriptDir))
        {
            try { transcriptFiles.AddRange(Directory.GetFiles(transcriptDir, "*_transcript*.txt", SearchOption.TopDirectoryOnly)); }
            catch { }
        }

        // Scan ALL selected directories (checked drives + custom folders)
        var selectedDirs = GetSelectedDirectories();
        // Also include DirectoryBox.Text as fallback if nothing is selected
        var dirBoxPath = NormalizePath(DirectoryBox.Text);
        if (!string.IsNullOrEmpty(dirBoxPath) && Directory.Exists(dirBoxPath)
            && !selectedDirs.Any(d => d.Equals(dirBoxPath, StringComparison.OrdinalIgnoreCase)))
        {
            selectedDirs.Add(dirBoxPath);
        }

        foreach (var dir in selectedDirs)
        {
            if (!Directory.Exists(dir)) continue;

            // Discover transcripts in each selected directory
            try { transcriptFiles.AddRange(Directory.GetFiles(dir, "*_transcript*.txt", SearchOption.AllDirectories)); }
            catch { }

            // Discover media files in each selected directory
            try
            {
                foreach (var ext in MediaExtensions)
                {
                    foreach (var f in Directory.GetFiles(dir, $"*{ext}", SearchOption.AllDirectories))
                    {
                        if (!mediaFiles.ContainsKey(f))
                            mediaFiles[f] = new MediaFileInfo { FullPath = f, LastModified = new FileInfo(f).LastWriteTime };
                    }
                }
            }
            catch { }
        }

        // 3. Match transcripts to media files
        foreach (var tf in transcriptFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var tfName = Path.GetFileNameWithoutExtension(tf);
            var idx = tfName.IndexOf("_transcript", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            var mediaBaseName = tfName[..idx];
            var modelPart = tfName[(idx + "_transcript".Length)..].TrimStart('_');
            var modelName = string.IsNullOrEmpty(modelPart) ? "default" : modelPart;

            // Find matching media file
            var matchKey = mediaFiles.Keys.FirstOrDefault(k =>
                Path.GetFileNameWithoutExtension(k).Equals(mediaBaseName, StringComparison.OrdinalIgnoreCase));

            if (matchKey != null)
            {
                var mf = mediaFiles[matchKey];
                mf.HasTranscript = true;
                if (!mf.TranscriptModels.Contains(modelName, StringComparer.OrdinalIgnoreCase))
                    mf.TranscriptModels.Add(modelName);

                // Set best transcript (latest/largest)
                if (mf.BestTranscriptPath == null || new FileInfo(tf).Length > (File.Exists(mf.BestTranscriptPath) ? new FileInfo(mf.BestTranscriptPath).Length : 0))
                {
                    mf.BestTranscriptPath = tf;
                    try { mf.TranscriptLength = (int)new FileInfo(tf).Length; } catch { }
                }
            }
        }

        return mediaFiles.Values.ToList();
    }



    private void TranscriptSortCombo_Changed(object sender, SelectionChangedEventArgs e) => ApplyMediaFileView();
    private void TranscriptFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyMediaFileView();
        if (FilterPlaceholder != null)
            FilterPlaceholder.Visibility = string.IsNullOrEmpty(TranscriptFilterBox.Text) 
                ? Visibility.Visible : Visibility.Collapsed;
    }
    // AnalysisSortCombo_Changed and AnalysisFilterBox_TextChanged removed — Analysis tab merged

    private void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog()
        {
            Title = "Select media directory to scan"
        };
        if (dialog.ShowDialog() == true)
        {
            var folder = dialog.FolderName;
            DirectoryBox.Text = folder;
            TranscriptFileInfo.MediaDirectory = folder;
            
            // Add as a custom folder checkbox if not already present
            if (!_appSettings.CustomFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                _appSettings.CustomFolders.Add(folder);
                _appSettings.SelectedDrives.Add(folder);
                SaveAppSettings();
                RefreshDrives();
            }
        }
    }

    /// <summary>Normalize bare drive letters (e.g. "C:") to root paths ("C:\") so Directory.Exists and os.walk work correctly.</summary>
    private static string NormalizePath(string path)
    {
        path = path.Trim().Replace('/', '\\');
        // "C:" or "D:" without trailing separator refers to CWD on that drive, not the root
        if (path.Length == 2 && char.IsLetter(path[0]) && path[1] == ':')
            path += Path.DirectorySeparatorChar;
        return path;
    }

    // ScanBtn_Click removed — Scan tab has been removed

    // BatchTranscribeBtn_Click removed — Voice Only button has been removed

    private string GetSelectedWhisperModel()
    {
        var model = (WhisperModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "large-v1";
        _appSettings.WhisperModel = model;
        SaveAppSettings();
        return model;
    }

    private async void TranscribeAllBtn_Click(object sender, RoutedEventArgs e)
    {
        var dirs = GetSelectedDirectories();
        if (dirs.Count == 0)
        {
            MessageBox.Show("Please check at least one drive or add a custom folder.", "No Drives Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isScanRunning = false;
        _silentCount = 0;
        _silentFiles.Clear();

        TranscribeProgress.Value = 0;
        bool useVad = !(NoVadCheck.IsChecked ?? false);
        _appSettings.NoVadEnabled = NoVadCheck.IsChecked ?? true;
        SaveAppSettings();
        var model = GetSelectedWhisperModel();
        AnalyticsService.TrackEvent("transcribe_all", new { drive_count = dirs.Count, model });

        TranscribeAllBtn.IsEnabled = false;
        FindMeetingsBtn.IsEnabled = false;
        CancelTranscribeBtn.IsEnabled = true;

        try
        {
            for (int i = 0; i < dirs.Count; i++)
            {
                var dir = dirs[i];
                TranscribeStatusLabel.Text = $"[{i + 1}/{dirs.Count}] Transcribing: {dir}";
                StatusBar.Text = $"Transcribing drive {i + 1}/{dirs.Count}: {dir}";
                DirectoryBox.Text = dir; // For downstream code that reads DirectoryBox.Text
                TranscriptFileInfo.MediaDirectory = dir;

                await _runner.RunBatchTranscribeDirAsync(dir, useVad, skipExisting: _appSettings.SkipExistingFiles, model: model);
            }
            TranscribeStatusLabel.Text = $"Done — transcribed {dirs.Count} location(s)";
            StatusBar.Text = "Transcription complete";
            RefreshMediaFileList();
        }
        catch (Exception ex)
        {
            TranscribeStatusLabel.Text = $"Error: {ex.Message}";
        }
        finally
        {
            TranscribeAllBtn.IsEnabled = true;
            FindMeetingsBtn.IsEnabled = true;
            CancelTranscribeBtn.IsEnabled = false;
        }
    }





    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        _runner.Cancel();
        StatusBar.Text = "Cancelling...";
    }

    private void LoadResultsBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog()
        {
            Title = "Select scan results JSON",
            Filter = "JSON files (*.json)|*.json",
            InitialDirectory = _scriptDir
        };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                var json = File.ReadAllText(dialog.FileName);
                _report = JsonSerializer.Deserialize<ScanReport>(json);
                if (_report != null)
                {
                    var voiceCount = _report.Results.Count(r => r.Error == null && r.Blocks.Count > 0);
                    var blockCount = _report.Results.Where(r => r.Error == null).Sum(r => r.Blocks.Count);
                    TranscribeCountLabel.Text = $"{voiceCount} files with voice ({blockCount} blocks)";
                    StatusBar.Text = $"Loaded results from {dialog.FileName}";
                    RefreshMediaFileList();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void RefreshTranscriptsBtn_Click(object sender, RoutedEventArgs e)
    {
        RefreshMediaFileList();
    }

    /// <summary>Helper to get a TranscriptFileInfo from the currently selected MediaFileInfo</summary>
    private TranscriptFileInfo? GetSelectedTranscriptFromMediaList()
    {
        if (MediaFileList.SelectedItem is MediaFileInfo mf && mf.BestTranscriptPath != null)
        {
            var ti = new TranscriptFileInfo { FullPath = mf.BestTranscriptPath };
            ti.ReadSize();
            return ti;
        }
        return null;
    }

    private void MediaFileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MediaFileList.SelectedItem is MediaFileInfo mf)
        {
            _isLoadingMediaFile = true;

            // Load best transcript if available
            if (mf.BestTranscriptPath != null && File.Exists(mf.BestTranscriptPath))
            {
                _selectedTranscriptPath = mf.BestTranscriptPath;
                var info = new TranscriptFileInfo { FullPath = mf.BestTranscriptPath };
                info.ReadSize();
                try
                {
                    var content = File.ReadAllText(info.FullPath);
                    LoadTranscriptLines(content);

                    TranscriptFileLabel.Text = $"📂 {mf.FullPath}";
                    OpenMediaBtn.Visibility = File.Exists(mf.FullPath) ? Visibility.Visible : Visibility.Collapsed;
                    OpenInExplorerBtn.Visibility = Visibility.Visible;

                    InlineSummarizeBtn.IsEnabled = true;
                    InlineOutlineBtn.IsEnabled = true;
                    RetranscribeBtn.IsEnabled = true;
                    RetranscribePanel.Visibility = Visibility.Visible;

                    var versions = FindSiblingVersions(info);
                    CompareBtn.Content = versions.Count > 1 ? $"📊 Compare ({versions.Count})" : "📊 Compare";
                    CompareBtn.IsEnabled = versions.Count > 1;
                    CompareBtn.Visibility = versions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

                    // Auto-select the model in ModelSelector matching this transcript
                    AutoSelectModelForTranscript(mf.BestTranscriptPath);

                    // Hide old VersionCombo — ModelSelector handles version switching now
                    VersionCombo.Visibility = Visibility.Collapsed;
                    InlineVersionLabel.Text = mf.TranscriptModels.Count > 1
                        ? $"{mf.TranscriptModels.Count} versions"
                        : "";

                    LoadMediaForTranscript(mf.FullPath);

                    StatusBar.Text = $"Viewing: {mf.FileName}" +
                        (mf.TranscriptModels.Count > 0 ? $" (models: {mf.ModelBadges})" : "");
                }
                catch (Exception ex)
                {
                    TranscriptLineList.ItemsSource = null;
                    AnalysisOutputBox.Text = $"Error reading transcript: {ex.Message}";
                }
            }
            else
            {
                // No transcript yet — show info
                _selectedTranscriptPath = null;
                TranscriptLineList.ItemsSource = null;
                TranscriptFileLabel.Text = $"📂 {mf.FullPath}";
                AnalysisOutputBox.Text = "No transcript yet — run Transcribe All Files to generate.";
                OpenMediaBtn.Visibility = File.Exists(mf.FullPath) ? Visibility.Visible : Visibility.Collapsed;
                OpenInExplorerBtn.Visibility = Visibility.Collapsed;
                InlineSummarizeBtn.IsEnabled = false;
                InlineOutlineBtn.IsEnabled = false;
                RetranscribeBtn.IsEnabled = true;
                RetranscribePanel.Visibility = Visibility.Visible;
                CompareBtn.Visibility = Visibility.Collapsed;
                VersionCombo.Visibility = Visibility.Collapsed;
                InlineVersionLabel.Text = "";
                LoadMediaForTranscript(mf.FullPath);
                StatusBar.Text = $"Selected: {mf.FileName} (no transcript)";
            }

            _isLoadingMediaFile = false;
        }
    }

    /// <summary>Auto-select the model in ModelSelector that matches the loaded transcript path.</summary>
    private void AutoSelectModelForTranscript(string transcriptPath)
    {
        var tfName = Path.GetFileNameWithoutExtension(transcriptPath);
        var idx = tfName.IndexOf("_transcript", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return;

        var modelPart = tfName[(idx + "_transcript".Length)..].TrimStart('_');
        if (string.IsNullOrEmpty(modelPart)) return;

        // Find matching ComboBoxItem in ModelSelector
        foreach (ComboBoxItem item in ModelSelector.Items)
        {
            var tag = item.Tag?.ToString() ?? "";
            if (tag.Equals(modelPart, StringComparison.OrdinalIgnoreCase))
            {
                ModelSelector.SelectedItem = item;
                break;
            }
        }
    }

    /// <summary>Find the transcript file path for a given media file and model name.</summary>
    private string? GetTranscriptPathForModel(string mediaPath, string modelName)
    {
        var mediaBaseName = Path.GetFileNameWithoutExtension(mediaPath);
        var expectedName = $"{mediaBaseName}_transcript_{modelName}.txt";

        // Check transcript directory first
        var transcriptDir = _runner.TranscriptDirectory;
        if (Directory.Exists(transcriptDir))
        {
            var path = Path.Combine(transcriptDir, expectedName);
            if (File.Exists(path)) return path;
        }

        // Check same directory as media file
        var mediaDir = Path.GetDirectoryName(mediaPath);
        if (!string.IsNullOrEmpty(mediaDir))
        {
            var path = Path.Combine(mediaDir, expectedName);
            if (File.Exists(path)) return path;
        }

        return null;
    }

    /// <summary>When user changes model, load matching transcript version or auto-retranscribe.</summary>
    private async void ModelSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingMediaFile) return; // Don't fire during file selection
        if (MediaFileList.SelectedItem is not MediaFileInfo mf) return;

        var model = (ModelSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (string.IsNullOrEmpty(model)) return;

        var transcriptPath = GetTranscriptPathForModel(mf.FullPath, model);

        if (transcriptPath != null)
        {
            // Transcript exists for this model — load it
            try
            {
                _selectedTranscriptPath = transcriptPath;
                var content = File.ReadAllText(transcriptPath);
                LoadTranscriptLines(content);
                StatusBar.Text = $"Viewing: {mf.FileName} ({model})";
            }
            catch (Exception ex)
            {
                TranscriptLineList.ItemsSource = null;
                StatusBar.Text = $"Error reading transcript: {ex.Message}";
            }
        }
        else
        {
            // No transcript for this model — auto-retranscribe
            if (!File.Exists(mf.FullPath))
            {
                MessageBox.Show("Could not find the source media file.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                TranscribeStatusLabel.Text = $"Transcribing with {model}...";
                StatusBar.Text = $"Transcribing {mf.FileName} with {model}...";

                bool useVad = !(NoVadCheck.IsChecked ?? false);
                await _runner.RunTranscribeFileAsync(mf.FullPath, model, useVad, skipExisting: false);

                // After transcription completes, try loading the new transcript
                var newPath = GetTranscriptPathForModel(mf.FullPath, model);
                if (newPath != null)
                {
                    try
                    {
                        _selectedTranscriptPath = newPath;
                        var content = File.ReadAllText(newPath);
                        LoadTranscriptLines(content);
                        StatusBar.Text = $"Viewing: {mf.FileName} ({model})";

                        // Refresh the file list to update model badges
                        RefreshMediaFileList();
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                StatusBar.Text = $"No {model} transcript — {ex.Message}";
                TranscribeStatusLabel.Text = "Use Re-transcribe when ready";
            }
        }
    }

    private void OpenInExplorerBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTranscriptPath != null && File.Exists(_selectedTranscriptPath))
        {
            Process.Start("explorer.exe", $"/select,\"{_selectedTranscriptPath}\"");
        }
    }

    private async void RetranscribeBtn_Click(object sender, RoutedEventArgs e)
    {
        string? mediaPath = null;
        if (MediaFileList.SelectedItem is MediaFileInfo mf)
            mediaPath = mf.FullPath;
        else
            return;

        if (mediaPath == null || !File.Exists(mediaPath))
        {
            MessageBox.Show("Could not find the source media file.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var model = (ModelSelector.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "large-v3";
        _isScanRunning = false;
        TranscribeStatusLabel.Text = $"Re-transcribing with {model}...";
        StatusBar.Text = $"Re-transcribing {Path.GetFileName(mediaPath)} with {model}...";

        bool useVad = !(NoVadCheck.IsChecked ?? false);
        await _runner.RunTranscribeFileAsync(mediaPath, model, useVad, skipExisting: false);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchPlaceholder != null)
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) 
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        var query = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(query)) { RefreshMediaFileList(); return; }

        var dir = NormalizePath(DirectoryBox.Text);
        if (!Directory.Exists(dir)) return;

        // Local in-process search — fast, no Python needed
        var queryWords = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var allTranscripts = _allTranscripts;

        // If list is empty, discover files first
        if (allTranscripts.Count == 0)
        {
            try
            {
                var found = Directory.GetFiles(dir, "*_transcript*.txt", SearchOption.AllDirectories);
                foreach (var f in found)
                {
                    var ti = new TranscriptFileInfo { FullPath = f };
                    ti.ReadSize();
                    allTranscripts.Add(ti);
                }
            }
            catch { }
        }

        // Filter and score
        var results = new List<(TranscriptFileInfo info, int score, string snippet)>();
        foreach (var t in allTranscripts)
        {
            try
            {
                var content = File.ReadAllText(t.FullPath).ToLowerInvariant();
                var score = queryWords.Count(w => content.Contains(w));
                if (score == 0) continue;

                // Find first matching line for snippet
                var lines = File.ReadAllLines(t.FullPath);
                var snippet = lines.FirstOrDefault(l => queryWords.Any(w => l.ToLowerInvariant().Contains(w))) ?? "";
                results.Add((t, score, snippet));
            }
            catch { }
        }

        results.Sort((a, b) => b.score.CompareTo(a.score));
        // Show matching transcripts — search results shown in output area
        TranscribeStatusLabel.Text = $"Search: {results.Count} files match \"{query}\"";

        // Show search results in the content area
        if (results.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Search results for: \"{query}\" ({results.Count} files)\n");
            foreach (var (info, score, snippet) in results)
            {
                sb.AppendLine($"--- {info.FileName} ({score}/{queryWords.Length} words matched) ---");
                sb.AppendLine($"  {snippet}");
                sb.AppendLine();
            }
            SetContentBoxText(sb.ToString());
        }
    }

    // ===== COMPARE VERSIONS =====

    private List<string> FindSiblingVersions(TranscriptFileInfo info)
    {
        var dir = info.FolderPath;
        var name = Path.GetFileNameWithoutExtension(info.FullPath);
        var idx = name.IndexOf("_transcript");
        if (idx < 0) return new List<string> { info.FullPath };
        var baseName = name[..idx];

        try
        {
            return Directory.GetFiles(dir, $"{baseName}_transcript*")
                .OrderBy(f => f)
                .ToList();
        }
        catch { return new List<string> { info.FullPath }; }
    }

    private void CompareBtn_Click(object sender, RoutedEventArgs e)
    {
        var info = GetSelectedTranscriptFromMediaList();
        if (info == null) return;

        var versions = FindSiblingVersions(info);
        if (versions.Count < 2)
        {
            MessageBox.Show("Only one version exists. Re-transcribe with a different model first.",
                "Compare", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Read all versions — strip the header line, keep just transcript lines
        var versionData = new List<(string label, string[] lines)>();
        foreach (var vpath in versions)
        {
            var fname = Path.GetFileNameWithoutExtension(vpath);
            var label = fname.Contains("_transcript_") 
                ? fname[(fname.IndexOf("_transcript_") + 12)..] 
                : "default";
            var allLines = File.ReadAllLines(vpath)
                .Where(l => !l.StartsWith("---")).ToArray();
            versionData.Add((label, allLines));
        }

        // Build a text-based diff in AnalysisOutputBox
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Comparing {versionData.Count} versions for: {Path.GetFileName(info.SourceMediaPath ?? info.FileName)}");
        sb.AppendLine();

        // Compare first version vs each other version
        var baseline = versionData[0];
        for (int v = 1; v < versionData.Count; v++)
        {
            var compare = versionData[v];
            sb.AppendLine($"=== {baseline.label} vs {compare.label} ===");
            sb.AppendLine();

            var maxLines = Math.Max(baseline.lines.Length, compare.lines.Length);
            for (int i = 0; i < maxLines; i++)
            {
                var lineA = i < baseline.lines.Length ? baseline.lines[i].Trim() : "";
                var lineB = i < compare.lines.Length ? compare.lines[i].Trim() : "";

                if (lineA == lineB)
                {
                    sb.AppendLine($"  {lineA}");
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(lineA)) sb.AppendLine($"- [{baseline.label}] {lineA}");
                    if (!string.IsNullOrWhiteSpace(lineB)) sb.AppendLine($"+ [{compare.label}] {lineB}");
                }
            }
            sb.AppendLine();
        }
        AnalysisOutputBox.Text = sb.ToString();
        StatusBar.Text = $"Comparing {versionData.Count} transcript versions";
    }

    // ===== HELPERS =====

    private void SetContentBoxText(string text)
    {
        AnalysisOutputBox.Text = text;
    }

    private static Paragraph MakeParagraph(string text, string hexColor, bool bold)
    {
        var color = (Color)ColorConverter.ConvertFromString(hexColor);
        var run = new Run(text) { Foreground = new SolidColorBrush(color) };
        if (bold) run.FontWeight = FontWeights.Bold;
        return new Paragraph(run) { Margin = new Thickness(0, 0, 0, 1) };
    }

    private void AppendLog(string text)
    {
        LogBox.AppendText(text + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private void ClearLog()
    {
        LogBox.Clear();
    }
    private string _currentSortColumn = "LastModified";
    private ListSortDirection _currentSortDirection = ListSortDirection.Descending;

    private void ApplyMediaFileView()
    {
        if (MediaFileList == null) return;
        var filter = TranscriptFilterBox?.Text?.Trim() ?? "";

        IEnumerable<MediaFileInfo> filtered = _allMediaFiles;

        // Filter by current folder if checkbox is checked
        if (CurrentFolderOnlyCheck?.IsChecked == true)
        {
            var activeDirs = GetSelectedDirectories();

            if (activeDirs.Count > 0)
            {
                // Ensure paths end with separator so "C:\media" doesn't match "C:\media_other"
                var normalizedDirs = activeDirs.Select(d => 
                    d.EndsWith(Path.DirectorySeparatorChar) ? d : d + Path.DirectorySeparatorChar).ToList();
                filtered = filtered.Where(m =>
                    normalizedDirs.Any(d => m.FullPath.StartsWith(d, StringComparison.OrdinalIgnoreCase)));
            }
        }

        if (!string.IsNullOrEmpty(filter))
            filtered = filtered.Where(m => m.FileName.Contains(filter, StringComparison.OrdinalIgnoreCase));

        // Use custom sort logic based on column header clicks
        Func<MediaFileInfo, object> keySelector = _currentSortColumn switch
        {
            "Voice" => m => m.VoiceDurationSec,
            "Transcribed" => m => m.HasTranscript,
            "FileName" => m => m.FileName,
            "Duration" => m => m.DurationSec,
            "TranscriptLength" => m => m.TranscriptLength,
            "medium.en" => m => m.HasModel("medium.en"),
            "small.en" => m => m.HasModel("small.en"),
            "large-v1" => m => m.HasModel("large-v1"),
            "large-v2" => m => m.HasModel("large-v2"),
            "large-v3" => m => m.HasModel("large-v3"),
            "turbo" => m => m.HasModel("turbo"),
            "medium" => m => m.HasModel("medium"),
            "small" => m => m.HasModel("small"),
            "base.en" => m => m.HasModel("base.en"),
            "base" => m => m.HasModel("base"),
            "tiny.en" => m => m.HasModel("tiny.en"),
            "tiny" => m => m.HasModel("tiny"),
            _ => m => m.LastModified
        };

        var allFiltered = filtered.ToList();

        // Split into transcribed and untranscribed
        var transcribed = allFiltered.Where(m => m.HasTranscript).ToList();
        var untranscribed = allFiltered.Where(m => !m.HasTranscript).ToList();

        // Sort transcribed list
        MediaFileList.ItemsSource = _currentSortDirection == ListSortDirection.Ascending
            ? transcribed.OrderBy(keySelector).ToList()
            : transcribed.OrderByDescending(keySelector).ToList();

        // Populate untranscribed list (sorted by voice duration descending — most promising first)
        if (UntranscribedFileList != null)
        {
            UntranscribedFileList.ItemsSource = untranscribed
                .OrderByDescending(m => m.VoiceDurationSec)
                .ToList();
        }

        // Update untranscribed count label
        if (UntranscribedCountLabel != null)
            UntranscribedCountLabel.Text = $"({untranscribed.Count})";
    }

    private void RefreshMediaListBtn_Click(object sender, RoutedEventArgs e)
    {
        RefreshMediaFileList();
    }

    private void CurrentFolderOnlyCheck_Click(object sender, RoutedEventArgs e)
    {
        RefreshMediaFileList();
    }

    private void ShowUntranscribedCheck_Click(object sender, RoutedEventArgs e)
    {
        if (UntranscribedFileList != null)
        {
            UntranscribedFileList.Visibility = ShowUntranscribedCheck.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void DeleteAllTranscriptsBtn_Click(object sender, RoutedEventArgs e)
    {
        var transcriptDir = _runner.TranscriptDirectory;
        if (!Directory.Exists(transcriptDir))
        {
            MessageBox.Show("No transcript directory found.", "Delete Transcripts", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var files = Directory.GetFiles(transcriptDir, "*.txt", SearchOption.AllDirectories);
        if (files.Length == 0)
        {
            MessageBox.Show("No transcript files found.", "Delete Transcripts", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"Delete {files.Length} transcript files from:\n{transcriptDir}?\n\nThis cannot be undone.",
            "Delete All Transcripts",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            int deleted = 0;
            foreach (var f in files)
            {
                try { File.Delete(f); deleted++; } catch { }
            }
            AppendLog($"[INFO] Deleted {deleted}/{files.Length} transcript files.");
            StatusBar.Text = $"Deleted {deleted} transcript files.";
            RefreshMediaFileList();
        }
    }

    private void MediaFileList_HeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader header && header.Column != null)
        {
            var column = header.Content as string;
            if (string.IsNullOrEmpty(column)) return;

            // Map header names to property names
            var property = column switch
            {
                "🎙️ Voice" => "Voice",
                "📝" => "Transcribed",
                "📁 Filename" => "FileName",
                "📄" => "TranscriptLength",
                "med.en" => "medium.en",
                "sm.en" => "small.en",
                "lg-v1" => "large-v1",
                "lg-v2" => "large-v2",
                "lg-v3" => "large-v3",
                "turbo" => "turbo",
                "med" => "medium",
                "small" => "small",
                "bs.en" => "base.en",
                "base" => "base",
                "ty.en" => "tiny.en",
                "tiny" => "tiny",
                _ => "LastModified"
            };

            if (_currentSortColumn == property)
            {
                // Toggle direction
                _currentSortDirection = _currentSortDirection == ListSortDirection.Ascending
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending;
            }
            else
            {
                _currentSortColumn = property;
                _currentSortDirection = ListSortDirection.Ascending;
            }

            ApplyMediaFileView();
        }
    }

    private async void FindMeetingsBtn_Click(object sender, RoutedEventArgs e)
    {
        var dir = NormalizePath(DirectoryBox.Text);
        if (!Directory.Exists(dir))
        {
            MessageBox.Show("Please select a valid directory first.", "Invalid Directory", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Get LLM provider settings from the UI
        var provider = (LlmProviderCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "local";
        string? model = null;
        string? apiKey = null;
        string? cloudModel = null;

        if (provider == "local")
        {
            model = (LocalModelCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString();
        }
        else
        {
            apiKey = ApiKeyBox.Password;
            cloudModel = CloudModelBox.Text;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                MessageBox.Show($"Please enter an API key for {provider}.", "API Key Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        FindMeetingsBtn.IsEnabled = false;
        AnalysisStatusLabel.Text = "Starting LLM meeting detection...";
        AnalysisOutputBox.Text = "";

        try
        {
            await _runner.RunDetectMeetingsAsync(dir, provider,
                model: model, apiKey: apiKey, cloudModel: cloudModel,
                transcriptDir: _runner.TranscriptDirectory);
        }
        catch (Exception ex)
        {
            AnalysisStatusLabel.Text = $"Error: {ex.Message}";
            MessageBox.Show($"Error during meeting detection: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            FindMeetingsBtn.IsEnabled = true;
        }
    }

    private void ViewLogBtn_Click(object sender, RoutedEventArgs e)
    {
        var dir = NormalizePath(DirectoryBox.Text);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            MessageBox.Show("Select a valid directory first.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var logPath = Path.Combine(dir, "scan_debug.log");
        if (File.Exists(logPath))
        {
            try { Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show("Failed to open log: " + ex.Message); }
        }
        else
        {
            MessageBox.Show($"Log file not found at:\n{logPath}\n\nRun a scan first.", "Log Not Found", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ViewSilentBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_silentFiles.Count == 0)
        {
            MessageBox.Show("No silent files detected.", "Silent Files", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dir = NormalizePath(DirectoryBox.Text);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) dir = _scriptDir;

        var path = Path.Combine(dir, "silent_files.txt");
        try
        {
            File.WriteAllLines(path, _silentFiles);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open silent files list: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }



    private void SetupTranscriptContextMenu()
    {
        var ctx = new ContextMenu();

        var openTranscript = new MenuItem { Header = "📄 Open Transcript" };
        openTranscript.Click += (s, e) =>
        {
            var info = GetSelectedTranscriptFromMediaList();
            if (info != null && File.Exists(info.FullPath))
            {
                try { Process.Start(new ProcessStartInfo(info.FullPath) { UseShellExecute = true }); }
                catch { }
            }
        };

        var openInPlayer = new MenuItem { Header = "▶ Open Media File in Player" };
        openInPlayer.Click += (s, e) =>
        {
            if (MediaFileList.SelectedItem is MediaFileInfo mf && File.Exists(mf.FullPath))
            {
                try { Process.Start(new ProcessStartInfo(mf.FullPath) { UseShellExecute = true }); }
                catch { }
            }
            else
            {
                MessageBox.Show("Could not find the source media file.", "Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };

        var revealInExplorer = new MenuItem { Header = "📂 Reveal in Explorer" };
        revealInExplorer.Click += (s, e) =>
        {
            if (MediaFileList.SelectedItem is MediaFileInfo mf && File.Exists(mf.FullPath))
            {
                Process.Start("explorer.exe", $"/select,\"{mf.FullPath}\"");
            }
        };

        var summarize = new MenuItem { Header = "📝 Summarize" };
        summarize.Click += async (s, e) =>
        {
            var info = GetSelectedTranscriptFromMediaList();
            if (info != null && File.Exists(info.FullPath))
                await RunContextMenuAnalysis(info.FullPath, "summarize");
        };

        var outline = new MenuItem { Header = "📋 Outline" };
        outline.Click += async (s, e) =>
        {
            var info = GetSelectedTranscriptFromMediaList();
            if (info != null && File.Exists(info.FullPath))
                await RunContextMenuAnalysis(info.FullPath, "outline");
        };

        var deleteTranscript = new MenuItem { Header = "🗑️ Delete Transcript" };
        deleteTranscript.Click += (s, e) =>
        {
            var info = GetSelectedTranscriptFromMediaList();
            if (info != null && File.Exists(info.FullPath))
            {
                var result = MessageBox.Show(
                    $"Delete \"{info.FileName}\"?\n\nThis cannot be undone.",
                    "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        File.Delete(info.FullPath);
                        RefreshMediaFileList();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to delete: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        };

        ctx.Items.Add(openTranscript);
        ctx.Items.Add(new Separator());
        ctx.Items.Add(openInPlayer);
        ctx.Items.Add(revealInExplorer);
        ctx.Items.Add(new Separator());
        ctx.Items.Add(summarize);
        ctx.Items.Add(outline);
        ctx.Items.Add(new Separator());
        ctx.Items.Add(deleteTranscript);
        MediaFileList.ContextMenu = ctx;
    }

    /// <summary>Run analysis from context menu — uses Analysis tab settings, switches to Analysis tab to show results.</summary>
    private async Task RunContextMenuAnalysis(string transcriptPath, string analyzeType)
    {
        // Stay on Transcribe tab (index 0) to show output
        // Analysis tab was merged into Transcribe tab

        var provider = (LlmProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "local";
        string? model = null;
        string? apiKey = null;
        string? cloudModel = null;

        if (provider == "local")
        {
            model = (LocalModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "phi-3-mini";
        }
        else
        {
            apiKey = ApiKeyBox.Password.Trim();
            cloudModel = CloudModelBox.Text.Trim();
            if (string.IsNullOrEmpty(apiKey))
            {
                AnalysisStatusLabel.Text = "Enter an API key in the Analysis tab first";
                return;
            }
        }

        AnalysisStatusLabel.Text = $"Running {analyzeType} on {Path.GetFileName(transcriptPath)}...";
        AnalysisOutputBox.Text = "Processing...";
        InlineSummarizeBtn.IsEnabled = false;
        InlineOutlineBtn.IsEnabled = false;
        CancelAnalysisBtn.IsEnabled = true;

        try
        {
            await _runner.RunAnalyzeAsync(transcriptPath, analyzeType, provider, model, apiKey, cloudModel);
        }
        catch (Exception ex)
        {
            AnalysisStatusLabel.Text = $"Analysis failed: {ex.Message}";
        }
        finally
        {
            InlineSummarizeBtn.IsEnabled = true;
            InlineOutlineBtn.IsEnabled = true;
            CancelAnalysisBtn.IsEnabled = false;
        }
    }

    // ===== OPEN TRANSCRIPT FOLDER =====

    private void OpenTranscriptFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        var dir = _runner.TranscriptDirectory;
        if (Directory.Exists(dir))
            Process.Start("explorer.exe", $"\"{dir}\"");
        else
            MessageBox.Show($"Transcript folder not found:\n{dir}", "Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ===== OPEN MEDIA FILE =====

    private void OpenMediaBtn_Click(object sender, RoutedEventArgs e)
    {
        if (MediaFileList.SelectedItem is MediaFileInfo mf && File.Exists(mf.FullPath))
        {
            Process.Start("explorer.exe", $"/select,\"{mf.FullPath}\"");
        }
        else
        {
            MessageBox.Show("Could not find the source media file.", "Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ===== ENGLISH ONLY FILTER =====

    private void EnglishOnlyCheck_Click(object sender, RoutedEventArgs e)
    {
        _appSettings.EnglishOnly = EnglishOnlyCheck.IsChecked ?? false;
        SaveAppSettings();
        ApplyEnglishOnlyFilter();
    }

    private void ApplyEnglishOnlyFilter()
    {
        bool englishOnly = _appSettings.EnglishOnly;
        foreach (ComboBoxItem item in ModelSelector.Items)
        {
            var tag = item.Tag?.ToString() ?? "";
            if (englishOnly)
            {
                // Show only .en models + large-v3 + turbo (no .en variant exists for these)
                item.Visibility = tag.EndsWith(".en") || tag == "large-v3" || tag == "turbo"
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                item.Visibility = Visibility.Visible;
            }
        }
        // If current selection is collapsed, select first visible
        if (ModelSelector.SelectedItem is ComboBoxItem sel && sel.Visibility != Visibility.Visible)
        {
            foreach (ComboBoxItem item in ModelSelector.Items)
            {
                if (item.Visibility == Visibility.Visible)
                {
                    ModelSelector.SelectedItem = item;
                    break;
                }
            }
        }
    }

    // ===== SEMANTIC SEARCH TAB =====

    private void SemanticSearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
            SemanticSearchBtn_Click(sender, e);
    }

    private async void SemanticSearchBtn_Click(object sender, RoutedEventArgs e)
    {
        var query = SemanticSearchBox.Text.Trim();
        if (string.IsNullOrEmpty(query)) return;

        var mode = (SearchModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "exact";
        SearchStatusLabel.Text = $"Searching ({mode})...";
        SemanticSearchBtn.IsEnabled = false;

        try
        {
            if (mode == "exact")
            {
                // Local in-process exact search
                await Task.Run(() => RunExactSearch(query));
            }
            else
            {
                // Semantic search via fast_engine.py
                var dir = NormalizePath(DirectoryBox.Text);
                var embedModel = (EmbeddingModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all-MiniLM-L6-v2";
                SearchStatusLabel.Text = $"Loading {embedModel} and searching...";
                await _runner.RunSemanticSearchAsync(dir, query, embedModel, _runner.TranscriptDirectory);
            }
        }
        catch (Exception ex)
        {
            SearchStatusLabel.Text = $"Search failed: {ex.Message}";
        }
        finally
        {
            SemanticSearchBtn.IsEnabled = true;
        }
    }

    private void RunExactSearch(string query)
    {
        var dir = "";
        var transcriptDir = "";
        Dispatcher.Invoke(() =>
        {
            dir = NormalizePath(DirectoryBox.Text);
            transcriptDir = _runner.TranscriptDirectory;
        });

        var results = new List<SearchResultItem>();
        var queryLower = query.ToLowerInvariant();

        // Search all transcript files
        var files = new List<string>();
        if (Directory.Exists(transcriptDir))
            files.AddRange(Directory.GetFiles(transcriptDir, "*_transcript*.txt"));
        if (Directory.Exists(dir))
        {
            try { files.AddRange(Directory.GetFiles(dir, "*_transcript*.txt", SearchOption.AllDirectories)); }
            catch { }
        }

        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var lines = File.ReadAllLines(file);
                foreach (var line in lines)
                {
                    if (line.ToLowerInvariant().Contains(queryLower))
                    {
                        results.Add(new SearchResultItem
                        {
                            FileName = Path.GetFileName(file),
                            FullPath = file,
                            Score = 1.0,
                            MatchSnippet = line.Trim()
                        });
                    }
                }
            }
            catch { }
        }

        Dispatcher.Invoke(() =>
        {
            SearchResultsGrid.ItemsSource = results;
            SearchStatusLabel.Text = $"Found {results.Count} matches for \"{query}\"";
        });
    }

    // ===== ANALYSIS TAB =====

    private void LlmProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LlmProviderCombo == null || LocalModelCombo == null || CloudApiPanel == null) return;
        var tag = (LlmProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "local";
        LocalModelCombo.Visibility = tag == "local" ? Visibility.Visible : Visibility.Collapsed;
        CloudApiPanel.Visibility = tag != "local" ? Visibility.Visible : Visibility.Collapsed;
        
        // Update default model name based on provider
        if (tag == "gemini") CloudModelBox.Text = "gemini-2.0-flash";
        else if (tag == "openai") CloudModelBox.Text = "gpt-4o";
        else if (tag == "claude") CloudModelBox.Text = "claude-sonnet-4-20250514";
    }

    // SummarizeAllBtn_Click and OutlineAllBtn_Click removed — functionality now in InlineSummarizeBtn_Click / InlineOutlineBtn_Click

    private async void SummarizeBatchBtn_Click(object sender, RoutedEventArgs e)
    {
        await RunBatchAnalysis("summarize");
    }

    private async void OutlineBatchBtn_Click(object sender, RoutedEventArgs e)
    {
        await RunBatchAnalysis("outline");
    }

    private async Task RunBatchAnalysis(string analyzeType)
    {
        if (_allTranscripts.Count == 0)
        {
            AnalysisStatusLabel.Text = "No transcripts loaded";
            return;
        }

        var provider = (LlmProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "local";
        string? model = null, apiKey = null, cloudModel = null;

        if (provider == "local")
            model = (LocalModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "phi-3-mini";
        else
        {
            apiKey = ApiKeyBox.Password.Trim();
            cloudModel = CloudModelBox.Text.Trim();
            if (string.IsNullOrEmpty(apiKey))
            {
                AnalysisStatusLabel.Text = "Enter an API key first";
                return;
            }
        }

        InlineSummarizeBtn.IsEnabled = false;
        InlineOutlineBtn.IsEnabled = false;
        SummarizeBatchBtn.IsEnabled = false;
        OutlineBatchBtn.IsEnabled = false;
        CancelAnalysisBtn.IsEnabled = true;
        AnalysisOutputBox.Text = "";
        _batchCancelled = false;

        var total = _allTranscripts.Count;
        var results = new System.Text.StringBuilder();
        int skipped = 0;

        for (int i = 0; i < total; i++)
        {
            var t = _allTranscripts[i];
            AnalysisStatusLabel.Text = $"[{i + 1}/{total}] {analyzeType}: {t.FileName}";
            // AnalysisProgress removed  no separate progress bar

            if (_batchCancelled) break;

            // Check if output already exists
            var outputPath = Path.ChangeExtension(t.FullPath, null) + $"_{analyzeType}.txt";
            bool shouldSkip = analyzeType == "summarize"
                ? true
                : true;
            if (shouldSkip && File.Exists(outputPath))
            {
                skipped++;
                AnalysisStatusLabel.Text = $"[{i + 1}/{total}] Skipped (exists): {t.FileName}";
                continue;
            }

            // Select corresponding media file to show progress
            var mfMatch = _allMediaFiles.FirstOrDefault(m => m.BestTranscriptPath?.Equals(t.FullPath, StringComparison.OrdinalIgnoreCase) == true);
            if (mfMatch != null) MediaFileList.SelectedItem = mfMatch;

            try
            {
                await _runner.RunAnalyzeAsync(t.FullPath, analyzeType, provider, model, apiKey, cloudModel);
            }
            catch (OperationCanceledException)
            {
                results.AppendLine($"\n--- CANCELLED at {i + 1}/{total} ---");
                break;
            }
            catch (Exception ex)
            {
                results.AppendLine($"\n--- ERROR on {t.FileName}: {ex.Message} ---");
            }
        }

        // AnalysisProgress removed
        var skipMsg = skipped > 0 ? $", {skipped} skipped" : "";
        AnalysisStatusLabel.Text = $"Batch {analyzeType} complete ({total} files{skipMsg})";
        InlineSummarizeBtn.IsEnabled = true;
        InlineOutlineBtn.IsEnabled = true;
        SummarizeBatchBtn.IsEnabled = true;
        OutlineBatchBtn.IsEnabled = true;
        CancelAnalysisBtn.IsEnabled = false;
    }

    private async Task RunAnalysisOnSelected(string analyzeType)
    {
        var info = GetSelectedTranscriptFromMediaList();
        if (info == null)
        {
            AnalysisStatusLabel.Text = "Select a media file with transcript first";
            return;
        }

        var provider = (LlmProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "local";
        string? model = null;
        string? apiKey = null;
        string? cloudModel = null;

        if (provider == "local")
        {
            model = (LocalModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "phi-3-mini";
        }
        else
        {
            apiKey = ApiKeyBox.Password.Trim();
            cloudModel = CloudModelBox.Text.Trim();
            if (string.IsNullOrEmpty(apiKey))
            {
                AnalysisStatusLabel.Text = "Enter an API key first";
                return;
            }
        }

        AnalysisStatusLabel.Text = $"Running {analyzeType} with {provider}...";
        AnalysisOutputBox.Text = "Processing...";
        InlineSummarizeBtn.IsEnabled = false;
        InlineOutlineBtn.IsEnabled = false;
        CancelAnalysisBtn.IsEnabled = true;

        try
        {
            await _runner.RunAnalyzeAsync(info.FullPath, analyzeType, provider, model, apiKey, cloudModel);
        }
        catch (Exception ex)
        {
            AnalysisStatusLabel.Text = $"Analysis failed: {ex.Message}";
        }
        finally
        {
            InlineSummarizeBtn.IsEnabled = true;
            InlineOutlineBtn.IsEnabled = true;
            CancelAnalysisBtn.IsEnabled = false;
        }
    }

    private void CancelAnalysisBtn_Click(object sender, RoutedEventArgs e)
    {
        _batchCancelled = true;
        _runner.Cancel();
        AnalysisStatusLabel.Text = "Cancelled";
    }

    // AnalysisTranscriptList_SelectionChanged removed — Analysis tab merged into Transcribe tab.
    // MediaFileList_SelectionChanged (above) handles all selection logic now.

    // ===== TRANSCRIPT CONTEXT MENU =====

    private async void ContextSummarize_Click(object sender, RoutedEventArgs e)
    {
        var info = GetSelectedTranscriptFromMediaList();
        if (info != null)
            await RunAnalysisOnTranscript(info.FullPath, "summarize");
    }

    private async void ContextOutline_Click(object sender, RoutedEventArgs e)
    {
        var info = GetSelectedTranscriptFromMediaList();
        if (info != null)
            await RunAnalysisOnTranscript(info.FullPath, "outline");
    }

    // ContextOpenAnalysis_Click removed — Analysis tab merged into Transcribe tab

    private void ContextOpenMediaPlayer_Click(object sender, RoutedEventArgs e)
    {
        if (MediaFileList.SelectedItem is not MediaFileInfo mf) return;
        if (File.Exists(mf.FullPath))
        {
            Process.Start(new ProcessStartInfo(mf.FullPath) { UseShellExecute = true });
        }
        else
        {
            MessageBox.Show("Source media file not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ContextRevealExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (MediaFileList.SelectedItem is not MediaFileInfo mf) return;
        if (File.Exists(mf.FullPath))
        {
            Process.Start("explorer.exe", $"/select,\"{mf.FullPath}\"");
        }
    }

    private async void InlineSummarizeBtn_Click(object sender, RoutedEventArgs e)
    {
        var info = GetSelectedTranscriptFromMediaList();
        if (info != null)
            await RunAnalysisOnTranscript(info.FullPath, "summarize");
    }

    private async void InlineOutlineBtn_Click(object sender, RoutedEventArgs e)
    {
        var info = GetSelectedTranscriptFromMediaList();
        if (info != null)
            await RunAnalysisOnTranscript(info.FullPath, "outline");
    }

    private void VersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VersionCombo.SelectedItem is ComboBoxItem item && item.Tag is string versionPath)
        {
            try
            {
                AnalysisOutputBox.Text = File.ReadAllText(versionPath);
                AnalysisStatusLabel.Text = $"Viewing: {Path.GetFileName(versionPath)}";
            }
            catch (Exception ex)
            {
                AnalysisOutputBox.Text = $"Error reading file: {ex.Message}";
            }
        }
    }

    private async void AnalysisContextSummarize_Click(object sender, RoutedEventArgs e)
    {
        var info = GetSelectedTranscriptFromMediaList();
        if (info != null)
            await RunAnalysisOnTranscript(info.FullPath, "summarize");
    }

    private async void AnalysisContextOutline_Click(object sender, RoutedEventArgs e)
    {
        var info = GetSelectedTranscriptFromMediaList();
        if (info != null)
            await RunAnalysisOnTranscript(info.FullPath, "outline");
    }

    private async Task RunAnalysisOnTranscript(string transcriptPath, string analyzeType)
    {
        var provider = (LlmProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "local";
        string? model = null, apiKey = null, cloudModel = null;

        if (provider == "local")
            model = (LocalModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "phi-3-mini";
        else
        {
            apiKey = ApiKeyBox.Password.Trim();
            cloudModel = CloudModelBox.Text.Trim();
            if (string.IsNullOrEmpty(apiKey))
            {
                MessageBox.Show("Enter an API key in the Analysis tab first.", "API Key Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        // Stay on Transcribe tab (index 0) to show progress



        AnalysisStatusLabel.Text = $"Running {analyzeType} with {provider}...";
        AnalysisOutputBox.Text = "Processing...";
        InlineSummarizeBtn.IsEnabled = false;
        InlineOutlineBtn.IsEnabled = false;
        CancelAnalysisBtn.IsEnabled = true;

        try
        {
            await _runner.RunAnalyzeAsync(transcriptPath, analyzeType, provider, model, apiKey, cloudModel);
        }
        catch (Exception ex)
        {
            AnalysisStatusLabel.Text = $"Analysis failed: {ex.Message}";
        }
        finally
        {
            InlineSummarizeBtn.IsEnabled = true;
            InlineOutlineBtn.IsEnabled = true;
            CancelAnalysisBtn.IsEnabled = false;
        }
    }

    // ===== MEDIA PLAYER HANDLERS =====

    // Matches HH:MM:SS or H:MM:SS format, e.g. [0:34:20] or [1:02:15.5]
    private static readonly System.Text.RegularExpressions.Regex _timestampRegex = 
        new(@"^\[?(\d{1,2}):(\d{2}):(\d{2}(?:\.\d+)?)\]?\s*(.*)", System.Text.RegularExpressions.RegexOptions.Compiled);

    // Matches seconds-range format, e.g. [2060.00 – 2065.00] or [2060.00 - 2065.00]
    private static readonly System.Text.RegularExpressions.Regex _secondsRangeRegex =
        new(@"^\[(\d+(?:\.\d+)?)\s*[\u2013\-]\s*(\d+(?:\.\d+)?)\]\s*(.*)", System.Text.RegularExpressions.RegexOptions.Compiled);

    private void LoadTranscriptLines(string content)
    {
        var newLines = new List<TranscriptLine>();
        var lines = content.Split('\n');
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Try HH:MM:SS format first
            var match = _timestampRegex.Match(line);
            if (match.Success)
            {
                int h = int.Parse(match.Groups[1].Value);
                int m = int.Parse(match.Groups[2].Value);
                double s = double.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                var offset = new TimeSpan(0, h, m, (int)s, (int)((s - (int)s) * 1000));
                newLines.Add(new TranscriptLine
                {
                    Offset = offset,
                    TimeLabel = $"[{h}:{m:D2}:{(int)s:D2}]",
                    Text = match.Groups[4].Value.Trim()
                });
                continue;
            }

            // Try seconds-range format: [2060.00 – 2065.00] text
            var rangeMatch = _secondsRangeRegex.Match(line);
            if (rangeMatch.Success)
            {
                double startSec = double.Parse(rangeMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                var offset = TimeSpan.FromSeconds(startSec);
                int h = (int)offset.TotalHours;
                int m = offset.Minutes;
                int s = offset.Seconds;
                newLines.Add(new TranscriptLine
                {
                    Offset = offset,
                    TimeLabel = $"[{h}:{m:D2}:{s:D2}]",
                    Text = rangeMatch.Groups[3].Value.Trim()
                });
                continue;
            }

            // Non-timestamped line
            newLines.Add(new TranscriptLine
            {
                Offset = TimeSpan.Zero,
                TimeLabel = "",
                Text = line
            });
        }
        _currentTranscriptLines = newLines;
        TranscriptLineList.ItemsSource = null;
        TranscriptLineList.ItemsSource = _currentTranscriptLines;
    }

    private void DetectTranscriptVersions(TranscriptFileInfo info)
    {
        var nameNoExt = Path.GetFileNameWithoutExtension(info.FileName);
        string[] modelSuffixes = { "_tiny", "_tiny.en", "_base", "_base.en", "_small", "_small.en", "_medium", "_medium.en", "_large", "_large-v1", "_large-v2", "_large-v3", "_turbo" };
        var baseName = nameNoExt;
        foreach (var suffix in modelSuffixes.OrderByDescending(s => s.Length))
        {
            if (nameNoExt.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                baseName = nameNoExt[..^suffix.Length];
                break;
            }
        }

        var dir = Path.GetDirectoryName(info.FullPath);
        if (string.IsNullOrEmpty(dir)) return;

        var versions = Directory.GetFiles(dir, $"{baseName}*.txt")
            .Where(f => !f.EndsWith("_summary.txt") && !f.EndsWith("_outline.txt"))
            .OrderBy(f => f)
            .ToList();

        if (versions.Count > 1)
        {
            VersionCombo.Items.Clear();
            foreach (var v in versions)
            {
                var vName = Path.GetFileNameWithoutExtension(v);
                var label = vName.Length > baseName.Length ? vName[baseName.Length..].TrimStart('_', '-') : "default";
                if (string.IsNullOrEmpty(label)) label = "default";
                VersionCombo.Items.Add(new ComboBoxItem { Content = label, Tag = v });
            }
            for (int i = 0; i < VersionCombo.Items.Count; i++)
            {
                if ((VersionCombo.Items[i] as ComboBoxItem)?.Tag?.ToString() == info.FullPath)
                {
                    VersionCombo.SelectedIndex = i;
                    break;
                }
            }
            InlineVersionLabel.Text = $"{versions.Count} versions:";
            VersionCombo.Visibility = Visibility.Visible;
        }
        else
        {
            InlineVersionLabel.Text = "";
            VersionCombo.Visibility = Visibility.Collapsed;
        }
    }

    private void LoadMediaForTranscript(string? mediaPath)
    {
        if (mediaPath != null && File.Exists(mediaPath))
        {
            try
            {
                MediaPlayer.Source = new Uri(mediaPath);
                MediaPlayer.Volume = MediaVolumeSlider.Value;
                MediaPlayerPanel.Visibility = Visibility.Visible;
                MediaControlsGrid.Visibility = Visibility.Visible;
                MediaNotFoundLabel.Visibility = Visibility.Collapsed;
                PlayPauseBtn.Content = "▶";
                _isPlayerPlaying = false;

                // Initialize player timer for sync
                if (_playerTimer == null)
                {
                    _playerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                    _playerTimer.Tick += PlayerTimer_Tick;
                }
            }
            catch
            {
                MediaPlayerPanel.Visibility = Visibility.Collapsed;
            }
        }
        else if (mediaPath != null)
        {
            // Media file not found — show message instead of hiding entirely
            MediaPlayerPanel.Visibility = Visibility.Visible;
            MediaControlsGrid.Visibility = Visibility.Collapsed;
            MediaNotFoundLabel.Visibility = Visibility.Visible;
            MediaNotFoundLabel.Text = $"⚠ Media file not found: {mediaPath}";
        }
        else
        {
            MediaPlayerPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void PlayPauseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlayerPlaying)
        {
            MediaPlayer.Pause();
            _playerTimer?.Stop();
            PlayPauseBtn.Content = "▶";
            _isPlayerPlaying = false;
        }
        else
        {
            MediaPlayer.Play();
            _playerTimer?.Start();
            PlayPauseBtn.Content = "⏸";
            _isPlayerPlaying = true;
        }
    }

    private void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        MediaPlayer.Stop();
        _playerTimer?.Stop();
        PlayPauseBtn.Content = "▶";
        _isPlayerPlaying = false;
        MediaSeekSlider.Value = 0;
        MediaTimeLabel.Text = "0:00 / 0:00";
    }

    private void MediaPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (MediaPlayer.NaturalDuration.HasTimeSpan)
        {
            var dur = MediaPlayer.NaturalDuration.TimeSpan;
            MediaSeekSlider.Maximum = dur.TotalSeconds;
            MediaTimeLabel.Text = $"0:00 / {(int)dur.TotalMinutes}:{dur.Seconds:D2}";
        }
    }

    private void MediaPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        MediaPlayer.Stop();
        _playerTimer?.Stop();
        PlayPauseBtn.Content = "▶";
        _isPlayerPlaying = false;
        MediaSeekSlider.Value = 0;
    }

    private void MediaSeekSlider_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        _isUserScrubbing = true;
    }

    private void MediaSeekSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _isUserScrubbing = false;
        MediaPlayer.Position = TimeSpan.FromSeconds(MediaSeekSlider.Value);
    }

    private void MediaVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MediaPlayer != null)
            MediaPlayer.Volume = e.NewValue;
    }

    private void PlayerTimer_Tick(object? sender, EventArgs e)
    {
        // Determine current playback position
        // When user is scrubbing, use slider value; otherwise use actual player position
        TimeSpan pos;
        if (_isUserScrubbing)
        {
            pos = TimeSpan.FromSeconds(MediaSeekSlider.Value);
        }
        else
        {
            if (!_isPlayerPlaying) return;
            pos = MediaPlayer.Position;
            MediaSeekSlider.Value = pos.TotalSeconds;
        }

        if (MediaPlayer.NaturalDuration.HasTimeSpan)
        {
            var dur = MediaPlayer.NaturalDuration.TimeSpan;
            MediaTimeLabel.Text = $"{(int)pos.TotalMinutes}:{pos.Seconds:D2} / {(int)dur.TotalMinutes}:{dur.Seconds:D2}";
        }

        // Bidirectional sync: highlight the current transcript line
        if (_currentTranscriptLines.Count > 0)
        {
            TranscriptLine? best = null;
            foreach (var tl in _currentTranscriptLines)
            {
                if (tl.Offset <= pos && tl.Offset != TimeSpan.Zero)
                    best = tl;
            }
            if (best != null && TranscriptLineList.SelectedItem != best)
            {
                _isSyncingTranscript = true; // prevent re-entrant seek
                TranscriptLineList.SelectedItem = best;
                TranscriptLineList.ScrollIntoView(best);
                _isSyncingTranscript = false;
            }
        }
    }

    private void TranscriptLineList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingTranscript) return; // Avoid re-entrant loop from timer
        if (TranscriptLineList.SelectedItem is TranscriptLine line && line.Offset != TimeSpan.Zero)
        {
            MediaPlayer.Position = line.Offset;
            if (!_isPlayerPlaying)
            {
                MediaPlayer.Play();
                _playerTimer?.Start();
                PlayPauseBtn.Content = "⏸";
                _isPlayerPlaying = true;
            }
        }
    }

    private void SaveAnalysisBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AnalysisOutputBox.Text) || _selectedTranscriptPath == null) return;
        var dir = Path.GetDirectoryName(_selectedTranscriptPath);
        var baseName = Path.GetFileNameWithoutExtension(_selectedTranscriptPath);
        var savePath = Path.Combine(dir ?? ".", $"{baseName}_analysis.txt");
        try
        {
            File.WriteAllText(savePath, AnalysisOutputBox.Text);
            SaveAnalysisBtn.IsEnabled = false;
            AnalysisStatusLabel.Text = $"Saved to {Path.GetFileName(savePath)}";
            StatusBar.Text = $"Analysis saved to {savePath}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void InlineEnglishOnlyCheck_Click(object sender, RoutedEventArgs e)
    {
        bool englishOnly = InlineEnglishOnlyCheck.IsChecked ?? false;
        foreach (ComboBoxItem item in ModelSelector.Items)
        {
            var tag = item.Tag?.ToString() ?? "";
            bool isEnglish = tag.EndsWith(".en");
            item.Visibility = (englishOnly && !isEnglish) ? Visibility.Collapsed : Visibility.Visible;
        }
        // Ensure a visible item is selected
        if (ModelSelector.SelectedItem is ComboBoxItem sel && sel.Visibility == Visibility.Collapsed)
        {
            var first = ModelSelector.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Visibility == Visibility.Visible);
            if (first != null) ModelSelector.SelectedItem = first;
        }
    }

    // ===== TIMESTAMPS TAB =====

    private const string TIMESTAMP_VENV_NAME = "timestamp_venv";
    private string? _selectedTimestampVideoPath;


    private async void CheckTimestampStatusBtn_Click(object sender, RoutedEventArgs e)
    {
        CheckTimestampStatusBtn.IsEnabled = false;
        CheckTimestampStatusBtn.Content = "⏳ Checking...";
        TimestampLogConsole.Text = "";
        await CheckTimestampVenvStatusAsync();
        CheckTimestampStatusBtn.IsEnabled = true;
        CheckTimestampStatusBtn.Content = "🔄 Check Status";
    }

    private async Task CheckTimestampVenvStatusAsync()
    {
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var venvPython = Path.Combine(appDir, TIMESTAMP_VENV_NAME, "Scripts", "python.exe");
        var tsScript = Path.Combine(_scriptDir, "timestamp_engine.py");

        Action<string> log = msg => Dispatcher.Invoke(() =>
        {
            TimestampLogConsole.AppendText(msg + "\n");
            TimestampLogConsole.ScrollToEnd();
        });

        int passed = 0;
        int total = 4;

        // 1. Check venv exists
        log("── Checking timestamp_venv ──");
        if (File.Exists(venvPython))
        {
            log($"✅ Venv python found: {venvPython}");
            passed++;
        }
        else
        {
            log($"❌ Venv python NOT found: {venvPython}");
            log("   → Click 'Install VLM Dependencies' to create it.");
            TimestampVenvStatus.Text = "⚠ Not installed";
            TimestampVenvStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF8866"));
            return;
        }

        // 2. Check timestamp_engine.py exists
        log("\n── Checking timestamp_engine.py ──");
        if (File.Exists(tsScript))
        {
            log($"✅ Script found: {tsScript}");
            passed++;
        }
        else
        {
            log($"❌ Script NOT found: {tsScript}");
        }

        // 3. Check ffmpeg accessible
        log("\n── Checking ffmpeg ──");
        try
        {
            var ffmpegCheck = new ProcessStartInfo
            {
                FileName = venvPython,
                Arguments = "-c \"import shutil; p = shutil.which('ffmpeg'); print('FFMPEG_PATH=' + (p or 'NONE')); p2 = shutil.which('ffprobe'); print('FFPROBE_PATH=' + (p2 or 'NONE'))\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            // Ensure system PATH is available
            var pyDir = Path.GetDirectoryName(venvPython);
            if (!string.IsNullOrEmpty(pyDir))
            {
                var envPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                ffmpegCheck.EnvironmentVariables["PATH"] = pyDir + ";" + envPath;
            }

            using var proc = Process.Start(ffmpegCheck);
            if (proc != null)
            {
                var output = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();

                bool ffmpegOk = output.Contains("FFMPEG_PATH=") && !output.Contains("FFMPEG_PATH=NONE");
                bool ffprobeOk = output.Contains("FFPROBE_PATH=") && !output.Contains("FFPROBE_PATH=NONE");

                if (ffmpegOk && ffprobeOk)
                {
                    log("✅ ffmpeg and ffprobe found on PATH");
                    foreach (var line in output.Trim().Split('\n'))
                        log($"   {line.Trim()}");
                    passed++;
                }
                else
                {
                    if (!ffmpegOk) log("❌ ffmpeg NOT found on PATH");
                    if (!ffprobeOk) log("❌ ffprobe NOT found on PATH");
                    log("   → Install ffmpeg: winget install ffmpeg");
                }
            }
        }
        catch (Exception ex)
        {
            log($"❌ ffmpeg check failed: {ex.Message}");
        }

        // 4. Check Python imports
        log("\n── Checking VLM imports ──");
        try
        {
            var importCheck = new ProcessStartInfo
            {
                FileName = venvPython,
                Arguments = "-c \"import torch; print('torch=' + torch.__version__); print('cuda=' + str(torch.cuda.is_available())); from transformers import Qwen2_5_VLForConditionalGeneration; print('transformers=OK'); from PIL import Image; print('pillow=OK'); from qwen_vl_utils import process_vision_info; print('qwen_vl_utils=OK')\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            importCheck.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";

            using var proc = Process.Start(importCheck);
            if (proc != null)
            {
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token);
                var stderr = await proc.StandardError.ReadToEndAsync(cts.Token);
                try { await proc.WaitForExitAsync(cts.Token); } catch (OperationCanceledException)
                {
                    log("⏰ Import check timed out after 30s — imports may be slow but could still work.");
                    try { proc.Kill(); } catch { }
                }

                if (proc.ExitCode == 0)
                {
                    log("✅ All VLM packages importable:");
                    foreach (var line in stdout.Trim().Split('\n'))
                        log($"   {line.Trim()}");
                    passed++;
                }
                else
                {
                    log("❌ Import check failed:");
                    foreach (var line in stderr.Trim().Split('\n'))
                        log($"   {line.Trim()}");
                }
            }
        }
        catch (Exception ex)
        {
            log($"❌ Import check failed: {ex.Message}");
        }

        // Summary
        log($"\n══ Result: {passed}/{total} checks passed ══");

        if (passed == total)
        {
            TimestampVenvStatus.Text = "✅ Ready";
            TimestampVenvStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#66FF88"));
            InstallTimestampDepsBtn.Content = "📦 Reinstall VLM Dependencies";
        }
        else
        {
            TimestampVenvStatus.Text = $"⚠ {passed}/{total} checks passed";
            TimestampVenvStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFAA44"));
        }
    }

    private async void InstallTimestampDepsBtn_Click(object sender, RoutedEventArgs e)
    {
        InstallTimestampDepsBtn.IsEnabled = false;
        TimestampInstallLog.Visibility = Visibility.Visible;
        TimestampInstallLog.Text = "Starting VLM dependency installation...\n";

        // Find base python
        string basePython = "python";
        var embeddedPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin", "python", "python.exe");
        if (File.Exists(embeddedPath)) basePython = embeddedPath;

        var venvPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, TIMESTAMP_VENV_NAME);
        var venvPython = Path.Combine(venvPath, "Scripts", "python.exe");

        Action<string> log = msg => Dispatcher.Invoke(() =>
        {
            TimestampInstallLog.AppendText(msg + "\n");
            TimestampInstallLog.ScrollToEnd();
        });

        try
        {
            // 1. Create venv if not exists
            if (!File.Exists(venvPython))
            {
                var baseInstaller = new PipInstaller(basePython);
                log($"Creating dedicated venv at {venvPath}...");
                await baseInstaller.CreateVenvAsync(venvPath, log);
            }

            if (File.Exists(venvPython))
            {
                var installer = new PipInstaller(venvPython);

                if (!installer.IsPipInstalled())
                {
                    log("Pip not found in venv. Installing pip...");
                    await installer.InstallPipAsync(log);
                }

                // 2. Install torch with CUDA 12.8 support (required for RTX 50-series sm_120)
                log("Installing PyTorch with CUDA 12.8 support (RTX 50-series compatible)...");
                await installer.TryRunCommandAsync(
                    "-m pip install torch torchvision --upgrade --no-warn-script-location --index-url https://download.pytorch.org/whl/cu128", log);

                // 3. Install transformers + accelerate
                log("Installing transformers + accelerate...");
                await installer.TryRunCommandAsync(
                    "-m pip install transformers accelerate --upgrade --no-warn-script-location --prefer-binary", log);

                // 4. Install qwen-vl-utils
                log("Installing qwen-vl-utils...");
                await installer.TryRunCommandAsync(
                    "-m pip install qwen-vl-utils --upgrade --no-warn-script-location --prefer-binary", log);

                // 5. Install Pillow
                log("Installing Pillow...");
                await installer.TryRunCommandAsync(
                    "-m pip install Pillow --upgrade --no-warn-script-location --prefer-binary", log);

                log("\n✅ All VLM dependencies installed successfully!");
                await CheckTimestampVenvStatusAsync();
                MessageBox.Show("VLM dependencies installed successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                log("[ERROR] Failed to create venv — python.exe not found.");
            }
        }
        catch (Exception ex)
        {
            log($"[ERROR] Installation failed: {ex.Message}");
            MessageBox.Show($"Installation failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            InstallTimestampDepsBtn.IsEnabled = true;
        }
    }

    private void TimestampBrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Video File",
            Filter = "Video Files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.ts;*.m4v|All Files|*.*",
            Multiselect = false
        };
        if (dlg.ShowDialog() == true)
        {
            _selectedTimestampVideoPath = dlg.FileName;
            TimestampFileLabel.Text = dlg.FileName;
            ExtractTimestampsBtn.IsEnabled = true;
            TimestampStatusLabel.Text = "Ready — click Extract to begin";
        }
    }

    private async void ExtractTimestampsBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedTimestampVideoPath)) return;

        // Get frame count from combo
        var selectedItem = TimestampFrameCountCombo.SelectedItem as ComboBoxItem;
        int numFrames = int.TryParse(selectedItem?.Tag?.ToString(), out var nf) ? nf : 5;

        // UI state
        ExtractTimestampsBtn.IsEnabled = false;
        TimestampBrowseBtn.IsEnabled = false;
        CancelTimestampBtn.IsEnabled = true;
        TimestampProgress.Value = 0;
        TimestampProgress.IsIndeterminate = true;
        TimestampStatusLabel.Text = "Starting extraction...";
        TimestampResultsGrid.ItemsSource = null;
        TimestampConsensusLabel.Text = "—";
        TimestampSummaryLabel.Text = "Extracting timestamps...";
        TimestampLogConsole.Text = ""; // Clear log console

        var results = new List<TimestampResult>();
        string? consensus = null;
        int framesReadable = 0;
        int framesExtracted = 0;

        // Hook into output to parse progress
        Action<string> outputHandler = (line) =>
        {
            Dispatcher.Invoke(() =>
            {
                // Parse progress messages
                if (line.StartsWith("[TIMESTAMP]"))
                {
                    var msg = line.Substring("[TIMESTAMP]".Length).Trim();
                    TimestampStatusLabel.Text = msg;

                    // Parse "Reading frame X/Y" for progress
                    var match = System.Text.RegularExpressions.Regex.Match(msg, @"Reading frame (\d+)/(\d+)");
                    if (match.Success)
                    {
                        var current = int.Parse(match.Groups[1].Value);
                        var total = int.Parse(match.Groups[2].Value);
                        TimestampProgress.IsIndeterminate = false;
                        TimestampProgress.Maximum = total;
                        TimestampProgress.Value = current;
                    }
                }

                // Parse the final JSON result
                if (line.StartsWith("[TIMESTAMP_RESULT]"))
                {
                    try
                    {
                        var json = line.Substring("[TIMESTAMP_RESULT]".Length).Trim();
                        var doc = System.Text.Json.JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("timestamps", out var tsArray))
                        {
                            foreach (var ts in tsArray.EnumerateArray())
                            {
                                results.Add(new TimestampResult
                                {
                                    FrameSec = ts.GetProperty("frame_sec").GetDouble().ToString("F1"),
                                    RawText = ts.GetProperty("raw_text").GetString() ?? "",
                                    Confidence = ts.GetProperty("confidence").GetString() ?? ""
                                });
                            }
                        }

                        if (root.TryGetProperty("consensus", out var c) && c.ValueKind != System.Text.Json.JsonValueKind.Null)
                            consensus = c.GetString();
                        if (root.TryGetProperty("frames_readable", out var fr))
                            framesReadable = fr.GetInt32();
                        if (root.TryGetProperty("frames_extracted", out var fe))
                            framesExtracted = fe.GetInt32();
                    }
                    catch (Exception ex)
                    {
                        TimestampStatusLabel.Text = $"Failed to parse results: {ex.Message}";
                    }
                }

                // Log ALL output to the console so errors are visible
                TimestampLogConsole.AppendText(line + "\n");
                TimestampLogConsole.ScrollToEnd();

                // Also log to main log
                AppendLog(line);
            });
        };

        _timestampRunner.OutputReceived += outputHandler;

        try
        {
            await _timestampRunner.RunExtractTimestampsAsync(_selectedTimestampVideoPath, numFrames);

            // Update UI with results
            TimestampResultsGrid.ItemsSource = results;
            TimestampConsensusLabel.Text = consensus ?? "Could not determine";
            TimestampSummaryLabel.Text = $"Readable: {framesReadable}/{framesExtracted} frames";
            TimestampProgress.IsIndeterminate = false;
            TimestampProgress.Value = TimestampProgress.Maximum;
            TimestampStatusLabel.Text = results.Count > 0 ? "Extraction complete ✓" : "No timestamps found";
        }
        catch (Exception ex)
        {
            TimestampStatusLabel.Text = $"Error: {ex.Message}";
        }
        finally
        {
            _timestampRunner.OutputReceived -= outputHandler;
            ExtractTimestampsBtn.IsEnabled = true;
            TimestampBrowseBtn.IsEnabled = true;
            CancelTimestampBtn.IsEnabled = false;
            TimestampProgress.IsIndeterminate = false;
        }
    }

    private void CancelTimestampBtn_Click(object sender, RoutedEventArgs e)
    {
        _timestampRunner.Cancel();
        TimestampStatusLabel.Text = "Cancelled";
        TimestampProgress.IsIndeterminate = false;
        TimestampProgress.Value = 0;
        ExtractTimestampsBtn.IsEnabled = true;
        TimestampBrowseBtn.IsEnabled = true;
        CancelTimestampBtn.IsEnabled = false;
    }

    // ===== BATCH VIDEO RENAME =====

    private string? _batchFolderPath;
    private List<BatchRenameItem> _batchItems = new();

    private void RefreshBatchDrives()
    {
        BatchDriveCombo.Items.Clear();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                var emoji = drive.DriveType switch
                {
                    DriveType.Fixed => "💾",
                    DriveType.Removable => "🔌",
                    DriveType.Network => "🌐",
                    _ => "📁"
                };
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "" : $" ({drive.VolumeLabel})";
                var freeGB = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
                var totalGB = drive.TotalSize / (1024.0 * 1024 * 1024);
                var item = new ComboBoxItem
                {
                    Content = $"{emoji} {drive.Name.TrimEnd('\\')}{label}  [{freeGB:F0}/{totalGB:F0} GB]",
                    Tag = drive.RootDirectory.FullName
                };
                BatchDriveCombo.Items.Add(item);
            }
            catch { }
        }
    }

    private void BatchDriveCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateBatchFolder();
    }

    private void BatchFolderPath_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateBatchFolder();
    }

    private void UpdateBatchFolder()
    {
        string? drivePath = null;
        if (BatchDriveCombo.SelectedItem is ComboBoxItem selected)
            drivePath = selected.Tag?.ToString();

        if (string.IsNullOrEmpty(drivePath))
        {
            _batchFolderPath = null;
            BatchScanBtn.IsEnabled = false;
            BatchRenameBtn.IsEnabled = false;
            BatchStatusLabel.Text = "Select a drive to begin";
            return;
        }

        var subfolder = BatchSubfolderPath.Text.Trim();
        var fullPath = string.IsNullOrEmpty(subfolder) ? drivePath : Path.Combine(drivePath, subfolder);

        if (!Directory.Exists(fullPath))
        {
            _batchFolderPath = null;
            BatchScanBtn.IsEnabled = false;
            BatchStatusLabel.Text = $"Folder not found: {fullPath}";
            return;
        }

        _batchFolderPath = fullPath;
        BatchRenameBtn.IsEnabled = false;
        BatchRenameGrid.ItemsSource = null;
        _batchItems.Clear();

        var scope = BatchRecursiveCheck.IsChecked == true ? " (including subfolders)" : "";
        BatchStatusLabel.Text = $"Ready to scan {fullPath}{scope} — click Scan & Rename";
        BatchScanBtn.IsEnabled = true;
    }

    private async void BatchScanBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_batchFolderPath)) return;

        // UI state
        BatchScanBtn.IsEnabled = false;
        BatchDriveCombo.IsEnabled = false;
        BatchRenameBtn.IsEnabled = false;
        BatchCancelBtn.IsEnabled = true;
        BatchProgress.Value = 0;
        BatchProgress.IsIndeterminate = true;
        BatchStatusLabel.Text = "Loading VLM model + scanning timestamps...";
        _batchItems.Clear();
        BatchRenameGrid.ItemsSource = null;
        TimestampLogConsole.Text = ""; // Clear log

        int totalFiles = Directory.GetFiles(_batchFolderPath, "*.mp4").Length;
        int processed = 0;

        Action<string> outputHandler = (line) =>
        {
            Dispatcher.Invoke(() =>
            {
                // Parse batch progress
                if (line.StartsWith("[BATCH]"))
                {
                    var msg = line.Substring("[BATCH]".Length).Trim();
                    BatchStatusLabel.Text = msg;

                    // Parse "Processing X/Y" for progress
                    var match = System.Text.RegularExpressions.Regex.Match(msg, @"Processing (\d+)/(\d+)");
                    if (match.Success)
                    {
                        processed = int.Parse(match.Groups[1].Value);
                        var total = int.Parse(match.Groups[2].Value);
                        BatchProgress.IsIndeterminate = false;
                        BatchProgress.Maximum = total;
                        BatchProgress.Value = processed;
                    }
                }

                // Parse per-file result
                if (line.StartsWith("[BATCH_RESULT]"))
                {
                    try
                    {
                        var json = line.Substring("[BATCH_RESULT]".Length).Trim();
                        var doc = System.Text.Json.JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        var filename = root.GetProperty("file").GetString() ?? "";
                        string? startTs = null, endTs = null;

                        if (root.TryGetProperty("start_timestamp", out var st) && st.ValueKind != System.Text.Json.JsonValueKind.Null)
                            startTs = st.GetString();
                        if (root.TryGetProperty("end_timestamp", out var et) && et.ValueKind != System.Text.Json.JsonValueKind.Null)
                            endTs = et.GetString();

                        var location = ParseLocationFromFilename(filename);
                        var newName = GenerateNewFilename(startTs, endTs, location, filename);

                        var item = new BatchRenameItem
                        {
                            OriginalName = filename,
                            NewName = newName,
                            FullPath = root.GetProperty("path").GetString() ?? "",
                            StartTimestamp = startTs,
                            EndTimestamp = endTs,
                            Status = (startTs != null && endTs != null) ? "Ready" : "⚠ Missing"
                        };

                        // Auto-rename if checked and timestamps extracted
                        if (BatchAutoRenameCheck.IsChecked == true && item.Status == "Ready")
                        {
                            try
                            {
                                var dir = Path.GetDirectoryName(item.FullPath) ?? ".";
                                var newPath = Path.Combine(dir, item.NewName);
                                if (File.Exists(newPath))
                                {
                                    item.Status = "⚠ Exists";
                                }
                                else
                                {
                                    File.Move(item.FullPath, newPath);
                                    item.FullPath = newPath;
                                    item.Status = "✅ Renamed";
                                }
                            }
                            catch (Exception renameEx)
                            {
                                item.Status = $"❌ {renameEx.Message}";
                            }
                        }

                        _batchItems.Add(item);
                        BatchRenameGrid.ItemsSource = null;
                        BatchRenameGrid.ItemsSource = _batchItems;
                    }
                    catch (Exception ex)
                    {
                        BatchStatusLabel.Text = $"Parse error: {ex.Message}";
                    }
                }

                // Log all output
                TimestampLogConsole.AppendText(line + "\n");
                TimestampLogConsole.ScrollToEnd();
                AppendLog(line);
            });
        };

        _timestampRunner.OutputReceived += outputHandler;

        try
        {
            var prefix = BatchPrefixFilter.Text.Trim();
            await _timestampRunner.RunBatchTimestampsAsync(
                _batchFolderPath,
                BatchRecursiveCheck.IsChecked == true,
                string.IsNullOrEmpty(prefix) ? null : prefix);

            BatchProgress.IsIndeterminate = false;
            BatchProgress.Value = BatchProgress.Maximum;
            var renamedCount = _batchItems.Count(i => i.Status == "✅ Renamed");
            var readyCount = _batchItems.Count(i => i.Status == "Ready");
            var failCount = _batchItems.Count(i => i.Status.StartsWith("⚠") || i.Status.StartsWith("❌"));
            if (renamedCount > 0)
                BatchStatusLabel.Text = $"Complete — {renamedCount} renamed, {readyCount} pending, {failCount} issues";
            else
                BatchStatusLabel.Text = $"Scan complete — {readyCount}/{_batchItems.Count} files ready to rename";
            BatchRenameBtn.IsEnabled = readyCount > 0;
        }
        catch (Exception ex)
        {
            BatchStatusLabel.Text = $"Error: {ex.Message}";
        }
        finally
        {
            _timestampRunner.OutputReceived -= outputHandler;
            BatchScanBtn.IsEnabled = true;
            BatchDriveCombo.IsEnabled = true;
            BatchCancelBtn.IsEnabled = false;
            BatchProgress.IsIndeterminate = false;
        }
    }

    private void BatchCancelBtn_Click(object sender, RoutedEventArgs e)
    {
        _timestampRunner.Cancel();
        BatchStatusLabel.Text = "Cancelled";
        BatchProgress.IsIndeterminate = false;
        BatchProgress.Value = 0;
        BatchScanBtn.IsEnabled = true;
        BatchDriveCombo.IsEnabled = true;
        BatchCancelBtn.IsEnabled = false;
    }

    private async void BatchRenameBtn_Click(object sender, RoutedEventArgs e)
    {
        var readyItems = _batchItems.Where(i => i.Status == "Ready").ToList();
        if (readyItems.Count == 0) return;

        var result = MessageBox.Show(
            $"Rename {readyItems.Count} file(s)?\n\nThis will rename the original video files. This action cannot be undone.",
            "Confirm Batch Rename",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        BatchRenameBtn.IsEnabled = false;
        int success = 0, fail = 0;

        foreach (var item in readyItems)
        {
            try
            {
                var dir = Path.GetDirectoryName(item.FullPath) ?? ".";
                var newPath = Path.Combine(dir, item.NewName);

                // Don't overwrite existing files
                if (File.Exists(newPath))
                {
                    item.Status = "⚠ Exists";
                    fail++;
                    continue;
                }

                File.Move(item.FullPath, newPath);
                item.FullPath = newPath;
                item.Status = "✅ Done";
                success++;
            }
            catch (Exception ex)
            {
                item.Status = $"❌ {ex.Message}";
                fail++;
            }
        }

        BatchRenameGrid.ItemsSource = null;
        BatchRenameGrid.ItemsSource = _batchItems;
        BatchStatusLabel.Text = $"Rename complete — {success} renamed, {fail} failed";
        BatchRenameBtn.IsEnabled = false;
    }

    /// <summary>
    /// Parse location from filename pattern: reo***-*-{location}-*.mp4
    /// Example: reo1102-3-fence1104-1-20260208125315-16.mp4 → fence1104
    /// </summary>
    private string ParseLocationFromFilename(string filename)
    {
        // Pattern: reo{digits}-{digit}-{LOCATION}-{rest}.mp4
        var match = System.Text.RegularExpressions.Regex.Match(
            filename, @"^reo\d+-\d+-([^-]+)-", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success)
            return match.Groups[1].Value;

        // Fallback: use filename without extension
        return Path.GetFileNameWithoutExtension(filename);
    }

    /// <summary>
    /// Generate new filename from timestamps: YYYYMMDD_HHMMSS-HHMMSS_location.mp4
    /// </summary>
    private string GenerateNewFilename(string? startTs, string? endTs, string location, string originalFilename)
    {
        if (string.IsNullOrEmpty(startTs) || string.IsNullOrEmpty(endTs))
            return originalFilename; // Can't rename without timestamps

        try
        {
            var startParsed = ParseTimestampText(startTs);
            var endParsed = ParseTimestampText(endTs);

            if (startParsed == null || endParsed == null)
                return originalFilename;

            var startStr = startParsed.Value.ToString("yyyyMMdd_HHmmss");
            var endStr = endParsed.Value.ToString("HHmmss");
            return $"{startStr}-{endStr}_{location}.mp4";
        }
        catch
        {
            return originalFilename;
        }
    }

    /// <summary>
    /// Parse VLM-extracted timestamp text into DateTime.
    /// Handles formats like "2026/01/27 11:00:00" or "01/27/2026 11:00:00 AM" etc.
    /// </summary>
    private DateTime? ParseTimestampText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Clean up common OCR artifacts
        text = text.Trim().Replace("  ", " ");

        // Try multiple common timestamp formats
        string[] formats = {
            "yyyy/MM/dd HH:mm:ss",
            "yyyy-MM-dd HH:mm:ss",
            "MM/dd/yyyy HH:mm:ss",
            "MM/dd/yyyy hh:mm:ss tt",
            "dd/MM/yyyy HH:mm:ss",
            "yyyy/M/d H:mm:ss",
            "yyyy-M-d H:mm:ss",
            "M/d/yyyy H:mm:ss",
            "yyyy/MM/dd H:mm:ss",
        };

        foreach (var fmt in formats)
        {
            if (DateTime.TryParseExact(text, fmt,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dt))
                return dt;
        }

        // Last resort: general parse
        if (DateTime.TryParse(text, out var dtGeneral))
            return dtGeneral;

        return null;
    }
}

// ===== TRANSCRIPT LINE MODEL (for clickable lines) =====

public class TranscriptLine
{
    public TimeSpan Offset { get; set; }
    public string TimeLabel { get; set; } = "";
    public string Text { get; set; } = "";
}

// ===== SEARCH RESULT MODEL =====

public class SearchResultItem
{
    public string FileName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public double Score { get; set; }
    public string ScoreDisplay => Score >= 1.0 ? "exact" : $"{Score:P0}";
    public string MatchSnippet { get; set; } = "";
}

// ===== ANALYSIS RESULT MODEL =====

public class AnalysisFileInfo
{
    public string FileName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string SummaryPreview { get; set; } = "Not analyzed yet";
    public string FullSummary { get; set; } = "";
    /// <summary>Base media file name (without model suffix) for grouping versions.</summary>
    public string MediaName { get; set; } = "";
}

// ===== TIMESTAMP RESULT MODEL =====

public class TimestampResult
{
    public string FrameSec { get; set; } = "";
    public string RawText { get; set; } = "";
    public string Confidence { get; set; } = "";
}

// ===== BATCH RENAME ITEM MODEL =====

public class BatchRenameItem
{
    public string OriginalName { get; set; } = "";
    public string NewName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string? StartTimestamp { get; set; }
    public string? EndTimestamp { get; set; }
    public string Status { get; set; } = "Pending";
}
