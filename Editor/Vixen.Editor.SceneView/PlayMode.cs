// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Engine.Behaviors;
using Vixen.Engine.Frames;

namespace Vixen.Editor.SceneView;

/// <summary>What the editor is doing with the scene.</summary>
public enum PlayState {
    /// <summary>Being edited.</summary>
    Editing,

    /// <summary>Running.</summary>
    Playing,

    /// <summary>Running, but not stepping.</summary>
    Paused
}

/// <summary>What leaked between entering and leaving play mode.</summary>
/// <param name="Category">What the tracker calls it.</param>
/// <param name="Count">How many are still live that were not before.</param>
public readonly record struct PlayLeak(string Category, int Count);

/// <summary>Play, pause, step and stop, with the scene put back exactly as it was.</summary>
/// <remarks>
///     <para>
///         <b>In-process, which is the default of doc 11's two topologies.</b> The game runs in the
///         viewport against the world the editor was editing, and a snapshot taken on entry is
///         restored on exit. What makes that affordable is the ECS layout —
///         <see cref="WorldSnapshot" /> says how — and what makes it <i>correct</i> is that the
///         restore clears first, so nothing a script created survives.
///     </para>
///     <para>
///         <b>The hazard is state that is not in the world.</b> A static field, a native allocation,
///         a subscription to an event that outlives the session: none of them are in a snapshot and
///         all of them make the second play-through behave differently from the first. Doc 11's
///         answer is that a play-stop which leaks should <i>fail</i> rather than degrade silently, so
///         the tracked-object count is compared across the session and <see cref="Leaks" /> is what
///         it found. A test asserts it is empty; the editor shows it as a notification.
///     </para>
///     <para>
///         ⚠ <b>The selection is translated, not kept.</b> Every entity gets a new handle on restore.
///         The controller does the translation for whatever it was handed, because a caller that
///         forgot would have a selection naming whatever landed in those slots — which looks like a
///         rendering fault and is not one.
///     </para>
///     <para>
///         <b>And it steps a real <see cref="EngineLoop" />, which until 2026-08-21 it did not.</b>
///         <see cref="ShouldTick" /> had no caller outside its own tests: Play snapshotted the world,
///         maximised the viewport, said so in a notification, and nothing advanced. <see cref="Tick" />
///         is the frame, and it is deliberately the *engine's* loop rather than a schedule written
///         here — see <see cref="Loop" /> for what that runs and, more importantly, for what it does
///         not.
///     </para>
///     <para>
///         ⚠ <b>What a session runs is stated rather than assumed, because the honest set is
///         small.</b> An <see cref="EngineLoop" />'s default graph is behaviours, coroutines and
///         transforms, and every other system a game runs — physics, audio, input, navigation, the
///         render extractions — is registered by that game's own <c>OnInitialise</c> against host
///         services an editor does not have. So this runs a *whole* graph of a *named* set, and
///         <see cref="Unsupported" /> plus the caller's own inventory are what stop the difference
///         being mistaken for a gameplay bug. [11](../../../docs/plan/11-editor.md) § "Play mode runs
///         a system graph" is the reasoning.
///     </para>
/// </remarks>
public sealed class PlayModeController : IDisposable {
    /// <summary>One authored behaviour, as bytes that outlive the copy a session runs.</summary>
    /// <param name="Entity">Which entity carried it, in pre-snapshot handles.</param>
    /// <param name="Alias">Its name, which is what survives the round trip.</param>
    /// <param name="State">Its values.</param>
    readonly record struct Authored(Entity Entity, string Alias, byte[] State);

    readonly World world;
    readonly BehaviorStore? authored;
    readonly IEditorRegistry? extensions;

    WorldSnapshot? snapshot;
    Dictionary<string, int> before = [];
    List<Authored> saved = [];

    /// <summary>The behaviours the session did not take over, so the teardown can leave them.</summary>
    /// <remarks>
    ///     ⚠ <b>By reference, and it is the exact answer rather than a heuristic.</b> Everything on
    ///     the world at <see cref="Play" /> was either detached into <see cref="saved" /> or left
    ///     here; everything attached afterwards is therefore the session's, including whatever a
    ///     script spawned. There is no public way to ask a <see cref="BehaviorStore" /> which
    ///     behaviours are its own — <c>AllOn</c> reads the entity's link, which is one component
    ///     however many stores share the world — so the set that was not taken is what identifies
    ///     the set that was.
    /// </remarks>
    readonly HashSet<Behavior> stranded = [];

    bool disposed;

