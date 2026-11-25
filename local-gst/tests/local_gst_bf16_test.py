#!/usr/bin/env python3
"""Extract or read subtitles, then translate them locally with MADLAD-400-3B-MT."""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
from dataclasses import dataclass
from pathlib import Path


TIMING_RE = re.compile(
    r"^(\d{2}:\d{2}:\d{2},\d{3}\s+-->\s+\d{2}:\d{2}:\d{2},\d{3})$"
)
BLOCK_SEPARATOR_RE = re.compile(r"\n{2,}")
TEXT_SUBTITLE_CODECS = {None, "subrip", "ass", "ssa", "webvtt", "mov_text", "text", "srt"}
LANGUAGE_SUFFIXES = {
    "en", "eng", "english", "no", "nor", "nob", "nno", "norwegian", "nb", "nn",
    "sv", "swe", "da", "dan", "de", "deu", "ger", "fr", "fra", "fre", "es", "spa",
    "it", "ita", "fi", "fin",
}


@dataclass
class Cue:
    number: str
    timing: str
    text: str


@dataclass
class SubtitleTrack:
    stream_index: int
    language: str
    title: str | None
    codec: str | None


def read_srt(path: Path) -> tuple[list[Cue], str]:
    raw = path.read_text(encoding="utf-8-sig")
    newline = "\r\n" if "\r\n" in raw else "\n"
    normalized = raw.replace("\r\n", "\n").replace("\r", "\n").strip("\n")
    if not normalized.strip():
        raise ValueError(f"SRT-filen er tom: {path}")

    cues: list[Cue] = []
    for block_number, block in enumerate(BLOCK_SEPARATOR_RE.split(normalized), start=1):
        parts = block.split("\n", 2)
        if len(parts) != 3 or not parts[0].strip().isdigit() or not TIMING_RE.fullmatch(parts[1].strip()):
            raise ValueError(
                f"Ugyldig SRT-blokk {block_number}. Forventet cue-nummer, tidslinje og tekst."
            )
        cues.append(Cue(parts[0].strip(), parts[1].strip(), parts[2]))
    return cues, newline


def write_srt(path: Path, cues: list[Cue], newline: str) -> None:
    content = "\n\n".join(f"{cue.number}\n{cue.timing}\n{cue.text}" for cue in cues)
    content = content.replace("\n", newline) + newline
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary_path: Path | None = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="w", encoding="utf-8", newline="", dir=path.parent,
            prefix=f".{path.name}.", suffix=".tmp", delete=False
        ) as handle:
            temporary_path = Path(handle.name)
            handle.write(content)
        os.replace(temporary_path, path)
    finally:
        if temporary_path is not None and temporary_path.exists():
            temporary_path.unlink()


def emit_event(event: dict[str, object]) -> None:
    print(json.dumps(event, ensure_ascii=False), flush=True)


def find_tool(name: str, candidates: list[Path]) -> str | None:
    for candidate in candidates:
        if candidate.is_file():
            return str(candidate)
    return shutil.which(name)


def discover_tracks(media_path: Path, ffprobe_path: str) -> list[SubtitleTrack]:
    result = subprocess.run(
        [
            ffprobe_path, "-v", "error", "-select_streams", "s",
            "-show_entries", "stream=index,codec_name:stream_tags=language,title",
            "-of", "json", str(media_path),
        ],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=300,
        check=False,
    )
    if result.returncode != 0:
        raise RuntimeError(result.stderr.strip() or "ffprobe klarte ikke å lese MKV-filen.")

    tracks: list[SubtitleTrack] = []
    for stream in json.loads(result.stdout).get("streams", []):
        tags = stream.get("tags") or {}
        tracks.append(
            SubtitleTrack(
                stream_index=int(stream["index"]),
                language=str(tags.get("language", "und")),
                title=tags.get("title"),
                codec=stream.get("codec_name"),
            )
        )
    return tracks


