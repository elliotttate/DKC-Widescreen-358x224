#!/usr/bin/env python3
"""Audit bank-BD object prefetch phase differences through atomic WRAM replay.

Runtime operation is limited to serial requests to an already-running
DKCLevelAutomation v0.1.3 loopback bridge. The model and tests are offline.
"""

from __future__ import annotations

import argparse
import base64
import gzip
import hashlib
import json
import re
import socket
import sys
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable, Sequence


WRAM_SIZE = 0x20000
WRAM_BASE = 0x7E0000
REQUIRED_BRIDGE_VERSION = "0.1.3"
BOOKKEEPING = 0x192B
BOOKKEEPING_LENGTH = 0x100
FIRST_ACTOR_INDEX = 0x02
LAST_ACTOR_INDEX = 0x32
SAFE_NAME = re.compile(r"[^A-Za-z0-9._-]+")


class AuditError(RuntimeError):
    """A user-facing validation, bridge, replay, or evidence error."""


@dataclass(frozen=True)
class ActorField:
    name: str
    offset: int
    signed: bool
    category: str


# DKC's normal-sprite structure-of-arrays uses raw even actor indexes as byte
# offsets. "collision_candidate" is intentionally conservative: the readable
# RAM map has no stable semantic names for these per-actor scratch words, but
# collision/interaction routines consume them for multiple actor types. The
# report never claims an individual candidate has one universal meaning.
ACTOR_FIELDS: tuple[ActorField, ...] = (
    ActorField("displayed_pose", 0x0AE5, False, "animation"),
    ActorField("x", 0x0B19, False, "position"),
    ActorField("oam_z", 0x0B8D, False, "render"),
    ActorField("y", 0x0BC1, False, "position"),
    ActorField("collision_candidate_0c35", 0x0C35, False, "collision_candidate"),
    ActorField("graphics", 0x0C69, False, "render"),
    ActorField("collision_candidate_0cdd", 0x0CDD, False, "collision_candidate"),
    ActorField("current_pose", 0x0D11, False, "animation"),
    ActorField("id", 0x0D45, False, "identity"),
    ActorField("collision_candidate_0db9", 0x0DB9, False, "collision_candidate"),
    ActorField("collision_candidate_0ded", 0x0DED, False, "collision_candidate"),
    ActorField("collision_candidate_0e21", 0x0E21, False, "collision_candidate"),
    ActorField("collision_candidate_0e55", 0x0E55, False, "collision_candidate"),
    ActorField("x_speed", 0x0E89, True, "motion"),
    ActorField("collision_candidate_0ebd", 0x0EBD, False, "collision_candidate"),
    ActorField("y_speed", 0x0EF1, True, "motion"),
    ActorField("collision_candidate_0f25", 0x0F25, False, "collision_candidate"),
    ActorField("collision_candidate_0f59", 0x0F59, False, "collision_candidate"),
    ActorField("collision_candidate_0f8d", 0x0F8D, False, "collision_candidate"),
    ActorField("collision_candidate_0fc1", 0x0FC1, False, "collision_candidate"),
    ActorField("collision_candidate_0ff5", 0x0FF5, False, "collision_candidate"),
    ActorField("state", 0x1029, False, "state"),
    ActorField("collision_candidate_109d", 0x109D, False, "collision_candidate"),
    ActorField("animation_id", 0x10D1, False, "animation"),
    ActorField("animation_timer", 0x1105, False, "animation"),
    ActorField("animation_speed", 0x1139, False, "animation"),
    ActorField("animation_script_index", 0x116D, False, "animation"),
    ActorField("source_record", 0x15FD, True, "identity"),
)

GENERAL_RECORD_TYPES = {0x01, 0x02, 0x03, 0x05, 0x06, 0x08, 0x0A, 0x0E, 0x0F, 0x10}


@dataclass
class FrameState:
    relative_frame: int
    emulator_frame: int
    sha256: str
    level_id: int
    entrance_id: int
    layer_x: int
    bookkeeping: bytes
    actors: dict[int, dict[str, Any]]
    records: dict[int, dict[str, Any]]


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def safe_name(value: str) -> str:
    return SAFE_NAME.sub("-", value).strip("-.") or "case"


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest().upper()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def parse_integer(value: Any, label: str) -> int:
    if isinstance(value, bool):
        raise AuditError(f"{label} must be an integer, not a boolean.")
    if isinstance(value, int):
        return value
    if not isinstance(value, str) or not value.strip():
        raise AuditError(f"{label} must be an integer or numeric string.")
    text = value.strip()
    if text.startswith("$"):
        text = "0x" + text[1:]
    try:
        return int(text, 0)
    except ValueError as exc:
        raise AuditError(f"{label} has invalid integer {value!r}.") from exc


def reject_unknown(document: dict[str, Any], allowed: set[str], label: str) -> None:
    unknown = set(document) - allowed
    if unknown:
        raise AuditError(f"{label} contains unknown keys: {', '.join(sorted(unknown))}.")


def macro_length(macro: str) -> int:
    maximum = -1
    for raw in re.split(r"[;,]", macro):
        segment = raw.strip()
        if not segment:
            continue
        if "=" not in segment:
            raise AuditError(f"Invalid macro segment {segment!r}.")
        text = segment.split("=", 1)[0].strip()
        parts = text.split("-", 1)
        try:
            first = int(parts[0], 10)
            last = int(parts[1], 10) if len(parts) == 2 else first
        except ValueError as exc:
            raise AuditError(f"Invalid macro frame range {text!r}.") from exc
        if first < 0 or last < first:
            raise AuditError(f"Invalid macro frame range {text!r}.")
        maximum = max(maximum, last)
    if maximum < 0:
        raise AuditError("A controller macro must contain at least one frame assignment.")
    return maximum + 1


