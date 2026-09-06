// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Microsoft.Extensions.Logging;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Composition;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.Reactive;

namespace Vixen.Editor.Ui;

/// <summary>The editor's window: a menu bar, a toolbar, a docking workspace and a status bar.</summary>
/// <remarks>
///     <para>
///         <b>Everything in this assembly, wired together once.</b> The pieces are usable on their
///         own — a registry with no shell, a workspace with no palette — and this is the arrangement
///         the editor uses, so that <c>Vixen.Editor.App</c> is a window, a device and a frame loop
///         rather than three hundred lines of assembly.
///     </para>
///     <para>
///         ⚠ <b>No platform, no device, no window.</b> A shell is a <see cref="UiDocument" /> and
///         nothing else — the same bargain <c>Samples/02-HelloUi</c>'s shell makes, and the reason
///         the whole of the editor's chrome is testable headless: doc 11's "headless editor host
///         driving synthetic input against the real element tree" needs this class to be
///         constructible without a GPU, and it is.
///     </para>
///     <para>
///         ⚠ <b>It registers the view commands and no others.</b> Save, Undo and Open belong to
///         something that has a project; the shell's default menu model names them anyway and the
///         menu builder skips what nothing has registered — so an application that registers
///         <c>file.save</c> gets it in the File menu, in the palette and on its shortcut without
///         telling the shell anything.
///     </para>
/// </remarks>
public sealed class EditorShell : IDisposable {
    /// <summary>How many frames the status bar's frame time is averaged over.</summary>
    /// <remarks>
    ///     ⚠ <b>Averaged, because an instantaneous frame time is a number nobody can read.</b> At
    ///     sixty hertz a per-frame figure changes sixty times a second and spends most of its life
    ///     mid-redraw; a mean over half a second is the difference between a cell somebody notices
    ///     getting worse and a cell they learn to ignore.
    /// </remarks>
    const int FrameWindow = 30;

    readonly UiElement chrome;
    readonly UiElement modeBar;
    readonly UiElement statusMessage;
    readonly UiElement statusSelection;
    readonly UiElement statusFrame;
    readonly ProgressBar statusProgress;
    readonly Button statusTasks;
    readonly Popover taskPopover;

    /// <summary>The task centre, which is a VXML component rather than a control.</summary>
    /// <remarks>
    ///     Held for <see cref="Show" />'s sake and nothing else. Keeping a mounted component alive
    ///     is <see cref="UiDocument.ComponentAt" />'s job now, not the caller's.
    ///     <para>
    ///         Not <c>readonly</c>, because <see cref="RemountTaskCenter" /> replaces it. See there
    ///         for why that has to be a second step rather than an argument to this constructor.
    ///     </para>
    /// </remarks>
    TaskCenter taskCenter;

    readonly double[] frames = new double[FrameWindow];

    int frameCursor;
    int frameCount;
    float phase;

