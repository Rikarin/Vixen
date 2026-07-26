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

    /// <summary>Reads a reference written by its run-time type.</summary>
    /// <typeparam name="TBase">The type the member is declared as.</typeparam>
    /// <returns>The value, or <see langword="null" />.</returns>
    /// <exception cref="SerializationException">The name is unknown, or names a type that is not a <typeparamref name="TBase" />.</exception>
    public TBase? ReadPolymorphic<TBase>() where TBase : class {
        if (ReadByte() == 0) {
            return null;
        }

        return ReadDynamic<TBase>();
    }

    /// <summary>Reads an array, element by element.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>The array, or <see langword="null" />.</returns>
    public T[]? ReadArray<T>() {
        if (!TryReadCount(out var count)) {
            return null;
        }

        var result = new T[count];
        SerializerRegistry.TryGet<T>(out var serializer);

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
        SerializerRegistry.TryGet<T>(out var serializer);

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
        SerializerRegistry.TryGet<TKey>(out var keys);
        SerializerRegistry.TryGet<TValue>(out var values);

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

    T ReadElement<T>(DataSerializer<T>? serializer) {
        if (!typeof(T).IsValueType) {
            if (ReadByte() == 0) {
                return default!;
            }

            if (!typeof(T).IsSealed) {
                return ReadDynamic<T>();
            }
        }

        if (serializer is null) {
            throw new SerializationException(
                $"No serializer is registered for '{typeof(T)}'. Annotate the type with [DataContract] so "
                + "one is generated, or register a hand-written one with SerializerRegistry.Register."
            );
        }

        var value = default(T)!;
        serializer.Deserialize(ref this, ref value);
        return value;
    }

    T ReadDynamic<T>() {
        var alias = ReadString()!;

        if (!SerializerRegistry.TryGetByAlias(alias, out var serializer)) {
            throw new SerializationException(
                $"The data names type '{alias}', which nothing in this build claims. Either the assembly "
                + "declaring it is not loaded, or it was renamed without a [DataAlias] recording the old name."
            );
        }

        // Before deserialising, not after. The alias could name anything, and reading a Texture's
        // bytes as a Mesh fails on whatever runs out first — which is how this check was originally
        // written and why the error said "the data is truncated" about data that was intact.
        if (!typeof(T).IsAssignableFrom(serializer.SerializedType)) {
            throw new SerializationException(
                $"The data names type '{alias}' ({serializer.SerializedType}) where a '{typeof(T)}' was expected."
            );
        }

        return (T)serializer.DeserializeObject(ref this);
    }
}
