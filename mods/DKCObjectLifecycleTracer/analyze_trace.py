#!/usr/bin/env python3
"""Summarize a DKCObjectLifecycleTracer session using only the Python stdlib."""

from __future__ import annotations

import argparse
import collections
import json
from pathlib import Path


def rows(path: Path):
    if not path.exists():
        return
    with path.open("r", encoding="utf-8") as handle:
        for line_no, line in enumerate(handle, 1):
            try:
                yield json.loads(line)
            except json.JSONDecodeError as exc:
                raise SystemExit(f"{path}:{line_no}: malformed JSONL: {exc}") from exc


LEGACY_OBSERVATION_TEXT = (
    "points to missing/invalid actor index",
    "is owned by bookkeeping records",
    "whose source record is",
    "does not point back to it",
    "missing child bookmark",
    "is $FF but is not a decoded type-5 group root",
)

SCANNER_PC_MEANINGS = {
    "$BDF3A2": "begin primary actor-pool search ($02-$1C)",
    "$BDF3B1": "primary actor pool exhausted; allocation index cleared",
    "$BDF3B5": "primary actor index found; reserve it",
    "$BDF3BA": "mark primary actor source $8000 (reserved)",
    "$BDF3BD": "primary actor reservation succeeded (carry clear)",
    "$BDF3C3": "begin secondary actor-pool search ($1E-$32)",
    "$BDF3D2": "secondary actor pool exhausted; allocation index cleared",
    "$BDF3D6": "secondary actor index found; reserve it",
    "$BDF3DB": "mark secondary actor source $8000 (reserved)",
    "$BDF3DE": "secondary actor reservation succeeded (carry clear)",
}


def scanner_meaning(row):
    return SCANNER_PC_MEANINGS.get(str(row.get("pc", "")).upper(), row.get("decision", "unknown"))


def is_legacy_observation(row):
    """v0.1 called transient/stale ownership states anomalies.

    DKC clears actor identity/source and its $192B bookmark at separate scanner
    sites, so those messages are leads rather than proof. Keep the old evidence
    visible without counting it as a definitive failure.
    """
    message = row.get("message", "")
    return row.get("type") in {"anomaly_started", "anomaly_ended"} and any(
        marker in message for marker in LEGACY_OBSERVATION_TEXT
    )


