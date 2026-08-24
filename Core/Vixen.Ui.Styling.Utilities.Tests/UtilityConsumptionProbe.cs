// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core.Mathematics;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Styling;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>What one scene produced, in four separately comparable parts.</summary>
/// <param name="Layout">Every element's rectangle, in document order.</param>
/// <param name="Paint">The draw list, its side buffers included.</param>
/// <param name="Cursor">What the pointer would look like over the probe and over its text.</param>
/// <param name="Hit">Which element answers a hit test, over a grid.</param>
/// <remarks>
///     ⚠ <b>Four strings and not one, because a single hash would answer the wrong question.</b> The
///     gate's verdict is "did anything move", which one hash gives; the <i>ledger</i>'s value is
///     saying which consumer moved, and that is what makes a half-implemented family legible at all —
///     a property the layout reads and the draw list ignores is <c>docs/plan/43</c> F1, and it looks
///     exactly like full support to anything that compares one number.
/// </remarks>
readonly record struct SceneSignature(string Layout, string Paint, string Cursor, string Hit);

/// <summary>A named arrangement of elements to try a declaration against.</summary>
/// <param name="Name">What it is called in a failure message.</param>
/// <param name="Css">Its rules, appended to <see cref="UtilityConsumptionProbe.Common" />.</param>
/// <remarks>
///     ⚠ <b>Several scenes, because a property is only observable in a situation that needs it, and
///     the situations conflict.</b> <c>flex-shrink</c> shows only where there is not enough room and
///     <c>flex-grow</c> only where there is spare; <c>right</c> shows only where <c>left</c> is not
///     already deciding; <c>transition-duration</c> shows only where <c>transition-property</c> is
///     something other than <c>none</c>, and <c>transition-property</c> shows only where it is. No one
///     arrangement can be both halves of any of those pairs, so the verdict is the union over all of
///     them — a property is acted on if <i>any</i> scene notices it.
/// </remarks>
/// <remarks>
///     <para>
///         ⚠ <b>This list is the gate's standing weakness, and it has been wrong six times where the
///         gate's logic has been wrong none.</b> A family measures inert when no scene puts it in the
///         situation it needs — which is indistinguishable, from inside the probe, from a family the
///         engine genuinely does not read. <b>So a green ledger is a claim about these arrangements,
///         not about the engine.</b> Every one of the six was found by somebody implementing the
///         property and being surprised, never by the gate:
///     </para>
///     <list type="bullet">
///         <item><c>flex-shrink</c> — <c>tight</c> overflowed but nothing could shrink, because CSS
///         Sizing §5.2.2 floors a definitely-sized box at its own width. Needed <c>squeezed</c>.</item>
///         <item><c>grid-template-columns</c> — every scene made the probe a flex container inside a
///         flex host, so grid measured inert before <i>and</i> after its bridge landed. Needed
///         <c>gridded</c>.</item>
///         <item><c>vertical-align</c> — no scene had a line box. Needed <c>inlined</c>.</item>
///         <item><c>transition-property</c> — <c>animated</c> already declared the family's only
///         emitted value, so the injected declaration changed nothing. Needed <c>primed</c>.</item>
///         <item><c>fill</c>/<c>stroke</c> — no scene had an <c>Icon</c>, because this project did not
///         reference <c>Vixen.Ui.Controls</c>. Needed the reference and a child icon.</item>
///         <item><c>overflow-x</c>/<c>overflow-y</c> — inert for real, but the scene that would have
///         noticed them becoming real did not exist until the clip landed.</item>
///     </list>
///     <para>
///         ⚠ <b>The shape to watch for: a new engine feature usually needs a new scene, and the person
///         who will notice is the one implementing the feature — not the next reader of the ledger.</b>
///         When a family you have just made real still measures inert, suspect this list before
///         suspecting your reader. And <b>the converse is not covered at all</b>: the verdict is a
///         union across four channels, so a family that moves one channel and should have moved two
///         passes. That distinction lives in <c>Vixen.Editor.Ui.Tests.UtilityFamilySupportTests</c>,
///         which asserts <i>what</i> happened rather than <i>that</i> something did.
///     </para>
/// </remarks>
/// <param name="Scrolling">
///     Whether <c>#probe</c> is a <see cref="ScrollView" /> nested inside another one, and the scene
///     drives scrolls between the recorded frames. False for every scene but <c>scrolled</c>.
/// </param>
/// <param name="Edited">
///     Whether the probe carries a focused <see cref="TextBox" />. False for every scene but
///     <c>edited</c>.
///     <para>
///         ⚠ <b>The eighth time this list has been the thing missing rather than the engine, and
///         the first where the missing ingredient was a <i>control state</i> rather than a shape.</b>
///         <c>TextField.OnDraw</c> returns before it draws anything at all unless
///         <see cref="UiElement.IsFocused" />, so a text box sitting unfocused in a scene
///         contributes a box and no caret — which is the <c>icon { width: 14px }</c> lesson one
///         parameter down, wearing focus instead of size. The caret is the only thing in the engine
///         that reads <c>caret-color</c>, so without the focus this scene would be present, look
///         right, and measure the property inert.
///     </para>
/// </param>
/// <param name="Unbroken">
///     Whether the probe carries a <c>#long</c> span holding one word with no break opportunity in
///     it. False for every scene but <c>overlong</c>.
///     <para>
///         ⚠ <b>A flag rather than a change to the shared children, and the difference is thirteen
///         baselines.</b> The obvious way to give the gate a long word is to lengthen
///         <c>#label</c>'s text, which every scene shares — and that re-measures every property in
///         the table against text nobody chose, for the sake of one family. Added the way the
///         <c>scrolled</c> scene adds its <c>#mark</c>: in one scene, leaving the other thirteen
///         byte-identical.
///     </para>
/// </param>
/// <param name="Figured">
///     Whether the probe carries a <c>#figures</c> span of digits in a face that implements the
///     OpenType features <c>font-variant-numeric</c> names. False for every scene but <c>figured</c>.
///     <para>
///         ⚠ <b>The ninth instance of this list's lesson, and the first where what was missing was a
///         <i>font</i> rather than a shape or a control state.</b> Every keyword of
///         <c>font-variant-numeric</c> is one OpenType feature — <c>tnum</c>, <c>onum</c>,
///         <c>zero</c>, <c>frac</c>, <c>ordn</c> — and TestShapeLana implements none of them, so the
///         whole family would have measured inert with the reader finished, the feature array
///         threaded and HarfBuzz correctly doing nothing with a tag the face has never heard of.
///         The same shape as <c>font-style</c> resolving <c>italic</c> against a family with no
///         italic, which is what the third face two hundred lines down is for.
///     </para>
///     <para>
///         ⚠ <b>And digits, which is the other half.</b> Every scene's text is
///         <c>"Ag jq Wm il"</c>; a numeric feature applied to a string with no figures in it changes
///         nothing, so the face alone would not have been enough. <c>0123456789</c> in a span of its
///         own, on a flag, leaving the other fourteen scenes byte-identical.
///     </para>
/// </param>
sealed record ProbeScene(
    string Name,
    string Css,
    bool Scrolling = false,
    bool Unbroken = false,
    bool Edited = false,
    bool Figured = false
);

/// <summary>Runs a declaration past the engine and reports what moved.</summary>
/// <remarks>
///     <para>
///         The measuring instrument behind <see cref="UtilityConsumptionGateTests" />. Everything here
///         is a real <see cref="UiDocument" /> — the same cascade, the same flexbox, the same draw
///         list — because the question is what the engine does, and no cheaper stand-in can answer it.
///     </para>
///     <para>
///         ⚠ <b>The emitted set is measured after the cascade, not read off the registry.</b>
///         <c>UtilityFamilies</c> says <c>rounded</c> emits <c>border-radius</c>; ExCSS expands that
///         into four corner longhands while parsing, and it is the four the consumers intern. The same
///         goes for <c>flex</c>, <c>padding</c>, <c>margin</c>, <c>inset</c> and <c>gap</c>. So the
///         emission table is taken by resolving a real element and asking what properties its computed
///         style ended up holding, which is post-expansion by construction and cannot drift from what
///         the loader does.
///     </para>
/// </remarks>
static class UtilityConsumptionProbe {
    /// <summary>A theme with one token of every kind, so the surface enumeration is total.</summary>
    /// <remarks>
    ///     ⚠ <b>Not the editor's theme, and not doc 09's worked example either.</b> Both are real
    ///     palettes with holes in them — the editor declares no <c>radius</c> scale at all, on purpose,
    ///     so every <c>rounded-*</c> family would silently drop out of the surface and go unmeasured.
    ///     A gate whose coverage depends on how rich somebody's palette happens to be is a gate that
    ///     quietly stops checking things. One token per kind, named so a failure message reads.
    /// </remarks>
    /// <remarks>
    ///     ⚠ <b>The colour token is not called <c>probe</c>, and the collision it avoids cost a run to
    ///     find.</b> <c>text-</c> resolves against the font-size scale before the colour table, so a
    ///     theme naming both of them the same thing makes <c>text-probe</c> a font size and leaves
    ///     <c>color</c> emitted by nothing at all — a property that then never appears in the ledger in
    ///     either column, which is worse than being reported wrong.
    /// </remarks>
    /// <remarks>
    ///     ⚠ <b><c>--drop-shadow-probe</c> is a second shadow token and not a duplicate of the
    ///     first.</b> <c>--shadow-*</c> and <c>--drop-shadow-*</c> are different v4 namespaces read
    ///     by different families into different dictionaries, so one token does not stand for both —
    ///     without this one, <see cref="UtilityFamilies.Surface" /> yields no <c>drop-shadow-&lt;token&gt;</c>
    ///     at all and the family is measured on its <c>none</c> keyword alone, which exercises the
    ///     theme lookup not at all. ⚠ And it carries no comma, deliberately: a <c>box-shadow</c> may
    ///     be a comma-separated list and an item of <c>filter</c> may not.
    /// </remarks>
    /// <remarks>
    ///     ⚠ <b>And <c>--*: initial;</c> before any of it, which keeps the gate measuring what it
    ///     says it measures.</b> The engine ships a default <c>@theme</c> now, so a theme file that
    ///     did not clear it would give every scale dozens of tokens — and
    ///     <c>UtilityFamilies.Probes</c> takes the ordinally <i>first</i> key of each. The probe
    ///     value would then be whichever v4 token happened to sort first, chosen by nobody, and a
    ///     failure message would name it. One token per kind is only one token per kind if nothing
    ///     else got in.
    /// </remarks>
    public const string ProbeTheme = """
        @theme {
            --*: initial;

            --color-paint: #3366cc;
            --spacing: 4px;
            --radius-probe: 6px;
            --text-probe: 13px;
            --text-probe--line-height: 20px;
            --font-weight-probe: 700;
            --breakpoint-probe: 640px;
            --shadow-probe: 0px 3px 6px rgba(0, 0, 0, 0.5);
            --drop-shadow-probe: 0px 3px 4px rgba(0, 0, 0, 0.4);
            --dark-mode: media;
        }
        """;

