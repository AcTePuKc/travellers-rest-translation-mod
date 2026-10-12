#!/usr/bin/env python3
"""Generate the Bulgarian labels file for the Translation Loader plugin.

The canonical translation dataset lives in the separate Crowdin working
repository and is the single source of truth. This script never edits it; it
only renders the plugin format.

Two escaping rules matter, because the plugin decodes them at load time:

* Newlines become the literal two-character sequence ``\\n`` and tabs become
  ``\\t``, matching ``DecodeValue`` in ``Plugin.cs``.
* No other backslash is touched, so a value that legitimately contains a
  backslash is preserved exactly.

Item keys are emitted twice: once bare (``item_name_625``) and once with the
``Items/`` sheet prefix (``Items/item_name_625``). Which form the game asks for
at runtime varies by call site, and the prefix alias costs a few hundred
kilobytes while removing the ambiguity.
"""

from __future__ import annotations

import argparse
import csv
import json
import re
from collections import OrderedDict
from pathlib import Path

ITEM_KEY_RE = re.compile(r"^item_(?:name|description)_-?\d+$")

# Unity rich-text tags that never need a closing partner.
SELF_CLOSING_TAGS = {"br", "sprite"}
RICH_TAG_RE = re.compile(r"<(/?)([A-Za-z][A-Za-z0-9]*)([^>]*)>")


def canonical_key(record: dict) -> str:
    """Return the key exactly as the source dataset stores it.

    Keys are never stripped: Crowdin preserves a developer typo such as
    ``" \\n         \\ntutorialPopUp103"`` as a distinct key, and normalising it
    would collide with the clean ``tutorialPopUp103`` record.
    """
    return record.get("key") or ""


def repair_unclosed_rich_text_tags(value: str) -> str:
    """Close any rich-text tag the developers left open.

    The Crowdin source itself contains unclosed and mismatched Unity tags (240 of
    the 9,778 records), so the XLIFF upload keeps them exactly as the developers
    wrote them. The labels file the plugin loads gets them balanced instead,
    because the game renders the text and an unclosed tag would leak its effect.

    Unity's own sample strings contain swapped pairs such as
    ``<b><color=#5B36AE>text</b></color>``. A closing tag that does not match the
    innermost open tag is dropped after closing whatever it does match, which
    yields ``<b><color=#5B36AE>text</color></b>`` -- the effect the developers
    meant, with no cascading closers.
    """
    result: list[str] = []
    open_tags: list[str] = []
    position = 0

    for match in RICH_TAG_RE.finditer(value):
        result.append(value[position:match.start()])
        is_closing = match.group(1) == "/"
        name = match.group(2).lower()
        position = match.end()

        if name in SELF_CLOSING_TAGS:
            result.append(match.group(0))
            continue

        if is_closing:
            if name not in open_tags:
                # stray closing tag with no opening partner: drop it
                continue
            index = len(open_tags) - 1 - open_tags[::-1].index(name)
            # close everything opened after the matching tag, innermost first
            for _ in range(len(open_tags) - 1, index, -1):
                result.append("</%s>" % open_tags.pop())
            result.append("</%s>" % open_tags.pop())
            continue

        result.append(match.group(0))
        open_tags.append(name)

    result.append(value[position:])
    result.extend("</%s>" % name for name in reversed(open_tags))
    return "".join(result)


def escape_value(value: str) -> str:
    """Render a translation on one physical line the way the plugin expects."""
    return (
        value.replace("\r\n", "\n")
        .replace("\r", "\n")
        .replace("\n", "\\n")
        .replace("\t", "\\t")
    )


