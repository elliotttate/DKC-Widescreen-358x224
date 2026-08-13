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
        public string LevelName { get; set; } = string.Empty;
        public List<GameActorRecord> Actors { get; set; } = new List<GameActorRecord>();
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
        public int Version { get; set; } = 2;
        public Dictionary<string, float> ComponentDepths { get; set; } =
            new Dictionary<string, float>();
        public ForegroundGroundSettings ForegroundGround { get; set; } =
            new ForegroundGroundSettings();
    }

    public sealed class ForegroundGroundSettings
    {
        public bool Enabled { get; set; }
        public int Background { get; set; }
        public int CutY { get; set; } = 184;
        public float Depth { get; set; } = -4f;
        public float OffsetY { get; set; }
        public float SurfaceScaleX { get; set; } = 1.05f;
        public float SurfaceScaleY { get; set; } = 1f;
        public bool FollowGroundEdge { get; set; } = true;
        public int EdgeSearchRadius { get; set; } = 56;
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

    public sealed class GameActorRecord
    {
        public int ActorSlot { get; set; }
        public int SpriteId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int WorldX { get; set; }
        public int WorldY { get; set; }
        public int ScreenX { get; set; }
        public int ScreenY { get; set; }
        public int CurrentPose { get; set; }
        public int DisplayedPose { get; set; }
    }

    public static class DkcSemanticNames
    {
        private static readonly Dictionary<int, string> ActorNames =
            new Dictionary<int, string>
            {
                [0x01]="Donkey Kong", [0x02]="Diddy Kong", [0x05]="Kritter / Krash",
                [0x06]="Klump", [0x09]="Rambi", [0x0A]="Expresso", [0x0B]="Winky",
                [0x0C]="Enguarde", [0x0D]="Squawks", [0x0E]="Nut-throwing Necky",
                [0x10]="Necky nut", [0x14]="Breakable wall", [0x15]="Banana bunch",
                [0x16]="KONG letter", [0x18]="Animal buddy box", [0x19]="Zinger",
                [0x1A]="Klaptrap", [0x1B]="Half tire", [0x1D]="Rolling tire",
                [0x1F]="Floating tire", [0x20]="Mincer", [0x22]="Steel keg",
                [0x23]="Barrel", [0x24]="Rope barrel", [0x25]="Oil drum",
                [0x26]="DK barrel", [0x27]="TNT barrel", [0x28]="Oil fire",
                [0x29]="Slippa", [0x2A]="Barrel piece", [0x2B]="DK barrel letters",
                [0x2C]="Item cache", [0x2F]="Army", [0x30]="Vertical rope",
                [0x31]="Swinging rope", [0x32]="Explosion", [0x34]="Life balloon",
                [0x38]="Barrel cannon", [0x39]="Sprite platform", [0x3C]="Sparkle",
                [0x3D]="Elevator lift", [0x42]="Bananas", [0x43]="Butterfly",
                [0x45]="Animal buddy token", [0x46]="Blue Krusha",
                [0x4A]="Checkpoint barrel", [0x4B]="Mini-Necky",
                [0x4C]="Enemy spawn barrel", [0x4D]="Gnawty", [0x4F]="Flying Necky",
                [0x50]="Manky Kong", [0x51]="Minecart", [0x53]="Chomps",
                [0x54]="Chomps Jr.", [0x55]="Bitesize", [0x56]="Squidge",
                [0x57]="Croctopus", [0x58]="Line-guide platform",
                [0x5A]="Ceiling light", [0x5C]="Fuel can", [0x61]="Clambo",
                [0x62]="Clambo pearl", [0x63]="Light-switch barrel",
                [0x68]="Rockkroc", [0x6A]="Exit door", [0x6B]="Underwater exit door",
                [0x6D]="Minigame barrel", [0x71]="Millstone Gnawty",
                [0x73]="Sign", [0x74]="Giant banana", [0x78]="Grey Krusha"
            };

        private static readonly Dictionary<int, string> LevelNames =
            new Dictionary<int, string>
            {
                [0x00]="Jungle Hijinxs", [0x01]="Reptile Rumble",
                [0x02]="Bouncy Bonanza", [0x03]="Misty Mine",
                [0x04]="Ropey Rampage", [0x05]="Orang-utan Gang",
                [0x06]="Barrel Cannon Canyon", [0x0C]="Manic Mincers",
                [0x0E]="Torchlight Trouble", [0x0F]="Elevator Antics",
                [0x17]="Poison Pond", [0x18]="Snow Barrel Blast",
                [0x19]="Mine Cart Madness", [0x1A]="Platform Perils",
                [0x1B]="Mine Cart Carnage", [0x1C]="Trick Track Trek",
                [0x1D]="Tanked Up Trouble", [0x1E]="Stop & Go Station",
                [0x23]="Loopy Lights", [0x25]="Croctopus Chase",
                [0x26]="Oil Drum Alley", [0x27]="Blackout Basement",
                [0x28]="Millstone Mayhem", [0x29]="Temple Tempest",
                [0x4C]="Gang-Plank Galleon", [0x51]="Slipslide Ride",
                [0x58]="Tree Top Town", [0x59]="Vulture Culture",
                [0x5B]="Ice Age Alley", [0x61]="Coral Capers",
                [0x64]="Rope Bridge Rumble", [0x65]="Forest Frenzy",
                [0x6A]="Winky's Walkway", [0x6B]="Clam City",
                [0x6C]="Boss room"
            };

        public static string Actor(int id) => ActorNames.TryGetValue(id,
            out string name) ? name : "DKC sprite $" + id.ToString("X2");

        public static string Level(int id) => LevelNames.TryGetValue(id,
            out string name) ? name : "DKC level $" + id.ToString("X4");
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

    public static class SpriteDepthOrdering
    {
        public static int RenderOrder(int startSlot, int slot)
        {
            startSlot &= 127;
            slot &= 127;
            return (slot - startSlot + 128) & 127;
        }

        public static float CompressedOffset(int startSlot, int slot,
            float orderSpacing)
        {
            int order = RenderOrder(startSlot, slot);
            orderSpacing = Math.Max(0.0001f, Math.Min(1f / 128f, orderSpacing));
            return order * orderSpacing - order / 128f;
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
