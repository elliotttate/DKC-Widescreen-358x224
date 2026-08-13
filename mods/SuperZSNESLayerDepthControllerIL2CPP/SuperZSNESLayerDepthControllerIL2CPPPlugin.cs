using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace SuperZSNESLayerDepthControllerIL2CPP
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class LayerDepthControllerPlugin : BasePlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.layerdepth.il2cpp";
        public const string PluginName = "SuperZSNES Layer Depth Controller IL2CPP";
        public const string PluginVersion = "0.8.0";
        private Harmony _harmony;
        private NativeTileDepthPatcher _nativeTileDepth;
        private NativeSpriteLoopPatcher _nativeSpriteLoop;
        private ConnectedComponentDepthMapper _componentMapper;

        public override void Load()
        {
            ConfigEntry<bool> enabled = Config.Bind("Controller", "Enabled", false,
                "Enable the hidden SuperZSNES Gimmick3D layer renderer and depth hooks.");
            ConfigEntry<bool> active = Config.Bind("Controller", "ActiveAtStartup", true,
                "Start in 3D mode. F6 toggles it while the game is running.");
            ConfigEntry<float> separation = Config.Bind("Depth", "Separation", 0.5f,
                "Global multiplier for all 13 adjacent priority-plane gaps (0..10).");
            ConfigEntry<bool> compressPriorityPlanes = Config.Bind("Depth",
                "CompressPriorityPlanes", true,
                "Keep SNES priority as a tiny ordering offset instead of turning it into visible scene geometry.");
            ConfigEntry<float> priorityPlaneSpacing = Config.Bind("Depth",
                "PriorityPlaneSpacing", 0.01f,
                "World-space spacing between priority planes while compression is enabled (0.001..0.1).");
            ConfigEntry<int> neutral = Config.Bind("Depth", "NeutralBoundary", 6,
                "Boundary anchored at Z=0, from 0 (backdrop) through 13 (front edge).");
            ConfigEntry<string> gaps = Config.Bind("Depth", "PlaneGaps",
                "1,1,1,1,1,1,1,1,1,1,1,1,1",
                "Thirteen distances: backdrop->P0, P0->P1, ... P11->P12.");
            ConfigEntry<string> scales = Config.Bind("Depth", "PlaneScales",
                "1,1,1,1,1,1,1,1,1,1,1,1,1,1",
                "Fourteen scale multipliers: backdrop, then priority planes P0..P12.");
            ConfigEntry<float> step = Config.Bind("Controls", "GapStep", 0.1f,
                "Amount Ctrl+= and Ctrl+- add to the selected gap.");
            ConfigEntry<float> cameraPitch = Config.Bind("Camera", "InitialPitch", 0f,
                "Initial camera pitch in degrees (-25..25). Mouse drag remains available.");
            ConfigEntry<float> cameraYaw = Config.Bind("Camera", "InitialYaw", 0f,
                "Initial camera yaw in degrees (-25..25). Mouse drag remains available.");
            ConfigEntry<float> cameraZoom = Config.Bind("Camera", "InitialZoom", 0f,
                "Initial hidden-camera zPos (-25..25). Mouse wheel remains available.");
            ConfigEntry<bool> perspectiveCompensation = Config.Bind("Depth",
                "PerspectiveCompensation", true,
                "Scale planes so they remain edge-aligned when viewed straight on.");
            ConfigEntry<bool> connectedComponents = Config.Bind("ConnectedComponents",
                "Enabled", false,
                "Split DKC backgrounds only at transparent boundaries between connected opaque tile groups.");
            ConfigEntry<int> componentDepthBands = Config.Bind("ConnectedComponents",
                "DepthBands", 7,
                "Number of stable automatic component depth bands (1..31).");
            ConfigEntry<float> componentSpacing = Config.Bind("ConnectedComponents",
                "Spacing", 0.08f,
                "World-space distance between adjacent automatic component depth bands (0..1).");
            ConfigEntry<int> minimumComponentTiles = Config.Bind("ConnectedComponents",
                "MinimumTiles", 2,
                "Components smaller than this remain on their stock plane unless a profile overrides them.");
            ConfigEntry<int> maximumAutoComponentTiles = Config.Bind("ConnectedComponents",
                "MaximumAutoTiles", 64,
                "Larger connected scenery remains on its stock plane unless a profile overrides it.");
            ConfigEntry<int> componentRefreshInterval = Config.Bind("ConnectedComponents",
                "RefreshIntervalFrames", 4,
                "Capture the newest streamed tile state this often; classification runs coalesced on one worker.");
            ConfigEntry<bool> removeDuplicateOamPass = Config.Bind("SpriteCohesion",
                "RemoveDuplicateOamPass", true,
                "Render each of the 128 SNES OAM slots once; fixes the stock 129th duplicate exposed by a tilted camera.");
            Config.Save();

            if (!enabled.Value)
            {
                Log.LogInfo(PluginName + " disabled; no Harmony patches applied.");
                return;
            }

            try
            {
                DepthController.Initialize(Log, Config, active, separation,
                    compressPriorityPlanes, priorityPlaneSpacing, neutral, gaps,
                    scales, step, cameraPitch, cameraYaw, cameraZoom,
                    perspectiveCompensation, connectedComponents,
                    Path.Combine(Paths.PluginPath,
                        "SuperZSNESLayerDepthControllerIL2CPP"));
                if (removeDuplicateOamPass.Value)
                {
                    _nativeSpriteLoop = new NativeSpriteLoopPatcher(
                        message => Log.LogWarning(message));
                    _nativeSpriteLoop.Apply(Path.Combine(Paths.GameRootPath,
                        "GameAssembly.dll"));
                    DepthController.SetSpriteLoopStatus(true,
                        _nativeSpriteLoop.AddressHex);
                }
                if (connectedComponents.Value)
                {
                    _nativeTileDepth = new NativeTileDepthPatcher(
                        message => Log.LogWarning(message));
                    _nativeTileDepth.Apply(Path.Combine(Paths.GameRootPath,
                        "GameAssembly.dll"));
                    string statusDirectory = Path.Combine(Paths.PluginPath,
                        "SuperZSNESLayerDepthControllerIL2CPP");
                    _componentMapper = new ConnectedComponentDepthMapper(_nativeTileDepth,
                        Log, componentDepthBands, componentSpacing,
                        minimumComponentTiles, maximumAutoComponentTiles,
                        componentRefreshInterval,
                        statusDirectory);
                    DepthController.SetComponentMapper(_componentMapper);
                    DepthController.SetNativeDetailStatus(true,
                        _nativeTileDepth.ModuleBaseHex,
                        _nativeTileDepth.TrampolineBaseHex,
                        _nativeTileDepth.DepthTableBaseHex);
                }
                _harmony = new Harmony(PluginGuid);
                PatchRequired(typeof(PPURenderer), "GenerateBackgrounds", Type.EmptyTypes,
                    nameof(DepthHooks.GenerateBackgroundsPrefix),
                    nameof(DepthHooks.GenerateBackgroundsPostfix));
                PatchRequired(typeof(PPURenderer), "SetupZPositions", Type.EmptyTypes,
                    null, nameof(DepthHooks.SetupZPositionsPostfix));
                PatchRequired(typeof(MasterExecutor), "Update", Type.EmptyTypes,
                    null, nameof(DepthHooks.UpdatePostfix));
                DepthController.WriteStatus("loaded");
                Log.LogWarning(PluginName + " active. F6 toggles 3D; Ctrl+PageUp/PageDown " +
                               "selects a gap; Ctrl+=/- adjusts it.");
            }
            catch (Exception exception)
            {
                try { _harmony?.UnpatchSelf(); } catch { }
                try { _nativeTileDepth?.Dispose(); } catch { }
                try { _nativeSpriteLoop?.Dispose(); } catch { }
                DepthController.SetSpriteLoopStatus(false, string.Empty);
                DepthController.WriteFailure(exception);
                Log.LogError(PluginName + " failed closed: " + exception);
            }
        }

        public override bool Unload()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
            try { _nativeTileDepth?.Dispose(); } catch { }
            try { _nativeSpriteLoop?.Dispose(); } catch { }
            DepthController.SetSpriteLoopStatus(false, string.Empty);
            DepthController.Shutdown();
            DepthController.WriteStatus("unloaded");
            return true;
        }

        private void PatchRequired(Type type, string name, Type[] parameters,
            string prefixName, string postfixName)
        {
            MethodInfo target = AccessTools.Method(type, name, parameters);
            if (target == null) throw new MissingMethodException(type.FullName, name);
            HarmonyMethod prefix = prefixName == null ? null :
                new HarmonyMethod(typeof(DepthHooks), prefixName) { priority = Priority.First };
            HarmonyMethod postfix = postfixName == null ? null :
                new HarmonyMethod(typeof(DepthHooks), postfixName) { priority = Priority.Last };
            _harmony.Patch(target, prefix, postfix);
            Patches info = Harmony.GetPatchInfo(target);
            if (info == null || (prefix != null && !info.Prefixes.Any(p => p.owner == PluginGuid)) ||
                (postfix != null && !info.Postfixes.Any(p => p.owner == PluginGuid)))
                throw new InvalidOperationException("Harmony did not retain " + type.Name + "." + name);
        }
    }

    internal static class DepthHooks
    {
        public static void GenerateBackgroundsPrefix(PPURenderer __instance,
            ref MainMenuManager.GFXModes __state)
        {
            DepthController.BeginFrame(__instance, ref __state);
        }

        public static void GenerateBackgroundsPostfix(PPURenderer __instance,
            MainMenuManager.GFXModes __state)
        {
            DepthController.EndFrame(__instance, __state);
        }

        public static void SetupZPositionsPostfix(PPURenderer __instance)
        {
            DepthController.Apply(__instance);
        }

        public static void UpdatePostfix()
        {
            DepthController.PollControls();
        }
    }

    public static class LayerDepthAuthoringApi
    {
        public static bool ExportCurrentComponents(string path) =>
            DepthController.ExportCurrentComponents(path);
    }

    internal static class DepthController
    {
        private static ManualLogSource _log;
        private static ConfigFile _config;
        private static ConfigEntry<bool> _activeSetting;
        private static ConfigEntry<float> _separation;
        private static ConfigEntry<bool> _compressPriorityPlanes;
        private static ConfigEntry<float> _priorityPlaneSpacing;
        private static ConfigEntry<int> _neutral;
        private static ConfigEntry<string> _gapsText;
        private static ConfigEntry<string> _scalesText;
        private static ConfigEntry<float> _step;
        private static ConfigEntry<float> _initialPitch;
        private static ConfigEntry<float> _initialYaw;
        private static ConfigEntry<float> _initialZoom;
        private static ConfigEntry<bool> _perspectiveCompensation;
        private static ConfigEntry<bool> _connectedComponents;
        private static ConnectedComponentDepthMapper _componentMapper;
        private static ForegroundGroundOverlay _foregroundGround;
        private static bool _active;
        private static bool _displayModeOverridden;
        private static MainMenuManager.GFXModes _savedDisplayMode;
        private static PPURenderer _renderer;
        private static bool _cameraInitialized;
        private static int _selectedGap;
        private static long _appliedFrames;
        private static string _lastError = string.Empty;
        private static bool _framebufferPatchDetected;
        private static bool _nativeDetailApplied;
        private static string _nativeModuleBase = string.Empty;
        private static string _nativeTrampolineBase = string.Empty;
        private static string _nativeDepthTableBase = string.Empty;
        private static bool _duplicateOamPassRemoved;
        private static string _spriteLoopPatchAddress = string.Empty;
        private static readonly string StatusDirectory = Path.Combine(
            Paths.PluginPath, "SuperZSNESLayerDepthControllerIL2CPP");

        internal static void Initialize(ManualLogSource log, ConfigFile config,
            ConfigEntry<bool> active, ConfigEntry<float> separation,
            ConfigEntry<bool> compressPriorityPlanes,
            ConfigEntry<float> priorityPlaneSpacing,
            ConfigEntry<int> neutral, ConfigEntry<string> gaps,
            ConfigEntry<string> scales, ConfigEntry<float> step,
            ConfigEntry<float> initialPitch, ConfigEntry<float> initialYaw,
            ConfigEntry<float> initialZoom,
            ConfigEntry<bool> perspectiveCompensation,
            ConfigEntry<bool> connectedComponents,
            string pluginDirectory)
        {
            _log = log;
            _config = config;
            _activeSetting = active;
            _separation = separation;
            _compressPriorityPlanes = compressPriorityPlanes;
            _priorityPlaneSpacing = priorityPlaneSpacing;
            _neutral = neutral;
            _gapsText = gaps;
            _scalesText = scales;
            _step = step;
            _initialPitch = initialPitch;
            _initialYaw = initialYaw;
            _initialZoom = initialZoom;
            _perspectiveCompensation = perspectiveCompensation;
            _connectedComponents = connectedComponents;
            _active = active.Value;
            Directory.CreateDirectory(StatusDirectory);
            _foregroundGround = new ForegroundGroundOverlay(pluginDirectory, log);
            MethodInfo backgrounds = AccessTools.Method(typeof(PPURenderer),
                "GenerateBackgrounds", Type.EmptyTypes);
            Patches patches = backgrounds == null ? null : Harmony.GetPatchInfo(backgrounds);
            _framebufferPatchDetected = patches != null && patches.Prefixes.Any(p =>
                p.owner == "dev.local.superzsnes.dkcframebuffer.il2cpp");
        }

        internal static void BeginFrame(PPURenderer renderer,
            ref MainMenuManager.GFXModes originalMode)
        {
            MainMenuManager manager = MainMenuManager.Instance;
            MainMenuManager.MainMenuSettings settings = manager?.mainMenuSettings;
            originalMode = settings == null ? MainMenuManager.GFXModes.None : settings.gfxMode;
            if (!_active || settings == null)
            {
                _componentMapper?.Clear();
                return;
            }

            _componentMapper?.Refresh(renderer);

            if (!_displayModeOverridden)
            {
                _savedDisplayMode = originalMode;
                _displayModeOverridden = true;
            }
            settings.gfxMode = MainMenuManager.GFXModes.Gimmick3D;
            if (_renderer != renderer)
            {
                _renderer = renderer;
                _cameraInitialized = false;
            }
            if (!_cameraInitialized)
            {
                renderer.xRot = Mathf.Clamp(_initialPitch.Value, -25f, 25f);
                renderer.yRot = Mathf.Clamp(_initialYaw.Value, -25f, 25f);
                renderer.zPos = Mathf.Clamp(_initialZoom.Value, -25f, 25f);
                _cameraInitialized = true;
            }
        }

        internal static void EndFrame(PPURenderer renderer,
            MainMenuManager.GFXModes originalMode)
        {
            _foregroundGround?.Refresh(renderer, _active,
                _perspectiveCompensation?.Value ?? false,
                GetCameraDistance(renderer));
            MainMenuManager.MainMenuSettings settings = MainMenuManager.Instance?.mainMenuSettings;
            if (_active && settings != null)
                settings.gfxMode = originalMode;
        }

        internal static void Apply(PPURenderer renderer)
        {
            if (!_active || renderer == null) return;
            if (!TryBuildProfile(out DepthProfile profile, out string error))
            {
                if (!string.Equals(_lastError, error, StringComparison.Ordinal))
                    _log?.LogError("Layer depth profile rejected: " + error);
                _lastError = error;
                return;
            }
            _lastError = string.Empty;
            if (renderer.zPositions == null || renderer.zPositions.Length != 13 ||
                renderer.zScales == null || renderer.zScales.Length != 13)
            {
                _lastError = "renderer depth arrays are not the expected 13 elements";
                return;
            }
            renderer.zPositionsBack = profile.BackdropZ;
            float cameraDistance = GetCameraDistance(renderer);
            renderer.zScalesBack = profile.BackdropScale *
                GetPlaneCompensation(profile.BackdropZ, cameraDistance);
            for (int i = 0; i < 13; i++)
            {
                renderer.zPositions[i] = profile.PlaneZ[i];
                renderer.zScales[i] = profile.PlaneScale[i] *
                    GetPlaneCompensation(profile.PlaneZ[i], cameraDistance);
            }
            if (renderer.backMain != null)
                renderer.backMain.localScale = Vector3.one * renderer.zScalesBack;
            if (renderer.backSub != null)
                renderer.backSub.localScale = Vector3.one * renderer.zScalesBack;
            _appliedFrames++;
            if (_appliedFrames == 1 || _appliedFrames % 300 == 0)
                WriteStatus("applied");
        }

        internal static void SetComponentMapper(ConnectedComponentDepthMapper mapper)
        {
            _componentMapper = mapper;
        }

        internal static bool ExportCurrentComponents(string path)
        {
            try { return _componentMapper?.ExportComponents(path) == true; }
            catch (Exception exception)
            {
                _log?.LogWarning("Could not export component authoring snapshot: " +
                    exception.Message);
                return false;
            }
        }

        internal static void SetNativeDetailStatus(bool applied, string moduleBase,
            string trampolineBase, string depthTableBase)
        {
            _nativeDetailApplied = applied;
            _nativeModuleBase = moduleBase ?? string.Empty;
            _nativeTrampolineBase = trampolineBase ?? string.Empty;
            _nativeDepthTableBase = depthTableBase ?? string.Empty;
        }

        internal static void SetSpriteLoopStatus(bool applied, string address)
        {
            _duplicateOamPassRemoved = applied;
            _spriteLoopPatchAddress = address ?? string.Empty;
        }

        private static float GetCameraDistance(PPURenderer renderer)
        {
            return Mathf.Clamp(30f - (renderer?.zPos ?? 0f), 5f, 55f);
        }

        private static float GetPlaneCompensation(float z, float cameraDistance)
        {
            return _perspectiveCompensation.Value
                ? DepthMath.PerspectiveCompensation(z, cameraDistance)
                : 1f;
        }

        internal static void PollControls()
        {
            if (Input.GetKeyDown(KeyCode.F6))
            {
                _active = !_active;
                _activeSetting.Value = _active;
                _config.Save();
                if (!_active)
                {
                    RestoreDisplayMode();
                    _componentMapper?.Clear();
                    _foregroundGround?.Hide("controller-inactive");
                }
                else _cameraInitialized = false;
                _log?.LogWarning("Layer depth 3D " + (_active ? "enabled" : "disabled"));
                WriteStatus(_active ? "enabled" : "disabled");
            }
            if (!_active || !(Input.GetKey(KeyCode.LeftControl) ||
                              Input.GetKey(KeyCode.RightControl))) return;
            if (Input.GetKeyDown(KeyCode.PageUp))
            {
                _selectedGap = (_selectedGap + 12) % 13;
                WriteStatus("selected-gap");
            }
            if (Input.GetKeyDown(KeyCode.PageDown))
            {
                _selectedGap = (_selectedGap + 1) % 13;
                WriteStatus("selected-gap");
            }
            if (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus))
                AdjustSelectedGap(Math.Abs(_step.Value));
            if (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus))
                AdjustSelectedGap(-Math.Abs(_step.Value));
            if (Input.GetKeyDown(KeyCode.Backspace) && _renderer != null)
            {
                _renderer.xRot = Mathf.Clamp(_initialPitch.Value, -25f, 25f);
                _renderer.yRot = Mathf.Clamp(_initialYaw.Value, -25f, 25f);
                _renderer.zPos = Mathf.Clamp(_initialZoom.Value, -25f, 25f);
                WriteStatus("camera-reset");
            }
        }

        private static void AdjustSelectedGap(float delta)
        {
            if (!DepthMath.TryParseCsv(_gapsText.Value, 13, 0f, 100f,
                    out float[] gaps, out string error))
            {
                _lastError = error;
                WriteStatus("invalid-profile");
                return;
            }
            gaps[_selectedGap] = Mathf.Clamp(gaps[_selectedGap] + delta, 0f, 100f);
            _gapsText.Value = DepthMath.ToCsv(gaps);
            _config.Save();
            _log?.LogInfo("Gap " + _selectedGap + " = " + gaps[_selectedGap].ToString("0.###"));
            WriteStatus("gap-adjusted");
        }

        private static bool TryBuildProfile(out DepthProfile profile, out string error)
        {
            profile = null;
            if (!DepthMath.TryParseCsv(_gapsText.Value, 13, 0f, 100f,
                    out float[] gaps, out error)) return false;
            if (!DepthMath.TryParseCsv(_scalesText.Value, 14, 0.01f, 10f,
                    out float[] scales, out error)) return false;
            float separation = _compressPriorityPlanes.Value
                ? Mathf.Clamp(_priorityPlaneSpacing.Value, 0.001f, 0.1f)
                : Mathf.Clamp(_separation.Value, 0f, 10f);
            int neutral = Mathf.Clamp(_neutral.Value, 0, 13);
            profile = DepthMath.Build(gaps, separation, neutral, scales);
            return true;
        }

        internal static void RestoreDisplayMode()
        {
            if (!_displayModeOverridden) return;
            MainMenuManager.MainMenuSettings settings = MainMenuManager.Instance?.mainMenuSettings;
            if (settings != null) settings.gfxMode = _savedDisplayMode;
            _displayModeOverridden = false;
        }

        internal static void Shutdown()
        {
            RestoreDisplayMode();
            try { _foregroundGround?.Dispose(); } catch { }
            _foregroundGround = null;
        }

        internal static void WriteFailure(Exception exception)
        {
            _lastError = exception.GetType().Name + ": " + exception.Message;
            WriteStatus("failed");
        }

        internal static void WriteStatus(string state)
        {
            try
            {
                Directory.CreateDirectory(StatusDirectory);
                string gaps = _gapsText?.Value ?? string.Empty;
                string scales = _scalesText?.Value ?? string.Empty;
                string json = "{" +
                    "\"version\":\"0.8.0\"," +
                    "\"state\":\"" + Escape(state) + "\"," +
                    "\"active\":" + (_active ? "true" : "false") + "," +
                    "\"appliedFrames\":" + _appliedFrames + "," +
                    "\"selectedGap\":" + _selectedGap + "," +
                    "\"separation\":" + (_separation?.Value ?? 0f).ToString(
                        System.Globalization.CultureInfo.InvariantCulture) + "," +
                    "\"compressPriorityPlanes\":" +
                        ((_compressPriorityPlanes?.Value ?? false) ? "true" : "false") + "," +
                    "\"priorityPlaneSpacing\":" +
                        (_priorityPlaneSpacing?.Value ?? 0f).ToString(
                            System.Globalization.CultureInfo.InvariantCulture) + "," +
                    "\"neutralBoundary\":" + (_neutral?.Value ?? 0) + "," +
                    "\"planeGaps\":\"" + Escape(gaps) + "\"," +
                    "\"planeScales\":\"" + Escape(scales) + "\"," +
                    "\"framebufferPatchDetected\":" +
                        (_framebufferPatchDetected ? "true" : "false") + "," +
                    "\"perspectiveCompensation\":" +
                        ((_perspectiveCompensation?.Value ?? false) ? "true" : "false") + "," +
                    "\"connectedComponents\":" +
                        ((_connectedComponents?.Value ?? false) ? "true" : "false") + "," +
                    "\"componentRebuilds\":" + (_componentMapper?.Rebuilds ?? 0) + "," +
                    "\"componentProbes\":" + (_componentMapper?.ProbeCount ?? 0) + "," +
                    "\"componentTableUpdates\":" + (_componentMapper?.TableUpdates ?? 0) + "," +
                    "\"componentBuildPending\":" +
                        ((_componentMapper?.BuildPending ?? false) ? "true" : "false") + "," +
                    "\"componentCount\":" + (_componentMapper?.ComponentCount ?? 0) + "," +
                    "\"componentLevel\":\"" +
                        Escape(_componentMapper?.LastLevelHex ?? string.Empty) + "\"," +
                    "\"nativeDetailApplied\":" +
                        (_nativeDetailApplied ? "true" : "false") + "," +
                    "\"nativeModuleBase\":\"" + Escape(_nativeModuleBase) + "\"," +
                    "\"nativeTrampolineBase\":\"" +
                        Escape(_nativeTrampolineBase) + "\"," +
                    "\"nativeDepthTableBase\":\"" +
                        Escape(_nativeDepthTableBase) + "\"," +
                    "\"duplicateOamPassRemoved\":" +
                        (_duplicateOamPassRemoved ? "true" : "false") + "," +
                    "\"spriteLoopPatchAddress\":\"" +
                        Escape(_spriteLoopPatchAddress) + "\"," +
                    "\"foregroundGroundVisible\":" +
                        ((_foregroundGround?.Visible ?? false) ? "true" : "false") + "," +
                    "\"foregroundGroundLevel\":\"" +
                        ((_foregroundGround?.Level ?? -1) < 0 ? string.Empty :
                         (_foregroundGround?.Level ?? 0).ToString("X4")) + "\"," +
                    "\"foregroundGroundCutY\":" +
                        (_foregroundGround?.CutY ?? 0) + "," +
                    "\"foregroundGroundDepth\":" +
                        (_foregroundGround?.Depth ?? 0f).ToString(
                            System.Globalization.CultureInfo.InvariantCulture) + "," +
                    "\"foregroundGroundSurfaceScaleX\":" +
                        (_foregroundGround?.SurfaceScaleX ?? 0f).ToString(
                            System.Globalization.CultureInfo.InvariantCulture) + "," +
                    "\"foregroundGroundSurfaceScaleY\":" +
                        (_foregroundGround?.SurfaceScaleY ?? 0f).ToString(
                            System.Globalization.CultureInfo.InvariantCulture) + "," +
                    "\"foregroundGroundSourceWidth\":" +
                        (_foregroundGround?.SourceWidth ?? 0f).ToString(
                            System.Globalization.CultureInfo.InvariantCulture) + "," +
                    "\"foregroundGroundSourceHeight\":" +
                        (_foregroundGround?.SourceHeight ?? 0f).ToString(
                            System.Globalization.CultureInfo.InvariantCulture) + "," +
                    "\"foregroundGroundShader\":\"" +
                        Escape(_foregroundGround?.ShaderName ?? string.Empty) + "\"," +
                    "\"foregroundGroundUploads\":" +
                        (_foregroundGround?.Uploads ?? 0) + "," +
                    "\"foregroundGroundReason\":\"" +
                        Escape(_foregroundGround?.LastReason ?? string.Empty) + "\"," +
                    "\"framebufferCompatibility\":\"Set PresentFramebuffer=false and ShadowRenderInterval=0\"," +
                    "\"lastError\":\"" + Escape(_lastError) + "\"}";
                File.WriteAllText(Path.Combine(StatusDirectory, "status.json"), json);
            }
            catch (Exception exception)
            {
                _log?.LogError("Could not write layer-depth status: " + exception.Message);
            }
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }
}