    /// <summary>What the editor is doing.</summary>
    public PlayState State { get; private set; } = PlayState.Editing;

    /// <summary>Whether the game is running, paused or not.</summary>
    public bool IsPlaying => State != PlayState.Editing;

    /// <summary>How many frames <see cref="Step" /> still owes.</summary>
    /// <remarks>
    ///     A count rather than a flag, so that "step ten frames" is the same mechanism as "step one"
    ///     and so that a step requested while the frame is already running is not lost.
    /// </remarks>
    public int PendingSteps { get; private set; }

    /// <summary>What was still live after the last session that was not live before it.</summary>
    public IReadOnlyList<PlayLeak> Leaks { get; private set; } = [];

    /// <summary>The graph this session steps, or <see langword="null" /> when nothing is running.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>An <see cref="EngineLoop" /> over the world being edited, which does not own it.</b>
    ///         Its default registration is the whole of what a session runs: the behaviour lifecycle
    ///         and its <c>Update</c>/<c>LateUpdate</c> passes, the four coroutine drains, and
    ///         <c>TransformSystem</c>. That is the same object a game head and a determinism test
    ///         drive, which is the point — a schedule written here would be a second opinion about
    ///         the order a frame happens in.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Exposed so a caller can add to it, and every addition is that caller's
    ///         claim.</b> Nothing is added <em>here</em>, because everything a game adds takes a host
    ///         service — a <c>PhysicsScene</c>, an <c>AudioEngine</c>, an <c>InputService</c>, a
    ///         <c>RenderView</c> — and a controller that invented one would be running a frame
    ///         nothing else in the process agrees with. What owns such a service says so through an
    ///         <see cref="IPlaySystems" /> contribution instead; see <see cref="Session" />.
    ///     </para>
    /// </remarks>
    public EngineLoop? Loop { get; private set; }

    /// <summary>This session's contributions and their teardown, or null when nothing is running.</summary>
    /// <remarks>
    ///     ⚠ <b>The answer to "what is this frame actually made of", and it is per session on
    ///     purpose.</b> A contribution's systems are added when Play is pressed and undone when Stop
    ///     is — physics belongs to play, not to editing — so the objects they own have exactly the
    ///     lifetime of the snapshot that will be restored over them.
    /// </remarks>
    public PlaySession? Session { get; private set; }

    /// <summary>The contributions that threw while attaching, by type name.</summary>
    /// <remarks>
    ///     ⚠ <b>Empty is the normal answer, and a non-empty one must be shown rather than logged.</b>
    ///     A contribution that could not stand its systems up is a part of the frame that is not
    ///     running — a session with no physics, say — and doc 11's rule for this feature is that a
    ///     thing which does not happen must be visibly not happening. Failing the whole session
    ///     instead would make a machine without Jolt's native library a machine where Play is broken.
    /// </remarks>
    public IReadOnlyList<string> Refused { get; private set; } = [];

    /// <summary>Behaviours on the world that this session could not take over, by type name.</summary>
    /// <remarks>
    ///     ⚠ <b>Empty is the normal answer, and a non-empty one must be shown rather than
    ///     logged.</b> A behaviour lands here when nothing registered its type — so there is no
    ///     binder to copy its values through — or when it belongs to a <see cref="BehaviorStore" />
    ///     other than the one this controller was given, which is what a second additively-opened
    ///     scene produces. Either way the behaviour does not run, and a play session that silently
    ///     skipped one would present as that script being broken.
    /// </remarks>
    public IReadOnlyList<string> Unsupported { get; private set; } = [];

    /// <summary>Raised when the state changes.</summary>
    public event Action<PlayModeController, PlayState>? StateChanged;

    /// <summary>Raised after a stop, with the table that translates old entities into new ones.</summary>
    public event Action<PlayModeController, IReadOnlyDictionary<Entity, Entity>>? Restored;

    /// <summary>Drives play mode over a world.</summary>
    /// <param name="world">The world being edited.</param>
    /// <param name="authored">
    ///     Where the scene's authored behaviours live, or <see langword="null" /> for a session that
    ///     runs none. Null is not a degrade for a caller that has no store — a world with no
    ///     behaviours on it plays identically either way — but an editor that has one and does not
    ///     pass it gets a Play button that runs the graph and none of the scripts.
    /// </param>
    /// <param name="extensions">
    ///     Where the <see cref="IPlaySystems" /> contributions are, or <see langword="null" /> for a
    ///     session that runs nothing beyond the loop's default graph. Read at every
    ///     <see cref="Play" /> rather than kept as a list, so a module or a plugin that registers one
    ///     after this controller was built still reaches the next session.
    /// </param>
    public PlayModeController(World world, BehaviorStore? authored = null, IEditorRegistry? extensions = null) {
        ArgumentNullException.ThrowIfNull(world);

        this.world = world;
        this.authored = authored;
        this.extensions = extensions;
    }

