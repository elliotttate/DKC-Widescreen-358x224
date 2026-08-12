using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SuperZSNESDKCFramebufferRenderer
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class SuperZSNESDKCFramebufferRendererPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.dkcframebuffer";
        public const string PluginName = "SuperZSNES DKC Framebuffer Renderer";
        public const string PluginVersion = "0.4.13";

        private Harmony _harmony;
        private ConfigEntry<bool> _enabled;
        private ConfigEntry<bool> _present;
        private ConfigEntry<int> _shadowInterval;
        private ConfigEntry<int> _width;
        private ConfigEntry<int> _height;
        private ConfigEntry<int> _leftExtension;
        private ConfigEntry<bool> _retainedBackgrounds;
        private ConfigEntry<KeyCode> _captureKey;
        private string _captureRequestPath;

        private void Awake()
        {
            _enabled = Config.Bind("Renderer", "Enabled", false,
                "Enable the experimental DKC Mode 1 framebuffer renderer. False applies no Harmony patches.");
            _present = Config.Bind("Renderer", "PresentFramebuffer", false,
                "Replace the stock Unity mesh renderer with the framebuffer output. Keep false until shadow captures pass QA.");
            _shadowInterval = Config.Bind("Renderer", "ShadowRenderInterval", 60,
                "When not presenting, render one candidate frame every N GenerateBackgrounds calls. 0 renders only on the capture hotkey.");
            _width = Config.Bind("Geometry", "Width", 358, "Raw widescreen framebuffer width.");
            _height = Config.Bind("Geometry", "Height", 224, "Raw SNES framebuffer height.");
            _leftExtension = Config.Bind("Geometry", "LeftExtension", 51,
                "Native pixels represented to the left of stock screen X=0. The 368-pixel renderer guard is cropped to 358 by default.");
            _retainedBackgrounds = Config.Bind("Renderer", "RetainedBackgrounds", true,
                "Reuse plugin-owned background planes until their exact state or relevant VRAM changes. Disable only for oracle A/B testing.");
            _captureKey = Config.Bind("Diagnostics", "CaptureKey", KeyCode.F10,
                "Write the most recent candidate framebuffer PNG and status JSON.");

            if (!_enabled.Value)
            {
                Logger.LogInfo(PluginName + " " + PluginVersion + " disabled; no Harmony patches applied.");
                return;
            }

            try
            {
                _captureRequestPath = Path.Combine(StatusDirectory(), "capture.request");
                var layout = RendererLayout.Resolve();
                FramebufferController.Initialize(Logger, _present, _shadowInterval, _width, _height,
                    _leftExtension, _retainedBackgrounds, StatusDirectory());
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

                var frameInfo = Harmony.GetPatchInfo(layout.GenerateBackgrounds);
                var presentInfo = Harmony.GetPatchInfo(layout.OnRenderImage);
                var ppuInfo = Harmony.GetPatchInfo(layout.WritePpuIo);
                if (frameInfo == null || !frameInfo.Prefixes.Any(p => p.owner == PluginGuid) ||
                    !frameInfo.Postfixes.Any(p => p.owner == PluginGuid) ||
                    presentInfo == null || !presentInfo.Prefixes.Any(p => p.owner == PluginGuid) ||
                    ppuInfo == null || !ppuInfo.Prefixes.Any(p => p.owner == PluginGuid))
                    throw new InvalidOperationException("Runtime Harmony chain did not retain all framebuffer patches.");

                Logger.LogWarning(PluginName + " active in " + (_present.Value ? "PRESENTATION" : "SHADOW") +
                                  " mode. Unsupported frames always fall back to the stock renderer.");
            }
            catch (Exception exception)
            {
                try { _harmony?.UnpatchSelf(); } catch { }
                FramebufferController.Shutdown();
                Logger.LogError("Framebuffer renderer failed closed: " + exception);
            }
        }

        private void Update()
        {
            if (_enabled == null || !_enabled.Value) return;
            bool requested = Input.GetKeyDown(_captureKey.Value);
            if (!string.IsNullOrEmpty(_captureRequestPath) && File.Exists(_captureRequestPath))
            {
                try { File.Delete(_captureRequestPath); } catch { }
                requested = true;
            }
            if (requested) FramebufferController.RequestCapture();
        }

        private void OnDestroy()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
            FramebufferController.Shutdown();
        }

        private static string StatusDirectory()
        {
            string directory = Path.Combine(Paths.PluginPath, "SuperZSNESDKCFramebufferRenderer");
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
                throw new MissingMemberException("Required SuperZSNES v0.230 renderer methods were not found.");
            return new RendererLayout { GenerateBackgrounds = backgrounds, OnRenderImage = render, WritePpuIo = writePpu };
        }
    }

    internal static class RendererPatches
    {
        public static bool GenerateBackgroundsPrefix(PPURenderer __instance)
        {
            return FramebufferController.BeforeGenerateBackgrounds(__instance);
        }

        public static void GenerateBackgroundsPostfix()
        {
            FramebufferController.AfterGenerateBackgrounds();
        }

        public static bool OnRenderImagePrefix(MainScreenBlit __instance, RenderTexture source, RenderTexture destination)
        {
            return !FramebufferController.TryPresent(__instance, source, destination);
        }

        public static void WritePpuIoPrefix(SNESPPU __instance, uint addr)
        {
            FramebufferController.ObservePpuWrite(__instance, addr);
        }
    }
}
