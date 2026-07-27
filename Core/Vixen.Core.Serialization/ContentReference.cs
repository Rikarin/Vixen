// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Serialization;

/// <summary>Turns a chunk id into the object it names, while something is being deserialised.</summary>
/// <remarks>
///     Implemented by whatever owns loaded content — at run time that is the asset manager, and in a
///     test it is a dictionary. The serializer never knows which.
/// </remarks>
public interface IContentResolver {
    /// <summary>Finds what a chunk id was loaded as.</summary>
    /// <param name="id">The chunk.</param>
    /// <param name="value">What it loaded to.</param>
    /// <returns><see langword="false" /> if nothing has loaded it.</returns>
    bool TryResolve(ObjectId id, out object? value);
}

/// <summary>Which resolver the current thread's deserialisation should use.</summary>
/// <remarks>
///     <para>
///         <b>Ambient, and that is defensible here in a way it was not for asset scopes.</b> Deserialising
///         one chunk is synchronous from start to finish — there is no <c>await</c> anywhere inside it,
///         so "the resolver in force" has exactly one meaning for the whole of it. The argument that
///         ruled out ambient scopes was that a load can outlive the block it started in; a chunk read
///         cannot.
///     </para>
///     <para>
///         The alternative would be threading a resolver through every generated serializer's
///         signature, which would put an asset-loading concept into the wire format of types that
///         have nothing to do with assets.
///     </para>
/// </remarks>
public static class ContentResolution {
    [ThreadStatic]
    static IContentResolver? current;

    /// <summary>The resolver in force on this thread, if any.</summary>
    public static IContentResolver? Current => current;

    /// <summary>Puts a resolver in force until the returned scope is disposed.</summary>
    /// <param name="resolver">The resolver.</param>
    /// <returns>A scope that restores the previous one.</returns>
    /// <remarks>
    ///     Restores rather than clears, so a nested read — a material that resolves a texture that
    ///     resolves something else — does not leave the outer read without a resolver half way
    ///     through.
    /// </remarks>
    public static Scope Push(IContentResolver resolver) {
        ArgumentNullException.ThrowIfNull(resolver);

        var previous = current;
        current = resolver;

        return new(previous);
    }

    /// <summary>Restores the resolver that was in force before.</summary>
    /// <param name="previous">The one to restore.</param>
    public readonly struct Scope(IContentResolver? previous) : IDisposable {
        /// <inheritdoc />
        public void Dispose() => current = previous;
    }
}

/// <summary>A pointer from one asset to another, stored as a chunk id and resolved on load.</summary>
/// <typeparam name="T">What it points at.</typeparam>
/// <remarks>
///     <para>
///         A material does not contain its textures; it names them. That is what lets two materials
///         share one texture, what lets the build put them in different bundles, and what makes the
///         chunk graph a graph rather than a set of trees that duplicate everything they share.
///     </para>
///     <para>
///         <b>What is written is the id and nothing else.</b> The value is filled in at read time, from
///         whatever <see cref="ContentResolution" /> has in force — which at run time means the object
///         the asset manager has already loaded, so two materials pointing at one texture get the same
///         instance rather than two copies of it.
///     </para>
///     <para>
///         <b>An unresolved reference is not an error.</b> Reading a chunk with no resolver in force —
///         a tool inspecting content, a test, an editor listing what points at what — gives a
///         reference that knows its id and not its value. That is a useful thing to have and the type
///         says which it is rather than pretending.
///     </para>
/// </remarks>
public sealed class ContentReference<T> where T : class {
    /// <summary>The chunk it points at.</summary>
    public ObjectId Id { get; }

    /// <summary>What that chunk loaded to, or <see langword="null" /> if nothing resolved it.</summary>
    public T? Value { get; private set; }

    /// <summary>Whether the value is there.</summary>
    public bool IsResolved => Value is not null;

    /// <summary>A reference to a chunk, not yet resolved.</summary>
    /// <param name="id">The chunk.</param>
    public ContentReference(ObjectId id) => Id = id;

    /// <summary>A reference to something already in hand.</summary>
    /// <param name="id">The chunk.</param>
    /// <param name="value">What it is.</param>
    public ContentReference(ObjectId id, T? value) {
        Id = id;
        Value = value;
    }

    /// <summary>A reference to nothing.</summary>
    public static ContentReference<T> Empty { get; } = new(ObjectId.Empty);

    /// <summary>What it points at.</summary>
    /// <exception cref="SerializationException">Nothing resolved it.</exception>
    public T Require() =>
        Value ?? throw new SerializationException(
            $"The reference to chunk {Id} was never resolved, so there is no {typeof(T).Name} to hand over. "
            + "Either the chunk was not loaded before the thing pointing at it, or it was read with no content "
            + "resolver in force."
        );

    /// <summary>Fills in the value. Used by the serializer and by a resolve pass.</summary>
    /// <param name="value">What the chunk loaded to.</param>
    public void Resolve(T? value) => Value = value;

    /// <inheritdoc />
    public override string ToString() => Value is null ? $"→ {Id}" : $"→ {Id} ({Value})";
}

/// <summary>Reads and writes a content reference: the id goes to the stream, the value does not.</summary>
/// <typeparam name="T">What it points at.</typeparam>
/// <remarks>
///     Hand-written and generic, and registered per closed type by the <c>[DataContract]</c> generator
///     when it sees a member of that type. That is what keeps it AOT-correct: every
///     <c>ContentReference&lt;Texture&gt;</c> a build can encounter was instantiated in generated
///     source rather than reflected into existence at run time.
/// </remarks>
public sealed class ContentReferenceSerializer<T> : DataSerializer<ContentReference<T>> where T : class {
    /// <inheritdoc />
    public override void Serialize(ref SerializationWriter writer, in ContentReference<T> value) {
        var id = value?.Id ?? ObjectId.Empty;
        writer.WriteUInt64(id.High);
        writer.WriteUInt64(id.Low);
    }

    /// <inheritdoc />
    public override void Deserialize(ref SerializationReader reader, ref ContentReference<T> value) {
        var id = new ObjectId(reader.ReadUInt64(), reader.ReadUInt64());

        if (id.IsEmpty) {
            value = ContentReference<T>.Empty;
            return;
        }

        value = new(id);

        // Resolved here rather than in a pass afterwards, because here is the only place that knows
        // both the id and the field it is going into without walking the object graph by reflection.
        if (ContentResolution.Current?.TryResolve(id, out var resolved) == true) {
            value.Resolve(resolved as T);
        }
    }
}
