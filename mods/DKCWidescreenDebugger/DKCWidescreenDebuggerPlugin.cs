using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace DKCWidescreenDebugger
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class DKCWidescreenDebuggerPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.dkcwidescreendebugger";
        public const string PluginName = "DKC Widescreen Debugger";
        public const string PluginVersion = "0.1.4";

        internal static DKCWidescreenDebuggerPlugin Instance;

        private readonly WramSearch _search = new WramSearch();
        private readonly RingLog _recent = new RingLog(18);
        private readonly List<MemoryWatch> _watches = new List<MemoryWatch>();
        private List<AddressRange> _executeBreakpoints = new List<AddressRange>();
        private List<AddressRange> _tracePcRanges = new List<AddressRange>();
        private List<AddressRange> _writeRanges = new List<AddressRange>();
        private List<AddressRange> _readRanges = new List<AddressRange>();

        private ConfigEntry<KeyCode> _overlayKey;
        private ConfigEntry<KeyCode> _pauseKey;
        private ConfigEntry<KeyCode> _stepFrameKey;
        private ConfigEntry<KeyCode> _captureKey;
        private ConfigEntry<KeyCode> _traceKey;
        private ConfigEntry<bool> _showAtStartup;
        private ConfigEntry<bool> _captureOnBreakpoint;
        private ConfigEntry<bool> _pauseOnWatchChange;
        private ConfigEntry<int> _maxInstructionsPerFrame;
        private ConfigEntry<string> _configuredWatches;
        private ConfigEntry<string> _configuredBreakpoints;
        private ConfigEntry<bool> _bridgeEnabled;
        private ConfigEntry<int> _bridgePort;

        private Harmony _harmony;
        private SessionLog _session;
        private CaptureService _capture;
        private LocalDebugBridge _bridge;
        private object _master;
        private MethodBase _cpuMethod;
        private MethodBase _writeMethod;
        private MethodBase _readMethod;
        private MethodBase _ppuWriteMethod;
        private bool _framePatched;
        private bool _cpuPatched;
        private bool _writePatched;
        private bool _readPatched;
        private bool _ppuPatched;
        private bool _inputPatched;
        private bool _overlayVisible;
        private bool _cpuTrace;
        private bool _ppuTrace;
        private bool _suppressMemoryHooks;
        private bool _capturePending;
        private bool _breakLatched;
        private string _captureReason = "manual";
        private string _status = "Waiting for SuperZSNES...";
        private string _watchText = "7E0000:u16:example_camera_x";
        private string _executeText = string.Empty;
        private string _traceFilterText = string.Empty;
        private string _writeText = string.Empty;
        private string _readText = string.Empty;
        private string _exactValueText = "00";
        private string _pokeAddressText = "7E0000";
        private string _pokeValueText = "00";
        private int _instructionsThisFrame;
        private uint _lastBreakpointPc = 0xFFFFFFFF;
        private int _lastFrame = -1;
        private int _forcedControllerIndex;
        private uint _forcedControllerMask;
        private int _forcedControllerFrames;
        private Vector2 _windowScroll;
        private Rect _windowRect = new Rect(20, 20, 760, 760);

        private void Awake()
        {
            Instance = this;
            _overlayKey = Config.Bind("Hotkeys", "ToggleOverlay", KeyCode.F10, "Show or hide the debugger overlay.");
            _pauseKey = Config.Bind("Hotkeys", "PauseResume", KeyCode.F6, "Pause or resume emulation.");
            _stepFrameKey = Config.Bind("Hotkeys", "StepFrame", KeyCode.F7, "Pause and advance exactly one emulated frame.");
            _captureKey = Config.Bind("Hotkeys", "Capture", KeyCode.F8, "Capture WRAM, PPU state, renderer state, and frame images.");
            _traceKey = Config.Bind("Hotkeys", "ToggleCpuTrace", KeyCode.F9, "Toggle instruction tracing.");
            _showAtStartup = Config.Bind("General", "ShowAtStartup", true, "Open the overlay when the plugin loads.");
            _captureOnBreakpoint = Config.Bind("Breakpoints", "CaptureOnBreakpoint", true, "Create a full capture after an execute or memory breakpoint.");
            _pauseOnWatchChange = Config.Bind("Watches", "PauseOnValueChange", false, "Pause when a polled WRAM watch changes.");
            _maxInstructionsPerFrame = Config.Bind("Tracing", "MaxInstructionsPerFrame", 100000, "Safety limit for instruction trace rows per emulated frame.");
            _configuredWatches = Config.Bind("Watches", "Definitions", string.Empty, "Comma-separated address:type:name watches, e.g. 7E1234:s16:camera_x.");
            _configuredBreakpoints = Config.Bind("Breakpoints", "Execute", string.Empty, "Comma-separated 24-bit execute addresses or ranges.");
            _bridgeEnabled = Config.Bind("MCP", "EnableBridge", true, "Expose the authenticated loopback bridge used by the ZSNES MCP server.");
            _bridgePort = Config.Bind("MCP", "BridgePort", 17816, "Loopback TCP port. Use 0 to select an available port automatically.");

            _overlayVisible = _showAtStartup.Value;
            if (!string.IsNullOrWhiteSpace(_configuredWatches.Value)) _watchText = _configuredWatches.Value;
            _executeText = _configuredBreakpoints.Value;

            var sessionRoot = Path.Combine(Paths.PluginPath, "DKCWidescreenDebugger", "Sessions", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            _session = new SessionLog(sessionRoot, Logger);
            _capture = new CaptureService(_session, Logger);
            if (_bridgeEnabled.Value)
            {
                _bridge = new LocalDebugBridge(Path.Combine(Paths.PluginPath, "DKCWidescreenDebugger", "bridge.json"), Logger);
                try { _bridge.Start(_bridgePort.Value); }
                catch (Exception ex) { Logger.LogError("Could not start the MCP bridge: " + ex); }
            }
            _harmony = new Harmony(PluginGuid);
            PatchFrameHook();
            PatchInputHook();
            ApplyDefinitions(false);
            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded. Press " + _overlayKey.Value + " for the overlay.");
        }

        private void OnDestroy()
        {
            try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
            if (_bridge != null) _bridge.Dispose();
            if (_session != null) _session.Dispose();
            if (ReferenceEquals(Instance, this)) Instance = null;
        }

        private void Update()
        {
            ProcessBridgeRequests();
            if (Input.GetKeyDown(_overlayKey.Value)) _overlayVisible = !_overlayVisible;
            if (Input.GetKeyDown(_pauseKey.Value)) TogglePause();
            if (Input.GetKeyDown(_stepFrameKey.Value)) StepFrame();
            if (Input.GetKeyDown(_captureKey.Value)) RequestCapture("manual-hotkey");
            if (Input.GetKeyDown(_traceKey.Value))
            {
                _cpuTrace = !_cpuTrace;
                AddRecent("CPU trace " + (_cpuTrace ? "enabled" : "disabled"));
                SyncDynamicPatches();
            }

            var current = Reflect.Static("MasterExecutor", "Instance");
            if (!_framePatched) PatchFrameHook();
            if (!_inputPatched) PatchInputHook();
            if (current != null && !ReferenceEquals(current, _master))
            {
                _master = current;
                _status = "Attached to SuperZSNES";
                AddRecent(_status);
                ResolveDynamicMethods();
                SyncDynamicPatches();
            }
        }

        private void LateUpdate()
        {
            if (!_capturePending || _master == null) return;
            _capturePending = false;
            try
            {
                _status = "Captured: " + _capture.Capture(_master, _captureReason);
                AddRecent("Full diagnostic capture saved");
            }
            catch (Exception ex)
            {
                _status = "Capture failed: " + ex.Message;
                Logger.LogError(ex);
            }
        }

        private void OnGUI()
        {
            if (!_overlayVisible) return;
            GUI.depth = -10000;
            _windowRect = GUI.Window(834726, _windowRect, DrawWindow, PluginName + " " + PluginVersion);
        }

        private void DrawWindow(int id)
        {
            _windowScroll = GUILayout.BeginScrollView(_windowScroll, GUILayout.Width(750), GUILayout.Height(710));
            GUILayout.Label(_status);
            DrawStatus();
            GUILayout.Space(6);
            DrawTransport();
            GUILayout.Space(8);
            DrawWidescreen();
            GUILayout.Space(8);
            DrawDefinitions();
            GUILayout.Space(8);
            DrawSearch();
            GUILayout.Space(8);
            GUILayout.Label("Recent events");
            foreach (var line in _recent.Lines) GUILayout.Label(line);
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, 730, 24));
        }

        private void DrawStatus()
        {
            if (_master == null)
            {
                GUILayout.Label("No active MasterExecutor. Load a ROM, then this panel will attach automatically.");
                return;
            }
            var cpu = Reflect.Get(_master, "CPUCore65c816");
            var pc = Reflect.UIntCall(cpu, "GetPCAddress", 0);
            var state = Reflect.TryCall(cpu, "GetSaveState");
            var timing = "Frame " + Reflect.IntCall(_master, "GetFrameNo", -1)
                + "  V=" + Reflect.IntCall(_master, "GetLineNo", -1)
                + " H=" + Reflect.IntCall(_master, "GetPixelNo", -1)
                + "  PC=$" + pc.ToString("X6");
            GUILayout.Label(timing);
            if (state != null)
            {
                GUILayout.Label("A=$" + Reflect.Get<int>(state, "regA").ToString("X4")
                    + " X=$" + Reflect.Get<int>(state, "regX").ToString("X4")
                    + " Y=$" + Reflect.Get<int>(state, "regY").ToString("X4")
                    + " S=$" + Reflect.Get<uint>(state, "regS").ToString("X4")
                    + " D=$" + Reflect.Get<uint>(state, "regD").ToString("X4")
                    + " DB=$" + Reflect.Get<uint>(state, "regDB").ToString("X6")
                    + "  " + CpuFlags(state));
            }
            var ppu = Reflect.Get(_master, "CorePPU");
            var ppuState = Reflect.TryCall(ppu, "GetState");
            var io = Reflect.BytesCall(ppu, "GetIORegisters");
            if (ppuState != null)
            {
                var mode = io != null && io.Length > 261 ? io[261] & 7 : -1;
                GUILayout.Label("PPU mode " + mode
                    + "  BG1 scroll " + Reflect.Get<int>(ppuState, "_scroll1X") + "," + Reflect.Get<int>(ppuState, "_scroll1Y")
                    + "  BG2 " + Reflect.Get<int>(ppuState, "_scroll2X") + "," + Reflect.Get<int>(ppuState, "_scroll2Y")
                    + "  BG3 " + Reflect.Get<int>(ppuState, "_scroll3X") + "," + Reflect.Get<int>(ppuState, "_scroll3Y")
                    + "  BG4 " + Reflect.Get<int>(ppuState, "_scroll4X") + "," + Reflect.Get<int>(ppuState, "_scroll4Y"));
            }
        }

        private void DrawTransport()
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(IsPaused() ? "Resume (F6)" : "Pause (F6)")) TogglePause();
            if (GUILayout.Button("Step frame (F7)")) StepFrame();
            if (GUILayout.Button("Full capture (F8)")) RequestCapture("manual-overlay");
            if (GUILayout.Button((_cpuTrace ? "Stop" : "Start") + " CPU trace (F9)"))
            {
                _cpuTrace = !_cpuTrace;
                SyncDynamicPatches();
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            _ppuTrace = GUILayout.Toggle(_ppuTrace, "PPU register trace ($2100-$21FF / DMA)");
            if (GUILayout.Button("Open session folder", GUILayout.Width(160)))
            {
                try { Application.OpenURL("file:///" + _session.Root.Replace('\\', '/')); } catch { }
            }
            GUILayout.EndHorizontal();
            SyncDynamicPatches();
        }

        private void DrawWidescreen()
        {
            GUILayout.Label("Live widescreen and layer controls");
            var renderer = Reflect.Get(_master, "snesRenderer");
            var settings = CurrentSettings();
            if (settings == null)
            {
                GUILayout.Label("Game-specific settings are unavailable until a ROM is loaded.");
                return;
            }
            GUILayout.BeginHorizontal();
            var wide = Reflect.Get<bool>(settings, "widescreenOverride");
            var changedWide = GUILayout.Toggle(wide, "Override enabled");
            if (changedWide != wide) SetSetting(settings, "widescreenOverride", changedWide);
            if (GUILayout.Button("Apply DKC baseline", GUILayout.Width(150))) ApplyDkcBaseline(settings);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            DrawIntSetting(settings, "wideScreenBG", "BG");
            DrawIntSetting(settings, "widescreenOBJ", "OBJ");
            DrawIntSetting(settings, "widescreenM7", "Mode 7");
            DrawIntSetting(settings, "widescreenCOL", "Color");
            GUILayout.EndHorizontal();

            if (renderer != null)
            {
                GUILayout.BeginHorizontal();
                DrawBoolMember(renderer, "disableBG1", "Hide BG1");
                DrawBoolMember(renderer, "disableBG2", "Hide BG2");
                DrawBoolMember(renderer, "disableBG3", "Hide BG3");
                DrawBoolMember(renderer, "disableBG4", "Hide BG4");
                DrawBoolMember(renderer, "disableObj", "Hide sprites");
                DrawBoolMember(renderer, "disableWin", "Hide windows");
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                DrawIntMember(renderer, "DebugLineStart", "First line", 0, 255);
                DrawIntMember(renderer, "DebugLineEnd", "Last line", 0, 256);
                DrawIntMember(renderer, "enableObjNo", "Sprite #", -1, 127);
                DrawIntMember(renderer, "priDis", "Priority", -1, 15);
                GUILayout.EndHorizontal();
            }
        }

        private void DrawDefinitions()
        {
            GUILayout.Label("Watches, breakpoints, and trace filters (24-bit SNES hex; ranges use '-')");
            LabeledTextField("Value watches", ref _watchText, "7E1234:s16:camera_x, 7E5678:u8:level");
            LabeledTextField("Execute breakpoints", ref _executeText, "80ABCD, 81C000-81C0FF");
            LabeledTextField("CPU trace PC filter", ref _traceFilterText, "empty = all instructions");
            LabeledTextField("Write watchpoints", ref _writeText, "7E0000-7E1FFF");
            LabeledTextField("Read watchpoints", ref _readText, "expensive; keep ranges narrow");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply definitions")) ApplyDefinitions(true);
            _pauseOnWatchChange.Value = GUILayout.Toggle(_pauseOnWatchChange.Value, "Pause on polled value change");
            _captureOnBreakpoint.Value = GUILayout.Toggle(_captureOnBreakpoint.Value, "Capture on breakpoint");
            GUILayout.EndHorizontal();

            var ram = GetRam();
            foreach (var watch in _watches)
            {
                ulong value;
                if (TryReadWatch(ram, watch, out value))
                    GUILayout.Label(watch.Name + "  $" + watch.Address.ToString("X6") + "  " + watch.Format(value));
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("Memory poke", GUILayout.Width(145));
            _pokeAddressText = GUILayout.TextField(_pokeAddressText, GUILayout.Width(90));
            _pokeValueText = GUILayout.TextField(_pokeValueText, GUILayout.Width(45));
            if (GUILayout.Button("Write byte", GUILayout.Width(90))) PokeByte();
            GUILayout.Label("Use while paused; values are 24-bit address + byte hex.");
            GUILayout.EndHorizontal();
        }

        private void DrawSearch()
        {
            GUILayout.Label("WRAM value search (Cheat-Engine-style, byte precision)");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("New unknown scan"))
            {
                var ram = GetRam();
                if (ram != null) { _search.Reset(ram); _status = "Unknown scan started with " + _search.CandidateCount + " candidates"; }
            }
            GUILayout.Label("Exact hex", GUILayout.Width(65));
            _exactValueText = GUILayout.TextField(_exactValueText, GUILayout.Width(50));
            if (GUILayout.Button("New exact scan")) SearchExact();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Changed")) FilterSearch(SearchComparison.Changed);
            if (GUILayout.Button("Unchanged")) FilterSearch(SearchComparison.Unchanged);
            if (GUILayout.Button("Increased")) FilterSearch(SearchComparison.Increased);
            if (GUILayout.Button("Decreased")) FilterSearch(SearchComparison.Decreased);
            if (GUILayout.Button("Exact")) FilterSearch(SearchComparison.Exact);
            GUILayout.EndHorizontal();
            GUILayout.Label(_search.Active ? "Candidates: " + _search.CandidateCount : "No active search");
            var currentRam = GetRam();
            if (currentRam != null && _search.Active)
            {
                var sample = _search.Results(24).Select(index => "$" + WramAddress(index).ToString("X6") + "=$" + currentRam[index].ToString("X2"));
                GUILayout.Label(string.Join("   ", sample.ToArray()));
            }
        }

        private void PatchFrameHook()
        {
            if (_framePatched || _harmony == null) return;
            var type = Reflect.Type("MasterExecutor");
            var method = type == null ? null : AccessTools.Method(type, "RunFrame", Type.EmptyTypes);
            if (method == null)
            {
                Logger.LogError("MasterExecutor.RunFrame was not found; frame polling will be unavailable.");
                return;
            }
            _harmony.Patch(method, postfix: new HarmonyMethod(AccessTools.Method(typeof(GameHooks), nameof(GameHooks.RunFramePostfix))));
            _framePatched = true;
        }

        private void ResolveDynamicMethods()
        {
            _cpuMethod = Method("CPU65c816", "ExecuteNextInstruction", Type.EmptyTypes);
            _writeMethod = Method("MainMemoryMap", "WriteMem", new[] { typeof(uint), typeof(byte) });
            _readMethod = Method("MainMemoryMap", "ReadMem", new[] { typeof(uint) });
            _ppuWriteMethod = Method("SNESPPU", "WriteIO", new[] { typeof(uint), typeof(byte) });
        }

        private static MethodBase Method(string typeName, string name, Type[] args)
        {
            var type = Reflect.Type(typeName);
            return type == null ? null : AccessTools.Method(type, name, args);
        }

        private void SyncDynamicPatches()
        {
            if (_harmony == null) return;
            if (_cpuMethod == null && _master != null) ResolveDynamicMethods();
            SetPrefixPatch(_cpuMethod, ref _cpuPatched, _cpuTrace || _executeBreakpoints.Count != 0, nameof(GameHooks.CpuInstructionPrefix));
            SetPrefixPatch(_writeMethod, ref _writePatched, _writeRanges.Count != 0, nameof(GameHooks.MemoryWritePrefix));
            SetPostfixPatch(_readMethod, ref _readPatched, _readRanges.Count != 0, nameof(GameHooks.MemoryReadPostfix));
            SetPrefixPatch(_ppuWriteMethod, ref _ppuPatched, _ppuTrace, nameof(GameHooks.PpuWritePrefix));
        }

        private void SetPrefixPatch(MethodBase target, ref bool patched, bool wanted, string hook)
        {
            if (target == null || patched == wanted) return;
            if (wanted) _harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(GameHooks), hook)));
            else _harmony.Unpatch(target, HarmonyPatchType.Prefix, PluginGuid);
            patched = wanted;
        }

        private void SetPostfixPatch(MethodBase target, ref bool patched, bool wanted, string hook)
        {
            if (target == null || patched == wanted) return;
            if (wanted) _harmony.Patch(target, postfix: new HarmonyMethod(AccessTools.Method(typeof(GameHooks), hook)));
            else _harmony.Unpatch(target, HarmonyPatchType.Postfix, PluginGuid);
            patched = wanted;
        }

        internal void OnEmulatedFrame(object master)
        {
            _master = master;
            var frame = Reflect.IntCall(master, "GetFrameNo", -1);
            if (frame != _lastFrame)
            {
                _lastFrame = frame;
                _instructionsThisFrame = 0;
            }
            if (_watches.Count != 0) PollWatches(frame);
            _session.Flush();
        }

        internal void OnCpuInstruction(object cpu)
        {
            if (cpu == null) return;
            var pc = Reflect.UIntCall(cpu, "GetPCAddress") & 0xFFFFFF;
            var hit = AddressParser.Contains(_executeBreakpoints, pc);
            if (hit && _lastBreakpointPc != pc)
            {
                _lastBreakpointPc = pc;
                Break("execute", pc, null);
            }
            else if (!hit)
            {
                _lastBreakpointPc = 0xFFFFFFFF;
            }

            if (!_cpuTrace || (_tracePcRanges.Count != 0 && !AddressParser.Contains(_tracePcRanges, pc))) return;
            if (_instructionsThisFrame++ >= Math.Max(1, _maxInstructionsPerFrame.Value)) return;
            try
            {
                _suppressMemoryHooks = true;
                var master = Reflect.Get(cpu, "masterExecutor") ?? _master;
                var state = Reflect.TryCall(cpu, "GetSaveState");
                var instruction = Convert.ToString(Reflect.TryCall(cpu, "GetDebugOpcodeString"), CultureInfo.InvariantCulture) ?? string.Empty;
                _session.Cpu(TimingCsv(master, cpu) + ","
                    + Reflect.Get<int>(state, "regA") + "," + Reflect.Get<int>(state, "regX") + "," + Reflect.Get<int>(state, "regY") + ","
                    + Reflect.Get<uint>(state, "regS") + "," + Reflect.Get<uint>(state, "regD") + "," + Reflect.Get<uint>(state, "regDB") + ","
                    + Csv(CpuFlags(state)) + "," + Csv(instruction));
            }
            finally { _suppressMemoryHooks = false; }
        }

        internal void OnMemoryWrite(object memory, uint address, byte value)
        {
            address &= 0xFFFFFF;
            if (_suppressMemoryHooks || !AddressParser.Contains(_writeRanges, address)) return;
            var cpu = Reflect.Get(_master, "CPUCore65c816");
            _session.Write(TimingCsv(_master, cpu) + "," + address.ToString("X6") + "," + value.ToString("X2"));
            Break("write", address, value);
        }

        internal void OnMemoryRead(object memory, uint address, byte value)
        {
            address &= 0xFFFFFF;
            if (_suppressMemoryHooks || !AddressParser.Contains(_readRanges, address)) return;
            var cpu = Reflect.Get(_master, "CPUCore65c816");
            _session.Read(TimingCsv(_master, cpu) + "," + address.ToString("X6") + "," + value.ToString("X2"));
            Break("read", address, value);
        }

        internal void OnPpuWrite(object ppu, uint address, byte value)
        {
            if (!_ppuTrace) return;
            var cpu = Reflect.Get(_master, "CPUCore65c816");
            _session.Ppu(TimingCsv(_master, cpu) + "," + (address & 0xFFFF).ToString("X4") + "," + value.ToString("X2"));
        }

        private void Break(string kind, uint address, byte? value)
        {
            if (_breakLatched) return;
            _breakLatched = true;
            var suffix = value.HasValue ? " <= $" + value.Value.ToString("X2") : string.Empty;
            AddRecent(kind.ToUpperInvariant() + " breakpoint at $" + address.ToString("X6") + suffix);
            Reflect.TryCall(_master, "PauseGame");
            _session.Event("breakpoint", new Dictionary<string, object>
            {
                { "kind", kind }, { "address", address.ToString("X6") }, { "value", value.HasValue ? value.Value.ToString("X2") : null },
                { "frame", Reflect.IntCall(_master, "GetFrameNo", -1) }, { "line", Reflect.IntCall(_master, "GetLineNo", -1) },
                { "dot", Reflect.IntCall(_master, "GetPixelNo", -1) }
            });
            if (_captureOnBreakpoint.Value) RequestCapture(kind + "-breakpoint-" + address.ToString("X6"));
        }

        private void PollWatches(int frame)
        {
            var ram = GetRam();
            if (ram == null) return;
            foreach (var watch in _watches)
            {
                ulong value;
                if (!TryReadWatch(ram, watch, out value)) continue;
                if (watch.HasLast && watch.LastValue != value)
                {
                    var message = watch.Name + " $" + watch.Address.ToString("X6") + ": " + watch.Format(watch.LastValue) + " -> " + watch.Format(value);
                    AddRecent(message);
                    _session.Event("watch-change", new Dictionary<string, object>
                    {
                        { "frame", frame }, { "name", watch.Name }, { "address", watch.Address.ToString("X6") },
                        { "previous", watch.LastValue }, { "value", value }, { "type", watch.Type.ToString() }
                    });
                    if (_pauseOnWatchChange.Value) Reflect.TryCall(_master, "PauseGame");
                }
                watch.LastValue = value;
                watch.HasLast = true;
            }
        }

        private void ApplyDefinitions(bool persist)
        {
            string error;
            var watches = WatchParser.Parse(_watchText, out error);
            if (error != null) { _status = error; return; }
            var execute = AddressParser.ParseRanges(_executeText, out error);
            if (error != null) { _status = error; return; }
            var trace = AddressParser.ParseRanges(_traceFilterText, out error);
            if (error != null) { _status = error; return; }
            var writes = AddressParser.ParseRanges(_writeText, out error);
            if (error != null) { _status = error; return; }
            var reads = AddressParser.ParseRanges(_readText, out error);
            if (error != null) { _status = error; return; }

            _watches.Clear();
            _watches.AddRange(watches);
            _executeBreakpoints = execute;
            _tracePcRanges = trace;
            _writeRanges = writes;
            _readRanges = reads;
            if (persist)
            {
                _configuredWatches.Value = _watchText;
                _configuredBreakpoints.Value = _executeText;
                Config.Save();
            }
            SyncDynamicPatches();
            _status = "Applied " + _watches.Count + " watches, " + _executeBreakpoints.Count + " execute, "
                + _writeRanges.Count + " write, and " + _readRanges.Count + " read ranges";
            AddRecent(_status);
        }

        private void TogglePause()
        {
            if (_master == null) return;
            if (IsPaused())
            {
                _breakLatched = false;
                Reflect.TryCall(_master, "ResumeGame");
            }
            else Reflect.TryCall(_master, "PauseGame");
        }

        private bool IsPaused()
        {
            return Reflect.Get<bool>(_master, "_gamePaused", false);
        }

        private void StepFrame()
        {
            if (_master == null) return;
            _breakLatched = false;
            Reflect.TryCall(_master, "PauseGame");
            Reflect.TryCall(_master, "StepFrameForward");
        }

        private void RequestCapture(string reason)
        {
            _captureReason = reason;
            _capturePending = true;
            _status = "Capture queued for end of rendered frame";
        }

        private object CurrentSettings()
        {
            var menu = Reflect.Get(_master, "mainMenuManager") ?? Reflect.Static("MainMenuManager", "Instance");
            var settings = Reflect.TryCall(menu, "GetGameSettings", string.Empty);
            if (settings != null) return settings;
            var loader = Reflect.Static("RomLoader", "Instance");
            var info = Reflect.Get(loader, "romInfo");
            var filename = Reflect.Get<string>(info, "filename", string.Empty);
            return string.IsNullOrEmpty(filename) ? null : Reflect.TryCall(menu, "GetGameSettings", filename);
        }

        private void ApplyDkcBaseline(object settings)
        {
            SetSetting(settings, "widescreenOverride", true);
            SetSetting(settings, "wideScreenBG", 7);
            SetSetting(settings, "widescreenOBJ", 7);
            SetSetting(settings, "widescreenM7", 0);
            SetSetting(settings, "widescreenCOL", 0);
            AddRecent("Applied SuperZSNES's DKC widescreen baseline (BG 7, OBJ 7, M7 0, COL 0)");
        }

        private void SetSetting(object settings, string name, object value)
        {
            Reflect.Set(settings, name, value);
            var ppu = Reflect.Get(_master, "CorePPU");
            Reflect.TryCall(ppu, "SetDirty");
        }

        private void DrawIntSetting(object settings, string member, string label)
        {
            var value = Reflect.Get<int>(settings, member);
            GUILayout.Label(label + " " + value, GUILayout.Width(75));
            if (GUILayout.Button("-", GUILayout.Width(25))) SetSetting(settings, member, Math.Max(0, value - 1));
            if (GUILayout.Button("+", GUILayout.Width(25))) SetSetting(settings, member, Math.Min(15, value + 1));
        }

        private void DrawBoolMember(object instance, string member, string label)
        {
            var value = Reflect.Get<bool>(instance, member);
            var changed = GUILayout.Toggle(value, label);
            if (changed != value) Reflect.Set(instance, member, changed);
        }

        private void DrawIntMember(object instance, string member, string label, int minimum, int maximum)
        {
            var value = Reflect.Get<int>(instance, member);
            GUILayout.Label(label + " " + value, GUILayout.Width(90));
            if (GUILayout.Button("-", GUILayout.Width(25))) Reflect.Set(instance, member, Math.Max(minimum, value - 1));
            if (GUILayout.Button("+", GUILayout.Width(25))) Reflect.Set(instance, member, Math.Min(maximum, value + 1));
        }

        private static void LabeledTextField(string label, ref string value, string hint)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(145));
            value = GUILayout.TextField(value ?? string.Empty);
            GUILayout.Label(hint, GUILayout.Width(250));
            GUILayout.EndHorizontal();
        }

        private byte[] GetRam()
        {
            return Reflect.BytesCall(Reflect.Get(_master, "CoreMemoryMap"), "GetRam");
        }

        private static bool TryReadWatch(byte[] ram, MemoryWatch watch, out ulong value)
        {
            value = 0;
            if (ram == null) return false;
            var index = WramIndex(watch.Address);
            if (index < 0 || index + watch.Size > ram.Length) return false;
            for (var i = 0; i < watch.Size; i++) value |= (ulong)ram[index + i] << (i * 8);
            return true;
        }

        private static int WramIndex(uint address)
        {
            var bank = (address >> 16) & 0xFF;
            var offset = (int)(address & 0xFFFF);
            if (bank == 0x7E) return offset;
            if (bank == 0x7F) return 0x10000 + offset;
            if ((bank <= 0x3F || (bank >= 0x80 && bank <= 0xBF)) && offset < 0x2000) return offset;
            return -1;
        }

        private static uint WramAddress(int index)
        {
            return index < 0x10000 ? 0x7E0000u + (uint)index : 0x7F0000u + (uint)(index - 0x10000);
        }

        private void SearchExact()
        {
            byte value;
            if (!byte.TryParse(_exactValueText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value))
            {
                _status = "Exact value must be one byte of hex (00-FF).";
                return;
            }
            var ram = GetRam();
            if (ram != null) { _search.BeginExact(ram, value); _status = "Exact scan: " + _search.CandidateCount + " candidates"; }
        }

        private void PokeByte()
        {
            uint address;
            byte value;
            if (!AddressParser.TryHex(_pokeAddressText, out address)
                || !byte.TryParse(_pokeValueText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value))
            {
                _status = "Memory poke requires a 24-bit hex address and a byte from 00 to FF.";
                return;
            }
            var memory = Reflect.Get(_master, "CoreMemoryMap");
            try
            {
                Reflect.Call(memory, "WriteMem", address & 0xFFFFFF, value);
                _status = "Wrote $" + value.ToString("X2") + " to $" + (address & 0xFFFFFF).ToString("X6");
                AddRecent(_status);
            }
            catch (Exception ex) { _status = "Memory poke failed: " + ex.Message; }
        }

        private void FilterSearch(SearchComparison comparison)
        {
            var ram = GetRam();
            if (ram == null) return;
            byte exact = 0;
            if (comparison == SearchComparison.Exact && !byte.TryParse(_exactValueText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out exact))
            {
                _status = "Exact value must be one byte of hex (00-FF).";
                return;
            }
            _search.Filter(ram, comparison, exact);
            _status = comparison + " filter: " + _search.CandidateCount + " candidates";
        }

        private static string CpuFlags(object state)
        {
            if (state == null) return "---------";
            return (Reflect.Get<bool>(state, "flagN") ? "N" : "n")
                + (Reflect.Get<bool>(state, "flagV") ? "V" : "v")
                + (Reflect.Get<bool>(state, "flagM") ? "M" : "m")
                + (Reflect.Get<bool>(state, "flagX") ? "X" : "x")
                + (Reflect.Get<bool>(state, "flagD") ? "D" : "d")
                + (Reflect.Get<bool>(state, "flagI") ? "I" : "i")
                + (Reflect.Get<bool>(state, "flagZ") ? "Z" : "z")
                + (Reflect.Get<bool>(state, "flagC") ? "C" : "c")
                + (Reflect.Get<bool>(state, "flagE") ? "E" : "e");
        }

        private static string TimingCsv(object master, object cpu)
        {
            return Reflect.IntCall(master, "GetFrameNo", -1) + "," + Reflect.IntCall(master, "GetLineNo", -1) + ","
                + Reflect.IntCall(master, "GetPixelNo", -1) + "," + Convert.ToString(Reflect.TryCall(cpu, "GetTotalCycles"), CultureInfo.InvariantCulture) + ","
                + Reflect.UIntCall(cpu, "GetPCAddress", 0).ToString("X6");
        }

        private static string Csv(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
        }

        private void ProcessBridgeRequests()
        {
            if (_bridge == null) return;
            BridgeRequest request;
            var handled = 0;
            while (handled++ < 32 && _bridge.TryDequeue(out request))
            {
                try { request.ResultJson = HandleBridgeCommand(request.Command, request.Arguments); }
                catch (TargetInvocationException ex) { request.Error = ex.InnerException ?? ex; }
                catch (Exception ex) { request.Error = ex; }
                finally { request.Signal(); }
            }
        }

        private string HandleBridgeCommand(string command, IDictionary<string, string> args)
        {
            command = (command ?? string.Empty).Trim().ToLowerInvariant();
            if (command == "ping")
                return Json.Object(new Dictionary<string, object> { { "plugin", PluginName }, { "version", PluginVersion }, { "attached", _master != null } });
            if (command == "get_status") return BridgeStatus();
            if (command == "get_rom_info") return BridgeRomInfo();
            if (command == "load_rom") return BridgeLoadRom(args);
            if (command == "reset")
            {
                RequireLoadedRom();
                _breakLatched = false;
                Reflect.Call(_master, "Reset");
                return BridgeStatus();
            }
            if (command == "save_state")
            {
                RequireLoadedRom();
                var suffix = SafeStateSuffix(Arg(args, "suffix", "-mcp"));
                Reflect.Call(_master, "SaveState", suffix);
                return Json.Object(new Dictionary<string, object> { { "saved", true }, { "suffix", suffix }, { "status", BridgeStatus() } });
            }
            if (command == "load_state")
            {
                RequireLoadedRom();
                var suffix = SafeStateSuffix(Arg(args, "suffix", "-mcp"));
                Reflect.Call(_master, "LoadState", suffix);
                return Json.Object(new Dictionary<string, object> { { "requested", true }, { "suffix", suffix }, { "status", BridgeStatus() } });
            }
            if (command == "load_state_file")
            {
                RequireLoadedRom();
                var path = Path.GetFullPath(RequiredArg(args, "path"));
                if (!File.Exists(path) || Path.GetFileName(path).IndexOf(".szst", StringComparison.OrdinalIgnoreCase) < 0)
                    throw new FileNotFoundException("Save state file was not found or is not an .szst file.", path);
                Reflect.Call(_master, "LoadStateFilename", path);
                return Json.Object(new Dictionary<string, object> { { "requested", true }, { "path", path }, { "status", BridgeStatus() } });
            }
            if (command == "pause") { RequireMaster(); Reflect.TryCall(_master, "PauseGame"); return BridgeStatus(); }
            if (command == "resume") { RequireMaster(); _breakLatched = false; Reflect.TryCall(_master, "ResumeGame"); return BridgeStatus(); }
            if (command == "step_frame") { RequireMaster(); StepFrame(); return BridgeStatus(); }
            if (command == "set_controller") return BridgeSetController(args);
            if (command == "capture")
            {
                RequireMaster();
                var path = _capture.Capture(_master, Arg(args, "reason", "mcp"));
                return Json.Object(new Dictionary<string, object> { { "path", path }, { "frame", Reflect.IntCall(_master, "GetFrameNo", -1) } });
            }
            if (command == "screenshot")
            {
                RequireMaster();
                var shot = _capture.Screenshot(_master, Arg(args, "target", "main"), Arg(args, "format", "png"), IntArg(args, "quality", 85));
                return Json.Object(new Dictionary<string, object>
                {
                    { "mimeType", shot.MimeType }, { "base64", Convert.ToBase64String(shot.Data) }, { "path", shot.Path },
                    { "target", shot.Target }, { "width", shot.Width }, { "height", shot.Height }, { "frame", shot.Frame }
                });
            }
            if (command == "get_cpu_state") return BridgeCpuState();
            if (command == "get_ppu_state") return BridgePpuState();
            if (command == "disassemble_at") return BridgeDisassemble(args);
            if (command == "read_memory") return BridgeReadMemory(args);
            if (command == "write_memory") return BridgeWriteMemory(args);
            if (command == "get_debug_config") return BridgeDebugConfig();
            if (command == "set_debug_config") return BridgeSetDebugConfig(args);
            if (command == "get_watches") return BridgeWatches();
            if (command == "search_begin_unknown") { RequireRam(); _search.Reset(GetRam()); return BridgeSearchResults(args); }
            if (command == "search_begin_exact")
            {
                var value = ByteArg(args, "value");
                _search.BeginExact(RequireRam(), value);
                return BridgeSearchResults(args);
            }
            if (command == "search_filter")
            {
                SearchComparison comparison;
                if (!Enum.TryParse(Arg(args, "comparison", "changed"), true, out comparison))
                    throw new ArgumentException("comparison must be exact, changed, unchanged, increased, or decreased");
                var value = comparison == SearchComparison.Exact ? ByteArg(args, "value") : (byte)0;
                _search.Filter(RequireRam(), comparison, value);
                return BridgeSearchResults(args);
            }
            if (command == "search_results") return BridgeSearchResults(args);
            if (command == "get_widescreen") return BridgeWidescreen();
            if (command == "set_widescreen") return BridgeSetWidescreen(args);
            if (command == "set_layers") return BridgeSetLayers(args);
            if (command == "set_renderer_debug") return BridgeSetRendererDebug(args);
            if (command == "get_recent_events") return Json.Value(_recent.Lines.ToArray());
            if (command == "list_captures") return BridgeListCaptures();
            throw new ArgumentException("Unknown bridge command: " + command);
        }

        private string BridgeStatus()
        {
            var data = new Dictionary<string, object>
            {
                { "attached", _master != null }, { "paused", IsPaused() }, { "cpuTrace", _cpuTrace }, { "ppuTrace", _ppuTrace },
                { "breakpointLatched", _breakLatched }, { "sessionRoot", _session.Root }
            };
            if (_master != null)
            {
                var cpu = Reflect.Get(_master, "CPUCore65c816");
                data["frame"] = Reflect.IntCall(_master, "GetFrameNo", -1);
                data["line"] = Reflect.IntCall(_master, "GetLineNo", -1);
                data["dot"] = Reflect.IntCall(_master, "GetPixelNo", -1);
                data["pc"] = Reflect.UIntCall(cpu, "GetPCAddress", 0).ToString("X6");
                data["running"] = Reflect.Get<bool>(_master, "_executing", false);
            }
            data["forcedController"] = _forcedControllerIndex + 1;
            data["forcedControllerMask"] = _forcedControllerMask.ToString("X4");
            data["forcedControllerFrames"] = _forcedControllerFrames;
            return Json.Object(data);
        }

        private string BridgeSetController(IDictionary<string, string> args)
        {
            var controller = Math.Max(1, Math.Min(5, IntArg(args, "controller", 1)));
            var frames = Math.Max(0, Math.Min(36000, IntArg(args, "frames", 0)));
            var buttons = Arg(args, "buttons", string.Empty);
            uint mask = 0;
            foreach (var raw in buttons.Split(new[] { ',', '+', ' ', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                switch (raw.Trim().ToUpperInvariant())
                {
                    case "B": mask |= 0x8000; break;
                    case "Y": mask |= 0x4000; break;
                    case "SELECT": case "SEL": case "SL": mask |= 0x2000; break;
                    case "START": case "ST": mask |= 0x1000; break;
                    case "UP": case "U": mask |= 0x0800; break;
                    case "DOWN": case "D": mask |= 0x0400; break;
                    case "LEFT": mask |= 0x0200; break;
                    case "RIGHT": mask |= 0x0100; break;
                    case "A": mask |= 0x0080; break;
                    case "X": mask |= 0x0040; break;
                    case "L": case "LB": mask |= 0x0020; break;
                    case "R": case "RB": mask |= 0x0010; break;
                    default: throw new ArgumentException("Unknown SNES button: " + raw);
                }
            }
            _forcedControllerIndex = controller - 1;
            _forcedControllerMask = frames > 0 ? mask : 0;
            _forcedControllerFrames = frames;
            AddRecent("Forced controller " + controller + " mask $" + _forcedControllerMask.ToString("X4") + " for " + frames + " frames");
            return BridgeStatus();
        }

        private void PatchInputHook()
        {
            if (_harmony == null || _inputPatched) return;
            var type = Reflect.Type("SNESPPU");
            var method = type == null ? null : AccessTools.Method(type, "UpdateInput", Type.EmptyTypes);
            if (method == null) return;
            _harmony.Patch(method, postfix: new HarmonyMethod(typeof(DKCWidescreenDebuggerPlugin), nameof(InputPostfix)));
            _inputPatched = true;
        }

        private static void InputPostfix(object __instance)
        {
            var plugin = Instance;
            if (plugin == null || plugin._forcedControllerFrames <= 0 || plugin._forcedControllerMask == 0) return;
            var controllers = Reflect.Get(__instance, "controller") as uint[];
            if (controllers == null || plugin._forcedControllerIndex < 0 || plugin._forcedControllerIndex >= controllers.Length) return;
            controllers[plugin._forcedControllerIndex] |= plugin._forcedControllerMask;
            plugin._forcedControllerFrames--;
            if (plugin._forcedControllerFrames == 0) plugin._forcedControllerMask = 0;
        }

        private string BridgeRomInfo()
        {
            var loader = Reflect.Static("RomLoader", "Instance");
            var loaded = loader != null && Convert.ToBoolean(Reflect.TryCall(loader, "Loaded") ?? false, CultureInfo.InvariantCulture);
            var info = Reflect.Get(loader, "romInfo");
            return "{\"loaded\":" + (loaded ? "true" : "false") + ",\"info\":" + Reflect.ScalarObjectJson(info) + "}";
        }

        private string BridgeLoadRom(IDictionary<string, string> args)
        {
            RequireMaster();
            var path = Path.GetFullPath(RequiredArg(args, "path"));
            if (!File.Exists(path)) throw new FileNotFoundException("ROM file was not found.", path);
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension != ".zip" && extension != ".smc" && extension != ".sfc" && extension != ".swc" && extension != ".ufo")
                throw new ArgumentException("ROM must be .zip, .smc, .sfc, .swc, or .ufo");
            var loadLastState = args.ContainsKey("load_last_state") && ParseBool(args["load_last_state"]);
            _breakLatched = false;
            var menu = Reflect.Get(_master, "mainMenuManager") ?? Reflect.Static("MainMenuManager", "Instance");
            var accepted = Convert.ToBoolean(Reflect.Call(menu, "LoadGame", path, loadLastState), CultureInfo.InvariantCulture);
            if (!accepted) throw new InvalidOperationException("SuperZSNES rejected the ROM path.");
            var effectiveLoadLastState = Reflect.Get<bool>(menu, "gameLoadLastState", loadLastState);
            Reflect.Call(_master, "LoadRom", path, effectiveLoadLastState);
            Reflect.Set(menu, "gameToLoad", string.Empty);
            return "{\"path\":" + Json.Escape(path) + ",\"rom\":" + BridgeRomInfo() + ",\"status\":" + BridgeStatus() + "}";
        }

        private string BridgeCpuState()
        {
            RequireMaster();
            var cpu = Reflect.Get(_master, "CPUCore65c816");
            var state = Reflect.TryCall(cpu, "GetSaveState");
            _suppressMemoryHooks = true;
            string instruction;
            try { instruction = Convert.ToString(Reflect.TryCall(cpu, "GetDebugOpcodeString"), CultureInfo.InvariantCulture); }
            finally { _suppressMemoryHooks = false; }
            return "{\"timing\":" + BridgeStatus() + ",\"state\":" + Reflect.ScalarObjectJson(state)
                + ",\"flags\":" + Json.Escape(CpuFlags(state)) + ",\"instruction\":" + Json.Escape(instruction) + "}";
        }

        private string BridgePpuState()
        {
            RequireMaster();
            var ppu = Reflect.Get(_master, "CorePPU");
            var renderer = Reflect.Get(_master, "snesRenderer");
            var state = Reflect.TryCall(ppu, "GetState");
            var io = Reflect.BytesCall(ppu, "GetIORegisters");
            var important = new Dictionary<string, object>();
            if (io != null)
            {
                AddRegister(important, io, "INIDISP", 0x100);
                AddRegister(important, io, "BGMODE", 0x105);
                AddRegister(important, io, "BG1SC", 0x107);
                AddRegister(important, io, "BG2SC", 0x108);
                AddRegister(important, io, "BG3SC", 0x109);
                AddRegister(important, io, "BG4SC", 0x10A);
                AddRegister(important, io, "BG12NBA", 0x10B);
                AddRegister(important, io, "BG34NBA", 0x10C);
                AddRegister(important, io, "TM", 0x12C);
                AddRegister(important, io, "TS", 0x12D);
                AddRegister(important, io, "TMW", 0x12E);
                AddRegister(important, io, "TSW", 0x12F);
                AddRegister(important, io, "CGWSEL", 0x130);
                AddRegister(important, io, "CGADSUB", 0x131);
                AddRegister(important, io, "COLDATA", 0x132);
                AddRegister(important, io, "SETINI", 0x133);
            }
            return "{\"timing\":" + BridgeStatus() + ",\"state\":" + Reflect.ScalarObjectJson(state)
                + ",\"importantRegisters\":" + Json.Object(important)
                + ",\"ioRegistersBase64\":" + Json.Escape(io == null ? null : Convert.ToBase64String(io))
                + ",\"renderer\":" + Reflect.ScalarObjectJson(renderer,
                    "numLines", "frameNo", "mode7Perspective", "mode7PerspectiveWrap", "mode7res", "tileScrollXScale",
                    "ratioXL", "ratioXR", "ratioY", "disableBG1", "disableBG2", "disableBG3", "disableBG4", "disableObj", "disableWin",
                    "DebugLineStart", "DebugLineEnd", "enableObjNo", "priDis") + "}";
        }

        private string BridgeDisassemble(IDictionary<string, string> args)
        {
            RequireMaster();
            var address = AddressArg(args, "address");
            var cpu = Reflect.Get(_master, "CPUCore65c816");
            var memory = Reflect.Get(_master, "CoreMemoryMap");
            byte opcode;
            _suppressMemoryHooks = true;
            try { opcode = Convert.ToByte(Reflect.Call(memory, "ReadMem", address), CultureInfo.InvariantCulture); }
            finally { _suppressMemoryHooks = false; }
            var text = Convert.ToString(Reflect.Call(cpu, "DisasmInstruction", opcode, address & 0xFFFF, address & 0xFF0000), CultureInfo.InvariantCulture);
            return Json.Object(new Dictionary<string, object> { { "address", address.ToString("X6") }, { "opcode", opcode.ToString("X2") }, { "instruction", text } });
        }

        private string BridgeReadMemory(IDictionary<string, string> args)
        {
            RequireMaster();
            var address = AddressArg(args, "address");
            var length = Math.Max(1, Math.Min(65536, IntArg(args, "length", 1)));
            var bytes = new byte[length];
            var memory = Reflect.Get(_master, "CoreMemoryMap");
            _suppressMemoryHooks = true;
            try
            {
                for (var i = 0; i < length; i++) bytes[i] = Convert.ToByte(Reflect.Call(memory, "ReadMem", (address + (uint)i) & 0xFFFFFF), CultureInfo.InvariantCulture);
            }
            finally { _suppressMemoryHooks = false; }
            return Json.Object(new Dictionary<string, object>
            {
                { "address", address.ToString("X6") }, { "length", length }, { "hex", BitConverter.ToString(bytes).Replace("-", string.Empty) },
                { "base64", Convert.ToBase64String(bytes) }
            });
        }

        private string BridgeWriteMemory(IDictionary<string, string> args)
        {
            RequireMaster();
            var address = AddressArg(args, "address");
            var bytes = ParseHexBytes(RequiredArg(args, "hex"));
            if (bytes.Length == 0 || bytes.Length > 65536) throw new ArgumentException("hex must contain 1 to 65536 bytes");
            var memory = Reflect.Get(_master, "CoreMemoryMap");
            _suppressMemoryHooks = true;
            try
            {
                for (var i = 0; i < bytes.Length; i++) Reflect.Call(memory, "WriteMem", (address + (uint)i) & 0xFFFFFF, bytes[i]);
            }
            finally { _suppressMemoryHooks = false; }
            return Json.Object(new Dictionary<string, object> { { "address", address.ToString("X6") }, { "bytesWritten", bytes.Length }, { "hex", BitConverter.ToString(bytes).Replace("-", string.Empty) } });
        }

        private string BridgeDebugConfig()
        {
            return Json.Object(new Dictionary<string, object>
            {
                { "watches", _watchText }, { "executeBreakpoints", _executeText }, { "tracePcRanges", _traceFilterText },
                { "writeWatchpoints", _writeText }, { "readWatchpoints", _readText }, { "cpuTrace", _cpuTrace }, { "ppuTrace", _ppuTrace },
                { "pauseOnWatchChange", _pauseOnWatchChange.Value }, { "captureOnBreakpoint", _captureOnBreakpoint.Value },
                { "maxInstructionsPerFrame", _maxInstructionsPerFrame.Value }
            });
        }

        private string BridgeSetDebugConfig(IDictionary<string, string> args)
        {
            SetIfPresent(args, "watches", value => _watchText = value);
            SetIfPresent(args, "execute_breakpoints", value => _executeText = value);
            SetIfPresent(args, "trace_pc_ranges", value => _traceFilterText = value);
            SetIfPresent(args, "write_watchpoints", value => _writeText = value);
            SetIfPresent(args, "read_watchpoints", value => _readText = value);
            SetIfPresent(args, "cpu_trace", value => _cpuTrace = ParseBool(value));
            SetIfPresent(args, "ppu_trace", value => _ppuTrace = ParseBool(value));
            SetIfPresent(args, "pause_on_watch_change", value => _pauseOnWatchChange.Value = ParseBool(value));
            SetIfPresent(args, "capture_on_breakpoint", value => _captureOnBreakpoint.Value = ParseBool(value));
            SetIfPresent(args, "max_instructions_per_frame", value => _maxInstructionsPerFrame.Value = Math.Max(1, int.Parse(value, CultureInfo.InvariantCulture)));
            ApplyDefinitions(true);
            if (_status.StartsWith("Invalid", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException(_status);
            SyncDynamicPatches();
            return BridgeDebugConfig();
        }

        private string BridgeWatches()
        {
            var ram = GetRam();
            var values = new List<object>();
            foreach (var watch in _watches)
            {
                ulong value;
                var readable = TryReadWatch(ram, watch, out value);
                values.Add(new Dictionary<string, object>
                {
                    { "name", watch.Name }, { "address", watch.Address.ToString("X6") }, { "type", watch.Type.ToString() },
                    { "readable", readable }, { "value", readable ? (object)value : null }, { "formatted", readable ? watch.Format(value) : null }
                });
            }
            return Json.Value(values);
        }

        private string BridgeSearchResults(IDictionary<string, string> args)
        {
            var limit = Math.Max(1, Math.Min(4096, IntArg(args, "limit", 128)));
            var offset = Math.Max(0, IntArg(args, "offset", 0));
            var ram = GetRam();
            var results = new List<object>();
            if (ram != null && _search.Active)
            {
                foreach (var index in _search.Results(int.MaxValue).Skip(offset).Take(limit))
                    results.Add(new Dictionary<string, object> { { "address", WramAddress(index).ToString("X6") }, { "value", ram[index] }, { "hex", ram[index].ToString("X2") } });
            }
            return Json.Object(new Dictionary<string, object>
            {
                { "active", _search.Active }, { "candidateCount", _search.CandidateCount }, { "offset", offset }, { "limit", limit }, { "results", results }
            });
        }

        private string BridgeWidescreen()
        {
            RequireMaster();
            var settings = CurrentSettings();
            if (settings == null) throw new InvalidOperationException("No game-specific settings are active. Load a ROM first.");
            return Reflect.ScalarObjectJson(settings);
        }

        private string BridgeSetWidescreen(IDictionary<string, string> args)
        {
            RequireMaster();
            var settings = CurrentSettings();
            if (settings == null) throw new InvalidOperationException("No game-specific settings are active. Load a ROM first.");
            if (args.ContainsKey("dkc_baseline") && ParseBool(args["dkc_baseline"])) ApplyDkcBaseline(settings);
            SetIfPresent(args, "enabled", value => SetSetting(settings, "widescreenOverride", ParseBool(value)));
            SetIfPresent(args, "bg", value => SetSetting(settings, "wideScreenBG", int.Parse(value, CultureInfo.InvariantCulture)));
            SetIfPresent(args, "obj", value => SetSetting(settings, "widescreenOBJ", int.Parse(value, CultureInfo.InvariantCulture)));
            SetIfPresent(args, "mode7", value => SetSetting(settings, "widescreenM7", int.Parse(value, CultureInfo.InvariantCulture)));
            SetIfPresent(args, "color", value => SetSetting(settings, "widescreenCOL", int.Parse(value, CultureInfo.InvariantCulture)));
            SetIfPresent(args, "aspect_override", value => SetSetting(settings, "aspectOverride", int.Parse(value, CultureInfo.InvariantCulture)));
            return BridgeWidescreen();
        }

        private string BridgeSetLayers(IDictionary<string, string> args)
        {
            RequireMaster();
            var renderer = Reflect.Get(_master, "snesRenderer");
            SetIfPresent(args, "bg1_visible", value => Reflect.Set(renderer, "disableBG1", !ParseBool(value)));
            SetIfPresent(args, "bg2_visible", value => Reflect.Set(renderer, "disableBG2", !ParseBool(value)));
            SetIfPresent(args, "bg3_visible", value => Reflect.Set(renderer, "disableBG3", !ParseBool(value)));
            SetIfPresent(args, "bg4_visible", value => Reflect.Set(renderer, "disableBG4", !ParseBool(value)));
            SetIfPresent(args, "sprites_visible", value => Reflect.Set(renderer, "disableObj", !ParseBool(value)));
            SetIfPresent(args, "windows_visible", value => Reflect.Set(renderer, "disableWin", !ParseBool(value)));
            return Reflect.ScalarObjectJson(renderer, "disableBG1", "disableBG2", "disableBG3", "disableBG4", "disableObj", "disableWin");
        }

        private string BridgeSetRendererDebug(IDictionary<string, string> args)
        {
            RequireMaster();
            var renderer = Reflect.Get(_master, "snesRenderer");
            SetIfPresent(args, "first_line", value => Reflect.Set(renderer, "DebugLineStart", int.Parse(value, CultureInfo.InvariantCulture)));
            SetIfPresent(args, "last_line", value => Reflect.Set(renderer, "DebugLineEnd", int.Parse(value, CultureInfo.InvariantCulture)));
            SetIfPresent(args, "sprite_number", value => Reflect.Set(renderer, "enableObjNo", int.Parse(value, CultureInfo.InvariantCulture)));
            SetIfPresent(args, "priority", value => Reflect.Set(renderer, "priDis", int.Parse(value, CultureInfo.InvariantCulture)));
            return Reflect.ScalarObjectJson(renderer, "DebugLineStart", "DebugLineEnd", "enableObjNo", "priDis");
        }

        private string BridgeListCaptures()
        {
            var captures = Directory.GetDirectories(_session.Root, "capture-*")
                .Select(path => new DirectoryInfo(path))
                .OrderByDescending(info => info.CreationTimeUtc)
                .Select(info => (object)new Dictionary<string, object>
                {
                    { "name", info.Name }, { "path", info.FullName }, { "createdUtc", info.CreationTimeUtc.ToString("O", CultureInfo.InvariantCulture) },
                    { "files", info.GetFiles().Select(file => file.Name).OrderBy(name => name).ToArray() }
                }).ToList();
            return Json.Value(captures);
        }

        private void RequireMaster()
        {
            if (_master == null) throw new InvalidOperationException("The debugger is not attached. Launch SuperZSNES and load a ROM.");
        }

        private void RequireLoadedRom()
        {
            RequireMaster();
            var loader = Reflect.Static("RomLoader", "Instance");
            if (loader == null || !Convert.ToBoolean(Reflect.TryCall(loader, "Loaded") ?? false, CultureInfo.InvariantCulture))
                throw new InvalidOperationException("No ROM is loaded.");
        }

        private byte[] RequireRam()
        {
            RequireMaster();
            var ram = GetRam();
            if (ram == null) throw new InvalidOperationException("WRAM is unavailable. Load a ROM first.");
            return ram;
        }

        private static string Arg(IDictionary<string, string> args, string name, string fallback)
        {
            string value;
            return args.TryGetValue(name, out value) ? value : fallback;
        }

        private static string RequiredArg(IDictionary<string, string> args, string name)
        {
            string value;
            if (!args.TryGetValue(name, out value) || string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Missing required argument: " + name);
            return value;
        }

        private static int IntArg(IDictionary<string, string> args, string name, int fallback)
        {
            string value;
            return args.TryGetValue(name, out value) ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;
        }

        private static uint AddressArg(IDictionary<string, string> args, string name)
        {
            uint value;
            var raw = RequiredArg(args, name);
            if (!AddressParser.TryHex(raw, out value)) throw new ArgumentException("Invalid 24-bit SNES address: " + raw);
            return value & 0xFFFFFF;
        }

        private static byte ByteArg(IDictionary<string, string> args, string name)
        {
            var raw = RequiredArg(args, name).Trim();
            if (raw.StartsWith("$")) raw = raw.Substring(1);
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) raw = raw.Substring(2);
            byte value;
            if (!byte.TryParse(raw, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)) throw new ArgumentException(name + " must be one byte of hex (00-FF)");
            return value;
        }

        private static bool ParseBool(string value)
        {
            bool result;
            if (bool.TryParse(value, out result)) return result;
            if (value == "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("on", StringComparison.OrdinalIgnoreCase)) return true;
            if (value == "0" || value.Equals("no", StringComparison.OrdinalIgnoreCase) || value.Equals("off", StringComparison.OrdinalIgnoreCase)) return false;
            throw new ArgumentException("Invalid boolean: " + value);
        }

        private static void SetIfPresent(IDictionary<string, string> args, string name, Action<string> setter)
        {
            string value;
            if (args.TryGetValue(name, out value)) setter(value);
        }

        private static byte[] ParseHexBytes(string text)
        {
            text = (text ?? string.Empty).Replace("0x", string.Empty).Replace("0X", string.Empty)
                .Replace(" ", string.Empty).Replace("-", string.Empty).Replace(":", string.Empty).Replace(",", string.Empty).Replace("_", string.Empty);
            if ((text.Length & 1) != 0) throw new ArgumentException("Hex byte data must contain an even number of digits.");
            var result = new byte[text.Length / 2];
            for (var i = 0; i < result.Length; i++) result[i] = byte.Parse(text.Substring(i * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            return result;
        }

        private static void AddRegister(IDictionary<string, object> values, byte[] io, string name, int index)
        {
            if (index >= 0 && index < io.Length) values[name] = io[index].ToString("X2");
        }

        private static string SafeStateSuffix(string suffix)
        {
            suffix = suffix ?? string.Empty;
            if (suffix.Length > 64 || suffix.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')))
                throw new ArgumentException("State suffix may contain only letters, digits, '-' and '_' and must be at most 64 characters.");
            return suffix;
        }

        private void AddRecent(string message)
        {
            _recent.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + message);
        }
    }

    internal static class GameHooks
    {
        public static void RunFramePostfix(object __instance)
        {
            var plugin = DKCWidescreenDebuggerPlugin.Instance;
            if (plugin != null) plugin.OnEmulatedFrame(__instance);
        }

        public static void CpuInstructionPrefix(object __instance)
        {
            var plugin = DKCWidescreenDebuggerPlugin.Instance;
            if (plugin != null) plugin.OnCpuInstruction(__instance);
        }

        public static void MemoryWritePrefix(object __instance, uint addr, byte val)
        {
            var plugin = DKCWidescreenDebuggerPlugin.Instance;
            if (plugin != null) plugin.OnMemoryWrite(__instance, addr, val);
        }

        public static void MemoryReadPostfix(object __instance, uint addr, byte __result)
        {
            var plugin = DKCWidescreenDebuggerPlugin.Instance;
            if (plugin != null) plugin.OnMemoryRead(__instance, addr, __result);
        }

        public static void PpuWritePrefix(object __instance, uint addr, byte val)
        {
            var plugin = DKCWidescreenDebuggerPlugin.Instance;
            if (plugin != null) plugin.OnPpuWrite(__instance, addr, val);
        }
    }
}
