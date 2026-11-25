#!/usr/bin/env python3
"""LAN-only CUDA translation worker for Jellyfin's Local GST plugin."""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
from pathlib import Path
import queue
import re
import shutil
import subprocess
import sys
import threading
import time
import uuid
from concurrent.futures import ThreadPoolExecutor
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from tempfile import TemporaryDirectory
from typing import Any


ID_RE = re.compile(r"^[a-f0-9]{32}$")
LANG_RE = re.compile(r"^[a-zA-Z][a-zA-Z0-9_-]{0,23}$")
PROGRESS_RE = re.compile(r"Oversatt\s+(\d+)/(\d+)\s+cues", re.IGNORECASE)
MAX_BODY = 8 * 1024 * 1024
JOBS: dict[str, dict[str, Any]] = {}
JOBS_LOCK = threading.Lock()
EXECUTOR = ThreadPoolExecutor(max_workers=1, thread_name_prefix="gst-cuda")
GPU_INFO: dict[str, Any] = {"cuda_available": False, "gpu_name": ""}
HISTORY_FILE: Path | None = None
RETRY_INPUT_DIR: Path | None = None
MAX_HISTORY = 200
MAX_RETRY_INPUTS = 25
RETRY_INPUT_MAX_AGE_DAYS = 30

DASHBOARD_HTML = """<!doctype html>
<html lang="no"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Local GST – jobber</title><style>
:root{color-scheme:dark;--bg:#11151b;--panel:#1c232d;--line:#303b49;--text:#e5edf7;--muted:#9aabba;--blue:#25a9df;--green:#49c58a;--red:#ff7777;--gold:#e8b04a}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.5 system-ui,Segoe UI,sans-serif}
main{max-width:1200px;margin:0 auto;padding:28px 20px}header{display:flex;justify-content:space-between;align-items:center;gap:16px;margin-bottom:20px}
h1{font-size:25px;margin:0}#gpu{color:var(--muted);font-size:13px}.stats{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:12px;margin:20px 0}
.stat,.job{background:var(--panel);border:1px solid var(--line);border-radius:12px;padding:16px}.stat span{display:block;color:var(--muted);font-size:13px}.stat strong{font-size:23px}
.job{margin:12px 0}.top{display:flex;justify-content:space-between;gap:12px;align-items:flex-start}.name{font-weight:650;overflow-wrap:anywhere}.meta{color:var(--muted);font-size:13px;margin-top:5px}
.badge{white-space:nowrap;border-radius:20px;padding:3px 10px;font-size:12px;background:#334155}.running,.queued{color:var(--gold)}.completed{color:var(--green)}.failed,.cancelled{color:var(--red)}
.bar{height:7px;background:#34404e;border-radius:9px;margin-top:14px;overflow:hidden}.bar i{display:block;height:100%;background:var(--blue)}.foot{display:flex;justify-content:space-between;gap:12px;color:var(--muted);font-size:13px;margin-top:8px}
details{margin-top:10px;color:#ff9a9a;white-space:pre-wrap;overflow-wrap:anywhere}.actions{margin-top:12px}.retry{border:1px solid var(--blue);border-radius:7px;padding:7px 12px;background:#17334a;color:var(--text);font:inherit;cursor:pointer}.retry:disabled{border-color:var(--line);background:#29313c;color:var(--muted);cursor:not-allowed}#notice{min-height:1.5em;color:var(--muted);font-size:13px}#empty{color:var(--muted);padding:28px 0}.refresh{color:var(--muted);font-size:12px}
@media(max-width:650px){main{padding:16px 12px}.stats{grid-template-columns:1fr}.top,.foot{align-items:flex-start;flex-direction:column}}
</style></head><body><main><header><div><h1>Local GST – Translations</h1><div id="gpu">Connecting to GPU worker…</div></div><div class="refresh">Refreshes every 5 seconds</div></header>
<section class="stats"><div class="stat"><span>Active now</span><strong id="active">–</strong></div><div class="stat"><span>Completed in the last 24 hours</span><strong id="done">–</strong></div><div class="stat"><span>Last completed</span><strong id="latest">–</strong></div></section>
<h2>Job history</h2><div id="notice" role="status" aria-live="polite"></div><div id="jobs"></div><div id="empty" hidden>No translations recorded yet.</div></main>
<script>
const labels={queued:'Queued',running:'Running',completed:'Completed',failed:'Failed',cancelled:'Cancelled'};
const date=v=>v?new Date(v).toLocaleString('en-US'):'–';
const elapsed=s=>{if(s==null)return'–';s=Math.max(0,Math.floor(s));const h=Math.floor(s/3600),m=Math.floor(s%3600/60),x=s%60;return h?`${h} hr ${m} min ${x} sec`:m?`${m} min ${x} sec`:`${x} sec`};
function el(tag,cls,text){const x=document.createElement(tag);if(cls)x.className=cls;if(text!==undefined)x.textContent=text;return x}
function render(data){const jobs=data.jobs||[],box=document.querySelector('#jobs');document.querySelector('#gpu').textContent=data.gpu_name||'GPU status unknown';
const active=jobs.filter(j=>j.status==='running'||j.status==='queued');const done=jobs.filter(j=>j.status==='completed'&&j.finished_at&&Date.now()-new Date(j.finished_at).getTime()<86400000);document.querySelector('#active').textContent=active.length;
document.querySelector('#done').textContent=done.length;const last=jobs.find(j=>j.status==='completed');document.querySelector('#latest').textContent=last?date(last.finished_at):'–';box.replaceChildren();document.querySelector('#empty').hidden=jobs.length>0;
for(const j of jobs){const card=el('article','job'),top=el('div','top'),left=el('div'),badge=el('span','badge '+j.status,labels[j.status]||j.status);
left.append(el('div','name',j.media_name));left.append(el('div','meta',`${(j.source_language||'').toUpperCase()} → ${(j.target_language||'').toUpperCase()} · ${j.completed||0} av ${j.total||0} cues`));top.append(left,badge);card.append(top);
if(j.status==='completed'&&j.quality_acceptance){const q=j.quality_acceptance;left.append(el('div','meta',`Quality: ${Number(q.actual_percent||0).toLocaleString('en-US')}% · ${Number(q.flagged_cue_count||0)} subtitle lines flagged`))}
const percent=Math.max(0,Math.min(100,Number(j.percent)||0)),bar=el('div','bar'),fill=el('i');fill.style.width=percent+'%';bar.append(fill);if(j.status==='running'||j.status==='queued')card.append(bar);
const start=j.started_at?`Started ${date(j.started_at)}`:`Submitted ${date(j.submitted_at)}`;const foot=el('div','foot');foot.append(el('span','',start),el('span','',`Elapsed ${elapsed(j.elapsed_seconds)}${j.status==='running'?' · ETA '+elapsed(j.eta_seconds):''}`));card.append(foot);
if(j.error){const details=el('details'),summary=el('summary','', 'Error details');details.append(summary,document.createTextNode(j.error));card.append(details)}
if(j.status==='failed'){const actions=el('div','actions'),button=el('button','retry',j.retrying?'Retry queued':j.retry_available?'Retry':'Retry unavailable');button.type='button';button.disabled=!j.retry_available||Boolean(j.retrying);button.title=j.retry_available?'Run the same subtitle again with current fixes':'The worker did not save subtitle data for this older job';button.addEventListener('click',()=>retryJob(j.id,button));actions.append(button);card.append(actions)}box.append(card)} }
async function retryJob(id,button){button.disabled=true;button.textContent='Submitting retry…';const notice=document.querySelector('#notice');notice.textContent='';try{const r=await fetch(`/api/jobs/${encodeURIComponent(id)}/retry`,{method:'POST',cache:'no-store'});const data=await r.json();if(!r.ok)throw new Error(data.error||`HTTP ${r.status}`);notice.textContent='The job was queued again.';await refresh()}catch(e){notice.textContent='Could not retry the job: '+e.message;button.disabled=false;button.textContent='Retry'}}
async function refresh(){try{const r=await fetch('/api/jobs',{cache:'no-store'});if(!r.ok)throw new Error('HTTP '+r.status);render(await r.json())}catch(e){document.querySelector('#gpu').textContent='Could not reach the worker: '+e.message}}
refresh();setInterval(refresh,5000);
</script></body></html>"""


