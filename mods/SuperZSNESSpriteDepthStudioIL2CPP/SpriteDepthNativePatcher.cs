using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SuperZSNESSpriteDepthStudio
{
    internal sealed class SpriteDepthNativePatcher : IDisposable
    {
        internal const string ExpectedGameAssemblySha256 =
            "0A5582B26EF2596FFA504AC6C1282E145EFA093B49EFD22974D4F2C74561271A";
        internal const int ScaleHookRva = 0x3925ED;
        internal const int ZHookRva = 0x393C74;
        internal static readonly byte[] ScaleHookBytes = Bytes(0xF3,0x0F,0x11,0x45,0x98);
        internal static readonly byte[] ZHookBytes = Bytes(0xF3,0x0F,0x58,0x4D,0x90);
        private const uint MemCommitReserve = 0x3000, MemRelease = 0x8000;
        private const uint PageReadWrite = 0x04, PageExecuteRead = 0x20, PageExecuteReadWrite = 0x40;
        private const int SlotCount = 128, OffsetTableOffset = 0, ScaleTableOffset = 512;
        private const int ScaleStubOffset = 64, ZStubOffset = 192;
        private readonly Action<string> _log;
        private readonly List<PatchRecord> _records = new List<PatchRecord>();
        private IntPtr _module, _data, _code;
        internal bool Applied { get; private set; }

        internal SpriteDepthNativePatcher(Action<string> log) { _log = log ?? (_ => { }); }

        internal void Apply(string assemblyPath)
        {
            if (IntPtr.Size != 4) throw new PlatformNotSupportedException("The verified sprite hook is x86-only.");
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath)));
            if (!string.Equals(hash, ExpectedGameAssemblySha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsupported GameAssembly.dll SHA-256: " + hash);
            _module = GetModuleHandleW("GameAssembly.dll");
            if (_module == IntPtr.Zero) throw new InvalidOperationException("GameAssembly.dll is not loaded.");
            Require(Add(_module, ScaleHookRva), ScaleHookBytes, "sprite scale hook");
            Require(Add(_module, ZHookRva), ZHookBytes, "sprite Z hook");
            _data = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, MemCommitReserve, PageReadWrite);
            _code = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, MemCommitReserve, PageReadWrite);
            if (_data == IntPtr.Zero || _code == IntPtr.Zero)
                throw new InvalidOperationException("VirtualAlloc failed: " + Marshal.GetLastWin32Error());
            try
            {
                Update(new float[SlotCount], Ones());
                byte[] scale = BuildScaleStub(Add(_code, ScaleStubOffset).ToInt64(),
                    Add(_module, ScaleHookRva + 5).ToInt64(), Add(_data, ScaleTableOffset));
                byte[] z = BuildZStub(Add(_code, ZStubOffset).ToInt64(),
                    Add(_module, ZHookRva + 5).ToInt64(), Add(_data, OffsetTableOffset));
                Marshal.Copy(scale, 0, Add(_code, ScaleStubOffset), scale.Length);
                Marshal.Copy(z, 0, Add(_code, ZStubOffset), z.Length);
                if (!VirtualProtect(_code, (UIntPtr)4096, PageExecuteRead, out uint _))
                    throw new InvalidOperationException("Could not protect sprite stubs.");
                FlushInstructionCache(GetCurrentProcess(), _code, (UIntPtr)4096);
                Patch(Add(_module, ScaleHookRva), ScaleHookBytes,
                    BuildJump(Add(_module, ScaleHookRva).ToInt64(), Add(_code, ScaleStubOffset).ToInt64(), 5), "scale");
                Patch(Add(_module, ZHookRva), ZHookBytes,
                    BuildJump(Add(_module, ZHookRva).ToInt64(), Add(_code, ZStubOffset).ToInt64(), 5), "Z");
                Applied = true;
                _log("Verified per-OAM sprite depth and scale hooks applied.");
            }
            catch { RollBack(); Release(); throw; }
        }

        internal void Update(float[] offsets, float[] scales)
        {
            if (_data == IntPtr.Zero) throw new InvalidOperationException("Sprite tables are not allocated.");
            if (offsets?.Length != SlotCount || scales?.Length != SlotCount)
                throw new ArgumentException("Sprite tables must have 128 entries.");
            Marshal.Copy(offsets, 0, Add(_data, OffsetTableOffset), SlotCount);
            Marshal.Copy(scales, 0, Add(_data, ScaleTableOffset), SlotCount);
        }

        internal static byte[] BuildScaleStub(long stub, long returnAddress, IntPtr table)
        {
            var b = new List<byte>(32);
            AddBytes(b, 0x50);                                      // push eax
            AddBytes(b, 0x8B,0x45,0x10);                            // mov eax,[ebp+10h]
            AddBytes(b, 0xF3,0x0F,0x59,0x04,0x85);                 // mulss xmm0,[eax*4+table]
            AddBytes(b, BitConverter.GetBytes(Address32(table)));
            AddBytes(b, 0x58);                                      // pop eax
            AddBytes(b, ScaleHookBytes);                            // original store
            AddBytes(b, BuildJump(stub + b.Count, returnAddress, 5));
            return b.ToArray();
        }

        internal static byte[] BuildZStub(long stub, long returnAddress, IntPtr table)
        {
            var b = new List<byte>(32);
            AddBytes(b, ZHookBytes);                                // stock priority Z + OAM order
            AddBytes(b, 0x50);                                      // push eax
            AddBytes(b, 0x8B,0x45,0x10);                            // mov eax,[ebp+10h]
            AddBytes(b, 0xF3,0x0F,0x58,0x0C,0x85);                 // addss xmm1,[eax*4+table]
            AddBytes(b, BitConverter.GetBytes(Address32(table)));
            AddBytes(b, 0x58);                                      // pop eax
            AddBytes(b, BuildJump(stub + b.Count, returnAddress, 5));
            return b.ToArray();
        }

        internal static byte[] BuildJump(long source, long target, int length)
        {
            long displacement = target - (source + 5);
            if (length < 5 || displacement < int.MinValue || displacement > int.MaxValue)
                throw new InvalidOperationException("Invalid rel32 jump.");
            byte[] result = new byte[length];
            for (int i = 0; i < result.Length; i++) result[i] = 0x90;
            result[0] = 0xE9;
            Buffer.BlockCopy(BitConverter.GetBytes((int)displacement), 0, result, 1, 4);
            return result;
        }

        private static float[] Ones() { var v = new float[SlotCount]; for (int i=0;i<v.Length;i++) v[i]=1f; return v; }
        private void Patch(IntPtr address, byte[] expected, byte[] replacement, string name)
        {
            Require(address, expected, name);
            WriteMemory(address, replacement);
            Require(address, replacement, name + " patched");
            _records.Add(new PatchRecord { Address=address, Original=(byte[])expected.Clone(), Replacement=(byte[])replacement.Clone() });
        }
        private void RollBack()
        {
            for (int i=_records.Count-1;i>=0;i--)
            {
                PatchRecord r=_records[i];
                try { if (Equal(Read(r.Address,r.Replacement.Length),r.Replacement)) WriteMemory(r.Address,r.Original); } catch { }
            }
            _records.Clear(); Applied=false;
        }
        public void Dispose() { RollBack(); Release(); }
        private void Release()
        {
            if(_code!=IntPtr.Zero) VirtualFree(_code,UIntPtr.Zero,MemRelease);
            if(_data!=IntPtr.Zero) VirtualFree(_data,UIntPtr.Zero,MemRelease);
            _code=IntPtr.Zero; _data=IntPtr.Zero;
        }
        private static void Require(IntPtr address, byte[] expected, string name)
        { if(!Equal(Read(address,expected.Length),expected)) throw new InvalidDataException(name+" bytes do not match v0.300."); }
        private static byte[] Read(IntPtr p,int n){var b=new byte[n];Marshal.Copy(p,b,0,n);return b;}
        private static bool Equal(byte[] a,byte[] b){if(a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true;}
        private static void WriteMemory(IntPtr p,byte[] b)
        {
            if(!VirtualProtect(p,(UIntPtr)b.Length,PageExecuteReadWrite,out uint old))throw new InvalidOperationException("VirtualProtect failed.");
            try{Marshal.Copy(b,0,p,b.Length);FlushInstructionCache(GetCurrentProcess(),p,(UIntPtr)b.Length);}finally{VirtualProtect(p,(UIntPtr)b.Length,old,out uint _);}
        }
        private static uint Address32(IntPtr p)=>unchecked((uint)p.ToInt64());
        private static IntPtr Add(IntPtr p,int o)=>new IntPtr(p.ToInt64()+o);
        private static byte[] Bytes(params byte[] b)=>b;
        private static void AddBytes(List<byte> b,params byte[] v)=>b.AddRange(v);
        private sealed class PatchRecord{internal IntPtr Address;internal byte[] Original;internal byte[] Replacement;}
        [DllImport("kernel32",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr GetModuleHandleW(string name);
        [DllImport("kernel32",SetLastError=true)]private static extern IntPtr VirtualAlloc(IntPtr a,UIntPtr s,uint t,uint p);
        [DllImport("kernel32",SetLastError=true)]private static extern bool VirtualFree(IntPtr a,UIntPtr s,uint t);
        [DllImport("kernel32",SetLastError=true)]private static extern bool VirtualProtect(IntPtr a,UIntPtr s,uint n,out uint o);
        [DllImport("kernel32")]private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32",SetLastError=true)]private static extern bool FlushInstructionCache(IntPtr p,IntPtr a,UIntPtr s);
    }
}
