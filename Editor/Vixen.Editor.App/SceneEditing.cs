// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Engine.Transforms;
using Vixen.Rendering.Ecs;

namespace Vixen.Editor.App;

/// <summary>The editor's <see cref="ISceneEditing" />: the entity and hierarchy verbs, moved out of the application.</summary>
/// <remarks>
///     <para>
///         <b>Moved, not rewritten (#1290).</b> Every method here was a private method in
///         <c>EditorParity.cs</c>'s "Selection and hierarchy verbs" block, reading <c>scene</c>,
///         <c>world</c>, <c>Viewport</c> and <c>probe</c> off the application. They read the same
///         things now through three callbacks the composition root supplies, which is all of their
///         state — so a test reaches them with a <see cref="SceneDocument" /> and nothing else.
///     </para>
///     <para>
///         ⚠ <b>In <c>Vixen.Editor.App</c> rather than beside the interface</b>, because two
///         verbs name their undo entry by an <c>EditorStrings</c> id and
///         <c>Vixen.Editor.SceneView</c> does not reference the chrome. The interface is where a
///         plugin can see it; this is where the strings are.
///     </para>
/// </remarks>
sealed class SceneEditing : ISceneEditing {
    /// <summary>What an entity's <c>LodLevel</c> is written through, so the write is undoable.</summary>
    /// <remarks>
    ///     ⚠ <b>The same seam the component panel writes through, rather than a bare
    ///     <c>World.Add</c>.</b> A command that added the component directly would put a change on
    ///     the scene that Ctrl+Z cannot take back — and undoing the reparent beside it <em>would</em>
    ///     work, so the half-undone state is five meshes back where they were, each still claiming to
    ///     be a level of a group that no longer exists.
    /// </remarks>
    static readonly ComponentBridge<LodLevel> LodLevels = new("LodLevel");

    readonly Func<SceneDocument> active;
    readonly Func<EditorCamera?> view;
    readonly Func<ISceneProbe?> probe;

    /// <summary>Builds the verbs over the composition root's answers.</summary>
    /// <param name="active">Which scene is active — asked every time, because it changes.</param>
    /// <param name="view">The focused pane's camera, or <see langword="null" />.</param>
    /// <param name="probe">What a ray hits in the active scene, or <see langword="null" />.</param>
    public SceneEditing(Func<SceneDocument> active, Func<EditorCamera?>? view = null, Func<ISceneProbe?>? probe = null) {
        ArgumentNullException.ThrowIfNull(active);

        this.active = active;
        this.view = view ?? (static () => null);
        this.probe = probe ?? (static () => null);
    }

    /// <inheritdoc />
    public SceneDocument Active => active();

    /// <inheritdoc />
    public EditorCamera? View => view();

    /// <inheritdoc />
    public event Action<ISceneEditing>? HierarchyChanged;

    World World => Active.World;

    /// <inheritdoc />
    public void InvertSelection() {
        var scene = Active;
        var selected = scene.Selection.ToHashSet();
        scene.Selection.Set([.. scene.Entities.Where(entity => !selected.Contains(entity))]);
    }

    /// <inheritdoc />
    public void SelectChildren() {
        var scene = Active;
        var world = World;
        List<Entity> children = [];

        foreach (var entity in scene.Selection) {
            // ⚠ Enumerated rather than added as a range. `ChildrenOf` gives back a struct sequence
            // over the sibling list, which is what keeps walking a hierarchy allocation-free — and
            // `AddRange` would box it into an enumerator per parent for no reason at all.
            foreach (var child in Hierarchy.ChildrenOf(world, entity)) {
                children.Add(child);
            }
        }

        if (children.Count > 0) {
            scene.Selection.Set(children);
        }
    }

    /// <inheritdoc />
    public void SelectParent() {
        var scene = Active;
        var world = World;
        List<Entity> parents = [];

        foreach (var entity in scene.Selection) {
            if (Hierarchy.ParentOf(world, entity) is var parent && parent != Entity.Null && !parents.Contains(parent)) {
                parents.Add(parent);
            }
        }

        if (parents.Count > 0) {
            scene.Selection.Set(parents);
        }
    }

    /// <inheritdoc />
    public Entity CreateChild() {
        var scene = Active;

        if (scene.Selection.Count == 0) {
            return Entity.Null;
        }

        var created = scene.Create("Entity", LocalTransform.Identity, scene.Selection[0]);
        scene.Selection.Set([created]);

        return created;
    }

