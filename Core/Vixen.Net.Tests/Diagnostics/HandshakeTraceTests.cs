// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Net.Diagnostics;
using Vixen.Net.Sessions;
using Vixen.Net.Tests.Sessions;
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

    static string? Tag(Activity activity, string name) => activity.GetTagItem(name)?.ToString();

    static List<string> Events(Activity activity) {
        var names = new List<string>();

        foreach (var item in activity.Events) {
            names.Add(item.Name);
        }

        return names;
    }

    /// <summary>Every span the source emitted while this exists.</summary>
    /// <remarks>
    ///     ⚠ <c>Sample</c> returns <see cref="ActivitySamplingResult.AllData" /> and nothing less.
    ///     <c>PropagationData</c> creates an <c>Activity</c> that records no tags and no events, so a
    ///     listener written that way collects spans whose every assertion below would be a null
    ///     compared with a null — the shape of a test that cannot fail.
    /// </remarks>
    sealed class Recorder : IDisposable {
        readonly ActivityListener listener;

        public Recorder() {
            listener = new() {
                ShouldListenTo = source => source.Name == NetworkActivity.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = Finished.Add
            };

            ActivitySource.AddActivityListener(listener);
        }

        public List<Activity> Finished { get; } = [];

        /// <summary>The one span from that side, insisting there is exactly one.</summary>
        public Activity One(string role) {
            var matching = Finished.FindAll(activity => (activity.GetTagItem("vixen.net.role") as string) == role);

            return Assert.Single(matching);
        }

        public void Dispose() => listener.Dispose();
    }
}
