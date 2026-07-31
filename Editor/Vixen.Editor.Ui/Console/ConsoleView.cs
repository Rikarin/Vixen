// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Microsoft.Extensions.Logging;
using Vixen.Core.Diagnostics;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Styling;

namespace Vixen.Editor.Ui;

/// <summary>The panel that is looked at most often when something has gone wrong.</summary>
/// <remarks>
///     <para>
///         <b>Doc 20 calls the Console "the least built thing in the shell" and the largest single
///         item in its Part A, and both were true: it was a <c>TextBlock</c> saying "Nothing logged
///         yet."</b> What replaces it is doc 20's A7 in full — a virtualised list over the ring the
///         diagnostics layer already keeps, level toggles carrying counts, a category filter, a
///         search box, collapse-duplicates, clear, clear-on-play, and a detail pane showing the whole
///         record and its stack.
///     </para>
///     <para>
///         ⚠ <b>It does not allocate per line.</b> Doc 20 is blunt about why — "a game logging per
///         frame into a panel that keeps strings is a leak with a UI" — and there are two halves to
///         it. <see cref="ConsoleModel" /> pulls only what is new out of the ring and keeps indices
///         rather than text; <see cref="VirtualizingPanel" /> keeps about thirty row elements
///         whatever the list holds. A hundred thousand lines is a hundred thousand records the sink
///         had already allocated, and thirty elements.
///     </para>
///     <para>
///         ⚠ <b>Rows are rebuilt from the model on <see cref="Tick" />, not from an event.</b> The
///         sink is written from every thread the editor runs work on — a content import logs from the
///         pool — so a subscription would rebuild the panel from a background thread. One pull per
///         frame on the thread that owns the document is the same bargain <c>ContentTasks</c> makes.
///     </para>
///     <para>
///         ⚠ <b>Selecting a row is not the same as it staying selected.</b> The list scrolls as
///         records arrive, and the detail pane is holding a record rather than a row index — so a
///         line selected and then pushed off the top by a burst still has its stack on screen.
///     </para>
/// </remarks>
public sealed partial class ConsoleView : Control {
    /// <summary>How the timestamp column is formatted.</summary>
    /// <remarks>
    ///     Time of day and milliseconds, and no date. A console spanning midnight is not the case to
    ///     optimise for, and the column that answers "how long between these two lines" is the one
    ///     that earns its width.
    /// </remarks>
    const string TimeFormat = @"HH\:mm\:ss\.fff";

    readonly Dictionary<ConsoleLevels, ToggleButton> toggles = [];

    /// <summary>Which item each realised row is showing, so a click knows what it landed on.</summary>
    /// <remarks>
    ///     ⚠ <b>Kept here rather than on the element, because a <c>UiElement</c> has no tag slot.</b>
    ///     The pool only ever grows and its rows are never removed, so this is bounded by the tallest
    ///     the panel has ever been — about thirty entries.
    /// </remarks>
    readonly Dictionary<UiElement, int> shown = [];

    Action<ConsoleModel>? onChanged;
    Action<UiDocument>? settle;
    LogRecord? selected;
    bool tail = true;

    /// <inheritdoc />
    protected override string TagName => "console-view";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>What it is showing.</summary>
    public ConsoleModel? Model { get; private set; }

    /// <summary>The strip along the top.</summary>
    public UiElement Toolbar { get; private set; } = null!;

    /// <summary>The list itself.</summary>
    public VirtualizingPanel List { get; private set; } = null!;

    /// <summary>The pane under it showing the whole of the selected record.</summary>
    public UiElement Detail { get; private set; } = null!;

    /// <summary>The search box.</summary>
    public SearchBox Search { get; private set; } = null!;

    /// <summary>The category picker.</summary>
    public Select Categories { get; private set; } = null!;

    /// <summary>Whether the console throws itself away when play mode is entered.</summary>
    /// <remarks>
    ///     ⚠ <b>The console owns the preference and the application owns the moment.</b> This class
    ///     has no idea what play mode is; what it can say is whether the user asked, which is what
    ///     the toggle on the strip records. <c>EditorApplication</c> reads it when it enters play.
    /// </remarks>
    public bool ClearsOnPlay { get; set; }

