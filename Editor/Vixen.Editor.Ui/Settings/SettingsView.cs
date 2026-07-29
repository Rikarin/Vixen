// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Styling;

namespace Vixen.Editor.Ui;

/// <summary>One page of a settings window: a line in the rail, and what fills the pane.</summary>
/// <param name="Id">What a saved selection and a search result call it.</param>
/// <param name="Title">What the rail says.</param>
/// <param name="Build">Fills the pane. Called the first time the page is shown, and again after a revert.</param>
/// <remarks>
///     ⚠ <b>A factory rather than an element, for <see cref="PanelDescriptor" />'s reason.</b> A
///     settings window with nine pages builds one; building the other eight would stand up nine
///     inspectors over nine settings objects the user has not asked to look at. It is also what makes
///     Revert a rebuild rather than nine drawers each having to know how to un-edit themselves.
/// </remarks>
public sealed record SettingsCategory(string Id, StringId Title, Action<UiElement> Build) {
    /// <summary>Puts this page's settings back to their defaults, or <see langword="null" /> if it cannot.</summary>
    /// <remarks>
    ///     Doc 20's A4 asks for "a Reset per category" rather than one for the window: a settings
    ///     window whose only reset throws away the other eight pages as well is one nobody presses.
    /// </remarks>
    public Action? Reset { get; init; }

    /// <summary>The words a search over every setting should match this page on.</summary>
    /// <remarks>
    ///     ⚠ <b>The page supplies them, because only it knows what is on it.</b> Doc 20 asks for "a
    ///     search box over every setting in every category", and a window that could only search the
    ///     nine category names would answer the easy half of that question. A page drawn from an
    ///     <c>[Inspector]</c> descriptor hands back its member names, which is the list somebody is
    ///     actually typing a fragment of.
    /// </remarks>
    public Func<IEnumerable<string>>? Keywords { get; init; }
}

/// <summary>The window behind both Preferences and Project Settings: a rail, a pane, and an Apply.</summary>
/// <remarks>
///     <para>
///         <b>Doc 20's A4 is "two windows, one mechanism", and this is the mechanism.</b> The only
///         difference between Preferences and Project Settings is whose store the pages are over —
///         the user's or the project's — so a second implementation of the rail, the search, the
///         reset and the dirty tracking would be a second place for those four to be subtly wrong.
///     </para>
///     <para>
///         ⚠ <b>A panel rather than a modal, and doc 20's A2 is why.</b> Everything modal in this
///         editor is a drawn <see cref="Dialog" /> in the shell's own document, so that the golden
///         suite can photograph it and the automation harness can drive it — and a settings window is
///         the one thing people leave open beside what they are changing. Registered as a panel, it
///         docks, it tabs, and <c>view.float-panel</c> gives it a real operating-system window when
///         the user wants one, which is the same answer doc 20's "two windows" asks for by a
///         different route.
///     </para>
///     <para>
///         ⚠ <b>Nothing is written on a keystroke.</b> Doc 20 is explicit: the layout file's rule —
///         written on the way down — applies here for the same reason, with an explicit Apply for
///         anything that costs something to change. So an edit marks the window dirty,
///         <see cref="Apply" /> is what commits, and <see cref="Revert" /> rebuilds the page from
///         what is still on disk.
///     </para>
/// </remarks>
public sealed partial class SettingsView : Control {
    readonly List<SettingsCategory> categories = [];
    readonly Dictionary<string, Button> tabs = new(StringComparer.Ordinal);

    string? current;
    string? filter;

    /// <inheritdoc />
    protected override string TagName => "settings-view";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The search box over every setting on every page.</summary>
    public SearchBox Search { get; private set; } = null!;

    /// <summary>The list of pages down the left.</summary>
    public UiElement Rail { get; private set; } = null!;

    /// <summary>Where the chosen page is built.</summary>
    public UiElement Pane { get; private set; } = null!;

    /// <summary>The strip along the bottom.</summary>
    public UiElement Footer { get; private set; } = null!;

    /// <summary>Puts this page back to its defaults.</summary>
    public Button ResetPage { get; private set; } = null!;

    /// <summary>Throws away what has been typed since the last write.</summary>
    public Button Revert { get; private set; } = null!;

    /// <summary>Writes it.</summary>
    public Button Apply { get; private set; } = null!;

    /// <summary>The pages, in the order they were added.</summary>
    public IReadOnlyList<SettingsCategory> Categories => categories;

    /// <summary>Which page is showing, or <see langword="null" /> before one has been chosen.</summary>
    public string? Current => current;

    /// <summary>Whether something has been edited and not yet written.</summary>
    /// <remarks>
    ///     ⚠ <b>Set by whoever built the page, because only it knows what an edit is.</b> A settings
    ///     object is a plain <c>[DataContract]</c> with no change notification of its own — which is
    ///     the price of it being the same kind of type as everything else the inspector draws, and
    ///     the same reason <c>ProjectSettingsStore.MarkChanged</c> is explicit.
    /// </remarks>
    public bool IsDirty {
        get;

        set {
            if (field == value) {
                return;
            }

            field = value;
            Restate();
        }
    }

