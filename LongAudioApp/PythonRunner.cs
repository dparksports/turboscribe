using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace LongAudioApp;

public class PythonRunner : IDisposable
{

    private const string SCRIPT_NAME = "fast_engine.py";

    private readonly string _scriptPath;
    private readonly string _pythonPath;
    private Process? _currentProcess;
    private Process? _serverProcess; // Persistent engine process
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _serverLock = new(1, 1);

    public event Action<string>? OutputReceived;
    public event Action<int, int, string>? ProgressUpdated; // current, total, filename
    public event Action<string>? VoiceDetected; // message
    public event Action<string>? ErrorOccurred;
    public event Action<bool>? RunningChanged;

    public bool IsRunning { get; private set; }
    public bool IsServerRunning => _serverProcess != null && !_serverProcess.HasExited;

    public string TranscriptDirectory { get; private set; }
    public string DevicePreference { get; set; } = "auto"; // "auto", "cuda", or "cpu"

    public PythonRunner(string scriptDirectory)
    {
        TranscriptDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LongAudioApp", "Transcripts");
        Directory.CreateDirectory(TranscriptDirectory);

        // Priority 1: Local Venv (Created by PipInstaller)
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var venvPython = Path.Combine(appDir, "fast_engine_venv", "Scripts", "python.exe");
        
        // Priority 2: Embedded Python (bin/python/python.exe)
        var embeddedPython = Path.Combine(appDir, "bin", "python", "python.exe");

        _scriptPath = Path.Combine(scriptDirectory, SCRIPT_NAME);

        if (File.Exists(venvPython))
        {
            _pythonPath = venvPython;
            Debug.WriteLine($"[PythonRunner] Using Venv: {_pythonPath}");
        }
        else if (File.Exists(embeddedPython))
        {
            _pythonPath = embeddedPython;
            Debug.WriteLine($"[PythonRunner] Using Embedded: {_pythonPath}");
        }
        else
        {
            // Fallback: System Path
            _pythonPath = "python";
            Debug.WriteLine("[PythonRunner] Using System Python (PATH)");
        }
    }

