import argparse
import torch
from faster_whisper import WhisperModel
import sys

# Force unbuffered output for real-time UI updates
sys.stdout.reconfigure(encoding='utf-8', line_buffering=True)
import os
import json
from datetime import datetime

import multiprocessing

# Device Configuration
def get_device_config(device_override=None):
    if device_override == "cpu":
        print("[INIT] Forced CPU mode.")
        return "cpu", "int8"
    elif device_override == "cuda":
        if torch.cuda.is_available():
            print("[INIT] Forced CUDA mode. GPU available.")
            return "cuda", "float16"
        else:
            print("[INIT] CUDA requested but not available! Falling back to CPU.")
            return "cpu", "int8"
    else:  # auto
        if torch.cuda.is_available():
            print("[INIT] CUDA detected. Using GPU.")
            return "cuda", "float16"
        else:
            print("[INIT] CUDA not found. Using CPU.")
            return "cpu", "int8"

DEVICE, COMPUTE = get_device_config()

MEDIA_EXTENSIONS = {'.mp4', '.mkv', '.avi', '.mov', '.wav', '.mp3', '.flac', '.m4a', '.webm', '.aac', '.wma', '.ogg', '.m4v', '.3gp', '.ts', '.mpg', '.mpeg'}

def find_media_files(directory):
    """Recursively find all media files in a directory."""
    # Robustly handle potential argument parsing artifacts (e.g. trailing quotes)
    if directory.endswith('"'): directory = directory[:-1]
    
    # Normalize bare drive letters: 'C:' -> 'C:\' (otherwise os.walk uses CWD on that drive)
    if len(directory) == 2 and directory[1] == ':':
        directory = directory + os.sep
    elif not os.path.isabs(directory):
        directory = os.path.abspath(directory)

    print(f"[DEBUG] Searching directory: {directory}")
    debug_log_path = os.path.join(os.path.dirname(os.path.abspath(directory)), "scan_debug.log")
    
    media_files = []
    
    try:
        with open(debug_log_path, "w", encoding="utf-8") as log:
            log.write(f"Scanning directory: {directory}\n")
            log.write(f"Extensions: {MEDIA_EXTENSIONS}\n")
            
            # Enable followlinks to find files in symlinked folders
            for root, dirs, files in os.walk(directory, followlinks=True):
                log.write(f"\nVisiting: {root}\n")
                for f in files:
                    ext = os.path.splitext(f)[1].lower()
                    if ext in MEDIA_EXTENSIONS:
                        full_path = os.path.join(root, f)
                        media_files.append(full_path)
                        log.write(f"  [ACCEPT] {f}\n")
                    else:
                        log.write(f"  [IGNORE] {f} ({ext})\n")
    except Exception as e:
        print(f"[ERROR] Discovery failed: {e}")
        try:
             with open(debug_log_path, "a", encoding="utf-8") as log:
                 log.write(f"[ERROR] {e}\n")
        except: pass

    media_files.sort()
    return media_files

def cluster_segments(segments, gap_threshold=180):
    """Cluster segments into blocks. Gap > gap_threshold seconds = new block."""
    blocks = []
    current_start = -1
    last_end = -1
    segment_count = 0

    for segment in segments:
        segment_count += 1
        if current_start == -1:
            current_start = segment.start

        if last_end != -1 and (segment.start - last_end) > gap_threshold:
            blocks.append({"start": round(current_start, 2), "end": round(last_end, 2)})
            current_start = segment.start

        last_end = segment.end

    if current_start != -1:
        blocks.append({"start": round(current_start, 2), "end": round(last_end, 2)})

    return blocks, segment_count


# ===== SILERO VAD-ONLY SCAN (no Whisper) =====

_vad_model = None

def _load_vad_model():
    """Load Silero VAD model (cached after first load)."""
    global _vad_model
    if _vad_model is None:
        import torch
        print("[VAD] Loading Silero VAD model...")
        _vad_model, _ = torch.hub.load(repo_or_dir='snakers4/silero-vad', model='silero_vad', trust_repo=True)
    return _vad_model

def _load_audio_as_tensor(file_path):
    """Load audio file as 16kHz mono float32 tensor using ffmpeg."""
    import torch
    import subprocess
    cmd = [
        'ffmpeg', '-i', file_path,
        '-f', 's16le', '-acodec', 'pcm_s16le',
        '-ar', '16000', '-ac', '1',
        '-v', 'quiet',
        '-'
    ]
    proc = subprocess.run(cmd, capture_output=True, timeout=600)
    if proc.returncode != 0:
        raise RuntimeError(f"ffmpeg failed: {proc.stderr.decode()[:200]}")
    import numpy as np
    audio = np.frombuffer(proc.stdout, dtype=np.int16).astype(np.float32) / 32768.0
    return torch.from_numpy(audio), len(audio) / 16000.0

def run_vad_scan(file_path, threshold=0.5):
    """
    Use Silero VAD to detect speech segments without transcription.
    Returns (speech_timestamps, speech_duration_sec, total_duration_sec)
    """
    import torch
    model = _load_vad_model()
    wav, total_duration = _load_audio_as_tensor(file_path)
    
    # Get speech timestamps
    speech_timestamps = model(wav, 16000, threshold=threshold, return_seconds=True)
    # model() with return_seconds returns list of dicts with 'start' and 'end' in seconds
    
    speech_duration = sum(t['end'] - t['start'] for t in speech_timestamps)
    
    return speech_timestamps, speech_duration, total_duration

