// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using Vixen.Net;
using Vixen.Net.Transport;
using Vixen.Ui.Reactive;

namespace Vixen.Editor.Debugger;

/// <summary>Where the editor's end of an inspector connection has got to.</summary>
public enum RemoteState : byte {
    /// <summary>Nothing attached.</summary>
    Detached,

    /// <summary>Connecting, or waiting for the far end's greeting.</summary>
    Attaching,

    /// <summary>Attached and talking.</summary>
    Attached,

    /// <summary>
    ///     Attached to something that speaks a different version of the protocol, which is a state of
    ///     its own rather than a failure to connect.
    /// </summary>
    Incompatible
}

/// <summary>The editor's half of doc 13's remote inspector.</summary>
/// <remarks>
///     <para>
///         <b>Doc 20's E4 exit criterion in one object: "a build on a device can be attached to and
///         an entity mutated live".</b> What it owns is the conversation — greet, ask for the tree,
///         collect what arrives, write a value back — and nothing about sockets: which transport
///         reaches which device is <c>Vixen.Net</c>'s question, which is what lets the whole of this
///         be tested over a loopback transport with no network at all.
///     </para>
///     <para>
///         ⚠ <b>Nothing is delivered outside <see cref="Poll" />.</b> That is the transport's own
///         contract and this keeps it: the entity tree is rebuilt on the thread that called Poll,
///         which is the frame thread, so the panel reading it never needs a lock. A client that
///         raised events from a socket thread would put a background thread inside the editor's
///         element tree.
///     </para>
///     <para>
///         ⚠ <b>The tree is rebuilt into a staging list and swapped, not mutated in place.</b> An
///         entity list that emptied and refilled over several polls would be a panel that blinks
///         every time somebody presses Refresh — and, worse, one whose selection is gone for a frame.
///     </para>
///     <para>
///         ⚠ <b>A version mismatch is a state, not an exception.</b> An editor attached to last
///         week's build is the ordinary case on a device; saying so is useful, and half-reading its
///         messages would show an empty tree that looks exactly like a build with no entities in it.
///     </para>
/// </remarks>
public sealed class RemoteInspectorClient : ITransportEvents, IDisposable {
    // ⚠ Signal-backed **additively**, which is `DeviceManager`'s bargain and is spelled out here
    // because the two halves of this class are on opposite sides of it.
    //
    // `Changed` is still raised by every path that raised it before, and the panel's entity tree is
    // still fed from that event — `TreeView` is painted from `TreeNode` *data* by `Refresh()`, which
    // is the panel ledger's shape 1 and which no attribute could ever bind. What the signals buy is
    // the half that *is* expressible: the status sentence, the Attach button's label, and the two
    // greyed buttons, which between them were the whole of `RemoteInspectorView.Restate`.
    //
    // ⚠ And **not** one `Signal<int>` that everything reads to force a re-evaluation. Each of these
    // is a value that genuinely changes, so a revision counter would stand in for five notifications
    // that are all perfectly expressible — and it would defeat the equality check that makes an
    // unchanged poll cost nothing, which matters here more than anywhere: `Poll` runs every frame.
    readonly Signal<RemoteState> state = new(RemoteState.Detached);
    readonly Signal<string?> buildName = new(null);
    readonly Signal<ushort> buildVersion = new(0);
    readonly Signal<bool> fetching = new(false);
    readonly CollectionSignal<RemoteEntity> entities = new();

    // ⚠ A `SignalDictionary`, which is the map shape wave 6 wanted and did not find. It used to be a
    // `Signal<ImmutableDictionary<string, double>>` — correct, because replacing the map *is* the
    // notification, and it allocated a rebalanced spine of tree nodes every time a build said its
    // frame rate had moved. `Poll` runs from the panel's tick, so that was per counter per frame.
    //
    // ⚠ **The equality short-circuit survives the change and had to.** `SetItem` with a value the
    // map already held returned the same instance, so the `Signal`'s reference comparison saw
    // nothing move and woke nobody; `SignalDictionary` checks the value comparer per key and reaches
    // the same answer without the copy. A build reporting an unchanged number still costs the panel
    // nothing, which is the property that makes a per-frame poll affordable at all.
    readonly SignalDictionary<string, double> counters = new(StringComparer.Ordinal);

    readonly ITransport transport;
    readonly ArrayBufferWriter<byte> outgoing = new(1024);
    readonly List<RemoteEntity> staging = [];

    // ⚠ Deliberately *not* signal-backed, which is what "additively" means. Nothing binds the log —
    // no panel in the tree draws it — so signal-backing it would be reactivity nobody reads, paid
    // for on every line of a conversation that is bounded at two hundred.
    readonly List<string> log = [];

    bool disposed;