def validate_recipe(document: Any, source: str = "recipe") -> dict[str, Any]:
    if not isinstance(document, dict) or document.get("schemaVersion") != 1:
        raise AuditError(f"{source}: expected an object with schemaVersion 1.")
    reject_unknown(
        document,
        {"$schema", "schemaVersion", "name", "description", "margin", "states"},
        source,
    )
    if not isinstance(document.get("name"), str) or not document["name"]:
        raise AuditError(f"{source}: name must be a non-empty string.")
    margin = document.get("margin", 0x38)
    if not isinstance(margin, int) or isinstance(margin, bool) or not 0 <= margin <= 0x100:
        raise AuditError(f"{source}: margin must be an integer in 0..256.")
    states = document.get("states")
    if not isinstance(states, list) or not states:
        raise AuditError(f"{source}: states must be a non-empty array.")
    state_ids: set[str] = set()
    for state_index, state in enumerate(states):
        label = f"{source}: states[{state_index}]"
        if not isinstance(state, dict):
            raise AuditError(f"{label} must be an object.")
        reject_unknown(
            state,
            {"id", "file", "identity", "expectedLevel", "trackRecords", "records", "scenarios"},
            label,
        )
        if not isinstance(state.get("id"), str) or not state["id"]:
            raise AuditError(f"{label}.id must be a non-empty string.")
        if state["id"] in state_ids:
            raise AuditError(f"{source}: duplicate state id {state['id']!r}.")
        state_ids.add(state["id"])
        if not isinstance(state.get("file"), str) or not state["file"]:
            raise AuditError(f"{label}.file must be a non-empty string.")
        if "expectedLevel" in state:
            level = parse_integer(state["expectedLevel"], f"{label}.expectedLevel")
            if not 0 <= level <= 0xFFFF:
                raise AuditError(f"{label}.expectedLevel must fit in 16 bits.")
        if not isinstance(state.get("trackRecords", True), bool):
            raise AuditError(f"{label}.trackRecords must be boolean.")
        records = state.get("records", [])
        if not isinstance(records, list):
            raise AuditError(f"{label}.records must be an array.")
        record_ids: set[int] = set()
        for record_index, record in enumerate(records):
            record_label = f"{label}.records[{record_index}]"
            if not isinstance(record, dict):
                raise AuditError(f"{record_label} must be an object.")
            reject_unknown(record, {"index", "type", "x", "y", "label"}, record_label)
            index = parse_integer(record.get("index"), f"{record_label}.index")
            if not 0 <= index < BOOKKEEPING_LENGTH or index in record_ids:
                raise AuditError(f"{record_label}.index must be unique in 0..255.")
            record_ids.add(index)
            if "type" in record and not 0 <= parse_integer(record["type"], f"{record_label}.type") <= 0xFF:
                raise AuditError(f"{record_label}.type must fit in one byte.")
            for coordinate in ("x", "y"):
                if coordinate in record and not 0 <= parse_integer(record[coordinate], f"{record_label}.{coordinate}") <= 0xFFFF:
                    raise AuditError(f"{record_label}.{coordinate} must fit in 16 bits.")
        scenarios = state.get("scenarios")
        if not isinstance(scenarios, list) or not scenarios:
            raise AuditError(f"{label}.scenarios must be a non-empty array.")
        scenario_ids: set[str] = set()
        for scenario_index, scenario in enumerate(scenarios):
            scenario_label = f"{label}.scenarios[{scenario_index}]"
            if not isinstance(scenario, dict):
                raise AuditError(f"{scenario_label} must be an object.")
            reject_unknown(scenario, {"id", "description", "maxFrame", "timeoutMs", "inputs"}, scenario_label)
            if not isinstance(scenario.get("id"), str) or not scenario["id"]:
                raise AuditError(f"{scenario_label}.id must be a non-empty string.")
            if scenario["id"] in scenario_ids:
                raise AuditError(f"{label} has duplicate scenario id {scenario['id']!r}.")
            scenario_ids.add(scenario["id"])
            maximum = scenario.get("maxFrame")
            if not isinstance(maximum, int) or isinstance(maximum, bool) or maximum < 0:
                raise AuditError(f"{scenario_label}.maxFrame must be a non-negative integer.")
            inputs = scenario.get("inputs")
            if not isinstance(inputs, dict) or not inputs:
                raise AuditError(f"{scenario_label}.inputs must map controllers to macros.")
            for controller, macro in inputs.items():
                try:
                    number = int(controller)
                except (TypeError, ValueError) as exc:
                    raise AuditError(f"{scenario_label} controller {controller!r} is invalid.") from exc
                if number < 1 or number > 5 or not isinstance(macro, str):
                    raise AuditError(f"{scenario_label} controllers must be 1-5 with string macros.")
                if macro_length(macro) < maximum:
                    raise AuditError(f"{scenario_label} controller {number} macro ends before maxFrame {maximum}.")
            timeout = scenario.get("timeoutMs", 60000)
            if not isinstance(timeout, int) or isinstance(timeout, bool) or timeout < 1000:
                raise AuditError(f"{scenario_label}.timeoutMs must be at least 1000.")
    return document


def u16(memory: bytes, offset: int) -> int:
    return memory[offset] | memory[offset + 1] << 8


def s16(value: int) -> int:
    return value - 0x10000 if value & 0x8000 else value


def canonical_source(value: int) -> int | None:
    if value == -0x8000:
        return None
    source = abs(value)
    return source if 0 <= source < BOOKKEEPING_LENGTH else None


def actor_from_memory(memory: bytes, actor_index: int) -> dict[str, Any]:
    fields: dict[str, Any] = {
        "actorIndex": actor_index,
        "actorIndexHex": f"0x{actor_index:02X}",
        "slot": actor_index // 2,
    }
    categories: dict[str, str] = {}
    for field in ACTOR_FIELDS:
        raw = u16(memory, field.offset + actor_index)
        fields[field.name] = s16(raw) if field.signed else raw
        categories[field.name] = field.category
    fields["sourceRecordCanonical"] = canonical_source(int(fields["source_record"]))
    fields["fieldCategories"] = categories
    return fields


def decode_actors(memory: bytes) -> dict[int, dict[str, Any]]:
    actors: dict[int, dict[str, Any]] = {}
    for actor_index in range(FIRST_ACTOR_INDEX, LAST_ACTOR_INDEX + 1, 2):
        actor = actor_from_memory(memory, actor_index)
        if actor["id"] != 0:
            actors[actor_index] = actor
    return actors


def record_observations(memory: bytes, actors: dict[int, dict[str, Any]]) -> dict[int, dict[str, Any]]:
    records: dict[int, dict[str, Any]] = {}
    actors_by_source: dict[int, list[dict[str, Any]]] = {}
    for actor in actors.values():
        source = actor["sourceRecordCanonical"]
        if source is not None:
            actors_by_source.setdefault(source, []).append(actor)
    for record in range(BOOKKEEPING_LENGTH):
        bookmark = memory[BOOKKEEPING + record]
        by_source = actors_by_source.get(record, [])
        bookmark_actor = actors.get(bookmark)
        bookmark_valid_index = FIRST_ACTOR_INDEX <= bookmark <= LAST_ACTOR_INDEX and bookmark % 2 == 0
        bookmark_source_matches = bool(
            bookmark_actor is not None and bookmark_actor["sourceRecordCanonical"] == record
        )
        selected_actor = bookmark_actor if bookmark_source_matches else (by_source[0] if by_source else None)
        group_marker = bookmark == 0xFF
        active = group_marker or selected_actor is not None
        if active or bookmark != 0 or by_source:
            records[record] = {
                "recordIndex": record,
                "recordIndexHex": f"0x{record:02X}",
                "bookmark": bookmark,
                "bookmarkHex": f"0x{bookmark:02X}",
                "bookmarkValidActorIndex": bookmark_valid_index,
                "bookmarkSourceMatches": bookmark_source_matches,
                "groupMarker": group_marker,
                "active": active,
                "allocated": selected_actor is not None,
                "actorIndex": selected_actor["actorIndex"] if selected_actor else None,
                "actor": selected_actor,
                "actorsWithSource": [actor["actorIndex"] for actor in by_source],
                "mappingConsistent": group_marker
                or (
                    selected_actor is not None
                    and bookmark_source_matches
                    and len(by_source) == 1
                ),
            }
    return records


