// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Ecs;

namespace Vixen.Engine.Behaviors;

/// <summary>Attaching, reading and removing one behaviour type without naming it.</summary>
/// <remarks>
///     ⚠ <b>Typed, and closed once at registration, which is the whole reason this interface
///     exists.</b> <see cref="BehaviorStore.Add{T}(Entity, T)" /> buckets by the <i>static</i> type at
///     the call site — that is what keeps the update loop monomorphic over a contiguous array — so a
///     type-erased attach through <c>Add&lt;Behavior&gt;</c> would put every behaviour in the project
///     into one bucket and undo the arrangement <see cref="BehaviorStore" /> is built around. Closing
///     the generic here means the editor and the scene loader can work in <see cref="Type" /> and the
///     store still sees the concrete one.
/// </remarks>
public interface ISceneBehaviorBinder {
    /// <summary>The name a scene carries — the behaviour's <c>[DataContract]</c> alias.</summary>
    string Name { get; }

    /// <summary>The CLR type.</summary>
    Type BehaviorType { get; }

    /// <summary>A fresh instance, unattached.</summary>
    object Create();

    /// <summary>An unattached copy of one, carrying exactly the state a scene would.</summary>
    /// <param name="behavior">The instance to copy.</param>
    /// <returns>The copy.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What makes a behaviour editable on a component's terms.</b> The inspector reads a
    ///         value, lets the rows write into it, and puts the result back as one undo step — which
    ///         works because a component is a struct and reading one gives you a copy. A behaviour is
    ///         a class, so a panel handed the live instance would edit the thing it was about to
    ///         record as the "before", and every undo would be a no-op.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Through the serializer rather than member by member, and the difference is the
    ///         member set.</b> A reflection descriptor is the <i>inspector's</i> view and deliberately
    ///         includes things a file does not — <c>Behavior.Position</c> is a façade over the
    ///         entity's transform, and copying it would move the entity. The serializer's contract is
    ///         the file's view, so a copy taken through it restores exactly what a save would have
    ///         written, which is the only definition of "the same behaviour" that undo can promise.
    ///     </para>
    /// </remarks>
    object Copy(object behavior);

    /// <summary>The behaviour of this type on an entity, if it has one.</summary>
    /// <param name="store">Where behaviours live.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>It, or <see langword="null" />.</returns>
    Behavior? Attached(BehaviorStore store, Entity entity);

    /// <summary>Attaches one, replacing whatever of this type was there.</summary>
    /// <param name="store">Where behaviours live.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="behavior">The instance.</param>
    /// <remarks>
    ///     ⚠ <b>Replacing rather than adding a second.</b> The store itself is happy to hold two
    ///     behaviours of one type on one entity — it is a list — but everything above this treats a
    ///     behaviour the way it treats a component, where an entity has one or none. An undo that put
    ///     a second <c>PlayerController</c> beside the first would be the visible form of the
    ///     difference.
    /// </remarks>
    void AttachTo(BehaviorStore store, Entity entity, object behavior);

    /// <summary>Takes it off an entity, now.</summary>
    /// <param name="store">Where behaviours live.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>Whether it was there.</returns>
    /// <remarks>
    ///     ⚠ <b><see cref="BehaviorStore.Remove" /> and not <c>Destroy</c>.</b> This is the authoring
    ///     path — an editor taking a behaviour off, or a loader replacing one — and a queued destroy
    ///     leaves the instance on the entity until a lifecycle drain that, in an editor, never comes.
    ///     What that looked like was an add-remove-add cycle stacking duplicates, which is what undo
    ///     and redo do.
    /// </remarks>
    bool RemoveFrom(BehaviorStore store, Entity entity);
}

