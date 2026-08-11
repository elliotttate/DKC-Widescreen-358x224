using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SuperZSNESFramebufferPresentationPrototype
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class SuperZSNESFramebufferPresentationPrototypePlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.framebufferpresentationprototype";
        public const string PluginName = "SuperZSNES Framebuffer Presentation Prototype";
        public const string PluginVersion = "0.1.0";

        private Harmony _harmony;

        private void Awake()
        {
            var enabled = Config.Bind("Prototype", "Enabled", false,
                "Enable provider evaluation and Harmony hooks. False applies no patches.");
            var dryRun = Config.Bind("Prototype", "DryRun", true,
                "Ask a registered CPU provider for frames and upload them, but keep the stock renderer/presenter.");
            var width = Config.Bind("Framebuffer", "Width", 398,
                "Final-composed indexed canvas width. 398x224 matches the stock transfer shader grid.");
            var height = Config.Bind("Framebuffer", "Height", 224,
                "Final-composed indexed canvas height.");
            var statusPath = Path.Combine(Paths.PluginPath, "SuperZSNESFramebufferPresentationPrototype", "status.json");
            if (!enabled.Value)
            {
                PresentationController.Configure(false, true, width.Value, height.Value, null, Logger, statusPath);
                PresentationController.WriteStatus("disabled");
                Logger.LogInfo(PluginName + " " + PluginVersion + " disabled; no Harmony patches applied.");
                return;
            }
            try
            {
                var layout = PresentationLayout.ResolveExactV0230();
                PresentationController.Configure(true, dryRun.Value, width.Value, height.Value,
                    layout.TransferMaterialUsed, Logger, statusPath);
                _harmony = new Harmony(PluginGuid);
                _harmony.Patch(layout.GenerateBackgrounds,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(PresentationPatches),
                        nameof(PresentationPatches.GenerateBackgroundsPrefix))));
                _harmony.Patch(layout.OnRenderImage,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(PresentationPatches),
                        nameof(PresentationPatches.OnRenderImagePrefix))));
                foreach (var invalidator in layout.Invalidators)
                    _harmony.Patch(invalidator,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(PresentationPatches),
                            nameof(PresentationPatches.InvalidatePrefix))));
                PresentationController.WriteStatus(dryRun.Value ? "dry-run" : "active");
                Logger.LogInfo("Framebuffer presentation hooks active. DryRun=" + dryRun.Value +
                               "; no frames are bypassed until a provider registers and accepts a frame.");
            }
            catch (Exception exception)
            {
                try { _harmony?.UnpatchSelf(); } catch { }
                PresentationController.Configure(false, true, width.Value, height.Value, null, Logger, statusPath);
                PresentationController.WriteStatus("failed-closed", exception.Message);
                Logger.LogError("Framebuffer prototype failed closed: " + exception);
            }
        }

        private void OnDestroy()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
            try { PresentationController.WriteStatus("shutdown"); } catch { }
            PresentationController.Dispose();
        }
    }

    internal sealed class PresentationLayout
    {
        internal MethodInfo GenerateBackgrounds;
        internal MethodInfo OnRenderImage;
        internal FieldInfo TransferMaterialUsed;
        internal MethodInfo[] Invalidators;

        internal static PresentationLayout ResolveExactV0230()
        {
            var generate = typeof(PPURenderer).GetMethods(AccessTools.all).SingleOrDefault(method =>
                method.Name == "GenerateBackgrounds" && method.ReturnType == typeof(void) &&
                method.GetParameters().Length == 0);
            var render = typeof(MainScreenBlit).GetMethods(AccessTools.all).SingleOrDefault(method =>
                method.Name == "OnRenderImage" && method.ReturnType == typeof(void) &&
                method.GetParameters().Length == 2 &&
                method.GetParameters()[0].ParameterType == typeof(RenderTexture) &&
                method.GetParameters()[1].ParameterType == typeof(RenderTexture));
            var transfer = AccessTools.Field(typeof(MainScreenBlit), "_transferMaterialUsed");
            var invalidators = new[]
            {
                AccessTools.Method(typeof(PPURenderer), "Init", Type.EmptyTypes),
                AccessTools.Method(typeof(PPURenderer), "ResetRenderer", Type.EmptyTypes),
                typeof(SNESPPU).GetMethods(AccessTools.all).SingleOrDefault(method => method.Name == "SetState" &&
                    method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType == typeof(SNESPPU.PPUParams))
            };
            if (generate == null || render == null || transfer == null || transfer.FieldType != typeof(Material) ||
                invalidators.Any(method => method == null) ||
                typeof(PPURenderer).GetField("mainScreenBlitter")?.FieldType != typeof(MainScreenBlit) ||
                typeof(MainScreenBlit).GetField("transferRenderTexture")?.FieldType != typeof(RenderTexture))
                throw new MissingMemberException("Required SuperZSNES v0.230 framebuffer presentation shape changed.");
            return new PresentationLayout
            {
                GenerateBackgrounds = generate,
                OnRenderImage = render,
                TransferMaterialUsed = transfer,
                Invalidators = invalidators
            };
        }
    }

    internal static class PresentationPatches
    {
        public static bool GenerateBackgroundsPrefix(PPURenderer __instance) =>
            PresentationController.BeforeGenerateBackgrounds(__instance);

        public static bool OnRenderImagePrefix(MainScreenBlit __instance, RenderTexture destination) =>
            !PresentationController.TryPresent(__instance, destination);

        public static void InvalidatePrefix() => PresentationController.Invalidate("renderer-or-state-reset");
    }
}
