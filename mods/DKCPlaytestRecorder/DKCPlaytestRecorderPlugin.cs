using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace DKCPlaytestRecorder
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class DKCPlaytestRecorderPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.dkcplaytestrecorder";
        public const string PluginName = "DKC Playtest Recorder";
        public const string PluginVersion = "0.1.0";
        private const string SupportedAssemblySha256 = "33ED627F3A29B5DB82ED8F5CFFC8306CCBACAA2743E1408C976666DC06131DED";

        internal static DKCPlaytestRecorderPlugin Instance;

        private sealed class StateAnchor
        {
            public long Sequence;
            public int EmulatedFrame;
            public MasterExecutor.SNESMemoryState State;
        }

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<int> _historySeconds;
        private ConfigEntry<int> _checkpointSeconds;
        private ConfigEntry<KeyCode> _reportHotkey;
        private ConfigEntry<string> _outputDirectory;
        private Harmony _harmony;
        private TimelineModel _timeline;
        private readonly List<StateAnchor> _anchors = new List<StateAnchor>();
        private readonly Stack<MasterExecutor.SNESMemoryState> _freeStates = new Stack<MasterExecutor.SNESMemoryState>();
        private readonly ushort[] _pendingControllers = new ushort[5];
        private string _attachedRom = string.Empty;
        private string _lastReport = string.Empty;
        private string _lastResetReason = "startup";
        private bool _reportRequested;
        private string _reportNote = string.Empty;
        private float _nextRequestPoll;
        private FieldInfo _pausedField;
        private FieldInfo _controllerField;
        private string _assemblyHash = string.Empty;

        private string PluginDataDirectory => Path.Combine(Paths.PluginPath, "DKCPlaytestRecorder");
        private string RequestPath => Path.Combine(PluginDataDirectory, "report.request");
        private string StatusPath => Path.Combine(PluginDataDirectory, "status.json");

        private void Awake()
        {
            Instance = this;
            _enabled = Config.Bind("Recorder", "Enabled", true, "Keep a rolling deterministic DKC playtest timeline in memory.");
            _historySeconds = Config.Bind("Recorder", "HistorySeconds", 60, "Controller history retained before a report (10-300 seconds).");
            _checkpointSeconds = Config.Bind("Recorder", "CheckpointSeconds", 5, "In-memory full-state interval (1-30 seconds). No disk write occurs until reporting.");
            _reportHotkey = Config.Bind("Recorder", "ReportHotkey", KeyCode.F10, "Write a portable repro bundle for the preceding timeline.");
            _outputDirectory = Config.Bind("Recorder", "OutputDirectory", string.Empty, "Bundle root; blank uses BepInEx/plugins/DKCPlaytestRecorder/Bundles.");
            Directory.CreateDirectory(PluginDataDirectory);
            RebuildTimeline();
            _assemblyHash = Sha256(typeof(MasterExecutor).Assembly.Location);
            if (!string.Equals(_assemblyHash, SupportedAssemblySha256, StringComparison.OrdinalIgnoreCase))
            {
                _enabled.Value = false;
                Logger.LogError("Unsupported Assembly-CSharp.dll. Expected " + SupportedAssemblySha256 + ", got " + _assemblyHash + ". No hooks were installed.");
                WriteStatus("unsupported-emulator-build");
                return;
            }
            _pausedField = AccessTools.Field(typeof(MasterExecutor), "_gamePaused");
            _controllerField = AccessTools.Field(typeof(SNESPPU), "controller");
            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(Assembly.GetExecutingAssembly());
            WriteStatus("loaded");
            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded. Press " + _reportHotkey.Value + " or create report.request to save a repro bundle.");
        }

        private void OnDestroy()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
            WriteStatus("stopped");
            if (ReferenceEquals(Instance, this)) Instance = null;
        }

        private void Update()
        {
            if (!_enabled.Value) return;
            if (Input.GetKeyDown(_reportHotkey.Value)) RequestReport("hotkey:" + _reportHotkey.Value);
            if (Time.unscaledTime < _nextRequestPoll) return;
            _nextRequestPoll = Time.unscaledTime + 0.25f;
            try
            {
                if (!File.Exists(RequestPath)) return;
                var note = File.ReadAllText(RequestPath).Trim();
                File.Delete(RequestPath);
                RequestReport(string.IsNullOrEmpty(note) ? "file-request" : note);
            }
            catch (Exception ex) { Logger.LogWarning("Could not consume report.request: " + ex.Message); }
        }

        private void RebuildTimeline()
        {
            var historyFrames = Math.Max(10, Math.Min(300, _historySeconds == null ? 60 : _historySeconds.Value)) * 60;
            var checkpointFrames = Math.Max(1, Math.Min(30, _checkpointSeconds == null ? 5 : _checkpointSeconds.Value)) * 60;
            _timeline = new TimelineModel(historyFrames, checkpointFrames);
            _anchors.Clear();
        }

        private void RequestReport(string note)
        {
            _reportNote = note ?? string.Empty;
            _reportRequested = true;
            Logger.LogInfo("Repro bundle requested: " + _reportNote);
        }

        internal void CaptureInput(SNESPPU ppu)
        {
            if (!_enabled.Value || ppu == null) return;
            var controllers = _controllerField?.GetValue(ppu) as uint[];
            if (controllers == null) return;
            for (var i = 0; i < _pendingControllers.Length; i++)
                _pendingControllers[i] = i < controllers.Length ? (ushort)controllers[i] : (ushort)0;
        }

        internal void FrameCompleted(MasterExecutor master)
        {
            if (!_enabled.Value || master == null || RomLoader.Instance == null || !RomLoader.Instance.Loaded()) return;
            try
            {
                var rom = RomLoader.Instance.romInfo?.filename ?? string.Empty;
                if (!string.Equals(rom, _attachedRom, StringComparison.OrdinalIgnoreCase))
                {
                    _attachedRom = rom;
                    ResetTimeline("ROM changed");
                }

                var frame = master.GetFrameNo();
                if (_timeline.Record(frame, _pendingControllers))
                {
                    RecycleAnchors();
                    _lastResetReason = "emulated frame discontinuity (state load/reset/rewind)";
                    Logger.LogInfo("Playtest timeline restarted after a frame discontinuity.");
                }

                var last = _anchors.Count == 0 ? 0 : _anchors[_anchors.Count - 1].Sequence;
                if (_timeline.CheckpointDue(last)) CaptureAnchor(master, frame);
                TrimAnchors();
            }
            catch (Exception ex) { Logger.LogError("Could not record emulated frame: " + ex); }
        }

        internal void MasterUpdateCompleted(MasterExecutor master)
        {
            if (!_enabled.Value || !_reportRequested || master == null) return;
            _reportRequested = false;
            try { ExportBundle(master, _reportNote); }
            catch (Exception ex)
            {
                Logger.LogError("Repro bundle export failed: " + ex);
                WriteStatus("report-failed: " + ex.Message);
            }
        }

        internal void TimelineInvalidated(string reason)
        {
            if (!_enabled.Value) return;
            ResetTimeline(reason);
        }

        private void CaptureAnchor(MasterExecutor master, int frame)
        {
            var reused = _freeStates.Count != 0;
            var state = reused ? _freeStates.Pop() : new MasterExecutor.SNESMemoryState();
            if (!reused) state.Initialize(master.uiInterface);
            state.SaveState(master.uiInterface, false, 0f);
            _anchors.Add(new StateAnchor { Sequence = _timeline.Sequence, EmulatedFrame = frame, State = state });
            WriteStatus("checkpoint");
        }

        private void TrimAnchors()
        {
            var maxAnchors = (Math.Max(10, Math.Min(300, _historySeconds.Value)) / Math.Max(1, Math.Min(30, _checkpointSeconds.Value))) + 3;
            while (_anchors.Count > maxAnchors)
            {
                _freeStates.Push(_anchors[0].State);
                _anchors.RemoveAt(0);
            }
        }

        private void ResetTimeline(string reason)
        {
            _timeline.Reset();
            RecycleAnchors();
            _lastResetReason = reason;
            WriteStatus("timeline-reset");
        }

        private void RecycleAnchors()
        {
            foreach (var anchor in _anchors) _freeStates.Push(anchor.State);
            _anchors.Clear();
        }

        private void ExportBundle(MasterExecutor master, string note)
        {
            if (_anchors.Count == 0) throw new InvalidOperationException("No full-state checkpoint exists yet; play at least one emulated frame and report again.");
            var anchor = SelectAnchor();
            var inputs = _timeline.SliceAfter(anchor.Sequence);
            var reportFrame = master.GetFrameNo();
            var wasPaused = _pausedField != null && Convert.ToBoolean(_pausedField.GetValue(master), CultureInfo.InvariantCulture);
            var currentState = new MasterExecutor.SNESMemoryState();
            currentState.Initialize(master.uiInterface);
            master.PauseGame();
            currentState.SaveState(master.uiInterface, false, 0f);
            var reportWram = (byte[])master.CoreMemoryMap.GetRam().Clone();

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            var root = string.IsNullOrWhiteSpace(_outputDirectory.Value)
                ? Path.Combine(PluginDataDirectory, "Bundles")
                : Path.GetFullPath(_outputDirectory.Value);
            Directory.CreateDirectory(root);
            var partial = Path.Combine(root, ".partial-" + stamp + "-f" + reportFrame.ToString("D8", CultureInfo.InvariantCulture));
            var final = Path.Combine(root, stamp + "-f" + reportFrame.ToString("D8", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(partial);

            string tempState = null;
            try
            {
                anchor.State.LoadState(master.uiInterface);
                master.CorePPU.SetDirty();
                var suffix = "-dkc-repro-" + stamp;
                tempState = StatePath(suffix);
                master.SaveState(suffix);
                if (!File.Exists(tempState)) throw new IOException("SuperZSNES did not produce the temporary anchor save state: " + tempState);
                File.Copy(tempState, Path.Combine(partial, "anchor.szst"), true);
            }
            finally
            {
                currentState.LoadState(master.uiInterface);
                master.CorePPU.SetDirty();
                if (!wasPaused) master.ResumeGame();
                if (!string.IsNullOrEmpty(tempState) && File.Exists(tempState))
                {
                    try { File.Delete(tempState); } catch (Exception ex) { Logger.LogWarning("Could not remove temporary anchor state: " + ex.Message); }
                }
            }

            File.WriteAllBytes(Path.Combine(partial, "report.wram.bin"), reportWram);
            WriteInputs(Path.Combine(partial, "inputs.csv"), inputs);
            WriteReplay(Path.Combine(partial, "replay.json"), inputs);
            var romPath = RomLoader.Instance.romInfo.filename;
            var statePath = Path.Combine(partial, "anchor.szst");
            var manifest = new Dictionary<string, object>
            {
                { "schema", "dkc-playtest-repro-v1" },
                { "createdUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
                { "note", note ?? string.Empty },
                { "pluginVersion", PluginVersion },
                { "emulator", "SuperZSNES v0.230 Mono" },
                { "assemblyCSharpSha256", _assemblyHash },
                { "romFileName", Path.GetFileName(romPath) },
                { "romSha256", File.Exists(romPath) ? Sha256(romPath) : string.Empty },
                { "anchorFrame", anchor.EmulatedFrame },
                { "reportFrame", reportFrame },
                { "replayFrames", inputs.Count },
                { "anchorStateSha256", Sha256(statePath) },
                { "reportWramSha256", Sha256(Path.Combine(partial, "report.wram.bin")) },
                { "timelineResetReason", _lastResetReason },
                { "files", new[] { "anchor.szst", "inputs.csv", "replay.json", "report.wram.bin", "manifest.json", "README.txt" } }
            };
            File.WriteAllText(Path.Combine(partial, "manifest.json"), JsonText.Serialize(manifest), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(partial, "README.txt"), BundleReadme(), new UTF8Encoding(false));
            Directory.Move(partial, final);
            var archive = final + ".dkcrepro.zip";
            ZipFile.CreateFromDirectory(final, archive, System.IO.Compression.CompressionLevel.Optimal, false);
            _lastReport = archive;
            WriteStatus("report-complete");
            Logger.LogInfo("Portable DKC repro bundle written to " + archive);
        }

        private StateAnchor SelectAnchor()
        {
            var minimum = _timeline.Sequence - Math.Max(10, Math.Min(300, _historySeconds.Value)) * 60L;
            foreach (var anchor in _anchors)
                if (anchor.Sequence >= minimum) return anchor;
            return _anchors[_anchors.Count - 1];
        }

        private static void WriteInputs(string path, IReadOnlyList<RecordedInput> inputs)
        {
            var text = new StringBuilder("relative_frame,emulated_frame,p1,p2,p3,p4,p5\r\n");
            for (var i = 0; i < inputs.Count; i++)
            {
                var item = inputs[i];
                text.Append(i).Append(',').Append(item.EmulatedFrame);
                for (var c = 0; c < 5; c++) text.Append(",0x").Append(item.Controllers[c].ToString("X4", CultureInfo.InvariantCulture));
                text.Append("\r\n");
            }
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        }

        private static void WriteReplay(string path, IReadOnlyList<RecordedInput> inputs)
        {
            var controllers = new List<object>();
            for (var i = 0; i < 5; i++) controllers.Add(new Dictionary<string, object>
            {
                { "controller", i + 1 }, { "macro", TimelineModel.BuildMacro(inputs, i) }
            });
            File.WriteAllText(path, JsonText.Serialize(new Dictionary<string, object>
            {
                { "schema", "dkc-playtest-replay-v1" }, { "frames", inputs.Count }, { "controllers", controllers }
            }), new UTF8Encoding(false));
        }

        private string StatePath(string suffix)
        {
            var filename = RomLoader.Instance.romInfo.filename;
            var directory = MainMenuManager.Instance.uiInterface.GetSaveDataPathForSave(filename);
            return Path.Combine(directory, Path.GetFileNameWithoutExtension(filename) + ".szst" + suffix);
        }

        private void WriteStatus(string state)
        {
            try
            {
                Directory.CreateDirectory(PluginDataDirectory);
                var status = new Dictionary<string, object>
                {
                    { "pluginVersion", PluginVersion }, { "enabled", _enabled != null && _enabled.Value },
                    { "supportedAssemblyCSharpSha256", SupportedAssemblySha256 }, { "assemblyCSharpSha256", _assemblyHash },
                    { "state", state }, { "rom", _attachedRom }, { "inputFrames", _timeline?.Count ?? 0 },
                    { "checkpoints", _anchors.Count }, { "lastFrame", _timeline?.LastEmulatedFrame ?? -1 },
                    { "lastResetReason", _lastResetReason }, { "lastReport", _lastReport },
                    { "hotkey", _reportHotkey == null ? "F10" : _reportHotkey.Value.ToString() },
                    { "requestFile", RequestPath }
                };
                var temp = StatusPath + ".tmp";
                File.WriteAllText(temp, JsonText.Serialize(status), new UTF8Encoding(false));
                if (File.Exists(StatusPath)) File.Replace(temp, StatusPath, null);
                else File.Move(temp, StatusPath);
            }
            catch (Exception ex) { Logger.LogWarning("Could not write recorder status: " + ex.Message); }
        }

        private static string Sha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
        }

        private static string BundleReadme()
        {
            return "DKC widescreen deterministic playtest repro bundle\r\n\r\n" +
                   "anchor.szst is a normal SuperZSNES save state from before the report.\r\n" +
                   "replay.json contains exact resolved controller input for every following emulated frame.\r\n" +
                   "report.wram.bin is the full 128 KiB WRAM image at the reported endpoint.\r\n\r\n" +
                   "Replay with cli/replay_bundle.py, DKCLevelAutomation v0.1.3+, the exact ROM hash in manifest.json, and this bundle.\r\n" +
                   "The ROM is intentionally not included.\r\n";
        }
    }

    [HarmonyPatch(typeof(SNESPPU), nameof(SNESPPU.UpdateInput))]
    [HarmonyAfter("dev.local.superzsnes.dkclevelautomation")]
    internal static class InputHook
    {
        private static void Postfix(SNESPPU __instance) => DKCPlaytestRecorderPlugin.Instance?.CaptureInput(__instance);
    }

    [HarmonyPatch(typeof(MasterExecutor), "RunFrame")]
    internal static class FrameHook
    {
        private static void Postfix(MasterExecutor __instance) => DKCPlaytestRecorderPlugin.Instance?.FrameCompleted(__instance);
    }

    [HarmonyPatch(typeof(MasterExecutor), "Update")]
    internal static class MasterUpdateHook
    {
        private static void Postfix(MasterExecutor __instance) => DKCPlaytestRecorderPlugin.Instance?.MasterUpdateCompleted(__instance);
    }

    [HarmonyPatch(typeof(MasterExecutor), nameof(MasterExecutor.Reset))]
    internal static class ResetHook
    {
        private static void Postfix() => DKCPlaytestRecorderPlugin.Instance?.TimelineInvalidated("emulator reset");
    }

    [HarmonyPatch(typeof(MasterExecutor), nameof(MasterExecutor.LoadRom), new[] { typeof(string), typeof(bool) })]
    internal static class LoadRomHook
    {
        private static void Postfix() => DKCPlaytestRecorderPlugin.Instance?.TimelineInvalidated("ROM load");
    }

    [HarmonyPatch(typeof(MasterExecutor), nameof(MasterExecutor.LoadState), new[] { typeof(string) })]
    internal static class LoadStateHook
    {
        private static void Postfix() => DKCPlaytestRecorderPlugin.Instance?.TimelineInvalidated("save-state load");
    }

    [HarmonyPatch(typeof(MasterExecutor), nameof(MasterExecutor.LoadStateFilename), new[] { typeof(string) })]
    internal static class LoadStateFilenameHook
    {
        private static void Postfix() => DKCPlaytestRecorderPlugin.Instance?.TimelineInvalidated("save-state file load");
    }
}
