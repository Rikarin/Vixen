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
    readonly UiElement chrome;
    readonly UiElement statusMessage;
    readonly ProgressBar statusProgress;
    readonly Button statusTasks;
    readonly Popover taskPopover;
    readonly TaskCenterView taskCenter;

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

        Menus = DefaultMenus();
        MenuBar = new MenuPresenter(chrome, Menus, Commands, Keys);

        Toolbar = new ToolbarPresenter(chrome, Commands, Keys);
        Workspace = new DockingWorkspace(chrome.Add<UiElement>("editor-workspace"));

        StatusBar = chrome.Add<UiElement>("status-bar");
        statusMessage = StatusBar.Add<UiElement>("status-message");

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

        Dispatcher = new CommandDispatcher(Commands, Keys);
        Dispatcher.Attach(Document);
        Dispatcher.Refused += command => Notifications.Show(
            command.Title.Text,
            NotificationSeverity.Warning,
            "Not available right now"
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

    /// <summary>The View menu, which is where a panel or a layout adds itself.</summary>
    public MenuGroup View { get; private set; } = null!;

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

    /// <summary>What the status bar says on the left.</summary>
    public string? Status {
        get => statusMessage.Text;
        set => statusMessage.Text = value;
    }

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
        Toolbar.Refresh();
        taskCenter.Refresh();

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

        model.AddMenu(EditorStrings.MenuFile)
            .Add("file.new-project", "file.open-project")
            .AddSeparator()
            .Add("file.save", "file.save-all")
            .AddSeparator()
            .Add("file.exit");

        model.AddMenu(EditorStrings.MenuEdit)
            .Add("edit.undo", "edit.redo")
            .AddSeparator()
            .Add("edit.preferences");

        View = model.AddMenu(EditorStrings.MenuView);
        View.Add("view.palette").AddSeparator();

        model.AddMenu(EditorStrings.MenuHelp).Add("help.documentation", "help.about");
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

        Commands.Add(
            new EditorCommand("view.toggle-theme", EditorStrings.CommandToggleTheme, Theme.Toggle) {
                Category = EditorStrings.CategoryView,
                Checked = () => Theme.Mode == ThemeMode.Dark
            }
        );

        // Doc 11 asks for Ctrl/Cmd+P by name. Meta as well as Control, rather than instead of it on
        // a Mac, because this assembly does not know what it is running on — and a chord bound
        // twice is two entries in the map, which is exactly what the conflict check permits.
        Keys.SetDefault("view.palette", new KeyChord(InputKey.P, ModifierKeys.Control));
        Keys.SetDefault("view.toggle-theme", new KeyChord(InputKey.D, ModifierKeys.Control | ModifierKeys.Alt));

        // ⚠ Dynamic, because the panel list and the preset list both grow after the shell is built
        // — a plugin registers a panel, an application registers a layout — and a menu described
        // once at start-up would show whichever of them happened to exist by then.
        View.AddSubmenu(EditorStrings.MenuPanels)
            .AddDynamic(() => Workspace.Panels.Select(panel => PanelCommand(panel.Id)));

        View.AddSubmenu(EditorStrings.MenuLayout)
            .AddDynamic(() => Workspace.Presets.Order(StringComparer.Ordinal).Select(LayoutCommand))
            .AddSeparator()
            .Add("view.reset-layout");

        View.AddSeparator().Add("view.toggle-theme");
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

    void RefreshStatus() {
        var running = Tasks.Tasks.Count;

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
