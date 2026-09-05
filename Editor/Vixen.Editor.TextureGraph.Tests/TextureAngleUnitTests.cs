// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>Every angle a kernel takes is in radians, and the roll call can tell.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/735">#735</a>.</b> Six kernels measured
///         an angle in radians and three in turns, and which one a number meant was a property of the
///         file it was declared in. <c>Placement/Tile Sampler</c> had a <c>rotation</c> in radians
///         immediately above a <c>rotationJitter</c> in turns; <c>Space/Transform 2D</c> turned a
///         whole circle for the number that turns a sixth of a degree on <c>Source/Shape</c>. Nothing
///         anywhere said so, and a node that read the wrong one would be out by 2π with no error and
///         a plausible picture.
///     </para>
///     <para>
///         ⚠ <b>The unit was only ever in a doc comment, which is why this file exists rather than a
///         paragraph.</b> Both conventions are defensible — a turn is what a 0–1 slider wants, radians
///         are what the trigonometry wants — so the thing that had to become impossible is *having
///         both*. The kernels are radians now, and what makes that stay true is the second assertion
///         below: a kernel that declares an angle may not carry a full-turn constant at all, because
///         the only thing such a constant is for is converting one.
///     </para>
///     <para>
///         ⚠ <b>Ask what this prints on the day it stops working.</b> A regex that matched nothing
///         would make every assertion here vacuous, so the parse is required to find the angles that
///         are known to exist by name — and the exemption is checked from both ends, so a kernel that
///         loses its turn constant loses the line excusing it.
///     </para>
/// </remarks>
public class TextureAngleUnitTests {
    /// <summary>What makes a scalar uniform an angle: it is one of these three words, or ends in one.</summary>
    /// <remarks>
    ///     ⚠ <c>Hsl</c>'s <c>hue</c> is deliberately not in here and is deliberately in turns. It is a
    ///     position on a colour wheel rather than a direction in the image, 0–1 is what every colour
    ///     picker in existence shows, and no node puts it beside a geometric angle — the thing #735
    ///     was about is one <em>node</em> exposing two units, not the word "angle" appearing twice in
    ///     an assembly.
    /// </remarks>
    static readonly string[] Angles = ["rotation", "angle", "elevation"];

    /// <summary>A kernel that may declare a turn constant despite taking an angle, and why.</summary>
    /// <remarks>
    ///     ⚠ <b>An entry is a claim about what the constant is for.</b> The rule is that nothing
    ///     converts a parameter, not that τ is forbidden — a kernel is free to build an angle of its
    ///     own out of one (<c>Noise</c> and <c>AmbientOcclusion</c> both do, and neither takes an
    ///     angle, so neither is in scope here).
    /// </remarks>
    static readonly (string Kernel, string Reason)[] Converts = [
        ("Gradient",
            "Its `angle` is radians and is subtracted from atan2's own output, which is radians too. The "
            + "InvTwoPi that follows converts the *result* into the 0..1 ramp coordinate a gradient is read "
            + "at — it is the sweep's output being normalised, not the parameter being decoded.")
    ];

    /// <summary>Angle parameters that must be found, or the parse below has stopped working.</summary>
    /// <remarks>
    ///     Deliberately a floor rather than the whole set: a slice that adds a kernel with an angle
    ///     adds a row nobody here can see, and an exact equality over a surface another branch can
    ///     grow is red on the merge and green everywhere else — which has happened three times in this
    ///     workstream. These four are the ones #735 named, one per shape of the problem.
    /// </remarks>
    static readonly (string Kernel, string Parameter)[] Known = [
        ("Transform2D", "rotation"),
        ("TileSampler", "rotationJitter"),
        ("Splatter", "rotationMapAmount"),
        ("Emboss", "elevation")
    ];

    /// <summary>A scalar uniform declaration, and the doc comment block above it.</summary>
    static readonly Regex Declaration = new(
        @"(?<doc>(?:^[ \t]*///.*\n)*)^[ \t]*var[ \t]+(?<name>[A-Za-z][A-Za-z0-9_]*)[ \t]*:[ \t]*float[ \t]*=",
        RegexOptions.Multiline
    );

    /// <summary>Any float literal, so a turn can be recognised by its value rather than by its name.</summary>
    /// <remarks>
    ///     ⚠ <b>By value, because the name is the half that is easy to change.</b> A kernel that
    ///     spelled <c>const val Circle: float = 6.28318f</c>, or wrote the number inline, would pass a
    ///     search for "Tau" and be exactly the defect this is for.
    /// </remarks>
    static readonly Regex Literal = new(@"(?<![A-Za-z0-9_.])(?<value>[0-9]+\.[0-9]+)(?:e-?[0-9]+)?f?");

    public static TheoryData<string> Kernels => [.. TextureKernels.Names];

