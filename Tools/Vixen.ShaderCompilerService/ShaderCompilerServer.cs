// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Vixen.Core.Serialization;
using Vixen.ShaderCompiler;
using Vixen.Shaders;

namespace Vixen.ShaderCompilerService;

/// <summary>
///     Compiles shader variants for devices that have no compiler.
/// </summary>
/// <remarks>
///     <para>
///         The other half of <see cref="RemoteEffectSource" />, and the reason doc 06 says this is
///         worth building early. A phone or a console has no Raven and no shader sources, so without
///         it every change to a <c>.rvn</c> means a content build and a redeploy — a loop long enough
///         that people stop making the change.
///     </para>
///     <para>
///         <strong>The disk cache sits on this side too, and that is what makes it fast.</strong>
///         Five devices asking for one variant is one compilation, and a restart of this process
///         costs nothing: the entries are still there. The device caches as well, so the second run
///         of the game does not ask at all — the two caches are for different things, one saving the
///         compile and the other saving the round trip.
///     </para>
///     <para>
///         <strong>A development tool.</strong> No authentication, no transport security. Anything
///         that reaches the port gets whatever this can compile, so it belongs on a desk behind a
///         firewall, bound to a specific interface if the desk is not a private one.
///     </para>
/// </remarks>
public sealed class ShaderCompilerServer : IDisposable {
    readonly ConcurrentDictionary<string, IEffectSource> targets = new(StringComparer.OrdinalIgnoreCase);
    readonly Func<string, IEffectSource?> open;
    readonly TcpListener listener;

    CancellationTokenSource? running;
    Task? accepting;

    /// <summary>What the server writes about what it is doing.</summary>
    public TextWriter Log { get; init; } = TextWriter.Null;

    /// <summary>Which port it ended up on. Meaningful after <see cref="Start" />.</summary>
    /// <remarks>
    ///     Asked rather than assumed, because port 0 means "any free one" — which is what a test
    ///     wants, and what a second instance on one machine needs.
    /// </remarks>
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    /// <summary>How many requests have been answered with a variant.</summary>
    public int Served { get; private set; }

    /// <summary>How many were answered with nothing.</summary>
    public int Missed { get; private set; }

    /// <summary>Creates a server.</summary>
    /// <param name="address">Which interface to listen on.</param>
    /// <param name="port">Which port, or 0 for any free one.</param>
    /// <param name="open">
    ///     Opens the source for a target, or null when this server cannot produce that target. Called
    ///     once per distinct target and the result reused.
    /// </param>
    public ShaderCompilerServer(IPAddress address, int port, Func<string, IEffectSource?> open) {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(open);

        this.open = open;
        listener = new(address, port);
    }

    /// <summary>Starts listening.</summary>
    public void Start() {
        listener.Start();
        running = new();
        accepting = AcceptAsync(running.Token);
        Log.WriteLine($"Listening on {listener.LocalEndpoint}.");
    }

    /// <summary>Stops, and waits for the accept loop to notice.</summary>
    public void Dispose() {
        running?.Cancel();
        listener.Dispose();

        try {
            accepting?.GetAwaiter().GetResult();
        } catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException) {
            // Shutting down is how this loop ends; every one of these is that.
        }

        running?.Dispose();
    }

    async Task AcceptAsync(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
            TcpClient client;

            try {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            } catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException) {
                return;
            }

            // Not awaited: one slow compilation must not stop the next device connecting. A device
            // that disconnects mid-request ends its own task and nothing else.
            _ = ServeAsync(client, cancellationToken);
        }
    }

    async Task ServeAsync(TcpClient client, CancellationToken cancellationToken) {
        using (client) {
            var stream = client.GetStream();

            try {
                while (!cancellationToken.IsCancellationRequested) {
                    var request = await Framing.ReadAsync<EffectCompileRequest>(stream, cancellationToken).ConfigureAwait(false);

                    if (request is null) {
                        return;
                    }

                    await Framing.WriteAsync(stream, Answer(request), cancellationToken).ConfigureAwait(false);
                }
            } catch (Exception exception) when (exception is IOException or SocketException or InvalidDataException or OperationCanceledException) {
                Log.WriteLine($"A connection ended: {exception.Message}");
            }
        }
    }

    /// <summary>Compiles one request, or explains why it could not.</summary>
    /// <remarks>
    ///     <para>
    ///         <strong>Total, deliberately: every failure becomes a response.</strong> This process
    ///         serves several devices and outlives all of them, and the thing most likely to go wrong
    ///         is a shader somebody is halfway through editing — which must not be able to take the
    ///         service down, take a connection down, or reach the device as a closed socket. The
    ///         person editing wants the diagnostics, and a socket cannot carry them.
    ///     </para>
    ///     <para>
    ///         Cancellation is the exception, and passes through: that is the service being stopped,
    ///         not a request going wrong.
    ///     </para>
    /// </remarks>
    internal EffectCompileResponse Answer(EffectCompileRequest request) {
        var key = request.ToKey();

        try {
            if (Source(request.Target) is not { } source) {
                Missed++;
                Log.WriteLine($"refused {key}: no '{request.Target}'");
                return new() { Diagnostics = [$"This compiler does not produce '{request.Target}'."] };
            }

            if (source.TryGet(key) is not { } effect) {
                Missed++;
                Log.WriteLine($"miss {key}");
                return new() { Diagnostics = [$"No shader named '{key.ShaderName}' is in these sources."] };
            }

            Served++;
            Log.WriteLine($"served {key}");
            return new() { Succeeded = true, Effect = effect };
        } catch (ShaderCompilationException exception) {
            Missed++;
            Log.WriteLine($"failed {key}");
            return new() { Diagnostics = [.. exception.Diagnostics] };
        } catch (Exception exception) when (exception is not OperationCanceledException) {
            Missed++;
            Log.WriteLine($"failed {key}: {exception.Message}");
            return new() { Diagnostics = [exception.Message] };
        }
    }

    IEffectSource? Source(string target) {
        var name = target ?? "";

        if (targets.TryGetValue(name, out var existing)) {
            return existing;
        }

        // Opened once per target and reused, because opening one parses every shader source in the
        // project. Doing that per request would make the first frame of every device pay for it —
        // and a source that could not be opened is not remembered, so fixing the shader and asking
        // again works without a restart.
        if (open(name) is not { } created) {
            return null;
        }

        return targets.GetOrAdd(name, created);
    }
}
