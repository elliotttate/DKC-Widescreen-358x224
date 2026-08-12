using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using UnityEngine;

namespace SuperZSNESDKCFramebufferRenderer
{
    internal sealed class DkcFrameRasterizer
    {
        private const int Lines = 224;
        private const int PaletteEntries = 256;
        private static readonly ParallelOptions ParallelWorkers = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(8, Environment.ProcessorCount))
        };

        private readonly LineState[] _line = new LineState[Lines];
        private readonly ushort[] _palette = new ushort[Lines * PaletteEntries];
        private readonly BackgroundCache[] _background =
        {
            new BackgroundCache(), new BackgroundCache(), new BackgroundCache()
        };
        private readonly PrepareOutcome[] _prepareOutcomes = new PrepareOutcome[3];
        private readonly int[] _prepareDifferingLines = new int[3];
        private readonly double[] _prepareFrameMs = new double[3];
        private SpritePixel[] _sprites;
        private byte[] _spriteOwner;
        private readonly byte[] _registers = new byte[64];
        private readonly byte[] _workingCgram = new byte[512];
        private string _verifiedRomPath;
        private bool _verifiedRom;
        private readonly bool _enableRetainedBackgrounds;

        internal DkcFrameRasterizer(bool enableRetainedBackgrounds = true)
        {
            _enableRetainedBackgrounds = enableRetainedBackgrounds;
        }

        internal long BackgroundCacheHits { get; private set; }
        internal long BackgroundCacheMisses { get; private set; }
        internal long RasterEffectRebuilds { get; private set; }
        internal int LastRebuiltLayers { get; private set; }
        internal readonly long[] PerBgHits = new long[3];
        internal readonly long[] PerBgMisses = new long[3];
        internal readonly long[] PerBgRasterRebuilds = new long[3];
        internal readonly long[] PerBgDecodedTileHits = new long[3];
        internal readonly long[] PerBgDecodedTileMisses = new long[3];
        internal readonly double[] PerBgPrepareMs = new double[3];
        internal readonly long[] PerBgPrepareCalls = new long[3];
        internal long StageFrames { get; private set; }
        internal double LineStateMs { get; private set; }
        internal double BackgroundMs { get; private set; }
        internal double SpriteMs { get; private set; }
        internal double CompositeMs { get; private set; }
        internal string LastRasterEffect { get; private set; } = string.Empty;
        internal string LineDiagnosticsJson { get; private set; } = "[]";

        private const string CanonicalRomSha256 = "B4AB46098E48218E70B5349E09E7FE71E344D23E3568F46E956B44C670006D6D";

        private static readonly int[] BgLow = { 7, 6, 1 };
        private static readonly int[] BgHigh = { 10, 9, 4 };
        private static readonly int[] ObjPriority = { 2, 5, 8, 11 };
        private static readonly int[] SmallWidth = { 1, 1, 1, 2, 2, 4, 2, 2 };
        private static readonly int[] SmallHeight = { 1, 1, 1, 2, 2, 4, 4, 4 };
        private static readonly int[] LargeWidth = { 2, 4, 8, 4, 8, 8, 4, 4 };
        private static readonly int[] LargeHeight = { 2, 4, 8, 4, 8, 8, 8, 4 };

        internal bool TryRender(PPURenderer renderer, int width, int height, int leftExtension,
            Color32[] destination, out string reason)
        {
            reason = string.Empty;
            if (renderer == null || renderer.snesPPU == null)
            {
                reason = "renderer-or-ppu-null";
                return false;
            }
            string filename = MainMenuManager.Instance?.GetLoadedGameFilename() ?? string.Empty;
            if (filename.IndexOf("DKC_Widescreen_358x224", StringComparison.OrdinalIgnoreCase) < 0)
            {
                reason = "not-canonical-dkc-widescreen-rom";
                return false;
            }
            if (!VerifyCanonicalRom(filename))
            {
                reason = "canonical-rom-hash-mismatch-or-path-unavailable";
                return false;
            }
            if (width != 358 || height != Lines || destination == null || destination.Length != width * height)
            {
                reason = "unsupported-framebuffer-geometry";
                return false;
            }

            SNESPPU ppu = renderer.snesPPU;
            if (ppu.masterExecutor == null || ppu.masterExecutor.IsOverscan)
            {
                reason = "overscan-or-executor-unavailable";
                return false;
            }
            if (renderer.screenMat == null || Math.Abs(renderer.screenMat.GetFloat("_UIFade")) > 0.000001f)
            {
                reason = "ui-fade-requires-stock-renderer";
                return false;
            }
            long stageStart = Stopwatch.GetTimestamp();
            if (!BuildLineState(ppu, out reason))
                return false;
            LineStateMs += ElapsedMilliseconds(stageStart);

            byte[] vram = ppu.GetPPUMemory();
            stageStart = Stopwatch.GetTimestamp();
            PrepareBackgroundPlanes(vram, width, height, leftExtension);
            BackgroundMs += ElapsedMilliseconds(stageStart);

            stageStart = Stopwatch.GetTimestamp();
            EnsureSpriteBuffers(width, height);
            Array.Clear(_sprites, 0, _sprites.Length);
            for (int i = 0; i < _spriteOwner.Length; i++) _spriteOwner[i] = byte.MaxValue;
            RasterizeSprites(ppu, width, height, leftExtension);
            SpriteMs += ElapsedMilliseconds(stageStart);

            stageStart = Stopwatch.GetTimestamp();
            Parallel.For(0, height, ParallelWorkers, y =>
            {
                LineState state = _line[y];
                for (int x = 0; x < width; x++)
                {
                    int nativeX = x - leftExtension;
                    LayerPixel main = Backdrop(y);
                    LayerPixel sub = Backdrop(y);

                    for (int bg = 0; bg < 3; bg++)
                    {
                        LayerPixel pixel = ReadPreparedBackgroundPixel(bg, x, y);
                        if (!pixel.Opaque) continue;
                        if ((state.Tm & (1 << bg)) != 0 && LayerVisible(state, bg, nativeX, true) && pixel.Priority > main.Priority)
                            main = pixel;
                        if ((state.Ts & (1 << bg)) != 0 && LayerVisible(state, bg, nativeX, false) && pixel.Priority > sub.Priority)
                            sub = pixel;
                    }

                    SpritePixel sprite = _sprites[y * width + x];
                    if (sprite.Opaque)
                    {
                        LayerPixel obj = new LayerPixel(sprite.PaletteIndex, sprite.Priority, 4,
                            sprite.PaletteIndex >= 192, true);
                        if ((state.Tm & 0x10) != 0 && LayerVisible(state, 4, nativeX, true) && obj.Priority > main.Priority)
                            main = obj;
                        if ((state.Ts & 0x10) != 0 && LayerVisible(state, 4, nativeX, false) && obj.Priority > sub.Priority)
                            sub = obj;
                    }

                    ushort mainColor = _palette[y * PaletteEntries + main.PaletteIndex];
                    ushort subColor = (state.Cgwsel & 2) != 0
                        ? _palette[y * PaletteEntries + sub.PaletteIndex]
                        : state.FixedColor;
                    bool colorWindow = EvaluateWindow(state, 5, nativeX);
                    // CGWSEL 7-6 clips the main result to black; 5-4 controls
                    // the color-math region. Keeping these separate is
                    // essential for DKC's color-window palette effects.
                    int clipMode = (state.Cgwsel >> 6) & 3;
                    int mathMode = (state.Cgwsel >> 4) & 3;
                    if (ApplyRegionMode(clipMode, colorWindow, false))
                        mainColor = 0;

                    bool mathAllowed = ApplyRegionMode(mathMode, colorWindow, true);
                    int mathBit = main.Source == 5 ? 5 : main.Source;
                    bool selected = (state.Cgadsub & (1 << mathBit)) != 0;
                    if (main.Source != 4 || main.ObjMathEligible)
                    {
                        if (mathAllowed && selected)
                            mainColor = Blend(mainColor, subColor, (state.Cgadsub & 0x80) != 0,
                                (state.Cgadsub & 0x40) != 0);
                    }

                    destination[(height - 1 - y) * width + x] = ToColor32(mainColor, state.Inidisp);
                }
            });
            CompositeMs += ElapsedMilliseconds(stageStart);
            StageFrames++;
            return true;
        }

        private void PrepareBackgroundPlanes(byte[] vram, int width, int height, int leftExtension)
        {
            LastRebuiltLayers = 0;
            Parallel.For(0, 3, ParallelWorkers, bg =>
            {
                long started = Stopwatch.GetTimestamp();
                int differingLine;
                _prepareOutcomes[bg] = PrepareBackgroundPlane(vram, bg, width, height,
                    leftExtension, out differingLine);
                _prepareDifferingLines[bg] = differingLine;
                _prepareFrameMs[bg] = ElapsedMilliseconds(started);
            });

            // Each worker owns one background cache. Aggregate diagnostics only
            // after all workers complete so the hot preparation path needs no
            // atomics or locks.
            for (int bg = 0; bg < 3; bg++)
            {
                PerBgPrepareMs[bg] += _prepareFrameMs[bg];
                PerBgPrepareCalls[bg]++;
                switch (_prepareOutcomes[bg])
                {
                    case PrepareOutcome.Hit:
                        BackgroundCacheHits++;
                        PerBgHits[bg]++;
                        break;
                    case PrepareOutcome.Miss:
                        BackgroundCacheMisses++;
                        PerBgMisses[bg]++;
                        LastRebuiltLayers++;
                        break;
                    case PrepareOutcome.RasterMiss:
                        BackgroundCacheMisses++;
                        PerBgMisses[bg]++;
                        RasterEffectRebuilds++;
                        PerBgRasterRebuilds[bg]++;
                        LastRebuiltLayers++;
                        LastRasterEffect = "BG" + (bg + 1) + "-line" + _prepareDifferingLines[bg];
                        break;
                    case PrepareOutcome.CacheDisabled:
                        RasterEffectRebuilds++;
                        PerBgRasterRebuilds[bg]++;
                        LastRebuiltLayers++;
                        LastRasterEffect = "cache-disabled-BG" + (bg + 1);
                        break;
                }
            }
        }

        private PrepareOutcome PrepareBackgroundPlane(byte[] vram, int bg, int width, int height,
            int leftExtension, out int differingLine)
        {
            differingLine = -1;
            const int guard = 8;
            BackgroundCache cache = _background[bg];
            int planeWidth = width + guard * 2;
            int planeHeight = height + guard * 2;
            cache.EnsurePixels(planeWidth, planeHeight);

            // Reuse decoded SNES character tiles across plane rebuilds. DKC's
            // nominal CHR ranges also contain unrelated streamed data, so each
            // tile is validated independently on first use in a frame.
            LineState decodeState = _line[Math.Min(1, height - 1)];
            int decodeBits = bg == 2 ? 2 : 4;
            PrepareDecodedTileFrame(cache, GetChrBase(decodeState, bg), decodeBits);

            if (!_enableRetainedBackgrounds)
            {
                cache.Valid = false;
                cache.RasterValid = false;
                cache.FirstLineDirect = false;
                cache.SampleX = guard;
                cache.SampleY = guard;
                for (int y = 0; y < height; y++)
                {
                    LineState state = _line[y];
                    int row = (y + guard) * planeWidth + guard;
                    for (int x = 0; x < width; x++)
                        cache.Pixels[row + x] = ReadBackgroundPixel(vram, state, bg,
                            x - leftExtension, y);
                }
                return PrepareOutcome.CacheDisabled;
            }

            // The legacy renderer starts its visible scanline walk at line 1.
            // DKC commonly has a frame-start scroll latch on line 0, followed by
            // one stable value for lines 1..223. Keep that first output row exact
            // without letting it defeat retained reuse for the other 223 rows.
            LineState first = decodeState;
            int scrollX = GetScrollX(first, bg) & 0xFFFF;
            int scrollY = GetScrollY(first, bg) & 0xFFFF;
            byte bgsc = GetBgsc(first, bg);
            byte bgmode = first.Bgmode;
            int chrBase = GetChrBase(first, bg);
            bool uniform = true;
            for (int y = 2; y < height; y++)
            {
                LineState state = _line[y];
                if (!BackgroundStateEquals(state, bg, scrollX, scrollY, bgsc, bgmode, chrBase))
                {
                    uniform = false;
                    differingLine = y;
                    break;
                }
            }

            if (!uniform)
            {
                cache.Valid = false;
                cache.FirstLineDirect = false;
                cache.SampleX = guard;
                cache.SampleY = guard;
                if (cache.RasterValid && RasterStateEquals(cache, bg, height) &&
                    MaskedVramEquals(vram, cache.RasterVramMask, cache.RasterVramSnapshot))
                {
                    return PrepareOutcome.Hit;
                }
                for (int y = 0; y < height; y++)
                {
                    LineState state = _line[y];
                    int row = (y + guard) * planeWidth + guard;
                    for (int x = 0; x < width; x++)
                        cache.Pixels[row + x] = ReadBackgroundPixel(vram, state, bg,
                            x - leftExtension, y);
                }
                SnapshotRasterState(cache, bg, height);
                BuildRasterVramMask(cache, bg, height);
                SnapshotMaskedVram(vram, cache.RasterVramMask, cache.RasterVramSnapshot);
                cache.RasterValid = true;
                return PrepareOutcome.RasterMiss;
            }

            cache.RasterValid = false;

            cache.FirstLineDirect = !BackgroundStateEquals(_line[0], bg, scrollX, scrollY,
                bgsc, bgmode, chrBase);
            if (cache.FirstLineDirect)
            {
                cache.EnsureFirstLine(width);
                for (int x = 0; x < width; x++)
                    cache.FirstLine[x] = ReadBackgroundPixel(vram, _line[0], bg,
                        x - leftExtension, 0);
            }

            int bucketX = TileBucket(scrollX);
            int bucketY = TileBucket(scrollY);
            int mapBase = (bgsc & 0xFC) << 9;
            int mapLength = 2048 << (((bgsc & 3) == 3) ? 2 : ((bgsc & 3) == 0 ? 0 : 1));
            bool keyMatches = cache.Valid && cache.Bgsc == bgsc && cache.Bgmode == bgmode &&
                              cache.ChrBase == chrBase && cache.BucketX == bucketX &&
                              cache.BucketY == bucketY &&
                              CircularRangeEquals(vram, mapBase, mapLength, cache.MapSnapshot) &&
                              UsedTileGraphicsEqual(vram, cache, chrBase, decodeBits);

            cache.SampleX = guard + (scrollX & 7);
            cache.SampleY = guard + (scrollY & 7);
            if (keyMatches)
            {
                return PrepareOutcome.Hit;
            }

            BeginPlaneBuild(cache);
            try
            {
                FillUniformPlane(vram, first, bg, cache, planeWidth, planeHeight,
                    guard, leftExtension, bucketX, bucketY, decodeBits, chrBase);
                CommitPlaneBuild(vram, cache, chrBase, decodeBits);
            }
            finally
            {
                cache.RecordUsedTiles = false;
            }

            cache.Bgsc = bgsc;
            cache.Bgmode = bgmode;
            cache.ChrBase = chrBase;
            cache.BucketX = bucketX;
            cache.BucketY = bucketY;
            SnapshotCircularRange(vram, mapBase, mapLength, ref cache.MapSnapshot);
            cache.Valid = true;
            return PrepareOutcome.Miss;
        }

        private static double ElapsedMilliseconds(long started)
        {
            return (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        }

        private static void PrepareDecodedTileFrame(BackgroundCache cache, int chrBase, int bits)
        {
            int tileBytes = bits * 8;
            if (cache.DecodedTiles == null || cache.DecodedTiles.Length != 1024 * 64)
                cache.DecodedTiles = new byte[1024 * 64];
            if (cache.DecodedTileSnapshot == null || cache.DecodedTileSnapshot.Length != 1024 * tileBytes)
                cache.DecodedTileSnapshot = new byte[1024 * tileBytes];
            if (cache.DecodedTileValid == null || cache.DecodedTileValid.Length != 1024)
                cache.DecodedTileValid = new byte[1024];
            if (cache.DecodedTileEpoch == null || cache.DecodedTileEpoch.Length != 1024)
                cache.DecodedTileEpoch = new int[1024];

            if (cache.DecodedChrBase != chrBase || cache.DecodedBits != bits)
            {
                Array.Clear(cache.DecodedTileValid, 0, cache.DecodedTileValid.Length);
                cache.DecodedChrBase = chrBase;
                cache.DecodedBits = bits;
            }

            if (cache.DecodeEpoch == int.MaxValue)
            {
                Array.Clear(cache.DecodedTileEpoch, 0, cache.DecodedTileEpoch.Length);
                cache.DecodeEpoch = 1;
            }
            else
            {
                cache.DecodeEpoch++;
            }
        }

        private int ReadDecodedTileColor(byte[] vram, BackgroundCache cache, int chrBase,
            int tile, int bits, int x, int y, int bg)
        {
            if (!EnsureDecodedTile(vram, cache, chrBase, tile, bits, bg))
                return DecodePlanar(vram, chrBase, tile, bits, x, y);

            return cache.DecodedTiles[tile * 64 + y * 8 + x];
        }

        private bool EnsureDecodedTile(byte[] vram, BackgroundCache cache, int chrBase,
            int tile, int bits, int bg)
        {
            if (tile < 0 || tile >= 1024 || cache.DecodedChrBase != chrBase ||
                cache.DecodedBits != bits)
                return false;

            if (cache.DecodedTileEpoch[tile] != cache.DecodeEpoch)
            {
                cache.DecodedTileEpoch[tile] = cache.DecodeEpoch;
                int tileBytes = bits * 8;
                int address = (chrBase + tile * tileBytes) & 0xFFFF;
                int snapshotOffset = tile * tileBytes;
                bool unchanged = cache.DecodedTileValid[tile] != 0 &&
                    TileRangeEquals(vram, address, cache.DecodedTileSnapshot,
                        snapshotOffset, tileBytes);
                if (!unchanged)
                {
                    int tileOffset = tile * 64;
                    for (int py = 0; py < 8; py++)
                    for (int px = 0; px < 8; px++)
                        cache.DecodedTiles[tileOffset + py * 8 + px] =
                            (byte)DecodePlanarAtAddress(vram, address, bits, px, py);
                    SnapshotTileRange(vram, address, cache.DecodedTileSnapshot,
                        snapshotOffset, tileBytes);
                    cache.DecodedTileValid[tile] = 1;
                    PerBgDecodedTileMisses[bg]++;
                }
                else
                {
                    PerBgDecodedTileHits[bg]++;
                }
            }
            return true;
        }

        private void FillUniformPlane(byte[] vram, LineState state, int bg,
            BackgroundCache cache, int planeWidth, int planeHeight, int guard,
            int leftExtension, int bucketX, int bucketY, int bits, int chrBase)
        {
            byte bgsc = GetBgsc(state, bg);
            bool size16 = ((state.Bgmode >> (4 + bg)) & 1) != 0;
            int mapBase = (bgsc & 0xFC) << 9;

            int py = 0;
            while (py < planeHeight)
            {
                int worldY = py - guard + bucketY;
                int tileY = FloorDiv8(worldY);
                int pixelY = worldY & 7;
                int rows = Math.Min(8 - pixelY, planeHeight - py);

                int px = 0;
                while (px < planeWidth)
                {
                    int worldX = px - guard - leftExtension + bucketX;
                    int tileX = FloorDiv8(worldX);
                    int pixelX = worldX & 7;
                    int columns = Math.Min(8 - pixelX, planeWidth - px);
                    int address = mapBase + GetTileAddress(tileX, tileY, bgsc, size16);
                    int descriptor = vram[address & 0xFFFF] |
                        (vram[(address + 1) & 0xFFFF] << 8);
                    int tile = descriptor & 0x3FF;
                    bool xflip = (descriptor & 0x4000) != 0;
                    bool yflip = (descriptor & 0x8000) != 0;
                    if (size16)
                    {
                        if (((tileX & 1) != 0) ^ xflip) tile++;
                        if (((tileY & 1) != 0) ^ yflip) tile += 16;
                    }

                    if (tile >= 0 && tile < 1024) cache.BuildingUsedTiles[tile] = 1;
                    else cache.BuildingUncacheableTile = true;
                    bool decoded = EnsureDecodedTile(vram, cache, chrBase, tile, bits, bg);
                    int palette = (descriptor >> 10) & 7;
                    bool high = (descriptor & 0x2000) != 0;
                    int priority = high ? BgHigh[bg] : BgLow[bg];
                    if (bg == 2 && high && (state.Bgmode & 8) != 0) priority = 12;

                    for (int dy = 0; dy < rows; dy++)
                    {
                        int sourceY = yflip ? 7 - (pixelY + dy) : pixelY + dy;
                        int destinationRow = (py + dy) * planeWidth + px;
                        for (int dx = 0; dx < columns; dx++)
                        {
                            int sourceX = xflip ? 7 - (pixelX + dx) : pixelX + dx;
                            int color = decoded
                                ? cache.DecodedTiles[tile * 64 + sourceY * 8 + sourceX]
                                : DecodePlanar(vram, chrBase, tile, bits, sourceX, sourceY);
                            if (color == 0)
                            {
                                cache.Pixels[destinationRow + dx] = default(LayerPixel);
                                continue;
                            }
                            int paletteIndex = bits == 4 ? palette * 16 + color : palette * 4 + color;
                            cache.Pixels[destinationRow + dx] = new LayerPixel((byte)paletteIndex,
                                (byte)priority, (byte)bg, false, true);
                        }
                    }
                    px += columns;
                }
                py += rows;
            }
        }

        private static void BeginPlaneBuild(BackgroundCache cache)
        {
            if (cache.BuildingUsedTiles == null || cache.BuildingUsedTiles.Length != 1024)
                cache.BuildingUsedTiles = new byte[1024];
            if (cache.PlaneUsedTiles == null || cache.PlaneUsedTiles.Length != 1024)
                cache.PlaneUsedTiles = new byte[1024];
            Array.Clear(cache.BuildingUsedTiles, 0, cache.BuildingUsedTiles.Length);
            cache.BuildingUncacheableTile = false;
            cache.RecordUsedTiles = true;
        }

        private static void CommitPlaneBuild(byte[] vram, BackgroundCache cache,
            int chrBase, int bits)
        {
            cache.RecordUsedTiles = false;
            byte[] previous = cache.PlaneUsedTiles;
            cache.PlaneUsedTiles = cache.BuildingUsedTiles;
            cache.BuildingUsedTiles = previous;
            cache.PlaneTilesCacheable = !cache.BuildingUncacheableTile;
            cache.PlaneChrBase = chrBase;
            cache.PlaneBits = bits;

            int tileBytes = bits * 8;
            int length = 1024 * tileBytes;
            if (cache.ChrSnapshot == null || cache.ChrSnapshot.Length != length)
                cache.ChrSnapshot = new byte[length];
            for (int tile = 0; tile < 1024; tile++)
            {
                if (cache.PlaneUsedTiles[tile] == 0) continue;
                int address = (chrBase + tile * tileBytes) & 0xFFFF;
                SnapshotTileRange(vram, address, cache.ChrSnapshot,
                    tile * tileBytes, tileBytes);
            }
        }

        private static bool UsedTileGraphicsEqual(byte[] vram, BackgroundCache cache,
            int chrBase, int bits)
        {
            if (!cache.PlaneTilesCacheable || cache.PlaneUsedTiles == null ||
                cache.ChrSnapshot == null || cache.PlaneChrBase != chrBase ||
                cache.PlaneBits != bits)
                return false;
            int tileBytes = bits * 8;
            for (int tile = 0; tile < 1024; tile++)
            {
                if (cache.PlaneUsedTiles[tile] == 0) continue;
                int address = (chrBase + tile * tileBytes) & 0xFFFF;
                if (!TileRangeEquals(vram, address, cache.ChrSnapshot,
                    tile * tileBytes, tileBytes)) return false;
            }
            return true;
        }

        private static bool TileRangeEquals(byte[] source, int sourceStart, byte[] snapshot,
            int snapshotOffset, int length)
        {
            sourceStart &= 0xFFFF;
            int first = Math.Min(length, 65536 - sourceStart);
            if (!RangeEquals(source, sourceStart, snapshot, snapshotOffset, first)) return false;
            int remainder = length - first;
            return remainder <= 0 || RangeEquals(source, 0, snapshot,
                snapshotOffset + first, remainder);
        }

        private static void SnapshotTileRange(byte[] source, int sourceStart, byte[] snapshot,
            int snapshotOffset, int length)
        {
            sourceStart &= 0xFFFF;
            int first = Math.Min(length, 65536 - sourceStart);
            Buffer.BlockCopy(source, sourceStart, snapshot, snapshotOffset, first);
            int remainder = length - first;
            if (remainder > 0)
                Buffer.BlockCopy(source, 0, snapshot, snapshotOffset + first, remainder);
        }

        private LayerPixel ReadPreparedBackgroundPixel(int bg, int x, int y)
        {
            BackgroundCache cache = _background[bg];
            if (y == 0 && cache.FirstLineDirect) return cache.FirstLine[x];
            return cache.Pixels[(y + cache.SampleY) * cache.Width + x + cache.SampleX];
        }

        private static bool BackgroundStateEquals(LineState state, int bg, int scrollX,
            int scrollY, byte bgsc, byte bgmode, int chrBase)
        {
            return (GetScrollX(state, bg) & 0xFFFF) == scrollX &&
                   (GetScrollY(state, bg) & 0xFFFF) == scrollY &&
                   GetBgsc(state, bg) == bgsc && state.Bgmode == bgmode &&
                   GetChrBase(state, bg) == chrBase;
        }

        private static int GetScrollX(LineState state, int bg)
        {
            return bg == 0 ? state.ScrollX1 : bg == 1 ? state.ScrollX2 : state.ScrollX3;
        }

        private static int GetScrollY(LineState state, int bg)
        {
            return bg == 0 ? state.ScrollY1 : bg == 1 ? state.ScrollY2 : state.ScrollY3;
        }

        private static byte GetBgsc(LineState state, int bg)
        {
            return bg == 0 ? state.Bg1sc : bg == 1 ? state.Bg2sc : state.Bg3sc;
        }

        private static int GetChrBase(LineState state, int bg)
        {
            return bg == 0 ? (state.Bg12nba & 0x0F) << 13
                : bg == 1 ? ((state.Bg12nba >> 4) & 0x0F) << 13
                : (state.Bg34nba & 0x0F) << 13;
        }

        private bool RasterStateEquals(BackgroundCache cache, int bg, int height)
        {
            if (cache.RasterState == null || cache.RasterState.Length != height * 5) return false;
            for (int y = 0; y < height; y++)
            {
                int index = y * 5;
                LineState state = _line[y];
                if (cache.RasterState[index] != (GetScrollX(state, bg) & 0xFFFF) ||
                    cache.RasterState[index + 1] != (GetScrollY(state, bg) & 0xFFFF) ||
                    cache.RasterState[index + 2] != GetBgsc(state, bg) ||
                    cache.RasterState[index + 3] != state.Bgmode ||
                    cache.RasterState[index + 4] != GetChrBase(state, bg))
                    return false;
            }
            return true;
        }

        private void SnapshotRasterState(BackgroundCache cache, int bg, int height)
        {
            if (cache.RasterState == null || cache.RasterState.Length != height * 5)
                cache.RasterState = new int[height * 5];
            for (int y = 0; y < height; y++)
            {
                int index = y * 5;
                LineState state = _line[y];
                cache.RasterState[index] = GetScrollX(state, bg) & 0xFFFF;
                cache.RasterState[index + 1] = GetScrollY(state, bg) & 0xFFFF;
                cache.RasterState[index + 2] = GetBgsc(state, bg);
                cache.RasterState[index + 3] = state.Bgmode;
                cache.RasterState[index + 4] = GetChrBase(state, bg);
            }
        }

        private void BuildRasterVramMask(BackgroundCache cache, int bg, int height)
        {
            if (cache.RasterVramMask == null || cache.RasterVramMask.Length != 65536)
            {
                cache.RasterVramMask = new byte[65536];
                cache.RasterVramSnapshot = new byte[65536];
            }
            Array.Clear(cache.RasterVramMask, 0, cache.RasterVramMask.Length);
            for (int y = 0; y < height; y++)
            {
                LineState state = _line[y];
                byte bgsc = GetBgsc(state, bg);
                int mapBase = (bgsc & 0xFC) << 9;
                int size = bgsc & 3;
                int mapLength = 2048 << (size == 3 ? 2 : (size == 0 ? 0 : 1));
                MarkCircularRange(cache.RasterVramMask, mapBase, mapLength);
                MarkCircularRange(cache.RasterVramMask, GetChrBase(state, bg), bg == 2 ? 16384 : 32768);
            }
        }

        private static void MarkCircularRange(byte[] mask, int start, int length)
        {
            for (int i = 0; i < length; i++) mask[(start + i) & 0xFFFF] = 1;
        }

        private static bool MaskedVramEquals(byte[] source, byte[] mask, byte[] snapshot)
        {
            if (mask == null || snapshot == null || mask.Length != 65536 || snapshot.Length != 65536)
                return false;
            for (int i = 0; i < 65536; i++)
                if (mask[i] != 0 && source[i] != snapshot[i]) return false;
            return true;
        }

        private static void SnapshotMaskedVram(byte[] source, byte[] mask, byte[] snapshot)
        {
            for (int i = 0; i < 65536; i++)
                if (mask[i] != 0) snapshot[i] = source[i];
        }

        private static int TileBucket(int value)
        {
            return value & 0xFFF8;
        }

        private static bool CircularRangeEquals(byte[] source, int start, int length, byte[] snapshot)
        {
            if (snapshot == null || snapshot.Length != length) return false;
            start &= 0xFFFF;
            int first = Math.Min(length, 65536 - start);
            if (!RangeEquals(source, start, snapshot, 0, first)) return false;
            int remainder = length - first;
            return remainder <= 0 || RangeEquals(source, 0, snapshot, first, remainder);
        }

        private static void SnapshotCircularRange(byte[] source, int start, int length, ref byte[] snapshot)
        {
            if (snapshot == null || snapshot.Length != length) snapshot = new byte[length];
            start &= 0xFFFF;
            int first = Math.Min(length, 65536 - start);
            Buffer.BlockCopy(source, start, snapshot, 0, first);
            int remainder = length - first;
            if (remainder > 0) Buffer.BlockCopy(source, 0, snapshot, first, remainder);
        }

        private static unsafe bool RangeEquals(byte[] left, int leftOffset, byte[] right,
            int rightOffset, int length)
        {
            if (length <= 0) return true;
            fixed (byte* leftBase = left)
            fixed (byte* rightBase = right)
            {
                byte* lp = leftBase + leftOffset;
                byte* rp = rightBase + rightOffset;
                int wide = length & ~7;
                for (int i = 0; i < wide; i += 8)
                    if (*(ulong*)(lp + i) != *(ulong*)(rp + i)) return false;
                for (int i = wide; i < length; i++)
                    if (lp[i] != rp[i]) return false;
            }
            return true;
        }

        private bool VerifyCanonicalRom(string loadedFilename)
        {
            string path = loadedFilename;
            if (!File.Exists(path))
            {
                string[] args = Environment.GetCommandLineArgs();
                path = null;
                for (int i = 0; i < args.Length; i++)
                {
                    if ((args[i].EndsWith(".sfc", StringComparison.OrdinalIgnoreCase) ||
                         args[i].EndsWith(".smc", StringComparison.OrdinalIgnoreCase)) && File.Exists(args[i]))
                    {
                        path = args[i];
                        break;
                    }
                }
            }
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            path = Path.GetFullPath(path);
            if (string.Equals(path, _verifiedRomPath, StringComparison.OrdinalIgnoreCase)) return _verifiedRom;
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
                _verifiedRom = string.Equals(actual, CanonicalRomSha256, StringComparison.OrdinalIgnoreCase);
                _verifiedRomPath = path;
                return _verifiedRom;
            }
        }

        private bool BuildLineState(SNESPPU ppu, out string reason)
        {
            reason = string.Empty;
            byte[] start = ppu._ppuStartFrame;
            if (start == null || start.Length < 64 || ppu.GetCGMemoryStartFrame() == null ||
                ppu.GetCGMemoryStartFrame().Length != 512 || ppu.GetPPUMemory() == null ||
                ppu.GetPPUMemory().Length != 65536 || ppu.GetStartFrameOAMMemory() == null ||
                ppu.GetStartFrameOAMMemory().Length != 544 || ppu._ppuLineChanges == null ||
                ppu._cgLineChanges == null || ppu._curPPUChangeIdx < 0 || ppu._cgChangeIdx < 0 ||
                ppu._curPPUChangeIdx > ppu._ppuLineChanges.Length ||
                ppu._cgChangeIdx > ppu._cgLineChanges.Length)
            {
                reason = "missing-start-frame-state";
                return false;
            }
            Array.Copy(start, _registers, 64);
            Array.Copy(ppu.GetCGMemoryStartFrame(), _workingCgram, 512);

            int sx1 = ppu._startScrollXBG1, sy1 = ppu._startScrollYBG1;
            int sx2 = ppu._startScrollXBG2, sy2 = ppu._startScrollYBG2;
            int sx3 = ppu._startScrollXBG3, sy3 = ppu._startScrollYBG3;
            uint fixedColor = ppu._startFixedColor;
            int pi = 0, ci = 0;
            for (int y = 0; y < Lines; y++)
            {
                while (ci < ppu._cgChangeIdx && ppu._cgLineChanges[ci].lineNo <= y)
                {
                    SNESPPU.CGLineChange change = ppu._cgLineChanges[ci++];
                    int offset = change.colNo * 2;
                    _workingCgram[offset] = change.colorLo;
                    _workingCgram[offset + 1] = change.colorHi;
                }

                while (pi < ppu._curPPUChangeIdx && ppu._ppuLineChanges[pi].lineNo <= y)
                {
                    SNESPPU.PPULineChange change = ppu._ppuLineChanges[pi++];
                    uint address = change.address;
                    byte value = change.val;
                    if (address >= 0x2101 && address <= 0x2104)
                    {
                        reason = "mid-frame-oam-write";
                        return false;
                    }
                    if (address >= 0x2100 && address <= 0x213F)
                        _registers[address - 0x2100] = value;
                    switch (address)
                    {
                        case 0x210D: sx1 = RotateScroll(sx1, value); break;
                        case 0x210E: sy1 = RotateScroll(sy1, value); break;
                        case 0x210F: sx2 = RotateScroll(sx2, value); break;
                        case 0x2110: sy2 = RotateScroll(sy2, value); break;
                        case 0x2111: sx3 = RotateScroll(sx3, value); break;
                        case 0x2112: sy3 = RotateScroll(sy3, value); break;
                        case 0x2132:
                            uint component = (uint)(value & 0x1F);
                            if ((value & 0x80) != 0) fixedColor = (fixedColor & 0x03FF) | (component << 10);
                            if ((value & 0x40) != 0) fixedColor = (fixedColor & 0x7C1F) | (component << 5);
                            if ((value & 0x20) != 0) fixedColor = (fixedColor & 0x7FE0) | component;
                            break;
                    }
                }

                int mode = _registers[5] & 7;
                if (mode != 1)
                {
                    reason = "unsupported-bg-mode-" + mode;
                    return false;
                }
                if ((_registers[6] & 0xF0) != 0)
                {
                    reason = "mosaic-active";
                    return false;
                }
                if ((_registers[48] & 1) != 0)
                {
                    reason = "direct-color-active";
                    return false;
                }
                if ((_registers[51] & 0x0B) != 0)
                {
                    reason = "interlace-or-hires-active";
                    return false;
                }

                _line[y] = new LineState(_registers, sx1, sy1, sx2, sy2, sx3, sy3, (ushort)fixedColor);
                int paletteBase = y * PaletteEntries;
                for (int color = 0; color < PaletteEntries; color++)
                    _palette[paletteBase + color] = (ushort)(_workingCgram[color * 2] |
                        (_workingCgram[color * 2 + 1] << 8));
            }
            LineDiagnosticsJson = BuildLineDiagnostics();
            return true;
        }

        private string BuildLineDiagnostics()
        {
            int[] samples = { 0, 1, 52, 72, 73, 85, 86, 100, 223 };
            string[] json = new string[samples.Length];
            for (int i = 0; i < samples.Length; i++)
            {
                int y = samples[i];
                LineState s = _line[y];
                json[i] = "{" +
                          "\"y\":" + y + "," +
                          "\"inidisp\":" + s.Inidisp + "," +
                          "\"bgmode\":" + s.Bgmode + "," +
                          "\"tm\":" + s.Tm + "," +
                          "\"ts\":" + s.Ts + "," +
                          "\"tmw\":" + s.Tmw + "," +
                          "\"tsw\":" + s.Tsw + "," +
                          "\"cgwsel\":" + s.Cgwsel + "," +
                          "\"cgadsub\":" + s.Cgadsub + "," +
                          "\"fixedColor\":" + s.FixedColor + "," +
                          "\"sx3\":" + (s.ScrollX3 & 0xFFFF) + "," +
                          "\"sy3\":" + (s.ScrollY3 & 0xFFFF) + "}";
            }
            return "[" + string.Join(",", json) + "]";
        }

        private static int RotateScroll(int current, byte value)
        {
            return ((current >> 8) | (value << 8)) & 0xFFFF;
        }

        private LayerPixel ReadBackgroundPixel(byte[] vram, LineState state, int bg, int screenX, int screenY)
        {
            return ReadBackgroundPixelAt(vram, state, bg, screenX, screenY,
                GetScrollX(state, bg), GetScrollY(state, bg));
        }

        private LayerPixel ReadBackgroundPixelAt(byte[] vram, LineState state, int bg,
            int screenX, int screenY, int scrollX, int scrollY)
        {
            int worldX = screenX + scrollX;
            int worldY = screenY + scrollY;
            int tileX = FloorDiv8(worldX);
            int tileY = FloorDiv8(worldY);
            int pixelX = worldX & 7;
            int pixelY = worldY & 7;
            byte bgsc = GetBgsc(state, bg);
            bool size16 = ((state.Bgmode >> (4 + bg)) & 1) != 0;
            int mapBase = (bgsc & 0xFC) << 9;
            int address = mapBase + GetTileAddress(tileX, tileY, bgsc, size16);
            int descriptor = vram[address & 0xFFFF] | (vram[(address + 1) & 0xFFFF] << 8);
            int tile = descriptor & 0x3FF;
            bool xflip = (descriptor & 0x4000) != 0;
            bool yflip = (descriptor & 0x8000) != 0;
            if (size16)
            {
                if (((tileX & 1) != 0) ^ xflip) tile++;
                if (((tileY & 1) != 0) ^ yflip) tile += 16;
            }
            if (xflip) pixelX = 7 - pixelX;
            if (yflip) pixelY = 7 - pixelY;

            int bits = bg == 2 ? 2 : 4;
            int chrBase = GetChrBase(state, bg);
            BackgroundCache cache = _background[bg];
            if (cache.RecordUsedTiles)
            {
                if (tile >= 0 && tile < 1024) cache.BuildingUsedTiles[tile] = 1;
                else cache.BuildingUncacheableTile = true;
            }
            int color = ReadDecodedTileColor(vram, cache, chrBase, tile, bits,
                pixelX, pixelY, bg);
            if (color == 0) return default(LayerPixel);
            int palette = (descriptor >> 10) & 7;
            int paletteIndex = bits == 4 ? palette * 16 + color : palette * 4 + color;
            bool high = (descriptor & 0x2000) != 0;
            int priority = high ? BgHigh[bg] : BgLow[bg];
            if (bg == 2 && high && (state.Bgmode & 8) != 0) priority = 12;
            return new LayerPixel((byte)paletteIndex, (byte)priority, (byte)bg, false, true);
        }

        private void RasterizeSprites(SNESPPU ppu, int width, int height, int leftExtension)
        {
            byte[] oam = ppu.GetStartFrameOAMMemory();
            byte[] vram = ppu.GetPPUMemory();
            int priorityAddress = ppu.GetOAMPriority();
            int start = (priorityAddress & 0x8000) != 0 ? (priorityAddress & 0xFE) >> 1 : 0;
            for (int order = 0; order < 128; order++)
            {
                int sprite = (start + order) & 0x7F;
                int x = oam[sprite * 4];
                int y = oam[sprite * 4 + 1] + 1;
                int tile = oam[sprite * 4 + 2];
                int attr = oam[sprite * 4 + 3];
                int upper = (oam[512 + sprite / 4] >> ((sprite & 3) * 2)) & 3;
                if ((upper & 1) != 0) x = x <= 127 ? x + 256 : x - 256;
                int ySigned = y > 239 ? y - 256 : y;
                int objsel = _line[Math.Max(1, Math.Min(Lines - 1, ySigned))].Objsel;
                int size = (objsel >> 5) & 7;
                int tilesWide = (upper & 2) != 0 ? LargeWidth[size] : SmallWidth[size];
                int tilesHigh = (upper & 2) != 0 ? LargeHeight[size] : SmallHeight[size];
                bool flipX = (attr & 0x40) != 0;
                bool flipY = (attr & 0x80) != 0;
                int priority = ObjPriority[(attr >> 4) & 3];
                int paletteBase = ((attr & 0x0E) >> 1) * 16 + 128;
                int nameGap = (((objsel >> 3) & 3) + 1) << 13;
                int nameBase = (objsel & 3) << 14;
                int baseAddress = nameBase + ((attr & 1) != 0 ? nameGap : 0) + tile * 32;

                for (int ty = 0; ty < tilesHigh; ty++)
                {
                    for (int tx = 0; tx < tilesWide; tx++)
                    {
                        int screenTileX = flipX ? x + (tilesWide - 1 - tx) * 8 : x + tx * 8;
                        int screenTileY = flipY ? ySigned + (tilesHigh - 1 - ty) * 8 : ySigned + ty * 8;
                        int tileAddress = (baseAddress + tx * 32 + ty * 32 * 16) & 0xFFFF;
                        for (int py = 0; py < 8; py++)
                        {
                            int sy = screenTileY + py;
                            if (sy < 0 || sy >= height) continue;
                            int sourceY = flipY ? 7 - py : py;
                            for (int px = 0; px < 8; px++)
                            {
                                int nativeX = screenTileX + px;
                                int dx = nativeX + leftExtension;
                                if (dx < 0 || dx >= width) continue;
                                int sourceX = flipX ? 7 - px : px;
                                int color = DecodePlanarAtAddress(vram, tileAddress, 4, sourceX, sourceY);
                                if (color == 0) continue;
                                int index = sy * width + dx;
                                SpritePixel old = _sprites[index];
                                if (old.Opaque && (old.Priority > priority ||
                                    (old.Priority == priority && _spriteOwner[index] <= order)))
                                    continue;
                                _sprites[index] = new SpritePixel((byte)(paletteBase + color), (byte)priority, true);
                                _spriteOwner[index] = (byte)order;
                            }
                        }
                    }
                }
            }
        }

        private void EnsureSpriteBuffers(int width, int height)
        {
            int count = width * height;
            if (_sprites == null || _sprites.Length != count)
            {
                _sprites = new SpritePixel[count];
                _spriteOwner = new byte[count];
            }
        }

        private LayerPixel Backdrop(int y)
        {
            return new LayerPixel(0, 0, 5, false, true);
        }

        private static bool LayerVisible(LineState state, int layer, int x, bool main)
        {
            byte designation = main ? state.Tmw : state.Tsw;
            if ((designation & (1 << layer)) == 0) return true;
            return !EvaluateWindow(state, layer, x);
        }

        private static bool EvaluateWindow(LineState state, int layer, int x)
        {
            int config;
            int operation;
            if (layer < 2)
            {
                config = (state.W12sel >> (layer * 4)) & 0xF;
                operation = (state.Wbglog >> (layer * 2)) & 3;
            }
            else if (layer < 4)
            {
                config = (state.W34sel >> ((layer - 2) * 4)) & 0xF;
                operation = (state.Wbglog >> (layer * 2)) & 3;
            }
            else
            {
                int shift = layer == 4 ? 0 : 4;
                config = (state.Wobjsel >> shift) & 0xF;
                operation = (state.Wobjlog >> (layer == 4 ? 0 : 2)) & 3;
            }
            bool enabled1 = (config & 2) != 0;
            bool enabled2 = (config & 8) != 0;
            if (!enabled1 && !enabled2) return false;
            bool inside1 = x >= 0 && x <= 255 && x >= state.Wh0 && x <= state.Wh1;
            bool inside2 = x >= 0 && x <= 255 && x >= state.Wh2 && x <= state.Wh3;
            if ((config & 1) != 0) inside1 = !inside1;
            if ((config & 4) != 0) inside2 = !inside2;
            if (!enabled1) return inside2;
            if (!enabled2) return inside1;
            switch (operation)
            {
                case 0: return inside1 || inside2;
                case 1: return inside1 && inside2;
                case 2: return inside1 ^ inside2;
                default: return !(inside1 ^ inside2);
            }
        }

        private static bool ApplyRegionMode(int mode, bool inside, bool math)
        {
            if (math)
            {
                switch (mode)
                {
                    case 0: return true;
                    case 1: return inside;
                    case 2: return !inside;
                    default: return false;
                }
            }
            switch (mode)
            {
                case 0: return false;
                case 1: return !inside;
                case 2: return inside;
                default: return true;
            }
        }

        private static ushort Blend(ushort main, ushort sub, bool subtract, bool half)
        {
            int mr = main & 31, mg = (main >> 5) & 31, mb = (main >> 10) & 31;
            int sr = sub & 31, sg = (sub >> 5) & 31, sb = (sub >> 10) & 31;
            int r = BlendChannel(mr, sr, subtract, half);
            int g = BlendChannel(mg, sg, subtract, half);
            int b = BlendChannel(mb, sb, subtract, half);
            return (ushort)(r | (g << 5) | (b << 10));
        }

        private static int BlendChannel(int main, int sub, bool subtract, bool half)
        {
            if (subtract)
            {
                int difference = Math.Max(0, main - sub);
                return half ? difference >> 1 : difference;
            }
            int sum = main + sub;
            return half ? Math.Min(31, sum >> 1) : Math.Min(31, sum);
        }

        private static Color32 ToColor32(ushort color, byte inidisp)
        {
            if ((inidisp & 0x80) != 0) return new Color32(0, 0, 0, 255);
            int brightness = inidisp & 15;
            byte r = ExpandStockChannel(color & 31, brightness);
            byte g = ExpandStockChannel((color >> 5) & 31, brightness);
            byte b = ExpandStockChannel((color >> 10) & 31, brightness);
            return new Color32(r, g, b, 255);
        }

        private static byte ExpandStockChannel(int value5, int brightness)
        {
            if (brightness == 0) return 0;
            // TileTextureGen.CalculatePalTexture stores c/32 rather than c/31
            // and clamps zero to 1/255. Match Unity's Color -> Color32 rounding
            // so the CPU framebuffer agrees with the legacy palette texture.
            double expanded = Math.Max(1.0, value5 * (255.0 / 32.0));
            int value = (int)Math.Round(expanded * brightness / 15.0,
                MidpointRounding.AwayFromZero);
            return (byte)Math.Max(0, Math.Min(255, value));
        }

        private static int DecodePlanar(byte[] vram, int chrBase, int tile, int bits, int x, int y)
        {
            int tileBytes = bits * 8;
            int address = (chrBase + tile * tileBytes) & 0xFFFF;
            return DecodePlanarAtAddress(vram, address, bits, x, y);
        }

        private static int DecodePlanarAtAddress(byte[] vram, int address, int bits, int x, int y)
        {
            int bit = 7 - x;
            int value = 0;
            int row = y * 2;
            value |= (vram[(address + row) & 0xFFFF] >> bit) & 1;
            value |= ((vram[(address + row + 1) & 0xFFFF] >> bit) & 1) << 1;
            if (bits >= 4)
            {
                value |= ((vram[(address + 16 + row) & 0xFFFF] >> bit) & 1) << 2;
                value |= ((vram[(address + 17 + row) & 0xFFFF] >> bit) & 1) << 3;
            }
            if (bits >= 8)
            {
                value |= ((vram[(address + 32 + row) & 0xFFFF] >> bit) & 1) << 4;
                value |= ((vram[(address + 33 + row) & 0xFFFF] >> bit) & 1) << 5;
                value |= ((vram[(address + 48 + row) & 0xFFFF] >> bit) & 1) << 6;
                value |= ((vram[(address + 49 + row) & 0xFFFF] >> bit) & 1) << 7;
            }
            return value;
        }

        private static int GetTileAddress(int x, int y, byte bgsc, bool tileSize16)
        {
            if (tileSize16) { x >>= 1; y >>= 1; }
            int offset = 0;
            if ((bgsc & 1) != 0)
            {
                if ((x & 0x20) != 0) offset += 2048;
                if ((bgsc & 2) != 0 && (y & 0x20) != 0) offset += 4096;
            }
            else if ((bgsc & 2) != 0 && (y & 0x20) != 0)
            {
                offset += 2048;
            }
            return (x & 31) * 2 + (y & 31) * 64 + offset;
        }

        private static int FloorDiv8(int value)
        {
            return value >= 0 ? value >> 3 : -(((-value) + 7) >> 3);
        }

        private sealed class BackgroundCache
        {
            internal LayerPixel[] Pixels;
            internal int Width;
            internal int Height;
            internal int SampleX;
            internal int SampleY;
            internal int BucketX;
            internal int BucketY;
            internal int ChrBase;
            internal byte Bgsc;
            internal byte Bgmode;
            internal byte[] MapSnapshot;
            internal byte[] ChrSnapshot;
            internal byte[] DecodedTiles;
            internal byte[] DecodedTileSnapshot;
            internal byte[] DecodedTileValid;
            internal int[] DecodedTileEpoch;
            internal int DecodedChrBase;
            internal int DecodedBits;
            internal int DecodeEpoch;
            internal byte[] PlaneUsedTiles;
            internal byte[] BuildingUsedTiles;
            internal int PlaneChrBase;
            internal int PlaneBits;
            internal bool PlaneTilesCacheable;
            internal bool RecordUsedTiles;
            internal bool BuildingUncacheableTile;
            internal int[] RasterState;
            internal byte[] RasterVramMask;
            internal byte[] RasterVramSnapshot;
            internal bool RasterValid;
            internal LayerPixel[] FirstLine;
            internal bool FirstLineDirect;
            internal bool Valid;

            internal void EnsurePixels(int width, int height)
            {
                if (Pixels != null && Width == width && Height == height) return;
                Width = width;
                Height = height;
                Pixels = new LayerPixel[width * height];
                Valid = false;
            }

            internal void EnsureFirstLine(int width)
            {
                if (FirstLine == null || FirstLine.Length != width)
                    FirstLine = new LayerPixel[width];
            }
        }

        private enum PrepareOutcome : byte
        {
            Hit,
            Miss,
            RasterMiss,
            CacheDisabled
        }

        private readonly struct LayerPixel
        {
            internal readonly byte PaletteIndex;
            internal readonly byte Priority;
            internal readonly byte Source;
            internal readonly bool ObjMathEligible;
            internal readonly bool Opaque;
            internal LayerPixel(byte paletteIndex, byte priority, byte source, bool objMathEligible, bool opaque)
            {
                PaletteIndex = paletteIndex;
                Priority = priority;
                Source = source;
                ObjMathEligible = objMathEligible;
                Opaque = opaque;
            }
        }

        private readonly struct SpritePixel
        {
            internal readonly byte PaletteIndex;
            internal readonly byte Priority;
            internal readonly bool Opaque;
            internal SpritePixel(byte paletteIndex, byte priority, bool opaque)
            {
                PaletteIndex = paletteIndex;
                Priority = priority;
                Opaque = opaque;
            }
        }

        private readonly struct LineState
        {
            internal readonly byte Inidisp, Objsel, Bgmode, Bg1sc, Bg2sc, Bg3sc, Bg12nba, Bg34nba;
            internal readonly byte W12sel, W34sel, Wobjsel, Wh0, Wh1, Wh2, Wh3, Wbglog, Wobjlog;
            internal readonly byte Tm, Ts, Tmw, Tsw, Cgwsel, Cgadsub;
            internal readonly int ScrollX1, ScrollY1, ScrollX2, ScrollY2, ScrollX3, ScrollY3;
            internal readonly ushort FixedColor;

            internal LineState(byte[] r, int sx1, int sy1, int sx2, int sy2, int sx3, int sy3, ushort fixedColor)
            {
                Inidisp = r[0]; Objsel = r[1]; Bgmode = r[5];
                Bg1sc = r[7]; Bg2sc = r[8]; Bg3sc = r[9]; Bg12nba = r[11]; Bg34nba = r[12];
                W12sel = r[35]; W34sel = r[36]; Wobjsel = r[37];
                Wh0 = r[38]; Wh1 = r[39]; Wh2 = r[40]; Wh3 = r[41];
                Wbglog = r[42]; Wobjlog = r[43]; Tm = r[44]; Ts = r[45]; Tmw = r[46]; Tsw = r[47];
                Cgwsel = r[48]; Cgadsub = r[49];
                ScrollX1 = sx1; ScrollY1 = sy1; ScrollX2 = sx2; ScrollY2 = sy2; ScrollX3 = sx3; ScrollY3 = sy3;
                FixedColor = fixedColor;
            }
        }
    }
}
