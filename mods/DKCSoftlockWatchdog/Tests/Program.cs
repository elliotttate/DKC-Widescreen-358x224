using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using DKCSoftlockWatchdog;

internal static class Program
{
    private static int _passed;

    private static void Main()
    {
        Run("gameplay gate rejects reused map WRAM", TestGameplayGate);
        Run("invalid entrance is bounded before ROM reads", TestInvalidEntrance);
        Run("sprite scripts resolve direct and inherited actor IDs", TestSpriteResolver);
        Run("object decoder classifies camera, group, and type-9 records", TestObjectDecode);
        Run("eligible unbooked critical record requires consecutive frames", TestEligibleUnbooked);
        Run("out-of-window critical record does not trigger", TestOutOfWindow);
        Run("booked actor disappearance requires persistent broken ownership", TestBookedActorMissing);
        Run("active type-5 parent detects persistently missing child", TestMissingGroupChild);
        Run("full primary pool is true frame-state allocator exhaustion", TestAllocatorExhaustion);
        Run("recovery resets a persistence run", TestRecoveryResetsPersistence);
        Run("type-9 authored range contradictions persist before trigger", TestType9Contradiction);
        Run("type-9 pending pointer contradictions are detected", TestType9PendingContradiction);
        Run("exact allocator witness is definitive and PC-specific", TestExactWitness);
        Run("allocator success and failure PCs are not reversed", TestOpcodePcSemantics);
        var cleanRom = Environment.GetEnvironmentVariable("DKC_CLEAN_ROM");
        if (!string.IsNullOrWhiteSpace(cleanRom)) Run("clean USA v1.0 allocator and type-9 opcodes match", () => TestCleanRom(cleanRom));
        else Console.WriteLine("SKIP: clean-ROM opcode test (set DKC_CLEAN_ROM or pass -CleanRomPath to build.ps1)");
        Console.WriteLine("All " + _passed + " DKC softlock-watchdog tests passed.");
    }

