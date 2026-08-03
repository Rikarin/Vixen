// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vixen.Editor.Ui;

namespace Vixen.Editor.Plugin;

/// <summary>Loads plugins, activates them in order, and takes them back out again.</summary>
/// <remarks>
///     <para>
///         <b>Nothing here throws for a plugin's mistake.</b> A manifest that does not parse, an
///         assembly that is not there, an entry type that cannot be found, a constructor that
///         throws, an <c>Activate</c> that throws, a command id somebody already owns — all six are
///         a <see cref="PluginDiagnostic" /> and a <see cref="PluginState.Failed" />, and the editor
///         carries on opening. An editor that will not start because of a third-party plugin is an
///         editor whose users learn to distrust plugins.
///     </para>
///     <para>
///         ⚠ <b>A failed activation is rolled back completely.</b> A plugin that registered two
///         commands and then threw would otherwise leave both of them behind, pointing into an
///         assembly nothing else refers to — half a plugin, permanently, and a context that can
///         never be collected. The registration scope is disposed on the way out of the failure,
///         which is the same code the unload path runs.
///     </para>
///     <para>
///         ⚠ <b>Not thread-safe, on purpose.</b> The shell's registries are not, the docking host is
///         not, and a loader that took a lock would be advertising a guarantee the things it writes
///         to do not make. Load, unload and reload from the frame thread.
///     </para>
/// </remarks>
public sealed class PluginHost {
    readonly EditorShell shell;
    readonly List<LoadedPlugin> plugins = [];
    readonly List<PluginDiagnostic> diagnostics = [];
    readonly HashSet<string> suppressed = new(StringComparer.Ordinal);

    /// <summary>Creates a host over a shell.</summary>
    /// <param name="shell">The editor's chrome, which is what a plugin registers into.</param>
    /// <param name="services">
    ///     The extension points that are not the shell's, or <c>null</c> for none. See
    ///     <see cref="PluginServices" />.
    /// </param>
    public PluginHost(EditorShell shell, PluginServices? services = null) {
        ArgumentNullException.ThrowIfNull(shell);

        this.shell = shell;
        Services = services ?? new PluginServices();
    }

    /// <summary>What plugins can ask the host for.</summary>
    public PluginServices Services { get; }

    /// <summary>Every plugin the host has been asked to load, whatever became of it.</summary>
    public IReadOnlyList<LoadedPlugin> Plugins => plugins;

    /// <summary>Everything the host has had to say, across every pass.</summary>
    public IReadOnlyList<PluginDiagnostic> Diagnostics => diagnostics;

    /// <summary>Raised after a plugin activates or is unloaded.</summary>
    /// <remarks>What a plugin-management panel listens to in order to redraw its list.</remarks>
    public event Action<LoadedPlugin>? Changed;

    /// <summary>The plugins the user has switched off, whatever their manifests say.</summary>
    /// <remarks>
    ///     ⚠ <b>The user's set, kept apart from <see cref="PluginManifest.Enabled" />.</b> The
    ///     manifest's flag is the <i>author's</i> and lives in the plugin's own directory — which
    ///     for a plugin checked into a repository is a file the whole team shares and for one
    ///     installed globally may not be writable at all. Somebody switching a plugin off in the
    ///     manager is making a statement about their machine, so it is recorded beside their layout
    ///     and their keymap. Either flag alone is enough to keep a plugin out.
    /// </remarks>
    public IReadOnlyCollection<string> Suppressed => suppressed;

    /// <summary>Declares which plugins the user has switched off, before anything is loaded.</summary>
    /// <param name="ids">Their ids.</param>
    /// <remarks>
    ///     Called with what the user store had in it, so that a plugin switched off last session is
    ///     not activated and then unloaded — which would run its <c>Activate</c>, and a plugin
    ///     somebody switched off because it broke the editor is exactly the one whose <c>Activate</c>
    ///     must not run.
    /// </remarks>
    public void Suppress(IEnumerable<string> ids) {
        ArgumentNullException.ThrowIfNull(ids);

        suppressed.Clear();

        foreach (var id in ids) {
            if (!string.IsNullOrEmpty(id)) {
                suppressed.Add(id);
            }
        }
    }

