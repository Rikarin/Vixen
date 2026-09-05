// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Ui;

namespace Vixen.Editor.AssetEditors;

/// <summary>What an editor is handed when something asks for an asset to be opened.</summary>
/// <param name="Project">The project it belongs to.</param>
/// <param name="Asset">Its identity, which is what a reopened tab is found by.</param>
/// <param name="Path">Where the file is, absolute.</param>
/// <remarks>
///     The path is resolved by the registry rather than by each editor, because
///     <see cref="AssetDatabase" /> stores project-relative paths and an editor that did its own
///     combining is an editor that would eventually do it differently from the next one.
/// </remarks>
public sealed record AssetEditorRequest(EditorProject Project, AssetId Asset, string Path);

/// <summary>One asset kind's editor: which files it claims, the document it opens, and the view over it.</summary>
/// <remarks>
///     <para>
///         <b>Both halves in one place, deliberately.</b> A document with no view is a model nobody
///         can reach and a view with no document is a control with nothing behind it; splitting the
///         registration in two would let a build have one without the other and find out when
///         somebody double-clicked a file.
///     </para>
///     <para>
///         ⚠ <b><see cref="CreateView" /> may run more than once for one document.</b> A dock panel's
///         factory runs again when the panel is reopened — the same rule
///         <c>Vixen.Editor.App</c>'s README states for its own panels — so nothing durable may live
///         in a view. What has to survive a reopen belongs on the document.
///     </para>
/// </remarks>
public interface IAssetEditorFactory {
    /// <summary>What this editor is called, in a menu and in a message about two of them clashing.</summary>
    string Name { get; }

    /// <summary>The extensions it claims, each with its leading dot.</summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>Opens an asset as a document.</summary>
    /// <param name="request">What to open.</param>
    /// <returns>The document, already registered with the project by <see cref="EditorDocument" />'s constructor.</returns>
    EditorDocument Open(AssetEditorRequest request);

    /// <summary>Builds the editor's controls into a panel.</summary>
    /// <param name="document">The document, as this factory's <see cref="Open" /> returned it.</param>
    /// <param name="panel">Where the controls go.</param>
    /// <returns>The root of what was built, for a caller that wants to reach it.</returns>
    UiElement CreateView(EditorDocument document, UiElement panel);
}

/// <summary>Which editor opens a file.</summary>
/// <remarks>
///     <para>
///         <b>Told, never discovered</b> — the same rule, and the same reasons, as
///         <c>ImporterRegistry</c>. An assembly scan makes "which editors does this build have" a
///         question with a different answer in a trimmed publish, and two editors claiming one
///         extension resolve by load order, which is how a <c>.vxprefab</c> comes to open in the
///         scene editor on one machine and the prefab editor on another.
///     </para>
///     <para>
///         <b>There is no fallback.</b> An importer has one because "this format has no importer
///         yet" should be a shrug rather than a blocker; a double-click that opened an unknown file
///         in a text editor would be the editor guessing that a <c>.fbx</c> is text. A file nothing
///         claims stays selected in the browser and its import settings are still inspectable, which
///         is what the inspector panel is for.
///     </para>
/// </remarks>
public sealed class AssetEditorRegistry {
    readonly Dictionary<string, IAssetEditorFactory> byExtension = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, IAssetEditorFactory> byName = new(StringComparer.Ordinal);

    /// <summary>How many editors are registered.</summary>
    public int Count => byName.Count;

    /// <summary>Everything registered, in no particular order.</summary>
    public IReadOnlyCollection<IAssetEditorFactory> Editors => byName.Values;

    /// <summary>Registers an editor, and hands back the removal.</summary>
    /// <param name="editor">The editor.</param>
    /// <returns>A scope that takes it out again.</returns>
    /// <exception cref="InvalidOperationException">Something already claims its name or one of its extensions.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>Adding hands back the removal, the way <c>IEditorRegistry.Add</c> already does.</b>
    ///         A plugin's registration scope keeps it, a test disposes it, and nothing has to name
    ///         what it added a second time in order to take it out.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Until this returned something, a plugin could not claim a file extension at
    ///         all.</b> A registration with no matching <c>PluginContext.OnUnload</c> is rule 2 of
    ///         the four that make unloading work — it does not leak an entry, it leaks the plugin's
    ///         whole assembly, permanently and with no error anywhere, because the factory and every
    ///         document it opened are references from the editor into the plugin. Nothing had hit it
    ///         because no module registers an editor: this registry is built once by
    ///         <see cref="StandardEditors.CreateDefault" /> for the life of the process.
    ///         <a href="https://github.com/Rikarin/Vixen/issues/739">#739</a>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The removal takes the name <i>and</i> every extension back out.</b> Either half
    ///         alone leaves a reload finding one free and the other taken, which throws from inside
    ///         somebody's <c>Activate</c> and reads as though the plugin had been loaded twice.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it runs once.</b> A scope disposed twice — a plugin that releases its own
    ///         registration <i>and</i> hands it to <c>PluginContext.Owns</c>, which is doing the
    ///         right thing twice — must not take out the replacement that a reload has since
    ///         registered under the same name. See <c>Removal</c>.
    ///     </para>
    /// </remarks>
    public IDisposable Add(IAssetEditorFactory editor) {
        ArgumentNullException.ThrowIfNull(editor);

        if (!byName.TryAdd(editor.Name, editor)) {
            throw new InvalidOperationException(
                $"Both {byName[editor.Name].GetType().Name} and {editor.GetType().Name} are called "
                + $"'{editor.Name}'. Two editors under one name make a menu that cannot say which is which."
            );
        }

        // ⚠ What has actually been claimed, rather than what the factory says it claims. A property
        // that answered differently on the second read — a plugin building its list lazily — would
        // otherwise leave an extension registered that the removal never looks at.
        var claimed = new List<string>();

        foreach (var extension in editor.Extensions) {
            if (!byExtension.TryAdd(extension, editor)) {
                // ⚠ The name and the extensions taken so far, before the throw. A failed registration
                // that left its name behind would make the *next* attempt fail for the wrong reason,
                // and the second message names an editor that is not registered.
                byName.Remove(editor.Name);

                foreach (var taken in claimed) {
                    byExtension.Remove(taken);
                }

                throw new InvalidOperationException(
                    $"Both '{byExtension[extension].Name}' and '{editor.Name}' claim '{extension}'. One of them "
                    + "has to give it up; whichever registered first is not a rule anyone can rely on."
                );
            }

            claimed.Add(extension);
        }

        return new Removal(this, editor, claimed);
    }