def run_batch_vad_scan(directory, threshold=0.5, report_path=None, skip_existing=False):
    """
    Scan all media files in directory using Silero VAD (no Whisper).
    Outputs JSON report compatible with existing format.
    """
    media_files = find_media_files(directory)
    total = len(media_files)
    print(f"[VAD-SCAN] Found {total} media files in: {directory}")
    print(f"[VAD-SCAN] Sensitivity threshold: {threshold}")

    if report_path is None:
        report_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "voice_scan_results.json")

    # Load existing report if skip_existing
    existing_results = {}
    if skip_existing and os.path.exists(report_path):
        try:
            with open(report_path, "r", encoding="utf-8") as f:
                data = json.load(f)
                if "results" in data:
                    for r in data["results"]:
                        if "file" in r:
                            existing_results[r["file"]] = r
            print(f"[VAD-SCAN] Loaded {len(existing_results)} existing results for skipping.")
        except Exception as e:
            print(f"[VAD-SCAN] Failed to load existing report: {e}")

    results = []
    files_with_voice = 0

    for i, file_path in enumerate(media_files, 1):
        # Skip if already scanned
        if skip_existing and file_path in existing_results:
            prev = existing_results[file_path]
            if "error" not in prev:
                print(f"\n[{i}/{total}] Skipping (already scanned): {os.path.basename(file_path)}")
                results.append(prev)
                if prev.get("segments_found", 0) > 0:
                    files_with_voice += 1
                continue

        print(f"\n[{i}/{total}] VAD scanning: {os.path.basename(file_path)}")

        try:
            timestamps, speech_dur, total_dur = run_vad_scan(file_path, threshold=threshold)

            if timestamps:
                files_with_voice += 1
                # Convert timestamps to blocks format
                blocks = [{"start": round(t['start'], 2), "end": round(t['end'], 2)} for t in timestamps]
                
                entry = {
                    "file": file_path,
                    "duration_sec": round(total_dur, 1),
                    "segments_found": len(timestamps),
                    "speech_duration_sec": round(speech_dur, 1),
                    "blocks": blocks,
                    "transcribe_cmds": []
                }
                print(f"  [VOICE] {len(timestamps)} segments, {speech_dur:.1f}s speech / {total_dur:.1f}s total")
                results.append(entry)
            else:
                print(f"  [SILENT] No speech detected")
                results.append({
                    "file": file_path,
                    "duration_sec": round(total_dur, 1),
                    "segments_found": 0,
                    "speech_duration_sec": 0,
                    "blocks": []
                })

        except Exception as e:
            print(f"  [ERROR] {e}")
            results.append({
                "file": file_path,
                "error": str(e)
            })

    # Write JSON report
    os.makedirs(os.path.dirname(os.path.abspath(report_path)), exist_ok=True)

    report = {
        "date": datetime.now().isoformat(),
        "directory": directory,
        "total_files": total,
        "files_with_voice": files_with_voice,
        "results": results,
        "scan_model": "silero-vad",
        "vad_threshold": threshold
    }

    with open(report_path, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2, ensure_ascii=False)

    print(f"\n[VAD-SCAN] Complete: {files_with_voice}/{total} files with voice")
    print(f"[VAD-SCAN] Report saved to: {report_path}")


def run_scanner(file_path, use_vad=True):
    """
    MODE 1: SCOUT (Speed)
    Scans using Tiny Model with optional VAD.
    """
    print(f"[STATUS] Scanning {file_path}...")
    print(f"[STATUS] VAD: {'ON' if use_vad else 'OFF'}")

    model = WhisperModel("tiny.en", device=DEVICE, compute_type=COMPUTE)

    transcribe_opts = dict(
        vad_filter=use_vad,
        beam_size=1
    )
    if use_vad:
        transcribe_opts["vad_parameters"] = dict(min_silence_duration_ms=2000)

    segments, _ = model.transcribe(file_path, **transcribe_opts)

    blocks, segment_count = cluster_segments(segments)

    for b in blocks:
        print(f"[BLOCK] {b['start']}|{b['end']}")

    if segment_count == 0:
        print("[INFO] No speech detected.")

def run_batch_scanner(directory, use_vad=True, report_path=None, model=None, skip_existing=False):
    """
    MODE 3: BATCH SCOUT
    Scans all media files in a directory for voice activity.
    Outputs a JSON report with detected speech blocks.
    """
    media_files = find_media_files(directory)
    total = len(media_files)
    print(f"[BATCH] Found {total} media files in: {directory}")
    print(f"[BATCH] VAD: {'ON' if use_vad else 'OFF (outdoor/noisy mode)'}")

    if model is None:
        print(f"[BATCH] Loading tiny.en model (one-time)...")
        model = WhisperModel("tiny.en", device=DEVICE, compute_type=COMPUTE)
    else:
        print(f"[BATCH] Using pre-loaded model...")

    # Load existing report if skip_existing is True
    existing_results = {}
    if report_path is None:
        report_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "voice_scan_results.json")
    
    if skip_existing and os.path.exists(report_path):
        try:
             with open(report_path, "r", encoding="utf-8") as f:
                 data = json.load(f)
                 if "results" in data:
                     for r in data["results"]:
                         if "file" in r:
                             existing_results[r["file"]] = r
             print(f"[BATCH] Loaded {len(existing_results)} existing results for skipping.")
        except Exception as e:
            print(f"[BATCH] Failed to load existing report: {e}")

    results = []
    files_with_voice = 0

    for i, file_path in enumerate(media_files, 1):
        # Skip if already in results and no error
        if skip_existing and file_path in existing_results:
             prev = existing_results[file_path]
             if "error" not in prev:
                 print(f"\n[{i}/{total}] Skipping (already scanned): {file_path}")
                 results.append(prev)
                 if prev.get("segments_found", 0) > 0:
                     files_with_voice += 1
                 continue

        print(f"\n[{i}/{total}] Scanning: {file_path}")

        try:
            transcribe_opts = dict(
                vad_filter=use_vad,
                beam_size=1
            )
            if use_vad:
                transcribe_opts["vad_parameters"] = dict(min_silence_duration_ms=2000)

            segments, info = model.transcribe(file_path, **transcribe_opts)

            blocks, segment_count = cluster_segments(segments)

            if blocks:
                files_with_voice += 1
                duration = info.duration if hasattr(info, 'duration') else 0

                entry = {
                    "file": file_path,
                    "duration_sec": round(duration, 1),
                    "segments_found": segment_count,
                    "blocks": blocks,
                    "transcribe_cmds": []
                }

                for b in blocks:
                    cmd = f'python fast_engine.py transcribe "{file_path}" --start {b["start"]} --end {b["end"]}'
                    entry["transcribe_cmds"].append(cmd)
                    print(f"  [VOICE] {b['start']:.1f}s - {b['end']:.1f}s")

                results.append(entry)
            else:
                print(f"  [SILENT] No speech detected")
                # Record silent files too so we skip them next time
                results.append({
                    "file": file_path,
                    "duration_sec": 0,
                    "segments_found": 0,
                    "blocks": []
                })

        except Exception as e:
            print(f"  [ERROR] {e}")
            results.append({
                "file": file_path,
                "error": str(e)
            })

    # Write JSON report
    # Ensure directory exists
    os.makedirs(os.path.dirname(os.path.abspath(report_path)), exist_ok=True)

    report = {
        "date": datetime.now().isoformat(),
        "directory": directory,
        "total_files": total,
        "files_with_voice": files_with_voice,
        "results": results,
        "scan_model": "tiny.en" if model is None else "custom"
    }

    with open(report_path, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2, ensure_ascii=False)

    if results:
        print(f"\n--- Files with Detected Voice ---")
        for r in results:
            if "error" not in r and r.get("blocks"):
                print(f"\n  {r['file']}")
                for b in r["blocks"]:
                    print(f"    [{b['start']:.1f}s - {b['end']:.1f}s]")
                for cmd in r["transcribe_cmds"]:
                    print(f"    > {cmd}")

