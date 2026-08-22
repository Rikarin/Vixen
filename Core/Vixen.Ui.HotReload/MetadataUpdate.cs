// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection.Metadata;
using Vixen.Ui.HotReload;

[assembly: MetadataUpdateHandler(typeof(MetadataUpdate))]

namespace Vixen.Ui.HotReload;

/// <summary>
///     The .NET runtime's hot-reload callback: what <c>dotnet watch</c> calls once it has replaced
///     a method body in the running process.
/// </summary>
/// <remarks>
///     <para>
///         This is the other half of the markup channel, and the half that is not ours. Editing a
///         <c>.vxml</c> regenerates a C# file, which changes <c>Build</c>, which the runtime
///         delivers here — and only then is there anything new to run. A watcher over
///         <c>.vxml</c> could see the edit sooner and do nothing useful with it.
///     </para>
///     <para>
///         ⚠ <b>Hosts are held weakly.</b> A static list of every document ever created is a leak
///         with a development-only cause and a production-shaped consequence, and the handler has no
///         idea when a window closes. A collected host simply stops being reloaded.
///     </para>
/// </remarks>
public static class MetadataUpdate {
    static readonly List<WeakReference<HotReloadHost>> Hosts = [];
    static readonly Lock Gate = new();

    /// <summary>Registers a host to be reloaded when the runtime updates the application.</summary>
    /// <param name="host">The host.</param>
    public static void Register(HotReloadHost host) {
        ArgumentNullException.ThrowIfNull(host);

        lock (Gate) {
            Hosts.Add(new WeakReference<HotReloadHost>(host));
        }
    }

    /// <summary>Stops reloading a host.</summary>
    /// <param name="host">The host.</param>
    /// <returns>Whether it was registered.</returns>
    public static bool Unregister(HotReloadHost host) {
        ArgumentNullException.ThrowIfNull(host);

        lock (Gate) {
            return Hosts.RemoveAll(
                reference => !reference.TryGetTarget(out var live) || ReferenceEquals(live, host)
            ) > 0;
        }
    }

    /// <summary>The registered hosts that are still alive.</summary>
    internal static IReadOnlyList<HotReloadHost> Live {
        get {
            lock (Gate) {
                Hosts.RemoveAll(reference => !reference.TryGetTarget(out _));
                return [.. Hosts.Select(reference => reference.TryGetTarget(out var host) ? host : null)
                    .OfType<HotReloadHost>()];
            }
        }
    }

    /// <summary>Called by the runtime before an update is applied.</summary>
    /// <param name="types">The types that changed, or null when the runtime does not know.</param>
    /// <remarks>
    ///     Nothing to clear: the composition runtime caches no metadata about a component, because
    ///     everything it knows it learned by running <c>Build</c> and it is about to run it again.
    ///     Declared anyway — the runtime looks the method up by name, and a handler type without one
    ///     is a silent no-op that looks like a working integration.
    /// </remarks>
    public static void ClearCache(Type[]? types) => _ = types;

    /// <summary>Called by the runtime after an update is applied.</summary>
    /// <param name="types">The types that changed, or null when the runtime does not know.</param>
    /// <remarks>
    ///     ⚠ <b>The types are passed on rather than dropped, and that is what reaches the component
    ///     channel.</b> Almost every update is a changed method body, and
    ///     re-running <c>Build</c> on the same object is both the cheap answer and the one that keeps
    ///     a panel's state. The exception is an instance the update left behind — the runtime can add
    ///     a field to a live type and the initialiser that would fill it does not run on an object
    ///     that already exists — and telling that apart from an ordinary mistake needs to know which
    ///     types the runtime actually touched. See
    ///     <see cref="HotReloadHost.ReloadComponents(IReadOnlyCollection{Type})" />.
    /// </remarks>
    public static void UpdateApplication(Type[]? types) {
        // A set rather than the array: the lookup is per tracked component and the runtime's array
        // is however long the edit was.
        var updated = types is null || types.Length == 0 ? null : new HashSet<Type>(types);

        foreach (var host in Live) {
            _ = host.ReloadComponents(updated);
        }
    }
}