def parse_frame(memory: bytes, relative_frame: int, emulator_frame: int, sha256: str | None = None) -> FrameState:
    if len(memory) != WRAM_SIZE:
        raise AuditError(f"Expected {WRAM_SIZE} WRAM bytes, received {len(memory)}.")
    actors = decode_actors(memory)
    return FrameState(
        relative_frame=relative_frame,
        emulator_frame=emulator_frame,
        sha256=sha256 or sha256_bytes(memory),
        level_id=u16(memory, 0x0030),
        entrance_id=u16(memory, 0x003E),
        layer_x=u16(memory, 0x088B),
        bookkeeping=memory[BOOKKEEPING : BOOKKEEPING + BOOKKEEPING_LENGTH],
        actors=actors,
        records=record_observations(memory, actors),
    )


def stock_window(record_type: int, layer_x: int) -> tuple[int, int] | None:
    if record_type in GENERAL_RECORD_TYPES:
        left = max(0, layer_x - 0x20)
        return left, left + 0x140
    if record_type == 0x04:
        return layer_x - 0x54, layer_x + 0x154
    if record_type == 0x07:
        return layer_x - 0xC0, layer_x + 0x1C0
    return None


def stock_eligible(record: dict[str, Any], frame: FrameState) -> bool | None:
    if "type" not in record or "x" not in record:
        return None
    window = stock_window(int(record["type"]), frame.layer_x)
    if window is None:
        return None
    return window[0] < int(record["x"]) <= window[1]


def wide_eligible(record: dict[str, Any], frame: FrameState, margin: int) -> bool | None:
    if "type" not in record or "x" not in record:
        return None
    record_type = int(record["type"])
    if record_type in GENERAL_RECORD_TYPES:
        left = max(0, frame.layer_x - 0x20 - margin)
        window = (left, left + 0x140 + 2 * margin)
    else:
        window = stock_window(record_type, frame.layer_x)
    if window is None:
        return None
    return window[0] < int(record["x"]) <= window[1]


def normalized_catalog(state: dict[str, Any]) -> dict[int, dict[str, Any]]:
    catalog: dict[int, dict[str, Any]] = {}
    for record in state.get("records", []):
        index = parse_integer(record["index"], "record.index")
        row = {"index": index, "indexHex": f"0x{index:02X}", "label": record.get("label", "")}
        for key in ("type", "x", "y"):
            if key in record:
                row[key] = parse_integer(record[key], f"record.{key}")
                row[key + "Hex"] = f"0x{int(row[key]):04X}" if key != "type" else f"0x{int(row[key]):02X}"
        catalog[index] = row
    return catalog


def first_frame(frames: Sequence[FrameState], record: int, key: str) -> int | None:
    for frame in frames:
        observation = frame.records.get(record)
        if observation and observation.get(key):
            return frame.relative_frame
    return None


def record_flag(frame: FrameState, record: int, key: str) -> bool:
    return bool((frame.records.get(record) or {}).get(key))


def boolean_episodes(values: Sequence[bool], frames: Sequence[FrameState]) -> list[dict[str, Any]]:
    if len(values) != len(frames):
        raise AuditError("Episode values and frames must have equal lengths.")
    episodes: list[dict[str, Any]] = []
    start: int | None = None
    for index, value in enumerate(values):
        if value and start is None:
            start = index
        if not value and start is not None:
            episodes.append(
                {
                    "startFrame": frames[start].relative_frame,
                    "lastActiveFrame": frames[index - 1].relative_frame,
                    "releaseFrame": frames[index].relative_frame,
                    "frameCount": index - start,
                    "leftCensored": start == 0,
                    "rightCensored": False,
                }
            )
            start = None
    if start is not None:
        episodes.append(
            {
                "startFrame": frames[start].relative_frame,
                "lastActiveFrame": frames[-1].relative_frame,
                "releaseFrame": None,
                "frameCount": len(frames) - start,
                "leftCensored": start == 0,
                "rightCensored": True,
            }
        )
    for ordinal, episode in enumerate(episodes):
        episode["episodeOrdinal"] = ordinal
        episode["reallocation"] = ordinal > 0 or not episode["leftCensored"]
    return episodes


def record_episodes(frames: Sequence[FrameState], record: int, key: str) -> list[dict[str, Any]]:
    episodes = boolean_episodes([record_flag(frame, record, key) for frame in frames], frames)
    for episode in episodes:
        start = int(episode["startFrame"])
        end = int(episode["lastActiveFrame"])
        episode["startMapping"] = mapping_summary(frames[start].records.get(record))
        episode["lastMapping"] = mapping_summary(frames[end].records.get(record))
        actor_indexes = [
            (frames[index].records.get(record) or {}).get("actorIndex")
            for index in range(start, end + 1)
        ]
        transitions = []
        for index in range(start + 1, end + 1):
            before = actor_indexes[index - start - 1]
            after = actor_indexes[index - start]
            if before != after:
                transitions.append({"frame": frames[index].relative_frame, "before": before, "after": after})
        episode["actorIndexTransitions"] = transitions
    return episodes


def eligibility_timeline(
    frames: Sequence[FrameState],
    record: dict[str, Any],
    variant: str,
    margin: int,
) -> tuple[list[bool], list[dict[str, Any]]]:
    values = [
        bool(stock_eligible(record, frame))
        if variant == "stock"
        else bool(wide_eligible(record, frame, margin))
        for frame in frames
    ]
    return values, boolean_episodes(values, frames)


def actor_comparison(stock_actor: dict[str, Any], wide_actor: dict[str, Any]) -> dict[str, Any]:
    differences: list[dict[str, Any]] = []
    categories: set[str] = set()
    for field in ACTOR_FIELDS:
        left = stock_actor[field.name]
        right = wide_actor[field.name]
        if left == right:
            continue
        categories.add(field.category)
        delta = right - left
        if field.name in {"x", "y"}:
            delta = s16(delta & 0xFFFF)
        differences.append(
            {
                "field": field.name,
                "category": field.category,
                "stock": left,
                "wide": right,
                "deltaWideMinusStock": delta,
                "addressBase": f"0x{WRAM_BASE + field.offset:06X}",
                "stockActorAddress": f"0x{WRAM_BASE + field.offset + stock_actor['actorIndex']:06X}",
                "wideActorAddress": f"0x{WRAM_BASE + field.offset + wide_actor['actorIndex']:06X}",
            }
        )
    behavior_categories = {"identity", "position", "motion", "state", "collision_candidate"}
    return {
        "stockActorIndex": stock_actor["actorIndex"],
        "wideActorIndex": wide_actor["actorIndex"],
        "slotChanged": stock_actor["actorIndex"] != wide_actor["actorIndex"],
        "differentFields": differences,
        "differentCategories": sorted(categories),
        "behaviorRelevantDifference": bool(categories & behavior_categories),
        "animationOrRenderOnly": bool(categories) and not bool(categories & behavior_categories),
        "exactFieldMatch": not differences,
    }


def evidence_ref(variant: str, frame: FrameState, archive: str, index: str) -> dict[str, Any]:
    return {
        "variant": variant,
        "relativeFrame": frame.relative_frame,
        "emulatorFrame": frame.emulator_frame,
        "sha256": frame.sha256,
        "archive": archive,
        "uncompressedOffset": frame.relative_frame * WRAM_SIZE,
        "length": WRAM_SIZE,
        "index": index,
    }


