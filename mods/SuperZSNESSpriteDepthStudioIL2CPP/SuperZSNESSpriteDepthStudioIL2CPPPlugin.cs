using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace SuperZSNESSpriteDepthStudio
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("dev.local.superzsnes.layerdepth.il2cpp", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class SpriteDepthStudioPlugin : BasePlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.spritedepthstudio.il2cpp";
        public const string PluginName = "SuperZSNES Object Depth Studio IL2CPP";
        public const string PluginVersion = "0.4.0";
        private Harmony _harmony;
        private SpriteDepthNativePatcher _native;

        public override void Load()
        {
            ConfigEntry<bool> enabled = Config.Bind("Studio", "Enabled", true,
                "Enable sprite capture, profile hot reload, and verified native depth hooks.");
            ConfigEntry<bool> launch = Config.Bind("Studio", "LaunchAtStartup", false,
                "Launch the external scrollable Object Depth Studio window at startup.");
            ConfigEntry<float> spacing = Config.Bind("Depth", "LayerSpacing", 0.2f,
                "World-space offset for each authored sprite depth layer (0..2).");
            ConfigEntry<float> orderSpacing = Config.Bind("Depth", "OamOrderSpacing",
                0.001f,
                "Tiny ordering-only spacing between OAM slots in 3D (0.0001..0.0078125).");
            ConfigEntry<bool> require3D = Config.Bind("Depth", "RequireGimmick3D", true,
                "Apply authored depths only while the layer controller has enabled Gimmick3D.");
            ConfigEntry<bool> persist = Config.Bind("Capture", "PersistCaptures", true,
                "Keep timestamped raw OAM/VRAM/CGRAM captures for later authoring and debugging.");
            Config.Save();
            if (!enabled.Value) { Log.LogInfo(PluginName + " disabled."); return; }
            try
            {
                _native = new SpriteDepthNativePatcher(message => Log.LogWarning(message));
                _native.Apply(Path.Combine(Paths.GameRootPath, "GameAssembly.dll"));
                SpriteDepthRuntime.Initialize(Log, _native, spacing, orderSpacing,
                    require3D, persist);
                _harmony = new Harmony(PluginGuid);
                PatchRequired(typeof(PPURenderer), "GenerateBackgrounds", Type.EmptyTypes,
                    nameof(SpriteDepthHooks.GenerateBackgroundsPrefix), null, Priority.Low);
                PatchRequired(typeof(MasterExecutor), "Update", Type.EmptyTypes,
                    null, nameof(SpriteDepthHooks.UpdatePostfix), Priority.Last);
                if (launch.Value) SpriteDepthRuntime.LaunchStudio();
                Log.LogWarning(PluginName + " active. Press F10 or use Capture Current Frame in the Studio window.");
            }
            catch (Exception exception)
            {
                try { _harmony?.UnpatchSelf(); } catch { }
                try { _native?.Dispose(); } catch { }
                Log.LogError(PluginName + " failed closed: " + exception);
            }
        }

        public override bool Unload()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
            SpriteDepthRuntime.Shutdown();
            try { _native?.Dispose(); } catch { }
            return true;
        }

        private void PatchRequired(Type type, string name, Type[] parameters,
            string prefixName, string postfixName, int priority)
        {
            MethodInfo target = AccessTools.Method(type, name, parameters);
            if (target == null) throw new MissingMethodException(type.FullName, name);
            HarmonyMethod prefix = prefixName == null ? null : new HarmonyMethod(typeof(SpriteDepthHooks), prefixName) { priority = priority };
            HarmonyMethod postfix = postfixName == null ? null : new HarmonyMethod(typeof(SpriteDepthHooks), postfixName) { priority = priority };
            _harmony.Patch(target, prefix, postfix);
            Patches info = Harmony.GetPatchInfo(target);
            if (info == null || (prefix != null && !info.Prefixes.Any(p => p.owner == PluginGuid)) ||
                (postfix != null && !info.Postfixes.Any(p => p.owner == PluginGuid)))
                throw new InvalidOperationException("Harmony did not retain " + type.Name + "." + name);
        }
    }

    internal static class SpriteDepthHooks
    {
        public static void GenerateBackgroundsPrefix(PPURenderer __instance) => SpriteDepthRuntime.BeforeRender(__instance);
        public static void UpdatePostfix() => SpriteDepthRuntime.Tick();
    }
}
