// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.Ui;

/// <summary>A panel the editor can show: an id, a name, and what fills it.</summary>
/// <param name="Id">
///     What a saved layout calls it. Stable across versions, because it is written into a file on
///     the user's disk the first time they move the panel.
/// </param>
/// <param name="Title">What its tab says.</param>
/// <param name="Build">Fills the panel. Called once, when the panel is first shown.</param>
/// <remarks>
///     ⚠ <b>A factory rather than an element, and the panel is built lazily.</b> An editor with
///     thirty registered panels shows six; building the other twenty-four at start-up would cost a
///     scene view, a profiler and a node graph that nobody asked for. It is also what lets a saved
///     layout name a panel a plugin has not registered yet — the tab appears when it does.
/// </remarks>
public sealed record PanelDescriptor(string Id, StringId Title, Action<DockPanel> Build) {
    /// <summary>Whether the user may close it.</summary>
    public bool CanClose { get; init; } = true;

    /// <summary>Called after the panel leaves the arrangement, however it left.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The mirror of <see cref="Build" />, and without it a factory is half a
    ///         contract.</b> Nothing durable may live in a panel — a factory runs again on every
    ///         reopen — so anything durable is held by whoever registered it, which leaves that
    ///         object holding a control the moment the tab is closed. A viewport asked for its width
    ///         after removal throws; an undo history polled after removal quietly rewrites rows
    ///         nobody can see. Both are the same missing half.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>"However it left" is the part that has to be true.</b> A panel goes through
    ///         <see cref="DockingWorkspace.Close" />, through the tab's own close button, through
    ///         <see cref="DockingWorkspace.Unregister" />, and through a whole arrangement being
    ///         replaced — and only the first of those four is a call anybody could hang a callback
    ///         off. So the workspace watches what the host is actually showing rather than trusting
    ///         the paths it knows about.
    ///     </para>
    /// </remarks>
    public Action? Closed { get; init; }
}

/// <summary>The docked arrangement, the panels in it, and the presets it can be put back to.</summary>
/// <remarks>
///     <para>
///         <b>What <see cref="DockingHost" /> deliberately does not do.</b> The host knows about
///         panels by id, splits, tabs and a serialisable arrangement, and nothing about where a
///         panel's contents come from or what "the Shading layout" is — which is right for a control
///         in <c>Vixen.Ui</c>. This is the editor's half: a registry of what can be shown, lazy
///         construction, named presets, and one place that saves.
///     </para>
///     <para>
///         ⚠ <b>Applying a layout creates the panels it names, in that order.</b>
///         <c>DockingHost.AddPanel</c> leaves a panel the arrangement already places where the
///         arrangement put it, so registering after loading is what makes a restored layout come
///         back as it was — and doing it the other way round drops everything into the first group.
///     </para>
/// </remarks>
public sealed class DockingWorkspace {
    readonly Dictionary<string, PanelDescriptor> descriptors = new(StringComparer.Ordinal);
    readonly Dictionary<string, Func<DockLayout>> presets = new(StringComparer.Ordinal);
    readonly List<PanelDescriptor> ordered = [];

    /// <summary>What was in the arrangement last time the host rebuilt it.</summary>
    /// <remarks>
    ///     ⚠ <b>What makes <see cref="PanelDescriptor.Closed" /> fire however a panel left.</b> The
    ///     host raises <c>LayoutChanged</c> at the end of every rebuild, and a rebuild is what every
    ///     close, dock, float and applied arrangement ends with — so comparing what it is showing
    ///     against what it was showing catches the tab's close button, which no call into this class
    ///     goes through.
    /// </remarks>
    readonly HashSet<string> shown = new(StringComparer.Ordinal);

    string fallback = string.Empty;

    /// <summary>Builds a docking host into an element.</summary>
    /// <param name="host">Where it goes.</param>
    public DockingWorkspace(UiElement host) {
        ArgumentNullException.ThrowIfNull(host);

        Host = host.Add<DockingHost>();

        Host.LayoutChanged += _ => {
            Retire();
            Changed?.Invoke(this);
        };
    }

    /// <summary>Tells the descriptors of panels that have left the arrangement that they have.</summary>
    void Retire() {
        // ⚠ Grown first, so a panel opened and closed between two rebuilds is not reported as having
        // closed without ever having been seen to open.
        foreach (var id in Host.Panels.Keys) {
            shown.Add(id);
        }

        if (shown.Count == Host.Panels.Count) {
            return;
        }

        foreach (var id in shown.Where(id => !Host.Panels.ContainsKey(id)).ToList()) {
            shown.Remove(id);

            if (descriptors.TryGetValue(id, out var descriptor)) {
                descriptor.Closed?.Invoke();
            }
        }
    }