def mapping_summary(observation: dict[str, Any] | None) -> dict[str, Any] | None:
    if not observation:
        return None
    actor = observation.get("actor")
    return {
        "bookmark": observation["bookmark"],
        "bookmarkHex": observation["bookmarkHex"],
        "groupMarker": observation["groupMarker"],
        "active": observation["active"],
        "allocated": observation["allocated"],
        "actorIndex": observation["actorIndex"],
        "actorIndexHex": None if observation["actorIndex"] is None else f"0x{observation['actorIndex']:02X}",
        "actorSlot": None if actor is None else actor["slot"],
        "actorId": None if actor is None else actor["id"],
        "actorIdHex": None if actor is None else f"0x{actor['id']:04X}",
        "sourceRecordSigned": None if actor is None else actor["source_record"],
        "sourceRecordCanonical": None if actor is None else actor["sourceRecordCanonical"],
        "actorsWithSource": observation["actorsWithSource"],
        "bookmarkSourceMatches": observation["bookmarkSourceMatches"],
        "mappingConsistent": observation["mappingConsistent"],
    }


def cull_gap_analyses(
    record: int,
    stock_frames: Sequence[FrameState],
    wide_frames: Sequence[FrameState],
    stock_allocation_episodes: Sequence[dict[str, Any]],
    stock_eligibility: Sequence[bool] | None,
) -> list[dict[str, Any]]:
    gaps: list[dict[str, Any]] = []
    for episode_index, episode in enumerate(stock_allocation_episodes):
        release = episode.get("releaseFrame")
        if release is None:
            continue
        next_episode = (
            stock_allocation_episodes[episode_index + 1]
            if episode_index + 1 < len(stock_allocation_episodes)
            else None
        )
        next_start = int(next_episode["startFrame"]) if next_episode else None
        gap_end = (next_start - 1) if next_start is not None else stock_frames[-1].relative_frame
        wide_allocated_in_gap = [
            record_flag(wide_frames[frame], record, "allocated")
            for frame in range(int(release), int(gap_end) + 1)
        ]
        comparison_frame = None
        comparison_reason = None
        if next_start is not None:
            if stock_eligibility is not None:
                next_end = int(next_episode["lastActiveFrame"])
                comparison_frame = next(
                    (
                        frame
                        for frame in range(next_start, next_end + 1)
                        if stock_eligibility[frame] and record_flag(stock_frames[frame], record, "allocated")
                    ),
                    None,
                )
                if comparison_frame is not None:
                    comparison_reason = "first_subsequent_stock_eligible_allocation"
            if comparison_frame is None:
                comparison_frame = next_start
                comparison_reason = "stock_reallocation"
        # A later eligibility transition may follow the first reallocation
        # frame. Prove wide continuity through the actual comparison, not just
        # through the beginning of the new stock episode.
        continuous_check_end = comparison_frame if comparison_frame is not None else int(gap_end)
        continuity_start = max(0, int(release) - 1)
        wide_actor_indexes = [
            (wide_frames[frame].records.get(record) or {}).get("actorIndex")
            for frame in range(continuity_start, continuous_check_end + 1)
        ]
        wide_continuously_allocated = bool(wide_actor_indexes) and all(index is not None for index in wide_actor_indexes)
        wide_same_continuous_actor = wide_continuously_allocated and len(set(wide_actor_indexes)) == 1
        comparison = None
        if comparison_frame is not None:
            stock_actor = (stock_frames[comparison_frame].records.get(record) or {}).get("actor")
            wide_actor = (wide_frames[comparison_frame].records.get(record) or {}).get("actor")
            if stock_actor is not None and wide_actor is not None:
                comparison = actor_comparison(stock_actor, wide_actor)
        gaps.append(
            {
                "stockReleaseFrame": release,
                "stockLastAllocatedFrame": episode["lastActiveFrame"],
                "stockReallocationFrame": next_start,
                "stockGapLastFrame": gap_end,
                "stockInactiveFrameCount": max(0, int(gap_end) - int(release) + 1),
                "wideAllocatedFramesDuringStockCull": sum(wide_allocated_in_gap),
                "wideContinuouslyAllocatedAcrossGap": wide_continuously_allocated,
                "wideSameContinuousActorAcrossGap": wide_same_continuous_actor,
                "wideContinuousActorIndex": wide_actor_indexes[0] if wide_same_continuous_actor else None,
                "comparisonFrame": comparison_frame,
                "comparisonReason": comparison_reason,
                "stockEligibleAtComparison": None
                if comparison_frame is None or stock_eligibility is None
                else stock_eligibility[comparison_frame],
                "actorComparison": comparison,
                "stockMappingAtRelease": mapping_summary(stock_frames[int(release)].records.get(record)),
                "wideMappingAtRelease": mapping_summary(wide_frames[int(release)].records.get(record)),
                "stockMappingAtComparison": None
                if comparison_frame is None
                else mapping_summary(stock_frames[comparison_frame].records.get(record)),
                "wideMappingAtComparison": None
                if comparison_frame is None
                else mapping_summary(wide_frames[comparison_frame].records.get(record)),
            }
        )
    return gaps


