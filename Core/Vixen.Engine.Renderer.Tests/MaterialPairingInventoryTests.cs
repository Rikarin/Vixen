// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Vixen.Engine.Renderer;
using Vixen.Rendering.Features;
using Vixen.Rendering.Materials;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     Every sampling shader the shipped library declares is one <c>WorldRenderer.Paired</c> pairs.
/// </summary>
/// <remarks>
///     <para>
///         <b>The failure this guards against is a picture and not an error.</b> <c>Paired</c> joins a
///         shader's composed <c>uint</c> to the material's texture name, and a sampling feature that is
///         not in it fails exactly like a renamed map: its index is never written, so it stays zero, so
///         the map is read from slot zero and the surface is shaded by the fallback checker — which for
///         a normal map is a lit surface whose shading is merely wrong rather than a surface that is
///         obviously untextured. No device reports anything.
///     </para>
///     <para>
///         ⚠ <b>This is <c>MaterialCompiler.OptionalSlots</c>' failure shape one layer over, and the
///         worse half of it.</b> That one fails loudly, at compile time, and has had a completeness
///         test reading the library since the <c>ScreenProbes</c> package landed without its line. This
///         one fails as a wrong frame at runtime. The list was complete when this was written, which is
///         the only moment such a test can be written honestly — see
///         <a href="https://github.com/Rikarin/Vixen/issues/371">#371</a>.
///     </para>
///     <para>
///         It reads <c>Raven/Library</c> rather than holding a list of its own, which is
///         <c>ComposeSlotInventoryTests</c>' shape and for its reason: a second list can be satisfied by
///         editing the second list, and the thing that arrives is a <em>new shader</em>.
///     </para>
/// </remarks>
public class MaterialPairingInventoryTests {
    /// <summary>A shader declaration and the bases it inherits.</summary>
    /// <remarks>
    ///     Anchored at the start of a trimmed line so a mention of <c>MaterialTextures</c> in a comment
    ///     or a doc block is not a declaration. The base list runs to the brace, because a shader
    ///     inherits <c>MaterialTextures</c> beside <c>IMaterialSurface</c> and the order is the
    ///     author's.
    /// </remarks>
    static readonly Regex Declaration = new(
        @"^shader\s+(?<name>\w+)\s*:\s*(?<bases>[^{]+)",
        RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant
    );

    /// <summary>What the shading pass is called, as the constructor pairs it.</summary>
    const string Shader = "ForwardPlus";

    /// <summary>
    ///     Every shader in the shipped library that inherits the table, and therefore samples.
    /// </summary>
    /// <remarks>
    ///     ⚠ Direct inheritance, which is what the library has: <c>MaterialTextures</c> is inherited
    ///     rather than composed, and every sampling feature names it on its own declaration line. A
    ///     shader that reached the table through a second shader would be missed here — and would also
    ///     be a shape the library does not have, so a test that walked a hierarchy would be walking one
    ///     that is one level deep.
    /// </remarks>
    static string[] Sampling() {
        var root = Path.Combine(AppContext.BaseDirectory, "Shaders");

        // Not a skip. The shaders are copied by this project's own .csproj, so their absence is a build
        // that did not happen rather than an environment this cannot run in — and a test that skipped
        // would report success on the day it stopped reading anything at all.
        Assert.True(Directory.Exists(root), $"the shipped shaders were not copied to {root}");

        var sampling = Directory
            .EnumerateFiles(root, "*.rvn", SearchOption.AllDirectories)
            .SelectMany(File.ReadAllLines)
            .Select(line => Declaration.Match(line.Trim()))
            .Where(match => match.Success)
            .Where(match => match.Groups["bases"].Value
                .Split(',')
                .Select(name => name.Trim())
                .Contains("MaterialTextures", StringComparer.Ordinal)
            )
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // And the instrument: an empty inventory is a regex that stopped matching, which passes every
        // assertion below it while checking nothing.
        Assert.NotEmpty(sampling);

        return sampling;
    }

