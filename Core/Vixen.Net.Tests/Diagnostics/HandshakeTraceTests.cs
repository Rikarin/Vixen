// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Net.Diagnostics;
using Vixen.Net.Sessions;
using Vixen.Net.Tests.Sessions;
using Vixen.Net.Transport;
using Xunit;

namespace Vixen.Net.Tests.Diagnostics;

/// <summary>The span a handshake gets, and every way one can end.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The instrument is checked before anything it measures.</b> An
///         <c>ActivityListener</c> whose <c>Sample</c> returns anything but
///         <see cref="ActivitySamplingResult.AllData" /> records nothing at all, and a suite built on
///         one would assert an empty list against an empty list for ever. <see cref="Recorder" />
///         therefore proves it is listening on its first use — <see cref="TheListenerRecordsAtAll" />
///         — and every test below asserts a span is <i>there</i>, so a listener that stopped working
///         reds the whole file rather than passing it.
///     </para>
///     <para>
///         <b>The claim these hold is that no exit leaks.</b> An <c>Activity</c> nobody stops is
///         never exported, which is not a wrong span but no span — indistinguishable from a handshake
///         that never happened. So each test names an ending: admitted, refused for each of the
///         reasons a server has, dropped, and the session stopping underneath one.
///     </para>
/// </remarks>
public sealed class HandshakeTraceTests {
    /// <summary>That the listener is listening, asserted before anything relies on it.</summary>
    [Fact]
    public void TheListenerRecordsAtAll() {
        using var recorder = new Recorder();
        using var harness = new SessionHarness();

        harness.StartServer();
        harness.StartClient();
        harness.Pump();

        Assert.NotEmpty(recorder.Finished);
        Assert.All(recorder.Finished, activity => Assert.Equal(NetworkActivity.HandshakeName, activity.OperationName));
    }

    /// <summary>Both halves of an ordinary join get a span, and both say who arrived.</summary>
    [Fact]
    public void AJoinIsTwoSpansOneEachSide() {
        using var recorder = new Recorder();
        using var harness = new SessionHarness();

        harness.StartServer();
        harness.StartClient();
        harness.Pump();

        var server = recorder.One("server");
        var client = recorder.One("client");

        Assert.Equal(ActivityKind.Server, server.Kind);
        Assert.Equal(ActivityKind.Client, client.Kind);
        Assert.Equal("admitted", Tag(server, "vixen.net.handshake.outcome"));
        Assert.Equal("admitted", Tag(client, "vixen.net.handshake.outcome"));
        Assert.Equal(ActivityStatusCode.Ok, server.Status);
        Assert.Equal(ActivityStatusCode.Ok, client.Status);

        // The player id is on the span, which is the join between a trace and everything else that
        // talks about players.
        Assert.Equal("1", Tag(server, "vixen.net.player"));
        Assert.Equal("1", Tag(client, "vixen.net.player"));
    }

    /// <summary>The server's span carries the steps, so the last event is where a failure stopped.</summary>
    [Fact]
    public void TheServerSpanNamesTheStepsItGotThrough() {
        using var recorder = new Recorder();
        using var harness = new SessionHarness();

        harness.StartServer(authenticator: new ScriptedAuthenticator(AuthenticationDecision.Accept));
        harness.StartClient();
        harness.Pump();

        Assert.Equal(
            ["request_read", "protocol_agreed", "content_agreed", "authenticated"],
            Events(recorder.One("server"))
        );
    }

    /// <summary>A protocol mismatch stops at the first step, and the span says which refusal it was.</summary>
    /// <remarks>
    ///     The whole argument for tracing a handshake in one test: the events say the request parsed
    ///     and got no further, and the tag says why — which is what no counter can answer, because a
    ///     counter of refusals is a number and the question is always <i>which step</i>.
    /// </remarks>
    [Fact]
    public void AProtocolMismatchIsRefusedAtTheStepItFailed() {
        using var recorder = new Recorder();
        using var harness = new SessionHarness();

        harness.StartServer(new() { ProtocolVersion = 7 });
        harness.StartClient(new() { ProtocolVersion = 8 });
        harness.Pump();

        var server = recorder.One("server");

        Assert.Equal(["request_read"], Events(server));
        Assert.Equal("refused", Tag(server, "vixen.net.handshake.outcome"));
        Assert.Equal(nameof(SessionRejectReason.ProtocolMismatch), Tag(server, "vixen.net.handshake.refusal"));
        Assert.Equal(ActivityStatusCode.Error, server.Status);

        // And the client's own span records what it was told, from the other end.
        var client = recorder.One("client");

        Assert.Equal(nameof(SessionRejectReason.ProtocolMismatch), Tag(client, "vixen.net.handshake.refusal"));
    }

