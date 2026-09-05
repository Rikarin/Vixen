// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Vixen.Core.Imaging;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.ShaderCompiler;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     Doc 48's exit criterion 4 — "a sabotage per node" — as a roll call over the kernels this
///     assembly ships, with the sabotage generated rather than written.
/// </summary>
/// <remarks>
///     <para>
///         <b>The criterion says why it exists: "a node whose golden survives is a golden of a black
///         image".</b> Sabotage arguments appear in eight device suites here and several are real,
///         but they were arguments — a paragraph saying what a test would catch — and there was no
///         per-kernel perturbation and, worse, <em>nothing noticed a missing one</em>. A kernel added
///         with a test that cannot see it is the exact defect the criterion is about, and the way
///         this assembly has twice made that mechanical is a roll call taken over the shipped files
///         with a written exemption for anything it cannot cover
///         (<c>TextureNodeLibraryTests.Every_kernel_has_a_node_or_a_written_reason_not_to</c>,
///         <c>TextureAngleUnitTests</c>).
///     </para>
///     <para>
///         ⚠ <b>The perturbation is generated from the kernel's own source, and that is what makes it
///         a roll call rather than forty-five hand-written tests.</b> Every kernel in
///         <c>Shaders/</c> writes through <c>target.Store(</c> — sixty-four call sites across
///         forty-five files, and nothing else stores anywhere — so <see cref="Perturb" /> renames the
///         shader, redirects every store through a function of its own, and puts a bounded
///         perturbation in that function. The plan then carries the result as an
///         <em>authored</em> kernel, which is the seam
///         <a href="https://github.com/Rikarin/Vixen/issues/729">#729</a> added for the Pixel
///         Processor: a name a plan brings with it, compiled by the same evaluator through the same
///         Raven front end as an embedded one.
///     </para>
///     <para>
///         ⚠ <b>What this proves and what it does not.</b> It proves that for every shipped kernel
///         there is an evaluation whose picture is <em>sensitive to that kernel's source</em> — the
///         kernel is reached, it is the thing that wrote the output, and a change inside it comes out
///         in the texels. That is the half of criterion 4 that was missing entirely. It is not a
///         golden per node: criterion 3's other half is still owed, and when a golden per node exists
///         this file is what says every one of them is perturbed.
///     </para>
///     <para>
///         <b>The perturbation is bounded on purpose.</b> <c>1 - saturate(v * 0.5 + c)</c>, with a
///         different <c>c</c> per channel, has exactly one fixed point per channel and they are four
///         different values — so a picture survives it only by being that one colour everywhere. A
///         plain scale would have been invisible in a kernel whose output already clamps: a channel
///         that stores 500 and one that stores 250 both read 255, and every "the picture changed"
///         assertion over such a kernel would have been a golden of a saturated image.
///     </para>
/// </remarks>
public class TextureKernelSabotageTests(ITestOutputHelper output) {
    const int Side = TextureKernelHarness.Side;

    /// <summary>The suffix an authored, perturbed copy of a kernel is named with.</summary>
    /// <remarks>
    ///     ⚠ <b>A different name is not a nicety, it is what <c>TexturePlan.Source</c> requires.</b> A
    ///     plan may not carry a kernel this assembly also ships — the evaluator caches a compiled
    ///     module on (name, output format) across plans, so the two would take turns — and the
    ///     evaluator resolves the shader inside the source <em>by the op's kernel name</em>, so the
    ///     <c>shader</c> declaration has to be renamed with it or the compile fails saying the source
    ///     declares no shader by that name.
    /// </remarks>
    const string Suffix = "Sabotaged";

