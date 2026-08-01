// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Ecs;

namespace Vixen.Engine.Scenes;

/// <summary>Reads and writes one component type as part of a compiled scene.</summary>
/// <remarks>
///     <para>
///         The join between three things that each know a different half of the job: the ECS knows
///         the component's dense id and how to put a value on an entity, the binary serializer knows
///         how to turn the value into bytes, and the compiled scene knows only a name. This is what
///         turns a name back into a write into a chunk.
///     </para>
///     <para>
///         <b>A binder is not generic and the implementation is.</b> A scene's blocks are walked with
///         the component types known only from strings, so the walk has to hold these behind a common
///         shape; closing the generic at registration keeps every read and write statically bound and
///         boxes nothing — which is the same trade <see cref="DataSerializer{T}" /> makes and for the
///         same reason.
///     </para>
/// </remarks>
public interface ISceneComponentBinder {
    /// <summary>The name a compiled scene carries — the component's <c>[DataContract]</c> alias.</summary>
    string Name { get; }

    /// <summary>The CLR type.</summary>
    Type ComponentType { get; }

    /// <summary>Its dense id, which is what an archetype is built from.</summary>
    ComponentTypeId TypeId { get; }

    /// <summary>Whether it is a data-free tag, and so has no bytes in a column at all.</summary>
    bool IsTag { get; }

    /// <summary>Reads one value out of a column and writes it onto an entity.</summary>
    /// <param name="reader">The column, positioned at this entity's value.</param>
    /// <param name="world">The world the entity is in.</param>
    /// <param name="entity">The entity, already in an archetype that has this component.</param>
    void Read(ref SerializationReader reader, World world, Entity entity);

    /// <summary>Writes one entity's value into a column.</summary>
    /// <param name="writer">The column.</param>
    /// <param name="world">The world the entity is in.</param>
    /// <param name="entity">The entity.</param>
    void Write(ref SerializationWriter writer, World world, Entity entity);

    /// <summary>Writes a value that is not on an entity into a column.</summary>
    /// <param name="writer">The column.</param>
    /// <param name="value">The value, boxed.</param>
    /// <exception cref="InvalidCastException">It is not of <see cref="ComponentType" />.</exception>
    /// <remarks>
    ///     What a compiler uses. It has bound the authored value out of a YAML document and has no
    ///     world to put it in — building one to serialise it would be an ECS world per asset in the
    ///     build, for nothing. The box is one allocation per authored component per build, against a
    ///     document that was itself bound by boxing every member.
    /// </remarks>
    void WriteValue(ref SerializationWriter writer, object value);

    /// <summary>Reads an entity's value as an object.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>The value, boxed, or the default for a tag.</returns>
    /// <remarks>What the editor's save uses, going from a live world back to an authored document.</remarks>
    object ValueOn(World world, Entity entity);

    /// <summary>Puts a value on an entity, adding the component if it is not there.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="value">The value, boxed.</param>
    /// <exception cref="InvalidCastException">It is not of <see cref="ComponentType" />.</exception>
    /// <remarks>
    ///     The other end of the editor's round trip, and a structural change per component — which is
    ///     the right cost in an editor opening a document and the wrong one in a load, where
    ///     <see cref="Read" /> writes into an archetype that already has the column.
    /// </remarks>
    void AddTo(World world, Entity entity, object value);

    /// <summary>Whether an entity carries this component.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>Whether it does.</returns>
    /// <remarks>
    ///     ⚠ <b>Asked before <see cref="ValueOn" />, which throws on an entity that does not have
    ///     one.</b> An editor drawing "what is on this entity" has to enumerate the registered
    ///     components and ask each — there is no way to go the other way, because an archetype knows
    ///     dense ids and a panel knows names.
    /// </remarks>
    bool Has(World world, Entity entity);

    /// <summary>Takes the component off an entity.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>Whether it was there to remove.</returns>
    /// <remarks>
    ///     A structural change, like <see cref="AddTo" />, and the same trade: the right cost in an
    ///     editor and the wrong one in a loop.
    /// </remarks>
    bool RemoveFrom(World world, Entity entity);
}

/// <summary>The binder for one component type.</summary>
/// <typeparam name="T">The component.</typeparam>
sealed class SceneComponentBinder<T> : ISceneComponentBinder {
    readonly DataSerializer<T> serializer;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public Type ComponentType => typeof(T);

