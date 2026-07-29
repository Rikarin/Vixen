// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vixen.Core.Collections;

namespace Vixen.Core.Diagnostics;

/// <summary>
///     Where a <see cref="RemoteSink" /> hands its bytes: the inspector connection, a socket, a test
///     double.
/// </summary>
/// <remarks>
///     An interface rather than a socket, because <c>Vixen.Core.Diagnostics</c> is below the
///     networking stack and must stay there — a foundation assembly that opened its own TCP
///     connection would drag a transport into every build that logs. Doc 13's remote inspector owns
///     the protocol, the discovery and the pairing; this owns "what to send and when to give up".
/// </remarks>
public interface IRemoteLogTransport {
    /// <summary>Whether the far end is attached. Nothing is sent while this is false.</summary>
    bool IsConnected { get; }

    /// <summary>Sends one batch of newline-separated JSON records.</summary>
    /// <param name="payload">The UTF-8 bytes, valid only for the duration of the call.</param>
    /// <remarks>
    ///     Called from the sink's own thread, never from a thread that logged. Framing is the
    ///     transport's business: a datagram transport sends the batch as one message, a stream one
    ///     appends it.
    /// </remarks>
    void Send(ReadOnlySpan<byte> payload);
}

/// <summary>
///     Streams the log to the editor's console over the inspector connection: JSON records, one per
///     line, batched, on a thread of the sink's own.
/// </summary>
/// <remarks>
///     <para>
///         The sink that makes a build running on a phone, a console or another machine
///         debuggable — doc 13's remote inspector reads the log stream from here, and "attach the
///         editor to a running player" is how mobile debugging actually happens.
///     </para>
///     <para>
///         <b>The logging thread never waits on the network.</b> A record is copied into a bounded
///         ring and the caller returns; a background thread drains it. When the far end is slow,
///         detached, or was never there, the ring overwrites its oldest records and
///         <see cref="DroppedCount" /> says how many — a remote console showing the most recent
///         thousand lines is useful, and a frame loop blocked on a socket write is not.
///     </para>
///     <para>
///         Records are JSON rather than the packed UTF-8 form doc 13 wants for the ring, because
///         the far end here is a tool over a wire rather than a byte ring in the same process, and
///         a self-describing line survives the two ends being different versions.
///     </para>
/// </remarks>
public sealed class RemoteSink : LogRecordSink {
    /// <summary>How many records are held for sending when no capacity is given.</summary>
    public const int DefaultCapacity = 4096;

    static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly IRemoteLogTransport transport;
    readonly RingBuffer<LogRecord> pending;
    readonly Lock gate = new();
    readonly SemaphoreSlim signal = new(0);
    readonly Thread worker;
    readonly ArrayBufferWriter<byte> payload = new(16 * 1024);
    readonly List<LogRecord> batch = [];
    volatile bool stopping;
    volatile bool draining;

    /// <summary>How many records are waiting to be sent.</summary>
    public int PendingCount {
        get {
            lock (gate) {
                return pending.Count;
            }
        }
    }

    /// <summary>
    ///     How many records were dropped because the far end could not keep up or was not there.
    ///     A remote console that does not admit its gaps is worse than one that shows fewer lines.
    /// </summary>
    public long DroppedCount {
        get {
            lock (gate) {
                return pending.OverwrittenCount;
            }
        }
    }

    /// <summary>Creates a sink streaming to a transport.</summary>
    /// <param name="transport">Where the bytes go.</param>
    /// <param name="capacity">How many records may wait to be sent.</param>
    /// <param name="minimumLevel">The level below which nothing is streamed.</param>
    /// <param name="filter">
    ///     The filter to use, or <see langword="null" /> for one of this sink's own.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="transport" /> is null.</exception>
    public RemoteSink(
        IRemoteLogTransport transport,
        int capacity = DefaultCapacity,
        LogLevel minimumLevel = LogLevel.Information,
        LogFilter? filter = null
    ) : base(filter) {
        ArgumentNullException.ThrowIfNull(transport);

        this.transport = transport;
        pending = new(capacity);

        if (filter is null) {
            MinimumLevel = minimumLevel;
        }

        worker = new(Pump) {
            IsBackground = true,
            Name = "Vixen Remote Log"
        };

        worker.Start();
    }

