// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Vixen.Core;

namespace Vixen.Ecs;

/// <summary>
///     Marks a component that carries no data — only the fact that the entity has it.
/// </summary>
/// <remarks>
///     <para>
///         A tag occupies a bit in the archetype mask and not one byte of chunk memory, so a
///         million entities tagged <c>Static</c> cost a megabyte less than they otherwise would and
///         a query for them is a mask test rather than a scan.
///     </para>
///     <para>
///         Declared rather than inferred. An empty C# struct still measures one byte, so "has no
///         fields" cannot be read from its size, and inferring it from reflection would mean a
///         struct that gains a field silently changes storage class — the kind of change that
///         compiles, runs, and loses data. Implementing this interface says it out loud, and a type
///         that implements it and then grows a field fails at registration instead.
///     </para>
/// </remarks>
public interface ITagComponent;

/// <summary>
///     Declares what a freshly created component of this type should hold, for the types whose zero
///     is not a usable starting point.
/// </summary>
/// <typeparam name="TSelf">The component itself.</typeparam>
/// <remarks>
///     <para>
///         <b>Opt-in, and a component that does not implement it costs nothing.</b> Most components
///         are data whose zero is a perfectly good starting point; this exists for the few whose zero
///         is not merely unhelpful but reads as a defect — a black light, a camera with a zero far
///         plane, a shot that is <i>disabled</i> as well as degenerate. Nothing is registered for a
///         type that does not implement this, no storage path branches on it, and
///         <see cref="World.Add{T}(Entity)" /> is unchanged for everybody.
///     </para>
///     <para>
///         ⚠ <b>This is consulted where something creates a component from nothing, and nowhere
///         else.</b> The ECS hands back zeroed storage and continues to — a chunk row is a memset and
///         <see cref="World.Add{T}(Entity)" /> still writes <c>default</c>. What reads this is the
///         authoring half: the editor's Add Component, and anything else building a value it is about
///         to hand to a person to edit. That boundary is the whole reason this is an interface and not
///         a field initializer: a field initializer runs on <c>new T()</c> and not on
///         <c>default(T)</c>, so it would be honoured on some paths and silently ignored on others,
///         with nothing in the source saying which. <see cref="World.AddDefault{T}" /> is how the
///         runtime asks for the same value on purpose.
///     </para>
///     <para>
///         ⚠ <b>Not a deserialization fallback.</b> A saved component carries every field, so a load
///         overwrites whatever it started from; a scene that omits a field means the field was
///         authored as zero. Reading this on a load would make an old file change meaning.
///     </para>
///     <para>
///         Implementing it changes nothing about the type's layout, its size or what a saved scene
///         holds — the member is static, so the struct is the same bytes it was.
///     </para>
///     <para>
///         <b>The engine's own components implement it explicitly</b>, which is the convention worth
///         copying: it keeps the value where it already was — <c>Lights.Default(kind)</c>,
///         <c>Camera.Perspective</c>, <c>TerrainComponent.Of(name)</c> — instead of growing a second
///         public name for it, so there is one place a default is written down and this is only the
///         wiring that says which one a fresh component takes.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     [Component]
///     [DataContract]
///     public struct Health : IDefaultComponent&lt;Health&gt; {
///         public float Current;
///         public float Maximum;
///
///         static Health IDefaultComponent&lt;Health&gt;.DefaultValue =&gt; new() { Current = 100f, Maximum = 100f };
///     }
///     </code>
/// </example>
public interface IDefaultComponent<TSelf> where TSelf : struct, IDefaultComponent<TSelf> {
    /// <summary>What a freshly created one holds.</summary>
    /// <remarks>
    ///     ⚠ <b>Not called <c>Default</c>, which is what it means.</b> An interface member by that name
    ///     is a name a language with <c>Default</c> as a keyword cannot implement (CA1716), and the
    ///     rule is worth keeping for a member a game's own types are meant to write. The types
    ///     themselves are free to call their factory <c>Default</c>, and most of them do.
    /// </remarks>
    static abstract TSelf DefaultValue { get; }
}

/// <summary>
///     Everything the storage layer needs to know about one component type: its dense id, how many
///     bytes a chunk gives it per entity, and whether the bytes are the value or a handle to it.
/// </summary>
public sealed class ComponentType {
    /// <summary>The dense id archetype masks and chunk layouts are keyed on.</summary>
    public ComponentTypeId Id { get; }

    /// <summary>The CLR type.</summary>
    public Type Type { get; }

    /// <summary>
    ///     How many bytes this occupies per entity in a chunk: the value's size for a plain struct,
    ///     four for a managed component's handle, zero for a tag.
    /// </summary>
    public int Size { get; }

    /// <summary>The alignment a chunk gives the column, so a vectorised sweep over it is legal.</summary>
    public int Alignment { get; }

    /// <summary>
    ///     Whether the type is or contains a reference, and so lives in the world's managed store
    ///     with the chunk holding a handle.
    /// </summary>
    public bool IsManaged { get; }

    /// <summary>Whether the type is a data-free <see cref="ITagComponent" />.</summary>
    public bool IsTag { get; }

    internal ComponentType(ComponentTypeId id, Type type, int size, int alignment, bool isManaged, bool isTag) {
        Id = id;
        Type = type;
        Size = size;
        Alignment = alignment;
        IsManaged = isManaged;
        IsTag = isTag;
    }

    /// <summary>Renders the type name and id, which is what a diagnostic wants.</summary>
    /// <returns>The description in text.</returns>
    public override string ToString() => $"{Type.Name}#{Id.Value}";
}

