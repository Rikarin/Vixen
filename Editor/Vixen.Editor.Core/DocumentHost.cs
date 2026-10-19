// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core;

namespace Vixen.Editor.Core;

/// <summary>Which kind opens a file, and the one place an asset becomes an open document.</summary>
/// <remarks>
///     <para>
///         <b>Doc 50 § D4's Documents half.</b> <see cref="TryOpen" /> resolves the kind; the
///         Workspace's <c>IWorkspace.Show</c> resolves the view. A module reaches it with
///         <c>Require&lt;IDocumentHost&gt;()</c> to claim an extension or to hear a document open.
///     </para>
///     <para>
///         <b>Told, never discovered</b> — the same rule, and the same reasons, as
///         <c>ImporterRegistry</c>. An assembly scan makes "which kinds does this build have" a
///         question with a different answer in a trimmed publish, and two kinds claiming one
///         extension resolve by load order, which is how a <c>.vxprefab</c> comes to open in the
///         scene editor on one machine and the prefab editor on another.
///     </para>
///     <para>
///         <b>There is no fallback.</b> A file nothing claims stays selected in the browser and its
///         import settings are still inspectable, which is what the inspector panel is for.
///     </para>
/// </remarks>
public interface IDocumentHost {
    /// <summary>Everything registered, in no particular order.</summary>
    IReadOnlyCollection<DocumentKind> Kinds { get; }

    /// <summary>Raised once for each document this host opens, after it is fully built.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>What a feature that has to reach <i>another</i> asset subscribes to.</b> A proxy
    ///         shape set needs the rig it hangs off; a move set needs the sets it overlays. Each of
    ///         those documents deliberately refuses to go looking, so each declares the shape of the
    ///         answer and a module with a project supplies it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not raised for a document that was already open.</b> Opening a tab that is already
    ///         open brings it forward; it is not a second open, and a resolver subscribed twice to one
    ///         document is one that answers twice.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And not from <c>EditorProject.Register</c>, which is where it looks like it
    ///         belongs.</b> That runs from <see cref="EditorDocument" />'s base constructor, so a
    ///         subscriber would be handed a half-built document.
    ///     </para>
    /// </remarks>
    event Action<EditorDocument>? Opened;

    /// <summary>Registers a kind, and hands back the removal.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>A scope that takes it out again — a plugin's registration scope keeps it.</returns>
    /// <exception cref="InvalidOperationException">Something already claims its name or one of its extensions.</exception>
    IDisposable Add(DocumentKind kind);

    /// <summary>Finds the kind for a file.</summary>
    /// <param name="path">The file's path or name.</param>
    /// <param name="kind">The kind.</param>
    /// <returns>Whether anything claims it.</returns>
    bool TryGetForFile(string path, [NotNullWhen(true)] out DocumentKind? kind);

    /// <summary>Finds a kind by name.</summary>
    /// <param name="name">The name.</param>
    /// <param name="kind">The kind.</param>
    /// <returns>Whether this build has it.</returns>
    bool TryGetByName(string name, [NotNullWhen(true)] out DocumentKind? kind);

    /// <summary>Opens an asset, or brings the document already editing it forward.</summary>
    /// <param name="asset">Which asset.</param>
    /// <param name="opened">The document editing it.</param>
    /// <returns>Whether anything opened it.</returns>
    bool TryOpen(AssetId asset, [NotNullWhen(true)] out EditorDocument? opened);
}

/// <summary>The project's <see cref="IDocumentHost" />: a name and extension index over the registered kinds.</summary>
/// <remarks>
///     ⚠ <b>Was <c>AssetEditorRegistry</c>, in <c>Vixen.Editor.AssetEditors</c>, until #1294</b> — and
///     every remark below that is about registration was written there, for the reasons it gives. It
///     is here now because nothing in it ever needed an element.
/// </remarks>
public sealed class DocumentHost : IDocumentHost {
    readonly Dictionary<string, DocumentKind> byExtension = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DocumentKind> byName = new(StringComparer.Ordinal);
    readonly EditorProject project;

    /// <summary>A host over a project, with nothing registered.</summary>
    /// <param name="project">Where opened documents go.</param>
    public DocumentHost(EditorProject project) {
        ArgumentNullException.ThrowIfNull(project);
        this.project = project;
    }

    /// <summary>How many kinds are registered.</summary>
    public int Count => byName.Count;

    /// <inheritdoc />
    public IReadOnlyCollection<DocumentKind> Kinds => byName.Values;

