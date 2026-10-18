// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Layout;
using Vixen.Ui.Rendering;
using Vixen.Ui.Styling;

namespace Vixen.Ui;

/// <remarks>
///     <para>
///         <b>Retained drawing, the first slice of doc 49 § 7.3 (#671).</b> Every element remembers
///         the ranges of the previous frame its subtree wrote, and the key of everything that
///         emission read. A subtree whose every element still has the key it was drawn with, called
///         with the same inherited fade, is <i>replayed</i> — its commands and side-buffer entries
///         copied out of the frame before, offsets moved — instead of walked. The finished list is
///         identical to a full rebuild, byte for byte, and nothing downstream of
///         <see cref="DrawList.EndFrame" /> can tell the two apart.
///     </para>
///     <para>
///         ⚠ <b>Keyed on inputs rather than invalidated by writers, and that is the soundness
///         argument.</b> A dirty flag is correct only if every writer of every input remembers to
///         set it, which is the "an animation that forgets to declare itself freezes" failure #91
///         names. A key is compared every frame against what the element is now, so a writer cannot
///         forget anything: the style is interned and immutable (a reference), the box, the
///         transform, the lengths context, the border and padding, the fragments and the text
///         layout are values or immutable references, and the children are compared as a list.
///         What it can still miss is an input the key does not name, which is what the verifier
///         below is for.
///     </para>
///     <para>
///         ⚠ <b>An element whose type overrides <see cref="UiElement.OnDraw" /> is never
///         retained, and neither is anything above it.</b> A control's own drawing reads whatever
///         state it likes — a caret phase, a scroll offset, a chart's points — none of which a key
///         can name. Its siblings and its children's own subtrees are retained independently, so the
///         cost of a custom-drawn control is its ancestors' walk and not the frame's.
///     </para>
///     <para>
///         ⚠ <b>In a Debug build every replay is checked against a fresh emission of the same
///         subtree, and a difference throws naming the element.</b> That turns every Debug test in
///         the repository that draws a frame twice into a differential test of this file, which is
///         a larger corpus than any fixture could be. It is sound to check per subtree: a fresh
///         emission depends only on the element, the inherited fade and two builder-wide switches
///         (<see cref="Compositing" /> and <c>forced-colors</c>), and whatever the parent does
///         afterwards — a collapse, a discard — it does to either.
///     </para>
///     <para>
///         Forced colours are never retained: the system palette can change with no element
///         changing. <see cref="Compositing" /> and <see cref="ClearDiagnostics" /> forget everything,
///         the second because a replayed subtree re-refuses nothing.
///     </para>
/// </remarks>
public sealed partial class DrawListBuilder {
    /// <summary>Entries recorded or replayed this frame, in the order they finished.</summary>
    readonly List<RetainedDrawing> recorded = [];

    /// <summary>Whether a type draws itself, by type.</summary>
    readonly Dictionary<Type, bool> drawsItself = [];

    /// <summary>Bumped by anything that makes every retained subtree the wrong picture.</summary>
    int generation;

    /// <summary>Bumped per build, so a subtree is checked at most once a frame.</summary>
    int stamp;

    /// <summary>Whether a fresh emission is under way only to be compared with a replay.</summary>
    bool verifying;

    /// <summary>Whether unchanged subtrees are replayed rather than walked.</summary>
    /// <remarks>
    ///     On by default. Turning it off is the full rebuild every frame that this file replaces, and
    ///     is what the differential tests compare against.
    /// </remarks>
    internal bool Retaining {
        get;
        set {
            if (field != value) {
                generation++;
            }

            field = value;
        }
    } = true;

    /// <summary>Whether each replay is checked against a fresh emission, throwing on a difference.</summary>
    /// <remarks>
    ///     On in a Debug build, which is what every test runs. A differential test turns it off, so that what it compares against a full rebuild is the
    ///     replay a Release build ships rather than a replay already corrected by this check.
    /// </remarks>
    internal bool Verifying { get; set; }
#if DEBUG
        = true;
#endif

