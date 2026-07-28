// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Net.Sockets;
using Vixen.Core;
using Vixen.Core.Serialization;

namespace Vixen.Shaders;

/// <summary>One variant, asked for over the wire.</summary>
/// <remarks>
///     A flat mirror of <see cref="EffectKey" /> rather than the key itself, for the reason the asset
///     compiler's messages mirror their domain types: the key is a struct with a precomputed hash and
///     an immutable array, and a change to it should not silently be a change to a protocol two
///     machines have to agree on.
/// </remarks>
[DataContract("VixenEffectCompileRequest")]
public sealed record EffectCompileRequest {
    /// <summary>The shader.</summary>
    public string Shader { get; set; } = string.Empty;

    /// <summary>Its permutation values, as <c>Name=Value</c>, qualified as the engine names them.</summary>
    public string[] Permutations { get; set; } = [];

    /// <summary>What fills its <c>compose</c> slots, as <c>slot=Shader</c>.</summary>
    public string[] Composition { get; set; } = [];

    /// <summary>Which backend the asking device needs, or empty for the server's own.</summary>
    /// <remarks>
    ///     Asked for rather than assumed, because the point of the service is a device that is not
    ///     the machine compiling: a phone wants GLSL ES and the laptop serving it builds SPIR-V for
    ///     itself all day. A server that cannot produce the target says so rather than sending back
    ///     modules the device will fail to create.
    /// </remarks>
    public string Target { get; set; } = string.Empty;

    /// <summary>The request for a key.</summary>
    public static EffectCompileRequest From(EffectKey key, string target = "") =>
        new() {
            Shader = key.ShaderName,
            Permutations = [.. key.Values.Select(value => $"{value.Key}={value.Value}")],
            Composition = [.. key.Composition.Slots.Select(slot => $"{slot.Key}={slot.Value}")],
            Target = target
        };

    /// <summary>The key it names.</summary>
    public EffectKey ToKey() =>
        EffectKey.Of(Shader, Split(Permutations), ShaderComposition.Of(Split(Composition)));

    static IEnumerable<KeyValuePair<string, string>> Split(string[] entries) {
        foreach (var entry in entries) {
            var separator = entry.IndexOf('=', StringComparison.Ordinal);

            if (separator > 0) {
                yield return new(entry[..separator], entry[(separator + 1)..]);
            }
        }
    }
}

/// <summary>What the compiler on the other end had to say.</summary>
[DataContract("VixenEffectCompileResponse")]
public sealed record EffectCompileResponse {
    /// <summary>Whether a variant came back.</summary>
    /// <remarks>
    ///     Distinct from <see cref="Effect" /> being null, because "no such shader" and "it did not
    ///     compile" are both failures to produce one and only the second is somebody's problem right
    ///     now. A miss falls through to the next tier; an error gets printed.
    /// </remarks>
    public bool Succeeded { get; set; }

    /// <summary>The variant, or null.</summary>
    public EffectData? Effect { get; set; }

    /// <summary>Everything the compiler said, if it said anything.</summary>
    public string[] Diagnostics { get; set; } = [];
}

/// <summary>
///     A variant compiled on a development machine and sent to the device that wants it.
/// </summary>
/// <remarks>
///     <para>
///         Stride's <c>EffectCompilerServer</c> pattern, and the thing that makes shader iteration on
///         a phone or a console tolerable: the device has no compiler and no shader sources, so
///         without this every change to a <c>.rvn</c> is a full content build and a redeploy. With
///         it, the device asks, the laptop compiles, and — stacked under an
///         <see cref="EffectDiskCache" /> — the device only ever asks once per variant.
///     </para>
///     <para>
///         It is an <see cref="IEffectSource" /> like every other tier, which is the whole reason the
///         seam was drawn there. Nothing above it knows a socket is involved.
///     </para>
///     <para>
///         <strong>A development tool, and it does not pretend otherwise.</strong> No authentication,
///         no transport security, and it hands whoever connects whatever it can compile. It belongs
///         on a desk, behind a firewall, and never in anything shipped — which is why a host has to
///         construct one deliberately for it to exist at all.
///     </para>
///     <para>
///         Failure of any kind is a miss. The laptop is asleep, the cable came out, the port moved:
///         all of those mean this tier has no answer, and the tier below should get its turn rather
///         than the frame dying. What a miss <em>costs</em> is a placeholder material for a few
///         frames, which is the arrangement doc 06 asks for anyway.
///     </para>
/// </remarks>
public sealed class RemoteEffectSource : IEffectSource, IDisposable {
    readonly Lock gate = new();

