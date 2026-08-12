using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SuperZSNESLayerDepthControllerIL2CPP
{
    internal sealed class NativeTileDepthPatcher : IDisposable
    {
        internal const string ExpectedGameAssemblySha256 =
            "0A5582B26EF2596FFA504AC6C1282E145EFA093B49EFD22974D4F2C74561271A";
        internal const int ScaleHookRva = 0x383674;
        internal const int ZHookRva = 0x383790;
        internal static readonly byte[] ScaleHookBytes =
            Bytes(0xF3, 0x0F, 0x10, 0x5C, 0xB0, 0x10);
        internal static readonly byte[] ZHookBytes =
            Bytes(0x0F, 0x28, 0xC3, 0xF3, 0x0F, 0x59, 0x44, 0x91, 0x10);

        private const uint MemCommitReserve = 0x3000;
        private const uint MemRelease = 0x8000;
        private const uint PageReadWrite = 0x04;
        private const uint PageExecuteRead = 0x20;
        private const uint PageExecuteReadWrite = 0x40;
        internal const int PaletteCount = 8;
        internal const int BackgroundCount = 4;
        internal const int PaletteOffsetCount = PaletteCount * BackgroundCount;
        private const int DataPaletteOffsets = 0;
        private const int DataCameraBase = PaletteOffsetCount * 4;
        private const int DataCurrentZ = DataCameraBase + 4;
        private const int ScaleStubOffset = 256;
        private const int ZStubOffset = 512;

        private readonly Action<string> _log;
        private readonly List<PatchRecord> _records = new List<PatchRecord>();
        private IntPtr _moduleBase;
        private IntPtr _dataBase;
        private IntPtr _trampolineBase;

        internal bool Applied { get; private set; }
        internal string ModuleBaseHex => HexAddress(_moduleBase);
        internal string TrampolineBaseHex => HexAddress(_trampolineBase);

        internal NativeTileDepthPatcher(Action<string> log)
        {
            _log = log ?? (_ => { });
        }

        internal void Apply(string gameAssemblyPath, float[] paletteOffsets)
        {
            if (Applied) throw new InvalidOperationException("Native tile-depth patch is active.");
            if (IntPtr.Size != 4)
                throw new PlatformNotSupportedException("The verified patch supports x86 v0.300 only.");
            if (paletteOffsets == null || paletteOffsets.Length != PaletteOffsetCount)
                throw new ArgumentException("Exactly 32 BG palette offsets are required.",
                    nameof(paletteOffsets));
            for (int i = 0; i < paletteOffsets.Length; i++)
            {
                float value = paletteOffsets[i];
                if (float.IsNaN(value) || float.IsInfinity(value) || value < -1f || value > 1f)
                    throw new ArgumentOutOfRangeException(nameof(paletteOffsets),
                        "Palette offset " + i + " is outside -1..1.");
            }
            string hash = ComputeSha256(gameAssemblyPath);
            if (!string.Equals(hash, ExpectedGameAssemblySha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsupported GameAssembly.dll SHA-256: " + hash);

            _moduleBase = GetModuleHandleW("GameAssembly.dll");
            if (_moduleBase == IntPtr.Zero)
                throw new InvalidOperationException("Loaded GameAssembly.dll was not found.");
            RequireBytes(Add(_moduleBase, ScaleHookRva), ScaleHookBytes, "scale hook");
            RequireBytes(Add(_moduleBase, ZHookRva), ZHookBytes, "Z hook");

            _dataBase = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096,
                MemCommitReserve, PageReadWrite);
            _trampolineBase = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096,
                MemCommitReserve, PageReadWrite);
            if (_dataBase == IntPtr.Zero || _trampolineBase == IntPtr.Zero)
                throw new InvalidOperationException("VirtualAlloc failed: " + Marshal.GetLastWin32Error());

            try
            {
                for (int i = 0; i < paletteOffsets.Length; i++)
                    WriteData(DataPaletteOffsets + i * 4,
                        BitConverter.GetBytes(paletteOffsets[i]));
                WriteData(DataCameraBase, BitConverter.GetBytes(30f));
                WriteData(DataCurrentZ, BitConverter.GetBytes(0f));

                byte[] scaleStub = BuildScaleStub(
                    Add(_trampolineBase, ScaleStubOffset).ToInt64(),
                    Add(_moduleBase, ScaleHookRva + ScaleHookBytes.Length).ToInt64(),
                    _dataBase);
                byte[] zStub = BuildZStub(
                    Add(_trampolineBase, ZStubOffset).ToInt64(),
                    Add(_moduleBase, ZHookRva + ZHookBytes.Length).ToInt64(),
                    Add(_dataBase, DataCurrentZ));
                Marshal.Copy(scaleStub, 0, Add(_trampolineBase, ScaleStubOffset), scaleStub.Length);
                Marshal.Copy(zStub, 0, Add(_trampolineBase, ZStubOffset), zStub.Length);
                uint oldProtection;
                if (!VirtualProtect(_trampolineBase, (UIntPtr)4096,
                        PageExecuteRead, out oldProtection))
                    throw new InvalidOperationException("Could not protect native stubs: " +
                        Marshal.GetLastWin32Error());
                FlushInstructionCache(GetCurrentProcess(), _trampolineBase, (UIntPtr)4096);

                Patch(Add(_moduleBase, ZHookRva), ZHookBytes,
                    BuildJump(Add(_moduleBase, ZHookRva).ToInt64(),
                        Add(_trampolineBase, ZStubOffset).ToInt64(), ZHookBytes.Length), "Z hook");
                Patch(Add(_moduleBase, ScaleHookRva), ScaleHookBytes,
                    BuildJump(Add(_moduleBase, ScaleHookRva).ToInt64(),
                        Add(_trampolineBase, ScaleStubOffset).ToInt64(), ScaleHookBytes.Length),
                    "scale hook");
                Applied = true;
                _log("Native BG palette-depth split applied at two verified DrawLines sites.");
            }
            catch
            {
                RollBack();
                ReleasePages();
                throw;
            }
        }

        internal static int CalculatePaletteIndex(int tileEntry, int background)
        {
            if (background < 0 || background >= BackgroundCount)
                throw new ArgumentOutOfRangeException(nameof(background));
            uint palette = (uint)((tileEntry >> 10) & 7);
            return background * PaletteCount + (int)palette;
        }

        internal static float CalculateOffset(int tileEntry, int background,
            float[] paletteOffsets)
        {
            if (paletteOffsets == null || paletteOffsets.Length != PaletteOffsetCount)
                throw new ArgumentException("Exactly 32 BG palette offsets are required.",
                    nameof(paletteOffsets));
            return paletteOffsets[CalculatePaletteIndex(tileEntry, background)];
        }

        internal static byte[] BuildScaleStub(long stubAddress, long returnAddress,
            IntPtr dataBase)
        {
            uint offsets = Address32(Add(dataBase, DataPaletteOffsets));
            uint camera = Address32(Add(dataBase, DataCameraBase));
            uint currentZ = Address32(Add(dataBase, DataCurrentZ));
            var bytes = new List<byte>(128);
            Add(bytes, ScaleHookBytes);                                  // original scale load
            Add(bytes, 0x50, 0x52);                                      // push eax; push edx
            Add(bytes, 0x8B, 0xC3);                                      // mov eax,ebx
            Add(bytes, 0xC1, 0xE8, 0x0A);                                // shr eax,10
            Add(bytes, 0x83, 0xE0, 0x07);                                // and eax,7 (palette)
            Add(bytes, 0x8B, 0x55, 0x10);                                // mov edx,[ebp+10h] (BG)
            Add(bytes, 0xC1, 0xE2, 0x03);                                // shl edx,3
            Add(bytes, 0x01, 0xD0);                                      // add eax,edx
            Add(bytes, 0xF3, 0x0F, 0x10, 0x04, 0x85);                    // movss xmm0,[eax*4+offsets]
            Add(bytes, BitConverter.GetBytes(offsets));
            Add(bytes, 0x5A);                                            // pop edx (priority)
            Add(bytes, 0xF3, 0x0F, 0x10, 0x4C, 0x91, 0x10);              // movss xmm1,[ecx+edx*4+10h]
            Add(bytes, 0xF3, 0x0F, 0x59, 0xCB);                          // mulss xmm1,xmm3
            Add(bytes, 0xF3, 0x0F, 0x58, 0xC1);                          // addss xmm0,xmm1
            AddAbs(bytes, Bytes(0xF3, 0x0F, 0x11, 0x05), currentZ);       // movss [currentZ],xmm0
            Add(bytes, 0x8B, 0x45, 0x08);                                // mov eax,[ebp+this]
            AddAbs(bytes, Bytes(0xF3, 0x0F, 0x10, 0x15), camera);         // movss xmm2,[30.0]
            Add(bytes, 0xF3, 0x0F, 0x5C, 0x90, 0x38, 0x02, 0x00, 0x00);  // subss xmm2,[eax+238h]
            Add(bytes, 0x0F, 0x28, 0xE2);                                // movaps xmm4,xmm2
            Add(bytes, 0xF3, 0x0F, 0x58, 0xE0);                          // addss xmm4,xmm0
            Add(bytes, 0xF3, 0x0F, 0x58, 0xD1);                          // addss xmm2,xmm1
            Add(bytes, 0xF3, 0x0F, 0x5E, 0xE2);                          // divss xmm4,xmm2
            Add(bytes, 0xF3, 0x0F, 0x59, 0xDC);                          // mulss xmm3,xmm4
            Add(bytes, 0x58);                                            // pop eax
            Add(bytes, BuildJump(stubAddress + bytes.Count, returnAddress, 5));
            return bytes.ToArray();
        }

        internal static byte[] BuildZStub(long stubAddress, long returnAddress,
            IntPtr currentZAddress)
        {
            var bytes = new List<byte>(16);
            AddAbs(bytes, Bytes(0xF3, 0x0F, 0x10, 0x05), Address32(currentZAddress));
            Add(bytes, BuildJump(stubAddress + bytes.Count, returnAddress, 5));
            return bytes.ToArray();
        }

        internal static byte[] BuildJump(long sourceAddress, long targetAddress, int length)
        {
            if (length < 5) throw new ArgumentOutOfRangeException(nameof(length));
            long displacement = targetAddress - (sourceAddress + 5);
            if (displacement < int.MinValue || displacement > int.MaxValue)
                throw new InvalidOperationException("Native target is outside rel32 range.");
            byte[] result = Repeat(0x90, length);
            result[0] = 0xE9;
            Buffer.BlockCopy(BitConverter.GetBytes((int)displacement), 0, result, 1, 4);
            return result;
        }

        private void WriteData(int offset, byte[] value) =>
            Marshal.Copy(value, 0, Add(_dataBase, offset), value.Length);

        private void Patch(IntPtr address, byte[] expected, byte[] replacement, string name)
        {
            RequireBytes(address, expected, name);
            WriteMemory(address, replacement);
            RequireBytes(address, replacement, name + " patched");
            _records.Add(new PatchRecord
            {
                Address = address, Original = (byte[])expected.Clone(),
                Replacement = (byte[])replacement.Clone(), Name = name
            });
        }

        private void RollBack()
        {
            for (int i = _records.Count - 1; i >= 0; i--)
            {
                PatchRecord record = _records[i];
                try
                {
                    if (Equal(ReadMemory(record.Address, record.Replacement.Length),
                            record.Replacement))
                        WriteMemory(record.Address, record.Original);
                    else
                        _log("Rollback skipped changed site: " + record.Name);
                }
                catch (Exception exception)
                {
                    _log("Rollback failed for " + record.Name + ": " + exception.Message);
                }
            }
            _records.Clear();
            Applied = false;
        }

        public void Dispose()
        {
            RollBack();
            ReleasePages();
        }

        private void ReleasePages()
        {
            if (_trampolineBase != IntPtr.Zero)
                VirtualFree(_trampolineBase, UIntPtr.Zero, MemRelease);
            if (_dataBase != IntPtr.Zero)
                VirtualFree(_dataBase, UIntPtr.Zero, MemRelease);
            _trampolineBase = IntPtr.Zero;
            _dataBase = IntPtr.Zero;
        }

        private static void WriteMemory(IntPtr address, byte[] bytes)
        {
            uint oldProtection;
            if (!VirtualProtect(address, (UIntPtr)bytes.Length,
                    PageExecuteReadWrite, out oldProtection))
                throw new InvalidOperationException("VirtualProtect failed: " +
                    Marshal.GetLastWin32Error());
            try
            {
                Marshal.Copy(bytes, 0, address, bytes.Length);
                FlushInstructionCache(GetCurrentProcess(), address, (UIntPtr)bytes.Length);
            }
            finally
            {
                uint ignored;
                VirtualProtect(address, (UIntPtr)bytes.Length, oldProtection, out ignored);
            }
        }

        private static void RequireBytes(IntPtr address, byte[] expected, string name)
        {
            byte[] actual = ReadMemory(address, expected.Length);
            if (!Equal(actual, expected))
                throw new InvalidDataException(name + " bytes differ. Expected " +
                    Hex(expected) + ", found " + Hex(actual));
        }

        private static byte[] ReadMemory(IntPtr address, int count)
        {
            byte[] bytes = new byte[count];
            Marshal.Copy(address, bytes, 0, count);
            return bytes;
        }

        private static string ComputeSha256(string path)
        {
            using FileStream stream = File.OpenRead(path);
            using SHA256 sha = SHA256.Create();
            return Hex(sha.ComputeHash(stream));
        }

        private static uint Address32(IntPtr address) => unchecked((uint)address.ToInt32());
        private static string HexAddress(IntPtr address) =>
            "0x" + unchecked((uint)address.ToInt32()).ToString("X8");
        private static IntPtr Add(IntPtr address, int offset) =>
            new IntPtr(address.ToInt64() + offset);
        private static byte[] Bytes(params byte[] values) => values;
        private static byte[] Repeat(byte value, int count)
        {
            byte[] result = new byte[count];
            for (int i = 0; i < count; i++) result[i] = value;
            return result;
        }
        private static bool Equal(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }
        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "");
        private static void Add(List<byte> target, params byte[] values) => target.AddRange(values);
        private static void AddAbs(List<byte> target, byte[] prefix, uint address)
        {
            target.AddRange(prefix);
            target.AddRange(BitConverter.GetBytes(address));
        }

        private sealed class PatchRecord
        {
            internal string Name;
            internal IntPtr Address;
            internal byte[] Original;
            internal byte[] Replacement;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string moduleName);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size,
            uint allocationType, uint protect);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr address, UIntPtr size,
            uint newProtect, out uint oldProtect);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FlushInstructionCache(IntPtr process, IntPtr address,
            UIntPtr size);
    }
}
