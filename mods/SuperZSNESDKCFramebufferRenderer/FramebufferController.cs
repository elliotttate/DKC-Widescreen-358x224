using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SuperZSNESDKCFramebufferRenderer
{
    internal static class FramebufferController
    {
        private static ManualLogSource _log;
        private static ConfigEntry<bool> _present;
        private static ConfigEntry<int> _shadowInterval;
        private static ConfigEntry<int> _width;
        private static ConfigEntry<int> _height;
        private static ConfigEntry<int> _leftExtension;
        private static ConfigEntry<bool> _retainedBackgrounds;
        private static string _directory;
        private static DkcFrameRasterizer _rasterizer;
        private static Texture2D _texture;
        private static RenderTexture _presentationTexture;
        private static Color32[] _pixels;
        private static bool _frameReady;
        private static bool _captureRequested;
        private static int _frameCalls;
        private static int _renderedFrames;
        private static int _fallbackFrames;
        private static double _totalMs;
        private static double _maxMs;
        private static string _lastFallback = "not-rendered";
        private static string _lastCapture = string.Empty;
        private static PPURenderer _lastRenderer;
        private static bool _activeDisplayVramWrite;
        private static readonly List<FallbackMetric> FallbackMetrics = new List<FallbackMetric>();
        private static FallbackMetric _pendingFallbackMetric;
        private static long _fallbackRendererStarted;
        private static long _measuredFallbackFrames;
        private static double _fallbackRendererTotalMs;
        private static double _fallbackRendererMaxMs;
        private static int _currentFallbackStreak;
        private static int _maxFallbackStreak;
        private static int _currentReasonStreak;
        private static string _currentFallbackReason = string.Empty;
        private static readonly List<SlowRenderEvent> SlowRenderEvents = new List<SlowRenderEvent>();
#if !IL2CPP
        private static readonly FieldInfo TransferMaterialUsed = AccessTools.Field(typeof(MainScreenBlit), "_transferMaterialUsed");
#endif

        internal static void Initialize(ManualLogSource log, ConfigEntry<bool> present,
            ConfigEntry<int> shadowInterval, ConfigEntry<int> width, ConfigEntry<int> height,
            ConfigEntry<int> leftExtension, ConfigEntry<bool> retainedBackgrounds, string directory)
        {
            _log = log;
            _present = present;
            _shadowInterval = shadowInterval;
            _width = width;
            _height = height;
            _leftExtension = leftExtension;
            _retainedBackgrounds = retainedBackgrounds;
            _directory = directory;
            _rasterizer = new DkcFrameRasterizer(retainedBackgrounds.Value);
            ResetTelemetry();
            EnsureSurface();
            WriteStatus("initialized");
        }

        internal static bool BeforeGenerateBackgrounds(PPURenderer renderer)
        {
            _lastRenderer = renderer;
            _frameCalls++;
            bool unsafeVramWrite = _activeDisplayVramWrite;
            _activeDisplayVramWrite = false;
            bool wantRender = _present.Value || _captureRequested ||
                              (_shadowInterval.Value > 0 && _frameCalls % _shadowInterval.Value == 0);
            if (!wantRender)
            {
                ResetFallbackStreak();
                return true;
            }

            if (unsafeVramWrite)
                return BeginFallback(renderer, "active-display-vram-write");

            EnsureSurface();
            double lineStateBefore = _rasterizer.LineStateMs;
            double backgroundsBefore = _rasterizer.BackgroundMs;
            double spritesBefore = _rasterizer.SpriteMs;
            double compositeBefore = _rasterizer.CompositeMs;
            long cacheHitsBefore = _rasterizer.BackgroundCacheHits;
            long cacheMissesBefore = _rasterizer.BackgroundCacheMisses;
            Stopwatch stopwatch = Stopwatch.StartNew();
            bool ok;
            string reason;
            try
            {
                ok = _rasterizer.TryRender(renderer, _width.Value, _height.Value,
                    _leftExtension.Value, _pixels, out reason);
            }
            catch (Exception exception)
            {
                ok = false;
                reason = exception.GetType().Name + ": " + exception.Message;
                _log.LogError("Framebuffer render failed closed: " + exception);
            }
            stopwatch.Stop();

            if (!ok)
                return BeginFallback(renderer, reason ?? "unsupported");

            double ms = stopwatch.Elapsed.TotalMilliseconds;
            _renderedFrames++;
            _totalMs += ms;
            if (ms > _maxMs) _maxMs = ms;
            if (ms >= 8.0)
                AddSlowRender(ms, lineStateBefore, backgroundsBefore, spritesBefore,
                    compositeBefore, cacheHitsBefore, cacheMissesBefore);
            bool endedFallbackStreak = _currentFallbackStreak > 0;
            ResetFallbackStreak();
            _lastFallback = string.Empty;
            _texture.SetPixels32(_pixels);
            _texture.Apply(false, false);
            Graphics.Blit(_texture, _presentationTexture);
            _frameReady = true;

            if (_captureRequested)
            {
                _captureRequested = false;
                WriteCapture();
            }
            if (_renderedFrames == 1 || _renderedFrames % 300 == 0 || endedFallbackStreak)
                WriteStatus(_present.Value ? "presenting" : "shadow");

            if (!_present.Value)
                return true;

            DisableLegacyCameras(renderer);
            return false;
        }

        internal static void AfterGenerateBackgrounds()
        {
            FallbackMetric metric = _pendingFallbackMetric;
            long started = _fallbackRendererStarted;
            _pendingFallbackMetric = null;
            _fallbackRendererStarted = 0;
            if (metric == null || started == 0)
                return;

            double ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            _measuredFallbackFrames++;
            _fallbackRendererTotalMs += ms;
            if (ms > _fallbackRendererMaxMs) _fallbackRendererMaxMs = ms;
            metric.MeasuredFrames++;
            metric.RendererMs += ms;
            if (ms > metric.MaxRendererMs) metric.MaxRendererMs = ms;

            // Keep telemetry useful without synchronously writing to disk on every fallback frame.
            if (_fallbackFrames == 1 || _fallbackFrames % 120 == 0)
                WriteStatus("fallback");
        }

        internal static bool TryPresent(MainScreenBlit blitter, RenderTexture source, RenderTexture destination)
        {
            if (_present == null || !_present.Value || !_frameReady || _texture == null)
                return false;

#if IL2CPP
            Material transfer = blitter._transferMaterialUsed;
#else
            Material transfer = TransferMaterialUsed?.GetValue(blitter) as Material;
#endif
            if (transfer == null)
                return false;

            float scale = (float)Screen.width / Screen.height * 0.5625f;
            bool use87 = MainMenuManager.Instance.mainMenuSettings.use87aspect;
            if (MainMenuManager.Instance.GetGameSettings() != null)
            {
                switch (MainMenuManager.Instance.GetGameSettings().aspectOverride)
                {
                    case 1: use87 = false; break;
                    case 2: use87 = true; break;
                }
            }
            float pixelAspect = use87 ? 1f : 1.1666666f;
            transfer.SetVector("_ScreenScale", new Vector4(scale / pixelAspect, 1f, 0f, 0f));
#if IL2CPP
            transfer.SetVector("_PixelScale", new Vector4(_texture.width, _texture.height,
#else
            transfer.SetVector("_PixelScale", new Vector4(398f, 224f,
#endif
                MainMenuManager.Instance.mainMenuSettings.scanlineStrength * 2f));
            Texture pixelTexture = MainMenuManager.Instance.mainMenuSettings.gfxMode == MainMenuManager.GFXModes.Scanlines
                ? blitter.scanline : blitter.white;
            transfer.SetTexture("_PixelTexture", pixelTexture);
            if (blitter.transferRenderTexture == null || _presentationTexture == null)
                return false;
            Graphics.Blit(_presentationTexture, blitter.transferRenderTexture);
            Graphics.Blit(blitter.transferRenderTexture, destination, transfer);
            return true;
        }

        internal static void RequestCapture()
        {
            _captureRequested = true;
            _log?.LogInfo("Framebuffer capture requested for the next supported DKC frame.");
        }

        internal static void ObservePpuWrite(SNESPPU ppu, uint address)
        {
            if ((address == 0x2118 || address == 0x2119) && ppu != null &&
                ppu.masterExecutor != null && !ppu.masterExecutor.IsInVBlank())
                _activeDisplayVramWrite = true;
        }

        internal static void Shutdown()
        {
            if (_lastRenderer != null)
                RestoreLegacyCameras(_lastRenderer);
            if (_texture != null)
                UnityEngine.Object.Destroy(_texture);
            if (_presentationTexture != null)
            {
                _presentationTexture.Release();
                UnityEngine.Object.Destroy(_presentationTexture);
            }
            _texture = null;
            _presentationTexture = null;
            _pixels = null;
            _rasterizer = null;
            _frameReady = false;
            try { WriteStatus("shutdown"); } catch { }
        }

        private static bool BeginFallback(PPURenderer renderer, string reason)
        {
            reason = string.IsNullOrEmpty(reason) ? "unsupported" : reason;
            _fallbackFrames++;
            _lastFallback = reason;
            _frameReady = false;
            RestoreLegacyCameras(renderer);

            FallbackMetric metric = GetFallbackMetric(reason);
            metric.Frames++;
            _currentFallbackStreak++;
            if (string.Equals(_currentFallbackReason, reason, StringComparison.Ordinal))
            {
                _currentReasonStreak++;
            }
            else
            {
                _currentFallbackReason = reason;
                _currentReasonStreak = 1;
            }
            if (_currentFallbackStreak > _maxFallbackStreak)
                _maxFallbackStreak = _currentFallbackStreak;
            if (_currentReasonStreak > metric.MaxConsecutiveFrames)
                metric.MaxConsecutiveFrames = _currentReasonStreak;

            _pendingFallbackMetric = metric;
            _fallbackRendererStarted = Stopwatch.GetTimestamp();
            return true;
        }

        private static FallbackMetric GetFallbackMetric(string reason)
        {
            for (int i = 0; i < FallbackMetrics.Count; i++)
            {
                if (string.Equals(FallbackMetrics[i].Reason, reason, StringComparison.Ordinal))
                    return FallbackMetrics[i];
            }
            FallbackMetric metric = new FallbackMetric { Reason = reason };
            FallbackMetrics.Add(metric);
            return metric;
        }

        private static void ResetFallbackStreak()
        {
            _currentFallbackStreak = 0;
            _currentReasonStreak = 0;
            _currentFallbackReason = string.Empty;
        }

        private static void ResetTelemetry()
        {
            _frameCalls = 0;
            _renderedFrames = 0;
            _fallbackFrames = 0;
            _totalMs = 0;
            _maxMs = 0;
            _lastFallback = "not-rendered";
            _lastCapture = string.Empty;
            _activeDisplayVramWrite = false;
            _pendingFallbackMetric = null;
            _fallbackRendererStarted = 0;
            _measuredFallbackFrames = 0;
            _fallbackRendererTotalMs = 0;
            _fallbackRendererMaxMs = 0;
            _currentFallbackStreak = 0;
            _maxFallbackStreak = 0;
            _currentReasonStreak = 0;
            _currentFallbackReason = string.Empty;
            FallbackMetrics.Clear();
            SlowRenderEvents.Clear();
        }

        private static void AddSlowRender(double milliseconds, double lineStateBefore,
            double backgroundsBefore, double spritesBefore, double compositeBefore,
            long cacheHitsBefore, long cacheMissesBefore)
        {
            if (SlowRenderEvents.Count >= 32) SlowRenderEvents.RemoveAt(0);
            SlowRenderEvents.Add(new SlowRenderEvent
            {
                FrameCall = _frameCalls,
                Milliseconds = milliseconds,
                LineStateMs = _rasterizer.LineStateMs - lineStateBefore,
                BackgroundMs = _rasterizer.BackgroundMs - backgroundsBefore,
                SpriteMs = _rasterizer.SpriteMs - spritesBefore,
                CompositeMs = _rasterizer.CompositeMs - compositeBefore,
                RebuiltLayers = _rasterizer.LastRebuiltLayers,
                CacheHits = _rasterizer.BackgroundCacheHits - cacheHitsBefore,
                CacheMisses = _rasterizer.BackgroundCacheMisses - cacheMissesBefore,
                RasterEffect = _rasterizer.LastRasterEffect ?? string.Empty
            });
        }

        private static void EnsureSurface()
        {
            int width = Math.Max(256, Math.Min(512, _width.Value));
            int height = Math.Max(200, Math.Min(240, _height.Value));
            if (_texture != null && _presentationTexture != null &&
                _texture.width == width && _texture.height == height)
                return;
            if (_texture != null)
                UnityEngine.Object.Destroy(_texture);
            if (_presentationTexture != null)
            {
                _presentationTexture.Release();
                UnityEngine.Object.Destroy(_presentationTexture);
            }
            _texture = new Texture2D(width, height, TextureFormat.RGBA32, false, false)
            {
                name = "DKC Indexed Framebuffer",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            _presentationTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear)
            {
                name = "DKC Indexed Framebuffer Presentation",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false
            };
            if (!_presentationTexture.Create())
                throw new InvalidOperationException("Framebuffer presentation RenderTexture creation failed.");
            _pixels = new Color32[width * height];
            _frameReady = false;
        }

        private static void DisableLegacyCameras(PPURenderer renderer)
        {
            if (renderer.mainScreenCamera != null) renderer.mainScreenCamera.enabled = false;
            if (renderer.subScreenCamera != null) renderer.subScreenCamera.enabled = false;
            if (renderer.windowCamera != null) renderer.windowCamera.enabled = false;
        }

        private static void RestoreLegacyCameras(PPURenderer renderer)
        {
            if (renderer.mainScreenCamera != null) renderer.mainScreenCamera.enabled = true;
            if (renderer.subScreenCamera != null) renderer.subScreenCamera.enabled = true;
            if (renderer.windowCamera != null) renderer.windowCamera.enabled = true;
        }

        private static void WriteCapture()
        {
            if (_texture == null || string.IsNullOrEmpty(_directory)) return;
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            string path = Path.Combine(_directory, "candidate-" + stamp + ".png");
            File.WriteAllBytes(path, _texture.EncodeToPNG());
            for (int bg = 0; bg < 3; bg++)
            {
                Color32[] layerPixels = _rasterizer?.CreateBackgroundDiagnosticPixels(
                    bg, _texture.width, _texture.height);
                if (layerPixels == null) continue;
                Texture2D layer = new Texture2D(_texture.width, _texture.height,
                    TextureFormat.RGBA32, false, false);
                try
                {
                    layer.SetPixels32(layerPixels);
                    layer.Apply(false, false);
                    File.WriteAllBytes(Path.Combine(_directory,
                        "candidate-" + stamp + "-bg" + (bg + 1) + ".png"), layer.EncodeToPNG());
                }
                finally
                {
                    UnityEngine.Object.Destroy(layer);
                }
            }
            Color32[] mainBackgroundPixels = _rasterizer?.CreateMainBackgroundDiagnosticPixels(
                _texture.width, _texture.height, _leftExtension.Value);
            if (mainBackgroundPixels != null)
            {
                Texture2D mainBackground = new Texture2D(_texture.width, _texture.height,
                    TextureFormat.RGBA32, false, false);
                try
                {
                    mainBackground.SetPixels32(mainBackgroundPixels);
                    mainBackground.Apply(false, false);
                    File.WriteAllBytes(Path.Combine(_directory,
                        "candidate-" + stamp + "-main-backgrounds.png"), mainBackground.EncodeToPNG());
                }
                finally
                {
                    UnityEngine.Object.Destroy(mainBackground);
                }
            }
            _lastCapture = path;
            _log.LogInfo("Wrote framebuffer candidate: " + path);
            WriteStatus("captured", path);
        }

        private static void WriteStatus(string state, string capture = "")
        {
            if (string.IsNullOrEmpty(_directory)) return;
            double average = _renderedFrames == 0 ? 0 : _totalMs / _renderedFrames;
            long stageFrames = _rasterizer?.StageFrames ?? 0;
            double stageDivisor = stageFrames == 0 ? 1 : stageFrames;
            string json = "{" +
#if IL2CPP
                          "\"version\":\"0.1.8-il2cpp\"," +
#else
                          "\"version\":\"0.4.13\"," +
#endif
                          "\"state\":\"" + Escape(state) + "\"," +
                          "\"present\":" + ((_present != null && _present.Value) ? "true" : "false") + "," +
                          "\"retainedBackgrounds\":" + ((_retainedBackgrounds != null && _retainedBackgrounds.Value) ? "true" : "false") + "," +
                          "\"frameCalls\":" + _frameCalls + "," +
                          "\"renderedFrames\":" + _renderedFrames + "," +
                          "\"fallbackFrames\":" + _fallbackFrames + "," +
                          "\"fallbackRate\":" + Number(_frameCalls == 0 ? 0 : 100.0 * _fallbackFrames / _frameCalls) + "," +
                          "\"fallbackRendererAverageMs\":" + Number(_measuredFallbackFrames == 0 ? 0 : _fallbackRendererTotalMs / _measuredFallbackFrames) + "," +
                          "\"fallbackRendererMaxMs\":" + Number(_fallbackRendererMaxMs) + "," +
                          "\"currentFallbackStreak\":" + _currentFallbackStreak + "," +
                          "\"maxFallbackStreak\":" + _maxFallbackStreak + "," +
                          "\"fallbackReasons\":" + FallbackReasonsJson() + "," +
                          "\"backgroundCacheHits\":" + (_rasterizer?.BackgroundCacheHits ?? 0) + "," +
                          "\"backgroundCacheMisses\":" + (_rasterizer?.BackgroundCacheMisses ?? 0) + "," +
                          "\"rasterEffectRebuilds\":" + (_rasterizer?.RasterEffectRebuilds ?? 0) + "," +
                          "\"rasterPartialRebuilds\":" + (_rasterizer?.RasterPartialRebuilds ?? 0) + "," +
                          "\"rasterPartialRows\":" + (_rasterizer?.RasterPartialRows ?? 0) + "," +
                          "\"fixedNativePillarboxFrames\":" + (_rasterizer?.FixedNativePillarboxFrames ?? 0) + "," +
                          "\"fixedNativePillarboxActive\":" + ((_rasterizer?.FixedNativePillarboxActive ?? false) ? "true" : "false") + "," +
                          "\"lastRebuiltLayers\":" + (_rasterizer?.LastRebuiltLayers ?? 0) + "," +
                          "\"perBgHits\":" + LongArray(_rasterizer?.PerBgHits) + "," +
                          "\"perBgMisses\":" + LongArray(_rasterizer?.PerBgMisses) + "," +
                          "\"perBgRasterRebuilds\":" + LongArray(_rasterizer?.PerBgRasterRebuilds) + "," +
                          "\"perBgDecodedTileHits\":" + LongArray(_rasterizer?.PerBgDecodedTileHits) + "," +
                          "\"perBgDecodedTileMisses\":" + LongArray(_rasterizer?.PerBgDecodedTileMisses) + "," +
                          "\"perBgAveragePrepareMs\":" + BackgroundAverages(_rasterizer) + "," +
                          "\"stageAverageMs\":{" +
                          "\"lineState\":" + Number((_rasterizer?.LineStateMs ?? 0) / stageDivisor) + "," +
                          "\"backgrounds\":" + Number((_rasterizer?.BackgroundMs ?? 0) / stageDivisor) + "," +
                          "\"sprites\":" + Number((_rasterizer?.SpriteMs ?? 0) / stageDivisor) + "," +
                          "\"composite\":" + Number((_rasterizer?.CompositeMs ?? 0) / stageDivisor) + "}," +
                          "\"lastRasterEffect\":\"" + Escape(_rasterizer?.LastRasterEffect) + "\"," +
                          "\"slowRenderEvents\":" + SlowRenderEventsJson() + "," +
                          "\"lineDiagnostics\":" + (_rasterizer?.LineDiagnosticsJson ?? "[]") + "," +
                          "\"averageRenderMs\":" + average.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + "," +
                          "\"maxRenderMs\":" + _maxMs.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + "," +
                          "\"lastFallback\":\"" + Escape(_lastFallback) + "\"," +
                          "\"capture\":\"" + Escape(string.IsNullOrEmpty(capture) ? _lastCapture : capture) + "\"}";
            File.WriteAllText(Path.Combine(_directory, "status.json"), json);
        }

        private static string LongArray(long[] values)
        {
            if (values == null) return "[0,0,0]";
            return "[" + values[0] + "," + values[1] + "," + values[2] + "]";
        }

        private static string FallbackReasonsJson()
        {
            string[] values = new string[FallbackMetrics.Count];
            for (int i = 0; i < FallbackMetrics.Count; i++)
            {
                FallbackMetric metric = FallbackMetrics[i];
                double average = metric.MeasuredFrames == 0 ? 0 : metric.RendererMs / metric.MeasuredFrames;
                values[i] = "{\"reason\":\"" + Escape(metric.Reason) + "\"," +
                            "\"frames\":" + metric.Frames + "," +
                            "\"averageStockRendererMs\":" + Number(average) + "," +
                            "\"maxStockRendererMs\":" + Number(metric.MaxRendererMs) + "," +
                            "\"maxConsecutiveFrames\":" + metric.MaxConsecutiveFrames + "}";
            }
            return "[" + string.Join(",", values) + "]";
        }

        private static string SlowRenderEventsJson()
        {
            string[] values = new string[SlowRenderEvents.Count];
            for (int i = 0; i < SlowRenderEvents.Count; i++)
            {
                SlowRenderEvent item = SlowRenderEvents[i];
                values[i] = "{\"frameCall\":" + item.FrameCall + "," +
                            "\"milliseconds\":" + Number(item.Milliseconds) + "," +
                            "\"lineStateMs\":" + Number(item.LineStateMs) + "," +
                            "\"backgroundMs\":" + Number(item.BackgroundMs) + "," +
                            "\"spriteMs\":" + Number(item.SpriteMs) + "," +
                            "\"compositeMs\":" + Number(item.CompositeMs) + "," +
                            "\"rebuiltLayers\":" + item.RebuiltLayers + "," +
                            "\"cacheHits\":" + item.CacheHits + "," +
                            "\"cacheMisses\":" + item.CacheMisses + "," +
                            "\"rasterEffect\":\"" + Escape(item.RasterEffect) + "\"}";
            }
            return "[" + string.Join(",", values) + "]";
        }

        private static string BackgroundAverages(DkcFrameRasterizer rasterizer)
        {
            if (rasterizer == null) return "[0,0,0]";
            string[] values = new string[3];
            for (int i = 0; i < 3; i++)
            {
                long calls = rasterizer.PerBgPrepareCalls[i];
                values[i] = Number(calls == 0 ? 0 : rasterizer.PerBgPrepareMs[i] / calls);
            }
            return "[" + string.Join(",", values) + "]";
        }

        private static string Number(double value)
        {
            return value.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private sealed class FallbackMetric
        {
            internal string Reason;
            internal long Frames;
            internal long MeasuredFrames;
            internal double RendererMs;
            internal double MaxRendererMs;
            internal int MaxConsecutiveFrames;
        }

        private sealed class SlowRenderEvent
        {
            internal int FrameCall;
            internal double Milliseconds;
            internal double LineStateMs;
            internal double BackgroundMs;
            internal double SpriteMs;
            internal double CompositeMs;
            internal int RebuiltLayers;
            internal long CacheHits;
            internal long CacheMisses;
            internal string RasterEffect;
        }
    }
}