    /// <summary>Switches a plugin off, now and for the next session.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>Whether there is a plugin under that id.</returns>
    public bool Disable(string id) {
        ArgumentNullException.ThrowIfNull(id);

        if (Find(id) is not { } plugin) {
            return false;
        }

        suppressed.Add(id);

        if (plugin.State == PluginState.Active) {
            Deactivate(plugin);
        } else {
            plugin.State = PluginState.Disabled;
        }

        Changed?.Invoke(plugin);
        return true;
    }

    /// <summary>Switches one back on and starts it.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>What happened, which for a plugin that then fails to start says why.</returns>
    /// <remarks>
    ///     ⚠ <b>Through <see cref="Reload" />, which re-reads the manifest and the assembly.</b> A
    ///     plugin switched off in one session and on in the next is one whose files may have changed
    ///     in between — and a plugin the user is switching on <i>because</i> they have just fixed it
    ///     is the ordinary case. Reusing a descriptor read at start-up would load the copy that did
    ///     not work.
    /// </remarks>
    public PluginReport Enable(string id) {
        ArgumentNullException.ThrowIfNull(id);
        suppressed.Remove(id);

        return Reload(id);
    }

    /// <summary>Whether a plugin is switched off, by its author or by the user.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>Whether it is.</returns>
    public bool IsSuppressed(string id) {
        ArgumentNullException.ThrowIfNull(id);
        return suppressed.Contains(id) || Find(id) is { Manifest.Enabled: false };
    }

    /// <summary>The plugin with an id, or <c>null</c>.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The plugin, or <c>null</c>.</returns>
    public LoadedPlugin? Find(string id) {
        ArgumentNullException.ThrowIfNull(id);
        return plugins.FirstOrDefault(plugin => string.Equals(plugin.Id, id, StringComparison.Ordinal));
    }

    /// <summary>Loads and activates everything a scan found.</summary>
    /// <param name="catalog">What discovery found.</param>
    /// <returns>What happened.</returns>
    /// <remarks>
    ///     The catalog's own diagnostics come through into the report, so one notification covers
    ///     "this manifest does not parse" and "this plugin's Activate threw" without the caller
    ///     having to join two lists.
    /// </remarks>
    public PluginReport Load(PluginCatalog catalog) {
        ArgumentNullException.ThrowIfNull(catalog);
        return Load(catalog.Plugins, catalog.Diagnostics);
    }

    /// <summary>Loads and activates a set of plugins.</summary>
    /// <param name="descriptors">The plugins, in discovery order.</param>
    /// <returns>What happened.</returns>
    public PluginReport Load(IReadOnlyList<PluginDescriptor> descriptors) => Load(descriptors, []);

    /// <summary>Takes a plugin back out.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>Whether there was an active plugin under that id.</returns>
    /// <remarks>
    ///     ⚠ <b>Returns as soon as the context has been asked to unload, which is before it has
    ///     been collected.</b> The runtime unloads a collectible context when the last reference
    ///     into it is gone and the GC gets round to it, so the assembly is still in memory when this
    ///     returns and there is nothing wrong with that. <see cref="WaitForCollection" /> is how to
    ///     find out whether it ever left.
    /// </remarks>
    public bool Unload(string id) {
        var plugin = Find(id);

        if (plugin is null || plugin.State != PluginState.Active) {
            return false;
        }

        Deactivate(plugin);
        Changed?.Invoke(plugin);

        return true;
    }

    /// <summary>Takes every active plugin back out, in reverse activation order.</summary>
    /// <remarks>
    ///     Reverse, for the reason the registration scope is: a plugin that depended on another one
    ///     goes first, so nothing is deactivated while something that needs it is still running.
    /// </remarks>
    public void UnloadAll() {
        foreach (var plugin in plugins.Where(plugin => plugin.State == PluginState.Active).Reverse().ToList()) {
            Deactivate(plugin);
            Changed?.Invoke(plugin);
        }
    }

