using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DKCTilemapInspector
{
    internal static class Json
    {
        public static string Escape(string value)
        {
            if (value == null) return "null";
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
        }

        public static string Value(object value)
        {
            if (value == null) return "null";
            if (value is string || value is char || value is Enum)
                return Escape(Convert.ToString(value, CultureInfo.InvariantCulture));
            if (value is bool) return (bool)value ? "true" : "false";
            if (value is float) return ((float)value).ToString("R", CultureInfo.InvariantCulture);
            if (value is double) return ((double)value).ToString("R", CultureInfo.InvariantCulture);
            if (value is decimal) return ((decimal)value).ToString(CultureInfo.InvariantCulture);
            if (value is IDictionary)
            {
                var pairs = new List<KeyValuePair<string, object>>();
                foreach (DictionaryEntry item in (IDictionary)value)
                    pairs.Add(new KeyValuePair<string, object>(Convert.ToString(item.Key, CultureInfo.InvariantCulture), item.Value));
                return Object(pairs);
            }
            if (value is IEnumerable && !(value is string))
            {
                var parts = new List<string>();
                foreach (var item in (IEnumerable)value) parts.Add(Value(item));
                return "[" + string.Join(",", parts) + "]";
            }
            if (value.GetType().IsPrimitive) return Convert.ToString(value, CultureInfo.InvariantCulture);
            return Escape(value.ToString());
        }

        public static string Object(IEnumerable<KeyValuePair<string, object>> values)
        {
            return "{" + string.Join(",", values.Select(p => Escape(p.Key) + ":" + Value(p.Value))) + "}";
        }
    }
}