def is_text_track(track: SubtitleTrack) -> bool:
    return track.codec in TEXT_SUBTITLE_CODECS


def choose_track(
    tracks: list[SubtitleTrack], source_language: str, requested_index: int | None
) -> SubtitleTrack | None:
    candidates = [track for track in tracks if is_text_track(track)]
    if requested_index is not None:
        return next((track for track in candidates if track.stream_index == requested_index), None)

    if source_language.lower() == "auto":
        return sorted(
            candidates,
            key=lambda track: (
                "sdh" in (track.title or "").lower(),
                track.language.lower() == "und",
            ),
        )[0] if candidates else None

    wanted = {source_language.lower()}
    if source_language.lower() == "en":
        wanted.add("eng")
    matching = [track for track in candidates if track.language.lower() in wanted]
    return sorted(matching, key=lambda track: "sdh" in (track.title or "").lower())[0] if matching else None


def list_tracks(tracks: list[SubtitleTrack], as_json: bool = False) -> None:
    if as_json:
        print(json.dumps([
            {
                "stream_index": track.stream_index,
                "language": track.language,
                "title": track.title,
                "codec": track.codec,
                "is_text": is_text_track(track),
            }
            for track in tracks
        ], ensure_ascii=False))
        return
    if not tracks:
        print("Fant ingen innebygde undertekstspor.")
        return
    print("Undertekstspor (stream-index fra ffprobe):")
    for track in tracks:
        kind = "tekst" if is_text_track(track) else "bilde/ikke støttet"
        print(
            f"  {track.stream_index}: språk={track.language}, codec={track.codec or 'ukjent'}, "
            f"type={kind}, tittel={track.title or ''}"
        )


def extract_mkv(
    media_path: Path,
    track: SubtitleTrack,
    output_path: Path,
    ffmpeg_path: str,
    mkvextract_path: str | None,
) -> None:
    output_path.parent.mkdir(parents=True, exist_ok=True)
    if mkvextract_path:
        try:
            result = subprocess.run(
                [mkvextract_path, str(media_path), "tracks", f"{track.stream_index}:{output_path}"],
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                timeout=300,
                check=False,
            )
            if result.returncode == 0 and output_path.is_file() and output_path.stat().st_size:
                try:
                    read_srt(output_path)
                    return
                except (OSError, UnicodeError, ValueError):
                    output_path.unlink(missing_ok=True)
        except (OSError, subprocess.TimeoutExpired):
            output_path.unlink(missing_ok=True)

    result = subprocess.run(
        [
            ffmpeg_path, "-hide_banner", "-loglevel", "error", "-i", str(media_path),
            "-map", f"0:{track.stream_index}", "-c:s", "srt", "-y", str(output_path),
        ],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=300,
        check=False,
    )
    if result.returncode != 0 or not output_path.is_file() or not output_path.stat().st_size:
        output_path.unlink(missing_ok=True)
        raise RuntimeError(result.stderr.strip() or "Kunne ikke hente ut undertekstsporet med ffmpeg.")
    read_srt(output_path)


def default_output_path(input_path: Path, target_language: str) -> Path:
    parts = input_path.stem.split(".")
    if len(parts) > 1 and parts[-1].lower() in LANGUAGE_SUFFIXES:
        parts.pop()
    output_language = "no" if target_language.lower() in {"nb", "nob", "nor", "no"} else target_language.lower()
    return input_path.with_name(".".join(parts) + f".{output_language}.srt")


