// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Vixen.Core;

namespace Vixen.Ecs;

/// <summary>What a recorded command does.</summary>
public enum CommandKind {
    /// <summary>Creates an entity, resolving the placeholder the recorder was handed.</summary>
    Create,

    /// <summary>Destroys an entity, if it is still alive.</summary>
    Destroy,

    /// <summary>Adds a component, or overwrites it if the entity already has one.</summary>
    Add,

    /// <summary>Overwrites a component the entity already has.</summary>
    Set,

    /// <summary>Removes a component, if the entity has one.</summary>
    Remove
}

/// <summary>
///     Structural changes recorded during iteration and applied at a sync point.
/// </summary>
/// <remarks>
///     <para>
///         Adding or removing a component moves an entity's row between chunks, which invalidates
///         the very span the loop that asked for it is walking. The engine's answer is not to detect
///         that — it is to make the mutation happen somewhere it cannot: recorded here, played back
///         when nothing is iterating. Jobs may only mutate through one of these.
///     </para>
///     <para>
///         <b>The buffer is lenient where <see cref="World" /> is strict.</b> <see cref="Add{T}(Entity, in T)" />
///         overwrites rather than refusing, <see cref="Remove{T}(Entity)" /> and <see cref="Destroy" /> do
///         nothing if there is nothing to do, and a command naming an entity that an earlier command
///         in the same playback destroyed is skipped. That is not laxity for its own sake: a
///         recorder runs during iteration and cannot look at the world to find out whether its
///         change is redundant, and two systems both deciding to remove the same tag or destroy the
///         same entity is ordinary rather than exceptional. A caller that <i>can</i> look uses
///         <see cref="World" /> and gets told when it is wrong.
///     </para>
/// </remarks>
public sealed class CommandBuffer {
    internal readonly record struct Command(
        CommandKind Kind,
        Entity Entity,
        ComponentTypeId Component,
        int Slot,
        int SortKey,
        int Sequence,
        int Channel
    );

    readonly World world;
    readonly Channel main;
    readonly ConcurrentDictionary<int, Channel> byThread = new();
    readonly List<Channel> channels = [];
    readonly List<Command> merged = [];
    readonly List<Entity> resolved = [];

    int placeholders;

    /// <summary>The world the commands will be played back into.</summary>
    public World World => world;

    /// <summary>How many commands are recorded, across every channel.</summary>
    public int Count {
        get {
            var total = 0;

            lock (channels) {
                foreach (var channel in channels) {
                    total += channel.Commands.Count;
                }
            }

            return total;
        }
    }

    /// <summary>Creates a buffer for a world.</summary>
    /// <param name="world">Where the commands will be applied.</param>
    public CommandBuffer(World world) {
        ArgumentNullException.ThrowIfNull(world);
        this.world = world;
        main = NewChannel();
    }

    // ---------------------------------------------------------------- recording

    /// <summary>Records the creation of an entity.</summary>
    /// <returns>
    ///     A placeholder handle: not a live entity, and usable in later commands on this buffer,
    ///     which resolve it to the entity playback actually creates.
    /// </returns>
    public Entity Create() => Create(main, 0);

    /// <summary>Records the destruction of an entity.</summary>
    /// <param name="entity">The entity, or a placeholder from <see cref="Create()" />.</param>
    public void Destroy(Entity entity) => main.Record(CommandKind.Destroy, entity, default, -1, 0);

    /// <summary>Records adding a component, overwriting it if the entity already has one.</summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <param name="entity">The entity, or a placeholder from <see cref="Create()" />.</param>
    /// <param name="value">Its value.</param>
    public void Add<T>(Entity entity, in T value) => main.Add(entity, value, 0);

    /// <summary>Records adding a component with its default value — the usual way to add a tag.</summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <param name="entity">The entity, or a placeholder from <see cref="Create()" />.</param>
    public void Add<T>(Entity entity) => Add<T>(entity, default!);