    /// <summary>Content that does not match is a different refusal one step further on.</summary>
    [Fact]
    public void AContentMismatchIsADifferentRefusalOneStepLater() {
        using var recorder = new Recorder();
        using var harness = new SessionHarness();

        harness.StartServer(new() { ContentHash = 1 });
        harness.StartClient(new() { ContentHash = 2 });
        harness.Pump();

        var server = recorder.One("server");

        Assert.Equal(["request_read", "protocol_agreed"], Events(server));
        Assert.Equal(nameof(SessionRejectReason.ContentMismatch), Tag(server, "vixen.net.handshake.refusal"));
    }

    /// <summary>An authenticator that says no ends the span, and it never reached "authenticated".</summary>
    [Fact]
    public void AnAuthenticatorThatSaysNoEndsTheSpan() {
        using var recorder = new Recorder();
        using var harness = new SessionHarness();

        harness.StartServer(authenticator: new ScriptedAuthenticator(AuthenticationDecision.Refuse("No.")));
        harness.StartClient();
        harness.Pump();

        var server = recorder.One("server");

        Assert.Equal(["request_read", "protocol_agreed", "content_agreed"], Events(server));
        Assert.Equal(nameof(SessionRejectReason.AuthenticationFailed), Tag(server, "vixen.net.handshake.refusal"));
    }

    /// <summary>A handshake that never finished is ended by the timeout rather than left open.</summary>
    /// <remarks>
    ///     The interesting span, and the one a request-scoped implementation would have lost: the
    ///     authenticator answers <c>Pending</c> for the whole test, so this handshake spans many
    ///     frames and is ended by something that never saw it start.
    /// </remarks>
    [Fact]
    public void AHandshakeThatTimesOutIsStillASpan() {
        using var recorder = new Recorder();
        using var harness = new SessionHarness();

        harness.StartServer(
            new() { AuthenticationTimeout = TimeSpan.FromMilliseconds(50) },
            new ScriptedAuthenticator(AuthenticationDecision.Pending)
        );

        harness.StartClient();
        harness.Pump(16);

        var server = recorder.One("server");

        Assert.Equal(nameof(SessionRejectReason.AuthenticationTimedOut), Tag(server, "vixen.net.handshake.refusal"));

        // It never got to "authenticated", because it never was.
        Assert.Equal(["request_read", "protocol_agreed", "content_agreed"], Events(server));
    }

    /// <summary>A server that is full refuses, and that refusal is a span like any other.</summary>
    /// <remarks>
    ///     ⚠ <b>The exit that is reached through a different door.</b> <c>Admit</c> takes the request
    ///     out of the pending table on its first line and only then discovers there is no room, so by
    ///     the time <c>RejectPending</c> runs there is nothing left in the table carrying the span —
    ///     and this is the refusal whose absence would matter most, because it is the one that means
    ///     the fleet needs another server.
    /// </remarks>
    [Fact]
    public void AFullServerRefusesWithASpanLikeAnyOther() {
        using var recorder = new Recorder();
        using var harness = new SessionHarness();

        harness.StartServer(new() { MaxPlayers = 1 });
        harness.StartClient();
        harness.StartClient();
        harness.Pump();

        var refused = recorder.Finished.FindAll(
            activity => (activity.GetTagItem("vixen.net.handshake.refusal") as string)
                == nameof(SessionRejectReason.ServerFull)
        );

        // One from the server, which decided it, and one from the client it told.
        Assert.Equal(2, refused.Count);
    }

