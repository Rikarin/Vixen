// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Plugin;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.App;

/// <summary>The plugin manager doc 11 calls "a view and nothing more", plus the three verbs.</summary>
/// <remarks>
///     <para>
///         <b>A grid over <see cref="PluginHost.Plugins" />, which has held everything this needs
///         since it was written.</b> A <see cref="LoadedPlugin" /> carries the manifest, the state,
///         the failure and the registration count, and <see cref="PluginDescriptor" /> is
///         deliberately "the result of reading, not of loading" — so a plugin that is disabled,
///         incompatible or broken is an ordinary row rather than an absence.
///     </para>
///     <para>
///         ⚠ <b>Enable, disable and reload, which is more than doc 11's "a view".</b> Doc 20's E3
///         exit criterion is that a plugin can be enabled, disabled <i>and</i> reloaded from a panel,
///         and the difference matters: the plugin-development loop is build, reload, look — and the
///         plugin-that-broke-my-editor loop is disable and restart. Both need somewhere to click.
///     </para>
///     <para>
///         ⚠ <b>The failure is under the grid rather than in a column.</b> A plugin that did not
///         start says why in a sentence — a missing dependency, a type that is not there, an
///         exception from its own <c>Activate</c> — and a sentence in a table cell is a sentence
///         nobody reads. It is also where the "did not unload cleanly" warning belongs, which is the
///         one failure the runtime reports by saying nothing at all.
///     </para>
/// </remarks>
sealed partial class PluginManagerView : Control {
    readonly List<LoadedPlugin> rows = [];

    PluginHost? host;
    string? filter;

    /// <inheritdoc />
    protected override string TagName => "plugin-manager";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The strip along the top.</summary>
    public UiElement Toolbar { get; private set; } = null!;

    /// <summary>The filter box.</summary>
    public SearchBox Search { get; private set; } = null!;

    /// <summary>Switches the selected plugin on or off.</summary>
    public Button Toggle { get; private set; } = null!;

    /// <summary>Unloads it and loads it again from disk.</summary>
    public Button Reload { get; private set; } = null!;

    /// <summary>The grid.</summary>
    public DataGrid Grid { get; private set; } = null!;

    /// <summary>The line under it saying what the selected plugin is, or why it did not start.</summary>
    public UiElement Detail { get; private set; } = null!;

    /// <summary>Raised after a plugin is switched on or off, so the choice can be persisted.</summary>
    public event Action<PluginManagerView>? Toggled;

    /// <summary>Which plugin the grid has selected, or <see langword="null" />.</summary>
    public LoadedPlugin? Selected =>
        Grid.Selection.Count == 1 && Grid.Items.ElementAtOrDefault(Grid.Selection.First()) is LoadedPlugin plugin
            ? plugin
            : null;

    /// <summary>Points the panel at a host.</summary>
    /// <param name="plugins">The host.</param>
    public void Show(PluginHost plugins) {
        ArgumentNullException.ThrowIfNull(plugins);

        Detach();

        host = plugins;
        plugins.Changed += Changed;

        Rebuild();
    }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Toolbar = Part("plugin-toolbar");

        Search = Toolbar.Add<SearchBox>();
        Search.Placeholder = EditorStrings.PluginsFilter.Text;

        Search.ValueChanged += (_, value) => {
            filter = string.IsNullOrWhiteSpace(value) ? null : value;
            Rebuild();
        };

        Toggle = Command(EditorStrings.PluginsDisable, Switch);
        Reload = Command(EditorStrings.PluginsReload, Restart);

        Grid = Part<DataGrid>();
        Grid.MultiSelect = false;

        Column(EditorStrings.PluginsColumnName.Text, 190f, plugin => plugin.Manifest.Name);
        Column(EditorStrings.PluginsColumnId.Text, 190f, plugin => plugin.Id);
        Column(EditorStrings.PluginsColumnVersion.Text, 80f, plugin => plugin.Manifest.Version.ToString(3));
        Column(EditorStrings.PluginsColumnState.Text, 90f, StateOf);
        Column(EditorStrings.PluginsColumnAuthor.Text, 140f, plugin => plugin.Manifest.Author);

        Grid.SelectionChanged += _ => Restate();

