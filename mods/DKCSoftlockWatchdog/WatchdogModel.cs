using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DKCSoftlockWatchdog
{
    internal static class DkcRam
    {
        public const int GameState = 0x002E;
        public const int LevelId = 0x0030;
        public const int EntranceId = 0x003E;
        public const int VerticalReference = 0x004A;
        public const int CurrentActorIndex = 0x0082;
        public const int ScannerCursorPrimary = 0x00A0;
        public const int ScannerCursorSecondary = 0x00A2;
        public const int ScannerRecordIndex = 0x00A4;
        public const int ScannerWindowLeft = 0x00EF;
        public const int ScannerWindowRight = 0x00F1;
        public const int LayerX = 0x088B;
        public const int LayerY = 0x0895;
        public const int OperatingMode = 0x0A75;
        public const int ActorPose = 0x0AE5;
        public const int ActorX = 0x0B19;
        public const int ActorY = 0x0BC1;
        public const int ActorGraphics = 0x0C69;
        public const int ActorCurrentPose = 0x0D11;
        public const int ActorId = 0x0D45;
        public const int ActorXSpeed = 0x0E89;
        public const int ActorYSpeed = 0x0EF1;
        public const int ActorState = 0x1029;
        public const int ActorAnimation = 0x10D1;
        public const int ActorSourceRecord = 0x15FD;
        public const int Bookkeeping = 0x192B;
        public const int BookkeepingLength = 0x100;
        public const int CameraY = 0x1A4C;
        public const int CameraX = 0x1A62;
        public const int CameraLowerBound = 0x1B23;
        public const int CameraUpperBound = 0x1B25;
        public const int SectionState = 0x1E03;
        public const int SectionPointer = 0x1E05;
        public const int SectionCurrent = 0x1E07;
        public const int SectionPending = 0x1E09;
        public const int SectionLimit = 0x1E0B;
        public const int SectionPendingLimit = 0x1E0D;
        public const int WramSize = 0x20000;
        public const int EntranceCount = 0xE6;
        public const int FirstActorIndex = 0x02;
        public const int LastActorIndex = 0x32;
        public const int PrimaryFirst = 0x02;
        public const int PrimaryLast = 0x1C;
        public const int SecondaryFirst = 0x1E;
        public const int SecondaryLast = 0x32;

        public static ushort U16(byte[] data, int offset)
        {
            return (ushort)(data[offset] | (data[offset + 1] << 8));
        }

        public static short S16(byte[] data, int offset)
        {
            return unchecked((short)U16(data, offset));
        }
    }

    internal static class Format
    {
        public static string Hex(uint value, int digits)
        {
            return "$" + value.ToString("X" + digits, CultureInfo.InvariantCulture);
        }
    }

    internal sealed class ActorState
    {
        public int Index;
        public ushort Id;
        public ushort X;
        public ushort Y;
        public short XSpeed;
        public short YSpeed;
        public ushort State;
        public ushort Animation;
        public ushort Pose;
        public ushort Graphics;
        public short SourceRecord;

        public bool Active { get { return Id != 0; } }

        public IDictionary<string, object> ToData()
        {
            return new Dictionary<string, object>
            {
                { "actorIndex", Index }, { "actorIndexHex", Format.Hex((uint)Index, 2) },
                { "pool", Index <= DkcRam.PrimaryLast ? "primary" : "secondary" },
                { "id", Format.Hex(Id, 4) }, { "name", DkcNames.Actor(Id) },
                { "x", Format.Hex(X, 4) }, { "y", Format.Hex(Y, 4) },
                { "xSpeed", XSpeed }, { "ySpeed", YSpeed }, { "state", Format.Hex(State, 4) },
                { "animation", Format.Hex(Animation, 4) }, { "pose", Format.Hex(Pose, 4) },
                { "graphics", Format.Hex(Graphics, 4) }, { "sourceRecord", SourceRecord },
                { "sourceRecordHex", Format.Hex(unchecked((ushort)SourceRecord), 4) }
            };
        }
    }

    internal sealed class FrameState
    {
        public int Frame;
        public ushort LevelId;
        public ushort EntranceId;
        public ushort GameState;
        public ushort OperatingMode;
        public ushort VerticalReference;
        public ushort LayerX;
        public ushort LayerY;
        public ushort CameraX;
        public ushort CameraY;
        public ushort CameraLower;
        public ushort CameraUpper;
        public ushort ScannerLeft;
        public ushort ScannerRight;
        public byte ScannerPrimary;
        public byte ScannerSecondary;
        public byte ScannerRecord;
        public ushort CurrentActorIndex;
        public ushort SectionState;
        public ushort SectionPointer;
        public byte SectionCurrent;
        public byte SectionPending;
        public byte SectionLimit;
        public byte SectionPendingLimit;
        public ActorState[] Actors;
        public byte[] Bookkeeping;

        public bool GameplayActive
        {
            get
            {
                string ignored;
                return IsGameplayActive(this, out ignored);
            }
        }

        public string GameplayReason
        {
            get
            {
                string reason;
                IsGameplayActive(this, out reason);
                return reason;
            }
        }

        public static FrameState FromRam(byte[] ram, int frame)
        {
            if (ram == null || ram.Length != DkcRam.WramSize)
                throw new ArgumentException("A full 128 KiB WRAM snapshot is required.", "ram");
            var actors = new List<ActorState>();
            for (var index = DkcRam.FirstActorIndex; index <= DkcRam.LastActorIndex; index += 2)
            {
                actors.Add(new ActorState
                {
                    Index = index,
                    Id = DkcRam.U16(ram, DkcRam.ActorId + index),
                    X = DkcRam.U16(ram, DkcRam.ActorX + index),
                    Y = DkcRam.U16(ram, DkcRam.ActorY + index),
                    XSpeed = DkcRam.S16(ram, DkcRam.ActorXSpeed + index),
                    YSpeed = DkcRam.S16(ram, DkcRam.ActorYSpeed + index),
                    State = DkcRam.U16(ram, DkcRam.ActorState + index),
                    Animation = DkcRam.U16(ram, DkcRam.ActorAnimation + index),
                    Pose = DkcRam.U16(ram, DkcRam.ActorCurrentPose + index),
                    Graphics = DkcRam.U16(ram, DkcRam.ActorGraphics + index),
                    SourceRecord = DkcRam.S16(ram, DkcRam.ActorSourceRecord + index)
                });
            }
            var bookkeeping = new byte[DkcRam.BookkeepingLength];
            Buffer.BlockCopy(ram, DkcRam.Bookkeeping, bookkeeping, 0, bookkeeping.Length);
            return new FrameState
            {
                Frame = frame,
                LevelId = DkcRam.U16(ram, DkcRam.LevelId),
                EntranceId = DkcRam.U16(ram, DkcRam.EntranceId),
                GameState = DkcRam.U16(ram, DkcRam.GameState),
                OperatingMode = DkcRam.U16(ram, DkcRam.OperatingMode),
                VerticalReference = DkcRam.U16(ram, DkcRam.VerticalReference),
                LayerX = DkcRam.U16(ram, DkcRam.LayerX),
                LayerY = DkcRam.U16(ram, DkcRam.LayerY),
                CameraX = DkcRam.U16(ram, DkcRam.CameraX),
                CameraY = DkcRam.U16(ram, DkcRam.CameraY),
                CameraLower = DkcRam.U16(ram, DkcRam.CameraLowerBound),
                CameraUpper = DkcRam.U16(ram, DkcRam.CameraUpperBound),
                ScannerLeft = DkcRam.U16(ram, DkcRam.ScannerWindowLeft),
                ScannerRight = DkcRam.U16(ram, DkcRam.ScannerWindowRight),
                ScannerPrimary = ram[DkcRam.ScannerCursorPrimary],
                ScannerSecondary = ram[DkcRam.ScannerCursorSecondary],
                ScannerRecord = ram[DkcRam.ScannerRecordIndex],
                CurrentActorIndex = DkcRam.U16(ram, DkcRam.CurrentActorIndex),
                SectionState = DkcRam.U16(ram, DkcRam.SectionState),
                SectionPointer = DkcRam.U16(ram, DkcRam.SectionPointer),
                SectionCurrent = ram[DkcRam.SectionCurrent],
                SectionPending = ram[DkcRam.SectionPending],
                SectionLimit = ram[DkcRam.SectionLimit],
                SectionPendingLimit = ram[DkcRam.SectionPendingLimit],
                Actors = actors.ToArray(),
                Bookkeeping = bookkeeping
            };
        }

        public static bool IsGameplayActive(FrameState frame, out string reason)
        {
            if (frame == null) { reason = "no frame"; return false; }
            if (frame.EntranceId >= DkcRam.EntranceCount)
            {
                reason = "entrance is outside the exact $E6-entry bank-BD gameplay table";
                return false;
            }
            if (frame.CameraLower == 0 && frame.CameraUpper == 0)
            {
                reason = "camera bounds are zero (map/menu/transition WRAM layout)";
                return false;
            }
            if (frame.CameraUpper < frame.CameraLower)
            {
                reason = "camera bounds are inverted";
                return false;
            }
            reason = "valid gameplay entrance and ordered nonzero camera bounds";
            return true;
        }

        public ActorState ActorAt(int index)
        {
            if ((index & 1) != 0 || index < DkcRam.FirstActorIndex || index > DkcRam.LastActorIndex) return null;
            return Actors[(index - DkcRam.FirstActorIndex) / 2];
        }

        public ActorState ActorForRecord(int record)
        {
            return Actors.FirstOrDefault(actor => actor.Active && actor.SourceRecord != unchecked((short)0x8000)
                && Math.Abs((int)actor.SourceRecord) == record);
        }

        public bool HasOwnedActor(int record)
        {
            var actor = ActorForRecord(record);
            if (actor == null || record < 0 || record >= Bookkeeping.Length) return false;
            return Bookkeeping[record] == actor.Index;
        }

        public int[] FreeIndices(bool secondary)
        {
            var first = secondary ? DkcRam.SecondaryFirst : DkcRam.PrimaryFirst;
            var last = secondary ? DkcRam.SecondaryLast : DkcRam.PrimaryLast;
            return Actors.Where(actor => actor.Index >= first && actor.Index <= last && !actor.Active).Select(actor => actor.Index).ToArray();
        }

        public IDictionary<string, object> ContextData()
        {
            return new Dictionary<string, object>
            {
                { "frame", Frame }, { "levelId", Format.Hex(LevelId, 4) }, { "level", DkcNames.Level(LevelId) },
                { "entranceId", Format.Hex(EntranceId, 4) }, { "gameState", Format.Hex(GameState, 4) },
                { "operatingMode", Format.Hex(OperatingMode, 4) }, { "gameplayActive", GameplayActive },
                { "gameplayReason", GameplayReason },
                { "layer", new Dictionary<string, object> { { "x", Format.Hex(LayerX, 4) }, { "y", Format.Hex(LayerY, 4) } } },
                { "camera", new Dictionary<string, object> { { "x", Format.Hex(CameraX, 4) }, { "y", Format.Hex(CameraY, 4) },
                    { "lower", Format.Hex(CameraLower, 4) }, { "upper", Format.Hex(CameraUpper, 4) } } },
                { "scanner", new Dictionary<string, object> { { "left", Format.Hex(ScannerLeft, 4) }, { "right", Format.Hex(ScannerRight, 4) },
                    { "primaryCursor", ScannerPrimary }, { "secondaryCursor", ScannerSecondary }, { "recordIndex", ScannerRecord },
                    { "currentActorIndex", CurrentActorIndex } } },
                { "section", new Dictionary<string, object> { { "state", Format.Hex(SectionState, 4) }, { "pointer", Format.Hex(SectionPointer, 4) },
                    { "currentStart", SectionCurrent }, { "currentEnd", SectionLimit }, { "pendingStart", SectionPending },
                    { "pendingEnd", SectionPendingLimit } } }
            };
        }
    }

    internal interface ISnesMemoryReader
    {
        byte ReadByte(uint address);
    }

    internal sealed class ObjectRecord
    {
        public int Index;
        public uint Address;
        public ushort Type;
        public ushort X;
        public ushort Y;
        public ushort Data;
        public int ParentIndex = -1;
        public ushort? ExpectedActorId;
        public string Category;
        public readonly List<ObjectRecord> Children = new List<ObjectRecord>();

        public bool LogicCritical { get { return !string.IsNullOrEmpty(Category); } }

        public IDictionary<string, object> ToData(FrameState frame)
        {
            var bookmark = frame != null && Index >= 0 && Index < frame.Bookkeeping.Length ? frame.Bookkeeping[Index] : (byte)0;
            var actor = frame == null ? null : frame.ActorForRecord(Index);
            return new Dictionary<string, object>
            {
                { "index", Index }, { "indexHex", Format.Hex((uint)Index, 2) }, { "address", Format.Hex(Address, 6) },
                { "type", Format.Hex(Type, 2) }, { "typeName", DkcNames.ObjectType(Type) },
                { "x", Format.Hex(X, 4) }, { "y", Format.Hex(Y, 4) }, { "data", Format.Hex(Data, 4) },
                { "parentIndex", ParentIndex < 0 ? (object)null : ParentIndex }, { "category", Category },
                { "expectedActorId", ExpectedActorId.HasValue ? Format.Hex(ExpectedActorId.Value, 4) : null },
                { "expectedActorName", ExpectedActorId.HasValue ? DkcNames.Actor(ExpectedActorId.Value) : null },
                { "bookkeeping", Format.Hex(bookmark, 2) }, { "ownedActor", actor == null ? null : actor.ToData() },
                { "children", Children.Select(child => (object)child.ToData(frame)).ToArray() }
            };
        }
    }

    internal sealed class SectionRange
    {
        public int Ordinal;
        public uint Address;
        public ushort ForwardPacked;
        public ushort X;
        public ushort Y;
        public ushort ReversePacked;

        public byte ForwardStart { get { return (byte)ForwardPacked; } }
        public byte ForwardEnd { get { return (byte)(ForwardPacked >> 8); } }
        public byte ReverseStart { get { return (byte)ReversePacked; } }
        public byte ReverseEnd { get { return (byte)(ReversePacked >> 8); } }

        public bool Matches(byte start, byte end)
        {
            return (ForwardStart == start && ForwardEnd == end) || (ReverseStart == start && ReverseEnd == end);
        }

        public IDictionary<string, object> ToData()
        {
            return new Dictionary<string, object>
            {
                { "ordinal", Ordinal }, { "address", Format.Hex(Address, 6) },
                { "forwardStart", ForwardStart }, { "forwardEnd", ForwardEnd },
                { "reverseStart", ReverseStart }, { "reverseEnd", ReverseEnd },
                { "x", Format.Hex(X, 4) }, { "y", Format.Hex(Y, 4) }
            };
        }
    }

    internal sealed class ObjectTable
    {
        public uint BaseAddress;
        public List<ObjectRecord> Records = new List<ObjectRecord>();
        public List<SectionRange> SectionRanges = new List<SectionRange>();
        public string Error;

        public IEnumerable<ObjectRecord> Flatten()
        {
            foreach (var record in Records)
            {
                yield return record;
                foreach (var child in record.Children) yield return child;
            }
        }

        public ObjectRecord Find(int index)
        {
            return Flatten().FirstOrDefault(record => record.Index == index);
        }
    }

    internal static class ObjectTableDecoder
    {
        public static ObjectTable Decode(ISnesMemoryReader memory, ushort entrance)
        {
            var table = new ObjectTable();
            if (memory == null) { table.Error = "SNES memory is unavailable."; return table; }
            if (entrance >= DkcRam.EntranceCount)
            {
                table.Error = "Entrance " + Format.Hex(entrance, 4) + " is outside the exact $E6-entry bank-BD table.";
                return table;
            }
            try
            {
                var pointer = ReadWord(memory, 0xBD8000u + (uint)(entrance * 2));
                if (pointer < 0x8000) { table.Error = "Invalid bank-BD object-list pointer " + Format.Hex(pointer, 4) + "."; return table; }
                table.BaseAddress = 0xBD0000u | pointer;
                for (var ordinal = 0; ordinal < 512; ordinal++)
                {
                    var address = (table.BaseAddress + (uint)(ordinal * 8)) & 0xFFFFFF;
                    var type = ReadWord(memory, address);
                    if (type == 0) break;
                    if (type > 0x10) { table.Error = "Implausible object type " + Format.Hex(type, 4) + " at " + Format.Hex(address, 6) + "."; break; }
                    var record = ReadRecord(memory, table.BaseAddress, address, -1);
                    if (record.Type == 5) DecodeGroup(memory, table.BaseAddress, record);
                    if (record.Type != 5 && record.Type != 9)
                        record.ExpectedActorId = SpriteScriptResolver.ResolveActorId(memory, record.Data);
                    record.Category = CriticalCategory(record);
                    table.Records.Add(record);
                    if (record.Type == 9) DecodeRanges(memory, record, table.SectionRanges);
                }
            }
            catch (Exception ex) { table.Error = ex.Message; }
            return table;
        }

        private static ObjectRecord ReadRecord(ISnesMemoryReader memory, uint baseAddress, uint address, int parentIndex)
        {
            return new ObjectRecord
            {
                Address = address,
                Index = unchecked((ushort)((address & 0xFFFF) - (baseAddress & 0xFFFF))) / 8,
                Type = ReadWord(memory, address), X = ReadWord(memory, address + 2),
                Y = ReadWord(memory, address + 4), Data = ReadWord(memory, address + 6), ParentIndex = parentIndex
            };
        }

        private static void DecodeGroup(ISnesMemoryReader memory, uint baseAddress, ObjectRecord group)
        {
            if (group.Data < 0x8000) return;
            var address = 0xBD0000u | unchecked((ushort)(group.Data + 8));
            for (var ordinal = 0; ordinal < 128; ordinal++, address += 8)
            {
                var type = ReadWord(memory, address);
                if (type == 0 || type > 0x10) break;
                var child = ReadRecord(memory, baseAddress, address, group.Index);
                if (child.Type != 5 && child.Type != 9) child.ExpectedActorId = SpriteScriptResolver.ResolveActorId(memory, child.Data);
                child.Category = CriticalCategory(child);
                group.Children.Add(child);
            }
        }

        private static void DecodeRanges(ISnesMemoryReader memory, ObjectRecord controller, List<SectionRange> result)
        {
            if (controller.Data < 0x8000) return;
            var address = 0xBD0000u | controller.Data;
            for (var ordinal = 0; ordinal < 64; ordinal++, address += 8)
            {
                var packed = ReadWord(memory, address);
                if (packed == 0) break;
                result.Add(new SectionRange
                {
                    Ordinal = ordinal, Address = address, ForwardPacked = packed,
                    X = ReadWord(memory, address + 2), Y = ReadWord(memory, address + 4),
                    ReversePacked = ReadWord(memory, address + 6)
                });
            }
        }

        private static string CriticalCategory(ObjectRecord record)
        {
            if (record.Type == 5) return "type5-group-parent";
            if (record.Type == 9) return "type9-section-controller";
            if (!record.ExpectedActorId.HasValue) return record.Type == 2 ? "type2-unresolved" : null;
            switch (record.ExpectedActorId.Value)
            {
                case 0x5D: return "camera-object";
                case 0x6A:
                case 0x6B: return "exit";
                case 0x23:
                case 0x24:
                case 0x25:
                case 0x26:
                case 0x27:
                case 0x38:
                case 0x4A:
                case 0x4C:
                case 0x63:
                case 0x6D: return "barrel";
                case 0x44:
                case 0x64:
                case 0x6C:
                case 0x70:
                case 0x75:
                case 0x77: return "controller";
                default: return null;
            }
        }

        internal static ushort ReadWord(ISnesMemoryReader memory, uint address)
        {
            return (ushort)(memory.ReadByte(address & 0xFFFFFF) | (memory.ReadByte((address + 1) & 0xFFFFFF) << 8));
        }
    }

    internal static class SpriteScriptResolver
    {
        public static ushort? ResolveActorId(ISnesMemoryReader memory, ushort pointer)
        {
            return ResolveActorId(memory, pointer, new HashSet<ushort>(), 0);
        }

        private static ushort? ResolveActorId(ISnesMemoryReader memory, ushort pointer, HashSet<ushort> visited, int depth)
        {
            if (memory == null || pointer < 0x8000 || depth > 8 || !visited.Add(pointer)) return null;
            var address = 0xB50000u | pointer;
            var parents = new List<ushort>();
            for (var operation = 0; operation < 96; operation++)
            {
                var opcode = ObjectTableDecoder.ReadWord(memory, address);
                if (opcode < 0x8000)
                {
                    var value = ObjectTableDecoder.ReadWord(memory, address + 2);
                    if (opcode == DkcRam.ActorId) return value;
                    address += 4;
                    continue;
                }
                var kind = opcode >> 8;
                if (kind == 0x80) break;
                if (kind == 0x82) parents.Add(ObjectTableDecoder.ReadWord(memory, address + 2));
                var words = OpcodeWords(kind);
                if (words == 0) break;
                address += (uint)(words * 2);
            }
            foreach (var parent in parents)
            {
                var resolved = ResolveActorId(memory, parent, visited, depth + 1);
                if (resolved.HasValue) return resolved;
            }
            return null;
        }

        private static int OpcodeWords(int kind)
        {
            switch (kind)
            {
                case 0x80: return 1;
                case 0x81:
                case 0x82:
                case 0x88:
                case 0x8D:
                case 0x8E:
                case 0x91:
                case 0x95:
                case 0x96:
                case 0x97: return 2;
                case 0x83:
                case 0x84:
                case 0x85:
                case 0x86:
                case 0x87:
                case 0x89:
                case 0x8A:
                case 0x8B:
                case 0x8C:
                case 0x93: return 1;
                case 0x8F:
                case 0x90:
                case 0x92: return 3;
                case 0x94: return 4;
                default: return 0;
            }
        }
    }

    internal static class ObjectWindow
    {
        public static bool IsEligible(FrameState frame, ObjectRecord record)
        {
            if (frame == null || record == null || !frame.GameplayActive) return false;
            if (!InSectionRange(frame, record)) return false;
            if (record.Type == 5) return GroupEligible(frame, record);
            if (record.Type == 9) return false;
            var x = record.X;
            switch (record.Type)
            {
                case 4: return unchecked((short)(x - frame.LayerX)) > -0x54 && unchecked((short)(x - frame.LayerX)) <= 0x154;
                case 7: return unchecked((short)(x - frame.LayerX)) > -0xC0 && unchecked((short)(x - frame.LayerX)) <= 0x1C0;
                case 6:
                case 15:
                case 16: return GeneralHorizontal(frame, x) && Vertical(frame, record.Y);
                default: return GeneralHorizontal(frame, x);
            }
        }

        private static bool InSectionRange(FrameState frame, ObjectRecord record)
        {
            if (frame.SectionState == 0 || record.ParentIndex >= 0 || record.Type == 9) return true;
            return frame.SectionCurrent <= frame.SectionLimit && record.Index >= frame.SectionCurrent && record.Index <= frame.SectionLimit;
        }

        private static bool GeneralHorizontal(FrameState frame, ushort x)
        {
            return frame.ScannerLeft < x && frame.ScannerRight >= x;
        }

        private static bool Vertical(FrameState frame, ushort y)
        {
            var target = Math.Max(0, (int)frame.VerticalReference + 0x20 - y);
            return frame.LayerY < target && frame.LayerY + 0x120 >= target;
        }

        private static bool GroupEligible(FrameState frame, ObjectRecord group)
        {
            if (group.Children.Count == 0) return false;
            var first = group.Children[0];
            var last = group.Children[group.Children.Count - 1];
            return first.X <= frame.ScannerRight && last.X > frame.ScannerLeft;
        }
    }

    internal static class DkcNames
    {
        private static readonly Dictionary<int, string> ActorNames = new Dictionary<int, string>
        {
            {0x23,"Barrel"},{0x24,"Rope barrel"},{0x25,"Oil drum"},{0x26,"DK barrel"},{0x27,"TNT barrel"},
            {0x38,"Barrel cannon"},{0x44,"Group controller"},{0x4A,"Checkpoint barrel"},{0x4C,"Enemy spawn barrel"},
            {0x5D,"Camera object"},{0x63,"Light switch barrel"},{0x64,"Light controller"},{0x6A,"Exit door"},
            {0x6B,"Underwater exit door"},{0x6C,"Minigame/boss controller"},{0x6D,"Minigame barrel"},
            {0x70,"Current-world boss"},{0x75,"Giant banana/controller"},{0x77,"Credits controller"}
        };

        private static readonly Dictionary<int, string> Levels = new Dictionary<int, string>
        {
            {0x00,"Jungle Hijinxs"},{0x01,"Reptile Rumble"},{0x02,"Bouncy Bonanza"},{0x03,"Misty Mine"},
            {0x04,"Ropey Rampage"},{0x05,"Orang-utan Gang"},{0x06,"Barrel Cannon Canyon"},{0x17,"Poison Pond"},
            {0x18,"Snow Barrel Blast"},{0x25,"Croctopus Chase"},{0x4C,"Gang-Plank Galleon"},{0x51,"Slipslide Ride"}
        };

        public static string Actor(ushort id)
        {
            string name;
            return ActorNames.TryGetValue(id, out name) ? name : "Actor " + Format.Hex(id, 4);
        }

        public static string Level(ushort id)
        {
            string name;
            return Levels.TryGetValue(id, out name) ? name : "Level " + Format.Hex(id, 4);
        }

        public static string ObjectType(ushort type)
        {
            switch (type)
            {
                case 1: return "standard object"; case 2: return "normal object variant"; case 3: return "two-stage object";
                case 4: return "wide-window object"; case 5: return "object group"; case 6: return "vertical/windowed object";
                case 7: return "wide activation object"; case 8: return "callback trigger"; case 9: return "section controller";
                case 10: return "one-shot controller"; case 11: return "conditional object"; case 12: return "special child";
                case 13: return "multi-OAM object"; case 14: return "secondary-slot object"; case 15: return "vertical object";
                case 16: return "conditional vertical object"; default: return type == 0 ? "end" : "unknown";
            }
        }
    }
}
