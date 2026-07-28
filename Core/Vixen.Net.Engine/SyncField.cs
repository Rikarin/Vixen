// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Net.Messaging;

namespace Vixen.Net.Engine;

/// <summary>One piece of a module's state, as the wire sees it.</summary>
/// <remarks>
///     <para>
///         The whole reason <c>SyncVar</c> gets delta encoding for nothing. A field declares the fixed
///         lanes it occupies, and a module's lanes are its fields' lanes end to end — which is exactly
///         what <see cref="DeltaCodec" /> needs and exactly what a generated
///         <c>[Replicated]</c> component provides. The two authoring styles meet here rather than in
///         two copies of the encoder.
///     </para>
///     <para>
///         <b>Fixed-width only.</b> A field whose size depends on its value cannot be part of a lane
///         layout, which is why <see cref="SyncList{T}" /> is not one of these and travels its own way.
///     </para>
/// </remarks>
public interface ISyncField {
    /// <summary>What it is called, for bandwidth attribution.</summary>
    string Name { get; }

    /// <summary>The fixed-width fields it occupies, in the order <see cref="Write" /> puts them.</summary>
    ReadOnlySpan<WireLane> Lanes { get; }

    /// <summary>Whether it has changed since the last <see cref="ClearDirty" />.</summary>
    bool IsDirty { get; }

    /// <summary>Writes the value.</summary>
    /// <param name="writer">Where the bits go.</param>
    void Write(ref BitWriter writer);

    /// <summary>Reads a value and takes it, raising whatever the field raises.</summary>
    /// <param name="reader">Where the bits come from.</param>
    /// <returns>Whether they were well-formed.</returns>
    bool Apply(ref BitReader reader);

    /// <summary>Marks it as sent.</summary>
    void ClearDirty();

    /// <summary>Gives it its name, once it is known which module it is in.</summary>
    /// <param name="name">The dotted path from the behaviour down to this field.</param>
    void Rename(string name);
}

/// <summary>How one type is put on the wire, for the fields that hold one.</summary>
/// <typeparam name="T">The type.</typeparam>
public interface ISyncCodec<T> {
    /// <summary>The lanes a value of this type occupies.</summary>
    ReadOnlySpan<WireLane> Lanes { get; }

    /// <summary>Writes one.</summary>
    /// <param name="writer">Where the bits go.</param>
    /// <param name="value">The value.</param>
    void Write(ref BitWriter writer, in T value);

    /// <summary>Reads one.</summary>
    /// <param name="reader">Where the bits come from.</param>
    /// <param name="value">The value.</param>
    /// <returns>Whether it was there.</returns>
    bool Read(ref BitReader reader, out T value);
}

/// <summary>The types a <see cref="SyncVar{T}" /> may hold.</summary>
/// <remarks>
///     <para>
///         <b>A closed set, resolved by a dictionary rather than by reflection.</b> iOS is NativeAOT
///         and trimming removes what it cannot see, so a codec discovered by walking a type at run
///         time is a codec that is not there in the product. The set is the same one
///         <c>Vixen.Net.Generators</c> understands for <c>[Replicated]</c> fields, and there is no
///         version of this where it is right for the two to disagree.
///     </para>
///     <para>
///         A game with a type of its own registers a codec for it before constructing the first
///         <c>SyncVar</c> that holds one — at start-up, beside the replication registry, where the
///         rest of the closed sets are declared.
///     </para>
/// </remarks>
public static class SyncCodecs {
    static readonly Dictionary<Type, object> Known = new() {
        [typeof(bool)] = new BoolCodec(),
        [typeof(byte)] = new ByteCodec(),
        [typeof(int)] = new Int32Codec(),
        [typeof(uint)] = new UInt32Codec(),
        [typeof(float)] = new SingleCodec(),
        [typeof(Vector3)] = new Vector3Codec(),
        [typeof(Quaternion)] = new RotationCodec()
    };

