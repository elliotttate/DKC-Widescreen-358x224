#!/usr/bin/env python3
"""Audit DKC1 level-object activation state from a 128 KiB WRAM capture.

The tool deliberately consumes the readable DKC1 disassembly rather than ROM
assets.  This keeps record indices, source labels, and initializer labels in
the report, which is much more useful when a save-state only fails sometimes.
"""

from __future__ import annotations

import argparse
import json
import re
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Iterable


POINTER_TABLE = "DATA_BD8000"
GENERAL_TYPES = {0x01, 0x02, 0x03, 0x06, 0x08, 0x0A, 0x0E, 0x0F, 0x10}
SPAWNING_TYPES = GENERAL_TYPES | {0x04, 0x05, 0x07, 0x0D}
LIKELY_ONE_SHOT_IDS = {0x15, 0x16, 0x34, 0x45, 0x65, 0x74}
LOGIC_CRITICAL_IDS = {0x38, 0x5D, 0x6C, 0x70, 0x75}


@dataclass(frozen=True)
class Record:
    index: int
    record_type: int
    x: int
    y: int
    initializer: str
    source_line: int


@dataclass(frozen=True)
class Window:
    left: int
    right: int

    def contains(self, x: int) -> bool:
        return self.left < x <= self.right


def parse_hex_word(token: str) -> int:
    match = re.fullmatch(r"\$([0-9A-Fa-f]{1,4})", token.strip())
    if not match:
        raise ValueError(f"not a hexadecimal word: {token}")
    return int(match.group(1), 16)


def source_lines(path: Path) -> list[str]:
    return path.read_text(encoding="utf-8", errors="replace").splitlines()


def locate_label(lines: list[str], label: str) -> int:
    needle = label + ":"
    for index, line in enumerate(lines):
        if line.strip() == needle:
            return index
    raise ValueError(f"label {label} was not found")


def parse_entrance_table(lines: list[str]) -> list[str]:
    start = locate_label(lines, POINTER_TABLE) + 1
    pointers: list[str] = []
    for line in lines[start:]:
        stripped = line.strip()
        if stripped.startswith("DATA_") and stripped.endswith(":"):
            break
        if not stripped.startswith("dw "):
            continue
        pointers.extend(part.strip() for part in stripped[3:].split(","))
    if not pointers:
        raise ValueError("entrance pointer table was empty")
    return pointers


def parse_records(lines: list[str], label: str) -> list[Record]:
    start = locate_label(lines, label) + 1
    records: list[Record] = []
    in_external_form = False
    for line_number, line in enumerate(lines[start:], start=start + 1):
        stripped = line.strip()
        if stripped == "else":
            in_external_form = True
            continue
        if not in_external_form or not stripped.startswith("dw "):
            continue
        parts = [part.strip() for part in stripped[3:].split(",")]
        if len(parts) < 4 or not all(re.fullmatch(r"\$[0-9A-Fa-f]{1,4}", value) for value in parts[:3]):
            continue
        record_type = parse_hex_word(parts[0])
        x = parse_hex_word(parts[1])
        y = parse_hex_word(parts[2])
        if record_type == 0:
            break
        records.append(Record(len(records), record_type, x, y, parts[3], line_number))
    if not records:
        raise ValueError(f"no external-form records were found at {label}")
    return records


def label_address(label: str) -> int:
    match = re.fullmatch(r"DATA_[0-9A-Fa-f]{2}([0-9A-Fa-f]{4})", label)
    if not match:
        raise ValueError(f"cannot derive an address from {label}")
    return int(match.group(1), 16)


def label_blocks(lines: list[str]) -> dict[str, list[str]]:
    blocks: dict[str, list[str]] = {}
    current: str | None = None
    for line in lines:
        match = re.fullmatch(r"(DATA_[0-9A-Fa-f]{6}):", line.strip())
        if match:
            current = match.group(1)
            blocks[current] = []
        elif current is not None:
            blocks[current].append(line.strip())
    return blocks


def resolve_initializer(
    initializer: str,
    blocks: dict[str, list[str]],
    sprite_names: dict[int, str],
) -> dict[str, object] | None:
    current = initializer
    visited: set[str] = set()
    while current in blocks and current not in visited:
        visited.add(current)
        for line in blocks[current]:
            match = re.search(
                r"NorSpr_SpriteIDLo\s*,\s*!Define_DKC1_NorSpr([0-9A-Fa-f]{2})_([A-Za-z0-9_]+)",
                line,
            )
            if match:
                sprite_id = int(match.group(1), 16)
                return {
                    "spriteId": sprite_id,
                    "spriteIdHex": f"{sprite_id:02X}",
                    "spriteName": sprite_names.get(sprite_id, match.group(2)),
                    "definition": match.group(2),
                    "oneShotLikely": sprite_id in LIKELY_ONE_SHOT_IDS,
                    "logicCritical": sprite_id in LOGIC_CRITICAL_IDS,
                }
        parent = None
        for line in blocks[current]:
            match = re.search(r"DKC1_SSS_Op82\((DATA_[0-9A-Fa-f]{6})\)", line)
            if match:
                parent = match.group(1)
                break
        if parent is None:
            break
        current = parent
    return None