    public async Task StartServerAsync()
    {
        if (IsServerRunning) return;

        await _serverLock.WaitAsync();
        try
        {
            if (IsServerRunning) return;

            var startInfo = new ProcessStartInfo
            {
                FileName = _pythonPath,
                Arguments = BuildArgs("server"),
                WorkingDirectory = Path.GetDirectoryName(_scriptPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            startInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";

            _serverProcess = new Process { StartInfo = startInfo };
            _serverProcess.Start();
            _serverProcess.BeginOutputReadLine();
            _serverProcess.BeginErrorReadLine();
            
            // We'll hook up events on demand or globally? 
            // For now, let's just log server output globally
            _serverProcess.ErrorDataReceived += (s, e) => { if (e.Data != null) OutputReceived?.Invoke($"[SERVER_ERR] {e.Data}"); };

            OutputReceived?.Invoke("[SERVER] Engine started.");
        }
        catch (Exception ex)
        {
            OutputReceived?.Invoke($"[SERVER] Failed to start: {ex.Message}");
            _serverProcess = null;
        }
        finally
        {
            _serverLock.Release();
        }
    }

    public async Task StopServerAsync()
    {
        await _serverLock.WaitAsync();
        try
        {
            if (_serverProcess != null && !_serverProcess.HasExited)
            {
                try 
                { 
                    var json = System.Text.Json.JsonSerializer.Serialize(new { action = "exit" });
                    await _serverProcess.StandardInput.WriteLineAsync(json);
                    await _serverProcess.StandardInput.FlushAsync();
                    await _serverProcess.WaitForExitAsync(new CancellationTokenSource(2000).Token);
                } 
                catch { }
                
                if (!_serverProcess.HasExited) _serverProcess.Kill();
                _serverProcess.Dispose();
                _serverProcess = null;
                OutputReceived?.Invoke("[SERVER] Engine stopped.");
            }
        }
        finally
        {
            _serverLock.Release();
        }
    }

    private string BuildArgs(string command, string args = "")
    {
        var deviceArg = $"--device {DevicePreference}";
        return $"\"{_scriptPath}\" {command} {deviceArg} {args}".Trim();
    }

    private async Task SendCommandAndWaitAsync(object commandObj, string actionName)
    {
        if (_serverProcess == null || _serverProcess.HasExited)
             throw new InvalidOperationException("Server is not running.");

        if (IsRunning) throw new InvalidOperationException("A process is already running.");
        
        IsRunning = true;
        RunningChanged?.Invoke(true);
        _cts = new CancellationTokenSource();

        var tcs = new TaskCompletionSource<bool>();
        
        DataReceivedEventHandler handler = (s, e) => 
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            
            // Forward output to UI
            OutputReceived?.Invoke(e.Data);
            
            // Validation/Parsing
            try 
            {
                if (e.Data.Trim().StartsWith("{") && e.Data.Trim().EndsWith("}"))
                {
                    var doc = System.Text.Json.JsonDocument.Parse(e.Data);
                    if (doc.RootElement.TryGetProperty("status", out var statusProp) && 
                        statusProp.GetString() == "complete" &&
                        doc.RootElement.TryGetProperty("action", out var actionProp) &&
                        actionProp.GetString() == actionName)
                    {
                        tcs.TrySetResult(true);
                    }
                }
            }
            catch {} // Ignore parsing errors for non-JSON lines

            // Also parse progress from server output
            var match = ProgressRegex.Match(e.Data);
            if (match.Success)
            {
                var c = int.Parse(match.Groups[1].Value);
                var t = int.Parse(match.Groups[2].Value);
                var f = match.Groups[3].Value.Trim();
                ProgressUpdated?.Invoke(c, t, f);
            }
            // Parse voice detection
            if (e.Data.Contains("[VOICE]")) VoiceDetected?.Invoke(e.Data);
            if (e.Data.Contains("[ERROR]")) ErrorOccurred?.Invoke(e.Data);
        };

        _serverProcess.OutputDataReceived += handler;

        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(commandObj);
            await _serverProcess.StandardInput.WriteLineAsync(json);
            await _serverProcess.StandardInput.FlushAsync();
            
            // Wait for completion or cancellation
            using (var registration = _cts.Token.Register(() => tcs.TrySetCanceled()))
            {
                await tcs.Task;
            }
        }
        catch (OperationCanceledException)
        {
            OutputReceived?.Invoke("[CANCELLED] Operation was cancelled.");
        }
        finally
        {
            _serverProcess.OutputDataReceived -= handler;
            IsRunning = false;
            RunningChanged?.Invoke(false);
            _cts = null;
        }
    }

    public async Task RunBatchScanAsync(string directory, bool useVad, string? reportPath = null)
    {
        // Strip trailing backslash to prevent it escaping the closing quote on the command line
        var safeDir = directory.TrimEnd('\\', '/');
        if (safeDir.Length == 2 && safeDir[1] == ':') safeDir += "\\"; 
        if (safeDir.EndsWith("\\")) safeDir = safeDir.TrimEnd('\\');
        
        if (IsServerRunning)
        {
            await SendCommandAndWaitAsync(new 
            { 
                action = "scan", 
                directory = safeDir, 
                use_vad = useVad, 
                report_path = reportPath 
            }, "scan");
        }
        else
        {
            var cmdArgs = $"--dir \"{safeDir}\"";
            if (reportPath != null) cmdArgs += $" --report \"{reportPath}\"";
            if (!useVad) cmdArgs += " --no-vad";
            
            await RunProcessAsync(BuildArgs("batch_scan", cmdArgs));
        }
    }

    public async Task RunBatchTranscribeAsync(string? reportPath = null, bool skipExisting = false)
    {
        // Server mode not implemented for this action yet
        var cmdArgs = $"--output-dir \"{TranscriptDirectory}\"";
        if (reportPath != null) cmdArgs += $" --report \"{reportPath}\"";
        if (skipExisting) cmdArgs += " --skip-existing";
        await RunProcessAsync(BuildArgs("batch_transcribe", cmdArgs));
    }

    public async Task RunBatchTranscribeDirAsync(string directory, bool useVad, bool skipExisting)
    {
        var safeDir = directory.TrimEnd('\\', '/');
        if (safeDir.EndsWith("\\")) safeDir = safeDir.TrimEnd('\\');

        // Server mode not implemented for this specific action yet in fast_engine.py?
        // Checked fast_engine.py, I haven't added "batch_transcribe_dir" to server mode.
        // So fallback to legacy always.
        
        var cmdArgs = $"--dir \"{safeDir}\" --output-dir \"{TranscriptDirectory}\"";
        if (!useVad) cmdArgs += " --no-vad";
        if (skipExisting) cmdArgs += " --skip-existing";
        await RunProcessAsync(BuildArgs("batch_transcribe_dir", cmdArgs));
    }

