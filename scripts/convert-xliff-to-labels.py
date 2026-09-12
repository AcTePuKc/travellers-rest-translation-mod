#!/usr/bin/env python3
"""Convert a Crowdin XLIFF language export to the plugin labels format."""

from __future__ import annotations

import argparse
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
import re


XLIFF_NS = "urn:oasis:names:tc:xliff:document:1.2"
NS = {"x": XLIFF_NS}


def escape_value(value: str) -> str:
    return value.replace("\r\n", "\n").replace("\r", "\n").replace("\n", r"\n").replace("\t", r"\t")


def repair_unclosed_rich_text_tags(value: str) -> str:
    self_closing = {"br", "sprite"}
    open_tags: list[str] = []
    tag_pattern = re.compile(r"<(/?)([A-Za-z]+)(?:[^>]*)?>")
    result: list[str] = []
    position = 0

    for match in tag_pattern.finditer(value):
        result.append(value[position : match.start()])
        is_closing = match.group(1) == "/"
        tag_name = match.group(2).lower()
        if tag_name in self_closing:
            result.append(match.group(0))
            position = match.end()
            continue
        if is_closing:
            if tag_name in open_tags:
                found = len(open_tags) - 1 - open_tags[::-1].index(tag_name)
                for index in range(len(open_tags) - 1, found, -1):
                    result.append(f"</{open_tags.pop()}>")
                result.append(match.group(0))
                open_tags.pop()
            position = match.end()
            continue
        result.append(match.group(0))
        open_tags.append(tag_name)
        position = match.end()

    result.append(value[position:])
    result.extend(f"</{tag_name}>" for tag_name in reversed(open_tags))
    return "".join(result)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, help="Crowdin .xliff file")
    parser.add_argument("--output", required=True, help="Output labels file")
    parser.add_argument(
        "--duplicate-report",
        help="Optional path for a report containing duplicate candidates",
    )
    args = parser.parse_args()

    input_path = Path(args.input)
    output_path = Path(args.output)
    if not input_path.is_file():
        parser.error(f"input file not found: {input_path}")

    root = ET.parse(input_path).getroot()
    records: dict[str, tuple[str, str, int]] = {}
    record_order: list[str] = []
    skipped_untranslated = 0
    duplicate_keys: set[str] = set()
    duplicate_records: list[tuple[str, str, int]] = []

    for unit_number, unit in enumerate(root.findall(".//x:trans-unit", NS), start=1):
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
        record = (key, value, unit_number)
        if key in records:
            if key not in duplicate_keys:
                duplicate_records.append(records[key])
                duplicate_keys.add(key)
            duplicate_records.append(record)
            continue

        records[key] = record
        record_order.append(key)

    entries = [
        (key, escape_value(repair_unclosed_rich_text_tags(records[key][1])))
        for key in record_order
        if key not in duplicate_keys
    ]

    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("w", encoding="utf-8", newline="\n") as output:
        for key, value in entries:
            output.write(f"{key}={value}\n")

    duplicate_report = Path(args.duplicate_report) if args.duplicate_report else Path(f"{output_path}.duplicates.txt")
    if duplicate_records:
        duplicate_report.parent.mkdir(parents=True, exist_ok=True)
        with duplicate_report.open("w", encoding="utf-8", newline="\n") as report:
            report.write("# Duplicate keys requiring manual review\n")
            report.write("# These entries were excluded from the generated labels file.\n\n")
            for key, value, unit_number in duplicate_records:
                report.write(f"[{key}] XLIFF trans-unit {unit_number}\n")
                report.write(f"{key}={escape_value(value)}\n\n")

    print(f"Input: {input_path}")
    print(f"Output: {output_path}")
    print(f"Entries: {len(entries)}")
    print(f"Untranslated entries skipped: {skipped_untranslated}")
    print(f"Duplicate keys excluded: {len(duplicate_keys)}")
    if duplicate_records:
        print(f"Duplicate report: {duplicate_report}")
    print(f"Verified: {str(not duplicate_keys).lower()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