/// <summary>
///     The per-type cache the hot paths read. <see cref="Id" /> is a static readonly field on a
///     closed generic, so after the first touch the JIT treats it as a constant.
/// </summary>
/// <typeparam name="T">The component type.</typeparam>
public static class ComponentType<T> {
    /// <summary>The description registered for <typeparamref name="T" />.</summary>
    public static readonly ComponentType Info = ComponentRegistry.Register(typeof(T), Describe());

    /// <summary>The dense id assigned to <typeparamref name="T" />.</summary>
    public static readonly ComponentTypeId Id = Info.Id;

    static ComponentRegistry.Shape Describe() {
        var isTag = typeof(ITagComponent).IsAssignableFrom(typeof(T));
        var size = Unsafe.SizeOf<T>();

        if (isTag) {
            // An empty struct measures one byte. Anything larger has a field in it, and a tag with a
            // field would have its data silently dropped — so this is where that stops.
            if (size != 1) {
                throw new InvalidOperationException(
                    $"{typeof(T)} implements {nameof(ITagComponent)} but is {size} bytes, so it has "
                    + "fields. A tag stores nothing; give it its own component type or drop the "
                    + "interface."
                );
            }

            return new(0, 1, false, true);
        }

        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) {
            // A chunk is a byte array and the GC cannot see references inside it. Managed components
            // live in the world's store and the chunk holds the handle.
            return new(sizeof(int), sizeof(int), true, false);
        }

        return new(size, Alignment(size), false, false);
    }

    /// <summary>
    ///     The alignment a column gets: the value's own size rounded up to a power of two, capped
    ///     at sixteen.
    /// </summary>
    /// <remarks>
    ///     Sixteen is where it stops because that is the widest natural alignment anything in
    ///     <c>Vixen.Core.Mathematics</c> asks for, and because the column offset is relative to a
    ///     managed byte array whose own start the runtime aligns to eight — promising thirty-two
    ///     would be a promise this cannot keep. Vector loads through
    ///     <see cref="System.Runtime.InteropServices.MemoryMarshal.Cast{TFrom,TTo}(Span{TFrom})" />
    ///     are unaligned loads regardless, so this is about layout tidiness and false-sharing, not
    ///     about correctness.
    /// </remarks>
    static int Alignment(int size) => size switch {
        <= 1 => 1,
        <= 2 => 2,
        <= 4 => 4,
        <= 8 => 8,
        _ => 16
    };
}

/// <summary>
///     The process-wide table mapping component types to dense ids, assigned from 1 on first use.
/// </summary>
/// <remarks>
///     <para>
///         Ids are assigned in the order types are first touched, which depends on what the program
///         did — so they are stable within a process and meaningless outside one. Nothing persists
///         them: a serialised world names component types by their
///         <see cref="DataContractAttribute.Alias" /> and sorts by that, not by id.
///     </para>
///     <para>
///         There is no unregister. A component type that has been seen keeps its id for the life of
///         the process, because an archetype mask taken before it went away would otherwise start
///         meaning something else.
///     </para>
/// </remarks>
public static class ComponentRegistry {
    internal readonly record struct Shape(int Size, int Alignment, bool IsManaged, bool IsTag);

    static readonly Lock Gate = new();
    static readonly ConcurrentDictionary<Type, ComponentType> ByType = new();
    static ComponentType?[] byId = new ComponentType?[64];
    static int nextId = 1;

    /// <summary>How many component types have been registered.</summary>
    public static int Count => nextId - 1;

    /// <summary>The description for <typeparamref name="T" />, registering it if this is the first ask.</summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <returns>Its description.</returns>
    public static ComponentType Of<T>() => ComponentType<T>.Info;

    /// <summary>The description for an already-registered id.</summary>
    /// <param name="id">The id.</param>
    /// <returns>Its description.</returns>
    /// <exception cref="ArgumentException">No component type has that id.</exception>
    public static ComponentType Get(ComponentTypeId id) {
        var table = Volatile.Read(ref byId);

        if (id.IsValid && id.Value < table.Length && table[id.Value] is { } type) {
            return type;
        }

        // The table grows by replacement, so a reader holding the previous array sees a valid id as
        // out of range. Taking the lock re-reads the current one; this costs nothing on the path
        // that matters because that path never gets here.
        lock (Gate) {
            if (id.IsValid && id.Value < byId.Length && byId[id.Value] is { } current) {
                return current;
            }
        }

        throw new ArgumentException($"No component type has id {id}.", nameof(id));
    }

    /// <summary>The description for a type, if it has been registered.</summary>
    /// <param name="type">The CLR type.</param>
    /// <param name="component">Its description, when it has one.</param>
    /// <returns>Whether it was registered.</returns>
    public static bool TryGet(Type type, out ComponentType? component) => ByType.TryGetValue(type, out component);

    internal static ComponentType Register(Type type, Shape shape) {
        if (ByType.TryGetValue(type, out var existing)) {
            return existing;
        }

        lock (Gate) {
            if (ByType.TryGetValue(type, out existing)) {
                return existing;
            }

            var id = new ComponentTypeId(nextId);
            var component = new ComponentType(id, type, shape.Size, shape.Alignment, shape.IsManaged, shape.IsTag);

            if (nextId >= byId.Length) {
                var grown = new ComponentType?[byId.Length * 2];
                Array.Copy(byId, grown, byId.Length);

                // Publish the bigger array before the id that indexes into it, so a reader that has
                // already taken a reference to the old one still sees a consistent table.
                byId = grown;
            }

            byId[nextId] = component;
            ByType[type] = component;
            nextId++;
            return component;
        }
    }
}
