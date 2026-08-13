using System;

namespace SuperZSNESLayerDepthControllerIL2CPP
{
    internal static class ForegroundGroundRasterizer
    {
        internal const int ViewWidth = 368;
        internal const int ViewHeight = 224;
        internal const int ViewLeft = -56;

        internal static bool TryRasterize(byte[] vram, byte[] cgramLines,
            byte[] registers, int[] scrollXLines, int[] scrollYLines,
            byte[] displayLines, byte[] mainScreenLines, byte[] colorControlLines,
            int background, int cutY, bool followGroundEdge, int edgeSearchRadius,
            int[] edgeWorkspace, int[] smoothWorkspace, uint[] destination,
            out string reason)
        {
            reason = string.Empty;
            if (vram == null || vram.Length != 65536)
            {
                reason = "invalid-vram";
                return false;
            }
            if (cgramLines == null ||
                (cgramLines.Length != 512 &&
                 cgramLines.Length != ViewHeight * 512))
            {
                reason = "invalid-cgram";
                return false;
            }
            if (registers == null || registers.Length < 64)
            {
                reason = "invalid-registers";
                return false;
            }
            if (destination == null || destination.Length != ViewWidth * ViewHeight)
            {
                reason = "invalid-destination";
                return false;
            }
            if (edgeWorkspace == null || edgeWorkspace.Length < ViewWidth ||
                smoothWorkspace == null || smoothWorkspace.Length < ViewWidth)
            {
                reason = "invalid-edge-workspace";
                return false;
            }
            if (scrollXLines == null || scrollXLines.Length < ViewHeight ||
                scrollYLines == null || scrollYLines.Length < ViewHeight)
            {
                reason = "invalid-scroll-lines";
                return false;
            }
            if (displayLines == null || displayLines.Length < ViewHeight ||
                mainScreenLines == null || mainScreenLines.Length < ViewHeight ||
                colorControlLines == null || colorControlLines.Length < ViewHeight)
            {
                reason = "invalid-video-lines";
                return false;
            }
            if (background < 0 || background > 2)
            {
                reason = "invalid-background";
                return false;
            }
            if ((registers[5] & 7) != 1)
            {
                reason = "not-mode1";
                return false;
            }
            int mosaicSize = (registers[6] >> 4) & 15;
            if (mosaicSize != 0 && (registers[6] & (1 << background)) != 0)
            {
                reason = "mosaic";
                return false;
            }

            Array.Clear(destination, 0, destination.Length);
            cutY = Math.Max(0, Math.Min(ViewHeight - 1, cutY));
            byte bgmode = registers[5];
            byte bgsc = registers[7 + background];
            bool size16 = ((bgmode >> (4 + background)) & 1) != 0;
            int mapBase = (bgsc & 0xFC) << 9;
            int bits = background == 2 ? 2 : 4;
            int chrBase = GetChrBase(registers, background);

            int decodeStart = followGroundEdge ? 0 : cutY;
            for (int sy = decodeStart; sy < ViewHeight; sy++)
            {
                if ((displayLines[sy] & 0x80) != 0 ||
                    (mainScreenLines[sy] & (1 << background)) == 0)
                    continue;
                if ((colorControlLines[sy] & 1) != 0)
                {
                    reason = "direct-color";
                    return false;
                }
                int brightness = displayLines[sy] & 15;
                int worldY = scrollYLines[sy] + sy;
                int tileY = FloorDiv(worldY, 8);
                int py = Mod(worldY, 8);
                for (int sx = 0; sx < ViewWidth; sx++)
                {
                    int worldX = scrollXLines[sy] + ViewLeft + sx;
                    int tileX = FloorDiv(worldX, 8);
                    int px = Mod(worldX, 8);
                    int address = (mapBase + GetTileAddress(tileX, tileY,
                        bgsc, size16)) & 0xFFFF;
                    int descriptor = vram[address] |
                        (vram[(address + 1) & 0xFFFF] << 8);
                    int color = DecodePixel(vram, descriptor, size16,
                        tileX, tileY, px, py, chrBase, bits);
                    if (color == 0) continue;
                    int palette = (descriptor >> 10) & 7;
                    int paletteIndex = palette * (bits == 2 ? 4 : 16) + color;
                    destination[sy * ViewWidth + sx] = ReadPalette(cgramLines,
                        sy, paletteIndex, brightness);
                }
            }
            if (followGroundEdge)
                ApplyNaturalGroundMask(destination, cutY, edgeSearchRadius,
                    edgeWorkspace, smoothWorkspace);
            return true;
        }

