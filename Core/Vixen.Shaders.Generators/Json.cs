// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace Vixen.Shaders.Generators;

/// <summary>
///     A minimal JSON reader, because an analyzer cannot assume one.
/// </summary>
/// <remarks>
///     <para>
///         A source generator runs inside the C# compiler's assembly load context, which contains
///         Roslyn and nothing this project brings with it — a <c>PackageReference</c> to
///         <c>System.Text.Json</c> would compile and then fail to load at build time in the consuming
///         project, which is the worst place to find out. So the reader is here, and it is the known
///         price of reading reflection as <c>AdditionalFiles</c> (docs/plan/07 § Generated C# bindings
///         says as much).
///     </para>
///     <para>
///         Deliberately small: it reads the subset Raven emits — objects, arrays, strings, numbers,
///         <c>true</c>/<c>false</c>/<c>null</c> — with no streaming, no comments and no big-number
///         handling. The input is a file this repository's own compiler wrote moments earlier, so
///         the interesting failure is a *schema* change, which surfaces as a missing member rather
///         than as malformed text.
///     </para>
/// </remarks>
abstract class JsonValue {
    public static JsonValue Parse(string text) {
        var reader = new Reader(text);
        var value = reader.ReadValue();
        reader.SkipWhitespace();
        return value;
    }

    /// <summary>The member of an object, or <see cref="JsonNull" /> when absent.</summary>
    public virtual JsonValue this[string name] => JsonNull.Instance;

    public virtual IReadOnlyList<JsonValue> Items => [];

    public virtual string? AsString() => null;

    public virtual double? AsNumber() => null;

    public virtual bool? AsBool() => null;

    public int AsInt(int fallback = 0) => AsNumber() is { } number ? (int)number : fallback;

    public string AsString(string fallback) => AsString() ?? fallback;

    public bool AsBool(bool fallback) => AsBool() ?? fallback;

    public bool IsNull => this is JsonNull;

    sealed class Reader(string text) {
        int position;

        public JsonValue ReadValue() {
            SkipWhitespace();

            if (position >= text.Length) {
                return JsonNull.Instance;
            }

            return text[position] switch {
                '{' => ReadObject(),
                '[' => ReadArray(),
                '"' => new JsonString(ReadString()),
                't' or 'f' => ReadKeyword(),
                'n' => ReadNull(),
                _ => ReadNumber()
            };
        }

        public void SkipWhitespace() {
            while (position < text.Length && char.IsWhiteSpace(text[position])) {
                position++;
            }
        }

        JsonObject ReadObject() {
            var members = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            position++;

            while (true) {
                SkipWhitespace();

                if (position >= text.Length || text[position] == '}') {
                    position++;
                    return new JsonObject(members);
                }

                var name = ReadString();
                SkipWhitespace();

                // The colon; anything else here is malformed and the value read below will be too,
                // which fails the build with the file named rather than silently mis-parsing.
                if (position < text.Length && text[position] == ':') {
                    position++;
                }

                members[name] = ReadValue();
                SkipWhitespace();

                if (position < text.Length && text[position] == ',') {
                    position++;
                }
            }
        }

        JsonArray ReadArray() {
            var items = new List<JsonValue>();
            position++;

            while (true) {
                SkipWhitespace();

                if (position >= text.Length || text[position] == ']') {
                    position++;
                    return new JsonArray(items);
                }

                items.Add(ReadValue());
                SkipWhitespace();

                if (position < text.Length && text[position] == ',') {
                    position++;
                }
            }
        }

        string ReadString() {
            var builder = new StringBuilder();
            position++;

            while (position < text.Length && text[position] != '"') {
                var c = text[position++];

                if (c != '\\') {
                    builder.Append(c);
                    continue;
                }

                var escape = position < text.Length ? text[position++] : '"';
                switch (escape) {
                    case 'n': builder.Append('\n'); break;
                    case 't': builder.Append('\t'); break;
                    case 'r': builder.Append('\r'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'u':
                        builder.Append((char)ushort.Parse(text.AsSpan(position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        position += 4;
                        break;
                    default: builder.Append(escape); break;
                }
            }

            position++;
            return builder.ToString();
        }

        JsonBool ReadKeyword() {
            var isTrue = text[position] == 't';
            position += isTrue ? 4 : 5;
            return new JsonBool(isTrue);
        }

        JsonNull ReadNull() {
            position += 4;
            return JsonNull.Instance;
        }

        JsonValue ReadNumber() {
            var start = position;

            while (position < text.Length && (char.IsDigit(text[position]) || "+-.eE".Contains(text[position]))) {
                position++;
            }

            var span = text.AsSpan(start, position - start);
            return double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? new JsonNumber(value)
                : JsonNull.Instance;
        }
    }
}

sealed class JsonObject(Dictionary<string, JsonValue> members) : JsonValue {
    public override JsonValue this[string name] => members.TryGetValue(name, out var value) ? value : JsonNull.Instance;
}

sealed class JsonArray(List<JsonValue> items) : JsonValue {
    public override IReadOnlyList<JsonValue> Items => items;
}

sealed class JsonString(string value) : JsonValue {
    public override string AsString() => value;
}

sealed class JsonNumber(double value) : JsonValue {
    public override double? AsNumber() => value;

    public override string AsString() => value.ToString(CultureInfo.InvariantCulture);
}

sealed class JsonBool(bool value) : JsonValue {
    public override bool? AsBool() => value;

    public override string AsString() => value ? "true" : "false";
}

sealed class JsonNull : JsonValue {
    public static readonly JsonNull Instance = new();

    JsonNull() { }
}
