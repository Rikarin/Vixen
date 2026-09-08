// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Xunit;

namespace Tests;

/// <summary>
///     Doc 48 § 4.9's named-metal lookup: the table is the kernel's, the enum is the names, and a
///     bake of each name is the row the kernel declares for it.
/// </summary>
/// <remarks>
///     <para>
///         <see href="https://github.com/Rikarin/Vixen/issues/1096">#1096</see>. ⚠ <b>Nothing here
///         can say whether the numbers are <em>right</em>, and no test could.</b> F0 per metal is
///         measured data; every plausible-looking triple draws a plausible metal, so a mistyped digit
///         has no oracle short of the published table the kernel's header cites. What is checkable is
///         everything <em>around</em> the numbers, and all of it is silent when wrong: a name whose
///         branch is missing bakes as iron, a renumbered enum bakes gold as brass, and a uniform that
///         never arrived bakes all ten as iron. Those are what this file is for.
///     </para>
///     <para>
///         ⚠ <b>The expected values are read out of the kernel, never written here.</b> A copy of the
///         table in a test is a second transcription of the thing the kernel's header refuses to have
///         two of — <see href="https://github.com/Rikarin/Vixen/issues/1095">#1095</see>'s defect —
///         and it would go on agreeing with a kernel that had drifted from the published source. So
///         the device test asserts <em>this index bakes the row the kernel declares for this
///         index</em>, which is the half a picture can settle.
///     </para>
/// </remarks>
public class TextureMetalReflectanceTests(ITestOutputHelper output) {
    const int Side = 32;

    /// <summary>A line comment, doc or plain.</summary>
    static readonly Regex Comment = new(@"//.*$", RegexOptions.Multiline);

    /// <summary>One branch's guard: the index the chain compares <c>metal</c> against.</summary>
    static readonly Regex Guard = new(@"metal\s*==\s*(\d+)");

    /// <summary>One row of the table, wherever it is returned from.</summary>
    static readonly Regex Returned = new(@"return\s+float3\(([^)]*)\)");

    /// <summary>A Raven float literal.</summary>
    static readonly Regex Literal = new(@"-?\d+(?:\.\d+)?");

    /// <summary>The kernel's source with its comments removed.</summary>
    /// <remarks>
    ///     ⚠ The header argues about the table in prose and the parser must not read the argument as
    ///     the table. It does not today — no comment in the file spells <c>return float3(</c> — which
    ///     is exactly the kind of thing that stops being true the next time somebody quotes the
    ///     kernel at itself, and <c>TextureKernelLanguageSeamTests</c> has already been bitten by it
    ///     once.
    /// </remarks>
    static string Source() =>
        string.Join(
            '\n',
            TextureKernels.Source("MetalReflectance").Split('\n').Select(line => Comment.Replace(line, string.Empty))
        );

