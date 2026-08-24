// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Layout;
using Vixen.Ui.Rendering;
using Vixen.Ui.Styling;

namespace Vixen.Ui;

/// <summary>Turns a laid-out, styled tree into a list of things to draw.</summary>
/// <remarks>
///     <para>
///         The last step of the chain this assembly exists to complete: the cascade said what
///         applies, the bridge turned that into lengths, flexbox turned those into rectangles, and
///         this turns the rectangles into commands. Nothing here decides anything — it reads.
///     </para>
///     <para>
///         <b>Painting order is <see cref="UiElement.PaintOrder" /></b>, parent before its children
///         and siblings in the order they were added unless a <c>z-index</c> says otherwise. That is
///         the same property hit testing walks in reverse, and the two have to agree: an element
///         drawn on top must be the one a click lands on, and any rule that made them disagree would
///         be a UI where things are not where they look. Neither of them having its own opinion is
///         what guarantees it.
///     </para>
/// </remarks>
public sealed class DrawListBuilder {
    /// <summary>How far past a viewport an edge is pushed when its axis is not clipped.</summary>
    /// <remarks>
    ///     ⚠ <b>A stand-in for infinity, chosen so that the sums stay exact.</b> A million pixels is
    ///     more than a hundred times the widest display anyone has, and two million is still well
    ///     inside the range where a <c>float</c> counts whole numbers one at a time — where
    ///     <c>float.MaxValue</c> would give a right edge of infinity and an infinite width a right edge
    ///     of NaN, and a NaN in the clip stack silently unclips everything below it.
    /// </remarks>
    internal const float UnboundedClip = 1_000_000f;

    readonly List<PositionedGlyph> placed = [];
    readonly StyleValueParser parser;
    readonly int backgroundColor;

    /// <summary>The four <c>border-*-color</c> longhands, clockwise from the top.</summary>
    readonly int[] borderColors;

    /// <summary>The four <c>border-*-radius</c> longhands, clockwise from the top left.</summary>
    readonly int[] borderRadii;

    /// <summary>The four logical corner radii, in the physical order they take under <c>ltr</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>Indexed to match <see cref="borderRadii" /> under <c>direction: ltr</c> and not under
    ///     <c>rtl</c>, which is the whole reason this array is separate rather than merged in at
    ///     load.</b> <c>border-start-start-radius</c> is the top-left corner in one direction and the
    ///     top-right in the other, so which physical corner a logical longhand feeds is not known
    ///     until an element is in hand — see <see cref="Corners" />.
    /// </remarks>
    readonly int[] logicalRadii;

    /// <summary>The four <c>outline-*</c> longhands.</summary>
    /// <remarks>
    ///     ⚠ <b>An outline is not a thin border, and the three ways it differs are all in
    ///     <see cref="EmitOutline" /> rather than here.</b> It is drawn <i>outside</i> the border box
    ///     rather than inside it, it takes no space in the layout — <c>Vixen.Ui.Layout</c> is never
    ///     told about it, which is what makes that true rather than approximately true — and it
    ///     follows the border radius outward instead of sharing it.
    /// </remarks>
    readonly int outlineWidth;

    readonly int outlineStyle;
    readonly int outlineColor;
    readonly int outlineOffset;

    /// <summary>The <c>outline-style: none</c> keyword. Its sibling <c>hidden</c> is the visibility one.</summary>
    readonly int styleNone;

    readonly int backgroundImage;
    readonly GradientReader gradients;

    /// <summary><c>mask-image</c>, read by <see cref="MasksFor" /> and by nothing else.</summary>
    /// <remarks>
    ///     ⚠ <b>Two of the <c>mask-*</c> longhands are read and the rest are absent rather than
    ///     ignored.</b> <c>mask-position</c>, <c>mask-size</c>, <c>mask-origin</c> and
    ///     <c>mask-repeat</c> all describe where a mask <i>image</i> is placed inside a box it does
    ///     not already fill; a gradient sized to the border box needs none of them, and honouring one
    ///     without the rest would place a mask the next property could not move. See
    ///     <c>InertProperties.txt</c>, which records them as absent for that reason.
    /// </remarks>
    readonly int maskImage;

    /// <summary><c>mask-composite</c>, read by <see cref="MasksFor" /> and by nothing else.</summary>
    /// <remarks>
    ///     ⚠ <b>Read as text and cached by value id, not interned as four keywords.</b> It is a
    ///     <i>list</i> property — <c>mask-composite: add, intersect</c> is two operators for two
    ///     layers — so the common single-keyword case is only the one-element case of the general
    ///     one, and a keyword comparison would have had to fall back to text for the rest anyway.
    /// </remarks>
    readonly int maskComposite;

    /// <summary>The operators of each <c>mask-composite</c> value seen, keyed by its id.</summary>
    /// <remarks>
    ///     The cache <see cref="GradientReader" /> keeps for the same reason: an interned id names a
    ///     fixed piece of text forever, so parsing it twice can only ever produce the same answer.
    /// </remarks>
    readonly Dictionary<int, MaskComposite[]> maskComposites = [];

    /// <summary>The table declaration values are interned in, for <see cref="MasksFor" />.</summary>
    readonly NameTable values;
    readonly int textColor;
    readonly OverflowReader overflow;
    readonly int visibility;
    readonly int hidden;
    readonly int collapse;
    readonly int opacity;
    readonly int filter;

    /// <summary>The property <see cref="Backdrop" /> reads, which is <i>not</i> <see cref="filter" />.</summary>
    /// <remarks>
    ///     ⚠ <b>A second property and not a second value of the first, because the two transform
    ///     different pictures and an element may carry both.</b> <c>filter</c> transforms what the
    ///     element drew; <c>backdrop-filter</c> transforms what is behind it. Reading one into the
    ///     other's field draws a picture that is wrong in a way the draw list cannot show, because
    ///     both open the same bracket.
    /// </remarks>
    readonly int backdropFilter;

    readonly int blurFunction;
    readonly int dropShadowFunction;

    /// <summary>The <c>opacity()</c> function, which only <c>backdrop-filter</c> accepts.</summary>
    /// <remarks>
    ///     ⚠ <b>Refused inside <c>filter</c> deliberately, and it is not an omission waiting to be
    ///     filled.</b> <c>UtilityComposition.Filter</c> emits nine functions and this is not one of
    ///     them, so nothing in the engine generates it there — while
    ///     <c>UtilityComposition.BackdropFilter</c> emits it for <c>backdrop-opacity-*</c>, which is one
    ///     of the ten roots the feature exists for. Accepting it in both would mean <see cref="ElementFilter" />
    ///     needed somewhere to put an alpha scale that <see cref="UiColorMatrix" /> cannot carry, on a
    ///     path where nothing would ever set it.
    /// </remarks>
    readonly int opacityFunction;

    /// <summary>The seven <c>filter</c> functions that are a colour matrix, interned in order.</summary>
    /// <remarks>
    ///     ⚠ An array indexed by <see cref="FilterFunction" /> rather than seven fields, because
    ///     <see cref="Filter" /> has to turn a keyword id back into <i>which</i> function it is and a
    ///     chain of seven comparisons is where the eighth one gets forgotten. The order is the enum's
    ///     and nothing else may reorder it.
    /// </remarks>
    readonly int[] filterFunctions;

    readonly int textAlign;
    readonly int direction;
    readonly int decorationLine;
    readonly int decorationColor;
    readonly int decorationStyle;
    readonly int decorationThickness;
    readonly int underlineOffset;
    readonly int keywordUnderline;
    readonly int keywordOverline;
    readonly int keywordLineThrough;
    readonly int keywordDouble;
    readonly int boxShadow;
    readonly int currentColor;
    readonly int alignedCenter;
    readonly int alignedLeft;
    readonly int alignedRight;
    readonly int alignedEnd;
    readonly int rtl;

    /// <summary>Creates a builder over a style engine's name tables.</summary>
    /// <param name="properties">The table property names are interned in.</param>
    /// <param name="values">The table declaration values are interned in.</param>
    /// <param name="keywords">The table identifiers are interned in.</param>
    public DrawListBuilder(NameTable properties, NameTable values, NameTable keywords) {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(keywords);

        parser = new StyleValueParser(values, keywords);

        backgroundColor = properties.Intern("background-color");

        // ⚠ <b>The image is a second layer and not an alternative to the colour.</b> CSS paints
        // `background-image` over `background-color`, so an element with both draws twice — which
        // matters the moment a gradient's near stop is `transparent`, as every `bg-linear-*` with no
        // `from-*` is. Reading one and skipping the other would be a coin toss dressed as a choice.
        backgroundImage = properties.Intern("background-image");
        gradients = new GradientReader(values, parser);

        // ⚠ Read through the *same* `GradientReader`, which is the whole reason a mask gradient and a
        // background gradient written the same way line up. A second reader tuned for masks would be
        // a second set of refusals to keep in step, and `mask-image: linear-gradient(...)` is the
        // identical production — only what is taken out of the result differs.
        maskImage = properties.Intern("mask-image");

        // ⚠ Its default is `add` and *not* `intersect`, which is the one thing about this property
        // that is worth being sure of. CSS Masking 1 § 5.4 gives `add` as the initial value, so a
        // hand-written two-layer `mask-image` with nothing beside it unions its layers. Tailwind's
        // mask utilities all write `intersect` explicitly, because that is the operator under which
        // an unset layer — which they emit as a fully opaque gradient — changes nothing.
        maskComposite = properties.Intern("mask-composite");
        this.values = values;

        // ⚠ The longhands, never the shorthands, and *all* of them. A shorthand is expanded before
        // it is interned — by ExCSS while parsing when the value is literal, and by
        // `ShorthandExpansion` at load when it holds a `var()`, which ExCSS is obliged to hand back
        // whole — so the cascade never carries a `border-color` or a `border-radius` for anything to
        // read. Written against the shorthands, every border and every rounded corner in the
        // document silently disappears.
        //
        // ⚠ <b>And it used to intern only the first of each set, which is not a smaller version of
        // the same thing.</b> Reading `border-top-color` alone made `border-b-accent` inert, as
        // expected — but it also made `border-top-width` paint a ring on all four edges, made the
        // other three widths paint nothing at all, and made `border-bottom-right-radius` disappear
        // while `border-top-left-radius` silently rounded the whole box. Twenty-one rules in the
        // editor's own themes were written against the three that draw nothing.
        borderColors = [
            properties.Intern("border-top-color"),
            properties.Intern("border-right-color"),
            properties.Intern("border-bottom-color"),
            properties.Intern("border-left-color")
        ];

        borderRadii = [
            properties.Intern("border-top-left-radius"),
            properties.Intern("border-top-right-radius"),
            properties.Intern("border-bottom-right-radius"),
            properties.Intern("border-bottom-left-radius")
        ];

        // ⚠ <b>The block half of each name is physical here and only the inline half is resolved,
        // and that asymmetry is deliberate rather than unfinished.</b> `Vixen.Ui.Layout` has no
        // writing mode, so the block axis is top-to-bottom in every configuration the engine can be
        // in and the leading `start`/`end` of each pair *is* top/bottom. The trailing half is the
        // inline axis, which `direction: rtl` genuinely mirrors — the same property
        // `StyleResolution.LeftEdge` mirrors the logical insets with — so it is the only half that
        // needs an element to resolve. Written in the `ltr` order so the array indexes line up with
        // `borderRadii` in the common case and `Corners` swaps a pair for `rtl`.
        logicalRadii = [
            properties.Intern("border-start-start-radius"),
            properties.Intern("border-start-end-radius"),
            properties.Intern("border-end-end-radius"),
            properties.Intern("border-end-start-radius")
        ];
        // ⚠ <b>No <c>outline</c> shorthand, for the reason the border longhands above give — ExCSS
        // expands it while parsing — and no <c>outline-style</c> *value* table beyond the two
        // keywords that switch the ring off.</b> Every other keyword draws a solid ring, which is
        // the same bargain `border-style` is not allowed to make and is allowed here for a reason
        // worth stating: no utility can produce one. `outline-dashed`, `-dotted` and `-double` are
        // not registered — there is no dash pattern in `Vixen.Ui` — so the only way to reach a
        // refused keyword is to hand-write it in a `.vcss`, where drawing solid is CSS's own
        // fallback behaviour for a style the UA cannot render rather than a family that lies.
        outlineWidth = properties.Intern("outline-width");
        outlineStyle = properties.Intern("outline-style");
        outlineColor = properties.Intern("outline-color");
        outlineOffset = properties.Intern("outline-offset");
        // ⚠ `outline-style: hidden` is spelled with the same id `visibility: hidden` is, because
        // `values` interns one table of keyword text and the two properties genuinely say the same
        // word. Reading `hidden` here rather than interning a second one is not a shortcut: a value
        // id is the text, and which property it was written on is the caller's business.
        styleNone = values.Intern("none");

        textColor = properties.Intern("color");
        overflow = new OverflowReader(properties, values);

        visibility = properties.Intern("visibility");
        this.hidden = values.Intern("hidden");
        collapse = values.Intern("collapse");
        opacity = properties.Intern("opacity");
        filter = properties.Intern("filter");

        // ⚠ The unprefixed spelling, and it is the only one anything emits. Tailwind writes
        // `-webkit-backdrop-filter` beside it for Safari; `UtilityFamilies` deliberately does not,
        // because a vendor prefix is a fact about a browser and this is not one — see
        // `UtilityFamilies.BackdropAlongside`.
        backdropFilter = properties.Intern("backdrop-filter");

        // ⚠ The keywords table, for the reason `currentColor` below says: a function name arrives
        // from `StyleValueParser` as an identifier, and an identifier is interned there.
        blurFunction = keywords.Intern("blur");
        dropShadowFunction = keywords.Intern("drop-shadow");
        opacityFunction = keywords.Intern("opacity");

        // In `FilterFunction`'s order, which `Filter` indexes by. The names are the parser's own
        // spellings — see `StyleValueParser.ParseFunction`, which interns exactly these seven.
        filterFunctions = [
            keywords.Intern("brightness"),
            keywords.Intern("contrast"),
            keywords.Intern("grayscale"),
            keywords.Intern("invert"),
            keywords.Intern("saturate"),
            keywords.Intern("sepia"),
            keywords.Intern("hue-rotate")
        ];

        textAlign = properties.Intern("text-align");
        direction = properties.Intern("direction");

        // ⚠ The four longhands and not `text-decoration`. ExCSS expands the shorthand while parsing,
        // exactly as it does `border` and `border-radius`, so a cascade written against the
        // shorthand carries nothing at all — `AssetEditorTheme.vcss` has had a
        // `text-decoration: line-through` in it the whole time this drew nothing, and reading the
        // shorthand would have kept it that way while looking like a fix.
        decorationLine = properties.Intern("text-decoration-line");
        decorationColor = properties.Intern("text-decoration-color");
        decorationStyle = properties.Intern("text-decoration-style");
        decorationThickness = properties.Intern("text-decoration-thickness");
        underlineOffset = properties.Intern("text-underline-offset");

        // The keywords table rather than `values`, for the reason `currentcolor` below gives: an
        // identifier reaches here through the one `StyleValueParser` was handed for keywords, and an
        // id from the wrong table can never compare equal.
        keywordUnderline = keywords.Intern("underline");
        keywordOverline = keywords.Intern("overline");
        keywordLineThrough = keywords.Intern("line-through");
        keywordDouble = keywords.Intern("double");

        // ⚠ Neither `auto` nor `from-font` is interned, and that is a statement rather than an
        // omission. CSS distinguishes them because `auto` lets the user agent pick a thickness it
        // likes and `from-font` insists on the face's; this engine only ever uses the face's, so the
        // two requests have one answer and `TextLength` reaches it without having to tell them apart.
        // The classes still resolve to the two different values — the difference is visible in the
        // cascade and absent from the picture, which is what parity means here.
        boxShadow = properties.Intern("box-shadow");
        // ⚠ The <i>keywords</i> table, not <c>values</c>. `StyleValueParser` interns an identifier it
        // does not recognise as a colour into the one it was handed for keywords, and the two tables
        // are separate — interning here from the wrong one gives an id that can never compare equal
        // and a `currentcolor` that silently refuses the declaration instead of resolving it.
        currentColor = keywords.Intern("currentcolor");

        alignedCenter = values.Intern("center");
        alignedLeft = values.Intern("left");
        alignedRight = values.Intern("right");
        alignedEnd = values.Intern("end");
        this.rtl = values.Intern("rtl");
    }

