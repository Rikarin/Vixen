// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Editor.App;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
using Vixen.Editor.Inspector;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.Testing;

namespace Vixen.Editor.Testing;

/// <summary>A whole editor under test: a project on disk, panels, and synthetic input into them.</summary>
/// <remarks>
///     <para>
///         <b>The real application, not a rehearsal of one.</b> Everything worth asserting about an
///         editor — which panel a click landed in, what the inspector was handed, which of the
///         several selections won, whether a saved scene comes back — is wiring that exists in the
///         head and nowhere else. A harness that built its own shell and its own panels would be a
///         harness for a copy of the thing that breaks.
///     </para>
///     <para>
///         ⚠ <b>The frame is <c>EditorHost</c>'s four steps in <c>EditorHost</c>'s order</b>: the
///         shell's tick, the layout, the application's update, the draw. The update has to sit
///         between the layout and the draw because a viewport measures itself in render pixels from
///         a box the layout pass produces — which is why <see cref="UiTest.Updated" /> exists at all.
///         A harness that ran it anywhere else would be one whose passing tests said nothing about
///         the loop that ships.
///     </para>
///     <para>
///         ⚠ <b>No GPU, no window, no platform.</b> The host's first four steps are all above the
///         RHI, which is the same reason <c>--frames N</c> is a smoke test on a machine with no
///         Vulkan. Everything up to and including the draw list runs here; only the submission does
///         not.
///     </para>
///     <para>
///         <b><see cref="Ui" /> is the vocabulary and this type is the nouns.</b> Selectors,
///         waiting-in-frames, assertions and screenshots are all
///         <see cref="Vixen.Ui.Testing.UiTest" />'s and are not reimplemented; what is added here is
///         everything that needs to know what an editor <i>is</i> — a panel id, a command id, a row
///         in the outliner, a restart.
///     </para>
///     <example>
///         <code>
///         using var editor = EditorSession.Start();
///
///         editor.Step("select the crate")
///               .Open("hierarchy")
///               .ExpandAll(editor.Hierarchy)
///               .ClickRow(editor.Hierarchy, "Crate")
///               .Run("edit.delete")
///               .Run("edit.undo");
///
///         editor.Ui.Contains("Crate").ShouldExist();
///         </code>
///     </example>
/// </remarks>
public sealed class EditorSession : IDisposable {
    /// <summary>How many frames the editor is given to settle after something is done to it.</summary>
    /// <remarks>
    ///     ⚠ <b>Two, because the editor's own consequences are a frame behind their causes.</b> A
    ///     command marks the outliner stale; the rebuild happens in the next update; the rows are
    ///     laid out in the pass after that. One frame would make half the assertions in a suite race
    ///     the thing they are about, and the fix would be a stray extra frame in each of them.
    /// </remarks>
    const int SettleFrames = 2;

    readonly EditorSessionOptions options;
    readonly UiTestOptions ui;

    /// <summary>The temporary directory this session made, which it also deletes.</summary>
    /// <remarks>
    ///     Null when the caller supplied its own: a harness that deleted a directory it was handed
    ///     would be one that eats a project somebody wanted to look at afterwards.
    /// </remarks>
    readonly string? owned;

    readonly List<string> trail = [];

    /// <summary>Where this session's contributions go. One per session unless the caller said otherwise.</summary>
    readonly IEditorRegistry extensions;

    EditorApplication editor;
    bool disposed;

    EditorSession(EditorSessionOptions options, string dataDirectory, string? owned) {
        this.options = options;
        this.owned = owned;

        ui = options.Ui ?? new UiTestOptions();
        extensions = options.Extensions ?? new EditorRegistry();
        DataDirectory = dataDirectory;

        (editor, Ui) = Launch();
    }

