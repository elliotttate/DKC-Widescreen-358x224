using System;
using System.Collections;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SuperZSNESPerformanceGuard
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class SuperZSNESPerformanceGuardPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.performanceguard";
        public const string PluginName = "SuperZSNES Performance Guard";
        public const string PluginVersion = "0.4.1";

        internal static SuperZSNESPerformanceGuardPlugin Instance;

        private ConfigEntry<bool> _disableRewindCapture;
        private ConfigEntry<bool> _disableHistoryCapture;
        private ConfigEntry<bool> _releaseRewindBuffer;
        private ConfigEntry<bool> _limitPresentationRate;
        private ConfigEntry<bool> _uncappedPresentation;
        private ConfigEntry<int> _targetPresentationRate;
        private ConfigEntry<bool> _limitPpuRenderTextures;
        private ConfigEntry<int> _ppuRenderTextureScale;
        private Harmony _harmony;
        private object _lastMenu;
        private object _lastMaster;
        private float _nextApply;
        private int _releasedStates;
        private int _originalVSyncCount;
        private int _originalTargetFrameRate;
        private bool _presentationOverrideApplied;

        private void Awake()
        {
            Instance = this;
            _disableRewindCapture = Config.Bind(
                "BackgroundServices", "DisableRewindCapture", true,
                "Disable the emulator's unconditional full-state rewind snapshots (normally eight per second). Rewind will be unavailable while enabled.");
            _disableHistoryCapture = Config.Bind(
                "BackgroundServices", "DisableHistoryCapture", true,
                "Disable the emulator's 20-second history snapshots and screenshots.");
            _releaseRewindBuffer = Config.Bind(
                "BackgroundServices", "ReleaseAllocatedRewindBuffer", true,
                "Clear already allocated rewind state objects after rewind capture is disabled.");
            _limitPresentationRate = Config.Bind(
                "Presentation", "LimitPresentationRate", false,
                "Disable VSync and run Unity presentation at a deliberate software-limited rate. Leave false for synchronized presentation; use only as an A/B option.");
            _uncappedPresentation = Config.Bind(
                "Presentation", "UncappedPresentation", false,
                "Keep VSync disabled and remove Unity's software presentation ceiling. This is an A/B option for scenes whose renderer cost falls between one 60 Hz and one 120 Hz scheduling interval.");
            _targetPresentationRate = Config.Bind(
                "Presentation", "TargetPresentationRate", 120,
                new ConfigDescription("Unity presentation ceiling. Emulation remains 60/50 Hz; 120 gives the renderer enough scheduling headroom to present each SNES frame separately.", new AcceptableValueRange<int>(60, 240)));
            _limitPpuRenderTextures = Config.Bind(
                "Presentation", "LimitPpuRenderTexturesTo2x", true,
                "Limit SuperZSNES's internal PPU surfaces instead of using 1592x896 at large desktop resolutions. The final game image still scales to the window.");
            _ppuRenderTextureScale = Config.Bind(
                "Presentation", "PpuRenderTextureScale", 2,
                new ConfigDescription("Internal PPU surface scale: 1 = 398x224 native SNES-line resolution; 2 = 796x448. Only used when LimitPpuRenderTexturesTo2x is true.",
                    new AcceptableValueRange<int>(1, 2)));

            _originalVSyncCount = QualitySettings.vSyncCount;
            _originalTargetFrameRate = Application.targetFrameRate;

            _harmony = new Harmony(PluginGuid);
            Patch("MasterExecutor", "SetupSaveFrames", new[] { R.Type("MainMenuManager") }, true, nameof(GuardHooks.SetupSaveFramesPrefix));
            Patch("MainMenuManager", "LoadMainMenuSave", Type.EmptyTypes, false, nameof(GuardHooks.LoadMainMenuSavePostfix));
            Patch("PPURenderer", "CreateWindowRenderTexture", new[] { typeof(bool), typeof(bool) }, true, nameof(GuardHooks.CreateWindowRenderTexturePrefix));
            Patch("PPURenderer", "OnResolutionChanged", Type.EmptyTypes, false, nameof(GuardHooks.OnResolutionChangedPostfix));
            ApplyPresentationSettings();
            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded. Rewind capture disabled=" +
                           _disableRewindCapture.Value + ", history capture disabled=" + _disableHistoryCapture.Value +
                           ", presentation target=" + (_limitPresentationRate.Value ? (_uncappedPresentation.Value ? "uncapped" : _targetPresentationRate.Value.ToString()) : "unchanged") +
                           ", limited PPU surfaces=" + _limitPpuRenderTextures.Value +
                           ", PPU scale=" + _ppuRenderTextureScale.Value + "x.");
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextApply) return;
            _nextApply = Time.unscaledTime + 1f;
            ApplyPresentationSettings();

            var menu = R.Static("MainMenuManager", "Instance");
            if (menu != null && !ReferenceEquals(menu, _lastMenu))
            {
                ApplyMenuSettings(menu);
                _lastMenu = menu;
            }

            var master = R.Static("MasterExecutor", "Instance");
            if (master != null && !ReferenceEquals(master, _lastMaster))
            {
                _lastMaster = master;
                ReleaseRewindStates(master);
                WriteStatus(menu, master);
            }
        }

        private void OnDestroy()
        {
            try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
            if (_presentationOverrideApplied)
            {
                QualitySettings.vSyncCount = _originalVSyncCount;
                Application.targetFrameRate = _originalTargetFrameRate;
                _presentationOverrideApplied = false;
            }
            if (ReferenceEquals(Instance, this)) Instance = null;
        }

        private void ApplyPresentationSettings()
        {
            if (!_limitPresentationRate.Value)
            {
                if (_presentationOverrideApplied)
                {
                    QualitySettings.vSyncCount = _originalVSyncCount;
                    Application.targetFrameRate = _originalTargetFrameRate;
                    _presentationOverrideApplied = false;
                }
                return;
            }
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = _uncappedPresentation.Value ? -1 : _targetPresentationRate.Value;
            _presentationOverrideApplied = true;
        }

        internal bool ShouldLimitPpuRenderTextures()
        {
            return _limitPpuRenderTextures != null && _limitPpuRenderTextures.Value;
        }

        internal RenderTexture CreatePpuRenderTexture(bool useBilinear)
        {
            var width = 398 * _ppuRenderTextureScale.Value;
            var height = 224 * _ppuRenderTextureScale.Value;
            var texture = new RenderTexture(width, height, 32, RenderTextureFormat.ARGB32, 0);
            texture.filterMode = useBilinear ? FilterMode.Bilinear : FilterMode.Point;
            texture.useMipMap = false;
            texture.Create();
            var active = RenderTexture.active;
            RenderTexture.active = texture;
            GL.Clear(true, true, Color.black);
            RenderTexture.active = active;
            return texture;
        }

        internal void EnforcePpuRenderTextureSize(object renderer)
        {
            if (!ShouldLimitPpuRenderTextures() || renderer == null) return;
            var width = 398 * _ppuRenderTextureScale.Value;
            var height = 224 * _ppuRenderTextureScale.Value;
            foreach (var field in new[] { "windowRenderTexture", "subScreenRenderTexture", "mainScreenRenderTexture", "transferScreenRenderTexture" })
            {
                var texture = R.Get(renderer, field) as RenderTexture;
                if (texture == null || (texture.width == width && texture.height == height)) continue;
                texture.Release();
                texture.width = width;
                texture.height = height;
                texture.Create();
            }
        }

        private void Patch(string typeName, string methodName, Type[] args, bool prefix, string hookName)
        {
            var type = R.Type(typeName);
            var method = type == null ? null : AccessTools.Method(type, methodName, args);
            var hook = AccessTools.Method(typeof(GuardHooks), hookName);
            if (method == null || hook == null)
            {
                Logger.LogWarning("Performance guard could not patch " + typeName + "." + methodName + ".");
                return;
            }
            if (prefix) _harmony.Patch(method, prefix: new HarmonyMethod(hook));
            else _harmony.Patch(method, postfix: new HarmonyMethod(hook));
        }

        internal void ApplyMenuSettings(object menu)
        {
            if (menu == null) return;
            var settings = R.Get(menu, "mainMenuSettings");
            if (settings == null) return;
            if (_disableRewindCapture.Value)
            {
                R.Set(settings, "rewindFPS", 0);
                R.Set(settings, "numRewindFrames", 0);
                R.Set(settings, "rewindSpeed", 0f);
            }
            if (_disableHistoryCapture.Value) R.Set(settings, "historyDisabled", true);
        }

        private void ReleaseRewindStates(object master)
        {
            if (!_disableRewindCapture.Value || !_releaseRewindBuffer.Value || master == null) return;
            var states = R.Get(master, "_rewindStates") as IList;
            if (states == null || states.Count == 0) return;
            _releasedStates += states.Count;
            states.Clear();
            R.Set(master, "_curRewindPtr", 0);
            R.Set(master, "_numRewinds", 0);
            Logger.LogInfo("Released " + _releasedStates + " allocated rewind state objects.");
        }

        private void WriteStatus(object menu, object master)
        {
            try
            {
                var settings = menu == null ? null : R.Get(menu, "mainMenuSettings");
                var directory = Path.Combine(Paths.PluginPath, "SuperZSNESPerformanceGuard");
                Directory.CreateDirectory(directory);
                var json = "{\"pluginVersion\":\"" + PluginVersion + "\",\"attached\":" + (master != null ? "true" : "false") +
                           ",\"disableRewindCapture\":" + (_disableRewindCapture.Value ? "true" : "false") +
                           ",\"disableHistoryCapture\":" + (_disableHistoryCapture.Value ? "true" : "false") +
                           ",\"rewindFPS\":" + R.Int(settings, "rewindFPS", -1) +
                           ",\"numRewindFrames\":" + R.Int(settings, "numRewindFrames", -1) +
                           ",\"historyDisabled\":" + (R.Bool(settings, "historyDisabled", false) ? "true" : "false") +
                           ",\"limitPresentationRate\":" + (_limitPresentationRate.Value ? "true" : "false") +
                           ",\"uncappedPresentation\":" + (_uncappedPresentation.Value ? "true" : "false") +
                           ",\"targetPresentationRate\":" + Application.targetFrameRate +
                           ",\"vSyncCount\":" + QualitySettings.vSyncCount +
                           ",\"limitPpuRenderTexturesTo2x\":" + (_limitPpuRenderTextures.Value ? "true" : "false") +
                           ",\"ppuRenderTextureScale\":" + _ppuRenderTextureScale.Value +
                           ",\"releasedStates\":" + _releasedStates + "}";
                File.WriteAllText(Path.Combine(directory, "status.json"), json);
            }
            catch (Exception ex) { Logger.LogWarning("Could not write performance guard status: " + ex.Message); }
        }
    }

    internal static class GuardHooks
    {
        public static void SetupSaveFramesPrefix(object inst)
        {
            var plugin = SuperZSNESPerformanceGuardPlugin.Instance;
            if (plugin != null) plugin.ApplyMenuSettings(inst);
        }

        public static void LoadMainMenuSavePostfix(object __instance)
        {
            var plugin = SuperZSNESPerformanceGuardPlugin.Instance;
            if (plugin != null) plugin.ApplyMenuSettings(__instance);
        }

        public static bool CreateWindowRenderTexturePrefix(bool useBilinear, ref RenderTexture __result)
        {
            var plugin = SuperZSNESPerformanceGuardPlugin.Instance;
            if (plugin == null || !plugin.ShouldLimitPpuRenderTextures()) return true;
            __result = plugin.CreatePpuRenderTexture(useBilinear);
            return false;
        }

        public static void OnResolutionChangedPostfix(object __instance)
        {
            var plugin = SuperZSNESPerformanceGuardPlugin.Instance;
            if (plugin != null) plugin.EnforcePpuRenderTextureSize(__instance);
        }
    }

    internal static class R
    {
        public static Type Type(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(name, false);
                if (type != null) return type;
            }
            return null;
        }

        public static object Static(string typeName, string field)
        {
            var type = Type(typeName);
            if (type == null) return null;
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var fieldInfo = type.GetField(field, flags);
            return fieldInfo == null ? type.GetProperty(field, flags)?.GetValue(null, null) : fieldInfo.GetValue(null);
        }

        public static object Get(object instance, string field)
        {
            if (instance == null) return null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var info = instance.GetType().GetField(field, flags);
            return info == null ? instance.GetType().GetProperty(field, flags)?.GetValue(instance, null) : info.GetValue(instance);
        }

        public static void Set(object instance, string field, object value)
        {
            if (instance == null) return;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var info = instance.GetType().GetField(field, flags);
            if (info != null) info.SetValue(instance, value);
            else instance.GetType().GetProperty(field, flags)?.SetValue(instance, value, null);
        }

        public static int Int(object instance, string field, int fallback)
        {
            var value = Get(instance, field);
            return value == null ? fallback : Convert.ToInt32(value);
        }

        public static bool Bool(object instance, string field, bool fallback)
        {
            var value = Get(instance, field);
            return value == null ? fallback : Convert.ToBoolean(value);
        }
    }
}
