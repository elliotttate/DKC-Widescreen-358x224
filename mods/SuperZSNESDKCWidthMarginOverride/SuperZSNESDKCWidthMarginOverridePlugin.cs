using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace SuperZSNESDKCWidthMarginOverride
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class SuperZSNESDKCWidthMarginOverridePlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.dkcwidthmarginoverride";
        public const string PluginName = "SuperZSNES DKC Width Margin Override";
        public const string PluginVersion = "0.1.0";

        private Harmony _harmony;

        private void Awake()
        {
            var enabled = Config.Bind("Experiment", "Enabled", false,
                "Enable the DKC-only GenerateBackground observer. False applies no Harmony patch.");
            var apply = Config.Bind("Experiment", "ApplyOverride", false,
                "Actually substitute the candidate BG margin during GenerateBackground. False is dry-run only.");
            var candidate = Config.Bind("Experiment", "CandidateBGMargin", 6,
                new ConfigDescription("Per-side BG margin to model or apply.", new AcceptableValueRange<int>(0, 16)));
            var expected = Config.Bind("Safety", "ExpectedCurrentBGMargin", 7,
                new ConfigDescription("Fail-closed eligibility value.", new AcceptableValueRange<int>(0, 16)));
            var filenameGate = Config.Bind("Safety", "FilenameContains", "DKC_Widescreen_358x224",
                "Case-insensitive loaded-filename gate. Empty values are rejected.");

            WidthMarginRuntime.Configure(Logger, apply.Value, candidate.Value, expected.Value, filenameGate.Value);
            WidthMarginRuntime.WriteStatus(enabled.Value ? "starting" : "disabled");
            if (!enabled.Value)
            {
                Logger.LogInfo(PluginName + " " + PluginVersion + " is disabled; no Harmony patch was applied. " +
                               WidthMath.Describe(candidate.Value));
                return;
            }

            var renderer = AccessTools.TypeByName("PPURenderer");
            var target = renderer == null ? null : renderer.GetMethods(AccessTools.all)
                .SingleOrDefault(method => method.Name == "GenerateBackground" &&
                                           method.ReturnType == typeof(void) &&
                                           method.GetParameters().Length == 2 &&
                                           method.GetParameters()[0].ParameterType == typeof(int) &&
                                           method.GetParameters()[1].ParameterType.Name == "BGData");
            var wideField = renderer == null ? null : AccessTools.Field(renderer, "wideScreenLengths");
            if (target == null || wideField == null || wideField.FieldType != typeof(List<int>))
                throw new MissingMemberException("Exact SuperZSNES v0.230 GenerateBackground/wideScreenLengths shape was not found.");
            if (filenameGate.Value.Length == 0)
                throw new InvalidOperationException("The DKC filename gate may not be empty.");

            WidthMarginRuntime.SetWideField(wideField);
            _harmony = new Harmony(PluginGuid);
            _harmony.Patch(target,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(GenerateBackgroundPatch), nameof(GenerateBackgroundPatch.Prefix))),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(GenerateBackgroundPatch), nameof(GenerateBackgroundPatch.Finalizer))));
            WidthMarginRuntime.WriteStatus(apply.Value ? "armed-apply" : "armed-dry-run");
            Logger.LogWarning((apply.Value ? "APPLY" : "DRY-RUN") + " DKC BG margin experiment armed. " +
                              WidthMath.Describe(candidate.Value));
        }

        private void OnDestroy()
        {
            try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
        }
    }

    public static class WidthMath
    {
        public const int NativeWidth = 256;
        public const int DefaultColumns = 33;
        public const int TargetWidth = 358;

        public static int RawColumns(int margin) { return DefaultColumns + margin * 2; }
        public static int RawColumnPixels(int margin) { return RawColumns(margin) * 8; }
        public static int ClampWidthPixels(int margin) { return NativeWidth + margin * 16; }
        public static int RequiredMargin(int targetWidth)
        {
            return Math.Max(0, (targetWidth - NativeWidth + 15) / 16);
        }
        public static double PerSideGuardPixels(int margin, int targetWidth)
        {
            return (ClampWidthPixels(margin) - targetWidth) / 2.0;
        }
        public static string Describe(int margin)
        {
            return "candidate=" + margin + ", rawColumns=" + RawColumns(margin) +
                   ", rawPixels=" + RawColumnPixels(margin) + ", clampedPixels=" + ClampWidthPixels(margin) +
                   ", targetPixels=" + TargetWidth + ", perSideGuard=" +
                   PerSideGuardPixels(margin, TargetWidth).ToString("0.###", CultureInfo.InvariantCulture) +
                   ", requiredMargin=" + RequiredMargin(TargetWidth) + ".";
        }
    }

    internal struct OverrideState
    {
        internal List<int> Values;
        internal int Index;
        internal int Original;
        internal bool Changed;
    }

    internal static class GenerateBackgroundPatch
    {
        public static void Prefix(object __instance, int __0, ref OverrideState __state)
        {
            WidthMarginRuntime.Before(__instance, __0, ref __state);
        }

        public static Exception Finalizer(Exception __exception, ref OverrideState __state)
        {
            WidthMarginRuntime.Restore(ref __state);
            return __exception;
        }
    }

    internal static class WidthMarginRuntime
    {
        private static BepInEx.Logging.ManualLogSource _log;
        private static FieldInfo _wideField;
        private static bool _apply;
        private static int _candidate;
        private static int _expected;
        private static string _filenameGate;
        private static MethodInfo _getLoadedFilename;
        private static PropertyInfo _managerInstance;
        private static long _calls;
        private static long _eligible;
        private static long _applied;
        private static long _gateRejects;
        private static string _lastFilename = string.Empty;
        private static string _lastState = "created";

        internal static void Configure(BepInEx.Logging.ManualLogSource log, bool apply, int candidate,
            int expected, string filenameGate)
        {
            _log = log;
            _apply = apply;
            _candidate = candidate;
            _expected = expected;
            _filenameGate = filenameGate ?? string.Empty;
            var manager = AccessTools.TypeByName("MainMenuManager");
            _managerInstance = manager == null ? null : AccessTools.Property(manager, "Instance");
            _getLoadedFilename = manager == null ? null : AccessTools.Method(manager, "GetLoadedGameFilename");
        }

        internal static void SetWideField(FieldInfo field) { _wideField = field; }

        internal static void Before(object renderer, int bgIndex, ref OverrideState state)
        {
            _calls++;
            if (bgIndex < 0 || bgIndex > 3 || _wideField == null) return;
            var filename = GetFilename();
            _lastFilename = filename;
            if (_filenameGate.Length == 0 || filename.IndexOf(_filenameGate, StringComparison.OrdinalIgnoreCase) < 0)
            {
                _gateRejects++;
                MaybeWriteStatus("filename-rejected");
                return;
            }

            var values = _wideField.GetValue(renderer) as List<int>;
            if (values == null || values.Count <= bgIndex || values[bgIndex] != _expected)
            {
                MaybeWriteStatus("shape-or-value-rejected");
                return;
            }

            _eligible++;
            if (_apply)
            {
                state.Values = values;
                state.Index = bgIndex;
                state.Original = values[bgIndex];
                state.Changed = true;
                values[bgIndex] = _candidate;
                _applied++;
            }
            MaybeWriteStatus(_apply ? "applied-temporarily" : "eligible-dry-run");
        }

        internal static void Restore(ref OverrideState state)
        {
            if (!state.Changed || state.Values == null || state.Index < 0 || state.Index >= state.Values.Count) return;
            state.Values[state.Index] = state.Original;
            state.Changed = false;
        }

        private static string GetFilename()
        {
            try
            {
                var manager = _managerInstance == null ? null : _managerInstance.GetValue(null, null);
                return manager == null || _getLoadedFilename == null
                    ? string.Empty
                    : (_getLoadedFilename.Invoke(manager, null) as string ?? string.Empty);
            }
            catch { return string.Empty; }
        }

        private static void MaybeWriteStatus(string state)
        {
            _lastState = state;
            if (_calls <= 4 || _calls % 240 == 0) WriteStatus(state);
        }

        internal static void WriteStatus(string state)
        {
            _lastState = state;
            try
            {
                var directory = Path.Combine(Paths.PluginPath, "SuperZSNESDKCWidthMarginOverride");
                Directory.CreateDirectory(directory);
                var json = "{\"utc\":\"" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) +
                           "\",\"state\":\"" + Escape(_lastState) + "\",\"apply\":" +
                           (_apply ? "true" : "false") + ",\"candidateMargin\":" + _candidate +
                           ",\"expectedMargin\":" + _expected + ",\"targetWidth\":" + WidthMath.TargetWidth +
                           ",\"rawColumns\":" + WidthMath.RawColumns(_candidate) +
                           ",\"clampedWidth\":" + WidthMath.ClampWidthPixels(_candidate) +
                           ",\"requiredMargin\":" + WidthMath.RequiredMargin(WidthMath.TargetWidth) +
                           ",\"calls\":" + _calls + ",\"eligible\":" + _eligible +
                           ",\"applied\":" + _applied + ",\"gateRejects\":" + _gateRejects +
                           ",\"lastFilename\":\"" + Escape(_lastFilename) + "\"}";
                File.WriteAllText(Path.Combine(directory, "status.json"), json);
            }
            catch (Exception ex)
            {
                if (_log != null) _log.LogWarning("Could not write width-margin status: " + ex.Message);
            }
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
