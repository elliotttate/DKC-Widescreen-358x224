using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace SuperZSNESDKCFramebufferRenderer
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class SuperZSNESDKCFramebufferRendererIL2CPPPlugin : BasePlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.dkcframebuffer.il2cpp";
        public const string PluginName = "SuperZSNES DKC Framebuffer Renderer IL2CPP";
        public const string PluginVersion = "0.1.1";

        private Harmony _harmony;
        private ConfigEntry<bool> _enabled;
        private string _captureRequestPath;

        public override void Load()
        {
            _enabled = Config.Bind("Renderer", "Enabled", false,
                "Enable the DKC Mode 1 framebuffer renderer. False applies no Harmony patches.");
            ConfigEntry<bool> present = Config.Bind("Renderer", "PresentFramebuffer", false,
                "Replace the stock v0.300 Unity compositor with the verified framebuffer output.");
            ConfigEntry<int> shadowInterval = Config.Bind("Renderer", "ShadowRenderInterval", 60,
                "When not presenting, render one candidate frame every N frame calls. Zero uses capture.request only.");
            ConfigEntry<int> width = Config.Bind("Geometry", "Width", 358, "Raw widescreen framebuffer width.");
            ConfigEntry<int> height = Config.Bind("Geometry", "Height", 224, "Raw SNES framebuffer height.");
            ConfigEntry<int> leftExtension = Config.Bind("Geometry", "LeftExtension", 51,
                "Native pixels represented left of stock X=0 after cropping the 368-pixel guard.");
            ConfigEntry<bool> retained = Config.Bind("Renderer", "RetainedBackgrounds", true,
                "Reuse plugin-owned background planes until exact relevant state changes.");
            Config.Save();

            if (!_enabled.Value)
            {
                Log.LogInfo(PluginName + " " + PluginVersion + " disabled; no Harmony patches applied.");
                return;
            }

            try
            {
                string directory = StatusDirectory();
                _captureRequestPath = Path.Combine(directory, "capture.request");
                RendererPatches.CaptureRequestPath = _captureRequestPath;
                RendererLayout layout = RendererLayout.Resolve();
                FramebufferController.Initialize(Log, present, shadowInterval, width, height,
                    leftExtension, retained, directory);
                _harmony = new Harmony(PluginGuid);
                _harmony.Patch(layout.GenerateBackgrounds,
                    prefix: new HarmonyMethod(typeof(RendererPatches), nameof(RendererPatches.GenerateBackgroundsPrefix))
                    { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(RendererPatches), nameof(RendererPatches.GenerateBackgroundsPostfix))
                    { priority = Priority.Last });
                _harmony.Patch(layout.OnRenderImage,
                    prefix: new HarmonyMethod(typeof(RendererPatches), nameof(RendererPatches.OnRenderImagePrefix))
                    { priority = Priority.First });
                _harmony.Patch(layout.WritePpuIo,
                    prefix: new HarmonyMethod(typeof(RendererPatches), nameof(RendererPatches.WritePpuIoPrefix))
                    { priority = Priority.First });

                if (!HasOwnPrefix(layout.GenerateBackgrounds) || !HasOwnPostfix(layout.GenerateBackgrounds) ||
                    !HasOwnPrefix(layout.OnRenderImage) ||
                    !HasOwnPrefix(layout.WritePpuIo))
                    throw new InvalidOperationException("Runtime Harmony chain did not retain all IL2CPP renderer patches.");

                Log.LogWarning(PluginName + " active in " + (present.Value ? "PRESENTATION" : "SHADOW") +
                               " mode. Unsupported frames always fall back to the stock renderer.");
            }
            catch (Exception exception)
            {
                try { _harmony?.UnpatchSelf(); } catch { }
                FramebufferController.Shutdown();
                Log.LogError("IL2CPP framebuffer renderer failed closed: " + exception);
            }
        }

        public override bool Unload()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
            FramebufferController.Shutdown();
            return true;
        }

        private static bool HasOwnPrefix(MethodInfo method)
        {
            Patches info = Harmony.GetPatchInfo(method);
            return info != null && info.Prefixes.Any(p => p.owner == PluginGuid);
        }

        private static bool HasOwnPostfix(MethodInfo method)
        {
            Patches info = Harmony.GetPatchInfo(method);
            return info != null && info.Postfixes.Any(p => p.owner == PluginGuid);
        }

        private static string StatusDirectory()
        {
            string directory = Path.Combine(Paths.PluginPath, "SuperZSNESDKCFramebufferRendererIL2CPP");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    internal sealed class RendererLayout
    {
        internal MethodInfo GenerateBackgrounds;
        internal MethodInfo OnRenderImage;
        internal MethodInfo WritePpuIo;

        internal static RendererLayout Resolve()
        {
            MethodInfo backgrounds = AccessTools.Method(typeof(PPURenderer), "GenerateBackgrounds", Type.EmptyTypes);
            MethodInfo render = AccessTools.Method(typeof(MainScreenBlit), "OnRenderImage",
                new[] { typeof(RenderTexture), typeof(RenderTexture) });
            MethodInfo writePpu = AccessTools.Method(typeof(SNESPPU), "WriteIO",
                new[] { typeof(uint), typeof(byte) });
            if (backgrounds == null || render == null || writePpu == null)
                throw new MissingMemberException("Required SuperZSNES v0.300 IL2CPP renderer methods were not found.");
            return new RendererLayout { GenerateBackgrounds = backgrounds, OnRenderImage = render, WritePpuIo = writePpu };
        }
    }

    internal static class RendererPatches
    {
        internal static string CaptureRequestPath;

        public static bool GenerateBackgroundsPrefix(PPURenderer __instance)
        {
            if (!string.IsNullOrEmpty(CaptureRequestPath) && File.Exists(CaptureRequestPath))
            {
                try { File.Delete(CaptureRequestPath); } catch { }
                FramebufferController.RequestCapture();
            }
            return FramebufferController.BeforeGenerateBackgrounds(__instance);
        }

        public static void GenerateBackgroundsPostfix()
        {
            FramebufferController.AfterGenerateBackgrounds();
        }

        public static bool OnRenderImagePrefix(MainScreenBlit __instance,
            RenderTexture source, RenderTexture destination)
        {
            return !FramebufferController.TryPresent(__instance, source, destination);
        }

        public static void WritePpuIoPrefix(SNESPPU __instance, uint addr)
        {
            FramebufferController.ObservePpuWrite(__instance, addr);
        }
    }
}