    /// <summary>Puts an empty over the selection and hangs everything selected under it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Under the first selected entity's parent, not at the root.</b> Grouping three
    ///         children of a rig and having the group appear beside the rig is the behaviour that
    ///         makes Ctrl+G untrustworthy — the group belongs where the things being grouped already
    ///         were.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One undo step, and the whole selection moves as one command.</b> A create plus a
    ///         reparent per member is n+1 entries, so Ctrl+Z over a group of five put the fifth
    ///         entity back and left a group holding the other four — issue 1217. The transaction
    ///         covers the create, and the batch <see cref="SceneDocument.Reparent(IEnumerable{Entity}, Entity)" />
    ///         is the overload whose own remarks say why a loop of the single-entity one is wrong.
    ///     </para>
    /// </remarks>
    public Entity Group() {
        var scene = Active;

        if (scene.Selection.Count == 0) {
            return Entity.Null;
        }

        var members = scene.Selection.ToList();
        var parent = Hierarchy.ParentOf(World, members[0]);

        using var batch = scene.Stack.BeginTransaction(EditorStrings.CommandEntityGroup.Text);

        var group = scene.Create("Group", LocalTransform.Identity, parent);

        scene.Reparent(members, group);
        scene.Selection.Set([group]);

        return group;
    }

    /// <summary>Turns the selection into a LOD chain under a group of its own.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Issue 1173's item (2).</b> Both components are <c>[Component] [DataContract]</c>,
    ///         so the inspector has always shown them and a scene has always serialised them — and
    ///         that was the whole of the authoring story. A three-level chain was three meshes, one
    ///         empty, three reparents, three <c>LodLevel</c> components typed in by hand and a
    ///         threshold list typed into a fourth.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Selection order is level order, and the first selected is LOD 0.</b> There is no
    ///         other honest source: the meshes are three separate imports with no relationship the
    ///         editor can read, and guessing from triangle counts would be a rule that is right until
    ///         the day somebody's coarse level is denser. The numbers are a field on each child
    ///         afterwards, so a wrong guess costs one edit rather than an undo.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Halving thresholds, and they have to descend.</b> <c>LodRenderFeature.Add</c>
    ///         throws on a list that does not, so a default that merely looked plausible would be a
    ///         command whose output crashes the renderer. Halving from a half of the viewport's
    ///         height is a chain an author edits rather than one they have to invent.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One undo step for the whole gesture.</b> A create, n reparents and n component
    ///         writes is 2n+1 commands, and Ctrl+Z that took back one of them would leave a group
    ///         with four levels in it. <c>entity.group</c> beside this one still has that shape —
    ///         filed rather than widened into here.
    ///     </para>
    /// </remarks>
    public Entity GroupAsLod() {
        var scene = Active;
        var world = World;
        var members = scene.Selection.Where(world.IsAlive).ToList();

        if (members.Count < 2) {
            return Entity.Null;
        }

        using var batch = scene.Stack.BeginTransaction(EditorStrings.CommandEntityGroupLod.Text);

        var parent = Hierarchy.ParentOf(world, members[0]);

        var group = scene.Create(
            "LOD Group",
            LocalTransform.Identity,
            parent,
            entity => world.Add(entity, new LodGroupComponent { Thresholds = LodThresholds(members.Count) })
        );

        for (var level = 0; level < members.Count; level++) {
            var member = members[level];

            scene.Reparent(member, group);

            scene.Stack.Execute(
                new SetComponentCommand(
                    scene,
                    LodLevels,
                    member,
                    world.Has<LodLevel>(member) ? world.Read<LodLevel>(member) : null,
                    new LodLevel { Level = level },
                    EditorStrings.CommandEntityGroupLod.Text
                )
            );
        }

        scene.Selection.Set([group]);

        return group;
    }

    /// <summary>The switch points a chain of <paramref name="levels" /> starts with.</summary>
    /// <param name="levels">How many levels the group has.</param>
    /// <returns>
    ///     <paramref name="levels" /> − 1 screen-height fractions, descending — empty for a group of
    ///     one, which <c>LodGroupComponent</c> reads as "one level" rather than as a mistake.
    /// </returns>
    static float[] LodThresholds(int levels) {
        var thresholds = new float[Math.Max(levels - 1, 0)];
        var fraction = 0.5f;

        for (var index = 0; index < thresholds.Length; index++) {
            thresholds[index] = fraction;
            fraction *= 0.5f;
        }

        return thresholds;
    }

