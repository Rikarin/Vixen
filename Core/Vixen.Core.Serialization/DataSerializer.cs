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
public abstract class DataSerializer<T> : DataSerializer {
    /// <summary>Writes a value.</summary>
    /// <param name="writer">Where to write.</param>
    /// <param name="value">What to write.</param>
    public abstract void Serialize(ref SerializationWriter writer, in T value);

    /// <summary>Reads a value into <paramref name="value" />.</summary>
    /// <param name="reader">Where to read from.</param>
    /// <param name="value">What to fill. Created if it is a class and <see langword="null" />.</param>
    public abstract void Deserialize(ref SerializationReader reader, ref T value);

    /// <inheritdoc />
    public sealed override Type SerializedType => typeof(T);

    /// <inheritdoc />
    public sealed override void SerializeObject(ref SerializationWriter writer, object value) {
        var typed = (T)value;
        Serialize(ref writer, in typed);
    }

    /// <inheritdoc />
    public sealed override object DeserializeObject(ref SerializationReader reader) {
        var value = default(T)!;
        Deserialize(ref reader, ref value);
        return value!;
    }
}

/// <summary>The non-generic face of a serializer, for when the type is only known at run time.</summary>
/// <remarks>
///     <para>
///         Polymorphism is the reason this exists. A field typed as a base class holding a derived
///         instance has to be written by the *derived* type's serializer, and the code doing the
///         writing knows only the base — so somewhere the dispatch has to go through a shape that is
///         not generic over the concrete type.
///     </para>
///     <para>
///         The <c>object</c> in these signatures would box a struct, which is why nothing uses them
///         for one: polymorphism applies to reference types, where the value is already a reference
///         and the cast is free. Value types go through
///         <see cref="DataSerializer{T}.Serialize" /> and never touch this.
///     </para>
/// </remarks>
public abstract class DataSerializer {
    /// <summary>What this serializer reads and writes.</summary>
    public abstract Type SerializedType { get; }

    /// <summary>Writes a value whose type is only known at run time.</summary>
    /// <param name="writer">Where to write.</param>
    /// <param name="value">What to write. Must be of <see cref="SerializedType" />.</param>
    public abstract void SerializeObject(ref SerializationWriter writer, object value);

    /// <summary>Reads a value whose type is only known at run time.</summary>
    /// <param name="reader">Where to read from.</param>
    /// <returns>The value.</returns>
    public abstract object DeserializeObject(ref SerializationReader reader);
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
    static readonly ConcurrentDictionary<string, DataSerializer> ByAlias = new(StringComparer.Ordinal);
    static readonly ConcurrentDictionary<Type, string> AliasOfType = new();

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

        // The chunk header carries this number and nothing else about the type, so a loader that
        // has an id and no static type — anything walking a dependency graph — needs the way back.
        ByTypeId[Storage.ContentHash.TypeId(typeof(T))] = serializer;
    }

    static readonly ConcurrentDictionary<ulong, DataSerializer> ByTypeId = new();

    /// <summary>Finds the serializer for the type a chunk header names.</summary>
    /// <param name="typeId">The identifier from the header.</param>
    /// <param name="serializer">Its serializer.</param>
    /// <returns><see langword="false" /> if nothing registered claims that type.</returns>
    /// <remarks>
    ///     What a content loader uses to deserialise a dependency it has only an id for. A chunk
    ///     whose type is not registered in this process is not corrupt — it belongs to an assembly
    ///     that was not loaded — so this answers false rather than throwing.
    /// </remarks>
    public static bool TryGetByTypeId(ulong typeId, [NotNullWhen(true)] out DataSerializer? serializer) =>
        ByTypeId.TryGetValue(typeId, out serializer);

    /// <summary>Registers a serializer under a name the wire format can carry.</summary>
    /// <typeparam name="T">The type it serialises.</typeparam>
    /// <param name="alias">The name written when this type is stored polymorphically.</param>
    /// <param name="serializer">The serializer.</param>
    /// <param name="formerAliases">Names this type used to be written under, newest first.</param>
    /// <exception cref="SerializationException">Another type already claims <paramref name="alias" />.</exception>
    /// <remarks>
    ///     <para>
    ///         The alias, not the CLR type name, is what goes in the stream — so a type can be
    ///         renamed, moved between namespaces, or moved between assemblies without invalidating a
    ///         byte of existing data. That is the whole job of <c>[DataContract(Alias)]</c>.
    ///     </para>
    ///     <para>
    ///         A collision is an error rather than a last-one-wins, because the failure it produces
    ///         otherwise is data that loads as the wrong type in whichever assembly happened to
    ///         initialise second.
    ///     </para>
    /// </remarks>
    public static void Register<T>(string alias, DataSerializer<T> serializer, params string[] formerAliases) {
        ArgumentException.ThrowIfNullOrEmpty(alias);
        ArgumentNullException.ThrowIfNull(serializer);
        Register(serializer);
        Claim(alias, serializer, primary: true);

        foreach (var former in formerAliases) {
            Claim(former, serializer, primary: false);
        }
    }

    /// <summary>Finds the serializer registered under a name.</summary>
    /// <param name="alias">The name, current or former.</param>
    /// <param name="serializer">Its serializer.</param>
    /// <returns><see langword="false" /> if nothing claims that name.</returns>
    public static bool TryGetByAlias(string alias, [NotNullWhen(true)] out DataSerializer? serializer) =>
        ByAlias.TryGetValue(alias, out serializer);

    /// <summary>Finds the name a type is written under.</summary>
    /// <param name="type">The type.</param>
    /// <param name="alias">Its name.</param>
    /// <returns><see langword="false" /> if it has none.</returns>
    public static bool TryGetAlias(Type type, [NotNullWhen(true)] out string? alias) =>
        AliasOfType.TryGetValue(type, out alias);

    static void Claim(string alias, DataSerializer serializer, bool primary) {
        var existing = ByAlias.GetOrAdd(alias, serializer);

        if (!ReferenceEquals(existing, serializer) && existing.SerializedType != serializer.SerializedType) {
            throw new SerializationException(
                $"Both '{existing.SerializedType}' and '{serializer.SerializedType}' claim the serialised name "
                + $"'{alias}'. Give one of them an explicit [DataContract(\"…\")] alias; the name is what "
                + "polymorphic data carries, so two types cannot share it."
            );
        }

        ByAlias[alias] = serializer;

        if (primary) {
            AliasOfType[serializer.SerializedType] = alias;
        }
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