    /// <summary>Unloads a plugin, re-reads its manifest, and activates it again.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>What happened.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The plugin-development loop, and the reason any of this is collectible.</b> Build
    ///         the plugin over the folder the editor is watching, reload, see the change — without
    ///         losing the open project, the scene, the layout or the undo history.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The manifest is read again and the assembly is read from disk again</b>, because
    ///         a reload that reused either would be a reload that could not pick up the build that
    ///         prompted it. The previous context is asked to unload first; if it cannot be collected
    ///         — because the plugin left something behind — the new copy loads anyway and the old
    ///         one stays in memory, which <see cref="WaitForCollection" /> is there to notice.
    ///     </para>
    /// </remarks>
    public PluginReport Reload(string id) {
        var existing = Find(id);

        if (existing is null) {
            return new PluginReport(
                [],
                [],
                [new PluginDiagnostic(PluginSeverity.Error, id, "is not loaded, so there is nothing to reload.")]
            );
        }

        Unload(id);
        plugins.Remove(existing);

        var catalog = PluginDiscovery.Scan(Path.GetDirectoryName(existing.Descriptor.Directory) ?? string.Empty);
        var descriptor = catalog.Find(id);

        if (descriptor is null) {
            return new PluginReport(
                [],
                [],
                [
                    .. catalog.Diagnostics.Where(diagnostic => string.Equals(diagnostic.PluginId, id, StringComparison.Ordinal)),
                    new PluginDiagnostic(PluginSeverity.Error, id, $"is no longer in {existing.Descriptor.Directory}.")
                ]
            );
        }

        return Load([descriptor]);
    }

    /// <summary>Waits for an unloaded plugin's assemblies to actually leave memory.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="timeout">How long to keep collecting before giving up.</param>
    /// <returns>Whether the context was collected.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>False means the plugin leaked, and nothing else reports it.</b> A collectible
    ///         context that cannot be collected produces no exception, no log line and no visible
    ///         symptom until the same plugin is loaded a second time and its static state is not
    ///         what it was. The usual causes are a static event the plugin subscribed to and never
    ///         unsubscribed from, a running thread, or a registration made without a matching
    ///         <see cref="PluginContext.OnUnload" />.
    ///     </para>
    ///     <para>
    ///         Two collections per attempt, because a finalizer that drops the last reference only
    ///         runs between them — the arrangement every collectible-context sample uses, for the
    ///         same reason. This is a development and test tool: an editor should not be forcing
    ///         full collections on a user's machine to find out whether a plugin behaved.
    ///     </para>
    /// </remarks>
    public bool WaitForCollection(string id, TimeSpan timeout) {
        var plugin = Find(id);

        if (plugin?.Collectible is null) {
            return true;
        }

        var deadline = Environment.TickCount64 + (long) timeout.TotalMilliseconds;

        do {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            if (!plugin.Collectible.IsAlive) {
                return true;
            }
        } while (Environment.TickCount64 < deadline);

        return false;
    }

