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
    QuantizedSingle,
    Vector3,
    QuantizedVector3,
    Rotation
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
    /// <summary>The mathematics types the wire knows about, by name.</summary>
    public const string Vector3Type = "Vixen.Core.Mathematics.Vector3";

    /// <summary>The rotation type, which is sent smallest-three.</summary>
    public const string QuaternionType = "Vixen.Core.Mathematics.Quaternion";

    const string Codec = "global::Vixen.Net.Messaging.MathCodec";

    /// <summary>Reads what a symbol's type means on the wire.</summary>
    /// <param name="type">The type.</param>
    /// <param name="quantized">Whether the declaration carried a <c>[Quantize]</c>.</param>
    /// <returns>Its kind, or <see cref="WireKind.Unsupported" />.</returns>
    public static WireKind KindOf(ITypeSymbol type, bool quantized = false) {
        var kind = type.SpecialType switch {
            SpecialType.System_Boolean => WireKind.Boolean,
            SpecialType.System_Byte => WireKind.Byte,
            SpecialType.System_SByte => WireKind.SByte,
            SpecialType.System_Int16 => WireKind.Int16,
            SpecialType.System_UInt16 => WireKind.UInt16,
            SpecialType.System_Int32 => WireKind.Int32,
            SpecialType.System_UInt32 => WireKind.UInt32,
            SpecialType.System_Single => quantized ? WireKind.QuantizedSingle : WireKind.Single,
            _ => WireKind.Unsupported
        };

        if (kind != WireKind.Unsupported) {
            return kind;
        }

        return type.ToDisplayString() switch {
            Vector3Type => quantized ? WireKind.QuantizedVector3 : WireKind.Vector3,

            // A rotation has no range to declare: a unit quaternion's three sent components are in
            // [-1/√2, 1/√2] because they have to be, so only the width would be a choice and it is
            // not one worth an attribute yet.
            QuaternionType => quantized ? WireKind.Unsupported : WireKind.Rotation,
            _ => WireKind.Unsupported
        };
    }

    /// <summary>Whether a type is one <c>[Quantize]</c> means anything for.</summary>
    /// <param name="type">The type.</param>
    /// <returns>Whether a range can be declared for it.</returns>
    public static bool AcceptsQuantize(ITypeSymbol type) =>
        type.SpecialType == SpecialType.System_Single || type.ToDisplayString() == Vector3Type;

    /// <summary>The statement that writes a value.</summary>
    /// <param name="value">The value.</param>
    /// <param name="expression">The C# that reads it.</param>
    /// <returns>The statement.</returns>
    public static string Write(in WireValue value, string expression) =>
        value.Kind switch {
            WireKind.QuantizedSingle => $"writer.WriteQuantized({expression}, {value.RangeName});",
            // Called statically rather than as extension methods: generated code qualifies
            // everything, and an extension call cannot be qualified at the receiver — it would
            // depend on a `using` that the file has no other reason to carry.
            WireKind.QuantizedVector3 => $"{Codec}.WriteVector3(ref writer, {expression}, {value.RangeName});",
            WireKind.Vector3 => $"{Codec}.WriteVector3(ref writer, {expression});",
            WireKind.Rotation => $"{Codec}.WriteRotation(ref writer, {expression});",
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
            WireKind.QuantizedVector3 => $"{Codec}.TryReadVector3(ref reader, {value.RangeName}, out var {local})",
            WireKind.Vector3 => $"{Codec}.TryReadVector3(ref reader, out var {local})",
            WireKind.Rotation => $"{Codec}.TryReadRotation(ref reader, out var {local})",
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

    /// <summary>
    ///     The fixed-width fields a value occupies on the wire, as <c>WireLane</c> constructors.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="lanes">Where to add them.</param>
    /// <returns>Whether this value has a fixed layout at all.</returns>
    /// <remarks>
    ///     <para>
    ///         This is what lets a component be delta-encoded without a line of generated delta code:
    ///         <c>DeltaCodec</c> works on bits, so all it needs from the generator is the widths and
    ///         which of them arithmetic means something for.
    ///     </para>
    ///     <para>
    ///         <b>The order here has to match <see cref="Write" /> exactly</b>, which is why both are
    ///         driven from the same field list in the same order and neither is written out
    ///         separately. A vector is three lanes and a rotation is four, because that is what those
    ///         two write.
    ///     </para>
    /// </remarks>
    public static bool TryLanes(in WireValue value, List<string> lanes) {
        switch (value.Kind) {
            case WireKind.Boolean:
                lanes.Add(Lane(1, offset: false));

                return true;

            case WireKind.Byte or WireKind.SByte:
                lanes.Add(Lane(8, offset: false));

                return true;

            case WireKind.Int16 or WireKind.UInt16:
                lanes.Add(Lane(16, offset: true));

                return true;

            case WireKind.Int32 or WireKind.UInt32:
                lanes.Add(Lane(32, offset: true));

                return true;

            // A float's bits are not a number you may subtract. Sent whole when it changes, which is
            // exactly what a component that wanted a difference should have declared a range for.
            case WireKind.Single:
                lanes.Add(Lane(32, offset: false));

                return true;

            case WireKind.QuantizedSingle:
                lanes.Add(Lane(value.Bits, offset: true));

                return true;

            case WireKind.Vector3:
                for (var i = 0; i < 3; i++) {
                    lanes.Add(Lane(32, offset: false));
                }

                return true;

            case WireKind.QuantizedVector3:
                for (var i = 0; i < 3; i++) {
                    lanes.Add(Lane(value.Bits, offset: true));
                }

                return true;

            case WireKind.Rotation:
                lanes.Add(Lane(2, offset: false));

                for (var i = 0; i < 3; i++) {
                    lanes.Add($"new(global::Vixen.Net.Messaging.MathCodec.RotationBits, true)");
                }

                return true;

            default:
                return false;
        }
    }

    static string Lane(int bits, bool offset) =>
        $"new({bits.ToString(CultureInfo.InvariantCulture)}, {(offset ? "true" : "false")})";

    /// <summary>The declaration of the <c>QuantizeRange</c> a value needs, if it needs one.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The field declaration, or an empty string.</returns>
    public static string RangeField(in WireValue value) =>
        value.Kind is not (WireKind.QuantizedSingle or WireKind.QuantizedVector3)
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
