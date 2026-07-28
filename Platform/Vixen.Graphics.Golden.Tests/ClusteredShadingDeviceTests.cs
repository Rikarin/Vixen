// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Rendering.Materials;
using Vixen.ShaderCompiler;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     The other half of Forward+: a fragment reading the list the culler filled.
/// </summary>
/// <remarks>
///     <para>
///         <c>ClusterCullingDeviceTests</c> proves the culler bins what it should. Nothing proved that
///         anything <em>reads</em> it: the clustered variant of <c>ForwardPlus</c> has a
///         <c>positionVS</c> stream that exists in no other variant, a lookup through
///         <c>ClusterGrid.Of</c> whose handedness was wrong until recently, and a light loop over
///         indices — none of which any test had run.
///     </para>
///     <para>
///         <strong>Two things asking for the variant turned up.</strong> A permutation folds
///         <em>code</em> and not bindings, so the clustered variant still declares the per-object
///         light list it never reads, and one with image-based lighting off still declares the
///         environment cube — a host binds all of it or binds nothing. And the pipeline layout an
///         effect was loaded with declared no push-constant range at all, which is what
///         <c>ForwardPlus</c> hands its world matrix through: every object would have drawn at the
///         origin. Neither was reachable without compiling this variant, which nothing did.
///     </para>
/// </remarks>
public class ClusteredShadingDeviceTests {
    /// <summary>The variant under test: clustered, and nothing else switched on.</summary>
    /// <remarks>
    ///     Image-based lighting and shadows off, so set 0 is the block, the light buffer and the
    ///     cluster list — the three things this is about — rather than an environment and an atlas the
    ///     fixture would have to supply to bind anything at all.
    /// </remarks>
    static EffectKey Key(ShaderComposition composition) =>
        EffectKey.Of(
            "ForwardPlus",
            [
                new("ForwardPlus.UseClusteredLights", "true"),
                new("ForwardPlus.UseImageBasedLighting", "false"),
                new("ForwardPlus.UseShadows", "false"),
                new("ForwardPlus.UseReflectionProbe", "false")
            ],
            composition
        );

    /// <summary>
    ///     The clustered variant compiles, and its per-frame set is the culler's output.
    /// </summary>
    /// <remarks>
    ///     The first thing to establish, and not a formality: a variant nobody has ever asked the
    ///     compiler for is a variant nobody knows compiles, and what it binds decides what a frame has
    ///     to supply.
    /// </remarks>
    [Fact]
    public void TheClusteredVariantCompilesAndBindsTheClusterList() {
        var material = Composed();
        var data = Compiler().TryGet(Key(material.Composition));

        Assert.NotNull(data);

        var frame = data!.Bindings
            .Where(binding => binding.Set == DescriptorSetSlot.PerFrame)
            .Select(binding => binding.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Contains("clusters", frame);
        Assert.Contains("lightBuffer", frame);

        // ⚠ And every binding the pass declares is still here, switched off or not: the shadow
        // atlas, the environment, the four probe cubes and their samplers, plus a 1296-byte per-draw
        // block for a light list this variant never reads. Permutations fold *code*, not bindings —
        // which the shader's own comments claim otherwise, and which a frame pays for by having to
        // supply resources nothing samples. See the remarks on this class.
        Assert.Contains("shadowMap", frame);
        Assert.Contains("environment", frame);

        Assert.Contains(
            data.Bindings,
            binding => binding.Set == DescriptorSetSlot.PerDraw && binding.Size == 1296
        );

        // The world matrix is pushed, and the range reaches the runtime — a pipeline layout that
        // declared none would drop every push against it, so every object in the frame would draw at
        // the origin.
        var pushed = Assert.Single(data.PushConstants);

        Assert.Equal(64, pushed.Size);
        Assert.Equal(0, pushed.Offset);
        Assert.True(pushed.Stages.HasFlag(ShaderStage.Vertex));

        // And the attribute locations, which are not zero-based and are not guessable: a shader's
        // `stream` variables take locations before its vertex inputs do, so these four start at five.
        // A pipeline described against 0 to 3 is refused outright — which is the one merciful failure
        // in this family, the other two being silent.
        Assert.Equal(
            [("position", 5), ("normal", 6), ("tangent", 7), ("texcoord", 8)],
            data.VertexInputs.Select(input => (input.Name, input.Location)).ToArray()
        );

        Assert.Equal(ShaderValueKind.Float4, data.VertexInputs.Single(input => input.Name == "tangent").Kind);
    }

    /// <summary>The material whose composition the pass is compiled against.</summary>
    /// <remarks>
    ///     Through <see cref="MaterialCompiler" /> rather than by hand, because it binds <em>every</em>
    ///     slot the library declares and not only the two this pass has — the material shaders compose
    ///     into each other, and an unbound slot anywhere in the tree is a compile error for all of it.
    /// </remarks>
    static Material Composed() {
        var compilation = MaterialCompiler.Compile(
            new() { ShaderName = "ForwardPlus", Features = [new MetalRoughnessFeature()] }
        );

        Assert.False(
            compilation.Failed,
            string.Join(Environment.NewLine, compilation.Diagnostics.Select(diagnostic => diagnostic.ToString()))
        );

        return compilation.Material!;
    }

    /// <summary>The compiler, over the whole library — the material shaders included this time.</summary>
    static RavenEffectCompiler Compiler() =>
        new(Directory.GetFiles(Library(), "*.rvn", SearchOption.AllDirectories));

    /// <summary>The shader library, found by walking up rather than by counting directories.</summary>
    static string Library() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            var candidate = Path.Combine(directory.FullName, "Raven", "Library");

            if (Directory.Exists(candidate)) {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException($"Raven/Library was not found above '{AppContext.BaseDirectory}'.");
    }
}