    /// <summary>Whether a translucent subtree is isolated into a group, or faded element by element.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>On by default since both executors landed, and the default is about the
    ///         <i>consumer</i> rather than about which answer is right.</b> Isolating is what CSS
    ///         Compositing 1 § 3 specifies and <see cref="DrawCommandKind.LayerPush" /> is how this asks
    ///         for it; fading each element is the approximation this file carried for years. But a group
    ///         only becomes a picture if whoever executes the draw list can render an offscreen surface
    ///         — and a consumer that ignores <c>UiGeometry.Layers</c> does something worse than
    ///         approximate: it draws the group's contents inline at <i>full</i> strength and skips the
    ///         composite, so a faded panel comes out opaque.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So turning this on is not a property of this file, and setting it back to true after
    ///         someone turns it off is not the whole of restoring it.</b> Both executors composite now —
    ///         <c>SoftwareUiRasterizer</c> always did, <c>UiRenderer.Compose</c> does as of the GPU half
    ///         — but a renderer that <i>can</i> composite still does not unless its host calls
    ///         <c>Compose</c> once a frame, and <c>Compose</c> itself returns having done nothing when
    ///         the host handed over no <c>UiShaders.Image</c>, because that is the stage a surface is
    ///         composited back with. A host missing either is in the opaque-panel state above, not in
    ///         the approximation. <c>EditorHost</c> and the <c>vixen-app</c> template do both; a host
    ///         written against neither has to be checked.
    ///     </para>
    ///     <para>
    ///         It is a switch rather than a capability the renderer reports because the decision has to
    ///         be made while the <i>draw list</i> is built, which is upstream of anything that knows what
    ///         a texture is. It stays a switch now that it is on, because that is what a host with a
    ///         consumer of its own has to reach for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The two settings must not be mixed within one application.</b> The whole hazard the
    ///         compositing work was written against is a picture that differs between the renderer that
    ///         ships and the one the baselines are recorded with; a host that left this off while its
    ///         test suite turned it on would have built that hazard deliberately.
    ///     </para>
    /// </remarks>
    public bool Compositing { get; set; } = true;

    /// <summary>Walks a document and fills a draw list.</summary>
    /// <param name="document">The document, already updated.</param>
    /// <param name="into">The list to fill.</param>
    /// <returns>Whether the drawing differs from the previous frame's.</returns>
    public bool Build(UiDocument document, DrawList into) {
        ArgumentNullException.ThrowIfNull(document);
        return Build(document, document.Root, into);
    }

    /// <summary>Walks one surface of a document and fills a draw list.</summary>
    /// <param name="document">The document, already updated.</param>
    /// <param name="root">The surface's root — <see cref="UiSurface.Root" />.</param>
    /// <param name="into">The list to fill.</param>
    /// <returns>Whether the drawing differs from the previous frame's.</returns>
    /// <remarks>
    ///     One list per window, because one window's frame is not another's. The walk stops at any
    ///     <i>other</i> surface's root it meets: a torn-off panel is still a child of this tree, and
    ///     drawing it here would put a copy of it in the main window at whatever coordinates its own
    ///     window happens to use.
    /// </remarks>
    public bool Build(UiDocument document, UiElement root, DrawList into) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(into);

