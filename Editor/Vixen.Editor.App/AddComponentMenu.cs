// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Editor.SceneView;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Styling;

namespace Vixen.Editor.App;

/// <summary>One line of the Add Component picker: a category to open, or a thing to add.</summary>
/// <remarks>
///     A <see cref="ButtonBase" /> rather than a <c>MenuItem</c>, for <c>PaletteRow</c>'s reason: the
///     rows must not take the focus, because the field above them has it and every keystroke belongs
///     to the query. What they carry beyond a label is the arrow that says a line opens something
///     rather than doing something, and the quiet word on the right that says which kind of thing it
///     is.
/// </remarks>
sealed partial class AddComponentRow : ButtonBase {
    /// <inheritdoc />
    protected override string TagName => "add-component-row";

    /// <summary>The word at the right: a category, or "Script".</summary>
    public UiElement DetailPart { get; private set; } = null!;

    /// <summary>The chevron shown on a line that opens a category.</summary>
    public Icon Arrow { get; private set; } = null!;

    /// <summary>Which line it is, as an index into what the picker is showing.</summary>
    public int Index { get; internal set; } = -1;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        DetailPart = Part("add-component-detail");

        Arrow = Part<Icon>();
        Arrow.Geometry = ControlIcons.ChevronRight;
    }
}

/// <summary>What the Add Component button drops: a search, then categories, then what is in one.</summary>
/// <remarks>
///     <para>
///         <b>A control rather than a <see cref="ContextMenu" />, and the three reasons are the three
///         things a menu cannot do.</b> A menu is a list of items and cannot hold a text field above
///         them — <see cref="Menu.Clear" /> takes every child, and <c>OnOpened</c> focuses the first
///         item, which is the field's focus taken away on the frame it is wanted. A menu also sizes
///         itself to its longest line, so the drop under a stretched button was a narrow column
///         floating under a wide control. And a menu's submenu hangs off the side, which for a panel
///         docked at the right edge of the window opens off-screen.
///     </para>
///     <para>
///         ⚠ <b>Categories first and one level deep, which is what makes the list readable at all.</b>
///         A project with a dozen of its own components plus the engine's is sixty-odd lines, sorted
///         by name, in which <c>Audio Source</c> sits between <c>Angular Velocity</c> and
///         <c>Camera</c> — a list where finding anything means reading all of it. The categories are
///         the shape somebody already has in their head, and going into one replaces the content
///         rather than opening a second floating box beside the first.
///     </para>
///     <para>
///         ⚠ <b>The search is over components and behaviours, never over categories.</b> Typing is
///         what somebody does when they know the name, and a query that matched "Audio" the
///         <i>category</i> would answer a question about our filing with a folder to open — one more
///         click, at the moment they had already told us exactly what they wanted. A match's category
///         is shown on the right instead, which says the same thing and costs nothing.
///     </para>
///     <para>
///         ⚠ <b>The field keeps the focus and the rows are not focusable</b>, which is
///         <c>CommandPalette</c>'s arrangement and is the opposite of <see cref="Menu" />'s. A picker
///         where Down moved the focus into the list is one where the next letter typed goes nowhere.
///     </para>
/// </remarks>
sealed partial class AddComponentMenu : Overlay {
    /// <summary>One thing the picker can offer.</summary>
    /// <param name="Bridge">What is added when the line is chosen.</param>
    /// <param name="Category">Which group it is filed under.</param>
    internal readonly record struct Entry(IComponentBridge Bridge, string Category);

    readonly List<Entry> offered = [];
    readonly List<AddComponentRow> rows = [];

    /// <summary>What each line is currently showing: a category to open, or something to add.</summary>
    readonly List<(string? Category, Entry? Add)> lines = [];

    string? opened;
    int highlighted;

    /// <inheritdoc />
    protected override string TagName => "add-component-menu";

    /// <summary>What is typed into it.</summary>
    public SearchBox Field { get; private set; } = null!;

