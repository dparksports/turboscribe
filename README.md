# 🎙️ TurboScribe

**Fast, private, GPU-accelerated transcription & video processing for Windows.**

TurboScribe transcribes audio and video files entirely on your machine using OpenAI's Whisper models. It also extracts burned-in timestamps from surveillance/dashcam footage and batch-renames files automatically. No cloud services, no subscriptions — your data stays on your computer.

---

## 📥 Download

**[⬇ Download TurboScribe v3.0](https://github.com/dparksports/turboscribe/releases/latest)**

Extract the zip → run `TurboScribe.exe` → done.

**Requirements:** Windows 10/11 (x64), .NET 8 Runtime. NVIDIA GPU recommended.

---

## ✨ Features

### 🎤 Transcription
- **12 Whisper models** — tiny through large-v3, turbo, and English-specific variants
- **GPU acceleration** — CUDA support for 4× faster transcription on NVIDIA GPUs
- **Voice Activity Detection** — Silero VAD scans files for speech before transcribing
- **Batch processing** — transcribe entire drives, folders, or USB devices
- **Smart skip** — automatically skips files that already have transcripts
- **Multi-model comparison** — run different models on the same file side-by-side

### 🎬 Batch Video Rename *(New in v3.0)*
- **Timestamp extraction** — uses Qwen2.5-VL vision model to read burned-in timestamps from video frames
- **Auto-rename** — generates standardized filenames: `YYYYMMDD_HHMMSS-HHMMSS_location.mp4`
- **Drive selector** — pick any drive from a dropdown, add optional subfolder path
- **Prefix filter** — only process files matching a keyword (e.g., "reo")
- **Recursive scanning** — include all subfolders with one checkbox
- **Concurrent processing** — transcribe files while batch-renaming runs simultaneously

### 🔍 Search
- **Keyword search** — exact text matching across all transcripts
- **Semantic search** — find content by meaning using sentence-transformers
- **5 embedding models** — MiniLM, mpnet, GTE, Qwen3-Embedding, Gemma-Embedding

### 🤖 AI Analysis
- **Summarize & outline** — generate structured summaries for transcripts
- **Local or cloud** — LLaMA, Mistral, Phi-3, Qwen2, Gemma locally or Gemini/OpenAI/Claude via API
- **Batch analysis** — process all transcripts at once

### ▶️ Media Player
- **Embedded playback** — play audio/video directly in the app
- **Transcript sync** — click lines to seek, or scrub timeline to highlight text

---

## 🚀 Quick Start

1. **Download** the [latest release](https://github.com/dparksports/turboscribe/releases/latest)
2. **Extract** and run `TurboScribe.exe`
3. **Install AI Libraries** — Settings → Install AI Libraries (one-time, ~2GB)
4. **Select drives/folders** using the checkboxes on the Transcribe tab
5. **Scan for Voice** → **Transcribe All Files**

### Batch Video Rename
1. Go to the **Timestamps** tab
2. Select a **drive** from the dropdown
3. Set the **prefix filter** (e.g., `reo`) to target specific files
4. Check **Include Subfolders** and **Auto Rename**
5. Click **Scan & Rename** — files are renamed as timestamps are extracted

---

## 🔧 Tech Stack

| Component | Technology |
|---|---|
| Transcription | [faster-whisper](https://github.com/SYSTRAN/faster-whisper) + CUDA |
| Timestamp OCR | [Qwen2.5-VL-7B](https://huggingface.co/Qwen/Qwen2.5-VL-7B-Instruct) |
| Voice Detection | Silero VAD |
| Semantic Search | sentence-transformers |
| AI Analysis | llama-cpp-python / cloud APIs |
| Desktop App | WPF, .NET 8, C# |

---

## 🛠️ Build from Source

```bash
git clone https://github.com/dparksports/turboscribe.git
cd turboscribe
dotnet restore
dotnet run --project LongAudioApp
```

---

## 📝 Changelog

### v3.0.0
- Batch video rename with VLM timestamp extraction
- Drive selector for easy path selection
- Auto-rename during scan (no separate rename step)
- Filename prefix filter
- Recursive subfolder scanning
- Dedicated timestamp runner for concurrent transcription + rename
- Drive path quoting fix for root drives

### v2.7.0
- Voice duration column with sorting
- Untranscribed files list
- Model names in transcript filenames
- Delete all transcripts option
- Rounded section borders UI refresh

---

## 📄 License

[Apache License 2.0](LICENSE)
