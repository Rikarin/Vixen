// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Rendering.Features;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Rendering.Tests;

/// <summary>
///     The one rule that makes this a type rather than a <c>Dictionary</c>: an assignment cannot
///     unregister a key.
/// </summary>
/// <remarks>
///     ⚠ <b>Both directions, because either one alone is the defect facing the other way.</b> A map
///     that let the host's assignment win compiled every three-layer material as the shader's declared
///     two — in both shipping samples and five golden device suites, because the host line runs after
///     the renderer's constructor. A map that let the registration win by discarding the assigned list
///     would compile the pass without clustered lights, shadows or records instead. The answer is the
///     union, and the ordering is what nothing about either call site shows.
/// </remarks>
public class PermutationKeyDictionaryTests {
    static readonly ParameterKey Clustered = ParameterKeys.NewPermutation(false, "ForwardPlus.UseClusteredLights");
    static readonly ParameterKey Shadows = ParameterKeys.NewPermutation(false, "ForwardPlus.UseShadows");
    static readonly ParameterKey Layers = ParameterKeys.NewPermutation(2, "ForwardPlus.LayerCount");

    /// <summary>The host's line runs second, and the registered key is still there afterwards.</summary>
    [Fact]
    public void AnAssignmentKeepsTheKeysSomethingRegistered() {
        var keys = new PermutationKeyDictionary();

        keys.Register("ForwardPlus", Layers);
        keys["ForwardPlus"] = [Clustered, Shadows];

        Assert.Equal([Clustered, Shadows, Layers], keys["ForwardPlus"]);
    }

    /// <summary>And in the other order, where being merely additive would have been enough.</summary>
    [Fact]
    public void RegisteringAfterAnAssignmentAppends() {
        var keys = new PermutationKeyDictionary();

        keys["ForwardPlus"] = [Clustered];
        keys.Register("ForwardPlus", Layers);

        Assert.Equal([Clustered, Layers], keys["ForwardPlus"]);
    }

    /// <summary>
    ///     Twice is once, in both directions: a repeated key splits the variant cache for nothing.
    /// </summary>
    /// <remarks>
    ///     Two renderers over one feature register the same key twice, and a host that assigns a
    ///     generated array containing a key the engine also registered must not end up with two of it.
    /// </remarks>
    [Fact]
    public void ARepeatedKeyIsOneEntry() {
        var keys = new PermutationKeyDictionary();

        keys.Register("ForwardPlus", Layers);
        keys.Register("ForwardPlus", Layers);
        keys["ForwardPlus"] = [Clustered, Clustered, Layers];

        Assert.Equal([Clustered, Layers], keys["ForwardPlus"]);
    }

    /// <summary>A registration is per shader, so a second pass is not given the first one's keys.</summary>
    [Fact]
    public void ARegistrationBelongsToOneShader() {
        var keys = new PermutationKeyDictionary();

        keys.Register("ForwardPlus", Layers);
        keys["DepthOnly"] = [Clustered];

        Assert.Equal([Clustered], keys["DepthOnly"]);
        Assert.Equal([Layers], keys["ForwardPlus"]);
    }

    /// <summary>A shader nothing has said anything about has no entry, rather than an empty one.</summary>
    /// <remarks>
    ///     What <c>MaterialRenderFeature.KeysFor</c> reads: an absent entry is one variant, which is
    ///     right for a shader that declares no permutations.
    /// </remarks>
    [Fact]
    public void AShaderNobodyMentionedHasNoEntry() {
        var keys = new PermutationKeyDictionary();

        Assert.False(keys.ContainsKey("ForwardPlus"));
        Assert.False(keys.TryGetValue("ForwardPlus", out var absent));
        Assert.Empty(absent);
        Assert.Empty(keys);
    }
}