def json_bytes(value: dict[str, Any]) -> bytes:
    return (json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")


def public_job(job_id: str, job: dict[str, Any]) -> dict[str, Any]:
    started_at = job.get("started_at")
    finished_at = job.get("finished_at")
    elapsed = job.get("duration_seconds", job.get("elapsed_seconds"))
    if started_at and elapsed is None:
        try:
            started = dt.datetime.fromisoformat(started_at)
            elapsed = max(0.0, (dt.datetime.now(dt.timezone.utc) - started).total_seconds())
        except (TypeError, ValueError):
            elapsed = None
    total = int(job.get("total", 0) or 0)
    completed = int(job.get("completed", 0) or 0)
    eta = None
    if job.get("status") == "running" and elapsed is not None and completed > 0 and total > completed:
        eta = round(elapsed * (total - completed) / completed, 1)
    retrying = any(
        candidate.get("retry_of") == job_id and candidate.get("status") in ("queued", "running")
        for candidate in JOBS.values()
    )
    retry_path = RETRY_INPUT_DIR / f"{job_id}.srt" if RETRY_INPUT_DIR else None
    return {
        "id": job_id,
        "status": job.get("status", "unknown"),
        "media_name": job.get("media_name") or "Ukjent fil (eldre jobb)",
        "source_language": job.get("source_language", ""),
        "target_language": job.get("target_language", ""),
        "submitted_at": job.get("submitted_at"),
        "started_at": started_at,
        "finished_at": finished_at,
        "elapsed_seconds": round(elapsed, 1) if elapsed is not None else None,
        "eta_seconds": eta,
        "completed": completed,
        "total": total,
        "percent": round(completed * 100 / total, 1) if total else 0,
        "gpu_name": job.get("gpu_name") or GPU_INFO.get("gpu_name", ""),
        "quality_acceptance": job.get("quality_acceptance"),
        "quality_warning_cue_count": job.get("quality_warning_cue_count", 0),
        "error": str(job.get("error", ""))[-4000:],
        "retry_of": job.get("retry_of"),
        "retry_available": bool(job.get("status") == "failed" and retry_path and retry_path.is_file()),
        "retrying": retrying,
    }


def save_retry_input(job_id: str, input_srt: str) -> None:
    """Keep failed-job input briefly so the local dashboard can retry it."""
    if RETRY_INPUT_DIR is None:
        return
    RETRY_INPUT_DIR.mkdir(parents=True, exist_ok=True)
    target = RETRY_INPUT_DIR / f"{job_id}.srt"
    temporary = target.with_suffix(".srt.tmp")
    temporary.write_text(input_srt, encoding="utf-8-sig")
    temporary.replace(target)

    cutoff = time.time() - RETRY_INPUT_MAX_AGE_DAYS * 24 * 60 * 60
    files = sorted(RETRY_INPUT_DIR.glob("*.srt"), key=lambda path: path.stat().st_mtime, reverse=True)
    for index, path in enumerate(files):
        try:
            if path.stat().st_mtime < cutoff or index >= MAX_RETRY_INPUTS:
                path.unlink(missing_ok=True)
        except OSError as error:
            print(f"Kunne ikke rydde retry-data {path.name}: {error}", flush=True)


def prune_retry_inputs() -> None:
    if RETRY_INPUT_DIR is None or not RETRY_INPUT_DIR.is_dir():
        return
    cutoff = time.time() - RETRY_INPUT_MAX_AGE_DAYS * 24 * 60 * 60
    files = sorted(RETRY_INPUT_DIR.glob("*.srt"), key=lambda path: path.stat().st_mtime, reverse=True)
    for index, path in enumerate(files):
        try:
            if path.stat().st_mtime < cutoff or index >= MAX_RETRY_INPUTS:
                path.unlink(missing_ok=True)
        except OSError as error:
            print(f"Kunne ikke rydde retry-data {path.name}: {error}", flush=True)


def persist_history_locked() -> None:
    """Persist metadata only; subtitle contents and process objects never reach disk."""
    if HISTORY_FILE is None:
        return
    records = [public_job(job_id, job) for job_id, job in JOBS.items()]
    records.sort(key=lambda item: item.get("submitted_at") or "", reverse=True)
    records = records[:MAX_HISTORY]
    temporary = HISTORY_FILE.with_suffix(HISTORY_FILE.suffix + ".tmp")
    try:
        HISTORY_FILE.parent.mkdir(parents=True, exist_ok=True)
        temporary.write_text(json.dumps(records, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        temporary.replace(HISTORY_FILE)
    except OSError as error:
        print(f"Kunne ikke lagre jobbhistorikk: {error}", flush=True)


def load_history() -> None:
    if HISTORY_FILE is None or not HISTORY_FILE.is_file():
        return
    try:
        records = json.loads(HISTORY_FILE.read_text(encoding="utf-8"))
        if not isinstance(records, list):
            return
        now = dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds")
        with JOBS_LOCK:
            for record in records[:MAX_HISTORY]:
                if not isinstance(record, dict) or not ID_RE.fullmatch(str(record.get("id", ""))):
                    continue
                if record.get("status") in ("queued", "running"):
                    record.update(
                        status="failed",
                        finished_at=now,
                        error="GPU-workeren startet på nytt mens jobben pågikk.",
                    )
                    if record.get("started_at"):
                        try:
                            record["duration_seconds"] = round((
                                dt.datetime.fromisoformat(now)
                                - dt.datetime.fromisoformat(record["started_at"])
                            ).total_seconds(), 1)
                        except (TypeError, ValueError):
                            pass
                JOBS[record["id"]] = record
            persist_history_locked()
    except (OSError, json.JSONDecodeError) as error:
        print(f"Kunne ikke lese jobbhistorikk: {error}", flush=True)


def check_cuda(python: str) -> dict[str, Any]:
    script = (
        "import json, torch; "
        "ok=torch.cuda.is_available(); "
        "print(json.dumps({'cuda_available':ok,'gpu_name':torch.cuda.get_device_name(0) if ok else ''}))"
    )
    # CUDA can take longer to initialize on a cold start; the old 45-second
    # limit caused the system worker task to exit before opening its API.
    result = subprocess.run([python, "-c", script], capture_output=True, text=True, timeout=120, check=False)
    if result.returncode != 0:
        return {"cuda_available": False, "gpu_name": "", "error": result.stderr[-1000:]}
    try:
        return json.loads(result.stdout.strip().splitlines()[-1])
    except (IndexError, json.JSONDecodeError):
        return {"cuda_available": False, "gpu_name": "", "error": "Kunne ikke lese CUDA-status."}


def execute_job(job_id: str, args: argparse.Namespace) -> None:
    with JOBS_LOCK:
        job = JOBS.get(job_id)
        if job is None:
            return
        job["status"] = "running"
        job["started_at"] = dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds")
        job["updated_at"] = job["started_at"]
        persist_history_locked()

    with TemporaryDirectory(prefix="local-gst-") as temp_name:
        directory = Path(temp_name)
        input_path = directory / "source.srt"
        output_path = directory / "translated.srt"
        diagnostics_path = directory / "diagnostics.json"
        input_path.write_text(job["input_srt"], encoding="utf-8-sig")
        command = [
            args.python,
            str(args.engine),
            str(input_path),
            str(output_path),
            "--source-language", job["source_language"],
            "--target-language", job["target_language"],
            "--model", str(args.model),
            "--device", "cuda",
            "--cpu-threads", str(args.cpu_threads),
            "--batch-size", str(args.batch_size),
            "--beams", str(args.beams),
            "--minimum-acceptance-percent", str(job.get("minimum_acceptance_percent", 95)),
            "--diagnostics-path", str(diagnostics_path),
            "--overwrite",
        ]
        if job["include_parenthetical_captions"]:
            command.append("--include-parenthetical-captions")

        environment = os.environ.copy()
        environment.update({
            "HF_HUB_OFFLINE": "1",
            "TRANSFORMERS_OFFLINE": "1",
            "HF_DATASETS_OFFLINE": "1",
            "TOKENIZERS_PARALLELISM": "false",
        })
        log_lines: list[str] = []
        try:
            process = subprocess.Popen(
                command,
                cwd=args.engine.parent,
                env=environment,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                encoding="utf-8",
                errors="replace",
                bufsize=1,
            )
            with JOBS_LOCK:
                job["process"] = process

            output_queue: queue.Queue[str | None] = queue.Queue()

            def read_output() -> None:
                assert process.stdout is not None
                for line in process.stdout:
                    output_queue.put(line)
                output_queue.put(None)

            threading.Thread(target=read_output, daemon=True).start()
            output_closed = False
            while process.poll() is None or not output_closed:
                try:
                    line = output_queue.get(timeout=0.5)
                except queue.Empty:
                    line = ""
                if line is None:
                    output_closed = True
                elif line:
                    log_lines.append(line.rstrip())
                    match = PROGRESS_RE.search(line)
                    if match:
                        with JOBS_LOCK:
                            job["completed"] = int(match.group(1))
                            job["total"] = int(match.group(2))
                if job["cancel_event"].is_set() and process.poll() is None:
                    process.terminate()
                    try:
                        process.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait()
                    break
            process.wait()

            if job["cancel_event"].is_set():
                with JOBS_LOCK:
                    job.update(status="cancelled", error="Jobben ble avbrutt.")
            elif process.returncode != 0:
                detail = "\n".join(log_lines[-30:])[-4000:]
                if diagnostics_path.is_file() and HISTORY_FILE is not None:
                    try:
                        diagnostics_directory = HISTORY_FILE.parent / "diagnostics"
                        diagnostics_directory.mkdir(parents=True, exist_ok=True)
                        shutil.copy2(diagnostics_path, diagnostics_directory / f"{job_id}.json")
                    except OSError as error:
                        log_lines.append(f"Kunne ikke bevare feilsøkingsrapporten: {error}")
                with JOBS_LOCK:
                    job.update(status="failed", error=detail or f"Oversetteren avsluttet med kode {process.returncode}.")
            elif not output_path.is_file() or output_path.stat().st_size == 0:
                with JOBS_LOCK:
                    job.update(status="failed", error="Oversetteren laget ingen SRT-fil.")
            else:
                output_srt = output_path.read_text(encoding="utf-8-sig")
                quality_acceptance = None
                quality_warning_cue_count = 0
                if diagnostics_path.is_file():
                    try:
                        report = json.loads(diagnostics_path.read_text(encoding="utf-8"))
                        quality_acceptance = report.get("quality_acceptance")
                        quality_warning_cue_count = int((quality_acceptance or {}).get("flagged_cue_count", 0))
                    except (OSError, json.JSONDecodeError, TypeError, ValueError):
                        pass
                with JOBS_LOCK:
                    job.update(status="completed", completed=job["total"], output_srt=output_srt,
                               gpu_name=GPU_INFO.get("gpu_name", ""),
                               quality_acceptance=quality_acceptance,
                               quality_warning_cue_count=quality_warning_cue_count)
        except Exception as error:
            with JOBS_LOCK:
                job.update(status="failed", error=f"{type(error).__name__}: {error}")
        finally:
            if job.get("status") == "failed" and isinstance(job.get("input_srt"), str):
                try:
                    save_retry_input(job_id, job["input_srt"])
                except OSError as error:
                    print(f"Kunne ikke lagre retry-data for {job_id}: {error}", flush=True)
            with JOBS_LOCK:
                job.pop("process", None)
                job.pop("cancel_event", None)
                job.pop("input_srt", None)
                job["updated_at"] = dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds")
                if job.get("status") in ("completed", "failed", "cancelled"):
                    job["finished_at"] = job["updated_at"]
                    started_at = job.get("started_at")
                    if started_at:
                        try:
                            job["duration_seconds"] = round((
                                dt.datetime.fromisoformat(job["finished_at"])
                                - dt.datetime.fromisoformat(started_at)
                            ).total_seconds(), 1)
                        except (TypeError, ValueError):
                            pass
                persist_history_locked()


def make_handler(args: argparse.Namespace) -> type[BaseHTTPRequestHandler]:
    class Handler(BaseHTTPRequestHandler):
        server_version = "LocalGSTGPU/1.1.0"

        def log_message(self, format_string: str, *values: object) -> None:
            print(f"[{self.log_date_time_string()}] {self.client_address[0]} {format_string % values}", flush=True)

        def _send(self, status: int, value: dict[str, Any]) -> None:
            body = json_bytes(value)
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(body)

        def _allowed(self) -> bool:
            if self.client_address[0] == args.allowed_client:
                return True
            self._send(403, {"error": "Denne worker-en godtar bare forespørsler fra Jellyfin-serveren."})
            return False

        def _dashboard_allowed(self) -> bool:
            if self.client_address[0] in ("127.0.0.1", "::1"):
                return True
            self._send(403, {"error": "Jobboversikten er bare tilgjengelig lokalt på GPU-maskinen."})
            return False

        def _read_json(self) -> dict[str, Any] | None:
            try:
                size = int(self.headers.get("Content-Length", "0"))
                if size < 1 or size > MAX_BODY:
                    self._send(413, {"error": "Forespørselen har ugyldig størrelse."})
                    return None
                value = json.loads(self.rfile.read(size))
                if not isinstance(value, dict):
                    self._send(400, {"error": "JSON-objekt forventet."})
                    return None
                return value
            except (ValueError, json.JSONDecodeError):
                self._send(400, {"error": "Ugyldig JSON."})
                return None

        def do_GET(self) -> None:  # noqa: N802
            if self.path == "/":
                if not self._dashboard_allowed():
                    return
                self.send_response(200)
                self.send_header("Content-Type", "text/html; charset=utf-8")
                self.send_header("Cache-Control", "no-store")
                self.end_headers()
                self.wfile.write(DASHBOARD_HTML.encode("utf-8"))
                return
            if self.path == "/api/jobs":
                if not self._dashboard_allowed():
                    return
                with JOBS_LOCK:
                    jobs = [public_job(job_id, job) for job_id, job in JOBS.items()]
                jobs.sort(key=lambda item: item.get("submitted_at") or "", reverse=True)
                self._send(200, {"jobs": jobs[:MAX_HISTORY], "gpu_name": GPU_INFO.get("gpu_name", "")})
                return
            if self.path == "/favicon.ico":
                if not self._dashboard_allowed():
                    return
                self.send_response(204)
                self.end_headers()
                return
            if not self._allowed():
                return
            if self.path == "/health":
                self._send(200, {
                    "status": "ok" if GPU_INFO.get("cuda_available") and args.model.is_dir() else "not_ready",
                    "cuda_available": bool(GPU_INFO.get("cuda_available")),
                    "model_available": args.model.is_dir(),
                    "gpu_name": GPU_INFO.get("gpu_name", ""),
                })
                return
            match = re.fullmatch(r"/v1/jobs/([a-f0-9]{32})", self.path)
            if not match:
                self._send(404, {"error": "Ukjent endepunkt."})
                return
            with JOBS_LOCK:
                job = JOBS.get(match.group(1))
                if job is None:
                    self._send(404, {"error": "Jobben finnes ikke."})
                    return
                result = {key: job.get(key) for key in ("status", "completed", "total", "output_srt", "error", "gpu_name")}
            self._send(200, result)

        def do_POST(self) -> None:  # noqa: N802
            retry_match = re.fullmatch(r"/api/jobs/([a-f0-9]{32})/retry", self.path)
            if retry_match:
                if not self._dashboard_allowed():
                    return
                original_id = retry_match.group(1)
                with JOBS_LOCK:
                    original = JOBS.get(original_id)
                    if original is None or original.get("status") != "failed":
                        self._send(409, {"error": "Bare feilede jobber kan prøves på nytt."})
                        return
                    retry_path = RETRY_INPUT_DIR / f"{original_id}.srt" if RETRY_INPUT_DIR else None
                    if retry_path is None or not retry_path.is_file():
                        self._send(409, {"error": "Denne jobben er eldre enn retry-lagringen. Undertekstdata finnes ikke lenger."})
                        return
                    if any(
                        candidate.get("retry_of") == original_id
                        and candidate.get("status") in ("queued", "running")
                        for candidate in JOBS.values()
                    ):
                        self._send(409, {"error": "En ny jobb for denne feilen ligger allerede i kø eller kjører."})
                        return
                    try:
                        source_srt = retry_path.read_text(encoding="utf-8-sig")
                    except OSError:
                        self._send(409, {"error": "Retry-data er ikke lenger tilgjengelig for denne jobben."})
                        return
                    retry_id = uuid.uuid4().hex
                    retry_job = {
                        "status": "queued",
                        "completed": 0,
                        "total": source_srt.count("-->"),
                        "media_name": original.get("media_name", "Ukjent fil"),
                        "input_srt": source_srt,
                        "source_language": original.get("source_language", "en"),
                        "target_language": original.get("target_language", "nb"),
                        "include_parenthetical_captions": bool(original.get("include_parenthetical_captions", False)),
                        "minimum_acceptance_percent": int(original.get("minimum_acceptance_percent", 95)),
                        "retry_of": original_id,
                        "cancel_event": threading.Event(),
                        "submitted_at": dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds"),
                    }
                    if retry_job["total"] < 1:
                        self._send(409, {"error": "Lagret undertekstdata er ugyldig og kan ikke prøves på nytt."})
                        return
                    JOBS[retry_id] = retry_job
                    persist_history_locked()
                EXECUTOR.submit(execute_job, retry_id, args)
                self._send(202, {"status": "queued", "id": retry_id, "retry_of": original_id})
                return
            if not self._allowed():
                return
            if self.path != "/v1/jobs":
                self._send(404, {"error": "Ukjent endepunkt."})
                return
            if not GPU_INFO.get("cuda_available") or not args.model.is_dir():
                self._send(503, {"error": "CUDA eller den lokale modellmappen er ikke tilgjengelig."})
                return
            value = self._read_json()
            if value is None:
                return
            job_id = str(value.get("id", ""))
            source = str(value.get("source_language", ""))
            target = str(value.get("target_language", ""))
            input_srt = value.get("input_srt")
            media_name = str(value.get("media_name", "")).strip()[:512]
            minimum_acceptance_percent = value.get("minimum_acceptance_percent", 95)
            if not ID_RE.fullmatch(job_id) or not LANG_RE.fullmatch(source) or not LANG_RE.fullmatch(target) or not isinstance(input_srt, str):
                self._send(400, {"error": "Jobbdata mangler eller er ugyldig."})
                return
            if isinstance(minimum_acceptance_percent, bool) or not isinstance(minimum_acceptance_percent, int) or not 0 <= minimum_acceptance_percent <= 100:
                self._send(400, {"error": "Akseptprosenten må være et heltall mellom 0 og 100."})
                return
            cue_count = input_srt.count("-->")
            if cue_count < 1:
                self._send(400, {"error": "SRT-filen inneholder ingen undertekstlinjer."})
                return
            job = {
                "status": "queued",
                "completed": 0,
                "total": cue_count,
                "media_name": Path(media_name.replace("\\", "/")).name if media_name else "Ukjent fil",
                "input_srt": input_srt,
                "source_language": source,
                "target_language": target,
                "include_parenthetical_captions": bool(value.get("include_parenthetical_captions", False)),
                "minimum_acceptance_percent": minimum_acceptance_percent,
                "cancel_event": threading.Event(),
                "submitted_at": dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds"),
            }
            with JOBS_LOCK:
                if job_id in JOBS:
                    self._send(409, {"error": "Jobb-ID-en er allerede i bruk."})
                    return
                JOBS[job_id] = job
                persist_history_locked()
            EXECUTOR.submit(execute_job, job_id, args)
            self._send(202, {"status": "queued", "id": job_id})

        def do_DELETE(self) -> None:  # noqa: N802
            if not self._allowed():
                return
            match = re.fullmatch(r"/v1/jobs/([a-f0-9]{32})", self.path)
            if not match:
                self._send(404, {"error": "Ukjent endepunkt."})
                return
            job_id = match.group(1)
            with JOBS_LOCK:
                job = JOBS.get(job_id)
                if job is None:
                    self._send(404, {"error": "Jobben finnes ikke."})
                    return
                cancel_event = job.get("cancel_event")
                if cancel_event:
                    cancel_event.set()
                if job.get("status") in ("completed", "failed", "cancelled"):
                    job.pop("input_srt", None)
                    job.pop("output_srt", None)
                    job.pop("process", None)
                    job.pop("cancel_event", None)
                    persist_history_locked()
            self._send(200, {"status": "cancel_requested"})

    return Handler


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--allowed-client", required=True)
    parser.add_argument("--python", type=Path, required=True)
    parser.add_argument("--engine", type=Path, required=True)
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--cpu-threads", type=int, default=4)
    parser.add_argument("--batch-size", type=int, default=8)
    parser.add_argument("--beams", type=int, default=4)
    parser.add_argument("--log-file", type=Path)
    parser.add_argument("--history-file", type=Path)
    args = parser.parse_args()
    args.python = args.python.resolve()
    args.engine = args.engine.resolve()
    args.model = args.model.resolve()
    global HISTORY_FILE, RETRY_INPUT_DIR
    HISTORY_FILE = args.history_file.resolve() if args.history_file else (
        args.log_file.resolve().parent / "jobs.json" if args.log_file else Path(__file__).resolve().with_name("jobs.json")
    )
    RETRY_INPUT_DIR = HISTORY_FILE.parent / "retry-input"
    if args.log_file:
        args.log_file.parent.mkdir(parents=True, exist_ok=True)
        sys.stdout = args.log_file.open("a", encoding="utf-8", buffering=1)
        sys.stderr = sys.stdout
    if not args.python.is_file() or not args.engine.is_file() or not args.model.is_dir():
        parser.error("Python, local_gst.py eller den lokale modellmappen finnes ikke.")

    global GPU_INFO
    prune_retry_inputs()
    load_history()
    try:
        GPU_INFO = check_cuda(str(args.python))
    except (OSError, subprocess.TimeoutExpired) as error:
        GPU_INFO = {"cuda_available": False, "gpu_name": "", "error": str(error)}
    if not GPU_INFO.get("cuda_available"):
        print(f"CUDA er ikke tilgjengelig: {GPU_INFO.get('error', 'ukjent feil')}", flush=True)
        return 2

    server = ThreadingHTTPServer((args.host, args.port), make_handler(args))
    server.daemon_threads = True
    print(
        f"Local GST GPU-worker lytter på {args.host}:{args.port}; GPU={GPU_INFO['gpu_name']}; "
        f"godtar oversettelsesjobber bare fra {args.allowed_client}; modellen kjøres offline.",
        flush=True,
    )
    try:
        server.serve_forever(poll_interval=0.5)
    except KeyboardInterrupt:
        return 0
    finally:
        server.server_close()


if __name__ == "__main__":
    raise SystemExit(main())