    /// <summary>How many commands have been replayed rather than built, over every frame.</summary>
    internal long CommandsReplayed { get; private set; }

    /// <summary>How many subtrees have been replayed, over every frame.</summary>
    internal long SubtreesReplayed { get; private set; }

    /// <summary>How many entries the frame has recorded so far, for a group to take back.</summary>
    int Recorded => recorded.Count;

    void BeginRetaining(DrawList into) {
        recorded.Clear();
        stamp++;

        // ⚠ Found by the verifier on its first run rather than by reading: a system colour keyword
        // (`background-color: Canvas`) is the same interned value in both appearances and resolves
        // against the palette at parse time, so an appearance change repaints with no element
        // changing at all. The palette's revision is what the parse cache already watches.
        if (palette.Revision != paletteRevision) {
            paletteRevision = palette.Revision;
            generation++;
        }
    }

    /// <summary>The <see cref="SystemPalette.Revision" /> the retained drawing was emitted against.</summary>
    int paletteRevision;

    void EndRetaining(DrawList into) => recorded.Clear();

    /// <summary>
    ///     Takes back every entry recorded since a group opened, because the group has just rewritten
    ///     the commands they name.
    /// </summary>
    /// <remarks>
    ///     A collapse fades the subtree's one command and removes the push in front of it; a discard
    ///     removes the push. Either way the ranges recorded inside no longer name what they did, and
    ///     a replay of one would fade a command twice. Both happen only to a subtree of at most one
    ///     command, so what this costs is a few leaves, which their parent's own entry still covers.
    /// </remarks>
    void Unrecord(int from) {
        for (var i = from; i < recorded.Count; i++) {
            recorded[i].Ranged = false;
        }
    }

    /// <summary>Notes that an element is about to walk its children, and with what fade.</summary>
    void Descending(UiElement element, float alpha) {
        if (verifying || element.Drawn is not { } entry) {
            return;
        }

        entry.Descended = true;
        entry.ChildAlpha = alpha;
    }

    /// <summary>Emits one element and its subtree, replaying it when nothing it reads has changed.</summary>
    /// <inheritdoc cref="EmitFresh" />
    void Emit(UiDocument document, UiElement element, DrawList into, float inherited) {
        if (verifying || !Retaining || forced) {
            EmitFresh(document, element, into, inherited);
            return;
        }

        if (TryReplay(document, element, into, inherited)) {
            return;
        }

        var entry = element.Drawn ??= new RetainedDrawing();
        var start = into.Mark;

        entry.Descended = false;
        entry.ChildAlpha = 0f;

        EmitFresh(document, element, into, inherited);

        entry.List = into;
        entry.Frame = into.Frames;
        entry.Generation = generation;
        entry.Ranged = true;
        entry.Start = start;
        entry.End = into.Mark;
        entry.Inherited = inherited;
        entry.Checked = 0;
        entry.Retainable = Retainable(element, entry, into);

        // Keyed only when it could ever be replayed, so an ancestor of a custom-drawn control —
        // which the root of every real interface is — pays nothing for the key it will never use.
        if (entry.Retainable) {
            entry.Key = KeyOf(document, element);
            Snapshot(element, entry);
        }

        recorded.Add(entry);
    }