    /// <inheritdoc />
    public ComponentTypeId TypeId => ComponentType<T>.Id;

    /// <inheritdoc />
    public bool IsTag => ComponentType<T>.Info.IsTag;

    internal SceneComponentBinder(string alias, DataSerializer<T> serializer) {
        Name = alias;
        this.serializer = serializer;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>A tag reads nothing, and its column holds nothing.</b> A tag occupies a bit in the
    ///     archetype mask and not a byte of chunk memory, so there is no column to write into —
    ///     <see cref="World.Set{T}" /> would fail looking for one. Being in the block's component set
    ///     is the whole of what a tag has to say.
    /// </remarks>
    public void Read(ref SerializationReader reader, World world, Entity entity) {
        if (IsTag) {
            return;
        }

        var value = default(T)!;
        serializer.Deserialize(ref reader, ref value);
        world.Set(entity, in value);
    }

    /// <inheritdoc />
    public void Write(ref SerializationWriter writer, World world, Entity entity) {
        if (IsTag) {
            return;
        }

        serializer.Serialize(ref writer, world.Read<T>(entity));
    }

    /// <inheritdoc />
    public void WriteValue(ref SerializationWriter writer, object value) {
        if (IsTag) {
            return;
        }

        var typed = (T) value;
        serializer.Serialize(ref writer, in typed);
    }

    /// <inheritdoc />
    public object ValueOn(World world, Entity entity) => IsTag ? default(T)! : world.Read<T>(entity)!;

    /// <inheritdoc />
    public void AddTo(World world, Entity entity, object value) {
        if (IsTag) {
            if (!world.Has<T>(entity)) {
                world.Add<T>(entity);
            }

            return;
        }

        var typed = (T) value;

        if (world.Has<T>(entity)) {
            world.Set(entity, in typed);
        } else {
            world.Add(entity, in typed);
        }
    }

    /// <inheritdoc />
    public bool Has(World world, Entity entity) {
        ArgumentNullException.ThrowIfNull(world);

        return world.Has<T>(entity);
    }

    /// <inheritdoc />
    public bool RemoveFrom(World world, Entity entity) {
        ArgumentNullException.ThrowIfNull(world);

        if (!world.Has<T>(entity)) {
            return false;
        }

        world.Remove<T>(entity);
        return true;
    }
}

/// <summary>Which component types a compiled scene is allowed to name, and how each of them loads.</summary>
/// <remarks>
///     <para>
///         <b>Discovered at compile time, which is not the same as scanned at run time.</b> A
///         component carrying both <c>[Component]</c> and <c>[DataContract]</c> is declared here by
///         <c>ComponentRegistrationGenerator</c>, through a <c>[ModuleInitializer]</c> the declaring
///         assembly emits. The objection this paragraph used to record stands and is answered rather
///         than dropped: an assembly scan reads metadata a trimmed publish has already deleted, and
///         it would make "which components can be in a scene" a question with a different answer in
///         the editor, in a worker process and in a shipped game. A generated call is the opposite —
///         what is registered is what a generator saw in the source, so the set is fixed at build
///         time and identical everywhere, which is the bargain <see cref="SerializerRegistry" />
///         already struck for the serializers themselves.
///     </para>
///     <para>
///         <b>The name is the serializer's alias and is not chosen here.</b> A component's
///         <c>[DataContract]</c> name is already what the binary format writes for it; taking it from
///         <see cref="SerializerRegistry" /> means there is one spelling rather than two that have to
///         be kept in step, and it makes registering a type with no serializer fail at the
///         registration rather than at the load.
///     </para>
///     <para>
///         ⚠ <b>Two attributes rather than one, because a handle is a component and is not scene
///         data.</b> <c>PhysicsBody</c> holds a body handle and carries <c>[Component]</c> without
///         <c>[DataContract]</c>, so it is excluded by construction — no denylist, no opt-out
///         attribute, and nothing for anybody to remember. <see cref="Transforms.LocalTransform" />
///         carries neither and is absent for its own reason: every entity in a scene has one, so it
///         is stored as three columns of the scene's own rather than as a component somebody could
///         also list, which would let a file say two different things about where an entity is.
///     </para>
/// </remarks>
public static class SceneComponentRegistry {
    static readonly ConcurrentDictionary<string, ISceneComponentBinder> ByAlias = new(StringComparer.Ordinal);
    static readonly ConcurrentDictionary<Type, ISceneComponentBinder> ByType = new();
    static readonly ConcurrentQueue<Action> Declared = new();