    /// <summary>
    ///     Every op implementation the generated sabotage cannot be taken over, and why.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every entry is a CPU operation, and that is the category this roll call was
    ///         blind to.</b> The subject was <see cref="TextureKernels.Names" /> alone — the embedded
    ///         <c>.rvn</c> files — so doc 48 § 4.6's exception to § D3 was outside it entirely: a CPU
    ///         operation ships no shader, and a roll call over shaders reports complete coverage of a
    ///         surface it cannot see part of. That is the same shape as
    ///         <a href="https://github.com/Rikarin/Vixen/issues/746">#746</a> one category along, and
    ///         the criterion's own words are "a sabotage per <em>node</em>" rather than per kernel.
    ///     </para>
    ///     <para>
    ///         <b>The perturbation genuinely cannot reach one.</b> <see cref="Perturb" /> rewrites
    ///         Raven text and hands it back on the plan; a CPU operation is C# in this assembly and
    ///         there is no seam that carries an authored one. So what an entry undertakes is that
    ///         something else perturbs it, named — and the roll call fails when a second CPU
    ///         operation ships without a line here, which is the half that was missing.
    ///     </para>
    /// </remarks>
    static readonly (string Kernel, string Reason)[] Unsabotaged = [
        ("NormalToHeightOperation",
            "A CPU operation: C# in this assembly, not Raven on a plan, so `Perturb`'s rewrite has nothing to "
            + "rewrite and `TexturePlan.Kernels` has no seam that would carry an authored one. What perturbs it "
            + "instead is `TextureNormalToHeightTests.A_flipped_axis_does_not_survive_the_round_trip`, which "
            + "negates one axis of the integration and requires the round trip to stop agreeing — a sabotage of "
            + "the operation's own arithmetic rather than of its store. ⚠ It is not the generated one and is "
            + "therefore something a slice can forget; this line is what makes forgetting it red.")
    ];

