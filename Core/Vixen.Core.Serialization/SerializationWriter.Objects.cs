// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Serialization;

public ref partial struct SerializationWriter {
    /// <summary>Writes a value type through its registered serializer.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="value">The value.</param>
    public void WriteValue<T>(in T value) where T : struct =>
        SerializerRegistry.Get<T>().Serialize(ref this, in value);

    /// <summary>Writes a reference, or a null marker.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="value">The value, which may be <see langword="null" />.</param>
    public void WriteReference<T>(T? value) where T : class {
        if (value is null) {
            WriteByte(0);
            return;
        }

        WriteByte(1);
        SerializerRegistry.Get<T>().Serialize(ref this, in value);
    }

    /// <summary>Writes a nullable value type.</summary>
    /// <typeparam name="T">The underlying type.</typeparam>
    /// <param name="value">The value.</param>
    public void WriteNullable<T>(T? value) where T : struct {
        if (value is null) {
            WriteByte(0);
            return;
        }

        WriteByte(1);
        var inner = value.Value;
        SerializerRegistry.Get<T>().Serialize(ref this, in inner);
    }

    /// <summary>Writes an array, element by element.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="value">The array, which may be <see langword="null" />.</param>
    public void WriteArray<T>(T[]? value) {
        if (value is null) {
            WriteVarUInt64(0);
            return;
        }

        WriteVarUInt64((ulong)value.Length + 1);
        var serializer = SerializerRegistry.Get<T>();

        foreach (var item in value) {
            WriteElement(serializer, item);
        }
    }

    /// <summary>Writes an array of a primitive type as one bulk copy.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="value">The array, which may be <see langword="null" />.</param>
    /// <remarks>
    ///     The path that makes a mesh cheap. Emitted by the generator only for element types whose
    ///     in-memory bytes are already the wire bytes on every target — the primitives — so it never
    ///     has to reason about padding or field order.
    /// </remarks>
    public void WriteBlittableArray<T>(T[]? value) where T : unmanaged {
        if (value is null) {
            WriteVarUInt64(0);
            return;
        }

        WriteVarUInt64((ulong)value.Length + 1);
        WriteBlittable<T>(value);
    }

    /// <summary>Writes a list, element by element.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="value">The list, which may be <see langword="null" />.</param>
    public void WriteList<T>(List<T>? value) {
        if (value is null) {
            WriteVarUInt64(0);
            return;
        }

        WriteVarUInt64((ulong)value.Count + 1);
        var serializer = SerializerRegistry.Get<T>();

        foreach (var item in value) {
            WriteElement(serializer, item);
        }
    }

    /// <summary>Writes a dictionary in its enumeration order.</summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="value">The dictionary, which may be <see langword="null" />.</param>
    /// <remarks>
    ///     <b>Enumeration order, which for <see cref="Dictionary{TKey,TValue}" /> depends on
    ///     insertion and removal history.</b> Two dictionaries holding the same pairs can therefore
    ///     serialise to different bytes, which matters because the content build's determinism gate
    ///     is a byte comparison. Where that matters, hold a <see cref="SortedDictionary{TKey,TValue}" />
    ///     or a sorted array instead — the serializer cannot sort for you without requiring every key
    ///     type to be comparable.
    /// </remarks>
    public void WriteDictionary<TKey, TValue>(Dictionary<TKey, TValue>? value) where TKey : notnull {
        if (value is null) {
            WriteVarUInt64(0);
            return;
        }

        WriteVarUInt64((ulong)value.Count + 1);
        var keys = SerializerRegistry.Get<TKey>();
        var values = SerializerRegistry.Get<TValue>();

        foreach (var (key, item) in value) {
            WriteElement(keys, key);
            WriteElement(values, item);
        }
    }

    void WriteElement<T>(DataSerializer<T> serializer, T item) {
        // The null flag is only spent where a null is possible. `typeof(T).IsValueType` is a JIT
        // constant for a specialised generic, so this branch does not exist in the compiled code for
        // an array of ints.
        if (!typeof(T).IsValueType) {
            if (item is null) {
                WriteByte(0);
                return;
            }

            WriteByte(1);
        }

        serializer.Serialize(ref this, in item);
    }
}