def run_transcriber(file_path, start, end, model=None, model_name="large-v3", output_dir=None, skip_existing=False):
    """
    MODE 2: SNIPER (Accuracy)
    Extracts the specific meeting and applies Large-v3.
    Saves transcription to a .txt file.
    """
    print(f"[STATUS] Transcribing: {file_path}")
    print(f"[STATUS] Range: {start:.1f}s - {end:.1f}s")

    if model is None:
        model = WhisperModel(model_name, device=DEVICE, compute_type=COMPUTE)

    segments, _ = model.transcribe(
        file_path,
        clip_timestamps=f"{start},{end}",
        beam_size=5,
        vad_filter=True
    )

    # Build transcription output
    lines = []
    for s in segments:
        line = f"[{s.start:.2f} - {s.end:.2f}] {s.text.strip()}"
        lines.append(line)
        print(f"[TEXT] {s.start:.2f}|{s.end:.2f}|{s.text.strip()}")

    # Determine output path — include model name for comparison
    safe_model = model_name.replace(".", "_").replace("-", "_")
    base_name = os.path.splitext(os.path.basename(file_path))[0]
    if output_dir:
        os.makedirs(output_dir, exist_ok=True)
        out_path = os.path.join(output_dir, f"{base_name}_transcript_{safe_model}.txt")
    else:
        out_path = f"{os.path.splitext(file_path)[0]}_transcript_{safe_model}.txt"

    if skip_existing and os.path.exists(out_path):
        print(f"[SKIPPING] Target exists: {out_path}")
        return []

    # Append if file exists (multiple blocks for same file)
    mode = "a" if os.path.exists(out_path) else "w"
    with open(out_path, mode, encoding="utf-8") as f:
        f.write(f"--- Transcription [{start:.1f}s - {end:.1f}s] ---\n")
        f.write(f"Source: {os.path.abspath(file_path)}\n")
        for line in lines:
            f.write(line + "\n")
        f.write("\n")

    print(f"[SAVED] {out_path}")
    return lines

def run_batch_transcriber(report_path=None, output_dir=None, skip_existing=False, model_name="large-v1"):
    """
    MODE 4: BATCH SNIPER
    Reads the scan report and transcribes all detected voice segments
    using the specified model. Saves transcriptions next to source files.
    """
    if report_path is None:
        report_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "voice_scan_results.json")

    if not os.path.exists(report_path):
        print(f"[ERROR] Report not found: {report_path}")
        print("[ERROR] Run batch_scan first to generate the report.")
        exit(1)

    with open(report_path, "r", encoding="utf-8") as f:
        report = json.load(f)

    # Filter to files with voice blocks (no errors)
    voice_files = [r for r in report["results"] if "error" not in r and r.get("blocks")]
    total_blocks = sum(len(r["blocks"]) for r in voice_files)

    print(f"[BATCH] Found {len(voice_files)} files with voice ({total_blocks} blocks to transcribe)")
    print(f"[BATCH] Loading {model_name} model (one-time)...")

    model = WhisperModel(model_name, device=DEVICE, compute_type=COMPUTE)

    block_num = 0
    for i, entry in enumerate(voice_files, 1):
        file_path = entry["file"]
        print(f"\n[{i}/{len(voice_files)}] {file_path}")

        for b in entry["blocks"]:
            block_num += 1
            print(f"  Block {block_num}/{total_blocks}: {b['start']:.1f}s - {b['end']:.1f}s")
            try:
                run_transcriber(file_path, b["start"], b["end"], model=model, model_name=model_name, output_dir=output_dir, skip_existing=skip_existing)
            except Exception as e:
                print(f"  [ERROR] {e}")

    print(f"\n{'='*60}")
    print(f"BATCH TRANSCRIPTION COMPLETE")
    print(f"{'='*60}")
    print(f"Files processed: {len(voice_files)}")
    print(f"Blocks transcribed: {block_num}")

def run_batch_transcribe_dir(directory, use_vad=True, output_dir=None, skip_existing=False, model_name="large-v1"):
    """
    MODE 5: FULL BATCH TRANSCRIBE
    Transcribes ALL media files in a directory using the specified model.
    """
    media_files = find_media_files(directory)
    total = len(media_files)
    print(f"[BATCH] Found {total} media files in: {directory}")
    print(f"[BATCH] VAD: {'ON' if use_vad else 'OFF (outdoor/noisy mode)'}")
    print(f"[BATCH] Loading {model_name} model (one-time)...")

    model = WhisperModel(model_name, device=DEVICE, compute_type=COMPUTE)

    transcribed = 0
    errors = 0

    if output_dir:
        os.makedirs(output_dir, exist_ok=True)

    for i, file_path in enumerate(media_files, 1):
        # Check skip existing before loading model or processing? 
        # Actually we need to calculate out_path to know if we skip.
        # But out_path logic is duplicated inside loop.
        
        safe_model = model_name.replace(".", "_").replace("-", "_")
        base_name = os.path.splitext(os.path.basename(file_path))[0]
        if output_dir:
            out_check = os.path.join(output_dir, f"{base_name}_transcript_{safe_model}.txt")
        else:
            out_check = f"{os.path.splitext(file_path)[0]}_transcript_{safe_model}.txt"
            
        if skip_existing and os.path.exists(out_check):
             print(f"\n[{i}/{total}] [SKIPPING] {file_path}")
             continue

        print(f"\n[{i}/{total}] Transcribing: {file_path}")
        try:
            transcribe_opts = dict(
                beam_size=5,
                vad_filter=use_vad,
            )
            if use_vad:
                transcribe_opts["vad_parameters"] = dict(min_silence_duration_ms=2000)

            segments, info = model.transcribe(file_path, **transcribe_opts)

            lines = []
            for s in segments:
                line = f"[{s.start:.2f} - {s.end:.2f}] {s.text.strip()}"
                lines.append(line)
                print(f"[TEXT] {s.start:.2f}|{s.end:.2f}|{s.text.strip()}")

            if lines:
                base_name = os.path.splitext(os.path.basename(file_path))[0]
                if output_dir:
                    out_path = os.path.join(output_dir, f"{base_name}_transcript_{safe_model}.txt")
                else:
                    out_path = f"{os.path.splitext(file_path)[0]}_transcript_{safe_model}.txt"
                
                with open(out_path, "w", encoding="utf-8") as f:
                    f.write(f"--- Full Transcription ({info.duration:.1f}s) ---\n")
                    f.write(f"Source: {os.path.abspath(file_path)}\n")
                    for line in lines:
                        f.write(line + "\n")
                print(f"[SAVED] {out_path}")
                transcribed += 1
            else:
                print(f"[SILENT] {file_path}")
        except Exception as e:
            print(f"[ERROR] {e}")
            errors += 1

    print(f"\n{'='*60}")
    print(f"BATCH TRANSCRIPTION COMPLETE")
    print(f"{'='*60}")
    print(f"Total files: {total}")
    print(f"Transcribed: {transcribed}")
    print(f"Errors:      {errors}")

