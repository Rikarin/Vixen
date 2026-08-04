// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Vixen.Live.Orchestration;

/// <summary>What a silo needs to know that is not a grain's business.</summary>
/// <param name="ClusterId">
///     Which cluster this silo belongs to. Two silos with different ids are two clusters, however
///     close together they are running.
/// </param>
/// <param name="ServiceId">
///     Which service. Stable across deployments of the same game — it is what grain storage is keyed
///     by, so changing it is changing which state a fresh cluster inherits.
/// </param>
/// <param name="Maps">What each map is, keyed by <c>Keys.ForMap</c>'s spelling of its shard key.</param>
/// <param name="Default">
///     What a map nobody configured gets. Null refuses instead, which is the right default for a
///     production cluster and the wrong one for a development loop.
/// </param>
public sealed record OrchestratorOptions(
    string ClusterId,
    string ServiceId,
    IReadOnlyDictionary<string, MapOptions> Maps,
    MapOptions? Default
);

/// <summary>Stands the orchestrator up. Doc 27 ADR-016.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A library that configures a host, not an executable.</b> Doc 17's model is that the
///         application <em>is</em> the executable and nothing in its boot path is a black box, and an
///         orchestrator is an application like any other: its <c>Program.cs</c> builds a host, calls
///         the one method below, and runs it. What that buys is that an integration test stands the
///         same silo up in-process, and that a deployment which wants a different clustering
///         provider edits its own five lines rather than a configuration schema this package invented.
///     </para>
///     <para>
///         ⚠ <b>Clustering is deliberately not chosen here.</b> Doc 27 ADR-016 lists the providers —
///         <c>Clustering.AdoNet</c>, <c>Clustering.Redis</c>, <c>Clustering.AzureStorage</c>,
///         <c>Hosting.Kubernetes</c> — and picking one would tie the engine to a deployment target
///         the brief explicitly keeps open. <see cref="UseVixenOrchestrator" /> configures the grains
///         and leaves membership to the caller; <see cref="UseDevelopmentCluster" /> is the one-line
///         localhost answer for a laptop and says so in its name.
///     </para>
/// </remarks>
public static class OrchestratorHost {
    /// <summary>Registers the orchestrator's grains and what they need.</summary>
    /// <param name="builder">The host being built.</param>
    /// <param name="options">The cluster's identity and its maps.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    ///     ⚠ <b>It does not call <c>UseOrleans</c>.</b> The caller does, because the caller is the one
    ///     that knows how silos find each other. This adds the services the grains resolve —
    ///     <see cref="MapOptions" /> per map, and the placement backend inside it — and nothing else.
    /// </remarks>
    public static IHostApplicationBuilder UseVixenOrchestrator(
        this IHostApplicationBuilder builder,
        OrchestratorOptions options
    ) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        // The whole configuration, and each map grain finds its own entry in it — because Orleans
        // resolves a grain's dependencies before it knows its key. Two maps of one game legitimately
        // differ in everything MapOptions holds: a city's soft cap is not a battleground's, and the
        // placement weights are a per-map asset.
        builder.Services.AddSingleton(options);

        return builder;
    }

    /// <summary>The localhost clustering a laptop wants, in one line.</summary>
    /// <param name="builder">The host being built.</param>
    /// <param name="options">The cluster's identity and its maps.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Named for what it is.</b> Localhost clustering is a single-machine membership provider
    ///     with no durability; a deployment that reached for it because it was the shortest line in
    ///     the sample would have a cluster that forgets everything when a silo restarts, and would
    ///     find out during its first rollout.
    /// </remarks>
    public static IHostApplicationBuilder UseDevelopmentCluster(
        this IHostApplicationBuilder builder,
        OrchestratorOptions options
    ) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        builder.UseVixenOrchestrator(options);
        builder.UseOrleans(silo => silo.UseLocalhostClustering());

        return builder;
    }
}
