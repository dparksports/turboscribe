# 🎙️ TurboScribe Private

**Transcribe your meetings and notes locally, privately, and 10x faster than online services.**

**TurboScribe Private** is a **free and open-source**, GPU-accelerated desktop tool designed for professionals who need to transcribe sensitive audio — **meetings, interviews, voice memos, and lectures** — without uploading data to the cloud. **(Requires NVIDIA GPU)**

> **🌟 Exceptional Noise Handling:** Works perfectly with **noisy outdoor meetings** involving car traffic, planes, lawn mowers, wind, and barking dogs.

# TurboScribe Private

**TurboScribe Private** is a secure, offline transcription tool powered by **faster-whisper** and **Whisper Large-v3**. It runs entirely on your local machine, ensuring no audio data ever leaves your device.

## 📥 Download

**[Download TurboScribe Private v1.3.6 (Windows x64)](https://github.com/dparksports/turboscribe-private/releases/download/v1.3.6/TurboScribePrivate-v1.3.6-win-x64.zip)**  
*(Extract the zip and run `TurboScribePrivate.exe`)*

## 🔒 Why TurboScribe Private?

- **100% Private & Offline**: Your audio files *never* leave your computer. Perfect for confidential meetings, legal interviews, and private voice notes.
- **10x Faster Generation**: Built on `faster-whisper` (CTranslate2) with CUDA acceleration to transcribe hours of audio in minutes.
- **Batch Processing**: Point it at a folder of 1,000 meeting recordings and let it run.
- **Smart Voice Detection**: Automatically scans directories to find files containing human speech, ignoring silence and music.
- **Robust Noise Handling**: Works exceptionally well with **outdoor meetings** involving car noise, plane noise, lawn mowers, wind, and barking dogs.
- **Search & Archive**: Instantly search through thousands of generated transcripts to find exactly what was said.

## ✨ Features

- **Transcribe Meetings & Notes** — Optimized for long-form speech audio.
- **Compare Versions** — Re-transcribe important segments with larger models (e.g., `large-v3`) and view differences.
- **Silent File Tracking** — Detects and lists metadata-only/silent files to clean up your archive.
- **Dateset Privacy** — 100% Private & Offline.
- **Free & Open Source** — No subscriptions, no limits, fully auditable code.
- **System Requirements** — Requires an **NVIDIA GPU** with CUDA support.

## 🛠️ Tech Stack

| Component | Technology |
|-----------|------------|
| Transcription Engine | [faster-whisper](https://github.com/SYSTRAN/faster-whisper) (large-v3, GPU) |
| Voice Detection | Silero VAD |
| Desktop App | WPF (.NET 8, C#) |
| GPU Acceleration | CUDA via CTranslate2 |

## 🚀 Getting Started

### Prerequisites

- Windows 10/11
- Python 3.10+ with CUDA support
- .NET 8 SDK
- NVIDIA GPU with CUDA (recommended)

### Setup

```bash
# 1. Clone the repo
git clone https://github.com/dparksports/turboscribe.git
cd turboscribe

# 2. Create Python environment
python -m venv longaudio
longaudio\Scripts\activate

# 3. Install Python dependencies
pip install faster-whisper torch

# 4. Build the WPF app
dotnet build LongAudioApp\LongAudioApp.csproj

# 5. Run
dotnet run --project LongAudioApp
```

### Usage

1. **Scan Tab** — Set directory path, click **Start Scan** to detect voice in media files
2. **Transcribe Tab** — Use **Transcribe Voice Only** (scan-based) or **Transcribe All Files** (no scan needed)
3. **Search** — Type keywords in the search box and press Enter to find across all transcripts
4. **Re-transcribe** — Select a transcript, pick a model from the dropdown, click **🔄 Re-transcribe**
5. **Compare** — After re-transcribing with a different model, click **📊 Compare** to see a color-coded diff

## 📁 Project Structure

```
mylongaudio/
├── fast_engine.py              # Python transcription engine (7 modes)
├── LongAudioApp/               # WPF desktop application
│   ├── MainWindow.xaml          # UI layout (dark theme)
│   ├── MainWindow.xaml.cs       # Application logic
│   ├── PythonRunner.cs          # Python subprocess manager
│   ├── AnalyticsService.cs      # GA4 Measurement Protocol
│   └── ScanResult.cs            # Data models
├── setup_env.bat               # Environment setup script
└── LICENSE                     # Apache 2.0
```

## 📋 Transcription Modes

| Mode | Description |
|------|-------------|
| `scan` | Detect voice segments in a single file |
| `batch_scan` | Scan all media in a directory |
| `transcribe` | Transcribe a specific time range |
| `batch_transcribe` | Transcribe all detected voice segments |
| `batch_transcribe_dir` | Transcribe all files in directory (no scan needed) |
| `transcribe_file` | Full-file transcription with model selection |
| `search_transcripts` | Search across all transcript files |

## 📄 License

Licensed under the [Apache License 2.0](LICENSE).