    /// <summary>Which record the detail pane is showing, or <see langword="null" />.</summary>
    public LogRecord? Selected {
        get => selected;

        set {
            selected = value;
            ShowDetail();
        }
    }

    /// <summary>Raised when a row is activated, for a host that can open a source file.</summary>
    /// <remarks>
    ///     ⚠ <b>An event rather than a call into an external-tool setting, because this assembly has
    ///     neither.</b> Doc 20 asks for double-click-to-open-source "through the external-tool
    ///     setting", which is a preference that does not exist yet and a process launch the shell is
    ///     deliberately not allowed to make. The record carries the exception and its stack; deciding
    ///     that a frame in it names a file worth opening is the application's.
    /// </remarks>
    public event Action<ConsoleView, LogRecord>? Activated;

    /// <summary>Points the console at a sink's records.</summary>
    /// <param name="model">What to show.</param>
    public void Show(ConsoleModel model) {
        ArgumentNullException.ThrowIfNull(model);

        if (Model is not null && onChanged is not null) {
            Model.Changed -= onChanged;
        }

        Model = model;
        onChanged ??= _ => Restate();

        model.Changed += onChanged;

        foreach (var category in model.Categories) {
            Categories.AddOption(category, category);
        }

        Restate();
    }

    /// <summary>Takes whatever has been logged and brings the rows up to date.</summary>
    /// <param name="now">Unused; taken so the shell can drive this like everything else it ticks.</param>
    public void Tick(TimeSpan now = default) {
        _ = now;
        Model?.Pull();
    }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Toolbar = Part("console-toolbar");

        var clear = Toolbar.Add<Button>();
        clear.Label = EditorStrings.ConsoleClear.Text;
        clear.Variant = ControlVariant.Subtle;
        clear.Size = ControlSize.Small;
        clear.Clicked += _ => Clear();

        Toggle(EditorStrings.ConsoleCollapse, on => {
            if (Model is not null) {
                Model.Collapse = on;
            }
        });

        Toggle(EditorStrings.ConsoleClearOnPlay, on => ClearsOnPlay = on);

        Search = Toolbar.Add<SearchBox>();
        Search.Placeholder = EditorStrings.ConsoleSearch.Text;
        Search.ValueChanged += (_, value) => {
            if (Model is not null) {
                Model.Search = value;
            }
        };

        Categories = Toolbar.Add<Select>();
        Categories.Placeholder = EditorStrings.ConsoleAllCategories.Text;
        Categories.Size = ControlSize.Small;

        // The empty value is "all of them", which is a real choice and has to be reachable — a
        // filter you can enter and not leave is the shape of complaint a category picker earns.
        Categories.AddOption(string.Empty, EditorStrings.ConsoleAllCategories.Text);

        Categories.SelectionChanged += (_, value) => {
            if (Model is not null) {
                Model.Category = string.IsNullOrEmpty(value) ? null : value;
            }
        };

        Badge(ConsoleLevels.Error, "level-error");
        Badge(ConsoleLevels.Warning, "level-warning");
        Badge(ConsoleLevels.Info, "level-info");
        Badge(ConsoleLevels.Verbose, "level-verbose");

        List = Part<VirtualizingPanel>();
        List.CreateRow = _ => Row();
        List.BindRow = Bind;

        // ⚠ Following the tail is a mode the user leaves by scrolling, not a checkbox. Every console
        // worth using pins to the bottom while you are not looking and stops the moment you scroll
        // up to read something — one that kept snapping back would be unreadable while anything was
        // being logged.
        List.Scroller.Scrolled += scroller =>
            tail = scroller.ScrollTop >= scroller.Content.Height - scroller.Height - List.RowHeight;

        Detail = Part("console-detail");
        ShowDetail();