    /// <summary>Builds the shell into a new document.</summary>
    /// <param name="width">The surface's width in device-independent pixels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="mode">Which theme to start in.</param>
    /// <param name="logger">
    ///     Where a stylesheet the cascade could not read is reported.
    ///     <para>
    ///         ⚠ <b>What makes <c>UiDocument</c>'s diagnostic drain reach a person in the one
    ///         application that edits <c>.vcss</c> files.</b> The drain logs; a document handed no
    ///         logger logs into <c>NullLogger</c>, which is the pre-existing silence with an extra
    ///         step. <c>Vixen.Editor.App</c> passes a logger over its own <c>RingBufferSink</c>, which
    ///         is the ring the Console panel reads — so a mistyped at-rule in <c>EditorTheme.vcss</c>
    ///         appears in the panel the moment the sheet is installed rather than never.
    ///     </para>
    /// </param>
    public EditorShell(float width, float height, ThemeMode mode = ThemeMode.Dark, ILogger? logger = null) {
        Document = new UiDocument(width, height, logger: logger);

        // ⚠ In this order, and all three. Each is written against the tokens the one before it
        // declares, and a custom property nothing declared substitutes to nothing.
        ControlTheme.Install(Document);
        AdvancedTheme.Install(Document);
        EditorTheme.Install(Document);

        Theme = new ThemeService(Document, mode);

        chrome = Document.Root.Add<UiElement>("editor-shell");

        Commands = new CommandRegistry();
        Keys = new KeyMap();

        // ⚠ Before the menu bar is built, because a menu item's shortcut text is written when the
        // item is made. Doing it later would leave the bar reading "Ctrl+S" on a machine whose every
        // other application says ⌘S, until something happened to trigger a rebuild.
        //
        // ⚠ And again from `RefreshShortcutFormat` once the host has a font, because whether the
        // glyph form is legible depends on the face — and there is none yet.
        KeyChord.UsePlatformFormat(Document);

        Menus = DefaultMenus();
        MenuBar = new MenuPresenter(chrome, Menus, Commands, Keys);

        // ⚠ Between the menu bar and the toolbar, which is doc 20's frame: menu bar → mode bar →
        // toolbar → workspace → status bar. Its own host element rather than the chrome directly,
        // because `ToolbarPresenter` reserves a slot in whatever it is given and a shell that put
        // two strips in one host would have them fight over which is second.
        //
        // ⚠ And hidden while nothing has registered a mode. An empty strip is a band of chrome with
        // padding in it, and a shell with no modes — every test, every sample — should look exactly
        // as it did before modes existed.
        modeBar = chrome.Add<UiElement>("mode-bar");
        modeBar.SetStyle("display", "none");

        ModeBar = new ToolbarPresenter(modeBar, Commands, Keys);

        Toolbar = new ToolbarPresenter(chrome, Commands, Keys);
        Workspace = new DockingWorkspace(chrome.Add<UiElement>("editor-workspace"));

        // ⚠ After the workspace, because entering a mode may open the mode's panel. Nothing here
        // touches either until an application registers a mode, so the order is a statement rather
        // than a requirement — and a statement is what stops the next person moving it.
        Modes = new EditorModes(this);
        Modes.Changed += _ => RefreshModeBar();

        // ⚠ **The control, not a bare element with a class on it, and what changes is not the
        // picture.** `AccessibleRole.Status` is a live region: a screen reader announces a change to
        // it *without* moving the focus, which is the entire behaviour a status bar exists to have
        // and which the hand-drawn strip had none of. It also puts the message and the cells in
        // separate parts — see below.
        StatusBar = chrome.Add<StatusBar>();
        statusMessage = StatusBar.Label;

        // ⚠ Four cells, left to right, and doc 20 names all four: the transient message, the
        // selection count, the editor's own frame time, and the task centre. The frame time is the
        // one that is not obvious and is the one that matters — doc 00's editor-shell performance
        // bar is a claim about the editor, and a claim nobody can see is one that gets worse a panel
        // at a time until a benchmark notices six months later.
        //
        // ⚠ **Into `Trailing`, and it has to be explicit.** `ContentHost` routes a *nested tag* in
        // markup; `Add<T>` is `Document.Create` and puts the child exactly where it was told. So a
        // port that only swapped the type would leave the four cells beside the message with an
        // empty `status-trailing` after them — and an empty flex item still takes a gap, which is
        // eight pixels of dead chrome at the right-hand end that nothing in the tree explains.
        var trailing = StatusBar.Trailing;

        statusSelection = trailing.Add<UiElement>("status-cell");
        statusSelection.SetStyle("display", "none");

        statusFrame = trailing.Add<UiElement>("status-cell");
        statusFrame.AddClass("status-frame");

        statusProgress = trailing.Add<ProgressBar>();
        statusProgress.SetStyle("display", "none");

        statusTasks = trailing.Add<Button>();
        statusTasks.Variant = ControlVariant.Subtle;
        statusTasks.Size = ControlSize.Small;
        statusTasks.Label = EditorStrings.TasksTitle.Text;

        Toasts = Document.Root.Add<ToastHost>();
        Notifications = new NotificationCenter(Toasts);
        Tasks = new BackgroundTaskManager();

        taskPopover = Document.Root.Add<Popover>();

        // The one panel written in VXML rather than in C#. `Build` is what mounts a component into
        // a document; everything below the popover's content element is the `.vxml` beside this
        // file, compiled by the markup generator into the same assembly.
        //
        // ⚠ Built directly, and therefore *not* reloadable until somebody says otherwise. A reload
        // host is built over a document and this constructor is what makes the document, so there
        // cannot be one to mount through yet. `RemountTaskCenter` is the second step, and
        // `Vixen.Editor.App` takes it.
        taskCenter = BuildContext.Build<TaskCenter>(Document, taskPopover.Content);
        taskCenter.Show(Tasks);

        statusTasks.Clicked += _ => taskPopover.Open(statusTasks);

        Palette = Document.Root.Add<CommandPalette>();
        Palette.AddSource(new CommandPaletteSource(Commands, Keys));

        // ⚠ The same machinery with different sources and a different question, which is doc 20's
        // A8 in one line: `Ctrl+K` is "run the thing I am naming" and `Ctrl+Shift+F` is "where is
        // this word in my project". Grouped by source with a preview, because the second question's
        // answer is four short lists rather than one ranked one — and a second palette rather than a
        // mode on the first, because a mode would be a palette whose Return means two things.
        Search = Document.Root.Add<CommandPalette>();
        Search.GroupBySource = true;
        Search.RequiresQuery = true;
        Search.Limit = 20;

        Dialogs = new DialogService(Document);

        // ⚠ Wired both ways, once, here. The registry answers "what context is this command in" so
        // the keymap can file a binding under it; the shell answers "what context has the focus" so
        // the registry can decide whether a command is reachable. Neither knows about the other, and
        // this is the only place that knows about both.
        Keys.ContextOf = id => Commands.TryGet(id, out var command) ? command.Context : null;
        Commands.FocusedContext = () => Context;

        // ⚠ The end of `CommandRoute`'s chain, and the third place this constructor joins two things
        // that do not know about each other. The route walks the focused element and its ancestors;
        // past the root it asks the document's responder and then the application's, and the
        // registry is what the editor puts in the second slot — AppKit's `NSApp` delegate, which
        // answers the verbs that are true everywhere and belong to no view.
        //
        // What it buys: a plain `Vixen.Ui` control with `Command = "edit.rename"` now resolves,
        // greys and runs the editor's command, through the editor's scope and enablement rules and
        // raising the editor's `Executed`, with nothing editor-shaped in the control. Without it the
        // route stopped at the root and every editor id was unhandled.
        //
        // Nothing existing changes: nothing in the editor resolved through `CommandRoute` before, so
        // this only adds answers where there were none.
        Document.ApplicationCommandResponder = Commands;

        // ⚠ The registry outlives no shell — this constructor is the only place one is made, and the
        // shell owns it — but it does outlive a plugin: a load adds commands and an unload removes
        // them, and either can turn a bound control live or dead. The document coalesces this to one
        // raise per frame, so a plugin registering forty commands costs one.
        //
        // Unsubscribed in `Dispose`, and honestly: this is not a leak fix. Registry and shell are one
        // ownership unit and the reference cycle between them — this subscription, and the
        // `FocusedContext` closure above, which captures the shell — is collected as a unit. It comes
        // off because `Dispose` is the moment the shell stops wanting to hear, and a disposed shell
        // told to invalidate a disposed document is a bug whether or not anything leaks.
        Commands.Changed += OnCommandsChanged;

        Dispatcher = new CommandDispatcher(Commands, Keys);
        Dispatcher.Attach(Document);
        // ⚠ The command's own reason where it has one. "Not available right now" is the honest answer
        // for a verb whose enablement is false this second; it is the wrong answer for one that has
        // not been written, and telling the two apart is what makes a greyed menu line readable.
        Dispatcher.Refused += command => Notifications.Show(
            command.Title.Text,
            NotificationSeverity.Warning,
            command.IsUnavailable ? command.Unavailable.Text : "Not available right now"
        );

        Tasks.Ended += Announce;

        RegisterViewCommands();
        RegisterShellPanels();
    }

    /// <summary>The keybinding editor, while its panel is open.</summary>
    public KeyBindingsView? Keyboard { get; private set; }

    /// <summary>The message log, while its panel is open.</summary>
    public MessageLogView? Messages { get; private set; }

