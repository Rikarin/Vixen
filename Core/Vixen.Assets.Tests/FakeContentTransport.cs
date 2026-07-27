// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Assets.Tests;

/// <summary>A content server that can be told to misbehave on purpose.</summary>
/// <remarks>
///     Every failure the cache is written to survive — a connection that dies half way, a server that
///     ignores a byte range, bytes that do not match what the catalog says — is a switch on here.
///     Producing those against a real HTTP server would mean either a proxy that corrupts traffic or a
///     test that hopes the network misbehaves at the right moment.
/// </remarks>
sealed class FakeContentTransport : IContentTransport {
    readonly Dictionary<string, byte[]> resources = new(StringComparer.Ordinal);
    readonly Lock gate = new();
    TaskCompletionSource? held;

    /// <summary>The offset each request asked to start at, in order.</summary>
    public List<long> RequestedOffsets { get; } = [];

    long bytesServed;

    /// <summary>How many bytes have actually been handed out across every request.</summary>
    public long BytesServed => Interlocked.Read(ref bytesServed);

    /// <summary>How many requests have been made.</summary>
    public int Requests {
        get {
            lock (gate) {
                return RequestedOffsets.Count;
            }
        }
    }

    /// <summary>Answers every range request with the whole resource, as a plain HTTP server does.</summary>
    public bool IgnoresRanges { get; set; }

    /// <summary>Cuts each response off after this many bytes, as a dropped connection does.</summary>
    public int CutOffAfter { get; set; } = int.MaxValue;

    /// <summary>Answers from this offset whatever was asked for, as a broken range implementation does.</summary>
    public long? AnswerFrom { get; set; }

    /// <summary>Publishes a resource.</summary>
    public void Serve(string url, byte[] contents) => resources[url] = contents;

    /// <summary>
    ///     Makes every request block until <see cref="Release" />, so that two callers really are in
    ///     flight at the same time. Without this the fake answers synchronously and a "concurrent"
    ///     test is two sequential downloads that never overlap — which passes whether the code
    ///     deduplicates or not.
    /// </summary>
    public void Hold() {
        lock (gate) {
            held ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Lets everything that is waiting through.</summary>
    public void Release() {
        TaskCompletionSource? waiting;

        lock (gate) {
            waiting = held;
            held = null;
        }

        waiting?.SetResult();
    }

    /// <inheritdoc />
    public async ValueTask<ContentDownload> GetAsync(
        string url,
        long offset = 0,
        CancellationToken cancellationToken = default
    ) {
        cancellationToken.ThrowIfCancellationRequested();

        TaskCompletionSource? waiting;

        lock (gate) {
            RequestedOffsets.Add(offset);
            waiting = held;
        }

        if (waiting is not null) {
            await waiting.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!resources.TryGetValue(url, out var contents)) {
            throw new ContentTransportException(url, "the server answered 404 NotFound");
        }

        var start = IgnoresRanges ? 0 : AnswerFrom ?? offset;

        if (start > contents.Length) {
            throw new ContentTransportException(url, "the server answered 416 RequestedRangeNotSatisfiable");
        }

        return new(new CountingStream(this, contents.AsMemory((int)start), CutOffAfter), start, contents.Length);
    }

    /// <summary>Hands out bytes, counting them, and stops dead once it has given out enough.</summary>
    sealed class CountingStream(FakeContentTransport transport, ReadOnlyMemory<byte> contents, int cutOffAfter)
        : Stream {
        int position;
        int served;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => contents.Length;

        public override long Position {
            get => position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) {
            if (served >= cutOffAfter) {
                throw new IOException("the connection was closed by the remote host");
            }

            // A read is capped by whatever comes first: what is left, what fits, and how much is
            // allowed through before the connection dies.
            var take = Math.Min(Math.Min(contents.Length - position, buffer.Length), cutOffAfter - served);

            if (take <= 0) {
                return 0;
            }

            contents.Span.Slice(position, take).CopyTo(buffer);
            position += take;
            served += take;
            Interlocked.Add(ref transport.bytesServed, take);

            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
