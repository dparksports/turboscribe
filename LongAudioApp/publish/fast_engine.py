import argparse
import torch
from faster_whisper import WhisperModel
import os
import json
from datetime import datetime

# RTX 5090 Settings
DEVICE = "cuda"
COMPUTE = "float16"

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

def run_batch_scanner(directory, use_vad=True):
    """
    MODE 3: BATCH SCOUT
    Scans all media files in a directory for voice activity.
    Outputs a JSON report with detected speech blocks.
    """
    media_files = find_media_files(directory)
    total = len(media_files)
    print(f"[BATCH] Found {total} media files in: {directory}")
    print(f"[BATCH] VAD: {'ON' if use_vad else 'OFF (outdoor/noisy mode)'}")
    print(f"[BATCH] Loading tiny.en model (one-time)...")

    model = WhisperModel("tiny.en", device=DEVICE, compute_type=COMPUTE)

    results = []
    files_with_voice = 0

    for i, file_path in enumerate(media_files, 1):
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

        except Exception as e:
            print(f"  [ERROR] {e}")
            results.append({
                "file": file_path,
                "error": str(e)
            })

    # Write JSON report
    report_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "voice_scan_results.json")
    report = {
        "scan_date": datetime.now().isoformat(),
        "directory": directory,
        "total_files": total,
        "files_with_voice": files_with_voice,
        "results": results
    }

    with open(report_path, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2, ensure_ascii=False)

    # Print summary
    print(f"\n{'='*60}")
    print(f"SCAN COMPLETE")
    print(f"{'='*60}")
    print(f"Total files scanned: {total}")
    print(f"Files with voice:    {files_with_voice}")
    print(f"Report saved to:     {report_path}")

    if results:
        print(f"\n--- Files with Detected Voice ---")
        for r in results:
            if "error" not in r and r.get("blocks"):
                print(f"\n  {r['file']}")
                for b in r["blocks"]:
                    print(f"    [{b['start']:.1f}s - {b['end']:.1f}s]")
                for cmd in r["transcribe_cmds"]:
                    print(f"    > {cmd}")

def run_transcriber(file_path, start, end, model=None):
    """
    MODE 2: SNIPER (Accuracy)
    Extracts the specific meeting and applies Large-v3.
    Saves transcription to a .txt file next to the source media.
    """
    print(f"[STATUS] Transcribing: {file_path}")
    print(f"[STATUS] Range: {start:.1f}s - {end:.1f}s")

    if model is None:
        model = WhisperModel("large-v3", device=DEVICE, compute_type=COMPUTE)

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

    # Save to file next to the media
    base = os.path.splitext(file_path)[0]
    out_path = f"{base}_transcript.txt"

    # Append if file exists (multiple blocks for same file)
    mode = "a" if os.path.exists(out_path) else "w"
    with open(out_path, mode, encoding="utf-8") as f:
        f.write(f"--- Transcription [{start:.1f}s - {end:.1f}s] ---\n")
        for line in lines:
            f.write(line + "\n")
        f.write("\n")

    print(f"[SAVED] {out_path}")
    return lines

