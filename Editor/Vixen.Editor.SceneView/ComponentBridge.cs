// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Rendering.Ecs;
using Vixen.Editor.Core;
using Vixen.Engine.Scenes;

namespace Vixen.Editor.SceneView;

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
    /// <summary>What the panel calls it.</summary>
    string Name { get; }

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
    public string Name { get; }

    /// <inheritdoc />
    public Type ComponentType => typeof(T);

    /// <summary>Describes a component.</summary>
    /// <param name="name">What the panel calls it.</param>
    /// <param name="initial">What a freshly added one holds, or <see langword="null" /> for the default.</param>
    public ComponentBridge(string name, Func<T>? initial = null) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        Name = name;
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
    readonly Func<object>? initial;

    /// <inheritdoc />
    public string Name => binder.Name;

    /// <inheritdoc />
    public Type ComponentType => binder.ComponentType;

    /// <summary>Wraps a registered binder.</summary>
    /// <param name="binder">The binder.</param>
    /// <param name="initial">
    ///     What a freshly added one holds, or <see langword="null" /> for a zeroed struct.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>Zero is the right default for most components and is wrong for a few, so the few say
    ///     so.</b> A zeroed <c>Light</c> has no colour, no intensity and no range, and adding one would
    ///     put a black light on the entity — which looks like the renderer is broken rather than like a
    ///     field needs filling in. A zeroed <c>Camera</c> has a zero far plane and every matrix derived
    ///     from it is degenerate. Both types already have a factory saying what a usable one is; this is
    ///     how a panel reaches it, and everything with no answer keeps the zero.
    /// </remarks>
    public SceneComponentBridge(ISceneComponentBinder binder, Func<object>? initial = null) {
        ArgumentNullException.ThrowIfNull(binder);

        this.binder = binder;
        this.initial = initial;
    }

    /// <inheritdoc />
    public bool Has(World world, Entity entity) => binder.Has(world, entity);

    /// <inheritdoc />
    public object Read(World world, Entity entity) => binder.ValueOn(world, entity);

    /// <inheritdoc />
    public void Write(World world, Entity entity, object value) => binder.AddTo(world, entity, value);

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>A tag has no value and <c>Activator</c> is right for the rest.</b> A component is a
    ///     struct — the ECS stores them in chunks — so its default is a zeroed one, which is what
    ///     adding a component in every editor gives you before you edit it.
    /// </remarks>
    public object Create() =>
        initial is not null
            ? initial()
            : binder.IsTag
                ? new object()
                : Activator.CreateInstance(binder.ComponentType) ?? new object();

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
