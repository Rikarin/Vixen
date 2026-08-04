// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using k8s;
using k8s.Models;

namespace Vixen.Live.Placement;

/// <summary>The six calls this backend makes of a cluster.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A seam over <c>IKubernetes</c> rather than the interface itself, and for the reason
///         every other backend has one.</b> <c>IKubernetes</c> is the whole generated API — dozens of
///         operation groups, hundreds of methods — and a fake of it is not something anybody writes.
///         Six methods is, and behind them the placement logic is asserted on every push instead of
///         only on the nightly <c>kind</c> leg.
///     </para>
///     <para>
///         It speaks in the library's own model types. Re-modelling <c>V1Pod</c> would be inventing a
///         second Kubernetes object model to avoid depending on the first, which is the mistake the
///         package exists to prevent.
///     </para>
/// </remarks>
public interface IClusterApi {
    /// <summary>Asks the API server what it is.</summary>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>Its version, or empty if it did not answer.</returns>
    Task<string> VersionAsync(CancellationToken cancellation);

    /// <summary>Creates a pod.</summary>
    /// <param name="pod">The pod.</param>
    /// <param name="space">Its namespace.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>What the API server made of it.</returns>
    Task<V1Pod> CreatePodAsync(V1Pod pod, string space, CancellationToken cancellation);

    /// <summary>Reads a pod back.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="space">Its namespace.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The pod, or null if it is gone.</returns>
    Task<V1Pod?> ReadPodAsync(string name, string space, CancellationToken cancellation);

    /// <summary>Deletes a pod.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="space">Its namespace.</param>
    /// <param name="grace">How long its process gets after the signal.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>When asked. A pod that is already gone is not an error.</returns>
    Task DeletePodAsync(string name, string space, TimeSpan grace, CancellationToken cancellation);

    /// <summary>Lists the pods carrying a label selector.</summary>
    /// <param name="space">The namespace.</param>
    /// <param name="selector">The selector, as <c>key=value</c>.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>What the API server has.</returns>
    Task<IReadOnlyList<V1Pod>> ListPodsAsync(string space, string selector, CancellationToken cancellation);

    /// <summary>Reads a node, for the address a player can actually reach.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The node, or null.</returns>
    Task<V1Node?> ReadNodeAsync(string name, CancellationToken cancellation);

    /// <summary>Follows a pod's output, a line at a time.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="space">Its namespace.</param>
    /// <param name="cancellation">Ends the stream.</param>
    /// <returns>Every line it writes, from now on.</returns>
    /// <remarks>
    ///     Plain text, unlike Docker's: the API server demultiplexes for you. What arrives is what
    ///     the realm wrote, and <c>RealmSignals</c> reads it the same way on both backends.
    /// </remarks>
    IAsyncEnumerable<string> FollowLogAsync(string name, string space, CancellationToken cancellation);
}

/// <summary>The real one, over <c>KubernetesClient</c>.</summary>
/// <param name="client">The generated client. Owned by whoever built it.</param>
public sealed class ClusterApi(IKubernetes client) : IClusterApi {
    /// <summary>Builds a client from the environment: in-cluster first, then a kubeconfig.</summary>
    /// <returns>A client, or null if neither is there.</returns>
    /// <remarks>
    ///     ⚠ <b>In-cluster first, deliberately.</b> A realm orchestrator running inside a cluster and
    ///     a developer's kubeconfig on the same machine is a normal state of affairs, and picking the
    ///     kubeconfig would place production realms into whatever context that file happened to point
    ///     at. The service-account token is unambiguous; the file is not.
    /// </remarks>
    public static IKubernetes? Connect() {
        try {
            return new Kubernetes(
                KubernetesClientConfiguration.IsInCluster()
                    ? KubernetesClientConfiguration.InClusterConfig()
                    : KubernetesClientConfiguration.BuildConfigFromConfigFile()
            );
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException) {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string> VersionAsync(CancellationToken cancellation) {
        var version = await client.Version.GetCodeAsync(cancellation).ConfigureAwait(false);

        return version?.GitVersion ?? "";
    }

    /// <inheritdoc />
    public Task<V1Pod> CreatePodAsync(V1Pod pod, string space, CancellationToken cancellation) =>
        client.CoreV1.CreateNamespacedPodAsync(pod, space, cancellationToken: cancellation);

    /// <inheritdoc />
    public async Task<V1Pod?> ReadPodAsync(string name, string space, CancellationToken cancellation) {
        try {
            return await client.CoreV1.ReadNamespacedPodAsync(name, space, cancellationToken: cancellation)
                .ConfigureAwait(false);
        } catch (k8s.Autorest.HttpOperationException failure)
            when (failure.Response.StatusCode == System.Net.HttpStatusCode.NotFound) {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task DeletePodAsync(string name, string space, TimeSpan grace, CancellationToken cancellation) {
        try {
            await client.CoreV1.DeleteNamespacedPodAsync(
                    name,
                    space,
                    gracePeriodSeconds: Math.Max(0, (int)grace.TotalSeconds),
                    cancellationToken: cancellation
                )
                .ConfigureAwait(false);
        } catch (k8s.Autorest.HttpOperationException failure)
            when (failure.Response.StatusCode == System.Net.HttpStatusCode.NotFound) {
            // Every backend races the thing it manages, and a pod that is already gone is the
            // ordinary end of that race rather than a failure to report.
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<V1Pod>> ListPodsAsync(
        string space,
        string selector,
        CancellationToken cancellation
    ) {
        var pods = await client.CoreV1
            .ListNamespacedPodAsync(space, labelSelector: selector, cancellationToken: cancellation)
            .ConfigureAwait(false);

        return [.. pods?.Items ?? []];
    }

    /// <inheritdoc />
    public async Task<V1Node?> ReadNodeAsync(string name, CancellationToken cancellation) {
        try {
            return await client.CoreV1.ReadNodeAsync(name, cancellationToken: cancellation).ConfigureAwait(false);
        } catch (k8s.Autorest.HttpOperationException) {
            // A node this orchestrator may not read is a cluster whose RBAC has not been told about
            // it, and the placement backend's answer is the pod's own host IP instead. Said in the
            // endpoint rather than thrown, because a fleet that refused to place anything because it
            // could not read a Node object would be a very confusing outage.
            return null;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> FollowLogAsync(
        string name,
        string space,
        [EnumeratorCancellation] CancellationToken cancellation
    ) {
        Stream stream;

        try {
            stream = await client.CoreV1
                .ReadNamespacedPodLogAsync(name, space, follow: true, cancellationToken: cancellation)
                .ConfigureAwait(false);
        } catch (k8s.Autorest.HttpOperationException) {
            yield break;
        }

        await using (stream.ConfigureAwait(false)) {
            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(cancellation).ConfigureAwait(false) is { } line) {
                yield return line;
            }
        }
    }
}
