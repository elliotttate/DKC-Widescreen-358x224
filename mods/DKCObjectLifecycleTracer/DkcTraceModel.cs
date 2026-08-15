using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DKCObjectLifecycleTracer
{
    internal static class DkcRam
    {
        public const int LevelId = 0x0030;
        public const int EntranceId = 0x003E;
        public const int GameState = 0x002E;
        public const int OperatingMode = 0x0A75;
        public const int CurrentActorIndex = 0x0082;
        public const int ScannerCursorPrimary = 0x00A0;
        public const int ScannerCursorSecondary = 0x00A2;
        public const int ScannerRecordIndex = 0x00A4;
        public const int ScannerWindowLeft = 0x00EF;
        public const int ScannerWindowRight = 0x00F1;
        public const int LayerX = 0x088B;
        public const int LayerY = 0x0895;
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
        public const int CameraY = 0x1A4C;
        public const int CameraX = 0x1A62;
        public const int CameraLowerBound = 0x1B23;
        public const int CameraUpperBound = 0x1B25;
        public const int SectionControllerState = 0x1E03;
        public const int SectionControllerPointer = 0x1E05;
        public const int SectionControllerCurrent = 0x1E07;
        public const int SectionControllerPending = 0x1E09;
        public const int SectionControllerLimit = 0x1E0B;
        public const int Bookkeeping = 0x192B;
        public const int BookkeepingLength = 0x100;
        public const int EntranceCount = 0xE6;
        public const int FirstStandardActorIndex = 0x02;
        public const int MaxStandardActorIndex = 0x32;

        public static bool IsInterestingWrite(int offset)
        {
            return InActorTable(offset, ActorId) || InActorTable(offset, ActorSourceRecord)
                || InActorTable(offset, ActorX) || InActorTable(offset, ActorY)
                || InActorTable(offset, ActorState)
                || (offset >= Bookkeeping && offset < Bookkeeping + BookkeepingLength)
                || (offset >= SectionControllerState && offset <= SectionControllerLimit + 1)
                || offset == LayerX || offset == LayerX + 1 || offset == LayerY || offset == LayerY + 1
                || offset == CameraLowerBound || offset == CameraLowerBound + 1
                || offset == CameraUpperBound || offset == CameraUpperBound + 1;
        }

        public static bool InActorTable(int offset, int table)
        {
            return offset >= table + FirstStandardActorIndex && offset <= table + MaxStandardActorIndex + 1;
        }

        public static int NormalizeWramAddress(uint address)
        {
            address &= 0xFFFFFF;
            var bank = (address >> 16) & 0xFF;
            var word = (int)(address & 0xFFFF);
            if (bank == 0x7E) return word;
            if (bank == 0x7F) return 0x10000 + word;
            if ((bank <= 0x3F || (bank >= 0x80 && bank <= 0xBF)) && word < 0x2000) return word;
            return -1;
        }
    }

    internal sealed class ActorSnapshot
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

        public Dictionary<string, object> ToData()
        {
            return new Dictionary<string, object>
            {
                { "index", Index }, { "actorIndex", Index }, { "actorIndexHex", Hex((uint)Index, 2) },
                { "poolOrdinal", (Index / 2) - 1 }, { "id", Hex(Id, 4) },
                { "name", DkcNames.Actor(Id) }, { "x", Hex(X, 4) }, { "y", Hex(Y, 4) },
                { "xSigned", unchecked((short)X) }, { "ySigned", unchecked((short)Y) },
                { "xSpeed", XSpeed }, { "ySpeed", YSpeed }, { "state", Hex(State, 4) },
                { "animation", Hex(Animation, 4) }, { "pose", Hex(Pose, 4) },
                { "graphics", Hex(Graphics, 4) }, { "sourceRecord", SourceRecord },
                { "sourceRecordHex", Hex(unchecked((ushort)SourceRecord), 4) }
            };
        }

        public static string Hex(uint value, int digits) { return "$" + value.ToString("X" + digits, CultureInfo.InvariantCulture); }
    }

    internal sealed class DkcFrameSnapshot
    {
        public int Frame;
        public ushort LevelId;
        public ushort EntranceId;
        public ushort GameState;
        public ushort OperatingMode;
        public ushort LayerX;
        public ushort LayerY;
        public ushort CameraX;
        public ushort CameraY;
        public ushort LowerBound;
        public ushort UpperBound;
        public ushort ScannerLeft;
        public ushort ScannerRight;
        public byte CursorPrimary;
        public byte CursorSecondary;
        public byte ScannerRecord;
        public ushort CurrentActorIndex;
        public ushort SectionState;
        public ushort SectionPointer;
        public ushort SectionCurrent;
        public ushort SectionPending;
        public ushort SectionLimit;
        public ActorSnapshot[] Actors;
        public byte[] Bookkeeping;

        public int ActiveActors { get { return Actors == null ? 0 : Actors.Count(a => a.Id != 0); } }
        public bool ObjectTracingActive { get { string ignored; return IsObjectTracingActive(this, out ignored); } }
        public string ObjectTracingReason { get { string reason; IsObjectTracingActive(this, out reason); return reason; } }

        public static DkcFrameSnapshot FromRam(byte[] ram, int frame)
        {
            if (ram == null || ram.Length < 0x1F00) throw new ArgumentException("A complete DKC WRAM snapshot is required.", "ram");
            var actors = new List<ActorSnapshot>();
            // DKC's bank-BD allocator uses X=$02,$04,...,$32.  X=$00 is the
            // reserved player slot and is not an object-list allocation slot.
            for (var index = DkcRam.FirstStandardActorIndex; index <= DkcRam.MaxStandardActorIndex; index += 2)
            {
                actors.Add(new ActorSnapshot
                {
                    Index = index,
                    Id = U16(ram, DkcRam.ActorId + index),
                    X = U16(ram, DkcRam.ActorX + index),
                    Y = U16(ram, DkcRam.ActorY + index),
                    XSpeed = S16(ram, DkcRam.ActorXSpeed + index),
                    YSpeed = S16(ram, DkcRam.ActorYSpeed + index),
                    State = U16(ram, DkcRam.ActorState + index),
                    Animation = U16(ram, DkcRam.ActorAnimation + index),
                    Pose = U16(ram, DkcRam.ActorCurrentPose + index),
                    Graphics = U16(ram, DkcRam.ActorGraphics + index),
                    SourceRecord = S16(ram, DkcRam.ActorSourceRecord + index)
                });
            }
            var bookkeeping = new byte[DkcRam.BookkeepingLength];
            Buffer.BlockCopy(ram, DkcRam.Bookkeeping, bookkeeping, 0, bookkeeping.Length);
            return new DkcFrameSnapshot
            {
                Frame = frame,
                LevelId = U16(ram, DkcRam.LevelId), EntranceId = U16(ram, DkcRam.EntranceId),
                GameState = U16(ram, DkcRam.GameState), OperatingMode = U16(ram, DkcRam.OperatingMode),
                LayerX = U16(ram, DkcRam.LayerX), LayerY = U16(ram, DkcRam.LayerY),
                CameraX = U16(ram, DkcRam.CameraX), CameraY = U16(ram, DkcRam.CameraY),
                LowerBound = U16(ram, DkcRam.CameraLowerBound), UpperBound = U16(ram, DkcRam.CameraUpperBound),
                ScannerLeft = U16(ram, DkcRam.ScannerWindowLeft), ScannerRight = U16(ram, DkcRam.ScannerWindowRight),
                CursorPrimary = ram[DkcRam.ScannerCursorPrimary], CursorSecondary = ram[DkcRam.ScannerCursorSecondary],
                ScannerRecord = ram[DkcRam.ScannerRecordIndex], CurrentActorIndex = U16(ram, DkcRam.CurrentActorIndex),
                SectionState = U16(ram, DkcRam.SectionControllerState), SectionPointer = U16(ram, DkcRam.SectionControllerPointer),
                SectionCurrent = U16(ram, DkcRam.SectionControllerCurrent), SectionPending = U16(ram, DkcRam.SectionControllerPending),
                SectionLimit = U16(ram, DkcRam.SectionControllerLimit), Actors = actors.ToArray(), Bookkeeping = bookkeeping
            };
        }

        public Dictionary<string, object> ToData(IEnumerable<ObjectRecord> nearby, IEnumerable<string> anomalies, IEnumerable<string> observations = null)
        {
            var active = ObjectTracingActive;
            return new Dictionary<string, object>
            {
                { "frame", Frame }, { "levelId", ActorSnapshot.Hex(LevelId, 4) }, { "level", DkcNames.Level(LevelId) },
                { "entranceId", ActorSnapshot.Hex(EntranceId, 4) },
                { "gameState", ActorSnapshot.Hex(GameState, 4) }, { "operatingMode", ActorSnapshot.Hex(OperatingMode, 4) },
                { "objectTracingActive", active }, { "objectTracingReason", ObjectTracingReason },
                { "layer", new Dictionary<string, object> { { "x", ActorSnapshot.Hex(LayerX, 4) }, { "y", ActorSnapshot.Hex(LayerY, 4) } } },
                { "camera", new Dictionary<string, object> { { "x", ActorSnapshot.Hex(CameraX, 4) }, { "y", ActorSnapshot.Hex(CameraY, 4) },
                    { "lowerBound", ActorSnapshot.Hex(LowerBound, 4) }, { "upperBound", ActorSnapshot.Hex(UpperBound, 4) },
                    { "span", unchecked((ushort)(UpperBound - LowerBound)) } } },
                { "scanner", new Dictionary<string, object> { { "left", ActorSnapshot.Hex(ScannerLeft, 4) }, { "right", ActorSnapshot.Hex(ScannerRight, 4) },
                    { "primaryCursor", CursorPrimary }, { "secondaryCursor", CursorSecondary }, { "recordIndex", ScannerRecord },
                    { "currentActorIndex", CurrentActorIndex }, { "sectionState", ActorSnapshot.Hex(SectionState, 4) },
                    { "sectionPointer", ActorSnapshot.Hex(SectionPointer, 4) }, { "sectionCurrent", ActorSnapshot.Hex(SectionCurrent, 4) },
                    { "sectionPending", ActorSnapshot.Hex(SectionPending, 4) }, { "sectionLimit", ActorSnapshot.Hex(SectionLimit, 4) } } },
                { "activeActorCount", active ? ActiveActors : 0 },
                { "actors", active ? Actors.Where(a => a.Id != 0).Select(a => (object)a.ToData()).ToArray() : Array.Empty<object>() },
                { "activeBookkeeping", active ? ActiveBookkeepingData() : Array.Empty<object>() },
                { "nearbyObjects", active ? (nearby ?? Enumerable.Empty<ObjectRecord>()).Select(r => (object)r.ToData(this)).ToArray() : Array.Empty<object>() },
                { "anomalies", active ? (anomalies ?? Enumerable.Empty<string>()).ToArray() : Array.Empty<string>() },
                { "observations", active ? (observations ?? Enumerable.Empty<string>()).ToArray() : Array.Empty<string>() }
            };
        }

        public static bool IsObjectTracingActive(DkcFrameSnapshot frame, out string reason)
        {
            if (frame == null) { reason = "no frame"; return false; }
            if (frame.EntranceId >= DkcRam.EntranceCount)
            {
                reason = "entrance " + ActorSnapshot.Hex(frame.EntranceId, 4) + " is outside the bank-BD gameplay entrance table";
                return false;
            }
            if (frame.LowerBound == 0 && frame.UpperBound == 0)
            {
                reason = "camera bounds are zero (map/menu/transition WRAM layout)";
                return false;
            }
            if (frame.UpperBound < frame.LowerBound)
            {
                reason = "camera bounds are inverted";
                return false;
            }
            reason = "valid gameplay entrance and camera bounds";
            return true;
        }

        public static bool IsObjectTracingActive(byte[] ram, out string reason)
        {
            if (ram == null || ram.Length <= DkcRam.CameraUpperBound + 1) { reason = "WRAM unavailable"; return false; }
            var entrance = U16(ram, DkcRam.EntranceId);
            var lower = U16(ram, DkcRam.CameraLowerBound);
            var upper = U16(ram, DkcRam.CameraUpperBound);
            if (entrance >= DkcRam.EntranceCount)
            {
                reason = "entrance " + ActorSnapshot.Hex(entrance, 4) + " is outside the bank-BD gameplay entrance table";
                return false;
            }
            if (lower == 0 && upper == 0) { reason = "camera bounds are zero (map/menu/transition WRAM layout)"; return false; }
            if (upper < lower) { reason = "camera bounds are inverted"; return false; }
            reason = "valid gameplay entrance and camera bounds";
            return true;
        }

        private object[] ActiveBookkeepingData()
        {
            var values = new List<object>();
            for (var i = 0; i < Bookkeeping.Length; i++)
                if (Bookkeeping[i] != 0) values.Add(new Dictionary<string, object> { { "record", i }, { "value", Bookkeeping[i] }, { "valueHex", ActorSnapshot.Hex(Bookkeeping[i], 2) } });
            return values.ToArray();
        }

        private static ushort U16(byte[] ram, int address) { return (ushort)(ram[address] | (ram[address + 1] << 8)); }
        private static short S16(byte[] ram, int address) { return unchecked((short)U16(ram, address)); }
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
        public List<ObjectRecord> Children = new List<ObjectRecord>();

        public Dictionary<string, object> ToData(DkcFrameSnapshot frame)
        {
            var book = frame != null && Index >= 0 && Index < frame.Bookkeeping.Length ? frame.Bookkeeping[Index] : (byte)0;
            var screenX = frame == null ? 0 : unchecked((short)(X - frame.LayerX));
            return new Dictionary<string, object>
            {
                { "index", Index }, { "address", ActorSnapshot.Hex(Address, 6) }, { "type", ActorSnapshot.Hex(Type, 4) },
                { "typeName", DkcNames.ObjectType(Type) }, { "x", ActorSnapshot.Hex(X, 4) }, { "y", ActorSnapshot.Hex(Y, 4) },
                { "data", ActorSnapshot.Hex(Data, 4) }, { "parentIndex", ParentIndex < 0 ? (object)null : ParentIndex },
                { "bookkeeping", ActorSnapshot.Hex(book, 2) }, { "screenX", screenX },
                { "window", frame == null ? null : WindowClassification(screenX, frame) },
                { "children", Children.Select(c => (object)c.ToData(frame)).ToArray() }
            };
        }

        private static string WindowClassification(int screenX, DkcFrameSnapshot frame)
        {
            if (screenX > -0x20 && screenX < 0x140) return "stock-window";
            if (screenX > -0x58 && screenX < 0x158) return "358x224-extension-only";
            if (screenX > -0x68 && screenX < 0x168) return "398x224-extension-only";
            return "outside";
        }
    }

    internal interface ISnesMemoryReader { byte ReadByte(uint address); }

    internal static class ObjectTableDecoder
    {
        public static List<ObjectRecord> Decode(ISnesMemoryReader memory, ushort entrance, out string error)
        {
            error = null;
            var result = new List<ObjectRecord>();
            if (memory == null) { error = "SNES memory is unavailable."; return result; }
            // DATA_BD8000 ends at DATA_BD81CC: exactly $E6 16-bit entries.
            // Reading entry $E6 used to consume the first bytes after the table
            // as a pointer and produced bogus object lists on the world map.
            if (entrance >= DkcRam.EntranceCount)
            {
                error = "Entrance " + ActorSnapshot.Hex(entrance, 4) + " is outside the " + DkcRam.EntranceCount.ToString(CultureInfo.InvariantCulture) + "-entry bank-BD gameplay entrance table.";
                return result;
            }
            try
            {
                var pointer = ReadWord(memory, 0xBD8000u + (uint)(entrance * 2));
                if (pointer < 0x8000) { error = "Entrance table returned an invalid bank-BD pointer " + ActorSnapshot.Hex(pointer, 4) + "."; return result; }
                var baseAddress = 0xBD0000u | pointer;
                for (var ordinal = 0; ordinal < 512; ordinal++)
                {
                    var address = (baseAddress + (uint)(ordinal * 8)) & 0xFFFFFF;
                    var type = ReadWord(memory, address);
                    if (type == 0) break;
                    if (type > 0x10) { error = "Stopped at implausible object type " + ActorSnapshot.Hex(type, 4) + " at " + ActorSnapshot.Hex(address, 6) + "."; break; }
                    var record = ReadRecord(memory, baseAddress, address, -1);
                    if (record.Type == 5) DecodeGroup(memory, baseAddress, record);
                    result.Add(record);
                }
            }
            catch (Exception ex) { error = ex.Message; }
            return result;
        }

        private static ObjectRecord ReadRecord(ISnesMemoryReader memory, uint levelBase, uint address, int parent)
        {
            return new ObjectRecord
            {
                Address = address, Index = unchecked((ushort)((address & 0xFFFF) - (levelBase & 0xFFFF))) / 8,
                Type = ReadWord(memory, address), X = ReadWord(memory, address + 2), Y = ReadWord(memory, address + 4),
                Data = ReadWord(memory, address + 6), ParentIndex = parent
            };
        }

        private static void DecodeGroup(ISnesMemoryReader memory, uint levelBase, ObjectRecord group)
        {
            if (group.Data < 0x8000) return;
            var address = 0xBD0000u | unchecked((ushort)(group.Data + 8));
            for (var i = 0; i < 128; i++, address += 8)
            {
                var type = ReadWord(memory, address);
                if (type == 0) break;
                if (type > 0x10) break;
                group.Children.Add(ReadRecord(memory, levelBase, address, group.Index));
            }
        }

        private static ushort ReadWord(ISnesMemoryReader memory, uint address)
        {
            return (ushort)(memory.ReadByte(address & 0xFFFFFF) | (memory.ReadByte((address + 1) & 0xFFFFFF) << 8));
        }
    }

    internal static class LifecycleAnalyzer
    {
        public static List<Dictionary<string, object>> Diff(DkcFrameSnapshot before, DkcFrameSnapshot after, Func<int, string> lastWriter)
        {
            var events = new List<Dictionary<string, object>>();
            if (before == null || after == null || !before.ObjectTracingActive || !after.ObjectTracingActive) return events;
            for (var i = 0; i < after.Actors.Length; i++)
            {
                var oldActor = before.Actors[i];
                var actor = after.Actors[i];
                if (oldActor.Id == actor.Id && oldActor.SourceRecord == actor.SourceRecord) continue;
                var kind = oldActor.Id == 0 && actor.Id != 0 ? "actor_allocated"
                    : oldActor.Id != 0 && actor.Id == 0 ? "actor_freed" : "actor_replaced";
                events.Add(new Dictionary<string, object>
                {
                    { "type", kind }, { "frame", after.Frame }, { "index", actor.Index }, { "actorIndex", actor.Index },
                    { "before", oldActor.ToData() }, { "after", actor.ToData() },
                    { "lastWriter", lastWriter == null ? null : lastWriter(DkcRam.ActorId + actor.Index) }
                });
            }
            for (var i = 0; i < DkcRam.BookkeepingLength; i++)
            {
                if (before.Bookkeeping[i] == after.Bookkeeping[i]) continue;
                events.Add(new Dictionary<string, object>
                {
                    { "type", "bookkeeping_changed" }, { "frame", after.Frame }, { "record", i },
                    { "before", ActorSnapshot.Hex(before.Bookkeeping[i], 2) }, { "after", ActorSnapshot.Hex(after.Bookkeeping[i], 2) },
                    { "lastWriter", lastWriter == null ? null : lastWriter(DkcRam.Bookkeeping + i) }
                });
            }
            return events;
        }

        public static List<string> FindAnomalies(DkcFrameSnapshot frame, IList<ObjectRecord> records)
        {
            var result = new List<string>();
            if (frame == null || !frame.ObjectTracingActive) return result;
            for (var record = 0; record < frame.Bookkeeping.Length; record++)
            {
                var value = frame.Bookkeeping[record];
                if (value == 0) continue;
                if (value != 0xFF && ((value & 1) != 0 || value < DkcRam.FirstStandardActorIndex || value > DkcRam.MaxStandardActorIndex))
                    result.Add("Bookkeeping record " + record + " contains structurally invalid bank-BD actor index " + ActorSnapshot.Hex(value, 2) + ".");
            }
            return result.Distinct().ToList();
        }

        // These conditions are useful leads, not proof of a game bug. DKC can
        // leave a bookmark stale until the scanner revisits the record, and it
        // clears actor identity/source separately from $192B bookkeeping.
        public static List<string> FindObservations(DkcFrameSnapshot frame, IList<ObjectRecord> records)
        {
            var result = new List<string>();
            if (frame == null || !frame.ObjectTracingActive) return result;
            var actorsByIndex = frame.Actors.ToDictionary(a => a.Index);
            var owners = new Dictionary<int, int>();
            for (var record = 0; record < frame.Bookkeeping.Length; record++)
            {
                var value = frame.Bookkeeping[record];
                if (value == 0 || value == 0xFF || (value & 1) != 0 || value < DkcRam.FirstStandardActorIndex || value > DkcRam.MaxStandardActorIndex) continue;
                ActorSnapshot actor;
                if (!actorsByIndex.TryGetValue(value, out actor) || actor.Id == 0)
                {
                    result.Add("Bookkeeping record " + record + " temporarily points to inactive actor index " + ActorSnapshot.Hex(value, 2) + ".");
                    continue;
                }
                int previous;
                if (owners.TryGetValue(value, out previous))
                    result.Add("Actor index " + ActorSnapshot.Hex(value, 2) + " is referenced by bookkeeping records " + previous + " and " + record + ".");
                else owners[value] = record;
                var expected = actor.SourceRecord < 0 ? -actor.SourceRecord : actor.SourceRecord;
                if (actor.SourceRecord != unchecked((short)0x8000) && expected != record)
                    result.Add("Bookkeeping record " + record + " references actor " + ActorSnapshot.Hex(value, 2) + " while its source record is " + actor.SourceRecord + ".");
            }
            foreach (var actor in frame.Actors.Where(a => a.Id != 0 && a.SourceRecord != unchecked((short)0x8000)))
            {
                var record = actor.SourceRecord < 0 ? -actor.SourceRecord : actor.SourceRecord;
                if (record <= 0 || record >= frame.Bookkeeping.Length) continue;
                if (frame.Bookkeeping[record] != actor.Index)
                    result.Add("Actor " + ActorSnapshot.Hex((uint)actor.Index, 2) + " (" + DkcNames.Actor(actor.Id) + ") temporarily lacks a source-record back-reference at record " + record + ".");
            }
            foreach (var group in (records ?? Array.Empty<ObjectRecord>()).Where(r => r.Type == 5 && r.Index >= 0 && r.Index < frame.Bookkeeping.Length && frame.Bookkeeping[r.Index] == 0xFF))
            {
                var missing = group.Children.Where(c => c.Index >= 0 && c.Index < frame.Bookkeeping.Length && frame.Bookkeeping[c.Index] == 0).ToArray();
                if (missing.Length != 0)
                    result.Add("Active type-5 group record " + group.Index + " has " + missing.Length + " missing child bookmark(s): " + string.Join(",", missing.Select(c => c.Index.ToString(CultureInfo.InvariantCulture)).ToArray()) + ".");
            }
            return result.Distinct().ToList();
        }

        public static IEnumerable<ObjectRecord> Nearby(DkcFrameSnapshot frame, IList<ObjectRecord> records, int radius)
        {
            if (frame == null || records == null) return Enumerable.Empty<ObjectRecord>();
            return records.Where(r => Math.Abs(unchecked((short)(r.X - frame.LayerX))) <= radius || r.Type == 5).Take(96).ToArray();
        }

        private static IEnumerable<ObjectRecord> Flatten(IEnumerable<ObjectRecord> records)
        {
            if (records == null) yield break;
            foreach (var record in records)
            {
                yield return record;
                foreach (var child in record.Children) yield return child;
            }
        }
    }

    internal static class DkcNames
    {
        private static readonly string[] Actors =
        {
            "None","Donkey Kong","Diddy Kong","Free-movement debug enemy","Unknown 04","Kritter/Krash","Klump","Diddy's hat",
            "Burst effect 08","Rambi","Expresso","Winky","Enguarde","Squawks","Nut-throwing Necky","Smoke puff","Necky nut",
            "Unknown 11","Unknown 12","Level-complete controller","Breakable wall","Banana bunch","KONG letter","Burst effect","Animal buddy box",
            "Zinger","Klaptrap","Half tire","Unknown half tire","Rolling tire","Unknown rolling tire","Floating tire","Mincer","Unknown 21",
            "Steel keg","Barrel","Rope barrel","Oil drum","DK barrel","TNT barrel","Oil fire","Slippa","Barrel piece","DK barrel letters",
            "Item cache","Unknown 2D","Flying rock","Army","Vertical rope","Swinging rope","Explosion","Unknown 33","Life balloon",
            "Explosion spawner","Snowflake spawner","Smoke effect","Barrel cannon","Sprite platform","Unknown 3A","Small smoke puff","Sparkle",
            "Elevator lift","Snowflake","DK-house fish","Unknown 40","Unknown 41","Bananas","Butterfly","Group controller","Animal buddy token",
            "Blue Krusha","Unknown 47","Elevator-lift spawner","Unknown 49","Checkpoint barrel","Mini-Necky","Enemy spawn barrel","Gnawty",
            "Lightning bolt","Flying Necky","Manky Kong","Minecart","Minecart sparks","Chomps","Chomps Jr.","Bitesize","Squidge","Croctopus",
            "Line-guide platform","Unknown 59","Ceiling light","Group special child","Fuel can","Camera object","Revealed item","Unknown 5F",
            "Unknown 60","Clambo","Clambo pearl","Light switch barrel","Light controller","Checkpoint-star spawner","Diddy's stars","Checkpoint stars",
            "Rockkroc","Lives HUD","Exit door","Underwater exit door","Minigame/boss controller","Minigame barrel","Minigame item","Unknown 6F",
            "Current-world boss","Millstone Gnawty","Timer","Sign","Giant banana","Unknown 75","Large animal token","Credits controller","Grey Krusha"
        };

        private static readonly Dictionary<int, string> Levels = new Dictionary<int, string>
        {
            {0x00,"Jungle Hijinxs"},{0x01,"Reptile Rumble"},{0x02,"Bouncy Bonanza"},{0x03,"Misty Mine"},{0x04,"Ropey Rampage"},
            {0x05,"Orang-utan Gang"},{0x06,"Barrel Cannon Canyon"},{0x0C,"Manic Mincers"},{0x0E,"Torchlight Trouble"},{0x0F,"Elevator Antics"},
            {0x17,"Poison Pond"},{0x18,"Snow Barrel Blast"},{0x19,"Mine Cart Madness"},{0x1A,"Platform Perils"},{0x1B,"Mine Cart Carnage"},
            {0x1C,"Trick Track Trek"},{0x1D,"Tanked Up Trouble"},{0x1E,"Stop & Go Station"},{0x23,"Loopy Lights"},{0x25,"Croctopus Chase"},
            {0x26,"Oil Drum Alley"},{0x27,"Blackout Basement"},{0x28,"Millstone Mayhem"},{0x29,"Temple Tempest"},{0x4C,"Gang-Plank Galleon"},
            {0x51,"Slipslide Ride"},{0x58,"Tree Top Town"},{0x59,"Vulture Culture"},{0x5B,"Ice Age Alley"},{0x61,"Coral Capers"},
            {0x64,"Rope Bridge Rumble"},{0x65,"Forest Frenzy"},{0x6A,"Winky's Walkway"},{0x6B,"Clam City"},{0x6C,"Boss arena"}
        };

        public static string Actor(ushort id) { return id < Actors.Length ? Actors[id] : "Actor " + ActorSnapshot.Hex(id, 4); }
        public static string Level(ushort id) { string value; return Levels.TryGetValue(id, out value) ? value : "Level " + ActorSnapshot.Hex(id, 4); }
        public static string ObjectType(ushort type)
        {
            switch (type)
            {
                case 1: return "standard object"; case 2: return "normal object variant"; case 3: return "two-stage window object";
                case 4: return "wide-window object"; case 5: return "object group"; case 6: return "vertical/windowed object";
                case 7: return "wide activation object"; case 8: return "callback trigger"; case 9: return "section controller";
                case 10: return "one-shot controller"; case 11: return "conditional object"; case 12: return "special child";
                case 13: return "multi-OAM object"; case 14: return "secondary-slot object";
                case 15: return "vertical object"; case 16: return "conditional vertical object";
                default: return type == 0 ? "end" : "unknown";
            }
        }
    }
}
