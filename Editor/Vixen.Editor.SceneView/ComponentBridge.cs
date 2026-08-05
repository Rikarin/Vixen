// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Engine.Behaviors;
using Vixen.Engine.Scenes;
using Vixen.Rendering.Ecs;

namespace Vixen.Editor.SceneView;

/// <summary>Which of the two things a scene can put on an entity this is.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § D5, and it is a label rather than a branch.</b> Nothing above
///         <see cref="IComponentBridge" /> behaves differently for the two — the Add menu, the
///         foldouts, the drawers, the remove button and the undo are one code path — which is the
///         whole return on that interface having existed before there was a second kind of thing to
///         put behind it. What this is for is telling a *person* apart: a list sorted by name with no
///         way to see that <c>PlayerController</c> is a script is a list that reads as a mistake.
///     </para>
///     <para>
///         ⚠ <b>It is emphatically not a ranking.</b> Doc 04's authoring rule is about scale and
///         shape: a behaviour is the right answer for logic whose instance count never justifies an
///         archetype, and a component-and-system pair is for the case that pays for itself. Framing
///         either as the beginner's option is what makes people write systems for door hinges.
///     </para>
/// </remarks>
public enum AuthoringKind : byte {
    /// <summary>A struct in a chunk. What <c>World.Set</c> writes.</summary>
    Component,

    /// <summary>A class in a <see cref="BehaviorStore" /> bucket. A script.</summary>
    Behavior
}

/// <summary>One kind of component, as something a panel can ask about by name.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>An archetype knows dense ids and a panel knows names, and the map only goes one
///         way.</b> A <see cref="ComponentTypeId" /> is handed out in first-touch order, so it means
///         nothing across two runs of the same program and cannot be shown to anybody. There is
///         therefore no "what components does this entity have" call: an inspector enumerates the
///         bridges it was given and asks each one <see cref="Has" />.
///     </para>
///     <para>
///         ⚠ <b>Two kinds of component exist in the editor and both have to appear.</b> One carries
///         <c>[Component]</c> and <c>[DataContract]</c>, so the engine's component generator declares
///         it to <c>SceneComponentRegistry</c> and a compiled scene can name it; the other —
///         <see cref="Light" />, <see cref="PrimitiveShape" /> — is editor-side and deliberately written as
///         its own field of the scene file, because a scene naming a type no <i>build</i> declares is
///         what a content compile refuses. An inspector that showed only the first would omit the light
///         on the light, which is the most obviously missing row in the panel.
///     </para>
/// </remarks>
public interface IComponentBridge {
    /// <summary>Which of the two kinds of authoring unit this is.</summary>
    /// <inheritdoc cref="AuthoringKind" select="remarks" />
    AuthoringKind Kind { get; }

    /// <summary>What it is called in a file: the serializer's alias, and its identity here.</summary>
    /// <remarks>
    ///     ⚠ <b>Not what the panel draws — see <see cref="DisplayName" />.</b> This is the spelling a
    ///     <c>.vxscene</c> carries and the key a preferences file holds, so it must not change when
    ///     somebody decides a component reads better with a space in it.
    /// </remarks>
    string Name { get; }

    /// <summary>What a person reads: the foldout's title and the Add Component menu's line.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Distinct from <see cref="Name" />, and it was not always.</b> The panel began with
    ///         hand-written bridges that carried a written-out name — <c>MeshShape</c> was offered as
    ///         "Mesh Shape" — and when those became registry entries the label silently became the
    ///         serializer's alias. What a user then read in an inspector was a type name, one row
    ///         above a member the same panel had written out as <c>Cone Inner Angle</c>.
    ///     </para>
    ///     <para>
    ///         Derived rather than declared, through <see cref="Core.EditorNames.Humanise" />: a
    ///         plugin's component and a game's own get a readable name with nothing asked of either,
    ///         which is the bargain the rest of this panel already makes.
    ///     </para>
    /// </remarks>
    string DisplayName { get; }

    /// <summary>The CLR type, which is what the rows are drawn from.</summary>
    Type ComponentType { get; }

    /// <summary>Whether an entity carries one.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>Whether it does.</returns>
    bool Has(World world, Entity entity);

