# 🎙️ TurboScribe

**Transcribe entire drives of audio and video — free, local and private.**

TurboScribe is a Windows desktop app that transcribes your media files entirely on your machine using GPU-accelerated Whisper. No cloud services, no subscriptions, and no data ever leaves your computer.

---

## 📥 Download

**[⬇ Download TurboScribe v2.5.0 (Windows x64)](https://github.com/dparksports/turboscribe/releases/download/v2.5.0/TurboScribe-v2.5.0-win-x64.zip)**

Extract the zip → run `TurboScribe.exe` → done.

**Requirements:** Windows 10/11 with .NET 8 Runtime. NVIDIA GPU recommended for fast transcription.

---

## ✨ Features

### Transcription
- **GPU-accelerated** — up to 4× faster than standard Whisper with CUDA
- **12 Whisper models** — tiny, base, small, medium, large-v1/v2/v3, turbo (+ English-only variants)
- **Multi-drive scanning** — check entire drives, USB devices, or custom folders
- **Smart filtering** — "Current Folder" toggle to show only files from selected locations
- **Re-transcribe** — run any file with different models and compare versions side-by-side
- **Skip existing** — automatically skip files that already have transcripts
- **Voice detection** — fast VAD scan to find files with speech before transcribing

### Integrated Media Player
- **Embedded playback** — play audio/video directly in the app
- **Bidirectional sync** — click transcript lines to seek, or scrub to highlight matching text
- **Full controls** — play/pause, stop, timeline scrubbing, volume

### AI Analysis
- **Summarize & Outline** — generate summaries or structured outlines for any transcript
- **Local or Cloud LLMs** — use local models (LLaMA, Mistral, Phi-3, Qwen2, Gemma) or cloud APIs (Gemini, OpenAI, Claude)
- **Batch analysis** — summarize or outline all transcripts at once
- **Export** — save analysis results to file

### Semantic Search
- **Exact match** — keyword search across all transcripts
- **Semantic search** — find content by meaning using sentence-transformers
- **5 embedding models** — MiniLM, mpnet, GTE, Qwen3-Embedding, Gemma-Embedding

### UI & Design
- **Dark theme** — polished dark UI with teal accents
- **Tabbed interface** — Transcribe, Semantic Search, Log, Settings
- **Model badges** — see which Whisper models have been used for each file
- **Context menus** — right-click actions on files

---

## 🚀 Quick Start

1. **Download** the [latest release](https://github.com/dparksports/turboscribe/releases/latest)
2. **Extract** and run `TurboScribe.exe`
3. **Install AI Libraries** (one-time):
   - Go to **Settings → Install AI Libraries**
   - This installs Python + faster-whisper (~2GB download)
4. **Select drives/folders** to scan using the checkboxes
5. **Click ▶ Transcribe All Files**

---

## 🔧 Tech Stack

| Component | Technology |
|---|---|
| Transcription | [faster-whisper](https://github.com/SYSTRAN/faster-whisper) with CUDA acceleration |
| Voice Detection | Silero VAD |
| Semantic Search | sentence-transformers (MiniLM, mpnet, GTE, Qwen3, Gemma) |
| AI Analysis | llama-cpp-python (local) or cloud APIs (Gemini, OpenAI, Claude) |
| Desktop App | WPF, .NET 8, C# |

---

## 🛠️ Build from Source

```bash
git clone https://github.com/dparksports/turboscribe.git
cd turboscribe
dotnet run --project LongAudioApp
```

---

## 📝 Changelog

### v2.5.0 (2026-02-09)
- **Fixed:** "Current Folder" now correctly lists only files from checked drives/folders
- **Fixed:** File list now updates when toggling drive checkboxes
- **Improved:** Multi-folder scanning now works correctly

### v2.0.1
- Initial public release

---

## 📄 License

[Apache License 2.0](LICENSE)