    /// <summary>The parts of the arrangement every scene shares.</summary>
    /// <remarks>
    ///     The probe is a container as well as an item — it has children that overflow it and text that
    ///     wraps — because half the properties under test are read on one side of that relationship and
    ///     half on the other. <c>#probe.moved</c> is the mutation the transition scene needs: something
    ///     has to change for a transition to be visible, and it has to out-specify both the scene's own
    ///     <c>#probe</c> rule and the injected declaration, or the frames after it would be measuring
    ///     the cascade rather than the animator.
    /// </remarks>
    public const string Common = """
        #host  { font-family: Probe; }
        .kid   { width: 24px; height: 14px; background-color: #40a040; }
        #wide  { width: 40px; height: 14px; background-color: #4040a0; }
        #label { width: 42px; background-color: #a04040; }
        #short { width: 90px; }
        #probe.moved { background-color: #ff8000; margin-left: 9px; }

        /* ⚠ Sized, or there is no icon. `Icon.OnDraw` gives up on a zero-area box before it looks at
           any paint at all, so an unsized icon would sit in the tree contributing nothing and `fill`
           and `stroke` would go on measuring inert with the reader present — the false gap that costs
           the most, because the honest response to it is to implement something already there. */
        icon { width: 14px; height: 14px; }
        """;

