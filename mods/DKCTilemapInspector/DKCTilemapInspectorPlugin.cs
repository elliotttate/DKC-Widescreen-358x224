using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace DKCTilemapInspector
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class DKCTilemapInspectorPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.dkctilemapinspector";
        public const string PluginName = "DKC Tilemap Inspector";
        public const string PluginVersion = "0.1.1";

        private ConfigEntry<KeyCode> _captureKey;
        private ConfigEntry<string> _layers;
        private ConfigEntry<int> _nativeWidth;
        private ConfigEntry<int> _targetWideWidth;
        private ConfigEntry<int> _rendererExtraTiles;
        private ConfigEntry<int> _viewHeight;
        private ConfigEntry<double> _seamThreshold;
        private ConfigEntry<int> _autoCaptureInterval;
        private ConfigEntry<bool> _bridgeEnabled;
        private ConfigEntry<int> _bridgePort;
        private TilemapCaptureService _capture;
        private LoopbackBridge _bridge;
        private object _master;
        private CaptureResult _latest;
        private int _lastAutoFrame = -1;

        private void Awake()
        {
            _captureKey = Config.Bind("Capture", "Hotkey", KeyCode.F11, "Capture BG tilemaps, CSV diagnostics, and reconstructed PNGs.");
            _layers = Config.Bind("Capture", "Layers", "1,2", "Comma-separated background layers to capture (BG1/BG2 only).");
            _nativeWidth = Config.Bind("Viewport", "NativeWidth", 256, "Native SNES viewport width in pixels.");
            _targetWideWidth = Config.Bind("Viewport", "TargetWideWidth", 358, "Final desired widescreen width, drawn in yellow on annotated images.");
            _rendererExtraTiles = Config.Bind("Viewport", "RendererExtraTilesPerSide", 7, "Extra 8-pixel columns generated at each side by the widescreen renderer.");
            _viewHeight = Config.Bind("Viewport", "Height", 224, "Viewport height in pixels.");
            _seamThreshold = Config.Bind("Heuristics", "HighSeamThreshold", 0.42, "0-1 RGB edge-difference threshold for a discontinuity candidate.");
            _autoCaptureInterval = Config.Bind("Capture", "AutoCaptureEveryFrames", 0, "Capture every N emulated frames; zero disables automatic capture.");
            _bridgeEnabled = Config.Bind("Bridge", "Enabled", true, "Enable the authenticated localhost command bridge.");
            _bridgePort = Config.Bind("Bridge", "Port", 17817, "Loopback bridge port; zero selects an available port.");

            var root = Path.Combine(Paths.PluginPath, "DKCTilemapInspector", "Captures");
            _capture = new TilemapCaptureService(root, Logger);
            if (_bridgeEnabled.Value)
            {
                _bridge = new LoopbackBridge(Path.Combine(Paths.PluginPath, "DKCTilemapInspector", "bridge.json"), Logger);
                try { _bridge.Start(_bridgePort.Value); }
                catch (Exception ex) { Logger.LogError("Could not start tilemap inspector bridge: " + ex); }
            }
            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded. Press " + _captureKey.Value + " to capture BG1/BG2.");
        }

        private void Update()
        {
            _master = Reflect.Static("MasterExecutor", "Instance");
            ProcessBridgeRequests();
            if (Input.GetKeyDown(_captureKey.Value)) TryCapture("hotkey", ParseLayers(_layers.Value));

            if (_master != null && _autoCaptureInterval.Value > 0)
            {
                var frame = Reflect.IntCall(_master, "GetFrameNo", -1);
                if (frame >= 0 && frame != _lastAutoFrame && frame % _autoCaptureInterval.Value == 0)
                {
                    _lastAutoFrame = frame;
                    TryCapture("automatic", ParseLayers(_layers.Value));
                }
            }
        }

        private void OnDestroy()
        {
            if (_bridge != null) _bridge.Dispose();
        }

        private void TryCapture(string reason, IList<int> layers)
        {
            try { _latest = _capture.Capture(_master, reason, layers, CurrentOptions()); }
            catch (Exception ex) { Logger.LogError("Tilemap capture failed: " + ex); }
        }

        private CaptureOptions CurrentOptions()
        {
            return new CaptureOptions
            {
                NativeWidth = Math.Max(8, _nativeWidth.Value),
                TargetWideWidth = Math.Max(8, _targetWideWidth.Value),
                RendererExtraTiles = Math.Max(0, _rendererExtraTiles.Value),
                ViewHeight = Math.Max(8, _viewHeight.Value),
                HighSeamThreshold = Math.Max(0, Math.Min(1, _seamThreshold.Value))
            };
        }

        private void ProcessBridgeRequests()
        {
            if (_bridge == null) return;
            BridgeRequest request;
            while (_bridge.TryDequeue(out request))
            {
                try
                {
                    switch ((request.Command ?? string.Empty).Trim().ToLowerInvariant())
                    {
                        case "status":
                            request.ResultJson = StatusJson();
                            break;
                        case "latest":
                            request.ResultJson = _latest == null ? "null" : _latest.ToJson();
                            break;
                        case "capture":
                            string reason;
                            string layerText;
                            request.Arguments.TryGetValue("reason", out reason);
                            request.Arguments.TryGetValue("layers", out layerText);
                            _latest = _capture.Capture(_master, string.IsNullOrWhiteSpace(reason) ? "bridge" : reason,
                                ParseLayers(string.IsNullOrWhiteSpace(layerText) ? _layers.Value : layerText), CurrentOptions());
                            request.ResultJson = _latest.ToJson();
                            break;
                        default:
                            throw new ArgumentException("Unknown command. Supported commands: status, capture, latest.");
                    }
                }
                catch (Exception ex) { request.Error = ex; }
                finally { request.SignalCompleted(); }
            }
        }

        private string StatusJson()
        {
            return Json.Object(new Dictionary<string, object>
            {
                { "attached", _master != null },
                { "frame", _master == null ? -1 : Reflect.IntCall(_master, "GetFrameNo", -1) },
                { "configuredLayers", ParseLayers(_layers.Value) },
                { "nativeWidth", _nativeWidth.Value }, { "targetWideWidth", _targetWideWidth.Value },
                { "rendererExtraTilesPerSide", _rendererExtraTiles.Value },
                { "latest", _latest == null ? null : _latest.Folder }
            });
        }

        private static List<int> ParseLayers(string value)
        {
            var result = new List<int>();
            foreach (var part in (value ?? string.Empty).Split(','))
            {
                int layer;
                if (int.TryParse(part.Trim().Replace("BG", string.Empty).Replace("bg", string.Empty),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out layer) && (layer == 1 || layer == 2))
                    result.Add(layer);
            }
            if (result.Count == 0) { result.Add(1); result.Add(2); }
            return result.Distinct().ToList();
        }
    }
}
