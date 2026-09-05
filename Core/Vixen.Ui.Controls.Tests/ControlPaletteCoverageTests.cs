// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Reflection;
using Vixen.Core.Imaging;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>
///     Every public element type in <c>Vixen.Ui.Controls</c> that paints anything repaints under
///     <c>root.dark</c>, or has a written reason for painting nothing.
/// </summary>
/// <remarks>
///     <para>
///         <b>What the theme suites cannot see.</b> <c>ControlVisualTests</c> commits thirty-nine
///         baselines and every one of them is drawn in the light palette;
///         <c>ControlThemeVisualTests</c> renders both palettes for six controls and asserts over the
///         whole gallery frame. So for every control outside that six, "this one ignores the
///         palette" is a state neither suite can report — which is not hypothetical, because the
///         slider thumb painted <c>#ffffff</c> on a <c>#ffffff</c> surface (#594) is exactly that
///         defect and it was found by a person looking at the light gallery.
///     </para>
///     <para>
///         ⚠ <b>The refusal this is built around is right and is not being reopened.</b> #325 refused
///         "a golden per control at both themes" with evidence: <c>ControlTheme.vcss</c> holds one
///         <c>root.dark</c> block, it declares tokens and nothing else, so thirty-nine dark
///         references would be thirty-nine pictures of one substitution. ⚠ <b>But "every control is
///         dark by substitution" is a claim about the stylesheet, and the failure it cannot see is a
///         claim about a control</b> — a control that paints a colour not drawn from a token looks
///         identical in both palettes and no reference of the sheet's behaviour would move. That is
///         what this asserts, and it commits no picture: the property is closed-form and needs no
///         reviewer.
///     </para>
///     <para>
///         ⚠ <b>The ground is a literal, and that is the whole instrument.</b> If the frame behind
///         the control were painted from <c>--surface</c>, every control would pass by sitting on a
///         backdrop that repaints — including one that draws in a hard-coded colour, which is the
///         only thing this test exists to catch. So the root paints <see cref="Ground" />, a colour
///         no token has, and the sweep refuses to compare at all if the pixel outside the control
///         moved between the two renderings.
///     </para>
///     <para>
///         ⚠ <b>The measure is over the ink and not over the box.</b> "The frames differ over a
///         fraction of the control's box" is a threshold that has to be set for the loudest control
///         and then means nothing for a checkbox, whose tick is 361 pixels of a 6160-pixel box.
///         What is asserted instead is scale-free: of the pixels this control actually painted,
///         what share changed colour when the palette did. Every control in the assembly measures
///         <b>1.000</b> today — not 0.95, not 0.99 — so a control that stops responding does not
///         drift towards the threshold, it falls off a cliff.
///     </para>
///     <para>
///         ⚠ <b>The controls are seeded with a word, because a control with nothing in it paints
///         nothing.</b> A sweep over bare controls is a sweep over blank frames that passes
///         perfectly: before seeding, twenty of these types painted zero pixels and would each
///         have needed an exemption saying "empty", which is a table recording the fixture's
///         omissions rather than the assembly's decisions. The seed is one rule rather than a
///         fixture apiece — the string property the control's own type declares, and
///         <c>UiElement.Text</c> for the leaves. ⚠ <c>Text</c> only for a childless element: setting
///         it on a control that has built its parts is a node that has children and measures itself,
///         which the layout refuses outright.
///     </para>
///     <para>
///         ⚠ <b>Held to its own residue, on <c>AccessibilityCoverageTests</c>' terms.</b> An
///         exemption for a control that has since started painting fails too, so the table can only
///         shrink — otherwise it is a ratchet, and the day a popup learns to draw itself closed
///         nothing would say the reason had expired.
///     </para>
/// </remarks>
public class ControlPaletteCoverageTests {
    /// <summary>The frame, and the box every control is given inside it.</summary>
    const float Width = 200f;
    const float Height = 80f;

    /// <summary>
    ///     A colour no token in <c>ControlTheme.vcss</c> holds, painted behind every control.
    /// </summary>
    /// <remarks>
    ///     Its value does not matter and its <i>independence</i> is everything: it is what makes
    ///     "this pixel is the control's" decidable, and what stops the backdrop's own repaint being
    ///     counted as the control's. See the class remarks.
    /// </remarks>
    const string Ground = "#7f3f9f";

    /// <summary>
    ///     ⚠ A class rather than a tag, so it beats the theme's own sizing. A control left at its
    ///     theme size is one whose ink can be a handful of pixels, and the floor below would then be
    ///     measuring the stylesheet's padding.
    /// </summary>
    const string Css = $$"""
        root   { flex-direction: column; align-items: flex-start; background-color: {{Ground}}; }
        .probe { width: 140px; height: 44px; }
        """;

    /// <summary>What share of a control's own ink has to move when the palette does.</summary>
    /// <remarks>
    ///     ⚠ <b>Every control measures exactly 1.000 today</b>, so this is slack rather than a
    ///     tolerance — room for a future control with a deliberate fixed mark, kept far enough below
    ///     one that a control losing a whole rule to a literal cannot hide under it. A badge whose
    ///     fill went hard-coded measures 0.009.
    /// </remarks>
    const double MinimumRepaint = 0.9d;

