// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Streaming;

/// <summary>The one thread that keeps every streaming voice's buffer topped up.</summary>
/// <remarks>
///     <para>
///         <b>One thread for all of them, not one per track.</b> A game plays music, ambience and
///         perhaps a dialogue line at once — three streams, and three threads that spend almost all
///         of their time asleep. Decoding half a second of Opus takes single-digit milliseconds; one
///         thread servicing every stream in round-robin has room for dozens before it is the
///         bottleneck, and it makes "how far behind is streaming" a single answerable question.
///     </para>
///     <para>
///         <b>It is also not required.</b> <see cref="Pump" /> is public and does the whole job
///         synchronously, so a test drives it frame by frame and a single-threaded platform — a
///         browser without threads, which is where <c>docs/plan/10</c> puts the web target — calls
///         it from its own loop instead of starting anything.
///     </para>
/// </remarks>
public sealed class AudioStreamPump : IDisposable {
    readonly Lock gate = new();
    readonly List<StreamingSampleProvider> registered = [];

    StreamingSampleProvider[] snapshot = [];
    Thread? thread;
    volatile bool running;

    /// <summary>How long the thread sleeps between passes.</summary>
    /// <remarks>
    ///     Ten milliseconds against a half-second buffer: fifty passes' worth of slack before a
    ///     stream can starve, which is the margin that lets this be a background thread at ordinary
    ///     priority rather than a real-time one.
    /// </remarks>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>How many streams it is servicing.</summary>
    public int StreamCount {
        get {
            lock (gate) {
                return registered.Count;
            }
        }
    }

    /// <summary>Whether its thread is running.</summary>
    public bool IsRunning => running;

    /// <summary>Takes on a stream, and fills it once before returning.</summary>
    /// <param name="provider">The stream.</param>
    /// <remarks>
    ///     The first fill is synchronous and deliberate: a track that started playing before its
    ///     buffer had anything in it would begin with a gap, and the caller is on the game thread
    ///     where a few milliseconds of decode is affordable.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="provider" /> is null.</exception>
    public void Register(StreamingSampleProvider provider) {
        ArgumentNullException.ThrowIfNull(provider);
        provider.Fill();
        provider.SetPumping(true);

        lock (gate) {
            registered.Add(provider);
            snapshot = [.. registered];
        }
    }

    /// <summary>Stops servicing a stream.</summary>
    /// <param name="provider">The stream.</param>
    public void Unregister(StreamingSampleProvider provider) {
        lock (gate) {
            if (registered.Remove(provider)) {
                provider.SetPumping(false);
                snapshot = [.. registered];
            }
        }
    }

    /// <summary>Fills every registered stream as far as it will go.</summary>
    /// <returns>How many frames were decoded across all of them.</returns>
    public int Pump() {
        var providers = snapshot;
        var total = 0;

        foreach (var provider in providers) {
            total += provider.Fill();
        }

        return total;
    }

    /// <summary>Starts the thread.</summary>
    public void Start() {
        lock (gate) {
            if (running) {
                return;
            }

            running = true;
            thread = new Thread(Loop) { IsBackground = true, Name = "Vixen Audio Streaming" };
            thread.Start();
        }
    }

    /// <summary>Stops the thread and waits for it.</summary>
    public void Stop() {
        Thread? joining;

        lock (gate) {
            running = false;
            joining = thread;
            thread = null;
        }

        joining?.Join();
    }

    /// <inheritdoc />
    public void Dispose() {
        Stop();

        lock (gate) {
            foreach (var provider in registered) {
                provider.SetPumping(false);
            }

            registered.Clear();
            snapshot = [];
        }
    }

    void Loop() {
        while (running) {
            Pump();
            Thread.Sleep(Interval);
        }
    }
}
