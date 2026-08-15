using System;
using System.Collections.Generic;

namespace DKCDebugInvincibility
{
    internal static class DkcInvincibilityPatch
    {
        // Pro Action Replay BFA2A060: replace the first opcode at $BF:A2A0
        // (LDY $84) with RTS.  See README.md for the carry-preserving damage
        // path that makes this useful as a narrowly scoped debug aid.
        public const int Address = 0xBFA2A0;
        public const int HiRomOffset = 0x3FA2A0;
        public const byte Value = 0x60;

        internal static readonly byte[] ExpectedBytes =
        {
            0xA4, 0x84,             // LDY $84
            0xB9, 0x12, 0x05,       // LDA $0512,y
            0xD0, 0x02,             // BNE +2
            0x18,                   // CLC
            0x60,                   // RTS
            0xAA,                   // TAX
            0xA9, 0x00, 0x00,       // LDA #$0000
            0x99, 0x12, 0x05        // STA $0512,y
        };

        public static bool ValidateRom(byte[] rom, out string reason)
        {
            if (rom == null)
            {
                reason = "No ROM is loaded.";
                return false;
            }
            if (rom.Length < HiRomOffset + ExpectedBytes.Length)
            {
                reason = "The loaded ROM is too small to contain DKC code at $BF:A2A0.";
                return false;
            }
            for (var i = 0; i < ExpectedBytes.Length; i++)
            {
                if (rom[HiRomOffset + i] == ExpectedBytes[i]) continue;
                reason = "ROM signature mismatch at $BF:A2A0+" + i.ToString("X")
                    + ": expected $" + ExpectedBytes[i].ToString("X2")
                    + ", found $" + rom[HiRomOffset + i].ToString("X2") + ".";
                return false;
            }
            reason = "DKC USA v1.0 damage-path signature matched.";
            return true;
        }
    }

    internal sealed class CheatLease
    {
        private IDictionary<int, byte> _ownedDictionary;

        public bool Applied
        {
            get
            {
                byte value;
                return _ownedDictionary != null
                    && _ownedDictionary.TryGetValue(DkcInvincibilityPatch.Address, out value)
                    && value == DkcInvincibilityPatch.Value;
            }
        }

        public bool Apply(IDictionary<int, byte> dictionary, out string result)
        {
            if (dictionary == null)
            {
                Release();
                result = "The emulator cheat dictionary is not available.";
                return false;
            }

            if (ReferenceEquals(dictionary, _ownedDictionary) && Applied)
            {
                result = "Debug invincibility is active.";
                return true;
            }

            Release();
            byte existing;
            if (dictionary.TryGetValue(DkcInvincibilityPatch.Address, out existing))
            {
                result = "Address $BFA2A0 is already overridden with $" + existing.ToString("X2")
                    + "; the debug plugin did not take ownership.";
                return false;
            }

            dictionary.Add(DkcInvincibilityPatch.Address, DkcInvincibilityPatch.Value);
            _ownedDictionary = dictionary;
            result = "Applied runtime read override BFA2A0=60.";
            return true;
        }

        public bool Release()
        {
            if (_ownedDictionary == null) return false;
            byte existing;
            var removed = _ownedDictionary.TryGetValue(DkcInvincibilityPatch.Address, out existing)
                && existing == DkcInvincibilityPatch.Value
                && _ownedDictionary.Remove(DkcInvincibilityPatch.Address);
            _ownedDictionary = null;
            return removed;
        }
    }
}