    /// <summary>The control showing the arrangement.</summary>
    public DockingHost Host { get; }

    /// <summary>What can be shown, in the order it was registered.</summary>
    public IReadOnlyList<PanelDescriptor> Panels => ordered;

    /// <summary>The named arrangements, in no particular order.</summary>
    public IReadOnlyCollection<string> Presets => presets.Keys;

    /// <summary>Which preset <see cref="Reset" /> goes back to.</summary>
    public string DefaultPreset {
        get => fallback;
        set {
            ArgumentNullException.ThrowIfNull(value);
            fallback = value;
        }
    }

    /// <summary>Raised after anything changes the arrangement.</summary>
    /// <remarks>The moment to save it. An application that persists layouts hangs off this — which
    ///     is what <see cref="DockingHost.LayoutChanged" /> says, forwarded so that a caller does
    ///     not have to know the host exists.</remarks>
    public event Action<DockingWorkspace>? Changed;

    /// <summary>Asked to register a panel a saved arrangement names and nothing declared.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Doc 20's A6: "open documents belong to an arrangement".</b> A panel a plugin
    ///         registers exists before the layout is applied, which is why the ordering note in this
    ///         class's own remarks works. An asset editor's panel does not: it is registered on demand
    ///         when somebody double-clicks a file, and named by the asset's GUID — so a restored
    ///         arrangement holding <c>asset.9e8a44c9…</c> would come back with the tab silently
    ///         missing and the id still in the file, which is the worst of both.
    ///     </para>
    ///     <para>
    ///         <b>A hook rather than a list of open documents in the layout file.</b> What a panel id
    ///         <i>means</i> is the application's — a GUID, a search query, a device — and a workspace
    ///         that knew would be a workspace that knows what an asset is. Answering
    ///         <see langword="true" /> means "I have registered it now"; answering false leaves the
    ///         id in the arrangement, which is the same bargain a plugin's panel already gets.
    ///     </para>
    /// </remarks>
    public Func<string, bool>? Resolve { get; set; }

    /// <summary>Declares a panel.</summary>
    /// <param name="descriptor">What it is.</param>
    /// <returns>The descriptor.</returns>
    /// <exception cref="ArgumentException">Something is already registered under that id.</exception>
    public PanelDescriptor Register(PanelDescriptor descriptor) {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (!descriptors.TryAdd(descriptor.Id, descriptor)) {
            throw new ArgumentException($"A panel is already registered as '{descriptor.Id}'.", nameof(descriptor));
        }

        ordered.Add(descriptor);
        return descriptor;
    }

    /// <summary>Declares a panel from its parts.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="title">What its tab says.</param>
    /// <param name="build">Fills it.</param>
    /// <returns>The descriptor.</returns>
    public PanelDescriptor Register(string id, StringId title, Action<DockPanel> build) =>
        Register(new PanelDescriptor(id, title, build));

    /// <summary>Takes a panel out of the registry, closing it if it is open.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>Whether it was registered.</returns>
    /// <remarks>
    ///     <para>
    ///         What unloading a plugin does, and the reason it closes rather than merely forgetting:
    ///         the panel's elements were built by the plugin's factory, so a panel left docked would
    ///         be a frame the user cannot rebuild and — the half that matters — a live reference into
    ///         an assembly the editor is trying to collect.
    ///     </para>
    ///     <para>
    ///         The saved layout still names it, which is the same bargain the keymap makes with a
    ///         plugin's shortcut: reinstalling the plugin puts the panel back where it was rather
    ///         than in the first group.
    ///     </para>
    /// </remarks>
    public bool Unregister(string id) {
        ArgumentNullException.ThrowIfNull(id);

        if (!descriptors.TryGetValue(id, out var descriptor)) {
            return false;
        }

        // ⚠ Closed while the descriptor is still registered, so `Retire` can find it and run the
        // panel's teardown. Removing it first would make unregistering the one path that silently
        // skips `PanelDescriptor.Closed` — and unloading a plugin is exactly when the thing being
        // released matters most.
        Close(id);

        descriptors.Remove(id);
        ordered.Remove(descriptor);

        Changed?.Invoke(this);
        return true;
    }

    /// <summary>Whether a panel is currently in the arrangement.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>Whether it is.</returns>
    public bool IsOpen(string id) {
        ArgumentNullException.ThrowIfNull(id);
        return Host.Panels.ContainsKey(id);
    }