    /// <summary>Waits for everything logged so far to reach the transport.</summary>
    /// <param name="timeout">How long to wait.</param>
    /// <returns><see langword="true" /> if the queue drained within the timeout.</returns>
    /// <remarks>
    ///     For shutdown and for tests. Ordinary logging never calls it — the point of the sink is
    ///     that nothing waits for the wire.
    /// </remarks>
    public bool Flush(TimeSpan timeout) {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        // Both conditions, not just the queue: the worker empties the ring before it serialises and
        // sends, so a queue that has gone empty says the records have left the ring rather than that
        // they have reached the transport.
        while (Environment.TickCount64 < deadline) {
            if (PendingCount == 0 && !draining) {
                return true;
            }

            if (!stopping) {
                signal.Release();
            }

            Thread.Sleep(1);
        }

        return PendingCount == 0 && !draining;
    }

    /// <inheritdoc />
    protected override void Write(LogRecord record) {
        lock (gate) {
            pending.Enqueue(record);
        }

        // Not after the sink has been told to stop: the semaphore may be gone by then, and a line
        // logged on the way down — which is exactly when the interesting ones are logged — must not
        // turn into an ObjectDisposedException in the caller's shutdown path.
        if (!stopping) {
            signal.Release();
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing) {
        base.Dispose(disposing);

        if (!disposing) {
            return;
        }

        stopping = true;
        signal.Release();

        // Bounded: a transport wedged inside Send must not stop the process from exiting, and the
        // thread is a background one, so the worst case is a few unsent lines. The semaphore is
        // released only if the worker actually finished — disposing one a live thread is waiting on
        // throws there, on a thread with nobody to catch it, which turns a slow inspector into a
        // crash on shutdown.
        if (worker.Join(TimeSpan.FromSeconds(2))) {
            signal.Dispose();
        }
    }

    static void WriteRecord(Utf8JsonWriter writer, LogRecord record) {
        writer.WriteStartObject();
        writer.WriteString("t", record.Timestamp);
        writer.WriteString("level", record.Level.ToString());
        writer.WriteNumber("id", record.EventId.Id);
        writer.WriteString("category", record.Category);
        writer.WriteString("message", record.Message);
        writer.WriteNumber("thread", record.ThreadId);

        if (record.SuppressedCount > 0) {
            writer.WriteNumber("repeated", record.SuppressedCount);
        }

        if (record.Exception is not null) {
            writer.WriteString("exception", record.Exception.ToString());
        }

        writer.WriteEndObject();
    }

    void Pump() {
        while (!stopping) {
            signal.Wait(TimeSpan.FromMilliseconds(250));
            Drain();
        }

        Drain();
    }

    void Drain() {
        if (!transport.IsConnected) {
            return;
        }

        batch.Clear();

        lock (gate) {
            if (pending.Count == 0) {
                return;
            }

            // Raised inside the lock, before the ring is emptied, so that Flush cannot see a queue
            // that has gone empty and conclude the records reached the wire.
            draining = true;

            while (pending.TryDequeue(out var record)) {
                batch.Add(record);
            }
        }

        payload.Clear();

        // One writer per batch rather than one per record: Utf8JsonWriter is cheap to reset and the
        // newline between records is what makes the far end able to split them without a framing
        // header of our own.
        using (var writer = new Utf8JsonWriter(payload, WriterOptions)) {
            foreach (var record in batch) {
                WriteRecord(writer, record);
                writer.Flush();
                writer.Reset(payload);
                payload.Write("\n"u8);
            }
        }

        try {
            transport.Send(payload.WrittenSpan);
        } catch (Exception exception) when (exception is IOException or ObjectDisposedException
            or InvalidOperationException) {
            // A transport that fails mid-send is a detached inspector, which is ordinary. Taking the
            // process down over a log line that could not be delivered is not.
        } finally {
            draining = false;
        }
    }
}