def parse_sprite_names(misc_defines: Path) -> dict[int, str]:
    names: dict[int, str] = {}
    if not misc_defines.exists():
        return names
    pattern = re.compile(
        r"^!Define_DKC1_NorSpr([0-9A-Fa-f]{2})_([A-Za-z0-9_]+)\s*=\s*\$[0-9A-Fa-f]{4}"
    )
    for line in source_lines(misc_defines):
        match = pattern.match(line.strip())
        if match:
            names[int(match.group(1), 16)] = match.group(2)
    return names


def parse_group_children(lines: list[str], label: str) -> list[tuple[int, int, int, str, int]]:
    start = locate_label(lines, label) + 1
    children: list[tuple[int, int, int, str, int]] = []
    saw_leading_sentinel = False
    for line_number, line in enumerate(lines[start:], start=start + 1):
        stripped = line.strip()
        if stripped.startswith("DATA_") and stripped.endswith(":"):
            break
        if not stripped.startswith("dw "):
            continue
        parts = [part.strip() for part in stripped[3:].split(",")]
        if len(parts) < 4 or not all(re.fullmatch(r"\$[0-9A-Fa-f]{1,4}", value) for value in parts[:3]):
            continue
        record_type = parse_hex_word(parts[0])
        if record_type == 0:
            if not saw_leading_sentinel:
                saw_leading_sentinel = True
                continue
            break
        if saw_leading_sentinel:
            children.append((record_type, parse_hex_word(parts[1]), parse_hex_word(parts[2]), parts[3], line_number))
    return children


def word(ram: bytes, offset: int) -> int:
    return ram[offset] | (ram[offset + 1] << 8)


def signed16(value: int) -> int:
    return value - 0x10000 if value & 0x8000 else value


def actor_rows(ram: bytes, sprite_names: dict[int, str]) -> list[dict[str, object]]:
    rows: list[dict[str, object]] = []
    # DKC's normal-sprite tables are indexed by even byte offsets.  The
    # allocation pool used by CODE_BDF3A2 is $02..$1C inclusive.
    for raw_index in range(0, 0x34, 2):
        sprite_id = word(ram, 0x0D45 + raw_index)
        if sprite_id == 0:
            continue
        rows.append(
            {
                "rawIndex": raw_index,
                "slot": raw_index // 2,
                "spriteId": sprite_id,
                "spriteIdHex": f"{sprite_id:02X}",
                "spriteName": sprite_names.get(sprite_id, "Unknown"),
                "x": word(ram, 0x0B19 + raw_index),
                "y": word(ram, 0x0BC1 + raw_index),
                "recordBacklink": signed16(word(ram, 0x15FD + raw_index)),
                "state": word(ram, 0x1029 + raw_index),
            }
        )
    return rows


def windows(camera_x: int, margin: int) -> dict[str, Window]:
    stock_left = max(0, camera_x - 0x20)
    wide_left = max(0, camera_x - 0x20 - margin)
    return {
        # CODE_BDF88A derives the right edge by adding the span to the
        # possibly clamped left edge. This distinction matters in the first
        # margin-width of a level.
        "stockGeneral": Window(stock_left, stock_left + 0x140),
        "wideGeneral": Window(wide_left, wide_left + 0x140 + 2 * margin),
        # Type $04 and $07 were already wider than the stock viewport and are
        # intentionally not modified by Widescreen_358x224.asm.
        "type04": Window(camera_x - 0x54, camera_x + 0x154),
        "type07": Window(camera_x - 0xC0, camera_x + 0x1C0),
    }


def record_window(record_type: int, available: dict[str, Window], wide: bool) -> Window | None:
    if record_type in GENERAL_TYPES or record_type == 0x05:
        return available["wideGeneral" if wide else "stockGeneral"]
    if record_type == 0x04:
        return available["type04"]
    if record_type == 0x07:
        return available["type07"]
    return None


