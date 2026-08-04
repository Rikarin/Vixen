// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Vixen.Live;

/// <summary>
///     The one-line encoding a <see cref="RealmSpec" /> and a <see cref="TransferTicket" /> travel in.
/// </summary>
/// <remarks>
///     <para>
///         <c>key=value;key=value</c>, with <c>%</c>, <c>;</c> and <c>=</c> percent-escaped inside
///         both halves. Deliberately not JSON: this assembly is the one a NativeAOT client links
///         (see the csproj), and the reflection-based serializer that would make JSON a one-liner is
///         precisely what does not survive trimming. A hand-written encoder for eleven scalar fields
///         is smaller than the source-generated context that would be the AOT-safe alternative, and
///         it is readable in a process listing — which is where a spec actually gets debugged.
///     </para>
///     <para>
///         Order is preserved on write and irrelevant on read. A duplicate key is an error rather
///         than a last-one-wins, because the two places that produce one of these are a placement
///         backend and a test, and a spec with two ports is a bug in whichever of them wrote it.
///     </para>
/// </remarks>
static class KeyValueText {
    /// <summary>Appends one pair, escaping both halves.</summary>
    /// <param name="text">What is being built.</param>
    /// <param name="key">The key.</param>
    /// <param name="value">The value. An empty one is written and reads back empty.</param>
    public static void Write(StringBuilder text, string key, string value) {
        if (text.Length > 0) {
            text.Append(';');
        }

        Escape(text, key);
        text.Append('=');
        Escape(text, value);
    }

    /// <summary>Reads every pair, or says which piece was not a pair.</summary>
    /// <param name="text">What <see cref="Write" /> produced.</param>
    /// <param name="fields">The pairs, on success.</param>
    /// <param name="error">Why not, otherwise.</param>
    /// <returns>Whether it read.</returns>
    public static bool TryRead(
        string? text,
        out Dictionary<string, string> fields,
        out string error
    ) {
        fields = new(StringComparer.Ordinal);
        error = "";

        if (string.IsNullOrWhiteSpace(text)) {
            error = "it is empty";

            return false;
        }

        foreach (var range in text.AsSpan().Split(';')) {
            var pair = text.AsSpan()[range];

            if (pair.IsEmpty) {
                continue;
            }

            var separator = pair.IndexOf('=');

            if (separator < 0) {
                error = $"`{pair}` is not a key=value pair";

                return false;
            }

            var key = Unescape(pair[..separator]);

            if (!fields.TryAdd(key, Unescape(pair[(separator + 1)..]))) {
                error = $"`{key}` appears twice";

                return false;
            }
        }

        if (fields.Count == 0) {
            error = "it holds no fields";

            return false;
        }

        return true;
    }

    static void Escape(StringBuilder text, string value) {
        foreach (var character in value) {
            switch (character) {
                case '%':
                    text.Append("%25");

                    break;

                case ';':
                    text.Append("%3B");

                    break;

                case '=':
                    text.Append("%3D");

                    break;

                default:
                    text.Append(character);

                    break;
            }
        }
    }

    static string Unescape(ReadOnlySpan<char> value) {
        if (value.IndexOf('%') < 0) {
            return value.ToString();
        }

        var text = new StringBuilder(value.Length);

        for (var index = 0; index < value.Length; index++) {
            if (value[index] == '%' && index + 2 < value.Length) {
                var pair = value.Slice(index + 1, 2);

                if (pair.Equals("25", StringComparison.OrdinalIgnoreCase)) {
                    text.Append('%');
                    index += 2;

                    continue;
                }

                if (pair.Equals("3B", StringComparison.OrdinalIgnoreCase)) {
                    text.Append(';');
                    index += 2;

                    continue;
                }

                if (pair.Equals("3D", StringComparison.OrdinalIgnoreCase)) {
                    text.Append('=');
                    index += 2;

                    continue;
                }
            }

            // A stray `%` is kept rather than rejected. This is not a URL and nothing else escapes
            // here, so the only way to produce one is to have written it, and losing it would make
            // the round trip lossy for a character no rule forbids.
            text.Append(value[index]);
        }

        return text.ToString();
    }
}
