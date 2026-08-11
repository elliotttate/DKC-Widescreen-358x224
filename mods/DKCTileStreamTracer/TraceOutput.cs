using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace DKCTileStreamTracer
{
    internal static class JsonLine
    {
        public static string Escape(string value)
        {
            if (value == null) return "null";
            var builder = new StringBuilder(value.Length + 2).Append('"');
            foreach (var ch in value)
            {
                switch (ch)
                {
                    case '\\': builder.Append("\\\\"); break;
                    case '"': builder.Append("\\\""); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (ch < 0x20) builder.Append("\\u").Append(((int)ch).ToString("X4"));
                        else builder.Append(ch);
                        break;
                }
            }
            return builder.Append('"').ToString();
        }

        public static string Value(object value)
        {
            if (value == null) return "null";
            if (value is string || value is char || value is Enum) return Escape(Convert.ToString(value, CultureInfo.InvariantCulture));
            if (value is bool) return (bool)value ? "true" : "false";
            if (value is byte[]) return Escape(BitConverter.ToString((byte[])value).Replace("-", string.Empty));
            if (value is IEnumerable<KeyValuePair<string, object>>) return Object((IEnumerable<KeyValuePair<string, object>>)value);
            if (value is System.Collections.IEnumerable && !(value is string))
            {
                var values = new List<string>();
                foreach (var item in (System.Collections.IEnumerable)value) values.Add(Value(item));
                return "[" + string.Join(",", values) + "]";
            }
            if (value is float) return ((float)value).ToString("R", CultureInfo.InvariantCulture);
            if (value is double) return ((double)value).ToString("R", CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public static string Object(IEnumerable<KeyValuePair<string, object>> values)
        {
            return "{" + string.Join(",", values.Select(pair => Escape(pair.Key) + ":" + Value(pair.Value))) + "}";
        }
    }

    internal sealed class TraceOutput : IDisposable
    {
        private readonly StreamWriter _jsonl;
        private readonly StreamWriter _pcCsv;
        private readonly StreamWriter _busCsv;

        public string DirectoryPath { get; private set; }
        public long PcRows { get; private set; }
        public long BusRows { get; private set; }

        public TraceOutput(string directory)
        {
            DirectoryPath = directory;
            Directory.CreateDirectory(directory);
            _jsonl = Writer("events.jsonl");
            _pcCsv = Writer("pc-trace.csv");
            _busCsv = Writer("ppu-dma.csv");
            _pcCsv.WriteLine("seq,frame,line,dot,cycles,pc,target,delta,a,x,y,s,d,db,pb,flags,opcode,w088b,w08a3,w0a75,w1a5b,w1b23,w1b25,vmain,vram_word,vram_mapped,vram_byte,vram_preview,dma_active,dma_channel,dma_summary");
            _busCsv.WriteLine("seq,frame,line,dot,pc,kind,address,value,vmain,vram_word,vram_mapped,vram_byte,vram_preview,dma_active,dma_channel,dma_summary");
        }

        public void SessionEvent(string type, IDictionary<string, object> data)
        {
            var fields = new List<KeyValuePair<string, object>>
            {
                new KeyValuePair<string, object>("utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
                new KeyValuePair<string, object>("type", type),
                new KeyValuePair<string, object>("data", data == null ? null : data.ToArray())
            };
            _jsonl.WriteLine(JsonLine.Object(fields));
        }

        public void Pc(IDictionary<string, object> row)
        {
            PcRows++;
            Json("pc", row);
            _pcCsv.WriteLine(string.Join(",", new[]
            {
                V(row,"seq"),V(row,"frame"),V(row,"line"),V(row,"dot"),V(row,"cycles"),V(row,"pc"),V(row,"target"),V(row,"delta"),
                V(row,"a"),V(row,"x"),V(row,"y"),V(row,"s"),V(row,"d"),V(row,"db"),V(row,"pb"),V(row,"flags"),V(row,"opcode"),
                V(row,"w088b"),V(row,"w08a3"),V(row,"w0a75"),V(row,"w1a5b"),V(row,"w1b23"),V(row,"w1b25"),
                V(row,"vmain"),V(row,"vram_word"),V(row,"vram_mapped"),V(row,"vram_byte"),V(row,"vram_preview"),V(row,"dma_active"),V(row,"dma_channel"),V(row,"dma_summary")
            }));
        }

        public void Bus(IDictionary<string, object> row)
        {
            BusRows++;
            Json("bus", row);
            _busCsv.WriteLine(string.Join(",", new[]
            {
                V(row,"seq"),V(row,"frame"),V(row,"line"),V(row,"dot"),V(row,"pc"),V(row,"kind"),V(row,"address"),V(row,"value"),
                V(row,"vmain"),V(row,"vram_word"),V(row,"vram_mapped"),V(row,"vram_byte"),V(row,"vram_preview"),V(row,"dma_active"),V(row,"dma_channel"),V(row,"dma_summary")
            }));
        }

        private void Json(string type, IDictionary<string, object> row)
        {
            var fields = new List<KeyValuePair<string, object>>(row.Count + 1)
            {
                new KeyValuePair<string, object>("type", type)
            };
            fields.AddRange(row.OrderBy(pair => pair.Key, StringComparer.Ordinal));
            _jsonl.WriteLine(JsonLine.Object(fields));
        }

        private static string V(IDictionary<string, object> row, string key)
        {
            object value;
            return row.TryGetValue(key, out value) ? Csv(Convert.ToString(value, CultureInfo.InvariantCulture)) : string.Empty;
        }

        private static string Csv(string value)
        {
            value = value ?? string.Empty;
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private StreamWriter Writer(string name)
        {
            return new StreamWriter(Path.Combine(DirectoryPath, name), false, new UTF8Encoding(false), 65536);
        }

        public void Flush()
        {
            _jsonl.Flush();
            _pcCsv.Flush();
            _busCsv.Flush();
        }

        public void Dispose()
        {
            try { Flush(); } catch { }
            _jsonl.Dispose();
            _pcCsv.Dispose();
            _busCsv.Dispose();
        }
    }
}
