// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Styling;

namespace Vixen.Editor.Profiler;

/// <summary>A flame chart: one bar per scope, nesting downwards, time across.</summary>
/// <remarks>
///     <para>
///         <b>Elements rather than drawing, and that is forced rather than chosen.</b> A bar has a
///         name on it, and text belongs to an element in this framework — there is no way to draw a
///         string from <c>OnDraw</c>. <c>TimelineRuler</c> reaches the same conclusion for its tick
///         labels, and this is the same pattern one level up: absolutely-positioned children out of
///         a pool, parked rather than removed when the chart needs fewer.
///     </para>
///     <para>
///         ⚠ <b>Bars narrower than <see cref="MinimumBarWidth" /> are dropped, subtree and all.</b> A
///         capture of two hundred frames holds tens of thousands of scopes and almost all of them are
///         sub-pixel — realising an element per scope would build a subtree the style engine cannot
///         walk in a frame, to draw bars nobody can see or click. Dropping a bar drops what is inside
///         it too, because a child is never wider than its parent. Zooming in brings them back, which
///         is what zooming in is for.
///     </para>
///     <para>
///         ⚠ <b>Clicking a bar zooms to it rather than only selecting it.</b> That is what a flame
///         chart does everywhere it exists, and without it the whole control is unusable at any
///         capture longer than a frame: the interesting scope is four pixels wide and there is no
///         other way to reach it.
///     </para>
///     <para>
///         ⚠ <b>Laid out from <c>LayoutFinished</c> rather than from the setter.</b> Every bar's
///         position is a fraction of this control's width, and during a tick the width is whatever it
///         was last frame — so a chart realised on assignment lands at the previous size and jumps on
///         the next frame. <c>ConsoleView</c> follows its tail from the same hook for the same
///         reason.
///     </para>
/// </remarks>
public sealed partial class FlameChartView : Control {
    /// <summary>How tall one row of the chart is, in device-independent pixels.</summary>
    public const float RowHeight = 18f;

    /// <summary>How narrow a bar may be before it is not worth an element.</summary>
    public const float MinimumBarWidth = 2f;

    /// <summary>How many bars the chart will realise, whatever the zoom asks for.</summary>
    /// <remarks>
    ///     A ceiling rather than a target. The width test above removes almost everything on its own;
    ///     this is what stops a pathological capture — ten thousand scopes each a pixel wide, side by
    ///     side — from being the frame where the editor stops responding.
    /// </remarks>
    public const int MaximumBars = 2048;

    /// <summary>How many colours a scope name is hashed into.</summary>
    /// <remarks>
    ///     Eight, and they are classes rather than computed colours so that both themes can choose
    ///     their own eight. Enough that adjacent scopes rarely collide, few enough that a person
    ///     learns to recognise "the green one is culling" across captures.
    /// </remarks>
    public const int HueCount = 8;

    readonly List<UiElement> pool = [];
    readonly Dictionary<UiElement, FlameNode> shown = [];

    IReadOnlyList<FlameNode> roots = [];
    Action<UiDocument>? settle;
    long windowBegin;
    long windowEnd;
    int rows;

    /// <summary>What the last realise was for, so an unchanged frame does no work.</summary>
    /// <remarks>
    ///     ⚠ <b>The editor redraws every frame, and this control walks a tree to place its bars.</b>
    ///     Doc 20 lists "the editor gets slower one panel at a time" as a risk and names the layout
    ///     hook this runs from; a chart of two thousand bars re-placed sixty times a second for a
    ///     capture nobody is touching is exactly that. The three things a bar's position depends on
    ///     are the width, the window and which capture is loaded, so comparing them is the whole
    ///     test.
    /// </remarks>
    (float Width, long Begin, long End, int Version) realised = (-1f, 0, 0, -1);

    /// <summary>Bumped whenever the roots are replaced, so the guard above cannot alias.</summary>
    int version;

    /// <inheritdoc />
    protected override string TagName => "flame-chart";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>Which bar is selected, or <see langword="null" />.</summary>
    public FlameNode? Selected { get; private set; }

    /// <summary>How many rows deep the chart currently is.</summary>
    public int Rows => rows;

    /// <summary>How many bars are on screen.</summary>
    public int BarCount => shown.Count;

    /// <summary>Whether the chart is showing less than the whole capture.</summary>
    public bool IsZoomed { get; private set; }

    /// <summary>Raised when a bar is chosen, so a panel can show the scope's numbers.</summary>
    public event Action<FlameChartView, FlameNode>? Chosen;

    /// <summary>Points the chart at a set of root scopes.</summary>
    /// <param name="scopes">The roots, in begin order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scopes" /> is null.</exception>
    public void Show(IReadOnlyList<FlameNode> scopes) {
        ArgumentNullException.ThrowIfNull(scopes);

        roots = scopes;
        Selected = null;
        version++;

        Reset();
    }

    /// <summary>Puts the whole of what it was given back on screen.</summary>
    public void Reset() {
        windowBegin = long.MaxValue;
        windowEnd = long.MinValue;

        foreach (var root in roots) {
            windowBegin = Math.Min(windowBegin, root.Sample.BeginTicks);
            windowEnd = Math.Max(windowEnd, root.EndTicks);
        }

        if (windowBegin > windowEnd) {
            windowBegin = 0;
            windowEnd = 0;
        }

        IsZoomed = false;
        Realise();
    }

    /// <summary>Narrows the window to one scope.</summary>
    /// <param name="node">The scope to fill the chart with.</param>
    /// <exception cref="ArgumentNullException"><paramref name="node" /> is null.</exception>
    public void ZoomTo(FlameNode node) {
        ArgumentNullException.ThrowIfNull(node);

        windowBegin = node.Sample.BeginTicks;
        windowEnd = Math.Max(node.EndTicks, node.Sample.BeginTicks + 1);

        IsZoomed = true;
        Realise();
    }

