// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Animation.Moves;
using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.AssetEditors.Animation;

/// <summary>A move set as a table, with the filter box that is a live query.</summary>
/// <remarks>
///     <para>
///         <b>The filter box is the point of the whole panel.</b> Typing the same query the runtime
///         would build shows what would be selected, in score order, with the breakdown — matched
///         facets, numeric proximity, penalties. "Why did it pick that clip" is otherwise
///         unanswerable, and the thing an author reaches for instead is a pairwise table they can
///         read, which is the design this document exists to avoid.
///     </para>
///     <para>
///         ⚠ <b>The query runs against the table and not against a content build.</b>
///         <see cref="MoveSetContent.Preview" /> bakes every row against nothing — selection reads
///         facets and traits and never touches a motion — so the answer is available while somebody
///         is still typing the row. An editor that needed the clips built to answer this would be one
///         nobody waits for.
///     </para>
///     <para>
///         ⚠ <b>Rows overridden by an overlay are struck through and named.</b> A composed set shows
///         the winner; the row underneath it is still in somebody's file, and a table that silently
///         showed only the winner is how an author spends an afternoon editing a row that no longer
///         has any effect.
///     </para>
/// </remarks>
public sealed class MoveSetView : Control {
    readonly List<(UiElement Row, MoveEntryRecord Entry)> rows = [];

    MoveSetDocument? document;
    MoveEntryRecord? selected;

    /// <inheritdoc />
    protected override string TagName => "moveset-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The bar across the top.</summary>
    public UiElement Bar { get; private set; } = null!;

    /// <summary>The query. Facets as <c>key=value</c>, plus <c>speed:</c> and <c>turn:</c>.</summary>
    public TextBox Filter { get; private set; } = null!;

    /// <summary>Adds a row.</summary>
    public Button AddMove { get; private set; } = null!;

    /// <summary>Removes the selected row.</summary>
    public Button RemoveMove { get; private set; } = null!;

    /// <summary>Whether the coverage sweep is showing instead of the table.</summary>
    public ToggleButton Coverage { get; private set; } = null!;

    /// <summary>The table.</summary>
    public UiElement Table { get; private set; } = null!;

    /// <summary>What the selected row is, and why it scored what it did.</summary>
    public UiElement Fields { get; private set; } = null!;

    /// <summary>Which row is selected, or <see langword="null" />.</summary>
    public MoveEntryRecord? Selected => selected;

    /// <summary>What the last query said, best first, or empty when the box is empty.</summary>
    public IReadOnlyList<MoveExplanation> Ranked { get; private set; } = [];

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Bar = Part("moveset-bar");

        Bar.Add("fact-name").Text = "Query";

        Filter = Bar.Add<TextBox>();
        Filter.Placeholder = "role=loop style=injured speed:3.2";

        AddMove = Bar.Add<Button>();
        AddMove.Label = "Add Move";

        RemoveMove = Bar.Add<Button>();
        RemoveMove.Label = "Remove";

        Coverage = Bar.Add<ToggleButton>();
        Coverage.Label = "Coverage";

        var body = Part("moveset-body");

        Table = body.Add("moveset-table");

        var side = body.Add("moveset-side");
        Fields = side.Add("moveset-fields");

        Filter.ValueChanged += (_, _) => Reload();
        Coverage.CheckedChanged += (_, _) => Reload();