    /// <summary>A session stopped with somebody halfway in ends their span rather than dropping it.</summary>
    /// <remarks>
    ///     ⚠ The exit that is easiest to leave out, and the one whose absence is invisible: an
    ///     unstopped <c>Activity</c> is not a broken span, it is no span, and a shutdown that lost
    ///     every in-flight handshake would read as a server nobody was connecting to.
    /// </remarks>
    [Fact]
    public void StoppingWithSomebodyHalfwayInEndsTheirSpan() {
        using var recorder = new Recorder();
        using var harness = new SessionHarness();

        var server = harness.StartServer(authenticator: new ScriptedAuthenticator(AuthenticationDecision.Pending));
        harness.StartClient();
        harness.Pump();

        Assert.Empty(recorder.Finished);

        server.Stop();

        Assert.Equal("session_stopped", Tag(recorder.One("server"), "vixen.net.handshake.outcome"));
    }

    /// <summary>A second connect on the client half ends the first handshake rather than losing it.</summary>
    /// <remarks>
    ///     ⚠ <b>The client arm assumed exactly one transport-level connect per session lifetime and
    ///     nothing enforced that assumption.</b> A transport that retries a route — which is what
    ///     transport fallback is — reports <c>OnConnected(Client, …)</c> again, and the assignment
    ///     that started the new span used to overwrite the old one without ending it. That is
    ///     invisible with no listener registered, which is why nothing noticed, and a span that is
    ///     never exported with one.
    /// </remarks>
    [Fact]
    public void ASecondClientConnectEndsTheFirstHandshake() {
        using var recorder = new Recorder();
        using var transport = new ScriptedClientTransport();
        using var session = new NetworkSession(transport, ownsTransport: false);

        session.StartClient();

        transport.Connect();
        session.Update(SessionHarness.Step);

        transport.Connect();
        session.Update(SessionHarness.Step);

        // The first span is ended by the second connect; the second is still open until something
        // else ends it, so the session is stopped to make both of them observable in one list.
        session.Stop();

        var outcomes = new List<string?>();

        foreach (var activity in recorder.Finished) {
            outcomes.Add(Tag(activity, "vixen.net.handshake.outcome"));
        }

        Assert.Equal(["reconnected", "session_stopped"], outcomes);
    }

    /// <summary>And so does a second connect on a connection the server is already deciding about.</summary>
    /// <remarks>
    ///     The same rule one arm over. A pending request is keyed by connection id, so a transport
    ///     that reports the same number twice replaces the entry — and the span it carried was the
    ///     only thing that would ever have ended it.
    /// </remarks>
    [Fact]
    public void ASecondConnectOnAPendingConnectionEndsItsHandshake() {
        using var recorder = new Recorder();
        using var transport = new ScriptedClientTransport();
        using var session = new NetworkSession(transport, ownsTransport: false);

        session.StartServer();

        transport.Connect(TransportRole.Server, new(7));
        session.Update(SessionHarness.Step);

        transport.Connect(TransportRole.Server, new(7));
        session.Update(SessionHarness.Step);

        Assert.Equal("reconnected", Tag(Assert.Single(recorder.Finished), "vixen.net.handshake.outcome"));
    }

    static string? Tag(Activity activity, string name) => activity.GetTagItem(name)?.ToString();

    /// <summary>A transport that reports exactly what a test tells it to, when the test says so.</summary>
    /// <remarks>
    ///     No shipped transport connects twice without a disconnect in between, which is why the
    ///     defect above was latent. Driving the callbacks by hand is what makes the interleaving a
    ///     test rather than a thought experiment, and it costs nothing in wall clock: the events
    ///     happen in <see cref="Poll" />, in the order they were queued, like every real transport's.
    /// </remarks>
    sealed class ScriptedClientTransport : ITransport {
        readonly Queue<(TransportRole Role, ConnectionId Connection)> connects = new();

        public TransportCapabilities Capabilities => new(1200, IsInProcess: true, IsLossy: false);
        public TransportState ServerState { get; private set; }
        public TransportState ClientState { get; private set; }