/// <summary>Which behaviour types a scene may name, and how each of them attaches.</summary>
/// <remarks>
///     <para>
///         <b><see cref="Scenes.SceneComponentRegistry" />'s twin, and deliberately not an entry in
///         it.</b> The two answer the same question — "what may a scene put on an entity" — about two
///         different kinds of thing: a component is a struct in a chunk that <c>World.Set</c> writes,
///         and a behaviour is a class in a <see cref="BehaviorStore" /> bucket that
///         <see cref="BehaviorStore.Add{T}(Entity, T)" /> attaches. Folding behaviours into the
///         component registry would mean a binder whose <c>TypeId</c> and <c>IsTag</c> mean nothing
///         and whose <c>Read</c> writes into a chunk that has no column for it.
///     </para>
///     <para>
///         ⚠ <b>Two registries, one namespace of aliases.</b> A scene entry is written
///         <c>- !PlayerController</c> whichever it is, so the loader asks this and the component
///         registry in turn — see <see cref="Scenes.SceneComponentRegistry" />'s <c>Require</c> for
///         what a name neither claims means. <see cref="Register{T}" /> refuses an alias a component
///         already holds, because a file that names one thing cannot mean two.
///     </para>
///     <para>
///         <b>Discovered at compile time, exactly as components are.</b> A <see cref="Behavior" />
///         subclass carrying <c>[DataContract]</c> is declared here by
///         <c>BehaviorRegistrationGenerator</c> through a <c>[ModuleInitializer]</c> the declaring
///         assembly emits — so what a project can author is what a generator saw in its source, which
///         survives trimming and is the same set in the editor and in a shipped game.
///     </para>
/// </remarks>
public static class SceneBehaviorRegistry {
    static readonly ConcurrentDictionary<string, ISceneBehaviorBinder> ByAlias = new(StringComparer.Ordinal);
    static readonly ConcurrentDictionary<Type, ISceneBehaviorBinder> ByType = new();
    static readonly ConcurrentQueue<Action> Declared = new();
    static readonly Lock Gate = new();

    /// <summary>Every behaviour a scene may name, in the order they were registered.</summary>
    public static IReadOnlyCollection<ISceneBehaviorBinder> Binders {
        get {
            Resolve();
            return (IReadOnlyCollection<ISceneBehaviorBinder>) ByAlias.Values;
        }
    }

    /// <summary>Says a behaviour exists, to be registered the first time anything asks.</summary>
    /// <typeparam name="T">The behaviour.</typeparam>
    /// <remarks>
    ///     <inheritdoc cref="Scenes.SceneComponentRegistry.Declare{T}" select="remarks/para[1]" />
    /// </remarks>
    public static void Declare<T>() where T : Behavior, new() => Declared.Enqueue(static () => Register<T>());

    /// <summary>Makes a behaviour type nameable by a scene.</summary>
    /// <typeparam name="T">The behaviour.</typeparam>
    /// <exception cref="SerializationException">It has no serializer, or no name to be written under.</exception>
    /// <exception cref="InvalidOperationException">Something else already claims its name.</exception>
    /// <remarks>
    ///     Idempotent for the same type, on <see cref="Scenes.SceneComponentRegistry.Register{T}" />'s
    ///     terms. Annotating the behaviour is the ordinary way in; this stays public for a type whose
    ///     assembly cannot run a generator, and for a test that wants one registered now.
    /// </remarks>
    public static void Register<T>() where T : Behavior, new() {
        if (ByType.ContainsKey(typeof(T))) {
            return;
        }

        // Asked for before the alias for the reason the component registry gives: a type with no
        // [DataContract] has neither, and "no name" sends somebody looking for a name to give it.
        if (!SerializerRegistry.TryGet<T>(out _)) {
            throw new SerializationException(
                $"'{typeof(T)}' has no serializer, so a scene could not carry one. Give it [DataContract] "
                + "so the generator writes one, and make sure the declaring assembly runs the serialization "
                + "generator."
            );
        }

        if (!SerializerRegistry.TryGetAlias(typeof(T), out var alias)) {
            throw new SerializationException(
                $"'{typeof(T)}' has a serializer registered without a name, so nothing could write it into a "
                + "scene. Register it with the overload that takes an alias — which is what [DataContract] "
                + "generates."
            );
        }

        // ⚠ Against the components as well as against the behaviours. A scene entry carries a name
        // and nothing else, so an alias two things answer to is a file whose meaning depends on which
        // registry the loader happens to ask first.
        if (Scenes.SceneComponentRegistry.TryGet(alias, out var component)) {
            throw new InvalidOperationException(
                $"The component '{component.ComponentType}' is already called '{alias}', so the behaviour "
                + $"'{typeof(T)}' cannot be. A scene entry carries the name and nothing else."
            );
        }

        var binder = new SceneBehaviorBinder<T>(alias);
        var existing = ByAlias.GetOrAdd(alias, binder);

        if (existing.BehaviorType != typeof(T)) {
            throw new InvalidOperationException(
                $"Both '{existing.BehaviorType}' and '{typeof(T)}' are called '{alias}'. A scene carries the "
                + "name and nothing else, so two behaviours cannot share one."
            );
        }

        ByType[typeof(T)] = binder;
    }