    /// <summary>Which shader each pairing entry names, read back off the keys it wrote.</summary>
    /// <remarks>
    ///     The pairing's own output rather than a list beside it. A key is the pass, the chain shader,
    ///     the feature's shader and the parameter — so the shader is the second-to-last segment, and
    ///     reading it back is what makes this a test of what <c>Paired</c> did rather than of what it
    ///     was supposed to do.
    /// </remarks>
    static HashSet<string> Pairs() {
        using var materials = new MaterialRenderFeature();

        WorldRenderer.Paired(materials, Shader);

        return materials.TextureIndices.Keys
            .Select(key => key.Name.Split('.'))
            .Select(segments => segments[^2])
            .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void EverySamplingShaderTheLibraryDeclaresHasAPairingEntry() {
        var paired = Pairs();

        Assert.NotEmpty(paired);

        foreach (var shader in Sampling()) {
            Assert.True(
                paired.Contains(shader),
                $"'{shader}' inherits MaterialTextures and WorldRenderer.Paired names nothing for it — "
                + "its index is never written, so it stays zero, so its map is read from slot zero and "
                + "the surface is shaded by the fallback checker, on every device, with nothing reported"
            );
        }
    }

    /// <summary>And the other direction, which is where a typo lands.</summary>
    /// <remarks>
    ///     A pairing entry naming a shader the library does not declare writes an index into a
    ///     parameter no variant has, which the effect path drops in silence — the same nothing as
    ///     omitting the entry, arrived at by writing one. The two assertions are separate because they
    ///     fail for opposite reasons and a reader should not have to work out which.
    /// </remarks>
    [Fact]
    public void EveryPairingEntryNamesASamplingShaderTheLibraryDeclares() {
        var sampling = Sampling().ToHashSet(StringComparer.Ordinal);

        foreach (var shader in Pairs()) {
            Assert.True(
                sampling.Contains(shader),
                $"WorldRenderer.Paired names '{shader}' and no shader in the shipped library declares "
                + "it as a sampler — the index it writes reaches no parameter, which is exactly as "
                + "silent as writing none"
            );
        }
    }

    /// <summary>
    ///     A layered material's <c>LayerCount</c> is in the key its variant is selected by.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The unregistered-permutation trap, which this host was sitting in.</b>
    ///         <c>MaterialLayersFeature</c> sets the count, <c>MaterialKeys</c>' own remarks say a host
    ///         that draws layered materials has to register the key, and no host did — so a three-layer
    ///         material resolved the variant compiled for the shader's declared two and wrote a third
    ///         layer into a block that holds two. Nothing reports it: an effect resolves, a pipeline
    ///         binds, a frame draws.
    ///     </para>
    ///     <para>
    ///         Additive rather than assigned, and asserted so: a host registers the generated
    ///         <c>UsedPermutationKeys</c> for a pass and this must not be the call that drops them.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ALayeredMaterialsCountIsInTheEffectKey() {
        using var materials = new MaterialRenderFeature();

        var existing = ParameterKeys.NewPermutation(false, $"{Shader}.UseClusteredLights");
        materials.PermutationKeys[Shader] = [existing];

        WorldRenderer.Permuted(materials, Shader);

        Assert.Contains(MaterialKeys.LayerCount(Shader), materials.PermutationKeys[Shader]);
        Assert.Contains(existing, materials.PermutationKeys[Shader]);

        // And twice is once, because a host that builds two renderers on one feature would otherwise
        // grow the key list by a duplicate that splits the cache for nothing.
        WorldRenderer.Permuted(materials, Shader);

        Assert.Equal(2, materials.PermutationKeys[Shader].Count);
    }

    /// <summary>
    ///     And the count does <em>not</em> reach the frame's own permutation collection.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>MaterialRenderFeature.SetPermutation</c> is the obvious call and the wrong one here: it
    ///     writes <c>Permutations</c> as well, and <c>Contribute</c> applies that collection last so a
    ///     material cannot claim a device capability by setting the same key. A count that belongs to
    ///     the material is the one shape that inverts — every layered material would resolve the
    ///     frame's value instead of its own, which is the same wrong picture the registration was added
    ///     to fix, arrived at from the other side.
    /// </remarks>
    [Fact]
    public void TheCountIsRegisteredWithoutAFrameWideValue() {
        using var materials = new MaterialRenderFeature();

        WorldRenderer.Permuted(materials, Shader);

        Assert.False(materials.Permutations.Has(MaterialKeys.LayerCount(Shader)));
    }

    /// <summary>
    ///     And the map names are distinct, which is the half the shader side cannot check.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>TextureIndices</c> is keyed by the shader-side name and valued by the material-side
    ///     one, so two sampling features sharing a map name are two indices filled from one texture —
    ///     an ORM map sampled as a normal, which shades and does not fail. The inventory above cannot
    ///     see it, because both entries are present.
    /// </remarks>
    [Fact]
    public void NoTwoSamplingFeaturesShareAMapName() {
        using var materials = new MaterialRenderFeature();

        WorldRenderer.Paired(materials, Shader);

        var maps = materials.TextureIndices.Values.Select(key => key.Name).ToArray();

        Assert.Equal(maps.Length, maps.Distinct(StringComparer.Ordinal).Count());
    }
}