    /// <summary>Held for the whole of a drain, so a lookup cannot run past a registration in flight.</summary>
    static readonly Lock Gate = new();

    /// <summary>How many components have been declared.</summary>
    static long declarations;

    /// <summary>The <see cref="declarations" /> count that a completed drain has registered all of.</summary>
    /// <remarks>
    ///     ⚠ <b>Written only at the end of a drain, which is what makes the fast path safe.</b> While a
    ///     drain is running this still holds the older count, so the equality below cannot hold and no
    ///     thread can skip the lock while a registration is in flight. A plain "have we drained" flag
    ///     cannot express that: it is either set before the drain, and lets a second thread past a
    ///     registration that has not happened, or set after it, and loses a declaration that arrived
    ///     while the drain was running.
    /// </remarks>
    static long resolved = -1;

    /// <summary>Every component a scene may name, in the order they were registered.</summary>
    /// <remarks>
    ///     ⚠ <b>What an "Add Component" menu is built from, and the only direction that works.</b> An
    ///     archetype knows dense <see cref="ComponentTypeId" />s and an editor knows names, and there
    ///     is no map from the first to the second — the ids are handed out in first-touch order, so
    ///     they differ between two runs of the same program. Enumerating what is registered and
    ///     asking each one <see cref="ISceneComponentBinder.Has" /> is how a panel finds out what an
    ///     entity carries.
    /// </remarks>
    public static IReadOnlyCollection<ISceneComponentBinder> Binders {
        get {
            Resolve();
            return (IReadOnlyCollection<ISceneComponentBinder>) ByAlias.Values;
        }
    }

    /// <summary>Says a component exists, to be registered the first time anything asks.</summary>
    /// <typeparam name="T">The component.</typeparam>
    /// <remarks>
    ///     <para>
    ///         <b>What the generated <c>[ModuleInitializer]</c> calls, and the deferral is the whole
    ///         point of it having its own method.</b> Registering needs the component's serializer,
    ///         which its assembly registers from a module initializer of its own — and the order two
    ///         module initializers in one assembly run in is unspecified. Enqueuing costs one static
    ///         delegate per component and cannot observe that order at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A failure therefore surfaces at the first lookup rather than here.</b> That is
    ///         only reachable when an assembly ran this generator and not the serialization one, which
    ///         is a build that is wired wrong rather than a component that is written wrong; the
    ///         message <see cref="Register{T}" /> throws names the type and the fix either way.
    ///     </para>
    /// </remarks>
    public static void Declare<T>() {
        Declared.Enqueue(static () => Register<T>());

        // ⚠ After the enqueue, so a drain that reads the count and then finds the queue empty has
        // not yet been told to expect this one — it will leave `resolved` behind the count and the
        // next lookup drains again.
        Interlocked.Increment(ref declarations);
    }

    /// <summary>Makes a component type nameable by a compiled scene.</summary>
    /// <typeparam name="T">The component.</typeparam>
    /// <exception cref="SerializationException">It has no serializer, or no name to be written under.</exception>
    /// <exception cref="InvalidOperationException">Another component already claims its name.</exception>
    /// <remarks>
    ///     Idempotent for the same type, so a game that registers its components from more than one
    ///     entry point — a test, a module initializer, a plugin loading twice — does not have to
    ///     coordinate. Annotating the component is the ordinary way in; this stays public for a type
    ///     whose assembly cannot run a generator, and for a test that wants one registered now.
    /// </remarks>
    public static void Register<T>() {
        if (ByType.ContainsKey(typeof(T))) {
            return;
        }

        // Asked for before the alias, so the message a caller gets first is the one they can act on:
        // a type with no [DataContract] has neither, and saying "no name" would send them looking
        // for a name to give it.
        if (!SerializerRegistry.TryGet<T>(out var serializer)) {
            throw new SerializationException(
                $"'{typeof(T)}' has no serializer, so a compiled scene could not carry one. Give it "
                + "[DataContract] so the generator writes one, and make sure the declaring assembly runs "
                + "the serialization generator."
            );
        }

        if (!SerializerRegistry.TryGetAlias(typeof(T), out var alias)) {
            throw new SerializationException(
                $"'{typeof(T)}' has a serializer registered without a name, so nothing could write it into a "
                + "scene. Register it with the overload that takes an alias — which is what [DataContract] "
                + "generates."
            );
        }

        var binder = new SceneComponentBinder<T>(alias, serializer);
        var existing = ByAlias.GetOrAdd(alias, binder);

        if (existing.ComponentType != typeof(T)) {
            throw new InvalidOperationException(
                $"Both '{existing.ComponentType}' and '{typeof(T)}' are called '{alias}'. A compiled scene "
                + "carries the name and nothing else, so two components cannot share one."
            );
        }

        ByType[typeof(T)] = binder;
    }

