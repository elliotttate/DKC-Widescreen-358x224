using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace SuperZSNESDKCBackgroundStateCache
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class SuperZSNESDKCBackgroundStateCachePlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.dkcbackgroundstatecache";
        public const string PluginName = "SuperZSNES DKC Background State Cache";
        public const string PluginVersion = "0.1.1";

        private Harmony _harmony;
        private ConfigEntry<bool> _enabled;
        private ConfigEntry<bool> _dryRun;

        private void Awake()
        {
            _enabled = Config.Bind("Cache", "Enabled", false,
                "Enable exact DKC background-state comparisons. False applies no Harmony patches.");
            _dryRun = Config.Bind("Cache", "DryRun", true,
                "When enabled, count predicted hits but never skip GenerateBackground. Set false only for controlled visual A/B testing.");
            if (!_enabled.Value)
            {
                Logger.LogInfo(PluginName + " " + PluginVersion + " is disabled; no Harmony patch was applied.");
                CacheController.Configure(false, true, Logger, null);
                WriteStatus("disabled");
                return;
            }

            try
            {
                var layout = CacheLayout.ResolveAndVerify();
                CacheController.Configure(true, _dryRun.Value, Logger, layout.SnesPpuField);
                _harmony = new Harmony(PluginGuid);
                _harmony.Patch(layout.GenerateBackgrounds,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(CachePatches), nameof(CachePatches.BackgroundsPrefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(CachePatches), nameof(CachePatches.BackgroundsPostfix))));
                _harmony.Patch(layout.GenerateBackground,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(CachePatches), nameof(CachePatches.BackgroundPrefix))));
                foreach (var invalidator in layout.Invalidators)
                    _harmony.Patch(invalidator,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(CachePatches), nameof(CachePatches.InvalidatePrefix))));

                var bgInfo = Harmony.GetPatchInfo(layout.GenerateBackgrounds);
                var layerInfo = Harmony.GetPatchInfo(layout.GenerateBackground);
                if (bgInfo == null || !bgInfo.Prefixes.Any(patch => patch.owner == PluginGuid) ||
                    !bgInfo.Postfixes.Any(patch => patch.owner == PluginGuid) ||
                    layerInfo == null || !layerInfo.Prefixes.Any(patch => patch.owner == PluginGuid))
                    throw new InvalidOperationException("Runtime Harmony chain did not retain the cache coordination patches.");

                Logger.LogInfo("Exact DKC whole-background cache active. DryRun=" + _dryRun.Value +
                               "; full VRAM is compared and no OBJ-only exclusion is used.");
                WriteStatus(_dryRun.Value ? "dry-run" : "active");
            }
            catch (Exception exception)
            {
                try { _harmony?.UnpatchSelf(); } catch { }
                CacheController.Configure(false, true, Logger, null);
                Logger.LogError("Background cache failed closed; no skipping remains active: " + exception);
                WriteStatus("failed-closed", exception.Message);
            }
        }

        private void OnDestroy()
        {
            try { CacheController.WriteStatus(StatusPath(), "shutdown"); } catch { }
            try { _harmony?.UnpatchSelf(); } catch { }
        }

        private void WriteStatus(string state, string detail = "")
        {
            CacheController.WriteStatus(StatusPath(), state, detail);
        }

        private static string StatusPath()
        {
            var directory = Path.Combine(Paths.PluginPath, "SuperZSNESDKCBackgroundStateCache");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "status.json");
        }
    }

    internal sealed class CacheLayout
    {
        internal MethodInfo GenerateBackgrounds;
        internal MethodInfo GenerateBackground;
        internal FieldInfo SnesPpuField;
        internal MethodInfo[] Invalidators;

        internal static CacheLayout ResolveAndVerify()
        {
            var renderer = typeof(PPURenderer);
            var backgrounds = AccessTools.Method(renderer, "GenerateBackgrounds", Type.EmptyTypes);
            var background = renderer.GetMethods(AccessTools.all).SingleOrDefault(method =>
                method.Name == "GenerateBackground" && method.GetParameters().Length == 2 &&
                method.GetParameters()[0].ParameterType == typeof(int));
            var snesPpu = AccessTools.Field(renderer, "snesPPU");
            var invalidatorNames = new[] { "Init", "ClearCache", "ResetRenderer", "UpdateModData" };
            var invalidators = invalidatorNames.Select(name => renderer.GetMethods(AccessTools.all)
                .SingleOrDefault(method => method.Name == name)).ToList();
            invalidators.Add(typeof(SNESPPU).GetMethods(AccessTools.all).SingleOrDefault(method =>
                method.Name == "SetState" && method.GetParameters().Length == 1 &&
                method.GetParameters()[0].ParameterType == typeof(SNESPPU.PPUParams)));
            if (backgrounds == null || background == null || snesPpu == null || invalidators.Any(method => method == null))
                throw new MissingMemberException("Required PPURenderer v0.230 members were not found exactly.");
            if (snesPpu.FieldType != typeof(SNESPPU))
                throw new InvalidOperationException("PPURenderer.snesPPU does not have the expected v0.230 type.");
            return new CacheLayout
            {
                GenerateBackgrounds = backgrounds,
                GenerateBackground = background,
                SnesPpuField = snesPpu,
                Invalidators = invalidators.ToArray()
            };
        }
    }

    internal static class CachePatches
    {
        public static void BackgroundsPrefix(PPURenderer __instance) => CacheController.BeginFrame(__instance);
        public static void BackgroundsPostfix(PPURenderer __instance) => CacheController.EndFrame(__instance);
        public static bool BackgroundPrefix(PPURenderer __instance) => CacheController.AllowGenerateBackground(__instance);
        public static void InvalidatePrefix() => CacheController.Invalidate("renderer-reset-or-scene-change");
    }
}
