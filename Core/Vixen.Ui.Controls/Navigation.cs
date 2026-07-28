// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Ui.Styling;

namespace Vixen.Ui.Controls;

/// <summary>One step of a breadcrumb trail.</summary>
public sealed partial class BreadcrumbItem : ButtonBase {
    /// <inheritdoc />
    protected override string TagName => "breadcrumb-item";

    /// <summary>What this step stands for. Meaningful to the application.</summary>
    [UiProperty]
    public partial string? Value { get; set; }
}

/// <summary>The path to where the user is.</summary>
/// <remarks>
///     ⚠ <b>The separators are elements rather than a <c>::before</c>.</b> There are no generated
///     boxes in this styling engine — no pseudo-elements — so the chevron between two steps has to
///     be something. Making it a real <see cref="Icon" /> also means the last step does not have
///     one, which a <c>::before</c> on every item would have to undo with a <c>:last-child</c> rule.
/// </remarks>
public sealed partial class Breadcrumb : Control {
    readonly List<BreadcrumbItem> steps = [];

    /// <inheritdoc />
    protected override string TagName => "breadcrumb";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The steps, in order from the root.</summary>
    public IReadOnlyList<BreadcrumbItem> Steps => steps;

    /// <summary>Raised when a step is activated.</summary>
    public event Action<Breadcrumb, BreadcrumbItem>? Navigated;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();
        AddHandler<ClickEvent>(static (element, args) => ((Breadcrumb) element).Chosen(args));
    }

    /// <summary>Adds a step at the end of the trail.</summary>
    /// <param name="label">What it says.</param>
    /// <param name="value">What it stands for.</param>
    /// <returns>The step.</returns>
    public BreadcrumbItem AddStep(string? label, string? value = null) {
        if (steps.Count > 0) {
            var separator = Add<Icon>();
            separator.Geometry = ControlIcons.ChevronRight;
            separator.AddClass("breadcrumb-separator");
        }

        var step = Add<BreadcrumbItem>();
        step.Label = label;
        step.Value = value ?? label;

        steps.Add(step);
        Restate();

        return step;
    }

    /// <summary>Marks the last step as the current one.</summary>
    /// <remarks>
    ///     ⚠ <c>:checked</c> rather than a class, because the last step is a state of the control
    ///     rather than a mode somebody put it in — and because the theme already reads that state on
    ///     every other selected thing in the set.
    /// </remarks>
    void Restate() {
        for (var i = 0; i < steps.Count; i++) {
            if (i == steps.Count - 1) {
                steps[i].State |= ElementState.Checked;
            } else {
                steps[i].State &= ~ElementState.Checked;
            }
        }
    }

    void Chosen(ClickEvent args) {
        if (args.Source is BreadcrumbItem step && steps.Contains(step)) {
            Navigated?.Invoke(this, step);
        }
    }
}

/// <summary>One page number, or one of the arrows beside them.</summary>
public sealed partial class PageButton : ButtonBase {
    /// <inheritdoc />
    protected override string TagName => "page-button";

    /// <summary>Which page it goes to. Zero-based; -1 for an ellipsis, which does nothing.</summary>
    public int Page { get; internal set; } = -1;
}

/// <summary>A row of page numbers.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The row is rebuilt when the page changes, not merely restyled</b>, because which
///         numbers are shown depends on which one is current: page 1 of 90 shows
///         <c>1 2 3 … 90</c> and page 45 shows <c>1 … 44 45 46 … 90</c>. That is a different set of
///         elements rather than a different highlight.
///     </para>
///     <para>
///         Rebuilding costs a handful of elements per page change, which is a user action. The
///         alternative — ninety buttons with eighty of them hidden — costs them on every page that
///         has a paginator on it.
///     </para>
/// </remarks>
public sealed partial class Pagination : Control {
    readonly List<PageButton> buttons = [];

    /// <inheritdoc />
    protected override string TagName => "pagination";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>How many pages there are.</summary>
    [UiProperty(Changed = nameof(OnPagingChanged))]
    public partial int PageCount { get; set; }

    /// <summary>Which page is showing, counting from zero.</summary>
    [UiProperty(Coerce = nameof(CoercePage), Changed = nameof(OnPagingChanged))]
    public partial int CurrentPage { get; set; }

    /// <summary>How many numbers to show either side of the current one.</summary>
    [UiProperty(Default = 1, Changed = nameof(OnPagingChanged))]
    public partial int Window { get; set; }

