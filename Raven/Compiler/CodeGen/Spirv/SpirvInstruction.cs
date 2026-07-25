using System.Globalization;
using System.Text;

namespace Vixen.Raven.CodeGen.Spirv;

/// <summary>What an operand word means, which is all that separates encoding from reading.</summary>
public enum SpirvOperandKind {
    /// <summary>A reference to a result id.</summary>
    Id,

    /// <summary>A literal number — one word, or two for a 64-bit value.</summary>
    Literal,

    /// <summary>A literal UTF-8 string, null-terminated and padded to a word boundary.</summary>
    String,

    /// <summary>A literal from one of the spec's enumerations; the name is kept for the listing.</summary>
    Enumerant
}

/// <summary>One operand of a <see cref="SpirvInstruction" />.</summary>
public readonly struct SpirvOperand {
    public SpirvOperandKind Kind { get; }

    /// <summary>The single word, for ids, enumerants and 32-bit literals.</summary>
    public uint Value { get; }

    /// <summary>The two words of a 64-bit literal, low word first.</summary>
    public ulong Wide { get; }

    /// <summary>The string, or an enumerant's spelling.</summary>
    public string? Text { get; }

    /// <summary>True for a literal that occupies two words.</summary>
    public bool IsWide { get; init; }

    /// <summary>How many words this operand occupies.</summary>
    public int WordCount =>
        Kind switch {
            SpirvOperandKind.String => StringWords(Text ?? string.Empty),
            SpirvOperandKind.Literal when IsWide => 2,
            _ => 1
        };

    SpirvOperand(SpirvOperandKind kind, uint value, ulong wide, string? text) {
        Kind = kind;
        Value = value;
        Wide = wide;
        Text = text;
    }

    public static SpirvOperand Id(uint id) => new(SpirvOperandKind.Id, id, 0, null);

    public static SpirvOperand Literal(uint value) => new(SpirvOperandKind.Literal, value, 0, null);

    public static SpirvOperand Literal(int value) => Literal(unchecked((uint)value));

    /// <summary>A 64-bit literal, which SPIR-V spells as two words, low first.</summary>
    public static SpirvOperand Literal64(ulong value) =>
        new(SpirvOperandKind.Literal, (uint)(value & 0xFFFFFFFF), value, null) { IsWide = true };

    public static SpirvOperand String(string value) => new(SpirvOperandKind.String, 0, 0, value);

    /// <summary>
    ///     A float literal. SPIR-V stores the bit pattern, but the listing keeps the
    ///     number, because <c>1065353216</c> tells a reader nothing about 1.0.
    /// </summary>
    public static SpirvOperand FloatLiteral(float value) =>
        new(
            SpirvOperandKind.Literal,
            BitConverter.SingleToUInt32Bits(value),
            0,
            value.ToString("R", CultureInfo.InvariantCulture)
        );

    public static SpirvOperand DoubleLiteral(double value) {
        var bits = BitConverter.DoubleToUInt64Bits(value);
        return new(
            SpirvOperandKind.Literal,
            (uint)(bits & 0xFFFFFFFF),
            bits,
            value.ToString("R", CultureInfo.InvariantCulture)
        ) { IsWide = true };
    }

    public static SpirvOperand Enumerant<T>(T value) where T : struct, Enum =>
        new(SpirvOperandKind.Enumerant, Convert.ToUInt32(value), 0, value.ToString());

    public override string ToString() =>
        Kind switch {
            SpirvOperandKind.Id => "%" + Value,
            SpirvOperandKind.String => "\"" + Text + "\"",
            SpirvOperandKind.Enumerant => Text ?? Value.ToString(),
            _ when Text is not null => Text,
            _ when IsWide => Wide.ToString(),
            _ => Value.ToString()
        };

    static void EncodeString(List<uint> words, string value) {
        var bytes = Encoding.UTF8.GetBytes(value);
        var padded = new byte[StringWords(value) * 4];
        bytes.CopyTo(padded, 0);

        for (var i = 0; i < padded.Length; i += 4) {
            words.Add(BitConverter.ToUInt32(padded, i));
        }
    }

    /// <summary>A literal string is UTF-8, null-terminated, padded with nulls to a word boundary.</summary>
    internal static int StringWords(string value) => Encoding.UTF8.GetByteCount(value) / 4 + 1;

    internal void Encode(List<uint> words) {
        switch (Kind) {
            case SpirvOperandKind.String:
                EncodeString(words, Text ?? string.Empty);
                break;

            case SpirvOperandKind.Literal when IsWide:
                words.Add((uint)(Wide & 0xFFFFFFFF));
                words.Add((uint)(Wide >> 32));
                break;

            default:
                words.Add(Value);
                break;
        }
    }
}

/// <summary>
///     One SPIR-V instruction, kept in the shape the spec describes: an opcode, an
///     optional result type, an optional result id, and operands. Holding it this way
///     rather than as raw words means the same object both encodes to the binary and
///     renders as the assembly listing, so the listing can never drift from what was
///     actually emitted.
/// </summary>
public sealed class SpirvInstruction(SpirvOp op, uint? resultType, uint? result, params SpirvOperand[] operands) {
    public SpirvOp Op { get; } = op;
    public uint? ResultType { get; } = resultType;
    public uint? Result { get; } = result;
    public IReadOnlyList<SpirvOperand> Operands { get; } = operands;

    public int WordCount =>
        1
        + (ResultType.HasValue ? 1 : 0)
        + (Result.HasValue ? 1 : 0)
        + operands.Sum(operand => operand.WordCount);

    public void Encode(List<uint> words) {
        words.Add((uint)(WordCount << 16) | (uint)Op);

        if (ResultType is { } resultTypeId) {
            words.Add(resultTypeId);
        }

        if (Result is { } resultId) {
            words.Add(resultId);
        }

        foreach (var operand in Operands) {
            operand.Encode(words);
        }
    }

    /// <summary>The assembly form, in the same shape <c>spirv-dis</c> prints.</summary>
    public override string ToString() {
        var builder = new StringBuilder();

        if (Result is { } resultId) {
            builder.Append('%').Append(resultId).Append(" = ");
        }

        builder.Append(SpirvOpNames.Of(Op));

        if (ResultType is { } typeId) {
            builder.Append(" %").Append(typeId);
        }

        foreach (var operand in Operands) {
            builder.Append(' ').Append(operand);
        }

        return builder.ToString();
    }
}
