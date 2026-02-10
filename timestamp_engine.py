"""
Timestamp Engine — Extract burned-in timestamps from video files using Qwen2.5-VL-7B.
Standalone script for the TurboScribe Timestamps tab.

Usage:
  python timestamp_engine.py <video_file> [--num-frames 5] [--crop-ratio 0.08]
  python timestamp_engine.py --batch-folder <folder> [--crop-ratio 0.08]
"""

import argparse
import json
import os
import sys
import subprocess
import tempfile
import shutil

# Force unbuffered output for real-time UI updates
sys.stdout.reconfigure(encoding='utf-8', line_buffering=True)

# Globals for lazy-loaded VLM
_vlm_model = None
_vlm_processor = None


def _load_vlm():
    """Load Qwen2.5-VL-7B model (cached after first load)."""
    global _vlm_model, _vlm_processor
    if _vlm_model is not None:
        return _vlm_model, _vlm_processor

    print("[TIMESTAMP] Loading Qwen2.5-VL-7B-Instruct model...")
    print("[TIMESTAMP] First run will download ~15GB. This is a one-time operation.")

    try:
        import torch
        from transformers import Qwen2_5_VLForConditionalGeneration, AutoProcessor
    except ImportError:
        print("[ERROR] Required packages not installed. Run:")
        print("[ERROR]   pip install transformers accelerate qwen-vl-utils torchvision Pillow")
        return None, None

    try:
        import torch
        model_name = "Qwen/Qwen2.5-VL-7B-Instruct"

        # Check CUDA compatibility — RTX 50-series (sm_120) needs cu128+
        use_cuda = torch.cuda.is_available()
        if use_cuda:
            cap = torch.cuda.get_device_capability()
            max_supported = (9, 0)  # cu126 supports up to sm_90
            if cap > max_supported:
                # Check if this PyTorch build actually supports the GPU
                try:
                    torch.zeros(1, device="cuda")  # Quick smoke test
                except RuntimeError:
                    print(f"[TIMESTAMP] GPU sm_{cap[0]}{cap[1]}0 not supported by this PyTorch build — falling back to CPU.")
                    print("[TIMESTAMP] To use GPU, reinstall VLM deps with the latest CUDA support.")
                    use_cuda = False

        dtype = torch.bfloat16 if use_cuda else torch.float32
        device_map = "auto" if use_cuda else "cpu"
        print(f"[TIMESTAMP] Loading model on {'GPU (CUDA)' if use_cuda else 'CPU'}...")

        _vlm_model = Qwen2_5_VLForConditionalGeneration.from_pretrained(
            model_name,
            torch_dtype=dtype,
            device_map=device_map,
        )
        _vlm_processor = AutoProcessor.from_pretrained(model_name)
        print("[TIMESTAMP] Model loaded successfully.")
        return _vlm_model, _vlm_processor
    except Exception as e:
        print(f"[ERROR] Failed to load VLM: {e}")
        return None, None


def _extract_frames(video_path, num_frames=5):
    """Extract evenly-spaced frames from a video file using ffmpeg."""
    # Get video duration via ffprobe
    probe_cmd = [
        "ffprobe", "-v", "error",
        "-show_entries", "format=duration",
        "-of", "csv=p=0", video_path
    ]
    try:
        duration = float(subprocess.check_output(probe_cmd, stderr=subprocess.DEVNULL).decode().strip())
    except Exception as e:
        print(f"[ERROR] ffprobe failed: {e}")
        print("[ERROR] Make sure ffmpeg/ffprobe is installed and on PATH.")
        return [], 0

    if duration <= 0:
        print("[ERROR] Video has zero duration")
        return [], 0

    print(f"[TIMESTAMP] Video duration: {duration:.1f}s")

    # Calculate timestamps for evenly spaced frames
    if num_frames == 1:
        timestamps = [duration / 2]
    elif num_frames == 2:
        # Batch mode: first + last frame (avoid black frames at edges)
        start = min(2.0, duration * 0.05)
        end = max(duration - 2.0, duration * 0.95)
        timestamps = [start, end]
    else:
        # Avoid first/last 1 second to skip potential black frames
        start = min(1.0, duration * 0.05)
        end = max(duration - 1.0, duration * 0.95)
        step = (end - start) / (num_frames - 1)
        timestamps = [start + i * step for i in range(num_frames)]

    frames = []
    tmp_dir = tempfile.mkdtemp(prefix="ts_frames_")

    for i, ts in enumerate(timestamps):
        out_path = os.path.join(tmp_dir, f"frame_{i:03d}.jpg")
        cmd = [
            "ffmpeg", "-y", "-ss", f"{ts:.2f}",
            "-i", video_path,
            "-vframes", "1", "-q:v", "2",
            out_path
        ]
        try:
            subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=True)
            if os.path.exists(out_path) and os.path.getsize(out_path) > 0:
                frames.append({"path": out_path, "timestamp_sec": ts})
            else:
                print(f"[WARN] Frame at {ts:.1f}s produced empty file")
        except Exception as e:
            print(f"[WARN] Frame at {ts:.1f}s failed: {e}")

    return frames, duration