    private static void Run(string name, Action test)
    {
        test();
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static DetectionOptions Options()
    {
        return new DetectionOptions
        {
            EligibleFrames = 2, OwnershipFrames = 2, GroupFrames = 2, ContradictionFrames = 2,
            TriggerCooldownFrames = 10, EligibleWithoutAllocation = true, BookedActorMissing = true,
            MissingGroupChildren = true, Type9Contradictions = true, ScannerContradictions = true,
            AllocatorExhaustion = true, ExactAllocatorWitness = true
        };
    }

    private static void TestGameplayGate()
    {
        var ram = new byte[DkcRam.WramSize];
        Put16(ram, DkcRam.EntranceId, DkcRam.EntranceCount);
        var frame = FrameState.FromRam(ram, 1);
        True(!frame.GameplayActive, "Map/menu WRAM must not be interpreted as gameplay.");
        Put16(ram, DkcRam.EntranceId, 1);
        Put16(ram, DkcRam.CameraLowerBound, 0x100);
        Put16(ram, DkcRam.CameraUpperBound, 0x80);
        frame = FrameState.FromRam(ram, 2);
        True(!frame.GameplayActive, "Inverted camera bounds must be rejected.");
    }

    private static void TestInvalidEntrance()
    {
        var memory = new FakeMemory();
        var table = ObjectTableDecoder.Decode(memory, DkcRam.EntranceCount);
        Equal(0, memory.ReadCount);
        True(table.Error != null && table.Error.Contains("outside"), "Expected an entrance bound error.");
    }

    private static void TestSpriteResolver()
    {
        var memory = new FakeMemory();
        memory.Word(0xB59000, 0x0D45);
        memory.Word(0xB59002, 0x005D);
        memory.Word(0xB59004, 0x8000);
        Equal((ushort)0x005D, SpriteScriptResolver.ResolveActorId(memory, 0x9000));

        memory.Word(0xB59100, 0x8200);
        memory.Word(0xB59102, 0x9000);
        memory.Word(0xB59104, 0x8000);
        Equal((ushort)0x005D, SpriteScriptResolver.ResolveActorId(memory, 0x9100));
    }

    private static void TestObjectDecode()
    {
        var memory = new FakeMemory();
        memory.Word(0xBD8002, 0x9000);
        memory.Record(0xBD9000, 2, 0x120, 0x200, 0x9100);
        memory.Record(0xBD9008, 5, 0x140, 0x200, 0x9200);
        memory.Record(0xBD9010, 9, 0, 0, 0x9300);
        memory.Record(0xBD9018, 0, 0, 0, 0);
        memory.Word(0xB59100, 0x0D45);
        memory.Word(0xB59102, 0x005D);
        memory.Word(0xB59104, 0x8000);
        memory.Record(0xBD9208, 1, 0x150, 0x210, 0x9400);
        memory.Record(0xBD9210, 0, 0, 0, 0);
        memory.Record(0xBD9300, 0x0201, 0x180, 0x220, 0x0403);
        memory.Record(0xBD9308, 0, 0, 0, 0);
        var table = ObjectTableDecoder.Decode(memory, 1);
        Equal(null, table.Error);
        Equal(3, table.Records.Count);
        Equal("camera-object", table.Records[0].Category);
        Equal("type5-group-parent", table.Records[1].Category);
        Equal(1, table.Records[1].Children.Count);
        Equal("type9-section-controller", table.Records[2].Category);
        Equal(1, table.SectionRanges.Count);
        True(table.SectionRanges[0].Matches(1, 2), "Expected the authored forward range.");
        True(table.SectionRanges[0].Matches(3, 4), "Expected the authored reverse range.");
    }

    private static void TestEligibleUnbooked()
    {
        var detector = new WatchdogDetector(Options());
        var table = CriticalTable(2, 0x100);
        Equal(0, detector.Evaluate(GameplayFrame(1), table, null).Count);
        Equal(0, detector.Evaluate(GameplayFrame(2), table, null).Count);
        var result = detector.Evaluate(GameplayFrame(3), table, null);
        True(result.Any(item => item.Condition == "eligible_without_allocation"), "Expected a persistent eligible/unbooked trigger.");
    }

    private static void TestOutOfWindow()
    {
        var detector = new WatchdogDetector(Options());
        var table = CriticalTable(2, 0x500);
        detector.Evaluate(GameplayFrame(1), table, null);
        detector.Evaluate(GameplayFrame(2), table, null);
        Equal(0, detector.Evaluate(GameplayFrame(3), table, null).Count);
    }

    private static void TestBookedActorMissing()
    {
        var detector = new WatchdogDetector(Options());
        var table = CriticalTable(4, 0x100);
        var owned = GameplayRam();
        Put16(owned, DkcRam.ActorId + 2, 0x005D);
        Put16(owned, DkcRam.ActorSourceRecord + 2, 4);
        owned[DkcRam.Bookkeeping + 4] = 2;
        detector.Evaluate(FrameState.FromRam(owned, 1), table, null);

        var broken = (byte[])owned.Clone();
        Put16(broken, DkcRam.ActorId + 2, 0);
        Equal(0, detector.Evaluate(FrameState.FromRam(broken, 2), table, null).Count);
        var result = detector.Evaluate(FrameState.FromRam(broken, 3), table, null);
        True(result.Any(item => item.Condition == "booked_actor_missing"), "Expected broken ownership to persist before triggering.");

        detector = new WatchdogDetector(Options());
        detector.Evaluate(FrameState.FromRam(owned, 1), table, null);
        broken[DkcRam.Bookkeeping + 4] = 0;
        detector.Evaluate(FrameState.FromRam(broken, 2), table, null);
        result = detector.Evaluate(FrameState.FromRam(broken, 3), table, null);
        True(result.Any(item => item.Condition == "booked_actor_missing"), "A camera/exit/controller disappearance must remain watchable after its bookmark is cleared.");
    }

    private static void TestMissingGroupChild()
    {
        var detector = new WatchdogDetector(Options());
        var group = new ObjectRecord { Index = 4, Type = 5, Category = "type5-group-parent" };
        group.Children.Add(new ObjectRecord { Index = 10, ParentIndex = 4, Type = 1, X = 0x110, Y = 0x200 });
        group.Children.Add(new ObjectRecord { Index = 11, ParentIndex = 4, Type = 1, X = 0x130, Y = 0x200 });
        var table = new ObjectTable { Records = new List<ObjectRecord> { group } };
        var ram = GameplayRam();
        ram[DkcRam.Bookkeeping + 4] = 0xFF;
        detector.Evaluate(FrameState.FromRam(ram, 1), table, null);
        detector.Evaluate(FrameState.FromRam(ram, 2), table, null);
        var result = detector.Evaluate(FrameState.FromRam(ram, 3), table, null);
        True(result.Any(item => item.Condition == "type5_child_missing"), "Expected active group child loss.");
    }

    private static void TestAllocatorExhaustion()
    {
        var detector = new WatchdogDetector(Options());
        var table = CriticalTable(2, 0x100);
        var ram = GameplayRam();
        for (var index = DkcRam.PrimaryFirst; index <= DkcRam.PrimaryLast; index += 2)
            Put16(ram, DkcRam.ActorId + index, 1);
        detector.Evaluate(FrameState.FromRam(ram, 1), table, null);
        detector.Evaluate(FrameState.FromRam(ram, 2), table, null);
        var result = detector.Evaluate(FrameState.FromRam(ram, 3), table, null);
        var detection = result.FirstOrDefault(item => item.Condition == "allocator_exhaustion");
        True(detection != null && detection.Definitive, "Expected true all-slots-occupied exhaustion.");
    }

    private static void TestRecoveryResetsPersistence()
    {
        var detector = new WatchdogDetector(Options());
        var table = CriticalTable(2, 0x100);
        detector.Evaluate(GameplayFrame(1), table, null);
        Equal(0, detector.Evaluate(GameplayFrame(2), table, null).Count);
        var recovered = GameplayRam();
        Put16(recovered, DkcRam.ActorId + 2, 0x005D);
        Put16(recovered, DkcRam.ActorSourceRecord + 2, 2);
        recovered[DkcRam.Bookkeeping + 2] = 2;
        Equal(0, detector.Evaluate(FrameState.FromRam(recovered, 3), table, null).Count);
        Equal(0, detector.Evaluate(GameplayFrame(4), table, null).Count);
    }

    private static void TestType9Contradiction()
    {
        var detector = new WatchdogDetector(Options());
        var table = Type9Table();
        var ram = GameplayRam();
        Put16(ram, DkcRam.SectionState, 0x0100);
        ram[DkcRam.SectionCurrent] = 6;
        ram[DkcRam.SectionLimit] = 7;
        ram[DkcRam.ScannerCursorPrimary] = 6;
        detector.Evaluate(FrameState.FromRam(ram, 1), table, null);
        detector.Evaluate(FrameState.FromRam(ram, 2), table, null);
        var result = detector.Evaluate(FrameState.FromRam(ram, 3), table, null);
        True(result.Any(item => item.Condition == "type9_range_contradiction"), "Expected a non-authored active range trigger.");
    }

    private static void TestType9PendingContradiction()
    {
        var detector = new WatchdogDetector(Options());
        var table = Type9Table();
        var ram = GameplayRam();
        Put16(ram, DkcRam.SectionState, 0x0100);
        ram[DkcRam.SectionCurrent] = 1;
        ram[DkcRam.SectionLimit] = 2;
        ram[DkcRam.ScannerCursorPrimary] = 1;
        Put16(ram, DkcRam.SectionPointer, 0xA123);
        detector.Evaluate(FrameState.FromRam(ram, 1), table, null);
        detector.Evaluate(FrameState.FromRam(ram, 2), table, null);
        var result = detector.Evaluate(FrameState.FromRam(ram, 3), table, null);
        True(result.Any(item => item.Condition == "type9_pending_contradiction"), "Expected a pending descriptor-pointer trigger.");
    }

    private static void TestExactWitness()
    {
        var detector = new WatchdogDetector(Options());
        var table = CriticalTable(2, 0x100);
        detector.Evaluate(GameplayFrame(1), table, null);
        var witness = new AllocatorWitness { Frame = 2, RecordIndex = 2, Secondary = false, Pc = 0xBDF3B1, OccupiedIndices = Enumerable.Range(1, 14).Select(value => value * 2).ToArray() };
        var result = detector.Evaluate(GameplayFrame(2), table, new[] { witness });
        var detection = result.FirstOrDefault(item => item.Condition == "exact_allocator_exhaustion_witness");
        True(detection != null && detection.Definitive, "Expected a definitive exact-PC witness.");
    }

    private static void TestOpcodePcSemantics()
    {
        bool secondary;
        True(OpcodeSignatures.IsExhaustionPc(0xBDF3B1, out secondary) && !secondary, "$BDF3B1 must be primary exhaustion.");
        True(OpcodeSignatures.IsExhaustionPc(0xBDF3D2, out secondary) && secondary, "$BDF3D2 must be secondary exhaustion.");
        True(!OpcodeSignatures.IsExhaustionPc(0xBDF3B5, out secondary), "$BDF3B5 is primary success, not exhaustion.");
        True(!OpcodeSignatures.IsExhaustionPc(0xBDF3D6, out secondary), "$BDF3D6 is secondary success, not exhaustion.");
    }

    private static void TestCleanRom(string path)
    {
        var rom = File.ReadAllBytes(path);
        Equal(0x400000, rom.Length);
        Equal("FA8CACF5BBFC39EE6BBAA557ADF89133D60D42F6CF9E1DB30D5A36A469F74D15", Convert.ToHexString(SHA256.HashData(rom)));
        var validation = OpcodeSignatures.Validate(new RomReader(rom));
        True(validation.Valid, "Clean-ROM opcode mismatch: " + string.Join("; ", validation.Mismatches));
    }

    private static ObjectTable CriticalTable(int index, int x)
    {
        return new ObjectTable
        {
            Records = new List<ObjectRecord>
            {
                new ObjectRecord { Index = index, Type = 2, X = (ushort)x, Y = 0x200, Data = 0x9000,
                    ExpectedActorId = 0x005D, Category = "camera-object" }
            }
        };
    }

    private static ObjectTable Type9Table()
    {
        return new ObjectTable
        {
            Records = new List<ObjectRecord>
            {
                new ObjectRecord { Index = 0, Type = 9, Category = "type9-section-controller" },
                new ObjectRecord { Index = 1, Type = 1 }, new ObjectRecord { Index = 2, Type = 1 },
                new ObjectRecord { Index = 3, Type = 1 }, new ObjectRecord { Index = 4, Type = 1 }
            },
            SectionRanges = new List<SectionRange>
            {
                new SectionRange { Ordinal = 0, Address = 0xBDA000, ForwardPacked = 0x0201, ReversePacked = 0x0403 }
            }
        };
    }

    private static FrameState GameplayFrame(int frame) { return FrameState.FromRam(GameplayRam(), frame); }

    private static byte[] GameplayRam()
    {
        var ram = new byte[DkcRam.WramSize];
        Put16(ram, DkcRam.LevelId, 0x25);
        Put16(ram, DkcRam.EntranceId, 0x3E);
        Put16(ram, DkcRam.CameraLowerBound, 0x38);
        Put16(ram, DkcRam.CameraUpperBound, 0x6C8);
        Put16(ram, DkcRam.ScannerWindowLeft, 0x40);
        Put16(ram, DkcRam.ScannerWindowRight, 0x1F0);
        Put16(ram, DkcRam.LayerX, 0x98);
        return ram;
    }

    private static void Put16(byte[] data, int offset, int value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
    }

    private static void Equal(object expected, object actual)
    {
        if (!object.Equals(expected, actual)) throw new InvalidOperationException("Expected " + expected + ", got " + actual + ".");
    }

    private static void True(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class FakeMemory : ISnesMemoryReader
    {
        private readonly Dictionary<uint, byte> _bytes = new Dictionary<uint, byte>();
        public int ReadCount { get; private set; }
        public byte ReadByte(uint address) { ReadCount++; byte value; return _bytes.TryGetValue(address & 0xFFFFFF, out value) ? value : (byte)0; }
        public void Word(uint address, int value) { _bytes[address & 0xFFFFFF] = (byte)value; _bytes[(address + 1) & 0xFFFFFF] = (byte)(value >> 8); }
        public void Record(uint address, int type, int x, int y, int data)
        {
            Word(address, type); Word(address + 2, x); Word(address + 4, y); Word(address + 6, data);
        }
    }

    private sealed class RomReader : ISnesMemoryReader
    {
        private readonly byte[] _rom;
        public RomReader(byte[] rom) { _rom = rom; }
        public byte ReadByte(uint address) { return _rom[address & 0x3FFFFF]; }
    }
}
