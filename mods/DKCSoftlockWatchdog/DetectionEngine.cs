using System;
using System.Collections.Generic;
using System.Linq;

namespace DKCSoftlockWatchdog
{
    internal sealed class DetectionOptions
    {
        public int EligibleFrames = 12;
        public int OwnershipFrames = 4;
        public int GroupFrames = 8;
        public int ContradictionFrames = 3;
        public int TriggerCooldownFrames = 300;
        public bool EligibleWithoutAllocation = true;
        public bool BookedActorMissing = true;
        public bool MissingGroupChildren = true;
        public bool Type9Contradictions = true;
        public bool ScannerContradictions = true;
        public bool AllocatorExhaustion = true;
        public bool ExactAllocatorWitness = true;
    }

    internal sealed class AllocatorWitness
    {
        public int Frame;
        public int RecordIndex;
        public bool Secondary;
        public uint Pc;
        public int[] OccupiedIndices;

        public IDictionary<string, object> ToData()
        {
            return new Dictionary<string, object>
            {
                { "frame", Frame }, { "recordIndex", RecordIndex }, { "recordIndexHex", Format.Hex((uint)RecordIndex, 2) },
                { "pool", Secondary ? "secondary" : "primary" }, { "pc", Format.Hex(Pc, 6) },
                { "occupiedActorIndices", (OccupiedIndices ?? Array.Empty<int>()).Select(index => (object)Format.Hex((uint)index, 2)).ToArray() },
                { "opcodeMeaning", Secondary ? "secondary allocator exhausted ($1E-$32)" : "primary allocator exhausted ($02-$1C)" }
            };
        }
    }

    internal sealed class Detection
    {
        public string Key;
        public string Condition;
        public string Summary;
        public int PersistenceFrames;
        public int? RecordIndex;
        public bool Definitive;
        public IDictionary<string, object> Details;
    }

    internal sealed class Candidate
    {
        public string Key;
        public string Condition;
        public string Summary;
        public int Threshold;
        public int? RecordIndex;
        public bool Definitive;
        public IDictionary<string, object> Details;
    }

    internal sealed class CandidateState
    {
        public int Count;
        public bool Fired;
    }

    internal sealed class RecordHistory
    {
        public bool WasEligible;
        public bool WasOwned;
        public bool EverOwned;
    }

    internal sealed class WatchdogDetector
    {
        private readonly DetectionOptions _options;
        private readonly Dictionary<string, CandidateState> _states = new Dictionary<string, CandidateState>(StringComparer.Ordinal);
        private readonly Dictionary<int, RecordHistory> _history = new Dictionary<int, RecordHistory>();
        private readonly Dictionary<string, int> _lastTriggerFrame = new Dictionary<string, int>(StringComparer.Ordinal);
        private bool _primed;
        private ushort _level = 0xFFFF;
        private ushort _entrance = 0xFFFF;

        public WatchdogDetector(DetectionOptions options)
        {
            _options = options ?? throw new ArgumentNullException("options");
        }

        public int ActiveCandidateCount { get { return _states.Count; } }

        public void Reset()
        {
            _states.Clear();
            _history.Clear();
            _lastTriggerFrame.Clear();
            _primed = false;
            _level = 0xFFFF;
            _entrance = 0xFFFF;
        }

