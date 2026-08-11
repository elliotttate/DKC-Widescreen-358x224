using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SuperZSNESPaletteCacheProbe
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class SuperZSNESPaletteCacheProbePlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.palettecacheprobe";
        public const string PluginName = "SuperZSNES Palette Cache Probe";
        public const string PluginVersion = "0.1.0";

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<int> _windowSeconds;
        private Harmony _harmony;
        private StreamWriter _writer;
        private long _windowStart;

        private void Awake()
        {
            _enabled = Config.Bind("Probe", "Enabled", false,
                "Measure palette texture cache misses, stale evictions, and cache-method durations. Startup-only; false installs no patches.");
            _windowSeconds = Config.Bind("Probe", "WindowSeconds", 5,
                new ConfigDescription("Aggregation window in seconds.", new AcceptableValueRange<int>(1, 60)));
            if (!_enabled.Value)
            {
                Logger.LogInfo(PluginName + " " + PluginVersion + " loaded disabled; no target methods were patched and no output file was opened.");
                return;
            }

            var output = Path.Combine(Paths.PluginPath, "SuperZSNESPaletteCacheProbe",
                "session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(output);
            _writer = new StreamWriter(Path.Combine(output, "windows.jsonl"), false) { AutoFlush = true };
            ProbeCounters.Reset();
            _windowStart = Stopwatch.GetTimestamp();

            _harmony = new Harmony(PluginGuid);
            Patch(nameof(TileTextureGen.CalculatePalTexture), nameof(ProbeHooks.CalculatePrefix), nameof(ProbeHooks.CalculatePostfix), new[] { typeof(byte[]) });
            Patch(nameof(TileTextureGen.GenerateTextures), nameof(ProbeHooks.GeneratePrefix), nameof(ProbeHooks.GeneratePostfix), new[] { typeof(int) });
            Patch(nameof(TileTextureGen.ClearCache), nameof(ProbeHooks.ClearPrefix), nameof(ProbeHooks.ClearPostfix), Type.EmptyTypes);
            Logger.LogInfo(PluginName + " armed. Output: " + output);
        }

        private void Patch(string targetName, string prefixName, string postfixName, Type[] parameters)
        {
            var target = AccessTools.Method(typeof(TileTextureGen), targetName, parameters);
            var prefix = AccessTools.Method(typeof(ProbeHooks), prefixName);
            var postfix = AccessTools.Method(typeof(ProbeHooks), postfixName);
            if (target == null || prefix == null || postfix == null)
                throw new MissingMethodException("Palette probe target/hook missing: " + targetName);
            _harmony.Patch(target, new HarmonyMethod(prefix), new HarmonyMethod(postfix));
        }

        private void Update()
        {
            if (_writer == null) return;
            var now = Stopwatch.GetTimestamp();
            var seconds = (double)(now - _windowStart) / Stopwatch.Frequency;
            if (seconds < _windowSeconds.Value) return;
            var snapshot = ProbeCounters.Take();
            _writer.WriteLine(snapshot.ToJson(DateTime.UtcNow, seconds));
            _windowStart = now;
        }

        private void OnDestroy()
        {
            try
            {
                if (_writer != null)
                {
                    var now = Stopwatch.GetTimestamp();
                    _writer.WriteLine(ProbeCounters.Take().ToJson(DateTime.UtcNow,
                        (double)(now - _windowStart) / Stopwatch.Frequency, "shutdown"));
                    _writer.Dispose();
                }
            }
            catch { }
            try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
        }
    }

    internal struct ProbeCallState
    {
        public long Started;
        public int CountBefore;
    }

    internal static class ProbeHooks
    {
        public static void CalculatePrefix(Dictionary<uint, Texture2D> ___paletteTextures, out ProbeCallState __state)
        {
            __state = new ProbeCallState { Started = Stopwatch.GetTimestamp(), CountBefore = ___paletteTextures == null ? 0 : ___paletteTextures.Count };
        }

        public static void CalculatePostfix(Dictionary<uint, Texture2D> ___paletteTextures, ProbeCallState __state)
        {
            ProbeCounters.Calculate(__state.CountBefore, ___paletteTextures == null ? 0 : ___paletteTextures.Count,
                Stopwatch.GetTimestamp() - __state.Started);
        }

        public static void GeneratePrefix(Dictionary<uint, Texture2D> ___paletteTextures, out ProbeCallState __state)
        {
            __state = new ProbeCallState { Started = Stopwatch.GetTimestamp(), CountBefore = ___paletteTextures == null ? 0 : ___paletteTextures.Count };
        }

        public static void GeneratePostfix(Dictionary<uint, Texture2D> ___paletteTextures, ProbeCallState __state)
        {
            ProbeCounters.Generate(__state.CountBefore, ___paletteTextures == null ? 0 : ___paletteTextures.Count,
                Stopwatch.GetTimestamp() - __state.Started);
        }

        public static void ClearPrefix(Dictionary<uint, Texture2D> ___paletteTextures, out ProbeCallState __state)
        {
            __state = new ProbeCallState { Started = Stopwatch.GetTimestamp(), CountBefore = ___paletteTextures == null ? 0 : ___paletteTextures.Count };
        }

        public static void ClearPostfix(Dictionary<uint, Texture2D> ___paletteTextures, ProbeCallState __state)
        {
            ProbeCounters.Clear(__state.CountBefore, ___paletteTextures == null ? 0 : ___paletteTextures.Count,
                Stopwatch.GetTimestamp() - __state.Started);
        }
    }

    internal sealed class ProbeSnapshot
    {
        public long CalculateCalls, Misses, CalculateTicks, MissTicks, CalculateMaxTicks;
        public long GenerateCalls, GenerateTicks, GenerateMaxTicks, Evictions;
        public long ClearCalls, ClearTicks, ClearMaxTicks;
        public int CacheMinimum, CacheMaximum, CacheEnd;

        private static double Microseconds(long ticks) { return ticks * 1000000.0 / Stopwatch.Frequency; }
        private static string N(double value) { return value.ToString("0.###", CultureInfo.InvariantCulture); }

        public string ToJson(DateTime utc, double seconds, string reason = "interval")
        {
            var averageCalculate = CalculateCalls == 0 ? 0 : Microseconds(CalculateTicks) / CalculateCalls;
            var averageMiss = Misses == 0 ? 0 : Microseconds(MissTicks) / Misses;
            var averageGenerate = GenerateCalls == 0 ? 0 : Microseconds(GenerateTicks) / GenerateCalls;
            return "{\"utc\":\"" + utc.ToString("O", CultureInfo.InvariantCulture) + "\",\"reason\":\"" + reason +
                   "\",\"windowSeconds\":" + N(seconds) +
                   ",\"calculate\":{\"calls\":" + CalculateCalls + ",\"misses\":" + Misses +
                   ",\"hitRate\":" + N(CalculateCalls == 0 ? 0 : (double)(CalculateCalls - Misses) / CalculateCalls) +
                   ",\"avgUs\":" + N(averageCalculate) + ",\"missAvgUs\":" + N(averageMiss) +
                   ",\"maxUs\":" + N(Microseconds(CalculateMaxTicks)) + "}" +
                   ",\"generate\":{\"calls\":" + GenerateCalls + ",\"evictions\":" + Evictions +
                   ",\"avgUs\":" + N(averageGenerate) + ",\"maxUs\":" + N(Microseconds(GenerateMaxTicks)) + "}" +
                   ",\"clear\":{\"calls\":" + ClearCalls + ",\"totalUs\":" + N(Microseconds(ClearTicks)) +
                   ",\"maxUs\":" + N(Microseconds(ClearMaxTicks)) + "}" +
                   ",\"cache\":{\"min\":" + CacheMinimum + ",\"max\":" + CacheMaximum + ",\"end\":" + CacheEnd + "}}";
        }
    }

    internal static class ProbeCounters
    {
        private static ProbeSnapshot _value;

        public static void Reset() { _value = NewSnapshot(); }
        private static ProbeSnapshot NewSnapshot() { return new ProbeSnapshot { CacheMinimum = int.MaxValue }; }
        private static void Count(int value)
        {
            if (value < _value.CacheMinimum) _value.CacheMinimum = value;
            if (value > _value.CacheMaximum) _value.CacheMaximum = value;
            _value.CacheEnd = value;
        }
        private static void Max(ref long current, long value) { if (value > current) current = value; }

        public static void Calculate(int before, int after, long ticks)
        {
            _value.CalculateCalls++;
            _value.CalculateTicks += ticks;
            Max(ref _value.CalculateMaxTicks, ticks);
            if (after > before)
            {
                _value.Misses += after - before;
                _value.MissTicks += ticks;
            }
            Count(after);
        }

        public static void Generate(int before, int after, long ticks)
        {
            _value.GenerateCalls++;
            _value.GenerateTicks += ticks;
            Max(ref _value.GenerateMaxTicks, ticks);
            if (after < before) _value.Evictions += before - after;
            Count(after);
        }

        public static void Clear(int before, int after, long ticks)
        {
            _value.ClearCalls++;
            _value.ClearTicks += ticks;
            Max(ref _value.ClearMaxTicks, ticks);
            Count(before);
            Count(after);
        }

        public static ProbeSnapshot Take()
        {
            var result = _value ?? NewSnapshot();
            if (result.CacheMinimum == int.MaxValue) result.CacheMinimum = result.CacheEnd;
            _value = NewSnapshot();
            return result;
        }
    }
}
