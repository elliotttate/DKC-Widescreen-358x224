using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace DKCWramFlightRecorder
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class DKCWramFlightRecorderPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.dkcwramflightrecorder";
        public const string PluginName = "DKC WRAM Flight Recorder";
        public const string PluginVersion = "0.1.0";
        internal static DKCWramFlightRecorderPlugin Instance;

        private ConfigEntry<bool> _armedAtStartup;
        private ConfigEntry<string> _rangeFile;
        private ConfigEntry<string> _outputRoot;
        private ConfigEntry<int> _instructionRingCapacity;
        private ConfigEntry<int> _writeRingCapacity;
        private ConfigEntry<int> _maxRanges;
        private ConfigEntry<int> _maxRangeBytes;
        private ConfigEntry<int> _maxTargetWrites;
        private ConfigEntry<int> _maxPendingCaptures;
        private ConfigEntry<int> _maxEmulatedFrames;
        private ConfigEntry<bool> _captureOpcodeText;
        private ConfigEntry<bool> _consumeWatchdogEvidence;
        private ConfigEntry<bool> _consumeWatchdogCaptureRequests;
        private ConfigEntry<string> _watchdogEvidenceRoot;
        private ConfigEntry<bool> _disarmAfterWatchdogEvidence;

        private readonly object _gate = new object();
        private readonly Queue<TargetWriteCapture> _pending = new Queue<TargetWriteCapture>();
        private Harmony _harmony;
        private RuntimeBinding _binding;
        private IDictionary<string, object> _contractEvidence;
        private RangePlan _plan;
        private RingBuffer<InstructionSample> _instructions;
        private RingBuffer<WriteSample> _writes;
        private InstructionSample _currentInstruction;
        private bool _hasCurrentInstruction;
        private TraceSession _session;
        private volatile bool _armed;
        private bool _cpuPatched;
        private bool _writePatched;
        private volatile bool _hookFaulted;
        private string _hookFault = string.Empty;
        private volatile bool _stopRequested;
        private string _stopReason = string.Empty;
        private long _instructionSequence;
        private long _writeSequence;
        private long _capturedTargetWrites;
        private int _startFrame = int.MinValue;
        private int _lastFrame = int.MinValue;
        private string _activeRangeFile = string.Empty;
        private string _lastError = string.Empty;
        private string _lastAction = "startup-disarmed";
        private string _lastDump = string.Empty;
        private string _lastWatchdogEvidence = string.Empty;
        private DateTime _armedUtc;
        private float _nextControlPoll;
        private float _nextWatchdogPoll;
        private float _nextStatusWrite;

        private string PluginRoot { get { return Path.Combine(Paths.PluginPath, "DKCWramFlightRecorder"); } }
        private string ControlRoot { get { return Path.Combine(PluginRoot, "control"); } }
        private string StatusPath { get { return Path.Combine(ControlRoot, "status.json"); } }

        private void Awake()
        {
            Instance = this;
            _armedAtStartup = Config.Bind("General", "ArmedAtStartup", false,
                "Install the per-instruction and per-WRAM-write prefixes at startup. False by default; disarmed mode has no Harmony hooks.");
            _rangeFile = Config.Bind("Paths", "RangeFile", string.Empty,
                "Range plan path. Empty uses BepInEx/plugins/DKCWramFlightRecorder/control/ranges.txt.");
            _outputRoot = Config.Bind("Paths", "OutputRoot", string.Empty,
                "Trace root. Empty uses BepInEx/plugins/DKCWramFlightRecorder/Traces.");
            _instructionRingCapacity = Config.Bind("Rings", "PrecedingInstructions", 32, "Bounded instruction history attached to each target write (1-256).");
            _writeRingCapacity = Config.Bind("Rings", "PrecedingWramWrites", 16, "Bounded all-WRAM-write history attached to each target write (1-256).");
            _maxRanges = Config.Bind("Limits", "MaxRanges", 64, "Maximum non-overlapping configured ranges (1-256).");
            _maxRangeBytes = Config.Bind("Limits", "MaxRangeBytes", 4096, "Maximum total configured WRAM bytes (1-131072).");
            _maxTargetWrites = Config.Bind("Limits", "MaxTargetWrites", 100000, "Automatically dump and disarm after this many target writes.");
            _maxPendingCaptures = Config.Bind("Limits", "MaxPendingCaptures", 4096, "Fail closed if Unity Update cannot drain captured target writes before this bound.");
            _maxEmulatedFrames = Config.Bind("Limits", "MaxEmulatedFrames", 600, "Automatically dump and disarm after this many emulated frames; zero disables the frame limit.");
            _captureOpcodeText = Config.Bind("Capture", "OpcodeText", true, "Include SuperZSNES disassembly text in instruction samples.");
            _consumeWatchdogEvidence = Config.Bind("Watchdog", "ObserveCommittedEvidence", false,
                "Read newly committed DKCSoftlockWatchdog evidence.json files and atomically dump this recorder. Never alters watchdog files.");
            _consumeWatchdogCaptureRequests = Config.Bind("Watchdog", "ConsumeCaptureRequest", false,
                "Consume the optional sibling watchdog capture.request convention while armed, then correlate the newest committed evidence. Off by default.");
            _watchdogEvidenceRoot = Config.Bind("Watchdog", "EvidenceRoot", string.Empty,
                "Watchdog Sessions root. Empty uses the sibling DKCSoftlockWatchdog/Sessions convention; there is no assembly dependency.");
            _disarmAfterWatchdogEvidence = Config.Bind("Watchdog", "DisarmAfterEvidence", false,
                "Disarm after a watchdog-triggered dump. False keeps recording until a configured limit or disarm.request.");

            Directory.CreateDirectory(ControlRoot);
            _harmony = new Harmony(PluginGuid);
            WriteExampleRangeFile();
            WriteStatus();
            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded disarmed. No Harmony hooks are installed while disarmed.");
            if (_armedAtStartup.Value) Arm("startup", ResolveRangeFile(null));
        }

        private void Update()
        {
            if (Time.realtimeSinceStartup >= _nextControlPoll)
            {
                _nextControlPoll = Time.realtimeSinceStartup + 0.10f;
                ProcessRequests();
            }
            DrainPending();
            if (_armed && _consumeWatchdogEvidence.Value && Time.realtimeSinceStartup >= _nextWatchdogPoll)
            {
                _nextWatchdogPoll = Time.realtimeSinceStartup + 0.50f;
                ObserveWatchdogEvidence();
            }
            if (_armed && _hookFaulted)
            {
                Dump("hook-fault", null);
                Disarm("hook-fault: " + _hookFault, false);
            }
            else if (_armed && _stopRequested)
            {
                DrainPending();
                Dump(_stopReason, null);
                Disarm(_stopReason, false);
            }
            else if (_armed && Time.realtimeSinceStartup >= _nextStatusWrite)
            {
                _nextStatusWrite = Time.realtimeSinceStartup + 0.50f;
                WriteStatus();
            }
        }

        private void OnDestroy()
        {
            try { Disarm("shutdown", false); } catch { }
            try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
            if (ReferenceEquals(Instance, this)) Instance = null;
        }

        private void Arm(string reason, string rangePath)
        {
            if (_armed) { SetError("Arm ignored: recorder is already armed."); return; }
            RangePlan plan;
            RuntimeContractResult contract;
            TraceSession session = null;
            try
            {
                if (!File.Exists(rangePath)) throw new FileNotFoundException("Range file does not exist.", rangePath);
                var rangeBytes = File.ReadAllBytes(rangePath);
                var rangeText = new UTF8Encoding(false, true).GetString(rangeBytes);
                plan = RangePlan.Parse(rangeText, Clamp(_maxRanges.Value, 1, 256), Clamp(_maxRangeBytes.Value, 1, WramAddress.WramSize));
                contract = SuperZsnesContract.Validate();
                if (!contract.Valid) throw new InvalidOperationException("Runtime contract rejected arming: " + contract.Error);

                var sessionRoot = UniqueDirectory(Path.Combine(ResolveOutputRoot(), DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)));
                var settings = new Dictionary<string, object>
                {
                    { "reason", reason }, { "rangeFile", rangePath }, { "rangeFileSha256", AtomicFile.Sha256(rangeBytes) },
                    { "precedingInstructions", Clamp(_instructionRingCapacity.Value, 1, 256) },
                    { "precedingWramWrites", Clamp(_writeRingCapacity.Value, 1, 256) },
                    { "maxTargetWrites", Math.Max(1, _maxTargetWrites.Value) },
                    { "maxEmulatedFrames", Math.Max(0, _maxEmulatedFrames.Value) },
                    { "opcodeText", _captureOpcodeText.Value }, { "watchdogObservation", _consumeWatchdogEvidence.Value },
                    { "watchdogCaptureRequest", _consumeWatchdogCaptureRequests.Value }
                };
                session = new TraceSession(sessionRoot, plan, contract.Evidence, settings);

                lock (_gate)
                {
                    _binding = contract.Binding;
                    _contractEvidence = contract.Evidence;
                    _plan = plan;
                    _instructions = new RingBuffer<InstructionSample>(Clamp(_instructionRingCapacity.Value, 1, 256));
                    _writes = new RingBuffer<WriteSample>(Clamp(_writeRingCapacity.Value, 1, 256));
                    _pending.Clear();
                    _session = session;
                    _hasCurrentInstruction = false;
                    _hookFaulted = false;
                    _hookFault = string.Empty;
                    _stopRequested = false;
                    _stopReason = string.Empty;
                    _instructionSequence = 0;
                    _writeSequence = 0;
                    _capturedTargetWrites = 0;
                    _startFrame = int.MinValue;
                    _lastFrame = int.MinValue;
                    _activeRangeFile = rangePath;
                    _armedUtc = DateTime.UtcNow;
                }

                InstallHotHooks();
                _armed = true;
                _lastError = string.Empty;
                _lastAction = "armed: " + reason;
                session.Event("armed", new Dictionary<string, object> { { "reason", reason }, { "rangeFile", rangePath } });
                WriteStatus();
                Logger.LogWarning(PluginName + " armed for a bounded diagnostic replay; per-instruction tracing is expensive. Session: " + sessionRoot);
            }
            catch (Exception ex)
            {
                _armed = false;
                RollBackHotHooks();
                if (session != null)
                {
                    try { session.Event("arm_failed", new Dictionary<string, object> { { "error", ex.Message } }); } catch { }
                    try { session.Dispose(); } catch { }
                }
                lock (_gate) { if (ReferenceEquals(_session, session)) _session = null; }
                SetError("Arm failed closed; no recorder hooks retained: " + ex.Message);
            }
        }

        private void InstallHotHooks()
        {
            if (_binding == null) throw new InvalidOperationException("Runtime binding is missing.");
            var cpuHook = AccessTools.Method(typeof(RecorderHooks), nameof(RecorderHooks.CpuPrefix));
            var writeHook = AccessTools.Method(typeof(RecorderHooks), nameof(RecorderHooks.WritePrefix));
            if (cpuHook == null || writeHook == null) throw new MissingMethodException("Recorder hook methods are missing.");
            try
            {
                _harmony.Patch(_binding.ExecuteNextInstruction, prefix: new HarmonyMethod(cpuHook));
                _cpuPatched = HasOwnedPrefix(_binding.ExecuteNextInstruction);
                if (!_cpuPatched) throw new InvalidOperationException("CPU prefix was not owned after patching.");
                _harmony.Patch(_binding.WriteMem, prefix: new HarmonyMethod(writeHook));
                _writePatched = HasOwnedPrefix(_binding.WriteMem);
                if (!_writePatched) throw new InvalidOperationException("WRAM write prefix was not owned after patching.");
            }
            catch
            {
                RollBackHotHooks();
                throw;
            }
        }

        private void Disarm(string reason, bool writeDump)
        {
            if (!_armed && !_cpuPatched && !_writePatched) return;
            _armed = false;
            if (writeDump) Dump(reason, null);
            DrainPending();
            var session = _session;
            try { if (session != null) session.Event("disarmed", new Dictionary<string, object> { { "reason", reason } }); } catch { }
            RollBackHotHooks();
            try { if (session != null) session.Dispose(); } catch { }
            lock (_gate)
            {
                if (ReferenceEquals(_session, session)) _session = null;
                _pending.Clear();
            }
            _lastAction = "disarmed: " + reason;
            WriteStatus();
        }

        private void RollBackHotHooks()
        {
            try
            {
                if (_harmony != null) _harmony.UnpatchSelf();
            }
            catch (Exception ex) { _lastError = "Harmony rollback threw: " + ex.Message; }
            _cpuPatched = _binding != null && HasOwnedPrefix(_binding.ExecuteNextInstruction);
            _writePatched = _binding != null && HasOwnedPrefix(_binding.WriteMem);
            if (_cpuPatched || _writePatched)
                _lastError = "CRITICAL: disarm could not remove every owned hot prefix; capture is fail-closed but restart is required.";
        }

        private bool HasOwnedPrefix(MethodBase method)
        {
            if (method == null) return false;
            var info = Harmony.GetPatchInfo(method);
            return info != null && info.Prefixes.Any(patch => string.Equals(patch.owner, PluginGuid, StringComparison.Ordinal));
        }

        internal void OnCpuInstruction(object cpu)
        {
            if (!_armed || _hookFaulted || _stopRequested || cpu == null) return;
            try
            {
                var binding = _binding;
                var master = binding.CpuMaster.GetValue(cpu);
                var sample = new InstructionSample
                {
                    Sequence = 0,
                    Frame = InvokeInt(binding.GetFrameNo, master),
                    Line = InvokeInt(binding.GetLineNo, master),
                    Dot = InvokeInt(binding.GetPixelNo, master),
                    Pc = Convert.ToUInt32(binding.GetPcAddress.Invoke(cpu, null), CultureInfo.InvariantCulture) & 0xFFFFFF,
                    Pb = (uint)binding.RegPb.GetValue(cpu), Db = (uint)binding.RegDb.GetValue(cpu), D = (uint)binding.RegD.GetValue(cpu),
                    A = (int)binding.RegA.GetValue(cpu), X = (int)binding.RegX.GetValue(cpu), Y = (int)binding.RegY.GetValue(cpu), S = (uint)binding.RegS.GetValue(cpu),
                    Cycles = (long)binding.TotalCycles.GetValue(cpu) + (int)binding.NumCycles.GetValue(cpu),
                    Flags = Flags(binding, cpu),
                    Opcode = _captureOpcodeText.Value ? Convert.ToString(binding.GetDebugOpcodeString.Invoke(cpu, null), CultureInfo.InvariantCulture) : string.Empty
                };
                lock (_gate)
                {
                    if (!_armed) return;
                    sample.Sequence = ++_instructionSequence;
                    if (sample.Frame >= 0 && _lastFrame != int.MinValue && sample.Frame < _lastFrame)
                    {
                        _instructions.Clear();
                        _writes.Clear();
                        _hasCurrentInstruction = false;
                        _startFrame = sample.Frame;
                    }
                    if (_hasCurrentInstruction) _instructions.Add(_currentInstruction);
                    _currentInstruction = sample;
                    _hasCurrentInstruction = true;
                    if (_startFrame == int.MinValue && sample.Frame >= 0) _startFrame = sample.Frame;
                    if (sample.Frame >= 0) _lastFrame = sample.Frame;
                    var maxFrames = Math.Max(0, _maxEmulatedFrames.Value);
                    if (maxFrames > 0 && _startFrame >= 0 && sample.Frame >= _startFrame && sample.Frame - _startFrame >= maxFrames)
                    {
                        _stopRequested = true;
                        _stopReason = "emulated-frame-limit";
                    }
                }
            }
            catch (Exception ex) { FaultFromHook("CPU instruction capture failed", ex); }
        }

        internal void OnMemoryWrite(object memory, uint busAddress, byte newValue)
        {
            if (!_armed || _hookFaulted || _stopRequested || memory == null) return;
            int offset;
            if (!WramAddress.TryNormalizeBus(busAddress, out offset)) return;
            try
            {
                var binding = _binding;
                var ram = binding.MainRam.GetValue(memory) as byte[];
                if (ram == null || ram.Length != WramAddress.WramSize) throw new InvalidOperationException("MainMemoryMap.mainRam is not exactly 128 KiB.");
                var master = binding.MemoryMaster.GetValue(memory);
                var range = _plan.Find(offset);
                var write = new WriteSample
                {
                    Sequence = 0,
                    Frame = InvokeInt(binding.GetFrameNo, master), Line = InvokeInt(binding.GetLineNo, master), Dot = InvokeInt(binding.GetPixelNo, master),
                    Pc = 0,
                    BusAddress = busAddress & 0xFFFFFF, WramOffset = offset, OldValue = ram[offset], NewValue = newValue, Targeted = range != null
                };
                lock (_gate)
                {
                    if (!_armed) return;
                    write.Sequence = ++_writeSequence;
                    write.Pc = _hasCurrentInstruction ? _currentInstruction.Pc : 0;
                    var precedingWrites = range == null ? null : _writes.Snapshot();
                    if (range != null)
                    {
                        var targetLimit = Math.Max(1, _maxTargetWrites.Value);
                        if (_pending.Count >= Clamp(_maxPendingCaptures.Value, 1, 100000))
                            throw new InvalidOperationException("Pending target-write capture bound was reached before Unity Update drained it.");
                        _pending.Enqueue(new TargetWriteCapture
                        {
                            Write = write, Range = range, CurrentInstruction = _currentInstruction, HasCurrentInstruction = _hasCurrentInstruction,
                            PrecedingInstructions = _instructions.Snapshot(), PrecedingWrites = precedingWrites
                        });
                        _capturedTargetWrites++;
                        if (_capturedTargetWrites >= targetLimit)
                        {
                            _stopRequested = true;
                            _stopReason = "target-write-limit";
                        }
                    }
                    _writes.Add(write);
                }
            }
            catch (Exception ex) { FaultFromHook("WRAM write capture failed", ex); }
        }

        private void FaultFromHook(string message, Exception error)
        {
            lock (_gate)
            {
                _hookFaulted = true;
                _hookFault = message + ": " + error.GetBaseException().Message;
            }
        }

        private void DrainPending()
        {
            while (true)
            {
                TargetWriteCapture capture;
                TraceSession session;
                lock (_gate)
                {
                    if (_pending.Count == 0) return;
                    capture = _pending.Dequeue();
                    session = _session;
                }
                try { if (session != null) session.TargetWrite(capture); }
                catch (Exception ex) { FaultFromHook("Trace output failed", ex); return; }
            }
        }

        private void Dump(string reason, string watchdogEvidence)
        {
            TraceSession session;
            InstructionSample current;
            bool hasCurrent;
            InstructionSample[] instructions;
            WriteSample[] writes;
            long captured;
            lock (_gate)
            {
                session = _session;
                if (session == null) { SetError("Dump ignored: recorder is disarmed."); return; }
                current = _currentInstruction;
                hasCurrent = _hasCurrentInstruction;
                instructions = _instructions.Snapshot();
                writes = _writes.Snapshot();
                captured = _capturedTargetWrites;
            }
            try
            {
                DrainPending();
                _lastDump = session.Dump(reason, hasCurrent, current, instructions, writes, captured, watchdogEvidence);
                _lastAction = "dump: " + reason;
                _lastError = string.Empty;
                WriteStatus();
            }
            catch (Exception ex) { SetError("Dump failed: " + ex.Message); }
        }

        private void ProcessRequests()
        {
            ProcessRequest("disarm.request", contents => Disarm(string.IsNullOrWhiteSpace(contents) ? "disarm.request" : contents.Trim(), true));
            ProcessRequest("arm.request", contents => Arm("arm.request", ResolveRangeFile(contents)));
            ProcessRequest("dump.request", contents => Dump(string.IsNullOrWhiteSpace(contents) ? "dump.request" : contents.Trim(), null));
            ProcessRequest("mark.request", contents =>
            {
                if (_session == null) throw new InvalidOperationException("Recorder is disarmed.");
                _session.Event("marker", new Dictionary<string, object> { { "text", contents.Trim() }, { "instructionSequence", _instructionSequence }, { "writeSequence", _writeSequence } });
                _lastAction = "marker";
                WriteStatus();
            });
            ProcessRequest("watchdog.request", contents => ConsumeWatchdogEvidence(contents.Trim(), "watchdog.request"));
            if (_armed && _consumeWatchdogCaptureRequests.Value)
                ProcessRequestAt(Path.Combine(PluginRoot, "capture.request"), contents => ConsumeWatchdogCaptureRequest(contents));
        }

        private void ProcessRequest(string name, Action<string> action)
        {
            ProcessRequestAt(Path.Combine(ControlRoot, name), action);
        }

        private void ProcessRequestAt(string path, Action<string> action)
        {
            if (!File.Exists(path)) return;
            var claimed = path + ".processing-" + Guid.NewGuid().ToString("N");
            try { File.Move(path, claimed); }
            catch (FileNotFoundException) { return; }
            catch (IOException) { return; }
            try
            {
                var contents = File.ReadAllText(claimed);
                action(contents);
            }
            catch (Exception ex) { SetError(Path.GetFileName(path) + " failed: " + ex.GetBaseException().Message); }
            finally { try { if (File.Exists(claimed)) File.Delete(claimed); } catch { } }
        }

        private void ConsumeWatchdogCaptureRequest(string contents)
        {
            if (!_armed) throw new InvalidOperationException("Recorder must be armed before consuming capture.request.");
            var evidence = LatestWatchdogEvidence();
            if (evidence == null) throw new FileNotFoundException("capture.request was present, but no committed watchdog evidence.json exists below " + ResolveWatchdogRoot() + ".");
            if (_session != null) _session.Event("watchdog_capture_request_consumed", new Dictionary<string, object>
            {
                { "requestText", contents == null ? string.Empty : contents.Trim() }, { "requestOwnedByRecorder", true },
                { "watchdogEvidenceReadOnly", true }
            });
            ConsumeWatchdogEvidence(evidence, "capture.request");
        }

        private void ObserveWatchdogEvidence()
        {
            if (!_armed) return;
            var root = ResolveWatchdogRoot();
            if (!Directory.Exists(root)) return;
            try
            {
                var candidate = Directory.GetFiles(root, "evidence.json", SearchOption.AllDirectories)
                    .Select(path => new FileInfo(path))
                    .Where(file => file.LastWriteTimeUtc >= _armedUtc && !string.Equals(file.FullName, _lastWatchdogEvidence, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(file => file.LastWriteTimeUtc).LastOrDefault();
                if (candidate != null) ConsumeWatchdogEvidence(candidate.FullName, "watchdog-observer");
            }
            catch (Exception ex) { SetError("Watchdog evidence scan failed: " + ex.Message); }
        }

        private string LatestWatchdogEvidence()
        {
            var root = ResolveWatchdogRoot();
            if (!Directory.Exists(root)) return null;
            return Directory.GetFiles(root, "evidence.json", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path)).OrderBy(file => file.LastWriteTimeUtc).LastOrDefault()?.FullName;
        }

        private void ConsumeWatchdogEvidence(string path, string source)
        {
            if (!_armed) throw new InvalidOperationException("Recorder must be armed before consuming a watchdog trigger.");
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("watchdog.request must contain an evidence.json path or trigger directory.");
            var resolved = Path.GetFullPath(path);
            if (Directory.Exists(resolved)) resolved = Path.Combine(resolved, "evidence.json");
            if (!File.Exists(resolved) || !string.Equals(Path.GetFileName(resolved), "evidence.json", StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException("Committed watchdog evidence.json was not found.", resolved);
            var hash = AtomicFile.Sha256File(resolved);
            _lastWatchdogEvidence = resolved;
            if (_session != null) _session.Event("watchdog_evidence_observed", new Dictionary<string, object>
            {
                { "source", source }, { "path", resolved }, { "sha256", hash }, { "readOnly", true }
            });
            Dump("watchdog-evidence", resolved);
            if (_disarmAfterWatchdogEvidence.Value) Disarm("watchdog-evidence", false);
        }

        private void WriteStatus()
        {
            try
            {
                Dictionary<string, object> status;
                lock (_gate)
                {
                    status = new Dictionary<string, object>
                    {
                        { "schemaVersion", 1 }, { "tool", PluginName }, { "version", PluginVersion },
                        { "utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
                        { "armed", _armed }, { "hotHooksPresent", _cpuPatched || _writePatched },
                        { "cpuPrefixPresent", _cpuPatched }, { "writePrefixPresent", _writePatched },
                        { "failClosed", _hookFaulted }, { "lastAction", _lastAction }, { "lastError", _lastError },
                        { "rangeFile", _activeRangeFile }, { "rangeCount", _plan == null ? 0 : _plan.Ranges.Count },
                        { "rangeBytes", _plan == null ? 0 : _plan.TotalBytes }, { "sessionRoot", _session == null ? null : _session.Root },
                        { "instructionSequence", _instructionSequence }, { "wramWriteSequence", _writeSequence },
                        { "startFrame", _startFrame == int.MinValue ? (object)null : _startFrame },
                        { "lastFrame", _lastFrame == int.MinValue ? (object)null : _lastFrame },
                        { "capturedTargetWrites", _capturedTargetWrites }, { "pendingCaptures", _pending.Count },
                        { "lastDump", _lastDump }, { "lastWatchdogEvidence", _lastWatchdogEvidence },
                        { "observationOnly", true }, { "gameplayWrites", false }
                    };
                }
                AtomicFile.WriteText(StatusPath, Json.Object(status));
            }
            catch (Exception ex) { Logger.LogWarning("Could not write recorder status: " + ex.Message); }
        }

        private void SetError(string message)
        {
            _lastError = message;
            _lastAction = "error";
            try { Logger.LogWarning(message); } catch { }
            WriteStatus();
        }

        private string ResolveRangeFile(string requested)
        {
            if (!string.IsNullOrWhiteSpace(requested)) return Path.GetFullPath(requested.Trim());
            if (!string.IsNullOrWhiteSpace(_rangeFile.Value)) return Path.GetFullPath(_rangeFile.Value.Trim());
            return Path.Combine(ControlRoot, "ranges.txt");
        }

        private string ResolveOutputRoot()
        {
            return string.IsNullOrWhiteSpace(_outputRoot.Value) ? Path.Combine(PluginRoot, "Traces") : Path.GetFullPath(_outputRoot.Value.Trim());
        }

        private string ResolveWatchdogRoot()
        {
            return string.IsNullOrWhiteSpace(_watchdogEvidenceRoot.Value)
                ? Path.Combine(Paths.PluginPath, "DKCSoftlockWatchdog", "Sessions")
                : Path.GetFullPath(_watchdogEvidenceRoot.Value.Trim());
        }

        private void WriteExampleRangeFile()
        {
            var path = Path.Combine(ControlRoot, "ranges.example.txt");
            if (!File.Exists(path)) AtomicFile.WriteText(path,
                "# Copy to ranges.txt and replace with FirstDivergence-selected ranges.\n" +
                "$7E192B-$7E1930 scanner-bookmarks\n" +
                "# 0x1A5B+2 layer-x\n");
        }

        private static string UniqueDirectory(string proposed)
        {
            if (!Directory.Exists(proposed)) return proposed;
            for (var index = 1; index < 10000; index++)
            {
                var candidate = proposed + "-" + index.ToString("D3", CultureInfo.InvariantCulture);
                if (!Directory.Exists(candidate)) return candidate;
            }
            throw new IOException("Could not allocate a unique trace session directory.");
        }

        private static int InvokeInt(MethodInfo method, object instance)
        {
            if (method == null || instance == null) return -1;
            return Convert.ToInt32(method.Invoke(instance, null), CultureInfo.InvariantCulture);
        }

        private static string Flags(RuntimeBinding binding, object cpu)
        {
            return Flag(binding.FlagN, cpu, 'N') + Flag(binding.FlagV, cpu, 'V') + Flag(binding.FlagM, cpu, 'M') +
                   Flag(binding.FlagX, cpu, 'X') + Flag(binding.FlagD, cpu, 'D') + Flag(binding.FlagI, cpu, 'I') +
                   Flag(binding.FlagZ, cpu, 'Z') + Flag(binding.FlagC, cpu, 'C') + Flag(binding.FlagE, cpu, 'E');
        }

        private static string Flag(FieldInfo field, object instance, char name)
        {
            return (bool)field.GetValue(instance) ? name.ToString() : name.ToString().ToLowerInvariant();
        }

        private static int Clamp(int value, int minimum, int maximum) { return Math.Max(minimum, Math.Min(maximum, value)); }
    }

    internal static class RecorderHooks
    {
        internal static void CpuPrefix(object __instance)
        {
            var instance = DKCWramFlightRecorderPlugin.Instance;
            if (instance != null) instance.OnCpuInstruction(__instance);
        }

        internal static void WritePrefix(object __instance, uint addr, byte val)
        {
            var instance = DKCWramFlightRecorderPlugin.Instance;
            if (instance != null) instance.OnMemoryWrite(__instance, addr, val);
        }
    }
}