    static readonly ProbeScene[] Scenes = [
        // Tight. Nothing fits: the probe overflows its host and its children overflow it, so
        // shrinking, wrapping, clipping and every box-model longhand has something to move.
        new(
            "tight",
            """
            #host  { display: flex; flex-direction: row; width: 120px; height: 46px; align-items: stretch; }
            #probe { display: flex; flex-direction: row; flex-wrap: wrap; width: 44px;
                     position: relative; top: 3px; left: 3px;
                     border-width: 2px; border-color: #c02020; border-radius: 1px;
                     background-color: #204080; box-sizing: content-box; color: #e0e0e0; }
            #after { width: 96px; height: 20px; margin-left: -6px; background-color: #a0a040; }
            """
        ),

        // ⚠ A SCROLLPORT, and the thirteen scenes before it had none — which is not the same
        // sentence as "one of them scrolls". The `scrolled` scene's `#probe` is a `ScrollView`, and
        // `ControlTheme.vcss` gives a `scroll-view` `overflow: hidden` with an absolutely positioned
        // bar laid over the top, because that is how this framework has always drawn one. So no
        // scene anywhere put `Overflow.Scroll` on `#probe`, and `scrollbar-width` — which is read on
        // exactly that condition and on no other — measured inert with 180 corpus fixtures asserting
        // that it is not. That is the failure mode this list's own remark predicts: suspect the
        // scenes before the reader.
        //
        // Deliberately a plain `div` rather than a second `ScrollView`. The question is what the
        // LAYOUT store does with a scroll container, and going through the control would answer it
        // through `ControlTheme.vcss`, whose `hidden` is the thing that hid this in the first place.
        //
        // ⚠ <b>`#wide` gives up its width and the stack is pushed to the end, and without BOTH of
        // those this scene sees nothing.</b> `Common` gives every shared child a definite width, and
        // in `ltr` a gutter moves only the END edges of the content box — so a scene full of
        // fixed-size children stacked from the top is byte-identical with the gutter and without it,
        // scroll container or no scroll container. `width: auto` on `#wide` makes the stretch read
        // the inline content edge; `justify-content: flex-end` makes the stack read the block one.
        // One per axis, which is what it takes to see a property whose two axes are crossed.
        new(
            "scrollport",
            """
            #host  { display: flex; flex-direction: row; width: 120px; height: 80px; align-items: flex-start; }
            #probe { display: flex; flex-direction: column; width: 70px; height: 50px;
                     overflow: scroll; justify-content: flex-end;
                     background-color: #204080; color: #e0e0e0; }
            #wide  { width: auto; height: 14px; background-color: #404060; }
            #after { width: 40px; height: 20px; background-color: #a0a040; }
            """
        ),

        // ⚠ Squeezed, and the only scene where anything can actually shrink. `tight` overflows, but
        // overflowing is not the same as shrinking: CSS Flexbox §4.5 floors every item at its
        // automatic minimum, and CSS Sizing §5.2.2 makes a definitely-sized box contribute its own
        // width — so two items with a `width` cannot go below it however little room there is, and
        // `flex-shrink` moves nothing at all. `min-width: 0` is the opt-out CSS provides and the same
        // idiom a two-column control needs; without it this scene would overflow exactly like
        // `tight` and say just as little.
        //
        // The engine learned §5.2.2 late, which is why this scene is younger than the rest: before
        // that, an empty sized box contributed zero, `tight` did observe shrinking, and the gate was
        // green for a reason that had stopped being true.
        new(
            "squeezed",
            """
            #host  { display: flex; flex-direction: row; width: 100px; height: 40px; align-items: stretch; }
            #probe { display: flex; flex-direction: row; width: 80px; min-width: 0;
                     background-color: #204080; color: #e0e0e0; }
            #after { width: 80px; min-width: 0; height: 20px; background-color: #a0a040; }
            """
        ),

        // Roomy, and the probe is not sized. Growing, intrinsic sizing, `aspect-ratio` and the
        // min/max clamps only say anything where the box is free to be a different size.
        new(
            "roomy",
            """
            #host  { display: flex; flex-direction: row; width: 300px; height: 120px; align-items: flex-start; }
            #probe { display: flex; flex-direction: row; flex-wrap: nowrap;
                     background-color: #204080; color: #e0e0e0; }
            #after { width: 40px; height: 20px; background-color: #a0a040; }
            """
        ),

        // Out of flow, with no offsets of its own. The four physical insets and the two logical ones
        // are each decided by nothing else here, which is the only arrangement in which `right` and
        // `bottom` are not shadowed by `left` and `top`.
        new(
            "absolute",
            """
            #host  { display: flex; flex-direction: row; width: 200px; height: 100px; position: relative; }
            #probe { display: flex; flex-direction: row; width: 40px; height: 24px;
                     position: absolute; background-color: #204080; color: #e0e0e0; }
            #after { width: 30px; height: 20px; background-color: #a0a040; }
            """
        ),

        // A wrap container with more cross space than its lines need. `align-content` is the one
        // alignment property that says nothing at all unless there are several lines *and* room left
        // over, which no other scene here has both of.
        new(
            "wrapped",
            """
            #host  { display: flex; flex-direction: row; width: 200px; height: 130px; align-items: flex-start; }
            #probe { display: flex; flex-direction: row; flex-wrap: wrap; width: 60px; height: 110px;
                     align-content: flex-start; justify-content: flex-start; align-items: flex-start;
                     background-color: #204080; color: #e0e0e0; }
            #label { width: 42px; }
            #short { width: 40px; }
            #after { width: 30px; height: 20px; background-color: #a0a040; }
            """
        ),

        // Everything a few pixels across. The utility scales start at one spacing step, so `min-w-2`
        // is eight pixels — smaller than anything in the other scenes, where a minimum that low is
        // satisfied before it is applied and moves nothing. A floor only shows above the floor.
        new(
            "tiny",
            """
            #host  { display: flex; flex-direction: row; width: 40px; height: 40px; align-items: flex-start; }
            #probe { display: flex; flex-direction: row; width: 4px; height: 4px; overflow: hidden;
                     background-color: #204080; color: #e0e0e0; }
            .kid   { width: 2px; height: 2px; }
            #wide  { width: 2px; height: 2px; }
            #label { width: 2px; }
            #short { width: 2px; }
            #after { width: 2px; height: 2px; background-color: #a0a040; }
            """
        ),

        // The tight scene with transitions already switched on, so that a duration or a timing
        // function has something to be the duration *of*. See `primed` for the other half.
        new(
            "animated",
            """
            #host  { display: flex; flex-direction: row; width: 120px; height: 46px; align-items: stretch; }
            #probe { display: flex; flex-direction: row; flex-wrap: wrap; width: 44px;
                     position: relative; border-width: 2px; border-color: #c02020;
                     background-color: #204080; color: #e0e0e0;
                     transition-property: all; transition-duration: 200ms;
                     transition-timing-function: linear; }
            #after { width: 96px; height: 20px; background-color: #a0a040; }
            """
        ),

        // ⚠ <b>Primed: a duration and a timing function, aimed at a property the mutation does not
        // touch.</b> This is the only arrangement in which `transition-property` itself is visible,
        // and the file was wrong about that for as long as the property was inert for another reason.
        //
        // The comment on `animated` used to say the scenes with transitions *switched off* were where
        // `transition-property` would show. They are not, and cannot be: `transition-duration`
        // defaults to zero, so `transition-property: all` in a scene that declares nothing else is a
        // transition of length nought — the animator declines it explicitly — and the frames are
        // byte-identical to having written nothing. Meanwhile the family's only emitted value *is*
        // `all`, which is already what `animated` declares, so injecting it there changes nothing
        // either. Between the two the property had nowhere to be observed, and it measured inert on
        // the run that made every other part of the machinery real. That is the third time a gate
        // scene has been the thing missing rather than the engine.
        //
        // `color` is named because `#probe.moved` changes `background-color` and `margin-left` and
        // leaves `color` alone: the baseline therefore transitions nothing, and an injected
        // `transition-property: all` starts fading both. A property named `none` would do as well;
        // this way the scene also proves the animator honours the *list* rather than treating any
        // `transition-property` at all as "everything".
        new(
            "primed",
            """
            #host  { display: flex; flex-direction: row; width: 120px; height: 46px; align-items: stretch; }
            #probe { display: flex; flex-direction: row; flex-wrap: wrap; width: 44px;
                     position: relative; border-width: 2px; border-color: #c02020;
                     background-color: #204080; color: #e0e0e0;
                     transition-property: color; transition-duration: 200ms;
                     transition-timing-function: linear; }
            #after { width: 96px; height: 20px; background-color: #a0a040; }
            """
        ),

        // ⚠ <b>Gridded, and it is the only scene in which a grid property can move anything at all.</b>
        // Every scene above makes `#probe` a flex container inside a flex host, and on a flex box a
        // track list is not wrong — it is simply not read, by CSS as much as by this engine. So
        // `grid-template-columns` measured as inert for as long as this file had no grid in it, and
        // it went on measuring inert after the bridge landed: the gate would have stayed green while
        // `InertProperties.txt` said grid was unreachable and it no longer was. That is the failure
        // this gate exists to prevent, pointed at itself.
        //
        // ⚠ The probe is a grid container *and* a grid item, which no other scene needs to arrange
        // deliberately. `grid-template-columns` is read on the container and `grid-column` on the
        // item, and with `#host` flex — as it is everywhere else — a placement longhand on `#probe`
        // has no grid to be placed in and stays invisible. Both halves have to be a grid at once.
        //
        // Three columns and three rows against four children, so an item that moves by one track
        // moves visibly and the implicit-track properties have a row to create.
        new(
            "gridded",
            """
            #host  { display: grid; grid-template-columns: 60px 60px; width: 120px; height: 90px; }
            #probe { display: grid; grid-template-columns: 20px 20px 20px; grid-auto-rows: 14px;
                     width: 60px; height: 46px;
                     background-color: #204080; color: #e0e0e0; }
            #after { width: 96px; height: 20px; background-color: #a0a040; }
            """
        ),

        // ⚠ <b>Placed, and every one of the five properties it exists for measured inert with
        // `gridded` already present.</b> That is the point worth keeping: `gridded` is a grid at both
        // ends and still could not see `grid-auto-columns`, `grid-column-end`, `grid-row`,
        // `grid-row-end` or `justify-self`. A scene being *about* a feature does not make it able to
        // observe the feature, and the gate said so the first time the families were registered.
        //
        // Four arrangements, each answering one of them, and none of them arbitrary:
        //
        // ⚠ <b>The probe starts on the last track, not the first.</b> An auto-placed item occupies
        // one track and therefore already ends on the line after it — so `grid-column-end: 2` on an
        // item in column 1 is the position it was in anyway, and both `-end` families measured inert
        // for that reason alone. Starting at line 3 means the emitted end line is somewhere the item
        // is not, and §8.3's rule that a reversed pair swaps is what carries it there.
        //
        // ⚠ <b>The probe is smaller than its cell.</b> `justify-self` distributes free space, and
        // `gridded`'s probe is exactly its column's width — so every value of it resolved to the same
        // rectangle. A definite size narrower than the track is what leaves the property something to
        // move, and it is also the honest case: an item that fills its cell is one nobody writes
        // `justify-self` on.
        //
        // ⚠ <b>The row position is written as the `grid-row` shorthand and the column position as a
        // longhand. That asymmetry WAS load-bearing and is no longer — the defect it worked around is
        // fixed, and the asymmetry is kept only because it costs nothing and this scene is expensive
        // to get wrong.</b> `ApplyPlacements` used to apply the shorthand first and then overwrite it
        // from the longhands whenever they were present, so a longhand beat a shorthand *whatever
        // order they were declared in*. With `grid-row-start: 3` here, every `grid-row` this gate
        // injected was silently discarded and the property measured inert while the engine read it
        // perfectly; declaring the row through the shorthand left the injected shorthand something to
        // replace.
        //
        // That was a real defect and not only a fact about this scene — `grid-row: 1 / -1` from a
        // `row-span-full` was dropped on any element some other rule had given a `grid-row-start`, no
        // diagnostic, item auto-placed into a real cell. It is fixed in the cascade rather than in the
        // bridge: `ShorthandExpansion` now splits `grid-column` and `grid-row` into their two
        // longhands at load, so the two forms are comparable declarations and the later one wins. The
        // longhand form of the row position was tried here and all five properties still measure live,
        // so the workaround is genuinely spent — but a scene rewrite buys nothing and this file's own
        // history is that the five are easy to make inert again by accident.
        //
        // ⚠ <b>The probe flows in columns, where every other scene flows in rows.</b>
        // `grid-auto-columns` sizes implicit *columns*, and a container with explicit columns and
        // `grid-auto-flow: row` never creates one however many children it has. `grid-auto-rows` is
        // read in `gridded` for the mirror-image reason, which is why only one of the pair was inert.
        new(
            "placed",
            """
            #host  { display: grid; grid-template-columns: 70px 70px 70px;
                     grid-template-rows: 44px 44px 44px; width: 210px; height: 132px; }
            #probe { display: grid; grid-template-rows: 14px 14px; grid-auto-flow: column;
                     grid-column-start: 3; grid-row: 3 / 4;
                     width: 40px; height: 30px;
                     background-color: #204080; color: #e0e0e0; }
            #after { width: 20px; height: 20px; background-color: #a0a040; }
            """
        ),

        // ⚠ <b>Inlined, and it is the only scene in which a line box exists at all.</b> The same
        // lesson the `gridded` scene records, arriving for the third time and by now predictable:
        // `vertical-align` sat in `InertProperties.txt` with a task number against it, and the day
        // an inline formatting context landed it would have gone on measuring inert — every other
        // scene here makes `#probe` a flex, block or grid container, and CSS applies
        // `vertical-align` to none of those. The gate would have stayed green while the file said
        // the property was unreachable and it no longer was.
        //
        // ⚠ The probe is an inline-level box *and* the container of a line, which is the same
        // both-halves-at-once arrangement `gridded` needs and for the same reason: `vertical-align`
        // is read on the item and `display: inline-block` shows its effect on the container. With
        // `#host` flex — as it is everywhere else — an item's `vertical-align` has no line to sit on.
        //
        // The three children are deliberately unequal in height, because every value of
        // `vertical-align` this engine implements is a *relation between* boxes on one line: three
        // boxes of the same height are aligned identically by `baseline`, `top` and `bottom`, and a
        // scene made of those would measure the property inert while implementing it correctly.
        new(
            "inlined",
            """
            #host  { display: block; width: 200px; height: 120px; }
            #probe { display: inline-block; background-color: #204080; color: #e0e0e0; }
            .kid   { display: inline-block; width: 24px; height: 14px; }
            #wide  { display: inline-block; width: 40px; height: 22px; }
            #label { display: inline-block; width: 42px; height: 8px; }
            #short { display: inline-block; width: 30px; height: 18px; }
            #after { display: inline-block; width: 30px; height: 20px; background-color: #a0a040; }
            """
        ),

        // ⚠ <b>Translated, and it is the only scene in which the probe is transformed at all.</b> The
        // lesson `gridded`, `inlined` and `primed` each record, added this time *before* it costs a
        // run rather than after. `translate` itself is observable everywhere — moving a box moves its
        // rectangle, its draw commands and the hit grid in every arrangement above — so this scene is
        // not what gives the gate eyes for the family that just landed. It is what gives it eyes for
        // the next one.
        //
        // A `transform-origin`, a `transform-box`, or `scale` and `rotate` when the compositor they
        // need arrives, are all properties that modify *an existing transform* and say nothing at all
        // where there is none — exactly the shape of `transition-duration`, which needed `animated`,
        // and `transition-property`, which needed `primed` and measured inert for a whole release
        // because neither existed. Every other scene here leaves `#probe` untransformed, so each of
        // those would have measured inert on the day it became real.
        //
        // ⚠ The host clips and the probe is pushed partly out of it, which is the second thing no
        // other scene arranges: a transform and a clip interacting. That is where an implementation
        // that moved the box and not the rectangle it is cut by would show, and it is the interaction
        // the per-axis overflow work had to get right for the untransformed case.
        // ⚠ <b>A probe that already carries a two-layer <c>mask-image</c> — the sixth instance of the
        // lesson <c>gridded</c>, <c>inlined</c>, <c>primed</c>, <c>translated</c> and <c>clipped</c>
        // each record.</b> <c>mask-composite</c> says how the layers of a mask list combine, so on an
        // element with no mask list it is a declaration about nothing and moves not one pixel of any
        // of the four channels. Every scene above has no mask, so all four of
        // <c>mask-add</c>/<c>subtract</c>/<c>intersect</c>/<c>exclude</c> measured inert with the
        // reader sitting in <c>DrawListBuilder.Composites</c> — the false gap that costs the most,
        // because the honest response to it is to implement something already there.
        //
        // ⚠ <b>Two layers and not one, and they have to disagree.</b> Every operator is the identity
        // on a list of one — see <c>UiMask.Coverage</c> — so a single-layer mask here would leave the
        // property just as invisible as no mask at all. Two ramps at right angles differ under all
        // four: <c>add</c> unions them, <c>intersect</c> multiplies, and the corner where one is
        // opaque and the other is not is where the four part company.
        //
        // ⚠ It is on <c>#probe</c> rather than on a child because that is where <c>Run</c> injects,
        // and the two have to meet on one element: a mask on the child and the operator on the parent
        // would be two declarations that never see each other.
        new(
            "masked",
            """
            #host  { display: flex; flex-direction: row; width: 120px; height: 46px; align-items: stretch; }
            #probe { display: flex; flex-direction: row; flex-wrap: wrap; width: 44px;
                     background-color: #204080; color: #e0e0e0;
                     mask-image: linear-gradient(to right, #000000, transparent), linear-gradient(to bottom, #000000, transparent); }
            #after { width: 96px; height: 20px; background-color: #a0a040; }
            """
        ),

        new(
            "translated",
            """
            #host  { display: flex; flex-direction: row; width: 120px; height: 46px; overflow: hidden; }
            #probe { display: flex; flex-direction: row; flex-wrap: wrap; width: 44px;
                     translate: 12px 6px;
                     border-width: 2px; border-color: #c02020;
                     background-color: #204080; color: #e0e0e0; }
            #after { width: 96px; height: 20px; background-color: #a0a040; }
            """
        ),

        // ⚠ <b>A line of text that is too long for the box it is in and forbidden from wrapping —
        // the one arrangement in which an overflow marker has anything to mark.</b> Fifth instance of
        // the lesson `gridded`, `inlined`, `primed` and `translated` each record, and the reason it
        // keeps recurring is worth stating plainly: this gate's logic has never been wrong, and its
        // scene list has been wrong five times. A property is observable only in a situation that
        // needs it, and the situations are hand-written.
        //
        // Nothing above could see `text-overflow`. Every other scene either lets the label wrap — in
        // which case there is no overflowing line to truncate — or clips it with `overflow: hidden`
        // on `#probe` while the text lives in a child that is free to be as wide as it likes. `tiny`
        // comes closest and still cannot: at two pixels the label has already overflowed everything,
        // but it wraps, so the marker would have nothing to replace.
        //
        // ⚠ <b>The three declarations are on `#label` and the fourth is what gets injected.</b> That
        // is forced by the instrument rather than chosen: `Run` puts the declaration under test on
        // `#probe`, and `#probe` owns no text — its glyphs are in the `#label` and `#short` spans. So
        // the scene has to establish everything `text-overflow` *needs* and leave the property itself
        // to be the one thing that varies. It reaches the span by inheriting, which is Vixen's
        // deviation from CSS and is argued at `UiDocument.EllipsisOf`; a reader that did not inherit
        // would resolve on `#probe`, find no text there, and measure inert with the feature working.
        //
        // `#short` is left wrapping on purpose, so that one text child in the scene is unaffected and
        // the signature says the marker landed on the line that overflowed rather than on all of them.
        new(
            "clipped",
            """
            #host  { display: flex; flex-direction: row; width: 200px; height: 60px; align-items: flex-start; }
            #probe { display: flex; flex-direction: row; flex-wrap: wrap; width: 80px;
                     background-color: #204080; color: #e0e0e0; }
            .kid   { width: 8px; height: 8px; }
            #wide  { width: 8px; height: 8px; }
            #label { width: 36px; overflow: hidden; white-space: nowrap; }
            #short { width: 36px; }
            #after { width: 30px; height: 20px; background-color: #a0a040; }
            """
        ),

        // ⚠ <b>Overlong: one word with no break opportunity in it, wider than the box it is in — the
        // only arrangement in which `overflow-wrap` decides anything.</b> The eighth instance of the
        // lesson `gridded`, `inlined`, `primed`, `translated`, `clipped`, `masked` and `decorated`
        // each record, and this one was found the same way five of those were: by measuring a
        // property with a finished reader and being told it was dead.
        //
        // `UiDocument.WrapModeOf` has read `overflow-wrap` for as long as there has been a wrapper to
        // read it for, and `LineWrapper` applies `TextWrapMode.Anywhere` in exactly one branch —
        // "nothing fits: one unbreakable run is wider than the whole line". Every scene above shares
        // `#label`'s "Ag jq Wm il", whose longest word is two characters: there is always an earlier
        // opportunity that fitted, the branch never runs, and the property measured inert in all
        // fourteen of them. It is not a narrow box that is needed — `tiny` has a two-pixel one — it
        // is a long *word*.
        //
        // ⚠ The word is on a child span and the declaration lands on `#probe`, so this measures the
        // inheritance at the same time — the same arrangement `decorated` uses and for the same
        // reason. `overflow-wrap` is in `InheritedProperties`, and a `wrap-break-word` written on a
        // row whose text is in a child is essentially the only way anybody writes it.
        //
        // ⚠ And the box is wide enough for several graphemes rather than one. A one-grapheme box
        // makes `Squeeze` return `start` and the wrapper falls out of the branch it was supposed to
        // exercise, so the property would measure inert for want of room rather than for want of a
        // reader — `tiny` again, one step further along.
        new(
            "overlong",
            """
            #host  { display: flex; flex-direction: row; width: 200px; height: 90px; align-items: flex-start; }
            #probe { display: flex; flex-direction: row; flex-wrap: wrap; width: 90px;
                     background-color: #204080; color: #e0e0e0; }
            .kid   { width: 8px; height: 8px; }
            #wide  { width: 8px; height: 8px; }
            #label { width: 40px; }
            #short { width: 40px; }
            #long  { width: 44px; }
            #after { width: 30px; height: 20px; background-color: #a0a040; }
            """,
            Unbroken: true
        ),

        // ⚠ <b>Figured: digits, in the one embedded face that implements a numeric OpenType
        // feature.</b> The ninth instance of the lesson `gridded`, `inlined`, `primed`,
        // `translated`, `clipped`, `masked`, `decorated`, `overlong` and `edited` each record, and
        // it is worth saying which half was missing because both were. `font-variant-numeric`'s
        // keywords are `tnum`, `pnum`, `onum`, `lnum`, `zero`, `ordn`, `frac` and `afrc`;
        // TestShapeLana — which is every other scene's only face — implements not one of them, and
        // no scene's text contains a digit for one to apply to. So the family would have measured
        // inert with `UiDocument.ResolveText` reading the property, `TextShaper` handing HarfBuzz
        // the array and HarfBuzz correctly ignoring a tag the font has never heard of: three
        // finished pieces and a green gate calling all of them dead.
        //
        // ⚠ The declaration lands on `#probe` and the digits are in a child, so this measures the
        // inheritance at the same time — the same arrangement `decorated` and `overlong` use, and
        // for the same reason: `font-variant-numeric` is in `InheritedProperties`, and `tabular-nums`
        // is written on a table row whose figures are in cells essentially always.
        //
        // ⚠ `#figures` is the only element in any scene in the `Figured` family, and that is
        // deliberate: putting Open Sans on `#host` would re-measure every typography property in the
        // table against a face nobody chose.
        new(
            "figured",
            """
            #host    { display: flex; flex-direction: row; width: 240px; height: 90px; align-items: flex-start; }
            #probe   { display: flex; flex-direction: row; flex-wrap: wrap; width: 200px;
                       background-color: #204080; color: #e0e0e0; }
            .kid     { width: 8px; height: 8px; }
            #wide    { width: 8px; height: 8px; }
            #label   { width: 40px; }
            #short   { width: 40px; }
            #figures { font-family: Figured; font-size: 20px; }
            #after   { width: 30px; height: 20px; background-color: #a0a040; }
            """,
            Figured: true
        ),

        // ⚠ <b>Decorated: the probe already carries an underline, and without that the four
        // properties that <i>modify</i> one all measure inert.</b> `primed`'s lesson again, and this
        // time it was predicted rather than discovered — but only just, and the measurement is worth
        // recording because the shape recurs. `text-decoration-line` is observable in every scene
        // above: it adds a rectangle to the draw list wherever there is text. Its thickness, colour,
        // style and offset are not observable anywhere, because each of them changes a bar that only
        // exists once something else asked for one, and the injected declaration is the *only*
        // declaration — so `text-decoration-thickness: 4px` on an undecorated probe correctly moves
        // nothing at all. Four properties, a reader for each, and a green gate that would have said
        // they were dead.
        //
        // ⚠ <b>The underline is on `#probe` and the text is in its children, which is the second
        // half of the feature and not a convenience.</b> `#label` and `#short` are spans inside the
        // probe; a decoration written on a container and read on the text inside it is what every
        // `.vxml` panel does, because an interpolation emits its text as a child element. So this
        // scene measures the inheritance at the same time — the same argument the icon two hundred
        // lines down makes for `fill` and `stroke`, and for the same reason: an injected declaration
        // lands on `#probe` and nowhere else.
        new(
            "decorated",
            """
            #host  { display: flex; flex-direction: row; width: 200px; height: 80px; align-items: flex-start; }
            #probe { display: flex; flex-direction: row; flex-wrap: wrap; width: 120px;
                     background-color: #204080; color: #e0e0e0;
                     text-decoration-line: underline; }
            .kid   { width: 8px; height: 8px; }
            #wide  { width: 8px; height: 8px; }
            #label { width: 100px; }
            #short { width: 40px; }
            #after { width: 30px; height: 20px; background-color: #a0a040; }
            """
        ),

        // ⚠ <b>Ringed: the probe already carries an outline, and this is `decorated`'s lesson
        // applied before the fact rather than after it.</b> Three of the four `outline-*` longhands
        // only *modify* a ring — a width, a colour and an offset are all answers to "which ring" —
        // and the injected declaration is the only declaration, so on a probe with no outline
        // `outline-color: #fff` correctly moves nothing at all. Same shape as
        // `text-decoration-thickness`, same count: three readers and a green gate that would have
        // called them dead.
        //
        // ⚠ <b>`outline-style` is the one that needs the ring to be here for the opposite reason.</b>
        // Its two registered values are `solid` and `none`; `solid` is what a width already means in
        // this engine, so injecting it changes nothing anywhere and could not. `none` is observable
        // exactly where there is a ring to remove, which is here. The verdict is a union over every
        // value a family can emit — see `Take` — so one scene carrying an outline is what makes the
        // pair measurable at all, and a scene with a *bare* width would not have done it.
        //
        // ⚠ <b>The offset is a non-zero one on purpose.</b> With `outline-offset: 0` in the baseline,
        // the injected `outline-offset-0` is the value already there and moves nothing — the exact
        // accident that made `transition-property` unmeasurable in `animated` for a week. Two pixels
        // here leaves every one of the family's five values different from the baseline except one.
        //
        // ⚠ <b>And the host is roomy, which is the part a rectangle-shaped scene gets wrong.</b> An
        // outline is drawn *outside* the border box and takes no space in the layout, so a probe
        // packed against its host's edge would have its ring painted over the viewport boundary and
        // the rasterised difference between a 1px and an 8px ring would be mostly off-screen. The
        // draw list is compared rather than the pixels here, so this is belt and braces — but the
        // scene is also the one a pixel test will reach for next.
        new(
            "ringed",
            """
            #host  { display: flex; flex-direction: row; width: 200px; height: 80px;
                     align-items: center; justify-content: center; }
            #probe { display: flex; flex-direction: row; flex-wrap: wrap; width: 60px; height: 24px;
                     background-color: #204080; color: #e0e0e0;
                     border-width: 2px; border-color: #c02020; border-radius: 3px;
                     outline-width: 2px; outline-color: #40c040; outline-offset: 2px; }
            .kid   { width: 8px; height: 8px; }
            #after { width: 30px; height: 20px; background-color: #a0a040; }
            """
        ),

        // ⚠ <b>Scrolled, and it is the only scene in which anything scrolls at all.</b> The seventh
        // time this list has been the thing missing rather than the engine, and the first where the
        // arrangement had to *do* something between frames as well as be shaped a certain way.
        //
        // The four scroll families say where a scroll lands, how it gets there and what happens at
        // the end. Every one of those is a question about a scroll that is actually happening, so a
        // static tree measures all four inert however well `ScrollView` reads them — and a tree with
        // one scroll container measures half of them inert, because `scroll-margin` is read off the
        // *target* and `scroll-padding` off the *container* and the injected declaration only ever
        // lands on `#probe`. Hence the nesting: `#probe` is a `ScrollView` inside a `ScrollView`, so
        // it is the target of the outer one's `ScrollIntoView` and the container of its own.
        //
        // ⚠ <b>And the scrolls are driven in three phases, because one call cannot see both edges.</b>
        // `ScrollIntoView` moves the minimum that works, so a target above the viewport exercises
        // `scroll-margin-top` and tells you nothing whatever about `scroll-margin-bottom` — the
        // branch never runs. The gate is per-longhand, so a scene that only ever approached from one
        // side would leave six of the twelve inset properties measuring inert with the reader present.
        // Phase 0 approaches everything from below-right, phase 1 from above-left, and phase 2 is the
        // wheel. `direction` is left `ltr`, so `-inline-start` folds onto the left and `-inline-end`
        // onto the right and the same two phases cover them.
        //
        // ⚠ <b>The wheel is last on purpose.</b> It calls `ScrollView.Settle`, which abandons any
        // smooth scroll in flight — so a phase 2 that ran before the recorded frames would erase the
        // one thing `scroll-behavior: smooth` can be seen by, and the property would measure inert
        // because the scene cancelled it rather than because nothing reads it.
        new(
            "scrolled",
            """
            /* ⚠ `ControlTheme.vcss` itself, quoted, because this project loads no theme and a
               `ScrollView` without one does not scroll. `scroll-content { flex-shrink: 0 }` is the
               load-bearing line: a column flex container shrinks its items to fit before it lets
               them overflow, so an unstyled scroll view measures its content at its own height and
               `MaximumTop` is zero for ever. That is the `dock-panel.scrolls > *` finding one level
               down, and it cost a run — the four right-hand insets measured consumed and the eight
               top and left ones inert, which reads exactly like a half-written reader. */
            scroll-view          { flex-direction: column; overflow: hidden; position: relative; }
            scroll-content       { flex-direction: column; flex-shrink: 0; align-self: flex-start; min-width: 100%; }
            scrollbar            { position: absolute; }
            scrollbar.vertical   { top: 0px; right: 0px; bottom: 0px; width: 10px; }
            scrollbar.horizontal { left: 0px; right: 0px; bottom: 0px; height: 10px; }

            #host   { display: flex; flex-direction: column; width: 200px; height: 140px; align-items: flex-start; }
            #outer  { width: 100px; height: 60px; }
            #lead   { width: 260px; height: 90px; background-color: #404060; }
            #trail  { width: 260px; height: 90px; background-color: #604040; }
            #probe  { width: 60px; height: 40px; margin-left: 110px; background-color: #204080; }
            #above  { width: 140px; height: 50px; background-color: #206040; }
            #below  { width: 140px; height: 50px; background-color: #402060; }
            #mark   { width: 30px; height: 12px; margin-left: 70px; background-color: #c0a020; }
            .kid    { width: 8px; height: 8px; }
            #wide   { width: 8px; height: 8px; }
            #label  { width: 30px; }
            #short  { width: 30px; }
            #after  { width: 30px; height: 12px; background-color: #a0a040; }
            """,
            Scrolling: true
        ),

        // ⚠ <b>Edited, and it is the only scene in which anything has a caret.</b> `caret-color` is
        // read in exactly two places — `TextField.CaretColour` and `CodeEditor`'s copy of it — and
        // both are inside an `if (!IsFocused) return;`. So the ingredient is a *state* rather than a
        // shape, which is new: every earlier gap in this list was an arrangement the tree could not
        // make, and this one is an arrangement it could make perfectly while measuring the property
        // inert, because nothing in the probe had ever focused anything.
        //
        // ⚠ <b>The field is a child of `#probe` and not `#probe` itself</b>, for the reason the icon
        // is: `caret-color` inherits, the injected declaration lands on `#probe`, and a
        // `caret-accent` is at least as likely to be written on a form row as on the input in it. A
        // scene that made the probe the field would measure the reader and not the inheritance, and
        // the inheritance is the half that `InheritedProperties` had to be changed for.
        //
        // ⚠ <b>And the value is non-empty on purpose.</b> `TextField.OnDraw` has two caret branches
        // — one for a field with no text block at all and one for a field with lines — and only the
        // second is reached here. Both call the same helper, so one is enough to measure the
        // property; picking the empty branch instead would have measured a rectangle whose height
        // comes from `min-height: 1.2em`, which is a font metric this project resolves against the
        // probe face and a needlessly brittle thing for a baseline to depend on.
        new(
            "edited",
            """
            /* `ControlTheme.vcss` quoted, for the reason the scrolled scene quotes it: this project
               loads no theme, and `field-text { min-height: 1.2em }` is the line that stops the
               field being its padding and nothing else. The colours are literals rather than the
               theme's `var()`s — there is no `root` rule here to declare the tokens against, and a
               `var()` that resolves to nothing would leave the caret's fallback path measuring
               instead of the property. */
            textbox          { flex-direction: row; align-items: center; padding: 4px;
                               border-width: 1px; border-color: #c02020; background-color: #f0f0f0;
                               color: #202020; position: relative; }
            field-text       { flex-shrink: 0; white-space: nowrap; min-height: 1.2em; }
            field-placeholder { position: absolute; left: 4px; display: none; }

            #host  { display: flex; flex-direction: column; width: 200px; height: 120px;
                     align-items: flex-start; }
            #probe { display: flex; flex-direction: column; width: 160px;
                     background-color: #204080; color: #e0e0e0; }
            .kid   { width: 8px; height: 8px; }
            #wide  { width: 8px; height: 8px; }
            #label { width: 30px; }
            #short { width: 30px; }
            #after { width: 30px; height: 12px; background-color: #a0a040; }
            """,
            Edited: true
        )
    ];

