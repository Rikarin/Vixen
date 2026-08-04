// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Live;

/// <summary>The four lines a realm and its launcher say to each other over stdio.</summary>
/// <remarks>
///     <para>
///         <b>A realm with no orchestrator still needs a control plane, and this is the smallest one
///         that is not a lie: the process's own standard streams.</b> The launcher writes commands to
///         stdin, the realm writes its state to stdout, and both are things every one of the three
///         placement backends already has — <c>Process</c> directly, Docker through the container's
///         attached streams, Kubernetes through the pod's. Doc 27 § Cost defines L0 as a dedicated
///         server with a lifecycle and no orchestrator intelligence; this is the wire that lifecycle
///         travels on until L1 replaces it with grain calls.
///     </para>
///     <para>
///         ⚠ <b>This is a lifecycle channel, not a management API, and the asymmetry is deliberate.</b>
///         Everything here is a statement about the process as a whole — it is up, drain it, stop it.
///         Nothing player-specific, nothing per-tick and nothing that needs an answer, because the
///         moment stdio carries a request-response protocol somebody has written an RPC layer with no
///         framing, no versioning and no authentication.
///     </para>
///     <para>
///         The prefix is on every line so that a realm's ordinary logging — which also goes to
///         stdout — cannot be mistaken for a signal, and so that a human reading a container's logs
///         can see the lifecycle in among the noise.
///     </para>
/// </remarks>
public static class RealmSignals {
    /// <summary>What every line starts with.</summary>
    public const string Prefix = "vixen-realm ";

    /// <summary>Written by the realm when its map is loaded and it is accepting sessions.</summary>
    /// <remarks>
    ///     Followed by the endpoint it actually bound, which is not always the one in the spec: a
    ///     backend may hand a realm port zero and let the operating system choose, and then this line
    ///     is the only thing that knows the answer.
    /// </remarks>
    public const string Ready = Prefix + "ready";

    /// <summary>Written by the realm when it is no longer taking arrivals.</summary>
    public const string Draining = Prefix + "draining";

    /// <summary>Written by the realm as the last thing it does.</summary>
    public const string Stopped = Prefix + "stopped";

    /// <summary>Read by the realm: stop taking arrivals and move everyone out at safe moments.</summary>
    public const string Drain = Prefix + "drain";

    /// <summary>Read by the realm: exit now.</summary>
    /// <remarks>
    ///     The polite half of <see cref="StopMode.Immediate" /> — a process that is going to be killed
    ///     anyway gets one chance to flush its logs and release its lease. What makes killing it
    ///     survivable is that nothing durable was in it (ADR-021), not that it was asked nicely.
    /// </remarks>
    public const string Stop = Prefix + "stop";

    /// <summary>The line a realm writes when it is ready.</summary>
    /// <param name="endpoint">Where it actually bound.</param>
    /// <returns>The line, without a trailing newline.</returns>
    public static string FormatReady(RealmEndpoint endpoint) => $"{Ready} {endpoint}";

    /// <summary>Reads a ready line, if that is what this was.</summary>
    /// <param name="line">A line of the realm's output.</param>
    /// <param name="endpoint">Where it bound, on success.</param>
    /// <returns>Whether the line was a ready signal carrying a reachable endpoint.</returns>
    public static bool TryReadReady(string? line, out RealmEndpoint endpoint) {
        endpoint = RealmEndpoint.None;

        if (line is null || !line.StartsWith(Ready, StringComparison.Ordinal)) {
            return false;
        }

        return RealmEndpoint.TryParse(line[Ready.Length..].Trim(), out endpoint) && endpoint.IsValid;
    }

    /// <summary>Whether a line is one of the commands a realm obeys.</summary>
    /// <param name="line">A line of the realm's input.</param>
    /// <returns>The command, trimmed, or an empty string.</returns>
    /// <remarks>
    ///     Anything that is not a command is ignored rather than refused. A realm's stdin is also
    ///     where a developer's stray keystroke lands, and a server that exited because somebody
    ///     pressed return would be a worse server than one that ignored them.
    /// </remarks>
    public static string ReadCommand(string? line) {
        var trimmed = line?.Trim() ?? "";

        return trimmed is Drain or Stop ? trimmed : "";
    }
}
