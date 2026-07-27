// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Vixen.Assets;
using Vixen.ContentServer;
using Vixen.Core.IO;

namespace Vixen.Samples.AddressablesRemote;

/// <summary>
///     A content update, end to end and in one process: build, serve, download, change one asset,
///     download again — and count the bytes.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is Phase 3's exit criterion made runnable.</b> The claim
///         <c>docs/plan/14</c> makes is "a remote content update fetches only the changed bundles,
///         asserted by byte count". There is a test that asserts it; this is the version a person can
///         watch, which is a different and also necessary thing — a passing test says the property
///         held once, and a sample says what the property <em>is</em>.
///     </para>
///     <para>
///         Everything here is the shipping code. The server is the one <c>vixen content serve</c>
///         runs; the client is <see cref="ContentUpdate" />, <see cref="BundleCache" /> and
///         <see cref="RemoteBundleSource" /> as a game would use them. The only thing invented for
///         the sample is the byte counter, and it is invented because the claim is about bytes.
///     </para>
/// </remarks>
static class Program {
    static async Task<int> Main() {
        var root = Path.Combine(Path.GetTempPath(), $"vixen-addressables-{Environment.ProcessId}");
        var published = Path.Combine(root, "cdn");

        try {
            return await Run(published);
        } finally {
            // A sample that leaves a hundred megabytes in the temp directory is a sample somebody
            // runs once.
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    static async Task<int> Run(string published) {
        Console.WriteLine("Vixen — addressables over HTTP");
        Console.WriteLine();

        // ── 1. Decide where it will live, because the build records that ────────────────────────
        var port = FreePort();
        var baseUrl = $"http://localhost:{port}/";

        // ── 2. Build and publish version 1 ──────────────────────────────────────────────────────
        var first = Content.Publish(published, baseUrl, "A torch, burning");
        Console.WriteLine($"  Published v1  {Describe(published)}");

        // ── 3. Serve it, exactly as `vixen content serve` would ─────────────────────────────────
        var serving = new VirtualFileSystem();
        serving.Mount(new("/content"), new PhysicalFileProvider(published, isReadOnly: true));

        using var host = new ContentServerHost(new(serving, new("/content")), port);
        using var stopping = new CancellationTokenSource();

        var server = host.RunAsync(stopping.Token);
        Console.WriteLine($"  Serving       {host.Prefix}");
        Console.WriteLine();

        // ── 3. The device: an empty cache and a URL ─────────────────────────────────────────────
        var device = new VirtualFileSystem();
        var storage = Path.Combine(Path.GetTempPath(), $"vixen-addressables-{Environment.ProcessId}", "device");
        Directory.CreateDirectory(storage);
        device.Mount(new("/cache"), new PhysicalFileProvider(storage));

        using var transport = new CountingTransport(new HttpContentTransport());
        var catalogUrl = $"{host.Prefix}catalog.bin";

        // What ships in the application: an empty catalog of the current format. A game that ships
        // some content locally would pass its own here and the remote one would be laid over it —
        // that merge is what makes a hybrid build possible, and an all-remote build is the degenerate
        // case of it.
        //
        // The format version has to be the real one. A version-0 placeholder is refused with
        // "merging across versions would need a migration nobody has written", which is the right
        // answer to a genuinely different catalog format and a confusing one to a stand-in — it is
        // what this sample got first.
        var shipped = new ContentCatalog(first.Version, default, first.Target, [], []);

        var cold = await Fetch(device, transport, catalogUrl, shipped, "First run — nothing cached");

        // ── 4. Change one asset and publish again ───────────────────────────────────────────────
        Console.WriteLine();
        var second = Content.Publish(published, baseUrl, "A torch, burning brighter");
        Console.WriteLine($"  Published v2  only props/torch changed; characters/hero is byte-identical");
        Console.WriteLine($"                catalog {first.BuildHash.ToString()[..12]}… → {second.BuildHash.ToString()[..12]}…");
        Console.WriteLine();

        // ── 5. The same device, the same cache ──────────────────────────────────────────────────
        var warm = await Fetch(device, transport, catalogUrl, shipped, "Second run — same cache, one asset changed");

        await stopping.CancelAsync();
        await Ignore(server);

        // ── 6. The claim, in numbers ────────────────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("  ────────────────────────────────────────────────────────────────");
        Console.WriteLine($"  Cold start        {Bytes(cold),10}");
        Console.WriteLine($"  After the update  {Bytes(warm),10}   ({(double)warm / cold:P0} of a full download)");
        Console.WriteLine("  ────────────────────────────────────────────────────────────────");

        // The sample asserts its own point rather than inviting the reader to eyeball it. A demo
        // that quietly stops demonstrating is worse than no demo.
        if (warm >= cold) {
            Console.Error.WriteLine("  The update was not cheaper than the cold start. That is the bug.");
            return 1;
        }

        return 0;
    }

    /// <summary>One device session: update the catalog, then load both assets.</summary>
    static async Task<long> Fetch(
        VirtualFileSystem device,
        CountingTransport transport,
        string catalogUrl,
        ContentCatalog shipped,
        string title
    ) {
        transport.Reset();

        Console.WriteLine($"  {title}");

        // Step 1 of doc 08's boot sequence: fetch the 32-byte hash, and the catalog only if it names
        // something new. On an unchanged build this is the entire cost of starting up.
        var update = new ContentUpdate(device, new("/cache"), transport, catalogUrl);
        var result = await update.ApplyAsync(shipped);

        Console.WriteLine($"    catalog       {result.Outcome}");

        // Printed because it is the interesting half. Nothing the server does throws here — every
        // failure comes back as an outcome and a sentence — so a sample that showed only the outcome
        // would be hiding the part that tells somebody what to fix.
        if (result.Reason is { Length: > 0 } reason) {
            Console.WriteLine($"                  {reason}");
        }

        if (result.Catalog is not { } catalog) {
            throw new InvalidOperationException($"No catalog: {result.Outcome}. {result.Reason}");
        }

        var cache = new BundleCache(device, new("/cache/bundles"), transport);
        using var bundles = new RemoteBundleSource(device, cache);
        var assets = new AssetManager(catalog, bundles);

        foreach (var address in (string[])["characters/hero", "props/torch"]) {
            var before = transport.Bytes;
            var handle = assets.Load<Greeting>(address);
            var value = handle.Result;
            var cost = transport.Bytes - before;

            Console.WriteLine(
                $"    {address,-17} {(cost == 0 ? "cache hit" : Bytes(cost)),10}   \"{value.Text}\""
            );

            handle.Release();
        }

        foreach (var request in transport.Requests) {
            Console.WriteLine($"      ← {request.Url,-46} {Bytes(request.Bytes),10}");
        }

        return transport.Bytes;
    }

    static string Describe(string directory) {
        var files = Directory.GetFiles(directory);
        var total = files.Sum(file => new FileInfo(file).Length);

        return $"{files.Length} files, {Bytes(total)}";
    }

    static string Bytes(long value) =>
        value < 1024 ? $"{value} B"
        : value < 1024 * 1024 ? $"{value / 1024.0:0.#} KB"
        : $"{value / (1024.0 * 1024):0.#} MB";

    /// <summary>
    ///     A port nothing is listening on.
    /// </summary>
    /// <remarks>
    ///     Asked for by binding to zero and reading back what the OS chose, then releasing it. There
    ///     is a race between that and the server binding, and it is the standard one: the alternative
    ///     is a hard-coded port that fails on a machine already using it, which is a certainty rather
    ///     than a race.
    /// </remarks>
    static int FreePort() {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }

    static async Task Ignore(Task task) {
        try {
            await task;
        } catch (OperationCanceledException) {
            // Asked for.
        } catch (HttpListenerException) {
            // The listener was closed out from under the accept loop, which is how it stops.
        }
    }
}
