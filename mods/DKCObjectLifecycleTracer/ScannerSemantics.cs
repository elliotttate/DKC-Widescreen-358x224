using System.Collections.Generic;

namespace DKCObjectLifecycleTracer
{
    internal static class ScannerSemantics
    {
        private static readonly Dictionary<uint, string> Exact = new Dictionary<uint, string>
        {
            // CODE_BDF3A2 and CODE_BDF3C3 return carry SET on exhaustion and
            // carry CLEAR after reserving a free actor index. Keep the failure
            // and success PCs distinct; $BDF3B5/$BDF3D6 are success labels.
            {0xBDF3A2,"begin primary actor-pool search ($02-$1C)"},
            {0xBDF3B1,"primary actor pool exhausted; allocation index cleared"},
            {0xBDF3B5,"primary actor index found; reserve it"},
            {0xBDF3BA,"mark primary actor source $8000 (reserved)"},
            {0xBDF3BD,"primary actor reservation succeeded (carry clear)"},
            {0xBDF3C3,"begin secondary actor-pool search ($1E-$32)"},
            {0xBDF3D2,"secondary actor pool exhausted; allocation index cleared"},
            {0xBDF3D6,"secondary actor index found; reserve it"},
            {0xBDF3DB,"mark secondary actor source $8000 (reserved)"},
            {0xBDF3DE,"secondary actor reservation succeeded (carry clear)"},
            {0xBDF476,"normal actor deallocation"},{0xBDF488,"clear actor identity/source"},{0xBDF502,"evaluate active object/group retention"},
            {0xBDF570,"object left activation window; clear bookmark and despawn"},{0xBDF585,"type-5 group retention evaluation"},
            {0xBDF5C5,"type-5 group child ownership scan"},{0xBDF60F,"type-5 group missing/other child path"},
            {0xBDF61D,"type-5 group child cleanup loop"},{0xBDF664,"clear type-5 root bookmark and deallocate root"},
            {0xBDF6A9,"clear normal object bookmark and deallocate"},{0xBDF8A2,"level object scanner entry"},
            {0xBDF8D5,"type-14 secondary-slot window test"},{0xBDF8FC,"type-14 object outside activation window"},
            {0xBDF8FF,"type-14 object already active or no secondary slot"},{0xBDF902,"type-13 multi-OAM allocation test"},
            {0xBDF915,"type-13/14 actor allocation accepted"},
            {0xBDF9A2,"type-15 horizontal/vertical window test"},{0xBDF9E8,"type-3 two-stage activation test"},
            {0xBDFA31,"type-6 horizontal/vertical window test"},{0xBDFA61,"standard scanner window reached"},
            {0xBDFA6F,"standard object allocation attempt"},{0xBDFAD7,"object accepted/already active (carry set)"},
            {0xBDFAD9,"object rejected/outside window (carry clear)"},{0xBDFB26,"primary-slot object allocation attempt"},
            {0xBDFB6E,"primary-slot allocation completed/already active/no slot"},{0xBDFB76,"type-5 group entry (widescreen hook site)"},
            {0xBDFB72,"type-5 group already active"},{0xBDFB74,"type-5 group out of range or no root slot"},
            {0xBDFBBF,"type-5 root accepted"},{0xBDFBF5,"type-5 child retry/allocation loop"},
            {0xBDFC1A,"type-5 special child allocation"},{0xBDFC59,"type-5 normal child allocation"},
            {0xBDFCCB,"type-5 child skipped or allocation finished"},{0xBDFCCC,"type-10 one-shot activation test"},
            {0xBDFD00,"type-8 callback activation test"},{0xBDFDBD,"type-9 section-controller initialization"},
            {0xBDFE39,"main object scan"},{0xBDFE70,"dispatch current object record"},{0xBDFE7F,"seek scanner cursor"},
            {0xBDFECA,"advance scanner cursor"},{0xBDFEE6,"update type-9 section controller"},{0xBDFF04,"type-9 pending section found"},
            {0xBDFF24,"type-9 active section window test"},{0xBDFF55,"type-9 crossed right/bottom boundary"},
            {0xBDFF6D,"type-9 crossed left/top boundary"},{0xBDFF85,"type-9 commit section transition"},
            {0xBDFF95,"type-9 transition rejected/finished"}
        };

        public static string Describe(uint pc)
        {
            string value;
            pc &= 0xFFFFFF;
            if (Exact.TryGetValue(pc, out value)) return value;
            if (pc >= 0xCA6C61 && pc < 0xCA7100) return "widescreen free-space helper";
            return null;
        }
    }
}
