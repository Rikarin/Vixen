// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Vixen.Core.Serialization;

/// <summary>Reads and writes one type.</summary>
/// <typeparam name="T">The type.</typeparam>
/// <remarks>
///     <para>
///         An abstract class rather than an interface with static abstract members. The registry has
///         to hold these behind a common shape so that a member of type <c>Foo</c> can be serialised
///         from generic code that only knows <c>T</c>, and a static-abstract interface cannot be
///         stored. One virtual call per object, against a body that is writing fields, is not where
///         the time goes.
///     </para>
///     <para>
///         <see cref="Deserialize" /> takes its value by <see langword="ref" /> so a struct is filled
///         in place rather than copied back, and so a class can be reused instead of reallocated —
///         which is what makes loading a scene into an existing object graph possible later.
///     </para>
/// </remarks>
public abstract class DataSerializer<T> {
    /// <summary>Writes a value.</summary>
    /// <param name="writer">Where to write.</param>
    /// <param name="value">What to write.</param>
    public abstract void Serialize(ref SerializationWriter writer, in T value);

    /// <summary>Reads a value into <paramref name="value" />.</summary>
    /// <param name="reader">Where to read from.</param>
    /// <param name="value">What to fill. Created if it is a class and <see langword="null" />.</param>
    public abstract void Deserialize(ref SerializationReader reader, ref T value);
}

/// <summary>Where serializers are found.</summary>
/// <remarks>
///     <para>
///         Lookup by type is a single static field read — the JIT specialises
///         <see cref="Get{T}" /> per type and the field is the whole implementation. There is no
///         dictionary on the serialisation path.
///     </para>
///     <para>
///         Generated serializers register themselves from a <c>[ModuleInitializer]</c> the generator
///         emits per assembly, so referencing an assembly is enough to be able to read its types.
///         <c>Vixen.Core.Reflection</c> will subsume this with the wider type registry it needs
///         anyway; until then this is self-contained, which is what lets serialisation be tested
///         without it.
///     </para>
/// </remarks>
public static class SerializerRegistry {
    static readonly ConcurrentDictionary<Type, object> ByType = new();

    static SerializerRegistry() => BuiltInSerializers.Register();

    /// <summary>How many serializers are registered.</summary>
    public static int Count => ByType.Count;

    /// <summary>Registers a serializer, replacing any previous one for the same type.</summary>
    /// <typeparam name="T">The type it serialises.</typeparam>
    /// <param name="serializer">The serializer.</param>
    public static void Register<T>(DataSerializer<T> serializer) {
        ArgumentNullException.ThrowIfNull(serializer);
        SerializerHolder<T>.Instance = serializer;
        ByType[typeof(T)] = serializer;
    }

    /// <summary>Finds the serializer for a type.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <returns>Its serializer.</returns>
    /// <exception cref="SerializationException">No serializer is registered for it.</exception>
    public static DataSerializer<T> Get<T>() =>
        SerializerHolder<T>.Instance ?? throw new SerializationException(
            $"No serializer is registered for '{typeof(T)}'. Annotate the type with [DataContract] so "
            + "one is generated, or register a hand-written one with SerializerRegistry.Register."
        );

    /// <summary>Finds the serializer for a type, if there is one.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="serializer">Its serializer.</param>
    /// <returns><see langword="false" /> if none is registered.</returns>
    public static bool TryGet<T>([NotNullWhen(true)] out DataSerializer<T>? serializer) {
        serializer = SerializerHolder<T>.Instance;
        return serializer is not null;
    }

    /// <summary>Whether a type has a serializer.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <returns><see langword="true" /> if one is registered.</returns>
    public static bool IsRegistered<T>() => TryGet<T>(out _);

    /// <summary>Whether a type has a serializer. For tooling that has a <see cref="Type" /> and not a generic parameter.</summary>
    /// <param name="type">The type.</param>
    /// <returns><see langword="true" /> if one is registered.</returns>
    public static bool IsRegistered(Type type) {
        ArgumentNullException.ThrowIfNull(type);
        return ByType.ContainsKey(type);
    }

    /// <summary>Every registered type. For diagnostics and for the editor's type list.</summary>
    /// <returns>The types.</returns>
    public static IReadOnlyCollection<Type> RegisteredTypes => (IReadOnlyCollection<Type>)ByType.Keys;

    static class SerializerHolder<T> {
        internal static DataSerializer<T>? Instance;
    }
}
