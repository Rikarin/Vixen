// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Vixen.Core.IO;

namespace Vixen.Platform.Web;

/// <summary>Read-write storage in a browser, on top of IndexedDB.</summary>
/// <remarks>
///     <para>
///         <b>IndexedDB and not the alternatives, for reasons that are about the limits rather than
///         the API.</b> <c>localStorage</c> is five megabytes of strings and is synchronous in a way
///         that blocks the compositor. Cache Storage is the first thing evicted when an origin is
///         over quota, which is precisely wrong for saves. The Origin Private File System is the
///         better answer, has a synchronous access handle, and is not in Safari on iOS — which is
///         where the storage limits bite hardest and where a save that vanished would matter most.
///         IndexedDB is everywhere, is large, and is evicted last.
///     </para>
///     <para>
///         <b>The directory is in memory; the contents are not.</b> IndexedDB is asynchronous and
///         <see cref="IFileProvider" />'s <c>Exists</c>, <c>TryGetEntry</c> and <c>Enumerate</c> are
///         not — and on the browser's one thread nothing may block, because blocking stops the
///         request it is waiting for from ever completing. So every key with its length and write
///         time is read once, at mount, and kept; values are read and written on demand. A cache of
///         downloaded bundles is hundreds of megabytes and the point of a directory is to answer
///         questions about it without being it.
///     </para>
///     <para>
///         <b>Writes are visible immediately and durable shortly afterwards.</b> Closing a write
///         stream updates the in-memory directory synchronously — so <see cref="Exists" /> is true
///         the instant the stream is closed, which is what a caller means by "I saved it" — and
///         starts the IndexedDB put. <see cref="FlushAsync" /> waits for the puts, and is what the
///         platform calls when the tab is being hidden. A page closed between the two loses the last
///         write, which is why <see cref="OpenWriteAsync" />'s stream also implements
///         <see cref="IAsyncDisposable" />: <c>await using</c> waits, and is what save code should
///         use.
///     </para>
/// </remarks>
[SupportedOSPlatform("browser")]
public sealed class IndexedDbFileProvider : IFileProvider, IDisposable {
    readonly int database;
    readonly Dictionary<string, FileEntry> files = new(StringComparer.Ordinal);
    readonly HashSet<string> directories = new(StringComparer.Ordinal) { "/" };
    readonly List<Task> pending = [];
    readonly Lock gate = new();

    bool disposed;

    IndexedDbFileProvider(int database) => this.database = database;

    /// <summary>Opens a store and reads its directory.</summary>
    /// <param name="name">The database name. Per origin, so two applications on one origin want
    /// two.</param>
    /// <param name="cancellationToken">Abandons the open.</param>
    /// <returns>The provider.</returns>
    /// <exception cref="IOException">The browser refused to open the database — private browsing in
    /// Firefox, or an origin whose storage the user has blocked.</exception>
    public static async Task<IndexedDbFileProvider> OpenAsync(
        string name,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        int handle;

        try {
            handle = await WebInterop.OpenDatabase(name).WaitAsync(cancellationToken).ConfigureAwait(false);
        } catch (Exception exception) when (exception is not OperationCanceledException) {
            throw new IOException(
                $"IndexedDB refused to open '{name}'. A page in Firefox's private browsing has no "
                + "IndexedDB at all, and an origin the user has blocked storage for behaves the same "
                + "way.",
                exception
            );
        }

        var provider = new IndexedDbFileProvider(handle);
        await provider.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return provider;
    }

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <summary>How many files are in the store.</summary>
    public int FileCount {
        get {
            lock (gate) {
                return files.Count;
            }
        }
    }

    /// <inheritdoc />
    public bool Exists(VirtualPath path) {
        lock (gate) {
            return files.ContainsKey(path.Value) || directories.Contains(path.Value);
        }
    }