def default_extracted_path(input_path: Path, source_language: str) -> Path:
    parts = input_path.stem.split(".")
    if len(parts) > 1 and parts[-1].lower() in LANGUAGE_SUFFIXES:
        parts.pop()
    normalized = {
        "eng": "en", "english": "en", "nob": "nb", "nor": "no", "norwegian": "no",
        "nno": "nn", "swe": "sv", "dan": "da", "deu": "de", "ger": "de",
        "fra": "fr", "fre": "fr", "spa": "es", "ita": "it", "fin": "fi",
    }.get(source_language.lower(), source_language.lower())
    if normalized == "auto":
        normalized = "und"
    return input_path.with_name(".".join(parts) + f".{normalized}.srt")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Hent eventuelt undertekster fra MKV og oversett lokalt med MADLAD-400-3B-MT."
    )
    parser.add_argument("input", type=Path, help="Kilde-SRT eller MKV med innebygd undertekst")
    parser.add_argument("output", type=Path, nargs="?", help="Output-SRT (standard: input-basename.no.srt)")
    parser.add_argument("--source-language", default="en", help="Språkkode for MKV-spor (standard: en; bruk auto for automatisk valg)")
    parser.add_argument("--target-language", default="nb", help="Målspråk (standard: nb/bokmål)")
    parser.add_argument("--track-index", type=int, help="Velg et spesifikt ffprobe stream-index i MKV")
    parser.add_argument("--list-tracks", action="store_true", help="List MKV-spor og avslutt uten oversettelse")
    parser.add_argument("--json", action="store_true", help="Skriv sporlisten som JSON (brukes av WPF-grensesnittet)")
    parser.add_argument("--extract-only", action="store_true", help="Hent valgt MKV-spor til SRT uten å oversette")
    parser.add_argument("--ffprobe", help="Sti til ffprobe.exe hvis det ikke finnes i PATH")
    parser.add_argument("--ffmpeg", help="Sti til ffmpeg.exe hvis det ikke finnes i PATH")
    parser.add_argument("--mkvextract", help="Sti til mkvextract.exe; ffmpeg brukes som reserve")
    default_model_dir = Path(__file__).resolve().parent / "models" / "madlad400-3b-mt"
    parser.add_argument(
        "--model", type=Path, default=default_model_dir,
        help="Lokal mappe med MADLAD-modellfilene (standard: %(default)s)",
    )
    parser.add_argument("--batch-size", type=int, default=16, help="Cues per GPU-batch (standard: %(default)s)")
    parser.add_argument("--beams", type=int, default=1, help="Beam search-størrelse (standard: %(default)s)")
    parser.add_argument("--max-input-tokens", type=int, default=512)
    parser.add_argument("--max-new-tokens", type=int, default=64)
    parser.add_argument("--overwrite", action="store_true", help="Erstatt en eksisterende outputfil")
    parser.add_argument("--live-events", action="store_true", help="Send kilde- og batchdata som JSON-linjer til GUI-et")
    return parser.parse_args()