def run_transcribe_file(file_path, model_name="large-v3", use_vad=True, output_dir=None, skip_existing=False):
    """
    MODE 6: FULL FILE TRANSCRIBE
    Transcribes a single media file with the specified model.
    """
    print(f"[STATUS] Transcribing full file: {file_path}")
    print(f"[STATUS] Model: {model_name}")

    base_name = os.path.splitext(os.path.basename(file_path))[0]
    safe_model = model_name.replace("/", "_").replace("\\", "_")
    if output_dir:
        out_check = os.path.join(output_dir, f"{base_name}_transcript_{safe_model}.txt")
    else:
        out_check = f"{os.path.splitext(file_path)[0]}_transcript_{safe_model}.txt"

    if skip_existing and os.path.exists(out_check):
        print(f"[SKIPPING] Target exists: {out_check}")
        return

    print(f"[STATUS] VAD: {'ON' if use_vad else 'OFF'}")
    print(f"[BATCH] Loading {model_name} model...")

    model = WhisperModel(model_name, device=DEVICE, compute_type=COMPUTE)

    transcribe_opts = dict(
        beam_size=5,
        vad_filter=use_vad,
    )
    if use_vad:
        transcribe_opts["vad_parameters"] = dict(min_silence_duration_ms=2000)

    segments, info = model.transcribe(file_path, **transcribe_opts)

    lines = []
    for s in segments:
        line = f"[{s.start:.2f} - {s.end:.2f}] {s.text.strip()}"
        lines.append(line)
        print(f"[TEXT] {s.start:.2f}|{s.end:.2f}|{s.text.strip()}")

    if lines:
        base_name = os.path.splitext(os.path.basename(file_path))[0]
        # Use model name in filename for versioning
        safe_model = model_name.replace("/", "_").replace("\\", "_")
        
        if output_dir:
            os.makedirs(output_dir, exist_ok=True)
            out_path = os.path.join(output_dir, f"{base_name}_transcript_{safe_model}.txt")
        else:
            out_path = f"{os.path.splitext(file_path)[0]}_transcript_{safe_model}.txt"
            
        with open(out_path, "w", encoding="utf-8") as f:
            f.write(f"--- Transcription ({model_name}, {info.duration:.1f}s) ---\n")
            f.write(f"Source: {os.path.abspath(file_path)}\n")
            for line in lines:
                f.write(line + "\n")
        print(f"[SAVED] {out_path}")
    else:
        print(f"[SILENT] {file_path}")

    print(f"\n[1/1] Transcribing: {file_path}")
    print("TRANSCRIPTION COMPLETE")

def run_search_transcripts(directory, query):
    """
    MODE 7: SEARCH TRANSCRIPTS
    Searches all _transcript.txt files for matching text.
    """
    import re
    directory = os.path.abspath(directory)
    query_lower = query.lower()
    query_words = query_lower.split()

    results = []
    transcript_files = []

    for root, dirs, files in os.walk(directory):
        for f in files:
            if f.endswith("_transcript.txt"):
                transcript_files.append(os.path.join(root, f))

    print(f"[SEARCH] Searching {len(transcript_files)} transcript files for: {query}")

    for fpath in transcript_files:
        try:
            with open(fpath, "r", encoding="utf-8") as f:
                content = f.read()

            content_lower = content.lower()

            # Score: count how many query words appear
            score = sum(1 for w in query_words if w in content_lower)
            if score == 0:
                continue

            # Find matching lines
            matching_lines = []
            for line_num, line in enumerate(content.split("\n"), 1):
                if any(w in line.lower() for w in query_words):
                    matching_lines.append((line_num, line.strip()))

            results.append({
                "file": fpath,
                "score": score,
                "total_words": len(query_words),
                "matches": len(matching_lines),
                "lines": matching_lines[:10]  # Cap at 10 matches per file
            })
        except Exception as e:
            print(f"[ERROR] {fpath}: {e}")

    # Sort by score descending
    results.sort(key=lambda r: r["score"], reverse=True)

    print(f"\n[SEARCH] Found {len(results)} matching files\n")

    for i, r in enumerate(results, 1):
        print(f"[RESULT] {i}. [{r['score']}/{r['total_words']} words] {r['file']}")
        for line_num, line in r["lines"]:
            print(f"  L{line_num}: {line}")
        print()

    # Output JSON for the app to parse
    print(f"[SEARCH_JSON] {json.dumps(results)}")


# =============================================================================
#   MODE 8: SEMANTIC SEARCH
#   Uses sentence-transformers to search transcripts by meaning.
# =============================================================================