        public List<Detection> Evaluate(FrameState frame, ObjectTable table, IEnumerable<AllocatorWitness> witnesses)
        {
            var detections = new List<Detection>();
            if (frame == null || !frame.GameplayActive || table == null || table.Error != null)
            {
                Reset();
                return detections;
            }
            if (_level != frame.LevelId || _entrance != frame.EntranceId)
            {
                Reset();
                _level = frame.LevelId;
                _entrance = frame.EntranceId;
            }

            var candidates = BuildCandidates(frame, table, witnesses ?? Array.Empty<AllocatorWitness>()).ToList();
            if (!_primed)
            {
                PrimeHistory(frame, table);
                _primed = true;
                return detections;
            }

            var current = new HashSet<string>(candidates.Select(candidate => candidate.Key), StringComparer.Ordinal);
            foreach (var stale in _states.Keys.Where(key => !current.Contains(key)).ToArray()) _states.Remove(stale);
            foreach (var candidate in candidates)
            {
                CandidateState state;
                if (!_states.TryGetValue(candidate.Key, out state))
                {
                    state = new CandidateState();
                    _states[candidate.Key] = state;
                }
                state.Count++;
                if (!state.Fired && state.Count >= Math.Max(1, candidate.Threshold) && CooldownAllows(candidate.Key, frame.Frame))
                {
                    state.Fired = true;
                    _lastTriggerFrame[candidate.Key] = frame.Frame;
                    detections.Add(new Detection
                    {
                        Key = candidate.Key, Condition = candidate.Condition, Summary = candidate.Summary,
                        PersistenceFrames = state.Count, RecordIndex = candidate.RecordIndex,
                        Definitive = candidate.Definitive, Details = candidate.Details
                    });
                }
            }
            UpdateHistory(frame, table);
            return detections;
        }

        private IEnumerable<Candidate> BuildCandidates(FrameState frame, ObjectTable table, IEnumerable<AllocatorWitness> witnesses)
        {
            if (_options.ScannerContradictions && frame.ScannerRight < frame.ScannerLeft)
            {
                yield return Basic("scanner-window-inverted", "scanner_window_contradiction",
                    "The scanner's right window edge is below its left edge.", _options.ContradictionFrames, null, true,
                    new Dictionary<string, object> { { "left", Format.Hex(frame.ScannerLeft, 4) }, { "right", Format.Hex(frame.ScannerRight, 4) } });
            }

            if (_options.Type9Contradictions)
            {
                foreach (var candidate in Type9Candidates(frame, table)) yield return candidate;
            }

            foreach (var record in table.Records.Where(item => item.LogicCritical && item.Type != 9))
            {
                var eligible = ObjectWindow.IsEligible(frame, record);
                var bookmark = record.Index >= 0 && record.Index < frame.Bookkeeping.Length ? frame.Bookkeeping[record.Index] : (byte)0;
                var actor = frame.ActorForRecord(record.Index);
                var owned = frame.HasOwnedActor(record.Index);
                RecordHistory history;
                if (!_history.TryGetValue(record.Index, out history)) history = new RecordHistory();

                var persistentLifecycleActor = record.Category == "camera-object" || record.Category == "exit" || record.Category == "controller";
                if (_options.BookedActorMissing && eligible && !owned
                    && (IsActorBookmark(bookmark) || (persistentLifecycleActor && history.EverOwned)))
                {
                    yield return RecordCandidate(record, "booked_actor_missing",
                        "A logic-critical record remained eligible after its booked actor disappeared or ceased to own the bookmark.",
                        _options.OwnershipFrames, false, frame, actor,
                        new Dictionary<string, object>
                        {
                            { "previousFrameOwned", history.WasOwned }, { "ownedEarlierInContext", history.EverOwned },
                            { "persistentLifecycleCategory", persistentLifecycleActor }, { "bookmark", Format.Hex(bookmark, 2) }
                        });
                    continue;
                }

                if (_options.EligibleWithoutAllocation && eligible && bookmark == 0 && actor == null && !history.EverOwned)
                {
                    var secondary = record.Type == 14;
                    var free = frame.FreeIndices(secondary);
                    if (_options.AllocatorExhaustion && free.Length == 0)
                    {
                        yield return RecordCandidate(record, "allocator_exhaustion",
                            "A logic-critical eligible record has no booking or actor while its required actor pool is completely occupied.",
                            _options.EligibleFrames, true, frame, null,
                            new Dictionary<string, object> { { "pool", secondary ? "secondary" : "primary" }, { "freeCount", 0 } });
                    }
                    else
                    {
                        yield return RecordCandidate(record, "eligible_without_allocation",
                            "A logic-critical record is eligible for scanning but has neither a $192B booking nor a source-linked actor.",
                            _options.EligibleFrames, false, frame, null,
                            new Dictionary<string, object>
                            {
                                { "pool", secondary ? "secondary" : "primary" }, { "freeActorIndices", free.Select(index => (object)Format.Hex((uint)index, 2)).ToArray() },
                                { "enteredWindowUnderObservation", !history.WasEligible }
                            });
                    }
                }

                if (_options.MissingGroupChildren && record.Type == 5 && eligible && bookmark == 0xFF)
                {
                    foreach (var child in record.Children)
                    {
                        if (child.Index < 0 || child.Index >= frame.Bookkeeping.Length) continue;
                        var childBookmark = frame.Bookkeeping[child.Index];
                        if (childBookmark != 0 || frame.ActorForRecord(child.Index) != null) continue;
                        var secondary = child.Type == 12 || child.Type == 14;
                        var free = frame.FreeIndices(secondary);
                        yield return new Candidate
                        {
                            Key = "type5_child_missing:" + record.Index + ":" + child.Index,
                            Condition = free.Length == 0 ? "type5_child_allocator_exhaustion" : "type5_child_missing",
                            Summary = "An active, eligible type-5 parent persistently lacks a child booking and source-linked actor.",
                            Threshold = _options.GroupFrames, RecordIndex = child.Index, Definitive = free.Length == 0,
                            Details = new Dictionary<string, object>
                            {
                                { "parent", record.ToData(frame) }, { "child", child.ToData(frame) },
                                { "pool", secondary ? "secondary" : "primary" },
                                { "freeActorIndices", free.Select(index => (object)Format.Hex((uint)index, 2)).ToArray() }
                            }
                        };
                    }
                }
            }

            if (_options.ExactAllocatorWitness)
            {
                foreach (var witness in witnesses)
                {
                    var record = table.Find(witness.RecordIndex);
                    if (record == null || !record.LogicCritical || !ObjectWindow.IsEligible(frame, record)) continue;
                    yield return new Candidate
                    {
                        Key = "allocator_witness:" + witness.RecordIndex + ":" + (witness.Secondary ? "secondary" : "primary"),
                        Condition = "exact_allocator_exhaustion_witness",
                        Summary = "The clean-ROM allocator failure PC executed with every slot in the selected pool occupied for an eligible logic-critical record.",
                        Threshold = 1, RecordIndex = witness.RecordIndex, Definitive = true,
                        Details = new Dictionary<string, object> { { "record", record.ToData(frame) }, { "witness", witness.ToData() } }
                    };
                }
            }
        }