        /// <summary>Queues a connect to be reported on the next poll.</summary>
        public void Connect(TransportRole role = TransportRole.Client, ConnectionId connection = default) =>
            connects.Enqueue((role, connection));

        public void StartServer() => ServerState = TransportState.Running;
        public void StopServer() => ServerState = TransportState.Stopped;
        public void StartClient() => ClientState = TransportState.Running;
        public void StopClient() => ClientState = TransportState.Stopped;
        public void Disconnect(ConnectionId connection) { }
        public void SendToClient(ConnectionId connection, ReadOnlySpan<byte> payload, Channel channel) { }
        public void SendToServer(ReadOnlySpan<byte> payload, Channel channel) { }

        public void Poll(TimeSpan elapsed, ITransportEvents events) {
            while (connects.TryDequeue(out var connect)) {
                events.OnConnected(connect.Role, connect.Connection);
            }
        }

        public void Dispose() { }
    }

    static List<string> Events(Activity activity) {
        var names = new List<string>();

        foreach (var item in activity.Events) {
            names.Add(item.Name);
        }

        return names;
    }

    /// <summary>Every span this test's own sessions emitted.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <c>Sample</c> returns <see cref="ActivitySamplingResult.AllData" /> and nothing less.
    ///         <c>PropagationData</c> creates an <c>Activity</c> that records no tags and no events, so a
    ///         listener written that way collects spans whose every assertion above would be a null
    ///         compared with a null — the shape of a test that cannot fail.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it filters by trace, which is not tidiness — the first version of this was
    ///         flaky and passed alone.</b> An <c>ActivityListener</c> subscribes to a source
    ///         <i>process-wide</i> and by name, so every other test in this assembly that runs a
    ///         session — and xunit runs classes in parallel — was dropping its handshakes into this
    ///         list. <c>One("server")</c> insists on exactly one and would find two, at random,
    ///         depending on what else happened to be running. That is this repository's own "a
    ///         different test failing each run is one shared cause" in miniature.
    ///     </para>
    ///     <para>
    ///         The fix is to give the test a root span of its own. <c>Activity.Current</c> is an
    ///         <c>AsyncLocal</c>, so the sessions this test drives start their handshakes underneath
    ///         it and inherit its trace id, while a session in a parallel test inherits a different
    ///         one. Filtering on the trace id is therefore an exact answer rather than a heuristic.
    ///     </para>
    /// </remarks>
    sealed class Recorder : IDisposable {
        // ⚠ A const, and the predicate below closes over *it* rather than over `Own`. Constructing an
        // `ActivitySource` calls every registered listener's `ShouldListenTo` — including the one a
        // previous `Recorder` left behind — so a predicate that read `Own.Name` would be asked about
        // `Own` while `Own` was the field being initialised, and would dereference null inside a
        // class constructor. The first version did exactly that, and it presented as forty-five
        // unrelated tests failing, because a listener that throws breaks every `ActivitySource` any
        // of them creates.
        const string OwnName = "Vixen.Net.Tests.HandshakeTraces";

        static readonly ActivitySource Own = new(OwnName);

        readonly ActivityListener listener;
        readonly Activity? root;

        public Recorder() {
            listener = new() {
                ShouldListenTo = source => source.Name is NetworkActivity.SourceName or OwnName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = Keep
            };

            ActivitySource.AddActivityListener(listener);
            root = Own.StartActivity("one test");

            Assert.NotNull(root);
        }

        public List<Activity> Finished { get; } = [];

        /// <summary>The one span from that side, insisting there is exactly one.</summary>
        public Activity One(string role) {
            var matching = Finished.FindAll(activity => (activity.GetTagItem("vixen.net.role") as string) == role);

            return Assert.Single(matching);
        }

        public void Dispose() {
            root?.Dispose();
            listener.Dispose();
        }

        // Called on whatever thread stopped the activity, which for a parallel test is not this one —
        // hence the filter before the add rather than after it.
        void Keep(Activity activity) {
            if (activity.Source.Name == NetworkActivity.SourceName && activity.TraceId == root?.TraceId) {
                Finished.Add(activity);
            }
        }
    }
}