    static readonly Dictionary<string, SceneSignature> Baselines = new(StringComparer.Ordinal);
    static readonly Dictionary<string, IReadOnlyList<string>> Verdicts = new(StringComparer.Ordinal);
    static readonly Lock Gate = new();

    /// <summary>Which observables a declaration moves, over every scene.</summary>
    /// <param name="property">The CSS property.</param>
    /// <param name="value">The value a utility gives it.</param>
    /// <returns>The channel names, ordered. Empty means nothing in the engine acts on it.</returns>
    public static IReadOnlyList<string> Channels(string property, string value) {
        var key = $"{property}:{value}";

        lock (Gate) {
            if (Verdicts.TryGetValue(key, out var cached)) {
                return cached;
            }

            var channels = new List<string>();

            foreach (var scene in Scenes) {
                if (!Baselines.TryGetValue(scene.Name, out var plain)) {
                    plain = Run(scene, null);
                    Baselines[scene.Name] = plain;
                }

                var probed = Run(scene, $"{property}: {value};");

                Note(channels, "layout", plain.Layout, probed.Layout);
                Note(channels, "paint", plain.Paint, probed.Paint);
                Note(channels, "cursor", plain.Cursor, probed.Cursor);
                Note(channels, "hit", plain.Hit, probed.Hit);

                if (channels.Count == 4) {
                    break;
                }
            }

            channels.Sort(StringComparer.Ordinal);
            Verdicts[key] = channels;
            return channels;
        }
    }