    /// <summary>Copies the selection in place, and selects what came out.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>No offset, which is what every editor with this key does.</b> A duplicate nudged
    ///         sideways is a duplicate somebody has to put back; a copy exactly on top of the
    ///         original, already selected, is one drag away from wherever they meant. Blockout's
    ///         array tools pass an offset because a row of nine walls is what they are for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The copies become the selection, and the originals stop being it.</b> Pressing
    ///         Ctrl+D twice has to give two copies rather than a copy of a copy of the same thing —
    ///         and leaving the originals selected would make the second press duplicate them again.
    ///     </para>
    /// </remarks>
    public int DuplicateSelection() {
        var scene = Active;

        if (scene.Selection.Count == 0) {
            return 0;
        }

        List<Entity> copies = [];

        var copied = SceneClone.Duplicate(scene, scene.Selection.ToList(), Vector3.Zero, copies);

        if (copied == 0) {
            return 0;
        }

        scene.Selection.Set(copies);
        HierarchyChanged?.Invoke(this);

        return copied;
    }

    /// <summary>Makes everything selected a root, keeping where each one is in the world.</summary>
    /// <remarks>
    ///     ⚠ <b>One <c>ReparentCommand</c> for the whole selection.</b> A loop of the single-entity
    ///     overload was one undo entry per entity — issue 1217 — so a single Ctrl+Z after unparenting
    ///     five rows put one of them back under its old parent and left the other four at the root.
    /// </remarks>
    public void ClearParent() {
        var scene = Active;
        scene.Reparent(scene.Selection.ToList(), Entity.Null);
    }

    /// <inheritdoc />
    public bool SetParent(IReadOnlyList<Entity> entities, Entity parent) {
        ArgumentNullException.ThrowIfNull(entities);

        if (!Active.Reparent(entities, parent)) {
            return false;
        }

        HierarchyChanged?.Invoke(this);
        return true;
    }

    /// <inheritdoc />
    public string? WhyNotParent(IReadOnlyList<Entity> entities, Entity candidate) {
        ArgumentNullException.ThrowIfNull(entities);

        var scene = Active;
        var world = World;

        foreach (var entity in entities) {
            if (candidate == entity) {
                return "That is the entity being moved.";
            }

            if (Hierarchy.IsAncestorOf(world, entity, candidate)) {
                return $"It is inside {scene.NameOf(entity)}, which is what is moving.";
            }
        }

        // ⚠ Refused only when *every* one of them is already there. A mixed selection with one
        // entity outside still has something to move, and `ReparentCommand` drops the ones that
        // do not — so greying the row would refuse a move that is half meaningful, which is the
        // opposite of that command's own rule.
        return entities.All(entity => Hierarchy.ParentOf(world, entity) == candidate)
            ? "Already the parent."
            : null;
    }

    /// <summary>Puts the selection where the camera is, facing the way it faces.</summary>
    /// <remarks>
    ///     ⚠ <b>Through the stack as one command, so one Ctrl+Z puts all of them back.</b> A
    ///     multi-selection aligned entity by entity would be one undo step per object, which is the
    ///     shape of every "undo did not undo what I did" report. The write goes to
    ///     <c>LocalTransform</c> and <c>TransformSystem</c> turns it into the matrix the viewport
    ///     draws from — see <c>EditorApplication.ResolveTransforms</c>.
    /// </remarks>
    public void AlignWithView() {
        if (View is not { } camera) {
            return;
        }

        var scene = Active;
        var world = World;

        var targets = scene.Selection
            .Where(entity => world.Has<LocalTransform>(entity))
            .Select(entity => (Entity: entity, Was: world.Read<LocalTransform>(entity)))
            .ToList();

        if (targets.Count == 0) {
            return;
        }

        var position = camera.Position;

        // ⚠ From the camera's own yaw and pitch rather than from `EditorCamera.Rotation`, which is a
        // basis matrix and which nothing here can turn back into a quaternion — the maths library
        // deliberately has no matrix-to-quaternion path. The two agree: yaw about +Y then pitch
        // about +X takes −Z to exactly the vector `EditorCamera.Forward` computes, which is the
        // engine's forward (Conventions.md § Handedness).
        var rotation = Quaternion.FromYawPitchRoll(camera.Yaw, camera.Pitch, 0f);

        scene.Stack.Execute(
            new DelegateCommand(
                "Align With View",
                _ => {
                    foreach (var (entity, was) in targets) {
                        world.Set(entity, was with { Position = position, Rotation = rotation });
                    }
                },
                _ => {
                    foreach (var (entity, was) in targets) {
                        world.Set(entity, was);
                    }
                }
            )
        );
    }

