using System.Globalization;
using System.Text;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;

namespace Vixen.Raven.Binding;

/// <summary>
/// Turns a literal token's source text into a typed value. The lexer keeps
/// tokens verbatim (quotes, escapes, suffixes and all), so this is where
/// <c>42f</c> becomes a <c>float</c>.
/// </summary>
/// <remarks>
/// A string literal has no runtime type: strings do not exist on a GPU. It
/// still parses, because attribute arguments such as
/// <c>[Semantic("SV_Target")]</c> are compile-time metadata read straight off
/// the syntax. In expression position the binder rejects it.
/// </remarks>
public static class LiteralParser {
    const string IntegerSuffixes = "uU";
    const string AllSuffixes = "uUfFdDmM";

    /// <summary>The type and value a literal expression denotes.</summary>
    public static (TypeSymbol Type, object? Value) Parse(LiteralExpressionSyntax syntax) => syntax.Kind switch {
        SyntaxKind.TrueLiteralExpression => (BuiltInTypes.Bool, true),
        SyntaxKind.FalseLiteralExpression => (BuiltInTypes.Bool, false),
        // `default` takes its type from context, which this phase does not track;
        // the error type lets it flow anywhere without reporting.
        SyntaxKind.DefaultLiteralExpression => (ErrorTypeSymbol.Instance, null),
        // Metadata only — the value is here for attribute readers, the type is
        // deliberately absent.
        SyntaxKind.StringLiteralExpression => (ErrorTypeSymbol.Instance, ParseString(syntax.Token.Text)),
        _ => ParseNumeric(syntax.Token.Text)
    };

    static (TypeSymbol Type, object? Value) ParseNumeric(string text) {
        var digits = text.Replace("_", string.Empty);

        // A radix prefix takes only integer suffixes: in `0x1F` the trailing `F`
        // is a digit, not a float marker.
        if (digits.Length > 2 && digits[0] == '0' && digits[1] is 'x' or 'X' or 'b' or 'B') {
            var radix = digits[1] is 'x' or 'X' ? 16 : 2;
            var (prefixed, prefixedSuffix) = SplitSuffix(digits[2..], IntegerSuffixes);
            return FromInteger(ParseUnsigned(prefixed, radix), prefixedSuffix);
        }

        var (body, suffix) = SplitSuffix(digits, AllSuffixes);

        var isReal = suffix is "f" or "d" or "m"
            || body.Contains('.')
            || body.Contains('e', StringComparison.OrdinalIgnoreCase);

        if (isReal) {
            var value = double.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0d;

            // Each branch boxes its own type: without the cast the conditional
            // would unify on `double` and a `float` literal would carry a double.
            return suffix == "f"
                ? (BuiltInTypes.Float, (object)(float)value)
                : (BuiltInTypes.Double, (object)value);
        }

        return FromInteger(ulong.TryParse(body, out var integer) ? integer : 0UL, suffix);
    }

    static (TypeSymbol Type, object? Value) FromInteger(ulong value, string suffix) {
        if (suffix.Contains('u')) {
            return (BuiltInTypes.UInt, (uint)value);
        }

        if (suffix == "f") {
            return (BuiltInTypes.Float, (float)value);
        }

        if (suffix is "d" or "m") {
            return (BuiltInTypes.Double, (double)value);
        }

        // An untyped integer literal is `int`; one too large for it takes the
        // unsigned shape, which is as far as a GPU's integers go.
        return value <= int.MaxValue
            ? (BuiltInTypes.Int, (object)(int)value)
            : (BuiltInTypes.UInt, (object)(uint)value);
    }

    static ulong ParseUnsigned(string digits, int radix) {
        ulong value = 0;
        foreach (var character in digits) {
            var digit = Convert.ToInt32(character.ToString(), 16);
            value = value * (ulong)radix + (ulong)digit;
        }

        return value;
    }

    static (string Body, string Suffix) SplitSuffix(string text, string suffixCharacters) {
        var end = text.Length;
        while (end > 0 && suffixCharacters.Contains(text[end - 1])) {
            end--;
        }

        return (text[..end], text[end..].ToLowerInvariant());
    }

    static string ParseString(string text) => text.Length < 2 ? string.Empty : Unescape(text[1..^1]);

    static string Unescape(string text) {
        if (!text.Contains('\\')) {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++) {
            if (text[i] != '\\' || i + 1 >= text.Length) {
                builder.Append(text[i]);
                continue;
            }

            var escape = text[++i];
            switch (escape) {
                case 'n': builder.Append('\n'); break;
                case 't': builder.Append('\t'); break;
                case 'r': builder.Append('\r'); break;
                case '0': builder.Append('\0'); break;
                case 'a': builder.Append('\a'); break;
                case 'b': builder.Append('\b'); break;
                case 'f': builder.Append('\f'); break;
                case 'v': builder.Append('\v'); break;
                case 'u' when i + 4 < text.Length: {
                    var hex = text.Substring(i + 1, 4);
                    if (ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) {
                        builder.Append((char)code);
                        i += 4;
                    }
                    else {
                        builder.Append(escape);
                    }

                    break;
                }
                default: builder.Append(escape); break;
            }
        }

        return builder.ToString();
    }
}
