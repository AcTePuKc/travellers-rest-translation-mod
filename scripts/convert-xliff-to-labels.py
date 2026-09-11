#!/usr/bin/env python3
"""Convert a Crowdin XLIFF language export to the plugin labels format."""

from __future__ import annotations

import argparse
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


XLIFF_NS = "urn:oasis:names:tc:xliff:document:1.2"
NS = {"x": XLIFF_NS}


def escape_value(value: str) -> str:
    return value.replace("\r\n", "\n").replace("\r", "\n").replace("\n", r"\n").replace("\t", r"\t")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, help="Crowdin .xliff file")
    parser.add_argument("--output", required=True, help="Output labels file")
    args = parser.parse_args()

    input_path = Path(args.input)
    output_path = Path(args.output)
    if not input_path.is_file():
        parser.error(f"input file not found: {input_path}")

    root = ET.parse(input_path).getroot()
    entries: list[tuple[str, str]] = []
    seen: set[str] = set()
    skipped_untranslated = 0
    duplicate_keys: list[str] = []

    for unit in root.findall(".//x:trans-unit", NS):
        key = unit.get("resname") or unit.findtext("x:source", default="", namespaces=NS)
        target = unit.find("x:target", NS)
        source = unit.findtext("x:source", default="", namespaces=NS)
        if target is None:
            skipped_untranslated += 1
            continue

        value = "".join(target.itertext())
        if not key or not value or value == source:
            skipped_untranslated += 1
            continue
        if key in seen:
            duplicate_keys.append(key)
            continue

        seen.add(key)
        entries.append((key, escape_value(value)))

    if duplicate_keys:
        print("Duplicate keys found:", file=sys.stderr)
        print("\n".join(duplicate_keys), file=sys.stderr)
        return 2

    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("w", encoding="utf-8", newline="\n") as output:
        for key, value in entries:
            output.write(f"{key}={value}\n")

    print(f"Input: {input_path}")
    print(f"Output: {output_path}")
    print(f"Entries: {len(entries)}")
    print(f"Untranslated entries skipped: {skipped_untranslated}")
    print("Verified: true")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
