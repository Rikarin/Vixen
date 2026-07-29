// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Styling;

namespace Vixen.Editor.Ui;

/// <summary>The panel doc 20's A7 asks for: where the toasts accumulate once they have gone.</summary>
/// <remarks>
///     <para>
///         <b><see cref="NotificationCenter" /> has kept a bounded history since it was written and
///         nothing showed it.</b> A toast is a message that expires, and the thing a person does
///         after an import fails is look away, look back, and find it gone — errors are exempt from
///         expiry precisely because of that, which leaves the editor's corner slowly filling with
///         undismissed errors instead. This is the place they go.
///     </para>
///     <para>
///         ⚠ <b>Not the Console, and the difference is who wrote the line.</b> The console is the
///         whole of <c>Vixen.Core.Diagnostics</c>' ring — every category, every level, the game's
///         lines and the engine's. This is what the <i>editor</i> decided was worth interrupting
///         somebody about, which is a list two orders of magnitude shorter and the one you scan
///         after something went wrong. The console mirror means every entry here is in there too;
///         the reverse is emphatically not true.
///     </para>
///     <para>
///         ⚠ <b>The history is the model and this holds no copy of it.</b>
///         <see cref="NotificationCenter.History" /> is newest-first and bounded, so the rows are
///         built from it on change rather than accumulated here — an editor left open for a week is
///         then bounded by the centre's own limit rather than by two lists that have to agree.
///     </para>
/// </remarks>
public sealed partial class MessageLogView : Control {
    /// <summary>How the timestamp column is written, on the shell's clock rather than the wall's.</summary>
    const string TimeFormat = @"hh\:mm\:ss";

    readonly List<Notification> shown = [];
    readonly Dictionary<UiElement, int> indices = [];

    NotificationCenter? centre;

    NotificationSeverity? level;
    string? search;
    int selected = -1;

    /// <inheritdoc />
    protected override string TagName => "message-log";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The strip along the top.</summary>
    public UiElement Toolbar { get; private set; } = null!;

    /// <summary>The search box.</summary>
    public SearchBox Search { get; private set; } = null!;

    /// <summary>The severity picker.</summary>
    public Select Levels { get; private set; } = null!;

    /// <summary>The list.</summary>
    public VirtualizingPanel List { get; private set; } = null!;

    /// <summary>The pane under it showing the whole of the selected message.</summary>
    public UiElement Detail { get; private set; } = null!;

    /// <summary>What it is showing.</summary>
    public NotificationCenter? Centre => centre;

    /// <summary>What passes the filters, newest first.</summary>
    public IReadOnlyList<Notification> Shown => shown;

    /// <summary>How many messages pass the filters.</summary>
    public int Count => shown.Count;

    /// <summary>Which message the detail pane is showing, or <see langword="null" />.</summary>
    public Notification? Selected => selected >= 0 && selected < shown.Count ? shown[selected] : null;

    /// <summary>Points the panel at a notification centre.</summary>
    /// <param name="notifications">Whose history to show.</param>
    public void Show(NotificationCenter notifications) {
        ArgumentNullException.ThrowIfNull(notifications);

        Detach();

        centre = notifications;
        notifications.Changed += Changed;

        Restate();
    }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Toolbar = Part("message-log-toolbar");

        var clear = Toolbar.Add<Button>();

        clear.Label = EditorStrings.NotificationsClear.Text;
        clear.Variant = ControlVariant.Subtle;
        clear.Size = ControlSize.Small;
        clear.Clicked += _ => Clear();

        Search = Toolbar.Add<SearchBox>();
        Search.Placeholder = EditorStrings.ConsoleSearch.Text;

        Search.ValueChanged += (_, value) => {
            search = string.IsNullOrWhiteSpace(value) ? null : value;
            Restate();
        };

        Levels = Toolbar.Add<Select>();
        Levels.Size = ControlSize.Small;

        // The empty value is "all of them", for `ConsoleView`'s reason: a filter you can enter and
        // not leave is the shape of complaint a picker earns.
        Levels.AddOption(string.Empty, EditorStrings.MessagesAllLevels.Text);
        Levels.AddOption(nameof(NotificationSeverity.Error), EditorStrings.MessagesErrors.Text);
        Levels.AddOption(nameof(NotificationSeverity.Warning), EditorStrings.MessagesWarnings.Text);
        Levels.AddOption(nameof(NotificationSeverity.Success), EditorStrings.MessagesSuccesses.Text);
        Levels.AddOption(nameof(NotificationSeverity.Info), EditorStrings.MessagesInfos.Text);
        Levels.Value = string.Empty;

        Levels.SelectionChanged += (_, value) => {
            level = Enum.TryParse<NotificationSeverity>(value, out var chosen) ? chosen : null;
            Restate();
        };

