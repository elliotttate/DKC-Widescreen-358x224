using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;

namespace DKCDebugInvincibility
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class DKCDebugInvincibilityPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.dkcdebuginvincibility";
        public const string PluginName = "DKC Debug Invincibility";
        public const string PluginVersion = "0.1.0";

        private static int _programmaticRequest = -1;

        private ConfigEntry<bool> _enabledAtStartup;
        private readonly CheatLease _lease = new CheatLease();
        private object _master;
        private object _romDataIdentity;
        private bool _desired;
        private bool _romValid;
        private string _romValidation = "No ROM has been inspected.";
        private string _romTitle = string.Empty;
        private string _romPath = string.Empty;
        private string _lastResult = "Disabled by default.";
        private string _lastStatusFingerprint = string.Empty;

        public static bool DesiredEnabled { get; private set; }
        public static bool Applied { get; private set; }

        // In-process API for another diagnostic plugin. The request is consumed
        // on Unity's main thread; callers never touch the emulator dictionary.
        public static void SetEnabled(bool enabled)
        {
            Interlocked.Exchange(ref _programmaticRequest, enabled ? 1 : 0);
        }

        private string CommandRoot { get { return Path.Combine(Paths.PluginPath, "DKCDebugInvincibility"); } }

        private void Awake()
        {
            _enabledAtStartup = Config.Bind("General", "EnabledAtStartup", false,
                "Debug-only collision invincibility. Keep false for normal play and performance measurements.");
            _desired = _enabledAtStartup.Value;
            Directory.CreateDirectory(CommandRoot);
            Synchronize(true);
            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded; desired=" + _desired + ".");
        }

        private void Update()
        {
            ProcessCommands();
            Synchronize(false);
        }

        private void OnDestroy()
        {
            var removed = _lease.Release();
            Applied = false;
            if (removed) Logger.LogInfo("Removed owned BFA2A0 runtime override during plugin shutdown.");
        }

        private void ProcessCommands()
        {
            var programmatic = Interlocked.Exchange(ref _programmaticRequest, -1);
            if (programmatic >= 0)
            {
                _desired = programmatic != 0;
                _lastResult = "Consumed in-process SetEnabled(" + _desired.ToString().ToLowerInvariant() + ").";
            }
            if (Consume("enable.request"))
            {
                _desired = true;
                _lastResult = "Consumed enable.request.";
            }
            if (Consume("disable.request"))
            {
                _desired = false;
                _lastResult = "Consumed disable.request.";
            }
            if (Consume("status.request")) WriteStatus(true);
        }

        private bool Consume(string name)
        {
            var path = Path.Combine(CommandRoot, name);
            if (!File.Exists(path)) return false;
            try { File.Delete(path); return true; }
            catch (Exception ex)
            {
                _lastResult = "Could not consume " + name + ": " + ex.Message;
                return false;
            }
        }

        private void Synchronize(bool forceStatus)
        {
            var master = Reflect.Static("MasterExecutor", "Instance");
            if (!ReferenceEquals(master, _master))
            {
                _lease.Release();
                _master = master;
                _romDataIdentity = null;
                forceStatus = true;
            }

            InspectRom(ref forceStatus);
            DesiredEnabled = _desired;
            if (!_desired)
            {
                if (_lease.Release()) _lastResult = "Removed owned BFA2A0 runtime override.";
                Applied = false;
            }
            else if (!_romValid)
            {
                _lease.Release();
                Applied = false;
                _lastResult = "Refused to enable: " + _romValidation;
            }
            else
            {
                var dictionary = Reflect.Get(_master, "cheatCodes") as IDictionary<int, byte>;
                string result;
                Applied = _lease.Apply(dictionary, out result);
                if (!Applied || result != "Debug invincibility is active.") _lastResult = result;
            }
            WriteStatus(forceStatus);
        }

        private void InspectRom(ref bool changed)
        {
            var loader = Reflect.Static("RomLoader", "Instance");
            var data = Reflect.TryCall(loader, "GetRomData") as byte[];
            if (ReferenceEquals(data, _romDataIdentity)) return;
            _romDataIdentity = data;
            _romValid = DkcInvincibilityPatch.ValidateRom(data, out _romValidation);
            var info = Reflect.Get(loader, "romInfo");
            _romTitle = Convert.ToString(Reflect.Get(info, "cartridgeTitle"), CultureInfo.InvariantCulture) ?? string.Empty;
            _romPath = Convert.ToString(Reflect.Get(info, "filename"), CultureInfo.InvariantCulture) ?? string.Empty;
            changed = true;
        }

        private void WriteStatus(bool force)
        {
            var fingerprint = _desired + "|" + Applied + "|" + _romValid + "|" + _romValidation
                + "|" + _romPath + "|" + _lastResult;
            if (!force && fingerprint == _lastStatusFingerprint) return;
            var json = new StringBuilder();
            json.Append("{\n");
            Field(json, "plugin", PluginName, true);
            Field(json, "version", PluginVersion, true);
            json.Append("  \"desiredEnabled\": ").Append(_desired ? "true" : "false").Append(",\n");
            json.Append("  \"applied\": ").Append(Applied ? "true" : "false").Append(",\n");
            json.Append("  \"romValidated\": ").Append(_romValid ? "true" : "false").Append(",\n");
            Field(json, "romValidation", _romValidation, true);
            Field(json, "romTitle", _romTitle, true);
            Field(json, "romPath", _romPath, true);
            Field(json, "override", "BFA2A0=60", true);
            Field(json, "lastResult", _lastResult, true);
            Field(json, "updatedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), false);
            json.Append("}\n");
            try
            {
                AtomicWrite(Path.Combine(CommandRoot, "status.json"), json.ToString());
                _lastStatusFingerprint = fingerprint;
            }
            catch (Exception ex) { Logger.LogWarning("Could not write invincibility status: " + ex.Message); }
        }

        private static void Field(StringBuilder json, string name, string value, bool comma)
        {
            json.Append("  \"").Append(Escape(name)).Append("\": \"").Append(Escape(value ?? string.Empty)).Append('"');
            if (comma) json.Append(',');
            json.Append('\n');
        }

        private static string Escape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static void AtomicWrite(string path, string contents)
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, contents, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
    }
}