def record_dict(
    record: Record,
    bookkeeping: int,
    available: dict[str, Window],
    initializer: dict[str, object] | None,
) -> dict[str, object]:
    stock = record_window(record.record_type, available, False)
    wide = record_window(record.record_type, available, True)
    return {
        **asdict(record),
        "recordTypeHex": f"{record.record_type:02X}",
        "xHex": f"{record.x:04X}",
        "yHex": f"{record.y:04X}",
        "bookkeeping": bookkeeping,
        "bookkeepingHex": f"{bookkeeping:02X}",
        "stockEligibleX": stock.contains(record.x) if stock else None,
        "wideEligibleX": wide.contains(record.x) if wide else None,
        "resolvedInitializer": initializer,
    }


def audit(disassembly: Path, wram_path: Path, margin: int) -> dict[str, object]:
    lines = source_lines(disassembly)
    entrances = parse_entrance_table(lines)
    blocks = label_blocks(lines)
    sprite_names = parse_sprite_names(disassembly.with_name("Misc_Defines_DKC1.asm"))
    ram = wram_path.read_bytes()
    if len(ram) != 0x20000:
        raise ValueError(f"WRAM capture must be exactly 131072 bytes, got {len(ram)}")

    entrance = word(ram, 0x003E) & 0xFF
    if entrance >= len(entrances):
        raise ValueError(f"entrance ${entrance:02X} is outside DATA_BD8000")
    label = entrances[entrance]
    records = parse_records(lines, label)
    camera_x = word(ram, 0x088B)
    available = windows(camera_x, margin)
    scan_start = ram[0x00A0]
    scan_end = ram[0x1E0B]
    has_section_controller = records[0].record_type == 0x09

    if has_section_controller and scan_end != 0xFF:
        scanner_records = [record for record in records if scan_start <= record.index <= scan_end]
    else:
        scanner_records = records

    rows = [
        record_dict(
            record,
            ram[0x192B + record.index],
            available,
            resolve_initializer(record.initializer, blocks, sprite_names),
        )
        for record in scanner_records
    ]
    by_index = {record.index: record for record in records}
    group_children: list[dict[str, object]] = []
    base_address = label_address(label)
    for row in rows:
        if row["record_type"] != 0x05 or not row["initializer"].startswith("DATA_"):
            continue
        parent = by_index[row["index"]]
        parent_active = row["bookkeeping"] != 0
        parent_address = (base_address + parent.index * 8) & 0xFFFF
        first_child_address = (label_address(parent.initializer) + 8) & 0xFFFF
        first_bookkeeping = parent.index + ((first_child_address - parent_address) & 0xFFFF) // 8
        for offset, child in enumerate(parse_group_children(lines, parent.initializer)):
            child_type, child_x, child_y, child_initializer, source_line = child
            bookkeeping_index = first_bookkeeping + offset
            child_init = resolve_initializer(child_initializer, blocks, sprite_names)
            group_children.append(
                {
                    "parentRecordIndex": parent.index,
                    "parentActive": parent_active,
                    "childOffset": offset,
                    "bookkeepingIndex": bookkeeping_index,
                    "bookkeepingIndexHex": f"{bookkeeping_index:02X}",
                    "bookkeeping": ram[0x192B + bookkeeping_index],
                    "bookkeepingHex": f"{ram[0x192B + bookkeeping_index]:02X}",
                    "recordType": child_type,
                    "x": child_x,
                    "xHex": f"{child_x:04X}",
                    "y": child_y,
                    "yHex": f"{child_y:04X}",
                    "initializer": child_initializer,
                    "resolvedInitializer": child_init,
                    "sourceLine": source_line,
                }
            )
    missing = [
        row
        for row in rows
        if row["record_type"] in SPAWNING_TYPES
        and row["wideEligibleX"] is True
        and row["bookkeeping"] == 0
    ]
    wide_only = [row for row in rows if row["wideEligibleX"] is True and row["stockEligibleX"] is False]

    actors = actor_rows(ram, sprite_names)
    occupied_raw = {row["rawIndex"] for row in actors}
    allocation_raw = list(range(0x02, 0x1E, 2))
    free_raw = [value for value in allocation_raw if value not in occupied_raw]

    return {
        "input": {
            "disassembly": str(disassembly.resolve()),
            "wram": str(wram_path.resolve()),
            "margin": margin,
        },
        "state": {
            "levelId": word(ram, 0x0030) & 0xFF,
            "levelIdHex": f"{word(ram, 0x0030) & 0xFF:02X}",
            "entranceId": entrance,
            "entranceIdHex": f"{entrance:02X}",
            "spriteDataLabel": label,
            "cameraX": camera_x,
            "cameraXHex": f"{camera_x:04X}",
            "cameraY": word(ram, 0x0895),
            "cameraLower": word(ram, 0x1B23),
            "cameraUpper": word(ram, 0x1B25),
            "sectionController": {
                "present": has_section_controller,
                "packedState": word(ram, 0x1E03),
                "transitionRecordPointer": word(ram, 0x1E05),
                "primaryStart": ram[0x1E07],
                "secondaryStart": ram[0x1E09],
                "primaryEnd": ram[0x1E0B],
                "secondaryEnd": ram[0x1E0D],
                "scannerStart": scan_start,
            },
        },
        "windows": {name: asdict(value) for name, value in available.items()},
        "pool": {
            "normalAllocationRawIndices": allocation_raw,
            "freeRawIndices": free_raw,
            "freeCount": len(free_raw),
            "occupiedCount": len(allocation_raw) - len(free_raw),
        },
        "actors": actors,
        "scannerRecords": rows,
        "groupChildren": group_children,
        "wideOnlyRecords": wide_only,
        "missingWideEligibleRecords": missing,
        "warnings": build_warnings(has_section_controller, free_raw, missing, wide_only, group_children),
    }