def run_semantic_search(directories, query, model_name="all-MiniLM-L6-v2", transcript_dir=None):
    """
    MODE 8: SEMANTIC SEARCH
    Searches transcripts using embedding similarity.
    Falls back to exact search if sentence-transformers is not installed.
    """
    try:
        from sentence_transformers import SentenceTransformer, util
    except ImportError:
        print("[ERROR] sentence-transformers not installed. Run 'Install Libraries' in Settings.")
        print(json.dumps({"status": "error", "message": "sentence-transformers not installed"}))
        return

    print(f"[SEMANTIC] Loading embedding model: {model_name}...")
    try:
        embed_model = SentenceTransformer(model_name)
    except Exception as e:
        print(f"[ERROR] Failed to load model {model_name}: {e}")
        return

    # Collect all transcript files
    transcript_files = []
    search_dirs = [d for d in (directories if isinstance(directories, list) else [directories]) if d and os.path.isdir(d)]
    if transcript_dir and os.path.isdir(transcript_dir):
        search_dirs.append(transcript_dir)

    seen = set()
    for d in search_dirs:
        for root, _, files in os.walk(d):
            for f in files:
                if "_transcript" in f and f.endswith(".txt"):
                    fpath = os.path.abspath(os.path.join(root, f))
                    if fpath not in seen:
                        seen.add(fpath)
                        transcript_files.append(fpath)

    print(f"[SEMANTIC] Found {len(transcript_files)} transcript files")
    if not transcript_files:
        print(json.dumps({"status": "complete", "action": "semantic_search", "results": []}))
        return

    # Build chunks: each line with a timestamp is a chunk
    chunks = []  # (file_path, line_text)
    for fpath in transcript_files:
        try:
            with open(fpath, "r", encoding="utf-8") as f:
                for line in f:
                    line = line.strip()
                    if line and not line.startswith("---") and not line.startswith("Source:"):
                        chunks.append((fpath, line))
        except Exception:
            continue

    if not chunks:
        print("[SEMANTIC] No content found in transcripts")
        print(json.dumps({"status": "complete", "action": "semantic_search", "results": []}))
        return

    print(f"[SEMANTIC] Encoding {len(chunks)} chunks...")
    chunk_texts = [c[1] for c in chunks]

    # Encode in batches to avoid OOM
    query_embedding = embed_model.encode(query, convert_to_tensor=True)
    chunk_embeddings = embed_model.encode(chunk_texts, convert_to_tensor=True, batch_size=256, show_progress_bar=False)

    # Compute cosine similarities
    scores = util.cos_sim(query_embedding, chunk_embeddings)[0]

    # Get top results (threshold > 0.3)
    results = []
    for idx in scores.argsort(descending=True)[:100]:
        score = scores[idx].item()
        if score < 0.3:
            break
        fpath, line = chunks[idx]
        results.append({
            "file": os.path.basename(fpath),
            "full_path": fpath,
            "score": round(score, 4),
            "snippet": line
        })

    print(f"[SEMANTIC] Found {len(results)} relevant matches")
    for i, r in enumerate(results[:20], 1):
        print(f"[RESULT] {i}. [{r['score']:.2f}] {r['file']}: {r['snippet'][:100]}")

    print(f"[SEARCH_RESULTS] {json.dumps(results)}")


# =============================================================================
#   MODE 9: ANALYZE (Summarize/Outline)
#   Uses local LLM via llama-cpp-python or cloud APIs.
# =============================================================================

def run_analyze(transcript_path, action_type="summarize", provider="local",
                model_name=None, api_key=None, cloud_model=None):
    """
    MODE 9: ANALYZE
    Summarizes or outlines a transcript using LLM.
    provider: "local", "gemini", "openai", "claude"
    """
    # Read transcript
    if not os.path.exists(transcript_path):
        print(f"[ERROR] File not found: {transcript_path}")
        return

    with open(transcript_path, "r", encoding="utf-8") as f:
        content = f.read()

    # Strip headers
    lines = [l for l in content.split("\n") if l.strip() and not l.startswith("---") and not l.startswith("Source:")]
    transcript_text = "\n".join(lines)

    if not transcript_text.strip():
        print("[ERROR] Transcript is empty")
        return

    # Truncate if too long (keep first ~8k chars for context window safety)
    if len(transcript_text) > 8000:
        transcript_text = transcript_text[:8000] + "\n... (truncated)"

    if action_type == "summarize":
        prompt = f"""Please provide a concise summary of the following transcript. Include key topics discussed, main points, and any important details. Keep timestamps where relevant.

Transcript:
{transcript_text}

Summary:"""
    elif action_type == "outline":
        prompt = f"""Please create a structured outline of the following transcript. Use headings and bullet points. Include timestamps for each section.

Transcript:
{transcript_text}

Outline:"""
    elif action_type == "detect_meeting":
        prompt = f"""Analyze this transcript and determine if it contains a real conversation or meeting, or if it is hallucinated/repetitive nonsense from a speech recognition model.

Signs of HALLUCINATION: identical repeated phrases, single-word loops (e.g. "I" repeated many times), no conversational flow, no topic progression, very short repeated segments.
Signs of REAL MEETING: varied sentences, questions and answers, topic changes, multiple speakers, natural conversation flow, specific details like names/places/plans.

Respond with ONLY this JSON (no other text):
{{"has_meeting": true, "confidence": 85, "reason": "one sentence explanation"}}

The confidence field is an integer from 0 to 100 where 100 means absolute certainty.

Transcript:
{transcript_text}

JSON:"""

    print(f"[ANALYZE] {action_type.title()} using {provider}...")

    try:
        if provider == "local":
            result = _analyze_local(prompt, model_name)
        elif provider == "gemini":
            result = _analyze_gemini(prompt, api_key, cloud_model or "gemini-2.0-flash")
        elif provider == "openai":
            result = _analyze_openai(prompt, api_key, cloud_model or "gpt-4o")
        elif provider == "claude":
            result = _analyze_claude(prompt, api_key, cloud_model or "claude-sonnet-4-20250514")
        else:
            print(f"[ERROR] Unknown provider: {provider}")
            return

        if result:
            print(f"[ANALYSIS_RESULT] {json.dumps({'file': transcript_path, 'type': action_type, 'result': result})}")
        else:
            print("[ERROR] Analysis returned no result")
    except Exception as e:
        print(f"[ERROR] Analysis failed: {e}")