    /// <summary>Raised when the keybinding panel is built, so a host can wire what it cannot.</summary>
    /// <remarks>
    ///     ⚠ <b>Import and export need a file picker, which this assembly deliberately has no way to
    ///     reach.</b> The panel says what the user asked for and something with an
    ///     <c>INativeDialogs</c> answers — and a host with none disables the two buttons rather than
    ///     leaving a pair that do nothing. Raised every time the panel is built, because a panel's
    ///     factory runs again when it is reopened and the view is a new one each time.
    /// </remarks>
    public event Action<KeyBindingsView>? KeyboardBuilt;

    /// <summary>The two panels that are views over what the shell itself owns.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The shell registers these two and no others, and the line is not arbitrary.</b>
    ///         Everything else in the editor is a view over something the <i>application</i> has — a
    ///         project, a scene, a plugin host — and a shell that registered those would be a shell
    ///         that knows what they are, which is the one thing this class is built not to. The
    ///         keybinding editor is a view over <see cref="Commands" /> and <see cref="Keys" />; the
    ///         message log is a view over <see cref="Notifications" />. All four are here.
    ///     </para>
    ///     <para>
    ///         The consequence is the one worth having: any host of this shell — the editor, a
    ///         sample, a test — gets both, and doc 20's Part A calls them shell infrastructure for
    ///         exactly that reason.
    ///     </para>
    /// </remarks>
    void RegisterShellPanels() {
        RegisterPanel(
            new PanelDescriptor(
                KeyBindingsPanel,
                EditorStrings.PanelKeys,
                panel => {
                    Keyboard = panel.Add<KeyBindingsView>();
                    Keyboard.Show(Commands, Keys);

                    KeyboardBuilt?.Invoke(Keyboard);
                }
            ) {
                // ⚠ Both halves of the factory's contract. A field holding a control from a closed
                // panel is a pointer into a detached tree — see `PanelDescriptor.Closed`.
                Closed = () => Keyboard = null
            }
        );

        RegisterPanel(
            new PanelDescriptor(
                MessageLogPanel,
                EditorStrings.PanelMessages,
                panel => {
                    Messages = panel.Add<MessageLogView>();
                    Messages.Show(Notifications);
                }
            ) {
                Closed = () => Messages = null
            }
        );
    }

    /// <summary>Builds the task centre again through something that can reload it.</summary>
    /// <param name="mount">
    ///     Builds the component type it is handed into the element it is handed, and keeps track of
    ///     it. A hot-reload host's <c>Mount(Type, UiElement)</c>, in the one application that has a
    ///     host.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="mount" /> is null.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The delegate did not build a task centre. It is handed the type it is to build.
    /// </exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An ordering problem rather than a markup one, and this is the second step it
    ///         needs.</b> The task centre is the shell's one <c>.vxml</c> panel and it was the one
    ///         panel a <c>dotnet watch</c> could not reach: only a component mounted through a
    ///         reload host is rebuilt when the runtime replaces its <c>Build</c>, and the host is
    ///         built over a document that this class's constructor is what creates. So the shell
    ///         builds it plainly, and a caller that has a host swaps it for a tracked one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A delegate rather than the host itself, deliberately.</b> This assembly is
    ///         chrome — see the note in its <c>.csproj</c> about not referencing
    ///         <c>Vixen.Editor.Core</c> — and <c>Vixen.Ui.HotReload</c> is a development tool that
    ///         is neither trimmable nor AOT-compatible. Naming it here would put both properties on
    ///         every application that hosts a shell, to say something a <c>Func</c> already says.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the type travels as a <see cref="Type" /> because it cannot travel any other
    ///         way.</b> A component compiled from a <c>.vxml</c> is <c>internal</c> to the assembly
    ///         the markup is in, so <c>Vixen.Editor.App</c> cannot write the name <c>TaskCenter</c>
    ///         at all — the caller supplies the tracking and this supplies the type, which is the
    ///         only division of labour the accessibility allows.
    ///     </para>
    ///     <para>
    ///         The old component's elements are taken out of the document first, so the popover holds
    ///         one task centre rather than two — and <see cref="Show" />'s wiring is re-made here,
    ///         because it belongs to the instance and the instance is new.
    ///     </para>
    /// </remarks>
    public void RemountTaskCenter(Func<Type, UiElement, Component> mount) {
        ArgumentNullException.ThrowIfNull(mount);

        Document.Remove(taskCenter.Root);

        if (mount(typeof(TaskCenter), taskPopover.Content) is not TaskCenter remounted) {
            throw new InvalidOperationException(
                $"the mount delegate built something other than a {nameof(TaskCenter)}."
            );
        }

        taskCenter = remounted;
        taskCenter.Show(Tasks);
    }

    /// <summary>What the keybinding editor's panel is called in an arrangement.</summary>
    public const string KeyBindingsPanel = "keybindings";

    /// <summary>And the message log's.</summary>
    public const string MessageLogPanel = "messages";

    /// <summary>The document the host lays out, draws and dispatches into.</summary>
    public UiDocument Document { get; }

    /// <summary>Everything the editor can be asked to do.</summary>
    public CommandRegistry Commands { get; }

    /// <summary>What runs it from the keyboard.</summary>
    public KeyMap Keys { get; }

    /// <summary>What listens for the chords.</summary>
    public CommandDispatcher Dispatcher { get; }

    /// <summary>The panels, the arrangement, and the presets.</summary>
    public DockingWorkspace Workspace { get; }

    /// <summary>What is on the menu bar.</summary>
    public MenuModel Menus { get; }

    /// <summary>The Window menu, which is where a panel or a layout adds itself.</summary>
    /// <remarks>
    ///     ⚠ <b>Titled Window and reached as <see cref="View" /> too, and the ids under it are still
    ///     <c>view.*</c>.</b> Doc 20's Part C calls this menu Window, which is what both reference
    ///     editors call it and what somebody looking for "where did my panel go" reads. The property
    ///     and the command ids are not renamed with it: <see cref="View" /> is what every plugin
    ///     written against this shell already names, and a command id is what a saved keymap holds —
    ///     so renaming either would silently drop a user's bindings to make a label read better.
    /// </remarks>
    public MenuGroup Window { get; private set; } = null!;

    /// <inheritdoc cref="Window" />
    public MenuGroup View => Window;

    /// <summary>The menu bar itself.</summary>
    public MenuPresenter MenuBar { get; }

