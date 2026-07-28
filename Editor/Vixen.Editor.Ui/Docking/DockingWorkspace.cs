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

    string fallback = string.Empty;

    /// <summary>Builds a docking host into an element.</summary>
    /// <param name="host">Where it goes.</param>
    public DockingWorkspace(UiElement host) {
        ArgumentNullException.ThrowIfNull(host);

        Host = host.Add<DockingHost>();
        Host.LayoutChanged += _ => Changed?.Invoke(this);
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

        if (!descriptors.TryGetValue(id, out var descriptor)) {
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

    /// <summary>Shows a named arrangement.</summary>
    /// <param name="name">Which one.</param>
    /// <returns>Whether there is one by that name.</returns>
    public bool Apply(string name) {
        ArgumentNullException.ThrowIfNull(name);

        if (!presets.TryGetValue(name, out var build)) {
            return false;
        }

        Show(build());
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

        Show(layout);
    }

    /// <summary>Applies an arrangement and opens what it names.</summary>
    void Show(DockLayout layout) {
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