    /// <summary>Enters play mode, taking a snapshot first.</summary>
    /// <returns>Whether it entered.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The behaviours come off the world before the snapshot is taken, and that ordering
    ///         is the whole of why leaving play mode is clean.</b> <c>BehaviorRef</c> is a managed
    ///         component holding an array of live objects, so a snapshot taken with it in place would
    ///         copy the <i>reference</i> — and the restore would hand the scene back the very
    ///         instances a session had woken, started and mutated, with their <c>Entity</c> naming a
    ///         handle that no longer exists. Bytes and an alias are what crosses instead, which is
    ///         the same gap <c>ProjectAssemblies</c> crosses for a code reload and for the same
    ///         reason.
    ///     </para>
    ///     <para>
    ///         So the session's behaviours are <i>copies</i>, in the loop's own store — which is
    ///         what <c>SceneDocument.Behaviors</c> already says happens: "the behaviours it runs are
    ///         the ones a load builds into <i>its</i> store rather than these".
    ///     </para>
    /// </remarks>
    public bool Play() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (IsPlaying) {
            // Already playing is not an error and is not a restart: the play button is a toggle in
            // every editor, and a second press that re-snapshotted would throw away the session.
            return Resume();
        }

        before = TrackedByCategory();
        saved = Detach(out var unsupported);
        Unsupported = unsupported;

        snapshot = WorldSnapshot.Capture(world);

        // ⚠ Handed the world rather than making one, which is also what stops the loop disposing it:
        // `EngineLoop` owns a world only when it had to create one.
        Loop = new EngineLoop(world);

        foreach (var (entity, alias, state) in saved) {
            if (SceneBehaviorRegistry.TryGet(alias, out var binder)) {
                binder.AttachTo(Loop.Behaviors, entity, binder.Restore(state));
            }
        }

        Contribute();

        Leaks = [];

