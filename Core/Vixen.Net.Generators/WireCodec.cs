// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Vixen.Net.Generators;

/// <summary>What a value is, for the purposes of putting it on a wire.</summary>
enum WireKind {
    Unsupported,
    Boolean,
    Byte,
    SByte,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Single,
    QuantizedSingle
}

/// <summary>One value to encode: a component's field, or an RPC's argument.</summary>
readonly record struct WireValue(string Name, WireKind Kind, float Min, float Max, int Bits) {
    /// <summary>The name of the generated <c>QuantizeRange</c> for this value, where it has one.</summary>
    public string RangeName => $"{Name}Range";
}

/// <summary>
///     The one place that knows how each supported type is written and read.
/// </summary>
/// <remarks>
///     Shared between the replication generator and the RPC one, because a component field and an RPC
///     argument are the same problem and there is no version of this where it is right for them to
///     disagree. A type added here is a type both understand.
/// </remarks>
static class WireCodec {
    /// <summary>Reads what a symbol's type means on the wire.</summary>
    /// <param name="type">The type.</param>
    /// <returns>Its kind, or <see cref="WireKind.Unsupported" />.</returns>
    public static WireKind KindOf(ITypeSymbol type) =>
        type.SpecialType switch {
            SpecialType.System_Boolean => WireKind.Boolean,
            SpecialType.System_Byte => WireKind.Byte,
            SpecialType.System_SByte => WireKind.SByte,
            SpecialType.System_Int16 => WireKind.Int16,
            SpecialType.System_UInt16 => WireKind.UInt16,
            SpecialType.System_Int32 => WireKind.Int32,
            SpecialType.System_UInt32 => WireKind.UInt32,
            SpecialType.System_Single => WireKind.Single,
            _ => WireKind.Unsupported
        };

    /// <summary>The statement that writes a value.</summary>
    /// <param name="value">The value.</param>
    /// <param name="expression">The C# that reads it.</param>
    /// <returns>The statement.</returns>
    public static string Write(in WireValue value, string expression) =>
        value.Kind switch {
            WireKind.QuantizedSingle => $"writer.WriteQuantized({expression}, {value.RangeName});",
            WireKind.Single => $"writer.WriteSingle({expression});",
            WireKind.Boolean => $"writer.WriteBool({expression});",
            WireKind.Byte => $"writer.Write({expression}, 8);",
            WireKind.SByte => $"writer.Write((uint)(byte){expression}, 8);",
            WireKind.Int16 => $"writer.Write((uint)(ushort){expression}, 16);",
            WireKind.UInt16 => $"writer.Write({expression}, 16);",
            WireKind.Int32 => $"writer.WriteInt32({expression});",
            _ => $"writer.WriteUInt32({expression});"
        };

    /// <summary>The condition that reads a value into a local.</summary>
    /// <param name="value">The value.</param>
    /// <param name="local">What to call the local.</param>
    /// <returns>The condition.</returns>
    public static string Read(in WireValue value, string local) =>
        value.Kind switch {
            WireKind.QuantizedSingle => $"reader.TryReadQuantized({value.RangeName}, out var {local})",
            WireKind.Single => $"reader.TryReadSingle(out var {local})",
            WireKind.Boolean => $"reader.TryReadBool(out var {local})",
            WireKind.Byte or WireKind.SByte => $"reader.TryRead(8, out var {local})",
            WireKind.Int16 or WireKind.UInt16 => $"reader.TryRead(16, out var {local})",
            WireKind.Int32 => $"reader.TryReadInt32(out var {local})",
            _ => $"reader.TryReadUInt32(out var {local})"
        };

    /// <summary>The expression that turns the local a read produced back into the declared type.</summary>
    /// <param name="value">The value.</param>
    /// <param name="local">The local a read produced.</param>
    /// <returns>The expression.</returns>
    public static string Convert(in WireValue value, string local) =>
        value.Kind switch {
            WireKind.Byte => $"(byte){local}",
            WireKind.SByte => $"(sbyte)(byte){local}",
            WireKind.Int16 => $"(short)(ushort){local}",
            WireKind.UInt16 => $"(ushort){local}",
            _ => local
        };

    /// <summary>The declaration of the <c>QuantizeRange</c> a value needs, if it needs one.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The field declaration, or an empty string.</returns>
    public static string RangeField(in WireValue value) =>
        value.Kind != WireKind.QuantizedSingle
            ? string.Empty
            : $"    static readonly global::Vixen.Net.Messaging.QuantizeRange {value.RangeName} = "
            + $"new({Literal(value.Min)}, {Literal(value.Max)}, {value.Bits});";

    /// <summary>A float as C# source.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Its literal.</returns>
    public static string Literal(float value) => value.ToString("R", CultureInfo.InvariantCulture) + "f";

    /// <summary>The stable id of a name: 32-bit FNV-1a.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The id, never zero.</returns>
    /// <remarks>
    ///     The same function the runtime computes in <c>ReplicationRegistry.HashTypeName</c> and
    ///     <c>RpcMethod.Hash</c>. A generator cannot reference the runtime — it targets
    ///     netstandard2.1 and runs inside the compiler — so there are two implementations and a test
    ///     that says they are one function.
    /// </remarks>
    public static uint Hash(string name) {
        var hash = 2166136261u;

        foreach (var character in name) {
            hash ^= character;
            hash *= 16777619u;
        }

        return hash == 0 ? 1u : hash;
    }
}