def run_batch_transcriber(report_path=None):
    """
    MODE 4: BATCH SNIPER
    Reads the scan report and transcribes all detected voice segments
    using large-v3. Saves transcriptions next to source files.
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
    print(f"[BATCH] Loading large-v3 model (one-time)...")

    model = WhisperModel("large-v3", device=DEVICE, compute_type=COMPUTE)

    block_num = 0
    for i, entry in enumerate(voice_files, 1):
        file_path = entry["file"]
        print(f"\n[{i}/{len(voice_files)}] {file_path}")

        for b in entry["blocks"]:
            block_num += 1
            print(f"  Block {block_num}/{total_blocks}: {b['start']:.1f}s - {b['end']:.1f}s")
            try:
                run_transcriber(file_path, b["start"], b["end"], model=model)
            except Exception as e:
                print(f"  [ERROR] {e}")

    print(f"\n{'='*60}")
    print(f"BATCH TRANSCRIPTION COMPLETE")
    print(f"{'='*60}")
    print(f"Files processed: {len(voice_files)}")
    print(f"Blocks transcribed: {block_num}")

def run_batch_transcribe_dir(directory, use_vad=True):
    """
    MODE 5: FULL BATCH TRANSCRIBE
    Transcribes ALL media files in a directory using large-v3.
    No scan step needed — processes every file start-to-finish.
    """
    media_files = find_media_files(directory)
    total = len(media_files)
    print(f"[BATCH] Found {total} media files in: {directory}")
    print(f"[BATCH] VAD: {'ON' if use_vad else 'OFF (outdoor/noisy mode)'}")
    print(f"[BATCH] Loading large-v3 model (one-time)...")

    model = WhisperModel("large-v3", device=DEVICE, compute_type=COMPUTE)

    transcribed = 0
    errors = 0

    for i, file_path in enumerate(media_files, 1):
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
                base = os.path.splitext(file_path)[0]
                out_path = f"{base}_transcript.txt"
                with open(out_path, "w", encoding="utf-8") as f:
                    f.write(f"--- Full Transcription ({info.duration:.1f}s) ---\n")
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

def run_transcribe_file(file_path, model_name="large-v3", use_vad=True):
    """
    MODE 6: FULL FILE TRANSCRIBE
    Transcribes a single media file with the specified model.
    """
    print(f"[STATUS] Transcribing full file: {file_path}")
    print(f"[STATUS] Model: {model_name}")
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
        base = os.path.splitext(file_path)[0]
        # Use model name in filename for versioning
        safe_model = model_name.replace("/", "_").replace("\\", "_")
        out_path = f"{base}_transcript_{safe_model}.txt"
        with open(out_path, "w", encoding="utf-8") as f:
            f.write(f"--- Transcription ({model_name}, {info.duration:.1f}s) ---\n")
            for line in lines:
                f.write(line + "\n")
        print(f"[SAVED] {out_path}")
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

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Fast Whisper Voice Scanner & Transcriber")
    parser.add_argument("mode", choices=["scan", "batch_scan", "transcribe", "batch_transcribe", "batch_transcribe_dir", "transcribe_file", "search_transcripts"])
    parser.add_argument("file", nargs="?", help="Path to media file (for scan/transcribe)")
    parser.add_argument("--dir", help="Directory to batch scan or transcribe")
    parser.add_argument("--start", type=float)
    parser.add_argument("--end", type=float)
    parser.add_argument("--no-vad", action="store_true", help="Disable VAD filter (for outdoor/noisy recordings)")
    parser.add_argument("--report", help="Path to scan report JSON (for batch_transcribe)")
    parser.add_argument("--model", default="large-v3", help="Whisper model to use (e.g. tiny.en, base.en, small.en, medium.en, large-v3)")
    parser.add_argument("--query", help="Search query for search_transcripts mode")
    args = parser.parse_args()

    use_vad = not args.no_vad

    if args.mode == "scan":
        run_scanner(args.file, use_vad=use_vad)
    elif args.mode == "batch_scan":
        directory = args.dir or args.file
        if not directory:
            print("Error: Provide a directory with --dir or as positional argument")
            exit(1)
        run_batch_scanner(directory, use_vad=use_vad)
    elif args.mode == "transcribe":
        run_transcriber(args.file, args.start, args.end)
    elif args.mode == "batch_transcribe":
        run_batch_transcriber(args.report)
    elif args.mode == "batch_transcribe_dir":
        directory = args.dir or args.file
        if not directory:
            print("Error: Provide a directory with --dir or as positional argument")
            exit(1)
        run_batch_transcribe_dir(directory, use_vad=use_vad)
    elif args.mode == "transcribe_file":
        if not args.file:
            print("Error: Provide a file path")
            exit(1)
        run_transcribe_file(args.file, model_name=args.model, use_vad=use_vad)
    elif args.mode == "search_transcripts":
        directory = args.dir or "."
        if not args.query:
            print("Error: Provide a search query with --query")
            exit(1)
        run_search_transcripts(directory, args.query)