def load_records(path: Path) -> list[dict]:
    with path.open(encoding="utf-8-sig", newline="") as handle:
        reader = csv.DictReader(handle)
        reader.fieldnames = [(name or "").strip().lstrip("\ufeff").strip('"') for name in reader.fieldnames]
        records = []
        for row in reader:
            if None in row:
                raise SystemExit(f"Ragged CSV row in {path}")
            records.append(row)
        return records


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--translations", required=True, help="Canonical bulgarian_translations.csv")
    parser.add_argument("--out", required=True, help="Output labels.<lang>.txt")
    parser.add_argument("--item-prefix", default="Items", help="Sheet prefix used for the item alias (default: Items)")
    parser.add_argument("--no-item-alias", action="store_true", help="Emit only the bare item keys")
    parser.add_argument("--no-tag-repair", action="store_true",
                        help="Keep unclosed Unity rich-text tags exactly as the source has them")
    parser.add_argument("--report", help="Optional JSON verification report path")
    args = parser.parse_args()

    source = Path(args.translations)
    output = Path(args.out)
    records = load_records(source)

    entries: "OrderedDict[str, str]" = OrderedDict()
    skipped_empty = 0
    skipped_duplicate = 0
    duplicates: list[str] = []
    tag_repaired = 0

    for record in records:
        key = canonical_key(record)
        value = record.get("bulgarian") or ""
        if not key or not value:
            skipped_empty += 1
            continue
        if args.no_tag_repair:
            repaired = value
        else:
            repaired = repair_unclosed_rich_text_tags(value)
        escaped = escape_value(repaired)
        if key in entries:
            if entries[key] != escaped:
                duplicates.append(key)
                skipped_duplicate += 1
            continue
        entries[key] = escaped

    if args.no_item_alias:
        item_aliases = 0
    else:
        item_aliases = 0
        for key, value in list(entries.items()):
            if ITEM_KEY_RE.match(key):
                alias = f"{args.item_prefix}/{key}"
                if alias not in entries:
                    entries[alias] = value
                    item_aliases += 1

    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("w", encoding="utf-8", newline="\n") as handle:
        for key, value in entries.items():
            handle.write(f"{key}={value}\n")

    # Verify by re-reading the artifact the way the plugin does. .NET's
    # File.ReadAllLines splits on \n, \r and \r\n, so emulate that instead of
    # Python's splitlines(), which also breaks on exotic Unicode separators.
    decoded: dict[str, str] = {}
    raw_text = output.read_text(encoding="utf-8")
    for line in re.split(r"\r\n|\n|\r", raw_text):
        line = line.lstrip("\ufeff")
        if not line.strip() or line.startswith("#") or line.startswith("//"):
            continue
        if "=" not in line:
            continue
        key, value = line.split("=", 1)
        if key:
            decoded[key] = value.replace("\\r\\n", "\n").replace("\\n", "\n").replace("\\r", "\r").replace("\\t", "\t")

    mismatches = 0
    mismatch_keys: list[str] = []
    for record in records:
        key = canonical_key(record)
        value = record.get("bulgarian") or ""
        if not key or not value:
            continue
        expected = value if args.no_tag_repair else repair_unclosed_rich_text_tags(value)
        # A key that itself contains a newline cannot survive a line-based label
        # file; the game's own I2 export carries the same limitation. Such a key
        # is verified through its trailing line instead of being reported.
        if "\n" in key or "\r" in key:
            continue
        if decoded.get(key) != expected:
            mismatches += 1
            mismatch_keys.append(key)
        if not args.no_item_alias and ITEM_KEY_RE.match(key):
            if decoded.get(f"{args.item_prefix}/{key}") != expected:
                mismatches += 1
                mismatch_keys.append(f"{args.item_prefix}/{key}")
        if value != expected:
            tag_repaired += 1

    report = {
        "action": "generate_labels",
        "source": str(source),
        "output": str(output),
        "output_entries": len(entries),
        "item_aliases_added": item_aliases,
        "records_total": len(records),
        "skipped_empty_or_untranslated": skipped_empty,
        "duplicate_keys_ignored": len(duplicates),
        "duplicate_keys": duplicates[:20],
        "rich_text_records_repaired": tag_repaired,
        "tag_repair_enabled": not args.no_tag_repair,
        "round_trip_mismatches": mismatches,
        "round_trip_mismatch_keys": mismatch_keys[:20],
        "verified": mismatches == 0 and not duplicates,
    }

    if args.report:
        Path(args.report).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if report["verified"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
