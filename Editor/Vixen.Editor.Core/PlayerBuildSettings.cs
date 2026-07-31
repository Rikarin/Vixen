// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Editor.Core;

/// <summary>What <c>Build and Run</c> builds: for what, as what, with which scenes, and where to.</summary>
/// <remarks>
///     <para>
///         <b>Doc 20's B7, and it is the project's rather than the user's.</b> Which platform a game
///         ships on and which scenes are in it are facts about the game, so they are committed under
///         <c>ProjectSettings/</c> and arrive with a checkout — the same argument
///         <c>ContentBuildSettings</c> makes about the content target, one level up.
///     </para>
///     <para>
///         ⚠ <b>Here rather than in <c>Vixen.Editor.App</c>, which is where it was, and for
///         <c>ProjectWorkspace</c>'s reason.</b> The content build reads <see cref="Scenes" /> to
///         write the manifest a player boots from, and that build is shared: it is
///         <c>ContentPipeline</c>, called by the editor's Build and Run <i>and</i> by
///         <c>vixen content build</c>. A settings type only the editor could see would have meant the
///         two heads producing different content for one project — a player that opens its level when
///         the editor built it and opens nothing when CI did.
///     </para>
///     <para>
///         ⚠ <b>No <c>[Inspector]</c>, deliberately.</b> <c>BuildSettingsView</c> is this type's
///         drawer and always has been: order, a picker offering only scenes that exist and a column
///         saying which entries no longer resolve are none of them expressible as a property
///         attribute. Attributes that no page drew were the sort of decoration doc 20's "every member
///         has a reader" bar exists to keep out.
///     </para>
///     <para>
///         ⚠ <b><see cref="Target" /> is the <i>player</i>'s and <c>ContentBuildSettings.Target</c>
///         is the <i>content</i>'s, and they are deliberately two settings.</b> They agree in the
///         ordinary case and must be able to disagree: importing for Android while the editor keeps
///         drawing desktop textures is what a team building for a phone from a workstation does all
///         day, and a single field would make "build the Android player" also mean "reimport the whole
///         project as ASTC". The build reads this one and passes it to the content pipeline; the
///         editor's own panels go on reading the other.
///     </para>
///     <para>
///         ⚠ <b>The variant is a property and not a configuration</b>, which is <c>PlayerBuild</c>'s
///         rule and doc 17's: Development is optimised and keeps its profiler, so it is a Release
///         compile with a different <c>VixenVariant</c>. The Build menu's <c>Configuration ▸</c> is a
///         view over this one field rather than a second setting beside it.
///     </para>
/// </remarks>
[DataContract("Build")]
public sealed class PlayerBuildSettings {
    /// <summary>Which platform the player is built for. Empty means this machine.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Which of doc 17's variants it is built as.</summary>
    public string Variant { get; set; } = "Debug";

    /// <summary>Where the artefact goes. Empty means <c>Build/&lt;target&gt;</c> under the project.</summary>
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>The scenes that ship, first one first.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Project-relative asset paths rather than GUIDs, and this is the one place in the
    ///         editor where that is the right way round.</b> Doc 08 chose a GUID over a path
    ///         everywhere a reference has to survive a file being moved — and this is a list a person
    ///         edits, reviews in a diff and merges when two branches each add a level. A column of
    ///         GUIDs is a merge conflict nobody can resolve by reading it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A path here has to reach an <i>addressable</i> scene, and the content build
    ///         refuses when it does not.</b> A build resolves every entry to the address its sidecar
    ///         declares and writes them, in this order, as the <c>SceneManifest</c> beside the
    ///         catalog; the host opens the first of them unless the game named one itself. A scene
    ///         with no address is in no bundle, so the alternative to refusing is a player that starts
    ///         to an empty world — which is the failure this list exists to prevent.
    ///     </para>
    /// </remarks>
    public List<string> Scenes { get; set; } = [];
}
