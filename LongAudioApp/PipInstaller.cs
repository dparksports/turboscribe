using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace LongAudioApp;

public class PipInstaller
{
    private readonly string _pythonPath;

    public PipInstaller(string pythonPath)
    {
        _pythonPath = pythonPath;
    }

    public bool IsPipInstalled()
    {
        // Check if pip module is available
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _pythonPath,
                Arguments = "-m pip --version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task CreateVenvAsync(string venvPath, Action<string> onOutput)
    {
        onOutput($"Creating venv at: {venvPath}...");
        
        var psi = new ProcessStartInfo
        {
            FileName = _pythonPath, // This should be the BASE python (e.g. system or embedded)
            Arguments = $"-m venv \"{venvPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (s, e) => { if (e.Data != null) onOutput(e.Data); };
        p.ErrorDataReceived += (s, e) => { if (e.Data != null) onOutput($"[ERR] {e.Data}"); };
        
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        
        await p.WaitForExitAsync();
        
        if (p.ExitCode != 0)
        {
            throw new Exception($"Failed to create venv. Exit code {p.ExitCode}");
        }
        
        onOutput("Venv created successfully.");
    }

    public async Task InstallPipAsync(Action<string> onOutput)
    {
        onOutput("Downloading get-pip.py...");
        var getPipUrl = "https://bootstrap.pypa.io/get-pip.py";
        var tempFile = Path.Combine(Path.GetTempPath(), "get-pip.py");
        
        using (var client = new System.Net.Http.HttpClient())
        {
            var bytes = await client.GetByteArrayAsync(getPipUrl);
            await File.WriteAllBytesAsync(tempFile, bytes);
        }

        onOutput("Installing pip...");
        await RunCommandAsync($"\"{tempFile}\" --no-warn-script-location", onOutput);
    }

    public async Task InstallLibrariesAsync(Action<string> onOutput)
    {
        int failures = 0;

        // Install torch with CUDA 12.4 support (cu124).
        // cu124 works with RTX 3090, 4090, and 5090 and has the widest wheel availability.
        // Without the --index-url, pip installs CPU-only torch on Windows.
        onOutput("Installing PyTorch with CUDA 12.4 (cu124) support...");
        if (!await TryRunCommandAsync("-m pip install torch torchaudio --upgrade --no-warn-script-location --index-url https://download.pytorch.org/whl/cu124", onOutput))
            failures++;
        
        // Install faster-whisper separately (from default PyPI)
        onOutput("Installing faster-whisper...");
        if (!await TryRunCommandAsync("-m pip install faster-whisper --upgrade --no-warn-script-location --prefer-binary", onOutput))
            failures++;

        // Install sentence-transformers for semantic search
        onOutput("Installing sentence-transformers for semantic search...");
        if (!await TryRunCommandAsync("-m pip install sentence-transformers --upgrade --no-warn-script-location --prefer-binary", onOutput))
            failures++;

        // Install llama-cpp-python with CUDA support for GPU inference
        // Default PyPI wheel is CPU-only on Windows; use the pre-built CUDA wheel
        onOutput("Installing llama-cpp-python with CUDA support...");
        if (!await TryRunCommandAsync("-m pip install llama-cpp-python --upgrade --no-warn-script-location --prefer-binary --extra-index-url https://abetlen.github.io/llama-cpp-python/whl/cu124", onOutput))
            failures++;

        // Install huggingface-hub for model downloading
        onOutput("Installing huggingface-hub...");
        if (!await TryRunCommandAsync("-m pip install huggingface-hub --upgrade --no-warn-script-location --prefer-binary", onOutput))
            failures++;

        // Install openai SDK (used for OpenAI and Gemini APIs)
        onOutput("Installing openai SDK...");
        if (!await TryRunCommandAsync("-m pip install openai --upgrade --no-warn-script-location --prefer-binary", onOutput))
            failures++;

        // Install anthropic SDK (for Claude API)
        onOutput("Installing anthropic SDK...");
        if (!await TryRunCommandAsync("-m pip install anthropic --upgrade --no-warn-script-location --prefer-binary", onOutput))
            failures++;

        if (failures > 0)
            onOutput($"\nDone with {failures} warning(s). Some packages may have failed — check the log above.");
        else
            onOutput("\nAll libraries installed successfully.");
    }

    private async Task RunCommandAsync(string args, Action<string> onOutput)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _pythonPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (s, e) => { if (e.Data != null) onOutput(e.Data); };
        p.ErrorDataReceived += (s, e) => { if (e.Data != null) onOutput($"[ERR] {e.Data}"); };
        
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        
        await p.WaitForExitAsync();
        
        if (p.ExitCode != 0)
        {
            throw new Exception($"Command failed with exit code {p.ExitCode}");
        }
    }

    /// <summary>Runs a pip command, returns true on success, false on failure (logs warning instead of throwing).</summary>
    private async Task<bool> TryRunCommandAsync(string args, Action<string> onOutput)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _pythonPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (s, e) => { if (e.Data != null) onOutput(e.Data); };
        p.ErrorDataReceived += (s, e) => { if (e.Data != null) onOutput($"[ERR] {e.Data}"); };
        
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        
        await p.WaitForExitAsync();
        
        if (p.ExitCode != 0)
        {
            onOutput($"[WARN] Command exited with code {p.ExitCode} — continuing...");
            return false;
        }
        return true;
    }
}
