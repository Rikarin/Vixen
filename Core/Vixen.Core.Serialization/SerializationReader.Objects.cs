// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Serialization;

public ref partial struct SerializationReader {
    /// <summary>Reads a value type through its registered serializer.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <returns>The value.</returns>
    public T ReadValue<T>() where T : struct {
        var value = default(T);
        SerializerRegistry.Get<T>().Deserialize(ref this, ref value);
        return value;
    }

    /// <summary>Reads a reference, which may be null.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <returns>The value, or <see langword="null" />.</returns>
    public T? ReadReference<T>() where T : class {
        if (ReadByte() == 0) {
            return null;
        }

        var value = default(T)!;
        SerializerRegistry.Get<T>().Deserialize(ref this, ref value);
        return value;
    }

    /// <summary>Reads a nullable value type.</summary>
    /// <typeparam name="T">The underlying type.</typeparam>
    /// <returns>The value.</returns>
    public T? ReadNullable<T>() where T : struct {
        if (ReadByte() == 0) {
            return null;
        }

        var value = default(T);
        SerializerRegistry.Get<T>().Deserialize(ref this, ref value);
        return value;
    }

    /// <summary>Reads an array, element by element.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>The array, or <see langword="null" />.</returns>
    public T[]? ReadArray<T>() {
        if (!TryReadCount(out var count)) {
            return null;
        }

        var result = new T[count];
        var serializer = SerializerRegistry.Get<T>();

        for (var index = 0; index < count; index++) {
            result[index] = ReadElement(serializer);
        }

        return result;
    }

    /// <summary>Reads an array of a primitive type as one bulk copy.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>The array, or <see langword="null" />.</returns>
    public T[]? ReadBlittableArray<T>() where T : unmanaged {
        if (!TryReadCount(out var count)) {
            return null;
        }

        var result = new T[count];
        ReadBlittable<T>(result);
        return result;
    }

    /// <summary>Reads a list, element by element.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>The list, or <see langword="null" />.</returns>
    public List<T>? ReadList<T>() {
        if (!TryReadCount(out var count)) {
            return null;
        }

        var result = new List<T>(count);
        var serializer = SerializerRegistry.Get<T>();

        for (var index = 0; index < count; index++) {
            result.Add(ReadElement(serializer));
        }

        return result;
    }

    /// <summary>Reads a dictionary.</summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <returns>The dictionary, or <see langword="null" />.</returns>
    public Dictionary<TKey, TValue>? ReadDictionary<TKey, TValue>() where TKey : notnull {
        if (!TryReadCount(out var count)) {
            return null;
        }

        var result = new Dictionary<TKey, TValue>(count);
        var keys = SerializerRegistry.Get<TKey>();
        var values = SerializerRegistry.Get<TValue>();

        for (var index = 0; index < count; index++) {
            result[ReadElement(keys)] = ReadElement(values);
        }

        return result;
    }

    bool TryReadCount(out int count) {
        var encoded = ReadVarUInt64();

        if (encoded == 0) {
            count = 0;
            return false;
        }

        var value = encoded - 1;

        // A corrupt length would otherwise ask for an array larger than the file it came from, and
        // the allocation would succeed long enough to matter.
        if (value > (ulong)Remaining) {
            throw new SerializationException(
                $"A collection claims {value} elements but only {Remaining} bytes remain."
            );
        }

        count = (int)value;
        return true;
    }

    T ReadElement<T>(DataSerializer<T> serializer) {
        if (!typeof(T).IsValueType && ReadByte() == 0) {
            return default!;
        }

        var value = default(T)!;
        serializer.Deserialize(ref this, ref value);
        return value;
    }
}