    /// <summary>The strip under it: which mode the viewport's input is in, and that mode's tools.</summary>
    /// <remarks>
    ///     Shown by <see cref="Modes" /> and rebuilt whenever the mode set or the active mode changes.
    ///     A host that wants to put something else on it is describing a toolbar rather than a mode
    ///     bar, and <see cref="Toolbar" /> is that.
    /// </remarks>
    public ToolbarPresenter ModeBar { get; }

    /// <summary>What the viewport's input means right now, and what else it could mean.</summary>
    /// <remarks>
    ///     Empty until an application registers a mode, which is what a shell with no viewport in it
    ///     should be. See <see cref="IEditorMode" /> for why the seam exists before the second mode
    ///     does.
    /// </remarks>
    public EditorModes Modes { get; }

    /// <summary>The strip under it.</summary>
    public ToolbarPresenter Toolbar { get; }

    /// <summary>The strip along the bottom.</summary>
    /// <remarks>
    ///     ⚠ <b>Its cells are in <see cref="Vixen.Ui.Controls.StatusBar.Trailing" />, not directly
    ///     under it.</b> The message is a part of its own, so a walk of <c>Children</c> here finds
    ///     two elements rather than five.
    /// </remarks>
    public StatusBar StatusBar { get; }

    /// <summary>Where messages appear.</summary>
    public ToastHost Toasts { get; }

    /// <summary>What the editor has told the user.</summary>
    public NotificationCenter Notifications { get; }

    /// <summary>What it is doing in the background.</summary>
    public BackgroundTaskManager Tasks { get; }

    /// <summary>Light or dark, and the user's own tokens over it.</summary>
    public ThemeService Theme { get; }

    /// <summary>Fuzzy search over everything.</summary>
    public CommandPalette Palette { get; }

    /// <summary>Fuzzy search over everything the editor <i>has</i>: assets, entities, settings.</summary>
    /// <remarks>
    ///     ⚠ <b>Empty until something adds a source, and that is the shell being honest.</b> The
    ///     shell knows what a command is and nothing else — there is no project here, no scene and no
    ///     asset database — so the sources are the application's to add, exactly as
    ///     <see cref="Palette" />'s extra ones are. What the shell supplies is the overlay, the
    ///     ranking, the grouping, the preview and the key.
    /// </remarks>
    public CommandPalette Search { get; }

    /// <summary>How the editor asks a question.</summary>
    public DialogService Dialogs { get; }

    /// <summary>What File ▸ Open Recent lists, asked every time the menu is built.</summary>
    /// <remarks>
    ///     ⚠ <b>Command ids, not paths.</b> Every dynamic menu in this shell is a set of ids — see
    ///     <see cref="MenuDynamic" /> — because a line has to have a title, an enablement and a place
    ///     in the palette, and only a registered command has all three. An application producing this
    ///     registers one command per recent project and hands back their ids.
    /// </remarks>
    public Func<IEnumerable<string>>? Recent { get; set; }

    /// <summary>What the window's title bar should say.</summary>
    /// <remarks>
    ///     ⚠ <b>The shell composes it and the host applies it.</b> A shell that set a window title
    ///     would be a shell that knows what a window is, which is the one thing this class is built
    ///     not to know — so the host reads this and calls the platform. It is the only affordance
    ///     that answers "which project is this window" when three are open.
    /// </remarks>
    public string Title { get; private set; } = "Vixen";

    /// <summary>Raised when <see cref="Title" /> changes.</summary>
    public event Action<string>? TitleChanged;

    /// <summary>Decides again how a shortcut is written, now that there is a font to judge with.</summary>
    /// <remarks>
    ///     ⚠ <b>Called by the host after it installs a face, and the ordering is the whole reason
    ///     this exists.</b> The shell is built before anything has a font — it is a
    ///     <c>UiDocument</c> and nothing else, which is what makes it testable headless — so the
    ///     first decision is made against no face at all and is necessarily the conservative one.
    ///     A machine whose borrowed font does have ⌘ should get ⌘, and that can only be known later.
    /// </remarks>
    public void RefreshShortcutFormat() {
        KeyChord.UsePlatformFormat(Document);

        // Both views, because a shortcut is drawn by a menu item when the item is made and by a
        // toolbar button's label when the strip is built. Neither re-reads it on its own.
        MenuBar.Rebuild();
        ModeBar.Rebuild();
        Toolbar.Rebuild();
    }

    /// <summary>Says what this window is looking at.</summary>
    /// <param name="document">The open document's name, or <see langword="null" /> for none.</param>
    /// <param name="dirty">Whether it has unsaved changes.</param>
    /// <param name="project">The project's name, or <see langword="null" />.</param>
    /// <remarks>
    ///     <c>&lt;scene&gt;* — &lt;project&gt; — Vixen</c>, dropping whichever parts are absent so a
    ///     shell with no project open is titled "Vixen" rather than " —  — Vixen".
    /// </remarks>
    public void Describe(string? document, bool dirty, string? project) {
        var parts = new List<string>(3);

        if (!string.IsNullOrEmpty(document)) {
            parts.Add(dirty ? document + "*" : document);
        }

        if (!string.IsNullOrEmpty(project)) {
            parts.Add(project);
        }

        parts.Add("Vixen");

        var title = string.Join(" — ", parts);

        if (!string.Equals(title, Title, StringComparison.Ordinal)) {
            Title = title;
            TitleChanged?.Invoke(title);
        }
    }

    /// <summary>What the status bar says on the left.</summary>
    public string? Status {
        get => statusMessage.Text;
        set => statusMessage.Text = value;
    }