    /// <summary>Opens an editor.</summary>
    /// <param name="options">How big, where, and how patient. Sensible defaults when omitted.</param>
    /// <returns>The session, already laid out and ready to be clicked.</returns>
    /// <remarks>
    ///     ⚠ <b>A directory per session unless told otherwise.</b> Opening a project writes to it —
    ///     sidecars, a GUID index, the seeded scene, and on the way down a layout and a keymap — so
    ///     two sessions sharing one are two editors scanning the same tree, which is a race rather
    ///     than a test.
    /// </remarks>
    public static EditorSession Start(EditorSessionOptions? options = null) {
        var settings = options ?? new EditorSessionOptions();

        if (settings.DataDirectory is { Length: > 0 } given) {
            Directory.CreateDirectory(given);
            return new EditorSession(settings, given, owned: null);
        }

        var directory = Path.Combine(
            Path.GetTempPath(),
            "vixen-editor-sessions",
            Guid.NewGuid().ToString("N")
        );

        Directory.CreateDirectory(directory);
        return new EditorSession(settings, directory, directory);
    }

    /// <summary>The application itself, for the suites that drive what no panel exposes.</summary>
    /// <remarks>
    ///     ⚠ <b>Internal, and it stays internal.</b> The whole point of this harness is that a test
    ///     says what a person does — click a row, run a command — rather than what a field holds, and
    ///     everything that <i>can</i> be said that way is said that way above. What this is for is
    ///     the handful of things a person does through a surface the harness has no vocabulary for
    ///     yet: opening a second scene, reading the world settings. A public one would be an
    ///     invitation to write the other kind of test.
    /// </remarks>
    internal EditorApplication Editor => editor;

    /// <summary>The document harness underneath: selectors, assertions, frames, pictures.</summary>
    /// <remarks>
    ///     ⚠ <b>A different object after <see cref="Restart" />.</b> The shell owns the document and
    ///     a restart replaces the shell, so a test that held this in a local across one would be
    ///     driving the interface of an editor that has closed.
    /// </remarks>
    public UiTest Ui { get; private set; }

    /// <summary>Where the user's layouts, keymap and theme live.</summary>
    /// <remarks>What survives a <see cref="Restart" />, which is the whole point of naming it.</remarks>
    public string DataDirectory { get; }

    /// <summary>The project's root on disk.</summary>
    public string ProjectRoot => Project.Paths.Root;

    /// <summary>The steps a scenario has announced, in order.</summary>
    public IReadOnlyList<string> Trail => trail;

    /// <summary>The shell: commands, keymap, menus, docking, dialogs, notifications.</summary>
    public EditorShell Shell => editor.Shell;

    /// <summary>The application itself, for the suite that lives beside it.</summary>
    /// <remarks>
    ///     ⚠ <b>Internal, and it stays that way.</b> Everything this harness publishes is a public
    ///     type belonging to the assembly that owns it — a panel, a document, a command registry —
    ///     which is what lets a plugin's tests use it. The head's own internals are for the head's
    ///     own suite, which is already a friend of it.
    /// </remarks>
    internal EditorApplication Application => editor;

    /// <summary>The interface tree, which is the shell's.</summary>
    public UiDocument Document => editor.Shell.Document;

    /// <summary>The project the editor has open.</summary>
    public EditorProject Project => editor.Project;

    /// <summary>The scene it is editing.</summary>
    public SceneDocument Scene => editor.Scene;

    /// <summary>The focused pane, or <see langword="null" /> while the scene panel is closed.</summary>
    public SceneViewport? Viewport => editor.Viewport;

    /// <summary>Every pane of the scene panel, in reading order. Empty while it is closed.</summary>
    /// <remarks>
    ///     What a test of a split layout asserts against: the panes have their own cameras, their own
    ///     view modes and their own show flags, and the point of the split is that they disagree.
    /// </remarks>
    public IReadOnlyList<SceneViewport> Viewports => editor.Viewports;

    /// <summary>The outliner's tree.</summary>
    /// <remarks>Opens the panel if it is not already up, because a closed panel has no tree at all.</remarks>
    public TreeView Hierarchy => Tree("hierarchy");

