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
        // Core libraries
        // We use --prefer-binary to avoid building from source
        // We also want to ensure we get the CPU version of torch if no GPU? 
        // Actually faster-whisper handles this well usually, but on Windows 
        // we might need specific torch versions for CUDA if the user wants GPU.
        // For now, let's trust standard pip install which grabs latest stable.
        
        var libs = "faster-whisper torch torchaudio";
        onOutput($"Installing libraries: {libs}...");
        
        // --no-warn-script-location suppresses warnings about scripts not in PATH
        await RunCommandAsync($"-m pip install {libs} --upgrade --no-warn-script-location --prefer-binary", onOutput);
        
        onOutput("Installation complete.");
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
}