    /// <summary>Records overwriting a component the entity has.</summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <param name="entity">The entity, or a placeholder from <see cref="Create()" />.</param>
    /// <param name="value">Its value.</param>
    public void Set<T>(Entity entity, in T value) => main.Set(entity, value, 0);

    /// <summary>Records removing a component, if the entity has one.</summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <param name="entity">The entity, or a placeholder from <see cref="Create()" />.</param>
    public void Remove<T>(Entity entity) =>
        main.Record(CommandKind.Remove, entity, ComponentType<T>.Id, main.RemoverSlot<T>(), 0);

    /// <summary>A view that jobs record through, one channel per thread.</summary>
    /// <returns>The writer.</returns>
    public ParallelWriter AsParallelWriter() => new(this);

    // ---------------------------------------------------------------- playback

    /// <summary>
    ///     Applies every recorded command in sort-key order and empties the buffer.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     A command could not be applied. The message names the command and the entity, because a
    ///     failure here happens far from where it was recorded.
    /// </exception>
    /// <remarks>
    ///     Ordering is by sort key first, then by channel, then by the order the channel recorded
    ///     them. So a parallel job that passes the item index it is working on gets the same result
    ///     however the work was distributed across threads — <b>which is what makes a fixed-step
    ///     simulation reproducible</b>, and is the reason the sort key is a parameter rather than
    ///     something the buffer invents.
    /// </remarks>
    public void Playback() {
        merged.Clear();

        lock (channels) {
            foreach (var channel in channels) {
                merged.AddRange(channel.Commands);
            }
        }

        merged.Sort(static (left, right) => {
                var key = left.SortKey.CompareTo(right.SortKey);

                if (key != 0) {
                    return key;
                }

                var channel = left.Channel.CompareTo(right.Channel);
                return channel != 0 ? channel : left.Sequence.CompareTo(right.Sequence);
            }
        );

        resolved.Clear();

        for (var index = 0; index < placeholders; index++) {
            resolved.Add(Entity.Null);
        }

        foreach (var command in merged) {
            Apply(command);
        }

        Clear();
    }

    /// <summary>Throws away everything recorded without applying it.</summary>
    public void Clear() {
        lock (channels) {
            foreach (var channel in channels) {
                channel.Clear();
            }
        }

        merged.Clear();
        placeholders = 0;

        // `resolved` deliberately survives, so Resolve still answers for the playback that has just
        // happened. It is rebuilt at the start of the next one.
    }

    /// <summary>What a placeholder from <see cref="Create()" /> became.</summary>
    /// <param name="placeholder">The handle a recording call handed back.</param>
    /// <returns>The entity playback created, or <see cref="Entity.Null" /> if it was culled.</returns>
    /// <remarks>
    ///     Valid from the end of a playback until the start of the next one. Without it a caller
    ///     that spawns through a buffer has no way to reach what it spawned, which is most of what
    ///     makes the parallel writer worth having: a job that creates an entity almost always wants
    ///     to record it somewhere afterwards.
    /// </remarks>
    public Entity Resolve(Entity placeholder) {
        if (placeholder.Id >= 0) {
            return placeholder;
        }

        var index = PlaceholderIndex(placeholder);
        return index < resolved.Count ? resolved[index] : Entity.Null;
    }

    void Apply(in Command command) {
        var entity = ResolveDuringPlayback(command.Entity);

        if (command.Kind == CommandKind.Create) {
            resolved[PlaceholderIndex(command.Entity)] = world.Create();
            return;
        }

        // An entity an earlier command destroyed, or a placeholder whose Create was culled. Skipping
        // is the whole point of recording: the recorder could not have known.
        if (entity.IsNull || !world.IsAlive(entity)) {
            return;
        }

        try {
            if (command.Kind == CommandKind.Destroy) {
                world.Destroy(entity);
                return;
            }

            Channels(command.Channel).Payload(command.Component).Apply(command.Kind, world, entity, command.Slot);
        } catch (Exception failure) when (failure is InvalidOperationException or ArgumentException) {
            var what = command.Component.IsValid
                ? $"{command.Kind} of {ComponentRegistry.Get(command.Component).Type.Name}"
                : command.Kind.ToString();

            throw new InvalidOperationException(
                $"Playing back {what} on entity {entity} failed. A command buffer is applied far "
                + "from where it was recorded, so this is where the reason has to be said out loud.",
                failure
            );
        }
    }