    /// <summary>Which context the user is in, which decides what a scoped command means.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Set by whatever owns the focus, and read by everything that shows a command.</b>
    ///         The shell does not work it out for itself: a context is a claim about meaning — "the
    ///         outliner", "the content browser", "the graph canvas" — and only the thing that put the
    ///         focus somewhere knows which claim it just made. An application that never sets it has
    ///         every command in scope, which is the behaviour every command with no
    ///         <see cref="EditorCommand.Context" /> gets anyway.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Changing it rebuilds nothing.</b> Menus apply enablement as they open and the
    ///         toolbar refreshes on the tick, so a context that changes on every click costs a
    ///         predicate rather than a layout pass — which is what makes it safe to set from a
    ///         pointer handler.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This is not <c>UiElement.CommandScope</c>, and #642 asks five times over for it
    ///         to be deleted in favour of one. It carries a fact the focus does not have.</b>
    ///         <c>CommandRoute.ScopeOf</c> answers "which scope is the focused element in"; most
    ///         writers here agree with that — every <c>panel.WhenPressedIn(() =&gt; Shell.Context =
    ///         …)</c> in <c>EditorApplication</c>, <c>EditorWorlds</c>, <c>DiagnosticsModule</c> and
    ///         <c>BlockoutModulePanels</c> is a press inside a panel. Two writers do not.
    ///         <c>EditorApplication.cs:2207</c> reads <c>Shell.Modes.Context ?? SceneContext</c>, and
    ///         <c>RegisterModes</c> claims the context on entering a mode <i>without waiting for a
    ///         press</i> — its own comment says why: somebody who has just pressed the Blockout
    ///         button has aimed at the viewport and should not have to click it as well. The focus at
    ///         that instant is on the toolbar button, so a scope derived from it would report the
    ///         toolbar and the mode's own bindings would silently stop resolving.
    ///     </para>
    ///     <para>
    ///         So the honest shape is <c>Modes.Context</c> outranking a focus-derived scope rather
    ///         than a deletion — a behaviour change across <c>Vixen.Editor.App</c>,
    ///         <c>.Blockout</c>, <c>.Terrain</c>, <c>.Water</c> and <c>.Diagnostics</c> that wants a
    ///         running editor to judge, because a press that does not move the focus stops changing
    ///         the scope the day it lands.
    ///     </para>
    /// </remarks>
    public string? Context {
        get;
        set {
            if (!string.Equals(field, value, StringComparison.Ordinal)) {
                field = value;
                ContextChanged?.Invoke(value);
            }
        }
    }

    /// <summary>Raised when <see cref="Context" /> changes.</summary>
    /// <remarks>What a panel listens to in order to stop showing itself as the active one.</remarks>
    public event Action<string?>? ContextChanged;

    /// <summary>How many things are selected, for the status bar to report.</summary>
    /// <remarks>
    ///     ⚠ <b>A delegate rather than a number the application pushes.</b> There is one selection
    ///     per open document plus one for the project, and which of them the count is about is the
    ///     application's arbitration — see <c>EditorApplication.FollowSelection</c> — so a shell that
    ///     held a number would hold whichever one was written last. Unset, the cell is not drawn at
    ///     all, which is right for a shell with no documents in it.
    /// </remarks>
    public Func<int>? SelectionCount { get; set; }

    /// <summary>The mean time one editor frame has taken lately, in milliseconds.</summary>
    /// <remarks>
    ///     Measured from the deltas the host passes to <see cref="Tick" />, so it is the whole frame
    ///     — events, layout, the application's update and the draw — rather than the part of it this
    ///     class is responsible for. That is the number doc 00's performance bar is about.
    /// </remarks>
    public double FrameTime { get; private set; }

    /// <summary>Declares a panel and the command that shows it.</summary>
    /// <param name="descriptor">What the panel is.</param>
    /// <returns>The descriptor.</returns>
    /// <remarks>
    ///     ⚠ <b>The command comes with the panel, which is what makes the View menu build itself.</b>
    ///     A panel registered without one would be a panel reachable only by a layout that already
    ///     mentions it — and closing it would be permanent.
    /// </remarks>
    public PanelDescriptor RegisterPanel(PanelDescriptor descriptor) {
        ArgumentNullException.ThrowIfNull(descriptor);
        Workspace.Register(descriptor);

        var id = descriptor.Id;

        Commands.Add(
            new EditorCommand(PanelCommand(id), descriptor.Title, () => Workspace.Toggle(id)) {
                Category = EditorStrings.CategoryPanel,
                Checked = () => Workspace.IsOpen(id)
            }
        );

        return descriptor;
    }

    /// <summary>Declares a panel and the command that shows it.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="title">What its tab says.</param>
    /// <param name="build">Fills it.</param>
    /// <returns>The descriptor.</returns>
    public PanelDescriptor RegisterPanel(string id, StringId title, Action<DockPanel> build) =>
        RegisterPanel(new PanelDescriptor(id, title, build));

    /// <summary>Takes a panel and its command back out.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>Whether it was registered.</returns>
    /// <remarks>
    ///     ⚠ <b>Both halves, because <see cref="RegisterPanel(PanelDescriptor)" /> made both.</b> A
    ///     workspace that forgot the panel while the registry still had the command would leave a
    ///     View-menu line and a palette entry that toggle nothing — and, for a plugin's panel, a
    ///     lambda over the plugin's own state that keeps its assembly loaded for the session.
    /// </remarks>
    public bool UnregisterPanel(string id) {
        if (!Workspace.Unregister(id)) {
            return false;
        }

        Commands.Remove(PanelCommand(id));
        return true;
    }

    /// <summary>Declares a named arrangement and the command that applies it.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="title">What the menu line says.</param>
    /// <param name="layout">Builds it.</param>
    public void RegisterLayout(string name, StringId title, Func<DockLayout> layout) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Workspace.AddPreset(name, layout);

