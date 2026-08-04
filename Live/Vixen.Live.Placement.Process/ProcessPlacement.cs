// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Vixen.Live.Placement;

/// <summary>Realms as child processes. ADR-019's third backend, and the one that always answers.</summary>
/// <remarks>
///     <para>
///         No cluster, no daemon, no container runtime: a port from a pool, a
///         <see cref="RealmSpec" /> on the command line, and the process's own stdio as the
///         lifecycle channel (<see cref="RealmSignals" />). That is enough to run a fleet on a
///         laptop, in CI, and in a small deployment that has one machine — which doc 27 § Cost's L0
///         is exactly the shape of.
///     </para>
///     <para>
///         <b>And it is what makes the rest of this testable.</b> Doc 27 § Testing: this backend is
///         to doc 27 what <c>Vixen.Net.Transport.Local</c> is to doc 16. Everything above the
///         interface — placement scoring, spawn and merge hysteresis, drain, rolling upgrades — is an
///         ordinary unit test against a fleet that starts in milliseconds, with
///         <see cref="IRealmProcessHost" /> swapped for one that starts nothing at all.
///     </para>
///     <para>
///         ⚠ <b>An instance outlives this object only if you let it.</b> <see cref="Dispose" /> kills
///         everything it started, because a launcher that exited leaving eight realms holding UDP
///         ports is the thing that makes a developer reboot. A deployment that wants realms to
///         survive their launcher wants the Kubernetes backend, where an owner reference says so
///         explicitly.
///     </para>
/// </remarks>
public sealed class ProcessPlacement : IRealmPlacement, IDisposable {
    /// <summary>What this backend calls itself in a <see cref="PlacementProbe" />.</summary>
    public const string BackendName = "process";

    readonly Dictionary<string, Running> running = new(StringComparer.Ordinal);
    readonly List<Channel<PlacementEvent>> watchers = [];
    readonly Lock gate = new();
    readonly ProcessPlacementOptions options;
    readonly IRealmProcessHost host;
    readonly TimeProvider time;

    bool disposed;

