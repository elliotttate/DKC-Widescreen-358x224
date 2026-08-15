using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace DKCLevelAutomation
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class DKCLevelAutomationPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.dkclevelautomation";
        public const string PluginName = "DKC Level Automation";
        public const string PluginVersion = "0.1.3";

        internal static DKCLevelAutomationPlugin Instance;

        private readonly ControllerSchedule[] _schedules =
        {
            new ControllerSchedule(), new ControllerSchedule(), new ControllerSchedule(),
            new ControllerSchedule(), new ControllerSchedule()
        };

        private ConfigEntry<bool> _bridgeEnabled;
        private ConfigEntry<int> _bridgePort;
        private ConfigEntry<int> _requestTimeoutSeconds;
        private Harmony _harmony;
        private LoopbackBridge _bridge;
        private object _master;
        private ActiveFrameOperation _active;
        private bool _framePatched;
        private bool _inputPatched;
        private bool _hasActiveSchedules;

        private void Awake()
        {
            Instance = this;
            _bridgeEnabled = Config.Bind("Bridge", "Enabled", true, "Expose the authenticated localhost automation bridge.");
            _bridgePort = Config.Bind("Bridge", "Port", 17817, "Loopback TCP port. Falls back to an available port if busy; 0 always selects one.");
            _requestTimeoutSeconds = Config.Bind("Bridge", "RequestTimeoutSeconds", 180, "Maximum wall-clock time for a frame-running bridge request.");

            _harmony = new Harmony(PluginGuid);
            PatchHooks();
            if (_bridgeEnabled.Value)
            {
                var endpoint = Path.Combine(Paths.PluginPath, "DKCLevelAutomation", "bridge.json");
                _bridge = new LoopbackBridge(endpoint, Logger, _requestTimeoutSeconds.Value);
                try { _bridge.Start(_bridgePort.Value); }
                catch (Exception ex) { Logger.LogError("Could not start level automation bridge: " + ex); }
            }
            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded. No emulator process was launched by this plugin.");
        }

        private void OnDestroy()
        {
            CancelActive("Plugin is shutting down.");
            try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
            if (_bridge != null) _bridge.Dispose();
            if (ReferenceEquals(Instance, this)) Instance = null;
        }

        private void Update()
        {
            var current = Reflect.Static("MasterExecutor", "Instance");
            if (current != null && !ReferenceEquals(current, _master))
            {
                _master = current;
                Logger.LogInfo("Attached level automation to MasterExecutor.");
            }
            if (!_framePatched || !_inputPatched) PatchHooks();
            ProcessBridgeRequests();
            DriveActiveOperation();
        }

        private void PatchHooks()
        {
            if (!_framePatched)
            {
                var type = Reflect.Type("MasterExecutor");
                var method = type == null ? null : AccessTools.Method(type, "RunFrame", Type.EmptyTypes);
                if (method != null)
                {
                    _harmony.Patch(method, postfix: new HarmonyMethod(AccessTools.Method(typeof(AutomationHooks), nameof(AutomationHooks.RunFramePostfix))));
                    _framePatched = true;
                }
            }
            if (!_inputPatched)
            {
                var type = Reflect.Type("SNESPPU");
                var method = type == null ? null : AccessTools.Method(type, "UpdateInput", Type.EmptyTypes);
                if (method != null)
                {
                    _harmony.Patch(method, postfix: new HarmonyMethod(AccessTools.Method(typeof(AutomationHooks), nameof(AutomationHooks.UpdateInputPostfix))));
                    _inputPatched = true;
                }
            }
        }

        internal void OnInputSample(object ppu)
        {
            if (!_hasActiveSchedules) return;
            try
            {
                var controllers = Reflect.Get(ppu, "controller") as uint[];
                if (controllers == null) return;
                for (var i = 0; i < _schedules.Length && i < controllers.Length; i++)
                    if (_schedules[i].Enabled) controllers[i] = _schedules[i].SampleAndAdvance();
            }
            catch (Exception ex) { Logger.LogError("Controller automation hook failed: " + ex); }
        }

        internal void OnEmulatedFrame(object master)
        {
            _master = master ?? _master;
            var active = _active;
            if (active == null || !active.AwaitingFrame) return;
            active.AwaitingFrame = false;
            active.AdvancedFrames++;
            try
            {
                if (active.Condition != null)
                {
                    ulong observed;
                    if (active.Condition.Matches(RequireRam(), out observed))
                    {
                        CompleteActive(Json.Object(new Dictionary<string, object>
                        {
                            { "matched", true }, { "framesAdvanced", active.AdvancedFrames },
                            { "observed", observed }, { "observedHex", "0x" + observed.ToString("X") },
                            { "status", StatusData(false) }
                        }));
                        return;
                    }
                    if (active.AdvancedFrames >= active.TargetFrames)
                    {
                        FailActive(new TimeoutException("WRAM condition was not met within " + active.TargetFrames + " emulated frames; last value was 0x" + observed.ToString("X") + "."));
                        return;
                    }
                }
                else if (active.AdvancedFrames >= active.TargetFrames)
                {
                    CompleteActive(Json.Object(new Dictionary<string, object>
                    {
                        { "framesAdvanced", active.AdvancedFrames }, { "status", StatusData(false) }
                    }));
                }
            }
            catch (Exception ex) { FailActive(ex); }
        }

        private void DriveActiveOperation()
        {
            var active = _active;
            if (active == null) return;
            if (DateTime.UtcNow >= active.DeadlineUtc)
            {
                FailActive(new TimeoutException("Frame operation exceeded its wall-clock timeout after advancing " + active.AdvancedFrames + " frames."));
                return;
            }
            if (active.AwaitingFrame) return;
            try
            {
                RequireLoadedRom();
                Reflect.Call(_master, "PauseGame");
                active.AwaitingFrame = true;
                Reflect.Call(_master, "StepFrameForward");
            }
            catch (Exception ex)
            {
                active.AwaitingFrame = false;
                FailActive(ex);
            }
        }

        private void ProcessBridgeRequests()
        {
            if (_bridge == null) return;
            for (var i = 0; i < 16; i++)
            {
                BridgeRequest request;
                if (!_bridge.TryDequeue(out request)) break;
                try
                {
                    var command = (request.Command ?? string.Empty).Trim().ToLowerInvariant();
                    if (command == "status")
                    {
                        request.ResultJson = Json.Object(StatusData());
                        request.Signal();
                        continue;
                    }
                    if (command == "cancel")
                    {
                        var cancelled = _active != null;
                        CancelActive("Cancelled by bridge client.");
                        request.ResultJson = Json.Object(new Dictionary<string, object> { { "cancelled", cancelled }, { "status", StatusData() } });
                        request.Signal();
                        continue;
                    }
                    if (_active != null) throw new InvalidOperationException("Another frame operation is active. Use status or cancel first.");
                    var result = Dispatch(request, command);
                    if (_active == null)
                    {
                        request.ResultJson = result;
                        request.Signal();
                    }
                }
                catch (Exception ex)
                {
                    request.Error = Unwrap(ex);
                    request.Signal();
                }
            }
        }

        private string Dispatch(BridgeRequest request, string command)
        {
            var args = request.Arguments;
            switch (command)
            {
                case "pause":
                    RequireMaster();
                    Reflect.Call(_master, "PauseGame");
                    return Json.Object(StatusData());
                case "resume":
                    RequireMaster();
                    Reflect.Call(_master, "ResumeGame");
                    return Json.Object(StatusData());
                case "load_rom": return LoadRom(args);
                case "load_state": return LoadState(args, false);
                case "load_state_file": return LoadState(args, true);
                case "schedule": return LoadSchedule(args);
                case "clear_schedule": return ClearSchedule(args);
                case "reset_schedule": return ResetSchedule(args);
                case "run_macro":
                {
                    LoadSchedule(args);
                    var controller = ParseController(args);
                    BeginRun(request, _schedules[controller].Length, args);
                    return null;
                }
                case "run_frames":
                case "step_frames":
                {
                    var count = ParseInt(Required(args, "count"), "count", 0, 10000000);
                    if (count == 0) return Json.Object(new Dictionary<string, object> { { "framesAdvanced", 0 }, { "status", StatusData() } });
                    BeginRun(request, count, args);
                    return null;
                }
                case "wait_wram":
                    BeginWait(request, args);
                    return _active == null ? request.ResultJson : null;
                case "read_wram": return ReadWram(args);
                case "snapshot_wram": return SnapshotWram();
                case "write_wram": return WriteWram(args);
                default: throw new ArgumentException("Unknown automation command '" + command + "'.");
            }
        }

        private string LoadRom(IDictionary<string, string> args)
        {
            RequireMaster();
            var path = Path.GetFullPath(Required(args, "path"));
            if (!File.Exists(path)) throw new FileNotFoundException("ROM file was not found.", path);
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension != ".sfc" && extension != ".smc" && extension != ".swc" && extension != ".ufo" && extension != ".zip")
                throw new ArgumentException("Unsupported ROM extension '" + extension + "'.");
            var menu = Reflect.Get(_master, "mainMenuManager") ?? Reflect.Static("MainMenuManager", "Instance");
            if (menu == null) throw new InvalidOperationException("MainMenuManager is unavailable.");
            var loadLast = OptionalBool(args, "load_last_state", false);
            var accepted = Convert.ToBoolean(Reflect.Call(menu, "LoadGame", path, loadLast), CultureInfo.InvariantCulture);
            if (!accepted) throw new InvalidOperationException("SuperZSNES rejected the ROM path.");
            var effectiveLoadLast = Reflect.Get<bool>(menu, "gameLoadLastState", loadLast);
            Reflect.Call(_master, "LoadRom", path, effectiveLoadLast);
            Reflect.Set(menu, "gameToLoad", string.Empty);
            Reflect.Call(_master, "PauseGame");
            ClearAllSchedules();
            return Json.Object(new Dictionary<string, object> { { "loaded", true }, { "path", path }, { "paused", true }, { "schedulesCleared", true }, { "status", StatusData() } });
        }

        private string LoadState(IDictionary<string, string> args, bool file)
        {
            RequireLoadedRom();
            Reflect.Call(_master, "PauseGame");
            if (file)
            {
                var path = Path.GetFullPath(Required(args, "path"));
                if (!File.Exists(path)) throw new FileNotFoundException("Save-state file was not found.", path);
                Reflect.Call(_master, "LoadStateFilename", path);
            }
            else
            {
                var suffix = Optional(args, "suffix", string.Empty);
                if (suffix.Length > 128 || suffix.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
                    throw new ArgumentException("State suffix contains invalid path characters or is too long.");
                Reflect.Call(_master, "LoadState", suffix);
            }
            Reflect.Call(_master, "PauseGame");
            ClearAllSchedules();
            return Json.Object(new Dictionary<string, object> { { "loaded", true }, { "paused", true }, { "schedulesCleared", true }, { "status", StatusData() } });
        }

        private string LoadSchedule(IDictionary<string, string> args)
        {
            RequireLoadedRom();
            Reflect.Call(_master, "PauseGame");
            var controller = ParseController(args);
            _schedules[controller].Load(Required(args, "macro"));
            _hasActiveSchedules = true;
            return Json.Object(new Dictionary<string, object>
            {
                { "controller", controller + 1 }, { "length", _schedules[controller].Length },
                { "cursor", _schedules[controller].Cursor }, { "exactOverride", true }
            });
        }

        private string ClearSchedule(IDictionary<string, string> args)
        {
            string raw;
            if (!args.TryGetValue("controller", out raw) || string.IsNullOrWhiteSpace(raw) || raw.Equals("all", StringComparison.OrdinalIgnoreCase))
                ClearAllSchedules();
            else _schedules[ParseController(args)].Clear();
            RefreshActiveSchedules();
            return Json.Object(new Dictionary<string, object> { { "status", StatusData() } });
        }

        private string ResetSchedule(IDictionary<string, string> args)
        {
            string raw;
            if (!args.TryGetValue("controller", out raw) || string.IsNullOrWhiteSpace(raw) || raw.Equals("all", StringComparison.OrdinalIgnoreCase))
                foreach (var schedule in _schedules) if (schedule.Enabled) schedule.Reset();
            else _schedules[ParseController(args)].Reset();
            RefreshActiveSchedules();
            return Json.Object(new Dictionary<string, object> { { "status", StatusData() } });
        }

        private void BeginRun(BridgeRequest request, int frames, IDictionary<string, string> args)
        {
            RequireLoadedRom();
            Reflect.Call(_master, "PauseGame");
            _active = new ActiveFrameOperation
            {
                Request = request, Kind = "run_frames", TargetFrames = frames,
                DeadlineUtc = DateTime.UtcNow.AddMilliseconds(OperationTimeout(args))
            };
        }

        private void BeginWait(BridgeRequest request, IDictionary<string, string> args)
        {
            RequireLoadedRom();
            var size = ParseInt(Optional(args, "size", "1"), "size", 1, 4);
            var offset = Wram.ParseOffset(Required(args, "address"), size);
            var op = Optional(args, "op", "eq").Trim().ToLowerInvariant();
            if (op != "eq" && op != "ne" && op != "lt" && op != "le" && op != "gt" && op != "ge")
                throw new ArgumentException("op must be eq, ne, lt, le, gt, or ge.");
            var signed = OptionalBool(args, "signed", false);
            var fullMask = Wram.FullMask(size);
            var mask = args.ContainsKey("mask") ? Wram.ParseUnsigned(args["mask"]) : fullMask;
            if (mask > fullMask) throw new ArgumentOutOfRangeException("mask", "Mask does not fit in the requested WRAM size.");
            var condition = new WramCondition
            {
                Offset = offset, Size = size, Operator = op,
                Expected = Wram.ParseSizedValue(Required(args, "value"), size, signed),
                Mask = mask, Signed = signed
            };
            ulong observed;
            if (condition.Matches(RequireRam(), out observed))
            {
                request.ResultJson = Json.Object(new Dictionary<string, object>
                {
                    { "matched", true }, { "framesAdvanced", 0 }, { "observed", observed },
                    { "observedHex", "0x" + observed.ToString("X") }, { "status", StatusData() }
                });
                return;
            }
            var maxFrames = ParseInt(Optional(args, "max_frames", "3600"), "max_frames", 1, 10000000);
            Reflect.Call(_master, "PauseGame");
            _active = new ActiveFrameOperation
            {
                Request = request, Kind = "wait_wram", TargetFrames = maxFrames,
                Condition = condition, DeadlineUtc = DateTime.UtcNow.AddMilliseconds(OperationTimeout(args))
            };
        }

        private string ReadWram(IDictionary<string, string> args)
        {
            RequireLoadedRom();
            var size = ParseInt(Optional(args, "size", "1"), "size", 1, 4);
            var offset = Wram.ParseOffset(Required(args, "address"), size);
            var raw = Wram.ReadUnsigned(RequireRam(), offset, size);
            var signed = OptionalBool(args, "signed", false);
            return Json.Object(new Dictionary<string, object>
            {
                { "address", "0x" + (0x7E0000 + offset).ToString("X6") }, { "size", size },
                { "value", signed ? (object)Wram.ToSigned(raw, size) : raw }, { "valueHex", "0x" + raw.ToString("X" ) }
            });
        }

        private string SnapshotWram()
        {
            RequireLoadedRom();
            var source = RequireRam();
            var snapshot = new byte[0x20000];
            Buffer.BlockCopy(source, 0, snapshot, 0, snapshot.Length);
            byte[] digest;
            using (var sha256 = SHA256.Create()) digest = sha256.ComputeHash(snapshot);
            return Json.Object(new Dictionary<string, object>
            {
                { "address", "0x7E0000" }, { "size", snapshot.Length },
                { "encoding", "base64" }, { "data", Convert.ToBase64String(snapshot) },
                { "sha256", BitConverter.ToString(digest).Replace("-", string.Empty) },
                { "frame", _master == null ? -1 : Reflect.IntCall(_master, "GetFrameNo", -1) },
                { "paused", _master != null && Reflect.Get<bool>(_master, "_gamePaused", false) }
            });
        }

        private string WriteWram(IDictionary<string, string> args)
        {
            RequireLoadedRom();
            var size = ParseInt(Optional(args, "size", "1"), "size", 1, 4);
            var offset = Wram.ParseOffset(Required(args, "address"), size);
            var value = Wram.ParseUnsigned(Required(args, "value"));
            Reflect.Call(_master, "PauseGame");
            Wram.WriteUnsigned(RequireRam(), offset, size, value);
            return ReadWram(args);
        }

        private IDictionary<string, object> StatusData(bool includeActive = true)
        {
            var loader = Reflect.Static("RomLoader", "Instance");
            var loaded = loader != null && Reflect.BoolCall(loader, "Loaded", false);
            var menu = Reflect.Static("MainMenuManager", "Instance");
            var schedules = new List<object>();
            for (var i = 0; i < _schedules.Length; i++)
            {
                var schedule = _schedules[i];
                schedules.Add(new Dictionary<string, object>
                {
                    { "controller", i + 1 }, { "enabled", schedule.Enabled },
                    { "cursor", schedule.Cursor }, { "length", schedule.Length }
                });
            }
            var data = new Dictionary<string, object>
            {
                { "attached", _master != null }, { "loaded", loaded },
                { "paused", _master != null && Reflect.Get<bool>(_master, "_gamePaused", false) },
                { "frame", _master == null ? -1 : Reflect.IntCall(_master, "GetFrameNo", -1) },
                { "rom", menu == null ? string.Empty : Convert.ToString(Reflect.TryCall(menu, "GetLoadedGameFilename"), CultureInfo.InvariantCulture) },
                { "frameHook", _framePatched }, { "inputHook", _inputPatched }, { "schedules", schedules }
            };
            if (includeActive && _active != null)
            {
                data["active"] = new Dictionary<string, object>
                {
                    { "kind", _active.Kind }, { "advancedFrames", _active.AdvancedFrames },
                    { "targetFrames", _active.TargetFrames }, { "awaitingFrame", _active.AwaitingFrame }
                };
            }
            else data["active"] = null;
            return data;
        }

        private byte[] RequireRam()
        {
            RequireMaster();
            var memory = Reflect.Get(_master, "CoreMemoryMap");
            var ram = Reflect.Call(memory, "GetRam") as byte[];
            if (ram == null || ram.Length < 0x20000) throw new InvalidOperationException("128 KiB WRAM is unavailable.");
            return ram;
        }

        private void RequireMaster()
        {
            if (_master == null) _master = Reflect.Static("MasterExecutor", "Instance");
            if (_master == null) throw new InvalidOperationException("MasterExecutor is unavailable. Wait for the SuperZSNES main scene.");
        }

        private void RequireLoadedRom()
        {
            RequireMaster();
            var loader = Reflect.Static("RomLoader", "Instance");
            if (loader == null || !Reflect.BoolCall(loader, "Loaded", false)) throw new InvalidOperationException("No ROM is loaded.");
        }

        private void CompleteActive(string resultJson)
        {
            var active = _active;
            _active = null;
            if (active == null) return;
            try { if (_master != null) Reflect.Set(_master, "_progressFrame", false); } catch { }
            active.Request.ResultJson = resultJson;
            active.Request.Signal();
        }

        private void FailActive(Exception error)
        {
            var active = _active;
            _active = null;
            if (active == null) return;
            try { if (_master != null) Reflect.Set(_master, "_progressFrame", false); } catch { }
            active.Request.Error = Unwrap(error);
            active.Request.Signal();
        }

        private void CancelActive(string reason)
        {
            if (_active != null) FailActive(new OperationCanceledException(reason));
        }

        private void ClearAllSchedules()
        {
            foreach (var schedule in _schedules) schedule.Clear();
            _hasActiveSchedules = false;
        }

        private void RefreshActiveSchedules()
        {
            _hasActiveSchedules = false;
            foreach (var schedule in _schedules)
            {
                if (!schedule.Enabled) continue;
                _hasActiveSchedules = true;
                break;
            }
        }

        private int OperationTimeout(IDictionary<string, string> args)
        {
            var maximum = Math.Max(1000, (_requestTimeoutSeconds.Value - 1) * 1000);
            return ParseInt(Optional(args, "timeout_ms", maximum.ToString(CultureInfo.InvariantCulture)), "timeout_ms", 100, maximum);
        }

        private static int ParseController(IDictionary<string, string> args)
        {
            return ParseInt(Optional(args, "controller", "1"), "controller", 1, 5) - 1;
        }

        private static int ParseInt(string raw, string name, int minimum, int maximum)
        {
            int value;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < minimum || value > maximum)
                throw new ArgumentOutOfRangeException(name, name + " must be between " + minimum + " and " + maximum + ".");
            return value;
        }

        private static bool OptionalBool(IDictionary<string, string> args, string name, bool fallback)
        {
            string raw;
            if (!args.TryGetValue(name, out raw)) return fallback;
            bool value;
            if (!bool.TryParse(raw, out value)) throw new FormatException(name + " must be true or false.");
            return value;
        }

        private static string Required(IDictionary<string, string> args, string name)
        {
            string value;
            if (!args.TryGetValue(name, out value) || string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Missing required argument '" + name + "'.");
            return value;
        }

        private static string Optional(IDictionary<string, string> args, string name, string fallback)
        {
            string value;
            return args.TryGetValue(name, out value) ? value : fallback;
        }

        private static Exception Unwrap(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }
    }

    internal static class AutomationHooks
    {
        public static void RunFramePostfix(object __instance)
        {
            var plugin = DKCLevelAutomationPlugin.Instance;
            if (plugin != null) plugin.OnEmulatedFrame(__instance);
        }

        public static void UpdateInputPostfix(object __instance)
        {
            var plugin = DKCLevelAutomationPlugin.Instance;
            if (plugin != null) plugin.OnInputSample(__instance);
        }
    }
}
