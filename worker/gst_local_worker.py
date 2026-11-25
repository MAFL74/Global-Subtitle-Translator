#!/usr/bin/env python3
"""Process Local GST translation jobs from the Jellyfin shared filesystem queue."""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
from pathlib import Path
import queue
import re
import subprocess
import sys
import threading
import time
import traceback


ID_RE = re.compile(r"^[a-f0-9]{32}$")
LANGUAGE_RE = re.compile(r"^[a-zA-Z][a-zA-Z0-9_-]{0,23}$")
PROGRESS_RE = re.compile(r"Oversatt\s+(\d+)/(\d+)\s+cues", re.IGNORECASE)
def atomic_json(path: Path, data: dict[str, object]) -> None:
    temp = path.with_name(path.name + ".tmp")
    temp.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(temp, path)


def result_path(queue_dir: Path, job_id: str) -> Path:
    return queue_dir / f"{job_id}.result.json"


def process_job(
    job_path: Path,
    queue_dir: Path,
    engine: Path,
    python: str,
    model: Path,
    cpu_threads: int,
    batch_size: int,
    beams: int,
) -> None:
    job_id = job_path.name.removesuffix(".running.json")
    if not ID_RE.fullmatch(job_id):
        print(f"Avviser jobb med ugyldig ID: {job_path.name}", flush=True)
        job_path.unlink(missing_ok=True)
        return

    output_file: Path | None = None
    log_path = queue_dir / f"{job_id}.worker.log"
    progress_path = queue_dir / f"{job_id}.progress.json"
    cancel_path = queue_dir / f"{job_id}.cancel"
    try:
        request = json.loads(job_path.read_text(encoding="utf-8"))
        input_name = str(request["input_file"])
        if Path(input_name).name != input_name or not input_name.endswith(".input.srt"):
            raise ValueError("Ugyldig inputfil i oversettelsesjobben.")
        input_path = (queue_dir / input_name).resolve()
        if input_path.parent != queue_dir or not input_path.is_file():
            raise FileNotFoundError("Jobbens lokale SRT-inndata mangler.")

        output_name = str(request["output_file"])
        if Path(output_name).name != output_name or output_name != f"{job_id}.translated.srt":
            raise ValueError("Ugyldig outputfil i oversettelsesjobben.")
        output_path = (queue_dir / output_name).resolve()
        if output_path.parent != queue_dir:
            raise ValueError("Outputfilen må ligge i den lokale jobbemappen.")
        output_file = output_path

        source = str(request["source_language"])
        target = str(request["target_language"])
        if not LANGUAGE_RE.fullmatch(source) or not LANGUAGE_RE.fullmatch(target):
            raise ValueError("Ugyldig språkvalg i oversettelsesjobben.")
        if not model.is_dir():
            raise FileNotFoundError(f"Fant ikke den lokale modellmappen: {model}")
        if not engine.is_file():
            raise FileNotFoundError(f"Fant ikke lokal oversetter: {engine}")

        command = [
            python,
            str(engine),
            str(input_path),
            str(output_path),
            "--source-language", source,
            "--target-language", target,
            "--model", str(model),
            "--device", "cpu",
            "--cpu-threads", str(cpu_threads),
            "--batch-size", str(batch_size),
            "--beams", str(beams),
            "--minimum-acceptance-percent", str(max(0, min(100, int(request.get("minimum_acceptance_percent", 95))))),
            "--diagnostics-path", str(queue_dir / f"{job_id}.diagnostics.json"),
            "--overwrite",
        ]
        if bool(request.get("include_parenthetical_captions", False)):
            command.append("--include-parenthetical-captions")

        environment = os.environ.copy()
        environment.update({
            "HF_HUB_OFFLINE": "1",
            "TRANSFORMERS_OFFLINE": "1",
            "HF_DATASETS_OFFLINE": "1",
            "TOKENIZERS_PARALLELISM": "false",
            "OMP_NUM_THREADS": str(cpu_threads),
            "MKL_NUM_THREADS": str(cpu_threads),
        })
        started = dt.datetime.now(dt.timezone.utc)
        print(f"Starter lokal jobb {job_id}: {output_path.name}", flush=True)
        with log_path.open("w", encoding="utf-8") as log_file:
            process = subprocess.Popen(
                command,
                cwd=engine.parent,
                env=environment,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                bufsize=1,
            )
            next_heartbeat = 0.0
            assert process.stdout is not None
            output_lines: queue.Queue[str | None] = queue.Queue()

            def read_output() -> None:
                for output_line in process.stdout:
                    output_lines.put(output_line)
                output_lines.put(None)

            threading.Thread(target=read_output, daemon=True).start()
            output_closed = False
            while process.poll() is None or not output_closed:
                try:
                    line = output_lines.get(timeout=0.5)
                except queue.Empty:
                    line = ""
                if line is None:
                    output_closed = True
                elif line:
                    log_file.write(line)
                    log_file.flush()
                    match = PROGRESS_RE.search(line)
                    if match:
                        atomic_json(progress_path, {
                            "completed": int(match.group(1)),
                            "total": int(match.group(2)),
                            "updated_at": dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds"),
                        })
                if time.monotonic() >= next_heartbeat:
                    (queue_dir / ".worker.heartbeat").touch()
                    next_heartbeat = time.monotonic() + 5
                if process.poll() is None and cancel_path.exists():
                    process.terminate()
                    try:
                        process.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait()
                    raise InterruptedError("Oversettelsen ble avbrutt.")
            process.wait()
            exit_code = process.returncode

        if exit_code != 0:
            tail = log_path.read_text(encoding="utf-8", errors="replace")[-4000:]
            raise RuntimeError(f"Oversetteren avsluttet med kode {exit_code}.\n{tail}")
        if not output_path.is_file() or output_path.stat().st_size == 0:
            raise RuntimeError("Oversetteren fullførte uten å lage en SRT-fil.")

        # Jellyfin runs as uid 1000 in the container and needs to read the
        # sidecar after the plugin atomically moves it into place.
        output_path.chmod(0o664)
        atomic_json(result_path(queue_dir, job_id), {
            "status": "completed",
            "started_at": started.isoformat(timespec="seconds"),
            "completed_at": dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds"),
            "output_file": str(output_path),
        })
        print(f"Fullførte lokal jobb {job_id}", flush=True)
    except InterruptedError as error:
        if output_file is not None:
            output_file.unlink(missing_ok=True)
        atomic_json(result_path(queue_dir, job_id), {"status": "cancelled", "error": str(error)})
        print(f"Avbrøt lokal jobb {job_id}", flush=True)
    except Exception as error:
        if output_file is not None:
            output_file.unlink(missing_ok=True)
        atomic_json(result_path(queue_dir, job_id), {
            "status": "failed",
            "error": str(error),
            "exception_type": type(error).__name__,
            "traceback": traceback.format_exc(),
        })
        print(f"Feil i lokal jobb {job_id}: {error}", flush=True)
    finally:
        progress_path.unlink(missing_ok=True)
        cancel_path.unlink(missing_ok=True)
        job_path.unlink(missing_ok=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--queue-dir", type=Path, required=True)
    parser.add_argument("--engine", type=Path, required=True)
    parser.add_argument("--python", default=sys.executable)
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--cpu-threads", type=int, default=20)
    parser.add_argument("--batch-size", type=int, default=16)
    parser.add_argument("--beams", type=int, default=4)
    parser.add_argument("--poll-seconds", type=float, default=0.5)
    args = parser.parse_args()
    if min(args.cpu_threads, args.batch_size, args.beams) < 1 or args.poll_seconds <= 0:
        parser.error("Tråder, batch, beams og pollingintervall må være større enn null.")

    queue_dir = args.queue_dir.resolve()
    engine = args.engine.resolve()
    model = args.model.resolve()
    queue_dir.mkdir(parents=True, exist_ok=True)
    heartbeat = queue_dir / ".worker.heartbeat"

    # Requeue work left in progress by an unclean service stop.
    for running in queue_dir.glob("*.running.json"):
        queued = running.with_name(running.name.removesuffix(".running.json") + ".job.json")
        if not queued.exists():
            os.replace(running, queued)

    os.environ.update({
        "HF_HUB_OFFLINE": "1",
        "TRANSFORMERS_OFFLINE": "1",
        "HF_DATASETS_OFFLINE": "1",
        "TOKENIZERS_PARALLELISM": "false",
    })
    print(
        f"Local GST worker klar: CPU-tråder={args.cpu_threads}, batch={args.batch_size}, "
        f"modell={model}, kø={queue_dir}; nettverkstilgang for modelloppslag er deaktivert.",
        flush=True,
    )
    while True:
        try:
            heartbeat.touch()
        except OSError as error:
            print(f"Kan ikke oppdatere worker-heartbeat: {error}", flush=True)
            time.sleep(args.poll_seconds)
            continue
        jobs = sorted(queue_dir.glob("*.job.json"))
        if not jobs:
            time.sleep(args.poll_seconds)
            continue
        for queued in jobs:
            running = queued.with_name(queued.name.removesuffix(".job.json") + ".running.json")
            try:
                os.replace(queued, running)
            except FileNotFoundError:
                continue
            process_job(running, queue_dir, engine, args.python, model, args.cpu_threads, args.batch_size, args.beams)


if __name__ == "__main__":
    raise SystemExit(main())