    /// <summary>The binder for a name, if a scene may name it.</summary>
    /// <param name="alias">The name.</param>
    /// <param name="binder">Its binder.</param>
    /// <returns><see langword="false" /> if nothing registered claims it.</returns>
    public static bool TryGet(string alias, [NotNullWhen(true)] out ISceneBehaviorBinder? binder) {
        Resolve();
        return ByAlias.TryGetValue(alias, out binder);
    }

    /// <summary>The binder for a behaviour type, if it has one.</summary>
    /// <param name="type">The CLR type.</param>
    /// <param name="binder">Its binder.</param>
    /// <returns><see langword="false" /> if it was never registered.</returns>
    public static bool TryGet(Type type, [NotNullWhen(true)] out ISceneBehaviorBinder? binder) {
        Resolve();
        return ByType.TryGetValue(type, out binder);
    }

    /// <summary>Registers everything <see cref="Declare{T}" /> has been told about and not yet built.</summary>
    /// <inheritdoc cref="Scenes.SceneComponentRegistry.Declare{T}" select="remarks/para[2]" />
    static void Resolve() {
        if (Declared.IsEmpty) {
            return;
        }

        lock (Gate) {
            while (Declared.TryDequeue(out var register)) {
                register();
            }
        }
    }
}

/// <summary>The binder for one behaviour type.</summary>
/// <typeparam name="T">The behaviour.</typeparam>
sealed class SceneBehaviorBinder<T>(string alias) : ISceneBehaviorBinder where T : Behavior, new() {
    /// <inheritdoc />
    public string Name { get; } = alias;

    /// <inheritdoc />
    public Type BehaviorType => typeof(T);

    /// <inheritdoc />
    public object Create() => new T();

    /// <inheritdoc />
    public object Copy(object behavior) {
        ArgumentNullException.ThrowIfNull(behavior);

        if (behavior is not T typed) {
            throw new ArgumentException($"'{behavior.GetType()}' is not a {typeof(T)}.", nameof(behavior));
        }

        // ⚠ A fresh instance to read into, not `default(T)`. The serializer reuses the object it is
        // given where it can, so passing the source would fill the source — and passing null would
        // make every behaviour's copy depend on its serializer choosing to allocate one.
        var copy = new T();

        Serializer.Read<T>(Serializer.ToBytes(typed), ref copy);
        return copy;
    }

    /// <inheritdoc />
    public Behavior? Attached(BehaviorStore store, Entity entity) {
        ArgumentNullException.ThrowIfNull(store);
        return store.Get<T>(entity);
    }

    /// <inheritdoc />
    public void AttachTo(BehaviorStore store, Entity entity, object behavior) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(behavior);

        if (behavior is not T typed) {
            throw new ArgumentException($"'{behavior.GetType()}' is not a {typeof(T)}.", nameof(behavior));
        }

        RemoveFrom(store, entity);

        // ⚠ `Add<T>` and not `Add<Behavior>`, which is the whole reason this type is generic. See
        // `ISceneBehaviorBinder`.
        store.Add(entity, typed);
    }

    /// <inheritdoc />
    public bool RemoveFrom(BehaviorStore store, Entity entity) {
        ArgumentNullException.ThrowIfNull(store);

        if (store.Get<T>(entity) is not { } existing) {
            return false;
        }

        return store.Remove(existing);
    }
}
