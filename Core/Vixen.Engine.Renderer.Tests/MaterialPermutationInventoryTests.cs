// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Engine.Renderer;
using Vixen.Rendering.Features;
using Vixen.Rendering.Materials;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     Every permutation a material feature sets is one <c>WorldRenderer.Permuted</c> registers.
/// </summary>
/// <remarks>
///     <para>
///         <b><c>MaterialPairingInventoryTests</c>' question asked of the other registration
///         table.</b> That file reads the shipped library and asserts one pairing entry per sampling
///         slot, because a slot nobody paired samples the fallback checker. This one asks what
///         <c>WorldRenderer.Permuted</c> does on the day a feature sets a permutation nobody
///         registered — and until this file existed the answer was <em>nothing</em>: the two keys it
///         registers were named by hand in one assertion, so a third would have been missed by every
///         test in the repository.
///     </para>
///     <para>
///         ⚠ <b>The unregistered-permutation trap, and it is quieter here than a wrong texture.</b> A
///         value in a material's <c>Permutations</c> that is not also in
///         <see cref="MaterialRenderFeature.PermutationKeys" /> never reaches the compiler: the
///         variant silently takes the <c>.rvn</c> default. An unregistered <c>LayerCount</c> blends
///         two layers of a three-layer material; an unregistered <c>HeightBlended</c> leaves a
///         material's height map unsampled and draws the old blend. Both are frames, not errors.
///     </para>
///     <para>
///         It reads what the compiler <em>did</em> rather than a list beside it: every feature the
///         rendering assembly ships is compiled, and the permutation keys that come back out of the
///         parameter collection are the inventory. A second list is satisfied by editing the second
///         list, and the thing that arrives is a <em>new feature</em>.
///     </para>
/// </remarks>
public class MaterialPermutationInventoryTests {
    /// <summary>What the shading pass is called, as the constructor registers it.</summary>
    const string Shader = "ForwardPlus";

    /// <summary>
    ///     Every permutation key the shipped features set, read off compiled materials.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         One material per feature rather than one material holding all of them, for two reasons
    ///         the compiler enforces: a chain holds eight features, and a shader may not fill two
    ///         slots — <see cref="MaterialLayersFeature" /> and
    ///         <see cref="TexturedMaterialLayersFeature" /> would also collide on <c>LayerCount</c>
    ///         and hide each other. Compiled one at a time, each feature answers for itself.
    ///     </para>
    ///     <para>
    ///         ⚠ A feature whose compilation is refused contributes nothing —
    ///         <see cref="GraphSurfaceFeature" /> with no graph behind it names no shader, which is a
    ///         rejection by design. That is why the roster below has a floor of its own rather than
    ///         relying on this set being non-empty.
    ///     </para>
    /// </remarks>
    static HashSet<ParameterKey> Set() {
        var keys = new HashSet<ParameterKey>();

        foreach (var feature in Features()) {
            var compilation = MaterialCompiler.Compile(new() { ShaderName = Shader, Features = [feature] });

            if (compilation.Material is not { } material) {
                continue;
            }

            foreach (var (key, _) in material.Parameters.Permutations) {
                keys.Add(key);
            }
        }

        // The instrument. An empty inventory is a compiler that started refusing everything, or a
        // reflection query that stopped matching, and it satisfies the assertion below it while
        // checking nothing. The library has two material permutations today and both are set
        // unconditionally by a feature, so anything under two is this file having stopped working.
        Assert.True(keys.Count >= 2, $"only {keys.Count} material permutations were compiled");

        return keys;
    }

    /// <summary>Every material feature the rendering assembly ships, one instance each.</summary>
    /// <remarks>
    ///     Reflected rather than listed, for the reason the inventory is compiled rather than listed:
    ///     the failure this guards against arrives as a new feature, and a list is satisfied by
    ///     editing the list.
    /// </remarks>
    static IMaterialFeature[] Features() {
        var features = typeof(MetalRoughnessFeature).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false })
            .Where(type => typeof(IMaterialFeature).IsAssignableFrom(type))
            .Where(type => type.GetConstructor(Type.EmptyTypes) is not null)
            .Select(type => (IMaterialFeature)Activator.CreateInstance(type)!)
            .ToArray();

        Assert.True(features.Length >= 15, $"only {features.Length} material features were reflected");

        return features;
    }

    /// <summary>What <c>Permuted</c> registered, read back off the feature it wrote into.</summary>
    static HashSet<ParameterKey> Registered() {
        using var materials = new MaterialRenderFeature();

        WorldRenderer.Permuted(materials, Shader);

        return [.. materials.PermutationKeys[Shader]];
    }

    /// <summary>
    ///     A permutation a feature sets is a permutation the effect key is built from.
    /// </summary>
    /// <remarks>
    ///     The silent direction, and the one that has already cost this engine a wrong frame twice.
    ///     See <c>PermutationKeyDictionary</c>, whose whole existence is the second half of the same
    ///     failure.
    /// </remarks>
    [Fact]
    public void EveryPermutationAFeatureSetsIsRegistered() {
        var registered = Registered();

        Assert.NotEmpty(registered);

        foreach (var key in Set()) {
            Assert.True(
                registered.Contains(key),
                $"a material feature sets '{key.Name}' and WorldRenderer.Permuted registers nothing "
                + "for it — so the value never reaches the compiler, the variant takes the shader's "
                + "own default, and the frame draws the feature switched off with nothing reported"
            );
        }
    }

    /// <summary>And the other direction, which is where a renamed permutation lands.</summary>
    /// <remarks>
    ///     A registered key no feature sets splits the variant cache for nothing — and it is how the
    ///     first direction stays green while the real key goes missing, because renaming a
    ///     permutation in <see cref="MaterialKeys" /> moves both halves of the hand-written assertion
    ///     at once and neither of the compiled ones.
    /// </remarks>
    [Fact]
    public void EveryRegisteredPermutationIsOneAFeatureSets() {
        var set = Set();

        foreach (var key in Registered()) {
            Assert.True(
                set.Contains(key),
                $"WorldRenderer.Permuted registers '{key.Name}' and no material feature in the "
                + "shipped assembly sets it — the key reaches the effect key and no material ever "
                + "varies it, which splits the variant cache for nothing"
            );
        }
    }
}