    /// <summary>Finds the editor for a file.</summary>
    /// <param name="path">The file's path or name.</param>
    /// <param name="editor">The editor.</param>
    /// <returns>Whether anything claims it.</returns>
    public bool TryGetForFile(string path, [NotNullWhen(true)] out IAssetEditorFactory? editor) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var extension = Path.GetExtension(path);

        if (extension.Length == 0) {
            editor = null;
            return false;
        }

        return byExtension.TryGetValue(extension, out editor);
    }

    /// <summary>Finds an editor by name.</summary>
    /// <param name="name">The name.</param>
    /// <param name="editor">The editor.</param>
    /// <returns>Whether this build has it.</returns>
    public bool TryGetByName(string name, [NotNullWhen(true)] out IAssetEditorFactory? editor) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return byName.TryGetValue(name, out editor);
    }

    /// <summary>Raised once for each document this registry opens, after it is fully built.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>What a feature that has to reach <i>another</i> asset subscribes to.</b> A proxy
    ///         shape set needs the rig it hangs off; a move set needs the sets it overlays; a clip
    ///         needs the scene it was marked up against. Each of those documents deliberately refuses
    ///         to go looking — one that knew how a project is laid out would be one no test could open
    ///         — so each declares the shape of the answer and somebody with a project supplies it.
    ///         Before this, "somebody" was a line in the editor's application, which meant a second
    ///         feature with resolvers would have been a second line.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not raised for a document that was already open.</b> Opening a tab that is already
    ///         open brings it forward; it is not a second open, and a resolver subscribed twice to one
    ///         document is one that answers twice.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And not from <c>EditorProject.Register</c>, which is where it looks like it
    ///         belongs.</b> That runs from <c>EditorDocument</c>'s base constructor, so a subscriber
    ///         would be handed a half-built document — the one thing that class's own remarks promise
    ///         does not happen.
    ///     </para>
    /// </remarks>
    public event Action<EditorDocument>? Opened;

    /// <summary>Opens an asset, or brings the document already editing it forward.</summary>
    /// <param name="project">The project.</param>
    /// <param name="asset">Which asset.</param>
    /// <param name="opened">The document editing it.</param>
    /// <returns>Whether anything opened it.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An asset already open is returned, not opened again.</b> A second document over
    ///         one file is two undo histories over one set of bytes, and whichever saved last wins —
    ///         which is a way to lose an afternoon's work by double-clicking a scene twice.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>These three sentences used to sit above <see cref="Opened" />, describing it.</b>
    ///         Two <c>&lt;summary&gt;</c> blocks over one member is not a compiler error — the second
    ///         wins — so the event carried this method's documentation and this method carried none,
    ///         in the file every asset editor is read from.
    ///     </para>
    /// </remarks>
    public bool TryOpen(EditorProject project, AssetId asset, [NotNullWhen(true)] out EditorDocument? opened) {
        ArgumentNullException.ThrowIfNull(project);

        if (project.TryGetDocument(asset, out var existing)) {
            project.Activate(existing);
            opened = existing;

            return true;
        }

        if (!project.Assets.TryGetByGuid(asset, out var entry) || entry.IsFolder) {
            opened = null;
            return false;
        }

        if (!TryGetForFile(entry.Path, out var editor)) {
            opened = null;
            return false;
        }

        opened = editor.Open(new(project, asset, project.Paths.Absolute(entry.Path)));
        Opened?.Invoke(opened);

        return true;
    }

    /// <summary>What <see cref="Add" /> hands back: the name and the extensions, given up together.</summary>
    /// <remarks>
    ///     ⚠ <b>Once, and the once is the whole guard.</b> An identity check — "remove this only if
    ///     the registry still holds <i>my</i> factory" — was written here first and is unreachable:
    ///     <see cref="Add" /> refuses a name that is taken, so a live registration and its
    ///     replacement cannot both exist, and by the time a name can be claimed again this scope has
    ///     already run. A second dispose is therefore the only way a stale removal can reach a
    ///     replacement, and <c>done</c> is what stops it. ⚠ It is also the <i>stronger</i> of the
    ///     two: a plugin that registered the same instance again would pass an identity check.
    /// </remarks>
    sealed class Removal(AssetEditorRegistry registry, IAssetEditorFactory editor, List<string> extensions)
        : IDisposable {
        bool done;

        /// <inheritdoc />
        public void Dispose() {
            if (done) {
                return;
            }

            done = true;
            registry.byName.Remove(editor.Name);

            foreach (var extension in extensions) {
                registry.byExtension.Remove(extension);
            }
        }
    }
}