    TcpClient? connection;
    bool disposed;

    /// <summary>Where the compiler is.</summary>
    public string Host { get; }

    /// <summary>Which port it listens on.</summary>
    public int Port { get; }

    /// <summary>Which backend to ask for, or empty for the server's own.</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>How long to wait for an answer before calling it a miss.</summary>
    /// <remarks>
    ///     Generous, because the answer involves a compilation and a cold one is not fast; bounded,
    ///     because a frame waiting forever on a machine that is not going to answer is worse than a
    ///     frame drawn with a placeholder.
    /// </remarks>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>What the last failed request reported, for a host that wants to show it.</summary>
    public IReadOnlyList<string> Diagnostics { get; private set; } = [];

    /// <summary>How many requests have been answered with a variant.</summary>
    public int Served { get; private set; }

    /// <summary>Points at a compiler.</summary>
    public RemoteEffectSource(string host, int port) {
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentOutOfRangeException.ThrowIfNegative(port);

        Host = host;
        Port = port;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Synchronous over an asynchronous transport, deliberately. Every tier answers synchronously
    ///     because the tier that matters — a dictionary lookup — is synchronous, and an
    ///     <c>IEffectSource</c> shaped around the slowest one would put a <c>Task</c> in the way of
    ///     every frame to accommodate a development tool. The caller is expected to resolve off the
    ///     render thread, which is what doc 06 asks for regardless: compile asynchronously, draw a
    ///     placeholder until it arrives.
    /// </remarks>
    public EffectData? TryGet(EffectKey key) {
        ObjectDisposedException.ThrowIf(disposed, this);

        lock (gate) {
            for (var attempt = 0; attempt < 2; attempt++) {
                try {
                    return Exchange(key);
                } catch (Exception exception) when (exception is IOException or SocketException or InvalidDataException or ObjectDisposedException) {
                    // The first failure is usually a connection the server closed while idle, which
                    // is ordinary and worth one silent retry. The second is the machine being gone.
                    Close();

                    if (attempt == 1) {
                        Diagnostics = [$"The shader compiler at {Host}:{Port} did not answer: {exception.Message}"];
                        return null;
                    }
                }
            }

            return null;
        }
    }

    /// <summary>Drops the connection. The next request opens another.</summary>
    public void Dispose() {
        disposed = true;

        lock (gate) {
            Close();
        }
    }

    EffectData? Exchange(EffectKey key) {
        var client = connection ??= Connect();
        var stream = client.GetStream();

        using var deadline = new CancellationTokenSource(Timeout);

        Framing.WriteAsync(stream, EffectCompileRequest.From(key, Target), deadline.Token).GetAwaiter().GetResult();

        var response = Framing.ReadAsync<EffectCompileResponse>(stream, deadline.Token).GetAwaiter().GetResult()
                       ?? throw new IOException("The shader compiler closed the connection without answering.");

        Diagnostics = response.Diagnostics;

        if (response.Succeeded && response.Effect is { } effect) {
            Served++;
            return effect;
        }

        return null;
    }

    TcpClient Connect() {
        var client = new TcpClient { NoDelay = true };

        using var deadline = new CancellationTokenSource(Timeout);
        client.ConnectAsync(Host, Port, deadline.Token).AsTask().GetAwaiter().GetResult();

        return client;
    }

    void Close() {
        connection?.Dispose();
        connection = null;
    }
}