    /// <summary>Attaches to a transport that has already been pointed at an endpoint.</summary>
    /// <param name="transport">The transport. This does not own it and will not dispose it.</param>
    /// <param name="editorName">What the editor calls itself in its greeting.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public RemoteInspectorClient(ITransport transport, string editorName = "Vixen Editor") {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(editorName);

        this.transport = transport;
        EditorName = editorName;
    }

    /// <summary>How many lines of conversation are kept.</summary>
    /// <remarks>
    ///     Bounded because a build reporting a result per frame would otherwise fill the editor's
    ///     heap with a log nobody scrolls to the top of. The console is where a build's *own* log
    ///     goes; this is the handful of lines about the connection itself.
    /// </remarks>
    public const int LogCapacity = 200;

    /// <summary>What the editor calls itself.</summary>
    public string EditorName { get; }

    /// <summary>Where the conversation has got to.</summary>
    /// <remarks>
    ///     ⚠ <b>Signal-backed, and a read is still immediate.</b> Only the effects a write schedules
    ///     are queued (ADR-007), so <c>RemoteInspectorTests</c>' <c>Assert.Equal(RemoteState.Attached,
    ///     client.State)</c> on the line after <c>Settle()</c> means exactly what it did.
    /// </remarks>
    public RemoteState State {
        get => state.Value;
        private set => state.Value = value;
    }

    /// <summary>What the far end calls itself, once it has said.</summary>
    /// <inheritdoc cref="State" select="remarks" />
    public string? BuildName {
        get => buildName.Value;
        private set => buildName.Value = value;
    }

    /// <summary>What version it speaks.</summary>
    /// <inheritdoc cref="State" select="remarks" />
    public ushort BuildVersion {
        get => buildVersion.Value;
        private set => buildVersion.Value = value;
    }

    /// <summary>The entities it has reported, in the order they arrived.</summary>
    /// <remarks>
    ///     ⚠ <b>A <see cref="CollectionSignal{T}" />, which already <i>is</i> an
    ///     <c>IReadOnlyList&lt;T&gt;</c></b> — so the property is unchanged and counting it inside a
    ///     binding subscribes. That is what makes the status line say "3 entit(ies)" without anybody
    ///     calling <c>Restate</c>.
    /// </remarks>
    public IReadOnlyList<RemoteEntity> Entities => entities;

    /// <summary>The live counters, by name.</summary>
    /// <remarks>
    ///     ⚠ <b>A <see cref="SignalDictionary{TKey,TValue}" />, which already <i>is</i> an
    ///     <c>IReadOnlyDictionary&lt;string, double&gt;</c></b> — so the property's type is unchanged
    ///     and reading a key, a count or the key set inside a binding subscribes, exactly as
    ///     <see cref="Entities" /> does. ⚠ What did change is that this is a <b>live view and not a
    ///     snapshot</b>: it used to hand out an immutable map that could be held across frames, and
    ///     holding this one across frames reads whatever the map says later. Nothing in the tree does
    ///     — the counter pane projects it to a sorted array inside its binding — and a caller that
    ///     wants a snapshot takes one.
    /// </remarks>
    public IReadOnlyDictionary<string, double> Counters => counters;

    /// <summary>The last few things that happened, oldest first.</summary>
    /// <remarks>⚠ Not signal-backed; nothing binds it. See the field.</remarks>
    public IReadOnlyList<string> Log => log;

    /// <summary>Whether a tree is still arriving.</summary>
    /// <inheritdoc cref="State" select="remarks" />
    public bool IsFetching {
        get => fetching.Value;
        private set => fetching.Value = value;
    }

    /// <summary>Raised when the entities, the counters or the state changed.</summary>
    public event Action<RemoteInspectorClient>? Changed;

    /// <summary>Starts the transport's client half and greets whatever answers.</summary>
    public void Attach() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (State is not RemoteState.Detached) {
            return;
        }

        State = RemoteState.Attaching;
        Note("Attaching…");

        try {
            transport.StartClient();
        } catch (TransportException exception) {
            // A refused endpoint is an ordinary answer to "attach to this device", not a reason to
            // take the editor down — the device may simply not be running the build yet.
            State = RemoteState.Detached;
            Note("Could not attach: " + exception.Message);
        }

        Changed?.Invoke(this);
    }

    /// <summary>Stops the transport's client half and forgets everything it said.</summary>
    public void Detach() {
        if (State is RemoteState.Detached) {
            return;
        }

        transport.StopClient();
        Reset();

        Note("Detached.");
        Changed?.Invoke(this);
    }

    /// <summary>Asks the far end for its entity tree.</summary>
    public void Refresh() {
        if (State is not RemoteState.Attached) {
            return;
        }

        staging.Clear();
        IsFetching = true;

        Send(writer => InspectorProtocol.WriteBare(writer, InspectorMessage.RequestTree));
    }

    /// <summary>Writes a component member on the far end.</summary>
    /// <param name="entity">Which entity.</param>
    /// <param name="member">Which member, as <c>Component.Member</c>.</param>
    /// <param name="value">The new value, as text.</param>
    /// <exception cref="ArgumentNullException">Either string is null.</exception>
    public void SetValue(ulong entity, string member, string value) {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(value);

        if (State is not RemoteState.Attached) {
            return;
        }

        Send(writer => InspectorProtocol.WriteSetValue(writer, entity, member, value));
        Note($"Set {member} on entity {entity}.");
    }

    /// <summary>Asks the far end to do something — capture a frame, collect, reload shaders.</summary>
    /// <param name="verb">What to do.</param>
    /// <exception cref="ArgumentNullException"><paramref name="verb" /> is null.</exception>
    public void Command(string verb) {
        ArgumentNullException.ThrowIfNull(verb);

        if (State is not RemoteState.Attached) {
            return;
        }

        Send(writer => InspectorProtocol.WriteText(writer, InspectorMessage.Command, verb));
        Note("Sent: " + verb);
    }

    /// <summary>Advances the transport and delivers whatever arrived.</summary>
    /// <param name="elapsed">How much time has passed.</param>
    public void Poll(TimeSpan elapsed) {
        if (disposed) {
            return;
        }

        transport.Poll(elapsed, this);
    }

    /// <inheritdoc />
    public void OnConnected(TransportRole role, ConnectionId connection) {
        if (role is not TransportRole.Client) {
            return;
        }

        Note("Connected. Saying hello.");
        Send(writer => InspectorProtocol.WriteHello(writer, EditorName));
    }

    /// <inheritdoc />
    public void OnDisconnected(TransportRole role, ConnectionId connection, DisconnectReason reason) {
        if (role is not TransportRole.Client) {
            return;
        }

        Reset();
        Note("Disconnected: " + reason + ".");

        Changed?.Invoke(this);
    }

    /// <inheritdoc />
    public void OnData(TransportRole role, ConnectionId connection, Channel channel, ReadOnlySpan<byte> payload) {
        if (role is not TransportRole.Client || !InspectorProtocol.TryReadKind(payload, out var message)) {
            return;
        }

        switch (message) {
            case InspectorMessage.Welcome:
                Welcome(payload);
                break;

            case InspectorMessage.Entity:
                if (InspectorProtocol.TryReadEntity(payload, out var entity) && entity is not null) {
                    staging.Add(entity);
                }

                break;

            case InspectorMessage.TreeComplete:
                entities.Clear();

                foreach (var arrived in staging) {
                    entities.Add(arrived);
                }

                staging.Clear();

                IsFetching = false;
                Note($"{entities.Count} entities.");

                Changed?.Invoke(this);
                break;

            case InspectorMessage.Counter:
                if (InspectorProtocol.TryReadCounter(payload, out var counter)) {
                    counters[counter.Name] = counter.Value;
                    Changed?.Invoke(this);
                }

                break;

            case InspectorMessage.Result:
                if (InspectorProtocol.TryReadText(payload, out var text)) {
                    Note(text);
                    Changed?.Invoke(this);
                }

                break;

            default:
                // A message the editor does not send and does not expect back. Ignored rather than
                // refused: a newer build sending something extra should not break an older editor,
                // which is the whole reason the kind is a byte at a fixed offset.
                break;
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        // The transport is the caller's — it may be a listen server the editor also uses for
        // something else — so this stops its client half and leaves the object alone.
        if (State is not RemoteState.Detached) {
            transport.StopClient();
        }

        Reset();
    }

    void Welcome(ReadOnlySpan<byte> payload) {
        if (!InspectorProtocol.TryReadGreeting(payload, out var version, out var name)) {
            return;
        }

        BuildVersion = version;
        BuildName = name;

        if (version != InspectorProtocol.Version) {
            State = RemoteState.Incompatible;
            Note($"'{name}' speaks protocol {version}; this editor speaks {InspectorProtocol.Version}.");
        } else {
            State = RemoteState.Attached;
            Note("Attached to " + name + ".");

            Refresh();
        }

        Changed?.Invoke(this);
    }

    void Send(Action<ArrayBufferWriter<byte>> write) {
        outgoing.Clear();
        write(outgoing);

        // Reliable, because every message here is a request or a description and losing one leaves
        // the two ends disagreeing about what was asked. Counters are the one thing that supersedes
        // itself, and they travel the other way.
        transport.SendToServer(outgoing.WrittenSpan, Channel.Reliable);
    }

    void Reset() {
        State = RemoteState.Detached;
        BuildName = null;
        BuildVersion = 0;
        IsFetching = false;

        entities.Clear();
        staging.Clear();
        counters.Clear();
    }

    void Note(string line) {
        log.Add(line);

        if (log.Count > LogCapacity) {
            log.RemoveRange(0, log.Count - LogCapacity);
        }
    }
}
