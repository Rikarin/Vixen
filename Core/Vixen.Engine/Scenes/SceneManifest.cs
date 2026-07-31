// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Engine.Scenes;

/// <summary>Which scenes a content build shipped, in the order the project listed them.</summary>
/// <remarks>
///     <para>
///         <b>The half of "scenes in build" that survives the editor.</b> The list a person edits is
///         project-relative paths under <c>ProjectSettings/</c> — reviewable in a diff, mergeable when
///         two branches each add a level — and none of that exists in a player. This is what the
///         content build turns it into: the addresses those paths resolved to, which is the only form
///         a shipped binary can act on.
///     </para>
///     <para>
///         ⚠ <b>Written beside the catalog rather than into it.</b> A catalog is an index of addresses
///         and this is a statement about which of them the game opens with — a different question,
///         with a different lifetime, and folding it in would mean a catalog format version for every
///         change to what a build records about itself. It is the same argument the shader bundle
///         makes, one file along; see <c>ContentMount</c>.
///     </para>
///     <para>
///         ⚠ <b>The order is the whole content of this file.</b> The first entry is what
///         <c>AppConfig.StartupScene</c> defaults to, which is what makes moving a level to the top of
///         the Build Settings list mean "this is the one the game opens with". A game that names a
///         scene itself is not overridden by it.
///     </para>
/// </remarks>
[DataContract("SceneManifest")]
public sealed class SceneManifest {
    /// <summary>The version this build writes and reads.</summary>
    public const int Current = 1;

    /// <summary>Which version of this file it is.</summary>
    /// <remarks>
    ///     Read for <see cref="SceneAsset" />'s reason: a manifest in a shipped build may be read by a
    ///     binary older than the content update that replaced it. A newer one is ignored rather than
    ///     thrown on — the game boots into no scene and says so, which beats a player that will not
    ///     start.
    /// </remarks>
    public int Version { get; set; } = Current;

    /// <summary>The addresses of the scenes that ship, first one first.</summary>
    public List<string> Scenes { get; set; } = [];
}
