using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using BepInEx.Configuration;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace SuperZSNESLayerDepthControllerIL2CPP
{
    internal sealed class ConnectedComponentDepthMapper
    {
        private readonly NativeTileDepthPatcher _native;
        private readonly ManualLogSource _log;
        private readonly ConfigEntry<int> _depthBands;
        private readonly ConfigEntry<float> _spacing;
        private readonly ConfigEntry<int> _minimumTiles;
        private readonly ConfigEntry<int> _maximumAutoTiles;
        private readonly ConfigEntry<int> _refreshIntervalFrames;
        private readonly string _directory;
        private readonly string _profilesDirectory;
        private byte[] _vram = new byte[65536];
        private readonly float[] _depthTable =
            new float[NativeTileDepthPatcher.DepthTableCount];
        private readonly byte[][] _usedTiles =
        {
            new byte[1024], new byte[1024], new byte[1024]
        };
        private ulong _lastFingerprint;
        private ulong _lastMappingFingerprint;
        private bool _hasFingerprint;
        private bool _hasMappingFingerprint;
        private bool _tableWasNonzero;
        private bool _supportedLastFrame;
        private ulong _lastLayoutKey;
        private int _generation;
        private int _framesUntilCapture;
        private int _rebuilds;
        private int _probes;
        private int _tableUpdates;
        private int _lastLevel = -1;
        private List<ComponentInfo> _lastComponents = new List<ComponentInfo>();
        private Task<BuildOutput> _activeBuild;
        private BuildSnapshot _queuedSnapshot;

        internal int Rebuilds => _rebuilds;
        internal int ComponentCount => _lastComponents.Count;
        internal int LastLevel => _lastLevel;
        internal string LastLevelHex => _lastLevel < 0 ? string.Empty : _lastLevel.ToString("X4");
        internal int ProbeCount => _probes;
        internal int TableUpdates => _tableUpdates;
        internal bool BuildPending => _activeBuild != null || _queuedSnapshot != null;
        internal float Spacing => Math.Max(0f, Math.Min(1f, _spacing.Value));

        internal ConnectedComponentDepthMapper(NativeTileDepthPatcher nativePatcher,
            ManualLogSource log, ConfigEntry<int> depthBands,
            ConfigEntry<float> spacing, ConfigEntry<int> minimumTiles,
            ConfigEntry<int> maximumAutoTiles,
            ConfigEntry<int> refreshIntervalFrames,
            string directory)
        {
            _native = nativePatcher ?? throw new ArgumentNullException(nameof(nativePatcher));
            _log = log;
            _depthBands = depthBands;
            _spacing = spacing;
            _minimumTiles = minimumTiles;
            _maximumAutoTiles = maximumAutoTiles;
            _refreshIntervalFrames = refreshIntervalFrames;
            _directory = directory;
            _profilesDirectory = Path.Combine(directory, "profiles");
            Directory.CreateDirectory(_profilesDirectory);
            WriteProfileHelp();
        }

        internal void Refresh(PPURenderer renderer)
        {
            try
            {
                if (!TryReadState(renderer, out SNESPPU ppu, out byte[] registers,
                        out int level, out string reason))
                {
                    Invalidate(reason);
                    return;
                }
                string profilePath = ProfilePath(level);
                long profileStamp = File.Exists(profilePath)
                    ? File.GetLastWriteTimeUtc(profilePath).Ticks : 0L;
                ulong layoutKey = BuildLayoutKey(registers, level, profileStamp);
                if (!_supportedLastFrame || layoutKey != _lastLayoutKey)
                {
                    _generation++;
                    _queuedSnapshot = null;
                    ClearNativeTable();
                    _hasFingerprint = false;
                    _hasMappingFingerprint = false;
                    _lastLayoutKey = layoutKey;
                }
                _supportedLastFrame = true;

                CompleteBuildIfReady();
                if (--_framesUntilCapture > 0) return;
                _framesUntilCapture = Math.Max(1, Math.Min(60,
                    _refreshIntervalFrames.Value));
                BuildSnapshot snapshot = Capture(ppu, registers, level, profilePath,
                    profileStamp);
                if (_activeBuild == null) StartBuild(snapshot);
                else _queuedSnapshot = snapshot;
            }
            catch (Exception exception)
            {
                if (_activeBuild != null && _activeBuild.IsCompleted)
                    _activeBuild = null;
                _queuedSnapshot = null;
                Invalidate(exception.GetType().Name + ": " + exception.Message);
                _log?.LogError("Connected-component depth refresh failed closed: " + exception);
            }
        }

        internal void Clear()
        {
            Invalidate("inactive");
        }

        private BuildSnapshot Capture(SNESPPU ppu, byte[] registers, int level,
            string profilePath, long profileStamp)
        {
            var snapshot = new BuildSnapshot
            {
                Generation = _generation,
                Registers = (byte[])registers.Clone(),
                Vram = new byte[65536],
                Level = level,
                ProfilePath = profilePath,
                ProfileStamp = profileStamp,
                DepthBands = Math.Max(1, Math.Min(31, _depthBands.Value)),
                Spacing = Math.Max(0f, Math.Min(1f, _spacing.Value)),
                MinimumTiles = Math.Max(1, _minimumTiles.Value),
                MaximumAutoTiles = Math.Max(1, _maximumAutoTiles.Value),
                PreviousFingerprint = _hasFingerprint ? _lastFingerprint : 0UL,
                HasPreviousFingerprint = _hasFingerprint
            };
            Copy(ppu.GetPPUMemory(), snapshot.Vram, snapshot.Vram.Length);
            return snapshot;
        }

        private void StartBuild(BuildSnapshot snapshot)
        {
            _activeBuild = Task.Run(() => BuildSnapshotMap(snapshot));
        }

        private void CompleteBuildIfReady()
        {
            if (_activeBuild == null || !_activeBuild.IsCompleted) return;
            BuildOutput output = _activeBuild.GetAwaiter().GetResult();
            _activeBuild = null;
            _probes++;
            if (output.Generation == _generation && output.Changed)
            {
                _lastFingerprint = output.Fingerprint;
                _hasFingerprint = true;
                _rebuilds++;
                if (!_hasMappingFingerprint ||
                    output.MappingFingerprint != _lastMappingFingerprint)
                {
                    Array.Clear(_depthTable, 0, _depthTable.Length);
                    bool anyDepth = false;
                    foreach (ComponentInfo component in output.Components)
                    {
                        if (component.Depth == 0f) continue;
                        anyDepth = true;
                        for (int i = 0; i < component.Addresses.Length; i++)
                            _depthTable[NativeTileDepthPatcher.CalculateDepthIndex(
                                component.Background, component.Addresses[i])] = component.Depth;
                    }
                    _native.UpdateDepthTable(_depthTable);
                    _tableWasNonzero = anyDepth;
                    _lastMappingFingerprint = output.MappingFingerprint;
                    _hasMappingFingerprint = true;
                    _tableUpdates++;
                }
                bool levelChanged = _lastLevel != output.Level;
                _lastLevel = output.Level;
                _lastComponents = output.Components;
                if (_rebuilds == 1 || levelChanged || _rebuilds % 60 == 0)
                    WriteComponents(output.Level, output.Components);
                if (_rebuilds == 1 || _rebuilds % 60 == 0)
                    _log?.LogInfo("Connected depth map: " + output.Components.Count +
                        " conservative components at level $" +
                        output.Level.ToString("X4") + ".");
            }
            else if (output.Generation == _generation)
            {
                _lastFingerprint = output.Fingerprint;
                _hasFingerprint = true;
            }

            BuildSnapshot queued = _queuedSnapshot;
            _queuedSnapshot = null;
            if (queued != null)
            {
                queued.PreviousFingerprint = _lastFingerprint;
                queued.HasPreviousFingerprint = _hasFingerprint;
                StartBuild(queued);
            }
        }

        private BuildOutput BuildSnapshotMap(BuildSnapshot snapshot)
        {
            _vram = snapshot.Vram;
            ulong fingerprint = ComputeFingerprint(snapshot.Registers, snapshot.Level);
            fingerprint = Mix(fingerprint, unchecked((uint)snapshot.ProfileStamp));
            fingerprint = Mix(fingerprint, unchecked((uint)(snapshot.ProfileStamp >> 32)));
            fingerprint = Mix(fingerprint, (uint)snapshot.DepthBands);
            fingerprint = Mix(fingerprint,
                unchecked((uint)BitConverter.SingleToInt32Bits(snapshot.Spacing)));
            fingerprint = Mix(fingerprint, (uint)snapshot.MinimumTiles);
            fingerprint = Mix(fingerprint, (uint)snapshot.MaximumAutoTiles);
            if (snapshot.HasPreviousFingerprint &&
                fingerprint == snapshot.PreviousFingerprint)
                return new BuildOutput
                {
                    Generation = snapshot.Generation,
                    Fingerprint = fingerprint,
                    Changed = false,
                    Level = snapshot.Level
                };

            Dictionary<string, float> overrides = LoadOverrides(snapshot.ProfilePath);
            var allComponents = new List<ComponentInfo>();
            byte bgmode = snapshot.Registers[5];
            for (int bg = 0; bg < 3; bg++)
            {
                byte bgsc = snapshot.Registers[7 + bg];
                bool size16 = ((bgmode >> (4 + bg)) & 1) != 0;
                int width = (bgsc & 1) != 0 ? 64 : 32;
                int height = (bgsc & 2) != 0 ? 64 : 32;
                TileShape[] cells = BuildCells(snapshot.Registers, bg, bgsc,
                    size16, width, height);
                ComponentBuildResult result = ConnectedComponentModel.Build(cells,
                    width, height, bg, snapshot.DepthBands, snapshot.Spacing,
                    snapshot.MinimumTiles, snapshot.MaximumAutoTiles, overrides, true);
                allComponents.AddRange(result.Components);
            }
            return new BuildOutput
            {
                Generation = snapshot.Generation,
                Fingerprint = fingerprint,
                MappingFingerprint = BuildMappingFingerprint(allComponents),
                Changed = true,
                Level = snapshot.Level,
                Components = allComponents
            };
        }

        private ulong BuildLayoutKey(byte[] registers, int level, long profileStamp)
        {
            ulong hash = 1469598103934665603UL;
            hash = Mix(hash, (uint)level);
            hash = Mix(hash, registers[5]);
            for (int i = 7; i <= 12; i++) hash = Mix(hash, registers[i]);
            hash = Mix(hash, unchecked((uint)profileStamp));
            hash = Mix(hash, unchecked((uint)(profileStamp >> 32)));
            hash = Mix(hash, (uint)_depthBands.Value);
            hash = Mix(hash, unchecked((uint)BitConverter.SingleToInt32Bits(_spacing.Value)));
            hash = Mix(hash, (uint)_minimumTiles.Value);
            hash = Mix(hash, (uint)_maximumAutoTiles.Value);
            return hash;
        }

        private static ulong BuildMappingFingerprint(List<ComponentInfo> components)
        {
            var entries = new List<MappingEntry>();
            foreach (ComponentInfo component in components)
            {
                if (component.Depth == 0f) continue;
                uint depth = unchecked((uint)BitConverter.SingleToInt32Bits(component.Depth));
                for (int i = 0; i < component.Addresses.Length; i++)
                    entries.Add(new MappingEntry
                    {
                        Background = component.Background,
                        Address = component.Addresses[i],
                        Depth = depth
                    });
            }
            entries.Sort((left, right) =>
            {
                int compare = left.Background.CompareTo(right.Background);
                if (compare != 0) return compare;
                compare = left.Address.CompareTo(right.Address);
                return compare != 0 ? compare : left.Depth.CompareTo(right.Depth);
            });
            ulong hash = 1469598103934665603UL;
            foreach (MappingEntry entry in entries)
            {
                hash = Mix(hash, (uint)entry.Background);
                hash = Mix(hash, (uint)entry.Address);
                hash = Mix(hash, entry.Depth);
            }
            return hash;
        }

        private bool TryReadState(PPURenderer renderer, out SNESPPU ppu,
            out byte[] registers, out int level, out string reason)
        {
            ppu = renderer?.snesPPU;
            registers = null;
            level = 0;
            reason = string.Empty;
            string filename = MainMenuManager.Instance?.GetLoadedGameFilename() ?? string.Empty;
            if (filename.IndexOf("DKC_Widescreen_", StringComparison.OrdinalIgnoreCase) < 0)
            {
                reason = "not-supported-dkc-rom";
                return false;
            }
            if (renderer.dynamicFont)
            {
                reason = "dynamic-font-active";
                return false;
            }
            if (ppu == null || ppu._ppuStartFrame == null ||
                ppu._ppuStartFrame.Length < 64 || ppu.GetPPUMemory() == null ||
                ppu.GetPPUMemory().Length != 65536)
            {
                reason = "missing-ppu-state";
                return false;
            }
            registers = new byte[64];
            Copy(ppu._ppuStartFrame, registers, 64);
            if ((registers[5] & 7) != 1)
            {
                reason = "not-mode1";
                return false;
            }
            if (ppu._ppuLineChanges == null || ppu._curPPUChangeIdx < 0 ||
                ppu._curPPUChangeIdx > ppu._ppuLineChanges.Length)
            {
                reason = "invalid-raster-change-list";
                return false;
            }
            for (int i = 0; i < ppu._curPPUChangeIdx; i++)
            {
                uint address = ppu._ppuLineChanges[i].address;
                if (address == 0x2105 || (address >= 0x2107 && address <= 0x210C))
                {
                    reason = "mid-frame-bg-layout-change";
                    return false;
                }
            }
            try
            {
                Il2CppStructArray<byte> ram = ppu.masterExecutor?.CoreMemoryMap?.GetRam();
                if (ram != null && ram.Length > 0x31)
                    level = ram[0x30] | (ram[0x31] << 8);
            }
            catch { level = 0; }
            return true;
        }

        private ulong ComputeFingerprint(byte[] registers, int level)
        {
            ulong hash = 1469598103934665603UL;
            hash = Mix(hash, (uint)level);
            hash = Mix(hash, registers[5]);
            hash = Mix(hash, registers[7]);
            hash = Mix(hash, registers[8]);
            hash = Mix(hash, registers[9]);
            hash = Mix(hash, registers[11]);
            hash = Mix(hash, registers[12]);
            for (int bg = 0; bg < 3; bg++)
            {
                Array.Clear(_usedTiles[bg], 0, _usedTiles[bg].Length);
                byte bgsc = registers[7 + bg];
                int width = (bgsc & 1) != 0 ? 64 : 32;
                int height = (bgsc & 2) != 0 ? 64 : 32;
                int mapBase = (bgsc & 0xFC) << 9;
                bool size16 = ((registers[5] >> (4 + bg)) & 1) != 0;
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int address = (mapBase + GetTileAddress(x, y, bgsc, false)) & 0xFFFF;
                        int descriptor = ReadWord(address);
                        hash = Mix(hash, (uint)descriptor);
                        MarkDescriptorTiles(_usedTiles[bg], descriptor, size16);
                    }
                }
                int bits = bg == 2 ? 2 : 4;
                int chrBase = GetChrBase(registers, bg);
                int tileBytes = bits * 8;
                for (int tile = 0; tile < 1024; tile++)
                {
                    if (_usedTiles[bg][tile] == 0) continue;
                    int address = (chrBase + tile * tileBytes) & 0xFFFF;
                    for (int i = 0; i < tileBytes; i++)
                        hash = Mix(hash, _vram[(address + i) & 0xFFFF]);
                }
            }
            return hash;
        }

        private TileShape[] BuildCells(byte[] registers, int bg, byte bgsc,
            bool size16, int width, int height)
        {
            int mapBase = (bgsc & 0xFC) << 9;
            int bits = bg == 2 ? 2 : 4;
            int chrBase = GetChrBase(registers, bg);
            int pixels = size16 ? 16 : 8;
            TileShape[] cells = new TileShape[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int address = (mapBase + GetTileAddress(x, y, bgsc, false)) & 0xFFFF;
                    ushort descriptor = (ushort)ReadWord(address);
                    ushort left = 0, right = 0, top = 0, bottom = 0;
                    bool opaque = false;
                    for (int py = 0; py < pixels; py++)
                    {
                        for (int px = 0; px < pixels; px++)
                        {
                            if (!OpaquePixel(descriptor, size16, x, y, px, py,
                                    chrBase, bits)) continue;
                            opaque = true;
                            if (px == 0) left |= (ushort)(1 << py);
                            if (px == pixels - 1) right |= (ushort)(1 << py);
                            if (py == 0) top |= (ushort)(1 << px);
                            if (py == pixels - 1) bottom |= (ushort)(1 << px);
                        }
                    }
                    cells[y * width + x] = new TileShape(address, descriptor,
                        left, right, top, bottom, opaque);
                }
            }
            return cells;
        }

        private bool OpaquePixel(int descriptor, bool size16, int cellX, int cellY,
            int px, int py, int chrBase, int bits)
        {
            int tile = descriptor & 0x3FF;
            bool xflip = (descriptor & 0x4000) != 0;
            bool yflip = (descriptor & 0x8000) != 0;
            int localX = px & 7;
            int localY = py & 7;
            if (size16)
            {
                int tileX = cellX * 2 + (px >> 3);
                int tileY = cellY * 2 + (py >> 3);
                if (((tileX & 1) != 0) ^ xflip) tile++;
                if (((tileY & 1) != 0) ^ yflip) tile += 16;
            }
            if (xflip) localX = 7 - localX;
            if (yflip) localY = 7 - localY;
            return DecodePlanar(chrBase, tile & 0x3FF, bits, localX, localY) != 0;
        }

        private int DecodePlanar(int chrBase, int tile, int bits, int x, int y)
        {
            int address = (chrBase + tile * bits * 8) & 0xFFFF;
            int bit = 7 - x;
            int color = 0;
            for (int plane = 0; plane < bits; plane++)
            {
                int planeAddress = address + (plane >> 1) * 16 + y * 2 + (plane & 1);
                color |= ((_vram[planeAddress & 0xFFFF] >> bit) & 1) << plane;
            }
            return color;
        }

        private static void MarkDescriptorTiles(byte[] used, int descriptor, bool size16)
        {
            int tile = descriptor & 0x3FF;
            used[tile] = 1;
            if (!size16) return;
            used[(tile + 1) & 0x3FF] = 1;
            used[(tile + 16) & 0x3FF] = 1;
            used[(tile + 17) & 0x3FF] = 1;
        }

        private int ReadWord(int address) => _vram[address & 0xFFFF] |
            (_vram[(address + 1) & 0xFFFF] << 8);

        private static int GetTileAddress(int x, int y, byte bgsc, bool size16)
        {
            if (size16) { x >>= 1; y >>= 1; }
            int offset = 0;
            if ((bgsc & 1) != 0)
            {
                if ((x & 0x20) != 0) offset += 2048;
                if ((bgsc & 2) != 0 && (y & 0x20) != 0) offset += 4096;
            }
            else if ((bgsc & 2) != 0 && (y & 0x20) != 0) offset += 2048;
            return (x & 31) * 2 + (y & 31) * 64 + offset;
        }

        private static int GetChrBase(byte[] registers, int bg) => bg == 0
            ? (registers[11] & 0x0F) << 13
            : bg == 1 ? ((registers[11] >> 4) & 0x0F) << 13
            : (registers[12] & 0x0F) << 13;

        private Dictionary<string, float> LoadOverrides(string path)
        {
            if (!File.Exists(path)) return new Dictionary<string, float>();
            string json = File.ReadAllText(path);
            LayerComponentProfile profile = JsonSerializer.Deserialize<LayerComponentProfile>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return profile?.ComponentDepths ?? new Dictionary<string, float>();
        }

        private string ProfilePath(int level) => Path.Combine(_profilesDirectory,
            "level-" + level.ToString("X4") + ".json");

        internal bool ExportComponents(string path)
        {
            if (_lastLevel < 0 || _lastComponents == null || _lastComponents.Count == 0 ||
                string.IsNullOrWhiteSpace(path)) return false;
            WriteComponents(_lastLevel, _lastComponents, path);
            return true;
        }

        private void WriteComponents(int level, List<ComponentInfo> components,
            string path = null)
        {
            var report = new ComponentReport
            {
                Version = 1,
                Level = level.ToString("X4"),
                Spacing = Spacing,
                SafetyRule = "Components join only across touching opaque edge pixels; no palette/tile-number cuts.",
                Components = components
            };
            string output = string.IsNullOrWhiteSpace(path)
                ? Path.Combine(_directory, "components-current.json") : path;
            Directory.CreateDirectory(Path.GetDirectoryName(output) ?? _directory);
            File.WriteAllText(output,
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true,
                    IncludeFields = true }));
        }

        private void WriteProfileHelp()
        {
            string path = Path.Combine(_profilesDirectory, "README.txt");
            if (File.Exists(path)) return;
            File.WriteAllText(path,
                "Optional per-level component depth overrides.\r\n" +
                "Create level-XXXX.json using component IDs from ..\\components-current.json.\r\n" +
                "Example:\r\n{\r\n  \"version\": 1,\r\n  \"componentDepths\": {\r\n" +
                "    \"BG1-A1234-0123456789ABCDEF\": 0.12\r\n  }\r\n}\r\n" +
                "Equal depth values merge components visually. Values are clamped to -4..4.\r\n");
        }

        private void Invalidate(string reason)
        {
            if (_supportedLastFrame)
            {
                _generation++;
                _queuedSnapshot = null;
                ClearNativeTable();
                _hasFingerprint = false;
                _hasMappingFingerprint = false;
                _supportedLastFrame = false;
            }
        }

        private void ClearNativeTable()
        {
            if (!_tableWasNonzero) return;
            Array.Clear(_depthTable, 0, _depthTable.Length);
            _native.UpdateDepthTable(_depthTable);
            _tableWasNonzero = false;
            _lastMappingFingerprint = 0UL;
            _hasMappingFingerprint = false;
        }

        private static void Copy(Il2CppStructArray<byte> source, byte[] destination, int count)
        {
            for (int i = 0; i < count; i++) destination[i] = source[i];
        }

        private static ulong Mix(ulong hash, uint value)
        {
            hash ^= value;
            return hash * 1099511628211UL;
        }

        private sealed class ComponentReport
        {
            public int Version;
            public string Level;
            public float Spacing;
            public string SafetyRule;
            public List<ComponentInfo> Components;
        }

        private sealed class BuildSnapshot
        {
            internal int Generation;
            internal byte[] Registers;
            internal byte[] Vram;
            internal int Level;
            internal string ProfilePath;
            internal long ProfileStamp;
            internal int DepthBands;
            internal float Spacing;
            internal int MinimumTiles;
            internal int MaximumAutoTiles;
            internal ulong PreviousFingerprint;
            internal bool HasPreviousFingerprint;
        }

        private sealed class BuildOutput
        {
            internal int Generation;
            internal ulong Fingerprint;
            internal ulong MappingFingerprint;
            internal bool Changed;
            internal int Level;
            internal List<ComponentInfo> Components;
        }

        private struct MappingEntry
        {
            internal int Background;
            internal int Address;
            internal uint Depth;
        }
    }
}
