using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SuperZSNESSpriteDepthStudio
{
    public sealed class SpriteDepthProfile
    {
        public int Version { get; set; } = 1;
        public string RomFileName { get; set; } = string.Empty;
        public string RomSha256 { get; set; } = string.Empty;
        public List<SpriteDepthRule> Rules { get; set; } = new List<SpriteDepthRule>();
    }

    public sealed class SpriteDepthRule
    {
        public string MatchMode { get; set; } = "slot";
        public int Slot { get; set; }
        public int TileBank { get; set; }
        public int Palette { get; set; }
        public int Priority { get; set; }
        public int NameSelect { get; set; }
        public bool Large { get; set; }
        public int SizeSelector { get; set; }
        public int DepthLayer { get; set; }
        public string Note { get; set; } = string.Empty;

        public bool Matches(SpriteRecord sprite)
        {
            if (sprite == null) return false;
            if (string.Equals(MatchMode, "slot", StringComparison.OrdinalIgnoreCase))
                return Slot == sprite.Slot;
            return (sprite.Tile >> 4) == TileBank && sprite.Palette == Palette &&
                   sprite.Priority == Priority && sprite.NameSelect == NameSelect &&
                   sprite.Large == Large && sprite.SizeSelector == SizeSelector;
        }

        public static SpriteDepthRule From(SpriteRecord sprite, bool allMatching,
            int depthLayer)
        {
            if (sprite == null) throw new ArgumentNullException(nameof(sprite));
            return new SpriteDepthRule
            {
                MatchMode = allMatching ? "appearance" : "slot",
                Slot = sprite.Slot,
                TileBank = sprite.Tile >> 4,
                Palette = sprite.Palette,
                Priority = sprite.Priority,
                NameSelect = sprite.NameSelect,
                Large = sprite.Large,
                SizeSelector = sprite.SizeSelector,
                DepthLayer = depthLayer,
                Note = "OAM #" + sprite.Slot + " tile $" + sprite.Tile.ToString("X2")
            };
        }
    }

    public sealed class SpriteCaptureManifest
    {
        public int Version { get; set; } = 1;
        public string CapturedUtc { get; set; } = string.Empty;
        public string RomPath { get; set; } = string.Empty;
        public string RomFileName { get; set; } = string.Empty;
        public string RomSha256 { get; set; } = string.Empty;
        public string ProfileFile { get; set; } = string.Empty;
        public int PriorityAddress { get; set; }
        public int MidFrameOamWrites { get; set; }
        public int MidFrameObjSelWrites { get; set; }
        public int MidFrameCgramWrites { get; set; }
        public int CgramBytes { get; set; }
        public int ActiveSpriteCount { get; set; }
        public string Level { get; set; } = string.Empty;
        public int[] BackgroundScrollX { get; set; } = new int[3];
        public int[] BackgroundScrollY { get; set; } = new int[3];
        public float BackgroundDepthStep { get; set; } = 0.08f;
        public int VisibleBackgroundObjectCount { get; set; }
        public string OamFile { get; set; } = "snapshot-oam.bin";
        public string VramFile { get; set; } = "snapshot-vram.bin";
        public string CgramFile { get; set; } = "snapshot-cgram.bin";
        public string ObjSelFile { get; set; } = "snapshot-objsel.bin";
        public string ObjActiveFile { get; set; } = "snapshot-objactive.bin";
        public string RegistersFile { get; set; } = "snapshot-registers.bin";
        public string ComponentReportFile { get; set; } = "snapshot-components.json";
        public string ComponentProfileFile { get; set; } = string.Empty;
    }

    public sealed class BackgroundComponentReport
    {
        public int Version { get; set; } = 1;
        public string Level { get; set; } = string.Empty;
        public float Spacing { get; set; } = 0.08f;
        public string SafetyRule { get; set; } = string.Empty;
        public List<BackgroundComponentInfo> Components { get; set; } =
            new List<BackgroundComponentInfo>();
    }

    public sealed class BackgroundComponentInfo
    {
        public string Id { get; set; } = string.Empty;
        public int Background { get; set; }
        public int TileCount { get; set; }
        public int MinX { get; set; }
        public int MinY { get; set; }
        public int MaxX { get; set; }
        public int MaxY { get; set; }
        public float Depth { get; set; }
        public int[] Addresses { get; set; } = Array.Empty<int>();
    }

    public sealed class BackgroundDepthProfile
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, float> ComponentDepths { get; set; } =
            new Dictionary<string, float>();
    }

    public sealed class BackgroundObjectRecord
    {
        public string Id { get; set; } = string.Empty;
        public int Background { get; set; }
        public int TileCount { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int OpaquePixels { get; set; }
        public float AutomaticDepth { get; set; }
        public uint[] Pixels { get; set; } = Array.Empty<uint>();

        public string Identity => "BG" + (Background + 1) + "  " +
            (Id.Length > 24 ? Id.Substring(Id.Length - 24) : Id);
    }

    public sealed class SpriteRecord
    {
        public int Slot { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Tile { get; set; }
        public int Attributes { get; set; }
        public int Palette { get; set; }
        public int Priority { get; set; }
        public int NameSelect { get; set; }
        public bool Large { get; set; }
        public int SizeSelector { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int OpaquePixels { get; set; }
        public bool IntersectsScreen { get; set; }
        public uint[] Pixels { get; set; } = Array.Empty<uint>();

        public string Identity => "#" + Slot.ToString("000") + "  $" +
            Tile.ToString("X2") + "  " + Width + "x" + Height;
    }

    public static class SpriteDepthRules
    {
        public static int Resolve(SpriteDepthProfile profile, SpriteRecord sprite)
        {
            if (profile?.Rules == null || sprite == null) return 0;
            for (int i = profile.Rules.Count - 1; i >= 0; i--)
                if (profile.Rules[i] != null && profile.Rules[i].Matches(sprite))
                    return Math.Max(-12, Math.Min(12, profile.Rules[i].DepthLayer));
            return 0;
        }

        public static bool IsAppearanceRule(SpriteDepthProfile profile, SpriteRecord sprite)
        {
            if (profile?.Rules == null || sprite == null) return false;
            for (int i = profile.Rules.Count - 1; i >= 0; i--)
                if (profile.Rules[i] != null && profile.Rules[i].Matches(sprite))
                    return string.Equals(profile.Rules[i].MatchMode, "appearance",
                        StringComparison.OrdinalIgnoreCase);
            return false;
        }

        public static void Set(SpriteDepthProfile profile, SpriteRecord sprite,
            bool allMatching, int depthLayer)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (sprite == null) throw new ArgumentNullException(nameof(sprite));
            profile.Rules ??= new List<SpriteDepthRule>();
            profile.Rules.RemoveAll(rule => rule != null &&
                ((string.Equals(rule.MatchMode, "slot", StringComparison.OrdinalIgnoreCase) &&
                  rule.Slot == sprite.Slot) ||
                 (string.Equals(rule.MatchMode, "appearance", StringComparison.OrdinalIgnoreCase) &&
                  rule.TileBank == (sprite.Tile >> 4) && rule.Palette == sprite.Palette &&
                  rule.Priority == sprite.Priority && rule.NameSelect == sprite.NameSelect &&
                  rule.Large == sprite.Large && rule.SizeSelector == sprite.SizeSelector)));
            if (depthLayer != 0)
                profile.Rules.Add(SpriteDepthRule.From(sprite, allMatching,
                    Math.Max(-12, Math.Min(12, depthLayer))));
        }
    }

    public static class SpriteDepthFiles
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static T ReadJson<T>(string path) where T : class
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        }

        public static void WriteJsonAtomic<T>(string path, T value)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        public static string SafeProfileName(string romFileName, string sha256)
        {
            string stem = Path.GetFileNameWithoutExtension(romFileName ?? string.Empty);
            foreach (char invalid in Path.GetInvalidFileNameChars()) stem = stem.Replace(invalid, '_');
            if (string.IsNullOrWhiteSpace(stem)) stem = "unknown-rom";
            string suffix = string.IsNullOrEmpty(sha256) ? "unknown" :
                sha256.Substring(0, Math.Min(12, sha256.Length)).ToLowerInvariant();
            return stem + "-" + suffix + ".json";
        }
    }
}