    /// <summary>The properties an element ends up holding when given one declaration.</summary>
    /// <param name="declaration">The declaration text, without the braces.</param>
    /// <returns>The property names the cascade resolved, post-expansion.</returns>
    /// <remarks>
    ///     Post-expansion is the whole point: <c>border-radius: 4px</c> comes back as four corner
    ///     longhands, which is what the consumers intern and what the gate therefore has to judge.
    /// </remarks>
    public static IReadOnlyList<string> Resolved(string declaration) {
        using var document = new UiDocument(200f, 100f);
        document.Load($"#probe {{ {declaration} }}", StyleOrigin.Author);

        var probe = document.Create("div", document.Root, "probe");
        var bare = document.Create("div", document.Root);

        document.Update();

        var names = document.Styles.Properties;
        var baseline = new HashSet<int>();

        foreach (var id in bare.Style.Properties) {
            baseline.Add(id);
        }

        var resolved = new List<string>();

        foreach (var id in probe.Style.Properties) {
            if (!baseline.Contains(id)) {
                resolved.Add(names.NameOf(id));
            }
        }

        resolved.Sort(StringComparer.Ordinal);
        return resolved;
    }

    /// <summary>Every property/value pair the whole family table can put on an element.</summary>
    /// <returns>Pairs of property and value, each with the utilities that emit it.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Through the generator and the loader rather than through
    ///         <c>UtilityFamilies.TryResolve</c> directly</b>, so that the answer is what a project's
    ///         real generated sheet would put in the cascade: escaped selectors,
    ///         <c>@layer utilities</c>, and ExCSS's shorthand expansion all included. Resolving the
    ///         declarations by hand would measure the registry's intentions, and the registry's
    ///         intentions are not what a consumer interns.
    /// </para>
    ///     <para>
    ///         ⚠ <b>The probe has children, and a family that styles them would otherwise be invisible
    ///         to this gate entirely — which is worse than being reported inert.</b>
    ///         <c>space-x-4</c> and <c>divide-y</c> emit
    ///         <c>&gt; :not(:last-child) { … }</c>: nothing at all lands on the element carrying the
    ///         class, so an element-only reading returns no properties for them, they never enter
    ///         <c>Emitted</c>, and they are neither <c>Consumers</c> nor <c>Inert</c> nor
    ///         <c>Composed</c> — unclassified, in a file whose whole claim is that nothing escapes
    ///         classification. Reading the first child closes it, and closes it for the next scoped
    ///         family too rather than for these two.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two children, and the baseline has two as well.</b> Two because
    ///         <c>:not(:last-child)</c> matches nothing under a single child, so a one-child probe
    ///         would report a scoped family as emitting nothing and be exactly as blind as no child at
    ///         all. And the bare element needs the same shape because the child <i>inherits</i> —
    ///         <c>text-probe</c> puts a <c>font-size</c> on the probe and its children read it — so a
    ///         childless baseline would attribute every inherited property to the child as though the
    ///         family had set it there.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<(string Property, string Value, string Utility)> Emissions() {
        var tokens = ThemeTokens.Parse(ProbeTheme);
        var generator = new UtilityGenerator(tokens);
        var emissions = new List<(string, string, string)>();