def classify_record(
    record: int,
    stock_frames: Sequence[FrameState],
    wide_frames: Sequence[FrameState],
    catalog: dict[int, dict[str, Any]],
    stock_archive: str,
    wide_archive: str,
    stock_index: str,
    wide_index: str,
    margin: int,
) -> dict[str, Any]:
    first_wide_active = first_frame(wide_frames, record, "active")
    first_stock_active = first_frame(stock_frames, record, "active")
    first_wide_allocation = first_frame(wide_frames, record, "allocated")
    first_stock_allocation = first_frame(stock_frames, record, "allocated")
    first_stock_eligible = None
    first_wide_eligible = None
    stock_active_episodes = record_episodes(stock_frames, record, "active")
    wide_active_episodes = record_episodes(wide_frames, record, "active")
    stock_allocation_episodes = record_episodes(stock_frames, record, "allocated")
    wide_allocation_episodes = record_episodes(wide_frames, record, "allocated")
    stock_eligibility_values: list[bool] | None = None
    wide_eligibility_values: list[bool] | None = None
    stock_eligibility_episodes: list[dict[str, Any]] = []
    wide_eligibility_episodes: list[dict[str, Any]] = []
    eligibility_known = record in catalog and "type" in catalog[record] and "x" in catalog[record]
    if eligibility_known:
        stock_eligibility_values, stock_eligibility_episodes = eligibility_timeline(
            stock_frames, catalog[record], "stock", margin
        )
        wide_eligibility_values, wide_eligibility_episodes = eligibility_timeline(
            wide_frames, catalog[record], "wide", margin
        )
        if stock_eligibility_episodes:
            first_stock_eligible = int(stock_eligibility_episodes[0]["startFrame"])
        if wide_eligibility_episodes:
            first_wide_eligible = int(wide_eligibility_episodes[0]["startFrame"])
    cull_gaps = cull_gap_analyses(
        record,
        stock_frames,
        wide_frames,
        stock_allocation_episodes,
        stock_eligibility_values,
    )
    continuous_early_active = sum(
        int(gap["stockInactiveFrameCount"])
        for gap in cull_gaps
        if gap["wideSameContinuousActorAcrossGap"]
    )
    stop = first_stock_allocation if first_stock_allocation is not None else len(stock_frames)
    early_active = sum(
        1
        for index in range(min(stop, len(wide_frames)))
        if (wide_frames[index].records.get(record) or {}).get("active")
        and not (stock_frames[index].records.get(record) or {}).get("active")
    )
    early_before_eligibility = None
    if eligibility_known:
        early_before_eligibility = sum(
            1
            for index in range(min(len(stock_frames), len(wide_frames)))
            if (wide_frames[index].records.get(record) or {}).get("active")
            and stock_eligible(catalog[record], stock_frames[index]) is False
        )

    comparison = None
    classification = "no_observed_allocation"
    rationale = "Neither replay allocated an actor for this record within the observation horizon."
    evidence: list[dict[str, Any]] = []
    for variant, frame_number, frames, archive, frame_index in (
        ("wide", first_wide_allocation, wide_frames, wide_archive, wide_index),
        ("stock", first_stock_allocation, stock_frames, stock_archive, stock_index),
        ("wide", first_wide_active, wide_frames, wide_archive, wide_index),
        ("stock", first_stock_active, stock_frames, stock_archive, stock_index),
    ):
        if frame_number is not None:
            evidence.append(evidence_ref(variant, frames[frame_number], archive, frame_index))
    if first_wide_allocation is not None and first_stock_allocation is None:
        classification = "indeterminate_without_stock_allocation"
        rationale = "Wide allocated the record, but stock never allocated it within the horizon; phase safety cannot be judged."
    elif first_stock_allocation is not None:
        stock_observation = stock_frames[first_stock_allocation].records.get(record, {})
        wide_observation = wide_frames[first_stock_allocation].records.get(record, {})
        stock_actor = stock_observation.get("actor")
        wide_actor = wide_observation.get("actor")
        evidence.extend(
            [
                evidence_ref("stock", stock_frames[first_stock_allocation], stock_archive, stock_index),
                evidence_ref("wide", wide_frames[first_stock_allocation], wide_archive, wide_index),
            ]
        )
        if first_wide_allocation is None or first_wide_allocation > first_stock_allocation:
            classification = "behavior_phase_difference"
            rationale = "Stock allocated before wide, so this is not a wide visual-prefetch lead."
        elif wide_actor is None:
            classification = "behavior_phase_advancement"
            rationale = "Wide allocated early but the mapped actor was already absent at the stock allocation frame."
        elif stock_actor is None:
            classification = "mapping_inconsistent"
            rationale = "Stock allocation was observed without a recoverable mapped actor."
        else:
            comparison = actor_comparison(stock_actor, wide_actor)
            if comparison["behaviorRelevantDifference"]:
                if first_wide_allocation < first_stock_allocation:
                    classification = "behavior_phase_advancement"
                    rationale = (
                        "After an earlier wide allocation, wide differs at the stock allocation frame in identity, "
                        "position, motion, state, or conservative collision-candidate fields."
                    )
                else:
                    classification = "behavior_phase_difference"
                    rationale = (
                        "Allocation was not early, but the aligned actors differ in identity, position, motion, state, "
                        "or conservative collision-candidate fields."
                    )
            elif first_wide_allocation < first_stock_allocation:
                classification = "harmless_visual_prefetch"
                rationale = (
                    "Wide allocated earlier, but at the stock allocation frame fields match exactly or differ only "
                    "in animation/render phase."
                )
            else:
                classification = "synchronized_allocation"
                rationale = "Both variants first allocated on the same relative frame without behavior-relevant drift."
    elif first_wide_active is not None or first_stock_active is not None:
        classification = "active_marker_without_actor_comparison"
        rationale = "A bookmark/group marker was active, but no comparable actor allocation was observed."

    classification_comparison = comparison
    classification_comparison_frame = first_stock_allocation
    behavior_gap = next(
        (
            gap
            for gap in cull_gaps
            if gap["wideSameContinuousActorAcrossGap"]
            and gap["actorComparison"] is not None
            and gap["actorComparison"]["behaviorRelevantDifference"]
        ),
        None,
    )
    persistent_without_reload = next(
        (
            gap
            for gap in cull_gaps
            if gap["wideSameContinuousActorAcrossGap"] and gap["stockReallocationFrame"] is None
        ),
        None,
    )
    harmless_gap = next(
        (
            gap
            for gap in cull_gaps
            if gap["wideSameContinuousActorAcrossGap"]
            and gap["actorComparison"] is not None
            and not gap["actorComparison"]["behaviorRelevantDifference"]
        ),
        None,
    )
    if behavior_gap is not None:
        classification = "behavior_phase_advancement"
        classification_comparison = behavior_gap["actorComparison"]
        classification_comparison_frame = behavior_gap["comparisonFrame"]
        rationale = (
            "Stock culled and later reallocated the record while wide kept the same actor continuously active; "
            "at the subsequent stock allocation/eligibility comparison, position, motion, state, identity, or "
            "collision-candidate fields had advanced."
        )
    elif persistent_without_reload is not None and classification not in {
        "behavior_phase_advancement",
        "behavior_phase_difference",
    }:
        classification = "wide_persists_stock_culls"
        classification_comparison = None
        classification_comparison_frame = None
        rationale = (
            "Stock released the record while wide kept the same actor continuously active, and stock did not "
            "reallocate within the horizon. This is indeterminate and must not be labeled harmless."
        )
    elif harmless_gap is not None and classification not in {
        "behavior_phase_advancement",
        "behavior_phase_difference",
    }:
        classification = "harmless_visual_prefetch"
        classification_comparison = harmless_gap["actorComparison"]
        classification_comparison_frame = harmless_gap["comparisonFrame"]
        rationale = (
            "Stock culled and reallocated while wide kept the same actor continuously active, but the subsequent "
            "stock comparison matched behavior fields exactly or differed only in animation/render phase."
        )

    for gap in cull_gaps:
        release = int(gap["stockReleaseFrame"])
        evidence.extend(
            [
                evidence_ref("stock", stock_frames[release], stock_archive, stock_index),
                evidence_ref("wide", wide_frames[release], wide_archive, wide_index),
            ]
        )
        comparison_frame = gap.get("comparisonFrame")
        if comparison_frame is not None:
            evidence.extend(
                [
                    evidence_ref("stock", stock_frames[int(comparison_frame)], stock_archive, stock_index),
                    evidence_ref("wide", wide_frames[int(comparison_frame)], wide_archive, wide_index),
                ]
            )
        elif gap["stockReallocationFrame"] is None:
            evidence.extend(
                [
                    evidence_ref("stock", stock_frames[-1], stock_archive, stock_index),
                    evidence_ref("wide", wide_frames[-1], wide_archive, wide_index),
                ]
            )

    classification_disposition = (
        "behavioral"
        if classification in {"behavior_phase_advancement", "behavior_phase_difference"}
        else "harmless"
        if classification == "harmless_visual_prefetch"
        else "neutral"
        if classification in {"synchronized_allocation", "no_observed_allocation"}
        else "indeterminate"
    )

    # De-duplicate evidence references from first-allocation and comparison-frame overlap.
    unique_evidence: list[dict[str, Any]] = []
    seen: set[tuple[str, int]] = set()
    for item in evidence:
        key = (item["variant"], item["relativeFrame"])
        if key not in seen:
            seen.add(key)
            unique_evidence.append(item)
    return {
        "recordIndex": record,
        "recordIndexHex": f"0x{record:02X}",
        "catalog": catalog.get(record),
        "firstWideActiveFrame": first_wide_active,
        "firstStockActiveFrame": first_stock_active,
        "firstWideAllocationFrame": first_wide_allocation,
        "firstStockAllocationFrame": first_stock_allocation,
        "firstWideActiveMapping": None
        if first_wide_active is None
        else mapping_summary(wide_frames[first_wide_active].records.get(record)),
        "firstStockActiveMapping": None
        if first_stock_active is None
        else mapping_summary(stock_frames[first_stock_active].records.get(record)),
        "firstWideAllocationMapping": None
        if first_wide_allocation is None
        else mapping_summary(wide_frames[first_wide_allocation].records.get(record)),
        "firstStockAllocationMapping": None
        if first_stock_allocation is None
        else mapping_summary(stock_frames[first_stock_allocation].records.get(record)),
        "firstStockEligibleFrame": first_stock_eligible,
        "firstWideEligibleFrame": first_wide_eligible,
        "stockEligibilityKnown": eligibility_known,
        "wideAllocationLeftCensored": first_wide_allocation == 0,
        "stockAllocationLeftCensored": first_stock_allocation == 0,
        "prefetchLeadFrames": None
        if first_wide_allocation is None or first_stock_allocation is None
        else first_stock_allocation - first_wide_allocation,
        "earlyActiveFrameCount": early_active,
        "earlyActiveBeforeStockEligibilityFrameCount": early_before_eligibility,
        "continuousEarlyActiveFrames": continuous_early_active,
        "stockActiveEpisodes": stock_active_episodes,
        "wideActiveEpisodes": wide_active_episodes,
        "stockAllocationEpisodes": stock_allocation_episodes,
        "wideAllocationEpisodes": wide_allocation_episodes,
        "stockReleaseFrames": [episode["releaseFrame"] for episode in stock_allocation_episodes if episode["releaseFrame"] is not None],
        "wideReleaseFrames": [episode["releaseFrame"] for episode in wide_allocation_episodes if episode["releaseFrame"] is not None],
        "stockReallocationFrames": [episode["startFrame"] for episode in stock_allocation_episodes if episode["reallocation"]],
        "wideReallocationFrames": [episode["startFrame"] for episode in wide_allocation_episodes if episode["reallocation"]],
        "stockEligibilityEpisodes": stock_eligibility_episodes,
        "wideEligibilityEpisodes": wide_eligibility_episodes,
        "stockBecameEligibleFrames": [episode["startFrame"] for episode in stock_eligibility_episodes],
        "stockBecameIneligibleFrames": [episode["releaseFrame"] for episode in stock_eligibility_episodes if episode["releaseFrame"] is not None],
        "stockEligibleFrameCount": None if stock_eligibility_values is None else sum(stock_eligibility_values),
        "wideEligibleFrameCount": None if wide_eligibility_values is None else sum(wide_eligibility_values),
        "stockCullGaps": cull_gaps,
        "classification": classification,
        "classificationDisposition": classification_disposition,
        "classificationRationale": rationale,
        "actorComparisonAtStockAllocationFrame": comparison,
        "classificationComparisonFrame": classification_comparison_frame,
        "classificationActorComparison": classification_comparison,
        "mappingAtStockAllocationFrame": None
        if first_stock_allocation is None
        else {
            "stock": mapping_summary(stock_frames[first_stock_allocation].records.get(record)),
            "wide": mapping_summary(wide_frames[first_stock_allocation].records.get(record)),
        },
        "evidence": unique_evidence,
    }