    /// <summary>Every angle parameter any kernel declares says radians, and none is scaled.</summary>
    [Fact]
    public void Every_angle_a_kernel_takes_is_in_radians() {
        List<(string Kernel, string Parameter)> found = [];

        foreach (var kernel in TextureKernels.Names) {
            var source = TextureKernels.Source(kernel);

            foreach (var parameter in AnglesIn(source)) {
                found.Add((kernel, parameter.Name));

                Assert.True(
                    parameter.Doc.Contains("radian", StringComparison.OrdinalIgnoreCase),
                    $"{kernel}.{parameter.Name} is an angle and its declaration does not say radians. Every "
                    + "angle in this folder is radians — #735, where three of them were turns and the unit "
                    + "was a property of which file you were reading."
                );
            }
        }

        Assert.NotEmpty(found);

        // The parse found the rows that are known to be there, so a regex that had stopped matching
        // could not leave the assertion above vacuously true.
        foreach (var row in Known) {
            Assert.Contains(row, found);
        }
    }

    /// <summary>
    ///     ⚠ And no kernel that takes an angle carries a constant for converting one, which is the
    ///     half a doc comment cannot enforce.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the assertion that would have caught #735</b>, and the doc-comment one above
    ///         would not have: <c>TileSampler</c>'s <c>rotationJitter</c> <em>was</em> documented as
    ///         turns, honestly and in the right place, and that is precisely why nothing was wrong
    ///         enough to fix for three batches. A τ in a kernel that takes an angle means some
    ///         parameter is being decoded, and the whole of the fix was deleting three of them.
    ///     </para>
    ///     <para>
    ///         <b>Half a turn counts too.</b> A π would be a parameter in half-turns, which is the
    ///         same defect with a different constant.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Kernels))]
    public void A_kernel_that_takes_an_angle_declares_no_turn_constant(string kernel) {
        var source = TextureKernels.Source(kernel);
        var angles = AnglesIn(source).Select(parameter => parameter.Name).ToArray();
        var turns = Turns(source);
        var excused = Converts.Any(entry => string.Equals(entry.Kernel, kernel, StringComparison.Ordinal));

        if (angles.Length == 0 || excused) {
            return;
        }

        Assert.True(
            turns.Length == 0,
            $"{kernel} takes the angle(s) {string.Join(", ", angles)} and declares {string.Join(", ", turns)}, "
            + "which is a whole or half turn. The only use for one in a kernel that takes an angle is "
            + "converting it, and every angle here is radians — #735. If it is genuinely something else, say "
            + "so in TextureAngleUnitTests.Converts."
        );
    }

    /// <summary>Every excused kernel still has the constant its line excuses, and still takes an angle.</summary>
    /// <remarks>
    ///     ⚠ <b>The dead-exemption check, which is the failure mode of every list like this.</b> A
    ///     kernel that stopped converting, or stopped taking an angle, would go on being excused by a
    ///     line nobody re-reads — and the day a real conversion appeared in it, nothing would say so.
    /// </remarks>
    [Fact]
    public void Every_excused_kernel_is_still_doing_the_thing_it_is_excused_for() {
        Assert.NotEmpty(Converts);

        foreach (var (kernel, reason) in Converts) {
            Assert.Contains(kernel, TextureKernels.Names);

            var source = TextureKernels.Source(kernel);

            Assert.True(reason.Length > 40, kernel);
            Assert.NotEmpty(AnglesIn(source));

            Assert.True(
                Turns(source).Length > 0,
                $"{kernel} is excused from the turn-constant rule and no longer has a turn constant, so the "
                + "line excusing it is excusing nothing. Delete the entry — #735."
            );
        }
    }

    /// <summary>The angle-valued scalar uniforms one kernel's source declares, with their doc blocks.</summary>
    static IEnumerable<(string Name, string Doc)> AnglesIn(string source) {
        foreach (Match match in Declaration.Matches(source)) {
            var name = match.Groups["name"].Value;

            if (Angles.Any(angle => name.StartsWith(angle, StringComparison.OrdinalIgnoreCase))) {
                yield return (name, match.Groups["doc"].Value);
            }
        }
    }

    /// <summary>Whole- and half-turn constants in a kernel's source, by value.</summary>
    static string[] Turns(string source) =>
        [
            .. Literal.Matches(source)
                .Select(match => match.Groups["value"].Value)
                .Where(text => Turn(double.Parse(text, CultureInfo.InvariantCulture)))
                .Distinct(StringComparer.Ordinal)
        ];

    /// <summary>Whether a number is a turn's worth of radians, or the reciprocal that undoes one.</summary>
    /// <remarks>
    ///     The reciprocals are here because a conversion has two directions and only one of them
    ///     multiplies: <c>Gradient</c>'s <c>1/2π</c> is what turns an angle back into a 0–1 coordinate.
    ///     They are matched far more tightly than τ itself, because 0.159 is a number a kernel could
    ///     plausibly mean for something else and 6.283 is not.
    /// </remarks>
    static bool Turn(double value) =>
        Math.Abs(value - Math.Tau) < 1e-3
        || Math.Abs(value - Math.PI) < 1e-3
        || Math.Abs(value - (1d / Math.Tau)) < 1e-7
        || Math.Abs(value - (1d / Math.PI)) < 1e-7;
}