        AddHandler<ClickEvent>(static (element, args) => ((MoveSetView) element).Chosen(args));
        AddHandler<PointerEvent>(static (element, args) => ((MoveSetView) element).Pressed(args));
    }

    /// <summary>Shows a set.</summary>
    /// <param name="set">The document.</param>
    public void Show(MoveSetDocument set) {
        ArgumentNullException.ThrowIfNull(set);

        if (document is { } previous) {
            previous.Changed -= Reload;
        }

        document = set;
        set.Changed += Reload;

        Reload(set);
    }

    /// <summary>Rebuilds from the document.</summary>
    /// <param name="set">The document.</param>
    public void Reload(MoveSetDocument set) {
        ArgumentNullException.ThrowIfNull(set);
        Reload();
    }

    /// <summary>Rebuilds the table, or the coverage sweep in its place.</summary>
    public void Reload() {
        Table.Empty();
        rows.Clear();

        if (document is not { } set) {
            Restate();
            return;
        }

        var moves = set.Preview();
        var query = MoveQueryText.Parse(Filter.Value ?? string.Empty);

        Ranked = query is { } asked ? MoveExplanations.Explain(moves, asked) : [];

        if (Coverage.IsChecked) {
            Sweep(moves);
            Restate();

            return;
        }

        var header = Table.Add("moveset-row");
        header.AddClass("header");

        header.Add("moveset-name").Text = "Move";
        header.Add("moveset-facets").Text = "Facets";
        header.Add("moveset-number").Text = "Speed";
        header.Add("moveset-number").Text = "Turn";
        header.Add("moveset-number").Text = "Rate";
        header.Add("moveset-number").Text = "Score";

        foreach (var (entry, source, overridden) in Resolved(set)) {
            var row = Table.Add("moveset-row");

            row.Add("moveset-name").Text = source is null ? entry.Name : $"{entry.Name}  ({source})";
            row.Add("moveset-facets").Text = MoveSetDocument.Describe(entry);
            row.Add("moveset-number").Text = Number(entry.Speed);
            row.Add("moveset-number").Text = Number(entry.TurnRate);
            row.Add("moveset-number").Text = $"{Number(entry.MinRate)}–{Number(entry.MaxRate)}";

            var found = overridden ? null : Find(entry.Name);

            row.Add("moveset-number").Text = found is { Eligible: true } scored ? Number(scored.Score) : "—";

            // ⚠ Three states again: eligible, filtered out, and not queried at all. A row greyed for
            // failing a filter and a row greyed because nobody asked anything look the same, and
            // conflating them makes an empty query look like an empty set.
            row.SetClass("ineligible", found is { Eligible: false });
            row.SetClass("chosen", found is { Eligible: true } && ReferenceEquals(entry, Best(set)));
            row.SetClass("overridden", overridden);
            row.SetClass("inherited", source is not null);

            // Only this set's own rows are selectable: a base row is somebody else's file, and
            // offering to edit it here would write the edit into the wrong one.
            if (source is null) {
                rows.Add((row, entry));
            }
        }

        Restate();
    }

    /// <summary>
    ///     Every row the composed set has: the bases' first, then this set's, with a base row this set
    ///     replaces marked as overridden.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The overridden row is shown rather than hidden.</b> It is still in somebody's file, and
    ///     a table that showed only the winner is how an author spends an afternoon editing a row that
    ///     no longer has any effect. It is struck through and named with the set it came from.
    /// </remarks>
    List<(MoveEntryRecord Entry, string? Source, bool Overridden)> Resolved(MoveSetDocument set) {
        List<(MoveEntryRecord, string?, bool)> display = [];

        var own = new HashSet<string>(set.Set.Entries.Select(static entry => entry.Name), StringComparer.Ordinal);
        var underlay = set.Underlay();

        for (var index = 0; index < underlay.Count; index++) {
            var (address, content) = underlay[index];
            var name = Path.GetFileNameWithoutExtension(address);

            foreach (var entry in content.Entries) {
                // Overridden by this set, or by a later base — the composition is base-first, so the
                // last mention of a name wins.
                var beaten = own.Contains(entry.Name)
                    || underlay.Skip(index + 1).Any(later => later.Content.Entries.Any(row => row.Name == entry.Name));

                display.Add((entry, name, beaten));
            }
        }

        foreach (var entry in Order(set)) {
            display.Add((entry, null, false));
        }

        return display;
    }

    /// <summary>The rows in score order when there is a query, and in file order when there is not.</summary>
    /// <remarks>
    ///     ⚠ <b>File order is the default and it matters.</b> A table that always sorted by score
    ///     would reorder itself under somebody's cursor as they typed a facet, and the row they were
    ///     editing would move.
    /// </remarks>
    IEnumerable<MoveEntryRecord> Order(MoveSetDocument set) {
        if (Ranked.Count == 0) {
            return set.Set.Entries;
        }

        var order = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var index = 0; index < Ranked.Count; index++) {
            order[Ranked[index].Name] = index;
        }

        return set.Set.Entries.OrderBy(entry => order.GetValueOrDefault(entry.Name, int.MaxValue));
    }

    MoveEntryRecord? Best(MoveSetDocument set) =>
        Ranked.FirstOrDefault(static entry => entry.Eligible) is { Name.Length: > 0 } top
            ? set.Set.Entries.Find(entry => string.Equals(entry.Name, top.Name, StringComparison.Ordinal))
            : null;

    MoveExplanation? Find(string name) {
        foreach (var entry in Ranked) {
            if (string.Equals(entry.Name, name, StringComparison.Ordinal)) {
                return entry;
            }
        }

        return null;
    }

    /// <summary>Which regions of the query space the set has nothing for.</summary>
    /// <remarks>
    ///     Not an error and not a build failure: the thing an author needs to see before shipping. A
    ///     set with no injured stop and nothing above four metres a second is a set that will fall
    ///     back in game, silently, on exactly the inputs nobody tested.
    /// </remarks>
    void Sweep(MoveSet moves) {
        List<FacetSet> required = [];

        foreach (var role in MoveRole.All) {
            required.Add(FacetSet.Of(MoveRole.Facet(role)));
        }

        var coverage = MoveCoverage.Sweep(moves, required);

        var header = Table.Add("moveset-row");
        header.AddClass("header");

        header.Add("moveset-name").Text = "Asked for";
        header.Add("moveset-facets").Text = "Chosen";
        header.Add("moveset-number").Text = "Speed";
        header.Add("moveset-number").Text = "Error";

        foreach (var cell in coverage.Cells) {
            if (!cell.FallsBack) {
                continue;
            }

            var row = Table.Add("moveset-row");

            row.AddClass(cell.Answered ? "ineligible" : "missing");

            row.Add("moveset-name").Text = cell.Required.ToString();
            row.Add("moveset-facets").Text = cell.Answered ? cell.Chosen : "nothing at all";
            row.Add("moveset-number").Text = Number(cell.Speed);
            row.Add("moveset-number").Text = cell.Answered ? Number(cell.Error) : "—";
        }

        if (Table.Children.Count == 1) {
            Table.Add("text").Text = "Every region of the query space is answered without falling back.";
        }
    }

    void Restate() {
        Fields.Empty();

        foreach (var (element, row) in rows) {
            element.SetClass("selected", ReferenceEquals(row, selected));
        }

        if (document is not { } set) {
            return;
        }

        Fields.Add("moveset-title").Text = set.Set.Name;

        Fields.Add("fact-row").Add("fact-name").Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{set.Set.Entries.Count} move(s), {set.Set.Rules.Count} rule(s), {set.Set.Bases.Count} overlay(s)"
        );

        if (set.LoadError is { } error) {
            Fields.Add("moveset-error").Text = error;
        }

        if (selected is not { } entry) {
            Fields.Add("text").Text = "Select a move.";
            return;
        }

        Fields.Add("moveset-title").Text = entry.Name;

        Line("Name", entry.Name, value => set.SetField(entry, "Rename Move", static row => row.Name, static (row, name) => row.Name = name, value));
        Line("Clip", entry.Clip, value => set.SetField(entry, "Set Clip", static row => row.Clip, static (row, clip) => row.Clip = clip, value));

        Facets(set, entry);

        Number("Speed", entry.Speed, value => set.SetField(entry, "Set Speed", static row => row.Speed, static (row, speed) => row.Speed = speed, value));
        Number("Turn rate", entry.TurnRate, value => set.SetField(entry, "Set Turn Rate", static row => row.TurnRate, static (row, turn) => row.TurnRate = turn, value));
        Number("Min rate", entry.MinRate, value => set.SetField(entry, "Set Min Rate", static row => row.MinRate, static (row, rate) => row.MinRate = rate, value));
        Number("Max rate", entry.MaxRate, value => set.SetField(entry, "Set Max Rate", static row => row.MaxRate, static (row, rate) => row.MaxRate = rate, value));
        Number("Foot phase", entry.FootPhase, value => set.SetField(entry, "Set Foot Phase", static row => row.FootPhase, static (row, phase) => row.FootPhase = phase, value));

        Breakdown(entry);
    }

    /// <summary>The score, term by term. The single most valuable thing in the panel.</summary>
    void Breakdown(MoveEntryRecord entry) {
        if (Find(entry.Name) is not { } scored) {
            return;
        }

        Fields.Add("moveset-title").Text = "Why";

        if (!scored.Eligible) {
            Fields.Add("moveset-error").Text =
                $"Not eligible: it does not say {scored.Missing}. A required facet is a filter and not a "
                + "preference, so no amount of scoring brings this row back.";

            return;
        }

        foreach (var term in scored.Terms) {
            var row = Fields.Add("fact-row");

            row.SetClass("warning", term.Amount < 0f);
            row.Add("fact-name").Text = term.Reason;
            row.Add("fact-value").Text = Number(term.Amount);
        }

        var total = Fields.Add("fact-row");

        total.Add("fact-name").Text = "Total";
        total.Add("fact-value").Text = $"{Number(scored.Score)} at {Number(scored.PlaybackRate)}×";
    }

    void Facets(MoveSetDocument set, MoveEntryRecord entry) {
        var line = Fields.Add("fact-row");

        line.Add("fact-name").Text = "Facets";

        var box = line.Add("fact-value").Add<TextBox>();

        box.Value = MoveSetDocument.Describe(entry);

        box.ValueChanged += (control, text) => {
            if (!set.SetFacets(entry, text ?? string.Empty)) {
                // A pair that did not parse leaves the field showing what the row still says, which
                // is the only honest thing to show.
                ((TextBox) control).Value = MoveSetDocument.Describe(entry);
            }
        };
    }

    static string Number(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    void Line(string label, string value, Action<string> write) {
        var line = Fields.Add("fact-row");

        line.Add("fact-name").Text = label;

        var box = line.Add("fact-value").Add<TextBox>();

        box.Value = value;
        box.ValueChanged += (_, text) => write(text ?? string.Empty);
    }

    void Number(string label, float value, Action<float> write) {
        var line = Fields.Add("fact-row");

        line.Add("fact-name").Text = label;

        var box = line.Add("fact-value").Add<NumericInput>();

        box.Decimals = 3;
        box.Number = value;
        box.NumberChanged += (_, number) => write((float) number);
    }

    void Chosen(ClickEvent args) {
        if (document is not { } set) {
            return;
        }

        for (var element = args.Source; element is not null; element = element.Parent) {
            if (ReferenceEquals(element, AddMove)) {
                selected = set.Add(new() { Name = $"move {set.Set.Entries.Count + 1}", MinRate = 1f, MaxRate = 1f });
                args.Handled = true;

                return;
            }

            if (ReferenceEquals(element, RemoveMove)) {
                if (selected is { } entry && set.Remove(entry)) {
                    selected = null;
                }

                args.Handled = true;
                return;
            }
        }
    }

    void Pressed(PointerEvent args) {
        if (args.Action != PointerAction.Pressed || args.Button != PointerButton.Primary) {
            return;
        }

        foreach (var (element, entry) in rows) {
            if (element.Bounds.Contains(new Vector2(args.X, args.Y))) {
                selected = entry;
                Restate();

                args.Handled = true;
                return;
            }
        }
    }
}