        private IEnumerable<Candidate> Type9Candidates(FrameState frame, ObjectTable table)
        {
            var hasController = table.Records.Any(record => record.Type == 9);
            if (!hasController && frame.SectionState != 0)
            {
                yield return Basic("type9-state-without-controller", "type9_range_contradiction",
                    "Section-controller WRAM is active but the entrance object list contains no type-9 controller.",
                    _options.ContradictionFrames, null, true, null);
                yield break;
            }
            if (!hasController || frame.SectionState == 0) yield break;
            if (table.SectionRanges.Count == 0)
            {
                yield return Basic("type9-no-ranges", "type9_range_contradiction",
                    "The entrance has a type-9 controller but its authored range table could not be decoded.",
                    _options.ContradictionFrames, null, true, null);
                yield break;
            }
            if (frame.SectionCurrent > frame.SectionLimit || frame.SectionLimit >= table.Records.Count)
            {
                yield return Basic("type9-current-order", "type9_range_contradiction",
                    "The active type-9 record range is inverted or extends beyond the decoded entrance list.",
                    _options.ContradictionFrames, null, true,
                    new Dictionary<string, object> { { "currentStart", frame.SectionCurrent }, { "currentEnd", frame.SectionLimit }, { "recordCount", table.Records.Count } });
            }
            if (!table.SectionRanges.Any(range => range.Matches(frame.SectionCurrent, frame.SectionLimit)))
            {
                yield return Basic("type9-current-not-authored", "type9_range_contradiction",
                    "The active type-9 start/end pair does not match either direction of any authored range descriptor.",
                    _options.ContradictionFrames, null, true,
                    new Dictionary<string, object> { { "currentStart", frame.SectionCurrent }, { "currentEnd", frame.SectionLimit } });
            }
            if (frame.ScannerPrimary < frame.SectionCurrent || frame.ScannerPrimary > frame.SectionLimit)
            {
                yield return Basic("type9-primary-cursor-outside", "type9_range_contradiction",
                    "The primary scanner cursor is outside the active type-9 range.",
                    _options.ContradictionFrames, null, false,
                    new Dictionary<string, object> { { "cursor", frame.ScannerPrimary }, { "currentStart", frame.SectionCurrent }, { "currentEnd", frame.SectionLimit } });
            }
            if (frame.SectionPointer == 0)
            {
                if (frame.ScannerSecondary != 0)
                    yield return Basic("type9-secondary-without-pointer", "type9_pending_contradiction",
                        "The secondary scanner cursor is nonzero while the pending type-9 descriptor pointer is zero.",
                        _options.ContradictionFrames, null, true,
                        new Dictionary<string, object> { { "secondaryCursor", frame.ScannerSecondary } });
                yield break;
            }
            var pending = table.SectionRanges.FirstOrDefault(range => (ushort)range.Address == frame.SectionPointer);
            if (pending == null)
            {
                yield return Basic("type9-pending-pointer-not-authored", "type9_pending_contradiction",
                    "The pending type-9 pointer does not name an authored range descriptor.",
                    _options.ContradictionFrames, null, true,
                    new Dictionary<string, object> { { "pointer", Format.Hex(frame.SectionPointer, 4) } });
                yield break;
            }
            if (!pending.Matches(frame.SectionPending, frame.SectionPendingLimit))
            {
                yield return Basic("type9-pending-range-mismatch", "type9_pending_contradiction",
                    "The pending type-9 start/end pair does not match the descriptor named by $1E05.",
                    _options.ContradictionFrames, null, true,
                    new Dictionary<string, object> { { "descriptor", pending.ToData() }, { "pendingStart", frame.SectionPending }, { "pendingEnd", frame.SectionPendingLimit } });
            }
            if (frame.ScannerSecondary != frame.SectionPending)
            {
                yield return Basic("type9-pending-cursor-mismatch", "type9_pending_contradiction",
                    "The secondary scanner cursor does not equal the pending type-9 range start.",
                    _options.ContradictionFrames, null, true,
                    new Dictionary<string, object> { { "secondaryCursor", frame.ScannerSecondary }, { "pendingStart", frame.SectionPending } });
            }
        }

