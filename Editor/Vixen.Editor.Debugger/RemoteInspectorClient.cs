// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using Vixen.Net;
using Vixen.Net.Transport;

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
    readonly ITransport transport;
    readonly ArrayBufferWriter<byte> outgoing = new(1024);
    readonly List<RemoteEntity> entities = [];
    readonly List<RemoteEntity> staging = [];
    readonly Dictionary<string, double> counters = new(StringComparer.Ordinal);
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
    public RemoteState State { get; private set; } = RemoteState.Detached;

    /// <summary>What the far end calls itself, once it has said.</summary>
    public string? BuildName { get; private set; }

    /// <summary>What version it speaks.</summary>
    public ushort BuildVersion { get; private set; }

    /// <summary>The entities it has reported, in the order they arrived.</summary>
    public IReadOnlyList<RemoteEntity> Entities => entities;

    /// <summary>The live counters, by name.</summary>
    public IReadOnlyDictionary<string, double> Counters => counters;

    /// <summary>The last few things that happened, oldest first.</summary>
    public IReadOnlyList<string> Log => log;

    /// <summary>Whether a tree is still arriving.</summary>
    public bool IsFetching { get; private set; }

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
                entities.AddRange(staging);
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