def build_warnings(
    sectioned: bool,
    free_raw: list[int],
    missing: list[dict[str, object]],
    wide_only: list[dict[str, object]],
    group_children: list[dict[str, object]],
) -> list[str]:
    warnings: list[str] = []
    if len(free_raw) <= 2:
        warnings.append("normal sprite allocation pool has two or fewer free entries")
    if missing:
        warnings.append("one or more spawning records are inside the wide X window but have zero bookkeeping")
    if any(
        row["bookkeeping"] == 0
        and row["resolvedInitializer"]
        and row["resolvedInitializer"]["logicCritical"]
        for row in missing
    ):
        warnings.append("a logic-critical wide-eligible record has zero bookkeeping")
    if any(child["parentActive"] and child["bookkeeping"] == 0 for child in group_children):
        warnings.append("an active or nearby type-$05 group has one or more missing children")
    if wide_only:
        warnings.append("wide activation adds records that stock would leave inactive at this camera position")
    if sectioned:
        warnings.append("type-$09 section controller is active; audit $1E03-$1E0D and both $BDFF window checks")
    return warnings


def text_report(result: dict[str, object]) -> str:
    state = result["state"]
    pool = result["pool"]
    section = state["sectionController"]
    lines = [
        f"level=${state['levelIdHex']} entrance=${state['entranceIdHex']} data={state['spriteDataLabel']}",
        f"cameraX=${state['cameraXHex']} bounds=${state['cameraLower']:04X}..${state['cameraUpper']:04X}",
        f"normal pool occupied={pool['occupiedCount']} free={pool['freeCount']}",
    ]
    if section["present"]:
        lines.append(
            "section "
            f"state=${section['packedState']:04X} transition=${section['transitionRecordPointer']:04X} "
            f"scan=${section['scannerStart']:02X}..${section['primaryEnd']:02X}"
        )
    lines.append("records in current scanner range:")
    for row in result["scannerRecords"]:
        flags = []
        if row["wideEligibleX"]:
            flags.append("wide")
        if row["stockEligibleX"]:
            flags.append("stock")
        if row["bookkeeping"]:
            flags.append(f"active:{row['bookkeepingHex']}")
        resolved = row["resolvedInitializer"]
        name = resolved["spriteName"] if resolved else "unresolved"
        lines.append(
            f"  [{row['index']:02X}] type={row['recordTypeHex']} x=${row['xHex']} y=${row['yHex']} "
            f"{' '.join(flags) or '-'} {name} via {row['initializer']} (line {row['source_line']})"
        )
    if result["groupChildren"]:
        lines.append("type-$05 children:")
        for child in result["groupChildren"]:
            resolved = child["resolvedInitializer"]
            name = resolved["spriteName"] if resolved else "unresolved"
            lines.append(
                f"  parent[{child['parentRecordIndex']:02X}] child[{child['childOffset']}] "
                f"parentActive={str(child['parentActive']).lower()} "
                f"book[${child['bookkeepingIndexHex']}]={child['bookkeepingHex']} "
                f"x=${child['xHex']} y=${child['yHex']} {name}"
            )
    if result["warnings"]:
        lines.append("warnings:")
        lines.extend("  - " + warning for warning in result["warnings"])
    return "\n".join(lines)


def main(argv: Iterable[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--disassembly", required=True, type=Path, help="Routine_Macros_DKC1.asm")
    parser.add_argument("--wram", required=True, type=Path, help="captured wram-7e7f.bin")
    parser.add_argument("--margin", type=lambda value: int(value, 0), default=0x38)
    parser.add_argument("--json", action="store_true", help="emit JSON instead of the compact text report")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args(argv)
    result = audit(args.disassembly, args.wram, args.margin)
    output = json.dumps(result, indent=2) if args.json else text_report(result)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(output + "\n", encoding="utf-8")
    else:
        print(output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
