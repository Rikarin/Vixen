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

    /// <summary>Writes a reference by its run-time type, so a derived instance survives.</summary>
    /// <typeparam name="TBase">The type the member is declared as.</typeparam>
    /// <param name="value">The value, which may be <see langword="null" /> or any subtype.</param>
    /// <exception cref="SerializationException">The concrete type has no registered name.</exception>
    /// <remarks>
    ///     Costs a name in the stream — a short string, once per object — against
    ///     <see cref="WriteReference{T}" />'s single null byte. The generator picks between them by
    ///     whether the member's declared type can have a subtype at all: a sealed class cannot, so it
    ///     never pays.
    /// </remarks>
    public void WritePolymorphic<TBase>(TBase? value) where TBase : class {
        if (value is null) {
            WriteByte(0);
            return;
        }

        WriteByte(1);
        WriteDynamic(value);
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
        SerializerRegistry.TryGet<T>(out var serializer);

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
        SerializerRegistry.TryGet<T>(out var serializer);

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
        SerializerRegistry.TryGet<TKey>(out var keys);
        SerializerRegistry.TryGet<TValue>(out var values);

        foreach (var (key, item) in value) {
            WriteElement(keys, key);
            WriteElement(values, item);
        }
    }

    void WriteElement<T>(DataSerializer<T>? serializer, T item) {
        // The null flag is only spent where a null is possible, and the type name only where the
        // element type can have a subtype. Both tests are JIT constants for a specialised generic,
        // so neither branch exists in the compiled code for an array of ints.
        if (!typeof(T).IsValueType) {
            if (item is null) {
                WriteByte(0);
                return;
            }

            WriteByte(1);

            if (!typeof(T).IsSealed) {
                // Same rule an element gets as a member: a collection of a base type holds whatever
                // each element actually is, and the abstract element type may have no serializer of
                // its own at all.
                WriteDynamic(item);
                return;
            }
        }

        if (serializer is null) {
            throw new SerializationException(
                $"No serializer is registered for '{typeof(T)}'. Annotate the type with [DataContract] so "
                + "one is generated, or register a hand-written one with SerializerRegistry.Register."
            );
        }

        serializer.Serialize(ref this, in item);
    }

    void WriteDynamic(object item) {
        var type = item.GetType();

        if (!SerializerRegistry.TryGetAlias(type, out var alias)) {
            throw new SerializationException(
                $"'{type}' has no serialised name, so it cannot be written as an element of a collection "
                + "of a base type. Annotate it with [DataContract]."
            );
        }

        WriteString(alias);
        SerializerRegistry.TryGetByAlias(alias, out var serializer);
        serializer!.SerializeObject(ref this, item);
    }
}
