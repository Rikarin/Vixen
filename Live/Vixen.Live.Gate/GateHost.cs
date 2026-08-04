// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Vixen.Live.Gate;

/// <summary>Registers a gate.</summary>
public static class GateHost {
    /// <summary>Adds the service plane to a container.</summary>
    /// <param name="services">Where.</param>
    /// <param name="configure">What this gate is.</param>
    /// <returns><paramref name="services" />, for chaining.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What this does <em>not</em> register is the interesting half.</b>
    ///         <c>IPersistence</c>, <c>IFleetDirectory</c>, the two signers and every
    ///         <c>IAccountAuthority</c> are the caller's, because each of them is a decision only the
    ///         deployment can make: which database, which cluster, where the secrets live, and who is
    ///         allowed to say who somebody is. A gate assembled with none of them fails at
    ///         construction rather than at the first sign-in.
    ///     </para>
    ///     <para>
    ///         In particular <b>nothing registers <see cref="DevelopmentAuthority" /></b>. A gate with
    ///         no authority refuses every sign-in, which is loud; one that quietly trusted whatever it
    ///         was told would not be.
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddVixenGate(this IServiceCollection services, Action<GateOptions> configure) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new GateOptions();

        configure(options);

        services.AddSingleton(options);
        services.AddSingleton<ServicePlane>();
        services.AddSingleton<GateService>();

        return services;
    }
}

/// <summary>The gate's log, as generated call sites.</summary>
/// <remarks>
///     <c>[LoggerMessage]</c> rather than the extension methods, for the reason doc 13 gives: a
///     gate under load logs on every request, and the interpolated overloads box every argument.
/// </remarks>
public static partial class GateLog {
    /// <summary>Somebody signed in.</summary>
    /// <param name="log">Where.</param>
    /// <param name="account">Who.</param>
    /// <param name="scheme">Which authority said so.</param>
    [LoggerMessage(
        EventId = 27101,
        Level = LogLevel.Information,
        Message = "Signed in {Account} through `{Scheme}`."
    )]
    public static partial void GateSignedIn(this ILogger log, Guid account, string scheme);

    /// <summary>Somebody was placed.</summary>
    /// <param name="log">Where.</param>
    /// <param name="player">Who.</param>
    /// <param name="shard">Where.</param>
    /// <param name="reason">Why that shard — doc 27 § Diagnostics' `placement explain`.</param>
    [LoggerMessage(
        EventId = 27102,
        Level = LogLevel.Information,
        Message = "Placed {Player} on {Shard}: {Reason}"
    )]
    public static partial void GatePlaced(this ILogger log, PlayerKey player, ShardId shard, string reason);

    /// <summary>Somebody was not.</summary>
    /// <param name="log">Where.</param>
    /// <param name="player">Who.</param>
    /// <param name="status">What they were told.</param>
    /// <param name="reason">Why.</param>
    [LoggerMessage(
        EventId = 27103,
        Level = LogLevel.Information,
        Message = "Did not place {Player}: {Status} — {Reason}"
    )]
    public static partial void GateNotPlaced(this ILogger log, PlayerKey player, PlayStatus status, string reason);

    /// <summary>A service-plane socket opened.</summary>
    /// <param name="log">Where.</param>
    /// <param name="account">Whose.</param>
    /// <param name="open">How many are now open.</param>
    [LoggerMessage(
        EventId = 27104,
        Level = LogLevel.Debug,
        Message = "Service-plane socket opened for {Account}; {Open} open."
    )]
    public static partial void GateStreamOpened(this ILogger log, Guid account, int open);

    /// <summary>One closed.</summary>
    /// <param name="log">Where.</param>
    /// <param name="account">Whose.</param>
    /// <param name="open">How many are left.</param>
    [LoggerMessage(
        EventId = 27105,
        Level = LogLevel.Debug,
        Message = "Service-plane socket closed for {Account}; {Open} open."
    )]
    public static partial void GateStreamClosed(this ILogger log, Guid account, int open);
}
