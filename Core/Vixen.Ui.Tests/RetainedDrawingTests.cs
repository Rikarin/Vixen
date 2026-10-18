// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>Retained drawing (#671): what an unchanged subtree costs, and that it costs it honestly.</summary>
/// <remarks>
///     <para>
///         Two properties, each held from both sides. <b>Work:</b> a still document builds no command
///         after its first frame, and one changed label builds its own card and nothing beside it —
///         counted in commands replayed against commands emitted, never in time. <b>Identity:</b> the
///         list a retaining builder finishes is the list a full rebuild finishes, byte for byte,
///         across hundreds of randomised mutations of the same tree.
///     </para>
///     <para>
///         ⚠ <b>The differential turns the Debug verifier off on the retaining side.</b> With it on,
///         a stale replay throws before the comparison ever runs, and the test would be measuring
///         the verifier rather than the replay a Release build ships.
///     </para>
/// </remarks>
public class RetainedDrawingTests {
    const int Cards = 12;

    const string Sheet = """
        root { width: 400px; height: 300px; display: flex; flex-wrap: wrap; align-content: flex-start; }
        card { width: 80px; height: 40px; background-color: #345; border: 2px solid #f00; padding: 2px; }
        label { color: #fff; font-size: 12px; }
        .rounded { border-radius: 4px 8px 2px 0px; }
        .faded { opacity: 0.5; }
        .nudged { margin-left: 7px; }
        .wide { width: 120px; }
        .masked { mask-image: linear-gradient(black, transparent); }
        .turned { transform: rotate(10deg); }
        .shadowed { box-shadow: 2px 2px 4px #000; }
        .hidden { visibility: hidden; }
        .gone { display: none; }
        .dashed { border-style: dashed; }
        .clipped { overflow: hidden; }
        .blended { mix-blend-mode: multiply; }
        .ringed { outline: 2px dotted #0f0; }
        .graded { background-image: linear-gradient(90deg, #f00, #00f); }
        .tiny { font-size: 7px; }
        .sunk { z-index: -1; position: relative; }
        dial { width: 10px; height: 10px; }
        """;

    static readonly string[] Classes = [
        "rounded", "faded", "nudged", "wide", "masked", "turned", "shadowed", "hidden", "gone", "dashed",
        "clipped", "blended", "ringed", "graded", "tiny", "sunk"
    ];

    static readonly string[] Words = ["", "a", "label", "a longer label", "wrapped over two lines of text", "zz"];

    static readonly Text.FontFace Face = LoadFace();

    static Text.FontFace LoadFace() {
        using var stream = System.Reflection.Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Tests.Fonts.TestShapeLana.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return Text.FontFace.Load(memory.ToArray(), name: "TestShapeLana");
    }

    static UiDocument Board(bool retaining) {
        var document = new UiDocument(400f, 300f);
        document.Fonts.Register("Test", Face);
        document.RetainsDrawing = retaining;
        document.Load(Sheet);

        for (var index = 0; index < Cards; index++) {
            var card = document.Root.Add("card");
            card.Add("label").Text = $"card {index}";
        }

        return document;
    }

    static void Frame(UiDocument document) {
        document.Update();
        document.Draw();
    }

    static long Built(UiDocument document) =>
        document.Diagnostics.DrawCommandsEmitted - document.Diagnostics.DrawCommandsReplayed;

    [Fact]
    public void A_still_document_builds_nothing_after_its_first_frame() {
        using var document = Board(retaining: true);

        Frame(document);

        var picture = document.Drawing.Commands.Count;

        Assert.True(picture > Cards * 2, $"the board drew only {picture} commands");
        Assert.Contains(document.Drawing.Commands, command => command.Kind == DrawCommandKind.Text);
        Assert.Equal(picture, Built(document));

        for (var frame = 1; frame < 30; frame++) {
            Frame(document);
        }

        Assert.Equal(picture * 29L, document.Diagnostics.DrawCommandsReplayed);
        Assert.Equal(picture, Built(document));
    }

    /// <summary>The other half: the same board rebuilt in full replays nothing.</summary>
    [Fact]
    public void A_board_that_does_not_retain_builds_its_whole_picture_every_frame() {
        using var document = Board(retaining: false);

        for (var frame = 0; frame < 30; frame++) {
            Frame(document);
        }

        Assert.Equal(0L, document.Diagnostics.DrawCommandsReplayed);
        Assert.Equal(document.Drawing.Commands.Count * 30L, Built(document));
    }