        Commands.Add(
            new EditorCommand(LayoutCommand(name), title, () => Workspace.Apply(name)) {
                Category = EditorStrings.CategoryView
            }
        );
    }

    /// <summary>Takes a named arrangement and its command back out.</summary>
    /// <param name="name">What it was called.</param>
    /// <returns>Whether it was registered.</returns>
    public bool UnregisterLayout(string name) {
        if (!Workspace.RemovePreset(name)) {
            return false;
        }

        Commands.Remove(LayoutCommand(name));
        return true;
    }

    /// <summary>What the command that shows a panel is called.</summary>
    /// <param name="panelId">The panel.</param>
    /// <returns>The command id.</returns>
    public static string PanelCommand(string panelId) => "view.panel." + panelId;

    /// <summary>What the command that applies a layout is called.</summary>
    /// <param name="layoutName">The layout.</param>
    /// <returns>The command id.</returns>
    public static string LayoutCommand(string layoutName) => "view.layout." + layoutName;

    /// <summary>Advances whatever moves by itself, and applies what the background has reported.</summary>
    /// <param name="now">The time since the application started.</param>
    /// <param name="delta">How long the last frame took.</param>
    /// <remarks>
    ///     ⚠ <b>The host drives the clock, and everything timed in the interface is built that
    ///     way.</b> Nothing in <c>Vixen.Ui</c> knows what time it is except through an input event,
    ///     so a toast that expires, a gesture that is a long press and a spinner that spins all need
    ///     telling. The background tasks are pumped here for a different reason: this is the point
    ///     in the frame at which the numbers may change without a layout pass seeing half of them.
    /// </remarks>
    public void Tick(TimeSpan now, TimeSpan delta) {
        phase = (phase + (float) delta.TotalSeconds) % 1f;

        // ⚠ <b>The document's tick, not the recogniser's, and the difference is four features
        // wide.</b> This read `Document.Gestures.Tick(now)` — the one thing the shell happened to
        // need on the day it was written — so `UiDocument.Now` stayed at zero for the life of the
        // editor and `UiDocument.Ticked` was never raised at all. `Overlay` and `Toasts` both hang
        // their expiry on that event, so a tooltip's delay and a toast's dismissal were driven by
        // nothing here; the editor's own `Notifications` has a separate clock, which is why the
        // second of those was invisible.
        //
        // ⚠ And it is what makes a CSS transition run. `UiDocument.Tick` advances the animator and
        // marks the document dirty while anything is in flight — a host that skips it does not get
        // instant changes, it gets *stuck* ones, because a fade stamped at zero against a clock that
        // never leaves zero has made no progress on any frame. That failure mode is worth more alarm
        // than the missing tooltips: it holds a property at the value it was leaving, for ever, and
        // reads as a rendering bug rather than a timing one.
        Document.Tick(now);
        Notifications.Tick(now);

        Tasks.Pump();

        // ⚠ **No `Dialogs.Pump()` here, and its absence is the wiring rather than a gap.**
        // `DialogService` subscribes to `UiDocument.Ticked`, so the pump already ran inside
        // `Document.Tick` above — which is what makes dialogs work in a `Vixen.Ui` application that
        // has no shell at all. The ordering this line used to buy is still bought: the answer
        // resumes before `Toolbar.Refresh` and before `RefreshStatus`, so a command that was
        // awaiting a dialog is entitled to change both, and before `Effects.Flush` at the bottom, so
        // a signal it writes is drained on this frame. See `DialogService.Pump`.
        //
        // ⚠ **Still polled, and doc 45 step 4 expected this line to go.** Every button on both
        // strips is now bound to its id, so each one follows `UiDocument.CommandsInvalidated` by
        // itself — which is the whole point of step 5 and does make the poll unnecessary for a
        // command whose state says it changed. Two of the editor's own do not say so. `file.save`
        // reads a scene's dirty signal and `assets.build` reads a content build's busy flag —
        // `Volatile.Read` over a counter a worker thread moves — and neither has any notification
        // to hang an invalidation on. Deleting this would leave Save greyed after the first edit
        // and Build enabled through a build, with nothing failing anywhere, which is worse than a
        // poll.
        //
        // ⚠ And it asks *these buttons*, not the document. `Document.InvalidateCommands()` here
        // would be the same poll spelled correctly and would wake every bound `MenuItem` on the bar
        // as well — two hundred lines that are not on screen, asked sixty times a second, in place
        // of ten that are. See #430.
        Toolbar.Refresh();

        // ⚠ Only when there is one, and for the mode's *tools* rather than for the mode buttons.
        // Entering a mode raises `Modes.Changed`, which rebuilds this strip outright — so which
        // mode you are in is drawn without any help from here. What is left is the strip the active
        // mode contributes: Blockout's element kinds and Terrain's brush categories are radio groups
        // whose `Checked` moves when a tool command runs, and a command running invalidates nothing.
        if (Modes.Modes.Count > 0) {
            ModeBar.Refresh();
        }

        Measure(delta);
        RefreshStatus();

        // ⚠ **Last, and it is the point in the frame the whole signal graph was designed around.**
        // Writing a signal only queues; nothing above this line has changed an element, it has
        // changed what the elements are *going to say*. Draining here means one flush per frame,
        // after every model the interface reads has been pumped, and before the host lays the
        // document out — never in the middle of a pass that is walking the tree.
        //
        // ⚠ This document's queue, not the thread's. `UiDocument.Effects` says why, and the reason
        // is not theoretical: a shell that drained the thread's would run every other document's
        // bindings, disposed ones included.
        Document.Effects.Flush();
    }

    /// <summary>Changes the surface's size.</summary>
    /// <param name="width">The new width.</param>
    /// <param name="height">Its height.</param>
    public void Resize(float width, float height) {
        Document.Resize(width, height);

        // The pass is still run here rather than left to the host's next frame, so that a caller
        // that resizes and then reads a box gets the new one. What it no longer has to do is tell
        // the virtualisers: `Control.WhenResized` is how they find out, and it fires from inside
        // this pass rather than from a caller who remembered.
        Document.Update();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The menu presenter goes first, and it is the only thing here that has to.</b> It is
    ///     subscribed to <c>Strings.Changed</c>, which is static — a shell disposed without it
    ///     would rebuild a menu bar into a disposed document the next time anybody switched
    ///     language. Everything else in the shell is subscribed only to things the shell owns.
    /// </remarks>
    public void Dispose() {
        MenuBar.Dispose();

        // ⚠ Not for the menu presenter's reason: `Commands` is the shell's own and goes with it, so
        // this subscription is a cycle inside one ownership unit rather than a static holding a dead
        // document. It comes off because a disposed shell asked to invalidate a disposed document is
        // wrong on its own terms, and because a caller that keeps the registry — a test, a host
        // rebuilding its shell around one — must not find the old shell still listening.
        // `Document.Dispose` below drops the other direction, the registry the document was pointed at.
        Commands.Changed -= OnCommandsChanged;

        // ⚠ Before the document, and it answers rather than drops. A command awaiting a dialog is a
        // continuation holding whatever it was in the middle of — the save-on-close prompt is the
        // one that matters — and a task nobody completes is a shutdown that never finishes.
        // `Dispose` rather than `CancelAll` because the service is subscribed to the document's
        // tick: dropping the subscription is what stops the shell's queue outliving the shell.
        Dialogs.Dispose();

        // ⚠ Disposed rather than `CancelAll`, and the difference is what happens to a task that does
        // not stop. `CancelAll` asks and leaves the manager listening, so work still on the pool
        // keeps enqueueing reports into a queue this shell will never pump again — and a task whose
        // delegate came from a plugin keeps that plugin's collectible load context alive through the
        // closure. Disposing asks *and* stops accepting, which is what makes a reload not a leak.
        Tasks.Dispose();

        Document.Dispose();
    }

    /// <summary>Tells every command surface to re-ask, because the set of commands changed.</summary>
    /// <remarks>
    ///     A named method rather than a lambda so that <see cref="Dispose" /> can take it off again.
    ///     A closure would be a different delegate every time it was written and could not be
    ///     unsubscribed, which is the ordinary way this kind of subscription becomes permanent.
    /// </remarks>
    void OnCommandsChanged(CommandRegistry registry) => Document.InvalidateCommands();

    /// <summary>The menus the editor ships with.</summary>
    /// <remarks>
    ///     Ids that nothing has registered are skipped when the bar is built, so this is what the
    ///     shell offers rather than what it promises: an application that registers none of the file
    ///     commands gets a File menu with only Exit in it, and one that registers all of them gets
    ///     the menu everybody expects.
    /// </remarks>
    MenuModel DefaultMenus() {
        var model = new MenuModel();

        var file = model.AddMenu(EditorStrings.MenuFile);

        file.Add("file.new-project", "file.open-project");
        file.AddSubmenu(EditorStrings.MenuRecent).AddDynamic(() => Recent?.Invoke() ?? []);

        file.AddSeparator()
            .Add("file.new-scene", "file.open-scene", "file.save", "file.save-as", "file.save-all", "file.revert")
            .AddSeparator()
            .Add("assets.import-files", "file.export-package")
            .AddSeparator()
            .Add("file.project-settings")
            .AddSeparator()
            .Add("file.exit");

        model.AddMenu(EditorStrings.MenuEdit)
            .Add("edit.undo", "edit.redo", "edit.undo-history")
            .AddSeparator()
            .Add("edit.cut", "edit.copy", "edit.paste", "edit.paste-as-child", "edit.duplicate", "edit.delete")
            .Add("edit.rename")
            .AddSeparator()
            .Add("edit.select-all", "edit.deselect-all", "edit.invert-selection")
            .Add("edit.select-children", "edit.select-parent")
            .AddSeparator()
            .Add("edit.search-everywhere", "edit.find-references")
            .AddSeparator()
            .Add("edit.preferences", "edit.keybindings");

        // ⚠ Assets, Entity, Scene, Play, Build and Tools are <i>not</i> here, and doc 20's Part C
        // lists all ten. The six missing ones are made entirely of an application's verbs — there is
        // no shell-level meaning to "reimport" or "align with view" — and a shell that put them on
        // the bar would put six words there that drop open onto nothing in every host that is not
        // the editor. `MenuModel.InsertMenu` is how the editor puts them back in Part C's order; the
        // four below are the ones a shell can genuinely fill.
        Window = model.AddMenu(EditorStrings.MenuWindow);
        Window.Add("view.palette").AddSeparator();

        model.AddMenu(EditorStrings.MenuHelp)
            .Add("help.documentation", "help.api-reference", "help.release-notes")
            .AddSeparator()
            .Add("help.report-bug", "help.show-log-folder")
            .AddSeparator()
            .Add("help.about");

        return model;
    }

    void RegisterViewCommands() {
        Commands.Add(
            new EditorCommand("view.palette", EditorStrings.CommandPalette, () => Palette.OpenPalette()) {
                Category = EditorStrings.CategoryView,

                // ⚠ Hidden from the palette, because a palette entry that opens the palette is a
                // line the user reads once, chooses once, and finds nothing happens.
                IsHiddenFromPalette = true
            }
        );

        Commands.Add(
            new EditorCommand("mode.next", EditorStrings.NextMode, () => Modes.Next()) {
                Category = EditorStrings.CategoryMode,
                Enablement = () => Modes.Modes.Count > 1
            }
        );

        // ⚠ Registered by the shell rather than by the application, unlike every other `edit.*` id.
        // The overlay is the shell's — see `Search` — and doc 20's Part C puts the line on the Edit
        // menu, which the shell's own default model already names. An application that adds no
        // sources gets an empty search rather than a dangling menu line, which is the same bargain
        // the palette makes.
        Commands.Add(
            new EditorCommand("edit.search-everywhere", EditorStrings.CommandSearchEverywhere, () => Search.OpenPalette()) {
                Category = EditorStrings.CategoryEdit,
                Enablement = () => Search.Sources.Count > 0,
                IsHiddenFromPalette = true
            }
        );

        Keys.SetDefault(
            "edit.search-everywhere",
            new KeyChord(InputKey.F, ModifierKeys.Control | ModifierKeys.Shift)
        );

        Commands.Add(
            new EditorCommand("view.reset-layout", EditorStrings.CommandResetLayout, ResetLayout) {
                Category = EditorStrings.CategoryView,
                Enablement = () => Workspace.Presets.Count > 0
            }
        );

        // ⚠ Greyed out where there can be no second window, rather than absent. A browser tab, an
        // Android activity and iOS all have one window, and the enablement is a runtime question with
        // a runtime answer — the same shape `PlatformCapabilities` uses, and the reason nothing above
        // the platform layer carries a `#if`.
        Commands.Add(
            new EditorCommand("view.float-panel", EditorStrings.CommandFloatPanel, () => Workspace.FloatActive()) {
                Category = EditorStrings.CategoryView,
                Enablement = () => Workspace.CanFloatActive
            }
        );

        Commands.Add(
            new EditorCommand("view.toggle-theme", EditorStrings.CommandToggleTheme, Theme.Toggle) {
                Category = EditorStrings.CategoryView,
                Checked = () => Theme.Mode == ThemeMode.Dark
            }
        );

        // The three tab verbs every editor with tabs has, and the reason they are the shell's rather
        // than the application's: what a tab *is* belongs to the docking workspace, which is here.
        Commands.Add(
            new EditorCommand("view.close-panel", EditorStrings.CommandClosePanel, () => Workspace.CloseActive()) {
                Category = EditorStrings.CategoryView,
                Enablement = () => Workspace.Host.Active is not null
            }
        );

        Commands.Add(
            new EditorCommand("view.next-tab", EditorStrings.CommandNextTab, () => Workspace.CycleTab(1)) {
                Category = EditorStrings.CategoryView
            }
        );

        Commands.Add(
            new EditorCommand("view.previous-tab", EditorStrings.CommandPreviousTab, () => Workspace.CycleTab(-1)) {
                Category = EditorStrings.CategoryView
            }
        );

        // ⚠ `Ctrl+K`, not the `Ctrl+P` doc 11 named. `Ctrl+P` is Play in Unity and Print almost
        // everywhere else, and a palette on it is a palette that fights the muscle memory of anyone
        // who has used either. `Ctrl+K` is what every editor with a palette settled on.
        Keys.SetDefault("view.palette", new KeyChord(InputKey.K, ModifierKeys.Control));

        // ⚠ Bare Tab, and it beats the interface's own focus traversal rather than fighting it.
        // `Keyboard.Dispatch` moves the focus only when the route left the event unhandled and the
        // command dispatcher is on that route. Blockout had this chord for its element-kind toggle;
        // a mode you can enter with Tab and not leave with it is the reason it does not any more.
        Keys.SetDefault("mode.next", new KeyChord(InputKey.Tab, ModifierKeys.None));
        Keys.SetDefault("view.toggle-theme", new KeyChord(InputKey.D, ModifierKeys.Control | ModifierKeys.Alt));
        Keys.SetDefault("view.close-panel", new KeyChord(InputKey.W, ModifierKeys.Control));
        Keys.SetDefault("view.next-tab", new KeyChord(InputKey.Tab, ModifierKeys.Control));
        Keys.SetDefault("view.previous-tab", new KeyChord(InputKey.Tab, ModifierKeys.Control | ModifierKeys.Shift));

        // ⚠ Dynamic, because the panel list and the preset list both grow after the shell is built
        // — a plugin registers a panel, an application registers a layout — and a menu described
        // once at start-up would show whichever of them happened to exist by then.
        Window.AddSubmenu(EditorStrings.MenuLayout)
            .AddDynamic(() => Workspace.Presets.Order(StringComparer.Ordinal).Select(LayoutCommand))
            .AddSeparator()
            .Add("view.save-layout", "view.reset-layout");

        Window.AddSubmenu(EditorStrings.MenuPanels)
            .AddDynamic(() => Workspace.Panels.Select(panel => PanelCommand(panel.Id)));

        Window.AddSeparator()
            .Add("view.float-panel", "view.close-panel", "view.next-tab", "view.previous-tab")
            .AddSeparator()
            .Add("view.toggle-theme", "view.full-screen");
    }

    /// <summary>Draws the mode strip again, and takes it off the window when there is nothing on it.</summary>
    void RefreshModeBar() {
        var entries = Modes.Bar();

        modeBar.SetStyle("display", entries.Count == 0 ? "none" : "flex");
        ModeBar.Show([.. entries]);
    }

    void ResetLayout() {
        Workspace.Reset();
        Notifications.Show(EditorStrings.LayoutReset.Text, NotificationSeverity.Success);
    }

    void Announce(BackgroundTask task) {
        switch (task.State) {
            case BackgroundTaskState.Failed:
                Notifications.Error(task.Title + " — " + EditorStrings.TasksFailed.Text, task.Failure?.Message);
                break;

            case BackgroundTaskState.Cancelled:
                Notifications.Show(task.Title + " — " + EditorStrings.TasksCancelled.Text, NotificationSeverity.Warning);
                break;

            default:
                break;
        }
    }

    /// <summary>Adds a frame to the rolling window and recomputes the mean.</summary>
    /// <remarks>
    ///     A fixed array walked by a cursor rather than a queue, because this runs every frame for
    ///     the life of the process and a per-frame allocation in the shell's own tick is the thing
    ///     doc 20 asks the cell to make visible.
    /// </remarks>
    void Measure(TimeSpan delta) {
        frames[frameCursor] = delta.TotalMilliseconds;
        frameCursor = (frameCursor + 1) % frames.Length;
        frameCount = Math.Min(frameCount + 1, frames.Length);

        var total = 0d;

        for (var index = 0; index < frameCount; index++) {
            total += frames[index];
        }

        FrameTime = frameCount == 0 ? 0d : total / frameCount;
    }

    /// <summary>How many frames pass between rewrites of the frame-time cell.</summary>
    /// <inheritdoc cref="RefreshStatus" path="/remarks" />
    const int StatusInterval = 15;

    int statusFrames;

    void RefreshStatus() {
        var running = Tasks.Tasks.Count;

        var selected = SelectionCount?.Invoke() ?? 0;

        // Absent rather than "0 selected", which is a cell that says nothing and is on screen most
        // of the time.
        statusSelection.SetStyle("display", selected == 0 ? "none" : "flex");

        if (selected > 0) {
            statusSelection.Text = string.Format(
                CultureInfo.CurrentCulture,
                EditorStrings.StatusSelection.Text,
                selected
            );
        }

        // ⚠ Four times a second rather than sixty, and it is a performance decision before it is a
        // legibility one. This cell ends in a frame time to one decimal, so it changed every frame —
        // and one changed character makes the whole window's draw list differ, which takes away every
        // chance to re-use the geometry built for it. The window then re-emits every vertex it drew
        // last time, whatever is on screen and whether or not anything moved.
        //
        // ⚠ It is on the *shell*, which is why this was not one panel's problem. A status bar is
        // present whichever panel has the focus, so the cost followed the user around and looked like
        // whichever panel they happened to be looking at.
        //
        // A number rewritten sixty times a second is also one nobody can read a value off, so the
        // rate that is cheap is the rate that is useful.
        if (statusFrames++ % StatusInterval == 0) {
            statusFrame.Text = string.Format(
                CultureInfo.CurrentCulture,
                EditorStrings.StatusFrameTime.Text,
                FrameTime.ToString("F1", CultureInfo.CurrentCulture)
            );
        }

        statusTasks.Label = running == 0
            ? EditorStrings.TasksTitle.Text
            : string.Create(CultureInfo.InvariantCulture, $"{EditorStrings.TasksTitle.Text} ({running})");

        // The bar is only there while there is something for it to say, so the status bar of an
        // idle editor is a line of text rather than an empty gauge.
        statusProgress.SetStyle("display", running == 0 ? "none" : "flex");

        if (running == 0) {
            return;
        }

        statusProgress.Phase = phase;
        statusProgress.IsIndeterminate = Tasks.Progress <= 0f;
        statusProgress.Value = Tasks.Progress * statusProgress.Maximum;
    }
}