def _analyze_local(prompt, model_name=None):
    """Run analysis using llama-cpp-python with a GGUF model."""
    try:
        from llama_cpp import Llama
    except ImportError:
        print("[ERROR] llama-cpp-python not installed. Run 'Install Libraries' in Settings.")
        return None

    # Default model repo and file
    gguf_models = {
        "llama-3.1-8b": ("bartowski/Meta-Llama-3.1-8B-Instruct-GGUF", "Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf"),
        "mistral-7b": ("TheBloke/Mistral-7B-Instruct-v0.2-GGUF", "mistral-7b-instruct-v0.2.Q4_K_M.gguf"),
        "phi-3-mini": ("bartowski/Phi-3.1-mini-4k-instruct-GGUF", "Phi-3.1-mini-4k-instruct-Q4_K_M.gguf"),
        "qwen2-7b": ("Qwen/Qwen2-7B-Instruct-GGUF", "qwen2-7b-instruct-q4_k_m.gguf"),
        "gemma-2-2b": ("bartowski/gemma-2-2b-it-GGUF", "gemma-2-2b-it-Q4_K_M.gguf"),
    }

    if not model_name or model_name not in gguf_models:
        model_name = "phi-3-mini"  # Default: smallest, fastest

    repo_id, filename = gguf_models[model_name]

    # Download model if needed
    try:
        from huggingface_hub import hf_hub_download
        print(f"[ANALYZE] Downloading/loading {model_name} ({filename})...")
        model_path = hf_hub_download(repo_id=repo_id, filename=filename)
    except ImportError:
        print("[ERROR] huggingface-hub not installed")
        return None
    except Exception as e:
        print(f"[ERROR] Failed to download model: {e}")
        return None

    print(f"[ANALYZE] Running local inference with {model_name}...")
    try:
        llm = Llama(
            model_path=model_path,
            n_ctx=4096,
            n_gpu_layers=0,  # Force CPU — GPU may be in use by faster-whisper
            verbose=False
        )
        output = llm(
            prompt,
            max_tokens=2048,
            temperature=0.3,
            stop=["\n\n\n"],
        )
        return output["choices"][0]["text"].strip()
    except Exception as e:
        print(f"[ERROR] Local inference failed: {e}")
        return None


def _analyze_gemini(prompt, api_key, model="gemini-2.0-flash"):
    """Run analysis using Google Gemini API via openai-compatible endpoint."""
    if not api_key:
        print("[ERROR] Gemini API key required")
        return None
    try:
        from openai import OpenAI
        client = OpenAI(api_key=api_key, base_url="https://generativelanguage.googleapis.com/v1beta/openai/")
        response = client.chat.completions.create(
            model=model,
            messages=[{"role": "user", "content": prompt}],
            max_tokens=2048,
            temperature=0.3,
        )
        return response.choices[0].message.content
    except ImportError:
        print("[ERROR] openai package not installed")
        return None
    except Exception as e:
        print(f"[ERROR] Gemini API call failed: {e}")
        return None


def _analyze_openai(prompt, api_key, model="gpt-4o"):
    """Run analysis using OpenAI API."""
    if not api_key:
        print("[ERROR] OpenAI API key required")
        return None
    try:
        from openai import OpenAI
        client = OpenAI(api_key=api_key)
        response = client.chat.completions.create(
            model=model,
            messages=[{"role": "user", "content": prompt}],
            max_tokens=2048,
            temperature=0.3,
        )
        return response.choices[0].message.content
    except ImportError:
        print("[ERROR] openai package not installed")
        return None
    except Exception as e:
        print(f"[ERROR] OpenAI API call failed: {e}")
        return None


def _analyze_claude(prompt, api_key, model="claude-sonnet-4-20250514"):
    """Run analysis using Anthropic Claude API."""
    if not api_key:
        print("[ERROR] Claude API key required")
        return None
    try:
        import anthropic
        client = anthropic.Anthropic(api_key=api_key)
        response = client.messages.create(
            model=model,
            max_tokens=2048,
            messages=[{"role": "user", "content": prompt}],
        )
        return response.content[0].text
    except ImportError:
        print("[ERROR] anthropic package not installed")
        return None
    except Exception as e:
        print(f"[ERROR] Claude API call failed: {e}")
        return None


# =============================================================================
#   MODE 10: DETECT MEETINGS
#   Batch-scans transcripts using LLM to classify real vs hallucinated.
# =============================================================================

