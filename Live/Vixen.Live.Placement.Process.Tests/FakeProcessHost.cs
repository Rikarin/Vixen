// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;
using Xunit;

namespace Vixen.Live.Placement.Tests;

/// <summary>A fleet with no processes in it.</summary>
/// <remarks>
///     This is the point of <see cref="IRealmProcessHost" />. A test that asserts what happens when a
///     realm dies at an exact moment cannot ask an operating system for that, and a test that starts
///     eight real processes is a test nobody runs on every push. Everything above the seam — the port
///     pool, the lifecycle, the events, the reconciliation — is the same code either way.
/// </remarks>
sealed class FakeProcessHost : IRealmProcessHost {
    readonly ConcurrentQueue<FakeProcess> started = new();

    int nextId = 1000;

    /// <summary>Every process it was asked to start, in order.</summary>
    public IReadOnlyCollection<FakeProcess> Started => started;

    /// <summary>The most recent one, which is what a single-realm test wants.</summary>
    public FakeProcess Last => started.Last();

    /// <summary>Set to throw instead of starting, which is how a start failure is injected.</summary>
    public Exception? Refuse { get; set; }

    public IRealmProcessHandle Start(RealmProcessRequest request) {
        if (Refuse is { } failure) {
            throw failure;
        }

        var process = new FakeProcess(
            Interlocked.Increment(ref nextId).ToString(CultureInfo.InvariantCulture),
            request
        );

        started.Enqueue(process);

        return process;
    }
}

/// <summary>A realm process that is a few fields and a <see cref="TaskCompletionSource" />.</summary>
sealed class FakeProcess(string id, RealmProcessRequest request) : IRealmProcessHandle {
    readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly ConcurrentQueue<string> input = new();

    public string Id { get; } = id;

    /// <summary>What it was launched with, so a test can read the spec back off the command line.</summary>
    public RealmProcessRequest Request { get; } = request;

    /// <summary>Every line the launcher said to it.</summary>
    public IReadOnlyCollection<string> Input => input;

    public bool HasExited { get; private set; }

    public int ExitCode { get; private set; }

    /// <summary>Whether the launcher ran out of patience.</summary>
    public bool WasKilled { get; private set; }

    public event Action<string>? OutputLine;

    /// <summary>The spec it was told to be, decoded off its own command line.</summary>
    public RealmSpec Spec {
        get {
            Assert.True(RealmSpec.TryRead(Request.Arguments, _ => null, out var spec, out var error), error);

            return spec!;
        }
    }

    /// <summary>Says a line as the realm would.</summary>
    public void Say(string line) => OutputLine?.Invoke(line);

    /// <summary>Reports ready, as a realm does once its map is loaded.</summary>
    public void SayReady() => Say(RealmSignals.FormatReady(Spec.Endpoint));

    /// <summary>Ends it, as the process would.</summary>
    public void Exit(int code = 0) {
        if (HasExited) {
            return;
        }

        ExitCode = code;
        HasExited = true;
        exited.TrySetResult();
    }

    public bool WriteLine(string line) {
        if (HasExited) {
            return false;
        }

        input.Enqueue(line);

        return true;
    }

    public void Kill() {
        WasKilled = true;
        Exit(code: 137);
    }

    public Task WaitForExitAsync(CancellationToken cancellation) => exited.Task.WaitAsync(cancellation);

    public void Dispose() { }
}