    /// <inheritdoc />
    public event Action<EditorDocument>? Opened;

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Until this returned something, a plugin could not claim a file extension at
    ///         all</b> (<a href="https://github.com/Rikarin/Vixen/issues/739">#739</a>). A
    ///         registration with no matching <c>PluginContext.OnUnload</c> leaks the plugin's whole
    ///         assembly, because the kind and every document it opened are references from the editor
    ///         into the plugin.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The removal takes the name <i>and</i> every extension back out, once.</b> Either
    ///         half alone leaves a reload finding one free and the other taken; a scope disposed twice
    ///         must not take out the replacement a reload has since registered under the same name.
    ///     </para>
    /// </remarks>
    public IDisposable Add(DocumentKind kind) {
        ArgumentNullException.ThrowIfNull(kind);

        if (!byName.TryAdd(kind.Name, kind)) {
            throw new InvalidOperationException(
                $"Both {byName[kind.Name].GetType().Name} and {kind.GetType().Name} are called "
                + $"'{kind.Name}'. Two kinds under one name make a menu that cannot say which is which."
            );
        }

        // ⚠ What has actually been claimed, rather than what the kind says it claims. A property that
        // answered differently on the second read would otherwise leave an extension registered that
        // the removal never looks at.
        var claimed = new List<string>();

        foreach (var extension in kind.Extensions) {
            if (!byExtension.TryAdd(extension, kind)) {
                // ⚠ The name and the extensions taken so far, before the throw. A failed registration
                // that left its name behind would make the *next* attempt fail for the wrong reason.
                byName.Remove(kind.Name);

                foreach (var taken in claimed) {
                    byExtension.Remove(taken);
                }

                throw new InvalidOperationException(
                    $"Both '{byExtension[extension].Name}' and '{kind.Name}' claim '{extension}'. One of them "
                    + "has to give it up; whichever registered first is not a rule anyone can rely on."
                );
            }

            claimed.Add(extension);
        }

        return new Removal(this, kind, claimed);
    }

    /// <inheritdoc />
    public bool TryGetForFile(string path, [NotNullWhen(true)] out DocumentKind? kind) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var extension = Path.GetExtension(path);

        if (extension.Length == 0) {
            kind = null;
            return false;
        }

        return byExtension.TryGetValue(extension, out kind);
    }

    /// <inheritdoc />
    public bool TryGetByName(string name, [NotNullWhen(true)] out DocumentKind? kind) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return byName.TryGetValue(name, out kind);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An asset already open is returned, not opened again.</b> A second document over
    ///         one file is two undo histories over one set of bytes, and whichever saved last wins.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A document of a type its kind did not declare is refused, closed, and thrown
    ///         about.</b> The view is found by that type, so a kind that drifted from what it declares
    ///         would otherwise open a document with no view and the double-click would do nothing
    ///         anyone could explain.
    ///     </para>
    /// </remarks>
    public bool TryOpen(AssetId asset, [NotNullWhen(true)] out EditorDocument? opened) {
        if (project.TryGetDocument(asset, out var existing)) {
            project.Activate(existing);
            opened = existing;

            return true;
        }

        if (!project.Assets.TryGetByGuid(asset, out var entry) || entry.IsFolder) {
            opened = null;
            return false;
        }

        if (!TryGetForFile(entry.Path, out var kind)) {
            opened = null;
            return false;
        }

        var document = kind.Open(new(project, asset, project.Paths.Absolute(entry.Path)));

        if (!kind.Declares(document)) {
            document.Close();

            throw new InvalidOperationException(
                $"'{kind.Name}' opened a {document.GetType().Name}, which is not one of the document types it "
                + "declares — so no view would be found for it. Add the type to DocumentTypes, with a "
                + "DocumentView for it."
            );
        }

        opened = document;
        Opened?.Invoke(document);

        return true;
    }

    /// <summary>What <see cref="Add" /> hands back: the name and the extensions, given up together, once.</summary>
    /// <remarks>
    ///     ⚠ <b>Once, and the once is the whole guard.</b> <see cref="Add" /> refuses a name that is
    ///     taken, so a second dispose is the only way a stale removal can reach a replacement, and
    ///     <c>done</c> is what stops it.
    /// </remarks>
    sealed class Removal(DocumentHost host, DocumentKind kind, List<string> extensions) : IDisposable {
        bool done;

        /// <inheritdoc />
        public void Dispose() {
            if (done) {
                return;
            }

            done = true;
            host.byName.Remove(kind.Name);

            foreach (var extension in extensions) {
                host.byExtension.Remove(extension);
            }
        }
    }
}