    Entity ResolveDuringPlayback(Entity entity) => entity.Id < 0 ? resolved[PlaceholderIndex(entity)] : entity;

    static int PlaceholderIndex(Entity entity) => -entity.Id - 1;

    Entity Create(Channel channel, int sortKey) {
        var index = Interlocked.Increment(ref placeholders) - 1;

        // Negative ids, so a placeholder can never be mistaken for a live handle: World.Live rejects
        // them before it indexes anything.
        var placeholder = new Entity(-index - 1, 0, world.Id);
        channel.Record(CommandKind.Create, placeholder, default, -1, sortKey);
        return placeholder;
    }

    Channel NewChannel() {
        // Indexed by its position, under the same lock that appends it, so `Channels(index)` is
        // always the channel that recorded the command. GetOrAdd can run its factory more than once
        // for the same key under contention; a channel that loses that race stays in the list and
        // stays empty, which costs a list slot and nothing else.
        lock (channels) {
            var channel = new Channel(channels.Count);
            channels.Add(channel);
            return channel;
        }
    }

    Channel Channels(int index) {
        lock (channels) {
            return channels[index];
        }
    }

    Channel ForCurrentThread() =>
        byThread.GetOrAdd(Environment.CurrentManagedThreadId, _ => NewChannel());

    /// <summary>
    ///     What a job records through. A struct, so passing it into a job costs nothing.
    /// </summary>
    /// <remarks>
    ///     <b>The sort key has to identify the work item</b> — the index of the entity or the batch
    ///     the job is processing. Commands sharing a sort key from different threads have an
    ///     unspecified order between them, which is the one thing that would make playback depend on
    ///     how the scheduler happened to distribute the work.
    /// </remarks>
    public readonly struct ParallelWriter {
        readonly CommandBuffer buffer;

        internal ParallelWriter(CommandBuffer buffer) => this.buffer = buffer;

        /// <summary>Records the creation of an entity.</summary>
        /// <param name="sortKey">Which work item this belongs to.</param>
        /// <returns>A placeholder handle, resolved at playback.</returns>
        public Entity Create(int sortKey) => buffer.Create(buffer.ForCurrentThread(), sortKey);

        /// <summary>Records the destruction of an entity.</summary>
        /// <param name="sortKey">Which work item this belongs to.</param>
        /// <param name="entity">The entity, or a placeholder.</param>
        public void Destroy(int sortKey, Entity entity) =>
            buffer.ForCurrentThread().Record(CommandKind.Destroy, entity, default, -1, sortKey);

        /// <summary>Records adding a component, overwriting it if the entity already has one.</summary>
        /// <typeparam name="T">The component type.</typeparam>
        /// <param name="sortKey">Which work item this belongs to.</param>
        /// <param name="entity">The entity, or a placeholder.</param>
        /// <param name="value">Its value.</param>
        public void Add<T>(int sortKey, Entity entity, in T value) =>
            buffer.ForCurrentThread().Add(entity, value, sortKey);

        /// <summary>Records adding a component with its default value.</summary>
        /// <typeparam name="T">The component type.</typeparam>
        /// <param name="sortKey">Which work item this belongs to.</param>
        /// <param name="entity">The entity, or a placeholder.</param>
        public void Add<T>(int sortKey, Entity entity) => Add<T>(sortKey, entity, default!);

        /// <summary>Records overwriting a component the entity has.</summary>
        /// <typeparam name="T">The component type.</typeparam>
        /// <param name="sortKey">Which work item this belongs to.</param>
        /// <param name="entity">The entity, or a placeholder.</param>
        /// <param name="value">Its value.</param>
        public void Set<T>(int sortKey, Entity entity, in T value) =>
            buffer.ForCurrentThread().Set(entity, value, sortKey);

        /// <summary>Records removing a component, if the entity has one.</summary>
        /// <typeparam name="T">The component type.</typeparam>
        /// <param name="sortKey">Which work item this belongs to.</param>
        /// <param name="entity">The entity, or a placeholder.</param>
        public void Remove<T>(int sortKey, Entity entity) {
            var channel = buffer.ForCurrentThread();
            channel.Record(CommandKind.Remove, entity, ComponentType<T>.Id, channel.RemoverSlot<T>(), sortKey);
        }
    }

