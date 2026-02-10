# 🎙️ TurboScribe

**Transcribe entire drives of audio and video — free, local and private.**

TurboScribe is a Windows desktop app powered by [faster-whisper](https://github.com/SYSTRAN/faster-whisper) that transcribes your media files entirely on your machine. No cloud services, no subscriptions, and no data ever leaves your computer. Point it at any drive — local, USB, or network — and let it process everything automatically.

---

## 📥 Download

**[⬇ Download TurboScribe v2.0.0 (Windows x64)](https://github.com/dparksports/turboscribe/releases/download/v2.0.0/TurboScribe-v2.0.0.zip)**

Extract the zip → run `TurboScribe.exe` → done.

**Requires:** Windows 10/11 with .NET 8 Runtime. NVIDIA GPU recommended for fast transcription.

---

## 📸 Screenshot

![TurboScribe v2.0.0](turboscribe_dark_ui_v160_1770686953675.png)

---

## ✨ Features

### Transcription
- **GPU-accelerated** via faster-whisper and CTranslate2 — up to 4× faster than standard Whisper
- **Multiple Whisper models** — tiny, base, small, medium, large-v1/v2/v3, turbo
- **English-only filter** — toggle to show only English-optimized models
- **Re-transcribe** — re-run any file with a different model and compare versions side-by-side
- **Batch processing** — check entire drives and transcribe everything in one click
- **Skip existing** — automatically skip files that already have transcripts
- **Exceptional noise handling** — works great with noisy outdoor recordings

### Integrated Media Player
- **Embedded playback** — play audio/video directly inside the app
- **Bidirectional sync** — click a transcript line to seek the player, or scrub the player to highlight the matching line
- **Timeline scrubbing** — seek slider, play/pause, stop, and volume controls

### AI Analysis
- **Summarize & Outline** — generate summaries or outlines for any transcript
- **Local or Cloud LLMs** — use local models (LLaMA, Mistral, Phi-3, Qwen2, Gemma) or cloud APIs (Gemini, OpenAI, Claude)
- **Save analysis** — export summaries and outlines to file

### Semantic Search
- **Keyword search** — exact-match search across all transcripts
- **Semantic search** — find content by meaning using sentence-transformers
- **Multiple embedding models** — MiniLM, mpnet, GTE, Qwen3, Gemma

### UI & Design
- **Dark theme** — polished dark UI with teal accent colors
- **Rounded section cards** — clean visual grouping with rounded borders
- **Tabbed interface** — Transcribe, Semantic Search, Log, and Settings tabs
- **Context menus** — right-click actions on transcript files

---

## 🚀 Quick Start

1. Download and extract the [latest release](https://github.com/dparksports/turboscribe/releases/latest)
2. Run `TurboScribe.exe`
3. Go to **Settings → Install AI Libraries** (one-time, installs Python + faster-whisper)
4. Check the drives you want to scan
5. Click **▶ Transcribe All Files**

---

## 🛠️ Build from Source

```bash
git clone https://github.com/dparksports/turboscribe.git
cd turboscribe
dotnet run --project LongAudioApp
```

---

## 🔧 Tech Stack

| Component | Technology |
|---|---|
| Transcription | [faster-whisper](https://github.com/SYSTRAN/faster-whisper) with CUDA acceleration |
| Voice Detection | Silero VAD |
| Semantic Search | sentence-transformers (MiniLM, mpnet, GTE, Qwen3, Gemma) |
| AI Analysis | Local (LLaMA, Mistral, Phi-3, Qwen2, Gemma) or Cloud (Gemini, OpenAI, Claude) |
| Desktop App | WPF, .NET 8, C# |

---

## 📄 License

[Apache License 2.0](LICENSE)