    /// <summary>Raised when the page changes.</summary>
    public event Action<Pagination, int>? PageChanged;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        AddHandler<ClickEvent>(static (element, args) => ((Pagination) element).Chosen(args));
        Rebuild();
    }

    int CoercePage(int value) => PageCount <= 0 ? 0 : Math.Clamp(value, 0, PageCount - 1);

    void OnPagingChanged(int previous, int current) {
        Rebuild();
        PageChanged?.Invoke(this, CurrentPage);
    }

    /// <summary>How many slots the row has: the ends, the window, and a gap either side.</summary>
    /// <remarks>
    ///     The number of page buttons, and it does not depend on which page is current — which is
    ///     what <see cref="Pages" /> exists to guarantee. For the default window of one that is
    ///     seven: <c>1 … 44 45 46 … 90</c>.
    /// </remarks>
    int Slots => (2 * Math.Max(0, Window)) + 5;

    /// <summary>Which page numbers to show, with -1 standing for a gap.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The first and last pages are always shown</b>, because they are the two anybody
    ///         navigates to by name — "back to the start", "how many are there". A window that slid
    ///         without pinning them makes the end of a long list reachable only by holding the
    ///         arrow.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The row is the same width on every page, and the window is extended rather than
    ///         truncated to keep it so.</b> Clamping the window against the ends instead — which is
    ///         the obvious way to write this — gives a row that grows from four buttons to seven and
    ///         back as the user pages through: <c>1 2 … 12</c>, <c>1 2 3 … 12</c>,
    ///         <c>1 2 3 4 … 12</c>, <c>1 … 3 4 5 … 12</c>. Every number under the pointer moves on
    ///         every click, so paging with the mouse means re-finding the button each time and
    ///         clicking "next" lands on whatever slid underneath it. It is the same argument the
    ///         arrows already make by staying disabled rather than disappearing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A gap never stands for one page.</b> An ellipsis is exactly as wide as the number
    ///         it replaced, so hiding a single page behind one buys nothing and costs the user the
    ///         page — <c>1 … 3 4 5 … 12</c> spends a slot concealing "2". The near-the-ends branches
    ///         are what stop it: the window reaches the end rather than leaving one page behind a
    ///         gap.
    ///     </para>
    /// </remarks>
    List<int> Pages() {
        var pages = new List<int>();

        if (PageCount <= 0) {
            return pages;
        }

        // Everything fits, so nothing is hidden. This is also the whole of the small-count case —
        // three pages must not come out as `1 … 3`.
        if (PageCount <= Slots) {
            for (var page = 0; page < PageCount; page++) {
                pages.Add(page);
            }

            return pages;
        }

        var window = Math.Max(0, Window);
        var run = (2 * window) + 3;

        // ⚠ The boundary is where the gap would start hiding fewer than two pages, which is
        // `Window + 2` and not `2 * Window + 1`. The two coincide at the default window of one and
        // diverge either side of it — with a window of zero the second lets `1 … 3 … 12` through,
        // where the gap has swallowed page 2 alone.
        if (CurrentPage <= window + 2) {
            // Near the start: the window is pinned there and grows to the right, so the left-hand
            // gap that would have hidden one or two pages does not exist at all.
            for (var page = 0; page < run; page++) {
                pages.Add(page);
            }

            pages.Add(-1);
            pages.Add(PageCount - 1);

            return pages;
        }

        if (CurrentPage >= PageCount - window - 3) {
            pages.Add(0);
            pages.Add(-1);

            for (var page = PageCount - run; page < PageCount; page++) {
                pages.Add(page);
            }

            return pages;
        }

        pages.Add(0);
        pages.Add(-1);

        for (var page = CurrentPage - window; page <= CurrentPage + window; page++) {
            pages.Add(page);
        }

        pages.Add(-1);
        pages.Add(PageCount - 1);

        return pages;
    }

    void Rebuild() {
        foreach (var button in buttons) {
            button.Remove();
        }

        buttons.Clear();

        Arrow(ControlIcons.ChevronLeft, CurrentPage - 1, "Previous page");

        foreach (var page in Pages()) {
            var button = Add<PageButton>();
            button.Page = page;

            if (page < 0) {
                button.Label = "…";
                button.Disabled = true;
                button.AddClass("ellipsis");
            } else {
                button.Label = (page + 1).ToString(CultureInfo.InvariantCulture);

                if (page == CurrentPage) {
                    button.State |= ElementState.Checked;
                }
            }

            buttons.Add(button);
        }

        Arrow(ControlIcons.ChevronRight, CurrentPage + 1, "Next page");
    }

    void Arrow(PathBuilder geometry, int page, string label) {
        var button = Add<PageButton>();
        button.Page = page;
        button.Label = label;
        button.AddClass("page-arrow");
        button.LeadingIcon.Geometry = geometry;

        // Disabled rather than absent at the ends. A row whose buttons move sideways when you reach
        // the first page is a row where the next click lands on something else.
        button.Disabled = page < 0 || page >= PageCount;

        buttons.Add(button);
    }

    void Chosen(ClickEvent args) {
        if (args.Source is PageButton { Page: >= 0 } button) {
            CurrentPage = button.Page;
        }
    }
}
