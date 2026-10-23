// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Engine.Scenes;
using Vixen.Engine.Transforms;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;

namespace Vixen.Editor.App;

/// <summary>The editor's <see cref="ISceneEditing" />: the entity and hierarchy verbs, moved out of the application.</summary>
/// <remarks>
///     <para>
///         <b>Moved, not rewritten (#1290).</b> Every method here was a private method in
///         <c>EditorParity.cs</c>'s "Selection and hierarchy verbs" block, reading <c>scene</c>,
///         <c>world</c>, <c>Viewport</c> and <c>probe</c> off the application. They read the active
///         scene this service holds and two callbacks the composition root supplies, which is all of
///         their state — so a test reaches them with a <see cref="SceneDocument" /> and nothing else.
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

    readonly List<SceneDocument> scenes = [];
    readonly Func<EditorCamera?> view;
    readonly Func<ISceneProbe?> probe;

    SceneDocument active;

    /// <summary>What reads a scene file into a document beside the others: the composition root's.</summary>
    /// <remarks>
    ///     ⚠ <b>The application's, handed in, because the read is tangled with which files other
    ///     documents edit</b> — a scene a tab already has open is refused, not loaded a second time —
    ///     and that arbitration is the Documents context's. It ends in <see cref="Open(SceneDocument)" />.
    ///     Unset, <see cref="OpenAdditively" /> opens nothing.
    /// </remarks>
    internal Func<string, SceneDocument?>? Load { get; set; }

    /// <summary>Builds the verbs over the scene the editor starts with and the composition root's answers.</summary>
    /// <param name="first">The scene the editor starts with: the first open and the active one.</param>
    /// <param name="view">The focused pane's camera, or <see langword="null" />.</param>
    /// <param name="probe">What a ray hits in the active scene, or <see langword="null" />.</param>
    /// <remarks>
    ///     ⚠ <b>The active scene is held here, not asked of the application (#1290).</b> It used to be a
    ///     callback reading the application's <c>scene</c> field, which only the application's own
    ///     multi-scene verbs could move; now the field follows <see cref="ActiveChanged" />.
    /// </remarks>
    public SceneEditing(SceneDocument first, Func<EditorCamera?>? view = null, Func<ISceneProbe?>? probe = null) {
        ArgumentNullException.ThrowIfNull(first);

        active = first;
        scenes.Add(first);
        Watch(first);

        this.view = view ?? (static () => null);
        this.probe = probe ?? (static () => null);
    }

    /// <summary>Follows a scene's structure and names, so the outliner redraws on an edit to any open scene.</summary>
    /// <remarks>
    ///     ⚠ <b>Here and not in the application (#1293)</b>, which subscribed the first scene in its
    ///     constructor and each additive one in <c>OpenSceneAdditively</c> — two copies of one rule about
    ///     every scene this service holds. Taken back off in <see cref="Close" />.
    /// </remarks>
    void Watch(SceneDocument scene) {
        scene.StructureChanged += Followed;
        scene.Renamed += Followed;
    }

    void Followed(SceneDocument _) => Restructured();

    void Followed(SceneDocument _, Entity __) => Restructured();

    /// <summary>Follows a play session: every open scene's behaviours play, and a restore re-keys the active scene.</summary>
    /// <param name="play">The editor's session.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every open scene's store, read at every Play</b>, because the controller is built over
    ///         the <i>first</i> document's — and a behaviour authored into an additively opened scene used
    ///         to be named in <c>Unsupported</c> rather than run. A scene opened after this still plays.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every entity gets a new handle when a play-mode snapshot is restored</b>, so the
    ///         document's name and stable-id tables — both keyed by handle — name nothing afterwards.
    ///         <see cref="SceneDocument.Remap" /> was written for exactly this and nothing called it: the
    ///         outliner came back from play mode as a list of blank rows. Subscribed to the controller
    ///         rather than to <c>Left</c> so that it also covers a restore made for any other reason.
    ///     </para>
    ///     <para>
    ///         Moved out of the application's constructor (#1293): both are facts about the scenes this
    ///         service holds, not about the composition.
    ///     </para>
    /// </remarks>
    internal void Follow(EditorPlaySession play) {
        ArgumentNullException.ThrowIfNull(play);

        play.Controller.Stores = () => scenes.Select(static document => document.Behaviors);
        play.Controller.Restored += (_, translation) => {
            active.Remap(translation);
            Restructured();
        };
    }

    /// <inheritdoc />
    public SceneDocument Active => active;

    /// <inheritdoc />
    public IReadOnlyList<SceneDocument> Scenes => scenes;

    /// <inheritdoc />
    public event Action<ISceneEditing>? ActiveChanged;

    /// <inheritdoc />
    public bool Activate(SceneDocument scene) {
        ArgumentNullException.ThrowIfNull(scene);

        if (ReferenceEquals(scene, active) || !scenes.Contains(scene)) {
            return false;
        }

        active = scene;
        ActiveChanged?.Invoke(this);

        return true;
    }

    /// <summary>Says the scene's shape changed where no verb of this service changed it.</summary>
    /// <remarks>
    ///     ⚠ <b>What the application's <c>hierarchyStale = true</c> became (#1290).</b> A play-mode restore,
    ///     a scene opened over the old one, a component added, an entity renamed through the document — the
    ///     application changes the shape itself in a score of places, and the outliner is a module's that
    ///     hears <see cref="HierarchyChanged" /> rather than a field the application sets.
    /// </remarks>
    internal void Restructured() => HierarchyChanged?.Invoke(this);

    /// <summary>Says an entity's hidden or locked mark changed where no verb of this service changed it.</summary>
    /// <remarks>The application's <c>RefreshMarks()</c> calls, for the same reason as <see cref="Restructured" />.</remarks>
    internal void Remarked() => MarksChanged?.Invoke(this);

    /// <inheritdoc />
    public event Action<ISceneEditing>? ScenesChanged;

    /// <inheritdoc />
    public SceneDocument? OpenAdditively(string path) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        return Load?.Invoke(path);
    }

    /// <summary>Adds a scene the host has opened from a file, without activating it.</summary>
    /// <param name="scene">The scene.</param>
    /// <returns>Whether it was not already open.</returns>
    internal bool Open(SceneDocument scene) {
        ArgumentNullException.ThrowIfNull(scene);

        if (scenes.Contains(scene)) {
            return false;
        }

        scenes.Add(scene);
        Watch(scene);
        ScenesChanged?.Invoke(this);

        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The scene before it is activated <i>through</i> <see cref="Activate" />, so the editor
    ///         follows (#1498).</b> The application's close moved its index back and then asked to
    ///         activate that index, which it already was — so closing the active scene left everything
    ///         pointed at the closed document.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The entities go with the scene</b>, moved here from the application's close. The
    ///         world is shared, so a document closed without deleting its roots would leave its entities
    ///         in the level with nothing naming them — the multi-scene version of a leak.
    ///     </para>
    /// </remarks>
    public bool Close(SceneDocument scene) {
        ArgumentNullException.ThrowIfNull(scene);

        var index = scenes.IndexOf(scene);

        if (index <= 0) {
            return false;
        }

        if (ReferenceEquals(scene, active)) {
            Activate(scenes[index - 1]);
        }

        scenes.RemoveAt(index);

        scene.Delete([.. scene.Roots]);
        scene.Close();

        scene.StructureChanged -= Followed;
        scene.Renamed -= Followed;

        Restructured();
        ScenesChanged?.Invoke(this);

        return true;
    }

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

        // ⚠ No raise of its own: the reparent raises `StructureChanged`, which every held scene's watch
        // turns into one `HierarchyChanged` (#1293). Raising here as well was a second one in the editor.
        return Active.Reparent(entities, parent);
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

    // ── Doc 20 § Part D's selection and transform verbs ─────────────────────────────────────────
    //
    // Moved from EditorSelectionVerbs.cs (#1290's second slice), bodies unchanged except that each
    // takes what its dialog asked as a parameter. The dialogs stayed in the application; what they
    // ask is this editor's business, what is done with the answer is the service's.
    //
    // ⚠ A selection set and an isolate are editor state and are not saved, on the argument
    // `entity.toggle-hidden` makes: what you were looking at is not what ships, and an undo step
    // spent on it is a step not spent on what you changed. They are lost when the scene is
    // reloaded, because they hold entity handles and a reload mints new ones.

    /// <summary>What Copy Transform put aside, or <see langword="null" /> before anything did.</summary>
    LocalTransform? copiedTransform;

    /// <summary>The named selections, in the order they were saved.</summary>
    readonly Dictionary<string, IReadOnlyList<Entity>> selectionSets = new(StringComparer.Ordinal);

    /// <summary>What was hidden before Isolate hid everything else, or null when not isolating.</summary>
    List<Entity>? isolatedFrom;

    /// <inheritdoc />
    public event Action<ISceneEditing>? MarksChanged;

    /// <inheritdoc />
    public bool IsIsolating => isolatedFrom is not null;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, IReadOnlyList<Entity>> SelectionSets => selectionSets;

    /// <inheritdoc />
    public bool CanCopyTransform => Transformable(Active.Selection.Primary);

    /// <inheritdoc />
    public bool HasCopiedTransform => copiedTransform is not null;

    /// <summary>Whether an entity is still alive and carries a transform to write.</summary>
    /// <param name="entity">The entity, which may be a handle nothing answers to any more.</param>
    /// <returns>Whether a transform verb can touch it.</returns>
    /// <remarks>
    ///     ⚠ <b>The liveness check is not defensive tidiness: <c>World.Has</c> <i>throws</i> on a dead
    ///     handle, and an enablement predicate runs from inside a menu refresh.</b> The selection
    ///     keeps what it held across an undo that destroyed it — undoing a Duplicate is the ordinary
    ///     way to produce one — so a predicate that dereferenced the primary without asking took the
    ///     whole frame down rather than greying a button. Every other enablement in the editor reads
    ///     <c>Selection.Count</c> or a set, which is why these are the first to have met it.
    /// </remarks>
    bool Transformable(Entity entity) => World.IsAlive(entity) && World.Has<LocalTransform>(entity);

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The ancestors and the descendants stay visible, and both halves are needed.</b>
    ///         Hidden-ness is inherited (<c>SceneDocument.IsHidden</c>), so hiding a selected
    ///         entity's parent would hide the entity — and isolating a crate without what is inside
    ///         it shows an empty crate, which reads as the command having deleted something.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What was hidden before is restored, not "everything becomes visible".</b> Somebody
    ///         who had turned three eyes off and then isolated would otherwise find the three back on
    ///         afterwards, with nothing to say why — the same rule <c>SubtreeSnapshot</c> follows about
    ///         restoring exactly what was there.
    ///     </para>
    /// </remarks>
    public void Isolate() {
        var scene = Active;
        var world = World;

        if (isolatedFrom is { } was) {
            foreach (var entity in scene.Entities) {
                scene.SetHidden(entity, false);
            }

            foreach (var entity in was) {
                if (world.IsAlive(entity)) {
                    scene.SetHidden(entity, true);
                }
            }

            isolatedFrom = null;
            MarksChanged?.Invoke(this);

            return;
        }

        if (scene.Selection.Count == 0) {
            return;
        }

        HashSet<Entity> kept = [];

        foreach (var entity in scene.Selection) {
            kept.Add(entity);

            for (var parent = Hierarchy.ParentOf(world, entity); !parent.IsNull;
                 parent = Hierarchy.ParentOf(world, parent)) {
                kept.Add(parent);
            }
        }

        // Descendants in a second pass, because an entity is kept when any *selected* ancestor of it
        // is — walking down from each selected root is the cheap way to say that.
        foreach (var entity in scene.Entities) {
            for (var parent = Hierarchy.ParentOf(world, entity); !parent.IsNull;
                 parent = Hierarchy.ParentOf(world, parent)) {
                if (scene.Selection.Contains(parent)) {
                    kept.Add(entity);
                    break;
                }
            }
        }

        isolatedFrom = [.. scene.Entities.Where(scene.IsHiddenDirectly)];

        foreach (var entity in scene.Entities) {
            scene.SetHidden(entity, !kept.Contains(entity));
        }

        MarksChanged?.Invoke(this);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Contains and case-insensitive, which is what the outliner's own filter does.</b> Two
    ///     surfaces answering "which entities are called Lamp" differently is how a person concludes
    ///     one of them is broken; <c>Matches</c> is the filter and this is the same predicate said
    ///     once.
    /// </remarks>
    public IReadOnlyList<Entity> SelectByName(string text) {
        ArgumentNullException.ThrowIfNull(text);

        var scene = Active;

        return Choose([.. scene.Entities.Where(entity => scene.NameOf(entity).Contains(text, StringComparison.OrdinalIgnoreCase))]);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Only the components something in this scene actually has.</b> The registry holds every
    ///     declared type in the process — a list of two hundred, most of which would select nothing —
    ///     and a picker whose rows are mostly dead ends is a picker people stop opening.
    /// </remarks>
    public IReadOnlyList<(string Component, int Count)> ComponentsInScene() {
        var scene = Active;
        var world = World;
        List<(string Component, int Count)> present = [];

        foreach (var binder in SceneComponentRegistry.Binders) {
            var count = scene.Entities.Count(entity => binder.Has(world, entity));

            if (count > 0) {
                present.Add((binder.Name, count));
            }
        }

        present.Sort(static (left, right) => string.CompareOrdinal(left.Component, right.Component));

        return present;
    }

    /// <inheritdoc />
    public IReadOnlyList<Entity> SelectByType(string component) {
        ArgumentNullException.ThrowIfNull(component);

        if (!SceneComponentRegistry.TryGet(component, out var binder)) {
            return [];
        }

        var world = World;

        return Choose([.. Active.Entities.Where(entity => binder.Has(world, entity))]);
    }

    /// <inheritdoc />
    public bool SaveSelectionSet(string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var scene = Active;

        if (scene.Selection.Count == 0) {
            return false;
        }

        selectionSets[name] = scene.Selection.ToList();
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>A set that has lost members is recalled anyway.</b> Deleting one of five is not a
    ///     reason to refuse the other four; a caller that compares the count with
    ///     <see cref="SelectionSets" /> can say so.
    /// </remarks>
    public IReadOnlyList<Entity> RecallSelectionSet(string name) {
        ArgumentNullException.ThrowIfNull(name);

        return selectionSets.TryGetValue(name, out var members)
            ? Choose([.. members.Where(World.IsAlive)])
            : [];
    }

    /// <summary>Makes a found list the selection, unless it is empty.</summary>
    List<Entity> Choose(List<Entity> found) {
        if (found.Count > 0) {
            Active.Selection.Set(found);
        }

        return found;
    }

    /// <inheritdoc />
    public void ResetTransform() => Rewrite("Reset Transform", static _ => LocalTransform.Identity);

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The <i>local</i> transform, which is what makes paste mean something.</b> A world
    ///     matrix copied off one entity and written onto another under a different parent is a
    ///     position neither of them has; what people copy a transform for is to give a second object
    ///     the first one's pose inside the same rig.
    /// </remarks>
    public bool CopyTransform() {
        var primary = Active.Selection.Primary;

        if (!Transformable(primary)) {
            return false;
        }

        copiedTransform = World.Read<LocalTransform>(primary);
        return true;
    }

    /// <inheritdoc />
    public void PasteTransform() {
        if (copiedTransform is { } copied) {
            Rewrite("Paste Transform", _ => copied);
        }
    }

    /// <summary>Rewrites the selection's local transforms as one undo step.</summary>
    void Rewrite(string name, Func<LocalTransform, LocalTransform> next) {
        var world = World;
        var targets = Active.Selection
            .Where(Transformable)
            .Select(entity => (Entity: entity, Was: world.Read<LocalTransform>(entity)))
            .ToList();

        if (targets.Count == 0) {
            return;
        }

        Active.Stack.Execute(
            new DelegateCommand(
                name,
                _ => {
                    foreach (var (entity, was) in targets) {
                        world.Set(entity, next(was));
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

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Onto the <i>primary</i> — the one selected last — rather than onto the average.</b>
    ///     Every editor with this verb aligns to the active object, and the reason is that aligning to
    ///     a mean moves every object including the one you were happy with, so there is nothing on
    ///     screen that stayed still to judge the result against.
    /// </remarks>
    public void Align(int axis) {
        ArgumentOutOfRangeException.ThrowIfNegative(axis);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(axis, 2);

        var world = World;
        var anchor = Active.Selection.Primary;

        if (!world.IsAlive(anchor) || !world.Has<WorldTransform>(anchor)) {
            return;
        }

        var to = world.Read<WorldTransform>(anchor).Value.Translation;

        if (AxisTargets() is not { Count: > 0 } targets) {
            return;
        }

        Displace(
            "Align",
            targets,
            entity => Along(axis, Component(to, axis) - Component(WorldPositionOf(entity), axis))
        );
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Between the extremes rather than by a fixed gap, and the two ends do not move.</b>
    ///     A fixed gap is a different verb — it needs a number — and one that moved the outermost
    ///     objects would change the extent of the row somebody had already placed.
    /// </remarks>
    public void Distribute(int axis) {
        ArgumentOutOfRangeException.ThrowIfNegative(axis);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(axis, 2);

        var targets = AxisTargets();

        var ordered = targets
            .Select(target => (target.Entity, Position: Component(WorldPositionOf(target.Entity), axis)))
            .OrderBy(static entry => entry.Position)
            .ToList();

        if (ordered.Count < 3) {
            return;
        }

        var first = ordered[0].Position;
        var step = (ordered[^1].Position - first) / (ordered.Count - 1);

        Dictionary<Entity, float> wanted = [];

        for (var index = 0; index < ordered.Count; index++) {
            wanted[ordered[index].Entity] = first + (step * index);
        }

        Displace(
            "Distribute",
            targets,
            entity => Along(axis, wanted[entity] - Component(WorldPositionOf(entity), axis))
        );
    }

    /// <summary>The selection's transformable entities, with the local transforms they hold.</summary>
    List<(Entity Entity, LocalTransform Was)> AxisTargets() {
        var world = World;

        return Active.Selection
            .Where(entity => Transformable(entity) && world.Has<WorldTransform>(entity))
            .Select(entity => (Entity: entity, Was: world.Read<LocalTransform>(entity)))
            .ToList();
    }

    /// <summary>Where an entity is in the world.</summary>
    Vector3 WorldPositionOf(Entity entity) => World.Read<WorldTransform>(entity).Value.Translation;

    /// <summary>One component of a vector, by index.</summary>
    static float Component(Vector3 value, int axis) => axis == 0 ? value.X : axis == 1 ? value.Y : value.Z;

    /// <summary>A distance along one axis, as a vector.</summary>
    static Vector3 Along(int axis, float distance) =>
        axis == 0 ? new Vector3(distance, 0f, 0f)
        : axis == 1 ? new Vector3(0f, distance, 0f)
        : new Vector3(0f, 0f, distance);

    /// <summary>Moves each target by a world-space offset, as one undo entry.</summary>
    /// <param name="name">What the history calls it.</param>
    /// <param name="targets">The entities and the local transforms they held.</param>
    /// <param name="offsetOf">How far each moves, in world space.</param>
    void Displace(
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

    // ── The create verbs (#1290's third slice) ──────────────────────────────────────────────
    // Moved from EditorApplication.cs, bodies unchanged but for reading the active scene and
    // answering with what they made.

    /// <summary>Creates an empty entity under the selection, and selects it.</summary>
    /// <remarks>
    ///     Under the first selected entity rather than at the root, which is what every editor does
    ///     and what somebody who has just clicked a parent means. Selecting the new one is what makes
    ///     the next thing they do — rename it, drag it — land on the thing they just made.
    /// </remarks>
    public Entity CreateEntity() {
        var scene = Active;
        var created = scene.Create("Entity", LocalTransform.Identity, Under());
        scene.Selection.Set([created]);

        return created;
    }

    /// <summary>Creates one of the built-in shapes under the selection, and selects it.</summary>
    /// <remarks>
    ///     ⚠ <b>At the parent's origin rather than in front of the camera.</b> Placing new geometry
    ///     where the viewport is looking is what Unity does with a preference turned on, and it needs
    ///     the camera's pivot taken back through the parent's inverse — which is a different answer
    ///     for a spawn from the hierarchy, where there may be no viewport open at all. The identity is
    ///     the one placement that means the same thing from both, and it is what Create Empty already
    ///     does.
    /// </remarks>
    public Entity CreateShape(PrimitiveKind kind) {
        var scene = Active;
        var created = scene.CreateShape(kind, LocalTransform.Identity, Under());
        scene.Selection.Set([created]);

        return created;
    }

    /// <summary>Creates a light under the selection, and selects it.</summary>
    /// <remarks>
    ///     ⚠ <b>Aimed downwards rather than left at the identity, and only this one is.</b> A shape at
    ///     the identity is a shape; a directional or a spot light at the identity points along +Z,
    ///     which for a sun means the horizon and for a spot means a cone lying flat in the floor —
    ///     both of which look like the command having done nothing. Every other kind ignores the
    ///     rotation, so the same placement is right for all five.
    /// </remarks>
    public Entity CreateLight(LightKind kind) {
        var scene = Active;
        var created = scene.CreateLight(kind, Aimed, Under());
        scene.Selection.Set([created]);

        return created;
    }

    /// <summary>Creates a camera under the selection, and selects it.</summary>
    /// <remarks>
    ///     Level and facing the way a directional light points, for the same reason: a camera looking
    ///     along +Z from the origin is a camera looking at the inside of whatever is at the origin.
    /// </remarks>
    public Entity CreateCamera() {
        var scene = Active;
        var created = scene.CreateCamera(LocalTransform.Identity, Under());
        scene.Selection.Set([created]);

        return created;
    }

    /// <summary>Pointing down and forward, the way a key light is hung.</summary>
    internal static LocalTransform Aimed =>
        LocalTransform.Identity with {
            Rotation = Quaternion.FromYawPitchRoll(
                MathUtil.DegreesToRadians(-30f),
                MathUtil.DegreesToRadians(50f),
                0f
            )
        };

    /// <summary>What a newly created entity hangs from.</summary>
    /// <remarks>
    ///     The first selected entity rather than the root, which is what every editor does and what
    ///     somebody who has just clicked a parent means. Selecting the new one afterwards is what
    ///     makes the next thing they do — rename it, drag it — land on the thing they just made.
    /// </remarks>
    Entity Under() => Active.Selection.Count > 0 ? Active.Selection[0] : Entity.Null;

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