    /// <inheritdoc />
    public bool TryGetEntry(VirtualPath path, out FileEntry entry) {
        lock (gate) {
            if (files.TryGetValue(path.Value, out entry)) {
                return true;
            }

            if (directories.Contains(path.Value)) {
                entry = new(path, 0, default, IsDirectory: true);
                return true;
            }

            entry = default;
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>Ordered by path, as the interface requires. A snapshot: the list is materialised
    /// under the lock rather than being an iterator over the live directory.</remarks>
    public IEnumerable<FileEntry> Enumerate(VirtualPath directory, bool recursive = false) {
        var prefix = directory.IsRoot ? "/" : directory.Value + "/";
        var found = new SortedDictionary<string, FileEntry>(StringComparer.Ordinal);

        lock (gate) {
            foreach (var child in directories) {
                if (child.Length <= prefix.Length || !child.StartsWith(prefix, StringComparison.Ordinal)) {
                    continue;
                }

                if (recursive || !child.AsSpan(prefix.Length).Contains('/')) {
                    found[child] = new(new(child), 0, default, IsDirectory: true);
                }
            }

            foreach (var (path, entry) in files) {
                if (!path.StartsWith(prefix, StringComparison.Ordinal)) {
                    continue;
                }

                if (recursive || !path.AsSpan(prefix.Length).Contains('/')) {
                    found[path] = entry;
                }
            }
        }

        return found.Values;
    }

    /// <inheritdoc />
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    public async ValueTask<Stream> OpenReadAsync(VirtualPath path, CancellationToken cancellationToken = default) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!Exists(path)) {
            throw new FileNotFoundException($"'{path}' is not in the store.", path.Value);
        }

        var handle = await WebInterop
            .ReadDatabase(database, path.Value)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        return new MemoryStream(WebBuffer.Take(handle), writable: false);
    }

