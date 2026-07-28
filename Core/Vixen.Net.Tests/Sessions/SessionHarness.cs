// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Net.Sessions;
using Vixen.Net.Transport;
using Vixen.Net.Transport.Local;

namespace Vixen.Net.Tests.Sessions;

/// <summary>A server and however many clients a test wants, all in this process, all driven by hand.</summary>
public sealed class SessionHarness : IDisposable {
    readonly LocalNetwork network = new();
    readonly List<NetworkSession> sessions = [];

    /// <summary>One step of the loop these sessions are driven by.</summary>
    public static TimeSpan Step { get; } = TimeSpan.FromMilliseconds(16);

    /// <summary>Everything the sessions have been told, in order.</summary>
    public List<string> Log { get; } = [];

    /// <summary>Makes a server session, listening.</summary>
    /// <param name="options">How it behaves.</param>
    /// <param name="authenticator">Who it lets in.</param>
    /// <returns>The session, started.</returns>
    public NetworkSession StartServer(SessionOptions? options = null, ISessionAuthenticator? authenticator = null) {
        var session = Track(new(new LocalTransport(network), options, authenticator, ownsTransport: true), "server");
        session.StartServer();

        return session;
    }

    /// <summary>Makes a host session — a server with a player of its own.</summary>
    /// <param name="options">How it behaves.</param>
    /// <returns>The session, started.</returns>
    public NetworkSession StartHost(SessionOptions? options = null) {
        var session = Track(new(new LocalTransport(network), options, null, ownsTransport: true), "host");
        session.StartHost();

        return session;
    }

    /// <summary>Makes a client session, connecting.</summary>
    /// <param name="options">How it behaves.</param>
    /// <param name="token">A reconnect token to present, if it is coming back.</param>
    /// <returns>The session, started.</returns>
    public NetworkSession StartClient(SessionOptions? options = null, ReadOnlySpan<byte> token = default) {
        var name = $"client{sessions.Count}";
        var session = Track(new(new LocalTransport(network), options, null, ownsTransport: true), name);

        if (!token.IsEmpty) {
            session.PresentReconnectToken(token);
        }

        session.StartClient();

        return session;
    }

    /// <summary>Makes a session over a transport the caller built — a simulated one, usually.</summary>
    /// <param name="transport">The transport.</param>
    /// <param name="name">What to call it in the log.</param>
    /// <param name="options">How it behaves.</param>
    /// <returns>The session, not started.</returns>
    public NetworkSession Add(ITransport transport, string name, SessionOptions? options = null) =>
        Track(new(transport, options, null, ownsTransport: true), name);

    /// <summary>A bare transport on the same network, for a test that wants to misbehave.</summary>
    /// <returns>The transport, not started. Disposed with the harness.</returns>
    public LocalTransport RawTransport() => new(network);

    /// <summary>Runs every session for a while.</summary>
    /// <param name="rounds">How many steps.</param>
    /// <param name="messages">Where user payloads go.</param>
    public void Pump(int rounds = 8, ISessionMessageHandler? messages = null) {
        for (var round = 0; round < rounds; round++) {
            foreach (var session in sessions) {
                session.Update(Step, messages);
            }
        }
    }

    /// <summary>Runs one session for a while, leaving the others frozen.</summary>
    /// <param name="session">The one to run.</param>
    /// <param name="rounds">How many steps.</param>
    /// <param name="messages">Where user payloads go.</param>
    public static void PumpOnly(NetworkSession session, int rounds, ISessionMessageHandler? messages = null) {
        for (var round = 0; round < rounds; round++) {
            session.Update(Step, messages);
        }
    }

    /// <summary>Disposes every session the test made.</summary>
    public void Dispose() {
        foreach (var session in sessions) {
            session.Dispose();
        }
    }

    /// <summary>UTF-8, so a test can read its own payloads.</summary>
    /// <param name="text">The text.</param>
    /// <returns>Its bytes.</returns>
    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    NetworkSession Track(NetworkSession session, string name) {
        sessions.Add(session);
        session.PlayerJoined += player => Log.Add($"{name}: {player.Id} joined");
        session.PlayerLeft += (player, reason) => Log.Add($"{name}: {player.Id} left ({reason})");

        session.PlayerConnectionChanged += player =>
            Log.Add($"{name}: {player.Id} {(player.IsConnected ? "back" : "away")}");

        session.Rejected += (reason, text) => Log.Add($"{name}: rejected ({reason}) {text}");

        return session;
    }
}

/// <summary>Records the payloads the sessions hand out.</summary>
public sealed class MessageRecorder : ISessionMessageHandler {
    /// <summary>Who sent what, in order.</summary>
    public List<(PlayerId From, Channel Channel, string Text)> Messages { get; } = [];

    /// <inheritdoc />
    public void OnMessage(PlayerId from, Channel channel, ReadOnlySpan<byte> payload) =>
        Messages.Add((from, channel, Encoding.UTF8.GetString(payload)));

    /// <summary>Just the text of each message.</summary>
    /// <returns>The texts, in order.</returns>
    public List<string> Texts() {
        var result = new List<string>();

        foreach (var message in Messages) {
            result.Add(message.Text);
        }

        return result;
    }
}

/// <summary>An authenticator a test drives by hand.</summary>
/// <param name="decision">What it says.</param>
public sealed class ScriptedAuthenticator(AuthenticationDecision decision) : ISessionAuthenticator {
    /// <summary>What it will say next time it is asked.</summary>
    public AuthenticationDecision Decision { get; set; } = decision;

    /// <summary>How many times it has been asked.</summary>
    public int Asked { get; private set; }

    /// <summary>What the last request carried.</summary>
    public byte[] LastPayload { get; private set; } = [];

    /// <summary>Whether the last request was a recognised reconnect.</summary>
    public bool LastWasReconnect { get; private set; }

    /// <inheritdoc />
    public AuthenticationDecision Authenticate(in AuthenticationRequest request) {
        Asked++;
        LastPayload = request.Payload.ToArray();
        LastWasReconnect = request.IsReconnect;

        return Decision;
    }
}