    /// <summary>How much ink a control has to put down before it is asked to repaint it.</summary>
    /// <remarks>
    ///     The floor the issue asks for, and it is under the smallest real control rather than at
    ///     it: <c>Pagination</c> paints 80 pixels and a seeded label is 103. Its job is to fail on
    ///     the day the harness renders nothing — a blank frame compared with a blank frame agrees
    ///     with itself perfectly.
    /// </remarks>
    const int MinimumInk = 32;

    /// <summary>How many public element types this assembly is expected to offer, at least.</summary>
    /// <remarks>
    ///     Sixty today, and the floor is under it so that adding a control is not a failing
    ///     test — but not far under, because the number's whole purpose is to fail on the day the
    ///     reflection query stops matching. A filter that quietly matched nothing satisfies every
    ///     other assertion in this file.
    /// </remarks>
    const int Elements = 56;

    /// <summary>And how many of them are expected to paint and repaint.</summary>
    /// <remarks>
    ///     Fifty today. The second number is what stops the first being met by an assembly of
    ///     exempted types: a population that had been reverted wholesale would still build sixty
    ///     elements and paint none of them.
    /// </remarks>
    const int Repainting = 45;

    /// <summary>The elements that paint nothing on their own, and why each of them does not.</summary>
    /// <remarks>
    ///     ⚠ <b>A reason is prose on purpose</b>, for <c>AccessibilityCoverageTests</c>' reason: a
    ///     boolean would let a control be excused by somebody who did not have to say why. Seven of
    ///     these are surfaces that are closed until something opens them, and the other three are
    ///     containers with no ground of their own — in both cases what a user sees is painted by
    ///     something else that is in this sweep in its own right.
    /// </remarks>
    static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal) {
        ["ContextMenu"] = "closed until a pointer opens it; laid out at zero size, so there is no box to compare",
        ["Dialog"] = "closed until it is shown; its surface and its buttons are `card` and `button` when it is",
        ["Drawer"] = "closed until it is shown, and slides in over a scrim that is not the drawer",
        ["Menu"] = "a popup surface; closed until a menu bar item or a context gesture opens it",
        ["Popover"] = "a positioned surface with nothing in it until an anchor gives it content",
        ["RadialMenu"] = "closed until it is opened; the wedge that paints is `RadialItem`, which is swept",
        ["Tooltip"] = "closed until a hover delay opens it",
        ["ScrollView"] = "a viewport with no ground of its own; the scroll bars inside it are `ScrollBar`",
        ["VirtualizingPanel"] = "a windowed layout; the realised children carry whatever they paint",
        ["VirtualizingGrid"] = "ditto, in two dimensions"
    };

    /// <summary>The string properties a control is seeded through, in the order they are tried.</summary>
    static readonly string[] Wordy = ["Label", "Text", "Title", "Value", "Placeholder", "Header", "Caption", "Content"];

    [Fact]
    public void Every_control_that_paints_repaints_under_the_dark_palette() {
        var make = typeof(ControlPaletteCoverageTests)
            .GetMethod(nameof(Make), BindingFlags.NonPublic | BindingFlags.Static)!;

        var built = new List<string>();
        var repainting = new List<string>();
        var blank = new List<string>();
        var offenders = new List<string>();

        var types = typeof(Button).Assembly.GetTypes().OrderBy(static type => type.Name, StringComparer.Ordinal);

        foreach (var type in types) {
            if (!type.IsPublic || type.IsAbstract || !typeof(UiElement).IsAssignableFrom(type)) {
                continue;
            }

            // ⚠ Reported rather than skipped, for the sweep's own sake: a type reflection cannot
            // build is a hole exactly the size of a control, and a `continue` here is how a sweep
            // comes to cover less than it says while staying green.
            if (type.GetConstructor(Type.EmptyTypes) is null) {
                offenders.Add($"{type.Name} has no parameterless constructor, so the sweep cannot reach it");
                continue;
            }

            var (lit, box) = Render(make, type, dark: false);
            var (dim, _) = Render(make, type, dark: true);
            built.Add(type.Name);

            // ⚠ Before anything is counted. If the backdrop moved, every pixel of every control
            // "repainted" and the sweep would report a clean assembly whatever the controls did.
            if (!Same(lit, dim, lit.Width - 1, lit.Height - 1)) {
                offenders.Add(
                    $"{type.Name}: the ground outside the control changed between the palettes, so the "
                    + $"frame is not painted with {Ground} and nothing measured here means anything"
                );

                continue;
            }

            var ground = Colour(lit, lit.Width - 1, lit.Height - 1);
            var painted = 0;
            var changed = 0;

            for (var y = box.Top; y < box.Bottom; y++) {
                for (var x = box.Left; x < box.Right; x++) {
                    if (Same(lit, ground, x, y)) {
                        continue;
                    }

                    painted++;

                    if (!Same(lit, dim, x, y)) {
                        changed++;
                    }
                }
            }

            if (painted == 0) {
                blank.Add(type.Name);

                if (!Exempt.TryGetValue(type.Name, out var reason) || string.IsNullOrWhiteSpace(reason)) {
                    offenders.Add($"{type.Name} painted nothing and has no written reason for painting nothing");
                }

                continue;
            }

            if (Exempt.ContainsKey(type.Name)) {
                offenders.Add(
                    $"{type.Name} is exempted as painting nothing and painted {painted} pixels; "
                    + "the exemption has expired"
                );
            }

            if (painted < MinimumInk) {
                offenders.Add(
                    $"{type.Name} painted only {painted} pixels, which is too little to conclude anything "
                    + "from; the fixture is not drawing it"
                );

                continue;
            }

            var share = changed / (double)painted;

            if (share < MinimumRepaint) {
                offenders.Add(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{type.Name} repainted {share:0.000} of its {painted} painted pixels under `dark`; "
                        + $"every control in this assembly measures 1.000, so a share this low is a colour "
                        + $"the control paints without asking the palette"
                    )
                );

                continue;
            }

            repainting.Add(type.Name);
        }

        // ⚠ First, and both of them: a sweep whose reflection found nothing satisfies every
        // assertion below perfectly, and so does one that built the types and rendered blank frames.
        Assert.True(built.Count >= Elements, $"only {built.Count} element types were built");

        Assert.True(
            repainting.Count >= Repainting,
            $"only {repainting.Count} of {built.Count} elements painted and repainted"
        );

        // Then the offenders, and before the residue below rather than after it: this one names the
        // control and says what is wrong with it, where two sorted lists diverging at index four do
        // not.
        Assert.Empty(offenders);

        // And the residue, stated rather than implied: the exemption table is exactly the set that
        // painted nothing. This is the only assertion that can see an entry naming a control that no
        // longer exists, since a deleted one never comes round the loop to contradict it.
        Assert.Equal(blank.Order(StringComparer.Ordinal), Exempt.Keys.Order(StringComparer.Ordinal));
    }

    static UiElement Make<T>(UiElement parent) where T : UiElement, new() => parent.Add<T>();

    /// <summary>Renders one control in one palette, and says where its own box is.</summary>
    /// <remarks>
    ///     A document apiece rather than one document reclassed, because the two renderings have to
    ///     be comparable pixel for pixel and a control that has been laid out once is a control whose
    ///     state a second layout may not reproduce.
    /// </remarks>
    static (Bitmap Image, Box Box) Render(MethodInfo make, Type type, bool dark) {
        using var ui = ControlHarness.Open(Width, Height, Css);

        if (dark) {
            // The class and nothing else, because that is the whole of how an application asks for
            // the dark palette — `root.dark` is `DarkModeStrategy.Class` as the utility generator
            // understands it, and a fixture that switched themes any other way would be testing a
            // mechanism no application uses.
            ui.Document.Root.AddClass("dark");
        }

        var element = (UiElement)make.MakeGenericMethod(type).Invoke(null, [ui.Document.Root])!;
        element.AddClass("probe");
        Seed(element);
        ui.Frame();

        var image = ui.Capture();
        var bounds = element.Bounds;

        return (
            image,
            new(
                Math.Max(0, (int)MathF.Round(bounds.X)),
                Math.Max(0, (int)MathF.Round(bounds.Y)),
                Math.Min(image.Width, (int)MathF.Round(bounds.X + bounds.Width)),
                Math.Min(image.Height, (int)MathF.Round(bounds.Y + bounds.Height))
            )
        );
    }

    /// <summary>Gives a control something to draw, by the one rule that fits every control.</summary>
    /// <remarks>
    ///     ⚠ <c>DeclaringType != typeof(UiElement)</c> on the named properties, and
    ///     <c>UiElement.Text</c> only for a childless element. Setting <c>Text</c> on a control that
    ///     has already built its parts throws out of the layout — "a node that has children cannot
    ///     also measure itself" — which is how this rule came to be two halves rather than one.
    /// </remarks>
    static void Seed(UiElement element) {
        foreach (var name in Wordy) {
            var property = element.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

            if (property is { CanWrite: true }
                && property.PropertyType == typeof(string)
                && property.DeclaringType != typeof(UiElement)) {
                property.SetValue(element, "Ag");
            }
        }

        if (element.Children.Count == 0) {
            element.Text = "Ag";
        }
    }

    static (byte R, byte G, byte B) Colour(in Bitmap image, int x, int y) {
        var offset = image.Offset(x, y);
        return (image.Pixels[offset], image.Pixels[offset + 1], image.Pixels[offset + 2]);
    }

    static bool Same(in Bitmap left, in Bitmap right, int x, int y) => Colour(left, x, y) == Colour(right, x, y);

    static bool Same(in Bitmap image, (byte R, byte G, byte B) colour, int x, int y) => Colour(image, x, y) == colour;

    /// <summary>The control's own box in the picture, in pixels.</summary>
    readonly record struct Box(int Left, int Top, int Right, int Bottom);
}
