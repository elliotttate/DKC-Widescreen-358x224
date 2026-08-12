using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SuperZSNESNativeAtlasDirtyFixIL2CPP
{
    internal sealed class NativeAtlasPatcher : IDisposable
    {
        internal const string ExpectedGameAssemblySha256 =
            "0A5582B26EF2596FFA504AC6C1282E145EFA093B49EFD22974D4F2C74561271A";

        internal sealed class PatchSite
        {
            internal string Name;
            internal int StoreRva;
            internal int HookRva;
            internal byte[] StoreBytes;
            internal byte[] ReplayBytes;
            internal byte ArrayFieldOffset;
            internal byte IndexLocalDisplacement;
        }

        internal static readonly PatchSite[] Sites =
        {
            new PatchSite
            {
                Name = "2bpp", StoreRva = 0x3A956E, HookRva = 0x3A95A0,
                StoreBytes = Bytes(0xC6, 0x44, 0x08, 0x10, 0x01),
                ReplayBytes = Bytes(0x8B, 0x46, 0x08, 0xC1, 0xE7, 0x08),
                ArrayFieldOffset = 0x54, IndexLocalDisplacement = 0xD4
            },
            new PatchSite
            {
                Name = "4bpp", StoreRva = 0x3A9A5E, HookRva = 0x3A9A90,
                StoreBytes = Bytes(0xC6, 0x44, 0x08, 0x10, 0x01),
                ReplayBytes = Bytes(0x8B, 0x7E, 0x08, 0x8B, 0xC2),
                ArrayFieldOffset = 0x58, IndexLocalDisplacement = 0xD8
            },
            new PatchSite
            {
                Name = "8bpp", StoreRva = 0x3A9FBE, HookRva = 0x3A9FF0,
                StoreBytes = Bytes(0xC6, 0x44, 0x08, 0x10, 0x01),
                ReplayBytes = Bytes(0x8B, 0x5E, 0x08, 0x8B, 0xC2),
                ArrayFieldOffset = 0x5C, IndexLocalDisplacement = 0xC0
            }
        };

        private const uint MemCommitReserve = 0x3000;
        private const uint MemRelease = 0x8000;
        private const uint PageReadWrite = 0x04;
        private const uint PageExecuteRead = 0x20;
        private const uint PageExecuteReadWrite = 0x40;
        private const int TrampolineStride = 64;

        private readonly Action<string> _log;
        private readonly List<PatchRecord> _records = new List<PatchRecord>();
        private IntPtr _moduleBase;
        private IntPtr _trampolineBase;

        internal bool Applied { get; private set; }
        internal string GameAssemblySha256 { get; private set; } = string.Empty;
        internal string ModuleBaseHex => "0x" + _moduleBase.ToInt64().ToString("X8");
        internal string TrampolineBaseHex => "0x" + _trampolineBase.ToInt64().ToString("X8");

        internal NativeAtlasPatcher(Action<string> log)
        {
            _log = log ?? (_ => { });
        }

        internal void Apply(string gameAssemblyPath)
        {
            if (Applied) throw new InvalidOperationException("Native atlas fix is already applied.");
            if (IntPtr.Size != 4)
                throw new PlatformNotSupportedException("The verified patch supports only the x86 v0.300 build.");
            if (!File.Exists(gameAssemblyPath))
                throw new FileNotFoundException("GameAssembly.dll was not found.", gameAssemblyPath);

            GameAssemblySha256 = ComputeSha256(gameAssemblyPath);
            if (!string.Equals(GameAssemblySha256, ExpectedGameAssemblySha256,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsupported GameAssembly.dll SHA-256: " + GameAssemblySha256);

            _moduleBase = GetModuleHandleW("GameAssembly.dll");
            if (_moduleBase == IntPtr.Zero)
                throw new InvalidOperationException("The loaded GameAssembly.dll module was not found.");

            ValidateOriginalMemory(_moduleBase);
            _trampolineBase = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, MemCommitReserve, PageReadWrite);
            if (_trampolineBase == IntPtr.Zero)
                throw new InvalidOperationException("VirtualAlloc failed: " + Marshal.GetLastWin32Error());

            try
            {
                PrepareTrampolines();
                uint oldProtection;
                if (!VirtualProtect(_trampolineBase, (UIntPtr)4096, PageExecuteRead, out oldProtection))
                    throw new InvalidOperationException("Could not protect native trampolines: " +
                        Marshal.GetLastWin32Error());
                FlushInstructionCache(GetCurrentProcess(), _trampolineBase, (UIntPtr)4096);

                // Hook first: while stores remain this is semantically redundant, never under-invalidating.
                for (int i = 0; i < Sites.Length; i++)
                {
                    PatchSite site = Sites[i];
                    IntPtr address = Add(_moduleBase, site.HookRva);
                    IntPtr trampoline = Add(_trampolineBase, i * TrampolineStride);
                    byte[] replacement = BuildJump(address.ToInt64(), trampoline.ToInt64(),
                        site.ReplayBytes.Length);
                    Patch(address, site.ReplayBytes, replacement, site.Name + " hook");
                }
                for (int i = 0; i < Sites.Length; i++)
                {
                    PatchSite site = Sites[i];
                    byte[] replacement = Repeat(0x90, site.StoreBytes.Length);
                    Patch(Add(_moduleBase, site.StoreRva), site.StoreBytes, replacement,
                        site.Name + " unconditional store");
                }

                Applied = true;
                _log("Native atlas dirty fix applied to six verified sites.");
            }
            catch
            {
                RollBackRecords();
                ReleaseTrampolines();
                throw;
            }
        }

        internal static void ValidateOriginalMemory(IntPtr moduleBase)
        {
            foreach (PatchSite site in Sites)
            {
                RequireBytes(Add(moduleBase, site.StoreRva), site.StoreBytes,
                    site.Name + " unconditional store");
                RequireBytes(Add(moduleBase, site.HookRva), site.ReplayBytes,
                    site.Name + " hook window");
            }
        }

        private void PrepareTrampolines()
        {
            for (int i = 0; i < Sites.Length; i++)
            {
                PatchSite site = Sites[i];
                IntPtr address = Add(_trampolineBase, i * TrampolineStride);
                long returnAddress = Add(_moduleBase,
                    site.HookRva + site.ReplayBytes.Length).ToInt64();
                byte[] stub = BuildStub(site, address.ToInt64(), returnAddress);
                if (stub.Length > TrampolineStride)
                    throw new InvalidOperationException(site.Name + " trampoline exceeds its slot.");
                Marshal.Copy(stub, 0, address, stub.Length);
            }
        }

        internal static byte[] BuildStub(PatchSite site, long stubAddress, long returnAddress)
        {
            var bytes = new List<byte>(32)
            {
                0x51,                         // push ecx
                0x8B, 0x46, site.ArrayFieldOffset, // mov eax,[esi+texture*bitDirty]
                0x8B, 0x4D, site.IndexLocalDisplacement, // mov ecx,[ebp+page local]
                0xC6, 0x44, 0x08, 0x10, 0x01, // mov byte ptr [eax+ecx+10h],1
                0x59                          // pop ecx
            };
            bytes.AddRange(site.ReplayBytes);
            long jumpAddress = stubAddress + bytes.Count;
            bytes.AddRange(BuildJump(jumpAddress, returnAddress, 5));
            return bytes.ToArray();
        }

        internal static byte[] BuildJump(long sourceAddress, long targetAddress, int length)
        {
            if (length < 5) throw new ArgumentOutOfRangeException(nameof(length));
            long displacement = targetAddress - (sourceAddress + 5);
            if (displacement < int.MinValue || displacement > int.MaxValue)
                throw new InvalidOperationException("Native jump target is outside rel32 range.");
            byte[] result = Repeat(0x90, length);
            result[0] = 0xE9;
            byte[] encoded = BitConverter.GetBytes((int)displacement);
            Buffer.BlockCopy(encoded, 0, result, 1, 4);
            return result;
        }

        private void Patch(IntPtr address, byte[] expected, byte[] replacement, string name)
        {
            RequireBytes(address, expected, name);
            var record = new PatchRecord
            {
                Name = name, Address = address,
                Original = (byte[])expected.Clone(), Replacement = (byte[])replacement.Clone()
            };
            try
            {
                WriteMemory(address, replacement);
                RequireBytes(address, replacement, name + " patched");
                _records.Add(record);
            }
            catch
            {
                // WriteMemory may have copied bytes before a cache-flush failure. Restore only
                // when the site still contains our complete replacement.
                try
                {
                    if (BytesEqual(ReadMemory(address, replacement.Length), replacement))
                        WriteMemory(address, expected);
                }
                catch (Exception exception)
                {
                    _log("Immediate rollback failed for " + name + ": " + exception.Message);
                }
                throw;
            }
        }

        private void RollBackRecords()
        {
            for (int i = _records.Count - 1; i >= 0; i--)
            {
                PatchRecord record = _records[i];
                try
                {
                    if (BytesEqual(ReadMemory(record.Address, record.Replacement.Length), record.Replacement))
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
            RollBackRecords();
            ReleaseTrampolines();
        }

        private void ReleaseTrampolines()
        {
            if (_trampolineBase == IntPtr.Zero) return;
            if (!VirtualFree(_trampolineBase, UIntPtr.Zero, MemRelease))
                _log("VirtualFree failed: " + Marshal.GetLastWin32Error());
            _trampolineBase = IntPtr.Zero;
        }

        private static void WriteMemory(IntPtr address, byte[] bytes)
        {
            uint oldProtection;
            if (!VirtualProtect(address, (UIntPtr)bytes.Length, PageExecuteReadWrite, out oldProtection))
                throw new InvalidOperationException("VirtualProtect failed: " + Marshal.GetLastWin32Error());
            try
            {
                Marshal.Copy(bytes, 0, address, bytes.Length);
                if (!FlushInstructionCache(GetCurrentProcess(), address, (UIntPtr)bytes.Length))
                    throw new InvalidOperationException("FlushInstructionCache failed: " +
                        Marshal.GetLastWin32Error());
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
            if (!BytesEqual(actual, expected))
                throw new InvalidDataException(name + " bytes differ. Expected " + Hex(expected) +
                    ", found " + Hex(actual) + ".");
        }

        private static byte[] ReadMemory(IntPtr address, int length)
        {
            byte[] bytes = new byte[length];
            Marshal.Copy(address, bytes, 0, length);
            return bytes;
        }

        private static string ComputeSha256(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
                return Hex(sha.ComputeHash(stream));
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        private static string Hex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", string.Empty);
        }

        private static byte[] Bytes(params byte[] values) => values;

        private static byte[] Repeat(byte value, int count)
        {
            byte[] result = new byte[count];
            for (int i = 0; i < result.Length; i++) result[i] = value;
            return result;
        }

        private static IntPtr Add(IntPtr address, int offset) =>
            new IntPtr(address.ToInt64() + offset);

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
        private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);
    }
}
