#!/usr/bin/env python3
"""Convert an I2Loc workbook language column to the plugin labels format."""

from __future__ import annotations

import argparse
import csv
import sys
from pathlib import Path

try:
    import openpyxl
except ImportError as exc:
    raise SystemExit("Missing dependency: openpyxl") from exc


def escape_value(value: str) -> str:
    """Keep one physical output line while preserving runtime line breaks."""
    return value.replace("\r\n", "\n").replace("\r", "\n").replace("\n", r"\n").replace("\t", r"\t")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, help="Crowdin/I2Loc .xlsx file")
    parser.add_argument("--language", required=True, help="Exact workbook column name, for example Bulgarian")
    parser.add_argument("--output", required=True, help="Output labels file")
    args = parser.parse_args()

    input_path = Path(args.input)
    output_path = Path(args.output)
    if not input_path.is_file():
        parser.error(f"input file not found: {input_path}")

    workbook = openpyxl.load_workbook(input_path, read_only=True, data_only=True)
    entries: list[tuple[str, str, str]] = []
    seen: set[str] = set()
    duplicate_keys: list[str] = []
    skipped_empty = 0
    skipped_rows = 0

    for sheet in workbook.worksheets:
        rows = sheet.iter_rows(values_only=True)
        header = next(rows, None)
        if not header or "Keys" not in header or args.language not in header:
            continue

        key_index = header.index("Keys")
        language_index = header.index(args.language)
        for row_number, row in enumerate(rows, start=2):
            key = row[key_index] if key_index < len(row) else None
            translation = row[language_index] if language_index < len(row) else None

            if not isinstance(key, str) or not key:
                skipped_rows += 1
                continue
            if not isinstance(translation, str) or not translation:
                skipped_empty += 1
                continue
            if key in seen:
                duplicate_keys.append(f"{sheet.title}!{row_number}:{key}")
                continue

            seen.add(key)
            entries.append((key, escape_value(translation), sheet.title))

    if duplicate_keys:
        print("Duplicate keys found:", file=sys.stderr)
        print("\n".join(duplicate_keys), file=sys.stderr)
        return 2

    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("w", encoding="utf-8", newline="\n") as output:
        for key, translation, _sheet in entries:
            output.write(f"{key}={translation}\n")

    print(f"Input: {input_path}")
    print(f"Language: {args.language}")
    print(f"Output: {output_path}")
    print(f"Entries: {len(entries)}")
    print(f"Empty translations skipped: {skipped_empty}")
    print(f"Rows without a key skipped: {skipped_rows}")
    print("Verified: true")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