    /// <summary>Runs every active plugin's per-frame work, once.</summary>
    /// <param name="delta">How long the last frame took.</param>
    /// <remarks>
    ///     ⚠ <b>A plugin that throws from here is unloaded rather than allowed to keep throwing.</b>
    ///     Sixty exceptions a second from one panel is an editor that has stopped, and the first one
    ///     is the interesting one — so it is reported and the plugin goes, which is the same answer
    ///     the loader gives a plugin whose <c>Activate</c> throws.
    /// </remarks>
    public void Update(TimeSpan delta) {
        // Over a copy, because a plugin that unloads itself from its own update would otherwise be
        // mutating the list being walked — and "this host cannot run me" is a legitimate thing for a
        // plugin to discover on its first frame.
        foreach (var plugin in plugins.Where(static entry => entry.State == PluginState.Active).ToList()) {
            foreach (var update in plugin.Scope.Updates.ToList()) {
                try {
                    update(delta);
                } catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) {
                    diagnostics.Add(
                        new PluginDiagnostic(
                            PluginSeverity.Error,
                            plugin.Id,
                            $"threw from its per-frame work and has been unloaded: {exception.Message}"
                        )
                    );

                    plugin.Failure = exception;
                    Deactivate(plugin);
                    plugin.State = PluginState.Failed;

                    Changed?.Invoke(plugin);
                    break;
                }
            }
        }
    }

    /// <summary>Activates a module that is compiled in, through the door a third party comes through.</summary>
    /// <param name="id">What everything refers to it by, as a manifest's would be.</param>
    /// <param name="name">What a plugin-management panel calls it.</param>
    /// <param name="module">The module. Its <c>Activate</c> is called immediately.</param>
    /// <param name="context">
    ///     The collectible context the module came out of, for something that was loaded rather than
    ///     compiled in — a project's editor scripts. <see langword="null" /> for a built-in, which is
    ///     already in the default context and stays there.
    /// </param>
    /// <returns>The module as the host is holding it — <see cref="PluginState.Active" />, or failed.</returns>
    /// <exception cref="ArgumentException">Something is already <i>active</i> under that id.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 36 § D2's producer 2, for the editor's own features.</b> Terrain, Blockout, the
    ///         profiler and the graph editors are meant to register through the same
    ///         <see cref="PluginContext" /> a third party uses, because an API whose own authors
    ///         bypass it is a guess rather than a contract. F2 is what bypassing it produced: twelve
    ///         feature assemblies referenced by hand, and a plugin surface that had never had to be
    ///         sufficient.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No assembly loading here, and no context unless the caller brings one.</b> A
    ///         compiled-in module is already in the default context and will be for the life of the
    ///         process; pretending otherwise would mean <see cref="WaitForCollection" /> reporting a
    ///         leak for every built-in. What it does share is everything that matters — the same
    ///         context, the same registration scope, the same rollback on a throw, and the same
    ///         <see cref="Unload" />, so a built-in that leaves a registration behind is as visible
    ///         as a third party's. A caller that <i>did</i> load an assembly — doc 36 § P5's script
    ///         host — hands over its context, and then the unload drops that too.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It does not participate in dependency ordering.</b> A module is named by the
    ///         composition root in the order that root wants; <see cref="PluginOrder" /> exists to
    ///         sort a set discovered on disk, which nobody chose. x    ///         <c>Load</c> so that a third-party plugin can depend on a built-in.
    ///     </para>
    /// </remarks>
    public LoadedPlugin Activate(string id, string name, IEditorPlugin module, PluginLoadContext? context = null) {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(module);

        if (Find(id) is { } existing) {
            if (existing.State == PluginState.Active) {
                throw new ArgumentException(
                    $"'{id}' is already loaded. Two things under one id would make unloading either of "
                    + "them take out whichever the lookup found first.",
                    nameof(id)
                );
            }

            // ⚠ An unloaded entry under this id is dropped rather than refused, because that is what
            // a reload is. Doc 36 § P5: a project's editor scripts are recompiled and activated again
            // every time somebody saves a file, and an id that could only ever be used once would
            // make the second save a diagnostic instead of a rebuild.
            plugins.Remove(existing);
        }

        // The editor's own version for both, because a module ships with the editor and there is no
        // second thing for its version to be. Three components, which is what a manifest's is and
        // what the manager's column formats.
        var version = new Version(EditorApi.Version.Major, EditorApi.Version.Minor, 0);

        var manifest = new PluginManifest {
            Id = id,
            Name = name,
            Version = version,
            Api = EditorApi.Version
        };

        // Empty paths, because there is no folder and no file. `PluginContext.Directory` is therefore
        // empty for a built-in, which is honest: a module's data belongs to the project or to the
        // user's editor directory, not to a plugin folder it does not have.
        // ⚠ Built-in means "ships with the editor and has no folder", which is exactly the case with
        // no load context: a compiled-in module lives in the default one. A project's editor scripts
        // arrive with a collectible context of their own and are not built-in — the plugin manager
        // lists them, and unloading one has an assembly to drop.
        var plugin = new LoadedPlugin(new(manifest, string.Empty, string.Empty, string.Empty)) {
            IsBuiltIn = context is null,
            Context = context
        };

        plugins.Add(plugin);

        try {
            module.Activate(new PluginContext(plugin.Descriptor, shell, Services, plugin.Scope));

            plugin.Instance = module;
            plugin.State = PluginState.Active;
        } catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) {
            // The same rollback the disk path runs. A module that registered two commands and then
            // threw would otherwise leave both behind, and a built-in's half-registration is harder
            // to spot than a plugin's because nobody thinks to look.
            plugin.Scope.Dispose();
            plugin.State = PluginState.Failed;
            plugin.Failure = exception;

            diagnostics.Add(
                new PluginDiagnostic(
                    PluginSeverity.Error,
                    id,
                    $"'{name}' did not start: {exception.Message}"
                )
            );
        }

        Changed?.Invoke(plugin);

        return plugin;
    }

    PluginReport Load(IReadOnlyList<PluginDescriptor> descriptors, IReadOnlyList<PluginDiagnostic> inherited) {
        ArgumentNullException.ThrowIfNull(descriptors);

        var pass = new List<PluginDiagnostic>(inherited);
        var activated = new List<LoadedPlugin>();
        var failed = new List<LoadedPlugin>();

        foreach (var descriptor in PluginOrder.Sort(descriptors, pass)) {
            var plugin = new LoadedPlugin(descriptor);
            plugins.Add(plugin);

            // Either switch is enough to keep it out: the author's, in the manifest, and the
            // user's, which the plugin manager writes beside their layout.
            if (!descriptor.Manifest.Enabled || suppressed.Contains(descriptor.Id)) {
                plugin.State = PluginState.Disabled;
                continue;
            }

            if (Activate(plugin, pass)) {
                activated.Add(plugin);
            } else {
                failed.Add(plugin);
            }

            Changed?.Invoke(plugin);
        }

        // The plugins the sort refused never became a LoadedPlugin — there is nothing to hold for
        // one that was not ordered — so the diagnostics are the whole record of them.
        diagnostics.AddRange(pass.Skip(inherited.Count));

        return new PluginReport(activated, failed, pass);
    }

    bool Activate(LoadedPlugin plugin, List<PluginDiagnostic> pass) {
        var manifest = plugin.Descriptor.Manifest;

        if (EditorApi.Explain(manifest.Api) is { } incompatible) {
            return Fail(plugin, pass, incompatible, null);
        }

        if (plugin.Descriptor.AssemblyPath.Length == 0) {
            return Fail(
                plugin,
                pass,
                $"names '{manifest.AssemblyFileName}', which is not in {plugin.Descriptor.Directory}.",
                null
            );
        }

        PluginLoadContext? context = null;

        try {
            context = new PluginLoadContext(plugin.Descriptor.AssemblyPath, "vixen-plugin:" + plugin.Id);
            var assembly = context.LoadPlugin();

            if (!TryFindEntryPoint(assembly, manifest, out var entry, out var reason)) {
                plugin.Collectible = new WeakReference(context);
                context.Unload();

                return Fail(plugin, pass, reason, null);
            }

            var instance = (IEditorPlugin) Activator.CreateInstance(entry)!;
            var registrations = plugin.Scope;

            instance.Activate(new PluginContext(plugin.Descriptor, shell, Services, registrations));

            plugin.Instance = instance;
            plugin.Context = context;
            plugin.State = PluginState.Active;
            plugin.Failure = null;

            return true;
        } catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) {
            // ⚠ Roll back before unloading, not after. The registrations are the references into
            // the assembly, and a context asked to unload while the command registry still holds a
            // lambda over the plugin's state unloads on paper and stays in memory in fact.
            plugin.Scope.Dispose();

            if (context is not null) {
                plugin.Collectible = new WeakReference(context);
                context.Unload();
            }

            return Fail(plugin, pass, "failed to activate: " + Describe(exception), exception);
        }
    }

    /// <remarks>
    ///     ⚠ <b>Not inlined, and the locals matter.</b> The instance and the context are held in
    ///     this frame; a jitted method that kept them alive in a register past the
    ///     <c>Unload</c> would make the very collection this is arranging impossible, and that is
    ///     precisely the failure mode <c>WaitForCollection</c> would then report on a plugin that
    ///     had done nothing wrong.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    void Deactivate(LoadedPlugin plugin) {
        try {
            plugin.Instance?.Deactivate();
        } catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) {
            Record(new PluginDiagnostic(PluginSeverity.Warning, plugin.Id, "threw while deactivating: " + Describe(exception)));
        }

        plugin.Scope.Dispose();

        foreach (var failure in plugin.Scope.Failures) {
            Record(new PluginDiagnostic(PluginSeverity.Warning, plugin.Id, "left a registration behind: " + Describe(failure)));
        }

        var context = plugin.Context;

        plugin.Instance = null;
        plugin.Context = null;
        plugin.State = PluginState.Unloaded;

        if (context is null) {
            return;
        }

        plugin.Collectible = new WeakReference(context);
        context.Unload();
    }

    static bool Fail(LoadedPlugin plugin, List<PluginDiagnostic> pass, string message, Exception? failure) {
        plugin.State = PluginState.Failed;
        plugin.Failure = failure;

        pass.Add(new PluginDiagnostic(PluginSeverity.Error, plugin.Id, message));
        return false;
    }

    void Record(PluginDiagnostic diagnostic) => diagnostics.Add(diagnostic);

    /// <summary>Finds the one type in the assembly that is the plugin.</summary>
    /// <remarks>
    ///     ⚠ <b>Two candidates and no <c>entryPoint</c> in the manifest is a refusal, not a
    ///     choice.</b> Which of two plugins in one file ran is not something anybody should have to
    ///     discover by experiment, and the fix — name it — is one line in a file the author already
    ///     has open.
    /// </remarks>
    static bool TryFindEntryPoint(
        Assembly assembly,
        PluginManifest manifest,
        [NotNullWhen(true)] out Type? entry,
        out string reason
    ) {
        entry = null;
        reason = string.Empty;

        Type[] types;

        try {
            types = assembly.GetTypes();
        } catch (ReflectionTypeLoadException exception) {
            // A plugin built against something that is not here. The loader exceptions name the
            // assembly that could not be found, which is the sentence the author needs.
            reason = "could not be read: "
                + string.Join("; ", exception.LoaderExceptions.Where(inner => inner is not null).Select(inner => inner!.Message).Distinct(StringComparer.Ordinal).Take(3));

            return false;
        }

        if (manifest.EntryPoint.Length > 0) {
            entry = types.FirstOrDefault(type => string.Equals(type.FullName, manifest.EntryPoint, StringComparison.Ordinal));

            if (entry is null) {
                reason = $"names '{manifest.EntryPoint}' as its entry point, and {assembly.GetName().Name} has no such type.";
                return false;
            }

            if (!IsCandidate(entry)) {
                reason = $"names '{manifest.EntryPoint}', which is not a public IEditorPlugin with a parameterless constructor.";
                entry = null;

                return false;
            }

            return true;
        }

        var candidates = types.Where(IsCandidate).ToList();

        switch (candidates.Count) {
            case 1:
                entry = candidates[0];
                return true;

            case 0:
                reason = $"has no public IEditorPlugin in {assembly.GetName().Name}.";
                return false;

            default:
                reason = "has more than one IEditorPlugin ("
                    + string.Join(", ", candidates.Select(type => type.FullName).Order(StringComparer.Ordinal))
                    + "). Name one in the manifest's 'entryPoint'.";

                return false;
        }
    }

    static bool IsCandidate(Type type) =>
        type is { IsClass: true, IsAbstract: false, IsPublic: true }
        && typeof(IEditorPlugin).IsAssignableFrom(type)
        && type.GetConstructor(Type.EmptyTypes) is not null;

    /// <summary>An exception as one line, with the inner one that is usually the real answer.</summary>
    static string Describe(Exception exception) {
        var real = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
        return $"{real.GetType().Name}: {real.Message}";
    }
}
