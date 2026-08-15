using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using DKCObjectLifecycleTracer;

internal static class Program
{
    private static int _passed;

    private static void Main()
    {
        Run("WRAM aliases normalize", TestAliases);
        Run("scanner pool semantics distinguish exhaustion from success", TestScannerSemantics);
        var cleanRom = Environment.GetEnvironmentVariable("DKC_CLEAN_ROM");
        if (!string.IsNullOrWhiteSpace(cleanRom)) Run("clean USA v1.0 ROM scanner opcodes match semantic PCs", () => TestScannerOpcodes(cleanRom));
        else Console.WriteLine("SKIP: clean-ROM opcode test (set DKC_CLEAN_ROM or pass -CleanRomPath to build.ps1)");
        Run("bank-BD actor pool uses raw even indexes $02-$32", TestActorPoolIndexing);
        Run("world-map WRAM is gated out", TestWorldMapGate);
        Run("invalid entrance is rejected before memory reads", TestInvalidEntranceDoesNotRead);
        Run("actor allocation diff carries source", TestActorDiff);
        Run("type-5 group children decode to bookkeeping indexes", TestGroupDecode);
        Run("active group missing child is a non-definitive observation", TestMissingGroupChild);
        Run("stale bookkeeping is an observation, not an anomaly", TestStaleBookmarkClassification);
        Run("odd actor index is a structural anomaly", TestInvalidActorIndex);
        Run("valid actor/bookkeeping ownership is accepted", TestValidOwnership);
        Console.WriteLine("All " + _passed + " DKC object-lifecycle tracer tests passed.");
    }