    /// <inheritdoc />
    /// <returns>
    ///     A stream that writes to memory and puts its contents into IndexedDB when it is closed.
    ///     Prefer <c>await using</c>: disposing asynchronously waits for the put, disposing
    ///     synchronously only starts it.
    /// </returns>
    public ValueTask<Stream> OpenWriteAsync(VirtualPath path, CancellationToken cancellationToken = default) {
        ObjectDisposedException.ThrowIf(disposed, this);
        return ValueTask.FromResult<Stream>(new IndexedDbWriteStream(this, path, []));
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Genuinely appends, which the interface's default does not: it reads the file back and
    ///     rewrites it, and the point of resuming a 400 MB download is not touching the first 300 MB
    ///     again. IndexedDB has no partial update, so the rewrite happens on the way out — but the
    ///     read to get there is one this has to do anyway, and the caller does not do it twice.
    /// </remarks>
    public async ValueTask<Stream> OpenAppendAsync(VirtualPath path, CancellationToken cancellationToken = default) {
        ObjectDisposedException.ThrowIf(disposed, this);

        var existing = Exists(path)
            ? WebBuffer.Take(
                await WebInterop.ReadDatabase(database, path.Value)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false)
            )
            : [];

        return new IndexedDbWriteStream(this, path, existing);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Removed from the directory at once and from the store shortly afterwards, so
    ///     <see cref="Exists" /> is false the moment this returns — which is what a caller means by
    ///     "deleted". <see cref="FlushAsync" /> waits for the store to catch up.
    /// </remarks>
    public bool Delete(VirtualPath path) {
        ObjectDisposedException.ThrowIf(disposed, this);

        lock (gate) {
            if (directories.Contains(path.Value)) {
                // Directories here are inferred from the paths that pass through them, so an empty
                // one has nothing to delete and a non-empty one is not empty. Both are refusals.
                return false;
            }

            if (!files.Remove(path.Value)) {
                return false;
            }

            pending.Add(WebInterop.DeleteDatabase(database, path.Value));
        }

        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Records the directory and nothing else. An object store has keys, not folders, so a
    ///     directory is the prefix of the paths under it — creating one is remembering it so that
    ///     <see cref="Exists" /> and <see cref="Enumerate" /> agree with a caller that made it
    ///     before writing into it.
    /// </remarks>
    public void CreateDirectory(VirtualPath path) {
        ObjectDisposedException.ThrowIf(disposed, this);

        lock (gate) {
            Remember(path.Value);
        }
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    ///     Always. Blocking on the browser's one thread stops the request it is waiting for from
    ///     completing; see <see cref="FetchFileProvider.OpenRead" />, which refuses for the same
    ///     reason.
    /// </exception>
    public Stream OpenRead(VirtualPath path) =>
        throw new NotSupportedException(
            $"'{path}' is in IndexedDB and this is a browser, where blocking the calling thread "
            + "stops the request it is waiting for from ever completing. Use OpenReadAsync."
        );

    /// <inheritdoc />
    /// <remarks>
    ///     Works, unlike the read: a write stream is memory until it is closed, and the put that
    ///     follows is started rather than waited for. <c>await using</c> is still what save code
    ///     should use, because only that waits for the bytes to reach the store.
    /// </remarks>
    public Stream OpenWrite(VirtualPath path) {
        ObjectDisposedException.ThrowIf(disposed, this);
        return new IndexedDbWriteStream(this, path, []);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Never. There is nothing to map: the bytes live in the browser's storage and reach the
    ///     runtime's heap by being copied.
    /// </remarks>
    public bool TryMap(VirtualPath path, [NotNullWhen(true)] out IMappedFile? mapped) {
        mapped = null;
        return false;
    }

    /// <summary>Waits for every write and delete that has been started.</summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <remarks>
    ///     What the platform calls when the tab is being hidden — which on the web is the last
    ///     moment there is, since a hidden tab may be discarded without another word. Also worth
    ///     calling after a save if the application would rather know than assume.
    /// </remarks>
    public async Task FlushAsync(CancellationToken cancellationToken = default) {
        Task[] waiting;

        lock (gate) {
            waiting = [.. pending];
            pending.Clear();
        }

        if (waiting.Length > 0) {
            await Task.WhenAll(waiting).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Re-reads the directory from the store.</summary>
    /// <param name="cancellationToken">Abandons the read.</param>
    /// <remarks>
    ///     Worth doing after a resume: two tabs of the same origin share one IndexedDB, so the other
    ///     one may have written while this was in the background.
    /// </remarks>
    public async Task RefreshAsync(CancellationToken cancellationToken = default) {
        var count = await WebInterop
            .ListDatabase(database)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        lock (gate) {
            files.Clear();
            directories.Clear();
            directories.Add("/");

            for (var index = 0; index < count; index++) {
                var name = WebInterop.ListingName(index);

                if (!VirtualPath.TryCreate(name, out var path)) {
                    // A key that is not a virtual path is not ours — another library sharing the
                    // origin's storage, or a record from an older schema. Skipped rather than
                    // rejected: refusing to mount because somebody else's key is in the store would
                    // make the mount fail for a reason the application cannot fix.
                    continue;
                }

                files[path.Value] = new(
                    path,
                    (long)WebInterop.ListingLength(index),
                    Moment(WebInterop.ListingTime(index)),
                    IsDirectory: false
                );

                Remember(path.Parent.Value);
            }
        }
    }

    /// <summary>How much of the origin's storage is used, and how much it has.</summary>
    /// <param name="cancellationToken">Abandons the query.</param>
    /// <returns>Bytes used and bytes granted. Both <c>0</c> where the browser will not say.</returns>
    /// <remarks>
    ///     A cache that writes until it is refused is a cache that gets the whole origin evicted,
    ///     taking the saves with it. This is the number a bundle cache's eviction policy is written
    ///     against.
    /// </remarks>
    public static async Task<(long Usage, long Quota)> GetStorageEstimateAsync(
        CancellationToken cancellationToken = default
    ) {
        var handle = await WebInterop.StorageEstimate().WaitAsync(cancellationToken).ConfigureAwait(false);
        var values = WebBuffer.TakeDoubles(handle, 2);
        return ((long)values[0], (long)values[1]);
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        WebInterop.CloseDatabase(database);
    }

    /// <summary>Takes a finished write stream's bytes and starts the put.</summary>
    internal void Commit(VirtualPath path, byte[] contents, out Task written) {
        var modified = DateTimeOffset.UtcNow;
        var staged = WebInterop.StageBuffer(contents);
        var task = WebInterop.WriteDatabase(database, path.Value, staged, modified.ToUnixTimeMilliseconds());

        lock (gate) {
            // The directory first, so that Exists() is true the instant the stream closed. A caller
            // that writes a save and immediately checks for it is not waiting on a round trip to
            // storage to be told what it just did.
            files[path.Value] = new(path, contents.Length, modified, IsDirectory: false);
            Remember(path.Parent.Value);
            pending.Add(task);
        }

        written = task;
    }

    /// <summary>Records a directory and every one above it. Call under the lock.</summary>
    void Remember(string path) {
        while (path.Length > 0 && directories.Add(path) && path != "/") {
            var last = path.LastIndexOf('/');
            path = last <= 0 ? "/" : path[..last];
        }
    }

    static DateTimeOffset Moment(double milliseconds) =>
        milliseconds > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds) : default;
}

/// <summary>A write in progress: memory now, IndexedDB on close.</summary>
/// <remarks>
///     IndexedDB stores whole values and has no partial update, so a write is buffered and put in
///     one transaction. That is not a compromise for small saves and is one for a large download —
///     which is why a bundle cache writes a bundle per key rather than appending to one.
/// </remarks>
[SupportedOSPlatform("browser")]
internal sealed class IndexedDbWriteStream(IndexedDbFileProvider provider, VirtualPath path, byte[] existing)
    : Stream {
    readonly MemoryStream buffer = new(existing.Length + 256);
    bool started;
    bool committed;
    Task? written;

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanSeek => !committed;

    /// <inheritdoc />
    public override bool CanWrite => !committed;

    /// <inheritdoc />
    public override long Length => buffer.Length;

    /// <inheritdoc />
    public override long Position {
        get => buffer.Position;
        set => buffer.Position = value;
    }

    /// <inheritdoc />
    public override void Write(byte[] source, int offset, int count) {
        Start();
        buffer.Write(source, offset, count);
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> source) {
        Start();
        buffer.Write(source);
    }

    /// <inheritdoc />
    public override void WriteByte(byte value) {
        Start();
        buffer.WriteByte(value);
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => buffer.Seek(offset, origin);

    /// <inheritdoc />
    public override void SetLength(long value) => buffer.SetLength(value);

    /// <inheritdoc />
    public override void Flush() { }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always. This is a write stream.</exception>
    public override int Read(byte[] destination, int offset, int count) =>
        throw new NotSupportedException("This stream was opened for writing.");

    /// <inheritdoc />
    /// <remarks>Starts the put and does not wait for it. <c>await using</c> waits.</remarks>
    protected override void Dispose(bool disposing) {
        Commit();
        base.Dispose(disposing);
    }

    /// <inheritdoc />
    /// <remarks>Waits for the bytes to reach the store, which is what save code wants.</remarks>
    public override async ValueTask DisposeAsync() {
        Commit();

        if (written is not null) {
            await written.ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Puts what is already in the file underneath, once, before the first write.</summary>
    /// <remarks>
    ///     <para>
    ///         Lazily, so an append that never writes anything does not pay to rewrite the file it
    ///         appended nothing to.
    ///     </para>
    ///     <para>
    ///         Guarded by a flag rather than by <c>buffer.Length == 0</c>, which is the same thing
    ///         until a caller truncates: <c>SetLength(0)</c> followed by a write would otherwise
    ///         prepend the existing contents a second time, and produce a file with its own tail
    ///         doubled.
    ///     </para>
    /// </remarks>
    void Start() {
        if (started) {
            return;
        }

        started = true;

        if (existing.Length > 0) {
            buffer.Write(existing);
        }
    }

    void Commit() {
        if (committed) {
            return;
        }

        committed = true;
        Start();
        provider.Commit(path, buffer.ToArray(), out written);
    }
}