        into.BeginFrame();
        Emit(document, root, into, 1f);
        return into.EndFrame();
    }

    /// <summary>Emits one element and its subtree.</summary>
    /// <param name="document">The document.</param>
    /// <param name="element">The element.</param>
    /// <param name="into">The list being filled.</param>
    /// <param name="inherited">
    ///     The <c>opacity</c> of everything above this element, multiplied together.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Opacity <s>is carried down as a multiplier rather than composited as a group</s> is
    ///         a group, and the difference was visible.</b> CSS Compositing 1 § 3 renders a translucent
    ///         element's subtree into its own surface and blends that surface once, so two overlapping
    ///         children of a half-opaque panel do <i>not</i> show through each other. Multiplying each
    ///         element's alpha instead made them show through. That was owed here for as long as this
    ///         file has existed; <see cref="DrawCommandKind.LayerPush" /> is the answer.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The multiplier has not gone away, and it is not a leftover.</b> A group whose
    ///         contents come to a <i>single</i> command is exactly equal to fading that command — see
    ///         <see cref="DrawList.Collapse" /> for the arithmetic — and a group is only ever needed
    ///         when two fragments might overlap. So the layer is opened optimistically and taken back
    ///         when the subtree turns out to be one thing, which is what almost every
    ///         <c>opacity-*</c> in this repository is written on.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><paramref name="inherited" /> is the fade still waiting to be applied by
    ///         multiplication, which is not the same as the accumulated opacity once groups exist.</b>
    ///         With <see cref="Compositing" /> off it <i>is</i> the accumulated opacity and this file
    ///         behaves exactly as it always did. With it on, a group resets it to one — the group's
    ///         surface carries the fade instead — so it is one everywhere, and what composes two nested
    ///         opacities is the collapse rather than this parameter: an inner group that came to one
    ///         command fades that command, and the outer group then fades the same command again, which
    ///         is the product.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A group that survives always contributes at least three commands, which is what
    ///         makes the collapse safe to nest.</b> A push, its contents and a pop cannot be mistaken
    ///         by an enclosing element for the single command it is allowed to fade in place — so a
    ///         parent can never collapse <i>across</i> a child's surviving surface and quietly fade its
    ///         contents twice.
    ///     </para>
    /// </remarks>
    void Emit(UiDocument document, UiElement element, DrawList into, float inherited) {
        var width = element.Width;
        var height = element.Height;

        // A zero-sized element draws nothing and clips nothing, and skipping it early keeps
        // `display: none` — which flexbox reports as a zero box — out of the list entirely rather
        // than in it as a stack of invisible commands.
        if (width <= 0f || height <= 0f) {
            return;
        }

        var own = Opacity(element);

        // ⚠ Fully transparent is skipped outright rather than emitted with a zero alpha, and the
        // subtree with it — `opacity: 0` is not inherited, but it multiplies, so nothing below can
        // bring it back. A frame full of invisible commands costs a batch and a draw each and is
        // indistinguishable in the picture from having emitted nothing.
        if (inherited * own <= 0f) {
            return;
        }

        // ⚠ <b>The second reason to open a group, and the first one that is not <i>optional</i>.</b>
        // An opacity can always be approximated by fading each element — that is what this file did
        // for years and what `Compositing` off still does. A blur cannot: it is a function of the
        // rasterised subtree, so with no surface there is nothing to convolve and the honest answer
        // is the unblurred picture. Read before the group is decided because it is half of that
        // decision.
        var filters = Filter(document, element);

        // ⚠ <b>A group is opened for the element's <i>own</i> opacity only.</b> What it inherited is
        // already being carried by a surface further up, or by the multiplier — either way it is not
        // this element's to isolate a second time. CSS agrees: each translucent element forms one
        // stacking context, and nesting two of them composites twice, which is what the product of
        // the two alphas means.
        //
        // ⚠ <b>And for a colour matrix on the same terms as a blur, which is the half of this that
        // looks optional and is not.</b> A per-pixel colour transform could plausibly be pushed down
        // onto each command's colour — no surface, no pass — and the result would be wrong wherever
        // two of the group's children overlap with partial coverage, because CSS transforms the
        // group's *rendered* result and not each thing in it. Filter Effects 1 § 5 says so outright:
        // any `filter` other than `none` makes the element a stacking context.
        // ⚠ <b>The third reason to open a group, and non-optional on exactly the blur's terms.</b>
        // A mask multiplies the *rendered* subtree's coverage, so pushing it down onto each command's
        // alpha would be right on a bare panel and wrong wherever two children overlap: two half-
        // covered things masked separately and then blended do not give the blend masked once. CSS
        // agrees — Masking 1 § 5 makes any `mask` other than `none` a stacking context, in the same
        // sentence shape Filter Effects uses for `filter`.
        //
        // ⚠ <b>And a list rather than one mask, which is not a generalisation for its own sake — it
        // is what twelve of Tailwind's mask roots need.</b> `mask-t-from-*` and its siblings are
        // per-edge ramps that only mean anything combined, under the `mask-composite` CSS gives as a
        // separate property. See `UiMask.Coverage`, which is the fold, and `DrawList.Masks`, which is
        // where the entries live.
        Span<UiMask> list = stackalloc UiMask[GradientReader.MostLayers];
        var masks = MasksFor(element, width, height, list);

        // ⚠ <b>The fourth reason to open a group, and the only one whose surface holds something the
        // element did not draw.</b> A <c>backdrop-filter</c> transforms the picture <i>behind</i> the
        // element, which needs the element to be a boundary in the paint order — Filter Effects 2 § 2
        // makes it a backdrop root and a stacking context for exactly that reason. Pushing it down
        // onto the element's own commands is not merely wrong here, it is meaningless: none of them
        // knows what is under it.
        var backdrop = Backdrop(document, element);

        // ⚠ <b>The fifth reason to open a group, and the only one where the group is not an isolation
        // but a change of coordinates.</b> The other four leave the subtree where it is and transform
        // what came out of it; this one leaves what came out of it alone and moves it. What they share
        // is the reason a surface is needed at all: a `DrawCommand` is an axis-aligned rectangle, so
        // there is no per-command form of a rotation to push down — the same shape of argument as a
        // colour matrix, arriving at the same seam from the other side.
        //
        // ⚠ <b>Read off the element rather than resolved here, because the hit test needs the same
        // matrix and neither of them may own it.</b> `UiDocument.Accumulate` composes it once per
        // pass, origin folded in; a transform painted from one composition and clicked through another
        // is the failure `TransformTests` exists to make unstateable.
        //
        // ⚠ <b>And it is `Compositing`-gated with the rest, which is a real consequence rather than
        // an oversight.</b> With compositing off there is no surface, so a rotated element paints
        // unrotated — the same bargain the other four make, and the same one `UiLayer.Blur`'s remark
        // describes for a consumer that ignores it. The hit test is not gated, so it would then be
        // clicked where it is *not* drawn; that is stated in the guide rather than papered over,
        // because the flag exists for tests that want a draw list with no brackets in it.
        var placed = element.Transform;

        // ⚠ <b>A degenerate transform skips the subtree outright, on `opacity: 0`'s terms and for a
        // sharper reason.</b> `scale: 0` — and `scale: 1 0`, and any composition that collapses to a
        // line — maps every point of the element to zero area, so there is nothing it could paint.
        // Dropping it later, where the group is resolved, is *not* the same thing and was measured
        // wrong: the subtree's own draws are appended as the walk descends, so a group discarded at
        // the geometry stage leaves them behind and the element paints at full size, unscaled, which
        // is the opposite of what was asked for. `scale-0` is a real class and a common way to hide
        // something, so this is the ordinary path rather than an edge case.
        //
        // ⚠ Ungated by `Compositing`, unlike the group below, because this is not a compositing
        // decision. An element scaled to nothing is invisible on any renderer, and the hit test
        // refuses it through the same singular matrix — see `UiDocument.HitTest`.
        if (placed is { } collapsed && collapsed.Invert() is null) {
            return;
        }

        var group = Compositing && (own < 1f || filters.Any || masks > 0 || backdrop is not null || placed is not null)
            ? into.Count
            : -1;

        if (group >= 0) {
            into.Add(
                new DrawCommand(
                    DrawCommandKind.LayerPush,
                    element.AbsoluteLeft,
                    element.AbsoluteTop,
                    width,
                    height,
                    new Color4(0f, 0f, 0f, own),
                    0f,
                    0f
                ) {
                    Blur = filters.Blur,
                    Filter = filters.Colour,
                    Shadow = filters.Shadow,
                    Backdrop = backdrop,
                    Transform = placed,

                    // ⚠ The range is claimed even when the group turns out to be discarded a few
                    // lines below, and the entries it named are then simply unreferenced until the
                    // next `BeginFrame` clears the buffer. Nothing reads them — a discarded group has
                    // no `LayerPush` left to name them — and they are the same entries every frame,
                    // so the diff is not disturbed either. Reclaiming them would mean trimming a
                    // buffer that a *nested* group may already have appended to.
                    Offset = masks > 0 ? into.AddMasks(list[..masks]) : 0,
                    Length = masks
                }
            );
        }

        // Inside the group the element paints at full strength and the surface carries the fade;
        // outside it the multiplier does, which is both the collapsed case and the whole of the
        // behaviour when <see cref="Compositing" /> is off.
        var alpha = group >= 0 ? 1f : inherited * own;

        EmitBody(document, element, into, alpha, width, height);

        if (group < 0) {
            return;
        }

        // ⚠ <b>Nothing, one thing, or a group — decided after the fact because it cannot be known
        // before.</b> An element's own commands are countable from its style, but `OnDraw` lets a
        // control emit anything it likes, so a predicate written up here would be a guess that is
        // wrong for exactly the controls that draw the most.
        var drawn = into.Count - group - 1;

        if (drawn == 0) {
            // ⚠ Discarded even when there is a blur, and that is not the same exception the collapse
            // needs. Blurring nothing gives nothing however wide the kernel is, so the surface would
            // cost two render passes to convolve a cleared target — the group is genuinely empty, not
            // merely small.
            //
            // ⚠ <b>And discarded even when there is a <c>backdrop-filter</c>, which is a stated
            // divergence rather than the same argument.</b> Blurring the backdrop of an element that
            // paints nothing of its own is a picture CSS would show and this does not. The reason is
            // structural and not thrift: a group with no draws has <c>Count == 0</c>, and both
            // executors walk the layer list by matching a draw index — a zero-width range matches its
            // own start and never advances. Every glass panel in practice paints a background, which
            // is what <c>bg-white/30</c> is for; an element that wants only the blur can carry a
            // fully transparent background to become one.
            into.Discard(group);
            return;
        }

        // ⚠ <b>The peephole is an identity for opacity and a lie for a filter, so a filtered group is
        // never collapsed.</b> `Collapse` throws the bracket away and multiplies the one command's
        // alpha instead — exactly right when the surface's only job was to carry a fade, and exactly
        // wrong here: a blurred rectangle is not a fainter rectangle, and neither is a grey one, and
        // the picture that came out would be sharp and full-colour at whatever opacity the filter had
        // nothing to do with. The single-command case is not rare enough to leave to chance either,
        // since a `blur-*` or a `grayscale` on a bare panel is one background rectangle and nothing
        // else, which is precisely the shape this branch catches.
        // ⚠ `masks == 0` joins the guard for the filter's reason word for word: a masked rectangle
        // is not a fainter rectangle either, and a single background rectangle under a `mask-*` is
        // exactly the shape this branch catches.
        // ⚠ `backdrop is null` joins the guard for the filter's reason and a stronger one: a
        // collapsed group has no surface *and no bracket*, so there is nothing left to say which
        // prefix of the frame the backdrop was to be captured from. A single background rectangle
        // under a `backdrop-blur-*` is precisely the shape of every glass panel there is.
        // ⚠ `placed is null` joins the guard for the filter's reason and the starkest version of it: a
        // rotated rectangle is not a fainter rectangle, and the collapse throws away the *bracket* —
        // so a group folded here would lose the only thing carrying the matrix and paint the element
        // square, at full strength, in the place it was not asked to be. A single background rectangle
        // under a `rotate-*` or a `scale-*` is precisely the shape this branch catches, which is to say
        // it is the commonest transformed element there is.
        if (!filters.Any
            && masks == 0
            && backdrop is null
            && placed is null
            && drawn == 1
            && DrawList.Fadeable(into.Commands[group + 1])) {
            into.Collapse(group, inherited * own);
            return;
        }

        into.Add(
            new DrawCommand(
                DrawCommandKind.LayerPop,
                element.AbsoluteLeft,
                element.AbsoluteTop,
                width,
                height,
                new Color4(0f, 0f, 0f, own),
                0f,
                0f
            ) {
                Blur = filters.Blur,
                Filter = filters.Colour,
                Shadow = filters.Shadow,
                Backdrop = backdrop,
                Transform = placed,

                // ⚠ The same range as the push names, and not a second copy of the entries.
                // `UiGeometryBuilder.Layer` reads the push's copy and never this one — see its
                // `Opening` remark — so appending here would put a duplicate list in the side buffer
                // for every masked group in the frame, which nothing would read and the diff would
                // walk.
                Offset = into.Commands[group].Offset,
                Length = masks
            }
        );
    }

    /// <summary>Everything an element paints, once the question of a group has been settled.</summary>
    /// <remarks>
    ///     Split out of <see cref="Emit" /> so that the group brackets it without the body having to
    ///     know: every early return in here would otherwise have to remember to close a layer, which
    ///     is precisely the pairing failure the clip stack's own remark warns about.
    /// </remarks>
    void EmitBody(UiDocument document, UiElement element, DrawList into, float alpha, float width, float height) {
        var x = element.AbsoluteLeft;
        var y = element.AbsoluteTop;

        var corners = Corners(element);

        // ⚠ The scalar every command still carries, and it is the *uniform* radius or nothing. A box
        // whose corners differ carries its radii in the side buffer and a zero here — putting the
        // top-left corner in the scalar instead would leave a consumer that reads only `Radius`
        // rounding all four corners by one of them, which is precisely the bug this file is fixing.
        var radius = corners.IsUniformCircular(out var uniform) ? uniform : 0f;

        // ⚠ `visibility: hidden` hides the element and *not* its subtree, which is what separates it
        // from `display: none`. It is an inherited property, so a child is hidden by having
        // inherited the value rather than by being skipped here — and a child that declares
        // `visibility: visible` reappears inside a hidden parent, which is the whole reason CSS has
        // two properties for this.
        //
        // ⚠ <b>`collapse` reads as `hidden` here, and that is CSS 2.1 §11.2 rather than an
        // approximation.</b> The third keyword only means something different on a table row, a
        // table column and their groups; on every other box the spec says it "has the same meaning
        // as `hidden`". This engine has no table formatting context at all — no `display: table-row`,
        // no `border-collapse` — so there is no box in it for which the other reading is the right
        // one, and mapping the keyword here is complete rather than partial.
        //
        // ⚠ The one place that is <i>not</i> true is a flex item, where Flexbox §4.1 makes a
        // collapsed item keep its contribution to the line's cross size — a strut whose main size
        // goes to zero. That is a layout effect and this is the paint walk, so it is not refused
        // here so much as unreachable from here: it needs `LayoutStyle` to carry the keyword, which
        // it does not. Suppressing the paint is right in that case too and strictly closer than the
        // previous behaviour, which was to paint a collapsed item in full. See the triage note in
        // `docs/plan/43-web-styling-parity.md`.
        var shown = !element.Style.TryGet(visibility, out var mode) || (mode != hidden && mode != collapse);

        if (shown) {
            // Before the background, which is where CSS paints it: a shadow is cast *by* the box and
            // therefore lies under it, and an element with a translucent background shows its own
            // shadow through itself.
            EmitShadow(document, element, into, x, y, width, height, corners, radius, alpha);

            if (Color(element, backgroundColor) is { } fill) {
                into.Add(
                    Styled(
                        new DrawCommand(
                            DrawCommandKind.Rectangle,
                            x,
                            y,
                            width,
                            height,
                            Fade(fill, alpha),
                            radius,
                            0f
                        ),
                        into,
                        corners
                    )
                );
            }

            EmitGradient(element, into, x, y, width, height, corners, radius, alpha);

            // The border is drawn after the background and before the children, which is the order
            // CSS paints them in — a child overlapping the edge covers the border, and a background
            // never covers its own.
            EmitBorder(document, element, into, x, y, width, height, corners, radius, alpha);

            // ⚠ <b>After the border and — the part that matters — <i>before</i> the overflow clip
            // this element is about to push.</b> `overflow: hidden` clips an element's content and
            // its descendants; it does not clip the element's own outline, which is drawn outside
            // the box the clip is made from. Emitting this two blocks lower, inside the clip, would
            // have removed the whole ring on every scrolling container in the editor — invisibly,
            // because a clipped-away ring and an unemitted one are the same picture.
            //
            // ⚠ <b>What this does not do is CSS's painting order, and the difference is real but
            // small.</b> CSS Painting §3 paints every outline in the stacking context *after* every
            // box in it, so an outline is on top of a later sibling that overlaps it; here it is
            // painted with its own element and a later sibling covers it. Matching the spec needs a
            // deferred list the whole tree walk appends to, which is the same machinery
            // `box-shadow` would need and does not have either. It shows only where a ring and a
            // sibling overlap, which is where `outline-offset` has already been asked to reach
            // under a neighbour.
            EmitOutline(element, into, x, y, width, height, corners, alpha);
        }

        var axes = overflow.Of(element.Style);
        if (axes.Any) {
            // ⚠ An unclipped axis is a pair of edges at infinity, and `UnboundedClip` stands in for
            // infinity because the arithmetic that consumes this cannot hold it: the clip stack
            // intersects rectangles, and an infinite width gives `X + Width` as a NaN that swallows
            // every clip below it. A finite stand-in is not an approximation here — the stack starts
            // from the viewport and only ever narrows, so an edge past the viewport is bounded by the
            // viewport, which is exactly what "not clipped on this axis" means.
            var left = axes.Horizontal ? x : -UnboundedClip;
            var top = axes.Vertical ? y : -UnboundedClip;
            var across = axes.Horizontal ? width : 2f * UnboundedClip;
            var down = axes.Vertical ? height : 2f * UnboundedClip;

            into.Add(new DrawCommand(DrawCommandKind.ClipPush, left, top, across, down, default, radius, 0f));
        }

        if (shown) {
            // Between the border and the children, which is where CSS puts an element's own content:
            // a child overlaps its parent's text, and its parent's text overlaps its parent's border.
            //
            // ⚠ <b>Inside the clip, and it used to be outside it.</b> `overflow` clips an element's
            // *content*, and an element's own text is content — the background and the border are
            // the two things it does not clip, which is why the push is below them and not above.
            // Emitting the text first meant `overflow: hidden` clipped an element's children and
            // never its own string, so a label too long for a fixed column drew straight across
            // whatever was beside it. Five places in the editor had written `overflow: hidden` on a
            // text-bearing element believing otherwise, and every one of them was a column that
            // silently overdrew its neighbour. It survived because a clip is invisible to the
            // element tree: every rectangle was the right size and the glyphs went somewhere else.
            EmitText(document, element, into, alpha);
            element.OnDraw(new DrawContext(element, into, alpha));
        }

        // Paint order rather than document order, which are the same list unless some child carries
        // a `z-index`. Hit testing walks the same property backwards, and that is the whole reason
        // it is a property of the element rather than a loop written twice.
        foreach (var child in element.PaintOrder) {
            // Another window's tree, which this frame is not. It is walked by its own surface's
            // build, against its own size and its own pixel grid.
            if (child.SurfaceRoot is not null) {
                continue;
            }

            Emit(document, child, into, alpha);
        }

        // ⚠ Popped only if it was pushed, and popped after the children rather than at the end of
        // the frame. A list whose pushes and pops do not pair is not a drawing with a mistake in it,
        // it is a clip stack that never unwinds — everything after the offending element stays
        // clipped to it for the rest of the frame.
        if (axes.Any) {
            into.Add(new DrawCommand(DrawCommandKind.ClipPop, x, y, width, height, default, radius, 0f));
        }
    }

    /// <summary>Emits an element's border: one ring when it is uniform, one band per edge when not.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The uniform case is the one that already worked, and it comes out byte for byte the
    ///         same.</b> Four equal widths and four equal colours are a single
    ///         <see cref="DrawCommandKind.Border" /> command — one quad, one distance field, one
    ///         antialiased outer edge shared by the border and the fill it sits on. Every box in every
    ///         theme in this repository that draws a border at all takes this path, which is what makes
    ///         the change safe: the fast path is not a new fast path, it is the old only path.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The non-uniform case is bands, and it is not a ring with extra colours.</b> The box
    ///         shader resolves a border as the difference of two coverages — the outline and the same
    ///         outline pushed <c>thickness</c> inwards — so the thickness and the colour are properties
    ///         of the <i>shape</i>, not of a side of it. There is no per-pixel notion of which edge a
    ///         fragment belongs to, and adding one means four more colours and four more thicknesses in
    ///         <see cref="Rendering.UiShape" />: eighty more bytes on a record every box in the frame
    ///         writes, to describe something almost none of them have. So an element whose edges differ
    ///         is drawn as up to four plain rectangles instead, which cost nothing anywhere else and
    ///         batch with the backgrounds around them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The horizontal edges take the corners and the vertical ones are inset between
    ///         them</b>, which is the join CSS draws only when the two edges meeting at a corner are
    ///         the same colour — otherwise it mitres them diagonally. A mitre is a triangle and this
    ///         emits rectangles, so the difference shows exactly when two adjacent edges are both thick
    ///         <i>and</i> differently coloured; at the one pixel every such rule in this repository
    ///         actually uses, the mitre is a single pixel and there is nothing to see. Said here rather
    ///         than fixed because the fix is the eighty bytes above.
    ///     </para>
    /// </remarks>
    void EmitBorder(
        UiDocument document,
        UiElement element,
        DrawList into,
        float x,
        float y,
        float width,
        float height,
        CornerRadii corners,
        float radius,
        float alpha
    ) {
        // Clockwise from the top, which is the order CSS lists the edges in and the order the colour
        // table above is interned in. The two agreeing is what lets one index mean one edge.
        var top = document.Layout.GetComputedBorder(element.LayoutNode, Edge.Top);
        var right = document.Layout.GetComputedBorder(element.LayoutNode, Edge.Right);
        var bottom = document.Layout.GetComputedBorder(element.LayoutNode, Edge.Bottom);
        var left = document.Layout.GetComputedBorder(element.LayoutNode, Edge.Left);

        if (top <= 0f && right <= 0f && bottom <= 0f && left <= 0f) {
            return;
        }

        var topColor = Color(element, borderColors[0]);
        var rightColor = Color(element, borderColors[1]);
        var bottomColor = Color(element, borderColors[2]);
        var leftColor = Color(element, borderColors[3]);

        var square = top == right && right == bottom && bottom == left;
        var oneColour = topColor == rightColor && rightColor == bottomColor && bottomColor == leftColor;

        if (square && oneColour) {
            if (topColor is { } stroke) {
                into.Add(
                    Styled(
                        new DrawCommand(
                            DrawCommandKind.Border,
                            x,
                            y,
                            width,
                            height,
                            Fade(stroke, alpha),
                            radius,
                            top
                        ),
                        into,
                        corners
                    )
                );
            }

            return;
        }

        // ⚠ The vertical bands are inset by the horizontal thicknesses and not the other way round,
        // so the corner square belongs to the top and bottom edges. Giving it to both would draw it
        // twice — which is invisible for an opaque colour and a doubled alpha for a translucent one,
        // and a border at 50% opacity is exactly what a focus ring is made of.
        var middle = MathF.Max(height - top - bottom, 0f);

        Band(topColor, top, x, y, width, top, corners.TopLeft, corners.TopRight, default, default);
        Band(bottomColor, bottom, x, y + height - bottom, width, bottom, default, default, corners.BottomRight, corners.BottomLeft);
        Band(leftColor, left, x, y + top, left, middle, default, default, default, default);
        Band(rightColor, right, x + width - right, y + top, right, middle, default, default, default, default);

        void Band(
            Color4? colour,
            float thickness,
            float bandX,
            float bandY,
            float bandWidth,
            float bandHeight,
            Vector2 topLeft,
            Vector2 topRight,
            Vector2 bottomRight,
            Vector2 bottomLeft
        ) {
            if (thickness <= 0f || bandWidth <= 0f || bandHeight <= 0f || colour is not { } fill) {
                return;
            }

            // ⚠ A band is a *filled* rectangle, not a border one. Its thickness is already its height
            // or its width, and asking the shader for a ring as well would hollow out a strip one
            // pixel tall into nothing at all.
            into.Add(
                Styled(
                    new DrawCommand(
                        DrawCommandKind.Rectangle,
                        bandX,
                        bandY,
                        bandWidth,
                        bandHeight,
                        Fade(fill, alpha),
                        0f,
                        0f
                    ),
                    into,
                    new CornerRadii(topLeft, topRight, bottomRight, bottomLeft)
                )
            );
        }
    }

    /// <summary>Emits an element's outline: a ring outside the border box that costs the layout nothing.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The whole feature is a <see cref="DrawCommandKind.Border" /> command on a bigger
    ///         rectangle, and reusing that kind rather than adding one is the load-bearing decision
    ///         here.</b> The box shader already resolves a ring as the difference of two coverages —
    ///         the shape, and the shape pushed <c>thickness</c> inwards — so a rectangle grown by
    ///         <c>offset + width</c> with a thickness of <c>width</c> produces a band occupying
    ///         exactly <c>[border edge + offset, border edge + offset + width]</c>, which is CSS UI 4
    ///         § 3.4's outline. A new <see cref="DrawCommandKind" /> would have needed a shader
    ///         branch, a <see cref="Rendering.UiGeometryBuilder" /> case, a
    ///         <c>SoftwareUiRasterizer</c> case and a fourth copy of the same distance field, to draw
    ///         a picture the existing one already draws.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing here touches the layout, and that is the property the reuse could most
    ///         easily have broken.</b> An outline does not take space: <c>Vixen.Ui.Layout</c> is never
    ///         told about <c>outline-width</c>, no <c>GetComputedBorder</c> is consulted, and the
    ///         rectangle is grown at paint time out of the box the layout already finished. So a ring
    ///         appearing on focus moves nothing on the screen — which is the entire reason CSS has a
    ///         second ring property at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The corner radius grows with the ring, except where it is zero.</b> CSS UI 4 makes
    ///         the outline follow the border curve, so the outer radius is the border radius plus the
    ///         distance the ring was pushed out — but a square corner stays square however far out it
    ///         goes, rather than acquiring a curve the box it traces does not have. Both halves are
    ///         what browsers do and the second is the one that looks wrong if you skip it: every
    ///         unrounded box in the editor would have grown soft corners the moment it took focus.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>outline-style</c> is read for exactly two keywords and every other value draws
    ///         solid.</b> <c>none</c> and <c>hidden</c> switch the ring off — CSS UI 4 makes them
    ///         synonyms on an outline, unlike on a border — and that pair is what <c>outline-none</c>
    ///         and <c>outline-hidden</c> compile to. <b>The forced-colors half of Tailwind's
    ///         <c>outline-hidden</c> is not expressible here and is not emulated:</b> v4 pairs the
    ///         <c>none</c> with a transparent two-pixel ring inside
    ///         <c>@media (forced-colors: active)</c>, and <c>MediaQuery</c> has no forced-colors
    ///         feature to evaluate, so the class collapses to <c>outline-none</c> exactly. Said here
    ///         because the two spellings being indistinguishable is the thing a reader will otherwise
    ///         assume is a bug.
    ///     </para>
    /// </remarks>
    void EmitOutline(
        UiElement element,
        DrawList into,
        float x,
        float y,
        float width,
        float height,
        CornerRadii corners,
        float alpha
    ) {
        if (element.Style.TryGet(outlineStyle, out var style) && (style == styleNone || style == hidden)) {
            return;
        }

        var thickness = OutlineLength(element, outlineWidth);

        if (thickness <= 0f) {
            return;
        }

        // ⚠ <b>Falls back to the text colour and not to a fixed one, because that is what
        // <c>outline-color</c>'s initial value means.</b> CSS UI 4 gives it <c>auto</c>, which is the
        // UA's focus colour where the UA has one and <c>currentColor</c> where it does not; this
        // engine has no focus colour of its own, so the foreground is the whole of the answer.
        // `Fade` is applied last so a group's opacity reaches the ring the same way it reaches the
        // border.
        var stroke = Color(element, outlineColor) ?? Color(element, textColor) ?? Color4.Black;

        // ⚠ Negative offsets are real and are not clamped. `-outline-offset-2` pulls the ring inside
        // the border box, which CSS allows and which is how a ring is drawn on an element that has
        // no room outside it. What *is* clamped is the resulting rectangle: pulled in far enough the
        // ring inverts, and a negative width is a shape the geometry builder cannot make.
        var grow = OutlineLength(element, outlineOffset) + thickness;

        var ringWidth = width + (2f * grow);
        var ringHeight = height + (2f * grow);

        if (ringWidth <= 0f || ringHeight <= 0f) {
            return;
        }

        var grown = new CornerRadii(
            Grow(corners.TopLeft, grow),
            Grow(corners.TopRight, grow),
            Grow(corners.BottomRight, grow),
            Grow(corners.BottomLeft, grow)
        );

        into.Add(
            Styled(
                new DrawCommand(
                    DrawCommandKind.Border,
                    x - grow,
                    y - grow,
                    ringWidth,
                    ringHeight,
                    Fade(stroke, alpha),
                    grown.IsUniformCircular(out var uniform) ? uniform : 0f,
                    thickness
                ),
                into,
                grown
            )
        );

        // A square corner stays square, and only a corner that already had a curve grows one. The
        // component-wise max is what keeps a shrinking ring — a negative offset — from folding a
        // small radius through zero into a curve bending the other way.
        static Vector2 Grow(Vector2 corner, float by) => new(
            corner.X > 0f ? MathF.Max(corner.X + by, 0f) : 0f,
            corner.Y > 0f ? MathF.Max(corner.Y + by, 0f) : 0f
        );
    }

    /// <summary>One <c>outline-*</c> length, in absolute pixels.</summary>
    /// <remarks>
    ///     Absolute lengths only, which is <see cref="Radius" />'s rule and is complete rather than
    ///     partial for these two properties: CSS UI 4 gives <c>outline-width</c> a <c>&lt;length&gt;</c>
    ///     and three keywords, gives <c>outline-offset</c> a bare <c>&lt;length&gt;</c>, and allows a
    ///     percentage on neither. Every value the utility families can emit is a pixel count.
    /// </remarks>
    float OutlineLength(UiElement element, int property) {
        if (!element.Style.TryGet(property, out var id)) {
            return 0f;
        }

        var value = parser.Parse(id);

        return value.Kind == StyleValueKind.Length && value.Unit is StyleUnit.Pixels or StyleUnit.None
            ? value.Number
            : 0f;
    }

    /// <summary>Emits an element's text, if it has any and there is a font for it.</summary>
    /// <remarks>
    ///     <para>
    ///         Positioned against the <b>content box</b> — inside the border and the padding — rather
    ///         than against the element's edge, because that is what those two properties mean. Read
    ///         from the layout results rather than from the style, so a percentage padding is the
    ///         number flexbox resolved rather than a percentage this would have to resolve again.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The y is the baseline, not the top.</b> Glyph origins sit on the baseline, so the
    ///         run's origin is the content box's top plus the font's ascender. Putting the top there
    ///         instead draws every line one ascender too low, which for a single line looks like a
    ///         padding mistake and for two lines looks like nothing at all.
    ///     </para>
    /// </remarks>
    void EmitText(UiDocument document, UiElement element, DrawList into, float alpha) {
        var borderLeft = document.Layout.GetComputedBorder(element.LayoutNode, Edge.Left);
        var paddingLeft = document.Layout.GetComputedPadding(element.LayoutNode, Edge.Left);
        var left = element.AbsoluteLeft + borderLeft + paddingLeft;

        var top = element.AbsoluteTop
            + document.Layout.GetComputedBorder(element.LayoutNode, Edge.Top)
            + document.Layout.GetComputedPadding(element.LayoutNode, Edge.Top);

        // Against the content box, which is what the run was positioned against — using the border
        // box here would push centred text off by half the padding, in the direction that looks like
        // the padding is uneven.
        var content = element.Width
            - borderLeft
            - paddingLeft
            - document.Layout.GetComputedBorder(element.LayoutNode, Edge.Right)
            - document.Layout.GetComputedPadding(element.LayoutNode, Edge.Right);

        // ⚠ The drawn block, not `Block()`. Under `text-overflow: ellipsis` a line too wide for the
        // content box comes back ending in one — and the content box is only known here, because
        // `truncate` sets `white-space: nowrap` and the wrap pass is therefore given an infinite
        // width on purpose. Everything else — the caret, hit testing, the measure function — goes on
        // reading the untruncated `Block()`, which is what those want.
        if (element.Ellipsized(content) is not { } block) {
            return;
        }

        var foreground = Color(element, textColor) ?? Color4.Black;
        var color = Fade(foreground, alpha);
        var decoration = Decoration(element);

        // ⚠ The decoration's own colour falls back to the *unfaded* foreground and is faded once.
        // Fading an already-faded colour would square the alpha, which is invisible at full opacity
        // and halves the bar against the text inside anything the compositor has dimmed.
        var decorationColour = decoration.Color is { } own ? Fade(own, alpha) : color;

        // ⚠ One command per run *per line*, because a command names one font and lies on one
        // baseline. A wrapped paragraph in two faces is four commands, and each of them carries its
        // own origin — which is also why a run's glyphs are placed from zero rather than the whole
        // block being placed once and sliced: a slice would put every later command's glyphs at
        // coordinates relative to the first one's origin.
        foreach (var line in block.Lines) {
            // ⚠ The alignment is per line, not per block. A centred paragraph centres each of its
            // lines within the content box; centring the block and laying the lines out inside it
            // would left-align every line but the widest.
            //
            // ⚠ <b>And a `text-indent` is taken out of the room the alignment has to play with,
            // rather than added to the result.</b> That is what makes the two compose the way CSS
            // says: an indented right-aligned line still ends flush with the content box's right
            // edge — the indent narrows the line box from the start edge — and a centred one is
            // centred in what is left after it. Adding `line.Offset` to `x` afterwards would push a
            // right-aligned line past the edge by the indent.
            var x = left + Indent(element, content - line.Width - line.Offset);
            var y = top + block.TopOf(block.Lines.IndexOf(line)) + line.Baseline;

            // The bars go under the glyphs and not under the indent, so they start where the glyphs
            // do. `PenOf` carries the same offset, which is why the runs below need nothing.
            EmitDecoration(into, line, decoration, decorationColour, x + line.Offset, y, under: true);

            for (var i = 0; i < line.Runs.Length; i++) {
                var run = line.Runs[i];

                placed.Clear();
                run.Place(placed);

                if (placed.Count == 0) {
                    continue;
                }

                // The glyphs are placed relative to the start of the run and the command carries
                // where that is, rather than each glyph carrying an absolute position. Two identical
                // labels in different places then hold identical glyph runs, which is what will let
                // the batcher notice.
                into.Add(
                    new DrawCommand(
                        DrawCommandKind.Text,
                        x + line.PenOf(i),
                        y,
                        run.Width,
                        line.Height,
                        color,
                        0f,
                        0f
                    ) {
                        Offset = into.AddGlyphs(placed),
                        Length = placed.Count,
                        Font = into.AddFont(run.Font),
                        FontSize = run.Size
                    }
                );
            }

            EmitDecoration(into, line, decoration, decorationColour, x + line.Offset, y, under: false);
        }
    }

    /// <summary>Emits one line's decoration bars, on one side of its glyphs.</summary>
    /// <param name="into">The draw list.</param>
    /// <param name="line">The line being decorated.</param>
    /// <param name="decoration">The resolved style. Nothing is emitted when it asks for nothing.</param>
    /// <param name="colour">The bar colour, already faded and already resolved from <c>currentColor</c>.</param>
    /// <param name="x">Where the line starts, after alignment.</param>
    /// <param name="y">Its baseline.</param>
    /// <param name="under">Whether to emit the lines painted under the glyphs or the ones over them.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A decoration is a rectangle, which is why this needed no command kind, no shader
    ///         and no second implementation.</b> It goes out as <see cref="DrawCommandKind.Rectangle" />
    ///         with a zero radius, so it reaches <c>UiGeometryBuilder.Box</c>, the rounded-box field
    ///         and both executors by exactly the path a background already takes — the software
    ///         rasteriser and the device draw the same bar because they are drawing the same quad,
    ///         rather than because two ports of a line-drawing routine were kept in step.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Per <i>line</i>, spanning <c>line.Width</c>, and taking its metrics from the
    ///         line's first run.</b> Three consequences worth stating. It decorates the gaps between
    ///         runs, which a per-run bar would leave as visible breaks in the middle of a word that
    ///         happened to change face. It is one rectangle rather than one per run, on a list that is
    ///         compared command by command every frame. And it uses the same width the alignment used
    ///         — <see cref="TextLine.Width" />, which excludes trailing whitespace — so a centred
    ///         underline is centred under the text rather than under the text plus a space. Taking the
    ///         first run's metrics where the faces differ is CSS Text Decoration 3 § 3's "first
    ///         available font" rule, and the alternative, a bar per run at its own thickness, draws a
    ///         visible step in the middle of a line.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It moves nothing that was measured.</b> The bars are placed from a baseline the
    ///         layout has already fixed and are never fed back into <c>TextLayout.Measure</c>, so an
    ///         underlined paragraph occupies exactly the box the same paragraph occupied without one.
    ///         That is CSS's rule and it is also the only behaviour compatible with measurement
    ///         reporting whole device pixels: a decoration that widened a line would round the block
    ///         up and move every element after it, for a mark that is not part of the text.
    ///     </para>
    /// </remarks>
    static void EmitDecoration(
        DrawList into,
        TextLine line,
        TextDecoration decoration,
        Color4 colour,
        float x,
        float y,
        bool under
    ) {
        if (decoration.IsNone) {
            return;
        }

        foreach (var bar in line.Runs[0].Bars(decoration, under)) {
            // ⚠ `decoration-0` is a real class and a zero thickness is a real request — for no line.
            // Emitting the empty rectangle anyway would draw nothing and cost a command, and would
            // make `decoration-0` and `no-underline` produce draw lists that differ without the
            // pictures differing, which is the one thing the frame diff must not be told.
            if (bar.Thickness <= 0f || line.Width <= 0f) {
                continue;
            }

            into.Add(
                new DrawCommand(
                    DrawCommandKind.Rectangle,
                    x,
                    y + bar.Top,
                    line.Width,
                    bar.Thickness,
                    colour,
                    0f,
                    0f
                )
            );
        }
    }

    /// <summary>Emits an element's <c>box-shadow</c>, if it has one this can read.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>box-shadow: &lt;x&gt; &lt;y&gt; &lt;blur&gt; [spread] &lt;colour&gt;</c>. The offset
    ///         and the spread are folded into the command's rectangle and the spread into its radius,
    ///         so what reaches the geometry is an ordinary rounded box that happens to be blurred —
    ///         which is why a shadow needs no fields on <c>DrawCommand</c> that a box does not have.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One shadow, and an outer one.</b> CSS takes a comma-separated list and an
    ///         <c>inset</c> keyword; a list would be a command each, which is easy, and <c>inset</c>
    ///         is a different distance field, which is not. Both are refused rather than
    ///         half-applied — the first shadow of a list being drawn and the rest silently dropped
    ///         is worse than nothing being drawn, because it looks like it worked.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it is not clipped to outside the border box.</b> CSS punches the box out of
    ///         its own shadow, so a translucent background does not darken over its own; here the
    ///         blurred box is drawn whole and the background sits on top of it. Visible only under a
    ///         background that is not opaque, and it needs a stencil or a second field to fix.
    ///     </para>
    /// </remarks>
    void EmitShadow(
        UiDocument document,
        UiElement element,
        DrawList into,
        float x,
        float y,
        float width,
        float height,
        CornerRadii corners,
        float radius,
        float alpha
    ) {
        if (!element.Style.TryGet(boxShadow, out var id)) {
            return;
        }

        var value = parser.Parse(id);

        // A shadow is at least two lengths and a colour, so anything that is not a list of values is
        // `none`, `inset` on its own, or a mistake — all of which draw nothing.
        if (value.Kind != StyleValueKind.List) {
            return;
        }

        var context = document.Viewport.WithFontSize(element.FontSize);
        Span<float> lengths = [0f, 0f, 0f, 0f];
        var count = 0;
        Color4? shade = null;

        foreach (var item in value.Items) {
            switch (item.Kind) {
                case StyleValueKind.Color:
                    shade = item.Color;
                    continue;

                // ⚠ <b><c>currentcolor</c> is the one keyword that is a colour</b>, and it is here
                // because a ring needs it. CSS Color 4 § 6.2 defines it as the computed <c>color</c>,
                // which is exactly what `ForegroundOf` answers — the same resolution an icon's
                // `IconPaintKind.Foreground` already gets. It matters rather than being a nicety:
                // `UtilityComposition.RingColor`'s initial value is `currentcolor`, so `ring-2`
                // written on its own — much the commonest way the class appears, on a focused control
                // — resolves through this branch. Without it the fallback would have had to be some
                // concrete colour nobody chose, or `transparent`, which would make `ring-2` cascade
                // perfectly and paint nothing.
                case StyleValueKind.Keyword when item.Keyword == currentColor:
                    shade = document.ForegroundOf(element);
                    continue;

                // ⚠ Every other keyword refuses the whole declaration, and `inset` is the one that
                // matters: an inset shadow drawn as an outer one is not a near miss, it is a shadow
                // on the wrong side of the box.
                case StyleValueKind.Keyword:
                    return;

                // A bare `0` is a length and only that one, which is the same rule `LengthContext`
                // applies — `box-shadow: 0 2px 4px #000` is how everybody writes it.
                case StyleValueKind.Number when item.Number == 0f && count < lengths.Length:
                    lengths[count++] = 0f;
                    continue;

                case StyleValueKind.Length when count < lengths.Length:
                    lengths[count++] = item.Number * context.PixelsPer(item.Unit);
                    continue;

                default:
                    return;
            }
        }

        if (count < 2 || shade is not { } colour) {
            return;
        }

        // ⚠ Half the CSS blur radius. CSS's blur is the *total* distance the edge fades over, and the
        // shader's is the half-extent either side of the boundary — passing the whole radius makes
        // every shadow twice as soft as it was asked to be, which reads as a blurry renderer rather
        // than as a unit mistake.
        var falloff = lengths[2] / 2f;

        // The spread grows the box in every direction, and the corner radius with it: a spread that
        // kept the original corner would give a shadow visibly squarer than the thing casting it.
        var spread = lengths[3];
        var wide = width + (spread * 2f);
        var tall = height + (spread * 2f);

        if (wide <= 0f || tall <= 0f) {
            return;
        }

        into.Add(
            Styled(
                new DrawCommand(
                    DrawCommandKind.Shadow,
                    x + lengths[0] - spread,
                    y + lengths[1] - spread,
                    wide,
                    tall,
                    Fade(colour, alpha),
                    MathF.Max(radius + spread, 0f),
                    falloff
                ),
                into,
                Grow(corners, spread)
            )
        );
    }

    /// <summary>Every corner grown by a shadow's spread, never below square.</summary>
    /// <remarks>
    ///     The same argument the uniform path makes about <c>radius + spread</c>, applied per corner:
    ///     a spread that kept the original radii would give a shadow visibly squarer than the thing
    ///     casting it. Both axes of each ellipse grow by the same amount, because the spread is a
    ///     distance outwards rather than a scale.
    /// </remarks>
    static CornerRadii Grow(CornerRadii corners, float spread) {
        if (spread == 0f) {
            return corners;
        }

        return new CornerRadii(
            Grow(corners.TopLeft, spread),
            Grow(corners.TopRight, spread),
            Grow(corners.BottomRight, spread),
            Grow(corners.BottomLeft, spread)
        );

        static Vector2 Grow(Vector2 corner, float spread) =>
            new(MathF.Max(corner.X + spread, 0f), MathF.Max(corner.Y + spread, 0f));
    }

    /// <summary>How far <c>text-align</c> moves a run along the slack it has.</summary>
    /// <param name="element">The element.</param>
    /// <param name="slack">The content box's width less the run's, which may be negative.</param>
    /// <returns>What to add to the run's left.</returns>
    /// <remarks>
    ///     <para>
    ///         <c>start</c> and <c>end</c> are resolved against <c>direction</c>, the same property
    ///         the layout resolves its logical edges with — so a label written <c>text-end</c> lands
    ///         on the same side as the padding <c>pe-2</c> gave it.
    ///     </para>
    ///     <para>
    ///         <c>justify</c> falls through to the start, which is not a shortcut: CSS aligns the
    ///         <i>last</i> line of a justified block to the start, and a single-line run is its own
    ///         last line. Stretching one would be wrong rather than unimplemented.
    ///     </para>
    ///     <para>
    ///         ⚠ Negative slack is left alone. Text wider than its box overflows to the right of the
    ///         start edge whatever the alignment says, because centring it would hide the beginning
    ///         of the string — and the beginning is the part a reader needs to recognise what has
    ///         been cut off.
    ///     </para>
    /// </remarks>
    float Indent(UiElement element, float slack) {
        if (slack <= 0f || !element.Style.TryGet(textAlign, out var alignment)) {
            return 0f;
        }

        // The physical keywords first, because they mean a side whatever the direction is — that is
        // the whole difference between them and the logical ones.
        if (alignment == alignedCenter) {
            return slack * 0.5f;
        }

        if (alignment == alignedRight) {
            return slack;
        }

        if (alignment == alignedLeft) {
            return 0f;
        }

        // `start`, `end`, and anything unrecognised — which lands on the start edge, the same place
        // an element with no `text-align` at all sits.
        var mirrored = element.Style.TryGet(direction, out var flow) && flow == rtl;
        return mirrored != (alignment == alignedEnd) ? slack : 0f;
    }

    /// <summary>Everything an element's <c>filter</c> asks for, in the three shapes a group can carry.</summary>
    /// <param name="Blur">The Gaussian's standard deviation in document pixels, or zero.</param>
    /// <param name="Colour">The colour transform, or null where every function was a blur.</param>
    /// <param name="Shadow">The alpha-silhouette shadow, or null where nobody wrote a visible one.</param>
    /// <remarks>
    ///     ⚠ <b>Three fields because the executors need three, not because CSS has three.</b> A blur
    ///     costs a scratch surface, two render passes and a bounds outset; a colour matrix rides the
    ///     composite draw the group was making anyway; a drop shadow costs a surface of its own, two
    ///     more passes and a second quad. <c>default</c> is "no filter" on all three counts, which is
    ///     what every element that says nothing gets.
    /// </remarks>
    readonly record struct ElementFilter(float Blur, UiColorMatrix? Colour, UiDropShadow? Shadow = null) {
        /// <summary>
        ///     What <c>opacity()</c> asks the result to be faded by, or null where nobody wrote one.
        ///     Only ever set while reading a <c>backdrop-filter</c>.
        /// </summary>
        /// <remarks>
        ///     ⚠ <b>Nullable rather than defaulting to one, because a record struct's default is
        ///     zero and zero is the value that erases the picture.</b> The distinction it buys is the
        ///     same one <see cref="Colour" /> needs: "nobody wrote an <c>opacity()</c>" and "somebody
        ///     wrote <c>opacity(0)</c>" are different declarations and the second is legal.
        ///     ⚠ It cannot ride <see cref="Colour" />: <see cref="UiColorMatrix" /> is three rows and
        ///     has no alpha row at all, which is the same limit <see cref="UiDropShadow" /> works
        ///     around by putting its colour's alpha on its quad. This does the same — see
        ///     <see cref="UiBackdrop.Alpha" />.
        /// </remarks>
        public float? Alpha { get; init; }

        /// <summary>Whether this is worth opening a group for.</summary>
        /// <remarks>
        ///     ⚠ <b>An identity matrix does not count, and that is a deliberate departure from CSS
        ///     with a real cost behind it.</b> Filter Effects 1 § 5 makes <i>any</i> <c>filter</c>
        ///     other than <c>none</c> a stacking context, so a browser isolates for
        ///     <c>brightness(1)</c>. A group here costs a viewport-sized render target and a pass —
        ///     see <c>UiRenderer.Compose</c> — and the utility layer assembles all eight functions
        ///     into every <c>filter</c> it emits, so <c>blur-0</c> alone would otherwise buy a
        ///     surface to convolve nothing and multiply by one. The engine has no other observable
        ///     that depends on the isolation, which is what makes the trade safe rather than merely
        ///     cheap: <c>Compositing</c> off already declines every group there is.
        /// </remarks>
        /// <remarks>
        ///     ⚠ <b>A transparent shadow does not count, on the same terms as the identity matrix
        ///     above and for a sharper reason.</b> <c>UtilityComposition.Filter</c> assembles all nine
        ///     functions into every <c>filter</c> it emits, and <c>drop-shadow</c>'s identity is a
        ///     shadow painted in <c>transparent</c> — so a bare <c>blur-2</c> carries one, on every
        ///     blurred element in the engine. Counted, it would buy each of them a second surface and
        ///     two more passes to composite nothing. See <see cref="UiDropShadow.IsInvisible" />.
        /// </remarks>
        public bool Any => Blur > 0f || Colour is not null || Shadow is not null;
    }

    /// <summary>Which of the seven colour functions a keyword names. The order is <c>filterFunctions</c>'.</summary>
    enum FilterFunction {
        Brightness,
        Contrast,
        Grayscale,
        Invert,
        Saturate,
        Sepia,
        HueRotate
    }

    /// <summary>Reads an element's <c>filter</c> declaration.</summary>
    /// <param name="document">The document, for the length context relative units resolve against.</param>
    /// <param name="element">The element.</param>
    /// <returns>What it asks for, or <c>default</c> — which is every element that says nothing.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The whole declaration is refused rather than partly applied, which is the rule
    ///         <see cref="EmitShadow" /> already keeps and the one that matters most here.</b>
    ///         <c>filter</c> is an ordered list of functions and this reads eight of them; a
    ///         <c>filter: drop-shadow(2px 2px 4px black) blur(4px)</c> that quietly dropped the shadow
    ///         would draw a blurred element that is missing something and look like a blur bug. So a
    ///         list containing any function this does not read — <c>drop-shadow</c>, <c>opacity</c>,
    ///         <c>url()</c>, a typo, <c>none</c> — is nothing at all, and the element draws as it
    ///         would have without the property. That is what makes each function landing here an
    ///         additive change with no silent middle state.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Order is honoured among the colour functions and is <i>meaningless</i> between one
    ///         of them and a blur, and both halves of that are load-bearing.</b>
    ///         <c>invert(1) brightness(2)</c> is not <c>brightness(2) invert(1)</c>, so the matrices
    ///         are composed left to right by <see cref="UiColorMatrix.Then" /> in the order the list
    ///         is walked. A Gaussian, on the other hand, is a weighted sum with weights summing to
    ///         one, and a colour matrix on premultiplied colour is linear — so the two commute
    ///         exactly and neither executor has to be told which came first. See
    ///         <see cref="DrawCommand.Filter" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The CSS length is the standard deviation and is not halved.</b> The shadow path a
    ///         few methods up halves its third length, because <c>box-shadow</c>'s blur is the total
    ///         fade distance and the box shader wants the half-extent. Filter Effects 1 § 8.4 says
    ///         <c>blur(r)</c> is a Gaussian of σ = r. The two conventions live in one file and only
    ///         one of them applies here.
    ///     </para>
    ///     <para>
    ///         ⚠ Negative is refused rather than clamped — for a blur, for a <c>brightness</c>, for a
    ///         <c>contrast</c> and for a <c>saturate</c>. Each is invalid CSS, and a clamp to zero
    ///         would spend a surface and two render passes, or a whole composited group, on a
    ///         declaration a browser would have thrown away at parse time. The four that CSS
    ///         <i>does</i> clamp — <c>grayscale</c>, <c>invert</c> and <c>sepia</c> above one — are
    ///         clamped, because there the spec says so.
    ///     </para>
    /// </remarks>
    ElementFilter Filter(UiDocument document, UiElement element) =>
        Functions(document, element, filter, backdrop: false);

    /// <summary>Reads an element's <c>backdrop-filter</c> declaration.</summary>
    /// <param name="document">The document, for the length context relative units resolve against.</param>
    /// <param name="element">The element.</param>
    /// <returns>What it asks for, or null — which is every element that says nothing.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The same grammar as <see cref="Filter" /> with two functions swapped, and the swap
    ///         is what the two properties actually differ by rather than a simplification.</b>
    ///         <c>drop-shadow()</c> is refused here — it is a Gaussian over an <i>alpha silhouette</i>
    ///         composited <i>under</i> the thing it belongs to, which is meaningless for a backdrop
    ///         that is already behind everything, and Tailwind emits no <c>backdrop-drop-shadow-*</c>.
    ///         <c>opacity()</c> is accepted here and nowhere else, because <c>backdrop-opacity-*</c> is
    ///         one of the ten roots. Everything else — the refusal of the whole list on one unreadable
    ///         function, the order among the colour functions, the quadrature of two blurs, the
    ///         clamping rules — is <see cref="Filter" />'s, from the same code.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An all-identity read is null and not an empty backdrop.</b> Every declaration
    ///         <c>UtilityComposition.BackdropFilter</c> emits names all nine functions, so
    ///         <c>backdrop-blur-0</c> alone would otherwise cost a capture surface, a capture pass and
    ///         a composite draw to reproduce the picture that was already there. See
    ///         <see cref="UiBackdrop.IsIdentity" />, which is the same departure from CSS's
    ///         "any filter is a stacking context" that <see cref="ElementFilter.Any" /> makes.
    ///     </para>
    /// </remarks>
    UiBackdrop? Backdrop(UiDocument document, UiElement element) {
        var read = Functions(document, element, backdropFilter, backdrop: true);
        var backdrop = new UiBackdrop(read.Blur, read.Alpha ?? 1f, read.Colour);

        return backdrop.IsIdentity ? null : backdrop;
    }

    /// <summary>Reads one of the two filter properties into the three shapes a group can carry.</summary>
    /// <param name="document">The document, for the length context relative units resolve against.</param>
    /// <param name="element">The element.</param>
    /// <param name="property">Either <see cref="filter" /> or <see cref="backdropFilter" />.</param>
    /// <param name="backdrop">
    ///     Which of the two, which decides only whether <c>drop-shadow()</c> or <c>opacity()</c> is the
    ///     function this list may contain. See <see cref="Backdrop" />.
    /// </param>
    ElementFilter Functions(UiDocument document, UiElement element, int property, bool backdrop) {
        if (!element.Style.TryGet(property, out var id)) {
            return default;
        }

        var value = parser.Parse(id);

        if (value.Kind != StyleValueKind.List) {
            return default;
        }

        // ⚠ One function or several, and the two arrive in shapes that are not nested the same way.
        // `StyleValueParser.Parse` splits on top-level whitespace and returns a *single* part's parse
        // directly rather than wrapping it, so a lone `blur(4px)` is the two-item `[keyword, length]`
        // list itself while `blur(4px) invert(1)` is a two-item list *of* those. Reading the first
        // item's kind is what tells them apart, and getting it wrong reads `blur` as a function name
        // and `4px` as a second function.
        var items = value.Items;

        // ⚠ The keyword alone decides, and it used to be the keyword <i>and</i> a length of two.
        // `drop-shadow(0 4px 6px black)` is a five-item list whose first item is a keyword, so a
        // length test here read it as the many-function case, found four items that were not lists
        // and refused the whole declaration — silently, and only for the one function that is not a
        // pair. A many-function list can never begin with a keyword: every function parses to a list
        // of its own, so `items[0].Kind` is `List` whenever there is more than one.
        if (items[0].Kind == StyleValueKind.Keyword) {
            return Settle(One(document, element, items, default, backdrop) ?? default);
        }

        var accumulated = new ElementFilter();

        foreach (var item in items) {
            // ⚠ <b>At least two and no longer exactly two, and the arity is <see cref="One" />'s to
            // check.</b> Eight of the nine functions are a keyword and one value; <c>drop-shadow</c>
            // is a keyword and three or four. An equality here read the whole declaration as
            // malformed the moment a drop shadow appeared beside anything else — silently, and only
            // for lists of more than one function, so a lone <c>drop-shadow</c> worked and
            // <c>blur(4px) drop-shadow(…)</c> drew neither.
            if (item.Kind != StyleValueKind.List
                || item.Items.Length < 2
                || item.Items[0].Kind != StyleValueKind.Keyword) {
                return default;
            }

            // ⚠ Null and not `default`, because "refused" and "read, and it came to the identity" are
            // different answers and `grayscale(0) blur(4px)` is the list that tells them apart. A
            // sentinel of `default` would drop the blur on the floor for being preceded by a
            // do-nothing colour function.
            if (One(document, element, item.Items, accumulated, backdrop) is not { } folded) {
                return default;
            }

            accumulated = folded;
        }

        return Settle(accumulated);
    }

    /// <summary>Drops a colour transform that came out the identity, so nothing pays for it.</summary>
    /// <remarks>
    ///     ⚠ <b>Once, at the end, and never inside the fold.</b> <c>invert(1) invert(1)</c> is the
    ///     identity and <c>invert(1)</c> alone is not, so a fold that discarded an identity as it went
    ///     would be discarding an intermediate — <c>grayscale(0)</c> followed by <c>sepia(1)</c> is a
    ///     sepia, and its first step is the identity. Only the composed answer is allowed to decide.
    /// </remarks>
    static ElementFilter Settle(ElementFilter read) {
        if (read.Colour is { IsIdentity: true }) {
            read = read with { Colour = null };
        }

        // ⚠ Here rather than in `Shadow`, and for a plainer reason than the matrix's: a
        // `drop-shadow(0 0 0 transparent)` is the identity the moment it is read and could have been
        // dropped there. It is dropped here so that the two identities are discarded in one place —
        // a reader looking for "what does this file consider nothing" finds both, and a third
        // function's identity has an obvious home.
        if (read.Shadow is { IsInvisible: true }) {
            read = read with { Shadow = null };
        }

        return read;
    }

    /// <summary>Folds one <c>filter</c> function into what has been read so far.</summary>
    /// <returns>Null when the function or its argument is one this refuses.</returns>
    /// <remarks>
    ///     ⚠ <b>Two blurs compose by quadrature rather than being refused, and that is the one place
    ///     this method is more generous than the rest of the file.</b> Convolving a Gaussian of σ₁
    ///     with one of σ₂ gives a Gaussian of √(σ₁² + σ₂²) exactly — it is not an approximation — so
    ///     <c>blur(3px) blur(4px)</c> has a single correct answer of <c>5px</c> and refusing it would
    ///     be refusing something this executor can do perfectly. Nothing else in a filter list has
    ///     that property, which is why nothing else gets the treatment.
    /// </remarks>
    ElementFilter? One(
        UiDocument document,
        UiElement element,
        ReadOnlySpan<StyleValue> pair,
        ElementFilter into,
        bool backdrop
    ) {
        var keyword = pair[0].Keyword;

        // ⚠ <b>Before the pair is unpacked, because this is the one function that is not a pair.</b>
        // Everything below reads <c>pair[1]</c> as <i>the</i> argument; <c>drop-shadow</c> has three
        // or four, and reading the first of them as the whole would be a shadow whose blur and colour
        // were silently the offset's.
        // ⚠ And refused outright inside a <c>backdrop-filter</c>, which takes the whole declaration
        // with it — the rule this method keeps for every function it cannot execute. A shadow of the
        // *backdrop* would be a silhouette composited under a picture that is already behind
        // everything, which is nothing at all; drawing it as if it were the element's own shadow is
        // the plausible mistake, and it would put a dark rectangle under every glass panel.
        if (keyword == dropShadowFunction) {
            return backdrop ? null : Shadow(document, element, pair[1..], into);
        }

        if (pair.Length != 2) {
            return null;
        }

        var argument = pair[1];

        // ⚠ <b>Accepted only for a backdrop, and it is the one function whose answer does not go into
        // the colour matrix.</b> <see cref="UiColorMatrix" /> has three rows and cannot scale alpha —
        // see <see cref="ElementFilter.Alpha" />, which is where this lands and why. Filter Effects 1
        // § 8.6 clamps it to [0, 1] rather than refusing what is outside, which is <c>grayscale</c>'s
        // rule and not <c>brightness</c>'s.
        if (keyword == opacityFunction) {
            if (!backdrop) {
                return null;
            }

            var opacity = argument.Kind switch {
                StyleValueKind.Number => argument.Number,
                StyleValueKind.Length when argument.Unit == StyleUnit.Percent => argument.Number / 100f,
                _ => float.NaN
            };

            if (!float.IsFinite(opacity)) {
                return null;
            }

            // ⚠ Multiplied into whatever is already there rather than replacing it, because CSS's
            // list applies each function to the result of the last and two `opacity()`s therefore
            // compose. `?? 1f` is what makes the first one land unchanged.
            return into with { Alpha = (into.Alpha ?? 1f) * Math.Clamp(opacity, 0f, 1f) };
        }

        if (keyword == blurFunction) {
            var pixels = argument.Kind switch {
                StyleValueKind.Length => argument.Number
                    * document.Viewport.WithFontSize(element.FontSize).PixelsPer(argument.Unit),
                StyleValueKind.Number when argument.Number == 0f => 0f,
                _ => float.NaN
            };

            if (float.IsNaN(pixels) || pixels < 0f || !float.IsFinite(pixels)) {
                return null;
            }

            return into with { Blur = MathF.Sqrt((into.Blur * into.Blur) + (pixels * pixels)) };
        }

        var which = Array.IndexOf(filterFunctions, keyword);

        if (which < 0) {
            return null;
        }

        var function = (FilterFunction) which;

        // A percentage and a number are the same value written two ways for all seven — `50%` and
        // `0.5` are one filter — and `hue-rotate` takes neither, so its unit is checked on its own
        // terms. `StyleValueParser` has already refused every other shape.
        var amount = argument.Kind switch {
            StyleValueKind.Number => argument.Number,
            StyleValueKind.Length when argument.Unit == StyleUnit.Percent => argument.Number / 100f,
            StyleValueKind.Length when argument.Unit == StyleUnit.Degrees => argument.Number,
            _ => float.NaN
        };

        if (!float.IsFinite(amount)) {
            return null;
        }

        // The angle is the only one of the seven that is not a proportion, so it is the only one a
        // unit tells apart. A `hue-rotate(50%)` reaching here would be a parser that let it.
        if ((function == FilterFunction.HueRotate) != (argument.Unit == StyleUnit.Degrees)
            && !(function == FilterFunction.HueRotate && amount == 0f)) {
            return null;
        }

        // ⚠ Refused below zero rather than clamped for the three where CSS calls it invalid, and
        // clamped above one for the three where CSS calls it clamped. Both are the spec's rule and
        // neither is this file's taste — `saturate(2)` is a real, useful over-saturation and
        // `grayscale(2)` is `grayscale(1)`.
        if (function != FilterFunction.HueRotate && amount < 0f) {
            return null;
        }

        var matrix = function switch {
            FilterFunction.Brightness => UiColorMatrix.Brightness(amount),
            FilterFunction.Contrast => UiColorMatrix.Contrast(amount),
            FilterFunction.Grayscale => UiColorMatrix.Grayscale(MathF.Min(amount, 1f)),
            FilterFunction.Invert => UiColorMatrix.Invert(MathF.Min(amount, 1f)),
            FilterFunction.Saturate => UiColorMatrix.Saturate(amount),
            FilterFunction.Sepia => UiColorMatrix.Sepia(MathF.Min(amount, 1f)),
            _ => UiColorMatrix.HueRotate(amount)
        };

        // ⚠ Composed onto what is already there and never the other way round, because
        // <see cref="UiColorMatrix.Then" /> reads in CSS's order and this walk is in CSS's order.
        var composed = (into.Colour ?? UiColorMatrix.Identity).Then(matrix);

        // ⚠ An identity is kept here and dropped by `Settle`, which is the only place that can tell
        // a composed identity from an intermediate one. See its remark.
        return into with { Colour = composed };
    }

    /// <summary>Reads one <c>drop-shadow()</c>'s arguments into the filter being folded.</summary>
    /// <param name="document">The document, for <c>currentcolor</c> and the length context.</param>
    /// <param name="element">The element, for the same two.</param>
    /// <param name="arguments">The function's items, keyword already stripped. Three or four of them.</param>
    /// <param name="into">What has been read so far.</param>
    /// <returns>Null when the arguments are a shape this refuses, which refuses the whole list.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The walk is <see cref="EmitShadow" />'s, deliberately and almost line for line,
    ///         because the two functions have the same grammar problem and only one of them had
    ///         solved it.</b> A length, a bare zero and a colour are told apart by kind rather than by
    ///         position — Filter Effects 1 § 8.4 puts the colour at either end — and
    ///         <c>currentcolor</c> is resolved here because it is the computed <c>color</c> and the
    ///         parser cannot see an element. What is <i>not</i> shared is the halving: <c>box-shadow</c>
    ///         passes half its blur to the box shader because that shader wants a half-extent, and
    ///         <c>drop-shadow(r)</c> is a Gaussian of σ = r. Both conventions live in this file and
    ///         only one of them applies here.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One shadow per element and a second one refuses the list, which is a real limit
    ///         and the same one <c>box-shadow</c> keeps.</b> CSS lets <c>filter</c> hold any number of
    ///         <c>drop-shadow()</c>s and each is a surface and two passes on the device; drawing the
    ///         first and dropping the rest is the silent-middle-state this whole method exists to
    ///         avoid. Refusing is what makes the second one a visible change rather than an invisible
    ///         one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The default colour is <c>currentcolor</c> and not black</b>, which is Filter
    ///         Effects 1 § 8.4's rule and the reason <c>drop-shadow(0 2px 4px)</c> on dark text is
    ///         a dark shadow and on light text a light one. Defaulting to black would be right on
    ///         every light theme and wrong on every dark one.
    ///     </para>
    /// </remarks>
    ElementFilter? Shadow(
        UiDocument document,
        UiElement element,
        ReadOnlySpan<StyleValue> arguments,
        ElementFilter into
    ) {
        if (into.Shadow is not null) {
            return null;
        }

        var context = document.Viewport.WithFontSize(element.FontSize);
        Span<float> lengths = [0f, 0f, 0f];
        var count = 0;
        Color4? shade = null;

        foreach (var item in arguments) {
            switch (item.Kind) {
                case StyleValueKind.Color when shade is null:
                    shade = item.Color;
                    continue;

                case StyleValueKind.Keyword when item.Keyword == currentColor && shade is null:
                    shade = document.ForegroundOf(element);
                    continue;

                // ⚠ <b><see cref="LengthContext.ToLength" /> and not
                // <see cref="LengthContext.PixelsPer" />, which is the trap <see cref="EmitShadow" />
                // is still in.</b> That method answers <i>zero</i> for a unit that measures no
                // distance, so a `drop-shadow(90deg 2px black)` read through it is a shadow with a
                // zero x-offset — invalid CSS silently clamped, which is the one behaviour the rest
                // of this file refuses. `ToLength` distinguishes "a length that came to nothing"
                // from "not a length", which is the whole reason it exists. A bare zero is a length
                // and only that one, and a percentage is a real unit here that this function has no
                // meaning for, so both fall out of the same test.
                case StyleValueKind.Number or StyleValueKind.Length
                    when count < lengths.Length && context.ToLength(item) is { Unit: LayoutUnit.Point } length:
                    lengths[count++] = length.Value;
                    continue;

                default:
                    return null;
            }
        }

        // Two lengths or three, and never fewer — the offset is not optional in the grammar and a
        // shadow with no offset and no blur is the element drawn twice.
        if (count < 2) {
            return null;
        }

        // ⚠ Refused rather than clamped, for the reason the blur above is: a negative standard
        // deviation is invalid CSS, and a clamp to zero would draw a hard-edged copy of the element
        // under itself where a browser would have thrown the declaration away.
        if (lengths[2] < 0f || !float.IsFinite(lengths[0]) || !float.IsFinite(lengths[1])) {
            return null;
        }

        return into with {
            Shadow = new UiDropShadow(
                new Vector2(lengths[0], lengths[1]),
                lengths[2],
                shade ?? document.ForegroundOf(element)
            )
        };
    }

    /// <summary>An element's own <c>opacity</c>, before anything above it is multiplied in.</summary>
    /// <remarks>
    ///     One when nothing said, and clamped — CSS clamps to 0–1 rather than treating <c>1.5</c> as
    ///     an error, and a value outside the range that silently drew nothing would be a stylesheet
    ///     bug nobody could find. Not inherited, which is why it has to be threaded through
    ///     <see cref="Emit" /> rather than read off the computed style of each element alone.
    /// </remarks>
    float Opacity(UiElement element) {
        if (!element.Style.TryGet(opacity, out var id)) {
            return 1f;
        }

        var value = parser.Parse(id);
        return value.Kind == StyleValueKind.Number ? Math.Clamp(value.Number, 0f, 1f) : 1f;
    }

    /// <summary>A colour with the accumulated opacity multiplied into its alpha.</summary>
    /// <remarks>
    ///     ⚠ Not <c>colour * alpha</c>, which the operator would read as scaling all four
    ///     components — right in premultiplied space and wrong here, where it would darken the
    ///     colour towards black as well as fading it. Internal because <see cref="DrawContext" />
    ///     fades what a custom-drawn control hands it, and the two must agree.
    /// </remarks>
    internal static Color4 Fade(Color4 colour, float alpha) =>
        alpha >= 1f ? colour : new Color4(colour.R, colour.G, colour.B, colour.A * alpha);

    /// <summary>An element's <c>text-decoration</c>, resolved as far as anything but a run can.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><see cref="TextDecoration.IsNone" /> is the answer for almost every element in
    ///         every frame, and this returns it after one <see cref="ComputedStyle.TryGet" />.</b>
    ///         Text is the most-emitted thing in an interface and undecorated text is nearly all of
    ///         it; four more lookups per label to discover that a colour and a thickness were never
    ///         set is a cost paid on every frame for nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Lengths resolve against the element's own font size, and a percentage does too.</b>
    ///         CSS Text Decoration 4 defines a percentage on both of these against one <c>em</c>,
    ///         which is the one case in this builder where a percentage <i>is</i> knowable — unlike
    ///         <see cref="Radius" />'s, which needs a box the layout has not finished. So it is
    ///         resolved rather than refused, and the refusal comment on the corner radius is not a
    ///         precedent for dropping it here.
    ///     </para>
    /// </remarks>
    TextDecoration Decoration(UiElement element) {
        var lines = Lines(element);

        if (lines == TextDecorationLine.None) {
            return default;
        }

        return new TextDecoration(
            lines,
            element.Style.TryGet(decorationStyle, out var style) && parser.Parse(style) is
                { Kind: StyleValueKind.Keyword } keyword && keyword.Keyword == keywordDouble
                    ? TextDecorationStyle.Double
                    : TextDecorationStyle.Solid,
            Color(element, decorationColor),
            TextLength(element, decorationThickness, float.NaN),
            TextLength(element, underlineOffset, 0f)
        );
    }

    /// <summary>Which lines <c>text-decoration-line</c> asks for.</summary>
    /// <remarks>
    ///     A space-separated list, because <c>underline overline</c> is one declaration and two
    ///     lines. Anything else — <c>none</c>, <c>blink</c>, a typo — contributes nothing, so the
    ///     declaration that names only unreadable values is the same as no declaration.
    /// </remarks>
    TextDecorationLine Lines(UiElement element) {
        if (!element.Style.TryGet(decorationLine, out var id)) {
            return TextDecorationLine.None;
        }

        var value = parser.Parse(id);

        if (value.Kind != StyleValueKind.List) {
            return Line(value);
        }

        var lines = TextDecorationLine.None;
        foreach (var item in value.Items) {
            lines |= Line(item);
        }

        return lines;

        TextDecorationLine Line(StyleValue value) {
            if (value.Kind != StyleValueKind.Keyword) {
                return TextDecorationLine.None;
            }

            if (value.Keyword == keywordUnderline) {
                return TextDecorationLine.Underline;
            }

            if (value.Keyword == keywordOverline) {
                return TextDecorationLine.Overline;
            }

            return value.Keyword == keywordLineThrough ? TextDecorationLine.LineThrough : TextDecorationLine.None;
        }
    }

    /// <summary>One decoration length in pixels, or the caller's <c>auto</c>.</summary>
    /// <param name="element">The element, whose font size an <c>em</c> resolves against.</param>
    /// <param name="property">Which length.</param>
    /// <param name="auto">What <c>auto</c>, <c>from-font</c> and anything unreadable mean.</param>
    /// <remarks>
    ///     ⚠ <b>Every keyword lands on <paramref name="auto" />, including one CSS has never heard
    ///     of, and that is the specified behaviour rather than a shrug.</b> The only two either
    ///     property accepts are <c>auto</c> and <c>from-font</c>, which mean the same thing here —
    ///     see the interning of <c>from-font</c> above — and CSS drops an invalid declaration, which
    ///     leaves the initial value, which is <c>auto</c>. So the three cases genuinely have one
    ///     answer, and writing them as three branches returning the same thing would look like a
    ///     distinction that is not there.
    /// </remarks>
    float TextLength(UiElement element, int property, float auto) {
        if (!element.Style.TryGet(property, out var id)) {
            return auto;
        }

        var value = parser.Parse(id);

        if (value.Kind != StyleValueKind.Length) {
            return auto;
        }

        return value.Unit switch {
            StyleUnit.Pixels or StyleUnit.None => value.Number,
            StyleUnit.Em => value.Number * element.FontSize,

            // CSS Text Decoration 4 resolves a percentage on both of these against one em.
            StyleUnit.Percent => value.Number * element.FontSize / 100f,
            StyleUnit.Rem => value.Number * element.Document.Root.FontSize,
            _ => auto
        };
    }

    Color4? Color(UiElement element, int property) {
        if (!element.Style.TryGet(property, out var id)) {
            return null;
        }

        var value = parser.Parse(id);
        return value.Kind == StyleValueKind.Color ? value.Color : null;
    }

    /// <summary>An element's four corner radii, each elliptical.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A corner arrives as <i>two</i> lengths — <c>8px 8px</c> — even when the stylesheet
    ///         wrote one</b>, because that is what the shorthand expands to. Both are read now: the pair
    ///         is the horizontal and vertical radius of an ellipse, which is CSS's
    ///         <c>border-radius: 40px / 20px</c> and what a pill-shaped button whose height is not its
    ///         width actually needs. Taking the first and dropping the second drew every such corner as a
    ///         circle.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The logical longhand wins over the physical one, which is CSS Cascade's rule read
    ///         the only way this cascade can read it.</b> CSS settles a conflict between
    ///         <c>border-start-start-radius</c> and <c>border-top-left-radius</c> by declaration
    ///         order, because they are two properties writing one used value. This cascade stores a
    ///         property-to-value map with no order in it, so declaration order is not recoverable
    ///         here — and the same problem was already settled for the logical insets, where
    ///         <c>StyleResolution.LeftEdge</c> gives the logical edge precedence outright. Following
    ///         it costs the rarer conflict and keeps one rule in the engine rather than two.
    ///     </para>
    /// </remarks>
    CornerRadii Corners(UiElement element) {
        // ⚠ Stack-allocated, because this runs for every element in the frame and the overwhelming
        // majority of them have no radius at all. A `Vector2[4]` here was four hundred allocations a
        // frame in the editor to describe corners that are almost always zero.
        Span<Vector2> physical = [
            Radius(element, borderRadii[0]),
            Radius(element, borderRadii[1]),
            Radius(element, borderRadii[2]),
            Radius(element, borderRadii[3])
        ];

        // ⚠ Read before the loop rather than inside it, and read even when no logical radius is set,
        // because `TryGet` on a miss is the cheap half — four misses are four dictionary probes and
        // the overwhelming majority of elements take exactly that path.
        var mirrored = element.Style.TryGet(direction, out var flow) && flow == rtl;

        for (var corner = 0; corner < 4; corner++) {
            if (!element.Style.TryGet(logicalRadii[corner], out _)) {
                continue;
            }

            // ⚠ <b>The mirror is a swap of the two corners on a row, not a reversal of the array.</b>
            // Reversing would send `start-start` to the *bottom*-right, which is the block axis — and
            // the block axis does not flip, because there is no writing mode to flip it. Only the
            // inline half of each name moves: 0↔1 across the top and 3↔2 across the bottom.
            var target = mirrored ? corner switch { 0 => 1, 1 => 0, 2 => 3, _ => 2 } : corner;
            physical[target] = Radius(element, logicalRadii[corner]);
        }

        return new CornerRadii(physical[0], physical[1], physical[2], physical[3]);
    }

    /// <summary>One corner's horizontal and vertical radius.</summary>
    /// <remarks>
    ///     A single length means a circle, which is the one-value form written out. Absolute lengths
    ///     only: a percentage radius resolves against the box's own size, which is a rule this
    ///     builder would have to know rather than read.
    /// </remarks>
    Vector2 Radius(UiElement element, int property) {
        if (!element.Style.TryGet(property, out var id)) {
            return Vector2.Zero;
        }

        var value = parser.Parse(id);

        if (value.Kind != StyleValueKind.List) {
            var single = Pixels(value);
            return new Vector2(single, single);
        }

        if (value.Items.Length == 0) {
            return Vector2.Zero;
        }

        var horizontal = Pixels(value.Items[0]);
        var vertical = value.Items.Length > 1 ? Pixels(value.Items[1]) : horizontal;

        return new Vector2(horizontal, vertical);

        static float Pixels(StyleValue value) =>
            value.Kind == StyleValueKind.Length && value.Unit == StyleUnit.Pixels ? value.Number : 0f;
    }

    /// <summary>Attaches a box style to a command, unless the cheap uniform path covers it.</summary>
    /// <remarks>
    ///     ⚠ <b>The whole reason the side buffer is worth having is that this usually returns the
    ///     command unchanged.</b> <see cref="DrawList.Boxes" /> is compared entry by entry every frame
    ///     alongside the commands, so an entry written for a box whose four corners are the same
    ///     circle would be pure cost — the scalar <see cref="DrawCommand.Radius" /> already says
    ///     everything about it, and <see cref="Rendering.UiGeometryBuilder" /> expands that scalar back
    ///     into four equal corners on the way to the shader. Only the boxes that are genuinely more
    ///     than a colour, a size and one radius go in.
    /// </remarks>
    static DrawCommand Styled(DrawCommand command, DrawList into, CornerRadii corners) =>
        corners.IsUniformCircular(out _)
            ? command
            : command with { Offset = into.AddBox(BoxStyle.Rounded(corners)), Length = 1 };

    /// <summary>The mask list this element's <c>mask-image</c> asks for, into a caller's span.</summary>
    /// <param name="element">The element.</param>
    /// <param name="width">Its border-box width, in document pixels.</param>
    /// <param name="height">Its border-box height.</param>
    /// <param name="into">Where to write the entries. At least <c>GradientReader.MostLayers</c> long.</param>
    /// <returns>How many entries were written. Zero when there is nothing to mask by.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A mask this cannot resolve masks <i>nothing</i>, which is the opposite of the
    ///         default and is the whole of why the refusal path is written out rather than left to a
    ///         null-conditional.</b> A background gradient that cannot be read is simply not painted
    ///         and the element keeps its own colour: the picture is missing something. A mask that
    ///         cannot be resolved, left out the same way, would leave the element <i>unmasked</i> —
    ///         also the picture missing something. But a mask that failed <i>closed</i> would erase
    ///         the element, which is indistinguishable from a layout collapse. Masking 1 § 4.1 says
    ///         the same thing for the same reason: a mask resource that cannot be fetched is ignored.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One unreadable layer refuses the <i>whole</i> list, and that is the fail-open
    ///         answer rather than a shortcut.</b> Dropping just the bad layer changes the arithmetic
    ///         of every operator around it — a missing <c>subtract</c> leaves the thing it was meant
    ///         to punch out — so a partly-resolved list is a mask that is confidently wrong. The
    ///         whole declaration is dropped instead, and the element is drawn plainly.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A list that provably changes nothing returns zero rather than an identity list,
    ///         and it has to, because a mask is what <i>opens the group</i>.</b> Returning entries
    ///         that cover everything would spend a viewport-sized surface and a composite pass on a
    ///         mask that changes no pixel. <c>UiColorMatrix</c>'s identity is dropped by
    ///         <c>UiRenderer</c> instead, one level down, because a `grayscale(0)` still isolates a
    ///         stacking context in CSS and this does not. See <see cref="Reduce" /> for which lists
    ///         are provably nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The zero-axis guard is on the shape and not on the axis, and the difference is
    ///         deliberate.</b> A radial mask has no axis at all — <see cref="BackgroundGradient.Axis" />
    ///         returns zero for one legitimately — so a guard written against the axis would delete
    ///         every round mask while looking like it guarded a division.
    ///     </para>
    /// </remarks>
    int MasksFor(UiElement element, float width, float height, Span<UiMask> into) {
        if (!element.Style.TryGet(maskImage, out var id) || width <= 0f || height <= 0f) {
            return 0;
        }

        var layers = gradients.ReadLayers(id);

        if (layers.Count == 0) {
            return 0;
        }

        var operators = Composites(element);
        var half = new Vector2(width * 0.5f, height * 0.5f);

        // ⚠ <b>The border box, in document pixels, and it is the element's own — not the group's.</b>
        // `UiLayer.Bounds` is the ink and has already been outset by a blur; resolving the ramp
        // against that would slide it the moment somebody added `blur-sm` beside the mask. See
        // `UiMask`, which carries the box for this reason. Every layer shares it, because
        // `mask-origin`, `mask-position` and `mask-size` — the three properties that would let them
        // differ — are not read: see `maskImage`.
        var centre = new Vector2(element.AbsoluteLeft + half.X, element.AbsoluteTop + half.Y);

        for (var layer = 0; layer < layers.Count; layer++) {
            var gradient = layers[layer];

            if (!gradient.IsPaintable) {
                return 0;
            }

            var axis = gradient.Axis(width, height);

            if (gradient.Shape == GradientShape.Linear && axis == Vector2.Zero) {
                return 0;
            }

            // ⚠ <b>Alphas alone, and the three colours are dropped on the floor here rather than
            // downstream.</b> `mask-mode` resolves to `alpha` for every image that is not an SVG
            // `<mask>`, so `linear-gradient(to right, black, transparent)` and
            // `linear-gradient(to right, #ff0000, #00ff0000)` are the same mask. Carrying the colours
            // to find that out later would be carrying a field whose only correct use is being
            // ignored.
            into[layer] = new UiMask(
                centre,
                half,
                axis,
                new Vector3(gradient.Start.A, gradient.Via.A, gradient.End.A),
                gradient.Stops,
                gradient.Shape,
                gradient.HasVia
            ) {
                // ⚠ Repeated to the layer count when there are fewer operators than layers, which is
                // what CSS does with every comma-separated `mask-*` list and is the case that
                // matters: Tailwind writes one `intersect` for a list of six.
                Composite = operators.Length == 0
                    ? MaskComposite.Add
                    : operators[layer % operators.Length]
            };
        }

        return Reduce(into[..layers.Count]);
    }

    /// <summary>Drops the entries of a mask list that provably cannot change its coverage.</summary>
    /// <param name="masks">The list, topmost first. Rewritten in place.</param>
    /// <returns>How many entries are left.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An optimisation with a correctness consequence, which is why it is here and not
    ///         in an executor.</b> A mask is what opens a composited group, so a list that reduces to
    ///         nothing has to reduce to nothing <i>before</i> the group is decided on — otherwise a
    ///         <c>mask-t-from-*</c> that happens to be fully opaque would still cost a
    ///         viewport-sized surface and two passes to composite a picture identical to the one that
    ///         needed neither.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it is what lets the utility layer emit Tailwind's shape at all.</b> Every
    ///         <c>mask-*</c> class writes the same six-layer <c>mask-image</c>, with the layers
    ///         nobody set falling back to a fully opaque gradient — that is how an unset layer
    ///         changes nothing under <c>intersect</c>, and it is Tailwind v4's own arrangement. A
    ///         <c>mask-radial-from-50%</c> therefore arrives here as six layers of which five are
    ///         opaque, and leaves as one.
    ///     </para>
    ///     <para>
    ///         The two rules are the only two that are true of <i>every</i> input, and both are
    ///         about <c>intersect</c> because it is the only operator with an identity inside
    ///         <c>[0, 1]</c>. Writing a third for <c>add</c> — whose identity is a transparent layer
    ///         — would be reasonable and is not done, because nothing generates one.
    ///     </para>
    /// </remarks>
    static int Reduce(Span<UiMask> masks) {
        var kept = 0;

        // An opaque entry composited with `intersect` is `1 · b`, which is `b`: the entry is the
        // identity wherever it sits, so long as it is not the bottom one — the bottom entry has no
        // backdrop to be the identity over.
        for (var index = 0; index < masks.Length; index++) {
            if (index < masks.Length - 1
                && masks[index].IsOpaque
                && masks[index].Composite == MaskComposite.Intersect) {
                continue;
            }

            masks[kept++] = masks[index];
        }

        // And the mirror of it: when the bottom entry is opaque, the entry above it sees a backdrop
        // of one, so `s · 1` is `s` and the bottom entry is what can go instead. Once only — the
        // entry that becomes the new bottom survived the loop above, so it is not itself an opaque
        // `intersect`.
        if (kept > 1 && masks[kept - 1].IsOpaque && masks[kept - 2].Composite == MaskComposite.Intersect) {
            kept--;
        }

        // ⚠ A single fully opaque ramp is nothing at all, and detecting it is worth the line because
        // `mask-linear-from-100%` is a real thing to write while tuning one — it should cost nothing
        // while it says nothing.
        return kept == 1 && masks[0].IsOpaque ? 0 : kept;
    }

    /// <summary>The <c>mask-composite</c> operators an element declares, in order.</summary>
    /// <returns>The operators, or empty when the property is absent or unreadable.</returns>
    /// <remarks>
    ///     ⚠ <b>Empty and not <c>[add]</c>, so that "nobody wrote one" and "somebody wrote
    ///     <c>add</c>" are the same picture without being the same value.</b> An unreadable keyword
    ///     drops the whole property rather than the one operator it appeared in, for
    ///     <see cref="MasksFor" />'s reason: a list with one operator silently replaced is a mask
    ///     that is confidently wrong, and CSS discards an invalid declaration whole.
    /// </remarks>
    MaskComposite[] Composites(UiElement element) {
        if (!element.Style.TryGet(maskComposite, out var id)) {
            return [];
        }

        if (maskComposites.TryGetValue(id, out var cached)) {
            return cached;
        }

        var text = values.NameOf(id).AsSpan();
        Span<Range> parts = stackalloc Range[GradientReader.MostLayers];
        var count = 0;
        var start = 0;

        for (var index = 0; index <= text.Length && count < parts.Length; index++) {
            if (index != text.Length && text[index] != ',') {
                continue;
            }

            parts[count++] = new Range(start, index);
            start = index + 1;
        }

        var operators = new MaskComposite[count];

        for (var index = 0; index < count; index++) {
            var name = text[parts[index]].Trim();

            operators[index] = name switch {
                _ when name.Equals("add", StringComparison.OrdinalIgnoreCase) => MaskComposite.Add,
                _ when name.Equals("subtract", StringComparison.OrdinalIgnoreCase) => MaskComposite.Subtract,
                _ when name.Equals("intersect", StringComparison.OrdinalIgnoreCase) => MaskComposite.Intersect,
                _ when name.Equals("exclude", StringComparison.OrdinalIgnoreCase) => MaskComposite.Exclude,
                _ => (MaskComposite) (-1)
            };

            if (operators[index] < 0) {
                operators = [];
                break;
            }
        }

        maskComposites[id] = operators;

        return operators;
    }

    /// <summary>Paints the <c>background-image</c> layer, when it is a gradient this engine draws.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This emits a command where the background colour did not, and that is deliberate.</b>
    ///         <c>bg-linear-to-r from-accent to-surface-3</c> sets no <c>background-color</c> at all, so
    ///         an element whose only background is a gradient has no colour for the caller above to
    ///         find — and a gradient that painted only over an existing fill would be invisible on
    ///         exactly the elements Tailwind's own gradient utilities produce.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A refused gradient paints nothing rather than falling back to one of its stops.</b>
    ///         The near colour is right along one edge and wrong everywhere else, which is a picture
    ///         somebody has to squint at; an absent gradient is a question they ask immediately. See
    ///         <see cref="GradientRefusal" /> for the whole argument.
    ///     </para>
    /// </remarks>
    void EmitGradient(
        UiElement element,
        DrawList into,
        float x,
        float y,
        float width,
        float height,
        CornerRadii corners,
        float radius,
        float alpha
    ) {
        if (!element.Style.TryGet(backgroundImage, out var id)) {
            return;
        }

        var gradient = gradients.Read(id);

        if (!gradient.IsPaintable) {
            return;
        }

        var axis = gradient.Axis(width, height);

        // ⚠ A degenerate box has no direction to run a linear ramp along, and there is nothing to see
        // at this size either way; not emitting says so honestly. Tested on the *shape* rather than on
        // the axis, because a radial gradient's axis is legitimately zero — it has no direction at all
        // — and the old sentinel would have erased every one of them.
        if (gradient.Shape == GradientShape.Linear && axis == Vector2.Zero) {
            return;
        }

        // ⚠ Unconditionally into the side buffer, unlike `Styled`. The cheap path exists because a
        // uniformly rounded box needs nothing but its scalar radius — and a gradient is precisely a
        // box that needs more than that, so the test that skips the record has to be skipped here.
        var offset = into.AddBox(
            new BoxStyle(corners, Fade(gradient.End, alpha), axis) {
                Shape = gradient.Shape,
                Space = gradient.Space,
                GradientVia = Fade(gradient.Via, alpha),
                HasVia = gradient.HasVia,
                Stops = gradient.Stops
            }
        );

        into.Add(
            new DrawCommand(
                DrawCommandKind.Rectangle,
                x,
                y,
                width,
                height,
                Fade(gradient.Start, alpha),
                radius,
                0f
            ) {
                Offset = offset,
                Length = 1
            }
        );
    }
}
