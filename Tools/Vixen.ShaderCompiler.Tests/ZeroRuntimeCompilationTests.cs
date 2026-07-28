// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics.Null;
using Vixen.ShaderCompiler;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     Doc 06's exit criterion: a shipping build compiles nothing, asserted rather than hoped.
/// </summary>
/// <remarks>
///     <para>
///         The shape of the claim is the whole argument. A development run resolves through a
///         compiler and records every key it asked for; that list is a manifest; the build compiles
///         the manifest into a bundle; and a second run resolves through the bundle <em>alone</em>
///         and misses nothing.
///     </para>
///     <para>
///         The shipping half cannot compile even if something wanted it to — the only source behind
///         it is a dictionary — which is what <see cref="IEffectProvider" /> was drawn for in the
///         first place. What these tests add is the other half: that the dictionary is
///         <em>complete</em>.
///     </para>
/// </remarks>
public class ZeroRuntimeCompilationTests {
    static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Lighting.rvn");

    static RavenEffectCompiler Compiler() => new([Fixture], "glsl");

    /// <summary>The permutation keys this shader's output depends on, as generated code would hold them.</summary>
    /// <remarks>
    ///     Interned under the fixture's own names, so this is the same list
    ///     <c>Vixen.Shaders.Generators</c> emits for it — the shader declares three keys and reads
    ///     two, and the third never reaches a key at all.
    /// </remarks>
    static ParameterKey[] Used => [
        ParameterKeys.NewPermutation(false, "Lighting.UseShadows"),
        ParameterKeys.NewPermutation(4, "Lighting.MaxLights")
    ];

    /// <summary>What a run of the game asks for, built the way a draw builds it.</summary>
    /// <remarks>
    ///     Through <see cref="EffectKey.From" /> and the shader's used keys rather than by hand,
    ///     because that is the step the whole arrangement turns on: the key a draw computes and the
    ///     key a build filed the artefact under have to be the same value, and a test that wrote both
    ///     out longhand would agree with itself and prove nothing.
    /// </remarks>
    static IEnumerable<EffectKey> Playthrough() {
        foreach (var shadows in (bool[])[false, true, false]) {
            var parameters = new ParameterCollection();
            parameters.Set((PermutationKey<bool>)Used[0], shadows);

            // Set by every draw and never varied — the ordinary case for a key that is in the list
            // because the shader reads it, not because anything changes it.
            parameters.Set((PermutationKey<int>)Used[1], 4);

            yield return EffectKey.From("Lighting", parameters, Used);
        }
    }

    /// <summary>
    ///     A bundle built from what a run asked for leaves the next run nothing to compile.
    /// </summary>
    [Fact]
    public void A_bundle_built_from_a_run_leaves_nothing_to_compile() {
        using var device = new NullDevice();

        // The development run: a compiler behind the system, and a playthrough that draws things.
        var development = new EffectSystem();
        development.AddProvider(new EffectSourceProvider(Compiler(), new(device)));

        foreach (var key in Playthrough()) {
            Assert.NotNull(development.Resolve(key));
        }

        // Three draws, two variants — the economy the whole cache exists for, showing up as a
        // number here rather than as a claim.
        Assert.Equal(2, development.RequestCount);

        // The build: everything that run asked for, compiled and baked.
        var builder = new EffectBundleBuilder(Compiler());
        builder.Add(EffectManifest.Of(development.Requests));

        Assert.Empty(builder.Missing);

        // The shipping run: one provider, over a bundle, with no compiler in reach of it.
        var shipping = new EffectSystem();
        shipping.AddProvider(new EffectSourceProvider(new EffectStore(builder.Build()), new(device)));

        foreach (var key in Playthrough()) {
            Assert.NotNull(shipping.Resolve(key));
        }

        Assert.Empty(shipping.Misses);
    }

    /// <summary>
    ///     And the assertion fails, by name, when the bundle is short of one variant.
    /// </summary>
    /// <remarks>
    ///     The half that makes the test above worth having. An empty miss list also means "nothing
    ///     was ever asked for", so the interesting question is whether a genuinely missing variant
    ///     shows up — and whether what it says is enough to go and add it.
    /// </remarks>
    [Fact]
    public void A_bundle_missing_a_variant_reports_it_by_name() {
        using var device = new NullDevice();

        var wanted = Playthrough().Distinct().ToArray();
        var builder = new EffectBundleBuilder(Compiler());
        builder.Add(wanted.Skip(1));

        var shipping = new EffectSystem();
        shipping.AddProvider(new EffectSourceProvider(new EffectStore(builder.Build()), new(device)));

        foreach (var key in wanted) {
            shipping.Resolve(key);
        }

        var missed = Assert.Single(shipping.Misses);

        Assert.Equal(wanted[0], missed);
        Assert.Contains("Lighting.UseShadows=false", missed.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     Enumerating the shader produces a superset of what the run asked for.
    /// </summary>
    /// <remarks>
    ///     The other way to build the bundle, and the one a project uses for the shaders every frame
    ///     goes through: no playthrough needed, at the cost of compiling variants nothing will draw.
    ///     Here the two happen to agree exactly, which is what a shader whose keys are read
    ///     unconditionally looks like — <see cref="PermutationClosureResult.Dependent" /> is what
    ///     says so.
    /// </remarks>
    [Fact]
    public void Enumerating_the_shader_covers_the_run() {
        var builder = new EffectBundleBuilder(Compiler());
        var closure = builder.AddClosure("Lighting");

        Assert.False(closure.Dependent);

        var store = new EffectStore(builder.Build());

        foreach (var key in Playthrough()) {
            Assert.NotNull(store.TryGet(key));
        }
    }
}