    /// <summary>Reads it off an entity, boxed.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>The value.</returns>
    /// <remarks>
    ///     ⚠ <b>A copy, and everything downstream depends on knowing that.</b> The rows edit the box;
    ///     the entity only changes when <see cref="Write" /> puts it back. That is what makes the
    ///     whole component one undo step instead of one per field.
    /// </remarks>
    object Read(World world, Entity entity);

    /// <summary>Puts a value on an entity, adding the component if it is not there.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="value">The value, boxed.</param>
    void Write(World world, Entity entity, object value);

    /// <summary>A fresh one, for adding a component that was not there.</summary>
    /// <returns>The default value.</returns>
    object Create();

    /// <summary>Takes it off an entity.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>Whether it was there.</returns>
    bool Remove(World world, Entity entity);
}

/// <summary>A bridge over a component this assembly knows the type of.</summary>
/// <typeparam name="T">The component.</typeparam>
/// <remarks>
///     The typed half exists because <c>World.Add</c>, <c>Read</c> and <c>Remove</c> are generic —
///     closing the generic once at construction keeps every call statically bound and boxes only at
///     the boundary the panel actually needs a box at.
/// </remarks>
public sealed class ComponentBridge<T> : IComponentBridge where T : struct {
    readonly Func<T>? initial;

    /// <inheritdoc />
    public AuthoringKind Kind => AuthoringKind.Component;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    public Type ComponentType => typeof(T);

    /// <summary>Describes a component.</summary>
    /// <param name="name">Its identity: the name a file would carry.</param>
    /// <param name="initial">What a freshly added one holds, or <see langword="null" /> for the default.</param>
    /// <param name="displayName">
    ///     What the panel draws, or <see langword="null" /> to write <paramref name="name" /> out.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>The override exists and is meant to stay unused.</b> It is
    ///     <c>InspectorMember</c>'s arrangement — a name, and a label that is the name written out
    ///     unless somebody had a reason — and the reason has to be that the derivation is wrong for
    ///     this one component, not that somebody preferred different words.
    /// </remarks>
    public ComponentBridge(string name, Func<T>? initial = null, string? displayName = null) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        Name = name;
        DisplayName = string.IsNullOrEmpty(displayName) ? EditorNames.Humanise(name) : displayName;
        this.initial = initial;
    }

    /// <inheritdoc />
    public bool Has(World world, Entity entity) {
        ArgumentNullException.ThrowIfNull(world);

        return world.Has<T>(entity);
    }

    /// <inheritdoc />
    public object Read(World world, Entity entity) {
        ArgumentNullException.ThrowIfNull(world);

        return world.Read<T>(entity);
    }

    /// <inheritdoc />
    public void Write(World world, Entity entity, object value) {
        ArgumentNullException.ThrowIfNull(world);

        var typed = (T) value;

        if (world.Has<T>(entity)) {
            world.Set(entity, in typed);
        } else {
            world.Add(entity, in typed);
        }
    }

    /// <inheritdoc />
    public object Create() => initial is null ? default(T) : initial();

    /// <inheritdoc />
    public bool Remove(World world, Entity entity) {
        ArgumentNullException.ThrowIfNull(world);

        if (!world.Has<T>(entity)) {
            return false;
        }

        world.Remove<T>(entity);
        return true;
    }
}

/// <summary>A bridge over a component a compiled scene may name.</summary>
/// <remarks>
///     ⚠ <b>The set is the <i>game's</i>, and that is the whole value of going through the
///     registry.</b> A game registers its components so its scenes can carry them; the editor's
///     component panel then draws them with nothing else asked of anybody, because the rows come
///     from the same <c>[DataContract]</c> description the serializer uses.
/// </remarks>
public sealed class SceneComponentBridge : IComponentBridge {
    readonly ISceneComponentBinder binder;

    /// <inheritdoc />
    public AuthoringKind Kind => AuthoringKind.Component;

    /// <inheritdoc />
    public string Name => binder.Name;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Computed once rather than per read.</b> Every bind of every foldout asks for this, and
    ///     a binder's alias does not change for the life of the process — see
    ///     <c>SceneComponentRegistry</c>, where it is the serializer's and is fixed at build time.
    /// </remarks>
    public string DisplayName { get; }

    /// <inheritdoc />
    public Type ComponentType => binder.ComponentType;