    /// <summary>The kernel's table, by the index the enum gives each metal.</summary>
    /// <returns>Ten triples, in <see cref="TextureMetal" /> order.</returns>
    /// <remarks>
    ///     ⚠ <b>The guards are read rather than assumed, which is the whole of the off-by-one
    ///     check.</b> The chain tests 1 through 9 and falls through to index 0, so a branch inserted
    ///     in the wrong place or a guard mistyped shows up as a guard sequence that is not
    ///     1…<c>n</c> — and every metal after the mistake would otherwise bake as its neighbour,
    ///     which is a picture nobody would call broken.
    /// </remarks>
    static ImmutableArray<float[]> Table() {
        var source = Source();

        int[] guards = [.. Guard.Matches(source).Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))];

        float[][] returned = [
            .. Returned
                .Matches(source)
                .Select(match => Literal
                    .Matches(match.Groups[1].Value)
                    .Select(number => float.Parse(number.Value, CultureInfo.InvariantCulture))
                    .ToArray())
        ];

        // The instrument. A regex that matched nothing would build an empty table, and every
        // comparison below over an empty table passes.
        Assert.NotEmpty(returned);
        Assert.Equal(guards.Length + 1, returned.Length);
        Assert.Equal([.. Enumerable.Range(1, guards.Length)], guards);
        Assert.All(returned, row => Assert.Equal(3, row.Length));

        var table = new float[returned.Length][];

        for (var branch = 0; branch < guards.Length; branch++) {
            table[guards[branch]] = returned[branch];
        }

        // The fall-through, which is the last `return` in the chain and the entry the enum calls zero.
        table[0] = returned[^1];

        return [.. table];
    }

    /// <summary>Every named metal has a branch of its own, and no two of them are the same colour.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Both directions, because each failure is silent in its own way.</b> A name added
    ///         to <see cref="TextureMetal" /> with no branch beside it takes the fall-through and
    ///         bakes as iron — a metal, in a picker, that quietly is not the metal it says. A branch
    ///         with no name is a row of the table no author can reach.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Distinctness is the assertion that would catch a copy-paste, and it is the one a
    ///         reviewer of the table cannot make by eye</b> — ten triples of three digits each all
    ///         look different at a glance, and two identical rows are exactly what transcribing a
    ///         printed table produces.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_named_metal_has_its_own_row_in_the_kernel_s_table() {
        var table = Table();
        var names = Enum.GetValues<TextureMetal>();

        Assert.Equal(names.Length, table.Length);

        // Contiguous from zero, which is what makes the enum's ordinal the kernel's index rather
        // than merely looking like it.
        Assert.Equal([.. Enumerable.Range(0, names.Length)], [.. names.Select(metal => (int)metal)]);

        Assert.All(
            table,
            row => Assert.All(row, channel => Assert.InRange(channel, 0.05f, 1f))
        );

        Assert.Equal(
            table.Length,
            table.Select(row => string.Join(',', row)).Distinct(StringComparer.Ordinal).Count()
        );
    }

    /// <summary>Each row is the metal it is named after, judged by properties the table does not state.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Written because the sabotage caught this file being useless at exactly the thing
    ///         it claimed.</b> Swapping gold's and copper's rows in the kernel left every other
    ///         assertion here green — necessarily, since both the expectation and the picture come
    ///         out of the same file. "This index bakes the row declared for this index" cannot see a
    ///         table whose rows are in the wrong order, which is the commonest way a printed table is
    ///         transcribed wrong.
    ///     </para>
    ///     <para>
    ///         <b>So the oracle here is physics rather than the table.</b> Seven of the ten are
    ///         near-neutral greys and three are strongly coloured; gold is the yellowest metal there
    ///         is, which is to say it has the least blue relative to its red of anything in the list;
    ///         copper is the reddest relative to its green; silver is the brightest and titanium the
    ///         darkest. None of that is a number read off the source — it is what the metals look
    ///         like — and every one of the statements is false of a table with two rows exchanged.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Ratios and orderings, deliberately, and no absolute value anywhere.</b> An
    ///         assertion naming a digit would be the second transcription this whole arrangement
    ///         exists to avoid, and it would go on agreeing with a kernel that had drifted from the
    ///         published source in the direction the test was written from.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Each_row_looks_like_the_metal_it_is_named_after() {
        var table = Table();

        static float Ratio(float[] row, int over, int under) => row[over] / row[under];
        static float Mean(float[] row) => (row[0] + row[1] + row[2]) / 3f;

        int[] chromatic = [(int)TextureMetal.Gold, (int)TextureMetal.Copper, (int)TextureMetal.Brass];

        for (var metal = 0; metal < table.Length; metal++) {
            var row = table[metal];
            var spread = row.Max() / row.Min();

            if (chromatic.Contains(metal)) {
                Assert.True(
                    row[0] > row[1] && row[1] > row[2] && spread > 1.7f,
                    $"{(TextureMetal)metal} is one of the three coloured metals and its row {Show(row)} "
                    + "is not warm — red above green above blue, by a wide margin"
                );
            } else {
                Assert.True(
                    spread < 1.35f,
                    $"{(TextureMetal)metal} is a grey metal and its row {Show(row)} is coloured"
                );
            }
        }

        // Gold is the yellowest metal there is: nothing else reflects so much less blue than red.
        Assert.Equal(
            TextureMetal.Gold,
            (TextureMetal)Ordered(table, row => Ratio(row, 0, 2))[^1]
        );

        // And copper is the reddest: nothing else reflects so much less green than red. ⚠ This is
        // the pair that tells the two apart, and swapping their rows is what this test was written
        // for — the ratio above alone would still name whichever row sat at gold's index.
        Assert.Equal(
            TextureMetal.Copper,
            (TextureMetal)Ordered(table, row => Ratio(row, 0, 1))[^1]
        );

        Assert.Equal(TextureMetal.Silver, (TextureMetal)Ordered(table, Mean)[^1]);
        Assert.Equal(TextureMetal.Titanium, (TextureMetal)Ordered(table, Mean)[0]);
    }

    /// <summary>The table's indices, ordered by some measure of their row.</summary>
    static int[] Ordered(IReadOnlyList<float[]> table, Func<float[], float> measure) => [
        .. Enumerable.Range(0, table.Count).OrderBy(metal => measure(table[metal]))
    ];

    /// <summary>One row, for a failure message.</summary>
    static string Show(float[] row) =>
        "(" + string.Join(", ", row.Select(channel => channel.ToString("0.###", CultureInfo.InvariantCulture))) + ")";

    /// <summary>Each name bakes the row the kernel declares for it, on a real device.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Ten bakes rather than one, because the failure this covers is invisible in
    ///         any one of them.</b> A <c>metal</c> uniform that never reached the shader, an enum
    ///         whose ordinals stopped being the kernel's indices, and a branch chain off by one all
    ///         produce a perfectly good picture of a perfectly good metal. Only the pairing of a name
    ///         with the row named for it can tell them apart.
    ///     </para>
    ///     <para>
    ///         <b><c>Rgba8</c> is linear unorm here and not sRGB</b> — <c>Source/Uniform</c>'s own
    ///         device test reads 0.25 back as 63 — so the expected byte is the declared reflectance
    ///         times 255. The tolerance is two steps, which is quantisation and the half-float the
    ///         kernel writes through. ⚠ <b>It is not "far below" the gap between two rows, and an
    ///         earlier version of this remark said so.</b> Chromium and iron are 3.3 steps apart in
    ///         red and 2.3 in green, so a ±2 window is a margin of about one step and it is red
    ///         alone that keeps those two distinguishable. That is enough — the ten rows are paired
    ///         with ten distinct expectations, so a uniform that never arrived is red on every row —
    ///         but it is a margin and not a chasm.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData((int)TextureMetal.Iron)]
    [InlineData((int)TextureMetal.Chromium)]
    [InlineData((int)TextureMetal.Nickel)]
    [InlineData((int)TextureMetal.Titanium)]
    [InlineData((int)TextureMetal.Platinum)]
    [InlineData((int)TextureMetal.Aluminium)]
    [InlineData((int)TextureMetal.Silver)]
    [InlineData((int)TextureMetal.Gold)]
    [InlineData((int)TextureMetal.Copper)]
    [InlineData((int)TextureMetal.Brass)]
    public void A_named_metal_bakes_the_row_the_kernel_declares_for_it(int metal) {
        var expected = Table()[metal];

        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [new(TextureFormat.Rgba8)],
            Ops = [
                new() {
                    Kernel = TextureColourKernels.MetalReflectance,
                    Output = 0,
                    Parameters = [new("metal", metal)]
                }
            ],
            Outputs = [0]
        };

        Assert.Empty(plan.Validate());

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan);

        var picture = bake.Read(0);

        // Four texels rather than one: a kernel that wrote the right colour into one invocation and
        // something else into the rest is a picture, and reading the corner alone cannot see it.
        foreach (var (x, y) in new[] { (0, 0), (Side - 1, 0), (0, Side - 1), (Side / 3, Side / 2) }) {
            for (var channel = 0; channel < 3; channel++) {
                var want = (int)Math.Round(expected[channel] * 255f);

                Assert.InRange((int)TextureKernelHarness.At(picture, x, y, channel), want - 2, want + 2);
            }
        }
    }
}
