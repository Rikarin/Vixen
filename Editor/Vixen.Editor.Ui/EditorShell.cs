// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

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
    readonly UiElement statusMessage;
    readonly UiElement statusSelection;
    readonly UiElement statusFrame;
    readonly ProgressBar statusProgress;
    readonly Button statusTasks;
    readonly Popover taskPopover;
    readonly TaskCenterView taskCenter;

    readonly double[] frames = new double[FrameWindow];

    int frameCursor;
    int frameCount;
    float phase;

    /// <summary>Builds the shell into a new document.</summary>
    /// <param name="width">The surface's width in device-independent pixels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="mode">Which theme to start in.</param>
    public EditorShell(float width, float height, ThemeMode mode = ThemeMode.Dark) {
        Document = new UiDocument(width, height);

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
        KeyChord.UsePlatformFormat();

        Menus = DefaultMenus();
        MenuBar = new MenuPresenter(chrome, Menus, Commands, Keys);

        Toolbar = new ToolbarPresenter(chrome, Commands, Keys);
        Workspace = new DockingWorkspace(chrome.Add<UiElement>("editor-workspace"));

        StatusBar = chrome.Add<UiElement>("status-bar");
        statusMessage = StatusBar.Add<UiElement>("status-message");

        // ⚠ Four cells, left to right, and doc 20 names all four: the transient message, the
        // selection count, the editor's own frame time, and the task centre. The frame time is the
        // one that is not obvious and is the one that matters — doc 00's editor-shell performance
        // bar is a claim about the editor, and a claim nobody can see is one that gets worse a panel
        // at a time until a benchmark notices six months later.
        statusSelection = StatusBar.Add<UiElement>("status-cell");
        statusSelection.SetStyle("display", "none");

        statusFrame = StatusBar.Add<UiElement>("status-cell");
        statusFrame.AddClass("status-frame");

        statusProgress = StatusBar.Add<ProgressBar>();
        statusProgress.SetStyle("display", "none");

        statusTasks = StatusBar.Add<Button>();
        statusTasks.Variant = ControlVariant.Subtle;
        statusTasks.Size = ControlSize.Small;
        statusTasks.Label = EditorStrings.TasksTitle.Text;

        Toasts = Document.Root.Add<ToastHost>();
        Notifications = new NotificationCenter(Toasts);
        Tasks = new BackgroundTaskManager();

        taskPopover = Document.Root.Add<Popover>();
        taskCenter = taskPopover.Content.Add<TaskCenterView>();
        taskCenter.Show(Tasks);

        statusTasks.Clicked += _ => taskPopover.Open(statusTasks);

        Palette = Document.Root.Add<CommandPalette>();
        Palette.AddSource(new CommandPaletteSource(Commands, Keys));

        Dialogs = new DialogService(Document);

        // ⚠ Wired both ways, once, here. The registry answers "what context is this command in" so
        // the keymap can file a binding under it; the shell answers "what context has the focus" so
        // the registry can decide whether a command is reachable. Neither knows about the other, and
        // this is the only place that knows about both.
        Keys.ContextOf = id => Commands.TryGet(id, out var command) ? command.Context : null;
        Commands.FocusedContext = () => Context;

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
    }

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

    /// <summary>The strip under it.</summary>
    public ToolbarPresenter Toolbar { get; }

    /// <summary>The strip along the bottom.</summary>
    public UiElement StatusBar { get; }

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

        Document.Gestures.Tick(now);
        Notifications.Tick(now);

        Tasks.Pump();

        // ⚠ Before the toolbar and the status bar, because a dialog's answer resumes here and the
        // command that was awaiting it is entitled to change both. After the notifications, so that
        // a command which answers by showing a toast is not one frame behind. See
        // `DialogService.Pump` for why the completion happens on the tick at all.
        Dialogs.Pump();

        Toolbar.Refresh();
        taskCenter.Refresh();

        Measure(delta);
        RefreshStatus();
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

        // ⚠ Before the document, and it answers rather than drops. A command awaiting a dialog is a
        // continuation holding whatever it was in the middle of — the save-on-close prompt is the
        // one that matters — and a task nobody completes is a shutdown that never finishes.
        Dialogs.CancelAll();

        Tasks.CancelAll();
        Document.Dispose();
    }

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
            .Add("file.new-scene", "file.open-scene", "file.save", "file.save-as", "file.save-all")
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

        // Doc 11 asks for Ctrl/Cmd+P by name. Meta as well as Control, rather than instead of it on
        // a Mac, because this assembly does not know what it is running on — and a chord bound
        // twice is two entries in the map, which is exactly what the conflict check permits.
        Keys.SetDefault("view.palette", new KeyChord(InputKey.P, ModifierKeys.Control));
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

        statusFrame.Text = string.Format(
            CultureInfo.CurrentCulture,
            EditorStrings.StatusFrameTime.Text,
            FrameTime.ToString("F1", CultureInfo.CurrentCulture)
        );

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
