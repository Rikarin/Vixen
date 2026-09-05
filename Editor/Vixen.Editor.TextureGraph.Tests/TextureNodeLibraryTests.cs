// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.ShaderCompiler;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     The node <em>library</em>: whether every kernel this assembly ships has a node, and whether
///     every node emits an op its kernel can actually run. No device.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § M4's real risk is not that a node is wrong — it is that a kernel has no node
///         at all.</b> Thirty-eight finished compute shaders nobody can reach from a graph would be
///         the largest instance this repository has had of its commonest defect, and nothing in a
///         compilation would say so: every test would pass, over the nodes that exist.
///         <see cref="Every_kernel_has_a_node_or_a_written_reason_not_to" /> is what closes that, and
///         it reads both surfaces rather than keeping a list of either — the kernels come out of the
///         assembly by reflection, exactly as <c>TextureColourKernelTests.Declared</c> finds them,
///         and the nodes come out of a plan the whole library actually compiles to.
///     </para>
///     <para>
///         ⚠ <b>Ask what this file prints on the day the fixture stops covering the library.</b> That
///         is the failure that would make every assertion here vacuous — a node added and not wired
///         into <see cref="Wire" /> is a node nothing below looks at, and its kernel would then read
///         as unnoded and fail the roll call for the wrong reason. So
///         <see cref="The_fixture_uses_every_node_type_the_assembly_declares" /> compares the paths
///         the fixture asked for against the registry's own, in both directions, and it is the first
///         thing to read when something here goes red.
///     </para>
/// </remarks>
public class TextureNodeLibraryTests {
    /// <summary>The graph the whole library compiles to, and what it is authored at.</summary>
    const int Side = 256;

    /// <summary>
    ///     Every kernel that deliberately has no node, and why.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A kernel reached only as a step of some node's chain is <em>not</em> on this
    ///         list.</b> <c>JumpFlood</c> and <c>FloodBounds</c> have no node of their own and never
    ///         will — how many of them there are is the node's business — but <c>Analysis/Distance</c>
    ///         and <c>Analysis/Flood Fill</c> dispatch them, so a graph reaches them and the roll call
    ///         counts them as covered. That is the distinction worth keeping: the question is whether
    ///         a kernel is reachable from a graph, not whether it has a class named after it.
    ///     </para>
    ///     <para>
    ///         <b>So everything below is genuinely unreachable</b>, and each entry says what would
    ///         have to change. All but one are blocked on something a node cannot ask
    ///         <c>TextureGraphCompiler</c> for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The list is checked in both directions.</b> A name here that is not a kernel is a
    ///         typo that would silently excuse a real gap, and a name here whose node was written
    ///         later would go on excusing a kernel that is in fact reachable — so the roll call
    ///         requires each of these to be a kernel the fixture's plan does <em>not</em> dispatch.
    ///     </para>
    /// </remarks>
    static readonly (string Kernel, string Reason)[] Unnoded = [
        ("FloodResidual",
            "The truncation report: whether the last propagation iteration changed anything. ⚠ The gap it was "
            + "blocked on is gone — #733 landed, and Colour/Auto Levels emits exactly the MinMaxReduce ladder the "
            + "residual would reduce over — so what is missing now is the other half: an author has nowhere to "
            + "*read* one texel of a bake. Analysis/Flood Fill would need a second output whose whole content is "
            + "a number, and a plan output is a picture. See FloodFillNode's remarks.")
    ];

    /// <summary>
    ///     Where a node's default differs from the default its kernel declares, and why.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A node's default and its kernel's are two answers to one question, and nothing makes
    ///     them agree.</b> The evaluator writes every parameter of every op, so a <c>.rvn</c>'s
    ///     initializer is only ever read by a person — which is exactly the arrangement that drifts,
    ///     and silently, because both numbers draw a picture. This is the list of the disagreements
    ///     that are meant, and every entry is a decision somebody should be able to defend;
    ///     everything else is required to match, which is what makes a mistyped default red rather
    ///     than merely different.
    /// </remarks>
    static readonly Dictionary<(string Kernel, string Parameter), string> Deliberate = new() {
        [("Uniform", "red")] = "A source node dropped in unwired should be visible. The kernel's zero is black, "
            + "which is indistinguishable from a node that never ran.",
        [("Uniform", "green")] = "As red.",
        [("Uniform", "blue")] = "As red.",
        [("Blur", "radius")] = "The kernel's 1 is a blur nobody can see at any bake resolution; the node's 8 is "
            + "the width a Blur dropped into a graph is expected to have.",
        [("Blur", "stepX")] = "Not a default at all: the second dispatch of the separable pair sets the axis.",
        [("Blur", "stepY")] = "As stepX.",
        [("BlurHq", "stepX")] = "As Blur's: the vertical pass of the separable pair.",
        [("BlurHq", "stepY")] = "As stepX.",
        [("Levels", "dither")] = "One 8-bit step by default where the kernel defaults to none. A levels curve that "
            + "lifts a narrow range bands an 8-bit output, and a bake is a file, so the banding is permanent. The "
            + "kernel defaults to zero because a hand-built plan calls it too; this is the authoring default.",
        [("JumpFlood", "first")] = "Not a default: set per pass by Analysis/Distance's chain, so that the seeding "
            + "dispatch reads the mask and every one after it reads the previous record.",
        [("JumpFlood", "step")] = "Halved per pass by the chain. Its whole point is that it is not constant.",
        [("FloodBounds", "first")] = "As JumpFlood's: the first iteration of Analysis/Flood Fill's propagation "
            + "reads the mask and the rest read the record.",
        [("ChannelShuffle", "sourceG")] = "The compiler's inserted splat, not a node: doc 48 § Part 4's grey-into-"
            + "colour promotion writes the first input's red on all three colour lanes.",
        [("ChannelShuffle", "sourceB")] = "As sourceG.",
        [("ChannelShuffle", "sourceA")] = "As sourceG — a constant one, because a splatted zero would make every "
            + "promoted image invisible to the Blend the promotion exists for.",
        [("MinMaxReduce", "first")] = "As JumpFlood's: set per rung by Colour/Auto Levels' ladder, so that the "
            + "first dispatch reads grey values and every one after it reads an already-reduced (min, max) pair. "
            + "Leaving it 1 on a later rung reduces the minimum against itself and loses the maximum, which is a "
            + "picture stretched from (min, min) — black."
    };

