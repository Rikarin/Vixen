// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Rendering.Materials;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>The upsample variant compiles against the whole library, and binds what <c>Apply</c> writes.</summary>
/// <remarks>
///     No device — the claim is about the compilation and its reflected names. A frame drawing the
///     pass is the next increment; until it exists, this is what keeps the pass from being a shader
///     nobody has asked the compiler for, which is the state every silent no-draw here has started in.
/// </remarks>
public sealed class ScreenProbeUpsampleCompileTests {
    [Fact]
    public void TheUpsampleCompilesAndBindsTheProbePlanes() {
        var key = EffectKey.Of("ScreenProbeUpsample", [], MaterialCompiler.PassComposition());
        var data = RavenEffects.Everything().TryGet(key);

        Assert.NotNull(data);

        var names = data!.Bindings.Select(binding => binding.Name).ToArray();

        // The names ScreenProbeTexture.Apply writes — unqualified, because the planes are declared
        // directly rather than composed. A rename on either side has to fail here, not in a frame.
        foreach (var plane in new[] { "probeL0", "probeL1R", "probeL1G", "probeL1B", "probeSurface", "probeNormal" }) {
            Assert.Contains(plane, names);
        }

        Assert.Contains("depthBuffer", names);
        Assert.Contains("normalBuffer", names);
        Assert.Contains("pointSampler", names);
    }
}
