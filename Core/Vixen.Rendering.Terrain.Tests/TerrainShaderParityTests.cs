// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Vixen.Terrain;
using Xunit;

namespace Vixen.Rendering.Terrain.Tests;

/// <summary>
///     That <c>Terrain.rvn</c> still contains the arithmetic <c>TerrainLodTree</c> defines.
/// </summary>
/// <remarks>
///     <para>
///         <b>The gap <see cref="Vixen.Rendering.GpuCulling" />'s remarks name, closed the same
///         way.</b> A CPU mirror says whether the arithmetic the device will run is the arithmetic
///         the engine means; what it cannot say is whether the shader <em>still contains</em> that
///         arithmetic. `GpuVisibilityGroupTests` answers that with a source assertion and so does
///         this — because the alternative is a device, and the property being checked is one the
///         no-crack tests in `Vixen.Terrain` have already established as arithmetic.
///     </para>
///     <para>
///         ⚠ <b>A source assertion is weaker than an execution and is chosen knowing that.</b> It
///         catches the failure that actually happens — somebody edits the shader's morph, or deletes
///         it, and every level boundary opens — and it does not catch a subtly different but
///         similar-looking expression. The golden image in [docs/plan/31 § Part 4] is what catches
///         that, and it needs a GPU.
///     </para>
/// </remarks>
public sealed partial class TerrainShaderParityTests {
    static string Source() => Source(Path.Combine("Terrain", "Terrain.rvn"));