def _crop_timestamp_region(image_path, crop_ratio=0.08):
    """Crop the top portion of an image where timestamps typically appear."""
    try:
        from PIL import Image
        img = Image.open(image_path)
        w, h = img.size
        crop_h = int(h * crop_ratio)
        if crop_h < 10:
            crop_h = 10
        cropped = img.crop((0, 0, w, crop_h))
        cropped_path = image_path.replace(".jpg", "_crop.jpg")
        cropped.save(cropped_path, quality=95)
        return cropped_path
    except ImportError:
        print("[WARN] Pillow not installed, using full frame")
        return image_path
    except Exception as e:
        print(f"[WARN] Crop failed: {e}, using full frame")
        return image_path


def _read_timestamp_from_image(model, processor, image_path):
    """Use VLM to read timestamp text from an image."""
    try:
        from PIL import Image
        from qwen_vl_utils import process_vision_info

        image = Image.open(image_path)

        prompt = (
            "Read the exact timestamp and camera name shown in this image. "
            "Return ONLY the timestamp text in the format it appears, nothing else. "
            "If no timestamp is visible, respond with 'NONE'."
        )

        messages = [
            {
                "role": "user",
                "content": [
                    {"type": "image", "image": image},
                    {"type": "text", "text": prompt},
                ],
            }
        ]

        text = processor.apply_chat_template(messages, tokenize=False, add_generation_prompt=True)
        image_inputs, video_inputs = process_vision_info(messages)
        inputs = processor(
            text=[text],
            images=image_inputs,
            videos=video_inputs,
            padding=True,
            return_tensors="pt",
        ).to(model.device)

        generated_ids = model.generate(**inputs, max_new_tokens=128)
        generated_ids_trimmed = [
            out_ids[len(in_ids):] for in_ids, out_ids in zip(inputs.input_ids, generated_ids)
        ]
        output_text = processor.batch_decode(
            generated_ids_trimmed, skip_special_tokens=True, clean_up_tokenization_spaces=False
        )[0].strip()

        return output_text

    except Exception as e:
        print(f"[ERROR] VLM inference failed: {e}")
        return "ERROR"


def _find_consensus(timestamps):
    """Find consensus among extracted timestamps using majority voting."""
    from collections import Counter

    valid = [t for t in timestamps if t and t != "NONE" and t != "ERROR"]
    if not valid:
        return None

    counter = Counter(valid)
    most_common = counter.most_common(1)[0][0]
    return most_common


def run_extract_timestamps(file_path, num_frames=5, crop_ratio=0.08):
    """
    Extract burned-in timestamps from video frames using Qwen2.5-VL.
    Samples multiple frames and uses majority voting for accuracy.
    """
    if not os.path.exists(file_path):
        print(f"[ERROR] File not found: {file_path}")
        return

    print(f"[TIMESTAMP] Processing: {file_path}")
    print(f"[TIMESTAMP] Extracting {num_frames} frames...")

    # Step 1: Extract frames
    frames, duration = _extract_frames(file_path, num_frames)
    if not frames:
        print("[ERROR] No frames could be extracted. Is ffmpeg installed?")
        return

    print(f"[TIMESTAMP] Extracted {len(frames)} frames")

    # Step 2: Load VLM
    model, processor = _load_vlm()
    if model is None:
        return

    # Step 3: Process each frame
    results = []
    for i, frame in enumerate(frames):
        ts_sec = frame["timestamp_sec"]
        print(f"[TIMESTAMP] Reading frame {i+1}/{len(frames)} (at {ts_sec:.1f}s)...")

        # Crop timestamp region
        cropped = _crop_timestamp_region(frame["path"], crop_ratio)

        # Read timestamp with VLM
        raw_text = _read_timestamp_from_image(model, processor, cropped)

        confidence = "high" if raw_text and raw_text != "NONE" and raw_text != "ERROR" else "low"
        results.append({
            "frame_sec": round(ts_sec, 1),
            "raw_text": raw_text,
            "confidence": confidence,
        })
        print(f"[TIMESTAMP] Frame {i+1}: {raw_text} ({confidence})")

    # Step 4: Find consensus
    all_texts = [r["raw_text"] for r in results]
    consensus = _find_consensus(all_texts)

    # Build output
    high_conf = [r for r in results if r["confidence"] == "high"]
    output = {
        "file": os.path.abspath(file_path),
        "timestamps": results,
        "consensus": consensus,
        "frames_extracted": len(frames),
        "frames_readable": len(high_conf),
        "video_duration_sec": round(duration, 1),
    }

    if len(high_conf) >= 2:
        output["first_timestamp"] = high_conf[0]["raw_text"]
        output["last_timestamp"] = high_conf[-1]["raw_text"]

    print(f"\n[TIMESTAMP] === Results ===")
    print(f"[TIMESTAMP] Consensus: {consensus}")
    print(f"[TIMESTAMP] Readable frames: {len(high_conf)}/{len(frames)}")

    # Output JSON for the C# app to parse
    print(f"[TIMESTAMP_RESULT] {json.dumps(output)}")

    # Cleanup temp frames
    for frame in frames:
        tmp_dir = os.path.dirname(frame["path"])
        if "ts_frames_" in tmp_dir:
            try:
                shutil.rmtree(tmp_dir)
            except Exception:
                pass
            break

    return output