    /// <summary>Puts the selection where the camera is looking.</summary>
    /// <remarks>
    ///     ⚠ <b>At the pivot, not at the eye, and that is what makes it different from Align With
    ///     View.</b> The pivot is the point the view orbits and therefore the point in the middle of
    ///     the pane; the eye is where you are standing. Moving something to the eye puts it inside the
    ///     near plane, which reads as the object having vanished.
    /// </remarks>
    public void MoveToView() {
        if (View is not { } camera) {
            return;
        }

        var scene = Active;
        var world = World;
        var pivot = camera.Pivot;

        // The middle of what is selected, so a group keeps its shape rather than collapsing onto one
        // point — the same rule `TransformGizmo`'s centre pivot follows.
        var centre = Vector3.Zero;
        var counted = 0;

        var targets = scene.Selection
            .Where(entity => world.Has<LocalTransform>(entity))
            .Select(entity => (Entity: entity, Was: world.Read<LocalTransform>(entity)))
            .ToList();

        foreach (var (entity, _) in targets) {
            if (world.Has<WorldTransform>(entity)) {
                centre += world.Read<WorldTransform>(entity).Value.Translation;
                counted++;
            }
        }

        if (counted == 0) {
            return;
        }

        var offset = pivot - (centre / counted);
        Displace("Move To View", targets, _ => offset);
    }

    /// <summary>Drops the selection onto whatever is under it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Straight down from each entity's own origin, ignoring the selection.</b> A ray
    ///         that could hit the thing being dropped answers "zero" at once, which is a Snap To Floor
    ///         that never moves anything — and with several things selected, one of them landing on
    ///         another is worse than nothing happening.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An entity with nothing under it falls to the ground plane rather than not
    ///         moving.</b> A block-out scene has no floor geometry at all, which is exactly the scene
    ///         somebody is placing things in; a verb that only worked once there was something to
    ///         land on would be one nobody would find out worked.
    ///     </para>
    /// </remarks>
    public void SnapToFloor() {
        var scene = Active;
        var world = World;

        var targets = scene.Selection
            .Where(entity => world.Has<LocalTransform>(entity) && world.Has<WorldTransform>(entity))
            .Select(entity => (Entity: entity, Was: world.Read<LocalTransform>(entity)))
            .ToList();

        if (targets.Count == 0) {
            return;
        }

        var ignore = scene.Selection.Items;
        var surfaces = probe();

        Displace(
            "Snap To Floor",
            targets,
            entity => {
                var from = world.Read<WorldTransform>(entity).Value.Translation;
                var ray = new Ray(from, -Vector3.UnitY);

                var landed = surfaces is not null && surfaces.Raycast(ray, ignore, out var hit)
                    ? hit.Point
                    : new Vector3(from.X, 0f, from.Z);

                return landed - from;
            }
        );
    }

    /// <summary>Moves each target by a world-space offset, as one undo entry.</summary>
    /// <param name="name">What the history calls it.</param>
    /// <param name="targets">The entities and the local transforms they held.</param>
    /// <param name="offsetOf">How far each moves, in world space.</param>
    internal void Displace(
        string name,
        List<(Entity Entity, LocalTransform Was)> targets,
        Func<Entity, Vector3> offsetOf
    ) {
        var world = World;
        var offsets = targets.Select(target => Local(target.Entity, offsetOf(target.Entity))).ToArray();

        Active.Stack.Execute(
            new DelegateCommand(
                name,
                _ => {
                    for (var index = 0; index < targets.Count; index++) {
                        var (entity, was) = targets[index];
                        world.Set(entity, was with { Position = was.Position + offsets[index] });
                    }
                },
                _ => {
                    foreach (var (entity, was) in targets) {
                        world.Set(entity, was);
                    }
                }
            )
        );
    }

    /// <summary>A world-space offset in the space an entity's <c>LocalTransform</c> is written in.</summary>
    Vector3 Local(Entity entity, Vector3 offset) {
        var world = World;
        var parent = new Transform(world, entity).Parent;

        return !parent.IsNull
            && world.Has<WorldTransform>(parent)
            && Matrix4x4.Invert(world.Read<WorldTransform>(parent).Value, out var inverse)
                ? Matrix4x4.TransformDirection(offset, inverse)
                : offset;
    }
}