    /// <summary>
    ///     One thread's recording. Never touched by two threads, which is what makes the plain
    ///     <see cref="List{T}" /> inside it correct.
    /// </summary>
    sealed class Channel(int index) {
        readonly Dictionary<ComponentTypeId, IPayloadStore> payloads = [];

        int sequence;

        public List<Command> Commands { get; } = [];

        public void Record(CommandKind kind, Entity entity, ComponentTypeId component, int slot, int sortKey) =>
            Commands.Add(new(kind, entity, component, slot, sortKey, sequence++, index));

        public void Add<T>(Entity entity, in T value, int sortKey) =>
            Record(CommandKind.Add, entity, ComponentType<T>.Id, Store<T>().Add(value), sortKey);

        public void Set<T>(Entity entity, in T value, int sortKey) =>
            Record(CommandKind.Set, entity, ComponentType<T>.Id, Store<T>().Add(value), sortKey);

        /// <summary>Registers the typed store a removal needs, without recording a value in it.</summary>
        public int RemoverSlot<T>() {
            _ = Store<T>();
            return -1;
        }

        public IPayloadStore Payload(ComponentTypeId component) => payloads[component];

        public void Clear() {
            Commands.Clear();
            sequence = 0;

            foreach (var payload in payloads.Values) {
                payload.Clear();
            }
        }

        PayloadStore<T> Store<T>() {
            if (payloads.TryGetValue(ComponentType<T>.Id, out var existing)) {
                return (PayloadStore<T>)existing;
            }

            var store = new PayloadStore<T>();
            payloads[ComponentType<T>.Id] = store;
            return store;
        }
    }

    /// <summary>The type-erased half of a channel's per-component value list.</summary>
    interface IPayloadStore {
        void Apply(CommandKind kind, World world, Entity entity, int slot);

        void Clear();
    }

    /// <summary>
    ///     One channel's recorded values for one component type.
    /// </summary>
    /// <remarks>
    ///     Typed rather than a byte blob, which is what lets a managed component ride in a command
    ///     buffer at all — and what keeps the whole thing free of the reflective "construct the
    ///     store for this id" step that would not survive NativeAOT. The type parameter is closed at
    ///     the recording call site, where it is known.
    /// </remarks>
    sealed class PayloadStore<T> : IPayloadStore {
        readonly List<T> values = [];

        public int Add(in T value) {
            values.Add(value);
            return values.Count - 1;
        }

        public void Apply(CommandKind kind, World world, Entity entity, int slot) {
            switch (kind) {
                case CommandKind.Add when world.Has<T>(entity):
                    // Add rather than refuse: see the class remarks. A recorder cannot look.
                    world.Set(entity, values[slot]);
                    break;

                case CommandKind.Add:
                    world.Add(entity, values[slot]);
                    break;

                case CommandKind.Set:
                    world.Set(entity, values[slot]);
                    break;

                case CommandKind.Remove when world.Has<T>(entity):
                    world.Remove<T>(entity);
                    break;

                default:
                    break;
            }
        }

        public void Clear() => values.Clear();
    }
}
