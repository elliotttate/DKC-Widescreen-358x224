using System;

namespace BepInEx.Logging
{
    internal sealed class ManualLogSource
    {
        public void LogInfo(object value) { }
        public void LogWarning(object value) { }
        public void LogError(object value) { }
    }
}

namespace DKCTilemapInspector
{
    internal static class DKCTilemapInspectorPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.dkctilemapinspector";
        public const string PluginVersion = "0.1.1";
    }
}