def run_detect_meetings(directory, provider="local", model_name=None,
                        api_key=None, cloud_model=None, transcript_dir=None):
    """
    MODE 10: DETECT MEETINGS
    Scans all transcript files and uses LLM to determine if each
    contains a real meeting/conversation or is hallucinated.
    """
    # Collect transcript files
    transcript_files = []
    search_dirs = []
    if directory and os.path.isdir(directory):
        search_dirs.append(directory)
    if transcript_dir and os.path.isdir(transcript_dir):
        search_dirs.append(transcript_dir)

    seen = set()
    for d in search_dirs:
        for root, _, files in os.walk(d):
            for f in files:
                if "_transcript" in f and f.endswith(".txt"):
                    fpath = os.path.abspath(os.path.join(root, f))
                    if fpath not in seen:
                        seen.add(fpath)
                        transcript_files.append(fpath)

    total = len(transcript_files)
    print(f"[DETECT] Found {total} transcript files to analyze")
    if total == 0:
        print(json.dumps({"status": "complete", "action": "detect_meetings", "results": []}))
        return

    results = []
    meetings_found = 0

    for i, fpath in enumerate(transcript_files, 1):
        fname = os.path.basename(fpath)
        print(f"\n[{i}/{total}] Analyzing: {fname}")

        try:
            with open(fpath, "r", encoding="utf-8") as f:
                content = f.read()

            # Strip headers
            lines = [l for l in content.split("\n")
                     if l.strip() and not l.startswith("---") and not l.startswith("Source:")]
            transcript_text = "\n".join(lines)

            if not transcript_text.strip():
                print(f"  [SKIP] Empty transcript")
                results.append({"file": fpath, "has_meeting": False,
                                "confidence": 100, "reason": "Empty transcript"})
                continue

            # Quick heuristic pre-filter: if >80% of lines are identical, skip LLM
            line_list = [l.strip() for l in lines if l.strip()]
            if len(line_list) > 5:
                # Extract text after timestamp brackets
                texts = []
                for l in line_list:
                    bracket_end = l.rfind("]")
                    if bracket_end >= 0:
                        texts.append(l[bracket_end+1:].strip())
                    else:
                        texts.append(l)
                unique_ratio = len(set(texts)) / len(texts) if texts else 0
                if unique_ratio < 0.15:
                    print(f"  [NO_MEETING] Repetition ratio {unique_ratio:.2f} — clearly hallucinated")
                    results.append({"file": fpath, "has_meeting": False,
                                    "confidence": 99, "reason": f"Extreme repetition (unique ratio: {unique_ratio:.2f})"})
                    continue

            # Truncate for LLM context window (detection needs less than summary)
            if len(transcript_text) > 4000:
                transcript_text = transcript_text[:4000] + "\n... (truncated)"

            # Use run_analyze infrastructure
            prompt = f"""Analyze this transcript and determine if it contains a real conversation or meeting, or if it is hallucinated/repetitive nonsense from a speech recognition model.

Signs of HALLUCINATION: identical repeated phrases, single-word loops (e.g. "I" repeated many times), no conversational flow, no topic progression, very short repeated segments.
Signs of REAL MEETING: varied sentences, questions and answers, topic changes, multiple speakers, natural conversation flow, specific details like names/places/plans.

Respond with ONLY this JSON (no other text):
{{"has_meeting": true, "confidence": 85, "reason": "one sentence explanation"}}

The confidence field is an integer from 0 to 100 where 100 means absolute certainty.

Transcript:
{transcript_text}

JSON:"""

            # Call LLM
            if provider == "local":
                result_text = _analyze_local(prompt, model_name)
            elif provider == "gemini":
                result_text = _analyze_gemini(prompt, api_key, cloud_model or "gemini-2.0-flash")
            elif provider == "openai":
                result_text = _analyze_openai(prompt, api_key, cloud_model or "gpt-4o")
            elif provider == "claude":
                result_text = _analyze_claude(prompt, api_key, cloud_model or "claude-sonnet-4-20250514")
            else:
                print(f"  [ERROR] Unknown provider: {provider}")
                continue

            if not result_text:
                print(f"  [ERROR] LLM returned no result")
                results.append({"file": fpath, "has_meeting": False,
                                "confidence": 0, "reason": "LLM returned no result"})
                continue

            # Parse JSON from LLM response
            try:
                # Try to extract JSON from response (LLM may add extra text)
                json_start = result_text.find("{")
                json_end = result_text.rfind("}") + 1
                if json_start >= 0 and json_end > json_start:
                    parsed = json.loads(result_text[json_start:json_end])
                else:
                    parsed = json.loads(result_text)

                has_meeting = parsed.get("has_meeting", False)
                confidence = int(parsed.get("confidence", 50))
                # Normalize if LLM returned 0.0-1.0 instead of 0-100
                if isinstance(parsed.get("confidence"), float) and parsed["confidence"] <= 1.0:
                    confidence = int(parsed["confidence"] * 100)
                reason = parsed.get("reason", "")
            except json.JSONDecodeError:
                # Fallback: look for keywords in response
                has_meeting = "true" in result_text.lower() and "has_meeting" in result_text.lower()
                confidence = 50
                reason = f"Could not parse JSON: {result_text[:100]}"

            tag = "MEETING_DETECTED" if has_meeting else "NO_MEETING"
            print(f"  [{tag}] confidence={confidence} — {reason}")

            if has_meeting:
                meetings_found += 1

            results.append({
                "file": fpath,
                "has_meeting": has_meeting,
                "confidence": confidence,
                "reason": reason
            })

        except Exception as e:
            print(f"  [ERROR] {e}")
            results.append({"file": fpath, "has_meeting": False,
                            "confidence": 0, "reason": str(e)})

    print(f"\n{'='*60}")
    print(f"MEETING DETECTION COMPLETE")
    print(f"{'='*60}")
    print(f"Total transcripts: {total}")
    print(f"Meetings found: {meetings_found}")
    print(f"Hallucinated: {total - meetings_found}")

    if meetings_found > 0:
        print(f"\n--- Files with Real Meetings ---")
        for r in results:
            if r["has_meeting"]:
                print(f"  ✅ {os.path.basename(r['file'])} ({r['confidence']}%) — {r['reason']}")

    print(f"\n[DETECTION_REPORT] {json.dumps(results)}")

if __name__ == "__main__":
    multiprocessing.freeze_support()  # Needed for PyInstaller on Windows
    parser = argparse.ArgumentParser(description="Fast Whisper Voice Scanner & Transcriber")
    parser.add_argument("mode", choices=["scan", "batch_scan", "vad_scan", "transcribe", "batch_transcribe", "batch_transcribe_dir", "transcribe_file", "search_transcripts", "semantic_search", "analyze", "detect_meetings", "server"])
    parser.add_argument("file", nargs="?", help="Path to media file (for scan/transcribe)")
    parser.add_argument("--dir", help="Directory to batch scan or transcribe")
    parser.add_argument("--start", type=float)
    parser.add_argument("--end", type=float)
    parser.add_argument("--no-vad", action="store_true", help="Disable VAD filter (for outdoor/noisy recordings)")
    parser.add_argument("--report", help="Path to scan report JSON (for batch_transcribe)")
    parser.add_argument("--model", default="large-v1", help="Whisper model to use (e.g. tiny.en, base.en, small.en, medium.en, large-v1, large-v3)")
    parser.add_argument("--query", help="Search query for search_transcripts mode")
    parser.add_argument("--output-dir", help="Directory to save transcript files")
    parser.add_argument("--skip-existing", action="store_true", help="Skip files if transcript already exists")
    parser.add_argument("--device", choices=["auto", "cuda", "cpu"], default="auto", help="Device to use: auto, cuda, or cpu")
    parser.add_argument("--embed-model", default="all-MiniLM-L6-v2", help="Sentence-transformers model for semantic search")
    parser.add_argument("--transcript-dir", help="Directory containing transcript files")
    parser.add_argument("--provider", choices=["local", "gemini", "openai", "claude"], default="local", help="LLM provider")
    parser.add_argument("--api-key", help="API key for cloud LLM")
    parser.add_argument("--cloud-model", help="Cloud model name")
    parser.add_argument("--analyze-type", choices=["summarize", "outline", "detect_meeting"], default="summarize", help="Analysis type")
    parser.add_argument("--vad-threshold", type=float, default=0.5, help="Silero VAD sensitivity threshold (0.0-1.0, lower = more sensitive)")
    args = parser.parse_args()

    # Apply device override before any model loading
    def apply_device_override(device_arg):
        global DEVICE, COMPUTE
        DEVICE, COMPUTE = get_device_config(device_arg)
    apply_device_override(args.device)

    use_vad = not args.no_vad

    if args.mode == "scan":
        run_scanner(args.file, use_vad=use_vad)
    elif args.mode == "batch_scan":
        directory = args.dir or args.file
        if not directory:
            print("Error: Provide a directory with --dir or as positional argument")
            exit(1)
        run_batch_scanner(directory, use_vad=use_vad, report_path=args.report)
    elif args.mode == "vad_scan":
        directory = args.dir or args.file
        if not directory:
            print("Error: Provide a directory with --dir or as positional argument")
            exit(1)
        run_batch_vad_scan(directory, threshold=args.vad_threshold, report_path=args.report, skip_existing=args.skip_existing)
    elif args.mode == "transcribe":
        run_transcriber(args.file, args.start, args.end, output_dir=args.output_dir, skip_existing=args.skip_existing)
    elif args.mode == "batch_transcribe":
        run_batch_transcriber(args.report, output_dir=args.output_dir, skip_existing=args.skip_existing, model_name=args.model)
    elif args.mode == "batch_transcribe_dir":
        directory = args.dir or args.file
        if not directory:
            print("Error: Provide a directory with --dir or as positional argument")
            exit(1)
        run_batch_transcribe_dir(directory, use_vad=use_vad, output_dir=args.output_dir, skip_existing=args.skip_existing, model_name=args.model)
    elif args.mode == "transcribe_file":
        if not args.file:
            print("Error: Provide a file path")
            exit(1)
        run_transcribe_file(args.file, model_name=args.model, use_vad=use_vad, output_dir=args.output_dir, skip_existing=args.skip_existing)
    elif args.mode == "search_transcripts":
        directory = args.dir or "."
        if not args.query:
            print("Error: Provide a search query with --query")
            exit(1)
        run_search_transcripts(directory, args.query)
    elif args.mode == "semantic_search":
        directory = args.dir or "."
        if not args.query:
            print("Error: Provide a search query with --query")
            exit(1)
        run_semantic_search(directory, args.query, model_name=args.embed_model, transcript_dir=args.transcript_dir)
    elif args.mode == "analyze":
        if not args.file:
            print("Error: Provide a transcript file path")
            exit(1)
        run_analyze(args.file, action_type=args.analyze_type, provider=args.provider,
                    model_name=args.model, api_key=args.api_key, cloud_model=args.cloud_model)
    elif args.mode == "detect_meetings":
        directory = args.dir or args.file or "."
        run_detect_meetings(directory, provider=args.provider,
                           model_name=args.model, api_key=args.api_key,
                           cloud_model=args.cloud_model, transcript_dir=args.transcript_dir)
    elif args.mode == "server":
        run_server()