def translate_cues(
    args: argparse.Namespace,
    input_path: Path,
    output_path: Path,
    source_cues: list[Cue],
    newline: str,
) -> int:
    # Block model-hub lookups: both weights and inference must remain local.
    os.environ["HF_HUB_OFFLINE"] = "1"
    if args.live_events:
        emit_event({
            "type": "source",
            "total": len(source_cues),
            "items": [
                {"number": cue.number, "timing": cue.timing, "input": cue.text}
                for cue in source_cues
            ],
        })
    try:
        import torch
        from accelerate import init_empty_weights
        from safetensors import safe_open
        from transformers import AutoConfig, AutoModelForSeq2SeqLM, AutoTokenizer
    except ImportError as error:
        print(f"Mangler Python-pakke: {error.name}. Installer pakkene fra requirements.txt.", file=sys.stderr)
        return 2

    if not torch.cuda.is_available():
        print("CUDA er ikke tilgjengelig i denne PyTorch-installasjonen. GPU-oversettelse avbrytes.", file=sys.stderr)
        print(f"PyTorch={torch.__version__}, bygget med CUDA={torch.version.cuda}", file=sys.stderr)
        return 2

    device = torch.device("cuda:0")
    gpu_name = torch.cuda.get_device_name(device)
    total_vram = torch.cuda.get_device_properties(device).total_memory
    print(f"GPU: {gpu_name} ({total_vram / 1024**3:.2f} GiB)")
    print(f"Kilde: {input_path} | {len(source_cues)} cues | batch: {args.batch_size}")

    model_dir = args.model.expanduser().resolve()
    if not model_dir.is_dir():
        print(f"Fant ikke lokal modellmappe: {model_dir}", file=sys.stderr)
        print("Legg modellfilene der før du starter oversetteren.", file=sys.stderr)
        return 2
    print("Modellmodus: kun lokale filer; nettverkstilgang er deaktivert.")

    model_start = time.perf_counter()
    try:
        tokenizer = AutoTokenizer.from_pretrained(model_dir, local_files_only=True)
        config = AutoConfig.from_pretrained(model_dir, local_files_only=True)
        with init_empty_weights():
            model = AutoModelForSeq2SeqLM.from_config(config)
        weights_path = model_dir / "model.safetensors"
        if not weights_path.is_file():
            raise FileNotFoundError(f"Fant ikke modellvektene: {weights_path}")
        with safe_open(weights_path, framework="np", device="cpu") as checkpoint:
            state_dict = {
                key: torch.from_numpy(checkpoint.get_tensor(key))
                for key in checkpoint.keys()
            }
            # The published safetensors file stores T5's shared encoder/decoder
            # embedding once under decoder.embed_tokens.weight.
            embedding = state_dict.get("decoder.embed_tokens.weight")
            if embedding is not None:
                state_dict.setdefault("shared.weight", embedding)
                state_dict.setdefault("encoder.embed_tokens.weight", embedding)
            model.load_state_dict(state_dict, strict=True, assign=True)
            del state_dict
        model = model.to(device=device, dtype=torch.bfloat16)
        model.eval()
    except Exception as error:  # Model-loading errors vary across Transformers versions.
        print(f"Kunne ikke laste modellen: {error}", file=sys.stderr)
        print("Kontroller at config, tokenizer og vektfiler ligger i modellmappen.", file=sys.stderr)
        return 2
    model_load_seconds = time.perf_counter() - model_start

    target_tag = args.target_language.strip().lower()
    if target_tag in {"nb", "nob", "nor", "norwegian bokmål"}:
        target_tag = "no"
    target_tag = re.sub(r"[^a-z0-9_-]", "", target_tag)
    if not target_tag:
        print("Ugyldig målspråkkode.", file=sys.stderr)
        return 2
    prompts = [f"<2{target_tag}> {cue.text}" for cue in source_cues]

    token_lengths: list[int] = []
    try:
        for start in range(0, len(prompts), 128):
            batch_tokens = tokenizer(
                prompts[start : start + 128], padding=False, truncation=False,
                add_special_tokens=True,
            )["input_ids"]
            token_lengths.extend(len(token_ids) for token_ids in batch_tokens)
    except Exception as error:
        print(f"Kunne ikke tokenisere SRT-teksten: {error}", file=sys.stderr)
        return 2
    too_long = [(i + 1, n) for i, n in enumerate(token_lengths) if n > args.max_input_tokens]
    if too_long:
        first_index, first_length = too_long[0]
        print(
            f"Cue {first_index} er {first_length} tokens, over grensen på {args.max_input_tokens}. "
            "Ingen tekst ble trunkert eller output skrevet.",
            file=sys.stderr,
        )
        return 2

    translated_texts: list[str] = []
    inference_start = time.perf_counter()
    torch.cuda.reset_peak_memory_stats(device)
    try:
        for start in range(0, len(prompts), args.batch_size):
            batch_texts = prompts[start : start + args.batch_size]
            encoded = tokenizer(batch_texts, return_tensors="pt", padding=True, truncation=False).to(device)
            with torch.inference_mode():
                generated = model.generate(
                    **encoded,
                    num_beams=args.beams,
                    max_new_tokens=args.max_new_tokens,
                    do_sample=False,
                )
            batch_outputs = tokenizer.batch_decode(generated, skip_special_tokens=True)
            translated_texts.extend(batch_outputs)
            done = min(start + len(batch_texts), len(prompts))
            if args.live_events:
                emit_event({
                    "type": "batch",
                    "completed": done,
                    "total": len(prompts),
                    "items": [
                        {
                            "number": source_cues[start + offset].number,
                            "timing": source_cues[start + offset].timing,
                            "input": source_cues[start + offset].text,
                            "output": translated_texts[start + offset],
                        }
                        for offset in range(len(batch_outputs))
                    ],
                })
            else:
                print(f"Oversatt {done}/{len(prompts)} cues", flush=True)
    except Exception as error:
        print(f"Oversettelsen feilet; kildemediet er urørt: {error}", file=sys.stderr)
        return 2

    inference_seconds = time.perf_counter() - inference_start
    if len(translated_texts) != len(source_cues):
        print("Modellen returnerte feil antall cues; output skrives ikke.", file=sys.stderr)
        return 2
    translated_cues = [
        Cue(source.number, source.timing, text.strip())
        for source, text in zip(source_cues, translated_texts, strict=True)
    ]
    if any(not cue.text for cue in translated_cues):
        print("Minst ett cue ble tomt etter oversettelse; output skrives ikke.", file=sys.stderr)
        return 2

    try:
        write_srt(output_path, translated_cues, newline)
    except OSError as error:
        print(f"Kunne ikke skrive output: {error}", file=sys.stderr)
        return 2

    allocated = torch.cuda.max_memory_allocated(device)
    reserved = torch.cuda.max_memory_reserved(device)
    print(f"Skrev: {output_path}")
    print(f"Modellinnlasting: {model_load_seconds:.1f} s")
    print(f"Oversettelse: {inference_seconds:.1f} s ({len(source_cues) / max(inference_seconds, 1e-9):.2f} cues/s)")
    print(f"Maks GPU-minne allokert: {allocated / 1024**3:.2f} GiB")
    print(f"Maks GPU-minne reservert: {reserved / 1024**3:.2f} GiB")
    print("Cue-numre og tidsstempler er kopiert direkte fra kildens SRT.")

    report = {
        "model": str(model_dir),
        "device": gpu_name,
        "input": str(input_path),
        "output": str(output_path),
        "cue_count": len(source_cues),
        "batch_size": args.batch_size,
        "beams": args.beams,
        "model_load_seconds": round(model_load_seconds, 3),
        "inference_seconds": round(inference_seconds, 3),
        "cues_per_second": round(len(source_cues) / max(inference_seconds, 1e-9), 3),
        "peak_gpu_allocated_gib": round(allocated / 1024**3, 3),
        "peak_gpu_reserved_gib": round(reserved / 1024**3, 3),
    }
    report_path = output_path.with_suffix(output_path.suffix + ".stats.json")
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Målerapport: {report_path}")
    return 0