        Detail = Part("plugin-detail");
        Restate();
    }

    /// <inheritdoc />
    protected override void OnRemoved() {
        Detach();
        base.OnRemoved();
    }

    /// <summary>Rebuilds the list from the host, honouring the filter.</summary>
    public void Rebuild() {
        var chosen = Selected?.Id;

        rows.Clear();

        if (host is { } plugins) {
            foreach (var plugin in plugins.Plugins) {
                if (Matches(plugin)) {
                    rows.Add(plugin);
                }
            }
        }

        rows.Sort(static (left, right) => string.CompareOrdinal(left.Manifest.Name, right.Manifest.Name));
        Grid.SetItems(rows);

        if (chosen is not null) {
            // ⚠ By id rather than by reference. `Reload` builds a *new* `LoadedPlugin` for the same
            // plugin — the old one is removed from the host's list — so a selection kept by identity
            // would empty itself every time somebody pressed Reload, which is the one button they
            // press repeatedly.
            var index = rows.FindIndex(plugin => string.Equals(plugin.Id, chosen, StringComparison.Ordinal));

            if (index >= 0) {
                Grid.Select(index);
            }
        }

        Restate();
    }

    void Switch() {
        if (host is not { } plugins || Selected is not { } plugin) {
            return;
        }

        if (plugins.IsSuppressed(plugin.Id)) {
            plugins.Enable(plugin.Id);
        } else {
            plugins.Disable(plugin.Id);
        }

        Rebuild();
        Toggled?.Invoke(this);
    }

    void Restart() {
        if (host is { } plugins && Selected is { } plugin) {
            plugins.Reload(plugin.Id);
            Rebuild();
        }
    }

    Button Command(StringId label, Action run) {
        var button = Toolbar.Add<Button>();

        button.Label = label.Text;
        button.Variant = ControlVariant.Subtle;
        button.Size = ControlSize.Small;
        button.Clicked += _ => run();

        return button;
    }

    void Column(string header, float width, Func<LoadedPlugin, string> value) {
        var column = Grid.AddColumn(header, item => item is LoadedPlugin plugin ? value(plugin) : string.Empty);

        column.Width = width;
    }

    static string StateOf(LoadedPlugin plugin) =>
        plugin.State switch {
            PluginState.Active => EditorStrings.PluginsStateActive.Text,
            PluginState.Failed => EditorStrings.PluginsStateFailed.Text,
            PluginState.Unloaded => EditorStrings.PluginsStateUnloaded.Text,
            _ => EditorStrings.PluginsStateDisabled.Text
        };

    bool Matches(LoadedPlugin plugin) =>
        filter is null
        || plugin.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || plugin.Manifest.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || plugin.Manifest.Author.Contains(filter, StringComparison.OrdinalIgnoreCase);

    void Restate() {
        var plugin = Selected;

        Toggle.Disabled = plugin is null;
        Reload.Disabled = plugin is null;

        Toggle.Label = plugin is not null && host?.IsSuppressed(plugin.Id) == true
            ? EditorStrings.PluginsEnable.Text
            : EditorStrings.PluginsDisable.Text;

        Detail.RemoveClass("failed");

        if (plugin is null) {
            Detail.Text = host is { Plugins.Count: 0 }
                ? EditorStrings.PluginsNone.Text
                : EditorStrings.PluginsPickRow.Text;

            return;
        }

        if (plugin.Failure is { } failure) {
            Detail.AddClass("failed");
            Detail.Text = failure.Message;

            return;
        }

        // ⚠ A plugin whose manifest says `enabled: false` and one the user switched off read the
        // same in the State column and are not the same thing: one is the author's decision in a
        // file the whole team shares, and the other is this user's. Only the second can be undone
        // from here, so only the second is worth a sentence.
        if (host?.Suppressed.Contains(plugin.Id) == true) {
            Detail.Text = EditorStrings.PluginsSwitchedOff.Text;
            return;
        }

        if (!plugin.Manifest.Enabled) {
            Detail.Text = EditorStrings.PluginsManifestOff.Text;
            return;
        }

        Detail.Text = plugin.Manifest.Description is { Length: > 0 } description
            ? description
            : plugin.Descriptor.Directory;
    }

    /// <inheritdoc cref="MessageLogView.Detach" />
    void Detach() {
        if (host is not null) {
            host.Changed -= Changed;
        }
    }

    void Changed(LoadedPlugin _) => Rebuild();
}