        private static Candidate RecordCandidate(ObjectRecord record, string condition, string summary, int threshold,
            bool definitive, FrameState frame, ActorState actor, IDictionary<string, object> extra)
        {
            var details = new Dictionary<string, object>
            {
                { "record", record.ToData(frame) }, { "actorForRecord", actor == null ? null : actor.ToData() },
                { "eligible", ObjectWindow.IsEligible(frame, record) }
            };
            if (extra != null) foreach (var pair in extra) details[pair.Key] = pair.Value;
            return new Candidate
            {
                Key = condition + ":" + record.Index, Condition = condition, Summary = summary,
                Threshold = threshold, RecordIndex = record.Index, Definitive = definitive, Details = details
            };
        }

        private static Candidate Basic(string key, string condition, string summary, int threshold, int? record,
            bool definitive, IDictionary<string, object> details)
        {
            return new Candidate
            {
                Key = key, Condition = condition, Summary = summary, Threshold = threshold,
                RecordIndex = record, Definitive = definitive, Details = details ?? new Dictionary<string, object>()
            };
        }

        private void PrimeHistory(FrameState frame, ObjectTable table)
        {
            UpdateHistory(frame, table);
        }

        private void UpdateHistory(FrameState frame, ObjectTable table)
        {
            foreach (var record in table.Flatten())
            {
                RecordHistory history;
                if (!_history.TryGetValue(record.Index, out history))
                {
                    history = new RecordHistory();
                    _history[record.Index] = history;
                }
                history.WasEligible = ObjectWindow.IsEligible(frame, record.ParentIndex < 0 ? record : table.Records.FirstOrDefault(parent => parent.Index == record.ParentIndex));
                history.WasOwned = frame.HasOwnedActor(record.Index);
                history.EverOwned |= history.WasOwned;
            }
        }