    /// <summary>Shows a panel, building it the first time.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>The panel, or <c>null</c> if nothing is registered under that id.</returns>
    /// <remarks>
    ///     A panel that is already open is brought to the front of its group rather than rebuilt,
    ///     which is what "show me the console" means when the console is behind another tab.
    /// </remarks>
    public DockPanel? Open(string id) {
        ArgumentNullException.ThrowIfNull(id);

        if (!descriptors.TryGetValue(id, out var descriptor)
            && (Resolve?.Invoke(id) ?? false)
            && descriptors.TryGetValue(id, out var registered)) {
            descriptor = registered;
        }

        if (descriptor is null) {
            return null;
        }

        if (Host.Panels.TryGetValue(id, out var existing)) {
            Select(id);
            return existing;
        }

        var panel = Host.AddPanel(id, descriptor.Title.Text);
        panel.CanClose = descriptor.CanClose;

        descriptor.Build(panel);

        // ⚠ After the contents, because building them adds elements and adding an element to a
        // panel that is not the selected tab of its group leaves it laid out and invisible — which
        // is fine, and which a selection makes right in one place rather than in every factory.
        Select(id);

        return panel;
    }

    /// <summary>Takes a panel out of the arrangement.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>Whether it was there.</returns>
    /// <remarks>
    ///     The elements go with it, so reopening runs the factory again. That is what closing a
    ///     panel means — an editor that kept a closed panel's tree alive would keep a scene view's
    ///     render target alive with it.
    /// </remarks>
    public bool Close(string id) => Host.RemovePanel(id);

    /// <summary>Shows a panel if it is hidden and hides it if it is shown.</summary>
    /// <param name="id">Its id.</param>
    public void Toggle(string id) {
        if (IsOpen(id)) {
            Close(id);
        } else {
            Open(id);
        }
    }

    /// <summary>Brings a panel to the front of its group.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>Whether the arrangement has it.</returns>
    public bool Select(string id) {
        if (Host.Layout.Find(id) is not { } located) {
            return false;
        }

        var (group, index) = located;
        group.Selected = index;
        Host.Rebuild();

        return true;
    }

    /// <summary>Closes the panel the user is in.</summary>
    /// <returns>Whether there was one to close.</returns>
    /// <remarks>
    ///     ⚠ <b>A panel that says it cannot be closed is not closed.</b>
    ///     <see cref="PanelDescriptor.CanClose" /> is what a panel the arrangement depends on
    ///     declares, and Ctrl+W is the one path to closing that does not go past the tab's own close
    ///     button — which is where that flag is otherwise honoured.
    /// </remarks>
    public bool CloseActive() {
        if (Host.Active is not { } panel) {
            return false;
        }

        return descriptors.TryGetValue(panel.Id, out var descriptor) && !descriptor.CanClose
            ? false
            : Close(panel.Id);
    }

    /// <summary>Moves to the next or previous tab of the group the user is in.</summary>
    /// <param name="delta">How far, and which way. <c>1</c> is the next tab.</param>
    /// <returns>Whether there was a group with more than one tab in it.</returns>
    /// <remarks>
    ///     ⚠ <b>It wraps.</b> Ctrl+Tab that stops at the last tab is one people press twice and then
    ///     reach for the mouse; every editor with tabs wraps, and a group of two makes the wrap the
    ///     only useful behaviour.
    /// </remarks>
    public bool CycleTab(int delta) {
        if (Host.Active is not { } panel || Host.Layout.Find(panel.Id) is not var (group, index)) {
            return false;
        }

        if (group.Panels.Count < 2) {
            return false;
        }

        var count = group.Panels.Count;

        group.Selected = (((index + delta) % count) + count) % count;
        Host.Rebuild();

        return true;
    }

    /// <summary>Takes the panel the user is in out into a window of its own.</summary>
    /// <returns>Whether there was one to take out.</returns>
    /// <remarks>
    ///     The menu path to what dragging a tab off the window already does — there for the same
    ///     reason every command in the editor has one: a gesture nobody discovers is a feature nobody
    ///     has. Where there can be no second window it does nothing and says so, which is what the
    ///     command's enablement reads.
    /// </remarks>
    public bool FloatActive() => Host.CanTearOut && Host.Active is { } panel && Host.Float(panel.Id);

    /// <summary>Whether <see cref="FloatActive" /> would do anything.</summary>
    /// <remarks>
    ///     False for a panel that is already in a window of its own — the command would take it out
    ///     of the window it is in and put it in an identical one, which is a menu item that appears
    ///     to do nothing at all.
    /// </remarks>
    public bool CanFloatActive =>
        Host.CanTearOut
        && Host.Active is { } panel
        && Host.Layout.Find(panel.Id) is var (group, _)
        && Host.Layout.IndexOfFloating(group) < 0;

