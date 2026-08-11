using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Logging;

namespace SuperZSNESDKCBackgroundStateCache
{
    internal static class CacheController
    {
        private static readonly ExactFrameSnapshot Baseline = new ExactFrameSnapshot();
        private static readonly ExactFrameSnapshot Pending = new ExactFrameSnapshot();
        private static readonly Dictionary<string, long> MissReasons = new Dictionary<string, long>();

        private static bool _enabled;
        private static bool _dryRun;
        private static bool _insideFrame;
        private static bool _skipThisFrame;
        private static bool _pendingReady;
        private static int _pendingEpoch;
        private static int _epoch;
        private static PPURenderer _frameRenderer;
        private static FieldInfo _snesPpuField;
        private static ManualLogSource _log;
        private static string _statusPath;
        private static string _state = "disabled";
        private static string _lastReason = "";

        internal static long Frames;
        internal static long EligibleFrames;
        internal static long PredictedHits;
        internal static long ActualSkippedFrames;
        internal static long GeneratedFrames;
        internal static long SkippedBackgroundCalls;
        internal static long AllowedBackgroundCalls;
        internal static long Invalidations;

        internal static void Configure(bool enabled, bool dryRun, ManualLogSource log, FieldInfo snesPpuField)
        {
            _enabled = enabled;
            _dryRun = dryRun;
            _log = log;
            _snesPpuField = snesPpuField;
            _state = !enabled ? "disabled" : (dryRun ? "dry-run" : "active");
            _insideFrame = false;
            _skipThisFrame = false;
            _pendingReady = false;
            Baseline.Valid = false;
        }

        internal static void BeginFrame(PPURenderer renderer)
        {
            _insideFrame = true;
            _frameRenderer = renderer;
            _skipThisFrame = false;
            _pendingReady = false;
            Frames++;

            if (!_enabled || _snesPpuField == null)
                return;

            if (!FrameView.TryCreate(renderer, _snesPpuField, _epoch, out var view, out var rejection))
            {
                Baseline.Valid = false;
                RecordMiss(rejection);
                GeneratedFrames++;
                PeriodicStatus();
                return;
            }

            EligibleFrames++;
            var mismatch = "cold-or-invalidated";
            if (Baseline.Valid && Baseline.Matches(view, out mismatch))
            {
                PredictedHits++;
                _lastReason = "exact-hit";
                _skipThisFrame = !_dryRun;
                if (_skipThisFrame) ActualSkippedFrames++;
                else GeneratedFrames++;
                PeriodicStatus();
                return;
            }

            RecordMiss(Baseline.Valid ? mismatch : "cold-or-invalidated");
            Pending.Capture(view);
            _pendingEpoch = _epoch;
            _pendingReady = true;
            GeneratedFrames++;
            PeriodicStatus();
        }

        internal static bool AllowGenerateBackground(PPURenderer renderer)
        {
            if (!_insideFrame || !ReferenceEquals(renderer, _frameRenderer) || !_skipThisFrame)
            {
                AllowedBackgroundCalls++;
                return true;
            }
            SkippedBackgroundCalls++;
            return false;
        }

        internal static void EndFrame(PPURenderer renderer)
        {
            if (ReferenceEquals(renderer, _frameRenderer) && _pendingReady && _pendingEpoch == _epoch)
                Baseline.CopyFrom(Pending);
            _pendingReady = false;
            _skipThisFrame = false;
            _insideFrame = false;
            _frameRenderer = null;
        }

        internal static void Invalidate(string reason)
        {
            _epoch++;
            Invalidations++;
            Baseline.Valid = false;
            _pendingReady = false;
            _skipThisFrame = false;
            _lastReason = reason ?? "invalidated";
        }

        private static void RecordMiss(string reason)
        {
            reason = string.IsNullOrEmpty(reason) ? "unknown" : reason;
            _lastReason = reason;
            MissReasons.TryGetValue(reason, out var count);
            MissReasons[reason] = count + 1;
        }

        private static void PeriodicStatus()
        {
            if (Frames % 300 != 0 || string.IsNullOrEmpty(_statusPath)) return;
            try { WriteStatus(_statusPath, _state); }
            catch (Exception exception) { _log?.LogWarning("Could not update background-cache status: " + exception.Message); }
        }

        internal static void WriteStatus(string path, string state, string detail = "")
        {
            if (string.IsNullOrEmpty(path)) return;
            _statusPath = path;
            _state = state ?? _state;
            var reasons = new List<string>();
            foreach (var pair in MissReasons)
                reasons.Add("\"" + Escape(pair.Key) + "\":" + pair.Value);
            var json = "{" +
                       "\"pluginVersion\":\"" + SuperZSNESDKCBackgroundStateCachePlugin.PluginVersion + "\"," +
                       "\"state\":\"" + Escape(_state) + "\"," +
                       "\"enabled\":" + Bool(_enabled) + "," +
                       "\"dryRun\":" + Bool(_dryRun) + "," +
                       "\"frames\":" + Frames + "," +
                       "\"eligibleFrames\":" + EligibleFrames + "," +
                       "\"predictedHits\":" + PredictedHits + "," +
                       "\"actualSkippedFrames\":" + ActualSkippedFrames + "," +
                       "\"generatedFrames\":" + GeneratedFrames + "," +
                       "\"skippedBackgroundCalls\":" + SkippedBackgroundCalls + "," +
                       "\"allowedBackgroundCalls\":" + AllowedBackgroundCalls + "," +
                       "\"invalidations\":" + Invalidations + "," +
                       "\"lastReason\":\"" + Escape(_lastReason) + "\"," +
                       "\"detail\":\"" + Escape(detail ?? "") + "\"," +
                       "\"missReasons\":{" + string.Join(",", reasons) + "}" +
                       "}";
            File.WriteAllText(path, json);
        }

        private static string Bool(bool value) => value ? "true" : "false";
        private static string Escape(string value) => (value ?? "").Replace("\\", "\\\\")
            .Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    }
}
