// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Inspector;

namespace Vixen.Editor.App;

/// <summary>What a project is called, by whom, and at what version.</summary>
/// <remarks>
///     <para>
///         <b>The first settings asset the editor ships, and the reason it is one rather than a
///         dialog is doc 11's claim that "adding a project setting is declaring a type".</b> It
///         carries <c>[DataContract]</c> so <c>ProjectSettingsStore</c> can read and write it with
///         the same YAML binder that reads a <c>.meta</c> file, and <c>[Inspector]</c> so the
///         settings window draws it with the same rows that draw a material. Neither attribute knows
///         about the other and this type does nothing except hold four strings.
///     </para>
///     <para>
///         ⚠ <b>Every member here has a reader, which is the bar a shipped setting has to clear.</b>
///         <see cref="ProductName" /> is what the title bar says when it is set — the directory's
///         name is a fallback rather than the answer — and it is what About reports. A settings page
///         of fields nothing reads is a page that teaches people the settings do not work.
///     </para>
/// </remarks>
[DataContract("ProjectInfo")]
public sealed class ProjectInfoSettings {
    /// <summary>What the game is called, which need not be the folder's name.</summary>
    [Inspector]
    [Tooltip("What the title bar and About say. Empty means the project directory's own name.")]
    public string ProductName { get; set; } = string.Empty;

    /// <summary>Who is making it.</summary>
    [Inspector]
    [Tooltip("Shown in About, and what a packaged build is attributed to.")]
    public string Company { get; set; } = string.Empty;

    /// <summary>Which version this is.</summary>
    [Inspector]
    [Tooltip("The project's own version. The editor does not interpret it.")]
    public string Version { get; set; } = "0.1.0";
}

/// <summary>What the content pipeline imports and packs for.</summary>
/// <remarks>
///     ⚠ <b>Empty means "this machine", which is what <c>ContentTasks</c> has always done.</b> A
///     content build is target-specific — the same texture is BC7 on a desktop and ASTC on a phone —
///     so there is no neutral answer, and the one that surprises nobody is the computer the editor is
///     running on. What this adds is the ability to say otherwise, which is what a team building for
///     a console from a workstation needs.
///     ⚠ <b>This is the <i>editor's</i> content target and not the player's.</b>
///     <see cref="PlayerBuildSettings.Target" /> is what a build ships for, and the two are separate
///     on purpose — see that type for why one field would be worse.
/// </remarks>
[DataContract("ContentBuild")]
public sealed class ContentBuildSettings {
    /// <summary>Which runtime target the import and the build are for.</summary>
    [Inspector]
    [Tooltip("The content target: windows-x64, macos-arm64, and so on. Empty builds for this machine.")]
    public string Target { get; set; } = string.Empty;
}

/// <summary>What <c>Build and Run</c> builds: for what, as what, with which scenes, and where to.</summary>
/// <remarks>
///     <para>
///         <b>Doc 20's B7, and it is the project's rather than the user's.</b> Which platform a game
///         ships on and which scenes are in it are facts about the game, so they are committed under
///         <c>ProjectSettings/</c> and arrive with a checkout — the same argument
///         <see cref="ContentBuildSettings" /> makes about the content target, one level up.
///     </para>
///     <para>
///         ⚠ <b><see cref="Target" /> is the <i>player</i>'s and <see cref="ContentBuildSettings.Target" />
///         is the <i>content</i>'s, and they are deliberately two settings.</b> They agree in the
///         ordinary case and must be able to disagree: importing for Android while the editor keeps
///         drawing desktop textures is what a team building for a phone from a workstation does all
///         day, and a single field would make "build the Android player" also mean "reimport the whole
///         project as ASTC". The build reads this one and passes it to the content pipeline; the
///         editor's own panels go on reading the other.
///     </para>
///     <para>
///         ⚠ <b>The variant is a property and not a configuration</b>, which is
///         <see cref="Assets.Content.PlayerBuild" />'s rule and doc 17's: Development is optimised and
///         keeps its profiler, so it is a Release compile with a different <c>VixenVariant</c>. The
///         Build menu's <c>Configuration ▸</c> is a view over this one field rather than a second
///         setting beside it.
///     </para>
/// </remarks>
[DataContract("Build")]
public sealed class PlayerBuildSettings {
    /// <summary>Which platform the player is built for. Empty means this machine.</summary>
    [Inspector]
    [Tooltip("The platform the player is published for. Empty builds for the machine the editor is on.")]
    public string Target { get; set; } = string.Empty;

    /// <summary>Which of doc 17's variants it is built as.</summary>
    [Inspector]
    [Tooltip("Debug, Development, Release or Server. Debug is the only one compiled unoptimised.")]
    public string Variant { get; set; } = "Debug";

    /// <summary>Where the artefact goes. Empty means <c>Build/&lt;target&gt;</c> under the project.</summary>
    [Inspector]
    [Tooltip("Where the published player is written. Empty is Build/<target> inside the project.")]
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
    ///         ⚠ <b>What reads it today is the build's own check, and what will read it at boot does
    ///         not exist yet.</b> A build reports every entry that names nothing in the asset
    ///         database, which is the failure this list actually has — a scene renamed outside the
    ///         editor, or deleted by somebody else. Doc 17's <c>AppConfig.StartupScene</c> is what
    ///         will make the first entry mean "this is what the game opens with"; until it is
    ///         written, the panel says so rather than implying the order does something.
    ///     </para>
    /// </remarks>
    [Inspector]
    [Tooltip("The scenes that ship with the player, in order.")]
    public List<string> Scenes { get; set; } = [];
}