    /// <summary>Declares a named arrangement.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="layout">Builds it, fresh each time, because applying one mutates it.</param>
    /// <remarks>
    ///     ⚠ <b>A factory rather than a layout.</b> A preset handed out as an object is a preset the
    ///     first splitter drag edits — and "reset to Default" then puts the window back to whatever
    ///     the user last dragged it to, which is the one thing reset exists not to do.
    /// </remarks>
    public void AddPreset(string name, Func<DockLayout> layout) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(layout);

        presets[name] = layout;

        if (fallback.Length == 0) {
            fallback = name;
        }
    }

    /// <summary>Takes a named arrangement out.</summary>
    /// <param name="name">Which one.</param>
    /// <returns>Whether there was one by that name.</returns>
    /// <remarks>
    ///     ⚠ <b>The fallback is not reassigned.</b> If the preset removed was the one
    ///     <see cref="Reset" /> goes back to, reset stops working until something declares another —
    ///     which is correct: silently promoting whichever preset happened to be registered next
    ///     would make "reset layout" mean something different depending on which plugins are
    ///     installed.
    /// </remarks>
    public bool RemovePreset(string name) {
        ArgumentNullException.ThrowIfNull(name);
        return presets.Remove(name);
    }

    /// <summary>Shows a named arrangement.</summary>
    /// <param name="name">Which one.</param>
    /// <returns>Whether there is one by that name.</returns>
    public bool Apply(string name) {
        ArgumentNullException.ThrowIfNull(name);

        if (!presets.TryGetValue(name, out var build)) {
            return false;
        }

        Install(build());
        return true;
    }

    /// <summary>Puts the arrangement back to <see cref="DefaultPreset" />.</summary>
    /// <returns>Whether there was one to go back to.</returns>
    public bool Reset() => Apply(fallback);

    /// <summary>The arrangement as YAML.</summary>
    /// <returns>The text.</returns>
    public string Save() => Host.Save();

    /// <summary>Shows a saved arrangement, building the panels it names.</summary>
    /// <param name="yaml">What <see cref="Save" /> wrote.</param>
    /// <remarks>
    ///     A layout that names nothing this editor knows leaves an empty window, which is worse than
    ///     a wrong one — so an arrangement whose panels are all unregistered falls back to the
    ///     default preset rather than being applied.
    /// </remarks>
    public void Load(string yaml) {
        ArgumentNullException.ThrowIfNull(yaml);

        var layout = DockLayout.Load(yaml);
        var known = layout.Groups().SelectMany(group => group.Panels).Any(descriptors.ContainsKey);

        if (!known) {
            Reset();
            return;
        }

        Install(layout);
    }

    /// <summary>Applies an arrangement and opens what it names.</summary>
    /// <param name="layout">The arrangement.</param>
    /// <remarks>
    ///     ⚠ <b>Public because an arrangement is not always a preset or a file.</b>
    ///     <see cref="Apply" /> takes a registered name and <see cref="Load" /> takes what
    ///     <see cref="Save" /> wrote; maximising a panel is neither — it is one panel filling the
    ///     window and the previous arrangement kept in a string until it is put back. Registering a
    ///     preset for it would put "Maximised" in the layout palette, which is not a layout anybody
    ///     chooses from a list.
    /// </remarks>
    public void Show(DockLayout layout) {
        ArgumentNullException.ThrowIfNull(layout);
        Install(layout);
    }

    void Install(DockLayout layout) {
        Host.SetLayout(layout);

        // ⚠ Which tab was in front is read *before* anything is opened and written back afterwards.
        // `Open` brings a panel to the front — which is what it means everywhere else — so opening
        // the four panels a saved layout names would otherwise leave whichever happened to be last
        // showing, and a restored arrangement would come back with the wrong tab selected every
        // time. The saved selection is the user's and outranks the order the panels were built in.
        var selected = layout.Groups().Select(group => (Group: group, group.Selected)).ToArray();

        // A copy, because opening a panel rebuilds the arrangement and a group's panel list is the
        // thing being walked. Enumerating it live throws on the first panel that gets docked.
        foreach (var id in selected.SelectMany(entry => entry.Group.Panels).ToArray()) {
            Open(id);
        }

        foreach (var (group, index) in selected) {
            group.Selected = Math.Clamp(index, 0, Math.Max(0, group.Panels.Count - 1));
        }

        Host.Rebuild();
    }
}
