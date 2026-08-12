using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;

namespace SuperZSNESNativeAtlasDirtyFixIL2CPP
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class NativeAtlasDirtyFixPlugin : BasePlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.nativeatlasdirtyfix.il2cpp";
        public const string PluginName = "SuperZSNES Native Atlas Dirty Fix IL2CPP";
        public const string PluginVersion = "0.1.0";

        private NativeAtlasPatcher _patcher;
        private string _statusPath;

        public override void Load()
        {
            ConfigEntry<bool> enabled = Config.Bind("Patch", "Enabled", false,
                "Apply the exact v0.300 x86 native atlas dirty-flag correction at startup.");
            Config.Save();
            string directory = Path.Combine(Paths.PluginPath, "SuperZSNESNativeAtlasDirtyFixIL2CPP");
            Directory.CreateDirectory(directory);
            _statusPath = Path.Combine(directory, "status.json");

            if (!enabled.Value)
            {
                WriteStatus("disabled", string.Empty);
                Log.LogInfo(PluginName + " disabled; no native memory was changed.");
                return;
            }

            _patcher = new NativeAtlasPatcher(message => Log.LogWarning(message));
            try
            {
                _patcher.Apply(Path.Combine(Paths.GameRootPath, "GameAssembly.dll"));
                WriteStatus("active", string.Empty);
                Log.LogWarning(PluginName + " active. The on-disk GameAssembly.dll remains unchanged.");
            }
            catch (Exception exception)
            {
                try { _patcher.Dispose(); } catch { }
                WriteStatus("failed-closed", exception.ToString());
                Log.LogError(PluginName + " failed closed: " + exception);
            }
        }

        public override bool Unload()
        {
            try { _patcher?.Dispose(); }
            finally { WriteStatus("unloaded", string.Empty); }
            return true;
        }

        private void WriteStatus(string state, string error)
        {
            if (string.IsNullOrEmpty(_statusPath)) return;
            string json = "{" +
                          "\"version\":\"" + PluginVersion + "\"," +
                          "\"state\":\"" + Escape(state) + "\"," +
                          "\"applied\":" + ((_patcher?.Applied ?? false) ? "true" : "false") + "," +
                          "\"gameAssemblySha256\":\"" + Escape(_patcher?.GameAssemblySha256) + "\"," +
                          "\"moduleBase\":\"" + Escape(_patcher?.ModuleBaseHex) + "\"," +
                          "\"trampolineBase\":\"" + Escape(_patcher?.TrampolineBaseHex) + "\"," +
                          "\"patchedSites\":" + ((_patcher?.Applied ?? false) ? 6 : 0) + "," +
                          "\"managedHotPathCallbacks\":0," +
                          "\"error\":\"" + Escape(error) + "\"}";
            string temporary = _statusPath + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, _statusPath, true);
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }
}