    private static void Run(string name, Action test)
    {
        test();
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static void TestAliases()
    {
        Equal(0x0D45, DkcRam.NormalizeWramAddress(0x7E0D45));
        Equal(0x0D45, DkcRam.NormalizeWramAddress(0x000D45));
        Equal(0x12345, DkcRam.NormalizeWramAddress(0x7F2345));
        Equal(-1, DkcRam.NormalizeWramAddress(0x402345));
    }

    private static void TestScannerSemantics()
    {
        Contains("exhausted", ScannerSemantics.Describe(0xBDF3B1));
        Contains("found", ScannerSemantics.Describe(0xBDF3B5));
        Contains("succeeded", ScannerSemantics.Describe(0xBDF3BD));
        Contains("exhausted", ScannerSemantics.Describe(0xBDF3D2));
        Contains("found", ScannerSemantics.Describe(0xBDF3D6));
        Contains("succeeded", ScannerSemantics.Describe(0xBDF3DE));
        True(!ScannerSemantics.Describe(0xBDF3B5).Contains("exhausted"), "$BDF3B5 is the primary success target, not exhaustion.");
        True(!ScannerSemantics.Describe(0xBDF3D6).Contains("exhausted"), "$BDF3D6 is the secondary success target, not exhaustion.");
    }

    private static void TestScannerOpcodes(string path)
    {
        var rom = File.ReadAllBytes(path);
        Equal(0x400000, rom.Length);
        Equal("FA8CACF5BBFC39EE6BBAA557ADF89133D60D42F6CF9E1DB30D5A36A469F74D15", Convert.ToHexString(SHA256.HashData(rom)));

        // Primary: search $02-$1C; fall through to STZ/SEC/RTS on exhaustion;
        // BEQ targets $BDF3B5, which reserves X and returns carry clear.
        Bytes(rom, 0xBDF3A2, 0xA2, 0x02, 0x00, 0xBD, 0x45, 0x0D, 0xF0, 0x0B, 0xE8, 0xE8, 0xE0, 0x1E, 0x00, 0xD0, 0xF4);
        Bytes(rom, 0xBDF3B1, 0x64, 0x86, 0x38, 0x60);
        Bytes(rom, 0xBDF3B5, 0x86, 0x86, 0xA9, 0x00, 0x80, 0x9D, 0xFD, 0x15, 0x18, 0x60);

        // Secondary: identical control flow over $1E-$32.
        Bytes(rom, 0xBDF3C3, 0xA2, 0x1E, 0x00, 0xBD, 0x45, 0x0D, 0xF0, 0x0B, 0xE8, 0xE8, 0xE0, 0x34, 0x00, 0xD0, 0xF4);
        Bytes(rom, 0xBDF3D2, 0x64, 0x86, 0x38, 0x60);
        Bytes(rom, 0xBDF3D6, 0x86, 0x86, 0xA9, 0x00, 0x80, 0x9D, 0xFD, 0x15, 0x18, 0x60);
    }

    private static void Bytes(byte[] rom, int snesAddress, params byte[] expected)
    {
        var offset = snesAddress & 0x3FFFFF; // headerless HiROM
        for (var i = 0; i < expected.Length; i++)
            if (rom[offset + i] != expected[i])
                throw new InvalidOperationException("ROM opcode mismatch at $" + (snesAddress + i).ToString("X6") + ": expected $" + expected[i].ToString("X2") + ", got $" + rom[offset + i].ToString("X2") + ".");
    }

    private static void TestActorDiff()
    {
        var beforeRam = new byte[0x20000];
        var afterRam = (byte[])beforeRam.Clone();
        SetGameplay(beforeRam);
        SetGameplay(afterRam);
        Put16(afterRam, DkcRam.ActorId + 2, 0x0030);
        Put16(afterRam, DkcRam.ActorX + 2, 0x1234);
        Put16(afterRam, DkcRam.ActorY + 2, 0x5678);
        Put16(afterRam, DkcRam.ActorSourceRecord + 2, 7);
        var events = LifecycleAnalyzer.Diff(DkcFrameSnapshot.FromRam(beforeRam, 10), DkcFrameSnapshot.FromRam(afterRam, 11), _ => "$BDF915 allocation");
        Equal(1, events.Count);
        Equal("actor_allocated", events[0]["type"]);
        Equal("$BDF915 allocation", events[0]["lastWriter"]);
    }

    private static void TestActorPoolIndexing()
    {
        var ram = new byte[0x20000];
        SetGameplay(ram);
        Put16(ram, DkcRam.ActorId, 1);
        Put16(ram, DkcRam.ActorId + 2, 2);
        var frame = DkcFrameSnapshot.FromRam(ram, 1);
        Equal(25, frame.Actors.Length);
        Equal(2, frame.Actors[0].Index);
        Equal(0x32, frame.Actors[frame.Actors.Length - 1].Index);
        True(frame.Actors.All(actor => actor.Index >= 2 && (actor.Index & 1) == 0), "Expected only raw bank-BD actor indexes.");
        Equal(2, ((Dictionary<string, object>)frame.Actors[0].ToData())["actorIndex"]);
        Equal(0, ((Dictionary<string, object>)frame.Actors[0].ToData())["poolOrdinal"]);
    }

    private static void TestWorldMapGate()
    {
        var ram = new byte[0x20000];
        Put16(ram, DkcRam.EntranceId, 0x00E6);
        for (var index = 2; index <= 0x32; index += 2) Put16(ram, DkcRam.ActorId + index, 2);
        var frame = DkcFrameSnapshot.FromRam(ram, 1);
        True(!frame.ObjectTracingActive, "World-map state must not be interpreted as gameplay actors.");
        var data = frame.ToData(Array.Empty<ObjectRecord>(), Array.Empty<string>());
        Equal(0, data["activeActorCount"]);
        Equal(0, ((object[])data["actors"]).Length);
        Equal(0, LifecycleAnalyzer.FindAnomalies(frame, Array.Empty<ObjectRecord>()).Count);
    }

    private static void TestInvalidEntranceDoesNotRead()
    {
        var memory = new FakeMemory();
        string error;
        var records = ObjectTableDecoder.Decode(memory, 0x00E6, out error);
        Equal(0, records.Count);
        Equal(0, memory.ReadCount);
        True(error != null && error.Contains("outside"), "Expected a bounded entrance-table error.");
    }

    private static void TestGroupDecode()
    {
        var memory = new FakeMemory();
        memory.Word(0xBD8006, 0x9000);
        memory.Record(0xBD9000, 5, 0x0100, 0x0200, 0x9100);
        memory.Record(0xBD9008, 0, 0, 0, 0);
        memory.Record(0xBD9108, 1, 0x0110, 0x0210, 0xBEEF);
        memory.Record(0xBD9110, 0, 0, 0, 0);
        string error;
        var records = ObjectTableDecoder.Decode(memory, 3, out error);
        Equal(null, error);
        Equal(1, records.Count);
        Equal(1, records[0].Children.Count);
        Equal(0x21, records[0].Children[0].Index);
        Equal(0, records[0].Children[0].ParentIndex);
    }

    private static void TestMissingGroupChild()
    {
        var ram = new byte[0x20000];
        SetGameplay(ram);
        ram[DkcRam.Bookkeeping + 4] = 0xFF;
        var group = new ObjectRecord { Index = 4, Type = 5 };
        group.Children.Add(new ObjectRecord { Index = 5, Type = 1, ParentIndex = 4 });
        var frame = DkcFrameSnapshot.FromRam(ram, 1);
        Equal(0, LifecycleAnalyzer.FindAnomalies(frame, new[] { group }).Count);
        var observations = LifecycleAnalyzer.FindObservations(frame, new[] { group });
        True(observations.Any(i => i.Contains("missing child")), "Expected a missing-child observation.");
    }

    private static void TestStaleBookmarkClassification()
    {
        var ram = new byte[0x20000];
        SetGameplay(ram);
        ram[DkcRam.Bookkeeping + 9] = 0x0C;
        var frame = DkcFrameSnapshot.FromRam(ram, 1);
        Equal(0, LifecycleAnalyzer.FindAnomalies(frame, Array.Empty<ObjectRecord>()).Count);
        True(LifecycleAnalyzer.FindObservations(frame, Array.Empty<ObjectRecord>()).Any(i => i.Contains("temporarily")), "Expected a transient stale-bookmark observation.");
    }

    private static void TestInvalidActorIndex()
    {
        var ram = new byte[0x20000];
        SetGameplay(ram);
        ram[DkcRam.Bookkeeping + 9] = 0x0D;
        var issues = LifecycleAnalyzer.FindAnomalies(DkcFrameSnapshot.FromRam(ram, 1), Array.Empty<ObjectRecord>());
        True(issues.Any(i => i.Contains("structurally invalid")), "Expected an impossible odd actor-index anomaly.");
    }

    private static void TestValidOwnership()
    {
        var ram = new byte[0x20000];
        SetGameplay(ram);
        Put16(ram, DkcRam.ActorId + 2, 0x0030);
        Put16(ram, DkcRam.ActorSourceRecord + 2, 7);
        ram[DkcRam.Bookkeeping + 7] = 2;
        var record = new ObjectRecord { Index = 7, Type = 1 };
        var issues = LifecycleAnalyzer.FindAnomalies(DkcFrameSnapshot.FromRam(ram, 1), new[] { record });
        Equal(0, issues.Count);
    }

    private static void Put16(byte[] data, int offset, int value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
    }

    private static void SetGameplay(byte[] data)
    {
        Put16(data, DkcRam.EntranceId, 0x22);
        Put16(data, DkcRam.CameraLowerBound, 0x38);
        Put16(data, DkcRam.CameraUpperBound, 0x6C8);
    }

    private static void Equal(object expected, object actual)
    {
        if (!object.Equals(expected, actual)) throw new InvalidOperationException("Expected " + expected + ", got " + actual + ".");
    }

    private static void True(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Contains(string expected, string actual)
    {
        if (actual == null || !actual.Contains(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Expected '" + actual + "' to contain '" + expected + "'.");
    }

    private sealed class FakeMemory : ISnesMemoryReader
    {
        private readonly Dictionary<uint, byte> _bytes = new Dictionary<uint, byte>();
        public int ReadCount { get; private set; }
        public byte ReadByte(uint address) { ReadCount++; byte value; return _bytes.TryGetValue(address & 0xFFFFFF, out value) ? value : (byte)0; }
        public void Word(uint address, int value) { _bytes[address] = (byte)value; _bytes[address + 1] = (byte)(value >> 8); }
        public void Record(uint address, int type, int x, int y, int data)
        {
            Word(address, type); Word(address + 2, x); Word(address + 4, y); Word(address + 6, data);
        }
    }
}
