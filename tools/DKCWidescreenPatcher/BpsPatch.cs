using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DkcWidescreenPatcher
{
    internal static class BpsPatch
    {
        private static readonly byte[] Header = Encoding.ASCII.GetBytes("BPS1");

        internal static byte[] Apply(byte[] source, byte[] patch)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (patch == null) throw new ArgumentNullException(nameof(patch));
            if (patch.Length < 16 || !patch.Take(4).SequenceEqual(Header))
                throw new InvalidDataException("This is not a valid BPS1 patch.");

            uint storedPatchCrc = ReadUInt32(patch, patch.Length - 4);
            uint actualPatchCrc = Crc32.Compute(patch, 0, patch.Length - 4);
            if (storedPatchCrc != actualPatchCrc)
                throw new InvalidDataException("The BPS patch is damaged (patch checksum mismatch).");

            int footerOffset = patch.Length - 12;
            uint expectedSourceCrc = ReadUInt32(patch, footerOffset);
            uint actualSourceCrc = Crc32.Compute(source, 0, source.Length);
            if (expectedSourceCrc != actualSourceCrc)
                throw new InvalidDataException("The selected ROM is not the clean DKC USA v1.0 ROM expected by this patch.");

            int cursor = 4;
            long sourceSize = ReadNumber(patch, ref cursor, footerOffset);
            long targetSize = ReadNumber(patch, ref cursor, footerOffset);
            long metadataSize = ReadNumber(patch, ref cursor, footerOffset);
            if (sourceSize != source.Length)
                throw new InvalidDataException("The selected ROM has the wrong size for this patch.");
            if (targetSize < 0 || targetSize > int.MaxValue)
                throw new InvalidDataException("The patch target is too large.");
            if (metadataSize < 0 || metadataSize > footerOffset - cursor)
                throw new InvalidDataException("The patch metadata is truncated.");
            cursor += (int)metadataSize;

            byte[] target = new byte[(int)targetSize];
            int outputOffset = 0;
            long sourceRelativeOffset = 0;
            long targetRelativeOffset = 0;

            while (outputOffset < target.Length)
            {
                long encoded = ReadNumber(patch, ref cursor, footerOffset);
                int action = (int)(encoded & 3);
                long lengthLong = (encoded >> 2) + 1;
                if (lengthLong > int.MaxValue || lengthLong > target.Length - outputOffset)
                    throw new InvalidDataException("A BPS action exceeds the target ROM size.");
                int length = (int)lengthLong;

                switch (action)
                {
                    case 0: // SourceRead: source and target offsets are identical.
                        EnsureRange(outputOffset, length, source.Length, "source-read");
                        Buffer.BlockCopy(source, outputOffset, target, outputOffset, length);
                        outputOffset += length;
                        break;

                    case 1: // TargetRead: literal bytes follow the action.
                        EnsureRange(cursor, length, footerOffset, "target-read patch data");
                        Buffer.BlockCopy(patch, cursor, target, outputOffset, length);
                        cursor += length;
                        outputOffset += length;
                        break;

                    case 2: // SourceCopy: relative source seek, then copy.
                        sourceRelativeOffset += DecodeSigned(ReadNumber(patch, ref cursor, footerOffset));
                        EnsureRange(sourceRelativeOffset, length, source.Length, "source-copy");
                        Buffer.BlockCopy(source, (int)sourceRelativeOffset, target, outputOffset, length);
                        sourceRelativeOffset += length;
                        outputOffset += length;
                        break;

                    case 3: // TargetCopy: relative target seek; overlap is intentional.
                        targetRelativeOffset += DecodeSigned(ReadNumber(patch, ref cursor, footerOffset));
                        EnsureRange(targetRelativeOffset, length, target.Length, "target-copy");
                        for (int i = 0; i < length; i++)
                            target[outputOffset++] = target[(int)targetRelativeOffset++];
                        break;

                    default:
                        throw new InvalidDataException("Unknown BPS action.");
                }
            }

            if (cursor != footerOffset)
                throw new InvalidDataException("The BPS action stream has trailing or missing data.");
            uint expectedTargetCrc = ReadUInt32(patch, footerOffset + 4);
            uint actualTargetCrc = Crc32.Compute(target, 0, target.Length);
            if (expectedTargetCrc != actualTargetCrc)
                throw new InvalidDataException("The patched ROM checksum is incorrect.");
            return target;
        }

        internal static byte[] Create(byte[] source, byte[] target, string metadata)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (target == null) throw new ArgumentNullException(nameof(target));
            byte[] metadataBytes = Encoding.UTF8.GetBytes(metadata ?? string.Empty);

            using (var stream = new MemoryStream())
            {
                stream.Write(Header, 0, Header.Length);
                WriteNumber(stream, source.Length);
                WriteNumber(stream, target.Length);
                WriteNumber(stream, metadataBytes.Length);
                stream.Write(metadataBytes, 0, metadataBytes.Length);

                int offset = 0;
                while (offset < target.Length)
                {
                    if (offset < source.Length && source[offset] == target[offset])
                    {
                        int start = offset++;
                        while (offset < target.Length && offset < source.Length &&
                               source[offset] == target[offset])
                            offset++;
                        WriteAction(stream, 0, offset - start);
                        continue;
                    }

                    int literalStart = offset++;
                    while (offset < target.Length)
                    {
                        int equalRun = 0;
                        while (offset + equalRun < target.Length &&
                               offset + equalRun < source.Length &&
                               source[offset + equalRun] == target[offset + equalRun] &&
                               equalRun < 4)
                            equalRun++;
                        if (equalRun == 4) break;
                        offset++;
                    }
                    int literalLength = offset - literalStart;
                    WriteAction(stream, 1, literalLength);
                    stream.Write(target, literalStart, literalLength);
                }

                WriteUInt32(stream, Crc32.Compute(source, 0, source.Length));
                WriteUInt32(stream, Crc32.Compute(target, 0, target.Length));
                byte[] withoutPatchCrc = stream.ToArray();
                WriteUInt32(stream, Crc32.Compute(withoutPatchCrc, 0, withoutPatchCrc.Length));
                byte[] result = stream.ToArray();

                // The creator is release tooling; verify its result before returning it.
                byte[] reapplied = Apply(source, result);
                if (!reapplied.SequenceEqual(target))
                    throw new InvalidOperationException("Internal BPS round-trip verification failed.");
                return result;
            }
        }

        private static void WriteAction(Stream stream, int action, int length)
        {
            if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
            WriteNumber(stream, ((long)(length - 1) << 2) | (uint)action);
        }

        private static long ReadNumber(byte[] data, ref int cursor, int limit)
        {
            long value = 0;
            long shift = 1;
            while (true)
            {
                if (cursor >= limit) throw new InvalidDataException("The BPS variable integer is truncated.");
                byte current = data[cursor++];
                checked { value += (current & 0x7F) * shift; }
                if ((current & 0x80) != 0) return value;
                checked
                {
                    shift <<= 7;
                    value += shift;
                }
            }
        }

        private static void WriteNumber(Stream stream, long value)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            while (true)
            {
                byte current = (byte)(value & 0x7F);
                value >>= 7;
                if (value == 0)
                {
                    stream.WriteByte((byte)(current | 0x80));
                    return;
                }
                stream.WriteByte(current);
                value--;
            }
        }

        private static long DecodeSigned(long value)
        {
            long magnitude = value >> 1;
            return (value & 1) != 0 ? -magnitude : magnitude;
        }

        private static void EnsureRange(long offset, int length, int total, string operation)
        {
            if (offset < 0 || offset > total || length < 0 || offset + length > total)
                throw new InvalidDataException("Invalid BPS " + operation + " range.");
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            return (uint)(data[offset] | (data[offset + 1] << 8) |
                          (data[offset + 2] << 16) | (data[offset + 3] << 24));
        }

        private static void WriteUInt32(Stream stream, uint value)
        {
            stream.WriteByte((byte)value);
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 24));
        }

        private static class Crc32
        {
            private static readonly uint[] Table = BuildTable();

            internal static uint Compute(byte[] data, int offset, int count)
            {
                uint crc = 0xFFFFFFFF;
                int end = checked(offset + count);
                for (int i = offset; i < end; i++)
                    crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
                return ~crc;
            }

            private static uint[] BuildTable()
            {
                var table = new uint[256];
                for (uint i = 0; i < table.Length; i++)
                {
                    uint value = i;
                    for (int bit = 0; bit < 8; bit++)
                        value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
                    table[i] = value;
                }
                return table;
            }
        }
    }
}