    /// <summary>The nine usages, one per <c>Output</c> node the fixture places.</summary>
    static readonly string[] Usages = [
        "baseColor",
        "normal",
        "roughness",
        "metalness",
        "occlusion",
        "height",
        "emissive",
        "opacity",
        "mask"
    ];

    /// <summary>
    ///     A node type the fixture does not place, and the reason it cannot be placed by a fixture.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>An entry here is a claim, not a waiver.</b> The Pixel Processor's whole input is a
    ///     Raven expression an author writes, so a fixture that wired one would be asserting against a
    ///     shader this file made up — and doc 48 § D6's point is that the expression goes through the
    ///     real compiler with its diagnostics mapped back to the node, which is
    ///     <c>TextureGraphExpressionTests</c>' subject rather than this file's.
    /// </remarks>
    static readonly string[] Unwired = ["Filters/Pixel Processor"];

    /// <summary>Every node type the fixture places is one the assembly declares, and the reverse.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The instrument for everything else in this file.</b> A node type nobody wires into
    ///         <see cref="Wire" /> contributes no op, so its kernel reads as unnoded and the roll call
    ///         below fails describing the wrong problem. This one names it exactly.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The exemption exists because this assertion went red on a merge and not on a
    ///         branch.</b> Two slices added nodes to one assembly in one batch; each was exhaustive
    ///         against what it could see, and neither could see the other. So the shape that survives
    ///         is not "everything is wired" but "everything is wired or is on a list that says why" —
    ///         which is the same answer the kernel roll call reached, and it still fails loudly for a
    ///         node somebody merely forgot.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_fixture_uses_every_node_type_the_assembly_declares() {
        var wiring = new Wiring();

        Wire(wiring);

        var declared = Registry().Types.Select(type => type.Path)
            .Except(Unwired, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var used = wiring.Used.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(declared, used);

        // The exemption is a list of node types, so a path nobody declares any more is a line to
        // delete rather than a silent allowance that outlives what it excused.
        Assert.All(Unwired, path => Assert.Contains(path, Registry().Types.Select(type => type.Path)));
    }

    /// <summary>
    ///     Every kernel this assembly ships is dispatched by some node, or is on the written list of
    ///     the ones that are not — with the reason.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The kernels are the <em>embedded</em> ones</b> —
    ///         <see cref="TextureKernels.Names" />, built from this assembly's <c>Shaders/*.rvn</c>
    ///         manifest resources. A kernel exists because a file ships, so that is what the roll call
    ///         is taken over, and a slice that ships a forty-sixth <c>.rvn</c> with no node for it
    ///         fails here by existing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It used to read the static <c>All</c> declarations instead, and that left a hole
    ///         a kernel was already in</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/746">#746</a>. The union of the
    ///         <c>All</c> surfaces was 44 names against 45 embedded kernels, and the difference was
    ///         <c>Levels</c>: <c>Shaders/Levels.rvn</c> ships, <c>FilterNodes.cs</c> dispatches it by
    ///         a bare string literal, and no <c>All</c> mentions it. A declaration is something a
    ///         slice can forget, so a roll call that reads declarations cannot see the thing it
    ///         exists to find — this file's own claim that "a slice that ships a forty-sixth kernel
    ///         and no node for it fails here by existing" was false while the declarations were the
    ///         source.
    ///     </para>
    ///     <para>
    ///         The declarations are still read, in the other direction: every name a slice declares
    ///         has to be a kernel that is actually embedded, which is what makes a typo in an
    ///         <c>All</c> red rather than a silently excused gap.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the exemptions are checked from both ends.</b> A kernel that acquires a node
    ///         must come off the list, or the list would go on excusing something that is no longer
    ///         missing — the class of dead exemption that makes a gate report success on the day it
    ///         stops doing anything.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_kernel_has_a_node_or_a_written_reason_not_to() {
        var plan = Library();
        var reached = plan.Ops.Select(op => op.Kernel).ToHashSet(StringComparer.Ordinal);
        var shipped = TextureKernels.Names.Order(StringComparer.Ordinal).ToArray();
        var declared = Declared().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        var cpu = CpuOperations();

