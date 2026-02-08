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
            string[] exts = [".mp4", ".mp3", ".wav", ".m4a", ".aac", ".ogg", ".flac", ".mkv", ".avi", ".mov", ".webm", ".wma"];
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

    public MainWindow()
    {
        InitializeComponent();

        _scriptDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..\\..\\..\\.."));
        if (!File.Exists(Path.Combine(_scriptDir, "fast_engine.py")))
            _scriptDir = AppDomain.CurrentDomain.BaseDirectory;
        if (!File.Exists(Path.Combine(_scriptDir, "fast_engine.py")))
            _scriptDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));

        _reportPath = Path.Combine(_scriptDir, "voice_scan_results.json");
        _runner = new PythonRunner(_scriptDir);

        WireUpRunner();
        DetectGpu();
        TryLoadExistingResults();
    }

    private void WireUpRunner()
    {
        _runner.OutputReceived += line => Dispatcher.BeginInvoke(() =>
        {
            AppendLog(line);

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
                        TranscribeStatusLabel.Text = $"Found {sorted.Count} transcript files";
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
                TranscribeStatusLabel.Text = $"[{current}/{total}] {Path.GetFileName(filename)}";
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
                    TranscribeStatusLabel.Text = "Transcription complete";
                    StatusBar.Text = "Transcription complete";
                    // Auto-refresh transcript list after transcription
                    RefreshTranscriptList();
                }
            }
        });
    }

    private bool _isScanRunning;

    private async void DetectGpu()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=name --format=csv,noheader")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            var proc = Process.Start(psi);
            if (proc != null)
            {
                var gpu = await proc.StandardOutput.ReadLineAsync();
                await proc.WaitForExitAsync();
                GpuLabel.Text = $"GPU: {gpu?.Trim() ?? "Unknown"}";
            }
        }
        catch
        {
            GpuLabel.Text = "GPU: Not detected";
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

        if (_report != null)
        {
            // Look for _transcript.txt files next to each scanned media file
            foreach (var result in _report.Results)
            {
                if (result.Error != null || result.Blocks.Count == 0) continue;

                var basePath = Path.ChangeExtension(result.File, null) + "_transcript.txt";
                if (File.Exists(basePath))
                {
                    var ti = new TranscriptFileInfo { FullPath = basePath };
                    ti.ReadSize();
                    transcripts.Add(ti);
                }
            }
        }

        // Also scan the directory for any _transcript*.txt files (including versioned)
        var dir = DirectoryBox.Text.Trim();
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

    private async void ScanBtn_Click(object sender, RoutedEventArgs e)
    {
        var dir = DirectoryBox.Text.Trim();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            MessageBox.Show("Please select a valid directory.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isScanRunning = true;
        ScanProgress.Value = 0;
        ScanStatusLabel.Text = "Starting scan...";
        StatusBar.Text = "Starting voice scan...";
        ClearLog();

        bool useVad = !(NoVadCheck.IsChecked ?? false);
        AnalyticsService.TrackEvent("scan_start", new { use_vad = useVad });
        await _runner.RunBatchScanAsync(dir, useVad);
    }

    private async void BatchTranscribeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_reportPath))
        {
            MessageBox.Show("No scan results found. Run a scan first.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isScanRunning = false;
        TranscribeProgress.Value = 0;
        TranscribeStatusLabel.Text = "Starting transcription...";
        StatusBar.Text = "Starting batch transcription with large-v3...";

        await _runner.RunBatchTranscribeAsync(_reportPath);
        AnalyticsService.TrackEvent("batch_transcribe");
    }

    private async void TranscribeAllBtn_Click(object sender, RoutedEventArgs e)
    {
        var dir = DirectoryBox.Text.Trim();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            MessageBox.Show("Please set a valid directory in the Scan tab first.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isScanRunning = false;
        TranscribeProgress.Value = 0;
        TranscribeStatusLabel.Text = "Starting full transcription...";
        StatusBar.Text = "Transcribing all files with large-v3 (no scan)...";

        bool useVad = !(NoVadCheck.IsChecked ?? false);
        AnalyticsService.TrackEvent("transcribe_all");
        await _runner.RunBatchTranscribeDirAsync(dir, useVad);
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
        await _runner.RunTranscribeFileAsync(mediaPath, model, useVad);
    }

    private void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        var query = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(query)) { RefreshTranscriptList(); return; }

        var dir = DirectoryBox.Text.Trim();
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
}

