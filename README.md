# TurboScribe

**GPU-accelerated audio transcription and meeting detection for Windows.**

TurboScribe uses OpenAI's Whisper models to transcribe long-form audio files (hours of content) with high accuracy. It includes an AI-powered meeting detector that uses local LLMs to distinguish real conversations from hallucinated transcripts.

## Download

**[📥 Download TurboScribe-Windows.zip](https://github.com/dparksports/turboscribe/releases/latest/download/TurboScribe-Windows.zip)**

Extract the zip and run `TurboScribe.exe` — no installation required.

## Features

### 🎙️ Transcription
- **Batch transcription** of entire directories (supports MP3, WAV, M4A, FLAC, OGG, WMA)
- **12 Whisper models** from tiny (fast) to large-v3 (most accurate)
- **GPU acceleration** via CUDA for 10-20x faster processing
- **Adjustable beam size** for speed/accuracy tradeoff
- **Multi-version support** — keep transcripts for multiple models per file

### 🤖 AI Meeting Detection
- **Local LLM** (llama-cpp-python) or cloud APIs (Gemini, OpenAI, Claude)
- **GPU-accelerated inference** with automatic CPU fallback
- **Real-time progress** updates during detection
- **Skip checked files** to resume interrupted scans
- **Cancel anytime** without losing progress

### 🔍 Search & Analysis
- **Semantic search** across all transcripts using sentence embeddings
- **Keyword search** with context snippets
- **Timestamp extraction** for video files (creates preview thumbnails)

### ⚙️ Developer-Friendly
- **Python backend** (`fast_engine.py`) with standalone CLI
- **WPF frontend** for Windows desktop
- **Persistent engine server** mode for faster repeated operations
- **JSON reports** for batch processing results

## System Requirements

- **OS**: Windows 10/11 (64-bit)
- **GPU**: NVIDIA GPU with CUDA support (optional, but highly recommended)
- **RAM**: 8 GB minimum, 16 GB recommended for large models
- **Storage**: ~10 GB for models and dependencies

## Quick Start

1. **Launch** `TurboScribe.exe`
2. **Select a directory** containing audio files
3. **Choose a Whisper model** (start with `base.en` for testing, `large-v3` for production)
4. **Click "Transcribe All Files"** — transcripts are saved as `filename_transcript_modelname.txt`
5. **Optional**: Click "Detect Meetings" to use AI to filter out hallucinated transcripts

## Model Comparison

| Model | Accuracy | Speed (1hr GPU) | Best For |
|---|---|---|---|
| `tiny.en` | ★★☆☆☆ | ~30 sec | Voice detection / scanning |
| `base.en` | ★★★☆☆ | ~1 min | Quick draft transcripts |
| `small.en` | ★★★½☆ | ~2 min | Everyday accuracy |
| `medium.en` | ★★★★☆ | ~4 min | High quality English-only |
| `large-v2` | ★★★★★ | ~8 min | Best stability, few hallucinations |
| `large-v3` | ★★★★★ | ~8 min | Best for accents / multilingual |
| `turbo` | ★★★★½ | ~3 min | Best speed/accuracy tradeoff |

## CLI Usage

The Python backend can be used standalone:

```bash
# Transcribe a single file
python fast_engine.py transcribe_file "audio.mp3" --model large-v3 --beam-size 5

# Batch transcribe a directory
python fast_engine.py batch_transcribe_dir --dir "C:\Audio" --model turbo

# Detect meetings in transcripts
python fast_engine.py detect_meetings --dir "C:\Audio" --provider local --model "llama-3.2-3b-instruct-q4_k_m.gguf"

# Semantic search across transcripts
python fast_engine.py semantic_search --dir "C:\Audio" --query "quarterly earnings"
```

## Configuration

### GPU Settings
- **Auto (default)**: Uses GPU if available, falls back to CPU
- **GPU (CUDA)**: Forces GPU mode (requires CUDA-enabled PyTorch)
- **CPU Only**: Disables GPU acceleration

### Beam Size
- **1 (Fast)**: Greedy decoding — fastest, fine for tiny/base
- **3**: Balanced — good for small/medium
- **5 (Best)**: Most accurate — recommended for large/turbo
- **Auto**: Picks 1 for tiny/base, 3 for small, 5 for large

## Building from Source

```bash
git clone https://github.com/dparksports/turboscribe.git
cd turboscribe
dotnet publish -c Release LongAudioApp/LongAudioApp.csproj -o publish
```

Python dependencies are auto-installed on first run via the built-in `PipInstaller`.

## License

MIT License — see [LICENSE](LICENSE) for details.

## Credits

- **Whisper**: OpenAI's speech recognition model
- **llama-cpp-python**: Python bindings for llama.cpp
- **faster-whisper**: CTranslate2-based Whisper implementation
- **sentence-transformers**: Semantic search embeddings