def analyze_replays(
    stock_frames: Sequence[FrameState],
    wide_frames: Sequence[FrameState],
    state: dict[str, Any],
    stock_archive: str,
    wide_archive: str,
    stock_index: str,
    wide_index: str,
    margin: int = 0x38,
) -> dict[str, Any]:
    if len(stock_frames) != len(wide_frames) or not stock_frames:
        raise AuditError("Stock and wide replays must contain the same non-zero number of aligned frames.")
    catalog = normalized_catalog(state)
    if not state.get("trackRecords", True):
        return {
            "recordTrackingEnabled": False,
            "reason": "Recipe marks this state as outside gameplay bank-BD object tracking.",
            "recordScope": "none",
            "records": [],
        }
    record_ids = set(catalog)
    for frame in [*stock_frames, *wide_frames]:
        record_ids.update(frame.records)
    rows = [
        classify_record(
            record,
            stock_frames,
            wide_frames,
            catalog,
            stock_archive,
            wide_archive,
            stock_index,
            wide_index,
            margin,
        )
        for record in sorted(record_ids)
    ]
    return {
        "recordTrackingEnabled": True,
        "recordScope": "union of dynamically observed bookmarks/source backlinks and recipe-cataloged records",
        "catalogRecordCount": len(catalog),
        "observedOrCatalogedRecordCount": len(rows),
        "unobservedUncatalogedAuthoredRecordsOmitted": True,
        "records": rows,
        "classificationCounts": counts(row["classification"] for row in rows),
        "behaviorPhaseAdvancementRecords": [
            row["recordIndex"] for row in rows if row["classification"] == "behavior_phase_advancement"
        ],
        "harmlessVisualPrefetchRecords": [
            row["recordIndex"] for row in rows if row["classification"] == "harmless_visual_prefetch"
        ],
        "widePersistsStockCullsRecords": [
            row["recordIndex"] for row in rows if row["classification"] == "wide_persists_stock_culls"
        ],
    }


def counts(values: Iterable[str]) -> dict[str, int]:
    result: dict[str, int] = {}
    for value in values:
        result[value] = result.get(value, 0) + 1
    return result


class SnapshotArchive:
    """Fixed-record raw WRAM stream plus a JSONL hash/offset index."""

    def __init__(self, folder: Path, variant: str) -> None:
        self.archive_path = folder / f"{variant}-wram.frames.bin.gz"
        self.index_path = folder / f"{variant}-wram.frames.jsonl"
        self._archive = gzip.open(self.archive_path, "wb", compresslevel=6)
        self._index = self.index_path.open("w", encoding="utf-8", newline="\n")
        self.frames = 0

    def append(self, memory: bytes, metadata: dict[str, Any]) -> None:
        if len(memory) != WRAM_SIZE:
            raise AuditError("Cannot archive a partial WRAM snapshot.")
        record = {
            "relativeFrame": self.frames,
            "emulatorFrame": metadata["emulatorFrame"],
            "sha256": metadata["sha256"],
            "uncompressedOffset": self.frames * WRAM_SIZE,
            "length": WRAM_SIZE,
        }
        self._archive.write(memory)
        self._index.write(json.dumps(record, separators=(",", ":")) + "\n")
        self.frames += 1

    def close(self) -> None:
        self._archive.close()
        self._index.close()

    def __enter__(self) -> "SnapshotArchive":
        return self

    def __exit__(self, *_args: object) -> None:
        self.close()