    public async Task RunTranscribeAsync(string file, double start, double end)
    {
        if (IsServerRunning)
        {
            await SendCommandAndWaitAsync(new 
            { 
                action = "transcribe", 
                file = file, 
                start = start, 
                end = end, 
                output_dir = TranscriptDirectory
            }, "transcribe");
        }
        else
        {
            var cmdArgs = $"\"{file}\" --start {start} --end {end} --output-dir \"{TranscriptDirectory}\"";
            await RunProcessAsync(BuildArgs("transcribe", cmdArgs));
        }
    }

    public async Task RunTranscribeFileAsync(string file, string model, bool useVad, bool skipExisting)
    {
         // "transcribe_file" mode also not added to server loop yet.
         // Fallback to legacy.
        var cmdArgs = $"\"{file}\" --model {model} --output-dir \"{TranscriptDirectory}\"";
        if (!useVad) cmdArgs += " --no-vad";
        if (skipExisting) cmdArgs += " --skip-existing";
        await RunProcessAsync(BuildArgs("transcribe_file", cmdArgs));
    }

    public async Task RunSearchTranscriptsAsync(string directory, string query)
    {
        var safeDir = directory.TrimEnd('\\', '/');
        var cmdArgs = $"--dir \"{safeDir}\" --query \"{query}\"";
        await RunProcessAsync(BuildArgs("search_transcripts", cmdArgs));
    }

    public async Task RunSemanticSearchAsync(string directory, string query, string embedModel, string transcriptDir)
    {
        // Run semantic search in its own independent process so it works even while transcription is running
        var safeDir = directory.TrimEnd('\\', '/');
        var cmdArgs = $"--dir \"{safeDir}\" --query \"{query}\" --embed-model {embedModel} --transcript-dir \"{transcriptDir}\"";
        var arguments = BuildArgs("semantic_search", cmdArgs);

        var psi = new ProcessStartInfo
        {
            FileName = _pythonPath,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(_scriptPath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
        var pyDir = Path.GetDirectoryName(_pythonPath);
        if (!string.IsNullOrEmpty(pyDir))
        {
            var envPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            psi.EnvironmentVariables["PATH"] = pyDir + ";" + envPath;
        }

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        // Read output in parallel (fires same events as main runner)
        var stdoutTask = Task.Run(async () =>
        {
            while (true)
            {
                var line = await proc.StandardOutput.ReadLineAsync();
                if (line == null) break;
                OutputReceived?.Invoke(line);
            }
        });
        var stderrTask = Task.Run(async () =>
        {
            while (true)
            {
                var line = await proc.StandardError.ReadLineAsync();
                if (line == null) break;
                OutputReceived?.Invoke(line);
            }
        });

        await Task.WhenAll(stdoutTask, stderrTask);
        await proc.WaitForExitAsync();
    }

    public async Task RunAnalyzeAsync(string transcriptFile, string analyzeType, string provider,
        string? model = null, string? apiKey = null, string? cloudModel = null)
    {
        var cmdArgs = $"\"{transcriptFile}\" --analyze-type {analyzeType} --provider {provider}";
        if (!string.IsNullOrEmpty(model)) cmdArgs += $" --model {model}";
        if (!string.IsNullOrEmpty(apiKey)) cmdArgs += $" --api-key \"{apiKey}\"";
        if (!string.IsNullOrEmpty(cloudModel)) cmdArgs += $" --cloud-model \"{cloudModel}\"";
        await RunProcessAsync(BuildArgs("analyze", cmdArgs));
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
                WorkingDirectory = Path.GetDirectoryName(_scriptPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            // Ensure unbuffered output
            psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";

            // Add python dir to PATH
            var pyDir = Path.GetDirectoryName(_pythonPath);
            if (!string.IsNullOrEmpty(pyDir))
            {
                 var envPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                 psi.EnvironmentVariables["PATH"] = pyDir + ";" + envPath;
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
        
        // Kill server process
        try { if (_serverProcess != null && !_serverProcess.HasExited) _serverProcess.Kill(); } catch {}
        _serverProcess?.Dispose();
    }
}