    /// <summary>Stands a launcher up.</summary>
    /// <param name="options">What to launch and how patient to be.</param>
    /// <param name="host">
    ///     How a process is actually started, or <see langword="null" /> for
    ///     <see cref="SystemProcessHost" />.
    /// </param>
    /// <param name="time">The clock, or <see langword="null" /> for the system's.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options" /> is null.</exception>
    public ProcessPlacement(
        ProcessPlacementOptions options,
        IRealmProcessHost? host = null,
        TimeProvider? time = null
    ) {
        ArgumentNullException.ThrowIfNull(options);

        this.options = options;
        this.host = host ?? SystemProcessHost.Instance;
        this.time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Always available, which is what makes it the last entry in ADR-019's probe order: there is
    ///     no configuration that can be missing. It still reports whether the executable it was
    ///     pointed at exists, because "available" and "will work" are different sentences and the
    ///     second one is the one somebody is about to need.
    /// </remarks>
    public ValueTask<PlacementProbe> ProbeAsync(CancellationToken cancellation) {
        cancellation.ThrowIfCancellationRequested();

        var executable = options.Executable;

        var detail = executable.Length == 0
            ? "no executable configured — StartAsync will refuse"
            : $"launching `{executable}` on {options.Ports}";

        return ValueTask.FromResult(new PlacementProbe(true, BackendName, detail));
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="spec" /> is null.</exception>
    /// <exception cref="InvalidOperationException">
    ///     There is no executable configured, the spec is not runnable, the port range is exhausted,
    ///     or the process would not start.
    /// </exception>
    public ValueTask<RealmInstance> StartAsync(RealmSpec spec, CancellationToken cancellation) {
        ArgumentNullException.ThrowIfNull(spec);
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellation.ThrowIfCancellationRequested();

        if (options.Executable.Length == 0) {
            throw new InvalidOperationException(
                "This launcher has no executable. Set ProcessPlacementOptions.Executable to the realm "
                + "binary, or to `dotnet` with the assembly as the first argument."
            );
        }

        if (!spec.IsValid) {
            throw new InvalidOperationException($"`{spec}` is not a runnable spec.");
        }

        var (bound, rented) = Bind(spec.Endpoint);
        var started = spec with { Endpoint = bound };

        IRealmProcessHandle handle;

        try {
            handle = host.Start(
                new(
                    options.Executable,
                    [.. options.Arguments, .. started.ToCommandLine()],
                    options.WorkingDirectory,
                    options.Environment
                )
            );
        } catch {
            // The port goes back before the exception leaves. A launcher that leaked one port per
            // failed start would run out of range on the machine where starts fail, which is the one
            // machine where somebody is watching.
            if (rented) {
                options.Ports.Return(bound.Port);
            }

            throw;
        }

        var instance = new RealmInstance(
            new(handle.Id),
            started.Shard,
            bound,
            BackendName,
            time.GetUtcNow()
        );

        var entry = new Running(instance, handle, rented);

        lock (gate) {
            running[handle.Id] = entry;
        }

        handle.OutputLine += line => OnLine(entry, line);

        Publish(new(PlacementEventKind.Started, instance.Id, instance.Shard, bound, "started"));

        // Not awaited, on purpose: the exit is an event, and a caller that awaited it would be
        // waiting for a realm to end rather than for it to begin.
        _ = WatchExitAsync(entry);

        return ValueTask.FromResult(instance);
    }

    /// <inheritdoc />
    public ValueTask StopAsync(RealmInstanceId instance, StopMode mode, CancellationToken cancellation) {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellation.ThrowIfCancellationRequested();

        Running? entry;

        lock (gate) {
            running.TryGetValue(instance.Value ?? "", out entry);
        }

        if (entry is null) {
            // Stopping something that is already gone is not an error: every backend races with the
            // process it manages, and making callers tell "it was not there" from "it would not
            // stop" would have them write the same retry loop three times.
            return ValueTask.CompletedTask;
        }

        entry.Asked = true;

        var command = mode == StopMode.Drain ? RealmSignals.Drain : RealmSignals.Stop;
        var patience = mode == StopMode.Drain ? options.DrainTimeout : options.StopGrace;

        if (!entry.Handle.WriteLine(command)) {
            // It has already gone, or its stdin is closed. Either way the wait below is what decides
            // whether anything more is needed.
            entry.Handle.Kill();

            return ValueTask.CompletedTask;
        }

        _ = KillAfterAsync(entry, patience);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<RealmInstance>> ListAsync(CancellationToken cancellation) {
        cancellation.ThrowIfCancellationRequested();

        lock (gate) {
            return ValueTask.FromResult<IReadOnlyList<RealmInstance>>(
                [.. running.Values.Select(entry => entry.Instance)]
            );
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Every watcher gets every event from the moment it subscribes, in order, on an unbounded
    ///     channel. Unbounded because the alternative is dropping a <c>Lost</c> — and a fleet view
    ///     that missed one shows a shard that is running and is not, which is worse than the memory.
    /// </remarks>
    public async IAsyncEnumerable<PlacementEvent> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellation
    ) {
        var channel = Channel.CreateUnbounded<PlacementEvent>(
            new() { SingleReader = true, SingleWriter = false }
        );

        lock (gate) {
            watchers.Add(channel);
        }

        try {
            await foreach (var placement in channel.Reader.ReadAllAsync(cancellation).ConfigureAwait(false)) {
                yield return placement;
            }
        } finally {
            lock (gate) {
                watchers.Remove(channel);
            }
        }
    }

    /// <summary>Kills everything this launcher started.</summary>
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        List<Running> outstanding;
        List<Channel<PlacementEvent>> listening;

        lock (gate) {
            outstanding = [.. running.Values];
            listening = [.. watchers];
            running.Clear();
            watchers.Clear();
        }

        foreach (var entry in outstanding) {
            entry.Asked = true;
            entry.Handle.Kill();
            entry.Handle.Dispose();

            if (entry.Rented) {
                options.Ports.Return(entry.Instance.Endpoint.Port);
            }
        }

        foreach (var channel in listening) {
            channel.Writer.TryComplete();
        }
    }

    (RealmEndpoint Endpoint, bool Rented) Bind(RealmEndpoint requested) {
        var host = requested.Host is { Length: > 0 } named ? named : options.Host;

        if (requested.Port > 0) {
            // The caller named a port. Honoured rather than second-guessed: a deployment that has
            // opened one port in a firewall means that port, and a pool that overrode it would be
            // choosing a port nobody can reach.
            return (new(host, requested.Port), false);
        }

        if (!options.Ports.TryRent(out var port)) {
            throw new InvalidOperationException(
                $"{options.Ports} — the range is exhausted. Widen it, or stop some realms."
            );
        }

        return (new(host, port), true);
    }

    void OnLine(Running entry, string line) {
        options.Output?.Invoke(entry.Instance.Id, line);

        if (!entry.Ready && RealmSignals.TryReadReady(line, out var endpoint)) {
            entry.Ready = true;

            // The realm's own word for where it bound wins over what it was told to bind. They agree
            // in every ordinary case; where they do not, the realm is right, because it is the one
            // holding the socket.
            Publish(
                new(PlacementEventKind.Ready, entry.Instance.Id, entry.Instance.Shard, endpoint, "ready")
            );
        }
    }

    async Task WatchExitAsync(Running entry) {
        try {
            await entry.Handle.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        } catch (Exception failure) when (failure is InvalidOperationException or OperationCanceledException) {
            // The handle went away underneath us, which is the same outcome as an exit.
        }

        var code = entry.Handle.ExitCode;

        lock (gate) {
            if (!running.Remove(entry.Instance.Id.Value)) {
                // Disposal got there first and has already published nothing on purpose.
                return;
            }
        }

        if (entry.Rented) {
            options.Ports.Return(entry.Instance.Endpoint.Port);
        }

        // Asked, or exited cleanly on its own — a shard whose last player left is allowed to stop.
        // Anything else is Lost, which doc 27 § Health makes a placement decision rather than a
        // resurrection.
        var expected = entry.Asked || code == 0;

        Publish(
            new(
                expected ? PlacementEventKind.Stopped : PlacementEventKind.Lost,
                entry.Instance.Id,
                entry.Instance.Shard,
                entry.Instance.Endpoint,
                expected ? $"exited with {code}" : $"exited with {code} without being asked"
            )
        );

        entry.Handle.Dispose();
    }

    async Task KillAfterAsync(Running entry, TimeSpan patience) {
        using var timeout = new CancellationTokenSource();

        try {
            await entry.Handle.WaitForExitAsync(timeout.Token).WaitAsync(patience, time).ConfigureAwait(false);
        } catch (TimeoutException) {
            // It would not go. Doc 27 says drain never force-disconnects a player; it says nothing
            // about a process that has stopped answering, and at this point the launcher is the only
            // thing left that can end it.
            entry.Handle.Kill();
        } catch (Exception failure) when (failure is InvalidOperationException or OperationCanceledException) {
            // Already gone.
        } finally {
            await timeout.CancelAsync().ConfigureAwait(false);
        }
    }

    void Publish(PlacementEvent placement) {
        lock (gate) {
            foreach (var channel in watchers) {
                channel.Writer.TryWrite(placement);
            }
        }
    }

    sealed class Running(RealmInstance instance, IRealmProcessHandle handle, bool rented) {
        public RealmInstance Instance { get; } = instance;

        public IRealmProcessHandle Handle { get; } = handle;

        public bool Rented { get; } = rented;

        /// <summary>Whether the launcher asked it to stop, which is what tells Stopped from Lost.</summary>
        public volatile bool Asked;

        /// <summary>Whether it has reported ready, so the signal is published once.</summary>
        public volatile bool Ready;
    }
}
