// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
using Vixen.Editor.Inspector;
using Vixen.Editor.SceneView;
using Vixen.Engine.Transforms;

namespace Vixen.Editor.AssetEditors.Prefabs;

/// <summary>What an inspected object was made from, answered out of the instance's own claims.</summary>
/// <remarks>
///     <para>
///         <b>Row 6 of doc 47 § 7, and the reason it was not wiring.</b> The presentation — the bolded
///         row, the enabled Revert item — has existed since the inspector was written and had never
///         been shown a pairing in the running editor, because nothing assigned
///         <c>InspectorView.Prefab</c>. This is what does, and two decisions had to be made first
///         rather than silently.
///     </para>
///     <para>
///         ⚠⚠ <b>Overridden means "the instance claims it", read from
///         <see cref="SceneDocument.Prefabs" />.</b> The previous implementation compared the
///         inspected object's value with the template's, which is model (A) of doc 47 § 3 and is
///         rejected there: a comparison cannot see an override to <c>0</c>, nor an override to a value
///         that happens to equal the template's, so the row would quietly stop being marked and the
///         revert button would grey out on exactly the edits an author most wants back. The file has
///         the right answer written down — <c>overrides:</c> is a list of <i>names</i> — and this
///         reads it.
///     </para>
///     <para>
///         ⚠ <b><see cref="SceneEntity" />'s position and rotation are world space and
///         <see cref="SceneEntityData" />'s are relative to the parent.</b> The two objects a pairing
///         joins do not mean the same thing by "position", so <see cref="TryGetPrefabValue" /> takes
///         the template's value through the instance's parent before handing it back — otherwise a
///         revert would write a local value into a world-space setter and put the entity somewhere
///         nobody asked for. It is a correctness bug that would have looked like a UI glitch.
///         <c>Scale</c> needs no conversion: both sides are relative to the parent, which is why
///         <c>SceneEntity.Scale</c> reads <c>LocalScale</c>.
///     </para>
///     <para>
///         <b>Objects, not entities, because that is what an inspector edits.</b> The shell pairs each
///         object it shows with the entity it belongs to and, for a component, the alias that names
///         it — see <see cref="Link" /> — and everything else is a member path built from that.
///     </para>
/// </remarks>
public sealed class PrefabSource : IPrefabSource {
    readonly SceneDocument document;
    readonly AssetDatabase assets;
    readonly Dictionary<object, Pairing> shown = new(ReferenceEqualityComparer.Instance);

    /// <summary>The templates opened so far, with a miss remembered as a null.</summary>
    /// <remarks>
    ///     ⚠ <b>A miss is cached too.</b> Reading a row asks for the template value once per repaint;
    ///     a prefab that is not in the project would otherwise be looked up, and reported missing,
    ///     every one of those times. <see cref="Clear" /> drops the cache with the pairings, so a
    ///     prefab edited and saved is re-read the next time a selection is shown.
    /// </remarks>
    readonly Dictionary<string, SceneFile?> templates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many objects are paired.</summary>
    public int Count => shown.Count;