    /// <summary>The content browser's tree.</summary>
    public TreeView Assets => Tree("project");

    /// <summary>The inspector.</summary>
    public InspectorView Inspector {
        get {
            Open("inspector");

            return Find<InspectorView>(Panel("inspector"))
                ?? throw Fail("the inspector panel has no inspector in it");
        }
    }

    /// <summary>Names what the next few commands are for, so a failure says where it was.</summary>
    /// <param name="what">A short phrase in the present tense — "import the crate", "reopen".</param>
    /// <returns>This, so it reads as part of a chain.</returns>
    /// <remarks>
    ///     ⚠ <b>What a scenario test needs and an assertion message cannot supply.</b> Doc 11's
    ///     scenario is eight verbs long — create, import, drag, edit, undo, save, reopen, assert —
    ///     and "expected 1, found 0" on the last one is a sentence about the eighth verb that tells
    ///     you nothing about which of the first seven did not happen. Every failure this type raises
    ///     carries the trail.
    /// </remarks>
    public EditorSession Step(string what) {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);

        trail.Add(what);
        return this;
    }

    /// <summary>Runs one frame, the way the host does.</summary>
    public void Frame() => Ui.Frame();

    /// <summary>Runs several.</summary>
    /// <param name="count">How many.</param>
    public void Frames(int count) => Ui.Frames(count);

    /// <summary>Runs enough frames for whatever just happened to be visible.</summary>
    /// <returns>This.</returns>
    public EditorSession Settle() {
        Ui.Frames(SettleFrames);
        return this;
    }

    /// <summary>Brings a panel forward, opening it if it is closed.</summary>
    /// <param name="id">Which panel.</param>
    /// <returns>This.</returns>
    /// <remarks>
    ///     ⚠ <b>Needed before a click, not merely before an assertion.</b> The outliner and the
    ///     content browser are tabs of one group in the default arrangement, so the one that is not
    ///     in front has no size at all — and a click aimed at the middle of a zero-sized row lands on
    ///     whatever is behind it, silently.
    /// </remarks>
    public EditorSession Open(string id) {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        if (Shell.Workspace.Open(id) is null) {
            throw Fail($"There is no panel called '{id}'. Registered: {Registered()}.");
        }

        return Settle();
    }

    /// <summary>Closes a panel.</summary>
    /// <param name="id">Which panel.</param>
    /// <returns>This.</returns>
    public EditorSession Close(string id) {
        Shell.Workspace.Close(id);
        return Settle();
    }

    /// <summary>Every panel that is open, in document order.</summary>
    /// <remarks>
    ///     Includes the ones registered on demand — an asset opened from the browser gets a panel of
    ///     its own named <c>asset.&lt;guid&gt;</c>, which is the only way to find it, because the
    ///     workspace was never told about it in advance.
    /// </remarks>
    public IReadOnlyList<DockPanel> Panels => [.. Descendants(Document.Root).OfType<DockPanel>()];

    /// <summary>The panel with an id, which must be open.</summary>
    /// <param name="id">Which panel.</param>
    /// <returns>The panel.</returns>
    public DockPanel Panel(string id) =>
        Descendants(Document.Root).OfType<DockPanel>().FirstOrDefault(panel => panel.Id == id)
        ?? throw Fail($"The '{id}' panel is not open.");

    /// <summary>The tree inside a panel, by the id the arrangement knows the panel by.</summary>
    /// <param name="id">Which panel.</param>
    /// <returns>Its tree.</returns>
    /// <remarks>
    ///     By panel rather than by type: both of the trees a suite reaches for are
    ///     <see cref="TreeView" />s in the same document, and "the first one" is an answer that
    ///     depends on which side of the window the layout put them on.
    /// </remarks>
    public TreeView Tree(string id) {
        Open(id);

        return Find<TreeView>(Panel(id)) ?? throw Fail($"The '{id}' panel has no tree in it.");
    }

    /// <summary>The one control of a type inside a panel, opening the panel if it is closed.</summary>
    /// <typeparam name="T">What to look for.</typeparam>
    /// <param name="id">Which panel.</param>
    /// <returns>The control.</returns>
    /// <remarks>
    ///     ⚠ <b>By panel rather than by type, for the reason <see cref="Tree" /> gives.</b> A
    ///     document with four inspectors and three grids in it has no useful answer to "the first
    ///     one" — it depends on which side of the window the layout put them. Every suite that
    ///     reached into a panel was writing its own recursive walk to do this; there is one now.
    /// </remarks>
    public T Control<T>(string id) where T : UiElement {
        Open(id);

        return Find<T>(Panel(id)) ?? throw Fail($"The '{id}' panel has no {typeof(T).Name} in it.");
    }

    /// <summary>The one VXML component of a type inside a panel, opening the panel if it is closed.</summary>
    /// <typeparam name="T">What to look for.</typeparam>
    /// <param name="id">Which panel.</param>
    /// <returns>The component.</returns>
    /// <remarks>
    ///     ⚠ <b><see cref="Control{T}" />'s counterpart, and it needs to exist because a component
    ///     is not an element.</b> A panel written in C# <i>is</i> the control a suite reaches for; a
    ///     panel written in VXML is an object beside the elements it drew, so the walk asks the
    ///     document which component each element is the host of rather than testing the element's
    ///     own type.
    /// </remarks>
    public T Component<T>(string id) where T : Vixen.Ui.Composition.Component {
        Open(id);

        return Mounted<T>(Panel(id)) ?? throw Fail($"The '{id}' panel has no {typeof(T).Name} in it.");
    }

    T? Mounted<T>(UiElement element) where T : Vixen.Ui.Composition.Component {
        if (Document.ComponentAt(element) is T match) {
            return match;
        }

        foreach (var child in element.Children) {
            if (Mounted<T>(child) is { } found) {
                return found;
            }
        }

        return null;
    }

    /// <summary>Everything inside a panel, as a subject to select within.</summary>
    /// <param name="id">Which panel.</param>
    /// <returns>A subject over the panel itself; chain <c>Find</c> from it.</returns>
    /// <remarks>
    ///     ⚠ <b>How a selector is kept honest in a document with four of everything in it.</b>
    ///     <c>Ui.Get("search-box")</c> in an editor matches the outliner's filter, the browser's
    ///     search and the inspector's — and picks whichever the layout happened to put first.
    /// </remarks>
    public UiSubject In(string id) {
        var panel = Panel(id);

        return Ui.Get($"dock-panel").Where(element => ReferenceEquals(element, panel), $"is the '{id}' panel");
    }

    /// <summary>Runs a command the way a menu line does, and waits for the consequences.</summary>
    /// <param name="command">Its id.</param>
    /// <returns>This.</returns>
    /// <remarks>
    ///     ⚠ <b>Refuses a command that is registered but disabled, rather than reporting false.</b>
    ///     <c>CommandRegistry.Execute</c> answers whether it ran, and a scenario that ignored the
    ///     answer would carry on and fail four steps later on a consequence that never happened. The
    ///     two failures are told apart in the message because they have different causes: a missing
    ///     id is a typo or an unregistered command, and a disabled one is the editor being in a state
    ///     the test did not expect.
    /// </remarks>
    public EditorSession Run(string command) {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        if (!Shell.Commands.TryGet(command, out var found)) {
            throw Fail($"There is no command called '{command}'.");
        }

        if (!Shell.Commands.CanExecute(found)) {
            throw Fail(
                $"'{command}' is registered but not enabled"
                + (Shell.Commands.IsInScope(found)
                    ? " — its enablement predicate says no."
                    : $" — it belongs to the '{found.Context}' context and the editor is in '{Shell.Context}'.")
            );
        }

        Shell.Commands.Execute(command);
        return Settle();
    }

    /// <summary>Whether a command would run right now.</summary>
    /// <param name="command">Its id.</param>
    /// <returns>Whether it is registered, in scope and enabled.</returns>
    public bool CanRun(string command) => Shell.Commands.CanExecute(command);

    /// <summary>Opens a menu on the bar and chooses a line, by clicking both.</summary>
    /// <param name="menu">What the menu is called — "File", "Entity".</param>
    /// <param name="path">The line, and any submenus above it, in order.</param>
    /// <returns>This.</returns>
    /// <remarks>
    ///     ⚠ <b>Through the pointer rather than through the registry, which is the whole point.</b>
    ///     Calling <see cref="Run" /> proves a command works; this proves it is <i>reachable</i> —
    ///     that the line exists, is spelled the way the test says, is not disabled, and is not
    ///     underneath something that never opens. Doc 20's first bar is "nothing they reach for is
    ///     missing", and that is a claim about the menu rather than about the command.
    /// </remarks>
    public EditorSession Menu(string menu, params string[] path) {
        ArgumentException.ThrowIfNullOrWhiteSpace(menu);
        ArgumentNullException.ThrowIfNull(path);

        if (path.Length == 0) {
            throw new ArgumentException("Name the line to choose, not only the menu.", nameof(path));
        }

        var bar = Shell.MenuBar.Bar;

        var entry = bar.Items.FirstOrDefault(item => item.Label == menu)
            ?? throw Fail(
                $"There is no '{menu}' menu on the bar. It has: "
                + string.Join(", ", bar.Items.Select(item => item.Label ?? "?"))
                + "."
            );

        Click(entry);

        var open = entry.Menu;

        for (var index = 0; index < path.Length; index++) {
            var label = path[index];

            var line = open.Items.FirstOrDefault(item => item.Label == label)
                ?? throw Fail(
                    $"The '{menu}' menu has no line called '{label}'. It has: "
                    + string.Join(", ", open.Items.Select(item => item.Label ?? "?"))
                    + "."
                );

            if (index == path.Length - 1) {
                if (line.Disabled) {
                    throw Fail($"'{menu} ▸ {string.Join(" ▸ ", path)}' is on the menu but disabled.");
                }

                Click(line);
                return Settle();
            }

            // A submenu opens on hover, the way it does for a pointer walking down a menu — clicking
            // one would be a gesture no menu anywhere responds to.
            Hover(line);

            open = line.Submenu
                ?? throw Fail($"'{label}' is a line on the '{menu}' menu rather than a submenu.");
        }

        return Settle();
    }

    /// <summary>The realised row showing some text.</summary>
    /// <param name="tree">Which tree.</param>
    /// <param name="text">What the row says.</param>
    /// <returns>The row.</returns>
    /// <remarks>
    ///     ⚠ <b>Realised, and the parked rows are skipped.</b> The trees virtualise, so the control
    ///     keeps a pool of rows off screen with stale text in them — and a click at the centre of one
    ///     of those lands wherever it happens to be parked.
    /// </remarks>
    public TreeRow Row(TreeView tree, string text) {
        ArgumentNullException.ThrowIfNull(tree);

        return tree.Rows.FirstOrDefault(row => !row.HasClass("parked") && row.Node?.Text == text)
            ?? throw Fail(
                $"No row saying '{text}' is on screen. Showing: "
                + string.Join(", ", tree.Rows.Where(row => !row.HasClass("parked")).Select(row => row.Node?.Text ?? "?"))
                + "."
            );
    }

    /// <summary>Clicks the row showing some text.</summary>
    /// <param name="tree">Which tree.</param>
    /// <param name="text">What the row says.</param>
    /// <param name="modifiers">Shift or the platform's own, for a range or an add.</param>
    /// <returns>This.</returns>
    public EditorSession ClickRow(TreeView tree, string text, ModifierKeys modifiers = ModifierKeys.None) {
        var row = Row(tree, text);

        if (modifiers != ModifierKeys.None) {
            Ui.Hold(modifiers);
        }

        try {
            Click(row);
        } finally {
            Ui.Hold(ModifierKeys.None);
        }

        return Settle();
    }

    /// <summary>Double-clicks it, which renames in the outliner and opens in the browser.</summary>
    /// <param name="tree">Which tree.</param>
    /// <param name="text">What the row says.</param>
    /// <returns>This.</returns>
    public EditorSession DoubleClickRow(TreeView tree, string text) {
        var (x, y) = Centre(Row(tree, text));

        // ⚠ Two presses with no rest between them. The gesture recogniser counts taps in a row, and
        // a control acting on the second one sees nothing from two clicks a second apart — which
        // every user gets right by being fast and every test gets wrong by being slow.
        Ui.At(x, y).DoubleClick();
        return Settle();
    }

    /// <summary>Right-clicks it, which is what opens a context menu on it.</summary>
    /// <param name="tree">Which tree.</param>
    /// <param name="text">What the row says.</param>
    /// <returns>This.</returns>
    public EditorSession RightClickRow(TreeView tree, string text) {
        var (x, y) = Centre(Row(tree, text));

        Ui.At(x, y).RightClick();
        return Settle();
    }

    /// <summary>Opens every node in a tree, so that a deep row is on screen to be clicked.</summary>
    /// <param name="tree">Which tree.</param>
    /// <returns>This.</returns>
    /// <remarks>
    ///     Both trees open with only their roots expanded, which is right for a person and a trap for
    ///     a test: a nested row is not realised, and a click aimed at one that is not on screen lands
    ///     on whatever is.
    /// </remarks>
    public EditorSession ExpandAll(TreeView tree) {
        ArgumentNullException.ThrowIfNull(tree);

        foreach (var node in Nodes(tree.Root).ToList()) {
            if (node.HasChildren) {
                tree.Expand(node);
            }
        }

        return Settle();
    }

    /// <summary>Every node in a tree, depth first.</summary>
    /// <param name="tree">Which tree.</param>
    /// <returns>The nodes, not including the invisible root.</returns>
    public static IEnumerable<TreeNode> NodesOf(TreeView tree) {
        ArgumentNullException.ThrowIfNull(tree);

        return Nodes(tree.Root);
    }

    /// <summary>What a tree is showing, top to bottom.</summary>
    /// <param name="tree">Which tree.</param>
    /// <returns>The labels.</returns>
    public static IReadOnlyList<string> Labels(TreeView tree) =>
        [.. NodesOf(tree).Select(node => node.Text ?? string.Empty)];

    /// <summary>Answers whatever dialog is on screen by pressing one of its buttons.</summary>
    /// <param name="label">What the button says — "Save", "Cancel", "Don't Save".</param>
    /// <returns>This.</returns>
    /// <remarks>
    ///     ⚠ <b>By clicking, which is why <c>DialogService</c> draws rather than going native.</b> A
    ///     modal that is an OS window cannot be screenshotted by the golden suite and cannot be
    ///     driven from here at all — the decision is recorded in doc 20's A2 and this is what it
    ///     buys.
    /// </remarks>
    public EditorSession Answer(string label) {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        if (Shell.Dialogs.Current is not { } dialog) {
            throw Fail($"Nothing asked anything, so there is no '{label}' to press.");
        }

        var button = Descendants(dialog).OfType<Button>().FirstOrDefault(candidate => candidate.Label == label)
            ?? throw Fail(
                $"The dialog has no '{label}' button. It has: "
                + string.Join(", ", Descendants(dialog).OfType<Button>().Select(candidate => candidate.Label ?? "?"))
                + "."
            );

        Click(button);

        // ⚠ Three settles rather than one. The answer completes from `Pump` on the next tick rather
        // than from the click handler — deliberately, so that a continuation cannot run inside the
        // event that closed the dialog — so the consequence of pressing Save is two frames behind
        // the press.
        Settle();
        return Settle();
    }

    /// <summary>Whether the editor is asking something.</summary>
    public bool IsAsking => Shell.Dialogs.IsOpen;

    /// <summary>Whether the editor has been asked to close and agreed to.</summary>
    public bool IsClosing => editor.IsClosing;

    /// <summary>Where the editor has been asked to reopen, or <see langword="null" />.</summary>
    /// <remarks>
    ///     What <c>Program</c> reads after the loop returns. A test asserts on it for the same reason
    ///     it asserts on <see cref="IsClosing" />: opening another project is a <i>request</i>, and
    ///     what a harness can check is that the request was made and survived the prompt.
    /// </remarks>
    public string? PendingProject => editor.PendingProject;

    /// <summary>The projects this editor has opened, newest first, as paths and times.</summary>
    /// <remarks>
    ///     Flattened to primitives rather than handed out as the application's own record, because
    ///     that type is internal to the head and a public member of this class cannot return one.
    /// </remarks>
    public IReadOnlyList<(string Path, DateTime Opened)> RecentProjects =>
        [.. editor.Recent.Entries.Select(entry => (entry.Path, entry.Opened))];

    /// <summary>Asks the editor to close this project and reopen over another.</summary>
    /// <param name="root">The new project's root.</param>
    /// <returns>This.</returns>
    /// <remarks>
    ///     ⚠ <b>An ask, exactly as <see cref="RequestClose" /> is.</b> With unsaved work it puts the
    ///     save prompt up and changes nothing until <see cref="Answer" /> presses one of its buttons
    ///     — which is the case doc 20's A2 names beside closing the window.
    /// </remarks>
    public EditorSession RequestProject(string root) {
        editor.RequestProject(root);
        return Settle();
    }

    /// <summary>Asks the editor to close, the way the window's close button does.</summary>
    /// <returns>This.</returns>
    /// <remarks>
    ///     ⚠ <b>An <i>ask</i>, which is the whole of what the request is for.</b> With unsaved work
    ///     it puts a dialog up and closes nothing until <see cref="Answer" /> presses one of its
    ///     buttons; with none it closes at once. A harness that disposed the editor instead would
    ///     prove nothing about the one path that loses an afternoon.
    /// </remarks>
    public EditorSession RequestClose() {
        editor.RequestClose();
        return Settle();
    }

    /// <summary>Clicks the middle of an element.</summary>
    /// <param name="element">What to click.</param>
    /// <returns>This.</returns>
    /// <remarks>
    ///     For the elements a selector cannot name because there are four of them and only their
    ///     identity tells them apart — a particular dock panel, a particular row. Everything with a
    ///     name is better reached through <see cref="Ui" />.
    /// </remarks>
    public EditorSession Click(UiElement element) {
        ArgumentNullException.ThrowIfNull(element);

        var (x, y) = Centre(element);

        Ui.At(x, y).Click();
        return Settle();
    }

    /// <summary>Takes a picture and compares it with the committed one.</summary>
    /// <param name="name">What the picture is called.</param>
    /// <returns>This.</returns>
    public EditorSession Screenshot(string name) {
        Ui.Screenshot(name);
        return this;
    }

    /// <summary>Closes the editor and opens it again over the same directories.</summary>
    /// <returns>This, with a new <see cref="Ui" />, <see cref="Shell" /> and <see cref="Scene" />.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The step doc 11's scenario ends on, and the one nothing else can fake.</b> "Save,
    ///         reopen, assert" is a claim about what reached the disk, and an in-process reload that
    ///         kept the same objects alive would pass for a scene that was never written. Everything
    ///         is torn down: the world, the document, the plugins, the asset database.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It persists first, because that is what closing does.</b> The host writes the
    ///         layout and the keymap on the way down rather than on every change — a splitter drag
    ///         raises a layout change per mouse-move — so a restart that skipped it would prove the
    ///         arrangement is <i>not</i> restored, which is the opposite of the truth.
    ///     </para>
    /// </remarks>
    public EditorSession Restart() {
        ObjectDisposedException.ThrowIf(disposed, this);

        editor.Persist();
        editor.Dispose();

        (editor, Ui) = Launch();
        return this;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The editor and nothing else.</b> It disposes the shell, which owns the document —
    ///     and <c>UiTest.Adopt</c>'s own remarks warn that adopting is not owning, so disposing the
    ///     harness too would take the document down twice.
    /// </remarks>
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        editor.Dispose();

        if (owned is null) {
            return;
        }

        try {
            Directory.Delete(owned, recursive: true);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            // A temp directory that would not go is not a failed test.
        }
    }

    /// <summary>A failure that carries the scenario's trail and the interface it happened in.</summary>
    /// <param name="message">What went wrong.</param>
    /// <returns>The exception, to throw.</returns>
    /// <remarks>
    ///     Returned rather than thrown, so that the call site reads <c>throw Fail(…)</c> and the
    ///     compiler knows control does not continue past it.
    /// </remarks>
    public EditorSessionException Fail(string message) {
        var text = new StringBuilder(message);

        if (trail.Count > 0) {
            text.AppendLine().AppendLine().AppendLine("Steps:");

            for (var index = 0; index < trail.Count; index++) {
                text.Append("  ").Append(index + 1).Append(". ").AppendLine(trail[index]);
            }
        }

        text.AppendLine().AppendLine("Interface:").Append(Ui.Tree());

        return new EditorSessionException(text.ToString());
    }

    /// <summary>Builds an application and a harness over its document, in the host's own order.</summary>
    (EditorApplication Editor, UiTest Ui) Launch() {
        var application = new EditorApplication(
            options.Width,
            options.Height,
            DataDirectory,
            options.ProjectRoot,
            services: null,

            // ⚠ This session's own, so a plugin one test loads is invisible to the twenty running
            // beside it. See `EditorSessionOptions.Extensions`. A restart keeps it, because the
            // contributions of the editor that closed are exactly what the one reopening should see
            // again — the same bargain the data directory makes.
            extensions
        );

        if (options.InstallFonts && !Fonts.Install(application.Shell.Document)) {
            application.Dispose();

            throw new EditorSessionException(
                "No usable font was found on this machine, so every label would measure zero wide and "
                + "every click would land somewhere a person's would not. Set "
                + $"{nameof(EditorSessionOptions)}.{nameof(EditorSessionOptions.InstallFonts)} to false "
                + "only if the suite asserts nothing that involves hitting anything."
            );
        }

        // ⚠ After the font, because how a shortcut is written depends on what the face can draw —
        // the same second decision `EditorHost` makes, and for the same reason. A harness that
        // skipped it would render menus a way the product never does.
        application.Shell.RefreshShortcutFormat();

        var test = UiTest.Adopt(application.Shell.Document, ui);

        // The host's four steps. `Ticked` runs before the layout and `Updated` after it, which is
        // where the shell's tick and the application's update respectively belong.
        test.Ticked += () => application.Shell.Tick(test.Now, ui.FrameDelta);
        test.Updated += () => application.Update(ui.FrameDelta);

        test.Frames(SettleFrames + 1);

        return (application, test);
    }

    string Registered() => string.Join(", ", Shell.Workspace.Panels.Select(panel => panel.Id));

    void Hover(UiElement element) {
        var (x, y) = Centre(element);

        Ui.At(x, y).Hover();
    }

    static (float X, float Y) Centre(UiElement element) =>
        (element.Bounds.X + (element.Bounds.Width * 0.5f), element.Bounds.Y + (element.Bounds.Height * 0.5f));

    static IEnumerable<TreeNode> Nodes(TreeNode node) {
        foreach (var child in node.Children) {
            yield return child;

            foreach (var descendant in Nodes(child)) {
                yield return descendant;
            }
        }
    }

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }

    static T? Find<T>(UiElement element) where T : UiElement {
        if (element is T match) {
            return match;
        }

        foreach (var child in element.Children) {
            if (Find<T>(child) is { } found) {
                return found;
            }
        }

        return null;
    }
}
