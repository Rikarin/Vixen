// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Net.Sessions;
using Vixen.Net.Transport;

namespace Vixen.Net.Diagnostics;

/// <summary>The spans a session emits, published where anything can listen.</summary>
/// <remarks>
///     <para>
///         <b><c>System.Diagnostics.Activity</c> is OpenTelemetry's tracing API in .NET</b>, the same
///         way <c>System.Diagnostics.Metrics</c> is its metrics one — so this file, like
///         <see cref="NetworkMetrics" />, depends on nothing at all. A server that already has a
///         pipeline gets every span below by adding <see cref="SourceName" /> to its source list;
///         <c>Vixen.Net.Telemetry</c> is there for one that does not.
///     </para>
///     <para>
///         <b>The handshake is the thing worth a span, and a tick is not.</b> A tick is one number
///         asked sixty times a second, which is what a histogram is for and what
///         <c>vixen.net.tick.duration</c> already answers. A handshake is four steps that can each
///         fail differently — protocol, content hash, an authenticator that may be a network call of
///         its own, and admission — happening at a rate a trace backend can afford, and the question
///         it answers is the one metrics cannot: <i>which</i> step this player's failed connection
///         died at, and how long the one before it took.
///     </para>
///     <para>
///         ⚠ <b>A handshake span outlives the call that started it.</b> The authenticator may answer
///         <c>Pending</c> and be asked again on later frames, so the <c>Activity</c> is carried on the
///         pending request and stopped by whichever of admission, rejection, timeout or a dropped
///         connection gets there first. An <c>Activity</c> that is never stopped is never exported —
///         it is not a wrong span, it is no span, which reads exactly like a handshake that never
///         happened. Every exit is therefore accounted for rather than the happy one.
///     </para>
///     <para>
///         <b>Nothing here costs anything when nobody is listening.</b>
///         <c>ActivitySource.StartActivity</c> returns null with no registered listener, and every
///         method below is written to be handed that null.
///     </para>
/// </remarks>
public static class NetworkActivity {
    /// <summary>The source's name, which is what a collector is configured with.</summary>
    public const string SourceName = "Vixen.Net";

    /// <summary>The span a handshake gets, on either side of it.</summary>
    public const string HandshakeName = "vixen.net.handshake";

    // Internal rather than public. The name is what a pipeline needs; the source itself is the
    // engine's, and a game starting its own spans on it would put them under a name a collector's
    // rules are written against.
    internal static readonly ActivitySource Source = new(SourceName);

    /// <summary>Starts the span for one connection's handshake.</summary>
    /// <param name="role">Which side this is.</param>
    /// <param name="connection">The connection it is about.</param>
    /// <returns>The span, or null when nothing is listening.</returns>
    /// <remarks>
    ///     <b>A client is the one that starts a trace and a server is the one that continues it</b> —
    ///     except that a handshake carries no trace context, so today they are two roots that a
    ///     backend joins by time and address rather than by parentage. Propagating the context would
    ///     be a field in <c>ConnectRequest</c>, which is a wire change; it is recorded here rather
    ///     than left to be rediscovered as a missing feature.
    /// </remarks>
    internal static Activity? StartHandshake(TransportRole role, ConnectionId connection) {
        var activity = Source.StartActivity(
            HandshakeName,
            role == TransportRole.Server ? ActivityKind.Server : ActivityKind.Client
        );

        activity?.SetTag("vixen.net.role", role == TransportRole.Server ? "server" : "client");

        if (connection.IsValid) {
            activity?.SetTag("vixen.net.connection", connection.Value);
        }

        return activity;
    }

    /// <summary>Records a step of the handshake that succeeded, so the failing one is the last event.</summary>
    /// <param name="activity">The span, or null.</param>
    /// <param name="name">What passed.</param>
    internal static void Step(this Activity? activity, string name) => activity?.AddEvent(new(name));

    /// <summary>Ends the span as an admitted player.</summary>
    /// <param name="activity">The span, or null.</param>
    /// <param name="player">Who they turned out to be.</param>
    /// <param name="resumed">Whether this was a reconnect rather than an arrival.</param>
    internal static void Admitted(this Activity? activity, PlayerId player, bool resumed) {
        if (activity is null) {
            return;
        }

        activity.SetTag("vixen.net.player", player.Value);
        activity.SetTag("vixen.net.handshake.outcome", resumed ? "resumed" : "admitted");
        activity.SetStatus(ActivityStatusCode.Ok);
        activity.Dispose();
    }

    /// <summary>Ends the span as a refusal, naming which of them it was.</summary>
    /// <param name="activity">The span, or null.</param>
    /// <param name="reason">Which refusal.</param>
    /// <param name="text">What the peer was told.</param>
    /// <remarks>
    ///     ⚠ <b>A refusal is <see cref="ActivityStatusCode.Error" /> and that is deliberate, even
    ///     though most refusals are the server working correctly.</b> A protocol mismatch during a
    ///     rollout is not a fault and is still the single most useful thing a trace backend can be
    ///     asked to show all of; the tag is what tells the ordinary refusals from the alarming ones,
    ///     and the status is what makes them findable at all.
    /// </remarks>
    internal static void Refused(this Activity? activity, SessionRejectReason reason, string text) {
        if (activity is null) {
            return;
        }

        activity.SetTag("vixen.net.handshake.outcome", "refused");
        activity.SetTag("vixen.net.handshake.refusal", reason.ToString());
        activity.SetStatus(ActivityStatusCode.Error, text);
        activity.Dispose();
    }

    /// <summary>Ends the span for a handshake that stopped without an answer.</summary>
    /// <param name="activity">The span, or null.</param>
    /// <param name="why">What happened to it.</param>
    /// <remarks>
    ///     The exit that would otherwise be missing. A peer that drops mid-handshake, and a session
    ///     that is stopped while somebody is halfway in, both leave a request in the pending table
    ///     that nothing else will ever answer — and a span left open is a span nothing exports.
    /// </remarks>
    internal static void Abandoned(this Activity? activity, string why) {
        if (activity is null) {
            return;
        }

        activity.SetTag("vixen.net.handshake.outcome", why);
        activity.SetStatus(ActivityStatusCode.Error, why);
        activity.Dispose();
    }
}