    [Fact]
    public void One_changed_label_builds_its_own_card_and_nothing_beside_it() {
        using var document = Board(retaining: true);
        using var twin = Board(retaining: false);

        Frame(document);
        Frame(document);

        var picture = document.Drawing.Commands.Count;
        var before = Built(document);

        document.Root.Children[5].Children[0].Text = "changed";
        twin.Root.Children[5].Children[0].Text = "changed";
        Frame(document);
        Frame(twin);

        var built = Built(document) - before;

        // A card is a background, a border and its label's text; the root has nothing of its own.
        // So the one card's few commands are rebuilt and the other eleven cards are replayed.
        Assert.InRange(built, 1L, picture / Cards + 1);
        Assert.Equal(twin.Drawing.Commands, document.Drawing.Commands);
        Assert.Equal(twin.Drawing.Glyphs, document.Drawing.Glyphs);
    }

    /// <summary>An element whose type draws itself is never replayed, and neither is its parent.</summary>
    [Fact]
    public void A_custom_drawn_element_and_its_ancestors_are_built_every_frame() {
        using var document = Board(retaining: true);
        var drawn = 0;

        var dial = document.Root.Children[3].Add<Dial>("dial");
        dial.Draws = context => {
            drawn++;
            context.FillRectangle(new Rectangle(context.Bounds.X, context.Bounds.Y, 4f, 4f), new Color4(drawn % 2, 0f, 0f, 1f));
        };

        Frame(document);
        var before = Built(document);
        Frame(document);

        // Its card's background, border and label, and the dial's own square, are built again; the
        // other eleven cards are replayed.
        Assert.Equal(2, drawn);
        Assert.InRange(Built(document) - before, 2L, document.Drawing.Commands.Count / Cards + 2);
        Assert.Contains(document.Drawing.Commands, command => command.Color.R > 0.5f);
    }

    /// <summary>
    ///     ⚠ A translucent group whose subtree comes to one command is collapsed: the push is removed
    ///     and the one command faded in place. A child replayed out of that frame would carry the
    ///     fade already, and the parent — rebuilt for a change that paints nothing — fades it again.
    /// </summary>
    [Fact]
    public void A_group_that_collapses_onto_a_replayed_child_fades_it_once() {
        using var retained = Collapsing(retaining: true);
        using var rebuilt = Collapsing(retaining: false);
        retained.VerifiesRetainedDrawing = false;

        for (var frame = 0; frame < 2; frame++) {
            Frame(retained);
            Frame(rebuilt);
        }

        // A change to the wrapper that paints nothing: the wrapper is rebuilt, its label is not.
        retained.Root.Children[0].SetStyle("outline-offset", "3px");
        rebuilt.Root.Children[0].SetStyle("outline-offset", "3px");
        Frame(retained);
        Frame(rebuilt);

        var text = Assert.Single(rebuilt.Drawing.Commands);
        Assert.Equal(DrawCommandKind.Text, text.Kind);
        Assert.True(text.Color.A < 1f, "the fixture's group did not collapse onto its label");
        Same(retained, rebuilt, "after the wrapper changed");
    }

    static UiDocument Collapsing(bool retaining) {
        var document = new UiDocument(200f, 100f);
        document.Fonts.Register("Test", Face);
        document.RetainsDrawing = retaining;
        document.Load("""
            root { width: 200px; height: 100px; }
            wrap { width: 100px; height: 20px; opacity: 0.5; }
            label { color: #fff; font-size: 12px; }
            """);

        document.Root.Add("wrap").Add("label").Text = "faded";
        return document;
    }

    sealed class Dial : UiElement {
        public Action<DrawContext>? Draws { get; set; }

        protected internal override void OnDraw(DrawContext context) => Draws?.Invoke(context);
    }

    /// <summary>
    ///     ⚠ <b>The identity half, and the one that can see a missing input.</b> The same tree is
    ///     mutated the same way in a retaining document and a rebuilding one; after every step the
    ///     two finished lists must agree in every command and every side-buffer entry.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void A_retained_frame_is_the_frame_a_full_rebuild_draws_through_random_mutations(int seed) {
        using var retained = Board(retaining: true);
        using var rebuilt = Board(retaining: false);
        retained.VerifiesRetainedDrawing = false;

        var random = new Random(seed);
        var pairs = new List<(UiElement Retained, UiElement Rebuilt)>();
        Collect(retained.Root, rebuilt.Root, pairs);

        Frame(retained);
        Frame(rebuilt);
        Same(retained, rebuilt, "the first frame");

        var replayedSteps = 0;

        for (var step = 0; step < 150; step++) {
            var replayedBefore = retained.Diagnostics.DrawCommandsReplayed;
            var what = Mutate(random, pairs, retained, rebuilt);

            Frame(retained);
            Frame(rebuilt);
            Same(retained, rebuilt, $"step {step}: {what}");

            if (retained.Diagnostics.DrawCommandsReplayed > replayedBefore) {
                replayedSteps++;
            }
        }