        Assert.NotEmpty(shipped);
        Assert.NotEmpty(declared);

        // ⚠ Every declaring surface names something this assembly actually ships, and there are two
        // kinds of thing it can name. A kernel is embedded; a CPU operation is not and never will be,
        // because doc 48 § 4.6's `Normal → Height` names no `.rvn` at all. Before this partition
        // existed the second kind read as the first and failed here saying a shader had gone missing,
        // which is the wrong sentence about the right fact — and a category a roll call cannot say the
        // name of is the shape this repository's gates go quiet in.
        //
        // ⚠ It walks the DECLARATIONS here and the EMBEDDED kernels below, and both directions are
        // load-bearing: a name in an `All` that no `.rvn` answers to is a typo that would reach the
        // compiler as a missing-shader exception at bake time, and an `.rvn` in no `All` was invisible
        // to this file entirely — which is what #746 found, with `Levels` already in the hole.
        foreach (var name in declared) {
            if (cpu.Contains(name)) {
                // ⚠ And a CPU operation must *not* be embedded, which is § D3's ban on a CPU twin made
                // mechanical: an implementation that reproduced what some kernel already does would
                // arrive named after it, and it would turn every parity test into a claim that two
                // transcriptions agree.
                Assert.DoesNotContain(name, TextureKernels.Names);

                continue;
            }

            Assert.Contains(name, shipped, StringComparer.Ordinal);
        }

        var excused = Unnoded.Select(entry => entry.Kernel).Order(StringComparer.Ordinal).ToArray();
        var unreached = shipped.Where(kernel => !reached.Contains(kernel)).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(excused, unreached);