        // ⚠ The tail is followed from `LayoutFinished` and not from `Restate`, and the reason is
        // that `Restate` runs during the tick — before the pass that measures anything. At that
        // point the scroller's viewport is whatever it was last frame and the content height has
        // just been invalidated by the new row count, so a scroll aimed at the bottom is clamped
        // against the old extent and lands at the top. Here both are known.
        settle = _ => Follow();
        Document.LayoutFinished += settle;
    }

    /// <inheritdoc />
    protected override void OnRemoved() {
        if (Model is not null && onChanged is not null) {
            Model.Changed -= onChanged;
        }

        if (settle is not null) {
            Document.LayoutFinished -= settle;
            settle = null;
        }

        base.OnRemoved();
    }

    /// <summary>Keeps the newest line on screen while the user is not reading an older one.</summary>
    void Follow() {
        if (!tail || Model is not { Count: > 0 }) {
            return;
        }

        var bottom = MathF.Max(0f, List.Scroller.Content.Height - List.Scroller.Height);

        if (List.Scroller.ScrollTop < bottom) {
            List.Scroller.ScrollTop = bottom;
        }
    }

    /// <summary>Empties the console, which empties the ring behind it.</summary>
    public void Clear() {
        Selected = null;
        Model?.Clear();
    }

    void Toggle(StringId title, Action<bool> set) {
        var button = Toolbar.Add<ToggleButton>();
        button.Label = title.Text;
        button.Size = ControlSize.Small;
        button.CheckedChanged += (source, on) => {
            _ = source;
            set(on);
        };
    }

    /// <summary>One of the four level buttons, which is a toggle and a count in one control.</summary>
    /// <remarks>
    ///     ⚠ <b>The count is on the button rather than beside it, and it counts what <i>arrived</i>
    ///     rather than what is shown.</b> A warning badge that read zero because warnings are hidden
    ///     would answer the opposite of the question somebody clicks it to ask.
    /// </remarks>
    void Badge(ConsoleLevels level, string className) {
        var button = Toolbar.Add<ToggleButton>();

        button.Size = ControlSize.Small;
        button.AddClass("console-level");
        button.AddClass(className);

        button.CheckedChanged += (_, on) => {
            if (Model is not null) {
                Model.Levels = on ? Model.Levels | level : Model.Levels & ~level;
            }
        };

        toggles[level] = button;
    }

    UiElement Row() {
        var row = List.Scroller.Content.Add<UiElement>("console-row");

        row.Add<UiElement>("console-level-mark");
        row.Add<UiElement>("console-time");
        row.Add<UiElement>("console-category");
        row.Add<UiElement>("console-message");
        row.Add<UiElement>("console-repeats");

        // ⚠ `TapEvent` and not `ClickEvent`. A click is an *activation*, and only a `Control` ever
        // raises one — a row is a bare element with five bare elements in it, so a handler waiting
        // for a click here waits forever, which is exactly how the panel came to have selection
        // styling that nothing could put on screen. A tap is what the document reports about a
        // press and release that stayed put, and it bubbles from whichever column was under the
        // pointer to the row that owns it.
        row.AddHandler<TapEvent>(
            (element, args) => {
                if (Model is null || !shown.TryGetValue(element, out var index) || index >= Model.Count) {
                    return;
                }

                var record = Model[index].Record;

                Selected = record;

                // ⚠ And the rows are re-bound, because `:checked` is written in `Bind` — the row
                // that has just been selected and the row that was are both already realised, so
                // nothing else would repaint them until the next line arrives.
                List.Realise();

                args.Handled = true;

                // Double-click is what opens a source file everywhere else, and the record's
                // exception is the only thing here that names one.
                if (args.Count > 1) {
                    Activated?.Invoke(this, record);
                }
            }
        );

        return row;
    }

    void Bind(UiElement row, int index) {
        if (Model is null || index >= Model.Count) {
            return;
        }

        var (record, repeats) = Model[index];

        shown[row] = index;

        var mark = row.Children[0];

        foreach (var name in Marks) {
            mark.RemoveClass(name);
        }

        mark.AddClass(ClassOf(record.Level));
        row.Children[1].Text = record.Timestamp.ToString(TimeFormat, CultureInfo.InvariantCulture);
        row.Children[2].Text = Short(record.Category);
        row.Children[3].Text = Line(record.Message);

        // Absent rather than "1", which is a column of ones down the side of every console that has
        // collapsing turned on.
        row.Children[4].Text = repeats > 1 ? repeats.ToString(CultureInfo.InvariantCulture) : null;

        if (ReferenceEquals(selected, record)) {
            row.State |= ElementState.Checked;
        } else {
            row.State &= ~ElementState.Checked;
        }
    }

    /// <summary>Brings the buttons, the categories and the rows into line with the model.</summary>
    void Restate() {
        if (Model is not { } model) {
            return;
        }

        foreach (var (level, button) in toggles) {
            button.IsChecked = (model.Levels & level) != 0;
            button.Label = Count(level, model).ToString(CultureInfo.InvariantCulture);
        }

        // A category appears the first time something logs under it, so the picker grows while the
        // editor runs — which is why this is a comparison rather than a rebuild: replacing the
        // options would drop the choice the user made.
        for (var index = Categories.Options.Count - 1; index < model.Categories.Count; index++) {
            var category = model.Categories[index];
            Categories.AddOption(category, Short(category));
        }

        List.Count = model.Count;
        List.Realise();
    }

    void ShowDetail() {
        while (Detail.Children.Count > 0) {
            Detail.Children[^1].Remove();
        }

        if (selected is not { } record) {
            Detail.Add<TextBlock>().Text = EditorStrings.ConsoleNoSelection.Text;
            Detail.AddClass("empty");

            return;
        }

        Detail.RemoveClass("empty");

        var heading = Detail.Add<UiElement>("console-detail-heading");
        heading.Text = record.Message;

        var meta = Detail.Add<UiElement>("console-detail-meta");

        meta.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{record.Level} · {record.Category} · {record.Timestamp.ToString(TimeFormat, CultureInfo.InvariantCulture)} · thread {record.ThreadId}"
        );

        if (record.SuppressedCount > 0) {
            Detail.Add<UiElement>("console-detail-meta").Text = string.Create(
                CultureInfo.InvariantCulture,
                $"and {record.SuppressedCount} identical line(s) the rate limiter dropped"
            );
        }

        if (record.Exception is { } exception) {
            // ⚠ `ToString()` rather than the message, because the stack is the reason somebody
            // clicked the row. A console detail pane that shows only what the list already showed is
            // a pane with no reason to exist.
            Detail.Add<UiElement>("console-detail-stack").Text = exception.ToString();
        }
    }

    static int Count(ConsoleLevels level, ConsoleModel model) =>
        level switch {
            ConsoleLevels.Error => model.Errors,
            ConsoleLevels.Warning => model.Warnings,
            ConsoleLevels.Info => model.Infos,
            _ => model.Verbose
        };

    /// <summary>The four classes a level mark can carry, for taking the previous one off.</summary>
    static readonly string[] Marks = ["level-error", "level-warning", "level-info", "level-verbose"];

    static string ClassOf(LogLevel level) =>
        ConsoleModel.BucketOf(level) switch {
            ConsoleLevels.Error => "level-error",
            ConsoleLevels.Warning => "level-warning",
            ConsoleLevels.Info => "level-info",
            _ => "level-verbose"
        };

    /// <summary>The last segment of a category, which is the type that logged.</summary>
    /// <remarks>
    ///     A category is a full type name, and a column showing
    ///     <c>Vixen.Editor.Assets.Content.ContentPipeline</c> is one that pushes the message off the
    ///     right of the panel. The whole of it is in the detail pane.
    /// </remarks>
    static string Short(string category) {
        var dot = category.LastIndexOf('.');
        return dot >= 0 && dot < category.Length - 1 ? category[(dot + 1)..] : category;
    }

    /// <summary>The first line of a message, which is all a row has space for.</summary>
    /// <remarks>
    ///     ⚠ <b>A row is one line tall and virtualisation requires that.</b> A multi-line message
    ///     rendered whole would make one row four times the height of the rest, and the panel's
    ///     arithmetic — row 40 000 is at 40 000 × height — would put every row after it in the wrong
    ///     place. The rest of the message is in the detail pane.
    /// </remarks>
    static string Line(string message) {
        var breakAt = message.AsSpan().IndexOfAny('\r', '\n');
        return breakAt < 0 ? message : string.Concat(message.AsSpan(0, breakAt), " …");
    }
}