    /// <summary>Pairs an inspector's objects with the scene they belong to.</summary>
    /// <param name="document">The scene document, which is where the claims live.</param>
    /// <param name="assets">The project's index, which is what turns a prefab reference into a file.</param>
    public PrefabSource(SceneDocument document, AssetDatabase assets) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);

        this.document = document;
        this.assets = assets;
    }

    /// <summary>Says which entity an inspected object stands for, and which part of it.</summary>
    /// <param name="target">The object an inspector will show.</param>
    /// <param name="entity">The entity it stands for.</param>
    /// <param name="alias">The <c>[DataContract]</c> alias when the object is a component, or empty
    ///     when it is the entity itself.</param>
    /// <remarks>
    ///     ⚠ <b>The alias and not the component's type.</b> A member path in the format is
    ///     <c>Alias.Member</c> — doc 47 § 4 — and the alias is what a <c>.vxscene</c> already writes
    ///     for the component, so building the path from anything else would be a second spelling of
    ///     one name.
    /// </remarks>
    public void Link(object target, Entity entity, string alias = "") {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(alias);

        shown[target] = new(entity, alias);
    }

    /// <summary>Forgets a pairing.</summary>
    /// <param name="target">The object.</param>
    /// <returns>Whether it had one.</returns>
    /// <remarks>
    ///     ⚠ <b>What a re-read of a boxed component needs.</b> A component is inspected as a box that
    ///     is replaced rather than mutated whenever the entity is re-read, so a panel that only ever
    ///     linked would accumulate a pairing per undo, all of them naming boxes nobody can see.
    /// </remarks>
    public bool Unlink(object target) {
        ArgumentNullException.ThrowIfNull(target);
        return shown.Remove(target);
    }

    /// <summary>Forgets every pairing, and every template read for one.</summary>
    public void Clear() {
        shown.Clear();
        templates.Clear();
    }

    /// <inheritdoc />
    public bool IsOverridden(object target, InspectorMember member) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(member);

        return shown.TryGetValue(target, out var pairing)
            && document.Prefabs.IsOverridden(pairing.Entity, pairing.PathTo(member));
    }

    /// <inheritdoc />
    public bool TryGetPrefabValue(object target, InspectorMember member, out object? value) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(member);

        value = null;

        if (!shown.TryGetValue(target, out var pairing)
            || !TryFindTemplate(pairing.Entity, out var node)
            || !PrefabOverrides.TryRead(node, pairing.PathTo(member), out var stored)) {
            return false;
        }

        // Only the entity's own members can be in the wrong space: a component's members are values
        // and mean the same thing on both sides of the pairing.
        value = pairing.Alias.Length == 0 ? ToWorldSpace(pairing.Entity, member.Name, stored) : stored;
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Undoable, on the scene's own stack, because it is a change to the scene.</b> A claim
    ///     is written into the file next to the value it is about, so a Ctrl+Z that put a lamp's
    ///     intensity back and left the instance still claiming it would leave the level saying
    ///     something its author never said — and would block the template's next change to that lamp
    ///     for ever.
    /// </remarks>
    public bool Claim(object target, InspectorMember member) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(member);

        if (!shown.TryGetValue(target, out var pairing) || !document.Prefabs.TryGet(pairing.Entity, out _)) {
            return false;
        }

        var entity = pairing.Entity;
        var path = pairing.PathTo(member);

        if (document.Prefabs.IsOverridden(entity, path)) {
            return false;
        }

        document.Stack.Execute(
            new DelegateCommand(
                $"Override {member.DisplayName}",
                _ => document.Prefabs.Mark(entity, path),
                _ => document.Prefabs.Clear(entity, path)
            )
        );

        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Forgetting the claim is the whole of a revert whenever the value already matches.</b>
    ///     Doc 47 § 4's override to a value identical to the template's is that case exactly, and
    ///     nothing else in a revert would do anything for it.
    /// </remarks>
    public bool Release(object target, InspectorMember member) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(member);

        if (!shown.TryGetValue(target, out var pairing)) {
            return false;
        }

        var entity = pairing.Entity;
        var path = pairing.PathTo(member);

        if (!document.Prefabs.IsOverridden(entity, path)) {
            return false;
        }

        document.Stack.Execute(
            new DelegateCommand(
                $"Revert {member.DisplayName}",
                _ => document.Prefabs.Clear(entity, path),
                _ => document.Prefabs.Mark(entity, path)
            )
        );

        return true;
    }

    /// <summary>The template entity one of the scene's entities is to be read against.</summary>
    /// <remarks>
    ///     ⚠ <b>The outer template first, which is nesting's whole rule and is
    ///     <c>PrefabOverrides.TryPair</c>'s, restated over the live world.</b> A run sitting inside an
    ///     instance of another prefab <i>is</i> that prefab's copy of it, the outer author's overrides
    ///     included, so reaching past it to the inner prefab's own file would show — and revert to —
    ///     values the outer template does not have.
    ///     <para>
    ///         ⚠ And a run inside an instance whose template cannot be opened is declined rather than
    ///         guessed at, for the reason the reconciler declines it: without the outer file there is
    ///         no telling a nested node from a separate instance somebody dragged in under one, and
    ///         the guess that is available is the destructive one.
    ///     </para>
    /// </remarks>
    bool TryFindTemplate(Entity entity, [MaybeNullWhen(false)] out SceneEntityData node) {
        node = null;

        if (!document.Prefabs.TryGet(entity, out var link)) {
            return false;
        }

        var reference = new AssetReference(link.Prefab).ToString();

        if (OuterOf(entity, link.Prefab) is { } outer) {
            return TryOpenTemplate(new AssetReference(outer.Prefab).ToString(), out var above)
                && PrefabOverrides.TryFindLink(above, reference, link.Source, out node);
        }

        return TryOpenTemplate(reference, out var own) && PrefabOverrides.TryFind(own, link.Source, out node);
    }

    /// <summary>The instance this entity's run sits inside, if it sits inside one.</summary>
    /// <remarks>
    ///     Walked through anything in between: an ancestor sharing this prefab is the same run — the
    ///     rule <c>SceneDocument.TryGetInstanceRoot</c> uses — and an unpacked or hand-made entity
    ///     between the two is a hierarchy somebody built, which stopping at would silently unnest the
    ///     run.
    /// </remarks>
    PrefabLink? OuterOf(Entity entity, AssetId prefab) {
        var world = document.World;

        for (var above = Hierarchy.ParentOf(world, entity);
             !above.IsNull && world.IsAlive(above);
             above = Hierarchy.ParentOf(world, above)) {
            if (document.Prefabs.TryGet(above, out var link) && link.Prefab != prefab) {
                return link;
            }
        }

        return null;
    }

    /// <summary>Opens a prefab and composes it against the prefabs it holds instances of.</summary>
    /// <remarks>
    ///     ⚠ <b>Composed, for the reason the open path composes a scene's templates.</b> A
    ///     <c>.vxprefab</c> holding an instance of another prefab carries that instance's values as
    ///     they were the day it was saved, so an inspector reading it raw would show a template value
    ///     that the level itself does not have — the level was reconciled against the composed one
    ///     when it opened. One level, exactly as doc 47 § 7b restricts it: the prefabs opened for this
    ///     step are not composed in their turn.
    /// </remarks>
    bool TryOpenTemplate(string reference, [MaybeNullWhen(false)] out SceneFile file) {
        if (templates.TryGetValue(reference, out var cached)) {
            file = cached;
            return cached is not null;
        }

        if (!PrefabReconcile.TryOpen(reference, assets, out var opened, out _)) {
            templates[reference] = null;
            file = null;

            return false;
        }

        PrefabReconcile.Run(opened, assets);

        templates[reference] = opened;
        file = opened;

        return true;
    }

    /// <summary>Takes a template's parent-relative transform value into the space the inspector shows.</summary>
    /// <remarks>
    ///     ⚠ <b>The rotation is <c>local * parentWorld</c> and the order is not interchangeable.</b>
    ///     Composition in this library reads left to right, so a child's own rotation is applied
    ///     before its parent's — the same equation <c>Transform.Rotation</c>'s setter solves in the
    ///     other direction. Writing the parent on the left instead gives the right answer only while
    ///     the two rotations commute.
    /// </remarks>
    object? ToWorldSpace(Entity entity, string member, object? stored) {
        var world = document.World;
        var parent = Hierarchy.ParentOf(world, entity);

        if (parent.IsNull || !world.IsAlive(parent) || !world.Has<WorldTransform>(parent)) {
            // A root entity's parent-relative value *is* its world value, which is why an instance
            // dropped at the top level needs no conversion at all.
            return stored;
        }

        return member switch {
            nameof(SceneEntityData.Position) when stored is Vector3 local =>
                new Transform(world, parent).TransformPoint(local),
            nameof(SceneEntityData.Rotation) when stored is Quaternion local =>
                local * new Transform(world, parent).Rotation,
            _ => stored
        };
    }

    /// <summary>Which entity an inspected object stands for, and which part of it.</summary>
    readonly record struct Pairing(Entity Entity, string Alias) {
        /// <summary>The member path the format spells this member as.</summary>
        public string PathTo(InspectorMember member) =>
            Alias.Length == 0 ? member.Name : $"{Alias}.{member.Name}";
    }
}
