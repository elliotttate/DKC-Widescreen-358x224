using System;
using System.Collections;
using System.Globalization;
using System.Text;

namespace DKCPlaytestRecorder
{
    internal static class JsonText
    {
        public static string Serialize(object value)
        {
            var text = new StringBuilder();
            Write(text, value);
            return text.ToString();
        }

        private static void Write(StringBuilder text, object value)
        {
            if (value == null) { text.Append("null"); return; }
            if (value is string s) { Quote(text, s); return; }
            if (value is bool b) { text.Append(b ? "true" : "false"); return; }
            if (value is IDictionary dictionary)
            {
                text.Append('{');
                var first = true;
                foreach (DictionaryEntry item in dictionary)
                {
                    if (!first) text.Append(',');
                    first = false;
                    Quote(text, Convert.ToString(item.Key, CultureInfo.InvariantCulture));
                    text.Append(':');
                    Write(text, item.Value);
                }
                text.Append('}');
                return;
            }
            if (value is IEnumerable enumerable)
            {
                text.Append('[');
                var first = true;
                foreach (var item in enumerable)
                {
                    if (!first) text.Append(',');
                    first = false;
                    Write(text, item);
                }
                text.Append(']');
                return;
            }
            if (value is IFormattable formattable)
            {
                text.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                return;
            }
            Quote(text, value.ToString());
        }

        private static void Quote(StringBuilder text, string value)
        {
            text.Append('"');
            foreach (var c in value ?? string.Empty)
            {
                switch (c)
                {
                    case '"': text.Append("\\\""); break;
                    case '\\': text.Append("\\\\"); break;
                    case '\n': text.Append("\\n"); break;
                    case '\r': text.Append("\\r"); break;
                    case '\t': text.Append("\\t"); break;
                    default:
                        if (c < 32) text.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                        else text.Append(c);
                        break;
                }
            }
            text.Append('"');
        }
    }
}
