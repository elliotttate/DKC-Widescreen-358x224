using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace DKCTilemapInspector
{
    internal sealed class CaptureOptions
    {
        public int NativeWidth = 256;
        public int TargetWideWidth = 358;
        public int RendererExtraTiles = 7;
        public int ViewHeight = 224;
        public double HighSeamThreshold = 0.42;
    }

    internal sealed class CaptureResult
    {
        public string Folder;
        public string ManifestPath;
        public int Frame;
        public List<int> Layers = new List<int>();

        public string ToJson()
        {
            return Json.Object(new Dictionary<string, object>
            {
                { "folder", Folder }, { "manifest", ManifestPath }, { "frame", Frame }, { "layers", Layers }
            });
        }
    }

    internal sealed class LayerSnapshot
    {
        public int Frame;
        public int ScrollX;
        public int ScrollY;
        public int MapWidth;
        public int MapHeight;
        public int WideFirstWorldTile;
        public int WideLastWorldTile;
        public ushort[] Entries;
    }

    internal sealed class TilemapCaptureService
    {
        private static readonly int[][] BitsPerPixel =
        {
            new[] { 2, 4, 4, 8, 8, 4, 4, 7 },
            new[] { 2, 4, 4, 4, 2, 2, 0, 7 }
        };

        private readonly string _root;
        private readonly ManualLogSource _log;
        private readonly Dictionary<int, LayerSnapshot> _previous = new Dictionary<int, LayerSnapshot>();

        public TilemapCaptureService(string root, ManualLogSource log)
        {
            _root = root;
            _log = log;
            Directory.CreateDirectory(root);
        }

        public CaptureResult Capture(object master, string reason, IList<int> requestedLayers, CaptureOptions options)
        {
            if (master == null) throw new InvalidOperationException("No active MasterExecutor. Load a ROM first.");
            var ppu = Reflect.Get(master, "CorePPU");
            if (ppu == null) throw new InvalidOperationException("The active emulator does not expose CorePPU.");
            var vram = Reflect.BytesCall(ppu, "GetPPUMemory");
            var cgram = Reflect.BytesCall(ppu, "GetCGMemory");
            var io = Reflect.BytesCall(ppu, "GetIORegisters");
            var state = Reflect.TryCall(ppu, "GetState");
            if (vram == null || vram.Length < 0x10000) throw new InvalidOperationException("PPU VRAM is unavailable or shorter than 64 KiB.");
            if (cgram == null || cgram.Length < 0x200) throw new InvalidOperationException("PPU CGRAM is unavailable or shorter than 512 bytes.");
            if (io == null || io.Length <= 307) throw new InvalidOperationException("PPU register mirror is unavailable.");
            if (state == null) throw new InvalidOperationException("PPU state is unavailable.");

            var frame = Reflect.IntCall(master, "GetFrameNo", -1);
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            var folder = Path.Combine(_root, "capture-f" + frame.ToString("D8", CultureInfo.InvariantCulture) + "-" + timestamp);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "vram.bin"), vram);
            File.WriteAllBytes(Path.Combine(folder, "cgram.bin"), cgram);
            File.WriteAllBytes(Path.Combine(folder, "io-registers.bin"), io);

            var layerObjects = new List<object>();
            var captured = new List<int>();
            foreach (var layer in requestedLayers.Distinct().Where(x => x == 1 || x == 2))
            {
                layerObjects.Add(CaptureLayer(folder, frame, layer, vram, cgram, io, state, options));
                captured.Add(layer);
            }
            if (captured.Count == 0) throw new ArgumentException("Capture layers must contain BG1 and/or BG2.");

            var manifestPath = Path.Combine(folder, "capture.json");
            var manifest = new Dictionary<string, object>
            {
                { "schema", "dkc-tilemap-inspector/v1" },
                { "utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
                { "reason", reason ?? "manual" },
                { "frame", frame },
                { "ppuMode", io[261] & 7 },
                { "nativeViewportWidth", options.NativeWidth },
                { "targetViewportWidth", options.TargetWideWidth },
                { "rendererExtraTilesPerSide", options.RendererExtraTiles },
                { "rendererSampleWidth", options.NativeWidth + options.RendererExtraTiles * 16 },
                { "viewHeight", options.ViewHeight },
                { "layers", layerObjects },
                { "heuristicNotice", "Flags are diagnostic candidates, not proof. Natural art edges, transparent tiles, and game look-ahead buffering can resemble stale columns." }
            };
            File.WriteAllText(manifestPath, Json.Object(manifest), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(_root, "latest.json"), Json.Object(new Dictionary<string, object>
            {
                { "folder", folder }, { "manifest", manifestPath }, { "frame", frame }, { "layers", captured }
            }), new UTF8Encoding(false));
            _log.LogInfo("Tilemap capture saved: " + folder);
            return new CaptureResult { Folder = folder, ManifestPath = manifestPath, Frame = frame, Layers = captured };
        }

        private object CaptureLayer(string folder, int frame, int layer, byte[] vram, byte[] cgram, byte[] io,
            object state, CaptureOptions options)
        {
            var index = layer - 1;
            var bgsc = io[263 + index];
            var mode = io[261] & 7;
            var tileSize = (io[261] >> (4 + index)) & 1;
            var bits = BitsPerPixel[index][mode];
            var tilemapBase = (bgsc & 0xFC) << 9;
            var chrRegister = io[267];
            var chrBase = layer == 1 ? (chrRegister & 0x0F) << 13 : ((chrRegister >> 4) & 0x0F) << 13;
            var mapWidth = (bgsc & 1) != 0 ? 64 : 32;
            var mapHeight = (bgsc & 2) != 0 ? 64 : 32;
            var scrollX = Reflect.Get<int>(state, "_scroll" + layer + "X");
            var scrollY = Reflect.Get<int>(state, "_scroll" + layer + "Y");
            var renderMargin = options.RendererExtraTiles * 8;
            var renderWidth = options.NativeWidth + renderMargin * 2;
            var wideStartWorldPixel = scrollX - renderMargin;
            var wideFirstWorldTile = FloorDiv(wideStartWorldPixel, 8);
            var wideLastWorldTile = FloorDiv(wideStartWorldPixel + renderWidth - 1, 8);
            var entries = ReadWholeMap(vram, tilemapBase, bgsc, mapWidth, mapHeight);
            LayerSnapshot previous;
            _previous.TryGetValue(layer, out previous);

            bool[] opaque;
            var rawPixels = RenderViewport(vram, cgram, bgsc, tilemapBase, chrBase, bits, tileSize, mode, layer,
                wideStartWorldPixel, scrollY, renderWidth, options.ViewHeight, out opaque);
            var rawPath = Path.Combine(folder, "bg" + layer + "-viewport-raw.png");
            WritePng(rawPath, renderWidth, options.ViewHeight, rawPixels);

            var seamScores = CalculateSeams(rawPixels, opaque, renderWidth, options.ViewHeight);
            var columns = BuildColumnRows(frame, layer, vram, bgsc, tilemapBase, chrBase, bits, tileSize,
                scrollX, scrollY, renderWidth, options.ViewHeight, renderMargin, entries, mapWidth, mapHeight,
                previous, seamScores, opaque, options.HighSeamThreshold);
            var annotated = (Color32[])rawPixels.Clone();
            DrawAnnotations(annotated, renderWidth, options.ViewHeight, renderMargin, options.NativeWidth,
                options.TargetWideWidth, seamScores, options.HighSeamThreshold);
            var annotatedPath = Path.Combine(folder, "bg" + layer + "-viewport-annotated.png");
            WritePng(annotatedPath, renderWidth, options.ViewHeight, annotated);

            var mapPath = Path.Combine(folder, "bg" + layer + "-tilemap.png");
            WriteMapPng(mapPath, entries, mapWidth, mapHeight);
            var csvPath = Path.Combine(folder, "bg" + layer + "-columns.csv");
            WriteColumnsCsv(csvPath, columns);

            var highSeams = columns.Where(x => (bool)x["highSeam"]).Select(x => x["viewColumn"]).ToList();
            var staleCandidates = columns.Where(x => ((string)x["flags"]).Contains("stale-candidate"))
                .Select(x => x["viewColumn"]).ToList();
            var layerJson = new Dictionary<string, object>
            {
                { "layer", "BG" + layer }, { "scrollX", scrollX }, { "scrollY", scrollY },
                { "bgsc", "0x" + bgsc.ToString("X2") }, { "tilemapBase", "0x" + tilemapBase.ToString("X4") },
                { "chrBase", "0x" + chrBase.ToString("X4") }, { "bitsPerPixel", bits },
                { "largeTiles", tileSize != 0 }, { "mapWidthEntries", mapWidth }, { "mapHeightEntries", mapHeight },
                { "rendererWideWorldPixelStart", wideStartWorldPixel },
                { "rendererWideWorldPixelEnd", wideStartWorldPixel + renderWidth - 1 },
                { "nativeWorldPixelStart", scrollX }, { "nativeWorldPixelEnd", scrollX + options.NativeWidth - 1 },
                { "previousFrame", previous == null ? -1 : previous.Frame },
                { "cameraDeltaX", previous == null ? 0 : scrollX - previous.ScrollX },
                { "highSeamColumns", highSeams }, { "staleCandidateColumns", staleCandidates },
                { "columnCsv", csvPath }, { "rawViewportPng", rawPath },
                { "annotatedViewportPng", annotatedPath }, { "tilemapPng", mapPath }
            };

            _previous[layer] = new LayerSnapshot
            {
                Frame = frame, ScrollX = scrollX, ScrollY = scrollY, MapWidth = mapWidth, MapHeight = mapHeight,
                WideFirstWorldTile = wideFirstWorldTile, WideLastWorldTile = wideLastWorldTile, Entries = entries
            };
            return layerJson;
        }

        private static List<Dictionary<string, object>> BuildColumnRows(int frame, int layer, byte[] vram, byte bgsc,
            int tilemapBase, int chrBase, int bits, int tileSize, int scrollX, int scrollY, int width, int height,
            int margin, ushort[] entries, int mapWidth, int mapHeight, LayerSnapshot previous, double[] seamScores,
            bool[] opaque, double highSeamThreshold)
        {
            var result = new List<Dictionary<string, object>>();
            var visibleRows = (height + 7) / 8 + 1;
            var firstWorldYTile = FloorDiv(scrollY, 8);
            var signatures = new Dictionary<ulong, List<int>>();
            var temp = new List<Tuple<int, ulong>>();
            for (var viewColumn = 0; viewColumn < width / 8; viewColumn++)
            {
                var worldPixelX = scrollX - margin + viewColumn * 8;
                var worldTileX = FloorDiv(worldPixelX, 8);
                ulong hash = 1469598103934665603UL;
                for (var row = 0; row < visibleRows; row++)
                {
                    var worldTileY = firstWorldYTile + row;
                    var addr = (tilemapBase + GetTileAddress(worldTileX, worldTileY, bgsc, tileSize)) & 0xFFFF;
                    var raw = (ushort)(vram[addr] | (vram[(addr + 1) & 0xFFFF] << 8));
                    hash ^= raw;
                    hash *= 1099511628211UL;
                }
                temp.Add(Tuple.Create(viewColumn, hash));
                List<int> list;
                if (!signatures.TryGetValue(hash, out list)) signatures[hash] = list = new List<int>();
                list.Add(viewColumn);
            }

            for (var viewColumn = 0; viewColumn < width / 8; viewColumn++)
            {
                var worldPixelX = scrollX - margin + viewColumn * 8;
                var worldTileX = FloorDiv(worldPixelX, 8);
                var mapX = Mod(tileSize != 0 ? FloorDiv(worldTileX, 2) : worldTileX, mapWidth);
                var firstWorldY = firstWorldYTile;
                var mapY = Mod(tileSize != 0 ? FloorDiv(firstWorldY, 2) : firstWorldY, mapHeight);
                var firstAddr = (tilemapBase + GetTileAddress(worldTileX, firstWorldY, bgsc, tileSize)) & 0xFFFF;
                var firstRaw = (ushort)(vram[firstAddr] | (vram[(firstAddr + 1) & 0xFFFF] << 8));
                var changedRows = ChangedRows(previous, entries, mapX, mapWidth, mapHeight);
                var newlyExposed = previous != null && (worldTileX < previous.WideFirstWorldTile || worldTileX > previous.WideLastWorldTile);
                var transparent = TransparentRatioForColumn(opaque, width, height, viewColumn * 8, 8);
                var seamLeft = viewColumn == 0 ? 0d : seamScores[viewColumn * 8];
                var seamRight = (viewColumn + 1) * 8 >= width ? 0d : seamScores[(viewColumn + 1) * 8];
                var highSeam = Math.Max(seamLeft, seamRight) >= highSeamThreshold;
                var duplicateColumns = signatures[temp[viewColumn].Item2].Where(x => Math.Abs(x - viewColumn) > 1).ToList();
                var flags = new List<string>();
                if (highSeam) flags.Add("high-seam");
                if (transparent >= 0.85) flags.Add("mostly-transparent");
                if (duplicateColumns.Count > 0) flags.Add("duplicate-distant");
                if (newlyExposed && changedRows == 0 && (highSeam || transparent >= 0.85)) flags.Add("stale-candidate");
                var zone = viewColumn * 8 < margin ? "left-extension"
                    : viewColumn * 8 >= margin + 256 ? "right-extension" : "native";
                result.Add(new Dictionary<string, object>
                {
                    { "frame", frame }, { "layer", "BG" + layer }, { "viewColumn", viewColumn },
                    { "screenX", viewColumn * 8 }, { "zone", zone }, { "worldPixelX", worldPixelX },
                    { "worldTileX", worldTileX }, { "mapX", mapX }, { "firstMapY", mapY },
                    { "firstVramAddress", "0x" + firstAddr.ToString("X4") },
                    { "firstRawEntry", "0x" + firstRaw.ToString("X4") }, { "firstTileNumber", firstRaw & 0x3FF },
                    { "firstPalette", (firstRaw >> 10) & 7 }, { "firstPriority", (firstRaw & 0x2000) != 0 },
                    { "firstHFlip", (firstRaw & 0x4000) != 0 }, { "firstVFlip", (firstRaw & 0x8000) != 0 },
                    { "chrBase", "0x" + chrBase.ToString("X4") }, { "bitsPerPixel", bits },
                    { "signature", "0x" + temp[viewColumn].Item2.ToString("X16") },
                    { "transparentRatio", Math.Round(transparent, 4) }, { "seamLeft", Math.Round(seamLeft, 4) },
                    { "seamRight", Math.Round(seamRight, 4) }, { "highSeam", highSeam },
                    { "newlyExposedSincePrevious", newlyExposed }, { "mapRowsChangedSincePrevious", changedRows },
                    { "duplicateDistantColumns", duplicateColumns }, { "flags", string.Join("|", flags) }
                });
            }
            return result;
        }

        private static ushort[] ReadWholeMap(byte[] vram, int tilemapBase, byte bgsc, int width, int height)
        {
            var result = new ushort[width * height];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var address = (tilemapBase + GetTileAddress(x, y, bgsc, 0)) & 0xFFFF;
                result[y * width + x] = (ushort)(vram[address] | (vram[(address + 1) & 0xFFFF] << 8));
            }
            return result;
        }

        private static int ChangedRows(LayerSnapshot previous, ushort[] current, int mapX, int width, int height)
        {
            if (previous == null || previous.MapWidth != width || previous.MapHeight != height) return -1;
            var changed = 0;
            for (var y = 0; y < height; y++)
                if (current[y * width + mapX] != previous.Entries[y * width + mapX]) changed++;
            return changed;
        }

        private static Color32[] RenderViewport(byte[] vram, byte[] cgram, byte bgsc, int tilemapBase, int chrBase,
            int bits, int tileSize, int mode, int layer, int worldStartX, int worldStartY, int width, int height,
            out bool[] opaque)
        {
            var pixels = new Color32[width * height];
            opaque = new bool[pixels.Length];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var worldX = worldStartX + x;
                var worldY = worldStartY + y;
                var worldTileX = FloorDiv(worldX, 8);
                var worldTileY = FloorDiv(worldY, 8);
                var address = (tilemapBase + GetTileAddress(worldTileX, worldTileY, bgsc, tileSize)) & 0xFFFF;
                var entry = vram[address] | (vram[(address + 1) & 0xFFFF] << 8);
                var tile = entry & 0x3FF;
                var hFlip = (entry & 0x4000) != 0;
                var vFlip = (entry & 0x8000) != 0;
                if (tileSize != 0)
                {
                    if (((worldTileX & 1) ^ (hFlip ? 1 : 0)) != 0) tile++;
                    if (((worldTileY & 1) ^ (vFlip ? 1 : 0)) != 0) tile += 16;
                }
                var pixelX = Mod(worldX, 8);
                var pixelY = Mod(worldY, 8);
                if (hFlip) pixelX = 7 - pixelX;
                if (vFlip) pixelY = 7 - pixelY;
                var colorIndex = DecodePixel(vram, (chrBase + tile * 8 * Math.Max(bits, 1)) & 0xFFFF,
                    Math.Max(bits, 1), pixelX, pixelY);
                var destination = (height - y - 1) * width + x;
                if (bits == 0 || bits == 7 || colorIndex == 0)
                {
                    var checker = ((x >> 3) ^ (y >> 3)) & 1;
                    var value = (byte)(checker == 0 ? 22 : 34);
                    pixels[destination] = new Color32(value, value, value, 255);
                    opaque[destination] = false;
                    continue;
                }
                var startColor = mode == 0 ? 32 * (layer - 1) : 0;
                var paletteIndex = bits == 8 ? colorIndex : startColor + ((entry >> 10) & 7) * (1 << bits) + colorIndex;
                paletteIndex &= 0xFF;
                var color = cgram[paletteIndex * 2] | (cgram[paletteIndex * 2 + 1] << 8);
                pixels[destination] = new Color32((byte)((color & 31) * 255 / 31),
                    (byte)(((color >> 5) & 31) * 255 / 31), (byte)(((color >> 10) & 31) * 255 / 31), 255);
                opaque[destination] = true;
            }
            return pixels;
        }

        private static int DecodePixel(byte[] vram, int tileAddress, int bits, int x, int y)
        {
            var bit = 7 - x;
            var result = 0;
            for (var plane = 0; plane < bits && plane < 8; plane++)
            {
                var pair = plane >> 1;
                var address = (tileAddress + pair * 16 + y * 2 + (plane & 1)) & 0xFFFF;
                result |= ((vram[address] >> bit) & 1) << plane;
            }
            return result;
        }

        private static double[] CalculateSeams(Color32[] pixels, bool[] opaque, int width, int height)
        {
            var scores = new double[width];
            for (var x = 1; x < width; x++)
            {
                double total = 0;
                var count = 0;
                for (var y = 0; y < height; y++)
                {
                    var left = y * width + x - 1;
                    var right = left + 1;
                    if (!opaque[left] && !opaque[right]) continue;
                    var a = pixels[left];
                    var b = pixels[right];
                    total += (Math.Abs(a.r - b.r) + Math.Abs(a.g - b.g) + Math.Abs(a.b - b.b)) / 765d;
                    count++;
                }
                scores[x] = count == 0 ? 0 : total / count;
            }
            return scores;
        }

        private static double TransparentRatioForColumn(bool[] opaque, int width, int height, int startX, int span)
        {
            var transparent = 0;
            var total = 0;
            for (var y = 0; y < height; y++)
            for (var x = startX; x < Math.Min(width, startX + span); x++)
            {
                if (!opaque[y * width + x]) transparent++;
                total++;
            }
            return total == 0 ? 0 : (double)transparent / total;
        }

        private static void DrawAnnotations(Color32[] pixels, int width, int height, int nativeStart, int nativeWidth,
            int targetWidth, double[] seamScores, double seamThreshold)
        {
            var yellow = new Color32(255, 220, 32, 255);
            var cyan = new Color32(0, 235, 255, 255);
            var red = new Color32(255, 40, 40, 255);
            var targetStart = Math.Max(0, (width - targetWidth) / 2);
            DrawVertical(pixels, width, height, targetStart, yellow);
            DrawVertical(pixels, width, height, Math.Min(width - 1, targetStart + targetWidth - 1), yellow);
            DrawVertical(pixels, width, height, nativeStart, cyan);
            DrawVertical(pixels, width, height, Math.Min(width - 1, nativeStart + nativeWidth - 1), cyan);
            for (var x = 8; x < width; x += 8)
                if (seamScores[x] >= seamThreshold) DrawVerticalDashed(pixels, width, height, x, red);
        }

        private static void DrawVertical(Color32[] pixels, int width, int height, int x, Color32 color)
        {
            if (x < 0 || x >= width) return;
            for (var y = 0; y < height; y++) pixels[y * width + x] = color;
        }

        private static void DrawVerticalDashed(Color32[] pixels, int width, int height, int x, Color32 color)
        {
            if (x < 0 || x >= width) return;
            for (var y = 0; y < height; y++) if ((y & 7) < 4) pixels[y * width + x] = color;
        }

        private static void WritePng(string path, int width, int height, Color32[] pixels)
        {
            Texture2D texture = null;
            try
            {
                texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                texture.SetPixels32(pixels);
                texture.Apply(false, false);
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally
            {
                if (texture != null) UnityEngine.Object.Destroy(texture);
            }
        }

        private static void WriteMapPng(string path, ushort[] entries, int width, int height)
        {
            const int scale = 4;
            var imageWidth = width * scale;
            var imageHeight = height * scale;
            var pixels = new Color32[imageWidth * imageHeight];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var entry = entries[y * width + x];
                var tile = entry & 0x3FF;
                var palette = (entry >> 10) & 7;
                var color = new Color32((byte)((tile * 73 + palette * 37) & 255),
                    (byte)((tile * 29 + palette * 83) & 255), (byte)((tile * 151 + palette * 17) & 255), 255);
                if ((entry & 0x2000) != 0) color = new Color32(255, color.g, color.b, 255);
                for (var py = 0; py < scale; py++)
                for (var px = 0; px < scale; px++)
                    pixels[(imageHeight - (y * scale + py) - 1) * imageWidth + x * scale + px] = color;
            }
            WritePng(path, imageWidth, imageHeight, pixels);
        }

        private static void WriteColumnsCsv(string path, IEnumerable<Dictionary<string, object>> rows)
        {
            var columns = new[]
            {
                "frame", "layer", "viewColumn", "screenX", "zone", "worldPixelX", "worldTileX", "mapX", "firstMapY",
                "firstVramAddress", "firstRawEntry", "firstTileNumber", "firstPalette", "firstPriority", "firstHFlip",
                "firstVFlip", "chrBase", "bitsPerPixel", "signature", "transparentRatio", "seamLeft", "seamRight",
                "highSeam", "newlyExposedSincePrevious", "mapRowsChangedSincePrevious", "duplicateDistantColumns", "flags"
            };
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                writer.WriteLine(string.Join(",", columns));
                foreach (var row in rows)
                    writer.WriteLine(string.Join(",", columns.Select(name => Csv(row[name]))));
            }
        }

        private static string Csv(object value)
        {
            string text;
            if (value is IEnumerable<int>) text = string.Join("|", (IEnumerable<int>)value);
            else if (value is double) text = ((double)value).ToString("0.####", CultureInfo.InvariantCulture);
            else text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        private static int GetTileAddress(int x, int y, byte bgsc, int tileSize)
        {
            if (tileSize > 0) { x >>= 1; y >>= 1; }
            var offset = 0;
            if ((bgsc & 1) != 0)
            {
                if ((x & 0x20) != 0) offset += 2048;
                if ((bgsc & 2) != 0 && (y & 0x20) != 0) offset += 4096;
            }
            else if ((bgsc & 2) != 0 && (y & 0x20) != 0) offset += 2048;
            return (x & 31) * 2 + (y & 31) * 64 + offset;
        }

        private static int FloorDiv(int value, int divisor)
        {
            var quotient = value / divisor;
            var remainder = value % divisor;
            return remainder < 0 ? quotient - 1 : quotient;
        }

        private static int Mod(int value, int modulus)
        {
            var result = value % modulus;
            return result < 0 ? result + modulus : result;
        }
    }
}
