using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace DKCSoftlockWatchdog
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class DKCSoftlockWatchdogPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.dkcsoftlockwatchdog";
        public const string PluginName = "DKC Softlock Watchdog";
        public const string PluginVersion = "0.1.0";
        internal static DKCSoftlockWatchdogPlugin Instance;

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<bool> _armedAtStartup;
        private ConfigEntry<bool> _instructionWitnessAtStartup;
        private ConfigEntry<bool> _pauseOnTriggerAtStartup;
        private ConfigEntry<bool> _externalCapturesAtStartup;
        private ConfigEntry<string> _externalCaptureTargets;
        private ConfigEntry<int> _statusInterval;
        private Harmony _harmony;
        private MethodBase _frameMethod;
        private MethodBase _cpuMethod;
        private bool _framePatched;
        private bool _cpuPatched;
        private bool _armed;
        private bool _instructionWitness;
        private bool _pauseOnTrigger;
        private bool _externalCaptures;
        private object _master;
        private object _memory;
        private ushort _decodedEntrance = 0xFFFF;
        private ObjectTable _table = new ObjectTable();
        private OpcodeValidation _opcodeValidation;
        private WatchdogDetector _detector;
        private EvidenceWriter _output;
        private FrameState _latestFrame;
        private int _lastStatusFrame = int.MinValue;
        private readonly object _queueGate = new object();
        private readonly Queue<PendingCapture> _captures = new Queue<PendingCapture>();
        private readonly List<AllocatorWitness> _witnesses = new List<AllocatorWitness>();

        private string CommandRoot { get { return Path.Combine(Paths.PluginPath, "DKCSoftlockWatchdog"); } }
        private string StatusPath { get { return Path.Combine(CommandRoot, "status.json"); } }

        private void Awake()
        {
            Instance = this;
            _enabled = Config.Bind("General", "Enabled", true, "Sample DKC object/actor state once per emulated frame.");
            _armedAtStartup = Config.Bind("General", "ArmedAtStartup", true, "Evaluate configured watch conditions after the first gameplay baseline frame.");
            _instructionWitnessAtStartup = Config.Bind("Witness", "AllocatorInstructionWitnessAtStartup", false,
                "Install a high-cost per-65C816-instruction prefix to witness exact $BDF3B1/$BDF3D2 allocator failures. Off by default.");
            _pauseOnTriggerAtStartup = Config.Bind("Actions", "PauseOnTriggerAtStartup", false,
                "Request PauseGame from Unity Update after evidence commits. This is the only gameplay-affecting action and is off by default.");
            _externalCapturesAtStartup = Config.Bind("Actions", "RequestExternalCapturesAtStartup", false,
                "Create capture.request in configured plugin directories after evidence commits. Off by default.");
            _externalCaptureTargets = Config.Bind("Actions", "ExternalCaptureTargets", string.Empty,
                "Semicolon-separated absolute paths or names below BepInEx/plugins that consume capture.request.");
            _statusInterval = Config.Bind("Output", "StatusIntervalFrames", 30, "Atomic status.json rewrite interval in emulated frames.");

            var options = new DetectionOptions
            {
                EligibleFrames = Config.Bind("Detection", "EligibleWithoutAllocationFrames", 12, "Consecutive gameplay frames before an eligible unallocated critical record triggers.").Value,
                OwnershipFrames = Config.Bind("Detection", "BookedActorMissingFrames", 4, "Consecutive frames before a missing booked actor triggers.").Value,
                GroupFrames = Config.Bind("Detection", "MissingGroupChildFrames", 8, "Consecutive frames before an active eligible type-5 parent with a missing child triggers.").Value,
                ContradictionFrames = Config.Bind("Detection", "ContradictionFrames", 3, "Consecutive frames required for scanner/type-9 structural contradictions.").Value,
                TriggerCooldownFrames = Config.Bind("Detection", "TriggerCooldownFrames", 300, "Minimum frames before the same recovered/reappearing condition can capture again.").Value,
                EligibleWithoutAllocation = Config.Bind("Conditions", "EligibleWithoutAllocation", true, "Capture eligible logic-critical records with no bookmark or source-linked actor.").Value,
                BookedActorMissing = Config.Bind("Conditions", "BookedActorMissing", true, "Capture persistent broken bookmark/actor ownership for logic-critical records.").Value,
                MissingGroupChildren = Config.Bind("Conditions", "MissingType5Children", true, "Capture active eligible type-5 roots with missing children.").Value,
                Type9Contradictions = Config.Bind("Conditions", "Type9Contradictions", true, "Capture authored/current/pending type-9 range contradictions.").Value,
                ScannerContradictions = Config.Bind("Conditions", "ScannerWindowContradictions", true, "Capture inverted scanner windows.").Value,
                AllocatorExhaustion = Config.Bind("Conditions", "AllocatorExhaustion", true, "Classify zero-free-slot critical allocation failures as allocator exhaustion.").Value,
                ExactAllocatorWitness = Config.Bind("Conditions", "ExactAllocatorWitness", true, "Capture verified allocator failure-PC witnesses when the optional instruction hook is enabled.").Value
            };
            _detector = new WatchdogDetector(options);
            _armed = _armedAtStartup.Value;
            _instructionWitness = _instructionWitnessAtStartup.Value;
            _pauseOnTrigger = _pauseOnTriggerAtStartup.Value;
            _externalCaptures = _externalCapturesAtStartup.Value;

            Directory.CreateDirectory(CommandRoot);
            var sessionRoot = Path.Combine(CommandRoot, "Sessions", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
            _output = new EvidenceWriter(sessionRoot);
            _output.Event(new Dictionary<string, object>
            {
                { "type", "session_start" }, { "utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
                { "plugin", PluginName }, { "version", PluginVersion }, { "armed", _armed },
                { "instructionWitness", _instructionWitness }, { "pauseOnTrigger", _pauseOnTrigger },
                { "externalCaptures", _externalCaptures }, { "observationOnly", !_pauseOnTrigger }
            });
            _harmony = new Harmony(PluginGuid);
            ResolveMethods();
            SyncPatches();
            WriteStatus("startup", null);
            Logger.LogInfo(PluginName + " " + PluginVersion + " writing to " + sessionRoot + ". Observation-only actions are the default.");
        }

        private void OnDestroy()
        {
            try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
            try { if (_output != null) _output.Dispose(); } catch { }
            if (ReferenceEquals(Instance, this)) Instance = null;
        }

        private void Update()
        {
            if (!_enabled.Value) return;
            var current = Reflect.Static("MasterExecutor", "Instance");
            if (current != null && !ReferenceEquals(current, _master))
            {
                _master = current;
                _memory = Reflect.Get(_master, "CoreMemoryMap");
                ResetContext("master-attached");
            }
            ResolveMethods();
            SyncPatches();
            ProcessFileCommands();
            DrainCaptures();
            if (_latestFrame != null && (_lastStatusFrame == int.MinValue || _latestFrame.Frame < _lastStatusFrame
                || _latestFrame.Frame - _lastStatusFrame >= Math.Max(1, _statusInterval.Value)))
                WriteStatus("periodic", null);
        }

        private void ResolveMethods()
        {
            var masterType = Reflect.Type("MasterExecutor");
            var cpuType = Reflect.Type("CPU65c816");
            _frameMethod = masterType == null ? null : AccessTools.Method(masterType, "RunFrame", Type.EmptyTypes);
            _cpuMethod = cpuType == null ? null : AccessTools.Method(cpuType, "ExecuteNextInstruction", Type.EmptyTypes);
        }

        private void SyncPatches()
        {
            if (_harmony == null) return;
            if (!_framePatched && _frameMethod != null)
            {
                _harmony.Patch(_frameMethod, postfix: new HarmonyMethod(AccessTools.Method(typeof(WatchdogHooks), nameof(WatchdogHooks.RunFramePostfix))));
                _framePatched = true;
            }
            var wantCpu = _instructionWitness && _opcodeValidation != null && _opcodeValidation.Valid;
            if (_cpuMethod != null && wantCpu != _cpuPatched)
            {
                if (wantCpu) _harmony.Patch(_cpuMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(WatchdogHooks), nameof(WatchdogHooks.CpuInstructionPrefix))));
                else _harmony.Unpatch(_cpuMethod, HarmonyPatchType.Prefix, PluginGuid);
                _cpuPatched = wantCpu;
            }
        }

        internal void OnFrame(object master)
        {
            if (!_enabled.Value) return;
            _master = master ?? _master;
            _memory = Reflect.Get(_master, "CoreMemoryMap") ?? _memory;
            var ram = Reflect.TryCall(_memory, "GetRam") as byte[];
            if (ram == null || ram.Length != DkcRam.WramSize) return;
            FrameState frame;
            try { frame = FrameState.FromRam(ram, Reflect.IntCall(_master, "GetFrameNo", -1)); }
            catch (Exception ex) { Logger.LogWarning("Could not sample DKC WRAM: " + ex.Message); return; }

            if (_latestFrame != null && frame.Frame <= _latestFrame.Frame) ResetContext("frame-rewind-or-state-load");
            EnsureObjectTable(frame);
            if (_opcodeValidation == null && _memory != null)
            {
                try { _opcodeValidation = OpcodeSignatures.Validate(new ReflectionSnesReader(_memory)); }
                catch (Exception ex) { _opcodeValidation = new OpcodeValidation { Valid = false, Mismatches = new List<string> { ex.Message } }; }
                if (!_opcodeValidation.Valid)
                    Logger.LogWarning("Allocator/type-9 opcode validation failed; exact instruction witnesses remain disabled. Frame watchpoints continue.");
            }

            var witnesses = ConsumeWitnesses(frame.Frame);
            _latestFrame = frame;
            if (_armed)
            {
                var detections = _detector.Evaluate(frame, _table, witnesses);
                if (detections.Count != 0)
                {
                    var evidence = BuildEvidence(frame, detections, "watchdog-trigger");
                    Enqueue(new PendingCapture
                    {
                        Wram = (byte[])ram.Clone(), Evidence = evidence,
                        Slug = "f" + frame.Frame.ToString("D8", CultureInfo.InvariantCulture) + "-" + detections[0].Condition,
                        RequestPause = _pauseOnTrigger, RequestExternalCaptures = _externalCaptures
                    });
                    WriteStatus("trigger-queued", detections.Select(item => item.Condition).ToArray());
                }
            }
            else _detector.Reset();
        }

        internal void OnCpuInstruction(object cpu)
        {
            if (!_armed || !_instructionWitness || _opcodeValidation == null || !_opcodeValidation.Valid || cpu == null || _memory == null) return;
            bool secondary;
            var pc = Reflect.UIntCall(cpu, "GetPCAddress", 0) & 0xFFFFFF;
            if (!OpcodeSignatures.IsExhaustionPc(pc, out secondary)) return;
            var ram = Reflect.TryCall(_memory, "GetRam") as byte[];
            if (ram == null || ram.Length != DkcRam.WramSize) return;
            var first = secondary ? DkcRam.SecondaryFirst : DkcRam.PrimaryFirst;
            var last = secondary ? DkcRam.SecondaryLast : DkcRam.PrimaryLast;
            var occupied = new List<int>();
            for (var index = first; index <= last; index += 2)
            {
                if (DkcRam.U16(ram, DkcRam.ActorId + index) == 0) return;
                occupied.Add(index);
            }
            var witness = new AllocatorWitness
            {
                Frame = Reflect.IntCall(_master, "GetFrameNo", -1), RecordIndex = ram[DkcRam.ScannerRecordIndex],
                Secondary = secondary, Pc = pc, OccupiedIndices = occupied.ToArray()
            };
            lock (_queueGate)
            {
                _witnesses.Add(witness);
                if (_witnesses.Count > 64) _witnesses.RemoveRange(0, _witnesses.Count - 64);
            }
        }

        private void EnsureObjectTable(FrameState frame)
        {
            if (!frame.GameplayActive)
            {
                _decodedEntrance = 0xFFFF;
                _table = new ObjectTable { Error = "Object decoding suppressed: " + frame.GameplayReason };
                return;
            }
            if (_memory == null || _decodedEntrance == frame.EntranceId) return;
            _table = ObjectTableDecoder.Decode(new ReflectionSnesReader(_memory), frame.EntranceId);
            _decodedEntrance = frame.EntranceId;
            _detector.Reset();
        }

        private List<AllocatorWitness> ConsumeWitnesses(int frame)
        {
            lock (_queueGate)
            {
                var result = _witnesses.Where(item => Math.Abs(item.Frame - frame) <= 1).ToList();
                _witnesses.RemoveAll(item => item.Frame <= frame);
                return result;
            }
        }

        private IDictionary<string, object> BuildEvidence(FrameState frame, IEnumerable<Detection> detections, string reason)
        {
            var primaryFree = frame.FreeIndices(false);
            var secondaryFree = frame.FreeIndices(true);
            var data = new Dictionary<string, object>(frame.ContextData())
            {
                { "type", "dkc_softlock_watchdog_evidence" }, { "reason", reason },
                { "utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
                { "plugin", PluginName }, { "pluginVersion", PluginVersion },
                { "observationOnlyAtTrigger", !_pauseOnTrigger },
                { "actions", new Dictionary<string, object> { { "pauseRequested", _pauseOnTrigger }, { "externalCapturesRequested", _externalCaptures } } },
                { "opcodeValidation", _opcodeValidation == null ? null : _opcodeValidation.ToData() },
                { "objectTable", new Dictionary<string, object>
                    {
                        { "baseAddress", Format.Hex(_table.BaseAddress, 6) }, { "decodeError", _table.Error },
                        { "records", _table.Records.Select(record => (object)record.ToData(frame)).ToArray() },
                        { "type9Ranges", _table.SectionRanges.Select(range => (object)range.ToData()).ToArray() }
                    }
                },
                { "actors", frame.Actors.Select(actor => (object)actor.ToData()).ToArray() },
                { "bookkeeping", frame.Bookkeeping.Select(value => (object)Format.Hex(value, 2)).ToArray() },
                { "allocator", new Dictionary<string, object>
                    {
                        { "primaryRange", "$02-$1C" }, { "primaryFree", primaryFree.Select(index => (object)Format.Hex((uint)index, 2)).ToArray() },
                        { "primaryExhausted", primaryFree.Length == 0 }, { "secondaryRange", "$1E-$32" },
                        { "secondaryFree", secondaryFree.Select(index => (object)Format.Hex((uint)index, 2)).ToArray() },
                        { "secondaryExhausted", secondaryFree.Length == 0 }
                    }
                },
                { "detections", (detections ?? Array.Empty<Detection>()).Select(detection => (object)new Dictionary<string, object>
                    {
                        { "key", detection.Key }, { "condition", detection.Condition }, { "summary", detection.Summary },
                        { "persistenceFrames", detection.PersistenceFrames }, { "recordIndex", detection.RecordIndex },
                        { "definitive", detection.Definitive }, { "details", detection.Details }
                    }).ToArray()
                }
            };
            return data;
        }

        private void Enqueue(PendingCapture capture)
        {
            lock (_queueGate)
            {
                if (_captures.Count >= 32) _captures.Dequeue();
                _captures.Enqueue(capture);
            }
        }

        private void DrainCaptures()
        {
            for (var count = 0; count < 4; count++)
            {
                PendingCapture capture;
                lock (_queueGate)
                {
                    if (_captures.Count == 0) break;
                    capture = _captures.Dequeue();
                }
                try
                {
                    var path = _output.Capture(capture);
                    var actionResults = new List<object>();
                    if (capture.RequestExternalCaptures) actionResults.AddRange(RequestExternalCaptures(capture.Slug));
                    if (capture.RequestPause)
                    {
                        var paused = TryPauseOnMainThread();
                        actionResults.Add(new Dictionary<string, object> { { "action", "pause" }, { "requested", true }, { "succeeded", paused } });
                    }
                    _output.Event(new Dictionary<string, object>
                    {
                        { "type", "trigger_actions_complete" }, { "utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
                        { "capturePath", path }, { "actions", actionResults.ToArray() }
                    });
                    WriteStatus("capture-committed", new[] { path });
                    Logger.LogWarning("DKC softlock watchpoint captured evidence: " + path);
                }
                catch (Exception ex)
                {
                    Logger.LogError("Could not commit watchdog evidence: " + ex);
                    WriteStatus("capture-error", new[] { ex.Message });
                }
            }
        }

        private bool TryPauseOnMainThread()
        {
            if (_master == null) return false;
            try { Reflect.Call(_master, "PauseGame"); return true; }
            catch (Exception ex) { Logger.LogWarning("Pause-on-trigger request failed: " + ex.Message); return false; }
        }

        private IEnumerable<object> RequestExternalCaptures(string reason)
        {
            var results = new List<object>();
            foreach (var item in (_externalCaptureTargets.Value ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var target = item.Trim();
                if (target.Length == 0) continue;
                if (!Path.IsPathRooted(target)) target = Path.Combine(Paths.PluginPath, target);
                try
                {
                    var request = Path.Combine(target, "capture.request");
                    var created = AtomicFile.TryCreateText(request, "DKCSoftlockWatchdog " + reason + " " + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    results.Add(new Dictionary<string, object> { { "action", "capture.request" }, { "target", request }, { "created", created } });
                }
                catch (Exception ex)
                {
                    results.Add(new Dictionary<string, object> { { "action", "capture.request" }, { "target", target }, { "created", false }, { "error", ex.Message } });
                }
            }
            return results;
        }

        private void ProcessFileCommands()
        {
            Directory.CreateDirectory(CommandRoot);
            if (Consume("arm.request")) { _armed = true; _detector.Reset(); WriteStatus("armed", null); }
            if (Consume("disarm.request")) { _armed = false; _detector.Reset(); WriteStatus("disarmed", null); }
            if (Consume("reset.request")) { ResetContext("file-command"); WriteStatus("reset", null); }
            if (Consume("instruction-witness-on.request")) { _instructionWitness = true; SyncPatches(); WriteStatus("instruction-witness-on", null); }
            if (Consume("instruction-witness-off.request")) { _instructionWitness = false; SyncPatches(); WriteStatus("instruction-witness-off", null); }
            if (Consume("pause-on-trigger-on.request")) { _pauseOnTrigger = true; WriteStatus("pause-on-trigger-on", null); }
            if (Consume("pause-on-trigger-off.request")) { _pauseOnTrigger = false; WriteStatus("pause-on-trigger-off", null); }
            if (Consume("external-captures-on.request")) { _externalCaptures = true; WriteStatus("external-captures-on", null); }
            if (Consume("external-captures-off.request")) { _externalCaptures = false; WriteStatus("external-captures-off", null); }
            var manual = Path.Combine(CommandRoot, "capture.request");
            if (!File.Exists(manual)) return;
            var reason = "manual-file-request";
            try { var text = File.ReadAllText(manual).Trim(); if (text.Length != 0) reason = text; } catch { }
            try { File.Delete(manual); } catch { }
            var ram = Reflect.TryCall(_memory, "GetRam") as byte[];
            if (_latestFrame == null || ram == null || ram.Length != DkcRam.WramSize)
            {
                WriteStatus("manual-capture-unavailable", null);
                return;
            }
            Enqueue(new PendingCapture
            {
                Wram = (byte[])ram.Clone(), Evidence = BuildEvidence(_latestFrame, Array.Empty<Detection>(), reason),
                Slug = "manual-" + reason, RequestPause = false, RequestExternalCaptures = false
            });
            WriteStatus("manual-capture-queued", null);
        }

        private bool Consume(string name)
        {
            var path = Path.Combine(CommandRoot, name);
            if (!File.Exists(path)) return false;
            try { File.Delete(path); } catch { }
            return true;
        }

        private void ResetContext(string reason)
        {
            _detector.Reset();
            _latestFrame = null;
            _decodedEntrance = 0xFFFF;
            _table = new ObjectTable { Error = "Context reset: " + reason };
            _opcodeValidation = null;
            lock (_queueGate) _witnesses.Clear();
            if (_output != null) _output.Event(new Dictionary<string, object>
            {
                { "type", "context_reset" }, { "reason", reason }, { "utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) }
            });
        }

        private void WriteStatus(string message, object details)
        {
            if (_output == null) return;
            var data = new Dictionary<string, object>
            {
                { "utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) }, { "message", message }, { "details", details },
                { "plugin", PluginName }, { "version", PluginVersion }, { "enabled", _enabled == null || _enabled.Value },
                { "armed", _armed }, { "instructionWitnessRequested", _instructionWitness }, { "instructionHookInstalled", _cpuPatched },
                { "pauseOnTrigger", _pauseOnTrigger }, { "externalCapturesOnTrigger", _externalCaptures },
                { "observationOnly", !_pauseOnTrigger }, { "activeCandidates", _detector == null ? 0 : _detector.ActiveCandidateCount },
                { "sessionRoot", _output.SessionRoot }, { "opcodeValidation", _opcodeValidation == null ? null : _opcodeValidation.ToData() },
                { "frame", _latestFrame == null ? -1 : _latestFrame.Frame }, { "gameplayActive", _latestFrame != null && _latestFrame.GameplayActive },
                { "levelId", _latestFrame == null ? null : Format.Hex(_latestFrame.LevelId, 4) },
                { "entranceId", _latestFrame == null ? null : Format.Hex(_latestFrame.EntranceId, 4) },
                { "objectDecodeError", _table == null ? null : _table.Error }
            };
            _output.Status(StatusPath, data);
            if (_latestFrame != null) _lastStatusFrame = _latestFrame.Frame;
        }
    }

    internal static class WatchdogHooks
    {
        public static void RunFramePostfix(object __instance)
        {
            var plugin = DKCSoftlockWatchdogPlugin.Instance;
            if (plugin != null) plugin.OnFrame(__instance);
        }

        public static void CpuInstructionPrefix(object __instance)
        {
            var plugin = DKCSoftlockWatchdogPlugin.Instance;
            if (plugin != null) plugin.OnCpuInstruction(__instance);
        }
    }
}
