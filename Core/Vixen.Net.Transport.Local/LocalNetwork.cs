// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Transport.Local;

/// <summary>
///     The wire the local transports share: where a client looks to find the server it was told to
///     connect to.
/// </summary>
/// <remarks>
///     <para>
///         An in-process transport still needs a rendezvous, because "the server" is an object and
///         the client has to be given a way to find it that is not a direct reference — otherwise a
///         test cannot express "connect to something that is not listening", and that is one of the
///         cases worth testing.
///     </para>
///     <para>
///         <b>An instance is a network.</b> Two <see cref="LocalNetwork" /> objects cannot reach each
///         other whatever they name their addresses, so two tests running side by side in the same
///         process are as isolated as two machines. That is why this is an object and not a static
///         registry: xunit runs test classes in parallel, and a static one would have them share a
///         world.
///     </para>
/// </remarks>
public sealed class LocalNetwork {
    /// <summary>The address a transport listens on and connects to when it is not told otherwise.</summary>
    public const string DefaultAddress = "local";

    readonly Lock gate = new();
    readonly Dictionary<string, LocalTransport> listeners = new(StringComparer.Ordinal);

    /// <summary>How many servers are listening on this network.</summary>
    public int ListenerCount {
        get {
            lock (gate) {
                return listeners.Count;
            }
        }
    }

    /// <summary>Whether a server is listening on <paramref name="address" />.</summary>
    /// <param name="address">The address to look for.</param>
    /// <returns><see langword="true" /> if a transport has that address bound.</returns>
    public bool IsListening(string address) {
        lock (gate) {
            return listeners.ContainsKey(address);
        }
    }

    internal void Bind(string address, LocalTransport listener) {
        lock (gate) {
            if (!listeners.TryAdd(address, listener)) {
                throw new TransportException(
                    $"A local server is already listening on '{address}'. Every listener on one LocalNetwork needs its own address."
                );
            }
        }
    }

    internal void Unbind(string address, LocalTransport listener) {
        lock (gate) {
            // Compare the value, not just the key: a transport that stopped after another one bound
            // the address it used to hold must not evict the newcomer.
            if (listeners.TryGetValue(address, out var bound) && ReferenceEquals(bound, listener)) {
                listeners.Remove(address);
            }
        }
    }

    internal LocalTransport? Find(string address) {
        lock (gate) {
            return listeners.GetValueOrDefault(address);
        }
    }
}