        internal static void CropToOpaqueBounds(uint[] source, int padding,
            uint[] destination, out int left, out int top,
            out int width, out int height)
        {
            if (source == null || source.Length != ViewWidth * ViewHeight)
                throw new ArgumentException("Invalid foreground source.", nameof(source));
            if (destination == null || destination.Length != source.Length)
                throw new ArgumentException("Invalid cropped destination.",
                    nameof(destination));
            int minX = ViewWidth, minY = ViewHeight, maxX = -1, maxY = -1;
            for (int y = 0; y < ViewHeight; y++)
            for (int x = 0; x < ViewWidth; x++)
            {
                if ((source[y * ViewWidth + x] >> 24) == 0) continue;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
            if (maxX < minX || maxY < minY)
            {
                left = top = width = height = 0;
                Array.Clear(destination, 0, destination.Length);
                return;
            }
            padding = Math.Max(0, Math.Min(16, padding));
            left = Math.Max(0, minX - padding);
            top = Math.Max(0, minY - padding);
            int right = Math.Min(ViewWidth - 1, maxX + padding);
            int bottom = Math.Min(ViewHeight - 1, maxY + padding);
            width = right - left + 1;
            height = bottom - top + 1;
            Array.Clear(destination, 0, destination.Length);
            for (int y = 0; y < height; y++)
                Array.Copy(source, (top + y) * ViewWidth + left,
                    destination, y * ViewWidth, width);
        }

        internal static void ApplyNaturalGroundMask(uint[] pixels, int seedY,
            int radius, int[] edges, int[] smooth)
        {
            radius = Math.Max(8, Math.Min(96, radius));
            int minimum = Math.Max(0, seedY - radius);
            int maximum = Math.Min(ViewHeight - 4, seedY + radius);
            int center = ViewWidth / 2;
            int anchor = FindBoundary(pixels, center, minimum, maximum, seedY);
            if (anchor < 0) anchor = seedY;
            edges[center] = anchor;
            for (int x = center + 1; x < ViewWidth; x++)
            {
                int next = FindBoundary(pixels, x,
                    Math.Max(minimum, edges[x - 1] - 4),
                    Math.Min(maximum, edges[x - 1] + 4), edges[x - 1]);
                edges[x] = next < 0 ? edges[x - 1] : next;
            }
            for (int x = center - 1; x >= 0; x--)
            {
                int next = FindBoundary(pixels, x,
                    Math.Max(minimum, edges[x + 1] - 4),
                    Math.Min(maximum, edges[x + 1] + 4), edges[x + 1]);
                edges[x] = next < 0 ? edges[x + 1] : next;
            }
            for (int x = 0; x < ViewWidth; x++)
            {
                int count = 0;
                for (int dx = -4; dx <= 4; dx++)
                    smooth[count++] = edges[Math.Max(0,
                        Math.Min(ViewWidth - 1, x + dx))];
                Array.Sort(smooth, 0, count);
                int edge = Math.Max(0, smooth[count / 2] - 2);
                edges[x] = edge;
                for (int y = 0; y < edge; y++)
                    pixels[y * ViewWidth + x] = 0;
                for (int y = edge; y < ViewHeight; y++)
                    if (!IsGroundColor(pixels[y * ViewWidth + x]))
                        pixels[y * ViewWidth + x] = 0;
            }

            // BG1 contains opaque scenery all the way to the bottom of the
            // screen. That is correct while BG1 is a flat plane, but copying
            // every pixel below the walking edge onto a nearer plane exposes
            // normally hidden dark/rock tiles as a rectangular slab. Follow
            // the sand-coloured connected surface downward and discard the
            // unrelated opaque scenery after a short gap instead.
            for (int x = 0; x < ViewWidth; x++)
            {
                int top = edges[x];
                int lastSurface = Math.Min(ViewHeight - 1, top + 16);
                int misses = 0;
                int limit = Math.Min(ViewHeight - 1, top + 96);
                for (int y = top; y <= limit; y++)
                {
                    int support = 0;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int sampleX = Math.Max(0, Math.Min(ViewWidth - 1, x + dx));
                        if (IsSurfaceCoreColor(
                                pixels[y * ViewWidth + sampleX])) support++;
                    }
                    if (IsSurfaceCoreColor(pixels[y * ViewWidth + x]) ||
                        support >= 2)
                    {
                        lastSurface = y;
                        misses = 0;
                    }
                    else if (++misses > 8 && y > top + 12)
                    {
                        break;
                    }
                }
                smooth[x] = Math.Max(top + 16, Math.Min(top + 72,
                    lastSurface + 3));
            }
            for (int x = 0; x < ViewWidth; x++)
            {
                int count = 0;
                int total = 0;
                for (int dx = -4; dx <= 4; dx++)
                {
                    total += smooth[Math.Max(0,
                        Math.Min(ViewWidth - 1, x + dx))];
                    count++;
                }
                edges[x] = (total + count / 2) / count;
            }
            for (int x = 0; x < ViewWidth; x++)
            for (int y = Math.Min(ViewHeight, edges[x] + 1);
                 y < ViewHeight; y++)
                pixels[y * ViewWidth + x] = 0;
        }

