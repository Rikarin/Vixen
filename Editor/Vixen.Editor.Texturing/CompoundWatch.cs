// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Texturing;

/// <summary>Whether the node library a document published has been overtaken by a compound.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/970">#970</a>: one staleness protocol,
///         where there were two identical ones.</b> <c>TextureGraphDocument</c> grew a folder, a
///         flag, a <c>DocumentSaving</c> handler, an <c>OnProjectFileChanged</c> override and a
///         <c>StartsWith(folder + separator)</c> test; <a
///         href="https://github.com/Rikarin/Vixen/issues/956">#956</a> gave <c>LayerStackDocument</c>
///         the same five, because the first file belonged to another slice at the time. Both are one
///         slice's now, and the duplication is exactly the shape <c>TextureNodeLibrary.FolderOf</c>'s
///         own remarks refuse one level up — a second spelling of "did that file change" is a second
///         answer to it.
///     </para>
///     <para>
///         ⚠ <b>The failure mode is a fix that reaches one copy.</b> #922 taught the graph document
///         about compounds changed outside the editor, and the stack document had to be taught it
///         again by hand a batch later; a third document that publishes a library would have made
///         three. That is the cost this type removes, and it is the only thing it does — it holds no
///         library and knows nothing about what a caller does with one.
///     </para>
///     <para>
///         ⚠ <b>A flag set by a notification, never a walk of the folder.</b> Both documents ask
///         whether they are stale from an interactive path — a compile, a panel's every show, an
///         opacity drag that raises an edit per frame — so a <c>stat</c> per compound per question is
///         the directory walk both fixes existed to remove, wearing a hat. Something says a compound
///         moved; this remembers that it did.
///     </para>
/// </remarks>
sealed class CompoundWatch {
    /// <summary>The assets folder to republish from.</summary>
    readonly string assets;

    /// <summary>The compound folder, absolute and without its trailing separator.</summary>
    /// <remarks>
    ///     Resolved once here rather than per comparison, which is the other half of one spelling:
    ///     the two documents each ran <c>Path.GetFullPath</c> and <c>TrimEndingDirectorySeparator</c>
    ///     at every question, and a difference between those two expressions would have been a
    ///     difference in which files counted.
    /// </remarks>
    readonly string folder;

    /// <summary>Whether a compound has moved since the library was built.</summary>
    bool stale;

    CompoundWatch(string assets, string folder) {
        this.assets = assets;
        this.folder = folder;
    }

    /// <summary>Follows the compound folder of a project's assets.</summary>
    /// <param name="assets">The project's assets folder.</param>
    /// <returns>The watch, or <see langword="null" /> when there is no folder to watch.</returns>
    /// <remarks>
    ///     ⚠ <b>Null is a real state and not a guard against a mistake.</b>
    ///     <see cref="TextureNodeLibrary.FolderOf" /> answers null for a project with no assets path,
    ///     and <c>TextureGraphDocument</c> reaches this only when it published its own library — a
    ///     caller that brought its own registry brings its own sub-graph source too, and has nothing
    ///     to keep fresh.
    /// </remarks>
    public static CompoundWatch? Over(string? assets) =>
        assets is { Length: > 0 } && TextureNodeLibrary.FolderOf(assets) is { } found
            ? new(assets, Path.TrimEndingDirectorySeparator(Path.GetFullPath(found)))
            : null;

    /// <summary>Records that the watcher lost events, so anything may have happened.</summary>
    /// <remarks>
    ///     ⚠ <b>The conservative answer, in both callers, and it is the honest one.</b> An overflow
    ///     says nothing about which file moved. The cost of being wrong is one republish; the cost of
    ///     assuming the best is a bake made from a compound nobody can see is old.
    /// </remarks>
    public void Lost() => stale = true;

    /// <summary>Records a file that moved, and answers whether the library is now stale.</summary>
    /// <param name="absolute">The file's path, absolute.</param>
    /// <returns>Whether a republish is owed — including from an earlier notification.</returns>
    /// <exception cref="ArgumentException"><paramref name="absolute" /> is null or empty.</exception>
    /// <remarks>
    ///     ⚠ <b>The answer is the accumulated flag rather than "was this one a compound".</b> A
    ///     caller that read a per-call answer would clear nothing and would miss a compound that
    ///     moved two notifications ago, which is the reason both documents wrote
    ///     <c>stale = stale || …</c> rather than an assignment.
    /// </remarks>
    public bool Noticed(string absolute) {
        ArgumentException.ThrowIfNullOrEmpty(absolute);

        stale = stale
            || Path.GetFullPath(absolute)
                .StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        return stale;
    }

    /// <summary>Rebuilds the library if a compound has moved since it was built.</summary>
    /// <param name="adopt">What to do with the new library.</param>
    /// <returns><see langword="true" /> if it was rebuilt, so a caller can re-read what it cached.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="adopt" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>A one-shot: it answers true once per change and false afterwards.</b> Anything that
    ///     compiles consumes it, which is why <c>TextureGraphView.Republished</c> exists to carry the
    ///     answer across from a caller that compiled before it drew.
    /// </remarks>
    public bool Republish(Action<TextureLibrary> adopt) {
        ArgumentNullException.ThrowIfNull(adopt);

        if (!stale) {
            return false;
        }

        stale = false;
        adopt(TextureNodeLibrary.Publish(assets));

        return true;
    }
}
