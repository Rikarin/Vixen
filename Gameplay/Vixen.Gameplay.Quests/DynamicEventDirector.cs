// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Quests;

/// <summary>An event ended and a chain moved.</summary>
/// <param name="Event">Which event.</param>
/// <param name="Status">How it ended.</param>
/// <param name="Started">What its ending started.</param>
public readonly record struct EventChainStep(DefId Event, DynamicEventStatus Status, IReadOnlyList<DefId> Started);

/// <summary>What runs the dynamic events on one realm, and what walks their chains.</summary>
/// <remarks>
///     <para>
///         <b>Success and failure both lead somewhere, and this is where that happens.</b> An escort
///         that fails starts "retake the camp"; the camp being retaken starts the escort again. Doc 28
///         calls that the thing that makes a chain feel alive, and the machinery for it is one
///         dictionary and one loop.
///     </para>
///     <para>
///         ⚠ <b>A chain is a graph with cycles and this does not try to prevent them.</b> The camp
///         being lost, retaken and lost again <em>is</em> the content, so there is no acyclicity check
///         here and there could not be one. What is bounded instead is a single resolution: an ending
///         starts its successors and stops, so a chain whose event succeeds instantly cannot recurse —
///         see <see cref="MaximumChainDepth" />.
///     </para>
///     <para>
///         ⚠ <b>An event already running is not started again.</b> Two failures both branching to
///         "retake the camp" is ordinary authoring, and the other reading gives two instances of one
///         event on one realm, each with half the participants and its own set of rewards.
///     </para>
/// </remarks>
public sealed class DynamicEventDirector : IDisposable {
    /// <summary>How many links of a chain one ending may resolve before the director stops walking.</summary>
    /// <remarks>
    ///     A backstop, not a design limit. It is reached only by a chain of events that each end the
    ///     instant they begin, which is a content mistake — and an unbounded walk of that is a realm
    ///     that stops responding rather than one that reports it.
    /// </remarks>
    public const int MaximumChainDepth = 16;

    readonly Dictionary<uint, DynamicEventInstance> running = [];
    readonly List<DefId> started = [];

    /// <summary>Makes a director.</summary>
    /// <param name="library">Where the events come from.</param>
    /// <param name="bus">Where the gameplay events come from.</param>
    public DynamicEventDirector(QuestLibrary library, GameplayEventBus bus) {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(bus);

        Library = library;
        Bus = bus;
    }

    /// <summary>Where the events come from.</summary>
    public QuestLibrary Library { get; }

    /// <summary>Where the gameplay events come from.</summary>
    public GameplayEventBus Bus { get; }

    /// <summary>Every event running now.</summary>
    public IEnumerable<DynamicEventInstance> Running => running.Values;

    /// <summary>How many are running.</summary>
    public int Count => running.Count;

    /// <summary>Raised when an event ends, with whatever its ending started.</summary>
    public event Action<EventChainStep>? Stepped;

    /// <summary>Whether an event is running.</summary>
    /// <param name="id">Which one.</param>
    /// <returns>Whether it is.</returns>
    public bool IsRunning(DefId id) => running.ContainsKey(id.Value);

    /// <summary>The running instance of an event.</summary>
    /// <param name="id">Which one.</param>
    /// <returns>It, or null.</returns>
    public DynamicEventInstance? Find(DefId id) => running.GetValueOrDefault(id.Value);

    /// <summary>Starts an event.</summary>
    /// <param name="id">Which one.</param>
    /// <returns>The instance, or null when this build has no such event or it is already running.</returns>
    public DynamicEventInstance? Begin(DefId id) {
        if (running.ContainsKey(id.Value) || Library.FindEvent(id) is not { } template) {
            return null;
        }

        var instance = new DynamicEventInstance(template, Bus);

        running.Add(id.Value, instance);

        return instance;
    }

    /// <summary>Advances every running event and resolves whatever ended.</summary>
    /// <param name="delta">How much time passed, in seconds.</param>
    /// <returns>How many events ended.</returns>
    public int Tick(float delta) {
        var ended = 0;

        // Over a copy: resolving a chain adds to the dictionary this is walking.
        foreach (var instance in running.Values.ToArray()) {
            if (!instance.Tick(delta)) {
                continue;
            }

            Resolve(instance);
            ended++;
        }

        return ended;
    }

    /// <summary>Ends an event and resolves its chain. What a scripted outcome does.</summary>
    /// <param name="id">Which event.</param>
    /// <param name="status">How it ended.</param>
    /// <returns>Whether it was running and this ended it.</returns>
    public bool Finish(DefId id, DynamicEventStatus status) {
        if (running.GetValueOrDefault(id.Value) is not { } instance || !instance.Finish(status)) {
            return false;
        }

        Resolve(instance);

        return true;
    }

    /// <inheritdoc />
    public void Dispose() {
        foreach (var instance in running.Values) {
            instance.Dispose();
        }

        running.Clear();
    }

    void Resolve(DynamicEventInstance instance) {
        var depth = 0;
        var pending = new Queue<DynamicEventInstance>();

        pending.Enqueue(instance);

        while (pending.Count > 0 && depth++ < MaximumChainDepth) {
            var ending = pending.Dequeue();

            running.Remove(ending.Id.Value);
            ending.Dispose();

            started.Clear();

            var links = ending.Status == DynamicEventStatus.Succeeded
                ? ending.Template.OnSuccess
                : ending.Template.OnFailure;

            foreach (var link in links) {
                if (Begin(link.Def) is not { } next) {
                    continue;
                }

                started.Add(link.Def);

                // A successor with no objectives and no clock cannot end here, so this only enqueues
                // one that has already resolved — which is what the depth bound is counting.
                if (next.IsTerminal) {
                    pending.Enqueue(next);
                }
            }

            Stepped?.Invoke(new(ending.Id, ending.Status, [.. started]));
        }
    }
}