    /// <summary>Wraps a registered binder.</summary>
    /// <param name="binder">The binder.</param>
    /// <remarks>
    ///     ⚠ <b>Zero is the right default for most components and is wrong for a few, so the few say
    ///     so — and they say it on themselves.</b> A zeroed <c>Light</c> has no colour, no intensity
    ///     and no range, and adding one would put a black light on the entity, which looks like the
    ///     renderer is broken rather than like a field needs filling in. The component declares
    ///     <c>IDefaultComponent&lt;itself&gt;</c> and the binder carries it here; this took a list of
    ///     types kept in the editor's own assembly until it did, which a game's component could never
    ///     appear on.
    /// </remarks>
    public SceneComponentBridge(ISceneComponentBinder binder) {
        ArgumentNullException.ThrowIfNull(binder);

        this.binder = binder;

        DisplayName = EditorNames.Humanise(binder.Name);
    }

    /// <inheritdoc />
    public bool Has(World world, Entity entity) => binder.Has(world, entity);

    /// <inheritdoc />
    public object Read(World world, Entity entity) => binder.ValueOn(world, entity);

    /// <inheritdoc />
    public void Write(World world, Entity entity, object value) => binder.AddTo(world, entity, value);

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Through the binder rather than through <c>Activator</c>, which is not the same value.</b>
    ///     <c>Activator.CreateInstance</c> runs a struct's parameterless constructor and its field
    ///     initializers where it has them, and the ECS paths that hand back a zeroed row do not — so
    ///     the two disagreed for exactly the types careless enough to write one, silently. The binder
    ///     answers with the declared default or with <c>default(T)</c>, and those are the only two
    ///     answers there are.
    /// </remarks>
    public object Create() => binder.IsTag ? new object() : binder.CreateDefault();

    /// <inheritdoc />
    public bool Remove(World world, Entity entity) => binder.RemoveFrom(world, entity);
}

/// <summary>Adding, removing and setting a component, undoably.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>One command for the whole component rather than one per field.</b> A component is
///         read as a box, edited, and written back whole — so recording each field would put a step
///         on the stack that undoes a change to a copy nobody can see, and the visible change would
///         belong to a different step.
///     </para>
///     <para>
///         ⚠ <b>Removing records what was there, so undo puts the values back and not just the
///         column.</b> An undo that restored a zeroed component would be worse than none: the
///         hierarchy would look right and the light would be black.
///     </para>
/// </remarks>
public sealed class SetComponentCommand : IEditorCommand {
    readonly SceneDocument document;
    readonly IComponentBridge bridge;
    readonly Entity entity;
    readonly object? before;
    readonly object? after;

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Records a change to one component on one entity.</summary>
    /// <param name="document">The scene it belongs to.</param>
    /// <param name="bridge">Which component.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="before">What it held, or <see langword="null" /> if it was not there.</param>
    /// <param name="after">What it should hold, or <see langword="null" /> to take it off.</param>
    /// <param name="name">What the undo history calls it.</param>
    public SetComponentCommand(
        SceneDocument document,
        IComponentBridge bridge,
        Entity entity,
        object? before,
        object? after,
        string name
    ) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentException.ThrowIfNullOrEmpty(name);

        this.document = document;
        this.bridge = bridge;
        this.entity = entity;
        this.before = before;
        this.after = after;

        Name = name;
    }

    /// <inheritdoc />
    public void Do(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        Apply(after);
        context.Touch(document);
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        Apply(before);
        context.Touch(document);
    }

    void Apply(object? value) {
        if (!document.World.IsAlive(entity)) {
            // The entity went while this was on the stack, which an undo of a delete puts right on
            // its own. Throwing here would take the editor down mid-undo.
            return;
        }

        if (value is null) {
            bridge.Remove(document.World, entity);
        } else {
            bridge.Write(document.World, entity, value);
        }

        // ⚠ Only when the *set* changed. Editing a component's fields writes the whole box back and
        // the rows are already showing it; telling the panel would rebuild it under the pointer of
        // whoever is dragging a slider.
        if (before is null || after is null) {
            document.Recomposed(entity);
        }
    }
}