def run_batch_rename(folder_path, crop_ratio=0.08, recursive=False, prefix=None):
    """
    Process all .mp4 files in a folder, extracting start/end timestamps
    for batch renaming. Uses only 2 frames per video for speed.
    """
    import glob

    if not os.path.isdir(folder_path):
        print(f"[ERROR] Folder not found: {folder_path}")
        return

    if recursive:
        mp4_files = sorted(glob.glob(os.path.join(folder_path, "**", "*.mp4"), recursive=True))
        scope = f"{folder_path} (including subfolders)"
    else:
        mp4_files = sorted(glob.glob(os.path.join(folder_path, "*.mp4")))
        scope = folder_path

    # Filter by prefix if specified
    if prefix:
        mp4_files = [f for f in mp4_files if os.path.basename(f).lower().startswith(prefix.lower())]
        scope += f" [prefix: {prefix}]"

    if not mp4_files:
        print(f"[ERROR] No .mp4 files found in {scope}")
        return

    print(f"[BATCH] Found {len(mp4_files)} video(s) in {scope}")

    # Pre-load VLM once for all videos
    model, processor = _load_vlm()
    if model is None:
        print("[ERROR] Cannot load VLM model — aborting batch.")
        return

    for idx, video_path in enumerate(mp4_files):
        filename = os.path.basename(video_path)
        print(f"\n[BATCH] Processing {idx+1}/{len(mp4_files)}: {filename}")

        # Extract only 2 frames: first + last
        frames, duration = _extract_frames(video_path, num_frames=2)
        if len(frames) < 2:
            print(f"[BATCH] Skipping {filename} — could not extract 2 frames")
            result = {
                "file": filename,
                "path": os.path.abspath(video_path),
                "start_timestamp": None,
                "end_timestamp": None,
                "error": "Could not extract frames"
            }
            print(f"[BATCH_RESULT] {json.dumps(result)}")
            # Cleanup
            _cleanup_frames(frames)
            continue

        start_text = None
        end_text = None

        for i, frame in enumerate(frames):
            cropped = _crop_timestamp_region(frame["path"], crop_ratio)
            raw = _read_timestamp_from_image(model, processor, cropped)
            if raw and raw != "NONE" and raw != "ERROR":
                if i == 0:
                    start_text = raw
                else:
                    end_text = raw
            label = "start" if i == 0 else "end"
            print(f"[BATCH]   {label}: {raw}")

        result = {
            "file": filename,
            "path": os.path.abspath(video_path),
            "start_timestamp": start_text,
            "end_timestamp": end_text,
            "duration_sec": round(duration, 1)
        }
        print(f"[BATCH_RESULT] {json.dumps(result)}")

        # Cleanup temp frames
        _cleanup_frames(frames)

    print(f"\n[BATCH] Done — processed {len(mp4_files)} videos.")


def _cleanup_frames(frames):
    """Remove temporary frame files."""
    for frame in frames:
        tmp_dir = os.path.dirname(frame["path"])
        if "ts_frames_" in tmp_dir:
            try:
                shutil.rmtree(tmp_dir)
            except Exception:
                pass
            break


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Extract burned-in timestamps from video files using Qwen2.5-VL-7B")
    parser.add_argument("file", nargs="?", help="Path to video file")
    parser.add_argument("--batch-folder", help="Process all .mp4 files in a folder (batch rename mode)")
    parser.add_argument("--recursive", action="store_true", help="Include subfolders when using --batch-folder")
    parser.add_argument("--prefix", help="Only process files starting with this prefix (e.g., 'reo')")
    parser.add_argument("--num-frames", type=int, default=5, help="Number of frames to extract (default: 5)")
    parser.add_argument("--crop-ratio", type=float, default=0.08, help="Fraction of frame height to crop from top (default: 0.08)")
    args = parser.parse_args()

    if args.batch_folder:
        run_batch_rename(args.batch_folder, crop_ratio=args.crop_ratio, recursive=args.recursive, prefix=args.prefix)
    elif args.file:
        run_extract_timestamps(args.file, num_frames=args.num_frames, crop_ratio=args.crop_ratio)
    else:
        parser.print_help()
