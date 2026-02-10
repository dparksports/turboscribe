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

public partial class MainWindow : Window
{
    private PythonRunner _runner;
    private ScanReport? _report;
    private readonly string _scriptDir;
    private readonly string _reportPath;
    private string? _selectedTranscriptPath;

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _runner?.Dispose();
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
        
        foreach (ComboBoxItem item in DeviceCombo.Items)
        {
            if (item.Tag?.ToString() == _appSettings.DevicePreference)
            {
                item.IsSelected = true;
                break;
            }
        }
        _runner.DevicePreference = _appSettings.DevicePreference;

        // Populate drive list
        RefreshDrives();

        // Defer heavy I/O and subprocess work until after window renders
        ContentRendered += OnContentRendered;
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered; // One-shot

    // Set the media directory so transcripts can find their source media files
    TranscriptFileInfo.MediaDirectory = DirectoryBox.Text;

        // GPU detection (spawns nvidia-smi subprocess)
        DetectGpu();

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
        public bool EnglishOnly { get; set; } = false;
        public bool NoVadEnabled { get; set; } = true;
        public string LastDirectory { get; set; } = "";
        public List<string> SelectedDrives { get; set; } = new();
        public List<string> CustomFolders { get; set; } = new();
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

            // When a transcript file is saved, add it to the list immediately
            if (line.Contains("[SAVED]"))
            {
                var path = line.Replace("[SAVED]", "").Trim();
                if (File.Exists(path))
                {
                    var current = TranscriptList.ItemsSource as List<TranscriptFileInfo> ?? new List<TranscriptFileInfo>();
                    // If it already exists (re-transcribe), update its size
                    var existing = current.FirstOrDefault(t => t.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        existing.ReadSize();
                        TranscriptList.ItemsSource = current.OrderByDescending(t => t.CharCount).ToList();
                        // Auto-select the updated transcript
                        TranscriptList.SelectedItem = TranscriptList.Items.Cast<TranscriptFileInfo>()
                            .FirstOrDefault(t => t.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        var info = new TranscriptFileInfo { FullPath = path };
                        info.ReadSize();
                        current.Add(info);
                        var sorted = current.OrderByDescending(t => t.CharCount).ToList();
                        TranscriptList.ItemsSource = sorted;
                        TranscribeStatusLabel.Text = $"Found {sorted.Count} transcript files (Silent: {_silentCount})";
                        // Auto-select the new transcript
                        TranscriptList.SelectedItem = sorted.FirstOrDefault(t => t.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase));
                    }
                }
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
            BrowseBtn.IsEnabled = !running;
            ViewSilentBtn.IsEnabled = !running && _silentFiles.Count > 0;
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
                    RefreshTranscriptList();
                }
            }
        });
    }

    private bool _isScanRunning;
    private List<TranscriptFileInfo> _allTranscripts = new();
    private volatile bool _batchCancelled;

    private bool _isCheckingGpu = false;

    private async void DetectGpu()
    {
        if (_isCheckingGpu) return;
        _isCheckingGpu = true;

        try
        {
            // Query for name, utilization, and memory usage
            // output format: "NVIDIA GeForce RTX 4090, 15 %, 300 MiB / 24564 MiB"
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=name,utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            var proc = Process.Start(psi);
            if (proc != null)
            {
                // Reading all lines in case of multiple GPUs, taking the first one for now
                var output = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();

                if (string.IsNullOrWhiteSpace(output))
                {
                    GpuLabel.Text = "GPU: Not available (using CPU)";
                    GpuLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94A3B8")); // Dim
                    return;
                }

                // Parse first line: "Name, Util, MemUsed, MemTotal"
                var parts = output.Split('\n')[0].Split(',');
                if (parts.Length >= 4)
                {
                    var name = parts[0].Trim();
                    var util = parts[1].Trim();
                    var memUsed = parts[2].Trim();
                    var memTotal = parts[3].Trim();

                    GpuLabel.Text = $"GPU: {name} | Load: {util}% | VRAM: {memUsed}/{memTotal} MiB";
                    GpuLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#22C55E")); // Green
                }
                else
                {
                    GpuLabel.Text = $"GPU: {parts[0].Trim()}";
                }
            }
        }
        catch
        {
            GpuLabel.Text = "GPU: Not detected (using CPU)";
            GpuLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94A3B8"));
        }
        finally
        {
            _isCheckingGpu = false;
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
                // Also refresh transcripts
                RefreshTranscriptList();
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] Failed to load results: {ex.Message}");
        }
    }

    private void RefreshTranscriptList()
    {
        var transcripts = new List<TranscriptFileInfo>();

        // 1. Search AppData Transcripts directory (primary location for new transcripts)
        var appDataTranscripts = _runner.TranscriptDirectory;
        if (Directory.Exists(appDataTranscripts))
        {
            try
            {
                var found = Directory.GetFiles(appDataTranscripts, "*_transcript*.txt", SearchOption.TopDirectoryOnly);
                foreach (var f in found)
                {
                    var ti = new TranscriptFileInfo { FullPath = f };
                    ti.ReadSize();
                    transcripts.Add(ti);
                }
            }
            catch { }
        }

        // 2. Look for legacy _transcript.txt files next to each scanned media file
        if (_report != null)
        {
            foreach (var result in _report.Results)
            {
                if (result.Error != null || result.Blocks.Count == 0) continue;

                var basePath = Path.ChangeExtension(result.File, null) + "_transcript.txt";
                if (File.Exists(basePath) && !transcripts.Any(t => t.FullPath.Equals(basePath, StringComparison.OrdinalIgnoreCase)))
                {
                    var ti = new TranscriptFileInfo { FullPath = basePath };
                    ti.ReadSize();
                    transcripts.Add(ti);
                }
            }
        }

        // 3. Also scan the media directory for any _transcript*.txt files (including versioned)
        var dir = NormalizePath(DirectoryBox.Text);
        if (Directory.Exists(dir))
        {
            try
            {
                var found = Directory.GetFiles(dir, "*_transcript*.txt", SearchOption.AllDirectories);
                foreach (var f in found)
                {
                    if (!transcripts.Any(t => t.FullPath.Equals(f, StringComparison.OrdinalIgnoreCase)))
                    {
                        var ti = new TranscriptFileInfo { FullPath = f };
                        ti.ReadSize();
                        transcripts.Add(ti);
                    }
                }
            }
            catch { /* Permission errors on some dirs */ }
        }

        _allTranscripts = transcripts;
        ApplyTranscriptView();

        TranscribeStatusLabel.Text = transcripts.Count > 0
            ? $"Found {transcripts.Count} transcript files"
            : "No transcripts yet — run batch transcribe first";
    }

    private void ApplyTranscriptView()
    {
        if (TranscriptList == null || AnalysisTranscriptList == null) return;
        var sortTag = (TranscriptSortCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "date";
        var filter = TranscriptFilterBox?.Text?.Trim() ?? "";

        IEnumerable<TranscriptFileInfo> filtered = _allTranscripts;
        if (!string.IsNullOrEmpty(filter))
            filtered = filtered.Where(t => t.FileName.Contains(filter, StringComparison.OrdinalIgnoreCase));

        var sorted = sortTag switch
        {
            "size" => filtered.OrderByDescending(t => t.CharCount),
            "name" => filtered.OrderBy(t => t.FileName, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderByDescending(t => t.LastModified) // "date"
        };

        var list = sorted.ToList();
        TranscriptList.ItemsSource = list;

        // Sync Analysis tab with same sort/filter
        var aSortTag = (AnalysisSortCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? sortTag;
        var aFilter = AnalysisFilterBox?.Text?.Trim() ?? "";

        IEnumerable<TranscriptFileInfo> aFiltered = _allTranscripts;
        if (!string.IsNullOrEmpty(aFilter))
            aFiltered = aFiltered.Where(t => t.FileName.Contains(aFilter, StringComparison.OrdinalIgnoreCase));

        var aSorted = aSortTag switch
        {
            "size" => aFiltered.OrderByDescending(t => t.CharCount),
            "name" => aFiltered.OrderBy(t => t.FileName, StringComparer.OrdinalIgnoreCase),
            _ => aFiltered.OrderByDescending(t => t.LastModified)
        };

        AnalysisTranscriptList.ItemsSource = aSorted.Select(t => new AnalysisFileInfo
        {
            FileName = t.FileName,
            FullPath = t.FullPath,
        }).ToList();
    }

    private void TranscriptSortCombo_Changed(object sender, SelectionChangedEventArgs e) => ApplyTranscriptView();
    private void TranscriptFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyTranscriptView();
        if (FilterPlaceholder != null)
            FilterPlaceholder.Visibility = string.IsNullOrEmpty(TranscriptFilterBox.Text) 
                ? Visibility.Visible : Visibility.Collapsed;
    }
    private void AnalysisSortCombo_Changed(object sender, SelectionChangedEventArgs e) => ApplyTranscriptView();
    private void AnalysisFilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyTranscriptView();

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
        path = path.Trim();
        // "C:" or "D:" without trailing separator refers to CWD on that drive, not the root
        if (path.Length == 2 && char.IsLetter(path[0]) && path[1] == ':')
            path += Path.DirectorySeparatorChar;
        return path;
    }

    // ScanBtn_Click removed — Scan tab has been removed

    // BatchTranscribeBtn_Click removed — Voice Only button has been removed

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
        ViewSilentBtn.IsEnabled = false;
        TranscribeProgress.Value = 0;
        bool useVad = !(NoVadCheck.IsChecked ?? false);
        _appSettings.NoVadEnabled = NoVadCheck.IsChecked ?? true;
        SaveAppSettings();
        AnalyticsService.TrackEvent("transcribe_all", new { drive_count = dirs.Count });

        TranscribeAllBtn.IsEnabled = false;
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

                await _runner.RunBatchTranscribeDirAsync(dir, useVad, skipExisting: _appSettings.SkipExistingFiles);
            }
            TranscribeStatusLabel.Text = $"Done — transcribed {dirs.Count} location(s)";
            StatusBar.Text = "Transcription complete";
        }
        catch (Exception ex)
        {
            TranscribeStatusLabel.Text = $"Error: {ex.Message}";
        }
        finally
        {
            TranscribeAllBtn.IsEnabled = true;
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
                    RefreshTranscriptList();
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
        RefreshTranscriptList();
    }

    private void TranscriptList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TranscriptList.SelectedItem is TranscriptFileInfo info)
        {
            _selectedTranscriptPath = info.FullPath;
            try
            {
                var content = File.ReadAllText(info.FullPath);
                SetContentBoxText(content);
                var mediaPath = info.SourceMediaPath;
                TranscriptFileLabel.Text = mediaPath != null 
                    ? $"📂 {mediaPath}" 
                    : info.FullPath;
                OpenMediaBtn.Visibility = mediaPath != null ? Visibility.Visible : Visibility.Collapsed;
                OpenInExplorerBtn.Visibility = Visibility.Visible;

                // Always show re-transcribe panel
                RetranscribePanel.Visibility = Visibility.Visible;

                // Show version count on Compare button
                var versions = FindSiblingVersions(info);
                CompareBtn.Content = versions.Count > 1 
                    ? $"📊 Compare ({versions.Count} versions)" 
                    : "📊 Compare Versions";
                CompareBtn.IsEnabled = versions.Count > 1;

                StatusBar.Text = $"Viewing: {info.FileName}" + 
                    (versions.Count > 1 ? $" ({versions.Count} versions available)" : "");
            }
            catch (Exception ex)
            {
                SetContentBoxText($"Error reading file: {ex.Message}");
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
        if (TranscriptList.SelectedItem is not TranscriptFileInfo info) return;
        var mediaPath = info.SourceMediaPath;
        if (mediaPath == null)
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
        if (string.IsNullOrEmpty(query)) { RefreshTranscriptList(); return; }

        var dir = NormalizePath(DirectoryBox.Text);
        if (!Directory.Exists(dir)) return;

        // Local in-process search — fast, no Python needed
        var queryWords = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var allTranscripts = TranscriptList.ItemsSource as List<TranscriptFileInfo> ?? new List<TranscriptFileInfo>();

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
        TranscriptList.ItemsSource = results.Select(r => r.info).ToList();
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
        if (TranscriptList.SelectedItem is not TranscriptFileInfo info) return;

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

        // Build a color-coded diff in the RichTextBox
        var doc = new FlowDocument { PageWidth = 2000 };
        doc.Blocks.Add(MakeParagraph(
            $"📊 Comparing {versionData.Count} versions for: {Path.GetFileName(info.SourceMediaPath ?? info.FileName)}\n",
            "#A78BFA", true));

        // Side by side: compare first version vs each other version
        var baseline = versionData[0];
        for (int v = 1; v < versionData.Count; v++)
        {
            var compare = versionData[v];
            doc.Blocks.Add(MakeParagraph(
                $"\n═══ {baseline.label} vs {compare.label} ═══\n", "#F59E0B", true));

            var maxLines = Math.Max(baseline.lines.Length, compare.lines.Length);
            for (int i = 0; i < maxLines; i++)
            {
                var lineA = i < baseline.lines.Length ? baseline.lines[i].Trim() : "";
                var lineB = i < compare.lines.Length ? compare.lines[i].Trim() : "";

                if (lineA == lineB)
                {
                    // Same — dim
                    doc.Blocks.Add(MakeParagraph($"  {lineA}", "#94A3B8", false));
                }
                else
                {
                    // Different — highlight
                    if (!string.IsNullOrWhiteSpace(lineA))
                        doc.Blocks.Add(MakeParagraph($"- [{baseline.label}] {lineA}", "#EF4444", false));
                    if (!string.IsNullOrWhiteSpace(lineB))
                        doc.Blocks.Add(MakeParagraph($"+ [{compare.label}] {lineB}", "#22C55E", false));
                }
            }
        }

        TranscriptContentBox.Document = doc;
        StatusBar.Text = $"Comparing {versionData.Count} transcript versions";
    }

    // ===== HELPERS =====

    private void SetContentBoxText(string text)
    {
        var doc = new FlowDocument { PageWidth = 2000 };
        var para = new Paragraph(new Run(text))
        {
            Foreground = (SolidColorBrush)FindResource("TextBrush"),
            FontFamily = new FontFamily("Cascadia Mono,Consolas,Courier New"),
            FontSize = 12
        };
        doc.Blocks.Add(para);
        TranscriptContentBox.Document = doc;
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
            if (TranscriptList.SelectedItem is TranscriptFileInfo info && File.Exists(info.FullPath))
            {
                try { Process.Start(new ProcessStartInfo(info.FullPath) { UseShellExecute = true }); }
                catch { }
            }
        };

        var openInPlayer = new MenuItem { Header = "▶ Open Media File in Player" };
        openInPlayer.Click += (s, e) =>
        {
            if (TranscriptList.SelectedItem is TranscriptFileInfo info)
            {
                var mediaPath = info.SourceMediaPath;
                if (mediaPath != null && File.Exists(mediaPath))
                {
                    try { Process.Start(new ProcessStartInfo(mediaPath) { UseShellExecute = true }); }
                    catch { }
                }
                else
                {
                    MessageBox.Show("Could not find the source media file.", "Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        };

        var revealInExplorer = new MenuItem { Header = "📂 Reveal Media File in Explorer" };
        revealInExplorer.Click += (s, e) =>
        {
            if (TranscriptList.SelectedItem is TranscriptFileInfo info)
            {
                var mediaPath = info.SourceMediaPath;
                if (mediaPath != null && File.Exists(mediaPath))
                {
                    Process.Start("explorer.exe", $"/select,\"{mediaPath}\"");
                }
                else if (Directory.Exists(info.FolderPath))
                {
                    Process.Start("explorer.exe", $"/select,\"{info.FullPath}\"");
                }
            }
        };

        var summarize = new MenuItem { Header = "📝 Summarize" };
        summarize.Click += async (s, e) =>
        {
            if (TranscriptList.SelectedItem is TranscriptFileInfo info && File.Exists(info.FullPath))
                await RunContextMenuAnalysis(info.FullPath, "summarize");
        };

        var outline = new MenuItem { Header = "📋 Outline" };
        outline.Click += async (s, e) =>
        {
            if (TranscriptList.SelectedItem is TranscriptFileInfo info && File.Exists(info.FullPath))
                await RunContextMenuAnalysis(info.FullPath, "outline");
        };

        var deleteTranscript = new MenuItem { Header = "🗑️ Delete Transcript" };
        deleteTranscript.Click += (s, e) =>
        {
            if (TranscriptList.SelectedItem is TranscriptFileInfo info && File.Exists(info.FullPath))
            {
                var result = MessageBox.Show(
                    $"Delete \"{info.FileName}\"?\n\nThis cannot be undone.",
                    "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        File.Delete(info.FullPath);
                        RefreshTranscriptList();
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
        TranscriptList.ContextMenu = ctx;
    }

    /// <summary>Run analysis from context menu — uses Analysis tab settings, switches to Analysis tab to show results.</summary>
    private async Task RunContextMenuAnalysis(string transcriptPath, string analyzeType)
    {
        // Switch to Analysis tab to show output
        MainTabControl.SelectedIndex = 1; // Analysis tab is index 1

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
        SummarizeAllBtn.IsEnabled = false;
        OutlineAllBtn.IsEnabled = false;
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
            SummarizeAllBtn.IsEnabled = true;
            OutlineAllBtn.IsEnabled = true;
            CancelAnalysisBtn.IsEnabled = false;
        }
    }

    // ===== OPEN MEDIA FILE =====

    private void OpenMediaBtn_Click(object sender, RoutedEventArgs e)
    {
        if (TranscriptList.SelectedItem is TranscriptFileInfo info)
        {
            var mediaPath = info.SourceMediaPath;
            if (mediaPath != null && File.Exists(mediaPath))
            {
                Process.Start("explorer.exe", $"/select,\"{mediaPath}\"");
            }
            else
            {
                MessageBox.Show("Could not find the source media file.", "Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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

    private async void SummarizeAllBtn_Click(object sender, RoutedEventArgs e)
    {
        await RunAnalysisOnSelected("summarize");
    }

    private async void OutlineAllBtn_Click(object sender, RoutedEventArgs e)
    {
        await RunAnalysisOnSelected("outline");
    }

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

        SummarizeAllBtn.IsEnabled = false;
        OutlineAllBtn.IsEnabled = false;
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
            AnalysisProgress.Value = (double)(i) / total * 100;

            if (_batchCancelled) break;

            // Check if output already exists
            var outputPath = Path.ChangeExtension(t.FullPath, null) + $"_{analyzeType}.txt";
            bool shouldSkip = analyzeType == "summarize"
                ? SkipExistingSummaryCheck.IsChecked == true
                : SkipExistingOutlineCheck.IsChecked == true;
            if (shouldSkip && File.Exists(outputPath))
            {
                skipped++;
                AnalysisStatusLabel.Text = $"[{i + 1}/{total}] Skipped (exists): {t.FileName}";
                continue;
            }

            // Select the file in Analysis list to show progress
            var items = AnalysisTranscriptList.ItemsSource as IEnumerable<AnalysisFileInfo>;
            var match = items?.FirstOrDefault(a => a.FullPath.Equals(t.FullPath, StringComparison.OrdinalIgnoreCase));
            if (match != null) AnalysisTranscriptList.SelectedItem = match;

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

        AnalysisProgress.Value = 100;
        var skipMsg = skipped > 0 ? $", {skipped} skipped" : "";
        AnalysisStatusLabel.Text = $"Batch {analyzeType} complete ({total} files{skipMsg})";
        SummarizeAllBtn.IsEnabled = true;
        OutlineAllBtn.IsEnabled = true;
        SummarizeBatchBtn.IsEnabled = true;
        OutlineBatchBtn.IsEnabled = true;
        CancelAnalysisBtn.IsEnabled = false;
    }

    private async Task RunAnalysisOnSelected(string analyzeType)
    {
        if (AnalysisTranscriptList.SelectedItem is not AnalysisFileInfo info)
        {
            AnalysisStatusLabel.Text = "Select a transcript first";
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
        SummarizeAllBtn.IsEnabled = false;
        OutlineAllBtn.IsEnabled = false;
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
            SummarizeAllBtn.IsEnabled = true;
            OutlineAllBtn.IsEnabled = true;
            CancelAnalysisBtn.IsEnabled = false;
        }
    }

    private void CancelAnalysisBtn_Click(object sender, RoutedEventArgs e)
    {
        _batchCancelled = true;
        _runner.Cancel();
        AnalysisStatusLabel.Text = "Cancelled";
    }

    private void AnalysisTranscriptList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AnalysisTranscriptList.SelectedItem is AnalysisFileInfo info)
        {
            AnalysisStatusLabel.Text = $"Selected: {info.FileName}";
        }
    }

    // ===== TRANSCRIPT CONTEXT MENU =====

    private async void ContextSummarize_Click(object sender, RoutedEventArgs e)
    {
        if (TranscriptList.SelectedItem is TranscriptFileInfo info)
            await RunAnalysisOnTranscript(info.FullPath, "summarize");
    }

    private async void ContextOutline_Click(object sender, RoutedEventArgs e)
    {
        if (TranscriptList.SelectedItem is TranscriptFileInfo info)
            await RunAnalysisOnTranscript(info.FullPath, "outline");
    }

    private void ContextOpenAnalysis_Click(object sender, RoutedEventArgs e)
    {
        if (TranscriptList.SelectedItem is not TranscriptFileInfo info) return;

        // Switch to Analysis tab (index 4: Transcribe=0, Settings=1, Log=2, Search=3, Analysis=4)
        MainTabControl.SelectedIndex = 4;

        // Select matching file in AnalysisTranscriptList
        var items = AnalysisTranscriptList.ItemsSource as IEnumerable<AnalysisFileInfo>;
        if (items != null)
        {
            var match = items.FirstOrDefault(a => a.FullPath.Equals(info.FullPath, StringComparison.OrdinalIgnoreCase));
            if (match != null) AnalysisTranscriptList.SelectedItem = match;
        }
    }

    private void ContextOpenMediaPlayer_Click(object sender, RoutedEventArgs e)
    {
        if (TranscriptList.SelectedItem is not TranscriptFileInfo info) return;
        var mediaPath = info.SourceMediaPath;
        if (mediaPath != null && File.Exists(mediaPath))
        {
            Process.Start(new ProcessStartInfo(mediaPath) { UseShellExecute = true });
        }
        else
        {
            MessageBox.Show("Source media file not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ContextRevealExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (TranscriptList.SelectedItem is not TranscriptFileInfo info) return;
        if (File.Exists(info.FullPath))
        {
            Process.Start("explorer.exe", $"/select,\"{info.FullPath}\"");
        }
    }

    private async void AnalysisContextSummarize_Click(object sender, RoutedEventArgs e)
    {
        if (AnalysisTranscriptList.SelectedItem is AnalysisFileInfo info)
            await RunAnalysisOnTranscript(info.FullPath, "summarize");
    }

    private async void AnalysisContextOutline_Click(object sender, RoutedEventArgs e)
    {
        if (AnalysisTranscriptList.SelectedItem is AnalysisFileInfo info)
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

        // Switch to Analysis tab to show progress
        MainTabControl.SelectedIndex = 4;

        // Select the file in the Analysis transcript list
        var items = AnalysisTranscriptList.ItemsSource as IEnumerable<AnalysisFileInfo>;
        if (items != null)
        {
            var match = items.FirstOrDefault(a => a.FullPath.Equals(transcriptPath, StringComparison.OrdinalIgnoreCase));
            if (match != null) AnalysisTranscriptList.SelectedItem = match;
        }

        AnalysisStatusLabel.Text = $"Running {analyzeType} with {provider}...";
        AnalysisOutputBox.Text = "Processing...";
        SummarizeAllBtn.IsEnabled = false;
        OutlineAllBtn.IsEnabled = false;
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
            SummarizeAllBtn.IsEnabled = true;
            OutlineAllBtn.IsEnabled = true;
            CancelAnalysisBtn.IsEnabled = false;
        }
    }
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
}