def run_server():
    print("[SERVER] Initializing engine...", flush=True)
    
    # Pre-load Tiny model for fast scanning
    print("[SERVER] Loading core models...", flush=True)
    try:
        # Load tiny model to cache it in VRAM/RAM
        scanner_model = WhisperModel("tiny.en", device=DEVICE, compute_type=COMPUTE)
        print("[SERVER] Engine ready.", flush=True)
    except Exception as e:
        print(f"[SERVER] Init failed: {e}", flush=True)
        return

    # Keep large model in memory if triggered? For now, load on demand to save VRAM? 
    # Or maybe keep one global 'current_model' and swap if needed.
    current_model = scanner_model
    current_model_name = "tiny.en"

    while True:
        try:
            line = sys.stdin.readline()
            if not line: break
            
            cmd = json.loads(line)
            action = cmd.get("action")
            
            if action == "ping":
                print(json.dumps({"status": "pong"}), flush=True)
                
            elif action == "scan":
                # Ensure tiny model is loaded
                if current_model_name != "tiny.en":
                     print("[SERVER] Switching to tiny.en model...", flush=True)
                     current_model = WhisperModel("tiny.en", device=DEVICE, compute_type=COMPUTE)
                     current_model_name = "tiny.en"
                
                directory = cmd.get("directory")
                use_vad = cmd.get("use_vad", True)
                report_path = cmd.get("report_path")
                
                run_batch_scanner(directory, use_vad=use_vad, report_path=report_path, model=current_model)
                print(json.dumps({"status": "complete", "action": "scan"}), flush=True)

            elif action == "vad_scan":
                directory = cmd.get("directory")
                threshold = cmd.get("vad_threshold", 0.5)
                report_path = cmd.get("report_path")
                skip_existing = cmd.get("skip_existing", False)
                
                run_batch_vad_scan(directory, threshold=threshold, report_path=report_path, skip_existing=skip_existing)
                print(json.dumps({"status": "complete", "action": "vad_scan"}), flush=True)

            elif action == "transcribe":
                # Ensure large-v3 (or requested model) is loaded
                model_name = cmd.get("model", "large-v3")
                if current_model_name != model_name:
                    print(f"[SERVER] Switching to {model_name} model...", flush=True)
                    current_model = WhisperModel(model_name, device=DEVICE, compute_type=COMPUTE)
                    current_model_name = model_name
                
                file_path = cmd.get("file")
                start = cmd.get("start")
                end = cmd.get("end")
                output_dir = cmd.get("output_dir")
                skip_existing = cmd.get("skip_existing", False)
                
                run_transcriber(file_path, start, end, model=current_model, output_dir=output_dir, skip_existing=skip_existing)
                print(json.dumps({"status": "complete", "action": "transcribe"}), flush=True) 

            elif action == "semantic_search":
                query = cmd.get("query", "")
                directory = cmd.get("directory", ".")
                embed_model_name = cmd.get("embed_model", "all-MiniLM-L6-v2")
                transcript_dir = cmd.get("transcript_dir")
                run_semantic_search(directory, query, model_name=embed_model_name, transcript_dir=transcript_dir)
                print(json.dumps({"status": "complete", "action": "semantic_search"}), flush=True)

            elif action == "analyze":
                file_path = cmd.get("file", "")
                analyze_type = cmd.get("analyze_type", "summarize")
                provider = cmd.get("provider", "local")
                model_name_llm = cmd.get("model", None)
                api_key = cmd.get("api_key", None)
                cloud_model = cmd.get("cloud_model", None)
                run_analyze(file_path, action_type=analyze_type, provider=provider,
                            model_name=model_name_llm, api_key=api_key, cloud_model=cloud_model)
                print(json.dumps({"status": "complete", "action": "analyze"}), flush=True)

            elif action == "detect_meetings":
                directory = cmd.get("directory", ".")
                provider = cmd.get("provider", "local")
                model_name_llm = cmd.get("model", None)
                api_key = cmd.get("api_key", None)
                cloud_model = cmd.get("cloud_model", None)
                transcript_dir = cmd.get("transcript_dir", None)
                run_detect_meetings(directory, provider=provider,
                                    model_name=model_name_llm, api_key=api_key,
                                    cloud_model=cloud_model, transcript_dir=transcript_dir)
                print(json.dumps({"status": "complete", "action": "detect_meetings"}), flush=True)

            elif action == "exit":
                break
                
        except json.JSONDecodeError:
            print(f"[ERROR] Invalid JSON command", flush=True)
        except Exception as e:
            print(f"[ERROR] {e}", flush=True)