        private static int FindBoundary(uint[] pixels, int x, int minimum,
            int maximum, int preferred)
        {
            int best = -1;
            int bestCost = int.MaxValue;
            for (int y = minimum; y <= maximum; y++)
            {
                int index = y * ViewWidth + x;
                if (!IsSurfaceCoreColor(pixels[index]) ||
                    !IsSurfaceCoreColor(pixels[index + ViewWidth]) ||
                    !IsSurfaceCoreColor(pixels[index + ViewWidth * 2])) continue;
                bool aboveGround = y >= 2 &&
                    (IsSurfaceCoreColor(pixels[index - ViewWidth]) ||
                     IsSurfaceCoreColor(pixels[index - ViewWidth * 2]));
                int cost = Math.Abs(y - preferred) * 8 + (aboveGround ? 2000 : 0);
                if (cost >= bestCost) continue;
                bestCost = cost;
                best = y;
            }
            return best;
        }

        private static bool IsGroundColor(uint argb)
        {
            if ((argb >> 24) == 0) return false;
            int red = (int)((argb >> 16) & 255);
            int green = (int)((argb >> 8) & 255);
            int blue = (int)(argb & 255);
            return red >= 72 && green >= 45 && red >= green + 18 &&
                   green >= blue + 12 && red + green >= 150;
        }

        private static bool IsSurfaceCoreColor(uint argb)
        {
            if (!IsGroundColor(argb)) return false;
            int red = (int)((argb >> 16) & 255);
            int green = (int)((argb >> 8) & 255);
            int blue = (int)(argb & 255);
            return red >= 175 && green >= 105 && blue <= 96 &&
                   red - green >= 35 && red - green <= 100;
        }

        private static int DecodePixel(byte[] vram, int descriptor, bool size16,
            int tileX, int tileY, int px, int py, int chrBase, int bits)
        {
            int tile = descriptor & 0x3FF;
            bool xflip = (descriptor & 0x4000) != 0;
            bool yflip = (descriptor & 0x8000) != 0;
            int localX = px & 7;
            int localY = py & 7;
            if (size16)
            {
                if (((tileX & 1) != 0) ^ xflip) tile++;
                if (((tileY & 1) != 0) ^ yflip) tile += 16;
            }
            if (xflip) localX = 7 - localX;
            if (yflip) localY = 7 - localY;
            int address = (chrBase + (tile & 0x3FF) * bits * 8) & 0xFFFF;
            int bit = 7 - localX;
            int color = 0;
            for (int plane = 0; plane < bits; plane++)
            {
                int planeAddress = address + (plane >> 1) * 16 +
                    localY * 2 + (plane & 1);
                color |= ((vram[planeAddress & 0xFFFF] >> bit) & 1) << plane;
            }
            return color;
        }

        private static uint ReadPalette(byte[] cgram, int line, int index,
            int brightness)
        {
            int address = (cgram.Length == 512 ? 0 : line * 512) +
                (index & 255) * 2;
            int value = cgram[address] | (cgram[address + 1] << 8);
            uint r = Scale((value & 31) * 255 / 31, brightness);
            uint g = Scale(((value >> 5) & 31) * 255 / 31, brightness);
            uint b = Scale(((value >> 10) & 31) * 255 / 31, brightness);
            return 0xFF000000u | (r << 16) | (g << 8) | b;
        }

        private static uint Scale(int channel, int brightness) =>
            (uint)(channel * Math.Max(0, Math.Min(15, brightness)) / 15);

        private static int GetTileAddress(int x, int y, byte bgsc,
            bool tileSize16)
        {
            if (tileSize16) { x >>= 1; y >>= 1; }
            int offset = 0;
            if ((bgsc & 1) != 0)
            {
                if ((x & 0x20) != 0) offset += 2048;
                if ((bgsc & 2) != 0 && (y & 0x20) != 0) offset += 4096;
            }
            else if ((bgsc & 2) != 0 && (y & 0x20) != 0) offset += 2048;
            return (x & 31) * 2 + (y & 31) * 64 + offset;
        }

        private static int GetChrBase(byte[] registers, int background) =>
            background == 0 ? (registers[11] & 0x0F) << 13 :
            background == 1 ? ((registers[11] >> 4) & 0x0F) << 13 :
            (registers[12] & 0x0F) << 13;

        private static int FloorDiv(int value, int divisor)
        {
            int quotient = value / divisor;
            if (value < 0 && value % divisor != 0) quotient--;
            return quotient;
        }

        private static int Mod(int value, int modulus)
        {
            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }
    }
}