    /// <summary>Raised when the user asks for the edits to be written.</summary>
    public event Action<SettingsView>? Applied;

    /// <summary>Raised when they ask for them to be thrown away.</summary>
    /// <remarks>The page is rebuilt afterwards, so a handler only has to reload the model.</remarks>
    public event Action<SettingsView>? Reverted;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        var header = Part("settings-header");

        Search = header.Add<SearchBox>();
        Search.Placeholder = EditorStrings.SettingsSearch.Text;

        Search.ValueChanged += (_, value) => {
            filter = string.IsNullOrWhiteSpace(value) ? null : value;
            Rebuild();
        };

        var body = Part("settings-body");

        Rail = body.Add<UiElement>("settings-rail");
        Pane = body.Add<UiElement>("settings-pane");

        Footer = Part("settings-footer");

        ResetPage = Command(EditorStrings.SettingsResetPage, ControlVariant.Subtle, ResetCurrent);
        Footer.Add<UiElement>("settings-spacer");

        Revert = Command(EditorStrings.SettingsRevert, ControlVariant.Subtle, () => {
                Reverted?.Invoke(this);

                IsDirty = false;
                Reload();
            }
        );

        Apply = Command(EditorStrings.SettingsApply, ControlVariant.Primary, () => {
                Applied?.Invoke(this);
                IsDirty = false;
            }
        );

        Restate();
    }

    /// <summary>Adds a page.</summary>
    /// <param name="category">What it is.</param>
    /// <returns>The category.</returns>
    /// <exception cref="ArgumentException">Something is already registered under that id.</exception>
    public SettingsCategory Add(SettingsCategory category) {
        ArgumentNullException.ThrowIfNull(category);

        if (categories.Any(existing => string.Equals(existing.Id, category.Id, StringComparison.Ordinal))) {
            throw new ArgumentException($"A settings page is already registered as '{category.Id}'.", nameof(category));
        }

        categories.Add(category);
        Rebuild();

        return category;
    }

    /// <summary>Shows a page.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>Whether there is one by that id.</returns>
    public bool Select(string id) {
        ArgumentNullException.ThrowIfNull(id);

        if (Find(id) is null) {
            return false;
        }

        current = id;
        Reload();

        return true;
    }

    /// <summary>Builds the current page again, from whatever the model now says.</summary>
    /// <remarks>What Revert does, and what an application calls after reloading a settings store.</remarks>
    public void Reload() {
        while (Pane.Children.Count > 0) {
            Pane.Children[^1].Remove();
        }

        if (current is not null && Find(current) is { } page) {
            page.Build(Pane);
        } else {
            Pane.Add<EmptyState>().Title = EditorStrings.SettingsNoPage.Text;
        }

        Restate();
    }

    /// <summary>Rebuilds the rail, honouring the filter, and keeps a page selected.</summary>
    void Rebuild() {
        while (Rail.Children.Count > 0) {
            Rail.Children[^1].Remove();
        }

        tabs.Clear();

        var visible = categories.Where(Matches).ToList();

        foreach (var page in visible) {
            var button = Rail.Add<Button>();
            var id = page.Id;

            button.Label = page.Title.Text;
            button.Variant = ControlVariant.Subtle;
            button.AddClass("settings-tab");
            button.Clicked += _ => Select(id);

            tabs[id] = button;
        }

        // ⚠ The filter chooses a page as well as narrowing the rail, and that is what makes the
        // search a search over settings rather than over headings: typing "orbit" should land on the
        // page that has it, not leave the user to notice that the rail now has one line and click it.
        if (visible.Count > 0 && (current is null || !visible.Any(page => string.Equals(page.Id, current, StringComparison.Ordinal)))) {
            Select(visible[0].Id);
            return;
        }

        Restate();
    }

    void ResetCurrent() {
        if (current is not null && Find(current)?.Reset is { } reset) {
            reset();

            IsDirty = true;
            Reload();
        }
    }

    Button Command(StringId label, ControlVariant variant, Action run) {
        var button = Footer.Add<Button>();

        button.Label = label.Text;
        button.Variant = variant;
        button.Size = ControlSize.Small;
        button.Clicked += _ => run();

        return button;
    }

    SettingsCategory? Find(string id) =>
        categories.FirstOrDefault(page => string.Equals(page.Id, id, StringComparison.Ordinal));

    bool Matches(SettingsCategory page) {
        if (filter is null) {
            return true;
        }

        if (page.Title.Text.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || page.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        return page.Keywords?.Invoke().Any(word => word.Contains(filter, StringComparison.OrdinalIgnoreCase)) ?? false;
    }

    void Restate() {
        foreach (var (id, button) in tabs) {
            if (string.Equals(id, current, StringComparison.Ordinal)) {
                button.State |= ElementState.Checked;
            } else {
                button.State &= ~ElementState.Checked;
            }
        }

        ResetPage.Disabled = current is null || Find(current)?.Reset is null;
        Revert.Disabled = !IsDirty;
        Apply.Disabled = !IsDirty;
    }
}