/// <summary>A behaviour, as the component panel's idea of a component.</summary>
/// <remarks>
///     <para>
///         <b>The seam that makes a <c>Behavior</c> authorable, and it is one class because the panel
///         above it only knows <see cref="IComponentBridge" />.</b> The Add Component menu, the
///         foldouts, the drawers, the remove button and the drag-to-reorder all work on a behaviour
///         with nothing added to any of them — which is the whole return on that interface having
///         existed before there was a second kind of thing to put behind it.
///     </para>
///     <para>
///         ⚠ <b><see cref="Read" /> hands back a <i>copy</i>, which is what lets everything above
///         stay one code path.</b> A component is a struct, so reading one out of a chunk gives the
///         panel a box it can edit freely and write back as a single
///         <see cref="SetComponentCommand" />. A behaviour is a class, and a panel handed the live
///         instance would edit the very object it was about to record as the "before" — so every
///         undo would restore what the edit had already changed, which is to say no undo at all.
///         Copying at the read makes the two kinds indistinguishable from here up, down to sharing
///         the command.
///     </para>
///     <para>
///         ⚠ <b>The consequence is that editing a behaviour replaces the instance on the entity.</b>
///         Nothing in an editor holds a reference to one — the lifecycle is not running, which is
///         <c>SceneDocument.Behaviors</c>' point — so identity is not something anything here can
///         observe. It would be in play mode, which runs against a store of its own.
///     </para>
///     <para>
///         ⚠ <b>The store is the document's, not the world's.</b> <see cref="IComponentBridge" /> is
///         written in terms of a <c>World</c> because a component lives in one; a behaviour lives in
///         a <see cref="BehaviorStore" /> beside it, so this closes over the document's. A bridge is
///         therefore per-document rather than per-process, which is the one way behaviours differ
///         from components in how the editor holds them.
///     </para>
/// </remarks>
public sealed class BehaviorBridge : IComponentBridge {
    readonly ISceneBehaviorBinder binder;
    readonly Func<BehaviorStore?> store;

    /// <inheritdoc />
    public AuthoringKind Kind => AuthoringKind.Behavior;

    /// <inheritdoc />
    public string Name => binder.Name;

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    public Type ComponentType => binder.BehaviorType;

    /// <summary>Wraps a registered behaviour.</summary>
    /// <param name="binder">The binder.</param>
    /// <param name="store">
    ///     Where behaviours live, asked for on each call rather than held — the document a panel is
    ///     showing changes, and a bridge that captured one store would keep answering about a scene
    ///     nobody has open.
    /// </param>
    public BehaviorBridge(ISceneBehaviorBinder binder, Func<BehaviorStore?> store) {
        ArgumentNullException.ThrowIfNull(binder);
        ArgumentNullException.ThrowIfNull(store);

        this.binder = binder;
        this.store = store;

        DisplayName = EditorNames.Humanise(binder.Name);
    }

    /// <inheritdoc />
    public bool Has(World world, Entity entity) => Attached(entity) is not null;

    /// <inheritdoc />
    /// <inheritdoc cref="BehaviorBridge" select="remarks/para[2]" />
    /// <exception cref="InvalidOperationException">The entity does not carry one.</exception>
    public object Read(World world, Entity entity) =>
        binder.Copy(
            Attached(entity)
            ?? throw new InvalidOperationException(
                $"The entity does not carry a '{binder.Name}', so there is nothing to read. `Has` is what "
                + "answers that question."
            )
        );

    /// <inheritdoc />
    public void Write(World world, Entity entity, object value) {
        if (store() is { } behaviors) {
            binder.AttachTo(behaviors, entity, value);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Its constructor's own defaults, where a component gets a zeroed struct.</b> That is
    ///     the one place a behaviour is the easier of the two: a behaviour is a class and is
    ///     constructed, so field initialisers run and an author writes <c>= 3f</c>. A component is a
    ///     row in a chunk and is never constructed, which is why the same intent has to be declared —
    ///     <c>IDefaultComponent</c> — rather than written as an initialiser that would run on some
    ///     paths and not others.
    /// </remarks>
    public object Create() => binder.Create();

    /// <inheritdoc />
    public bool Remove(World world, Entity entity) =>
        store() is { } behaviors && binder.RemoveFrom(behaviors, entity);

    Behavior? Attached(Entity entity) => store() is { } behaviors ? binder.Attached(behaviors, entity) : null;
}