        // ⚠ The other half: a retaining document that never replayed would pass every comparison
        // above trivially.
        Assert.True(replayedSteps > 75, $"only {replayedSteps} of 150 steps replayed anything");
    }

    static void Collect(UiElement retained, UiElement rebuilt, List<(UiElement, UiElement)> into) {
        into.Add((retained, rebuilt));

        for (var index = 0; index < retained.Children.Count; index++) {
            Collect(retained.Children[index], rebuilt.Children[index], into);
        }
    }

    static string Mutate(Random random, List<(UiElement Retained, UiElement Rebuilt)> pairs, UiDocument a, UiDocument b) {
        var (left, right) = pairs[random.Next(pairs.Count)];
        var kind = random.Next(9);

        // ⚠ The root only ever gains a card. A `display: none` or an `opacity: 0` on it draws nothing
        // at all for as long as it lasts, which passes every comparison below and replays nothing —
        // the half of this test that says the comparison was not trivial.
        if (left == a.Root && kind != 6) {
            return "nothing";
        }

        switch (kind) {
            case 0:
            case 1: {
                var name = Classes[random.Next(Classes.Length)];

                if (left.HasClass(name)) {
                    left.RemoveClass(name);
                    right.RemoveClass(name);
                    return $"-{name} on {left.Tag}";
                }

                left.AddClass(name);
                right.AddClass(name);
                return $"+{name} on {left.Tag}";
            }

            case 2: {
                // Text only on a label: an element with children cannot carry it.
                if (left.Tag != "label") {
                    return "nothing";
                }

                var word = Words[random.Next(Words.Length)];
                left.Text = word;
                right.Text = word;
                return $"text '{word}' on {left.Tag}";
            }

            case 3: {
                var colour = $"#{random.Next(0x1000):x3}";
                left.SetStyle("background-color", colour);
                right.SetStyle("background-color", colour);
                return $"background {colour} on {left.Tag}";
            }

            case 4: {
                var height = $"{random.Next(0, 60)}px";
                left.SetStyle("height", height);
                right.SetStyle("height", height);
                return $"height {height} on {left.Tag}";
            }

            case 5: {
                if (left == a.Root) {
                    return "nothing";
                }

                var gone = new List<(UiElement, UiElement)>();
                Collect(left, right, gone);

                left.Remove();
                right.Remove();
                pairs.RemoveAll(pair => gone.Exists(removed => removed.Item1 == pair.Retained));
                return $"removed a {left.Tag}";
            }

            case 6: {
                // A card under the root, a label under a card: the only places either may go.
                var tag = left == a.Root ? "card" : left.Tag == "card" ? "label" : null;

                if (tag is null) {
                    return "nothing";
                }

                var added = (left.Add(tag), right.Add(tag));
                pairs.Add(added);

                if (tag == "card") {
                    added = (added.Item1.Add("label"), added.Item2.Add("label"));
                    pairs.Add(added);
                }

                added.Item1.Text = "new";
                added.Item2.Text = "new";
                return $"added a {tag} under {left.Tag}";
            }

            case 7: {
                if (left.Parent is not { } parent || parent.Children.Count < 2) {
                    return "nothing";
                }

                var index = random.Next(parent.Children.Count);
                a.Move(left, index);
                b.Move(right, index);
                return $"moved a {left.Tag} to {index}";
            }

            default: {
                var opacity = random.Next(0, 5) / 4f;
                var text = opacity.ToString(System.Globalization.CultureInfo.InvariantCulture);
                left.SetStyle("opacity", text);
                right.SetStyle("opacity", text);
                return $"opacity {text} on {left.Tag}";
            }
        }
    }

    static void Same(UiDocument retained, UiDocument rebuilt, string when) {
        var left = retained.Drawing;
        var right = rebuilt.Drawing;

        Assert.True(
            left.Commands.SequenceEqual(right.Commands),
            $"{when}: the commands differ ({left.Commands.Count} retained, {right.Commands.Count} rebuilt)"
        );
        Assert.True(left.Glyphs.SequenceEqual(right.Glyphs), $"{when}: the glyphs differ");
        Assert.True(left.Segments.SequenceEqual(right.Segments), $"{when}: the path segments differ");
        Assert.True(left.Boxes.SequenceEqual(right.Boxes), $"{when}: the box styles differ");
        Assert.True(left.Masks.SequenceEqual(right.Masks), $"{when}: the masks differ");
        Assert.True(left.Fonts.SequenceEqual(right.Fonts), $"{when}: the fonts differ");
        Assert.Equal(left.Version, right.Version);
    }
}