        List = Part<VirtualizingPanel>();
        List.CreateRow = _ => Row();
        List.BindRow = Bind;

        Detail = Part("message-log-detail");
        ShowDetail();
    }

    /// <inheritdoc />
    protected override void OnRemoved() {
        Detach();
        base.OnRemoved();
    }

    /// <summary>Stops listening to whatever it was showing.</summary>
    /// <remarks>
    ///     ⚠ <b>A method group rather than a field holding a lambda.</b> Two delegates over the same
    ///     method and target compare equal, so <c>-=</c> finds this one — which is what makes the
    ///     field, and the <c>??=</c> that kept one instance alive to unsubscribe with, unnecessary.
    /// </remarks>
    void Detach() {
        if (centre is not null) {
            centre.Changed -= Changed;
        }
    }

    void Changed(NotificationCenter _) => Restate();

    /// <summary>Throws the history away.</summary>
    public void Clear() {
        selected = -1;

        centre?.Clear();
        ShowDetail();
    }

    /// <summary>Rebuilds the filtered list and the rows over it.</summary>
    public void Restate() {
        var was = Selected;

        shown.Clear();

        if (centre is { } notifications) {
            foreach (var entry in notifications.History) {
                if (Passes(entry)) {
                    shown.Add(entry);
                }
            }
        }

        // ⚠ The selection follows the message rather than the row index. New messages arrive at the
        // top — the history is newest-first — so an index kept across a change would silently move
        // the detail pane onto whatever has just been logged.
        selected = was is { } message ? shown.IndexOf(message) : -1;

        List.Count = shown.Count;
        List.Realise();

        ShowDetail();
    }

    UiElement Row() {
        var row = List.Scroller.Content.Add<UiElement>("message-row");

        row.Add<UiElement>("message-mark");
        row.Add<UiElement>("message-time");
        row.Add<UiElement>("message-text");
        row.Add<UiElement>("message-detail-text");

        row.AddHandler<ClickEvent>(
            (element, _) => {
                if (indices.TryGetValue(element, out var index) && index < shown.Count) {
                    selected = index;
                    ShowDetail();
                }
            }
        );

        return row;
    }

    void Bind(UiElement row, int index) {
        if (index >= shown.Count) {
            return;
        }

        var entry = shown[index];
        indices[row] = index;

        var mark = row.Children[0];

        foreach (var name in Marks) {
            mark.RemoveClass(name);
        }

        mark.AddClass(ClassOf(entry.Severity));

        row.Children[1].Text = entry.When.ToString(TimeFormat, CultureInfo.InvariantCulture);
        row.Children[2].Text = entry.Message;
        row.Children[3].Text = entry.Detail is { Length: > 0 } detail ? Line(detail) : null;

        if (index == selected) {
            row.State |= ElementState.Checked;
        } else {
            row.State &= ~ElementState.Checked;
        }
    }

    void ShowDetail() {
        while (Detail.Children.Count > 0) {
            Detail.Children[^1].Remove();
        }

        if (Selected is not { } entry) {
            Detail.AddClass("empty");
            Detail.Add<TextBlock>().Text = EditorStrings.MessagesNoSelection.Text;

            return;
        }

        Detail.RemoveClass("empty");
        Detail.Add<UiElement>("message-detail-heading").Text = entry.Message;

        Detail.Add<UiElement>("message-detail-meta").Text = string.Create(
            CultureInfo.CurrentCulture,
            $"{entry.Severity} · {entry.When.ToString(TimeFormat, CultureInfo.InvariantCulture)}"
        );

        if (entry.Detail is { Length: > 0 } detail) {
            Detail.Add<UiElement>("message-detail-body").Text = detail;
        }
    }

    bool Passes(Notification entry) {
        if (level is { } wanted && entry.Severity != wanted) {
            return false;
        }

        return search is null
            || entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
            || (entry.Detail?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>The four classes a mark can carry, for taking the previous one off.</summary>
    static readonly string[] Marks = ["level-error", "level-warning", "level-success", "level-info"];

    static string ClassOf(NotificationSeverity severity) =>
        severity switch {
            NotificationSeverity.Error => "level-error",
            NotificationSeverity.Warning => "level-warning",
            NotificationSeverity.Success => "level-success",
            _ => "level-info"
        };

    /// <summary>The first line of a detail, which is all a row has space for.</summary>
    /// <inheritdoc cref="ConsoleView" select="remarks" />
    static string Line(string text) {
        var breakAt = text.AsSpan().IndexOfAny('\r', '\n');
        return breakAt < 0 ? text : string.Concat(text.AsSpan(0, breakAt), " …");
    }
}