def main() -> int:
    args = parse_args()
    if args.batch_size < 1 or args.beams < 1:
        print("Feil: batch-størrelse og beams må være minst 1.", file=sys.stderr)
        return 2

    input_path = args.input.expanduser().resolve()
    if not input_path.is_file():
        print(f"Fant ikke kildefilen: {input_path}", file=sys.stderr)
        return 2
    output_path = (args.output or default_output_path(input_path, args.target_language)).expanduser().resolve()
    if input_path == output_path:
        print("Feil: output må være en annen fil enn input; kilden overskrives ikke.", file=sys.stderr)
        return 2
    if output_path.exists() and not args.overwrite and not args.list_tracks and not args.extract_only:
        print(f"Output finnes allerede: {output_path}. Bruk --overwrite for å erstatte den.", file=sys.stderr)
        return 2

    if input_path.suffix.lower() == ".srt":
        if args.extract_only:
            print("--extract-only kan bare brukes med en MKV-fil.", file=sys.stderr)
            return 2
        if args.list_tracks:
            print("--list-tracks kan bare brukes med en MKV-fil.", file=sys.stderr)
            return 2
        try:
            source_cues, newline = read_srt(input_path)
        except (OSError, UnicodeError, ValueError) as error:
            print(f"Kunne ikke lese SRT: {error}", file=sys.stderr)
            return 2
        return translate_cues(args, input_path, output_path, source_cues, newline)

    if input_path.suffix.lower() != ".mkv":
        print("Input må være en .srt- eller .mkv-fil.", file=sys.stderr)
        return 2

    ffprobe_path = args.ffprobe or find_tool("ffprobe", [
        Path(r"C:\Program Files\Jellyfin\Server\ffprobe.exe"),
        Path(r"C:\Program Files\Jellyfin\Server\jellyfin-ffprobe.exe"),
    ])
    ffmpeg_path = args.ffmpeg or find_tool("ffmpeg", [
        Path(r"C:\Program Files\Jellyfin\Server\ffmpeg.exe"),
        Path(r"C:\Program Files\Jellyfin\Server\jellyfin-ffmpeg.exe"),
    ])
    mkvextract_path = args.mkvextract or find_tool("mkvextract", [
        Path(r"C:\Program Files\MKVToolNix\mkvextract.exe"),
    ])
    if not ffprobe_path:
        print("Fant ikke ffprobe. Installer/bruk Jellyfin FFmpeg eller angi --ffprobe.", file=sys.stderr)
        return 2
    if not ffmpeg_path:
        print("Fant ikke ffmpeg. Installer/bruk Jellyfin FFmpeg eller angi --ffmpeg.", file=sys.stderr)
        return 2

    try:
        tracks = discover_tracks(input_path, ffprobe_path)
    except (OSError, subprocess.TimeoutExpired, RuntimeError, json.JSONDecodeError) as error:
        print(f"Klarte ikke å lese undertekstspor i MKV: {error}", file=sys.stderr)
        return 2
    if args.list_tracks:
        list_tracks(tracks, args.json)
        return 0

    selected = choose_track(tracks, args.source_language, args.track_index)
    if selected is None:
        print(f"Fant ikke et støttet tekstspor for '{args.source_language}'.", file=sys.stderr)
        list_tracks(tracks)
        return 2
    if args.extract_only and args.output is None:
        output_path = default_extracted_path(input_path, selected.language)
    if input_path == output_path:
        print("Feil: output må være en annen fil enn input; kilden overskrives ikke.", file=sys.stderr)
        return 2
    if output_path.exists() and not args.overwrite:
        print(f"Output finnes allerede: {output_path}. Bruk --overwrite for å erstatte den.", file=sys.stderr)
        return 2
    print(
        f"Valgte MKV-spor {selected.stream_index}: {selected.language}, "
        f"{selected.codec or 'ukjent codec'}{', ' + selected.title if selected.title else ''}"
    )
    with tempfile.TemporaryDirectory(prefix="local-gst-") as temporary_directory:
        extracted_path = Path(temporary_directory) / "source.srt"
        try:
            extract_mkv(input_path, selected, extracted_path, ffmpeg_path, mkvextract_path)
            source_cues, newline = read_srt(extracted_path)
            if args.extract_only:
                write_srt(output_path, source_cues, newline)
                print(f"Hentet ut {len(source_cues)} cues til: {output_path}")
                return 0
            return translate_cues(args, input_path, output_path, source_cues, newline)
        except (OSError, UnicodeError, ValueError, RuntimeError, subprocess.TimeoutExpired) as error:
            print(f"MKV-uttrekk/oversettelse feilet: {error}", file=sys.stderr)
            return 2


if __name__ == "__main__":
    raise SystemExit(main())