    /// <summary>Where the lines go.</summary>
    /// <remarks>
    ///     ⚠ <b>A real scroll region rather than <c>overflow: scroll</c> on a plain element.</b> The
    ///     layout understands the keyword and clips to it; what turns clipping into a wheel, a bar
    ///     and a keyboard is <see cref="ScrollView" />, and the list this drops is however many
    ///     components a project has.
    /// </remarks>
    public ScrollView List { get; private set; } = null!;

    /// <summary>What it says when a query matches nothing.</summary>
    public UiElement EmptyPart { get; private set; } = null!;

    /// <summary>Which category is open, or <see langword="null" /> for the top level.</summary>
    public string? Category => opened;

    /// <summary>Raised with what was chosen.</summary>
    public event Action<IComponentBridge>? Chose;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        IsFocusScope = true;
        LightDismiss = true;
        CloseOnEscape = true;

        Field = Part<SearchBox>();
        Field.Placeholder = "Search components…";

        List = Part<ScrollView>("add-component-list");
        EmptyPart = Part("add-component-empty");
        EmptyPart.Text = "Nothing matches";

        Field.ValueChanged += (_, _) => {
            // ⚠ The category is dropped the moment anything is typed. A search inside "Audio" that
            // silently answered from three components while the name being typed is a physics one is
            // a search box that appears to be broken — and the alternative, a scope somebody has to
            // notice and clear, is a mode.
            opened = null;
            highlighted = 0;

            Rebuild();
        };