        foreach (var utility in UtilityFamilies.Surface(tokens)) {
            using var document = new UiDocument(200f, 100f);
            document.Load(generator.Generate([utility]), StyleOrigin.Author);

            var probe = document.Create("div", document.Root, null, utility);
            var child = document.Create("div", probe);
            document.Create("div", probe);

            var bare = document.Create("div", document.Root);
            var bareChild = document.Create("div", bare);
            document.Create("div", bare);

            document.Update();

            var names = document.Styles.Properties;
            var values = document.Styles.Values;

            Collect(emissions, names, values, probe, bare, utility);
            Collect(emissions, names, values, child, bareChild, utility);
        }

        return emissions;

        static void Collect(
            List<(string, string, string)> emissions,
            NameTable names,
            NameTable values,
            UiElement measured,
            UiElement against,
            string utility
        ) {
            var baseline = new HashSet<int>();

            foreach (var id in against.Style.Properties) {
                baseline.Add(id);
            }

            var properties = measured.Style.Properties;
            var held = measured.Style.Values;

            for (var i = 0; i < properties.Length; i++) {
                if (!baseline.Contains(properties[i])) {
                    emissions.Add((names.NameOf(properties[i]), values.NameOf(held[i]), utility));
                }
            }
        }
    }

    /// <summary>The whole measurement: what is emitted, what is read, and by what.</summary>
    /// <param name="Emitted">Every property any utility can put on an element.</param>
    /// <param name="Consumers">The ones something acts on, and which observables moved.</param>
    /// <param name="Inert">The ones nothing acts on, and the utilities that emit them.</param>
    /// <param name="Composed">
    ///     The <c>--tw-*</c> fragments, and the properties each one is assembled into.
    ///     <para>
    ///         ⚠ <b>A third category, because the differential cannot judge a fragment and would be
    ///         wrong rather than uninformative if it tried.</b> <c>--tw-gradient-from</c> set on an
    ///         element by itself moves nothing, in every scene, at every value — not because the
    ///         engine ignores it but because it is not a declaration. It is half of one, and the other
    ///         half is on a different class. Calling that "inert" and demanding a line in
    ///         <c>InertProperties.txt</c> would put seven permanent entries in a file whose whole
    ///         design is that entries expire, and they would never expire: the day <c>#43</c> teaches
    ///         the draw list to paint a gradient, <c>background-image</c> starts moving <c>paint</c>
    ///         and the fragments still move nothing on their own.
    ///     </para>
    ///     <para>
    ///         <b>So a fragment's verdict is its assembler's, and the debt is recorded once on the
    ///         property that is actually owed.</b> <c>background-image</c> is judged the same way
    ///         everything else is and carries the one allow-list line; the seven fragments are
    ///         explained by it and carry none. That is strictly stronger than seven lines, because the
    ///         expiry still works — the run that makes <c>background-image</c> a consumer is the run
    ///         that fails on its exemption.
    ///     </para>
    /// </param>
    public sealed record Ledger(
        IReadOnlySet<string> Emitted,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Consumers,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Inert,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Composed
    );

    /// <summary>Which properties each fragment is assembled into, following <c>var()</c> through.</summary>
    /// <returns>Fragment to the non-fragment properties it reaches. Absent means nothing assembles it.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The one measurement here taken from the registry rather than from a frame, and the
    ///         only one that could be.</b> Everything else in this file asks what the engine did;
    ///         this asks what one declaration <i>says</i>, which is a question about text and has no
    ///         frame-shaped answer. It cannot be taken from <see cref="Emissions" /> either, for a
    ///         reason worth stating: those values are post-cascade, and the cascade has already
    ///         substituted every <c>var()</c> away — a probe element carrying only
    ///         <c>bg-linear-to-r</c> resolves its gradient entirely out of the fallbacks, so the
    ///         references this needs to see are gone by the time that method can report them.
    ///     </para>
    ///     <para>
    ///         <b>Transitive, because the references really do chain.</b> <c>via-*</c> puts the
    ///         three-stop list in <c>--tw-gradient-stops</c>, and only that list mentions
    ///         <c>--tw-gradient-via</c>; it is <c>background-image</c> that mentions the stop list. A
    ///         one-step reading would leave the two <c>via</c> fragments looking unassembled and
    ///         demand allow-list lines for them, which is the wrong answer arrived at honestly.
    ///     </para>
    ///     <para>
    ///         This is also the guard against the mechanism being used as a hiding place: a fragment
    ///         nothing references reaches no property, gets no explanation, and falls through to
    ///         <c>Inert</c> like anything else. <c>--rotate</c>, <c>--scale</c>, <c>--translate-x</c>,
    ///         <c>--translate-y</c> and <c>--blur</c> are not registered fragments at all and are
    ///         judged exactly as they were before — which is why their lines are still in the file.
    ///     </para>
    /// </remarks>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Assemblies() {
        var tokens = ThemeTokens.Parse(ProbeTheme);
        var declarations = new List<UtilityDeclaration>();
        var edges = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var utility in UtilityFamilies.Surface(tokens)) {
            if (!UtilityParser.TryParse(utility, out var parsed)
                || !UtilityFamilies.TryResolve(parsed, tokens, declarations)) {
                continue;
            }

            foreach (var declaration in declarations) {
                foreach (var fragment in UtilityComposition.Fragments) {
                    if (!declaration.Value.Contains($"var({fragment}", StringComparison.Ordinal)) {
                        continue;
                    }

                    if (!edges.TryGetValue(fragment, out var into)) {
                        edges[fragment] = into = new SortedSet<string>(StringComparer.Ordinal);
                    }

                    into.Add(declaration.Property);
                }
            }
        }

        // The initial values are references too, and they are the reason `from-accent bg-linear-to-r`
        // works with no `--tw-gradient-stops` ever emitted. Reading them here keeps the graph honest
        // about what a fragment reaches when nobody wrote the intermediate class.
        foreach (var fragment in UtilityComposition.Fragments) {
            var initial = UtilityComposition.InitialValueOf(fragment);

            foreach (var referenced in UtilityComposition.Fragments) {
                if (!initial.Contains($"var({referenced}", StringComparison.Ordinal)) {
                    continue;
                }

                if (!edges.TryGetValue(referenced, out var into)) {
                    edges[referenced] = into = new SortedSet<string>(StringComparer.Ordinal);
                }

                into.Add(fragment);
            }
        }

        var assembled = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var fragment in UtilityComposition.Fragments) {
            var reached = new SortedSet<string>(StringComparer.Ordinal);
            var pending = new Queue<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal) { fragment };
            pending.Enqueue(fragment);

            while (pending.Count > 0) {
                if (!edges.TryGetValue(pending.Dequeue(), out var into)) {
                    continue;
                }

                foreach (var property in into) {
                    if (!seen.Add(property)) {
                        continue;
                    }

                    if (UtilityComposition.IsFragment(property)) {
                        pending.Enqueue(property);
                    } else {
                        reached.Add(property);
                    }
                }
            }

            if (reached.Count > 0) {
                assembled[fragment] = [.. reached];
            }
        }

        return assembled;
    }

    static Ledger? ledger;

    /// <summary>Takes the measurement, once per test run.</summary>
    /// <returns>The ledger.</returns>
    /// <remarks>
    ///     ⚠ <b>Every value a property can be given, not one of them</b>, and the verdict is the union.
    ///     <c>overflow-x: visible</c> is the initial value and changes nothing; <c>overflow-x: hidden</c>
    ///     clips. A gate that probed one value per property would call the axis inert or not depending
    ///     on which keyword happened to sort first, which is the same class of accident as measuring a
    ///     family by one example class.
    /// </remarks>
    public static Ledger Take() {
        lock (Gate) {
            if (ledger is not null) {
                return ledger;
            }
        }

        var byProperty = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var byUtility = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var (property, value, utility) in Emissions()) {
            if (!byProperty.TryGetValue(property, out var values)) {
                byProperty[property] = values = new SortedSet<string>(StringComparer.Ordinal);
                byUtility[property] = new SortedSet<string>(StringComparer.Ordinal);
            }

            values.Add(value);
            byUtility[property].Add(utility);
        }

        var consumers = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var inert = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var composed = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var assemblies = Assemblies();

        foreach (var (property, values) in byProperty) {
            // A fragment that something assembles is not judged on its own. It cannot be: it is half
            // a declaration, and setting half a declaration moves nothing by construction. One that
            // nothing assembles falls through and is judged like anything else, which is what stops
            // this from being a way to launder an unread property into silence.
            if (assemblies.TryGetValue(property, out var into)) {
                composed[property] = into;
                continue;
            }

            var channels = new List<string>();

            foreach (var value in values) {
                foreach (var channel in Channels(property, value)) {
                    if (!channels.Contains(channel)) {
                        channels.Add(channel);
                    }
                }

                if (channels.Count == 4) {
                    break;
                }
            }

            channels.Sort(StringComparer.Ordinal);

            if (channels.Count > 0) {
                consumers[property] = channels;
            } else {
                inert[property] = [.. byUtility[property]];
            }
        }

        var taken = new Ledger(
            byProperty.Keys.ToHashSet(StringComparer.Ordinal),
            consumers,
            inert,
            composed
        );

        lock (Gate) {
            return ledger ??= taken;
        }
    }

    static void Note(List<string> channels, string name, string plain, string probed) {
        if (!string.Equals(plain, probed, StringComparison.Ordinal) && !channels.Contains(name)) {
            channels.Add(name);
        }
    }

    /// <summary>Builds a scene, runs it, mutates it, and writes down everything that happened.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The frames after the mutation are what make transitions observable at all.</b>
    ///         Declaring a transition changes nothing on its own — it changes what happens the next
    ///         time a value moves — so the scene has to move one. <c>#probe.moved</c> is that move, and
    ///         the three frames after it are sampled individually rather than only at the end, because
    ///         a transition and its absence agree about where the value finishes and disagree only in
    ///         between.
    ///     </para>
    ///     <para>
    ///         The injected declaration is appended last at the same specificity as the scene's own
    ///         <c>#probe</c> rule, so document order decides and the injection wins — which is the same
    ///         way a generated utility beats a hand-written rule of equal weight.
    ///     </para>
    /// </remarks>
    static SceneSignature Run(ProbeScene scene, string? declaration) {
        using var document = new UiDocument(240f, 160f);

        Typeset(document);

        var css = new StringBuilder(Common).AppendLine().Append(scene.Css);

        if (declaration is not null) {
            css.AppendLine().Append("#probe { ").Append(declaration).Append(" }");
        }

        document.Load(css.ToString(), StyleOrigin.Author);

        var host = document.Create("div", document.Root, "host");

        // In the scrolled scene `#probe` is a `ScrollView` rather than a `div`, and the shared
        // children go into its content rather than into it: a `ScrollView`'s own children are its
        // parts, and a kid added beside `scroll-content` is a kid that does not scroll.
        ScrollView? outer = null;
        ScrollView? inner = null;
        UiElement? mark = null;

        UiElement probe;
        UiElement body;

        if (scene.Scrolling) {
            outer = host.Add<ScrollView>(null, "outer");
            document.Create("div", outer.Content, "lead");

            inner = outer.Content.Add<ScrollView>(null, "probe");
            document.Create("div", outer.Content, "trail");

            probe = inner;
            body = inner.Content;
        } else {
            probe = document.Create("div", host, "probe");
            body = probe;
        }

        document.Create("div", body, null, "kid");
        document.Create("div", body, null, "kid");
        document.Create("div", body, "wide");

        var label = document.Create("span", body, "label");
        label.Text = "Ag jq Wm il";

        var short_ = document.Create("span", body, "short");
        short_.Text = "Ag";

        // One word, no space, no hyphen, no break opportunity anywhere inside it — so UAX#14 offers
        // the wrapper nothing between its first character and its last, and the only way it fits is
        // the one `overflow-wrap` asks for. Latin letters rather than a long number, because LB25
        // does find opportunities inside a numeric run.
        if (scene.Unbroken) {
            var long_ = document.Create("span", body, "long");
            long_.Text = "Wmilqjagwmilqjag";
        }

        // Digits, in the one face here that has numeric features. Tabular figures are a *different
        // width* from proportional ones and old-style figures a different height, so every keyword
        // of `font-variant-numeric` moves the layout and the paint of this span — and none of them
        // could move anything in a string of letters, whatever the face.
        if (scene.Figured) {
            var figures = document.Create("span", body, "figures");
            figures.Text = "0123456789";
        }

        // The focused field. `Focus` is called after the value is set rather than before, because a
        // field with no value takes the other caret branch and the branch is chosen at draw time
        // from whatever the value is then — see the scene's own remark.
        if (scene.Edited) {
            var field = body.Add<TextBox>(null, "field");
            field.Value = "Ag";
            document.Focus(field);
        }

        // ⚠ <b>An icon, because `fill` and `stroke` have no other observable and a scene without one
        // measures them inert however well the engine reads them.</b> A child of the probe rather
        // than the probe itself, deliberately: both properties inherit, and an injected declaration
        // lands on `#probe`, so this also measures the half of the feature that is the inheritance —
        // `fill-accent` is written on a button and read on the icon inside it, essentially always.
        //
        // Both paints are `Foreground`, which is SVG's `currentColor` marker and the only kind CSS
        // overrides: a `Literal` would be an icon the properties are supposed to leave alone, and a
        // `None` stroke would leave `stroke` with nothing to move. One path carrying both slots is
        // enough, and cheaper than two.
        var art = body.Add<Icon>();

        art.Art = new IconArt(
            new IconPath(
                new PathBuilder().AddRectangle(new Rectangle(4f, 4f, 16f, 16f)),
                IconPaint.Foreground,
                IconPaint.Foreground,
                2f
            )
        );

        if (scene.Scrolling) {
            document.Create("div", body, "above");
            mark = document.Create("div", body, "mark");
            document.Create("div", body, "below");
        }

        document.Create("div", host, "after");

        var order = new List<UiElement>();
        Walk(document.Root, order);

        var layout = new StringBuilder();
        var paint = new StringBuilder();
        var cursor = new StringBuilder();
        var hit = new StringBuilder();

        var now = TimeSpan.Zero;

        for (var frame = 0; frame < 2; frame++) {
            now += TimeSpan.FromMilliseconds(16);
            document.Tick(now);
            document.Update();
            document.Draw();
        }

        // ⚠ The extra frame is inside the branch, so every other scene's frame count is exactly what
        // it was. Adding one globally would have re-timed all thirteen existing baselines — every
        // transition would be sampled a frame further along — and quietly re-measured properties this
        // change has nothing to do with.
        if (scene.Scrolling) {
            Approach(0);

            now += TimeSpan.FromMilliseconds(16);
            document.Tick(now);
            document.Update();
            document.Draw();
        }

        Record();

        probe.AddClass("moved");

        if (scene.Scrolling) {
            Approach(1);
        }

        for (var frame = 0; frame < 3; frame++) {
            if (scene.Scrolling && frame == 2) {
                Approach(2);
            }

            now += TimeSpan.FromMilliseconds(16);
            document.Tick(now);
            document.Update();
            document.Draw();
            Record();
        }

        return new SceneSignature(layout.ToString(), paint.ToString(), cursor.ToString(), hit.ToString());

        // One of the three scroll approaches the `scrolled` scene needs. See that scene's remark for
        // why there are three of them and why the wheel is last.
        void Approach(int phase) {
            if (outer is null || inner is null || mark is null) {
                return;
            }

            // ⚠ <b>Twice, and neither is optional.</b> `ScrollIntoView`, `MaximumTop` and `Bounds` are
            // every one of them answers about the *laid-out* tree, and a phase runs between frames —
            // so without the first pass they are read from the frame before the offsets were set, and
            // without the second they are read from before this phase's own move. Getting this wrong
            // does not throw: it silently approaches from the side the previous phase left it on, and
            // the eight properties whose branch never ran measure inert with the reader present.
            document.Update();

            switch (phase) {
                // From below-right: the outer is at the top, so `#probe` is past the bottom edge and
                // past the right one, and the far-edge branch of both axes runs. That is what makes
                // `scroll-margin-bottom`, `-right` and `-inline-end` observable.
                case 0:
                    outer.ScrollTop = 0f;
                    outer.ScrollLeft = 0f;

                    inner.ScrollTop = 0f;
                    inner.ScrollLeft = 0f;

                    document.Update();

                    outer.ScrollIntoView(inner);
                    inner.ScrollIntoView(mark);
                    break;

                // From above-left, which is the other branch and the other six properties. The
                // inner's scroll is also the one `scroll-behavior` eases, and it is started here
                // rather than in phase 0 because the three frames after this point are recorded
                // individually — a smooth scroll and an instant one agree about where the offset
                // finishes and differ only on the way.
                case 1:
                    outer.ScrollTop = outer.MaximumTop;
                    outer.ScrollLeft = outer.MaximumLeft;

                    inner.ScrollTop = inner.MaximumTop;
                    inner.ScrollLeft = inner.MaximumLeft;

                    document.Update();

                    outer.ScrollIntoView(inner);
                    inner.ScrollIntoView(mark);
                    break;

                // The wheel, over an inner that has no room left. Whether the outer moves is the
                // whole of what `overscroll-behavior` decides, and a nested pair at its stop is the
                // only arrangement in which the question is asked.
                default:
                    // ⚠ At the stop, and it has to be *exactly* at it. A view with one pixel left
                    // scrolls that pixel, reports the wheel handled, and never asks
                    // `overscroll-behavior` anything — so the property measures inert for want of a
                    // clamp rather than for want of a reader. That is why the max is read after a
                    // pass rather than from the frame before.
                    // ⚠ <b>The outer is scrolled to the inner rather than to a pair of numbers, and
                    // the numbers are what this was written as first.</b> The wheel is hit-tested and
                    // the outer clips, so it has to land on an inner that is actually on screen — and
                    // where that is cannot be hardcoded, because `#probe.moved` in `Common` sets a
                    // `margin-left` of its own and out-specifies the scene's. A fixed reveal aimed at
                    // where the inner used to be hits the empty part of the outer, nothing chains
                    // whatever `overscroll-behavior` says, and all three of its longhands measure
                    // inert — with a reader present and correct, which is the exact false gap this
                    // file's own opening warns about.
                    outer.ScrollTop = 0f;
                    outer.ScrollLeft = 0f;

                    document.Update();

                    outer.ScrollIntoView(inner);

                    inner.ScrollTop = inner.MaximumTop;
                    inner.ScrollLeft = inner.MaximumLeft;

                    document.Update();

                    var over = inner.Bounds;

                    document.Dispatch(new WheelEvent {
                        X = over.X + over.Width * 0.5f,
                        Y = over.Y + over.Height * 0.5f,
                        DeltaX = 40f,
                        DeltaY = 40f
                    });

                    break;
            }
        }

        void Record() {
            foreach (var element in order) {
                layout.Append(element.AbsoluteLeft).Append(',')
                    .Append(element.AbsoluteTop).Append(',')
                    .Append(element.Width).Append(',')
                    .Append(element.Height).Append(';');
            }

            layout.Append('|');

            var drawing = document.Drawing;

            foreach (var command in drawing.Commands) {
                paint.Append(command).Append(';');
            }

            foreach (var glyph in drawing.Glyphs) {
                paint.Append(glyph).Append(';');
            }

            foreach (var segment in drawing.Segments) {
                paint.Append(segment).Append(';');
            }

            foreach (var box in drawing.Boxes) {
                paint.Append(box).Append(';');
            }

            // ⚠ <b>And the masks, for the boxes' reason and with a sharper edge on it.</b> A
            // `LayerPush` names a range of `DrawList.Masks` and carries nothing of the entries
            // themselves, so a declaration that changes only what is *in* that range leaves every
            // command in the frame byte-identical. `mask-composite` is exactly that declaration —
            // it moves one field of each entry and not the range — and it measured inert with
            // `DrawListBuilder.Composites` reading it perfectly, on a scene built to make it visible.
            foreach (var mask in drawing.Masks) {
                paint.Append(mask).Append(';');
            }

            // ⚠ <b>The faces, which <see cref="DrawList.Differs" /> deliberately does not compare and
            // which this has to.</b> That method's reasoning is that a command drawn in a different
            // face refers to it by a different index — true across a frame that uses several faces,
            // and false for the only frame that matters here, where one face is swapped for another
            // and both are index zero. It cost a run: `font-weight` reached the registry, picked the
            // bold face, and read as inert because nothing in the signature could tell.
            foreach (var font in drawing.Fonts) {
                paint.Append(font.Name).Append(';');
            }

            paint.Append('|');

            // The probe and one of its text children, because `cursor` inherits: a family that set it
            // on a box and did not reach the text inside would be a real difference.
            cursor.Append(document.CursorOf(probe.Style)).Append(',')
                .Append(document.CursorOf(label.Style)).Append('|');

            // A grid rather than one point. `pointer-events: none` on the probe hands the hit to
            // whatever is behind it, and which points change says whose box it was.
            for (var y = 4f; y < 100f; y += 12f) {
                for (var x = 4f; x < 160f; x += 12f) {
                    var element = document.HitTest(x, y);
                    hit.Append(element is null ? -1 : order.IndexOf(element)).Append(',');
                }
            }

            hit.Append('|');
        }
    }

    /// <summary>Gives a document two faces of one family, so that text and weights are measurable.</summary>
    /// <remarks>
    ///     ⚠ <b>Two <see cref="Vixen.Ui.Text.FontFace" /> objects over the same bytes, and the
    ///     duplication is the measurement.</b> Registering one face at both weights would make
    ///     <see cref="FontRegistry.Resolve" /> return the same object either way, and
    ///     <c>font-weight</c> would move nothing — not because the engine ignores it but because there
    ///     was nothing for it to choose between. What is under test here is whether the weight reaches
    ///     the registry, not whether the glyphs get heavier, and a second distinct face is the smallest
    ///     thing that can tell those apart.
    /// </remarks>
    /// <remarks>
    ///     ⚠ <b>And a third face for the slant, which is the same measurement one axis over and was
    ///     the whole of why <c>font-style</c> read as inert.</b> <c>UiDocument.FontStyleOf</c> reads
    ///     the property and <c>FontRegistry.Slanted</c> implements CSS Fonts 4 § 5.2's italic →
    ///     oblique → upright search in full — but a family with no italic variant resolves
    ///     <c>italic</c> to its upright by that search's own last resort, correctly and invisibly. So
    ///     the property measured inert with a finished reader and a finished matcher, for want of
    ///     anything to match. This repository has no italic font in it; a distinct
    ///     <see cref="Vixen.Ui.Text.FontFace" /> over the same bytes is what the weight above uses and
    ///     is exactly as much as the question needs, because what is under test is whether the slant
    ///     reaches the registry and not whether the glyphs lean.
    /// </remarks>
    static void Typeset(UiDocument document) {
        document.Fonts.Register("Probe", Regular, 400);
        document.Fonts.Register("Probe", Bold, 700);
        document.Fonts.Register("Probe", Italic, 400, FontStyle.Italic);

        // ⚠ A second *family*, and it is only ever asked for by the `figured` scene. Registering it
        // under `Probe` would have changed which face draws every string in every scene, which is
        // thirteen re-baselined signatures for the sake of one property — the same argument the
        // `overlong` scene's flag makes one level up.
        document.Fonts.Register("Figured", Figures, 400);
        document.Fonts.Default = Regular;
    }

    static readonly Text.FontFace Regular = LoadFont("regular");
    static readonly Text.FontFace Bold = LoadFont("bold");
    static readonly Text.FontFace Italic = LoadFont("italic");

    /// <summary>Open Sans, the only face here with the features <c>font-variant-numeric</c> names.</summary>
    static readonly Text.FontFace Figures = LoadFont("figures", "OpenSans-Regular.ttf");

    /// <summary>The same face, for the one test that measures the keywords one at a time.</summary>
    /// <remarks>
    ///     ⚠ Exposed rather than loaded twice, because a <see cref="Text.FontFace" /> is a parse and a
    ///     native allocation and two of them would also be two shaping caches' worth of glyph ids that
    ///     mean nothing to each other. <c>NumericFigureVisibilityTests</c> is the caller: it asks
    ///     which of the nine keywords this face can actually show, which is a fact about the scene
    ///     list's coverage and therefore belongs beside it.
    /// </remarks>
    public static Text.FontFace FiguredFace => Figures;

    static Text.FontFace LoadFont(string name, string file = "TestShapeLana.ttf") {
        using var stream = System.Reflection.Assembly.GetExecutingAssembly()
                .GetManifestResourceStream($"Vixen.Ui.Styling.Utilities.Tests.Fonts.{file}")
            ?? throw new InvalidOperationException($"the test font '{file}' is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return Text.FontFace.Load(memory.ToArray(), name: name);
    }

    static void Walk(UiElement element, List<UiElement> into) {
        into.Add(element);

        foreach (var child in element.Children) {
            Walk(child, into);
        }
    }
}