        Move(PlayState.Playing);
        return true;
    }

    /// <summary>Runs one frame of the game, if this is a frame the game should have.</summary>
    /// <param name="delta">How much unscaled time has passed since the last one.</param>
    /// <returns>Whether the graph advanced.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>What the editor calls once a frame, and the only caller <see cref="ShouldTick" />
    ///         needs.</b> The decision and the step are together here so that "paused" cannot mean
    ///         one thing in the viewport and another in the profiler, and so that a step is consumed
    ///         exactly when a frame is run.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The loop is checked before the state is, because <see cref="ShouldTick" />
    ///         consumes.</b> Asking first and finding no loop would spend a
    ///         <see cref="PendingSteps" /> on a frame that never happened, so Step Frame would need
    ///         pressing twice.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A caller that ticks must not also run its own <c>TransformSystem</c> that
    ///         frame.</b> The graph's runs in <c>PreRender</c>, and two instances over one world keep
    ///         separate "what have I already seen" versions — so each would tell the other that
    ///         nothing had changed, and the failure is a moved object that stops following its
    ///         parent rather than an error.
    ///     </para>
    /// </remarks>
    public bool Tick(TimeSpan delta) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (Loop is not { } running || !ShouldTick()) {
            return false;
        }

        running.Frame(delta);
        return true;
    }

    /// <summary>Stops the game and puts the scene back.</summary>
    /// <param name="selection">Entities to translate through the restore, if any.</param>
    /// <returns>What each of them is now, or an empty list when there was nothing to stop.</returns>
    public IReadOnlyList<Entity> Stop(IEnumerable<Entity>? selection = null) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (snapshot is not { } captured) {
            return [];
        }

        // ⚠ Before the restore, and it is not tidying: a behaviour that never had `OnDestroy` is one
        // whose native handles and subscriptions are still live, which is precisely what `Leaks`
        // below is measuring. Tearing down after `World.Clear` would find no entities to walk and
        // would report every one of them as a leak of this session's own making.
        if (Loop is { } running) {
            Teardown(running.Behaviors);

            // ⚠ After the behaviours and before the restore. After, because a script's `OnDestroy` is
            // entitled to ask the simulation a last question — a body's velocity, what it was resting
            // on — and a physics world torn down first would answer that with a native crash. Before,
            // because a contribution's bodies live on entities in *this* world, and `Restore` clears
            // it: releasing afterwards would be asking a scene to destroy bodies whose entities have
            // just stopped existing.
            Release();

            running.Dispose();
            Loop = null;
        }

        var translation = captured.Restore(world);

        captured.Dispose();
        snapshot = null;
        PendingSteps = 0;

        Reattach(translation);

        Leaks = Compare(before, TrackedByCategory());

        Move(PlayState.Editing);
        Restored?.Invoke(this, translation);

        return selection is null ? [] : WorldSnapshot.Remap(selection, translation);
    }

    /// <summary>Stops stepping without leaving play mode.</summary>
    /// <returns>Whether anything changed.</returns>
    public bool Pause() {
        if (State != PlayState.Playing) {
            return false;
        }

        Move(PlayState.Paused);
        return true;
    }

    /// <summary>Starts stepping again.</summary>
    /// <returns>Whether anything changed.</returns>
    public bool Resume() {
        if (State != PlayState.Paused) {
            return false;
        }

        Move(PlayState.Playing);
        return true;
    }

    /// <summary>Runs a number of frames while paused.</summary>
    /// <param name="frames">How many.</param>
    /// <remarks>
    ///     Entering play mode paused is the case this also covers: stepping from the editing state
    ///     starts the session first, because "step one frame" from a stopped game plainly means
    ///     "start it and run one frame" rather than nothing.
    /// </remarks>
    public void Step(int frames = 1) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frames);

        if (State == PlayState.Editing) {
            Play();
            Pause();
        }

        PendingSteps += frames;
    }

    /// <summary>Whether the game loop should run this frame, and consumes a step if it does.</summary>
    /// <returns>Whether to tick.</returns>
    /// <remarks>
    ///     Called once per frame by whatever drives the loop. It is the controller rather than the
    ///     host that decides, so "paused" means one thing in the viewport, the profiler and the
    ///     remote inspector instead of three.
    /// </remarks>
    public bool ShouldTick() {
        switch (State) {
            case PlayState.Playing:
                return true;

            case PlayState.Paused when PendingSteps > 0:
                PendingSteps--;
                return true;

            default:
                return false;
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        // ⚠ Before the loop, for `Stop`'s reason: a contribution's native world outlives a managed
        // one that is merely collected, so an editor closed mid-session with no release here leaks a
        // Jolt world per play-through — the exact thing `Leaks` is there to make visible, escaping
        // through the one path that never runs the comparison.
        Release();

        Loop?.Dispose();
        Loop = null;

        snapshot?.Dispose();
        snapshot = null;
    }

    /// <summary>Builds the session and lets every contribution add its systems to it.</summary>
    /// <remarks>
    ///     ⚠ <b>One that throws is named and skipped rather than allowed to fail the session.</b>
    ///     Standing systems up takes native libraries, devices and files, and any of them can be
    ///     missing on a particular machine; a Play button that refuses to work at all because audio
    ///     could not open a device would be a worse editor than one that plays without sound and says
    ///     so. See <see cref="Refused" /> for what says so.
    /// </remarks>
    void Contribute() {
        if (Loop is not { } running) {
            return;
        }

        var session = new PlaySession(running, world);
        List<string> refused = [];

        Session = session;

        foreach (var contribution in extensions?.All<IPlaySystems>() ?? []) {
            try {
                contribution.Attach(session);
            } catch (Exception failure) {
                refused.Add($"{contribution.GetType().Name} ({failure.Message})");
            }
        }

        Refused = refused;
    }

    /// <summary>Undoes everything this session's contributions did.</summary>
    void Release() {
        if (Session is not { } session) {
            return;
        }

        Session = null;
        session.Release();
    }

    /// <summary>Takes every authored behaviour off the world, keeping what was in it.</summary>
    /// <param name="unsupported">The type names of the ones that could not be taken off.</param>
    /// <remarks>
    ///     ⚠ <b>A behaviour that could not be detached is <i>left where it is</i> and named.</b> The
    ///     alternative — carrying on and letting the snapshot copy its <c>BehaviorRef</c> — is the
    ///     silent version of the same failure, and it corrupts the scene rather than only failing to
    ///     run the script. See <see cref="Unsupported" /> for the two ways it happens.
    /// </remarks>
    List<Authored> Detach(out IReadOnlyList<string> unsupported) {
        List<Authored> taken = [];
        List<string> refused = [];

        stranded.Clear();

        if (authored is not { } store) {
            unsupported = refused;
            return taken;
        }

        foreach (var entity in Carriers()) {
            // A copy, because detaching rewrites the very array `AllOn` hands back.
            foreach (var behavior in store.AllOn(entity).ToArray()) {
                if (SceneBehaviorRegistry.TryGet(behavior.GetType(), out var binder)
                    && binder.Save(behavior) is { } state
                    && binder.RemoveFrom(store, entity)) {
                    taken.Add(new(entity, binder.Name, state));
                    continue;
                }

                stranded.Add(behavior);
                refused.Add(behavior.GetType().Name);
            }
        }

        refused.Sort(StringComparer.Ordinal);

        unsupported = refused;
        return taken;
    }

    /// <summary>Puts the authored behaviours back, on the handles the restore issued.</summary>
    /// <remarks>
    ///     ⚠ <b>New instances, not the ones that were taken off.</b> Nothing was kept but bytes —
    ///     see <see cref="Play" /> — so what comes back is an object built from the values somebody
    ///     typed rather than one a session had a chance to change. That is the rule play mode
    ///     exists to enforce, applied to the half of the scene that is not in the world.
    /// </remarks>
    void Reattach(IReadOnlyDictionary<Entity, Entity> translation) {
        if (authored is { } store) {
            foreach (var (entity, alias, state) in saved) {
                if (translation.TryGetValue(entity, out var now)
                    && world.IsAlive(now)
                    && SceneBehaviorRegistry.TryGet(alias, out var binder)) {
                    binder.AttachTo(store, now, binder.Restore(state));
                }
            }
        }

        saved = [];
        stranded.Clear();
    }

    /// <summary>Destroys the session's behaviours and drains the callbacks that go with it.</summary>
    /// <remarks>
    ///     ⚠ <b>Only the session's, and <c>BehaviorStore.Destroy</c> will not stop you getting that
    ///     wrong.</b> <c>Remove</c> checks that the behaviour is this store's and answers false;
    ///     <c>Destroy</c> queues whatever it is handed, and the drain then indexes a bucket this
    ///     store has never had — a <c>KeyNotFoundException</c> out of the middle of Stop, for a
    ///     behaviour that belonged to the document all along. <see cref="stranded" /> is what makes
    ///     the distinction available.
    /// </remarks>
    void Teardown(BehaviorStore store) {
        foreach (var entity in Carriers(store.World)) {
            foreach (var behavior in store.AllOn(entity).ToArray()) {
                if (!stranded.Contains(behavior)) {
                    store.Destroy(behavior);
                }
            }
        }

        // The queue is what `Destroy` fills; this is the drain that runs `OnDisable` and
        // `OnDestroy` — the same one a game's `BehaviorLifecycleSystem` runs every frame.
        store.RunLifecycle();
    }

    /// <summary>Every entity carrying behaviours, collected before anything is changed.</summary>
    /// <remarks>
    ///     ⚠ <b>Collected first.</b> Detaching removes <c>BehaviorRef</c>, which moves the entity to
    ///     another archetype — so a walk that acted as it went would be rewriting the chunk list it
    ///     was iterating.
    /// </remarks>
    List<Entity> Carriers() => Carriers(world);

    static List<Entity> Carriers(World world) {
        List<Entity> carriers = [];
        var query = new QueryDescription().WithAll<BehaviorRef>();

        foreach (var chunk in world.Chunks(query)) {
            foreach (var entity in chunk.Entities[..chunk.Count]) {
                carriers.Add(entity);
            }
        }

        return carriers;
    }

    void Move(PlayState state) {
        if (State == state) {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, state);
    }

    static Dictionary<string, int> TrackedByCategory() {
        Dictionary<string, int> counts = new(StringComparer.Ordinal);

        foreach (var report in LeakTracker.Snapshot()) {
            counts[report.Category] = counts.GetValueOrDefault(report.Category) + 1;
        }

        return counts;
    }

    /// <summary>
    ///     What is live now that was not live before, by category.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Only growth counts.</b> A session that disposed something the editor had before it
    ///     started is a different bug and not this one, and reporting it here would make the leak
    ///     list fire on every play-through that happened to close a document.
    /// </remarks>
    static IReadOnlyList<PlayLeak> Compare(Dictionary<string, int> before, Dictionary<string, int> after) {
        List<PlayLeak> leaks = [];

        foreach (var (category, count) in after) {
            var grew = count - before.GetValueOrDefault(category);

            if (grew > 0) {
                leaks.Add(new(category, grew));
            }
        }

        leaks.Sort(static (left, right) => string.CompareOrdinal(left.Category, right.Category));

        return leaks;
    }
}
