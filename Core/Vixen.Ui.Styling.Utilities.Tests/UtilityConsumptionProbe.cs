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
sealed record ProbeScene(string Name, string Css);

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
        // longhand, and that asymmetry is load-bearing — do not tidy it.</b> `ApplyPlacements` applies
        // the shorthand first and then overwrites it from the longhands whenever they are present, so
        // a longhand beats a shorthand *whatever order they were declared in*. With `grid-row-start: 3`
        // here, every `grid-row` this gate injected was silently discarded and the property measured
        // inert while the engine read it perfectly. Declaring the row through the shorthand is what
        // leaves the injected shorthand something to replace.
        //
        // That ordering is a real defect and not only a fact about this scene — `grid-row: 1 / -1` is
        // dropped on any element some other rule gave a `grid-row-start` — but it is filed rather than
        // fixed here, because changing the bridge's precedence is not a change to a test fixture.
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
    ///     ⚠ <b>Through the generator and the loader rather than through
    ///     <c>UtilityFamilies.TryResolve</c> directly</b>, so that the answer is what a project's real
    ///     generated sheet would put in the cascade: escaped selectors, <c>@layer utilities</c>, and
    ///     ExCSS's shorthand expansion all included. Resolving the declarations by hand would measure
    ///     the registry's intentions, and the registry's intentions are not what a consumer interns.
    /// </remarks>
    public static IReadOnlyList<(string Property, string Value, string Utility)> Emissions() {
        var tokens = ThemeTokens.Parse(ProbeTheme);
        var generator = new UtilityGenerator(tokens);
        var emissions = new List<(string, string, string)>();

        foreach (var utility in UtilityFamilies.Surface(tokens)) {
            using var document = new UiDocument(200f, 100f);
            document.Load(generator.Generate([utility]), StyleOrigin.Author);

            var probe = document.Create("div", document.Root, null, utility);
            var bare = document.Create("div", document.Root);

            document.Update();

            var names = document.Styles.Properties;
            var values = document.Styles.Values;
            var baseline = new HashSet<int>();

            foreach (var id in bare.Style.Properties) {
                baseline.Add(id);
            }

            var properties = probe.Style.Properties;
            var held = probe.Style.Values;

            for (var i = 0; i < properties.Length; i++) {
                if (!baseline.Contains(properties[i])) {
                    emissions.Add((names.NameOf(properties[i]), values.NameOf(held[i]), utility));
                }
            }
        }

        return emissions;
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
        var probe = document.Create("div", host, "probe");

        document.Create("div", probe, null, "kid");
        document.Create("div", probe, null, "kid");
        document.Create("div", probe, "wide");

        var label = document.Create("span", probe, "label");
        label.Text = "Ag jq Wm il";

        var short_ = document.Create("span", probe, "short");
        short_.Text = "Ag";

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
        var art = probe.Add<Icon>();

        art.Art = new IconArt(
            new IconPath(
                new PathBuilder().AddRectangle(new Rectangle(4f, 4f, 16f, 16f)),
                IconPaint.Foreground,
                IconPaint.Foreground,
                2f
            )
        );

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

        Record();

        probe.AddClass("moved");

        for (var frame = 0; frame < 3; frame++) {
            now += TimeSpan.FromMilliseconds(16);
            document.Tick(now);
            document.Update();
            document.Draw();
            Record();
        }

        return new SceneSignature(layout.ToString(), paint.ToString(), cursor.ToString(), hit.ToString());

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
    static void Typeset(UiDocument document) {
        document.Fonts.Register("Probe", Regular, 400);
        document.Fonts.Register("Probe", Bold, 700);
        document.Fonts.Default = Regular;
    }

    static readonly Text.FontFace Regular = LoadFont("regular");
    static readonly Text.FontFace Bold = LoadFont("bold");

    static Text.FontFace LoadFont(string name) {
        using var stream = System.Reflection.Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("Vixen.Ui.Styling.Utilities.Tests.Fonts.TestShapeLana.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

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
