#!/usr/bin/env python3
"""Read-only weekly checks for Norwegian subtitle sidecars.

The report flags suspicious files for human review. It never edits or replaces
subtitle files and does not use previous translations as training data.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import re
from collections import Counter
from pathlib import Path

TIMING = re.compile(r"^\d{2}:\d{2}:\d{2}[,.]\d{3}\s+-->\s+\d{2}:\d{2}:\d{2}[,.]\d{3}$")
QA_VERSION = "1.0.0"
ENGLISH_CONTENT = {
    "the", "and", "you", "your", "what", "with", "this", "that", "have", "don't",
    "would", "could", "should", "because", "through", "there", "their", "they", "them",
    "when", "where", "which", "while", "about", "just", "know", "think", "want", "need",
}


def parse_srt(path: Path) -> tuple[list[dict[str, str]], list[str]]:
    text = path.read_text(encoding="utf-8-sig")
    lines = text.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    cues: list[dict[str, str]] = []
    errors: list[str] = []
    index = 0
    while index < len(lines):
        while index < len(lines) and not lines[index].strip():
            index += 1
        if index >= len(lines):
            break
        number_line = lines[index].strip()
        if not number_line.isdigit() or index + 1 >= len(lines) or not TIMING.fullmatch(lines[index + 1].strip()):
            errors.append(f"Ugyldig cue nær linje {index + 1}")
            index += 1
            continue
        number, timing = number_line, lines[index + 1].strip()
        index += 2
        text_lines: list[str] = []
        while index < len(lines):
            if lines[index].strip().isdigit() and index + 1 < len(lines) and TIMING.fullmatch(lines[index + 1].strip()):
                break
            if not lines[index].strip():
                next_nonempty = index + 1
                while next_nonempty < len(lines) and not lines[next_nonempty].strip():
                    next_nonempty += 1
                if next_nonempty >= len(lines) or (
                    lines[next_nonempty].strip().isdigit()
                    and next_nonempty + 1 < len(lines)
                    and TIMING.fullmatch(lines[next_nonempty + 1].strip())
                ):
                    index = next_nonempty
                    break
            text_lines.append(lines[index])
            index += 1
        cue_text = "\n".join(text_lines).strip()
        if not cue_text:
            errors.append(f"Tom tekst i cue {number}")
        cues.append({"number": number, "timing": timing, "text": cue_text})
    return cues, errors


def suspicious_translation(text: str) -> list[str]:
    clean = re.sub(r"</?[^>]+>", " ", text).strip()
    flags: list[str] = []
    if re.search(r"(?i)\bwww\b|(?:^|\s)w\s+w\s+w(?:\s|$)", clean):
        flags.append("mulig WWW/W-W-W-dekodingsfeil")
    if re.search(r"([a-zæøå]{2,})(?:\1){2,}", clean, re.IGNORECASE):
        flags.append("sammenklistret gjentakelse")
    repeated = re.search(r"\b([a-zæøå]{3,})\b(?:[\s,!.?…-]+\1\b){4,}", clean, re.IGNORECASE)
    if repeated and repeated.group(1).casefold() not in {"nei", "ja", "ok", "okay", "jaja"}:
        flags.append("samme ord gjentas minst fem ganger på rad")
    words = re.findall(r"[a-zæøå']+", clean.casefold())
    english_hits = {word for word in words if word in ENGLISH_CONTENT}
    if len(english_hits) >= 4:
        flags.append("flere engelske funksjonsord; mulig uoversatt tekst")
    if not clean:
        flags.append("tom tekst")
    return flags


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", action="append", required=True, help="Medierot (kan gjentas)")
    parser.add_argument("--report-dir", required=True)
    args = parser.parse_args()

    roots = [Path(value) for value in args.root]
    report_dir = Path(args.report_dir)
    report_dir.mkdir(parents=True, exist_ok=True)
    checked = 0
    findings: list[dict[str, object]] = []
    unavailable: list[str] = []

    for root in roots:
        if not root.is_dir():
            unavailable.append(str(root))
            continue
        for path in root.rglob("*.srt"):
            if not re.search(r"\.(?:no|nb)(?:\.AI-RIP)?\.srt$", path.name, re.IGNORECASE):
                continue
            checked += 1
            try:
                cues, parse_errors = parse_srt(path)
                issues = list(parse_errors)
                if not cues:
                    issues.append("ingen gyldige cues")
                for cue in cues:
                    for reason in suspicious_translation(cue["text"]):
                        issues.append(f"cue {cue['number']}: {reason}")
                if issues:
                    findings.append({"file": str(path), "cue_count": len(cues), "issues": issues[:30]})
            except (OSError, UnicodeError) as error:
                findings.append({"file": str(path), "issues": [f"kunne ikke lese fil: {error}"]})

    now = dt.datetime.now().astimezone()
    issue_counts = Counter(
        reason
        for item in findings
        for issue in item["issues"]
        for reason in [re.sub(r"^cue \d+: ", "", issue)]
    )
    report = {
        "qa_version": QA_VERSION,
        "created_at": now.isoformat(timespec="seconds"),
        "mode": "read_only",
        "files_checked": checked,
        "findings_count": len(findings),
        "issue_counts": dict(issue_counts),
        "unavailable_roots": unavailable,
        "findings": findings,
        "note": "Rapporten flagger mulige feil for manuell kontroll. Ingen SRT-filer er endret eller brukt til automatisk trening.",
    }
    stamp = now.strftime("%Y%m%d-%H%M%S")
    json_path = report_dir / f"weekly-subtitle-qa-{stamp}.json"
    latest_json = report_dir / "weekly-subtitle-qa-latest.json"
    json_text = json.dumps(report, ensure_ascii=False, indent=2) + "\n"
    json_path.write_text(json_text, encoding="utf-8")
    latest_json.write_text(json_text, encoding="utf-8")
    md = [
        f"# Ukentlig kontroll av norske undertekster — {now:%d.%m.%Y %H:%M}",
        "", f"Kontrollerte filer: **{checked}**", f"Filer flagget for gjennomgang: **{len(findings)}**", "",
        "Sjekken er skrivebeskyttet. Den lærer ikke automatisk fra oversettelser og endrer ingen SRT-filer.", "",
    ]
    if issue_counts:
        md += ["## Flagg per type", ""] + [f"- {label}: {count}" for label, count in issue_counts.most_common()] + [""]
    if unavailable:
        md += ["## Mapper som ikke var tilgjengelige", ""] + [f"- `{item}`" for item in unavailable] + [""]
    for item in findings:
        md += [f"## `{item['file']}`", ""] + [f"- {issue}" for issue in item["issues"]] + [""]
    if not findings:
        md += ["Ingen mistenkelige mønstre ble funnet i denne kjøringen.", ""]
    (report_dir / "weekly-subtitle-qa-latest.md").write_text("\n".join(md), encoding="utf-8")
    print(f"Kontrollerte {checked} norske SRT-filer; flagget {len(findings)} fil(er).")
    print(f"Rapport: {report_dir / 'weekly-subtitle-qa-latest.md'}")
    return 0 if not unavailable else 2


if __name__ == "__main__":
    raise SystemExit(main())
