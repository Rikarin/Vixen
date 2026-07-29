// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Editor.Plugin;

/// <summary>The extension points that do not live in the shell, published by whoever owns them.</summary>
/// <remarks>
///     <para>
///         <b>Doc 11 lists eight extension points and the shell is the vocabulary for five.</b>
///         Commands, menu items and panels are <see cref="PluginContext" />'s directly, because
///         <c>Vixen.Editor.Ui</c> is the one editor assembly this contract references. The other
///         three — property drawers, asset importers, node types, and the gizmos and build steps
///         that come with them — live in <c>Vixen.Editor.Inspector</c>, <c>.Assets</c>,
///         <c>.NodeGraph</c> and <c>.SceneView</c>, and are reached through here.
///     </para>
///     <para>
///         ⚠ <b>Why a lookup rather than four more project references.</b> A plugin that adds a menu
///         item would otherwise have <c>Vixen.Editor.Assets</c> in its build — which carries Assimp
///         and a model importer for two dozen authoring formats — for a contract it never calls. And
///         a plugin that <i>does</i> write an importer references that assembly itself, gets the
///         real <c>IAssetImporter</c> and the real <c>ImporterRegistry</c>, and hands the typed
///         registry to <see cref="Require{T}" />. The weak step is one line at the top of
///         <c>Activate</c>, and everything after it is as typed as it would ever have been.
///     </para>
///     <para>
///         ⚠ <b>One service per type.</b> The host publishes the registries it has; a second
///         <c>DrawerRegistry</c> would mean two answers to "where do drawers go" and half a plugin's
///         drawers landing in the one the inspector does not read. Publishing a type twice throws.
///     </para>
/// </remarks>
public sealed class PluginServices {
    readonly Dictionary<Type, object> services = [];

    /// <summary>What has been published, in no particular order.</summary>
    public IReadOnlyCollection<Type> Available => services.Keys;

    /// <summary>Publishes a service under its own type.</summary>
    /// <typeparam name="T">What plugins will ask for.</typeparam>
    /// <param name="service">The service.</param>
    /// <returns>This, so a host reads as a list.</returns>
    /// <exception cref="ArgumentException">Something is already published under that type.</exception>
    public PluginServices Add<T>(T service) where T : class {
        ArgumentNullException.ThrowIfNull(service);

        if (!services.TryAdd(typeof(T), service)) {
            throw new ArgumentException($"A service is already published as '{typeof(T)}'.", nameof(service));
        }

        return this;
    }

    /// <summary>Whether a service is published.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <returns>Whether it is.</returns>
    public bool Contains<T>() where T : class => services.ContainsKey(typeof(T));

    /// <summary>The service of a type, if the host published one.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="service">The service, if there is one.</param>
    /// <returns>Whether there is.</returns>
    /// <remarks>
    ///     What a plugin uses for an extension point it can do without: a plugin that adds a drawer
    ///     <i>and</i> a menu item should still install its menu item in a host that has no inspector.
    /// </remarks>
    public bool TryGet<T>([NotNullWhen(true)] out T? service) where T : class {
        var found = services.TryGetValue(typeof(T), out var value);
        service = value as T;

        return found;
    }

    /// <summary>The service of a type, or a failure naming it.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <returns>The service.</returns>
    /// <exception cref="PluginException">The host published no such service.</exception>
    /// <remarks>
    ///     The failure is caught by the loader and becomes a diagnostic against this plugin, so a
    ///     plugin that needs something this host has not got is refused with a sentence about what
    ///     was missing rather than by a null reference from inside its own <c>Activate</c>.
    /// </remarks>
    public T Require<T>() where T : class {
        if (TryGet<T>(out var service)) {
            return service;
        }

        throw new PluginException(
            $"This editor publishes no '{typeof(T)}'. The plugin needs a host that does — see PluginServices."
        );
    }
}