    /// <summary>
    ///     Every op implementation this assembly ships: an embedded kernel, or a CPU operation.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Both halves are read rather than declared, and for the same reason.</b> A kernel
    ///     exists because a file ships, so <see cref="TextureKernels.Names" /> is the manifest
    ///     resources; a CPU operation exists because a type ships, so it is the assembly's own
    ///     implementations of <see cref="ITextureCpuOperation" />. Reading a declaration — a static
    ///     <c>All</c>, a list here — is what #746 found a kernel already hiding behind.
    /// </remarks>
    static string[] Shipped() =>
        TextureKernels.Names
            .Concat(
                typeof(TextureKernels).Assembly.GetTypes()
                    .Where(type => type is { IsAbstract: false, IsInterface: false })
                    .Where(typeof(ITextureCpuOperation).IsAssignableFrom)
                    .Select(type => type.Name)
            )
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>The shipped kernels this file undertakes to perturb, in order.</summary>
    static string[] Covered() =>
        Shipped()
            .Where(name => !Unsabotaged.Any(entry => string.Equals(entry.Kernel, name, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>One case per shipped kernel, minus the written exemptions.</summary>
    public static TheoryData<string> Kernels {
        get {
            TheoryData<string> data = [];

            foreach (var kernel in Covered()) {
                data.Add(kernel);
            }

            return data;
        }
    }

    /// <summary>
    ///     ⚠ Every shipped kernel is perturbed by a case below, or is on the written list with a
    ///     reason — and nothing else is on that list.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the half criterion 4 was missing.</b> A per-kernel sabotage that nothing
    ///         counts is a sabotage for the kernels somebody remembered; the forty-sixth
    ///         <c>.rvn</c> arrives with no perturbation and every test in the assembly stays green.
    ///         So the roll call is taken over <see cref="Shipped" /> — the <em>embedded</em> kernel
    ///         files and the assembly's own CPU operations, because an op implementation exists when
    ///         a file or a type ships and not when a declaration mentions it, which is
    ///         <a href="https://github.com/Rikarin/Vixen/issues/746">#746</a>'s lesson — against the
    ///         theory's own data.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The CPU half was outside the subject set entirely until it was named.</b> The
    ///         roll call read the shaders, so it reported complete coverage of a surface doc 48
    ///         § 4.6's exception is not in — and the criterion says "per node", of which a CPU
    ///         operation is one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It cannot pass empty.</b> A <see cref="Kernels" /> that came back with nothing —
    ///         a manifest read that returned no resources, an exemption list that swallowed the lot —
    ///         would make every theory case below vanish rather than fail, which is precisely the
    ///         shape of a gate that reports success on the day it stops running.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_shipped_kernel_is_sabotaged_or_has_a_written_reason_not_to() {
        var shipped = Shipped();
        var covered = Covered();
        var excused = Unsabotaged.Select(entry => entry.Kernel).Order(StringComparer.Ordinal).ToArray();

        Assert.NotEmpty(shipped);
        Assert.NotEmpty(covered);

        // ⚠ Both categories are in the subject set. A reflection walk that stopped finding CPU
        // operations would make the union the kernels again, and the roll call would go back to
        // reporting complete coverage of a surface it cannot see half of — silently, which is the
        // failure it exists to refuse.
        Assert.NotEmpty(
            shipped.Where(name => !TextureKernels.Names.Contains(name, StringComparer.Ordinal)).ToArray()
        );

        // ⚠ And every name the theory undertakes to perturb is a kernel the generated sabotage can
        // actually rewrite. Without this, a CPU operation left off the list above is red only inside
        // the device theory below — which SKIPS on a machine with no adapter, so the roll call would
        // report success precisely where it had stopped measuring anything.
        Assert.All(covered, name => Assert.Contains(name, TextureKernels.Names, StringComparer.Ordinal));

        Assert.Equal(shipped, covered.Concat(excused).Order(StringComparer.Ordinal).ToArray());

        // And the theory really has that many cases. `Covered` is what the roll call reads and what
        // the data is built from, so the one way this could still be a claim about nothing is the
        // two disagreeing on the way to xUnit.
        Assert.Equal(covered.Length, Kernels.Count);

        // Both ends: a name here that is not a kernel is a typo excusing a real gap, and one whose
        // kernel went away is an allowance that has outlived what it excused.
        Assert.All(excused, kernel => Assert.Contains(kernel, shipped, StringComparer.Ordinal));
        Assert.All(Unsabotaged, entry => Assert.True(entry.Reason.Length > 40, entry.Kernel));
    }

    /// <summary>
    ///     ⚠ Perturbing one kernel's own store changes the picture that kernel draws.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Two evaluations of the same plan over the same source texture</b>, differing only
    ///         in which Raven the one op runs: the kernel as committed, and
    ///         <see cref="Perturb" />'s copy of it. Everything else — the parameters, the seed, the
    ///         image formats, the external texture — is identical, so a difference in the read-back
    ///         is a difference the kernel's own text caused.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The instruments come before the finding, because each of them is a way for this
    ///         to pass while measuring nothing.</b> The perturbation is asserted to have applied at
    ///         all (a rename that matched nothing would compile the kernel unchanged and the two
    ///         pictures would agree — a red test, but for the wrong reason, so it is named); the
    ///         authored name is asserted not to be one this assembly ships (or
    ///         <c>TexturePlan.Source</c> would hand back the embedded module); and both bakes are
    ///         asserted to have dispatched exactly once, which is what separates "the kernel drew
    ///         something different" from "neither kernel ran".
    ///     </para>
    ///     <para>
    ///         <b>The parameters are the kernel's own declared defaults</b>, so this asks nothing of
    ///         the node library and stays a claim about the shader. A default that makes a kernel a
    ///         no-op is not a problem here: the perturbation is on the store, so an identity kernel
    ///         still writes a different image.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Kernels))]
    public void A_perturbed_kernel_draws_a_different_picture(string kernel) {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}; kernel: {kernel}");

        var (texture, staging) = TextureKernelHarness.Upload(device, TextureKernelHarness.Unique(Side), Side, Side);

        try {
            var shape = Shape(kernel);
            var sabotaged = kernel + Suffix;
            var perturbed = Perturb(kernel);

            // The instrument: the rewrite happened, and the name it happened under is not one the
            // evaluator would resolve to an embedded module instead.
            Assert.DoesNotContain(sabotaged, TextureKernels.Names);
            Assert.Contains("SabotageStore", perturbed, StringComparison.Ordinal);
            Assert.Contains($"shader {sabotaged} ", perturbed, StringComparison.Ordinal);

            // Exactly one store survives, and it is the injected one: every call site the kernel
            // wrote now goes through the perturbation. A rewrite that missed one would leave a
            // kernel half-perturbed and a difference that says nothing about the other half.
            Assert.Equal(1, perturbed.Split("target.Store(").Length - 1);

            using var evaluator = new TexturePlanEvaluator(device);

            var honest = Bake(evaluator, Plan(kernel, shape, null), texture, shape.Inputs);
            var broken = Bake(evaluator, Plan(sabotaged, shape, perturbed), texture, shape.Inputs);

            // Two variants, so the second evaluation compiled the authored source rather than
            // finding the first one in the cache under a name it shares.
            Assert.Equal(2, evaluator.Compilations);

            var moved = TextureKernelHarness.LargestMove(honest, broken, 0)
                + TextureKernelHarness.LargestMove(honest, broken, 1)
                + TextureKernelHarness.LargestMove(honest, broken, 2)
                + TextureKernelHarness.LargestMove(honest, broken, 3);

            if (moved == 0) {
                Assert.Fail(
                    $"{kernel} on {TextureKernelHarness.Adapter(device)}: perturbing every store in the kernel "
                    + "left the picture identical in all four channels. Either the kernel's output does not depend "
                    + "on what it stores — in which case a golden of it is a golden of nothing — or the authored "
                    + "source did not reach the compiler. Doc 48 exit criterion 4."
                );
            }

            output.WriteLine(
                string.Create(CultureInfo.InvariantCulture, $"{kernel}: the perturbation moved a channel by {moved}")
            );
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }

    /// <summary>A one-op plan over as many external images as the kernel binds textures.</summary>
    /// <param name="kernel">The name the op gives its shader — the embedded one, or the authored copy.</param>
    /// <param name="shape">What that kernel binds and declares.</param>
    /// <param name="authored">The Raven to carry, for the authored copy; <c>null</c> for the embedded one.</param>
    static TexturePlan Plan(string kernel, (int Inputs, ImmutableArray<TextureParameter> Parameters) shape, string? authored) {
        var images = ImmutableArray.CreateBuilder<TextureImage>();

        for (var input = 0; input < shape.Inputs; input++) {
            images.Add(new(TextureFormat.Rgba8, External: true));
        }

        images.Add(new(TextureFormat.Rgba8));

        return new() {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = images.ToImmutable(),
            Ops = [
                new() {
                    Kernel = kernel,
                    Output = shape.Inputs,
                    Inputs = [.. Enumerable.Range(0, shape.Inputs)],
                    Parameters = shape.Parameters
                }
            ],
            Outputs = [shape.Inputs],
            Kernels = authored is null
                ? ImmutableDictionary<string, string>.Empty
                : ImmutableDictionary<string, string>.Empty.Add(kernel, authored)
        };
    }

    /// <summary>Evaluates one such plan, every input reading the one uploaded texture.</summary>
    /// <remarks>
    ///     Every input image is the same texture, which is what makes the two bakes comparable: a
    ///     kernel that blends two inputs blends one picture with itself, and the answer is still a
    ///     picture the kernel's own store decides.
    /// </remarks>
    static Bitmap Bake(
        TexturePlanEvaluator evaluator,
        TexturePlan plan,
        TextureHandle texture,
        int inputs
    ) {
        Assert.Empty(plan.Validate());

        Dictionary<int, TextureExternal> externals = [];

        for (var input = 0; input < inputs; input++) {
            externals[input] = new(texture, TextureKernelHarness.SourceUsage);
        }

        using var bake = evaluator.Evaluate(plan, externals);

        // One dispatch, so "the picture changed" is a claim about a kernel that ran once.
        Assert.Equal(1, bake.Dispatches);

        return bake.Read(inputs);
    }

    /// <summary>What one kernel binds and declares, read off the compiled effect.</summary>
    /// <remarks>
    ///     ⚠ <b>Off the effect rather than off a list, so a kernel that grew a texture or a member is
    ///     covered the day it does.</b> The defaults are parsed from the source because a compiled
    ///     effect does not carry one — the same split, and the same regex, as
    ///     <c>TextureNodeLibraryTests.Members</c> — and a member the regex missed falls back to zero
    ///     rather than being dropped, because a member the op omits is an exception about a uniform
    ///     and would read here as a broken kernel.
    /// </remarks>
    static (int Inputs, ImmutableArray<TextureParameter> Parameters) Shape(string kernel) {
        var data = Compile(kernel);

        var inputs = data.Bindings.Count(binding =>
            binding is { Set: DescriptorSetSlot.PerMaterial, Kind: DescriptorKind.SampledTexture }
        );

        var defaults = Defaults(kernel);
        var parameters = ImmutableArray.CreateBuilder<TextureParameter>();

        foreach (var member in data.Parameters.Where(member => member.Set == DescriptorSetSlot.PerMaterial)) {
            var name = Unqualified(member.Name, data.ShaderName);

            // The evaluator fills this one itself, from TexturePlan.SeedFor, and an op that carried
            // it would be writing over the value the plan decided.
            if (string.Equals(name, "seed", StringComparison.Ordinal)) {
                continue;
            }

            parameters.Add(new(name, defaults.GetValueOrDefault(name)));
        }

        return (inputs, parameters.ToImmutable());
    }

    /// <summary>The kernel's Raven, with every store redirected through a perturbing function.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Three rewrites, each of which is asserted to have happened.</b> The
    ///         <c>shader</c> declaration is renamed, because the evaluator looks the shader up inside
    ///         the source by the op's kernel name; every <c>target.Store(</c> becomes a call to a
    ///         function this adds; and that function is appended inside the shader block, before its
    ///         closing brace.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every statement in the injected function is on a line of its own, because in
    ///         Raven a newline ends a statement.</b> A body written as one expression across four
    ///         lines is four statements, three of them discarded — silently, and in shipped shaders
    ///         before now.
    ///     </para>
    /// </remarks>
    static string Perturb(string kernel) {
        var source = TextureKernels.Source(kernel);
        var declaration = $"shader {kernel} ";

        Assert.Contains(declaration, source, StringComparison.Ordinal);

        var renamed = source.Replace(declaration, $"shader {kernel}{Suffix} ", StringComparison.Ordinal);
        var stores = renamed.Split("target.Store(").Length - 1;

        // A kernel that stored through something else would be perturbed by nothing, and the two
        // pictures would agree for a reason that has nothing to do with the kernel's sensitivity.
        Assert.True(stores > 0, $"{kernel} never calls target.Store, so this generated sabotage perturbs nothing.");

        var redirected = renamed.Replace("target.Store(", "SabotageStore(", StringComparison.Ordinal);
        var close = redirected.LastIndexOf('}');

        Assert.True(close > 0, $"{kernel} has no closing brace to append to, which is not a shader.");

        const string Function = """

                /// Doc 48 exit criterion 4's perturbation, injected by TextureKernelSabotageTests.
                ///
                /// Bounded, and with a different fixed point per channel: a picture survives
                /// `1 - saturate(v * 0.5 + c)` only by being one exact colour everywhere.
                func SabotageStore(at: int2, value: float4) {
                    val red = 1f - saturate(value.x * 0.5f + 0.125f)
                    val green = 1f - saturate(value.y * 0.5f + 0.25f)
                    val blue = 1f - saturate(value.z * 0.5f + 0.375f)
                    val alpha = 1f - saturate(value.w * 0.5f + 0.5f)

                    target.Store(at, float4(red, green, blue, alpha))
                }

            """;

        return redirected[..close] + Function + redirected[close..];
    }

    static readonly Regex Declaration = new(
        @"^\s{4}var\s+(?<name>[A-Za-z][A-Za-z0-9_]*)\s*:\s*(?<type>int|uint|float)\s*=\s*(?<value>-?[0-9]+(?:\.[0-9]+)?)f?\s*$",
        RegexOptions.Multiline
    );

    static Dictionary<string, float> Defaults(string kernel) {
        Dictionary<string, float> members = new(StringComparer.Ordinal);

        foreach (Match match in Declaration.Matches(TextureKernels.Source(kernel))) {
            members[match.Groups["name"].Value] =
                float.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
        }

        return members;
    }

    static readonly Dictionary<string, EffectData> Compiled = new(StringComparer.Ordinal);

    /// <summary>One kernel through the real Raven front end, with no device.</summary>
    static EffectData Compile(string kernel) {
        lock (Compiled) {
            if (Compiled.TryGetValue(kernel, out var cached)) {
                return cached;
            }

            var name = TextureKernels.VariantName(kernel, TextureFormat.Rgba8);
            var source = TextureKernels.Variant(kernel, TextureFormat.Rgba8);
            var data = RavenEffectCompiler.FromSources([(name, source)]).TryGet(EffectKey.Of(kernel));

            Assert.NotNull(data);

            Compiled[kernel] = data;

            return data;
        }
    }

    static string Unqualified(string name, string shader) =>
        name.Length > shader.Length + 1
        && name.StartsWith(shader, StringComparison.Ordinal)
        && name[shader.Length] == '.'
            ? name[(shader.Length + 1)..]
            : name;
}