    /// <summary>Rebuilds the bars for the current window and width.</summary>
    /// <remarks>Cheap when nothing that decides a bar's position has changed since the last call.</remarks>
    public void Realise() {
        var bounds = Bounds;
        var width = bounds.Width;

        if (realised == (width, windowBegin, windowEnd, version)) {
            return;
        }

        realised = (width, windowBegin, windowEnd, version);

        var slot = 0;

        rows = 0;

        if (width > 0f && windowEnd > windowBegin) {
            foreach (var root in roots) {
                Place(root, width, ref slot);
            }
        }

        for (var index = slot; index < pool.Count; index++) {
            pool[index].AddClass("parked");
            shown.Remove(pool[index]);
        }

        // The control's own height, so a chart nested in a scroller can be scrolled past rather than
        // clipping its deepest rows against whatever height the panel happened to give it.
        SetStyle("height", Length((rows + 1) * RowHeight));
    }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        // ⚠ A pointer event rather than a <c>ClickEvent</c>, and the reason is what a bar is. A
        // click is raised by <c>Control</c>, which is what turns a press and a release on the *same*
        // control into one activation — and a bar is deliberately a bare <c>UiElement</c>, because
        // two thousand of them being controls would be two thousand focus scopes, hover states and
        // event tables for something that is a coloured rectangle with a name on it.
        AddHandler<PointerEvent>(
            (element, args) => {
                if (element is FlameChartView view && args.Action is PointerAction.Pressed) {
                    view.Hit(args);
                }
            }
        );

        settle = _ => Realise();
        Document.LayoutFinished += settle;
    }

    /// <inheritdoc />
    protected override void OnRemoved() {
        if (settle is not null) {
            Document.LayoutFinished -= settle;
            settle = null;
        }

        base.OnRemoved();
    }

    /// <summary>Which colour class a scope name draws in.</summary>
    /// <remarks>
    ///     ⚠ <b>Hashed on the name rather than on the key's id.</b> A <c>ProfilingKey</c>'s id is the
    ///     order it was registered in, which depends on which subsystems happened to be touched first
    ///     — so colouring by id gives the same scope a different colour in two runs of the same
    ///     program, which defeats the only purpose colour has here.
    /// </remarks>
    public static int HueOf(string name) {
        ArgumentNullException.ThrowIfNull(name);

        var hash = 17;

        foreach (var character in name) {
            hash = (hash * 31) + character;
        }

        return (hash & int.MaxValue) % HueCount;
    }

    void Place(FlameNode node, float width, ref int slot) {
        if (slot >= MaximumBars) {
            return;
        }

        var left = Fraction(node.Sample.BeginTicks) * width;
        var right = Fraction(node.EndTicks) * width;
        var span = right - left;

        // Dropped with everything inside it: a child is never wider than its parent, so a subtree
        // under a sub-pixel bar is sub-pixel throughout.
        if (span < MinimumBarWidth) {
            return;
        }

        while (pool.Count <= slot) {
            pool.Add(Add<UiElement>("flame-bar"));
        }

        var bar = pool[slot++];

        for (var hue = 0; hue < HueCount; hue++) {
            bar.RemoveClass(HueClasses[hue]);
        }

        bar.RemoveClass("parked");
        bar.AddClass(HueClasses[HueOf(node.Name)]);

        bar.SetStyle("left", Length(left));
        bar.SetStyle("top", Length(node.Level * RowHeight));
        bar.SetStyle("width", Length(span));
        bar.SetStyle("height", Length(RowHeight - 1f));

        // ⚠ The name only once there is room for it. A bar forty pixels wide with a clipped
        // "Render.Shad" on it reads as a different scope from the one beside it saying
        // "Render.Shado", and a chart of truncated prefixes is harder to read than one of bare bars.
        bar.Text = span >= 44f
            ? string.Create(CultureInfo.InvariantCulture, $"{node.Name} {node.Milliseconds:0.##} ms")
            : null;

        bar.State = ReferenceEquals(Selected, node) ? bar.State | ElementState.Checked : bar.State & ~ElementState.Checked;

        shown[bar] = node;
        rows = Math.Max(rows, node.Level);

        foreach (var child in node.Children) {
            Place(child, width, ref slot);
        }
    }

    void Hit(PointerEvent args) {
        for (var element = args.Source; element is not null; element = element.Parent) {
            if (!shown.TryGetValue(element, out var node)) {
                continue;
            }

            // ⚠ The secondary button zooms back out rather than a second click doing it. A press is
            // not a click and has no tap count, and inventing one here would mean this control
            // keeping its own double-click timer — a second answer to a question the framework
            // already answers for every control that is one.
            var chosen = ReferenceEquals(Selected, node);

            Selected = node;

            // The selection draws as a border on the bar, so the guard in `Realise` has to see it
            // change — and it is not part of the window, which is what the guard otherwise compares.
            version++;

            Chosen?.Invoke(this, node);

            if (args.Button is PointerButton.Secondary || (chosen && IsZoomed)) {
                Reset();
            } else {
                ZoomTo(node);
            }

            args.Handled = true;
            return;
        }
    }

    float Fraction(long ticks) {
        var span = windowEnd - windowBegin;
        return span <= 0 ? 0f : (float)Math.Clamp((ticks - windowBegin) / (double)span, 0d, 1d);
    }

    static string Length(float value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "px";

    static readonly string[] HueClasses = [
        "flame-hue-0",
        "flame-hue-1",
        "flame-hue-2",
        "flame-hue-3",
        "flame-hue-4",
        "flame-hue-5",
        "flame-hue-6",
        "flame-hue-7"
    ];
}