def segment_events(events):
    segment = 0
    result = []
    for row in events:
        if row.get("type") == "context_reset":
            segment += 1
        annotated = dict(row)
        annotated["traceSegment"] = segment
        result.append(annotated)
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("session", type=Path, help="Sessions/<timestamp> directory")
    parser.add_argument("--json", action="store_true", help="emit machine-readable JSON instead of text")
    args = parser.parse_args()
    session = args.session.resolve()
    if not session.is_dir():
        parser.error(f"not a session directory: {session}")

    events = segment_events(list(rows(session / "events.jsonl") or []))
    writes = list(rows(session / "writes.jsonl") or [])
    scanner = list(rows(session / "scanner.jsonl") or [])
    raw_kinds = collections.Counter(row.get("type", "unknown") for row in events)
    kinds = collections.Counter()
    for row in events:
        kind = row.get("type", "unknown")
        if is_legacy_observation(row):
            kind = "legacy_observation_started" if kind == "anomaly_started" else "legacy_observation_ended"
        kinds[kind] += 1
    decisions = collections.Counter(scanner_meaning(row) for row in scanner)
    corrected_scanner_labels = sum(
        1 for row in scanner
        if str(row.get("pc", "")).upper() in SCANNER_PC_MEANINGS
        and row.get("decision") != scanner_meaning(row)
    )
    writer_pcs = collections.Counter(row.get("pc", "unknown") for row in writes)
    definitive_anomalies = [
        row for row in events
        if row.get("type") == "anomaly_started" and not is_legacy_observation(row)
    ]
    observations = [
        row for row in events
        if row.get("type") == "observation_started"
        or (row.get("type") == "anomaly_started" and is_legacy_observation(row))
    ]
    unresolved = {}
    unresolved_observations = {}
    for row in events:
        message = row.get("message")
        key = (row.get("traceSegment"), message)
        if row.get("type") == "anomaly_started" and message:
            if is_legacy_observation(row):
                unresolved_observations[key] = row.get("frame")
            else:
                unresolved[key] = row.get("frame")
        elif row.get("type") == "observation_started" and message:
            unresolved_observations[key] = row.get("frame")
        elif row.get("type") == "anomaly_ended" and message:
            if is_legacy_observation(row):
                unresolved_observations.pop(key, None)
            else:
                unresolved.pop(key, None)
        elif row.get("type") == "observation_ended" and message:
            unresolved_observations.pop(key, None)
    allocations = [row for row in events if row.get("type") == "actor_allocated"]
    frees = [row for row in events if row.get("type") == "actor_freed"]
    max_segment = max((row["traceSegment"] for row in events), default=0)
    observation_signatures = []
    for message in sorted({row.get("message") for row in observations if row.get("message")}):
        matches = [row for row in observations if row.get("message") == message]
        observation_signatures.append({
            "message": message,
            "starts": len(matches),
            "traceSegments": sorted({row["traceSegment"] for row in matches}),
            "firstFrame": min((row.get("frame") for row in matches if row.get("frame") is not None), default=None),
            "lastFrame": max((row.get("frame") for row in matches if row.get("frame") is not None), default=None),
        })

    report = {
        "session": str(session),
        "eventCounts": dict(kinds),
        "rawEventCounts": dict(raw_kinds),
        "allocations": len(allocations),
        "deallocations": len(frees),
        "traceSegments": max_segment + 1,
        "definitiveAnomalyStarts": len(definitive_anomalies),
        "unresolvedDefinitiveAnomalies": [
            {"traceSegment": key[0], "message": key[1], "frame": frame}
            for key, frame in unresolved.items()
        ],
        "rawObservationStarts": len(observations),
        "uniqueObservationMessages": len({row.get("message") for row in observations}),
        "observationSignatures": observation_signatures,
        "currentSegmentUnresolvedObservations": [
            {"traceSegment": key[0], "message": key[1], "frame": frame}
            for key, frame in unresolved_observations.items() if key[0] == max_segment
        ],
        "legacyV01AnomaliesReclassifiedAsObservations": sum(
            1 for row in observations if row.get("type") == "anomaly_started"
        ),
        "topScannerDecisions": decisions.most_common(20),
        "legacyScannerLabelsCorrectedByPC": corrected_scanner_labels,
        "topRelevantWritePCs": writer_pcs.most_common(20),
        "lastLifecycleEvents": [
            {k: row.get(k) for k in ("type", "frame", "index", "record", "lastWriter")}
            for row in events
            if row.get("type") in {"actor_allocated", "actor_freed", "actor_replaced", "bookkeeping_changed"}
        ][-30:],
    }
    if args.json:
        print(json.dumps(report, indent=2))
        return 0

    print(f"DKC object lifecycle trace: {session}")
    print(f"Lifecycle: {len(allocations)} allocations, {len(frees)} deallocations")
    print(f"Trace segments (state-load/replay timelines): {report['traceSegments']}")
    print(f"Definitive anomalies: {len(definitive_anomalies)} started, {len(unresolved)} unresolved")
    print(
        f"Non-definitive observations: {report['uniqueObservationMessages']} unique messages "
        f"across {report['traceSegments']} replay segments"
    )
    if report["legacyV01AnomaliesReclassifiedAsObservations"]:
        print(
            "  Reclassified v0.1 transient bookmark/source/group messages: "
            f"{report['legacyV01AnomaliesReclassifiedAsObservations']}"
        )
    for (segment, message), frame in unresolved.items():
        print(f"  definitive, segment {segment}, frame {frame}: {message}")
    if decisions:
        print("Most frequent scanner decisions:")
        for decision, count in decisions.most_common(12):
            print(f"  {count:6d}  {decision}")
    if writer_pcs:
        print("Most frequent relevant write PCs:")
        for pc, count in writer_pcs.most_common(12):
            print(f"  {count:6d}  {pc}")
    print("Event counts: " + ", ".join(f"{key}={value}" for key, value in sorted(kinds.items())))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