/// <summary>The editor's own preferences, which belong to the user rather than to the project.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>What is <i>not</i> here is the point.</b> Doc 20's A4 is explicit that the three
///         scene-navigation preferences stay as ticked commands and the preferences window shows the
///         <i>same</i> commands rather than a second copy of their state — two writers to one setting
///         is how a preferences window and a menu tick come to disagree. The same rule keeps the
///         theme out of this type: <c>view.toggle-theme</c> owns it, and the Appearance page draws
///         that command.
///     </para>
///     <para>
///         So what is here is the set that has no command, and every one of them has a reader:
///         <see cref="ExternalEditor" /> is what a double-clicked console line opens,
///         <see cref="UndoDepth" /> is every command stack's capacity,
///         <see cref="RestoreOpenDocuments" /> decides whether a saved arrangement reopens the asset
///         editors it names, and <see cref="RecentProjects" /> bounds the startup browser's list.
///     </para>
/// </remarks>
[DataContract("EditorPreferences")]
public sealed class EditorPreferences {
    /// <summary>The command line that opens a source file, or empty for the file manager.</summary>
    /// <remarks>
    ///     ⚠ <b>Doc 20's A7 asks the console for "double-click-to-open-source through the
    ///     external-tool setting", and this is that setting.</b> <c>{file}</c> and <c>{line}</c> are
    ///     substituted; anything else is passed through. Left empty, a double-click reveals the
    ///     project folder, which is what it did before there was anywhere to say otherwise.
    /// </remarks>
    [Inspector]
    [Tooltip("How to open a source file: a program and its arguments, with {file} and {line} substituted.")]
    public string ExternalEditor { get; set; } = string.Empty;

    /// <summary>How many steps every undo history keeps.</summary>
    /// <remarks>
    ///     ⚠ <b>Lowering it drops the oldest entries immediately</b>, which <c>CommandStack.Capacity</c>
    ///     says and which is worth knowing before it is typed: if the last-saved point was among
    ///     them the document stays dirty for good, because there is no longer a sequence of undos
    ///     that reaches what is on disk.
    /// </remarks>
    [Inspector]
    [Tooltip("How many steps Undo remembers, per document. Lowering it forgets the oldest immediately.")]
    [Range(8, 4096)]
    public int UndoDepth { get; set; } = 256;

    /// <summary>Whether a restored arrangement reopens the asset editors it names.</summary>
    [Inspector]
    [Tooltip("Whether reopening the editor also reopens the assets that were open when it closed.")]
    public bool RestoreOpenDocuments { get; set; } = true;

    /// <summary>How many projects the startup browser lists.</summary>
    [Inspector]
    [Tooltip("How many entries Open Recent and the startup browser keep.")]
    [Range(1, 40)]
    public int RecentProjects { get; set; } = 12;

    /// <summary>Whether the Project panel opens as a grid of tiles rather than as a tree.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Deliberately not <c>[Inspector]</c>, and it is the only member here that is
    ///         not.</b> The button in the browser's own filter bar is the way this is changed, and a
    ///         settings row over the same state would be the two-writers mistake doc 20 names — with
    ///         the added indignity that the row and the button are in two different panels, so they
    ///         can be looked at disagreeing.
    ///     </para>
    ///     <para>
    ///         It is here rather than in the layout file because it is a preference and not an
    ///         arrangement: a user who works in tiles wants tiles in every project, and the layout is
    ///         per-window. It persisted nowhere at all until now, which meant the toggle was reset by
    ///         every restart <i>and</i> by closing the panel — a panel's factory runs again on reopen,
    ///         and the button was built unchecked each time.
    ///     </para>
    /// </remarks>
    public bool ProjectGridView { get; set; }

    /// <summary>How big the Project panel's tiles are, by the name <c>AssetGrid.TileSizes</c> gives.</summary>
    /// <remarks>
    ///     ⚠ <b>A name and not a number, which is what makes it survive a version that changes the
    ///     steps.</b> A tile is a width, a height and a glyph size that have to agree; storing the
    ///     width alone would restore a 152-pixel tile with a 40-pixel glyph in it the day the sizes
    ///     are re-tuned. A name nothing answers to falls back to the default rather than failing.
    ///     <para>
    ///         Not <c>[Inspector]</c>, for <see cref="ProjectGridView" />'s reason: the dropdown in
    ///         the browser's own filter bar is how it is changed.
    ///     </para>
    /// </remarks>
    public string ProjectTileSize { get; set; } = AssetGrid.DefaultTileSize;

    /// <summary>What order the inspector's component foldouts are shown in, by component name.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A preference and not a fact about any entity, which is why it is here.</b> An
    ///         archetype is a set — the ECS has no notion of a component being third — so there is
    ///         nowhere in a scene file to record that somebody dragged Light above Mesh Shape.
    ///         <c>ComponentsView.Order</c> says the rest.
    ///     </para>
    ///     <para>
    ///         Not <c>[Inspector]</c>, for <see cref="ProjectGridView" />'s reason: the drag in the
    ///         panel is how it is changed, and a settings row over the same list would be a second
    ///         writer that can be looked at disagreeing. A name in here that no component answers to
    ///         is harmless and is kept — a plugin that is not loaded today may be tomorrow.
    ///     </para>
    /// </remarks>
    public List<string> ComponentOrder { get; set; } = [];
}