    static string Source(string relative) {
        var directory = AppContext.BaseDirectory;

        // Walk up to the repository root, which is the directory holding Raven/. The test binary
        // lives several levels below it and the depth differs between configurations.
        for (var at = new DirectoryInfo(directory); at is not null; at = at.Parent) {
            var candidate = Path.Combine(at.FullName, "Raven", "Library", relative);

            if (File.Exists(candidate)) {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Raven/Library/{relative} was not found above {directory}.");
    }

    [Fact]
    public void TheShaderStillMorphsOddIndicesOntoTheirEvenNeighbour() {
        var source = Source();

        // `float(gridX) - float(gridX % 2) * node.morph`, whitespace-insensitively — the expression
        // TerrainLodTree.MorphIndex is, written in the shader's types.
        var morph = new Regex(
            @"float\(\s*grid([XZ])\s*\)\s*-\s*float\(\s*grid\1\s*%\s*2\s*\)\s*\*\s*node\.morph",
            RegexOptions.None,
            TimeSpan.FromSeconds(5)
        );

        var matches = morph.Matches(source);

        Assert.True(
            matches.Count == 2,
            $"expected the morph on both axes; found {matches.Count}. If the shader was refactored, "
            + "check it still degenerates a patch onto its parent's grid and update this pattern — "
            + "the no-crack property in Vixen.Terrain's TerrainLodTests is what it has to preserve."
        );
    }

    [Fact]
    public void TheShaderReadsTheHeightmapWithAnExplicitLevel() {
        // A vertex stage has no derivatives, so `Sample` outside a fragment stage never meant what it
        // looked like — SPIR-V was quietly substituting level zero. docs/plan/07 records this as the
        // reason SampleLevel landed, and a terrain is the shader that motivated it.
        var source = Source();

        Assert.Contains("heightMap.SampleLevel(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("heightMap.Sample(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShadersNodeStructIsTheOneTheHostPacks() {
        var source = Source();
        var start = source.IndexOf("struct TerrainNode", StringComparison.Ordinal);

        Assert.True(start >= 0, "the shader has no TerrainNode struct.");

        var body = source[start..source.IndexOf('}', start)];
        var fields = new Regex(@"var\s+(\w+)\s*:\s*(\w+)", RegexOptions.None, TimeSpan.FromSeconds(5))
            .Matches(body)
            .Select(match => (Name: match.Groups[1].Value, Type: match.Groups[2].Value))
            .ToArray();

        // Declaration order is the byte order, and the host writes these bytes.
        Assert.Equal(
            [
                ("origin", "float2"),
                ("step", "float"),
                ("morph", "float"),
                ("level", "float"),
                ("padding0", "float")
            ],
            fields
        );

        Assert.Equal(24, TerrainNodeRecord.SizeInBytes);
    }

    /// <summary>The shader still reads the atlas the host packs.</summary>
    /// <remarks>
    ///     ⚠ <b>An atlas coordinate that is off by a block draws a terrain made of the wrong
    ///     tiles</b>, which reads as a corrupt heightmap rather than as an arithmetic error — and the
    ///     two expressions have to be one expression written twice.
    /// </remarks>
    [Fact]
    public void TheShaderStillReadsTheAtlasTheHostPacks() {
        var source = Source();

        // The tile is floor(sample / tileQuads), clamped — `TerrainAtlas.Locate`'s division.
        Assert.Matches(new Regex(@"floor\(sample\s*/\s*tileQuads\)"), source);

        // And the texel is that tile's block plus the local offset, at the texel's centre.
        Assert.Matches(new Regex(@"tile\s*\*\s*tileSamples\s*\+\s*local"), source);
        Assert.Matches(new Regex(@"float2\(0\.5f,\s*0\.5f\)\)\s*/\s*heightMapSize"), source);
    }

    /// <summary>A patch reads its own level rather than level zero.</summary>
    /// <remarks>
    ///     ⚠ <b>Reading level 0 on a coarse patch gives it a height nothing between its own vertices
    ///     ever had</b>, so the surface swims as the camera moves — worst on the patches furthest
    ///     away, where it is hardest to attribute.
    /// </remarks>
    [Fact]
    public void TheShaderStillSamplesTheNodesOwnLevel() {
        var source = Source();

        Assert.Matches(new Regex(@"Height\(sample,\s*node\.level\)"), source);
        Assert.Matches(new Regex(@"SampleLevel\(heightSampler,\s*AtlasUv\(sample\),\s*level\)"), source);
    }

    /// <summary>And the weights are taken with the packed coordinate's derivatives.</summary>
    /// <remarks>
    ///     ⚠ <b>An atlas coordinate jumps by a whole block at every tile boundary</b>, so the
    ///     hardware's own derivative there is enormous and it picks the coarsest level it has — which
    ///     draws as a dark line one pixel wide along every tile edge, on every terrain, and reads as a
    ///     crack in the mesh.
    /// </remarks>
    [Fact]
    public void TheShaderStillTakesItsWeightDerivativesFromThePackedCoordinate() {
        var source = Source();

        Assert.Matches(new Regex(@"SampleGrad\("), source);
        Assert.Matches(new Regex(@"ddx\(sampleCoord\)"), source);
        Assert.Matches(new Regex(@"ddy\(sampleCoord\)"), source);
    }

    /// <summary>
    ///     The grid patch's side is a permutation, so the morph's divisions fold at compile time.
    /// </summary>
    /// <remarks>
    ///     A vertex stage that read the patch size from a uniform would do two integer divisions per
    ///     vertex on a value that is the same for the whole frame. As a permutation constant they
    ///     fold, which for a power of two is a shift and a mask.
    /// </remarks>
    [Fact]
    public void TheGridSizeIsAPermutationRatherThanAUniform() {
        var source = Source();

        Assert.Contains("[Permutation] val GridQuads: int", source, StringComparison.Ordinal);
        Assert.Equal(
            TerrainLodTree.DefaultGridQuads,
            int.Parse(
                new Regex(@"\[Permutation\] val GridQuads: int = (\d+)", RegexOptions.None, TimeSpan.FromSeconds(5))
                    .Match(source)
                    .Groups[1]
                    .Value
            )
        );
    }

    /// <summary>
    ///     The shader's morph, transliterated, agrees with the kernel's over every index and morph.
    /// </summary>
    /// <remarks>
    ///     The other half of the bargain, and the one a source assertion cannot make: the pattern
    ///     above says the expression is there, and this says the expression is <em>right</em>. It is a
    ///     transliteration rather than an execution, which is exactly what
    ///     <see cref="Vixen.Rendering.GpuCulling.IsVisible" /> is for its own shader.
    /// </remarks>
    [Theory]
    [InlineData(8)]
    [InlineData(32)]
    public void TheTransliteratedShaderMorphEqualsTheKernels(int gridQuads) {
        for (var vertex = 0; vertex < TerrainGridPatch.VertexCount(gridQuads); vertex++) {
            var (gridX, gridZ) = TerrainGridPatch.VertexOf(vertex, gridQuads);

            for (var step = 0; step <= 20; step++) {
                var morph = step / 20f;

                // What Terrain.rvn computes, written in C#.
                var shaderX = gridX - ((gridX % 2) * morph);
                var shaderZ = gridZ - ((gridZ % 2) * morph);

                Assert.Equal(TerrainLodTree.MorphIndex(gridX, morph), shaderX, 5);
                Assert.Equal(TerrainLodTree.MorphIndex(gridZ, morph), shaderZ, 5);
            }
        }
    }

    // ------------------------------------------------------------------ the lit path

    /// <summary>The shadow bias is added, which under reverse-Z is toward the light.</summary>
    /// <remarks>
    ///     ⚠ <b>Subtracting it is the conventional-Z habit, and it answers backwards here</b>: near
    ///     maps to 1 and far to 0, so moving a receiver toward the light moves it numerically up.
    ///     The compare itself — <c>depth &gt;= stored</c> — lives in <c>Lighting.ShadowTap</c>,
    ///     which the lit ground calls rather than copies; the bias line is the half it owns.
    /// </remarks>
    [Fact]
    public void TheLitShadowBiasIsAddedNotSubtracted() {
        var shared = Source(Path.Combine("Terrain", "FrameLit.rvn"));

        Assert.Matches(new Regex(@"ndc\.z\s*\+\s*bias"), shared);
        Assert.DoesNotMatch(new Regex(@"ndc\.z\s*-\s*bias"), shared);

        // And the tap it hands that depth to is still the reverse-Z compare.
        var lighting = Source(Path.Combine("Shading", "Lighting.rvn"));

        Assert.Matches(new Regex(@"depth\s*>=\s*stored"), lighting);
    }

    /// <summary>The split normal plane takes the raw signed world normal, never an encode.</summary>
    /// <remarks>
    ///     ⚠ <b><c>SceneNormals</c> is raw signed in <c>Rgba16Float</c>, where zero is the sky.</b>
    ///     A <c>*0.5+0.5</c> encode is every downstream reader — the probe gather, both occlusion
    ///     passes, the combine — fed normals folded into one hemisphere, which draws as 16-pixel
    ///     squares on the floor rather than as anything traceable to a normal.
    /// </remarks>
    [Fact]
    public void TheSplitNormalIsRawAndSigned() {
        var terrain = Source();
        var grass = Source(Path.Combine("Terrain", "Grass.rvn"));

        Assert.Matches(new Regex(@"targets\.normal\s*=\s*float4\(n,"), terrain);
        Assert.Matches(new Regex(@"targets\.normal\s*=\s*float4\(n,"), grass);
        Assert.DoesNotContain("0.5f + 0.5f", terrain, StringComparison.Ordinal);
        Assert.DoesNotContain("0.5f + 0.5f", grass, StringComparison.Ordinal);
    }

    /// <summary>The transliterated froxel grid still has the culler's shape.</summary>
    /// <remarks>
    ///     <c>FrameClusters</c> is <c>ClusterGrid</c> written twice — the Terrain package cannot
    ///     import the pipeline package without dragging its unbound compose slots into the editor's
    ///     standalone compilation — and a fragment that derives its cluster differently reads the
    ///     list that was culled for somewhere else.
    /// </remarks>
    [Fact]
    public void TheTransliteratedClusterGridEqualsTheCullers() {
        var shared = Source(Path.Combine("Terrain", "FrameLit.rvn"));

        Assert.Contains($"const val TilesX = {Vixen.Rendering.ClusterGrid.TilesX}", shared, StringComparison.Ordinal);
        Assert.Contains($"const val TilesY = {Vixen.Rendering.ClusterGrid.TilesY}", shared, StringComparison.Ordinal);
        Assert.Contains($"const val Slices = {Vixen.Rendering.ClusterGrid.Slices}", shared, StringComparison.Ordinal);
        Assert.Contains($"const val Capacity = {Vixen.Rendering.ClusterGrid.Capacity}", shared, StringComparison.Ordinal);
        Assert.Contains($"indices: uint[{Vixen.Rendering.ClusterGrid.Capacity}]", shared, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the cutout

    /// <summary>
    ///     Every fragment stage of the vegetation shaders discards through the one shared predicate.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The colour pass and the velocity pass must cut the same fragments out, and this
    ///         is the assertion that they still can.</b> The velocity pass depth-tests against the
    ///         frame's finished depth, so a fragment the colour pass discarded shows the terrain
    ///         behind it — and a velocity fragment surviving there writes the blade's motion over the
    ///         ground's, which resolves as a smear around every blade in every gust. The failure is
    ///         invisible in a still frame and unattributable in a moving one.
    ///     </para>
    ///     <para>
    ///         The shape asserted is <em>structural</em> rather than textual: one <c>Cutout</c> on
    ///         the base, three callers, and no stage testing the stipple itself. Two copies of the
    ///         same expression would pass a "the discard is present" test on the day it was written
    ///         and drift on the day one of them was edited, which is the thing that actually happens.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("Grass.rvn")]
    [InlineData("Foliage.rvn")]
    public void EveryVegetationFragmentDiscardsThroughTheOneCutout(string file) {
        var source = Source(Path.Combine("Terrain", file));

        var stages = Count(source, "[FragmentShader]");
        var declarations = Count(source, "func Cutout(");
        var callers = Count(source, "if (Cutout(");

        Assert.True(stages == 3, $"{file} has {stages} fragment stages; the preview, the lit and the velocity are three.");
        Assert.True(declarations == 1, $"{file} declares Cutout {declarations} times; the base owns it and only the base.");

        Assert.True(
            callers == stages,
            $"{file} has {stages} fragment stages and {callers} of them discard through Cutout. A stage that "
            + "tests its own coverage is a stage that can disagree with the others — see this test's remarks "
            + "for what that writes into the motion target."
        );

        // The stipple is reachable only through the predicate: a stage comparing it against `fade`
        // itself is the old shape, and the old shape is what drifted.
        Assert.DoesNotContain("Stipple(fragment", source, StringComparison.Ordinal);
    }

    /// <summary>And the predicate is the same predicate in both files.</summary>
    /// <remarks>
    ///     ⚠ <b>Grass and foliage stipple against one pattern deliberately</b> — a tree's far LOD
    ///     dissolving over grass that is fading must dissolve against the same noise, or the two
    ///     dithers interfere as a visible weave. The cutoff joined it for the same reason: the two
    ///     stacks share a frame.
    /// </remarks>
    [Fact]
    public void TheGrassAndFoliageCutoutsAreTheSameExpression() {
        Assert.Equal(CutoutBody("Grass.rvn"), CutoutBody("Foliage.rvn"));

        static string CutoutBody(string file) {
            var source = Source(Path.Combine("Terrain", file));
            var start = source.IndexOf("func Cutout(", StringComparison.Ordinal);

            Assert.True(start >= 0, $"{file} has no Cutout.");

            return Whitespace().Replace(source[start..(source.IndexOf('}', start) + 1)], " ");
        }
    }

    /// <summary>The velocity stages sample the alpha they are cutting out by.</summary>
    /// <remarks>
    ///     ⚠ <b>The albedo was bound to the velocity passes before anything read it</b> — a
    ///     descriptor set is written wholly or not at all — so the binding's presence proves nothing.
    ///     A velocity stage that passed a constant to <c>Cutout</c> would satisfy every assertion
    ///     above and still leave the card's margin moving.
    /// </remarks>
    [Theory]
    [InlineData("Grass.rvn")]
    [InlineData("Foliage.rvn")]
    public void TheVelocityStagesSampleTheAlphaTheyCutBy(string file) {
        var source = Source(Path.Combine("Terrain", file));

        Assert.Matches(
            new Regex(@"Cutout\(albedoMap\.Sample\(albedoSampler,\s*uv\)\.a,", RegexOptions.None, TimeSpan.FromSeconds(5)),
            source
        );
    }

    /// <summary>The cutout is a uniform rather than a permutation, and the host owns the number.</summary>
    /// <remarks>
    ///     ⚠ <b>A <c>[Permutation] AlphaTested</c> would be two places the answer is chosen.</b> The
    ///     draw's variant is resolved per grass type and the velocity's once for the whole scene —
    ///     <c>TerrainSceneRenderer.GrassShaders</c> against <c>ResolveVelocityShaders</c> — so the
    ///     two could be resolved differently, which is the drift this whole file is guarding. The
    ///     pipeline library's own shaders use the permutation because their two stages resolve
    ///     together; these do not.
    /// </remarks>
    [Theory]
    [InlineData("Grass.rvn")]
    [InlineData("Foliage.rvn")]
    public void TheCutoffIsAUniformTheHostWrites(string file) {
        var source = Source(Path.Combine("Terrain", file));

        Assert.Contains("var alphaCutoff: float = 0.5f", source, StringComparison.Ordinal);
        Assert.DoesNotContain("[Permutation] val AlphaTested", source, StringComparison.Ordinal);
    }

    /// <summary>An opaque target takes one, not the cutout's own mask.</summary>
    /// <remarks>
    ///     ⚠ <b>Writing <c>sampled.a</c> into <c>TerrainTargets.color</c> puts a coverage mask in a
    ///     channel whose readers take it for coverage</b> — and the terrain writing beside it writes
    ///     one. It is also the thing that made the cutout look unnecessary: an alpha in the target
    ///     reads like an alpha that did something.
    /// </remarks>
    [Theory]
    [InlineData("Grass.rvn")]
    [InlineData("Foliage.rvn")]
    public void TheColourTargetsTakeOneRatherThanTheCardsAlpha(string file) {
        var source = Source(Path.Combine("Terrain", file));

        Assert.DoesNotContain("sampled.a)", source, StringComparison.Ordinal);
    }

    static int Count(string source, string needle) {
        var total = 0;

        for (var at = source.IndexOf(needle, StringComparison.Ordinal); at >= 0;
            at = source.IndexOf(needle, at + needle.Length, StringComparison.Ordinal)) {
            total++;
        }

        return total;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>The lit terrain returns to world space before it asks the frame anything.</summary>
    /// <remarks>
    ///     The placement rides <c>viewProjection</c>, so <c>positionWS</c> is terrain-local — and a
    ///     cascade matrix given a local position shadows the terrain with a copy of the world
    ///     standing at the origin, which reads as shadows sliding off by exactly the terrain's
    ///     placement.
    /// </remarks>
    [Fact]
    public void TheLitTerrainRestoresItsWorldPlacement() {
        Assert.Matches(new Regex(@"positionWS\s*\+\s*originWS"), Source());
    }

    /// <summary>
    ///     Every shader that patches an indirect command's <c>firstInstance</c> is matched by Vulkan
    ///     asking the device for the feature that makes a non-zero one legal.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The one rendering bug in this file that no layer can report.</b> Without
    ///         <c>drawIndirectFirstInstance</c>, VUID-vkCmdDrawIndexedIndirect-firstInstance-00530
    ///         requires every command in an indirect buffer to carry zero there. The number lives in
    ///         a device buffer a compute pass wrote, and the validation layers read the draw call —
    ///         so there is no message, and the symptom is every cell drawing the first run's
    ///         instances: a full field of plausible vegetation in the wrong places.
    ///     </para>
    ///     <para>
    ///         Written as a sweep rather than as two named files, because the failure this guards
    ///         against is the <em>third</em> writer — somebody adding an indirect pass, patching the
    ///         field the way the two existing ones do, and never learning that the permission was a
    ///         separate decision. The count assertion below is what keeps the sweep honest: a regex
    ///         that stopped matching would otherwise pass by finding nothing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void EveryShaderPatchingFirstInstanceIsMatchedByTheFeatureBeingRequested() {
        var library = Library();

        var assignment = new Regex(
            @"\.firstInstance\s*=\s*(?<value>[^\r\n]+)",
            RegexOptions.None,
            TimeSpan.FromSeconds(5)
        );

        List<string> writers = [];

        foreach (var shader in Directory.EnumerateFiles(library, "*.rvn", SearchOption.AllDirectories)) {
            foreach (Match match in assignment.Matches(File.ReadAllText(shader))) {
                // `= 0`, `= 0u` and `= 0uy` are the legal writes and stay legal on every device; a
                // struct field left alone is the same thing and never reaches this regex at all.
                if (match.Groups["value"].Value.Trim().TrimEnd(';') is "0" or "0u") {
                    continue;
                }

                writers.Add(Path.GetFileName(shader));

                break;
            }
        }

        Assert.True(
            writers.Count >= 2,
            "expected GrassScatter.rvn and FoliageCull.rvn to still patch firstInstance; found "
            + $"[{string.Join(", ", writers)}]. If they were rewritten to fold the base in some other "
            + "way, this guard has nothing left to guard and should go with them — do not widen the "
            + "pattern until it matches something."
        );

        var request = RepoFile(Path.Combine("Platform", "Vixen.Graphics.Vulkan", "VulkanDevice.cs"));

        Assert.True(
            request.Contains("DrawIndirectFirstInstance = adapter.Supported.DrawIndirectFirstInstance", StringComparison.Ordinal),
            $"[{string.Join(", ", writers)}] write a non-zero firstInstance into an indirect command, "
            + "and VulkanDevice's PhysicalDeviceFeatures request does not ask for "
            + "drawIndirectFirstInstance. Nothing reports this: the layers read the draw call, not "
            + "the buffer a compute pass filled, so the frame draws every command from the first "
            + "run's instances and looks merely wrong. Add the bit to the intersection, or keep "
            + "firstInstance at zero and fold the base in another way."
        );

        var features = RepoFile(Path.Combine("Platform", "Vixen.Graphics.Vulkan", "VulkanFeatures.cs"));

        Assert.True(
            features.Contains("HasDrawIndirectFirstInstance = features.DrawIndirectFirstInstance", StringComparison.Ordinal),
            "the bit is requested but no capability reports it, so no pass can find out it was "
            + "refused — see GraphicsDeviceFeatures.HasDrawIndirectFirstInstance."
        );
    }

    /// <summary>The repository's <c>Raven/Library</c>, which every shader in the sweep lives under.</summary>
    static string Library() => Path.Combine(Root(), "Raven", "Library");

    /// <summary>One file of the repository, by its path from the root.</summary>
    static string RepoFile(string relative) => File.ReadAllText(Path.Combine(Root(), relative));

    /// <summary>
    ///     The repository root — the directory holding <c>Raven/Library</c>, found the way
    ///     <see cref="Source(string)" /> finds it.
    /// </summary>
    static string Root() {
        var directory = AppContext.BaseDirectory;

        for (var at = new DirectoryInfo(directory); at is not null; at = at.Parent) {
            if (Directory.Exists(Path.Combine(at.FullName, "Raven", "Library"))) {
                return at.FullName;
            }
        }

        throw new DirectoryNotFoundException($"Raven/Library was not found above {directory}.");
    }
}