/// <summary>Opens an authored move set.</summary>
public sealed class MoveSetEditorFactory : IAssetEditorFactory {
    /// <inheritdoc />
    public string Name => "Move Set";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [MoveSetDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        return new MoveSetDocument(request.Project, request.Asset, request.Path);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        var view = panel.Add<MoveSetView>();
        view.Show((MoveSetDocument) document);

        return view;
    }
}

/// <summary>Reads a written query into the one the runtime would build.</summary>
/// <remarks>
///     ⚠ <b>The same query, not a similar one.</b> The whole claim of the filter box is that what it
///     shows is what the game would pick, so this produces a <see cref="MoveQuery" /> and hands it to
///     the same scorer — a parser that built its own approximation would make the panel a liar in
///     exactly the cases somebody is using it to investigate.
/// </remarks>
public static class MoveQueryText {
    /// <summary>Reads a query, or <see langword="null" /> when nothing was asked.</summary>
    /// <param name="text">
    ///     Facets as <c>key=value</c>, preferences as <c>key=value*weight</c>, and numbers as
    ///     <c>speed:</c> and <c>turn:</c>. Anything else is ignored.
    /// </param>
    /// <returns>The query.</returns>
    public static MoveQuery? Parse(string text) {
        ArgumentNullException.ThrowIfNull(text);

        List<Facet> required = [];
        List<WeightedFacet> preferred = [];

        var speed = 0f;
        var turn = 0f;
        var asked = false;

        foreach (var word in text.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries)) {
            if (word.StartsWith("speed:", StringComparison.OrdinalIgnoreCase)
                && float.TryParse(word[6..], CultureInfo.InvariantCulture, out var metres)) {
                speed = metres;
                asked = true;

                continue;
            }

            if (word.StartsWith("turn:", StringComparison.OrdinalIgnoreCase)
                && float.TryParse(word[5..], CultureInfo.InvariantCulture, out var radians)) {
                turn = radians;
                asked = true;

                continue;
            }

            var split = word.IndexOf('=', StringComparison.Ordinal);

            if (split <= 0 || split == word.Length - 1) {
                continue;
            }

            var key = word[..split];
            var value = word[(split + 1)..];
            var star = value.IndexOf('*', StringComparison.Ordinal);

            if (star > 0 && float.TryParse(value[(star + 1)..], CultureInfo.InvariantCulture, out var weight)) {
                preferred.Add(new(Facet.Of(key, value[..star]), weight));
            } else {
                required.Add(Facet.Of(key, value));
            }

            asked = true;
        }

        return asked
            ? new MoveQuery {
                Required = FacetSet.Of([.. required]),
                Preferred = preferred.Count > 0 ? [.. preferred] : null,
                Numeric = new() { Speed = speed, TurnRate = turn }
            }
            : null;
    }
}