    /// <summary>The binder for a name, if a scene may name it.</summary>
    /// <param name="alias">The name.</param>
    /// <param name="binder">Its binder.</param>
    /// <returns><see langword="false" /> if nothing registered claims it.</returns>
    public static bool TryGet(string alias, [NotNullWhen(true)] out ISceneComponentBinder? binder) {
        Resolve();
        return ByAlias.TryGetValue(alias, out binder);
    }

    /// <summary>The binder for a component type, if it has one.</summary>
    /// <param name="type">The CLR type.</param>
    /// <param name="binder">Its binder.</param>
    /// <returns><see langword="false" /> if it was never registered.</returns>
    public static bool TryGet(Type type, [NotNullWhen(true)] out ISceneComponentBinder? binder) {
        Resolve();
        return ByType.TryGetValue(type, out binder);
    }

    /// <summary>The binder for a name, or a failure that says what to do about it.</summary>
    /// <param name="alias">The name.</param>
    /// <returns>The binder.</returns>
    /// <exception cref="SceneComponentException">Nothing registered claims that name.</exception>
    /// <remarks>
    ///     ⚠ <b>A scene naming an unknown component fails to load rather than loading without it.</b>
    ///     The component is part of the entity's archetype, so there is no "without it" that is the
    ///     same scene — an enemy that arrives with no <c>Health</c> is a level that runs and is wrong.
    ///     What this actually means is a content build newer than the binary reading it, and saying so
    ///     is the whole of the fix.
    /// </remarks>
    public static ISceneComponentBinder Require(string alias) =>
        TryGet(alias, out var binder)
            ? binder
            : throw new SceneComponentException(
                $"This build has no component called '{alias}', so the scene naming it cannot be loaded. "
                + "Either the content was built against a newer game than the one loading it, or the "
                + "component is missing [Component] or [DataContract] — a compiled scene may name a type "
                + "that carries both, and the declaring assembly has to run the engine's component "
                + "generator."
            );

    /// <summary>Registers everything <see cref="Declare{T}" /> has been told about and not yet built.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Dequeued before it is run, so a component that cannot be registered fails once.</b>
    ///         Leaving it on the queue would raise the same exception out of every lookup that followed,
    ///         including the ones that had nothing to do with it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Under a lock, because dequeuing is not registering.</b> An unlocked drain is empty
    ///         from the moment the last action is taken off it and before that action has written its
    ///         binder, so a second thread asking in that window drains nothing, finds nothing, and is
    ///         told the component was never declared. That window is microseconds wide and it is the
    ///         whole of a lookup, which is why it reads as a component that exists in one run of the
    ///         editor and not the next rather than as a race — the editor imports off a thread pool
    ///         while the scene panel compiles on another, and both arrive here first.
    ///     </para>
    /// </remarks>
    static void Resolve() {
        if (Volatile.Read(ref resolved) == Interlocked.Read(ref declarations)) {
            return;
        }

        lock (Gate) {
            // Read before the drain and stored after it. A Declare that lands in between raises the
            // count past what is stored here, so the next lookup drains again rather than trusting a
            // count this drain could not have covered.
            var seen = Interlocked.Read(ref declarations);

            while (Declared.TryDequeue(out var register)) {
                register();
            }

            Volatile.Write(ref resolved, seen);
        }
    }
}

/// <summary>A compiled scene names something this build cannot make.</summary>
/// <param name="message">What is missing, and what would fix it.</param>
public sealed class SceneComponentException(string message) : Exception(message);
