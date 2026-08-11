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

namespace DKCTileStreamTracer
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class DKCTileStreamTracerPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.dkctilestreamtracer";
        public const string PluginName = "DKC Tile Stream Tracer";
        public const string PluginVersion = "0.1.1";

        private static readonly uint[] DefaultTargets =
        {
            0x818705, 0x818711, 0x81883F, 0x818857,
            0x8188A8, 0x8188BD, 0x818DFA, 0x818E06
        };

        internal static DKCTileStreamTracerPlugin Instance;

        private ConfigEntry<bool> _autoArm;
        private ConfigEntry<KeyCode> _toggleKey;
        private ConfigEntry<int> _pcWindowBytes;
        private ConfigEntry<int> _maxRows;
        private ConfigEntry<bool> _captureBus;
        private ConfigEntry<string> _targetText;
        private ConfigEntry<string> _outputRoot;

        private Harmony _harmony;
        private MethodBase _cpuInstructionMethod;
        private MethodBase _ppuWriteMethod;
        private bool _cpuPatched;
        private bool _ppuPatched;
        private object _master;
        private TraceOutput _output;
        private HashSet<uint> _targets = new HashSet<uint>(DefaultTargets);
        private string _controlDirectory;
        private string _commandPath;
        private string _statusPath;
        private string _lastCommand = "startup";
        private string _lastError = string.Empty;
        private long _sequence;
        private float _nextControlPoll;
        private float _nextStatusWrite;
        private bool _armed;
        private bool _limitReached;

        private void Awake()
        {
            Instance = this;
            _autoArm = Config.Bind("Capture", "AutoArm", false, "Begin a new trace session when the plugin loads.");
            _toggleKey = Config.Bind("Capture", "ToggleHotkey", KeyCode.F11, "Arm or disarm the targeted tracer.");
            _pcWindowBytes = Config.Bind("Capture", "PcWindowBytes", 12, "Trace an instruction when its PC is within this many bytes of a configured target.");
            _maxRows = Config.Bind("Capture", "MaxRowsPerSession", 200000, "Stop a session after this many combined PC and bus rows.");
            _captureBus = Config.Bind("Capture", "CapturePpuAndDmaWrites", true, "Capture PPU VRAM registers, DMA enable, and DMA channel register writes while armed.");
            _targetText = Config.Bind("Capture", "TargetPCs", "818705,818711,81883F,818857,8188A8,8188BD,818DFA,818E06", "Comma-separated 24-bit SNES PCs.");
            _outputRoot = Config.Bind("Paths", "OutputRoot", string.Empty, "Trace directory. Empty uses BepInEx/plugins/DKCTileStreamTracer/Traces.");

            ParseTargets();
            _controlDirectory = Path.Combine(Paths.PluginPath, "DKCTileStreamTracer", "control");
            _commandPath = Path.Combine(_controlDirectory, "command.txt");
            _statusPath = Path.Combine(_controlDirectory, "status.json");
            Directory.CreateDirectory(_controlDirectory);

            _harmony = new Harmony(PluginGuid);
            _cpuInstructionMethod = ResolveMethod("CPU65c816", "ExecuteNextInstruction", Type.EmptyTypes);
            _ppuWriteMethod = ResolveMethod("SNESPPU", "WriteIO", new[] { typeof(uint), typeof(byte) });
            PatchPostfix("MasterExecutor", "RunFrame", Type.EmptyTypes, nameof(TracerHooks.FramePostfix));

            if (_autoArm.Value) Arm("auto");
            WriteStatus();
            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded; F11 toggles capture. Control: " + _commandPath);
        }

        private void OnDestroy()
        {
            Disarm("shutdown");
            try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
            try { WriteStatus(); } catch { }
            if (ReferenceEquals(Instance, this)) Instance = null;
        }

        private void Update()
        {
            var current = R.Static("MasterExecutor", "Instance");
            if (current != null) _master = current;
            if (Input.GetKeyDown(_toggleKey.Value))
            {
                if (_armed) Disarm("hotkey"); else Arm("hotkey");
            }
            if (Time.unscaledTime >= _nextControlPoll)
            {
                _nextControlPoll = Time.unscaledTime + 0.10f;
                ProcessCommandFile();
            }
        }

        private static MethodBase ResolveMethod(string typeName, string methodName, Type[] args)
        {
            var type = R.Type(typeName);
            var method = type == null ? null : AccessTools.Method(type, methodName, args);
            if (method == null) throw new MissingMethodException(typeName, methodName);
            return method;
        }

        private void SetHotPatches(bool enabled)
        {
            SetPrefixPatch(_cpuInstructionMethod, ref _cpuPatched, enabled, nameof(TracerHooks.CpuPrefix));
            SetPrefixPatch(_ppuWriteMethod, ref _ppuPatched, enabled && _captureBus.Value, nameof(TracerHooks.PpuWritePrefix));
        }

        private void SetPrefixPatch(MethodBase target, ref bool patched, bool wanted, string hook)
        {
            if (target == null || patched == wanted) return;
            if (wanted)
                _harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(TracerHooks), hook)));
            else
                _harmony.Unpatch(target, HarmonyPatchType.Prefix, PluginGuid);
            patched = wanted;
        }

        private void PatchPostfix(string typeName, string methodName, Type[] args, string postfixName)
        {
            var type = R.Type(typeName);
            var method = type == null ? null : AccessTools.Method(type, methodName, args);
            if (method == null) throw new MissingMethodException(typeName, methodName);
            _harmony.Patch(method, postfix: new HarmonyMethod(AccessTools.Method(typeof(TracerHooks), postfixName)));
        }

        private void ParseTargets()
        {
            var parsed = new HashSet<uint>();
            foreach (var token in (_targetText.Value ?? string.Empty).Split(','))
            {
                uint address;
                var clean = token.Trim().Replace("$", string.Empty).Replace("0x", string.Empty).Replace("0X", string.Empty);
                if (uint.TryParse(clean, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address)) parsed.Add(address & 0xFFFFFF);
            }
            if (parsed.Count != 0) _targets = parsed;
        }

        private uint NearestTarget(uint pc, out int delta)
        {
            uint nearest = 0;
            var best = int.MaxValue;
            foreach (var target in _targets)
            {
                if ((pc & 0xFF0000) != (target & 0xFF0000)) continue;
                var difference = unchecked((int)(pc & 0xFFFF) - (int)(target & 0xFFFF));
                if (Math.Abs(difference) < Math.Abs(best)) { best = difference; nearest = target; }
            }
            delta = best;
            return nearest;
        }

        internal void OnCpuInstruction(object cpu)
        {
            if (!_armed || _output == null || cpu == null || AtLimit()) return;
            var pc = R.UIntCall(cpu, "GetPCAddress") & 0xFFFFFF;
            int delta;
            var target = NearestTarget(pc, out delta);
            if (target == 0 || Math.Abs(delta) > Math.Max(0, _pcWindowBytes.Value)) return;

            try
            {
                var master = R.Get(cpu, "masterExecutor") ?? _master;
                if (master != null) _master = master;
                var state = R.Call(cpu, "GetSaveState");
                var row = CommonRow(master, pc);
                row["target"] = target.ToString("X6");
                row["delta"] = delta;
                row["a"] = Hex(R.Get<int>(state, "regA"), 4);
                row["x"] = Hex(R.Get<int>(state, "regX"), 4);
                row["y"] = Hex(R.Get<int>(state, "regY"), 4);
                row["s"] = Hex(R.Get<uint>(state, "regS"), 4);
                row["d"] = Hex(R.Get<uint>(state, "regD"), 4);
                row["db"] = Hex(R.Get<uint>(state, "regDB"), 6);
                row["pb"] = Hex(R.Get<uint>(state, "regPB"), 6);
                row["flags"] = Flags(state);
                row["cycles"] = R.Get<long>(state, "totalCycles") + R.Get<int>(state, "numCycles");
                row["opcode"] = Convert.ToString(R.Call(cpu, "GetDebugOpcodeString"), CultureInfo.InvariantCulture) ?? string.Empty;
                AddWram(row, master);
                AddPpuDma(row, master);
                _output.Pc(row);
            }
            catch (Exception ex) { Fail("CPU trace row failed", ex); }
        }

        internal void OnPpuWrite(object ppu, uint address, byte value)
        {
            if (!_armed || !_captureBus.Value || _output == null || ppu == null || AtLimit()) return;
            address &= 0xFFFF;
            var isVram = address >= 0x2115 && address <= 0x2119;
            var isDmaEnable = address == 0x420B || address == 0x420C;
            var isDmaRegister = address >= 0x4300 && address <= 0x437A && (address & 0xF) <= 0xA;
            if (!isVram && !isDmaEnable && !isDmaRegister) return;
            try
            {
                var master = R.Get(ppu, "masterExecutor") ?? _master;
                if (master != null) _master = master;
                var cpu = R.Get(master, "CPUCore65c816");
                var row = CommonRow(master, R.UIntCall(cpu, "GetPCAddress") & 0xFFFFFF);
                row["kind"] = isVram ? "vram" : (isDmaEnable ? "dma-enable" : "dma-register");
                row["address"] = address.ToString("X4");
                row["value"] = value.ToString("X2");
                AddPpuDma(row, master);
                _output.Bus(row);
            }
            catch (Exception ex) { Fail("PPU/DMA trace row failed", ex); }
        }

        internal void OnFrame(object master)
        {
            if (master != null) _master = master;
            if (_output != null) _output.Flush();
            if (_armed && AtLimit()) Disarm("row-limit");
            else if (_armed && Time.unscaledTime >= _nextStatusWrite)
            {
                _nextStatusWrite = Time.unscaledTime + 0.25f;
                WriteStatus();
            }
        }

        private Dictionary<string, object> CommonRow(object master, uint pc)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "seq", ++_sequence },
                { "frame", R.IntCall(master, "GetFrameNo", -1) },
                { "line", R.IntCall(master, "GetLineNo", -1) },
                { "dot", R.IntCall(master, "GetPixelNo", -1) },
                { "pc", pc.ToString("X6") }
            };
        }

        private static void AddWram(IDictionary<string, object> row, object master)
        {
            var memory = R.Get(master, "CoreMemoryMap");
            var ram = R.BytesCall(memory, "GetRam");
            row["w088b"] = Word(ram, 0x088B);
            row["w08a3"] = Word(ram, 0x08A3);
            row["w0a75"] = Word(ram, 0x0A75);
            row["w1a5b"] = Word(ram, 0x1A5B);
            row["w1b23"] = Word(ram, 0x1B23);
            row["w1b25"] = Word(ram, 0x1B25);
        }

        private static void AddPpuDma(IDictionary<string, object> row, object master)
        {
            var ppu = R.Get(master, "CorePPU");
            var io = R.BytesCall(ppu, "GetIORegisters");
            var vram = R.BytesCall(ppu, "GetPPUMemory");
            var state = R.Call(ppu, "GetState");
            if (io == null || io.Length < 0x2380)
            {
                row["vmain"] = row["vram_word"] = row["vram_mapped"] = row["vram_byte"] = row["vram_preview"] = string.Empty;
                row["dma_active"] = row["dma_summary"] = string.Empty;
                row["dma_channel"] = -1;
                return;
            }
            var vmain = io[0x115];
            var word = io[0x116] | (io[0x117] << 8);
            var mapped = MapVramWord(word, vmain);
            row["vmain"] = vmain.ToString("X2");
            row["vram_word"] = word.ToString("X4");
            row["vram_mapped"] = mapped.ToString("X4");
            row["vram_byte"] = (mapped * 2).ToString("X5");
            row["vram_preview"] = VramPreview(vram, mapped * 2, 16);
            row["dma_active"] = Hex(R.Get<uint>(state, "_dmaActive"), 2);
            row["dma_channel"] = R.Get<int>(ppu, "activeDMAChannel", -1);
            row["dma_summary"] = DmaSummary(io);
        }

        private static int MapVramWord(int word, byte vmain)
        {
            switch (vmain & 0x0C)
            {
                case 0x04: return ((word & 0xFF00) | ((word & 0xE0) >> 5) | ((word & 0x1F) << 3)) & 0x7FFF;
                case 0x08: return ((word & 0xFE00) | ((word & 0x1C0) >> 6) | ((word & 0x3F) << 3)) & 0x7FFF;
                case 0x0C: return ((word & 0xFC00) | ((word & 0x380) >> 7) | ((word & 0x7F) << 3)) & 0x7FFF;
                default: return word & 0x7FFF;
            }
        }

        private static string VramPreview(byte[] vram, int byteAddress, int count)
        {
            if (vram == null || vram.Length == 0) return string.Empty;
            var bytes = new byte[count];
            for (var i = 0; i < count; i++) bytes[i] = vram[(byteAddress + i) % vram.Length];
            return BitConverter.ToString(bytes).Replace("-", string.Empty);
        }

        private static string DmaSummary(byte[] io)
        {
            var entries = new List<string>();
            for (var channel = 0; channel < 8; channel++)
            {
                var offset = 0x2300 + channel * 0x10;
                if (offset + 6 >= io.Length) break;
                var control = io[offset];
                var dest = 0x2100 | io[offset + 1];
                var source = (io[offset + 4] << 16) | (io[offset + 3] << 8) | io[offset + 2];
                var remaining = (io[offset + 6] << 8) | io[offset + 5];
                if (control != 0 || dest != 0x2100 || source != 0 || remaining != 0)
                    entries.Add(channel + ":" + control.ToString("X2") + ":" + dest.ToString("X4") + ":" + source.ToString("X6") + ":" + remaining.ToString("X4"));
            }
            return string.Join("|", entries);
        }

        private static string Flags(object state)
        {
            return string.Concat(
                R.Get<bool>(state, "flagN") ? "N" : "n", R.Get<bool>(state, "flagV") ? "V" : "v",
                R.Get<bool>(state, "flagM") ? "M" : "m", R.Get<bool>(state, "flagX") ? "X" : "x",
                R.Get<bool>(state, "flagD") ? "D" : "d", R.Get<bool>(state, "flagI") ? "I" : "i",
                R.Get<bool>(state, "flagZ") ? "Z" : "z", R.Get<bool>(state, "flagC") ? "C" : "c",
                R.Get<bool>(state, "flagE") ? "E" : "e");
        }

        private static string Word(byte[] ram, int address)
        {
            if (ram == null || address < 0 || address + 1 >= ram.Length) return string.Empty;
            return (ram[address] | (ram[address + 1] << 8)).ToString("X4");
        }

        private static string Hex(long value, int digits) { return value.ToString("X" + digits, CultureInfo.InvariantCulture); }
        private static string Hex(ulong value, int digits) { return value.ToString("X" + digits, CultureInfo.InvariantCulture); }

        private bool AtLimit()
        {
            if (_output == null) return false;
            var limit = Math.Max(1, _maxRows.Value);
            _limitReached = _output.PcRows + _output.BusRows >= limit;
            return _limitReached;
        }

        private void Arm(string reason)
        {
            if (_armed) return;
            ParseTargets();
            _sequence = 0;
            _limitReached = false;
            _lastError = string.Empty;
            var root = string.IsNullOrWhiteSpace(_outputRoot.Value)
                ? Path.Combine(Paths.PluginPath, "DKCTileStreamTracer", "Traces")
                : Path.GetFullPath(_outputRoot.Value);
            var session = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
            _output = new TraceOutput(session);
            _armed = true;
            SetHotPatches(true);
            _lastCommand = "arm:" + reason;
            _output.SessionEvent("session-start", new Dictionary<string, object>
            {
                { "pluginVersion", PluginVersion }, { "targets", _targets.Select(t => t.ToString("X6")).ToArray() },
                { "pcWindowBytes", Math.Max(0, _pcWindowBytes.Value) }, { "reason", reason }
            });
            WriteStatus();
            Logger.LogInfo("DKC tile-stream trace armed: " + session);
        }

        private void Disarm(string reason)
        {
            if (!_armed && _output == null) return;
            _armed = false;
            SetHotPatches(false);
            _lastCommand = "disarm:" + reason;
            var output = _output;
            if (output != null)
            {
                output.SessionEvent("session-stop", new Dictionary<string, object>
                {
                    { "reason", reason }, { "pcRows", output.PcRows }, { "busRows", output.BusRows }
                });
                output.Dispose();
            }
            _output = null;
            WriteStatus();
            Logger.LogInfo("DKC tile-stream trace disarmed (" + reason + ").");
        }

        private void Mark(string text)
        {
            if (_output != null) _output.SessionEvent("mark", new Dictionary<string, object>
            {
                { "text", text ?? string.Empty }, { "frame", R.IntCall(_master, "GetFrameNo", -1) },
                { "line", R.IntCall(_master, "GetLineNo", -1) }, { "dot", R.IntCall(_master, "GetPixelNo", -1) }
            });
            _lastCommand = "mark:" + (text ?? string.Empty);
            WriteStatus();
        }

        private void ProcessCommandFile()
        {
            if (!File.Exists(_commandPath)) return;
            try
            {
                var command = File.ReadAllText(_commandPath).Trim();
                File.Delete(_commandPath);
                if (command.Equals("arm", StringComparison.OrdinalIgnoreCase)) Arm("command-file");
                else if (command.Equals("disarm", StringComparison.OrdinalIgnoreCase)) Disarm("command-file");
                else if (command.Equals("toggle", StringComparison.OrdinalIgnoreCase)) { if (_armed) Disarm("command-file"); else Arm("command-file"); }
                else if (command.Equals("status", StringComparison.OrdinalIgnoreCase)) { _lastCommand = "status"; WriteStatus(); }
                else if (command.StartsWith("mark ", StringComparison.OrdinalIgnoreCase)) Mark(command.Substring(5));
                else throw new ArgumentException("Unknown command. Use arm, disarm, toggle, status, or mark <text>.");
            }
            catch (Exception ex) { Fail("Command failed", ex); WriteStatus(); }
        }

        private void WriteStatus()
        {
            Directory.CreateDirectory(_controlDirectory);
            var rows = _output == null ? 0 : _output.PcRows + _output.BusRows;
            var fields = new List<KeyValuePair<string, object>>
            {
                new KeyValuePair<string, object>("plugin", PluginName),
                new KeyValuePair<string, object>("version", PluginVersion),
                new KeyValuePair<string, object>("armed", _armed),
                new KeyValuePair<string, object>("cpuHook", _cpuPatched),
                new KeyValuePair<string, object>("ppuWriteHook", _ppuPatched),
                new KeyValuePair<string, object>("attached", _master != null),
                new KeyValuePair<string, object>("rows", rows),
                new KeyValuePair<string, object>("pcRows", _output == null ? 0 : _output.PcRows),
                new KeyValuePair<string, object>("busRows", _output == null ? 0 : _output.BusRows),
                new KeyValuePair<string, object>("limitReached", _limitReached),
                new KeyValuePair<string, object>("session", _output == null ? null : _output.DirectoryPath),
                new KeyValuePair<string, object>("lastCommand", _lastCommand),
                new KeyValuePair<string, object>("lastError", string.IsNullOrEmpty(_lastError) ? null : _lastError),
                new KeyValuePair<string, object>("utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))
            };
            var temp = _statusPath + ".tmp";
            File.WriteAllText(temp, JsonLine.Object(fields));
            if (File.Exists(_statusPath)) File.Delete(_statusPath);
            File.Move(temp, _statusPath);
        }

        private void Fail(string context, Exception ex)
        {
            _lastError = context + ": " + ex.Message;
            Logger.LogError(_lastError);
        }
    }

    internal static class TracerHooks
    {
        public static void CpuPrefix(object __instance)
        {
            var plugin = DKCTileStreamTracerPlugin.Instance;
            if (plugin != null) plugin.OnCpuInstruction(__instance);
        }

        public static void PpuWritePrefix(object __instance, uint addr, byte val)
        {
            var plugin = DKCTileStreamTracerPlugin.Instance;
            if (plugin != null) plugin.OnPpuWrite(__instance, addr, val);
        }

        public static void FramePostfix(object __instance)
        {
            var plugin = DKCTileStreamTracerPlugin.Instance;
            if (plugin != null) plugin.OnFrame(__instance);
        }
    }
}
