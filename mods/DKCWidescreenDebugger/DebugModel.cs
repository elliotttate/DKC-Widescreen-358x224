using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DKCWidescreenDebugger
{
    internal sealed class AddressRange
    {
        public uint Start;
        public uint End;

        public bool Contains(uint address) { return address >= Start && address <= End; }
        public override string ToString() { return Start == End ? Start.ToString("X6") : Start.ToString("X6") + "-" + End.ToString("X6"); }
    }

    internal static class AddressParser
    {
        public static List<AddressRange> ParseRanges(string text, out string error)
        {
            error = null;
            var result = new List<AddressRange>();
            if (string.IsNullOrWhiteSpace(text)) return result;
            foreach (var raw in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = raw.Split(new[] { '-' }, 2);
                uint start;
                uint end = 0;
                if (!TryHex(pair[0], out start) || (pair.Length == 2 && !TryHex(pair[1], out end)))
                {
                    error = "Invalid SNES address/range: " + raw;
                    return new List<AddressRange>();
                }
                if (pair.Length == 1) end = start;
                start &= 0xFFFFFF;
                end &= 0xFFFFFF;
                if (end < start) { var swap = start; start = end; end = swap; }
                result.Add(new AddressRange { Start = start, End = end });
            }
            return result;
        }

        public static bool TryHex(string text, out uint value)
        {
            text = (text ?? string.Empty).Trim().Replace(":", string.Empty).Replace("_", string.Empty);
            if (text.StartsWith("$")) text = text.Substring(1);
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text.Substring(2);
            return uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
        }

        public static bool Contains(IList<AddressRange> ranges, uint address)
        {
            for (var i = 0; i < ranges.Count; i++) if (ranges[i].Contains(address & 0xFFFFFF)) return true;
            return false;
        }
    }

    internal enum WatchValueType { U8, S8, U16, S16, U24, U32 }

    internal sealed class MemoryWatch
    {
        public uint Address;
        public WatchValueType Type;
        public string Name;
        public ulong LastValue;
        public bool HasLast;

        public int Size
        {
            get
            {
                switch (Type)
                {
                    case WatchValueType.U16:
                    case WatchValueType.S16: return 2;
                    case WatchValueType.U24: return 3;
                    case WatchValueType.U32: return 4;
                    default: return 1;
                }
            }
        }

        public string Format(ulong value)
        {
            switch (Type)
            {
                case WatchValueType.S8: return unchecked((sbyte)value).ToString(CultureInfo.InvariantCulture) + " ($" + value.ToString("X2") + ")";
                case WatchValueType.S16: return unchecked((short)value).ToString(CultureInfo.InvariantCulture) + " ($" + value.ToString("X4") + ")";
                case WatchValueType.U16: return value.ToString(CultureInfo.InvariantCulture) + " ($" + value.ToString("X4") + ")";
                case WatchValueType.U24: return value.ToString(CultureInfo.InvariantCulture) + " ($" + value.ToString("X6") + ")";
                case WatchValueType.U32: return value.ToString(CultureInfo.InvariantCulture) + " ($" + value.ToString("X8") + ")";
                default: return value.ToString(CultureInfo.InvariantCulture) + " ($" + value.ToString("X2") + ")";
            }
        }
    }

    internal static class WatchParser
    {
        public static List<MemoryWatch> Parse(string text, out string error)
        {
            error = null;
            var result = new List<MemoryWatch>();
            if (string.IsNullOrWhiteSpace(text)) return result;
            foreach (var raw in text.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = raw.Trim().Split(':');
                uint address;
                if (parts.Length < 1 || !AddressParser.TryHex(parts[0], out address))
                {
                    error = "Invalid watch address: " + raw.Trim();
                    return new List<MemoryWatch>();
                }
                WatchValueType type = WatchValueType.U8;
                if (parts.Length >= 2 && !Enum.TryParse(parts[1], true, out type))
                {
                    error = "Invalid watch type in '" + raw.Trim() + "'. Use u8, s8, u16, s16, u24, or u32.";
                    return new List<MemoryWatch>();
                }
                result.Add(new MemoryWatch
                {
                    Address = address & 0xFFFFFF,
                    Type = type,
                    Name = parts.Length >= 3 ? string.Join(":", parts.Skip(2).ToArray()).Trim() : "watch_" + address.ToString("X6")
                });
            }
            return result;
        }
    }

    internal enum SearchComparison { Exact, Changed, Unchanged, Increased, Decreased }

    internal sealed class WramSearch
    {
        private byte[] _previous;
        private bool[] _candidates;
        public int CandidateCount { get; private set; }
        public bool Active { get { return _previous != null && _candidates != null; } }

        public void Reset(byte[] ram)
        {
            if (ram == null) return;
            _previous = (byte[])ram.Clone();
            _candidates = Enumerable.Repeat(true, ram.Length).ToArray();
            CandidateCount = ram.Length;
        }

        public void BeginExact(byte[] ram, byte value)
        {
            Reset(ram);
            Filter(ram, SearchComparison.Exact, value);
        }

        public void Filter(byte[] ram, SearchComparison comparison, byte exact = 0)
        {
            if (ram == null) return;
            if (!Active || _previous.Length != ram.Length) Reset(ram);
            var count = 0;
            for (var i = 0; i < ram.Length; i++)
            {
                if (!_candidates[i]) continue;
                var keep = comparison == SearchComparison.Exact ? ram[i] == exact
                    : comparison == SearchComparison.Changed ? ram[i] != _previous[i]
                    : comparison == SearchComparison.Unchanged ? ram[i] == _previous[i]
                    : comparison == SearchComparison.Increased ? ram[i] > _previous[i]
                    : ram[i] < _previous[i];
                _candidates[i] = keep;
                if (keep) count++;
            }
            Buffer.BlockCopy(ram, 0, _previous, 0, ram.Length);
            CandidateCount = count;
        }

        public IEnumerable<int> Results(int maximum)
        {
            if (!Active) yield break;
            var emitted = 0;
            for (var i = 0; i < _candidates.Length && emitted < maximum; i++)
            {
                if (!_candidates[i]) continue;
                emitted++;
                yield return i;
            }
        }
    }

    internal sealed class RingLog
    {
        private readonly int _capacity;
        private readonly Queue<string> _lines = new Queue<string>();
        public RingLog(int capacity) { _capacity = Math.Max(10, capacity); }
        public IEnumerable<string> Lines { get { return _lines; } }
        public void Add(string value)
        {
            while (_lines.Count >= _capacity) _lines.Dequeue();
            _lines.Enqueue(value);
        }
    }
}
