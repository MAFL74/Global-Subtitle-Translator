#!/usr/bin/env python3
"""Extract or read subtitles, then translate them with a local seq2seq model."""

from __future__ import annotations

import argparse
from collections import Counter
import datetime
import json
import os
import platform
import re
import shutil
import statistics
import subprocess
import sys
import tempfile
import time
import traceback
from dataclasses import dataclass
from pathlib import Path


# The WPF host and the worker's JSON events use UTF-8. Windows' legacy console
# encoding cannot represent some valid subtitle characters (for example ♦).
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="strict")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")


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
ENTITY_SENTENCE_STARTERS = {
    "and", "but", "so", "then", "let", "see", "that's", "this", "that", "the", "there", "it",
    "well", "yes", "no", "nope", "not", "is", "oh", "ah", "aah", "huh", "hmm", "hmmm", "mm", "um", "uh", "you\u0027re", "i\u0027m", "we\u0027re", "they\u0027re", "he\u0027s", "she\u0027s", "it\u0027s", "that\u0027s", "there\u0027s", "who\u0027s", "what\u0027s", "hey", "hi", "hello", "okay", "okey", "yeah", "yep", "yup", "please", "wait", "look", "listen", "come", "get", "tell", "stop", "hold", "take", "wipe", "go", "run", "move", "keep", "give", "make", "leave", "call", "remember", "try", "train", "maybe", "never", "even", "internet", "again", "jesus",
    "by", "at", "as", "in", "on", "to", "for", "of", "up", "out", "if", "while", "when", "where", "which", "because", "though", "since", "unless", "do", "how", "how's", "where's", "who's", "what's", "here's", "there's", "it's",
    "mom", "mommy", "mum", "mummy", "ma", "mama", "dad", "daddy", "mother", "father", "brother", "sister", "sir", "ma'am",
    "mom's", "mommy's", "mum's", "mummy's", "ma's", "mama's", "dad's", "daddy's", "mother's", "father's", "brother's", "sister's", "grandma's", "grandpa's",
}
ENTITY_SENTENCE_STARTERS.update({
    "am", "are", "can", "could", "does", "had", "has", "have", "may", "might",
    "must", "shall", "was", "were", "will", "would", "should",
})
ENTITY_TITLES = {"agent", "detective", "officer", "mr", "mrs", "ms", "dr", "sergeant", "chief", "uncle", "aunt"}
ENTITY_TITLE_PHRASES = {"special agent"}
NON_NAME_PHRASES = {
    "your honor", "girl scout", "army intelligence", "restricted housing unit", "code blue",
    "operating officer", "executive officer", "financial officer", "technology officer",
    "new york timesthing", "new york timesreporter", "timesreporter", "timesthing", "county road",
    "dropoutwith", "theranosis", "don't know's", "vice chairman", "post-trial",
}
NON_NAME_PHRASES_RE = re.compile(
    r"(?<![\w])(?:" + "|".join(re.escape(value) for value in sorted(NON_NAME_PHRASES, key=len, reverse=True)) + r")(?![\w])",
    flags=re.IGNORECASE,
)
ENGLISH_LEAK_WORDS = {
    "the", "and", "was", "were", "with", "without", "they", "them", "this", "that",
    "have", "has", "had", "you", "your", "him", "his", "who", "what", "when", "where",
    "why", "how", "from", "into", "been", "being", "are", "is", "am", "does", "did",
    "only", "there", "here", "keep", "keeps", "their", "we", "our", "she", "he", "it",
    "its", "them", "these", "those", "can", "could", "would", "should", "will", "shall",
    "not", "but", "because", "while", "after", "before", "about", "some", "any", "all",
}
ENGLISH_LEAK_CONTENT_WORDS = {
    "clearance", "briefing", "favor", "favors", "favour", "favours", "backup",
    "boonie", "boonies", "gridding", "fuck", "fucks", "fucked", "fucking",
    "shit", "shits", "shitty", "damn", "damned", "dammit", "hell", "crap",
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


def detect_repetition(text: str) -> bool:
    """Catch obvious decoder loops while allowing ordinary repeated dialogue."""
    # Formatting tags such as <i> must not count as subtitle words during QA.
    assessment_text = re.sub(r"<[^>]*>", " ", text)
    # Some tokenizer outputs concatenate a short word repeatedly (e.g. "fromfromfrom").
    if re.search(r"([a-zæøå]{2,})(?:\1){3,}", assessment_text, flags=re.IGNORECASE):
        return True

    words = re.findall(r"[\w’'-]+", assessment_text.casefold(), flags=re.UNICODE)
    # The former length guard missed short decoder failures such as "w w w".
    # Repeated single-letter tokens are not useful subtitle text; ordinary
    # repetitions such as "no, no, no" remain allowed.
    if len(words) >= 3:
        single_letter_words = [word for word in words if len(word) == 1]
        if len(single_letter_words) >= 3 and len(single_letter_words) * 4 >= len(words) * 3:
            return True

    if len(assessment_text) < 32:
        return False

    if any(len(word) >= 3 and count >= 5 for word, count in Counter(words).items()):
        return True
    for width in range(1, min(6, len(words) // 4) + 1):
        for start in range(len(words) - width * 3):
            phrase = words[start : start + width]
            repeats = 1
            while words[start + repeats * width : start + (repeats + 1) * width] == phrase:
                repeats += 1
            if repeats >= 4:
                return True
    return False


SHORT_ACK_RE = re.compile(
    r"^(?P<dash>\s*[-–—]\s*)?(?P<open>[\"“‘]?)(?P<word>okay|okey|o\.k\.|ok|yeah|yep|yup|nope|no|yes|hey|hi|hello|ah+|oh+|ooh+|relax|thanks|thank\s+you|all\s+right|alright|understood|got\s+it|then|and|but|so|because|um+|uh+|hmm+)(?P<punct>[.!?,…]*)(?P<close>[\"”’]?)$",
    flags=re.IGNORECASE,
)
SHORT_DIALOGUE_TRANSLATIONS = {
    "okay": "Ok", "okey": "Ok", "o.k.": "Ok", "ok": "Ok",
    "yeah": "Ja", "yep": "Jepp", "yup": "Jepp", "nope": "Nei", "no": "Nei", "yes": "Ja",
    "hey": "Hei", "hi": "Hei", "hello": "Hallo", "relax": "Slapp av",
    "thanks": "Takk", "thank you": "Takk", "all right": "Greit", "alright": "Greit",
    "understood": "Forstått", "got it": "Skjønner",
    "ah": "Åh", "aah": "Åh", "oh": "Å", "ooh": "Å",
    "then": "Så", "and": "Og", "but": "Men", "so": "Så", "because": "Fordi",
    "um": "Eh", "umm": "Eh", "uh": "Eh", "uhh": "Eh",
    "hmm": "Hmm", "hmmm": "Hmm",
}
SHORT_FIXED_PHRASE_RE = re.compile(
    r"^(?P<dash>\s*[-–—]\s*)?(?P<phrase>duress\s+code)(?P<punct>[.!?,…]*)$",
    flags=re.IGNORECASE,
)
SHORT_SWEAR_RE = re.compile(
    r"^(?P<dash>\s*[-–—]\s*)?(?P<word>fuck|shit|damn|dammit|hell|crap)(?P<punct>[.!?,…]*)$",
    flags=re.IGNORECASE,
)
SHORT_SWEAR_TRANSLATIONS = {
    "fuck": "Faen", "shit": "Dritt", "damn": "Pokker", "dammit": "Pokker",
    "hell": "Helvete", "crap": "Dritt",
}
_NAMED_PHRASE_PATTERN_CACHE: dict[tuple[str, ...], dict[str, re.Pattern[str]]] = {}


def _named_phrase_patterns(known_names: list[str]) -> dict[str, re.Pattern[str]]:
    """Compile name-bearing phrase rules once per subtitle's name list."""
    key = tuple(known_names)
    cached = _NAMED_PHRASE_PATTERN_CACHE.get(key)
    if cached is not None:
        return cached
    alternatives = "|".join(re.escape(name) for name in sorted(known_names, key=len, reverse=True))
    if not alternatives:
        alternatives = r"(?!)"
    name = rf"(?P<name>{alternatives})"
    cached = {
        "exact": re.compile(rf"^{name}(?P<punct>[.!?…]?)$", flags=re.IGNORECASE),
        "made_person_film": re.compile(
            rf"^(?P<after>after\s+)?I\s+made\s+{name}\s+film\s+(?:that|the)\s+video\s+with\s+me(?P<punct>[,!.?…]?)$",
            flags=re.IGNORECASE,
        ),
        "fan_question": re.compile(
            rf"^(?:you['’]re\s+a|are\s+you\s+a)\s+{name}\s+fan\?$", flags=re.IGNORECASE
        ),
        "greeting": re.compile(
            rf"^(?P<greeting>thanks|thank\s+you|hello|hi|hey|sorry),\s*{name}(?P<punct>[.!?…]?)$",
            flags=re.IGNORECASE,
        ),
        "right_question": re.compile(rf"^{name},\s*(?:right|correct)\?$", flags=re.IGNORECASE),
        "stop_no": re.compile(rf"^Stop\.\s+No,\s*{name}[.!?…]?$", flags=re.IGNORECASE),
        "possessive_relation": re.compile(
            rf"^{name}['’]s\s+(?P<relation>wife|husband|mother|father|son|daughter|brother|sister|family)(?P<punct>[.!?…]?)$",
            flags=re.IGNORECASE,
        ),
    }
    if len(_NAMED_PHRASE_PATTERN_CACHE) >= 8:
        _NAMED_PHRASE_PATTERN_CACHE.clear()
    _NAMED_PHRASE_PATTERN_CACHE[key] = cached
    return cached


def translate_short_acknowledgement(text: str) -> str | None:
    """Translate common isolated cues deterministically instead of guessing."""
    match = SHORT_ACK_RE.fullmatch(text.strip())
    if match:
        source_word = match.group("word")
        word = re.sub(r"\s+", " ", source_word.casefold())
        translated = SHORT_DIALOGUE_TRANSLATIONS.get(word)
        if translated is None:
            if word.startswith(("um", "uh")):
                translated = "Eh"
            elif word.startswith(("oh", "ooh")):
                translated = "Å"
            else:
                translated = "Hmm"
        if source_word.isupper():
            translated = translated.upper()
        return f"{match.group('dash') or ''}{match.group('open')}{translated}{match.group('punct')}{match.group('close')}"
    match = SHORT_FIXED_PHRASE_RE.fullmatch(text.strip())
    if match:
        phrase = match.group("phrase")
        translated = "Tvangskode" if phrase[:1].isupper() else "tvangskode"
        return f"{match.group('dash') or ''}{translated}{match.group('punct')}"
    match = SHORT_SWEAR_RE.fullmatch(text.strip())
    if match:
        word = match.group("word")
        translated = SHORT_SWEAR_TRANSLATIONS[word.casefold()]
        if word.isupper():
            translated = translated.upper()
        return f"{match.group('dash') or ''}{translated}{match.group('punct')}"
    return None


def translate_safe_named_phrase(text: str, known_names: list[str]) -> str | None:
    """Handle short name-bearing replies where the MT model often drops a name."""
    value = text.strip()
    county_road_question = re.fullmatch(
        r"Is that County Road (?P<number>\d+) at the bottom of the hill\?",
        value,
        flags=re.IGNORECASE,
    )
    if county_road_question:
        return f"Er det fylkesvei {county_road_question.group('number')} nederst i bakken?"
    for title, norwegian_title in (("Uncle", "onkel"), ("Aunt", "tante")):
        alternatives = "|".join(
            re.escape(name) for name in sorted(known_names, key=len, reverse=True)
        )
        if not alternatives:
            break
        relative_here = re.fullmatch(
            rf"Is\s+{title}\s+(?P<name>{alternatives})\s+here\?",
            value,
            flags=re.IGNORECASE,
        )
        if relative_here:
            name = relative_here.group("name")
            # This short, fixed question has a reliable Norwegian form. Keeping
            # the kinship title outside the protected name lets it translate,
            # while the person's spelling stays intact.
            return f"Er {norwegian_title} {name} her?"
    if re.fullmatch(r"911[.!?]?\s*what['’]s\s+your\s+emergency\?", value, flags=re.IGNORECASE):
        return "911. Hva gjelder det?"
    traumatic_hell_job = re.fullmatch(r"how\s+was\s+traumatic\s+hell\s+job\?", value, flags=re.IGNORECASE)
    if traumatic_hell_job:
        return "Hvordan var den traumatiske helvetesjobben?"
    quoted_faces_iconic = re.fullmatch(
        r"yeah[.!?]\s*[\"“‘](.+?)[.!?]?[\"”’]\s*it['’]s\s+iconic[.!?]?",
        value,
        flags=re.IGNORECASE,
    )
    if quoted_faces_iconic:
        return f"Ja. «{quoted_faces_iconic.group(1)}». Den er ikonisk."
    possessive_relations = {
        "wife": "kone", "husband": "mann", "mother": "mor", "father": "far",
        "son": "sønn", "daughter": "datter", "brother": "bror", "sister": "søster",
        "family": "familie",
    }
    patterns = _named_phrase_patterns(known_names)
    exact_name = patterns["exact"].fullmatch(value)
    if exact_name:
        return value
    quoted_why = re.fullmatch(r"why\s+([\"“‘])(.+?)\1\?", value, flags=re.IGNORECASE)
    if quoted_why:
        return f"Hvorfor «{quoted_why.group(2)}»?"
    quoted_movie_reference = re.fullmatch(
        r"something\?\s+what,\s+are\s+you\s+gonna\s+go\s+full\s+[\"“‘](.+?)[\"”’]\?",
        value,
        flags=re.IGNORECASE,
    )
    if quoted_movie_reference:
        return f"Noe? Hva, skal du gå helt «{quoted_movie_reference.group(1)}»?"
    quoted_title_is_funny = re.fullmatch(
        r"and\s+[\"“‘](.+?)[\"”’]\s+is\s+funny[.!?…]?",
        value,
        flags=re.IGNORECASE,
    )
    if quoted_title_is_funny:
        punctuation = value[-1] if value[-1] in ".!?…" else ""
        return f"Og «{quoted_title_is_funny.group(1)}» er morsom{punctuation}"
    quoted_faces_context = re.fullmatch(
        r"with\s+([\"“‘])(.+?),\1\s+people\s+thought\s+it\s+was\s+real,?",
        value,
        flags=re.IGNORECASE,
    )
    if quoted_faces_context:
        return f"Med «{quoted_faces_context.group(2)}» trodde folk at det var ekte,"
    youre_tag_question = re.fullmatch(
        r"(?:you['’]re|you\s+are)\s+(.+?),\s*aren['’]t\s+you\?",
        value,
        flags=re.IGNORECASE,
    )
    if youre_tag_question:
        return f"Du er {youre_tag_question.group(1)}, ikke sant?"
    quoted_what_is = re.fullmatch(
        r"(?:(.+?,\s*))?what\s+is\s+([\"“‘])(.+?)\2\?",
        value,
        flags=re.IGNORECASE,
    )
    if quoted_what_is:
        prefix = quoted_what_is.group(1) or ""
        return f"{prefix}hva er «{quoted_what_is.group(3)}»?"
    made_person_film = patterns["made_person_film"].fullmatch(value)
    if made_person_film:
        name = made_person_film.group("name")
        prefix = "Etter at jeg fikk" if made_person_film.group("after") else "Jeg fikk"
        return f"{prefix} {name} til å filme den videoen sammen med meg{made_person_film.group('punct')}"
    fan_question = patterns["fan_question"].fullmatch(value)
    if fan_question:
        return f"Er du fan av {fan_question.group('name')}?"
    greeting = patterns["greeting"].fullmatch(value)
    if greeting:
        translated_greeting = {
            "thanks": "Takk", "thank you": "Takk", "hello": "Hei", "hi": "Hei",
            "hey": "Hei", "sorry": "Beklager",
        }[re.sub(r"\s+", " ", greeting.group("greeting").casefold())]
        return f"{translated_greeting}, {greeting.group('name')}{greeting.group('punct')}"
    right_question = patterns["right_question"].fullmatch(value)
    if right_question:
        return f"{right_question.group('name')}, ikke sant?"
    stop_no = patterns["stop_no"].fullmatch(value)
    if stop_no:
        return re.sub(r"(?i)^Stop\.\s+No,", "Stopp. Nei,", value)
    possessive = patterns["possessive_relation"].fullmatch(value)
    if possessive:
        name = possessive.group("name")
        relation = possessive_relations[possessive.group("relation").casefold()]
        return f"{name}s {relation}{possessive.group('punct')}"
    return None


def preserve_ass_tags(source: str, translated: str) -> str:
    """Keep ASS positioning tags such as {\\an8} while translating cue text."""
    tags = re.findall(r"\{\\[^}]*\}", source)
    if not tags:
        return translated
    for tag in tags:
        tag_body = re.escape(tag[1:-1].lstrip("\\"))
        # Marian may strip the ASS backslash but leave its contents as text.
        translated = re.sub(r"\{\\?" + tag_body + r"\}", "", translated, flags=re.IGNORECASE)
    lines = translated.splitlines()
    for index, line in enumerate(lines):
        if line.strip():
            lines[index] = "".join(tags) + line.lstrip()
            break
    return "\n".join(lines)


def repair_known_translation_errors(source: str, translated: str) -> str:
    """Correct stable domain phrases that this local model repeatedly leaves in English."""
    source_without_speaker = re.sub(r"^\s*[A-Z][A-Z0-9 ]{0,24}:\s*", "", source)
    source_okey = re.match(r"^\s*([-–—]\s*)?okey\b", source_without_speaker, re.IGNORECASE)
    if source_okey:
        translated = re.sub(
            r"^(?P<prefix>\s*(?:[A-Z][A-Z0-9 ]{0,24}:\s*)?(?:[-–—]\s*)?)(?:w\s+o\s+w|wow|w\s+w\s+w)(?=[.!?,…\s]|$)",
            lambda match: match.group("prefix") + "Ok",
            translated,
            count=1,
            flags=re.IGNORECASE,
        )
    if re.search(r"\bclearance\s+rate\b", source, flags=re.IGNORECASE):
        translated = re.sub(
            r"\bclearance\s+rate\b",
            lambda match: "Oppklaringsprosent" if match.group()[0].isupper() else "oppklaringsprosent",
            translated,
            flags=re.IGNORECASE,
        )
    if re.search(r"\bfavors?\b", source, flags=re.IGNORECASE):
        translated = re.sub(
            r"\bfavors\b",
            lambda match: "Tjenester" if match.group()[0].isupper() else "tjenester",
            translated,
            flags=re.IGNORECASE,
        )
    if re.search(r"\bno\s+backup\b", source, flags=re.IGNORECASE):
        translated = re.sub(r"\bbackup\b", "forsterkning", translated, flags=re.IGNORECASE)
        if re.search(r"\bno\s+one\s+on\s+the\s+inside\b", source, flags=re.IGNORECASE):
            translated = re.sub(
                r"uten telefon,\s*forsterkning,\s*ingen på innsiden",
                "uten telefon, forsterkning eller noen på innsiden",
                translated,
                flags=re.IGNORECASE,
            )
    if re.search(r"\bgridding\s+the\s+sector\b", source, flags=re.IGNORECASE):
        translated = re.sub(r"\bgrider\s+sektoren\b", "finkjemmer sektoren", translated, flags=re.IGNORECASE)
        translated = re.sub(
            r"\bfavor\b",
            lambda match: "Tjeneste" if match.group()[0].isupper() else "tjeneste",
            translated,
            flags=re.IGNORECASE,
        )
    if re.search(r"\bi\s+make\s+my\s+own\s+coin\b", source, flags=re.IGNORECASE):
        translated = re.sub(
            r"jeg\s+lager\s+min\s+egen\s+mynt",
            "Jeg tjener mine egne penger",
            translated,
            flags=re.IGNORECASE,
        )
    if re.search(r"\bspicy\s+pickles\b", source, flags=re.IGNORECASE):
        translated = re.sub(r"\bpickles\b", "sterke sylteagurker", translated, flags=re.IGNORECASE)
    if re.search(r"\bnew\s+responsibilities\b", source, flags=re.IGNORECASE):
        translated = re.sub(r"\bnoen nye ansvar\b", "nye ansvarsoppgaver", translated, flags=re.IGNORECASE)
    if re.search(r"\bredeeming\s+factor\b", source, flags=re.IGNORECASE):
        translated = re.sub(r"\bgjenløsende faktoren\b", "formildende faktoren", translated, flags=re.IGNORECASE)
    return translated


def repair_contextual_cue_pairs(cues: list[Cue], translated_texts: list[str]) -> list[dict[str, object]]:
    """Repair known idioms split across adjacent subtitle timestamps."""
    repairs: list[dict[str, object]] = []
    normalized = [re.sub(r"\s+", " ", clean_subtitle_markup(cue.text)).strip() for cue in cues]
    for index in range(len(cues) - 1):
        if (
            re.fullmatch(r"when you accidentally charge underwear", normalized[index], re.IGNORECASE)
            and re.fullmatch(r"to your company credit card\.?", normalized[index + 1], re.IGNORECASE)
        ):
            translated_texts[index] = "Når du ved et uhell kjøper undertøy"
            translated_texts[index + 1] = "med firmakortet."
            repairs.append({"cue_numbers": [cues[index].number, cues[index + 1].number], "rule": "accidentally_charge_underwear"})
    for index in range(len(cues) - 2):
        if (
            re.fullmatch(r"i decided to include this sequence in my study", normalized[index], re.IGNORECASE)
            and re.fullmatch(r"for it portrayed a barbaric method of dealing", normalized[index + 1], re.IGNORECASE)
            and re.fullmatch(r"with the criminal\.?", normalized[index + 2], re.IGNORECASE)
        ):
            translated_texts[index] = "Jeg tok denne sekvensen med i studien min"
            translated_texts[index + 1] = "fordi den viste en barbarisk måte å behandle"
            translated_texts[index + 2] = "forbryteren på."
            repairs.append({"cue_numbers": [cues[index].number, cues[index + 1].number, cues[index + 2].number], "rule": "barbaric_method_split_across_cues"})
    return repairs


def restore_dropped_subject_name(source: str, translated: str, names: list[str]) -> str:
    """Put a protected name back when Marian replaces the named subject with a pronoun."""
    plain_source = clean_subtitle_markup(source)
    for name in names:
        titled_subject = re.match(
            r"(?i)^\s*(?P<title>special\s+agent|agent|detective|officer|sergeant|chief)\s+"
            + re.escape(name)
            + r"\s*,\s*(?P<pronoun>he|she|they)\s+(?:is|are|was|were)\b",
            plain_source,
        )
        if titled_subject:
            title_translation = {
                "special agent": "Spesialagent",
                "agent": "Agent",
                "detective": "Etterforsker",
                "officer": "Betjent",
                "sergeant": "Sersjant",
                "chief": "Sjef",
            }.get(re.sub(r"\s+", " ", titled_subject.group("title").casefold()), titled_subject.group("title"))
            display_name = f"{title_translation} {name}"
            translated = re.sub(
                r"(?i)^([\s]*(?:[-–—]\s*)?)(han|hun|de)\b",
                lambda match: match.group(1) + display_name,
                translated,
                count=1,
            )
        if re.match(r"(?i)^Why\s+would\s+" + re.escape(name) + r"\b", plain_source):
            translated = re.sub(
                r"(?i)(Hvorfor\s+skulle\s+)(han|hun|de)\b",
                lambda match: match.group(1) + name,
                translated,
                count=1,
            )
        if re.match(r"(?i)^" + re.escape(name) + r"\s+(?:is|was)\b", plain_source):
            translated = re.sub(
                r"(?i)^(\s*(?:[-–—]\s*)?)(han|hun|de|det|den)\b",
                lambda match: match.group(1) + name,
                translated,
                count=1,
            )
        possessive_name = re.fullmatch(r"(.+?)[\u0027\u2019]s", name)
        if possessive_name:
            base_name = possessive_name.group(1)
            contraction_subject = re.search(
                r"(?i)(?<![\w])" + re.escape(base_name)
                + r"[\u0027\u2019]s\s+(?:coming|going|leaving|arriving|calling|working|waiting|running|staying|moving|taking|getting|playing|talking|asking|looking|trying)\b",
                plain_source,
            )
            if contraction_subject and not re.search(
                r"(?i)(?<![\w])" + re.escape(base_name) + r"(?:s|[\u0027\u2019]s)?(?![\w])",
                translated,
            ):
                leading_context = re.match(r"(?i)^(og\s+du\s+vet,?\s*)", translated)
                if leading_context:
                    translated = (
                        leading_context.group(1) + base_name + " "
                        + translated[leading_context.end():]
                    )
                else:
                    subordinate_subject = re.match(
                        r"(?i)^((?:og\s+)?(?:du|dere)\s+vet\s+at\s+)(?:du|dere|han|hun|de)\b",
                        translated,
                    )
                    if subordinate_subject:
                        translated = (
                            subordinate_subject.group(1) + base_name
                            + translated[subordinate_subject.end():]
                        )
                    elif re.match(
                        r"(?i)^(kommer|går|drar|forlater|ankommer|ringer|jobber|venter|løper|blir|flytter|tar|får|spiller|snakker|spør|ser|prøver)\b",
                        translated,
                    ):
                        translated = base_name + " " + translated
    return translated


def has_unusable_short_output(source: str, translated: str) -> bool:
    """Reject punctuation-only or one-letter decoder output, including per line."""
    source_lines = [line for line in source.splitlines() if line.strip()]
    translated_lines = [line for line in translated.splitlines() if line.strip()]
    if not translated_lines:
        return True
    for index, line in enumerate(translated_lines):
        plain = clean_subtitle_markup(re.sub(r"^\s*[-–—]\s*", "", line)).strip()
        words = re.findall(r"[\w’'-]+", plain, flags=re.UNICODE)
        if not words:
            return True
        if len(words) == 1 and len(words[0]) == 1 and words[0].casefold() not in {"i", "å"}:
            source_line = source_lines[min(index, len(source_lines) - 1)] if source_lines else ""
            source_words = re.findall(r"[\w’'-]+", clean_subtitle_markup(source_line), flags=re.UNICODE)
            if source_words != words:
                return True
    return False


def detect_english_leakage(text: str) -> bool:
    """Reject outputs that clearly remain English instead of Norwegian."""
    words = re.findall(r"[a-z']+", clean_subtitle_markup(text).casefold())
    if any(word in ENGLISH_LEAK_CONTENT_WORDS for word in words):
        return True
    hits = [word for word in words if word in ENGLISH_LEAK_WORDS]
    # Require several English-only function words to avoid flagging short
    # translations containing a name, title, or an ambiguous single word.
    return len(hits) >= 4 and len(set(hits)) >= 3


def loses_dialogue_lines(source: str, translated: str) -> bool:
    """Catch a common MT failure where a speaker's entire reply disappears."""
    source_speakers = len(re.findall(r"(?m)^\s*[-–—]\s+", source))
    translated_speakers = len(re.findall(r"(?m)^\s*[-–—]\s+", translated))
    return source_speakers >= 2 and translated_speakers < source_speakers


def loses_sentence_content(source: str, translated: str) -> bool:
    """Flag cues where one or more complete source sentences disappeared."""
    # Transcripts commonly spell out a stutter as "I... I'm" or "I… I'm".
    # That is one interrupted sentence, not two sentences the translation must
    # reproduce. Do not count the repeated pronoun as lost sentence content.
    hesitation_re = re.compile(
        r"\b(I|you|he|she|we|they|it|that|this|there|here|what|who)"
        r"(?:['’](?:m|re|ve|ll|d|s))?(?:\s*…|\s*\.{2,})"
        r"\s+\1(?:['’](?:m|re|ve|ll|d|s))?\b",
        flags=re.IGNORECASE,
    )
    source = hesitation_re.sub(r"\1", clean_subtitle_markup(source))
    translated = clean_subtitle_markup(translated)
    sentence_end_re = re.compile(
        r"[.!?…]+(?:[\"'’”)]*)\s*(?=(?:[-–—]\s*)?[A-ZÀ-ÖØ-Þ]|\Z)",
    )
    source_count = len(sentence_end_re.findall(source))
    translated_count = len(sentence_end_re.findall(translated))
    return source_count >= 2 and translated_count < source_count


def remove_entity_title_prefix(words: list[str]) -> list[str]:
    """Keep job titles translatable while protecting only the person's name."""
    for title in sorted(ENTITY_TITLE_PHRASES, key=len, reverse=True):
        title_words = title.split()
        if [word.casefold().rstrip(".") for word in words[:len(title_words)]] == title_words:
            return words[len(title_words):]
    if words and words[0].casefold().rstrip(".") in ENTITY_TITLES:
        return words[1:]
    return words


def split_sentences(text: str) -> list[str]:
    """Split at sentence boundaries while keeping closing quotes and punctuation."""
    protected_text = re.sub(r"\b(Mr|Mrs|Ms|Dr)\.", r"\1§", text)
    boundary = re.compile(r"[.!?](?:[\"'’”)]*)\s+(?=(?:[-–—]\s*)?[A-ZÀ-ÖØ-Þ])")
    parts: list[str] = []
    start = 0
    for match in boundary.finditer(protected_text):
        segment_end = match.end() - len(match.group()) + len(match.group().rstrip())
        parts.append(protected_text[start:segment_end].replace("§", ".").strip())
        start = match.end()
    parts.append(protected_text[start:].replace("§", ".").strip())
    return [part for part in parts if part]


def collect_proper_names(cues: list[Cue]) -> list[str]:
    """Find names, places, quoted codewords, and acronyms to preserve during MT."""
    names: set[str] = set()
    token_re = re.compile(r"[A-Z][a-z]+(?:['’][A-Za-z]+)?")
    name_run_re = re.compile(
        r"(?<![\w])(?:[A-Z][a-z]+(?:['’][A-Za-z]+)?)(?:\s+(?:(?:and|of|the|de|van|von|la|le|del|di)\s+)?[A-Z][a-z]+(?:['’][A-Za-z]+)?)*"
    )
    quoted_literal_re = re.compile(r"[\"“‘]([A-Za-z0-9][A-Za-z0-9'’_-]{0,30})[.!?…]?[\"”’]")
    acronym_re = re.compile(r"\b[A-Z][A-Z0-9-]{1,7}\b")
    common_uppercase_words = {
        "OK", "OKAY", "NO", "YES", "HEY", "WHAT", "WHEN", "WHO", "WHY", "HOW", "STOP", "ID",
        "GO", "HELP", "RUN", "THE", "AND", "BUT", "YOU", "WE", "HE", "SHE", "IT", "NDA",
    }
    mid_sentence_name_counts: Counter[str] = Counter()
    for cue in cues:
        text = clean_subtitle_markup(cue.text)
        # Speaker labels are handled separately and must not become protected
        # entities (otherwise the label parser would never see them).
        text = re.sub(r"(?m)^\s*[-–—]?\s*[A-Z][A-Z0-9 ]{0,24}:\s*", "", text)
        text = "\n".join(
            line for line in text.splitlines()
            if not is_non_speech_caption(line.strip())
        )
        tokens = list(token_re.finditer(text))
        runs: list[list[re.Match[str]]] = []
        for token in tokens:
            if runs and text[runs[-1][-1].end():token.start()].isspace():
                runs[-1].append(token)
            else:
                runs.append([token])
        for run in runs:
            if len(run) < 2:
                continue
            words = [match.group() for match in run]
            while words and words[0].casefold() in ENTITY_SENTENCE_STARTERS:
                words.pop(0)
            if not words:
                continue
            phrase = " ".join(words)
            if phrase.casefold() in NON_NAME_PHRASES:
                continue
            # Titles are translated, while the person's name is kept verbatim.
            words = remove_entity_title_prefix(words)
            if words and " ".join(words).casefold() not in NON_NAME_PHRASES:
                names.add(" ".join(words))
                if len(words) > 1:
                    names.update(words)

        # The token-run logic above loses place names across lowercase
        # connectors ("between Harmony and Portersville").
        for match in name_run_re.finditer(text):
            words = token_re.findall(match.group())
            if len(words) < 2:
                continue
            while words and words[0].casefold() in ENTITY_SENTENCE_STARTERS:
                words.pop(0)
            words = remove_entity_title_prefix(words)
            if len(words) > 1 and " ".join(words).casefold() not in NON_NAME_PHRASES:
                names.add(" ".join(words))
                names.update(words)

        # A title-cased word in the middle of a sentence is a likely person or
        # place name even when it appears alone ("I know Jonah", "but Clarke").
        for match in token_re.finditer(text):
            prefix = text[:match.start()]
            sentence_tail = re.split(r"[.!?](?:[\"'’”)]*)\s+", prefix)[-1]
            if (
                re.search(r"[A-Za-z]", sentence_tail)
                and match.group().casefold() not in ENTITY_SENTENCE_STARTERS
                and match.group().casefold() not in ENTITY_TITLES
            ):
                mid_sentence_name_counts[match.group()] += 1

        # One-word literals in quotes (often passwords) and uppercase
        # abbreviations/codes are not ordinary translation text. Leave all-caps
        # speaker labels to prepare_cue_for_translation, which preserves them.
        names.update(
            match.group(1) for match in quoted_literal_re.finditer(text)
            if match.group(1).casefold() not in {
                "ok", "okay", "okey", "yes", "no", "hey", "hi", "hello", "thanks", "sorry", "yeah", "yep"
            }
        )
        for match in acronym_re.finditer(text):
            if match.group() in common_uppercase_words:
                continue
            if text[match.end():].lstrip().startswith(":"):
                continue
            names.add(match.group())
        single_word_cue = re.fullmatch(r"\s*[-–—]?\s*([A-Z][a-z][A-Za-z'’-]{0,30})\s*[.!?,…]?\s*", text)
        if single_word_cue and single_word_cue.group(1).casefold() not in ENTITY_SENTENCE_STARTERS:
            names.add(single_word_cue.group(1))
        for match in re.finditer(
            r"\b(?:hey|hi|hello|thanks|thank you|sorry|excuse me)[,!]?\s+([A-Z][a-z][A-Za-z'’-]{0,30})\b|"
            r"\b([A-Z][a-z][A-Za-z'’-]{0,30}),\s*(?:right|correct|okay|ok)\b",
            text,
            flags=re.IGNORECASE,
        ):
            candidate = next((group for group in match.groups() if group), None)
            if candidate and candidate[0].isupper() and candidate.casefold() not in ENTITY_SENTENCE_STARTERS:
                names.add(candidate)

        # A first name before a conjunction is often its own speaker/name.
        for match in re.finditer(
            r"\b([A-Z][a-z]+)\s+(?:and|or)\s+([A-Z][a-z]+(?:\s+[A-Z][a-z]+)+)\b", text
        ):
            first, rest = match.groups()
            if first.casefold() not in ENTITY_SENTENCE_STARTERS:
                names.add(first)
                names.add(rest)
                names.update(rest.split())

        for match in re.finditer(
            r"\b(?:Agent|Detective|Officer|Mr|Mrs|Ms|Dr|Sergeant|Chief)\.?\s+([A-Z][a-z]+)\b", text
        ):
            names.add(match.group(1))

    common_name_words = (
        ENGLISH_LEAK_WORDS
        | ENTITY_SENTENCE_STARTERS
        | ENTITY_TITLES
        | {"i", "me", "my", "mine", "you", "your", "yours", "we", "our", "ours", "he", "his", "him", "she", "her", "hers", "it", "its", "they", "them", "their", "us", "girl", "scout", "day", "all", "right", "fine", "maybe", "may", "will", "mark", "sorry", "god", "let", "see", "everybody", "everyone", "somebody", "someone", "papa", "goodbye", "bye", "farewell", "ma", "nobody", "anybody", "anyone", "everything", "something", "nothing", "operating", "again", "probably", "exactly", "awesome", "perfect", "visits", "first", "great", "sure", "now", "good", "cool", "board", "ask", "dear", "chairman", "vice", "timesthing", "timesreporter", "dropoutwith", "theranosis", "post-trial", "vaseline", "you're", "we're", "they're", "i'm", "it's", "that's", "there's", "what's", "who's", "don't", "didn't", "can't", "won't", "isn't", "aren't", "wasn't", "couldn't", "wouldn't", "shouldn't", "i've", "i'll", "he's", "she's"}
    )
    common_name_words.update(SHORT_SWEAR_TRANSLATIONS)
    common_name_words.update({"fucks", "fucked", "fucking", "shits", "shitty", "damned"})
    names.update(
        word for word, count in mid_sentence_name_counts.items()
        if count >= 2 and word.casefold() not in common_name_words
    )
    names = {
        name for name in names
        if (" " in name or name.casefold() not in common_name_words)
        and not re.search(r"[-–—]+$", name)
    }
    return sorted(names, key=lambda value: (-len(value), value.casefold()))


def protect_proper_names(text: str, names: list[str]) -> tuple[str, list[str]]:
    if not names:
        return text, []
    non_name_spans = [match.span() for match in NON_NAME_PHRASES_RE.finditer(text)]
    pattern = re.compile(
        r"(?<![\w])(?:" + "|".join(re.escape(name) for name in names) + r")(?![\w])"
    )
    protected: list[str] = []
    def mark(match: re.Match[str]) -> str:
        if any(start <= match.start() and match.end() <= end for start, end in non_name_spans):
            return match.group()
        protected.append(match.group())
        # Word-like tokens and names inside brackets were translated by Marian.
        # Numeric bracket markers survive decoding and map by occurrence order.
        return f"[[{len(protected) - 1}]]"
    return pattern.sub(mark, text), protected


def restore_proper_names(text: str, expected: list[str]) -> tuple[str, bool]:
    if not expected:
        return text, True
    expected_by_key = {name.casefold(): name for name in expected}
    # Marian occasionally drops or duplicates one bracket while copying a
    # numeric marker, so accept one to three unmatched brackets around it.
    token_re = re.compile(r"\[{1,3}\s*(\d+)\s*\]{0,3}")

    def restore_token(match: re.Match[str]) -> str:
        index = int(match.group(1))
        return expected[index] if index < len(expected) else match.group(0)

    text = token_re.sub(restore_token, text)
    marker_re = re.compile(r"[\[({]+([^\[\](){}\]\n]+?)[\])}]+")
    required_counts = Counter(name.casefold() for name in expected)
    restored_markers: list[str] = []

    def restore_marker(match: re.Match[str]) -> str:
        original = expected_by_key.get(match.group(1).strip().casefold())
        if original is not None:
            restored_markers.append(original)
            return original
        # If Marian mangles the digits but leaves the distinctive doubled
        # brackets around a short token (e.g. [[0]] -> [[å]]), restore the next
        # still-missing protected entity in source order.
        malformed_numeric_marker = (
            match.group(0).startswith("[[")
            and re.fullmatch(r"[A-Za-zÀ-ÿ]{1,8}", match.group(1).strip()) is not None
        )
        if malformed_numeric_marker:
            prefix = text[:match.start()] + "".join(restored_markers)
            for candidate in expected:
                required = required_counts[candidate.casefold()]
                present = len(re.findall(
                    r"(?<![\w])" + re.escape(candidate) + r"(?![\w])",
                    prefix,
                    flags=re.IGNORECASE,
                ))
                if present < required:
                    restored_markers.append(candidate)
                    return candidate
        return match.group(0)

    restored = marker_re.sub(restore_marker, text)
    # A few checkpoints omit the space immediately before a copied, hyphenated
    # codeword (for example "varther-A-nos"). Split that boundary so the codeword
    # is still recognized and retained as its own subtitle token.
    for name in expected:
        if "-" not in name:
            continue
        attached_name = re.compile(
            r"(?P<prefix>[\wÀ-ÿ])(?=" + re.escape(name) + r"(?![\w]))",
            flags=re.IGNORECASE,
        )
        restored = attached_name.sub(lambda match: match.group("prefix") + " ", restored)
    for folded, required in required_counts.items():
        name = expected_by_key[folded]
        # Norwegian possessive compounds use a trailing s without an apostrophe
        # ("Sams kone"), so count that as preserving the name too.
        possessive = re.fullmatch(r"(.+?)[\u0027\u2019]s", name, flags=re.IGNORECASE)
        accepted_forms = [name]
        if possessive:
            base = possessive.group(1)
            # English possessive names are translated with Norwegian -s; names
            # already ending in s take an apostrophe (e.g. James'). Names that
            # use English 's as the contraction "is" retain only the base name.
            accepted_forms.append(base)
            accepted_forms.append(base + ("'" if base.casefold().endswith(("s", "x", "z")) else "s"))
        elif name.casefold().endswith(("s", "x", "z")):
            accepted_forms.append(name + "'")
        else:
            # A possessive name may have been extracted without its English
            # apostrophe (Lance's -> Lance), while a valid Norwegian output is
            # then written as "Lances".
            accepted_forms.append(name + "s")
        actual = max((
            len(re.findall(r"(?<![\w])" + re.escape(form) + r"(?![\w])", restored, flags=re.IGNORECASE))
            for form in accepted_forms
        ), default=0)
        if actual < required:
            return restored, False
    return restored, True


def restore_dropped_trailing_possessive_name(
    source_text: str, translated_text: str, expected: list[str]
) -> tuple[str, bool]:
    """Restore a protected possessive name omitted from an interrupted cue ending.

    Marian can drop a protected marker at the end of a fragment such as
    "... telling Elizabeth's--". In that narrow case the translation already
    gives us a stable insertion point: before its trailing ellipsis/dash.
    """
    if not expected:
        return translated_text, False
    source = clean_subtitle_markup(source_text).strip()
    match = re.search(
        r"(?P<base>[A-Z][A-Za-z\u00c0-\u00d6\u00d8-\u00f6\u00f8-\u00ff0-9]*(?:['\u2019][A-Za-z\u00c0-\u00d6\u00d8-\u00f6\u00f8-\u00ff]+)*)"
        r"(?P<possessive>['\u2019]s)(?P<interrupt>(?:\s*(?:[-\u2013\u2014]+|\.{2,}|\u2026))+\s*)$",
        source,
    )
    if not match:
        return translated_text, False

    base = match.group("base")
    possessive_source = base + match.group("possessive")
    if not any(name.casefold() in {base.casefold(), possessive_source.casefold()} for name in expected):
        return translated_text, False

    norwegian_possessive = base + ("'" if base.casefold().endswith(("s", "x", "z")) else "s")
    existing_name = re.search(
        r"(?<![\w])" + re.escape(base) + r"(?:['\u2019]s|s|['\u2019])?(?![\w])",
        translated_text,
        flags=re.IGNORECASE,
    )
    if existing_name:
        return translated_text, False

    tail = re.search(r"(?:\s*(?:[-\u2013\u2014]+|\.{2,}|\u2026|[.!?]+))*\s*$", translated_text)
    if not tail:
        return translated_text, False
    body = translated_text[:tail.start()].rstrip()
    suffix = translated_text[tail.start():]
    if not body:
        return translated_text, False
    return f"{body} {norwegian_possessive}{suffix}", True


def restore_dropped_neighborhood_name(source_text: str, translated_text: str, expected: list[str]) -> tuple[str, bool]:
    """Restore a protected place name when the model keeps its neighborhood noun but drops the name."""
    plain_source = re.sub(r"</?[A-Za-z][^>]*>", "", source_text)
    for name in expected:
        source_place = re.compile(
            r"\b(?:in|at|near)\s+(?:the\s+)?" + re.escape(name)
            + r"\s+(?:neighbou?rhood|district|borough)\b",
            flags=re.IGNORECASE,
        )
        if not source_place.search(plain_source):
            continue
        if re.search(r"(?<![\w])" + re.escape(name) + r"(?![\w])", translated_text, flags=re.IGNORECASE):
            return translated_text, True
        norwegian_place = re.compile(r"\b(?:nabolaget|bydelen|distriktet|området)\b", flags=re.IGNORECASE)
        if norwegian_place.search(translated_text):
            repaired = norwegian_place.sub(lambda match: f"{match.group(0)} {name}", translated_text, count=1)
            return repaired, True
    return translated_text, False
def clean_subtitle_markup(text: str) -> str:
    """Remove SRT HTML styling tags before passing cue text to the MT model."""
    return re.sub(r"</?[A-Za-z][^>]*>", "", text).strip()


def is_non_speech_caption(text: str) -> bool:
    """Keep bracketed all-caps sound/music captions out of the translation model."""
    lines = [line.strip() for line in clean_subtitle_markup(text).splitlines() if line.strip()]
    if not lines:
        return False

    def is_caption_line(line: str) -> bool:
        line = re.sub(r"^[-–—]\s*", "", line)
        if len(line) < 3 or line[0] not in "([":
            return False
        expected_closing = ")" if line[0] == "(" else "]"
        if line[-1] != expected_closing:
            return False
        body = line[1:-1].strip()
        return bool(re.search(r"[A-Za-z]", body)) and body.upper() == body

    return all(is_caption_line(line) for line in lines)


def formatting_wrapper(text: str) -> str | None:
    """Preserve simple whole-cue SRT emphasis after stripping tags for translation."""
    match = re.fullmatch(r"\s*<(i|b|u)\b[^>]*>.*</\1>\s*", text, flags=re.IGNORECASE | re.DOTALL)
    return match.group(1).lower() if match else None


PARENTHETICAL_RE = re.compile(r"\([^()]*\)")


def remove_parenthetical_captions(text: str) -> str:
    """Remove text in parentheses, preserving any spoken words in the cue."""
    kept_lines = []
    for line in text.splitlines():
        line = PARENTHETICAL_RE.sub("", line)
        line = re.sub(r"\s+", " ", line).strip()
        line = re.sub(r"^[-–—]\s*$", "", line).strip()
        if clean_subtitle_markup(line):
            kept_lines.append(line)
    return "\n".join(kept_lines).strip()


def prepare_cue_for_translation(
    text: str, include_parenthetical_captions: bool = False
) -> tuple[str, bool, str | None, str | None, bool]:
    prepared_text = text if include_parenthetical_captions else remove_parenthetical_captions(text)
    removed = not clean_subtitle_markup(prepared_text)
    speaker_match = re.match(r"^\s*([-–—]\s*)?([A-Z][A-Z0-9 ]{0,24}:)\s*", prepared_text)
    speaker = None
    text_without_speaker = prepared_text
    if speaker_match:
        speaker = ("- " if speaker_match.group(1) else "") + speaker_match.group(2)
        text_without_speaker = prepared_text[speaker_match.end():]
        if speaker_match.group(1):
            text_without_speaker = re.sub(r"(?m)^\s*[-–—]\s*", "", text_without_speaker)
    cleaned = clean_subtitle_markup(text_without_speaker)
    # ASS positioning-only cues (for example {\an8}) are metadata, not dialogue.
    has_translatable_text = bool(re.sub(r"\{\\[^}]*\}", "", cleaned).strip())
    return (
        cleaned,
        removed or not has_translatable_text or is_non_speech_caption(prepared_text),
        formatting_wrapper(text_without_speaker),
        speaker,
        removed,
    )


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
        description="Hent eventuelt undertekster fra MKV og oversett lokalt med en seq2seq-modell."
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
    default_model_dir = Path(__file__).resolve().parent / "models" / "opus-mt-eng-nor"
    parser.add_argument(
        "--model", type=Path, default=default_model_dir,
        help="Lokal mappe med modellfilene (standard: %(default)s)",
    )
    parser.add_argument("--batch-size", type=int, default=16, help="Cues per GPU-batch (standard: %(default)s)")
    parser.add_argument("--beams", type=int, default=4, help="Beam search-størrelse (standard: %(default)s)")
    parser.add_argument("--device", choices=("cuda", "cpu"), default="cuda", help="Kjør lokalt på CUDA-GPU eller CPU (standard: cuda)")
    parser.add_argument("--cpu-threads", type=int, default=min(os.cpu_count() or 1, 20), help="Maks CPU-tråder for CPU-modus")
    parser.add_argument("--max-input-tokens", type=int, default=512)
    parser.add_argument("--max-new-tokens", type=int, default=64)
    parser.add_argument(
        "--minimum-acceptance-percent", type=float, default=95.0,
        help="Minste andel cues som må bestå kvalitetskontrollen; resten kan godtas som advarsler (standard: %(default)s)",
    )
    parser.add_argument("--overwrite", action="store_true", help="Erstatt en eksisterende outputfil")
    parser.add_argument(
        "--include-parenthetical-captions", action="store_true",
        help="Ta med lydeffekter og reaksjoner i parentes, som (GASPING)",
    )
    parser.add_argument("--live-events", action="store_true", help="Send kilde- og batchdata som JSON-linjer til GUI-et")
    parser.add_argument("--diagnostics-path", type=Path, help="Skriv lokal JSON-feilsøkingsrapport til denne filen")
    return parser.parse_args()


def translate_cues(
    args: argparse.Namespace,
    input_path: Path,
    output_path: Path,
    source_cues: list[Cue],
    newline: str,
) -> int:
    norwegian_targets = {"no", "nb", "nob", "nor", "norwegian", "nn", "nno"}
    validate_norwegian = str(args.target_language).strip().casefold() in norwegian_targets
    worker_model_override: str | None = None
    # The GPU worker stages each request in a local-gst-* temporary folder.
    # This lets an already-running worker adopt a configured local model without
    # restarting its HTTP service; normal desktop/CLI runs are unaffected.
    if input_path.parent.name.casefold().startswith("local-gst-") and not args.live_events:
        override_file = Path(__file__).resolve().parent / "worker-model.override"
        if override_file.is_file():
            configured_model = override_file.read_text(encoding="utf-8-sig").strip()
            candidate = Path(configured_model).expanduser()
            if not candidate.is_absolute():
                candidate = Path(__file__).resolve().parent / candidate
            candidate = candidate.resolve()
            if candidate.is_dir():
                args.model = candidate
                worker_model_override = str(candidate)
    # Block model-hub lookups: both weights and inference must remain local.
    os.environ["HF_HUB_OFFLINE"] = "1"
    diagnostics: dict[str, object] = {
        "started_at": datetime.datetime.now().astimezone().isoformat(timespec="seconds"),
        "status": "running",
        "input": str(input_path),
        "output": str(output_path),
        "source_language": args.source_language,
        "target_language": args.target_language,
        "track_index": args.track_index,
        "python": sys.version,
        "platform": platform.platform(),
        "model_directory": str(args.model.expanduser().resolve()),
        "worker_model_override": worker_model_override,
        "settings": {
            "batch_size": args.batch_size,
            "beams": args.beams,
            "max_input_tokens": args.max_input_tokens,
            "max_new_tokens": args.max_new_tokens,
            "no_repeat_ngram_size": 3,
            "repetition_penalty": 1.12,
            "include_parenthetical_captions": args.include_parenthetical_captions,
            "minimum_acceptance_percent": args.minimum_acceptance_percent,
        },
        "source": {
            "cue_count": len(source_cues),
            "empty_cues": sum(not cue.text.strip() for cue in source_cues),
            "cues_with_markup": sum(bool(re.search(r"</?[A-Za-z][^>]*>", cue.text)) for cue in source_cues),
        },
    }
    entity_names = collect_proper_names(source_cues)
    protected_names_by_cue: list[list[str]] = []
    prepared_cues = []
    for cue in source_cues:
        masked_text, protected_names = protect_proper_names(cue.text, entity_names)
        protected_names_by_cue.append(protected_names)
        prepared_cues.append(prepare_cue_for_translation(masked_text, args.include_parenthetical_captions))
    diagnostics["source"]["protected_proper_name_count"] = sum(bool(names) for names in protected_names_by_cue)
    skipped_indices = [index for index, (_, skip, _, _, _) in enumerate(prepared_cues) if skip]
    diagnostics["source"]["skipped_non_speech_cues"] = [source_cues[index].number for index in skipped_indices]
    removed_indices = [index for index, item in enumerate(prepared_cues) if item[4]]
    suppressed_indices = set(removed_indices)
    generated_parenthetical_indices: list[int] = []
    diagnostics["source"]["removed_parenthetical_cues"] = [source_cues[index].number for index in removed_indices]

    def save_diagnostics() -> str | None:
        if not args.diagnostics_path:
            return None
        try:
            report_path = args.diagnostics_path.expanduser().resolve()
            report_path.parent.mkdir(parents=True, exist_ok=True)
            diagnostics["finished_at"] = datetime.datetime.now().astimezone().isoformat(timespec="seconds")
            report_path.write_text(json.dumps(diagnostics, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
            if not args.live_events:
                print(f"Feilsøkingsrapport: {report_path}", flush=True)
            return str(report_path)
        except (OSError, TypeError, ValueError) as error:
            print(f"Kunne ikke lagre feilsøkingsrapport: {error}", file=sys.stderr, flush=True)
            return None

    def fail(message: str, *, details: dict[str, object] | None = None,
             affected: list[dict[str, str]] | None = None, exception: BaseException | None = None) -> int:
        diagnostics["status"] = "failed"
        diagnostics["failure"] = message
        if details:
            diagnostics.update(details)
        if exception is not None:
            diagnostics["exception_type"] = type(exception).__name__
            diagnostics["exception"] = str(exception)
            diagnostics["traceback"] = traceback.format_exc()
        report_path = save_diagnostics()
        if args.live_events:
            emit_event({
                "type": "failure",
                "message": message,
                "diagnostics_path": report_path,
                "items": affected or [],
            })
        if not args.live_events:
            print(message, file=sys.stderr, flush=True)
        return 2

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
        import transformers
        import accelerate
        import safetensors
        from accelerate import init_empty_weights
        from safetensors import safe_open
        from transformers import AutoConfig, AutoModelForSeq2SeqLM, AutoTokenizer
    except ImportError as error:
        return fail(f"Mangler Python-pakke: {error.name}. Installer pakkene fra requirements.txt.", exception=error)

    if args.device == "cuda" and not torch.cuda.is_available():
        return fail(
            "CUDA er ikke tilgjengelig i denne PyTorch-installasjonen. GPU-oversettelse avbrytes.",
            details={"runtime": {"torch": torch.__version__, "torch_cuda_build": torch.version.cuda}},
        )

    if args.device == "cpu":
        if args.cpu_threads < 1:
            return fail("CPU-trådantallet må være minst 1.")
        # Configure PyTorch before loading or running the model. One inter-op
        # thread avoids multiplying the intra-op pool on larger Jellyfin VMs.
        torch.set_num_threads(args.cpu_threads)
        try:
            torch.set_num_interop_threads(1)
        except RuntimeError:
            pass
        device = torch.device("cpu")
        device_name = f"CPU ({torch.get_num_threads()} tråder)"
        total_vram = None
    else:
        device = torch.device("cuda:0")
        device_name = torch.cuda.get_device_name(device)
        total_vram = torch.cuda.get_device_properties(device).total_memory
    diagnostics["runtime"] = {
        "torch": torch.__version__,
        "torch_cuda_build": torch.version.cuda,
        "transformers": transformers.__version__,
        "accelerate": accelerate.__version__,
        "safetensors": safetensors.__version__,
        "cuda_available": torch.cuda.is_available(),
        "device": str(device),
        "device_name": device_name,
        "cpu_threads": torch.get_num_threads() if device.type == "cpu" else None,
        "total_vram_bytes": total_vram,
    }
    if device.type == "cuda":
        print(f"GPU: {device_name} ({total_vram / 1024**3:.2f} GiB)")
    else:
        print(f"CPU: {device_name}")
    print(f"Kilde: {input_path} | {len(source_cues)} cues | batch: {args.batch_size}")

    model_dir = args.model.expanduser().resolve()
    if not model_dir.is_dir():
        return fail(f"Fant ikke lokal modellmappe: {model_dir}")
    print("Modellmodus: kun lokale filer; nettverkstilgang er deaktivert.")

    model_start = time.perf_counter()
    try:
        tokenizer = AutoTokenizer.from_pretrained(model_dir, local_files_only=True)
        config = AutoConfig.from_pretrained(model_dir, local_files_only=True)
        if config.model_type == "marian":
            # Marian checkpoints are much smaller than MADLAD and are loaded
            # through Transformers' native local checkpoint support.
            weights_path = next(
                (candidate for candidate in (model_dir / "model.safetensors", model_dir / "pytorch_model.bin") if candidate.is_file()),
                None,
            )
            if weights_path is None:
                raise FileNotFoundError(f"Fant ingen støttet Marian-vektsfil i {model_dir}")
            diagnostics["model"] = {
                "config_model_type": config.model_type,
                "config_architectures": getattr(config, "architectures", None),
                "weight_file": str(weights_path),
                "weight_file_bytes": weights_path.stat().st_size,
            }
            model = AutoModelForSeq2SeqLM.from_pretrained(
                model_dir,
                local_files_only=True,
                dtype=torch.float16 if device.type == "cuda" else torch.float32,
            ).to(device)
        else:
            with init_empty_weights():
                model = AutoModelForSeq2SeqLM.from_config(config)
            weights_path = model_dir / "model.safetensors"
            if not weights_path.is_file():
                raise FileNotFoundError(f"Fant ikke modellvektene: {weights_path}")
            diagnostics["model"] = {
                "config_model_type": config.model_type,
                "config_architectures": getattr(config, "architectures", None),
                "weight_file": str(weights_path),
                "weight_file_bytes": weights_path.stat().st_size,
            }
            with safe_open(weights_path, framework="np", device="cpu") as checkpoint:
                diagnostics["model"]["weight_tensor_count"] = len(checkpoint.keys())
                diagnostics["model"]["weight_dtype"] = checkpoint.get_slice(checkpoint.keys()[0]).get_dtype()
            # Large T5 checkpoints can exceed the workstation's free system RAM.
            # Stream one tensor at a time from the memory-mapped safetensors file
            # directly to CUDA instead of building a full CPU copy first.
            if device.type == "cuda":
                assigned_keys: set[str] = set()
                with safe_open(weights_path, framework="np", device="cpu") as checkpoint:
                    for key in checkpoint.keys():
                        tensor = torch.from_numpy(checkpoint.get_tensor(key)).to(device)
                        state = {key: tensor}
                        if key == "decoder.embed_tokens.weight":
                            # The published checkpoint stores the shared T5
                            # embedding once under decoder.embed_tokens.weight.
                            state["shared.weight"] = tensor
                            state["encoder.embed_tokens.weight"] = tensor
                        incompatible = model.load_state_dict(state, strict=False, assign=True)
                        if incompatible.unexpected_keys:
                            raise RuntimeError(
                                f"Uventet vekttensor i MADLAD-checkpoint: {incompatible.unexpected_keys}"
                            )
                        assigned_keys.update(state)
                        del state, tensor
                missing_keys = set(model.state_dict()) - assigned_keys
                if missing_keys:
                    raise RuntimeError(
                        "MADLAD-checkpoint mangler vekttensorer: "
                        + ", ".join(sorted(missing_keys)[:12])
                    )
            else:
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
            model = model.to(device)
        model.eval()
    except Exception as error:  # Model-loading errors vary across Transformers versions.
        return fail(f"Kunne ikke laste modellen: {error}", exception=error)
    model_load_seconds = time.perf_counter() - model_start
    diagnostics["model_load_seconds"] = round(model_load_seconds, 3)

    target_tag = args.target_language.strip().lower()
    if target_tag in {"nb", "nob", "nor", "norwegian bokmål"}:
        target_tag = "nob" if config.model_type == "marian" else "no"
    target_tag = re.sub(r"[^a-z0-9_-]", "", target_tag)
    if not target_tag:
        return fail("Ugyldig målspråkkode.")
    prompt_prefix = f">>{target_tag}<< " if config.model_type == "marian" else f"<2{target_tag}> "
    direct_translations: dict[int, str] = {}
    for index, (cleaned, skip, wrapper, speaker, removed) in enumerate(prepared_cues):
        if not validate_norwegian or skip or removed:
            continue
        direct = translate_short_acknowledgement(cleaned)
        if direct is None:
            direct = translate_safe_named_phrase(cleaned, entity_names)
        if direct is None:
            continue
        direct, direct_names_restored = restore_proper_names(direct, protected_names_by_cue[index])
        if protected_names_by_cue[index] and not direct_names_restored:
            continue
        if wrapper:
            direct = f"<{wrapper}>{direct}</{wrapper}>"
        if speaker:
            direct = f"{speaker} {direct}"
        direct_translations[index] = direct
    if direct_translations:
        diagnostics["deterministic_short_cues"] = [
            {
                "number": source_cues[index].number,
                "source_text": source_cues[index].text,
                "translated_text": translation,
                "rule": "isolated_acknowledgement",
            }
            for index, translation in direct_translations.items()
        ]

    prompts: list[str | None] = [
        None if skip or index in direct_translations else f"{prompt_prefix}{cleaned}"
        for index, (cleaned, skip, _wrapper, _speaker, _removed) in enumerate(prepared_cues)
    ]

    token_lengths = [0] * len(prompts)

    def update_token_count() -> None:
        translated_token_lengths = [
            token_lengths[index]
            for index, prompt in enumerate(prompts)
            if prompt is not None and token_lengths[index] > 0
        ]
        diagnostics["source"]["token_count"] = {
            "translated_cues": sum(prompt is not None for prompt in prompts),
            "tokenized_cues": len(translated_token_lengths),
            "skipped_cues": len(skipped_indices),
            "minimum": min(translated_token_lengths, default=0),
            "median": statistics.median(translated_token_lengths) if translated_token_lengths else 0,
            "maximum": max(translated_token_lengths, default=0),
        }

    update_token_count()

    translated_texts = [
        "" if prepared_cues[index][4]
        else direct_translations[index] if index in direct_translations
        else cue.text if prompts[index] is None
        else ""
        for index, cue in enumerate(source_cues)
    ]
    unchanged_fallbacks: list[str] = []
    linewise_recoveries: list[dict[str, object]] = []
    inference_start = time.perf_counter()
    if device.type == "cuda":
        torch.cuda.reset_peak_memory_stats(device)
    unresolved_quality_issues: dict[int, list[dict[str, object]]] = {}
    try:
        for start in range(0, len(prompts), args.batch_size):
            batch_end = min(start + args.batch_size, len(prompts))
            indexes = [index for index in range(start, batch_end) if prompts[index] is not None]
            batch_outputs: list[str] = []
            generated = None
            if indexes:
                encoded = tokenizer([prompts[index] for index in indexes], return_tensors="pt", padding=True, truncation=False)
                batch_token_lengths = [int(length) for length in encoded["attention_mask"].sum(dim=1).tolist()]
                for index, token_length in zip(indexes, batch_token_lengths, strict=True):
                    token_lengths[index] = token_length
                update_token_count()
                too_long = [
                    (index, token_length)
                    for index, token_length in zip(indexes, batch_token_lengths, strict=True)
                    if token_length > args.max_input_tokens
                ]
                if too_long:
                    first_index, first_length = too_long[0]
                    return fail(
                        f"Cue {source_cues[first_index].number} er {first_length} tokens, over grensen på {args.max_input_tokens}. "
                        "Ingen tekst ble trunkert eller output skrevet.",
                        details={
                            "overlong_cues": [
                                {"number": source_cues[index].number, "token_count": token_length}
                                for index, token_length in too_long
                            ]
                        },
                    )
                encoded = encoded.to(device)
                with torch.inference_mode():
                    generated = model.generate(
                        **encoded,
                        num_beams=args.beams,
                        max_new_tokens=args.max_new_tokens,
                        do_sample=False,
                        no_repeat_ngram_size=3,
                        repetition_penalty=1.12,
                    )
                batch_outputs = tokenizer.batch_decode(generated, skip_special_tokens=True)
            generation_issues: list[dict[str, object]] = []
            for offset, (index, output) in enumerate(zip(indexes, batch_outputs, strict=True)):
                output = output.strip()
                output, names_restored = restore_proper_names(output, protected_names_by_cue[index])
                if not names_restored:
                    output, _ = restore_dropped_neighborhood_name(
                        source_cues[index].text, output, protected_names_by_cue[index]
                    )
                    output, _ = restore_dropped_trailing_possessive_name(
                        source_cues[index].text, output, protected_names_by_cue[index]
                    )
                    output, names_restored = restore_proper_names(
                        output, protected_names_by_cue[index]
                    )
                if output.startswith("-") and (
                    prepared_cues[index][3] is not None
                    or not source_cues[index].text.lstrip().startswith("-")
                ):
                    output = output[1:].lstrip()
                if not output:
                    generation_issues.append({
                        "cue_index": index,
                        "number": source_cues[index].number,
                        "timing": source_cues[index].timing,
                        "source_text": source_cues[index].text,
                        "model_input": prepared_cues[index][0],
                        "generated_text": "",
                        "input_tokens": token_lengths[index],
                        "output_tokens": 0,
                        "ended_with_eos": False,
                        "eos_token_position": None,
                        "issue": "invalid_short_output",
                    })
                    continue
                if not args.include_parenthetical_captions:
                    output = remove_parenthetical_captions(output)
                    if not output:
                        translated_texts[index] = ""
                        suppressed_indices.add(index)
                        generated_parenthetical_indices.append(index)
                        continue
                wrapper = prepared_cues[index][2]
                speaker = prepared_cues[index][3]
                if wrapper:
                    output = f"<{wrapper}>{output}</{wrapper}>"
                if speaker:
                    output = f"{speaker} {output}"
                output = repair_known_translation_errors(source_cues[index].text, output)
                output = restore_dropped_subject_name(source_cues[index].text, output, entity_names)
                output = preserve_ass_tags(source_cues[index].text, output)
                translated_texts[index] = output
                token_sequence = [int(token) for token in generated[offset].tolist()]
                eos_ids = model.generation_config.eos_token_id
                eos_ids = eos_ids if isinstance(eos_ids, (list, tuple)) else [eos_ids]
                eos_ids = {int(token) for token in eos_ids if token is not None}
                eos_positions = [position for position, token in enumerate(token_sequence[1:], start=1) if token in eos_ids]
                ended_with_eos = bool(eos_positions)
                generated_token_count = max(0, (eos_positions[0] - 1) if ended_with_eos else len(token_sequence) - 1)
                if detect_repetition(output):
                    generation_issues.append({
                        "cue_index": index,
                        "number": source_cues[index].number,
                        "timing": source_cues[index].timing,
                        "source_text": source_cues[index].text,
                        "model_input": prepared_cues[index][0],
                        "generated_text": output,
                        "input_tokens": token_lengths[index],
                        "output_tokens": generated_token_count,
                        "ended_with_eos": ended_with_eos,
                        "eos_token_position": eos_positions[0] if ended_with_eos else None,
                        "issue": "repetition",
                    })
                if validate_norwegian and detect_english_leakage(output):
                    generation_issues.append({
                        "cue_index": index,
                        "number": source_cues[index].number,
                        "timing": source_cues[index].timing,
                        "source_text": source_cues[index].text,
                        "model_input": prepared_cues[index][0],
                        "generated_text": output,
                        "input_tokens": token_lengths[index],
                        "output_tokens": generated_token_count,
                        "ended_with_eos": ended_with_eos,
                        "eos_token_position": eos_positions[0] if ended_with_eos else None,
                        "issue": "english_language_leakage",
                    })
                if loses_dialogue_lines(source_cues[index].text, output):
                    generation_issues.append({
                        "cue_index": index,
                        "number": source_cues[index].number,
                        "timing": source_cues[index].timing,
                        "source_text": source_cues[index].text,
                        "model_input": prepared_cues[index][0],
                        "generated_text": output,
                        "input_tokens": token_lengths[index],
                        "output_tokens": generated_token_count,
                        "ended_with_eos": ended_with_eos,
                        "eos_token_position": eos_positions[0] if ended_with_eos else None,
                        "issue": "missing_dialogue_line",
                    })
                if validate_norwegian and loses_sentence_content(source_cues[index].text, output):
                    generation_issues.append({
                        "cue_index": index,
                        "number": source_cues[index].number,
                        "timing": source_cues[index].timing,
                        "source_text": source_cues[index].text,
                        "model_input": prepared_cues[index][0],
                        "generated_text": output,
                        "input_tokens": token_lengths[index],
                        "output_tokens": generated_token_count,
                        "ended_with_eos": ended_with_eos,
                        "eos_token_position": eos_positions[0] if ended_with_eos else None,
                        "issue": "missing_sentence_content",
                    })
                if has_unusable_short_output(source_cues[index].text, output):
                    generation_issues.append({
                        "cue_index": index,
                        "number": source_cues[index].number,
                        "timing": source_cues[index].timing,
                        "source_text": source_cues[index].text,
                        "model_input": prepared_cues[index][0],
                        "generated_text": output,
                        "input_tokens": token_lengths[index],
                        "output_tokens": generated_token_count,
                        "ended_with_eos": ended_with_eos,
                        "eos_token_position": eos_positions[0] if ended_with_eos else None,
                        "issue": "invalid_short_output",
                    })
                if not names_restored:
                    diagnostics.setdefault("proper_name_warnings", []).append({
                        "number": source_cues[index].number,
                        "source_text": source_cues[index].text,
                        "generated_text": output,
                        "protected_names": protected_names_by_cue[index],
                    })
                    generation_issues.append({
                        "cue_index": index,
                        "number": source_cues[index].number,
                        "timing": source_cues[index].timing,
                        "source_text": source_cues[index].text,
                        "model_input": prepared_cues[index][0],
                        "generated_text": output,
                        "input_tokens": token_lengths[index],
                        "output_tokens": generated_token_count,
                        "ended_with_eos": ended_with_eos,
                        "issue": "proper_name_loss",
                    })
                if generated_token_count >= args.max_new_tokens and not ended_with_eos:
                    generation_issues.append({
                        "cue_index": index,
                        "number": source_cues[index].number,
                        "timing": source_cues[index].timing,
                        "source_text": source_cues[index].text,
                        "model_input": prepared_cues[index][0],
                        "generated_text": output,
                        "input_tokens": token_lengths[index],
                        "output_tokens": generated_token_count,
                        "ended_with_eos": False,
                        "eos_token_position": None,
                        "issue": "max_new_tokens_without_eos",
                    })
            if generation_issues:
                issues_by_index = {}
                for issue in generation_issues:
                    index = int(issue["cue_index"])
                    previous = issues_by_index.get(index)
                    if (
                        previous is None
                        or issue["issue"] == "repetition"
                        or (
                            issue["issue"] == "missing_sentence_content"
                            and previous["issue"] != "repetition"
                        )
                    ):
                        issues_by_index[index] = issue

                # Retry suspect multi-line cues line by line. This handles
                # decoder loops, English leakage, and dropped speaker turns.
                recovered_indexes: set[int] = set()
                for index, issue in list(issues_by_index.items()):
                    raw_source_lines = [line.strip() for line in source_cues[index].text.splitlines() if line.strip()]
                    # Physical newlines can be subtitle wrapping rather than
                    # separate speaker turns. Split retries only for dashed dialogue.
                    if len(raw_source_lines) > 1 and any(
                        re.match(r"^[-–—]\s+", line) for line in raw_source_lines
                    ):
                        source_lines = raw_source_lines
                    else:
                        source_lines = [" ".join(raw_source_lines)]
                        sentences = split_sentences(source_lines[0])
                        if (
                            len(sentences) > 1
                            and any(
                                re.search(r"(?<![\w])" + re.escape(name) + r"(?![\w])", part, flags=re.IGNORECASE)
                                for name in entity_names
                                for part in sentences
                            )
                        ):
                            source_lines = sentences
                    expanded_lines: list[str] = []
                    split_sentence_groups = len(source_lines) > 1
                    for source_line in source_lines:
                        marker = re.match(r"^([-–—])\s+", source_line)
                        body = re.sub(r"^[-–—]\s+", "", source_line).strip()
                        sentences = split_sentences(body)
                        if len(sentences) > 1:
                            split_sentence_groups = True
                        for sentence_index, sentence in enumerate(sentences):
                            prefix = f"{marker.group(1)} " if marker and sentence_index == 0 else ""
                            expanded_lines.append(prefix + sentence)
                    if expanded_lines:
                        source_lines = expanded_lines
                    if not source_lines or issue["issue"] not in {
                        "repetition", "english_language_leakage", "missing_dialogue_line",
                        "invalid_short_output", "proper_name_loss", "missing_sentence_content",
                    }:
                        continue
                    retry_outputs: list[str] = []
                    retry_debug: list[dict[str, object]] = []
                    retry_ok = True
                    for source_line in source_lines:
                        marker = re.match(r"^([-–—])\s+", source_line)
                        raw_line_text = re.sub(r"^[-–—]\s+", "", source_line).strip()
                        line_wrapper = formatting_wrapper(raw_line_text) or prepared_cues[index][2]
                        ass_tags = re.findall(r"\{\\[^}]*\}", raw_line_text)
                        line_text = clean_subtitle_markup(re.sub(r"\{\\[^}]*\}", "", raw_line_text)).strip()
                        music_match = re.match(r"^([♪♫]+\s*)", line_text)
                        music_marker = music_match.group(1) if music_match else ""
                        if music_marker:
                            line_text = line_text[len(music_marker):].lstrip()
                        # Retry all known names and codewords, including one-word
                        # names that are often lost from short dialogue turns.
                        retry_names = entity_names
                        masked_line, line_names = protect_proper_names(line_text, retry_names)
                        direct_line = translate_short_acknowledgement(line_text) if validate_norwegian else None
                        if direct_line is None and validate_norwegian:
                            direct_line = translate_safe_named_phrase(line_text, retry_names)
                        if direct_line is not None:
                            translated_line = direct_line
                            line_names_restored = True
                        else:
                            line_input = f"{prompt_prefix}{masked_line}"
                            encoded_line = tokenizer(
                                [line_input], return_tensors="pt", padding=True, truncation=False
                            ).to(device)
                            with torch.inference_mode():
                                generated_line = model.generate(
                                    **encoded_line,
                                    num_beams=1,
                                    max_new_tokens=min(args.max_new_tokens, 96),
                                    do_sample=False,
                                    no_repeat_ngram_size=3,
                                    repetition_penalty=1.12,
                                )
                            translated_line = tokenizer.decode(
                                generated_line[0], skip_special_tokens=True
                            ).strip()
                            translated_line, line_names_restored = restore_proper_names(translated_line, line_names)
                            if line_names and not line_names_restored:
                                # Some Marian generations delete even numeric
                                # markers. Retry once with the literal source
                                # names exposed, then accept only if every name
                                # survives unchanged.
                                literal_input = f"{prompt_prefix}{line_text}"
                                literal_encoded = tokenizer(
                                    [literal_input], return_tensors="pt", padding=True, truncation=False
                                ).to(device)
                                with torch.inference_mode():
                                    literal_generated = model.generate(
                                        **literal_encoded,
                                        num_beams=max(4, args.beams),
                                        max_new_tokens=min(args.max_new_tokens, 96),
                                        do_sample=False,
                                        no_repeat_ngram_size=3,
                                        repetition_penalty=1.12,
                                    )
                                literal_text = tokenizer.decode(
                                    literal_generated[0], skip_special_tokens=True
                                ).strip()
                                literal_text, literal_names_restored = restore_proper_names(
                                    literal_text, line_names
                                )
                                if literal_names_restored:
                                    translated_line = literal_text
                                    line_names_restored = True
                        if translated_line.startswith("-"):
                            translated_line = translated_line[1:].lstrip()
                        translated_line = repair_known_translation_errors(line_text, translated_line)
                        translated_line = restore_dropped_subject_name(line_text, translated_line, retry_names)
                        translated_line, _ = restore_dropped_trailing_possessive_name(
                            line_text, translated_line, line_names
                        )
                        translated_line, line_names_restored = restore_proper_names(
                            translated_line, line_names
                        )
                        retry_debug.append({
                            "source_line": line_text,
                            "generated_line": translated_line,
                            "repetition": detect_repetition(translated_line),
                            "proper_names_restored": line_names_restored,
                        })
                        if (
                            not line_names_restored
                            or not translated_line
                            or detect_repetition(translated_line)
                            or has_unusable_short_output(line_text, translated_line)
                            or (validate_norwegian and detect_english_leakage(translated_line))
                        ):
                            retry_ok = False
                            break
                        if not args.include_parenthetical_captions:
                            translated_line = remove_parenthetical_captions(translated_line)
                        if not translated_line:
                            retry_ok = False
                            break
                        if line_wrapper:
                            translated_line = f"<{line_wrapper}>{translated_line}</{line_wrapper}>"
                        if ass_tags:
                            translated_line = "".join(ass_tags) + translated_line
                        if music_marker:
                            translated_line = music_marker + translated_line
                        retry_outputs.append(f"{marker.group(1)} {translated_line}" if marker else translated_line)
                    if retry_ok and len(retry_outputs) == len(source_lines):
                        if split_sentence_groups:
                            merged_outputs: list[str] = []
                            for retry_output in retry_outputs:
                                if merged_outputs and not re.match(r"^[-–—]\s+", retry_output):
                                    merged_outputs[-1] += " " + retry_output.strip()
                                else:
                                    merged_outputs.append(retry_output)
                            retry_outputs = merged_outputs
                        candidate_translation = "\n".join(retry_outputs)
                        candidate_issues = []
                        if loses_dialogue_lines(source_cues[index].text, candidate_translation):
                            candidate_issues.append("missing_dialogue_line")
                        if (
                            loses_sentence_content(source_cues[index].text, candidate_translation)
                            and not split_sentence_groups
                        ):
                            candidate_issues.append("missing_sentence_content")
                        if validate_norwegian and detect_english_leakage(candidate_translation):
                            candidate_issues.append("english_language_leakage")
                        if detect_repetition(candidate_translation):
                            candidate_issues.append("repetition")
                        if candidate_issues:
                            diagnostics.setdefault("linewise_candidate_failures", []).append({
                                "number": source_cues[index].number,
                                "candidate_text": candidate_translation,
                                "issues": candidate_issues,
                                "line_results": retry_debug,
                            })
                            retry_ok = False
                        else:
                            translated_texts[index] = candidate_translation
                            recovered_indexes.add(index)
                            issues_by_index.pop(index, None)
                            if "proper_name_warnings" in diagnostics:
                                diagnostics["proper_name_warnings"] = [
                                    warning for warning in diagnostics["proper_name_warnings"]
                                    if warning.get("number") != source_cues[index].number
                                ]
                            linewise_recoveries.append({
                                "number": source_cues[index].number,
                                "timing": source_cues[index].timing,
                                "reason": issue["issue"],
                                "source_text": source_cues[index].text,
                                "translated_text": translated_texts[index],
                                "strategy": "linewise_greedy_retry",
                            })
                    else:
                        diagnostics.setdefault("linewise_retry_failures", []).append({
                            "number": source_cues[index].number,
                            "retry_lines": retry_debug,
                        })
                if linewise_recoveries:
                    diagnostics["linewise_recoveries"] = linewise_recoveries
                generation_issues = [
                    issue for issue in generation_issues
                    if int(issue["cue_index"]) in issues_by_index
                ]
            if generation_issues:
                for issue in generation_issues:
                    index = int(issue["cue_index"])
                    unresolved_quality_issues.setdefault(index, []).append(issue)
            done = batch_end
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
                            "output": "" if start + offset in suppressed_indices else translated_texts[start + offset],
                        }
                        for offset in range(batch_end - start)
                    ],
                })
            else:
                print(f"Oversatt {done}/{len(prompts)} cues", flush=True)
    except Exception as error:
        return fail(
            f"Oversettelsen feilet; kildemediet er urørt: {error}",
            details={"failure_stage": "generation", "failed_batch_start": start if "start" in locals() else None},
            exception=error,
        )

    inference_seconds = time.perf_counter() - inference_start
    if len(translated_texts) != len(source_cues):
        return fail("Modellen returnerte feil antall cues; output skrives ikke.")
    warning_cue_count = len(unresolved_quality_issues)
    acceptance_percent = (
        100.0 * (len(source_cues) - warning_cue_count) / len(source_cues)
        if source_cues else 100.0
    )
    diagnostics["quality_acceptance"] = {
        "minimum_percent": args.minimum_acceptance_percent,
        "actual_percent": round(acceptance_percent, 2),
        "flagged_cue_count": warning_cue_count,
        "total_cue_count": len(source_cues),
        "accepted": acceptance_percent >= args.minimum_acceptance_percent,
    }
    diagnostics["quality_warnings"] = [
        issue
        for cue_issues in unresolved_quality_issues.values()
        for issue in cue_issues
    ]
    if acceptance_percent < args.minimum_acceptance_percent:
        return fail(
            f"Oversettelsen fullførte, men {acceptance_percent:.1f} % bestod kvalitetskontrollen; "
            f"minstekravet er {args.minimum_acceptance_percent:.1f} %. SRT-filen ble ikke skrevet.",
            details={"failure_stage": "acceptance_threshold"},
        )
    contextual_repairs = repair_contextual_cue_pairs(source_cues, translated_texts) if validate_norwegian else []
    translated_cues = [
        Cue(str(number), source.timing, text.strip())
        for number, (source, text) in enumerate(
            ((source, text) for source, text in zip(source_cues, translated_texts, strict=True) if text.strip()),
            start=1,
        )
    ]
    if any(not cue.text for cue in translated_cues):
        empty_cues = [cue.number for cue in translated_cues if not cue.text]
        return fail(f"Minst ett cue ble tomt etter oversettelse: {', '.join(empty_cues[:12])}. Output skrives ikke.")
    print(
        f"Kvalitetskontroll: {acceptance_percent:.1f} % godkjent; "
        f"{warning_cue_count} cues har varsler (grense {args.minimum_acceptance_percent:.1f} %).",
        flush=True,
    )

    try:
        write_srt(output_path, translated_cues, newline)
    except OSError as error:
        return fail(f"Kunne ikke skrive output: {error}", exception=error)

    allocated = torch.cuda.max_memory_allocated(device) if device.type == "cuda" else None
    reserved = torch.cuda.max_memory_reserved(device) if device.type == "cuda" else None
    print(f"Skrev: {output_path}")
    print(f"Modellinnlasting: {model_load_seconds:.1f} s")
    print(f"Oversettelse: {inference_seconds:.1f} s ({len(source_cues) / max(inference_seconds, 1e-9):.2f} cues/s)")
    if device.type == "cuda":
        print(f"Maks GPU-minne allokert: {allocated / 1024**3:.2f} GiB")
        print(f"Maks GPU-minne reservert: {reserved / 1024**3:.2f} GiB")
    print("Tidsstempler er kopiert direkte fra kildens SRT.")
    if suppressed_indices:
        print(f"Fjernet parentesinnhold i {len(suppressed_indices)} cues etter innstilling.")
    if unchanged_fallbacks:
        print(f"Beholdt kildeteksten i {len(unchanged_fallbacks)} cues som reserve: {', '.join(unchanged_fallbacks[:12])}")

    report = {
        "model": str(model_dir),
        "device": device_name,
        "input": str(input_path),
        "output": str(output_path),
        "cue_count": len(source_cues),
        "quality_acceptance": diagnostics["quality_acceptance"],
        "quality_warning_cue_count": warning_cue_count,
        "batch_size": args.batch_size,
        "beams": args.beams,
        "model_load_seconds": round(model_load_seconds, 3),
        "inference_seconds": round(inference_seconds, 3),
        "cues_per_second": round(len(source_cues) / max(inference_seconds, 1e-9), 3),
        "peak_gpu_allocated_gib": round(allocated / 1024**3, 3) if allocated is not None else None,
        "peak_gpu_reserved_gib": round(reserved / 1024**3, 3) if reserved is not None else None,
        "unchanged_fallback_cues": unchanged_fallbacks,
        "suppressed_parenthetical_cues": [source_cues[index].number for index in sorted(suppressed_indices)],
        "contextual_repairs": contextual_repairs,
    }
    diagnostics["status"] = "completed"
    diagnostics["contextual_repairs"] = contextual_repairs
    diagnostics["performance"] = report
    diagnostics_path = save_diagnostics()
    if args.live_events and diagnostics_path:
        print(f"Feilsøkingsrapport: {diagnostics_path}", flush=True)
    return 0


def main() -> int:
    args = parse_args()
    if args.batch_size < 1 or args.beams < 1:
        print("Feil: batch-størrelse og beams må være minst 1.", file=sys.stderr)
        return 2
    if not 0 <= args.minimum_acceptance_percent <= 100:
        print("Feil: minste akseptprosent må være mellom 0 og 100.", file=sys.stderr)
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









