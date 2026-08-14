using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PIXMYD_Nav.Core.Json
{
    /// <summary>
    /// A minimal JSON reader, to match the hand-rolled writer in PointSet.cs.
    ///
    /// The plugin has only ever produced JSON. The return leg -- reading a
    /// capture.json back off a phone -- is the first time it has to consume any,
    /// and RULES.md section 3 is explicit that a dependency is not the way to save
    /// fifty lines when the existing code hand-rolls its JSON and its glTF writer
    /// to stay dependency-free.
    ///
    /// Scope is deliberately the parsing side of what the writer emits: objects,
    /// arrays, strings with the standard escapes, numbers, the three literals.
    /// It is strict -- a trailing comma or a NaN is an error, not a repair --
    /// because a file this cannot read should produce one clear message rather
    /// than a plausible-looking placement 300 mm out.
    ///
    /// Pure. In WriterTests.csproj.
    /// </summary>
    public sealed class JsonValue
    {
        public enum Kind { Object, Array, String, Number, Bool, Null }

        public Kind Type;
        public Dictionary<string, JsonValue> Members;
        public List<JsonValue> Items;
        public string Text;
        public double Number;
        public bool Bool;

        public bool IsNull { get { return Type == Kind.Null; } }

        /// <summary>Member by name, or null. Never throws on a missing key -- an
        /// absent optional field is the normal case in every contract here.</summary>
        public JsonValue this[string name]
        {
            get
            {
                if (Type != Kind.Object || Members == null) return null;
                JsonValue value;
                return Members.TryGetValue(name, out value) ? value : null;
            }
        }

        public int Count
        {
            get
            {
                if (Type == Kind.Array && Items != null) return Items.Count;
                if (Type == Kind.Object && Members != null) return Members.Count;
                return 0;
            }
        }

        public JsonValue At(int index)
        {
            if (Type != Kind.Array || Items == null || index < 0 || index >= Items.Count) return null;
            return Items[index];
        }

        public string AsString(string fallback)
        {
            return Type == Kind.String ? Text : fallback;
        }

        public double AsNumber(double fallback)
        {
            return Type == Kind.Number ? Number : fallback;
        }

        public bool AsBool(bool fallback)
        {
            return Type == Kind.Bool ? Bool : fallback;
        }

        /// <summary>An array of numbers, or null when the shape is wrong.</summary>
        public double[] AsVector(int length)
        {
            if (Type != Kind.Array || Items == null || Items.Count != length) return null;
            var result = new double[length];
            for (int i = 0; i < length; i++)
            {
                if (Items[i].Type != Kind.Number) return null;
                result[i] = Items[i].Number;
            }
            return result;
        }

        public string[] AsStringArray()
        {
            if (Type != Kind.Array || Items == null) return new string[0];
            var result = new List<string>();
            foreach (JsonValue item in Items)
                if (item.Type == Kind.String) result.Add(item.Text);
            return result.ToArray();
        }
    }

    public class JsonParseException : Exception
    {
        public JsonParseException(string message) : base(message) { }
    }

    public static class JsonReader
    {
        public static JsonValue Parse(string text)
        {
            if (text == null) throw new JsonParseException("The file was empty.");
            int index = 0;
            SkipWhitespace(text, ref index);
            JsonValue value = ParseValue(text, ref index, 0);
            SkipWhitespace(text, ref index);
            if (index != text.Length)
                throw new JsonParseException("Unexpected text after the end of the JSON document.");
            return value;
        }

        private static JsonValue ParseValue(string text, ref int index, int depth)
        {
            // A bounded depth stops a hostile or corrupt file turning into a
            // stack overflow, which on a plugin takes Navisworks down with it.
            if (depth > 64) throw new JsonParseException("JSON nested too deeply.");
            if (index >= text.Length) throw new JsonParseException("The JSON document ended early.");

            char c = text[index];
            switch (c)
            {
                case '{': return ParseObject(text, ref index, depth);
                case '[': return ParseArray(text, ref index, depth);
                case '"': return new JsonValue { Type = JsonValue.Kind.String, Text = ParseString(text, ref index) };
                case 't': Expect(text, ref index, "true"); return new JsonValue { Type = JsonValue.Kind.Bool, Bool = true };
                case 'f': Expect(text, ref index, "false"); return new JsonValue { Type = JsonValue.Kind.Bool, Bool = false };
                case 'n': Expect(text, ref index, "null"); return new JsonValue { Type = JsonValue.Kind.Null };
                default: return ParseNumber(text, ref index);
            }
        }

        private static JsonValue ParseObject(string text, ref int index, int depth)
        {
            var value = new JsonValue
            {
                Type = JsonValue.Kind.Object,
                Members = new Dictionary<string, JsonValue>(StringComparer.Ordinal)
            };
            index++; // '{'
            SkipWhitespace(text, ref index);
            if (index < text.Length && text[index] == '}') { index++; return value; }

            while (true)
            {
                SkipWhitespace(text, ref index);
                if (index >= text.Length || text[index] != '"')
                    throw new JsonParseException("Expected a member name.");
                string name = ParseString(text, ref index);

                SkipWhitespace(text, ref index);
                if (index >= text.Length || text[index] != ':')
                    throw new JsonParseException("Expected ':' after member name '" + name + "'.");
                index++;

                SkipWhitespace(text, ref index);
                value.Members[name] = ParseValue(text, ref index, depth + 1);

                SkipWhitespace(text, ref index);
                if (index >= text.Length) throw new JsonParseException("The JSON object was not closed.");
                if (text[index] == ',') { index++; continue; }
                if (text[index] == '}') { index++; return value; }
                throw new JsonParseException("Expected ',' or '}' in an object.");
            }
        }

        private static JsonValue ParseArray(string text, ref int index, int depth)
        {
            var value = new JsonValue { Type = JsonValue.Kind.Array, Items = new List<JsonValue>() };
            index++; // '['
            SkipWhitespace(text, ref index);
            if (index < text.Length && text[index] == ']') { index++; return value; }

            while (true)
            {
                SkipWhitespace(text, ref index);
                value.Items.Add(ParseValue(text, ref index, depth + 1));

                SkipWhitespace(text, ref index);
                if (index >= text.Length) throw new JsonParseException("The JSON array was not closed.");
                if (text[index] == ',') { index++; continue; }
                if (text[index] == ']') { index++; return value; }
                throw new JsonParseException("Expected ',' or ']' in an array.");
            }
        }

        private static string ParseString(string text, ref int index)
        {
            index++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (index >= text.Length) throw new JsonParseException("A string was not closed.");
                char c = text[index++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }

                if (index >= text.Length) throw new JsonParseException("A string ended inside an escape.");
                char escape = text[index++];
                switch (escape)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (index + 4 > text.Length) throw new JsonParseException("A \\u escape was truncated.");
                        sb.Append((char)Convert.ToInt32(text.Substring(index, 4), 16));
                        index += 4;
                        break;
                    default:
                        throw new JsonParseException("Unknown escape '\\" + escape + "'.");
                }
            }
        }

        private static JsonValue ParseNumber(string text, ref int index)
        {
            int start = index;
            if (index < text.Length && (text[index] == '-' || text[index] == '+')) index++;
            while (index < text.Length)
            {
                char c = text[index];
                bool part = (c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-';
                if (!part) break;
                index++;
            }
            if (index == start) throw new JsonParseException("Expected a value.");

            string token = text.Substring(start, index - start);
            double result;
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
                throw new JsonParseException("'" + token + "' is not a number.");
            // JSON has no NaN or Infinity, and a coordinate that is one would
            // place geometry nowhere at all.
            if (double.IsNaN(result) || double.IsInfinity(result))
                throw new JsonParseException("'" + token + "' is not a finite number.");

            return new JsonValue { Type = JsonValue.Kind.Number, Number = result };
        }

        private static void Expect(string text, ref int index, string literal)
        {
            if (index + literal.Length > text.Length ||
                string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
                throw new JsonParseException("Expected '" + literal + "'.");
            index += literal.Length;
        }

        private static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length)
            {
                char c = text[index];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') index++;
                else break;
            }
        }
    }
}