    bool Retainable(UiElement element, RetainedDrawing entry, DrawList into) {
        if (DrawsItself(element)) {
            return false;
        }

        if (!entry.Descended) {
            return true;
        }

        foreach (var child in element.PaintOrder) {
            if (child.SurfaceRoot is not null) {
                continue;
            }

            if (child.Drawn is not { Retainable: true } drawn || drawn.List != into || drawn.Frame != into.Frames) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether an element's type draws something of its own beyond its style.</summary>
    /// <remarks>
    ///     Asked of the type once, through the delegate's target rather than a reflection walk:
    ///     binding <see cref="UiElement.OnDraw" /> to the instance resolves the virtual call, and the
    ///     method it lands on says who declared it. Anything that goes wrong in asking is read as
    ///     "draws itself", which costs a replay and never a picture.
    /// </remarks>
    bool DrawsItself(UiElement element) {
        var type = element.GetType();

        if (drawsItself.TryGetValue(type, out var answer)) {
            return answer;
        }

        try {
            Action<DrawContext> draw = element.OnDraw;
            answer = draw.Method.DeclaringType != typeof(UiElement);
        } catch (NotSupportedException) {
            answer = true;
        }

        drawsItself[type] = answer;
        return answer;
    }

    bool TryReplay(UiDocument document, UiElement element, DrawList into, float inherited) {
        if (element.Drawn is not { Ranged: true } entry
            || entry.List != into
            || entry.Frame != into.Frames - 1
            || !entry.Inherited.Equals(inherited)
            || !Unchanged(document, element, into)) {
            return false;
        }

        var start = into.Mark;

        if (!into.Replay(entry.Start, entry.End)) {
            return false;
        }

        var end = into.Mark;

        if (Verifying) {
            Verify(document, element, into, inherited, start, end);
        }

        Move(element, entry.Start, start, into);

        CommandsReplayed += end.Commands - start.Commands;
        SubtreesReplayed++;
        return true;
    }

    /// <summary>Whether an element and everything it drew still has the key it was drawn with.</summary>
    /// <remarks>
    ///     Memoised per frame, so a parent that finds a change deep down does not make each of its
    ///     descendants on the way there walk the same subtree again: the frame's checking is linear.
    /// </remarks>
    bool Unchanged(UiDocument document, UiElement element, DrawList into) {
        if (element.Drawn is not { Retainable: true } entry || entry.List != into || entry.Generation != generation) {
            return false;
        }

        if (entry.Checked == stamp) {
            return entry.Clean;
        }

        var clean = KeyOf(document, element).Equals(entry.Key) && ChildrenUnchanged(document, element, entry, into);

        entry.Checked = stamp;
        entry.Clean = clean;
        return clean;
    }

    bool ChildrenUnchanged(UiDocument document, UiElement element, RetainedDrawing entry, DrawList into) {
        // The same key makes the same early-return decisions, so an element that stopped short of
        // its children last time stops short of them again and they are not part of its drawing.
        if (!entry.Descended) {
            return true;
        }

        var index = 0;

        foreach (var child in element.PaintOrder) {
            if (child.SurfaceRoot is not null) {
                continue;
            }

            if (index >= entry.Children.Length
                || !ReferenceEquals(entry.Children[index++], child)
                || child.Drawn is not { } drawn
                || !drawn.Inherited.Equals(entry.ChildAlpha)
                || !Unchanged(document, child, into)) {
                return false;
            }
        }

        return index == entry.Children.Length;
    }

    /// <summary>
    ///     Moves the ranges of everything inside a replayed subtree to where the replay put them, so
    ///     that any of them can be replayed out of this frame by the next one.
    /// </summary>
    void Move(UiElement element, DrawListMark from, DrawListMark to, DrawList into) {
        var entry = element.Drawn!;

        // ⚠ The frame moves on either way: it is also what says to the parent's `Retainable` that
        // this entry describes this frame's drawing, which a replayed child does.
        entry.Ranged = entry.Ranged && entry.Frame == into.Frames - 1;
        entry.Frame = into.Frames;

        if (entry.Ranged) {
            entry.Start = entry.Start.Moved(from, to);
            entry.End = entry.End.Moved(from, to);
        }

        if (entry.Descended) {
            foreach (var child in entry.Children) {
                Move(child, from, to, into);
            }
        }

        recorded.Add(entry);
    }

    /// <summary>Emits the subtree again beside its replay and throws if the two differ.</summary>
    void Verify(UiDocument document, UiElement element, DrawList into, float inherited, DrawListMark start, DrawListMark end) {
        var fonts = into.FontCount;

        verifying = true;

        try {
            EmitFresh(document, element, into, inherited);
        } finally {
            verifying = false;
        }

        var difference = into.FirstDifference(start, end, into.Mark);
        into.Truncate(end, fonts);

        if (difference >= 0) {
            throw new InvalidOperationException(
                $"A retained drawing went stale: {Describe(element)} "
                + $"replayed a subtree that differs from a fresh emission at its command {difference}. Something the "
                + "element's drawing reads is missing from DrawListBuilder.KeyOf — add it there; turning retention off "
                + "only hides it (#671)."
            );
        }
    }

    /// <summary>Where an element is, as the tags from the root down to it.</summary>
    static string Describe(UiElement element) {
        var path = new List<string>();

        for (var at = element; at is not null; at = at.Parent) {
            path.Add(at.Tag);
        }

        path.Reverse();
        return string.Join(" > ", path);
    }

    /// <summary>Remembers the children an element walked, allocating only when they changed.</summary>
    static void Snapshot(UiElement element, RetainedDrawing entry) {
        if (!entry.Descended) {
            entry.Children = [];
            return;
        }

        var count = 0;
        var same = true;

        foreach (var child in element.PaintOrder) {
            if (child.SurfaceRoot is not null) {
                continue;
            }

            same &= count < entry.Children.Length && ReferenceEquals(entry.Children[count], child);
            count++;
        }

        if (same && count == entry.Children.Length) {
            return;
        }

        var children = new UiElement[count];
        count = 0;

        foreach (var child in element.PaintOrder) {
            if (child.SurfaceRoot is null) {
                children[count++] = child;
            }
        }

        entry.Children = children;
    }

    /// <summary>Everything about one element that its own emission reads.</summary>
    /// <remarks>
    ///     <para>
    ///         Read off <see cref="EmitFresh" />, <c>EmitBody</c> and what they call: every
    ///         <c>element.</c> and <c>document.</c> the builder touches is one of these or is a
    ///         function of the style. The parent's part — the fade it passes down — is checked beside
    ///         the key rather than in it, as <see cref="RetainedDrawing.Inherited" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The style by reference, not by value.</b> <see cref="ComputedStyle" /> is
    ///         immutable and interned, so the same values are the same instance — but its equality
    ///         ignores <see cref="ComputedStyle.Parent" />, and a key that is stricter than it has to
    ///         be costs a replay where one looser than it has to be costs a picture.
    ///     </para>
    /// </remarks>
    DrawKey KeyOf(UiDocument document, UiElement element) {
        var node = element.LayoutNode;
        var layout = document.Layout;
        var width = element.Width;
        var height = element.Height;

        var fragments = layout.GetFragmentCount(node);
        var first = fragments > 0 ? layout.GetFragment(node, 0) : default;
        var hash = 0;

        for (var index = 1; index < fragments; index++) {
            hash = HashCode.Combine(hash, layout.GetFragment(node, index));
        }

        var border = new Edges(
            layout.GetComputedBorder(node, Edge.Left),
            layout.GetComputedBorder(node, Edge.Top),
            layout.GetComputedBorder(node, Edge.Right),
            layout.GetComputedBorder(node, Edge.Bottom)
        );

        var padding = new Edges(
            layout.GetComputedPadding(node, Edge.Left),
            layout.GetComputedPadding(node, Edge.Top),
            layout.GetComputedPadding(node, Edge.Right),
            layout.GetComputedPadding(node, Edge.Bottom)
        );

        var content = width - border.Left - padding.Left - border.Right - padding.Right;
        var empty = width <= 0f || height <= 0f;

        // ⚠ The text only where `EmitBody` would read it — a box with size that is not
        // `visibility: hidden` — and not merely because it is there. Asked unconditionally, this was
        // a `new List<FontFace>()` a frame for every font-less text leaf the editor shell lays out
        // zero tall, which `EditorShellBudgetTests` measured as 4 000 bytes a settled frame: the full
        // rebuild never asks those leaves for their text at all.
        var shown = !element.Style.TryGet(visibility, out var mode) || (mode != hidden && mode != collapse);

        return new DrawKey(
            element.Style,
            element.AbsoluteLeft,
            element.AbsoluteTop,
            width,
            height,
            element.Transform,
            element.BackfaceHidden,
            element.AppliedLengths,
            element.FontSize,
            empty ? layout.GetStyle(node).Display : default,
            fragments,
            first,
            hash,
            border,
            padding,
            shown && !empty ? element.Ellipsized(content) : null
        );
    }

    /// <summary>Four edges of a box, in pixels.</summary>
    internal readonly record struct Edges(float Left, float Top, float Right, float Bottom);

    /// <summary>What <see cref="KeyOf" /> reads.</summary>
    /// <remarks>
    ///     ⚠ The fragments past the first are a hash rather than a list, which is the one place the
    ///     key can collide; only an inline box wrapped over several lines has any, and a collision
    ///     there needs the same first fragment and the same box. The verifier would name it.
    /// </remarks>
    internal readonly struct DrawKey(
        ComputedStyle style,
        float x,
        float y,
        float width,
        float height,
        UiTransform? transform,
        bool backfaceHidden,
        LengthContext lengths,
        float fontSize,
        Display display,
        int fragments,
        (float, float, float, float, LayoutFragmentEnds) first,
        int rest,
        Edges border,
        Edges padding,
        TextLayout? text
    ) : IEquatable<DrawKey> {
        readonly ComputedStyle style = style;
        readonly float x = x;
        readonly float y = y;
        readonly float width = width;
        readonly float height = height;
        readonly UiTransform? transform = transform;
        readonly bool backfaceHidden = backfaceHidden;
        readonly LengthContext lengths = lengths;
        readonly float fontSize = fontSize;
        readonly Display display = display;
        readonly int fragments = fragments;
        readonly (float, float, float, float, LayoutFragmentEnds) first = first;
        readonly int rest = rest;
        readonly Edges border = border;
        readonly Edges padding = padding;
        readonly TextLayout? text = text;

        public bool Equals(DrawKey other) =>
            ReferenceEquals(style, other.style)
            && ReferenceEquals(text, other.text)
            && x.Equals(other.x)
            && y.Equals(other.y)
            && width.Equals(other.width)
            && height.Equals(other.height)
            && Nullable.Equals(transform, other.transform)
            && backfaceHidden == other.backfaceHidden
            && lengths.Equals(other.lengths)
            && fontSize.Equals(other.fontSize)
            && display == other.display
            && fragments == other.fragments
            && first.Equals(other.first)
            && rest == other.rest
            && border.Equals(other.border)
            && padding.Equals(other.padding);

        public override bool Equals(object? obj) => obj is DrawKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(style, x, y, width, height);
    }

    /// <summary>What one element drew last frame, and everything that drawing read.</summary>
    internal sealed class RetainedDrawing {
        /// <summary>The list the ranges are in.</summary>
        public DrawList? List;

        /// <summary>The frame of <see cref="List" /> the ranges are in.</summary>
        public int Frame;

        /// <summary>The builder generation it was drawn under.</summary>
        public int Generation;

        /// <summary>Whether <see cref="Start" /> and <see cref="End" /> still name its drawing.</summary>
        public bool Ranged;

        /// <summary>Where its drawing starts.</summary>
        public DrawListMark Start;

        /// <summary>Where its drawing ends.</summary>
        public DrawListMark End;

        /// <summary>The fade its parent handed it.</summary>
        public float Inherited;

        /// <summary>Whether it walked its children, and the fade it handed them.</summary>
        public bool Descended;

        /// <summary>The fade it handed its children.</summary>
        public float ChildAlpha;

        /// <summary>Whether neither it nor anything it drew draws itself.</summary>
        public bool Retainable;

        /// <summary>The children it walked, in paint order.</summary>
        public UiElement[] Children = [];

        /// <summary>Everything its own emission read.</summary>
        internal DrawKey Key;

        /// <summary>The frame stamp it was last checked at, and the answer.</summary>
        public int Checked;

        /// <summary>The answer it was last checked with.</summary>
        public bool Clean;
    }
}
