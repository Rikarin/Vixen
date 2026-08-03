// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Assets;

/// <summary>Importers something other than this build contributed — a plugin, a project script.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § F8's fix.</b> That finding said "importers are constructed and handed in;
///         there is no registry for a plugin to add to", and the second half was the part that
///         mattered: <see cref="ImporterRegistry" /> has existed all along, but it is built fresh per
///         run by <see cref="BuiltInImporters.Create" /> — inside a background task, deliberately, so
///         that the editor and the CLI cannot disagree about the set. A plugin had nothing to add to
///         because every registry it could have reached was about to be thrown away.
///     </para>
///     <para>
///         ⚠ <b>What was needed is a set that outlives a run</b>, which is this, folded in by
///         <c>Create</c>. <c>EditorApplication</c>'s own remark said exactly that and named this
///         assembly as where the change belonged; this is that change.
///     </para>
///     <para>
///         ⚠ <b>Process-wide, and unlike <c>EditorRegistry</c> it has to be.</b> The consumers are
///         static factories called from background tasks with no editor to be handed —
///         <c>ProjectWorkspace.Importers()</c>, <c>ContentPipeline</c>, <c>Vixen.Cli</c> — and a
///         per-session registry would reach none of them. What makes that safe is that
///         <see cref="Add" /> hands back the removal: a plugin's scope disposes it on unload, and a
///         test disposes it in a <c>finally</c>. An editor that shut down without withdrawing its
///         contributions would leave an importer naming a type in an unloaded assembly, which is
///         F8's own trap one level down.
///     </para>
///     <para>
///         ⚠ <b>A contributed importer does not reach an out-of-process compiler worker, and this
///         does not pretend otherwise.</b> <c>Tools/Vixen.AssetCompiler</c> starts workers for crash
///         isolation and each builds its registry from the same <c>Create</c> — but a worker process
///         has not loaded the plugin, so its <c>Default</c> is empty and an asset only that plugin
///         can import fails there. Closing it means the worker loading the same plugin set the
///         coordinator has, which is a change to the worker's start-up and is not this.
///     </para>
/// </remarks>
public sealed class ImporterContributions {
    readonly Lock gate = new();
    readonly List<IAssetImporter> importers = [];

    /// <summary>The set every registry built in this process folds in.</summary>
    /// <remarks>
    ///     Published to plugins through <c>PluginServices</c>, on <c>DrawerRegistry.Default</c>'s
    ///     terms: a plugin asks the host for the one it publishes rather than reaching for the
    ///     static, so a host running two editors gets two answers rather than one shared one — even
    ///     though, here, the two answers happen to be the same object.
    /// </remarks>
    public static ImporterContributions Default { get; } = new();

    /// <summary>Everything contributed, oldest first.</summary>
    public IReadOnlyList<IAssetImporter> All {
        get {
            lock (gate) {
                return [.. importers];
            }
        }
    }

    /// <summary>Contributes an importer.</summary>
    /// <param name="importer">The importer.</param>
    /// <returns>A scope that withdraws it again.</returns>
    /// <remarks>
    ///     ⚠ <b>Not validated here, because the conflict is a property of a registry and not of a
    ///     list.</b> Two importers claiming one extension is an error <see cref="ImporterRegistry" />
    ///     raises when the set is assembled — with both names in the message — and raising it at
    ///     contribution time would mean a plugin failing to load because of an importer that had
    ///     already been withdrawn.
    /// </remarks>
    public IDisposable Add(IAssetImporter importer) {
        ArgumentNullException.ThrowIfNull(importer);

        lock (gate) {
            importers.Add(importer);
        }

        return new Removal(this, importer);
    }

    /// <summary>Forgets everything contributed. For a test that wants a clean process.</summary>
    public void Clear() {
        lock (gate) {
            importers.Clear();
        }
    }

    /// <summary>Adds every contribution to a registry.</summary>
    /// <param name="registry">The registry.</param>
    /// <returns>It, for chaining.</returns>
    /// <exception cref="InvalidOperationException">Two importers claim one extension, or one name.</exception>
    public ImporterRegistry ApplyTo(ImporterRegistry registry) {
        ArgumentNullException.ThrowIfNull(registry);

        foreach (var importer in All) {
            registry.Add(importer);
        }

        return registry;
    }

    sealed class Removal(ImporterContributions owner, IAssetImporter importer) : IDisposable {
        bool disposed;

        public void Dispose() {
            if (disposed) {
                return;
            }

            disposed = true;

            lock (owner.gate) {
                owner.importers.Remove(importer);
            }
        }
    }
}
