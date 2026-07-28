// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;

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
/// </remarks>
public sealed class PlayModeController : IDisposable {
    readonly World world;
    WorldSnapshot? snapshot;
    Dictionary<string, int> before = [];
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

    /// <summary>Raised when the state changes.</summary>
    public event Action<PlayModeController, PlayState>? StateChanged;

    /// <summary>Raised after a stop, with the table that translates old entities into new ones.</summary>
    public event Action<PlayModeController, IReadOnlyDictionary<Entity, Entity>>? Restored;

    /// <summary>Drives play mode over a world.</summary>
    /// <param name="world">The world being edited.</param>
    public PlayModeController(World world) {
        ArgumentNullException.ThrowIfNull(world);

        this.world = world;
    }

    /// <summary>Enters play mode, taking a snapshot first.</summary>
    /// <returns>Whether it entered.</returns>
    public bool Play() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (IsPlaying) {
            // Already playing is not an error and is not a restart: the play button is a toggle in
            // every editor, and a second press that re-snapshotted would throw away the session.
            return Resume();
        }

        before = TrackedByCategory();
        snapshot = WorldSnapshot.Capture(world);
        Leaks = [];

        Move(PlayState.Playing);
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

        var translation = captured.Restore(world);

        captured.Dispose();
        snapshot = null;
        PendingSteps = 0;

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
        snapshot?.Dispose();
        snapshot = null;
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
