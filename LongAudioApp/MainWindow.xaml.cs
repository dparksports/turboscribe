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

    /// <summary>Derive the source media file from the transcript filename.</summary>
    public string? SourceMediaPath
    {
        get
        {
            // Transcript format: {base}_transcript.txt or {base}_transcript_{model}.txt
            var dir = FolderPath;
            var name = Path.GetFileNameWithoutExtension(FullPath);

            // Strip _transcript or _transcript_{model} suffix
            var idx = name.IndexOf("_transcript");
            if (idx < 0) return null;
            var baseName = name[..idx];

            // Try common media extensions
            string[] exts = [".mp4", ".mp3", ".wav", ".m4a", ".aac", ".ogg", ".flac", ".mkv", ".avi", ".mov", ".webm", ".wma", ".m4v", ".3gp", ".ts", ".mpg", ".mpeg"];
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, baseName + ext);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }

    public void ReadSize()
    {
        try { CharCount = new FileInfo(FullPath).Length; }
        catch { CharCount = 0; }
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

        _scriptDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..\\..\\..\\.."));
        if (!File.Exists(Path.Combine(_scriptDir, "fast_engine.py")))
            _scriptDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));

        var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LongAudioApp");
        Directory.CreateDirectory(appDataDir);
        _reportPath = Path.Combine(appDataDir, "voice_scan_results.json");
        
        _runner = new PythonRunner(_scriptDir);

        WireUpRunner();
        SetupTranscriptContextMenu();
        DetectGpu();
        TryLoadExistingResults();

    // Settings Init
        AppVersionLabel.Text = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";
        AnalyticsCheck.IsChecked = AnalyticsService.IsEnabled;

        // Load Settings
        LoadAppSettings();

        // Initialize UI from settings
        foreach (ComboBoxItem item in GpuRefreshCombo.Items)
        {
            if (item.Tag?.ToString() == _appSettings.GpuRefreshIntervalSeconds.ToString())
            {
                item.IsSelected = true;
                break;
            }
        }

        // Start GPU monitoring
        _gpuTimer = new System.Windows.Threading.DispatcherTimer();
        UpdateGpuTimer();
        _gpuTimer.Tick += (s, args) => DetectGpu();
        if (_appSettings.GpuRefreshIntervalSeconds > 0) _gpuTimer.Start();

        // Start Engine Zombie process check
        StartZombieCheckTimer();

        // Load Settings UI
        GpuRefreshCombo.SelectedValue = _appSettings.GpuRefreshIntervalSeconds.ToString();
        StartEngineCheck.IsChecked = _appSettings.StartEngineOnLaunch;
        SkipExistingCheck.IsChecked = _appSettings.SkipExistingFiles;
        
        // Auto-start engine if enabled
        if (_appSettings.StartEngineOnLaunch)
        {
            _ = _runner.StartServerAsync();
        }
    }

    private DispatcherTimer _gpuTimer;
    private DispatcherTimer? _engineCheckTimer; // Check for zombies/status
    private AppSettings _appSettings = new();
    private readonly string _settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_settings.json");

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
                
                await venvInstaller.InstallLibrariesAsync(log => Dispatcher.Invoke(() => 
                {
                    InstallLogBox.AppendText(log + "\n");
                    InstallLogBox.ScrollToEnd();
                }));

                // 3. Restart Engine to pick up new venv
                if (_runner != null)
                {
                    InstallLogBox.AppendText("Reloading engine...\n");
                    _runner.Dispose();
                    _runner = new PythonRunner(_scriptDir);
                    WireUpRunner();
                    if (_appSettings.StartEngineOnLaunch) _ = _runner.StartServerAsync();
                }
                
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
        AnalyticsService.IsEnabled = AnalyticsCheck.IsChecked ?? true;
        AnalyticsService.TrackEvent("analytics_opt_changed", new { enabled = AnalyticsService.IsEnabled });
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
        });

        _runner.ProgressUpdated += (current, total, filename) => Dispatcher.BeginInvoke(() =>
        {
            if (_isScanRunning)
            {
                ScanProgress.Maximum = total;
                ScanProgress.Value = current;
                ScanStatusLabel.Text = $"[{current}/{total}] {Path.GetFileName(filename)}";
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
            ScanBtn.IsEnabled = !running;
            BatchTranscribeBtn.IsEnabled = !running;
            TranscribeAllBtn.IsEnabled = !running;
            LoadResultsBtn.IsEnabled = !running;
            BrowseBtn.IsEnabled = !running;
            ViewLogBtn.IsEnabled = !running;
            ViewSilentBtn.IsEnabled = !running && _silentFiles.Count > 0;
            CancelScanBtn.IsEnabled = running;
            CancelTranscribeBtn.IsEnabled = running;

            if (!running)
            {
                if (_isScanRunning)
                {
                    _isScanRunning = false;
                    ScanStatusLabel.Text = "Scan complete";
                    StatusBar.Text = "Scan complete — loading results...";
                    TryLoadExistingResults();
                }
                else
                {
                    TranscribeStatusLabel.Text = $"Transcription complete. ({_silentCount} silent files)";
                    StatusBar.Text = $"Transcription complete. ({_silentCount} silent files)";
                    // Auto-refresh transcript list after transcription
                    RefreshTranscriptList();
                }
            }
        });
    }

    private bool _isScanRunning;

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
                ResultsGrid.ItemsSource = _report.Results;
                var voiceCount = _report.Results.Count(r => r.Error == null && r.Blocks.Count > 0);
                var blockCount = _report.Results.Where(r => r.Error == null).Sum(r => r.Blocks.Count);
                ScanStatusLabel.Text = $"Loaded: {_report.TotalFiles} files, {voiceCount} with voice";
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

        TranscriptList.ItemsSource = transcripts.OrderByDescending(t => t.CharCount).ToList();
        TranscribeStatusLabel.Text = transcripts.Count > 0
            ? $"Found {transcripts.Count} transcript files"
            : "No transcripts yet — run batch transcribe first";
    }

    private void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog()
        {
            Title = "Select media directory to scan"
        };
        if (dialog.ShowDialog() == true)
        {
            DirectoryBox.Text = dialog.FolderName;
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

    private async void ScanBtn_Click(object sender, RoutedEventArgs e)
    {
        var dir = NormalizePath(DirectoryBox.Text);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            MessageBox.Show("Please select a valid directory.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isScanRunning = true;
        // UI Updates
        ScanBtn.Content = "🛑 Stop Scan";
        ScanStatusLabel.Text = "Loading AI models (this may take a moment)...";
        StatusBar.Text = "Starting scan...";
        
        ScanProgress.IsIndeterminate = true;
        
        // Clear previous results
        _report = new ScanReport();
        ResultsGrid.ItemsSource = null;

        try
        {
            ClearLog();

            bool useVad = !(NoVadCheck.IsChecked ?? false);
            AnalyticsService.TrackEvent("scan_start", new { use_vad = useVad });
            await _runner.RunBatchScanAsync(dir, useVad, _reportPath);
        }
        catch (OperationCanceledException)
        {
            AppendLog("[INFO] Scan cancelled by user.");
            StatusBar.Text = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] Scan failed: {ex.Message}");
            MessageBox.Show($"Scan failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusBar.Text = "Scan failed.";
        }
        finally
        {
            _isScanRunning = false;
            ScanBtn.Content = "🔍 Scan for Voice";
            ScanProgress.IsIndeterminate = false;
        }
    }

    private async void BatchTranscribeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_reportPath))
        {
            MessageBox.Show("No scan results found. Run a scan first.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isScanRunning = false;
        _silentCount = 0; // Reset counter
        _silentFiles.Clear();
        ViewSilentBtn.IsEnabled = false;
        TranscribeProgress.Value = 0;
        TranscribeStatusLabel.Text = "Starting transcription...";
        StatusBar.Text = "Starting batch transcription with large-v3...";

        await _runner.RunBatchTranscribeAsync(_reportPath, skipExisting: _appSettings.SkipExistingFiles);
        AnalyticsService.TrackEvent("batch_transcribe");
    }

    private async void TranscribeAllBtn_Click(object sender, RoutedEventArgs e)
    {
        var dir = NormalizePath(DirectoryBox.Text);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            MessageBox.Show("Please set a valid directory in the Scan tab first.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isScanRunning = false;
        _silentCount = 0; // Reset counter
        _silentFiles.Clear();
        ViewSilentBtn.IsEnabled = false;
        TranscribeProgress.Value = 0;
        TranscribeStatusLabel.Text = "Starting full transcription...";
        StatusBar.Text = "Transcribing all files with large-v3 (no scan)...";

        bool useVad = !(NoVadCheck.IsChecked ?? false);
        AnalyticsService.TrackEvent("transcribe_all");
        await _runner.RunBatchTranscribeDirAsync(dir, useVad, skipExisting: _appSettings.SkipExistingFiles);
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
                    ResultsGrid.ItemsSource = _report.Results;
                    var voiceCount = _report.Results.Count(r => r.Error == null && r.Blocks.Count > 0);
                    var blockCount = _report.Results.Where(r => r.Error == null).Sum(r => r.Blocks.Count);
                    ScanStatusLabel.Text = $"Loaded: {_report.TotalFiles} files, {voiceCount} with voice";
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
                TranscriptFileLabel.Text = info.FullPath;
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
        var openFile = new MenuItem { Header = "Open File" };
        openFile.Click += (s, e) =>
        {
            if (TranscriptList.SelectedItem is TranscriptFileInfo info && File.Exists(info.FullPath))
            {
                try { Process.Start(new ProcessStartInfo(info.FullPath) { UseShellExecute = true }); }
                catch { }
            }
        };
        var openFolder = new MenuItem { Header = "Open Folder" };
        openFolder.Click += (s, e) =>
        {
            if (TranscriptList.SelectedItem is TranscriptFileInfo info && Directory.Exists(info.FolderPath))
            {
                Process.Start("explorer.exe", $"/select,\"{info.FullPath}\"");
            }
        };
        ctx.Items.Add(openFile);
        ctx.Items.Add(openFolder);
        TranscriptList.ContextMenu = ctx;
    }
}

