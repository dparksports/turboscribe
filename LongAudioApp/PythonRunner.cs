using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace LongAudioApp;

public class PythonRunner : IDisposable
{
    private const string VENV_PATH = @"C:\Users\k2\venvs\longaudio";
    private const string SCRIPT_NAME = "fast_engine.py";

    private readonly string _scriptPath;
    private readonly string _pythonPath;
    private readonly bool _useBundled;
    private Process? _currentProcess;
    private CancellationTokenSource? _cts;

    public event Action<string>? OutputReceived;
    public event Action<int, int, string>? ProgressUpdated; // current, total, filename
    public event Action<string>? VoiceDetected; // message
    public event Action<string>? ErrorOccurred;
    public event Action<bool>? RunningChanged;

    public bool IsRunning { get; private set; }

    public string TranscriptDirectory { get; private set; }

    public PythonRunner(string scriptDirectory)
    {
        TranscriptDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LongAudioApp", "Transcripts");
        Directory.CreateDirectory(TranscriptDirectory);

        var bundledPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fast_engine", "fast_engine.exe");
        if (File.Exists(bundledPath))
        {
            _useBundled = true;
            _pythonPath = bundledPath;
            _scriptPath = "";
        }
        else
        {
            _useBundled = false;
            _scriptPath = Path.Combine(scriptDirectory, SCRIPT_NAME);
            _pythonPath = Path.Combine(VENV_PATH, "Scripts", "python.exe");
        }
    }

    private string BuildArgs(string command, string args = "")
    {
        if (_useBundled)
        {
            return $"{command} {args}".Trim();
        }
        else
        {
            return $"\"{_scriptPath}\" {command} {args}".Trim();
        }
    }

    public async Task RunBatchScanAsync(string directory, bool useVad, string? reportPath = null)
    {
        // Strip trailing backslash to prevent it escaping the closing quote on the command line
        var safeDir = directory.TrimEnd('\\', '/');
        if (safeDir.Length == 2 && safeDir[1] == ':') safeDir += "\\"; 
        if (safeDir.EndsWith("\\")) safeDir = safeDir.TrimEnd('\\');
        
        var cmdArgs = $"--dir \"{safeDir}\"";
        if (reportPath != null) cmdArgs += $" --report \"{reportPath}\"";
        if (!useVad) cmdArgs += " --no-vad";
        
        await RunProcessAsync(BuildArgs("batch_scan", cmdArgs));
    }

    public async Task RunBatchTranscribeAsync(string? reportPath = null)
    {
        var cmdArgs = $"--output-dir \"{TranscriptDirectory}\"";
        if (reportPath != null) cmdArgs += $" --report \"{reportPath}\"";
        await RunProcessAsync(BuildArgs("batch_transcribe", cmdArgs));
    }

    public async Task RunBatchTranscribeDirAsync(string directory, bool useVad)
    {
        var safeDir = directory.TrimEnd('\\', '/');
        if (safeDir.EndsWith("\\")) safeDir = safeDir.TrimEnd('\\');
        
        var cmdArgs = $"--dir \"{safeDir}\" --output-dir \"{TranscriptDirectory}\"";
        if (!useVad) cmdArgs += " --no-vad";
        await RunProcessAsync(BuildArgs("batch_transcribe_dir", cmdArgs));
    }

    public async Task RunTranscribeAsync(string file, double start, double end)
    {
        var cmdArgs = $"\"{file}\" --start {start} --end {end} --output-dir \"{TranscriptDirectory}\"";
        await RunProcessAsync(BuildArgs("transcribe", cmdArgs));
    }

    public async Task RunTranscribeFileAsync(string file, string model, bool useVad)
    {
        var cmdArgs = $"\"{file}\" --model {model} --output-dir \"{TranscriptDirectory}\"";
        if (!useVad) cmdArgs += " --no-vad";
        await RunProcessAsync(BuildArgs("transcribe_file", cmdArgs));
    }

    public async Task RunSearchTranscriptsAsync(string directory, string query)
    {
        var safeDir = directory.TrimEnd('\\', '/');
        var cmdArgs = $"--dir \"{safeDir}\" --query \"{query}\"";
        await RunProcessAsync(BuildArgs("search_transcripts", cmdArgs));
    }

    public void Cancel()
    {
        _cts?.Cancel();
        try
        {
            if (_currentProcess != null && !_currentProcess.HasExited)
            {
                _currentProcess.Kill(entireProcessTree: true);
            }
        }
        catch { }
    }

    private async Task RunProcessAsync(string arguments)
    {
        if (IsRunning) throw new InvalidOperationException("A process is already running.");

        _cts = new CancellationTokenSource();
        IsRunning = true;
        RunningChanged?.Invoke(true);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _pythonPath,
                Arguments = arguments,
                WorkingDirectory = _useBundled ? Path.GetDirectoryName(_pythonPath) : Path.GetDirectoryName(_scriptPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            // Add venv to PATH and force unbuffered output ONLY if not using bundled exe
            if (!_useBundled)
            {
                var envPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                psi.EnvironmentVariables["PATH"] = Path.Combine(VENV_PATH, "Scripts") + ";" + envPath;
                psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            }
            else
            {
                // For bundled exe, we might not need to set PATH, but PYTHONUNBUFFERED is still good
                psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            }

            _currentProcess = new Process { StartInfo = psi };
            _currentProcess.Start();

            var stdoutTask = ReadOutputAsync(_currentProcess.StandardOutput, _cts.Token);
            var stderrTask = ReadOutputAsync(_currentProcess.StandardError, _cts.Token, isError: true);

            await Task.WhenAll(stdoutTask, stderrTask);
            await _currentProcess.WaitForExitAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            OutputReceived?.Invoke("[CANCELLED] Operation was cancelled.");
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(ex.Message);
        }
        finally
        {
            IsRunning = false;
            RunningChanged?.Invoke(false);
            _currentProcess?.Dispose();
            _currentProcess = null;
        }
    }

    private static readonly Regex ProgressRegex = new(@"\[(\d+)/(\d+)\]\s+Scanning:\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex TranscribeProgressRegex = new(@"\[(\d+)/(\d+)\]\s+(.+)$", RegexOptions.Compiled);

    private async Task ReadOutputAsync(StreamReader reader, CancellationToken ct, bool isError = false)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;

            OutputReceived?.Invoke(line);

            if (isError) continue;

            // Parse progress: [1/216] Scanning: path
            var match = ProgressRegex.Match(line);
            if (match.Success)
            {
                var current = int.Parse(match.Groups[1].Value);
                var total = int.Parse(match.Groups[2].Value);
                var filename = match.Groups[3].Value.Trim();
                ProgressUpdated?.Invoke(current, total, filename);
                continue;
            }

            // Parse transcribe progress: [1/112] path
            match = TranscribeProgressRegex.Match(line);
            if (match.Success)
            {
                var current = int.Parse(match.Groups[1].Value);
                var total = int.Parse(match.Groups[2].Value);
                var filename = match.Groups[3].Value.Trim();
                ProgressUpdated?.Invoke(current, total, filename);
                continue;
            }

            // Parse voice detection
            if (line.Contains("[VOICE]"))
            {
                VoiceDetected?.Invoke(line);
            }

            if (line.Contains("[ERROR]"))
            {
                ErrorOccurred?.Invoke(line);
            }
        }
    }

    public void Dispose()
    {
        Cancel();
        _cts?.Dispose();
        _currentProcess?.Dispose();
    }
}
