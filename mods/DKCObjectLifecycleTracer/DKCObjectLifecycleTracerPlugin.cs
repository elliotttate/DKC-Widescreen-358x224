using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace DKCObjectLifecycleTracer
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class DKCObjectLifecycleTracerPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.dkcobjectlifecycletracer";
        public const string PluginName = "DKC Object Lifecycle Tracer";
        public const string PluginVersion = "0.2.1";
        internal static DKCObjectLifecycleTracerPlugin Instance;

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<bool> _writeTraceAtStartup;
        private ConfigEntry<bool> _scannerTraceAtStartup;
        private ConfigEntry<bool> _includePositionWrites;
        private ConfigEntry<int> _currentInterval;
        private ConfigEntry<int> _maxScannerEventsPerFrame;
        private ConfigEntry<int> _observationPersistenceFrames;
        private Harmony _harmony;
        private TraceOutput _output;
        private object _master;
        private object _memory;
        private MethodBase _frameMethod;
        private MethodBase _writeMethod;
        private MethodBase _cpuMethod;
        private bool _framePatched;
        private bool _writePatched;
        private bool _cpuPatched;
        private bool _writeTrace;
        private bool _scannerTrace;
        private int _scannerEventsThisFrame;
        private int _lastCurrentFrame = int.MinValue;
        private DkcFrameSnapshot _previous;
        private List<ObjectRecord> _objects = new List<ObjectRecord>();
        private string _objectDecodeError;
        private ushort _decodedEntrance = 0xFFFF;
        private readonly Dictionary<int, string> _lastWriters = new Dictionary<int, string>();
        private readonly HashSet<string> _lastAnomalies = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _observationCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> _lastPersistentObservations = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _scannerKeysThisFrame = new HashSet<string>(StringComparer.Ordinal);

        private void Awake()
        {
            Instance = this;
            _enabled = Config.Bind("General", "Enabled", true, "Collect frame-level DKC actor and object-bookkeeping diagnostics.");
            _writeTraceAtStartup = Config.Bind("Tracing", "RelevantMemoryWritesAtStartup", true, "Trace writes to actor identity/source, object bookkeeping, and section-controller state.");
            _scannerTraceAtStartup = Config.Bind("Tracing", "BankBDScannerTraceAtStartup", false, "Install the expensive per-instruction hook and record semantic bank-BD scanner decisions.");
            _includePositionWrites = Config.Bind("Tracing", "IncludeActorPositionWrites", false, "Also write high-volume actor X/Y/state memory writes to writes.jsonl.");
            _currentInterval = Config.Bind("Output", "CurrentSnapshotIntervalFrames", 30, "Rewrite current.json at this interval and immediately on lifecycle changes.");
            _maxScannerEventsPerFrame = Config.Bind("Output", "MaxScannerEventsPerFrame", 512, "Safety cap for semantic scanner events in one emulated frame.");
            _observationPersistenceFrames = Config.Bind("Output", "ObservationPersistenceFrames", 3, "Require this many consecutive gameplay frames before emitting a non-definitive ownership observation.");
            _writeTrace = _writeTraceAtStartup.Value;
            _scannerTrace = _scannerTraceAtStartup.Value;

            var root = Path.Combine(Paths.PluginPath, "DKCObjectLifecycleTracer", "Sessions", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            _output = new TraceOutput(root);
            _output.Event(new Dictionary<string, object>
            {
                { "type", "session_start" }, { "utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
                { "plugin", PluginName }, { "version", PluginVersion }, { "writeTrace", _writeTrace },
                { "scannerTrace", _scannerTrace }, { "requestDirectory", CommandRoot }
            });
            _harmony = new Harmony(PluginGuid);
            ResolveMethods();
            SyncPatches();
            Logger.LogInfo(PluginName + " " + PluginVersion + " writing to " + root);
        }

        private string CommandRoot { get { return Path.Combine(Paths.PluginPath, "DKCObjectLifecycleTracer"); } }

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
            if (!_framePatched || (_writeTrace && !_writePatched) || (_scannerTrace && !_cpuPatched))
            {
                ResolveMethods();
                SyncPatches();
            }
            ProcessFileCommands();
        }

        private void ResolveMethods()
        {
            var masterType = Reflect.Type("MasterExecutor");
            var memoryType = Reflect.Type("MainMemoryMap");
            var cpuType = Reflect.Type("CPU65c816");
            _frameMethod = masterType == null ? null : AccessTools.Method(masterType, "RunFrame", Type.EmptyTypes);
            _writeMethod = memoryType == null ? null : AccessTools.Method(memoryType, "WriteMem", new[] { typeof(uint), typeof(byte) });
            _cpuMethod = cpuType == null ? null : AccessTools.Method(cpuType, "ExecuteNextInstruction", Type.EmptyTypes);
        }

        private void SyncPatches()
        {
            if (_harmony == null) return;
            if (!_framePatched && _frameMethod != null)
            {
                _harmony.Patch(_frameMethod, postfix: new HarmonyMethod(AccessTools.Method(typeof(TracerHooks), nameof(TracerHooks.RunFramePostfix))));
                _framePatched = true;
            }
            SetPrefix(_writeMethod, ref _writePatched, _writeTrace, nameof(TracerHooks.WriteMemPrefix));
            SetPrefix(_cpuMethod, ref _cpuPatched, _scannerTrace, nameof(TracerHooks.CpuInstructionPrefix));
        }

        private void SetPrefix(MethodBase method, ref bool applied, bool wanted, string hook)
        {
            if (method == null || applied == wanted) return;
            if (wanted) _harmony.Patch(method, prefix: new HarmonyMethod(AccessTools.Method(typeof(TracerHooks), hook)));
            else _harmony.Unpatch(method, HarmonyPatchType.Prefix, PluginGuid);
            applied = wanted;
        }

        internal void OnFrame(object master)
        {
            if (!_enabled.Value) return;
            _master = master ?? _master;
            _memory = Reflect.Get(_master, "CoreMemoryMap") ?? _memory;
            var ram = Reflect.TryCall(_memory, "GetRam") as byte[];
            if (ram == null) return;
            var frameNumber = Reflect.IntCall(_master, "GetFrameNo", -1);
            DkcFrameSnapshot frame;
            try { frame = DkcFrameSnapshot.FromRam(ram, frameNumber); }
            catch (Exception ex) { Logger.LogWarning("Could not sample DKC WRAM: " + ex.Message); return; }

            _scannerEventsThisFrame = 0;
            _scannerKeysThisFrame.Clear();
            if (_previous != null && frame.Frame <= _previous.Frame) ResetContext("frame-rewind-or-state-load");
            var contextChanged = _previous == null || _previous.ObjectTracingActive != frame.ObjectTracingActive;
            EnsureObjectTable(frame);
            var changes = LifecycleAnalyzer.Diff(_previous, frame, LastWriter);
            foreach (var change in changes) _output.Event(change);
            var anomalies = LifecycleAnalyzer.FindAnomalies(frame, _objects);
            var observations = LifecycleAnalyzer.FindObservations(frame, _objects);
            foreach (var anomaly in anomalies)
            {
                if (_lastAnomalies.Add(anomaly))
                    _output.Event(new Dictionary<string, object> { { "type", "anomaly_started" }, { "frame", frame.Frame }, { "message", anomaly }, { "level", DkcNames.Level(frame.LevelId) } });
            }
            foreach (var ended in _lastAnomalies.Where(old => !anomalies.Contains(old)).ToArray())
            {
                _lastAnomalies.Remove(ended);
                _output.Event(new Dictionary<string, object> { { "type", "anomaly_ended" }, { "frame", frame.Frame }, { "message", ended } });
            }
            UpdatePersistentObservations(frame, observations);
            if (contextChanged)
            {
                _output.Event(new Dictionary<string, object>
                {
                    { "type", "object_tracing_context" }, { "frame", frame.Frame },
                    { "active", frame.ObjectTracingActive }, { "reason", frame.ObjectTracingReason },
                    { "entranceId", ActorSnapshot.Hex(frame.EntranceId, 4) },
                    { "lowerBound", ActorSnapshot.Hex(frame.LowerBound, 4) }, { "upperBound", ActorSnapshot.Hex(frame.UpperBound, 4) }
                });
            }
            if (_previous == null || _previous.LevelId != frame.LevelId || _previous.EntranceId != frame.EntranceId)
            {
                _output.Event(new Dictionary<string, object>
                {
                    { "type", "level_context" }, { "frame", frame.Frame }, { "levelId", ActorSnapshot.Hex(frame.LevelId, 4) },
                    { "level", DkcNames.Level(frame.LevelId) }, { "entranceId", ActorSnapshot.Hex(frame.EntranceId, 4) },
                    { "objectCount", _objects.Count }, { "decodeError", _objectDecodeError }
                });
            }
            var interval = Math.Max(1, _currentInterval.Value);
            if (changes.Count != 0 || anomalies.Count != 0 || observations.Count != 0 || contextChanged || frame.Frame - _lastCurrentFrame >= interval || _previous == null)
            {
                _output.Current(BuildCapture(frame, anomalies, observations, false, "current"));
                _lastCurrentFrame = frame.Frame;
            }
            _previous = frame;
        }

        private void EnsureObjectTable(DkcFrameSnapshot frame)
        {
            if (!frame.ObjectTracingActive)
            {
                _objects.Clear();
                _decodedEntrance = 0xFFFF;
                _objectDecodeError = "Object decoding suppressed: " + frame.ObjectTracingReason + ".";
                return;
            }
            if (_memory == null || _decodedEntrance == frame.EntranceId) return;
            _objects = ObjectTableDecoder.Decode(new ReflectionSnesReader(_memory), frame.EntranceId, out _objectDecodeError);
            _decodedEntrance = frame.EntranceId;
        }

        internal void OnMemoryWrite(uint address, byte value)
        {
            if (!_enabled.Value || !_writeTrace || _master == null) return;
            var offset = DkcRam.NormalizeWramAddress(address);
            if (offset < 0 || !DkcRam.IsInterestingWrite(offset)) return;
            var ram = Reflect.TryCall(_memory, "GetRam") as byte[];
            string tracingReason;
            if (!DkcFrameSnapshot.IsObjectTracingActive(ram, out tracingReason)) return;
            var before = ram != null && offset < ram.Length ? ram[offset] : (byte)0;
            if (before == value) return;
            var cpu = Reflect.Get(_master, "CPUCore65c816");
            var pc = Reflect.UIntCall(cpu, "GetPCAddress", 0) & 0xFFFFFF;
            var meaning = ScannerSemantics.Describe(pc);
            var writer = ActorSnapshot.Hex(pc, 6) + (meaning == null ? string.Empty : " " + meaning);
            _lastWriters[offset] = writer;

            if (!ShouldLogWrite(offset)) return;
            var data = new Dictionary<string, object>
            {
                { "type", "memory_write" }, { "frame", Reflect.IntCall(_master, "GetFrameNo", -1) },
                { "line", Reflect.IntCall(_master, "GetLineNo", -1) }, { "dot", Reflect.IntCall(_master, "GetPixelNo", -1) },
                { "pc", ActorSnapshot.Hex(pc, 6) }, { "pcMeaning", meaning }, { "address", ActorSnapshot.Hex(address & 0xFFFFFF, 6) },
                { "wramOffset", ActorSnapshot.Hex((uint)offset, 5) }, { "field", DescribeWram(offset) },
                { "before", ActorSnapshot.Hex(before, 2) }, { "after", ActorSnapshot.Hex(value, 2) }
            };
            AddContext(data, ram);
            _output.Write(data);
        }

        private bool ShouldLogWrite(int offset)
        {
            if (_includePositionWrites.Value) return true;
            return DkcRam.InActorTable(offset, DkcRam.ActorId) || DkcRam.InActorTable(offset, DkcRam.ActorSourceRecord)
                || (offset >= DkcRam.Bookkeeping && offset < DkcRam.Bookkeeping + DkcRam.BookkeepingLength)
                || (offset >= DkcRam.SectionControllerState && offset <= DkcRam.SectionControllerLimit + 1);
        }

        internal void OnCpuInstruction(object cpu)
        {
            if (!_enabled.Value || !_scannerTrace || cpu == null) return;
            var pc = Reflect.UIntCall(cpu, "GetPCAddress", 0) & 0xFFFFFF;
            var meaning = ScannerSemantics.Describe(pc);
            if (meaning == null || _scannerEventsThisFrame >= Math.Max(1, _maxScannerEventsPerFrame.Value)) return;
            var ram = Reflect.TryCall(_memory, "GetRam") as byte[];
            if (ram == null) return;
            string tracingReason;
            if (!DkcFrameSnapshot.IsObjectTracingActive(ram, out tracingReason)) return;
            var frame = Reflect.IntCall(_master, "GetFrameNo", -1);
            var key = frame.ToString(CultureInfo.InvariantCulture) + ":" + pc.ToString("X6") + ":" + ram[DkcRam.ScannerRecordIndex].ToString("X2");
            if (!_scannerKeysThisFrame.Add(key)) return;
            _scannerEventsThisFrame++;
            var state = Reflect.TryCall(cpu, "GetSaveState");
            var data = new Dictionary<string, object>
            {
                { "type", "scanner_decision" }, { "frame", frame }, { "line", Reflect.IntCall(_master, "GetLineNo", -1) },
                { "dot", Reflect.IntCall(_master, "GetPixelNo", -1) }, { "pc", ActorSnapshot.Hex(pc, 6) }, { "decision", meaning },
                { "a", Register(state, "regA") }, { "x", Register(state, "regX") }, { "y", Register(state, "regY") },
                { "flags", Flags(state) }
            };
            AddContext(data, ram);
            _output.Scanner(data);
        }

        private void AddContext(IDictionary<string, object> data, byte[] ram)
        {
            if (ram == null || ram.Length < 0x1F00) return;
            data["levelId"] = ActorSnapshot.Hex(U16(ram, DkcRam.LevelId), 4);
            data["entranceId"] = ActorSnapshot.Hex(U16(ram, DkcRam.EntranceId), 4);
            data["gameState"] = ActorSnapshot.Hex(U16(ram, DkcRam.GameState), 4);
            data["operatingMode"] = ActorSnapshot.Hex(U16(ram, DkcRam.OperatingMode), 4);
            string tracingReason;
            data["objectTracingActive"] = DkcFrameSnapshot.IsObjectTracingActive(ram, out tracingReason);
            data["objectTracingReason"] = tracingReason;
            data["layerX"] = ActorSnapshot.Hex(U16(ram, DkcRam.LayerX), 4);
            data["layerY"] = ActorSnapshot.Hex(U16(ram, DkcRam.LayerY), 4);
            data["lowerBound"] = ActorSnapshot.Hex(U16(ram, DkcRam.CameraLowerBound), 4);
            data["upperBound"] = ActorSnapshot.Hex(U16(ram, DkcRam.CameraUpperBound), 4);
            data["scannerLeft"] = ActorSnapshot.Hex(U16(ram, DkcRam.ScannerWindowLeft), 4);
            data["scannerRight"] = ActorSnapshot.Hex(U16(ram, DkcRam.ScannerWindowRight), 4);
            data["scannerRecord"] = ram[DkcRam.ScannerRecordIndex];
            data["scannerPrimary"] = ram[DkcRam.ScannerCursorPrimary];
            data["scannerSecondary"] = ram[DkcRam.ScannerCursorSecondary];
            data["currentActorIndex"] = U16(ram, DkcRam.CurrentActorIndex);
            object eventType;
            if (data.TryGetValue("type", out eventType) && string.Equals(eventType as string, "scanner_decision", StringComparison.Ordinal))
            {
                data["primaryFreeActorIndices"] = FreeActorIndices(ram, 0x02, 0x1C);
                data["secondaryFreeActorIndices"] = FreeActorIndices(ram, 0x1E, 0x32);
            }
            var record = FindRecord(ram[DkcRam.ScannerRecordIndex]);
            if (record != null) data["object"] = record.ToData(_previous);
        }

        private ObjectRecord FindRecord(int index)
        {
            foreach (var record in _objects)
            {
                if (record.Index == index) return record;
                var child = record.Children.FirstOrDefault(c => c.Index == index);
                if (child != null) return child;
            }
            return null;
        }

        private IDictionary<string, object> BuildCapture(DkcFrameSnapshot frame, IList<string> anomalies, IList<string> observations, bool full, string reason)
        {
            var data = frame.ToData(full ? _objects : LifecycleAnalyzer.Nearby(frame, _objects, 0x300), anomalies, observations);
            data["type"] = "object_lifecycle_capture";
            data["reason"] = reason;
            data["utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            data["objectDecodeError"] = _objectDecodeError;
            data["writeTrace"] = _writeTrace;
            data["scannerTrace"] = _scannerTrace;
            if (full) data["levelObjects"] = frame.ObjectTracingActive ? _objects.Select(r => (object)r.ToData(frame)).ToArray() : Array.Empty<object>();
            return data;
        }

        private void UpdatePersistentObservations(DkcFrameSnapshot frame, IList<string> observations)
        {
            var current = new HashSet<string>(observations ?? Array.Empty<string>(), StringComparer.Ordinal);
            foreach (var stale in _observationCounts.Keys.Where(key => !current.Contains(key)).ToArray()) _observationCounts.Remove(stale);
            var threshold = Math.Max(1, _observationPersistenceFrames.Value);
            foreach (var observation in current)
            {
                int count;
                _observationCounts.TryGetValue(observation, out count);
                count++;
                _observationCounts[observation] = count;
                if (count == threshold && _lastPersistentObservations.Add(observation))
                    _output.Event(new Dictionary<string, object>
                    {
                        { "type", "observation_started" }, { "frame", frame.Frame }, { "persistenceFrames", count },
                        { "message", observation }, { "level", DkcNames.Level(frame.LevelId) }, { "definitive", false }
                    });
            }
            foreach (var ended in _lastPersistentObservations.Where(old => !current.Contains(old)).ToArray())
            {
                _lastPersistentObservations.Remove(ended);
                _output.Event(new Dictionary<string, object> { { "type", "observation_ended" }, { "frame", frame.Frame }, { "message", ended } });
            }
        }

        private void ProcessFileCommands()
        {
            Directory.CreateDirectory(CommandRoot);
            if (Consume("scanner-trace-on.request")) { _scannerTrace = true; SyncPatches(); WriteCommandStatus("scanner trace enabled"); }
            if (Consume("scanner-trace-off.request")) { _scannerTrace = false; SyncPatches(); WriteCommandStatus("scanner trace disabled"); }
            if (Consume("write-trace-on.request")) { _writeTrace = true; SyncPatches(); WriteCommandStatus("memory write trace enabled"); }
            if (Consume("write-trace-off.request")) { _writeTrace = false; SyncPatches(); WriteCommandStatus("memory write trace disabled"); }
            if (Consume("reset.request")) { ResetContext("file-command"); WriteCommandStatus("trace context reset"); }
            var capture = Path.Combine(CommandRoot, "capture.request");
            if (File.Exists(capture))
            {
                var reason = "file-request";
                try { var value = File.ReadAllText(capture).Trim(); if (value.Length != 0) reason = value; } catch { }
                try { File.Delete(capture); } catch { }
                if (_previous != null)
                {
                    var anomalies = LifecycleAnalyzer.FindAnomalies(_previous, _objects);
                    var observations = LifecycleAnalyzer.FindObservations(_previous, _objects);
                    var path = _output.Capture(BuildCapture(_previous, anomalies, observations, true, reason), reason);
                    WriteCommandStatus("capture written", path);
                }
                else WriteCommandStatus("capture requested before a DKC frame was sampled");
            }
        }

        private bool Consume(string name)
        {
            var path = Path.Combine(CommandRoot, name);
            if (!File.Exists(path)) return false;
            try { File.Delete(path); } catch { }
            return true;
        }

        private void WriteCommandStatus(string message, string path = null)
        {
            var data = new Dictionary<string, object>
            {
                { "utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) }, { "message", message },
                { "path", path }, { "writeTrace", _writeTrace }, { "scannerTrace", _scannerTrace },
                { "sessionRoot", _output.Root }
            };
            File.WriteAllText(Path.Combine(CommandRoot, "command-status.json"), Json.Object(data));
            _output.Event(new Dictionary<string, object>(data) { { "type", "command" } });
        }

        private void ResetContext(string reason)
        {
            _previous = null;
            _objects.Clear();
            _decodedEntrance = 0xFFFF;
            _objectDecodeError = null;
            _lastWriters.Clear();
            _lastAnomalies.Clear();
            _observationCounts.Clear();
            _lastPersistentObservations.Clear();
            _output.Event(new Dictionary<string, object> { { "type", "context_reset" }, { "reason", reason }, { "utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) } });
        }

        private string LastWriter(int offset) { string value; return _lastWriters.TryGetValue(offset, out value) ? value : null; }

        private static string DescribeWram(int offset)
        {
            if (DkcRam.InActorTable(offset, DkcRam.ActorId)) return "actor-id[" + ((offset - DkcRam.ActorId) & ~1) + "]";
            if (DkcRam.InActorTable(offset, DkcRam.ActorSourceRecord)) return "actor-source[" + ((offset - DkcRam.ActorSourceRecord) & ~1) + "]";
            if (DkcRam.InActorTable(offset, DkcRam.ActorX)) return "actor-x[" + ((offset - DkcRam.ActorX) & ~1) + "]";
            if (DkcRam.InActorTable(offset, DkcRam.ActorY)) return "actor-y[" + ((offset - DkcRam.ActorY) & ~1) + "]";
            if (DkcRam.InActorTable(offset, DkcRam.ActorState)) return "actor-state[" + ((offset - DkcRam.ActorState) & ~1) + "]";
            if (offset >= DkcRam.Bookkeeping && offset < DkcRam.Bookkeeping + DkcRam.BookkeepingLength) return "object-bookkeeping[" + (offset - DkcRam.Bookkeeping) + "]";
            if (offset >= DkcRam.SectionControllerState && offset <= DkcRam.SectionControllerLimit + 1) return "section-controller";
            return "context";
        }

        private static ushort U16(byte[] ram, int address) { return (ushort)(ram[address] | (ram[address + 1] << 8)); }
        private static object[] FreeActorIndices(byte[] ram, int first, int last)
        {
            var result = new List<object>();
            for (var index = first; index <= last; index += 2)
                if (U16(ram, DkcRam.ActorId + index) == 0)
                    result.Add(new Dictionary<string, object> { { "actorIndex", index }, { "actorIndexHex", ActorSnapshot.Hex((uint)index, 2) } });
            return result.ToArray();
        }
        private static string Register(object state, string name)
        {
            var value = Reflect.Get(state, name);
            if (value == null) return null;
            try { return ActorSnapshot.Hex(Convert.ToUInt32(value, CultureInfo.InvariantCulture), 4); } catch { return null; }
        }

        private static string Flags(object state)
        {
            if (state == null) return null;
            return Bool(state, "flagN", 'N') + Bool(state, "flagV", 'V') + Bool(state, "flagM", 'M') + Bool(state, "flagX", 'X')
                + Bool(state, "flagD", 'D') + Bool(state, "flagI", 'I') + Bool(state, "flagZ", 'Z') + Bool(state, "flagC", 'C') + Bool(state, "flagE", 'E');
        }

        private static string Bool(object state, string name, char letter)
        {
            try { return Convert.ToBoolean(Reflect.Get(state, name), CultureInfo.InvariantCulture) ? letter.ToString() : char.ToLowerInvariant(letter).ToString(); }
            catch { return "?"; }
        }
    }

    internal static class TracerHooks
    {
        public static void RunFramePostfix(object __instance)
        {
            var plugin = DKCObjectLifecycleTracerPlugin.Instance;
            if (plugin != null) plugin.OnFrame(__instance);
        }

        public static void WriteMemPrefix(uint addr, byte val)
        {
            var plugin = DKCObjectLifecycleTracerPlugin.Instance;
            if (plugin != null) plugin.OnMemoryWrite(addr, val);
        }

        public static void CpuInstructionPrefix(object __instance)
        {
            var plugin = DKCObjectLifecycleTracerPlugin.Instance;
            if (plugin != null) plugin.OnCpuInstruction(__instance);
        }
    }
}
