// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;

namespace Vixen.Core.Serialization;

/// <summary>The short way to say it, for callers that have a whole value and want whole bytes.</summary>
/// <remarks>
///     The <see cref="SerializationWriter" /> and <see cref="SerializationReader" /> underneath are
///     the interesting part and remain public, because the asset pipeline writes many objects into
///     one buffer and needs to control where each begins. This is for everything else.
/// </remarks>
public static class Serializer {
    /// <summary>Serialises a value into a new array.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>The bytes.</returns>
    public static byte[] ToBytes<T>(in T value) {
        var buffer = new ArrayBufferWriter<byte>();
        Write(buffer, in value);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Serialises a value into a sink.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="destination">Where to write.</param>
    /// <param name="value">The value.</param>
    /// <returns>How many bytes were written.</returns>
    public static long Write<T>(IBufferWriter<byte> destination, in T value) {
        var writer = new SerializationWriter(destination);
        SerializerRegistry.Get<T>().Serialize(ref writer, in value);
        writer.Flush();
        return writer.BytesWritten;
    }

    /// <summary>Serialises a value into a fixed buffer.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="destination">Where to write.</param>
    /// <param name="value">The value.</param>
    /// <returns>How many bytes were written.</returns>
    /// <exception cref="SerializationException"><paramref name="destination" /> was too small.</exception>
    public static int Write<T>(Span<byte> destination, in T value) {
        var writer = new SerializationWriter(destination);
        SerializerRegistry.Get<T>().Serialize(ref writer, in value);
        return (int)writer.BytesWritten;
    }

    /// <summary>Deserialises a value.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="source">The bytes.</param>
    /// <returns>The value.</returns>
    public static T Read<T>(ReadOnlySpan<byte> source) {
        var reader = new SerializationReader(source);
        var value = default(T)!;
        SerializerRegistry.Get<T>().Deserialize(ref reader, ref value);
        return value;
    }

    /// <summary>Deserialises into an existing value, reusing it where the serializer can.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="source">The bytes.</param>
    /// <param name="value">What to fill.</param>
    public static void Read<T>(ReadOnlySpan<byte> source, ref T value) {
        var reader = new SerializationReader(source);
        SerializerRegistry.Get<T>().Deserialize(ref reader, ref value);
    }
}
