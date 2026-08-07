// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Ui;
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
    public const string ProbeTheme = """
        theme:
          colors:
            paint: "#3366cc"
          spacing:    { base: 4 }
          radius:     { probe: 6 }
          fontSize:   { probe: [13, 20] }
          fontWeight: { probe: 700 }
          screens:    { probe: 640 }
          shadow:
            probe: "0px 3px 6px rgba(0, 0, 0, 0.5)"
        darkMode: media
        content: []
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
        // function has something to be the duration *of*. The scene where they are switched off is
        // the one in which `transition-property` itself is visible; neither can be both.
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
    public sealed record Ledger(
        IReadOnlySet<string> Emitted,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Consumers,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Inert
    );

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

        foreach (var (property, values) in byProperty) {
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
            inert
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