def decode_snapshot(result: Any, label: str) -> tuple[bytes, dict[str, Any]]:
    if not isinstance(result, dict) or result.get("encoding") != "base64":
        raise AuditError(f"{label}: snapshot_wram returned an unexpected result.")
    try:
        memory = base64.b64decode(result["data"], validate=True)
    except (KeyError, ValueError) as exc:
        raise AuditError(f"{label}: snapshot_wram returned invalid base64.") from exc
    if len(memory) != WRAM_SIZE:
        raise AuditError(f"{label}: snapshot_wram returned {len(memory)} bytes instead of {WRAM_SIZE}.")
    digest = sha256_bytes(memory)
    if digest != str(result.get("sha256", "")).upper():
        raise AuditError(f"{label}: snapshot_wram digest did not match its payload.")
    if not result.get("paused"):
        raise AuditError(f"{label}: snapshot was not paused.")
    return memory, {"emulatorFrame": int(result["frame"]), "sha256": digest}


class BridgeClient:
    def __init__(self, endpoint: Path, timeout: float) -> None:
        self.endpoint = endpoint.resolve()
        try:
            self.info = json.loads(self.endpoint.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            raise AuditError(f"Could not read automation endpoint {self.endpoint}: {exc}") from exc
        host = str(self.info.get("host", ""))
        if host not in {"127.0.0.1", "localhost", "::1"}:
            raise AuditError(f"Refusing non-loopback automation host {host!r}.")
        if self.info.get("pluginVersion") != REQUIRED_BRIDGE_VERSION:
            raise AuditError(
                f"Expected DKCLevelAutomation {REQUIRED_BRIDGE_VERSION}, endpoint reports "
                f"{self.info.get('pluginVersion')!r}."
            )
        if self.info.get("protocol") != 1:
            raise AuditError(f"Expected bridge protocol 1, got {self.info.get('protocol')!r}.")
        self.timeout = timeout

    @staticmethod
    def encode(value: Any) -> str:
        if isinstance(value, bool):
            value = "true" if value else "false"
        return base64.b64encode(str(value).encode("utf-8")).decode("ascii")

    def request(self, command: str, arguments: dict[str, Any] | None = None) -> Any:
        request_id = uuid.uuid4().hex
        fields = [request_id, str(self.info["token"]), command]
        for key, value in (arguments or {}).items():
            fields.extend([self.encode(key), self.encode(value)])
        wire = ("\t".join(fields) + "\n").encode("utf-8")
        try:
            with socket.create_connection((str(self.info["host"]), int(self.info["port"])), self.timeout) as conn:
                conn.settimeout(self.timeout)
                conn.sendall(wire)
                received = bytearray()
                while b"\n" not in received:
                    block = conn.recv(65536)
                    if not block:
                        break
                    received.extend(block)
        except OSError as exc:
            raise AuditError(f"Automation request {command!r} failed: {exc}") from exc
        if not received:
            raise AuditError(f"Automation bridge closed without replying to {command!r}.")
        try:
            reply = json.loads(bytes(received).split(b"\n", 1)[0].decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise AuditError(f"Automation bridge returned invalid JSON for {command!r}.") from exc
        if not reply.get("ok"):
            raise AuditError(str(reply.get("error", f"Automation command {command!r} failed.")))
        return reply.get("result")


class PrefetchRunner:
    def __init__(self, recipe: dict[str, Any], client: BridgeClient, output: Path) -> None:
        self.recipe = recipe
        self.client = client
        self.output = output

    def prepare(self, rom: Path, state: Path, scenario: dict[str, Any]) -> None:
        self.client.request("load_rom", {"path": str(rom), "load_last_state": False})
        self.client.request("load_state_file", {"path": str(state)})
        self.client.request("pause")
        for controller in sorted(scenario["inputs"], key=int):
            self.client.request("schedule", {"controller": int(controller), "macro": scenario["inputs"][controller]})

    def replay(
        self,
        variant: str,
        rom: Path,
        state_path: Path,
        state: dict[str, Any],
        scenario: dict[str, Any],
        folder: Path,
    ) -> tuple[list[FrameState], dict[str, Any]]:
        self.prepare(rom, state_path, scenario)
        frames: list[FrameState] = []
        archive: SnapshotArchive | None = None
        try:
            archive = SnapshotArchive(folder, variant)
            with archive:
                for relative_frame in range(scenario["maxFrame"] + 1):
                    if relative_frame:
                        self.client.request(
                            "step_frames",
                            {"count": 1, "timeout_ms": scenario.get("timeoutMs", 60000)},
                        )
                    memory, metadata = decode_snapshot(
                        self.client.request("snapshot_wram"),
                        f"{variant} frame {relative_frame}",
                    )
                    archive.append(memory, metadata)
                    frame = parse_frame(memory, relative_frame, metadata["emulatorFrame"], metadata["sha256"])
                    if relative_frame == 0 and "expectedLevel" in state:
                        expected = parse_integer(state["expectedLevel"], f"state {state['id']} expectedLevel")
                        if frame.level_id != expected:
                            raise AuditError(
                                f"{state['id']}/{scenario['id']} {variant} loaded level "
                                f"0x{frame.level_id:04X}, expected 0x{expected:04X}."
                            )
                    frames.append(frame)
        finally:
            for controller in scenario["inputs"]:
                try:
                    self.client.request("clear_schedule", {"controller": int(controller)})
                except AuditError:
                    pass
        if archive is None:
            raise AuditError(f"Could not create {variant} evidence archive.")
        return frames, {
            "variant": variant,
            "rom": str(rom),
            "romSha256": sha256_file(rom),
            "archive": str(archive.archive_path),
            "index": str(archive.index_path),
            "frameCount": len(frames),
            "firstEmulatorFrame": frames[0].emulator_frame,
            "lastEmulatorFrame": frames[-1].emulator_frame,
        }

    def run_case(
        self,
        stock_rom: Path,
        wide_rom: Path,
        state: dict[str, Any],
        state_path: Path,
        scenario: dict[str, Any],
    ) -> dict[str, Any]:
        print(f"auditing: {state['id']} / {scenario['id']}", flush=True)
        folder = self.output / "cases" / safe_name(state["id"]) / safe_name(scenario["id"])
        folder.mkdir(parents=True, exist_ok=True)
        # Complete stock replay before beginning the wide replay. No lockstep or
        # concurrent-emulator assumption exists anywhere in the model.
        stock_frames, stock_evidence = self.replay("stock", stock_rom, state_path, state, scenario, folder)
        wide_frames, wide_evidence = self.replay("wide", wide_rom, state_path, state, scenario, folder)
        analysis = analyze_replays(
            stock_frames,
            wide_frames,
            state,
            stock_evidence["archive"],
            wide_evidence["archive"],
            stock_evidence["index"],
            wide_evidence["index"],
            int(self.recipe.get("margin", 0x38)),
        )
        return {
            "state": state["id"],
            "identity": state.get("identity", ""),
            "statePath": str(state_path),
            "stateSha256": sha256_file(state_path),
            "scenario": scenario["id"],
            "inputs": scenario["inputs"],
            "maxFrame": scenario["maxFrame"],
            "stockReplay": stock_evidence,
            "wideReplay": wide_evidence,
            "analysis": analysis,
        }

    def run(
        self,
        stock_rom: Path,
        wide_rom: Path,
        states: dict[str, Path],
        selected_cases: set[str],
    ) -> dict[str, Any]:
        self.output.mkdir(parents=True, exist_ok=True)
        report: dict[str, Any] = {
            "schemaVersion": 1,
            "tool": "DKCObjectPrefetchPhaseAuditor",
            "requiredAutomationVersion": REQUIRED_BRIDGE_VERSION,
            "startedUtc": utc_now(),
            "completedUtc": None,
            "recipe": self.recipe["name"],
            "stockRom": {"path": str(stock_rom), "sha256": sha256_file(stock_rom)},
            "wideRom": {"path": str(wide_rom), "sha256": sha256_file(wide_rom)},
            "collisionCandidateLimitation": (
                "Generic per-actor RAM tables are compared conservatively because their exact semantics vary by actor type; "
                "a difference is phase evidence, not proof that a collision occurred."
            ),
            "cases": [],
        }
        report_path = self.output / "report.json"
        for state in self.recipe["states"]:
            for scenario in state["scenarios"]:
                key = f"{state['id']}/{scenario['id']}"
                if selected_cases and key not in selected_cases:
                    continue
                report["cases"].append(self.run_case(stock_rom, wide_rom, state, states[state["id"]], scenario))
                report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        classifications = [
            row["classification"]
            for case in report["cases"]
            for row in case["analysis"].get("records", [])
        ]
        report["completedUtc"] = utc_now()
        report["summary"] = {
            "casesRun": len(report["cases"]),
            "recordClassifications": counts(classifications),
            "behaviorPhaseAdvancementCount": classifications.count("behavior_phase_advancement"),
            "harmlessVisualPrefetchCount": classifications.count("harmless_visual_prefetch"),
            "widePersistsStockCullsCount": classifications.count("wide_persists_stock_culls"),
        }
        report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        return report


def locate_recipe(value: str) -> Path:
    direct = Path(value)
    bundled = Path(__file__).resolve().parent / "recipes"
    for candidate in (direct, bundled / value, bundled / f"{value}.json"):
        if candidate.is_file():
            return candidate.resolve()
    raise AuditError(f"Recipe was not found: {value}")


def resolve_states(recipe: dict[str, Any], state_dir: Path | None, assignments: list[str]) -> dict[str, Path]:
    overrides: dict[str, Path] = {}
    for assignment in assignments:
        if "=" not in assignment:
            raise AuditError("--state must use STATE_ID=path syntax.")
        state_id, value = assignment.split("=", 1)
        overrides[state_id] = Path(value).resolve()
    known = {state["id"] for state in recipe["states"]}
    unknown = set(overrides) - known
    if unknown:
        raise AuditError("--state named unknown ids: " + ", ".join(sorted(unknown)))
    return {
        state["id"]: overrides.get(
            state["id"],
            ((state_dir / state["file"]).resolve() if state_dir else Path(state["file"]).resolve()),
        )
        for state in recipe["states"]
    }


def case_keys(recipe: dict[str, Any]) -> set[str]:
    return {f"{state['id']}/{scenario['id']}" for state in recipe["states"] for scenario in state["scenarios"]}


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Sequential stock-vs-wide bank-BD object prefetch phase auditor using atomic WRAM snapshots."
    )
    parser.add_argument("--recipe", required=True, help="Bundled recipe name or JSON path.")
    parser.add_argument("--stock-rom")
    parser.add_argument("--wide-rom")
    parser.add_argument("--state-dir")
    parser.add_argument("--state", action="append", default=[], help="Override as STATE_ID=path.")
    parser.add_argument("--case", action="append", default=[], help="Run only STATE_ID/SCENARIO_ID; repeatable.")
    parser.add_argument("--automation-endpoint", help="Path to DKCLevelAutomation v0.1.3 bridge.json.")
    parser.add_argument("--output")
    parser.add_argument("--socket-timeout", type=float, default=190.0)
    parser.add_argument(
        "--validate-only",
        action="store_true",
        help="Validate and print the plan without checking ROM/state/endpoint files or contacting a bridge.",
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        recipe_path = locate_recipe(args.recipe)
        recipe = validate_recipe(json.loads(recipe_path.read_text(encoding="utf-8")), str(recipe_path))
        state_dir = Path(args.state_dir).resolve() if args.state_dir else None
        states = resolve_states(recipe, state_dir, args.state)
        selected = set(args.case)
        unknown = selected - case_keys(recipe)
        if unknown:
            raise AuditError("Unknown --case values: " + ", ".join(sorted(unknown)))
        plan = {
            "ok": True,
            "mode": "validate-only" if args.validate_only else "run",
            "recipe": recipe["name"],
            "recipePath": str(recipe_path),
            "states": {key: str(value) for key, value in states.items()},
            "cases": sorted(selected or case_keys(recipe)),
            "catalogRecordCount": sum(len(state.get("records", [])) for state in recipe["states"]),
            "automationContacted": False,
        }
        if args.validate_only:
            print(json.dumps(plan, indent=2))
            return 0
        if not args.stock_rom or not args.wide_rom or not args.automation_endpoint:
            raise AuditError("Run mode requires --stock-rom, --wide-rom, and --automation-endpoint.")
        stock_rom = Path(args.stock_rom).resolve()
        wide_rom = Path(args.wide_rom).resolve()
        endpoint = Path(args.automation_endpoint).resolve()
        selected_or_all = selected or case_keys(recipe)
        needed_state_ids = {
            state["id"]
            for state in recipe["states"]
            if any(case.startswith(state["id"] + "/") for case in selected_or_all)
        }
        missing = [path for path in (stock_rom, wide_rom, endpoint) if not path.is_file()]
        missing.extend(states[state_id] for state_id in needed_state_ids if not states[state_id].is_file())
        if missing:
            raise AuditError("Required files were not found: " + ", ".join(str(path) for path in missing))
        timestamp = datetime.now().strftime("%Y%m%d-%H%M%S")
        output = Path(args.output).resolve() if args.output else (
            Path(__file__).resolve().parent / "PrefetchRuns" / f"{safe_name(recipe['name'])}-{timestamp}"
        )
        if output.exists() and any(output.iterdir()):
            raise AuditError(f"Output directory is not empty; refusing to overwrite evidence: {output}")
        runner = PrefetchRunner(recipe, BridgeClient(endpoint, args.socket_timeout), output)
        report = runner.run(stock_rom, wide_rom, states, selected)
        print(json.dumps({**plan, "automationContacted": True, "output": str(output), **report["summary"]}, indent=2))
        return 0
    except (AuditError, OSError, KeyError, json.JSONDecodeError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