        AddHandler<KeyEvent>(static (element, args) => ((AddComponentMenu) element).Keyed(args), RoutingStrategy.Capture);
        AddHandler<ClickEvent>(static (element, args) => ((AddComponentMenu) element).Chosen(args));
    }

    /// <summary>Drops the picker under a button, showing what may be added.</summary>
    /// <param name="anchor">The Add Component button.</param>
    /// <param name="entries">What is on offer, in no particular order.</param>
    /// <remarks>
    ///     ⚠ <b>As wide as the button, written as an inline width.</b> The button is stretched across
    ///     the inspector and the drop under it is the same gesture continued; a popup that came out
    ///     140 pixels wide under a 300-pixel control reads as a different, smaller thing having
    ///     happened. No rule can say this — the button's width is whatever the panel is this frame —
    ///     so it is measured and written, which is what <c>AssetGrid</c> does with a tile size for the
    ///     same reason.
    /// </remarks>
    public void OpenUnder(UiElement anchor, IEnumerable<Entry> entries) {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(entries);

        offered.Clear();
        offered.AddRange(entries);

        opened = null;
        highlighted = 0;

        // ⚠ Cleared on every open, for the reason `CommandPalette.OpenPalette` gives: a picker that
        // remembered the last query makes the common case start by deleting somebody else's word.
        Field.Value = string.Empty;

        Rebuild();

        SetStyle("width", anchor.Width.ToString("0.##", CultureInfo.InvariantCulture) + "px");

        Placement = Vixen.Ui.Controls.Placement.Bottom;
        Open(anchor);

        Document.Focus(Field);
    }

    /// <inheritdoc />
    protected override void OnOpened() {
        base.OnOpened();
        Document.Focus(Field);
    }

    /// <summary>Shows what is in a category, or the categories if given nothing.</summary>
    /// <param name="category">Which one, or <see langword="null" /> to go back to the top.</param>
    public void Show(string? category) {
        opened = category;
        highlighted = 0;

        Rebuild();
    }

    /// <summary>Chooses the highlighted line: opens a category, or adds a component.</summary>
    /// <returns>Whether there was one.</returns>
    public bool Accept() {
        if (highlighted < 0 || highlighted >= lines.Count) {
            return false;
        }

        var (category, add) = lines[highlighted];

        if (add is { } entry) {
            // ⚠ Closed before the component is added, for `CommandPalette.Accept`'s reason: adding
            // rebuilds the inspector under the picker, and a popup still standing over a panel whose
            // elements have all been replaced is a popup anchored to nothing.
            Close(CloseReason.Committed);
            Chose?.Invoke(entry.Bridge);

            return true;
        }

        if (category is null) {
            return false;
        }

        // ⚠ The query is cleared going into a category rather than kept. There is only ever a
        // category showing when the query is empty — typing drops the scope, above — so this is the
        // "Back" case, and coming back to a filtered list nobody typed would be a list that is
        // missing things for no visible reason.
        Field.Value = string.Empty;
        Show(category.Length == 0 ? null : category);

        return true;
    }

    /// <summary>Moves the highlight, wrapping at both ends.</summary>
    /// <param name="delta">By how many lines.</param>
    public void Move(int delta) {
        if (lines.Count == 0) {
            return;
        }

        highlighted = (((highlighted + delta) % lines.Count) + lines.Count) % lines.Count;
        Restyle();
    }

    /// <summary>What the picker is showing, as the lines a test can read.</summary>
    /// <remarks>
    ///     Includes the parked ones — the pool is bounded by however many components exist and rows
    ///     past <see cref="LineCount" /> are hidden rather than removed, so a caller reading this
    ///     filters on <see cref="AddComponentRow.Index" /> as the click handler does.
    /// </remarks>
    public IReadOnlyList<AddComponentRow> Rows => rows;

    /// <summary>How many lines are showing, whatever kind they are.</summary>
    public int LineCount => lines.Count;

    /// <summary>Everything this opening may add, whatever is currently on screen.</summary>
    /// <remarks>
    ///     The whole offer rather than the visible slice of it, because "what can be added to this
    ///     entity" is a question about the entity and the registry, and the categories and the query
    ///     are a way of getting at the answer rather than part of it.
    /// </remarks>
    public IReadOnlyList<Entry> Offered => offered;

    void Rebuild() {
        var query = Field.Value ?? string.Empty;

        lines.Clear();

        if (query.Length > 0) {
            Matches(query);
        } else if (opened is null) {
            Categories();
        } else {
            Contents(opened);
        }

        highlighted = Math.Clamp(highlighted, 0, Math.Max(0, lines.Count - 1));
        Build();
    }

    /// <summary>The categories, by name, each with what is in it.</summary>
    void Categories() {
        foreach (var category in offered
                     .Select(entry => entry.Category)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)) {
            lines.Add((category, null));
        }

        if (lines.Count == 0) {
            return;
        }

        // ⚠ A category holding one thing is still a category. Flattening it would make the top level
        // a mixture of headings and components with an arrow deciding which is which — the shape this
        // arrangement exists to get away from — and it would move as a project adds a second one.
    }

    /// <summary>What is filed under a category, plus the line back out of it.</summary>
    void Contents(string category) {
        // The empty string is the sentinel for "the top level", because the back line is a category
        // line and a category line carries a name.
        lines.Add((string.Empty, null));

        foreach (var entry in Within(category)) {
            lines.Add((null, entry));
        }
    }

    IEnumerable<Entry> Within(string category) =>
        offered
            .Where(entry => string.Equals(entry.Category, category, StringComparison.Ordinal))
            .OrderBy(entry => entry.Bridge.DisplayName, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>Everything whose name matches, across every category.</summary>
    /// <remarks>
    ///     ⚠ <b>Ranked so that a prefix beats a substring.</b> Typing "cam" for <c>Camera</c> and
    ///     getting <c>Virtual Camera Body</c> first is a list that has to be read anyway, which is the
    ///     thing typing was meant to avoid. Ordinal-ignore-case rather than a fuzzy match, because a
    ///     component name is a thing somebody is spelling rather than approximating — the palette is
    ///     where fuzzy belongs, and it is one chord away.
    /// </remarks>
    void Matches(string query) {
        foreach (var entry in offered
                     .Select(entry => (Entry: entry, Rank: Rank(entry.Bridge.DisplayName, query)))
                     .Where(scored => scored.Rank >= 0)
                     .OrderBy(scored => scored.Rank)
                     .ThenBy(scored => scored.Entry.Bridge.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                     .Select(scored => scored.Entry)) {
            lines.Add((null, entry));
        }
    }

    static int Rank(string name, string query) =>
        name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) ? 0
        : name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ? 1
        : -1;

    void Build() {
        while (rows.Count < lines.Count) {
            var row = List.Content.Add<AddComponentRow>();
            row.Focusable = false;

            rows.Add(row);
        }

        for (var index = 0; index < rows.Count; index++) {
            var row = rows[index];

            if (index >= lines.Count) {
                // Parked rather than removed, because the list is rebuilt on every keystroke — the
                // pool is bounded by however many components exist.
                row.AddClass("parked");
                row.Index = -1;

                continue;
            }

            var (category, add) = lines[index];

            row.RemoveClass("parked");
            row.Index = index;

            if (add is { } entry) {
                row.Label = entry.Bridge.DisplayName;
                row.Arrow.SetStyle("display", "none");

                // ⚠ The category on the right while searching and the kind while browsing. Inside
                // "Audio" every line is in Audio, so repeating it is a column of one word; in a
                // result list it is the only thing telling two similarly-named components apart.
                row.DetailPart.Text = opened is null && Field.Value is { Length: > 0 }
                    ? entry.Category
                    : entry.Bridge.Kind == AuthoringKind.Behavior ? "Script" : string.Empty;

                continue;
            }

            row.Arrow.SetStyle("display", "flex");
            row.DetailPart.Text = string.Empty;

            if (category is { Length: > 0 }) {
                row.Label = category;
                row.RemoveClass("back");

                // ⚠ The arrow is turned rather than swapped for another glyph, and it is turned here
                // rather than by a rule: a transform is not something this layout applies, so "the
                // same arrow, the other way" has to be the other chevron.
                row.Arrow.Geometry = ControlIcons.ChevronRight;

                // The count is the one fact a heading can carry that a name cannot, and it is what
                // says whether going in is worth a click.
                row.DetailPart.Text = Within(category).Count().ToString(CultureInfo.CurrentCulture);
            } else {
                row.Label = "All categories";
                row.Arrow.Geometry = ControlIcons.ChevronLeft;
                row.AddClass("back");
            }
        }

        EmptyPart.SetStyle("display", lines.Count == 0 ? "flex" : "none");
        Restyle();
    }

    void Restyle() {
        for (var index = 0; index < rows.Count; index++) {
            if (index == highlighted && index < lines.Count) {
                rows[index].State |= ElementState.Checked;
            } else {
                rows[index].State &= ~ElementState.Checked;
            }
        }
    }

    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed) {
            return;
        }

        switch (args.Key) {
            case InputKey.Down:
                Move(1);
                break;

            case InputKey.Up:
                Move(-1);
                break;

            case InputKey.Enter or InputKey.KeypadEnter:
                Accept();
                break;

            // ⚠ Left goes back out of a category and Escape still closes the whole thing, which is
            // the arrangement a menu has. Backspace is not used for it: the field is what has the
            // focus, and taking its Backspace would make the one key somebody presses to fix a typo
            // throw away the scope instead.
            case InputKey.Left when opened is not null && Field.Value is not { Length: > 0 }:
                Show(null);
                break;

            case InputKey.Right when opened is null && Field.Value is not { Length: > 0 }
                                     && highlighted < lines.Count && lines[highlighted].Category is { Length: > 0 } into:
                Show(into);
                break;

            default:
                return;
        }

        // ⚠ Handled on the capture leg, before the field sees it. A search box treats Enter as
        // "submit" and the arrows as caret movement, and both would fight the list.
        args.Handled = true;
    }

    void Chosen(ClickEvent args) {
        for (var element = args.Source; element is not null; element = element.Parent) {
            if (element is not AddComponentRow { Index: >= 0 } row) {
                continue;
            }

            highlighted = row.Index;
            Accept();

            args.Handled = true;
            return;
        }
    }
}