    /// <summary>Adds a codec for a type the engine does not know.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="codec">How to put it on the wire.</param>
    public static void Register<T>(ISyncCodec<T> codec) {
        ArgumentNullException.ThrowIfNull(codec);
        Known[typeof(T)] = codec;
    }

    /// <summary>Finds the codec for a type.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <returns>Its codec.</returns>
    /// <exception cref="NotSupportedException">There is not one, and nothing registered one.</exception>
    public static ISyncCodec<T> For<T>() =>
        Known.TryGetValue(typeof(T), out var codec)
            ? (ISyncCodec<T>)codec
            : throw new NotSupportedException(
                $"{typeof(T)} is not a type this wire knows. Register a codec for it with SyncCodecs.Register."
            );

    sealed class BoolCodec : ISyncCodec<bool> {
        static readonly WireLane[] Layout = [new("", 1, false)];

        public ReadOnlySpan<WireLane> Lanes => Layout;

        public void Write(ref BitWriter writer, in bool value) => writer.WriteBool(value);

        public bool Read(ref BitReader reader, out bool value) => reader.TryReadBool(out value);
    }

    sealed class ByteCodec : ISyncCodec<byte> {
        static readonly WireLane[] Layout = [new("", 8, false)];

        public ReadOnlySpan<WireLane> Lanes => Layout;

        public void Write(ref BitWriter writer, in byte value) => writer.Write(value, 8);

        public bool Read(ref BitReader reader, out byte value) {
            var read = reader.TryRead(8, out var raw);
            value = (byte)raw;

            return read;
        }
    }

    sealed class Int32Codec : ISyncCodec<int> {
        static readonly WireLane[] Layout = [new("", 32, true)];

        public ReadOnlySpan<WireLane> Lanes => Layout;

        public void Write(ref BitWriter writer, in int value) => writer.WriteInt32(value);

        public bool Read(ref BitReader reader, out int value) => reader.TryReadInt32(out value);
    }

    sealed class UInt32Codec : ISyncCodec<uint> {
        static readonly WireLane[] Layout = [new("", 32, true)];

        public ReadOnlySpan<WireLane> Lanes => Layout;

        public void Write(ref BitWriter writer, in uint value) => writer.WriteUInt32(value);

        public bool Read(ref BitReader reader, out uint value) => reader.TryReadUInt32(out value);
    }

    sealed class SingleCodec : ISyncCodec<float> {
        static readonly WireLane[] Layout = [new("", 32, false)];

        public ReadOnlySpan<WireLane> Lanes => Layout;

        public void Write(ref BitWriter writer, in float value) => writer.WriteSingle(value);

        public bool Read(ref BitReader reader, out float value) => reader.TryReadSingle(out value);
    }

    sealed class Vector3Codec : ISyncCodec<Vector3> {
        static readonly WireLane[] Layout = [new("X", 32, false), new("Y", 32, false), new("Z", 32, false)];

        public ReadOnlySpan<WireLane> Lanes => Layout;

        public void Write(ref BitWriter writer, in Vector3 value) => MathCodec.WriteVector3(ref writer, value);

        public bool Read(ref BitReader reader, out Vector3 value) => MathCodec.TryReadVector3(ref reader, out value);
    }

    sealed class RotationCodec : ISyncCodec<Quaternion> {
        static readonly WireLane[] Layout = [
            new("Dropped", 2, false),
            new("A", MathCodec.RotationBits, true),
            new("B", MathCodec.RotationBits, true),
            new("C", MathCodec.RotationBits, true)
        ];

        public ReadOnlySpan<WireLane> Lanes => Layout;

        public void Write(ref BitWriter writer, in Quaternion value) => MathCodec.WriteRotation(ref writer, value);

        public bool Read(ref BitReader reader, out Quaternion value) => MathCodec.TryReadRotation(ref reader, out value);
    }
}
