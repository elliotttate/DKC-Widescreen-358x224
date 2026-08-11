using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace SuperZSNESFramebufferPresentationPrototype
{
    internal static class PresentationController
    {
        private static readonly IndexedFramebuffer Framebuffer = new IndexedFramebuffer();
        private static readonly IndexedFramebufferRequest Request = new IndexedFramebufferRequest();
        private static readonly PersistentFrameSurface Surface = new PersistentFrameSurface();
        private static readonly Dictionary<string, long> Rejections = new Dictionary<string, long>();

        private static bool _enabled;
        private static bool _dryRun;
        private static int _width;
        private static int _height;
        private static FieldInfo _transferMaterialUsedField;
        private static PPURenderer _readyRenderer;
        private static bool _frameReady;
        private static ManualLogSource _log;
        private static string _statusPath;
        private static string _state = "disabled";
        private static string _lastReason = "";

        internal static long EvaluatedFrames;
        internal static long ProviderFrames;
        internal static long PredictedSubstitutions;
        internal static long ActualMeshBypasses;
        internal static long PresentedFrames;
        internal static long StockFallbacks;
        internal static long PresentationFailures;

        internal static void Configure(bool enabled, bool dryRun, int width, int height,
            FieldInfo transferMaterialUsedField, ManualLogSource log, string statusPath)
        {
            _enabled = enabled;
            _dryRun = dryRun;
            _width = width;
            _height = height;
            _transferMaterialUsedField = transferMaterialUsedField;
            _log = log;
            _statusPath = statusPath;
            _state = !enabled ? "disabled" : (dryRun ? "dry-run" : "active");
            Invalidate("configured");
        }

        internal static bool BeforeGenerateBackgrounds(PPURenderer renderer)
        {
            EvaluatedFrames++;
            _frameReady = false;
            _readyRenderer = null;
            if (!_enabled) return true;
            if (!TryValidateRuntime(renderer, out var reason))
            {
                Reject(reason);
                StockFallbacks++;
                PeriodicStatus();
                return true;
            }

            var source = FramebufferPresentationApi.Source;
            if (source == null)
            {
                Reject("no-provider");
                StockFallbacks++;
                PeriodicStatus();
                return true;
            }

            try
            {
                Framebuffer.EnsureSize(_width, _height);
                Request.Renderer = renderer;
                Request.Ppu = renderer.snesPPU;
                Request.Width = _width;
                Request.Height = _height;
                if (!source.TryRenderFrame(Request, Framebuffer, out var rowsAreTopDown, out var rejection))
                {
                    Reject(string.IsNullOrEmpty(rejection) ? "provider-unsupported-frame" : "provider-" + rejection);
                    StockFallbacks++;
                    PeriodicStatus();
                    return true;
                }
                ProviderFrames++;
                Surface.Upload(Framebuffer, rowsAreTopDown);
                PredictedSubstitutions++;
                _lastReason = "cpu-frame-ready";
                if (_dryRun)
                {
                    StockFallbacks++;
                    PeriodicStatus();
                    return true;
                }
                _readyRenderer = renderer;
                _frameReady = true;
                ActualMeshBypasses++;
                PeriodicStatus();
                return false;
            }
            catch (Exception exception)
            {
                PresentationFailures++;
                Reject("provider-or-upload-error-" + exception.GetType().Name);
                _log?.LogWarning("CPU framebuffer frame failed closed: " + exception);
                StockFallbacks++;
                PeriodicStatus();
                return true;
            }
        }

        private static bool TryValidateRuntime(PPURenderer renderer, out string reason)
        {
            reason = null;
            if (_width <= 0 || _height <= 0 || _width > 1024 || _height > 512)
            { reason = "invalid-configured-shape"; return false; }
            if (renderer == null || renderer.snesPPU == null || renderer.mainScreenBlitter == null ||
                renderer.mainScreenBlitter.transferRenderTexture == null ||
                _transferMaterialUsedField == null ||
                _transferMaterialUsedField.GetValue(renderer.mainScreenBlitter) as Material == null)
            { reason = "missing-runtime-presentation-chain"; return false; }
            var menu = MainMenuManager.Instance;
            if (menu == null || menu.mainMenuSettings == null)
            { reason = "missing-menu-settings"; return false; }
            var filename = menu.GetLoadedGameFilename() ?? string.Empty;
            if (filename.IndexOf("DKC_Widescreen_358x224", StringComparison.OrdinalIgnoreCase) < 0)
            { reason = "non-dkc-rom"; return false; }
            var ppu = renderer.snesPPU;
            if (ppu._ppuStartFrame == null || ppu._ppuStartFrame.Length < 64 ||
                ppu._ppuLineChanges == null || ppu._curPPUChangeIdx < 0 ||
                ppu._curPPUChangeIdx > ppu._ppuLineChanges.Length)
            { reason = "invalid-ppu-state"; return false; }
            if ((ppu._ppuStartFrame[5] & 7) == 7)
            { reason = "mode7-start-unsupported"; return false; }
            for (var i = 0; i < ppu._curPPUChangeIdx; i++)
                if (ppu._ppuLineChanges[i].address == 0x2105 && (ppu._ppuLineChanges[i].val & 7) == 7)
                { reason = "mode7-scanline-unsupported"; return false; }
            if (renderer.screenMat == null || Math.Abs(renderer.screenMat.GetFloat("_UIFade")) > 0.000001f)
            { reason = "ui-fade-requires-stock-composite"; return false; }
            return true;
        }

        internal static bool TryPresent(MainScreenBlit blitter, RenderTexture destination)
        {
            if (!_enabled || _dryRun || !_frameReady || _readyRenderer == null ||
                !ReferenceEquals(_readyRenderer.mainScreenBlitter, blitter)) return false;
            try
            {
                var source = Surface.PresentationTexture;
                var transfer = blitter.transferRenderTexture;
                var transferMaterial = _transferMaterialUsedField.GetValue(blitter) as Material;
                var menu = MainMenuManager.Instance;
                var settings = menu?.mainMenuSettings;
                if (source == null || transfer == null || transferMaterial == null || destination == null || settings == null)
                    throw new InvalidOperationException("Presentation chain became unavailable after frame acceptance.");

                // CPU output is already final-composed. Copy it into the stock
                // persistent transfer target, then reproduce the second half of
                // MainScreenBlit.OnRenderImage exactly.
                Graphics.Blit(source, transfer);
                var screenFactor = (float)Screen.width / Screen.height * 0.5625f;
                var use87Aspect = settings.use87aspect;
                var gameSettings = menu.GetGameSettings();
                if (gameSettings != null)
                {
                    if (gameSettings.aspectOverride == 1) use87Aspect = false;
                    else if (gameSettings.aspectOverride == 2) use87Aspect = true;
                }
                var aspectDivisor = use87Aspect ? 1f : 1.1666666f;
                transferMaterial.SetVector("_ScreenScale", new Vector4(screenFactor / aspectDivisor, 1f, 0f, 0f));
                transferMaterial.SetVector("_PixelScale", new Vector4(398f, 224f,
                    settings.scanlineStrength * 2f));
                var pixelTexture = settings.gfxMode == MainMenuManager.GFXModes.Scanlines
                    ? blitter.scanline : blitter.white;
                transferMaterial.SetTexture("_PixelTexture", pixelTexture);
                Graphics.Blit(transfer, destination, transferMaterial);
                PresentedFrames++;
                _lastReason = "cpu-frame-presented";
                return true;
            }
            catch (Exception exception)
            {
                PresentationFailures++;
                _frameReady = false;
                _readyRenderer = null;
                Reject("presentation-error-" + exception.GetType().Name);
                _log?.LogError("CPU framebuffer presentation failed; falling through to stock OnRenderImage: " + exception);
                return false;
            }
        }

        internal static void Invalidate(string reason)
        {
            _frameReady = false;
            _readyRenderer = null;
            _lastReason = reason ?? "invalidated";
        }

        internal static void Dispose()
        {
            Invalidate("shutdown");
            Surface.Dispose();
        }

        private static void Reject(string reason)
        {
            reason = string.IsNullOrEmpty(reason) ? "unknown" : reason;
            _lastReason = reason;
            Rejections.TryGetValue(reason, out var count);
            Rejections[reason] = count + 1;
        }

        private static void PeriodicStatus()
        {
            if (EvaluatedFrames % 300 != 0) return;
            try { WriteStatus(_state); }
            catch (Exception exception) { _log?.LogWarning("Could not write framebuffer status: " + exception.Message); }
        }

        internal static void WriteStatus(string state, string detail = "")
        {
            if (string.IsNullOrEmpty(_statusPath)) return;
            _state = state ?? _state;
            var rejections = new List<string>();
            foreach (var pair in Rejections)
                rejections.Add("\"" + Escape(pair.Key) + "\":" + pair.Value);
            var json = "{" +
                       "\"pluginVersion\":\"" + SuperZSNESFramebufferPresentationPrototypePlugin.PluginVersion + "\"," +
                       "\"state\":\"" + Escape(_state) + "\"," +
                       "\"enabled\":" + Bool(_enabled) + "," +
                       "\"dryRun\":" + Bool(_dryRun) + "," +
                       "\"evaluatedFrames\":" + EvaluatedFrames + "," +
                       "\"providerFrames\":" + ProviderFrames + "," +
                       "\"predictedSubstitutions\":" + PredictedSubstitutions + "," +
                       "\"actualMeshBypasses\":" + ActualMeshBypasses + "," +
                       "\"presentedFrames\":" + PresentedFrames + "," +
                       "\"stockFallbacks\":" + StockFallbacks + "," +
                       "\"presentationFailures\":" + PresentationFailures + "," +
                       "\"lastReason\":\"" + Escape(_lastReason) + "\"," +
                       "\"detail\":\"" + Escape(detail ?? "") + "\"," +
                       "\"rejections\":{" + string.Join(",", rejections) + "}" +
                       "}";
            Directory.CreateDirectory(Path.GetDirectoryName(_statusPath));
            File.WriteAllText(_statusPath, json);
        }

        private static string Bool(bool value) => value ? "true" : "false";
        private static string Escape(string value) => (value ?? "").Replace("\\", "\\\\")
            .Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    }
}