        // And no entry is a placeholder: each says something about why.
        Assert.All(Unnoded, entry => Assert.True(entry.Reason.Length > 40, entry.Kernel));
    }

    /// <summary>
    ///     Every op the library emits carries exactly the parameters its kernel declares, and every
    ///     parameter the kernel declares is one the op carries.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Both directions, because they fail differently.</b> A member the op omits is an
    ///     exception at bake time in a background task, with a message about a uniform; a parameter
    ///     the kernel does not declare is <em>silently dropped</em> and the picture is drawn with the
    ///     kernel's own default. The second is the plausible-picture failure, and it is exactly what
    ///     a node spelling <c>maxRadius</c> where the kernel says <c>radius</c> produces.
    /// </remarks>
    [Fact]
    public void Every_op_the_library_emits_carries_exactly_its_kernel_parameters() {
        var plan = Library();

        Assert.NotEmpty(plan.Ops);

        foreach (var kernel in plan.Ops.Select(op => op.Kernel).Distinct(StringComparer.Ordinal)) {
            // ⚠ A CPU operation has no uniform block to read, so the second source is its own builder
            // rather than a `.rvn`. That is a weaker check than the one above it — two things in the
            // same assembly rather than a C# surface against a shader — and it is still the check that
            // matters: a node spelling `iterations` where the operation reads `iteration` falls
            // through to the operation's own fallback and draws a picture at the default budget,
            // silently. It is stated as the weaker claim rather than skipped.
            var declared = Cpu(kernel) is { } builder
                ? builder.Parameters.Select(parameter => parameter.Name).Order(StringComparer.Ordinal).ToArray()
                : Members(kernel).Keys.Order(StringComparer.Ordinal).ToArray();

            foreach (var op in plan.Ops.Where(candidate => candidate.Kernel == kernel)) {
                Assert.Equal(
                    declared,
                    op.Parameters.Select(parameter => parameter.Name).Order(StringComparer.Ordinal).ToArray()
                );
            }
        }
    }

    /// <summary>Every op the library emits reads as many images as its kernel binds textures.</summary>
    /// <remarks>
    ///     The other half of the binding contract: the evaluator binds an op's inputs positionally
    ///     over the kernel's sampled textures, so a node that named three inputs where the kernel
    ///     declares four leaves a descriptor unwritten — a validation error at best, and whatever was
    ///     in the set at worst. It is the assertion the optional map ports of the two placement nodes
    ///     rest on.
    /// </remarks>
    [Fact]
    public void Every_op_the_library_emits_reads_as_many_images_as_its_kernel_binds() {
        var plan = Library();

        foreach (var kernel in plan.Ops.Select(op => op.Kernel).Distinct(StringComparer.Ordinal)) {
            // A CPU operation binds no descriptors — it is handed its inputs as bytes — so what it is
            // held to is the count its own builder emits, which is what `Run` indexes.
            var textures = Cpu(kernel) is { } builder
                ? builder.Inputs.Length
                : Compile(kernel).Bindings.Count(binding =>
                    binding is { Set: DescriptorSetSlot.PerMaterial, Kind: DescriptorKind.SampledTexture }
                );

            foreach (var op in plan.Ops.Where(candidate => candidate.Kernel == kernel)) {
                Assert.Equal(textures, op.Inputs.Length);
            }
        }
    }

    /// <summary>
    ///     A node's declared default is its kernel's declared default, except where the disagreement
    ///     is written down.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Nothing else can catch this.</b> The evaluator writes every parameter of every
    ///         op, so a <c>.rvn</c>'s initializer never reaches a device and a node whose default
    ///         drifted from it is not a failure anywhere — it is a graph that does something
    ///         slightly different from what the kernel's own documentation says it does, which
    ///         nobody discovers until they compare the two files.
    ///     </para>
    ///     <para>
    ///         <b>The kernel's defaults are parsed out of the committed Raven source</b>, which is
    ///         the same move the channel-selector test makes: read the thing, do not keep a second
    ///         list of it. The fixture authors nothing but the nine output usages, so every number in
    ///         its plan is a declared default.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_nodes_default_is_its_kernels_default_unless_the_difference_is_written_down() {
        var plan = Library();
        var differences = new List<string>();

        foreach (var op in plan.Ops) {
            // The same second source as above: a CPU operation's "declared default" is what its
            // builder's optional arguments produce, which is the number a node naming no port got.
            var members = Cpu(op.Kernel) is { } builder
                ? builder.Parameters.ToDictionary(
                    parameter => parameter.Name,
                    parameter => parameter.Value,
                    StringComparer.Ordinal
                )
                : Members(op.Kernel);

            foreach (var parameter in op.Parameters) {
                if (!members.TryGetValue(parameter.Name, out var declared)
                    || Math.Abs(declared - parameter.Value) < 1e-6f
                    || Deliberate.ContainsKey((op.Kernel, parameter.Name))) {
                    continue;
                }

                differences.Add(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"'{op.Kernel}' declares {parameter.Name} = {declared} and the node emits {parameter.Value}."
                    )
                );
            }
        }

        Assert.Equal([], differences.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

        // ⚠ And the list only shrinks. An entry whose two numbers have come back into agreement is an
        // exemption that has stopped doing anything, which is how a check quietly becomes decoration.
        foreach (var ((kernel, parameter), reason) in Deliberate) {
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{kernel}.{parameter} is excused with no reason.");

            Assert.Contains(
                plan.Ops.Where(op => op.Kernel == kernel).SelectMany(op => op.Parameters),
                emitted => emitted.Name == parameter
                    && Cpu(kernel) is null
                    && Members(kernel).TryGetValue(parameter, out var declared)
                    && Math.Abs(declared - emitted.Value) >= 1e-6f
            );
        }
    }

    /// <summary>The plan the whole library compiles to holds together on its own terms.</summary>
    [Fact]
    public void The_plan_the_whole_library_compiles_to_validates() {
        var plan = Library();

        Assert.Empty(plan.Validate());
        Assert.Equal(Usages.Length, plan.Outputs.Length);
    }

    /// <summary>
    ///     Every op a node emits is an embedded kernel or a declared CPU operation, and exactly one of
    ///     the two.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A node naming a kernel that is not there is an exception at evaluation about an
    ///         embedded resource, three frames from anything an author can select. It is the one
    ///         mistake in a node that
    ///         <see cref="Every_op_the_library_emits_carries_exactly_its_kernel_parameters" /> would
    ///         report as a compiler failure rather than as what it is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>"Exactly one of the two" is the half that needed writing.</b> Doc 48 § 4.11 counts
    ///         a third category — "not a kernel: one" — and until it existed the roll calls in this
    ///         file knew of two kinds of thing, so an op carrying a <see cref="TextureOp.Cpu" /> would
    ///         have read as a kernel whose <c>.rvn</c> had gone missing and the failure would have
    ///         described the wrong problem. What is asserted is a partition rather than an allowance:
    ///         an op that carries a CPU operation must not also be embedded, and one that does not
    ///         must be.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_op_a_node_emits_is_a_kernel_or_a_cpu_operation_and_not_both() {
        var ops = Library().Ops;
        var cpu = CpuOperations();

        // ⚠ Not vacuous, and it is worth saying so rather than trusting the fixture: the partition
        // below is only interesting while the library actually contains one of each kind.
        Assert.Contains(ops, op => op.Cpu is not null);
        Assert.Contains(ops, op => op.Cpu is null);

        foreach (var op in ops) {
            if (op.Cpu is null) {
                Assert.Contains(op.Kernel, TextureKernels.Names);
                Assert.DoesNotContain(op.Kernel, cpu);

                continue;
            }

            Assert.DoesNotContain(op.Kernel, TextureKernels.Names);

            // And it is *declared*, not merely present: an operation reachable from a node but named
            // by no `All` surface is invisible to every roll call in this assembly, which is the
            // failure this whole file is written against.
            Assert.Contains(op.Kernel, cpu);
        }
    }

    /// <summary>The declared builder for one CPU operation, or null when the name is a kernel's.</summary>
    /// <remarks>
    ///     ⚠ <b>Derived from the ops rather than from a list of names</b>, so a slice adding a second
    ///     CPU operation is classified by existing. An op is a CPU operation exactly when it carries a
    ///     <see cref="TextureOp.Cpu" />, which is the same fact <c>TexturePlanEvaluator</c> branches
    ///     on when it decides whether to end the command list.
    /// </remarks>
    static TextureOp? Cpu(string kernel) =>
        DeclaredOps()
            .FirstOrDefault(op => op.Cpu is not null && string.Equals(op.Kernel, kernel, StringComparison.Ordinal));

    /// <summary>The names of every declared CPU operation.</summary>
    static IReadOnlyCollection<string> CpuOperations() =>
        DeclaredOps().Where(op => op.Cpu is not null).Select(op => op.Kernel).ToHashSet(StringComparer.Ordinal);

    /// <summary>Every op any declaring surface in this assembly builds.</summary>
    static IEnumerable<TextureOp> DeclaredOps() {
        foreach (var type in typeof(TextureKernels).Assembly.GetTypes()) {
            if (type.GetProperty("All", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) is not
                { } all) {
                continue;
            }

            if (all.GetValue(null) is not IEnumerable<TextureOp> ops) {
                continue;
            }

            foreach (var op in ops) {
                yield return op;
            }
        }
    }

    /// <summary>
    ///     An unwired optional map takes the required input; a wired one takes what is wired.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Both halves, because each is a different silent failure.</b> A fallback that did not
    ///     fire is a <c>TG0002</c> and a graph that will not compile — loud. A fallback that fired
    ///     even where an edge exists is a splatter that reads its own pattern as its mask, drawn
    ///     without a word: the placement kernels' four map slots are all bound to *something*
    ///     whatever happens, so the input count says nothing about which.
    /// </remarks>
    [Fact]
    public void An_unwired_optional_map_binds_the_required_input_and_a_wired_one_does_not() {
        var plan = Library();
        var sampler = Assert.Single(plan.Ops, op => op.Kernel == "TileSampler");
        var splatter = Assert.Single(plan.Ops, op => op.Kernel == "Splatter");

        // Nothing is wired to either node's maps, so every slot is the pattern. The lengths are
        // asserted first because `All` over one element is true of anything.
        Assert.Equal(4, sampler.Inputs.Length);
        Assert.Equal(5, splatter.Inputs.Length);
        Assert.All(sampler.Inputs, input => Assert.Equal(sampler.Inputs[0], input));
        Assert.All(splatter.Inputs, input => Assert.Equal(splatter.Inputs[0], input));

        // And a wired optional port reads its edge. There are exactly two ChannelShuffle ops in this
        // plan and they are told apart by that: the compiler's promotion binds one grey image twice,
        // and the node's Second is wired to something else.
        var shuffles = plan.Ops.Where(op => op.Kernel == "ChannelShuffle").ToArray();

        Assert.Equal(2, shuffles.Length);
        Assert.All(shuffles, shuffle => Assert.Equal(2, shuffle.Inputs.Length));
        Assert.Single(shuffles, shuffle => shuffle.Inputs[0] == shuffle.Inputs[1]);
        Assert.Single(shuffles, shuffle => shuffle.Inputs[0] != shuffle.Inputs[1]);
    }

    /// <summary>
    ///     A filter setting the kernel would silently reinterpret is refused by name.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>#727's defect from the other side.</b> While the <c>Nodes</c> namespace shadowed the
    ///     assembly's <c>TextureFilter</c> with a two-member copy, <c>Box</c> was not a name these
    ///     settings would take and the diagnostic listing what they <em>would</em> take said so.
    ///     Deleting the copy makes <c>Box</c> parse — and both kernels compare <c>filter</c> against
    ///     zero and interpolate for everything else, so it would draw the bilinear picture under the
    ///     name of a box filter.
    /// </remarks>
    [Theory]
    [InlineData("Space/Transform 2D", "Input")]
    [InlineData("Space/Crop", "Input")]
    public void A_box_filter_is_refused_where_the_kernel_would_read_it_as_bilinear(string path, string port) {
        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var node = graph.Add(path);
        var output = graph.Add("Output/Output");

        node.SetText("Filter", "Box");
        graph.Connect(new(noise.Id, "Out"), new(node.Id, port));
        graph.Connect(new(node.Id, "Out"), new(output.Id, "Input"));

        var compilation = Compiler().Compile(graph);
        var refusal = Assert.Single(compilation.Diagnostics, diagnostic => diagnostic.Id == "TG0010");

        Assert.Equal(node.Id, refusal.Node);
        Assert.Equal("Filter", refusal.Port);
        Assert.Null(compilation.Artefact);

        // And the two the kernel does read still compile, so the refusal is about Box and not about
        // the setting having become unreadable.
        node.SetText("Filter", "Bilinear");

        Assert.Empty(Compiler().Compile(graph).Diagnostics);
    }

    /// <summary>A flood fill's budget is refused outside the range the node will emit.</summary>
    /// <remarks>
    ///     Every iteration is a dispatch over the whole image and an image in the plan, so a budget
    ///     typed with an extra digit is a bake that appears to hang. ⚠ The refusal is what a clamp
    ///     would not be: a clamped budget is a truncated flood drawn without a word, which is the
    ///     failure the node's whole four-kernel design exists to avoid.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(257)]
    public void A_flood_fills_budget_is_refused_rather_than_clamped(int iterations) {
        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var flood = graph.Add("Analysis/Flood Fill");
        var output = graph.Add("Output/Output");

        flood.SetValue("Iterations", iterations);
        graph.Connect(new(noise.Id, "Out"), new(flood.Id, "Mask"));
        graph.Connect(new(flood.Id, "Out"), new(output.Id, "Input"));

        var refusal = Assert.Single(Compiler().Compile(graph).Diagnostics, diagnostic => diagnostic.Id == "TG0012");

        Assert.Equal(flood.Id, refusal.Node);
        Assert.Equal("Iterations", refusal.Port);

        // The boundary is where it says it is, and the plan is one propagation dispatch per iteration
        // plus the read — which is what makes the ceiling a cost rather than a taste.
        flood.SetValue("Iterations", 256);

        var plan = Compiler().Compile(graph).Value;

        Assert.Equal(256, plan.Ops.Count(op => op.Kernel == "FloodBounds"));
        Assert.Single(plan.Ops, op => op.Kernel == "FloodFill");
    }

    static TextureGraphCompiler Compiler() =>
        new(Registry()) { BaseWidth = Side, BaseHeight = Side, Seed = 90210 };

    /// <summary>Records the node types a fixture asks for, so the roll call can check it covers them.</summary>
    sealed class Wiring {
        readonly List<string> used = [];

        public NodeGraphModel Graph { get; } = new() { Name = "Library" };

        public IReadOnlyList<string> Used => used;

        public GraphNode Add(string path) {
            used.Add(path);

            return Graph.Add(path);
        }

        public void Connect(GraphNode from, string output, GraphNode to, string input) =>
            Graph.Connect(new(from.Id, output), new(to.Id, input));

        public void Keep(GraphNode from, string usage) {
            var output = Add("Output/Output");

            output.SetText("Usage", usage);
            Connect(from, "Out", output, "Input");
        }
    }

    /// <summary>
    ///     One graph using every node type the assembly declares, authored at nothing but its output
    ///     usages.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Nothing here sets a port or a setting, deliberately.</b> Every number in the resulting
    ///     plan is therefore a declared default, which is what makes
    ///     <see cref="A_nodes_default_is_its_kernels_default_unless_the_difference_is_written_down" />
    ///     an assertion about the library rather than about this fixture — and it is why a node with a
    ///     default that refuses its own compilation would be caught here rather than shipped.
    /// </remarks>
    static void Wire(Wiring graph) {
        // The grey side: a lattice, a pattern and a board, each starting a chain.
        var noise = graph.Add("Source/Noise");
        var shape = graph.Add("Source/Shape");
        var checker = graph.Add("Source/Checker");
        var levels = graph.Add("Colour/Levels");
        var blur = graph.Add("Filters/Blur");
        var blurHq = graph.Add("Filters/Blur HQ");
        var mirror = graph.Add("Space/Mirror");
        var tile = graph.Add("Space/Tile");
        var crop = graph.Add("Space/Crop");

        graph.Connect(noise, "Out", levels, "Input");
        graph.Connect(levels, "Out", blur, "Input");
        graph.Connect(shape, "Out", blurHq, "Input");
        graph.Connect(checker, "Out", mirror, "Input");
        graph.Connect(mirror, "Out", tile, "Input");
        graph.Connect(tile, "Out", crop, "Input");

        // The two chains whose op count depends on the bake, and the mask they measure.
        var edge = graph.Add("Analysis/Edge Detect");
        var distance = graph.Add("Analysis/Distance");
        var flood = graph.Add("Analysis/Flood Fill");

        graph.Connect(crop, "Out", edge, "Input");
        graph.Connect(blur, "Out", distance, "Mask");
        graph.Connect(edge, "Out", flood, "Mask");

        // The filter chain, ending in something with relief in it.
        var emboss = graph.Add("Filters/Emboss");
        var sharpen = graph.Add("Filters/Sharpen");
        var directionalBlur = graph.Add("Filters/Directional Blur");
        var radialBlur = graph.Add("Filters/Radial Blur");
        var nonUniformBlur = graph.Add("Filters/Non-Uniform Blur");
        var warp = graph.Add("Filters/Warp");
        var directionalWarp = graph.Add("Filters/Directional Warp");
        var slopeBlur = graph.Add("Filters/Slope Blur");

        graph.Connect(blurHq, "Out", emboss, "Height");
        graph.Connect(emboss, "Out", sharpen, "Input");
        graph.Connect(sharpen, "Out", directionalBlur, "Input");
        graph.Connect(directionalBlur, "Out", radialBlur, "Input");
        graph.Connect(radialBlur, "Out", nonUniformBlur, "Input");
        graph.Connect(flood, "Out", nonUniformBlur, "Radius Map");
        graph.Connect(nonUniformBlur, "Out", warp, "Input");
        graph.Connect(distance, "Out", warp, "Warp");
        graph.Connect(warp, "Out", directionalWarp, "Input");
        graph.Connect(distance, "Out", directionalWarp, "Warp");
        graph.Connect(directionalWarp, "Out", slopeBlur, "Input");
        graph.Connect(distance, "Out", slopeBlur, "Slope");

        // The surface group, all of it downstream of one height.
        var heightToNormal = graph.Add("Surface/Height to Normal");
        var normalTransform = graph.Add("Surface/Normal Transform");
        var normalCombine = graph.Add("Surface/Normal Combine");
        var curvature = graph.Add("Surface/Curvature");
        var occlusion = graph.Add("Surface/Ambient Occlusion");

        graph.Connect(slopeBlur, "Out", heightToNormal, "Height");
        graph.Connect(heightToNormal, "Out", normalTransform, "Input");
        graph.Connect(heightToNormal, "Out", normalCombine, "Base");
        graph.Connect(normalTransform, "Out", normalCombine, "Detail");
        graph.Connect(normalTransform, "Out", curvature, "Normal");

        // ⚠ The one node in the library that is not a dispatch, wired *between* two that are. Doc
        // 48 § 4.6's `Normal → Height` is a Poisson solve on the CPU, so this edge is what makes the
        // fixture's plan contain a `TextureOp.Cpu` at all — and it is deliberately in the middle of a
        // chain rather than at its end, because the seams that break are the two either side of it.
        var normalToHeight = graph.Add("Surface/Normal to Height");

        graph.Connect(heightToNormal, "Out", normalToHeight, "Normal");
        graph.Connect(normalToHeight, "Out", occlusion, "Height");

        // The colour side. ⚠ The blend takes a colour and a grey, so the compiler's promotion is on
        // this path too — the one op in the plan no node emitted.
        var uniform = graph.Add("Source/Uniform");
        var transform = graph.Add("Space/Transform 2D");
        var hsl = graph.Add("Colour/HSL");
        var invert = graph.Add("Colour/Invert");
        var blend = graph.Add("Colour/Blend");
        var grayscale = graph.Add("Colour/Grayscale");
        var shuffle = graph.Add("Colour/Channel Shuffle");
        var vectorWarp = graph.Add("Filters/Vector Warp");

        graph.Connect(uniform, "Out", transform, "Input");
        graph.Connect(transform, "Out", hsl, "Input");
        graph.Connect(hsl, "Out", invert, "Input");
        graph.Connect(invert, "Out", blend, "Background");
        graph.Connect(blur, "Out", blend, "Foreground");
        graph.Connect(blend, "Out", grayscale, "Input");
        graph.Connect(blend, "Out", shuffle, "First");
        graph.Connect(normalCombine, "Out", shuffle, "Second");
        graph.Connect(shuffle, "Out", vectorWarp, "Input");
        graph.Connect(normalCombine, "Out", vectorWarp, "Vectors");

        // The placement pair, with every map port left unwired — which is the arrangement the
        // library's own remarks say is the common one, so it is the one the fixture proves compiles.
        var sampler = graph.Add("Placement/Tile Sampler");
        var splatter = graph.Add("Placement/Splatter");

        graph.Connect(shape, "Out", sampler, "Pattern");
        graph.Connect(shape, "Out", splatter, "Pattern");

        // The four images no op writes: a picture the caller supplies and three tables baked on the
        // CPU from the editor's own evaluators. Plus the two nodes that need an image at a level of
        // its own — a reduction ladder and a halving.
        var bitmap = graph.Add("Source/Bitmap");
        var gradient = graph.Add("Source/Gradient");
        var curve = graph.Add("Colour/Curve");
        var gradientMap = graph.Add("Colour/Gradient Map");
        var autoLevels = graph.Add("Colour/Auto Levels");
        var resample = graph.Add("Space/Resample");

        // ⚠ The one thing this fixture authors that is not an output usage, and it is not a number:
        // a Bitmap with no asset is the single node in the library whose *default* cannot compile,
        // because there is no picture a reference-less bitmap could draw and inventing a black one
        // would be the silent failure the node refuses. Every number below is still a declared
        // default, which is what keeps the defaults roll call an assertion about the library.
        bitmap.SetText("Source", "Assets/Textures/fixture.png");

        // ⚠ These three chains end in no Output node, which is legal and is what half an author's
        // canvas looks like: an image nothing reads is freed by the pool the moment its last reader
        // has run, and its *op* is still in the plan — which is what this file reads.
        graph.Connect(bitmap, "Out", resample, "Input");
        graph.Connect(gradient, "Out", curve, "Input");

        // Grey into the ladder and out through the ramp, because Gradient Map measures rather than
        // composites: a colour arriving at it is a TG0004 by design.
        graph.Connect(grayscale, "Out", autoLevels, "Input");
        graph.Connect(autoLevels, "Out", gradientMap, "Input");

        graph.Keep(vectorWarp, "baseColor");
        graph.Keep(normalTransform, "normal");
        graph.Keep(grayscale, "roughness");
        graph.Keep(curvature, "metalness");
        graph.Keep(occlusion, "occlusion");
        graph.Keep(slopeBlur, "height");
        graph.Keep(sampler, "emissive");
        graph.Keep(splatter, "opacity");
        graph.Keep(flood, "mask");
    }

    /// <summary>The fixture, compiled, with every diagnostic asserted away.</summary>
    static TexturePlan Library() {
        var wiring = new Wiring();

        Wire(wiring);

        var compiler = Compiler();
        var compilation = compiler.Compile(wiring.Graph);

        Assert.Empty(compilation.Diagnostics);
        Assert.Equal(Usages.Order(StringComparer.Ordinal), compiler.Outputs.Select(output => output.Usage).Order(StringComparer.Ordinal));

        return compilation.Value;
    }

    static NodeTypeRegistry Registry() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        return registry;
    }

    /// <summary>Every kernel name any slice of this assembly declares, found rather than listed.</summary>
    /// <remarks>
    ///     <c>TextureColourKernelTests.Declared</c>'s walk, and deliberately a second copy of it: that
    ///     one asks whether the folder and the declarations agree, this one asks whether the
    ///     declarations and the node library do. Sharing the method would make one file's refactor the
    ///     other's silent change of meaning, and there is nothing to keep in step — the convention it
    ///     reads is "a slice declares its kernels in a static <c>All</c>", which is one line long.
    /// </remarks>
    static IEnumerable<string> Declared() {
        foreach (var type in typeof(TextureKernels).Assembly.GetTypes()) {
            if (type.GetProperty("All", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) is not
                { } all) {
                continue;
            }

            switch (all.GetValue(null)) {
                case IEnumerable<string> names:
                    foreach (var name in names) {
                        yield return name;
                    }

                    break;

                case IEnumerable<TextureOp> ops:
                    foreach (var op in ops) {
                        yield return op.Kernel;
                    }

                    break;
            }
        }
    }

    static readonly Regex Declaration = new(
        @"^\s{4}var\s+(?<name>[A-Za-z][A-Za-z0-9_]*)\s*:\s*(?<type>int|uint|float)\s*=\s*(?<value>-?[0-9]+(?:\.[0-9]+)?)f?\s*$",
        RegexOptions.Multiline
    );

    static readonly Dictionary<string, Dictionary<string, float>> Declarations = new(StringComparer.Ordinal);

    /// <summary>One kernel's uniform members and the defaults its source declares, minus <c>seed</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>Parsed out of the Raven rather than read off the compiled effect, because the compiled
    ///     effect does not carry a default.</b> <c>EffectData.Parameters</c> names the members and
    ///     their sets; the initializer is a property of the source only. So this reads the source —
    ///     and it is checked against the compiled member list below, so a regex that silently matched
    ///     nothing would fail rather than excuse everything.
    /// </remarks>
    static Dictionary<string, float> Members(string kernel) {
        lock (Declarations) {
            if (Declarations.TryGetValue(kernel, out var cached)) {
                return cached;
            }

            Dictionary<string, float> members = new(StringComparer.Ordinal);

            foreach (Match match in Declaration.Matches(TextureKernels.Source(kernel))) {
                var name = match.Groups["name"].Value;

                // The one member the evaluator fills itself, from TexturePlan.SeedFor.
                if (name is "seed") {
                    continue;
                }

                members[name] = float.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
            }

            // ⚠ The instrument for the parse. A kernel whose members the regex missed would look like
            // a kernel with no parameters, and every comparison against it would pass.
            var data = Compile(kernel);

            var compiled = data.Parameters
                .Where(member => member.Set == DescriptorSetSlot.PerMaterial)
                .Select(member => Unqualified(member.Name, data.ShaderName))
                .Where(name => !string.Equals(name, "seed", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(compiled, members.Keys.Order(StringComparer.Ordinal).ToArray());

            Declarations[kernel] = members;

            return members;
        }
    }

    static readonly Dictionary<string, EffectData> Compiled = new(StringComparer.Ordinal);

    /// <summary>One kernel through the real Raven front end, with no device.</summary>
    static EffectData Compile(string kernel) {
        lock (Compiled) {
            if (Compiled.TryGetValue(kernel, out var cached)) {
                return cached;
            }

            var name = TextureKernels.VariantName(kernel, TextureFormat.Rgba16Float);
            var source = TextureKernels.Variant(kernel, TextureFormat.Rgba16Float);
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