        private bool CooldownAllows(string key, int frame)
        {
            int last;
            return !_lastTriggerFrame.TryGetValue(key, out last) || frame < last || frame - last >= Math.Max(0, _options.TriggerCooldownFrames);
        }

        private static bool IsActorBookmark(byte value)
        {
            return value >= DkcRam.FirstActorIndex && value <= DkcRam.LastActorIndex && (value & 1) == 0;
        }
    }

    internal sealed class OpcodeValidation
    {
        public bool Valid;
        public List<string> Mismatches = new List<string>();
        public IDictionary<string, object> ToData()
        {
            return new Dictionary<string, object> { { "valid", Valid }, { "mismatches", Mismatches.ToArray() } };
        }
    }

    internal static class OpcodeSignatures
    {
        private static readonly Dictionary<uint, byte[]> Signatures = new Dictionary<uint, byte[]>
        {
            { 0xBDF3A2, new byte[] { 0xA2,0x02,0x00,0xBD,0x45,0x0D,0xF0,0x0B,0xE8,0xE8,0xE0,0x1E,0x00,0xD0,0xF4 } },
            { 0xBDF3B1, new byte[] { 0x64,0x86,0x38,0x60 } },
            { 0xBDF3B5, new byte[] { 0x86,0x86,0xA9,0x00,0x80,0x9D,0xFD,0x15,0x18,0x60 } },
            { 0xBDF3C3, new byte[] { 0xA2,0x1E,0x00,0xBD,0x45,0x0D,0xF0,0x0B,0xE8,0xE8,0xE0,0x34,0x00,0xD0,0xF4 } },
            { 0xBDF3D2, new byte[] { 0x64,0x86,0x38,0x60 } },
            { 0xBDF3D6, new byte[] { 0x86,0x86,0xA9,0x00,0x80,0x9D,0xFD,0x15,0x18,0x60 } },
            { 0xBDFDBD, new byte[] { 0x4B,0xAB,0x9C,0x03,0x1E,0x9C,0x07,0x1E,0xA9,0xFF,0xFF,0x8D,0x0B,0x1E } },
            { 0xBDFF85, new byte[] { 0xAD,0x09,0x1E,0x8D,0x07,0x1E,0xAD,0x0D,0x1E,0x8D,0x0B,0x1E,0xA5,0xA2,0x85,0xA0 } },
            { 0xBDFF95, new byte[] { 0x64,0xA2,0x9C,0x05,0x1E,0x60 } }
        };

        public static OpcodeValidation Validate(ISnesMemoryReader memory)
        {
            var result = new OpcodeValidation();
            if (memory == null) { result.Mismatches.Add("SNES memory is unavailable."); return result; }
            foreach (var signature in Signatures)
            {
                for (var index = 0; index < signature.Value.Length; index++)
                {
                    var actual = memory.ReadByte((signature.Key + (uint)index) & 0xFFFFFF);
                    if (actual == signature.Value[index]) continue;
                    result.Mismatches.Add(Format.Hex(signature.Key + (uint)index, 6) + ": expected "
                        + Format.Hex(signature.Value[index], 2) + ", got " + Format.Hex(actual, 2));
                    break;
                }
            }
            result.Valid = result.Mismatches.Count == 0;
            return result;
        }

        public static bool IsExhaustionPc(uint pc, out bool secondary)
        {
            pc &= 0xFFFFFF;
            secondary = pc == 0xBDF3D2;
            return pc == 0xBDF3B1 || secondary;
        }
    }
}
