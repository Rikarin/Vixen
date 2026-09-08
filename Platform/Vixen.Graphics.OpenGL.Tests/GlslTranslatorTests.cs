// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Graphics.OpenGL.Tests;

/// <summary>The two places the RHI's Vulkan shape shows through into the shader.</summary>
/// <remarks>
///     Both are invisible to a compile: a shader with the wrong binding links and samples the wrong
///     texture, and a shader without the clip fixup renders a picture that is upside down and
///     depth-tested against the wrong half of the range. Neither produces a message from anything.
/// </remarks>
public sealed class GlslTranslatorTests {
    /// <summary>
    ///     The shape that reaches this translator, written the way the thing upstream of it writes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This fixture used to be a shape nothing produces</b> — a combined
    ///         <c>sampler2D</c> in a block with no <c>std140</c> — and that is why two of the three
    ///         defects on #475 were invisible for as long as they were. A test double more permissive
    ///         than the runtime is the failure class <c>CLAUDE.md</c> names, and this file was one.
    ///     </para>
    ///     <para>
    ///         So the uniform block is written as <c>Raven/Vixen.Raven.Tests/Fixtures/lambert.frag.glsl</c>
    ///         writes it, <c>std140</c> first. The <em>sampler</em> is combined because that is what
    ///         reaches this translator after <c>Vixen.Raven.Transpile</c> has been through the
    ///         module; Raven's own separate pair is refused, which
    ///         <see cref="RefusesRavensSeparateTextureAndSampler" /> is the test for.
    ///     </para>
    /// </remarks>
    const string Fragment = """
        #version 450 core
        layout(std140, set = 2, binding = 0) uniform Material { vec4 tint; } material;
        layout(set = 2, binding = 1) uniform sampler2D albedo;
        layout(location = 0) out vec4 colour;
        void main() { colour = texture(albedo, vec2(0.0)) * material.tint; }
        """;

    static GlBindingPlan Plan(int pushConstantBytes = 0) => GlBindingPlan.Build(
        [
            (
                DescriptorSetSlot.PerMaterial,
                [
                    new(0, DescriptorKind.UniformBuffer, ShaderStage.Fragment),
                    new(1, DescriptorKind.SampledTexture, ShaderStage.Fragment),

                    // The standalone sampler Raven's Vulkan GLSL declares beside the texture. It is
                    // in the plan so that RefusesRavensSeparateTextureAndSampler cannot pass for the
                    // wrong reason — without it the throw would be "the layout does not declare
                    // (set = 2, binding = 2)", which is a different defect entirely.
                    new(2, DescriptorKind.Sampler, ShaderStage.Fragment)
                ],
                "material"
            )
        ],
        pushConstantBytes
    );

    /// <summary>The profile's version directive replaces the source's rather than joining it.</summary>
    /// <remarks>
    ///     Two <c>#version</c> lines is a compile error on every driver, and a source that kept its
    ///     own would compile as desktop GLSL on a GLES context — which fails on the first
    ///     <c>precision</c>-less declaration, several hundred lines from the cause.
    /// </remarks>
    [Fact]
    public void ReplacesTheVersionDirective() {
        var result = GlslTranslator.Translate(Fragment, ShaderStage.Fragment, GlProfile.Es32, Plan());

        Assert.StartsWith("#version 320 es", result.Source, StringComparison.Ordinal);
        Assert.Equal(1, result.Source.Split("#version").Length - 1);
    }

    /// <summary>A set-and-binding pair folds into the flat index the plan computed.</summary>
    [Fact]
    public void FoldsSetAndBindingIntoOneIndex() {
        var result = GlslTranslator.Translate(Fragment, ShaderStage.Fragment, GlProfile.Core45, Plan());

        // Separate namespaces, so both are index 0 — a uniform block binding point and a texture
        // unit do not collide, and a plan that shared a counter would waste both.
        Assert.Contains("layout(std140, binding = 0) uniform Material", result.Source, StringComparison.Ordinal);
        Assert.Contains("layout(binding = 0) uniform sampler2D albedo", result.Source, StringComparison.Ordinal);
        Assert.Empty(result.Bindings);

        // ⚠ The half that was actually broken. The qualifier list Raven writes begins with `std140`,
        // and a pattern that expected it to begin with `set` or `binding` matched nothing — so the
        // `set = 2` survived into the source handed to the driver ("'descriptor set' : only allowed
        // when using GLSL for Vulkan") and the block's binding was never folded at all.
        Assert.DoesNotContain("set = ", result.Source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A layout that names no binding — a vertex attribute or a fragment output — is passed
    ///     through exactly as written.
    /// </summary>
    /// <remarks>
    ///     The other half of reading the qualifier list rather than matching a fixed order: the
    ///     pattern now matches every <c>layout(…)</c> in the source, so the ones that are none of
    ///     this rewriter's business have to be recognised and left alone rather than never seen.
    /// </remarks>
    [Fact]
    public void LeavesALayoutWithNoBindingAlone() {
        var result = GlslTranslator.Translate(Fragment, ShaderStage.Fragment, GlProfile.Core45, Plan());
        Assert.Contains("layout(location = 0) out vec4 colour;", result.Source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ The packing qualifier survives the profile that has to drop the binding entirely.
    /// </summary>
    /// <remarks>
    ///     GLSL's default block layout without <c>std140</c> is <c>shared</c>, whose member offsets
    ///     the driver chooses. A block that lost <c>std140</c> along with its binding links and draws
    ///     and reads its own fields out of the wrong bytes — which reads as bad content rather than
    ///     as a bug, and is the reason the qualifier list is rebuilt rather than deleted.
    /// </remarks>
    [Fact]
    public void KeepsThePackingQualifierWhenTheBindingCannotStay() {
        var result = GlslTranslator.Translate(Fragment, ShaderStage.Fragment, GlProfile.Es30, Plan());

        Assert.Contains("layout(std140) uniform Material", result.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("binding", result.Source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     GLSL ES has no default precision for <c>float</c>, so the translator declares one.
    /// </summary>
    /// <remarks>
    ///     ⚠ Raven emits no <c>precision</c> line — it emits Vulkan GLSL, where the qualifier is
    ///     accepted and ignored — so without this every fragment shader is
    ///     <c>'float' : type requires declaration of default precision qualifier</c> on its first
    ///     declaration, hundreds of lines from anything that looks like a cause. <c>highp</c> rather
    ///     than <c>mediump</c>: the engine's world space is metres in a float, and a renderer that
    ///     quietly halved its mantissa on GLES would show up as z-fighting rather than as a message.
    /// </remarks>
    [Theory]
    [InlineData(GlProfile.WebGl2)]
    [InlineData(GlProfile.Es30)]
    [InlineData(GlProfile.Es32)]
    public void DeclaresADefaultPrecisionOnEveryEsProfile(GlProfile profile) {
        var result = GlslTranslator.Translate(Fragment, ShaderStage.Fragment, profile, Plan());

        Assert.Contains("precision highp float;", result.Source, StringComparison.Ordinal);
        Assert.Contains("precision highp int;", result.Source, StringComparison.Ordinal);

        // After the version directive and before anything that could use it — a `precision` line
        // below the first declaration is a syntax error rather than a late default.
        Assert.True(
            result.Source.IndexOf("precision highp float;", StringComparison.Ordinal)
            < result.Source.IndexOf("uniform", StringComparison.Ordinal),
            $"The precision line is below the first declaration:\n{result.Source}"
        );
    }

    /// <summary>Desktop GLSL has defaults of its own and gets no precision line.</summary>
    [Fact]
    public void DeclaresNoPrecisionOnDesktop() {
        var result = GlslTranslator.Translate(Fragment, ShaderStage.Fragment, GlProfile.Core45, Plan());
        Assert.DoesNotContain("precision ", result.Source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ Raven's own separate texture and sampler is refused by name rather than translated.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the honest boundary of this file.</b> A separate <c>texture2D</c> and
    ///         <c>sampler</c> is <c>GL_KHR_vulkan_glsl</c> and is a syntax error on every GL profile,
    ///         desktop included — and combining the pair is a rewrite of every use site
    ///         (<c>texture(sampler2D(albedo, albedoSampler), uv)</c>) plus a question this text
    ///         cannot answer, since one texture read through two samplers is two combined uniforms.
    ///         <c>Vixen.Raven.Transpile</c> does it from the SPIR-V module, offline.
    ///     </para>
    ///     <para>
    ///         So what is asserted here is a refusal, not a translation, and it is worth a test
    ///         because the alternative is what used to happen: the declaration passed through
    ///         untouched and the driver reported <c>syntax error, unexpected IDENTIFIER</c> from a
    ///         line the caller had never written.
    ///     </para>
    /// </remarks>
    [Fact]
    public void RefusesRavensSeparateTextureAndSampler() {
        // Verbatim from Raven/Vixen.Raven.Tests/Fixtures/lambert.frag.glsl, the compiler's own
        // committed golden.
        const string Source = """
            #version 450
            layout(set = 2, binding = 1) uniform texture2D albedo;
            layout(set = 2, binding = 2) uniform sampler albedoSampler;
            layout(location = 0) out vec4 out_result;
            void main() { out_result = texture(sampler2D(albedo, albedoSampler), vec2(0.0)); }
            """;

        var error = Assert.Throws<NotSupportedException>(
            () => GlslTranslator.Translate(Source, ShaderStage.Fragment, GlProfile.Core45, Plan())
        );

        Assert.Contains("uniform texture2D albedo;", error.Message, StringComparison.Ordinal);
        Assert.Contains("Vixen.Raven.Transpile", error.Message, StringComparison.Ordinal);
    }

    /// <summary>And a combined <c>sampler2D</c>, which is what transpilation produces, is not.</summary>
    /// <remarks>
    ///     The other half of the pair above. A refusal that fired on <c>sampler2D</c> too would
    ///     refuse everything, and would still be green in a test that only asserted the throw.
    /// </remarks>
    [Fact]
    public void AcceptsTheCombinedSamplerTranspilationProduces() {
        var result = GlslTranslator.Translate(Fragment, ShaderStage.Fragment, GlProfile.Es30, Plan());
        Assert.Contains("uniform sampler2D albedo;", result.Source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Without explicit bindings the qualifier goes and the name is kept, block names and
    ///     variable names told apart.
    /// </summary>
    /// <remarks>
    ///     They are reached by two different entry points — <c>glGetUniformBlockIndex</c> for the
    ///     block and <c>glGetUniformLocation</c> for the sampler — and swapping them returns
    ///     <c>GL_INVALID_INDEX</c> from one and <c>-1</c> from the other, both of which GL then
    ///     ignores without complaint.
    /// </remarks>
    [Fact]
    public void KeepsNamesWhereBindingsCannotBeDeclared() {
        var result = GlslTranslator.Translate(Fragment, ShaderStage.Fragment, GlProfile.Es30, Plan());

        Assert.DoesNotContain("layout(binding", result.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("set = 2", result.Source, StringComparison.Ordinal);

        Assert.Equal(
            [
                new("Material", DescriptorKind.UniformBuffer, 0),
                new("albedo", DescriptorKind.SampledTexture, 0)
            ],
            result.Bindings
        );
    }

    /// <summary>A vertex shader gets the clip-space fixup on every profile without clip control.</summary>
    /// <remarks>
    ///     <para>
    ///         The <c>z</c> remap is against <c>w</c> and not against <c>1</c>, because it happens in
    ///         clip space rather than in NDC. Doing it after the divide is the usual version of this
    ///         mistake and produces depth that is correct only where <c>w</c> happens to be one —
    ///         which is every orthographic projection and no perspective one, so it looks like it
    ///         works.
    ///     </para>
    /// </remarks>
    [Fact]
    public void WrapsTheVertexEntryPointWhereThereIsNoClipControl() {
        const string Source = "#version 450 core\nvoid main() { gl_Position = vec4(0.0); }";
        var result = GlslTranslator.Translate(Source, ShaderStage.Vertex, GlProfile.Es30, Plan());

        Assert.Contains($"#define main {GlslTranslator.WrappedEntryPoint}", result.Source, StringComparison.Ordinal);
        Assert.Contains("#undef main", result.Source, StringComparison.Ordinal);
        Assert.Contains("gl_Position.y = -gl_Position.y;", result.Source, StringComparison.Ordinal);
        Assert.Contains("gl_Position.z = 2.0 * gl_Position.z - gl_Position.w;", result.Source, StringComparison.Ordinal);
    }

    /// <summary>GL 4.5 gets none of it, because <c>glClipControl</c> already said it.</summary>
    [Fact]
    public void LeavesTheVertexEntryPointAloneOnDesktop() {
        const string Source = "#version 450 core\nvoid main() { gl_Position = vec4(0.0); }";
        var result = GlslTranslator.Translate(Source, ShaderStage.Vertex, GlProfile.Core45, Plan());

        Assert.DoesNotContain("#define main", result.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("gl_Position.y = -", result.Source, StringComparison.Ordinal);
    }

    /// <summary>A fragment shader is never wrapped, on any profile.</summary>
    /// <remarks>It has no <c>gl_Position</c> to fix, and wrapping it would rename a <c>main</c> the
    /// linker is looking for.</remarks>
    [Fact]
    public void NeverWrapsAFragmentShader() {
        var result = GlslTranslator.Translate(Fragment, ShaderStage.Fragment, GlProfile.WebGl2, Plan());
        Assert.DoesNotContain("#define main", result.Source, StringComparison.Ordinal);
    }

    /// <summary>Push constants arrive as a uniform array sized by the layout.</summary>
    [Fact]
    public void DeclaresThePushConstantArray() {
        var result = GlslTranslator.Translate(Fragment, ShaderStage.Fragment, GlProfile.Core45, Plan(36));

        // 36 bytes rounds up to three vec4s. A partially used vector is what a Vulkan push-constant
        // block costs too, once std430 has aligned it.
        Assert.Contains(
            $"uniform vec4 {GlslTranslator.PushConstantUniform}[3];",
            result.Source,
            StringComparison.Ordinal
        );
    }

    /// <summary>A layout with no push constants declares no array at all.</summary>
    [Fact]
    public void DeclaresNothingWhenThereAreNoPushConstants() {
        var result = GlslTranslator.Translate(Fragment, ShaderStage.Fragment, GlProfile.Core45, Plan());
        Assert.DoesNotContain(GlslTranslator.PushConstantUniform, result.Source, StringComparison.Ordinal);
    }

    /// <summary>A binding the layout does not declare is a hard error.</summary>
    /// <remarks>
    ///     The alternative is a shader that links and reads texture unit zero for the life of the
    ///     process. The layout and the shader come from one <c>BindingPlan</c> in Raven
    ///     (<c>docs/plan/07</c> § C), so reaching this means the two have drifted and the message
    ///     should say so.
    /// </remarks>
    [Fact]
    public void RefusesABindingTheLayoutDoesNotHave() {
        const string Source = """
            #version 450 core
            layout(set = 3, binding = 7) uniform sampler2D stray;
            void main() { }
            """;

        var error = Assert.Throws<InvalidOperationException>(
            () => GlslTranslator.Translate(Source, ShaderStage.Fragment, GlProfile.Core45, Plan())
        );

        Assert.Contains("set = 3", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A declaration with no set defaults to per-material, matching the RHI's rule.</summary>
    [Fact]
    public void TreatsAnUnmarkedBindingAsPerMaterial() {
        const string Source = """
            #version 450 core
            layout(binding = 1) uniform sampler2D albedo;
            void main() { }
            """;

        var result = GlslTranslator.Translate(Source, ShaderStage.Fragment, GlProfile.Core45, Plan());
        Assert.Contains("layout(binding = 0) uniform sampler2D albedo", result.Source, StringComparison.Ordinal);
    }

    /// <summary>The body between declarations is passed through untouched.</summary>
    /// <remarks>
    ///     This is a rewriter and not a compiler, and the moment it starts understanding expressions
    ///     it becomes a second GLSL front end that has to agree with the driver's.
    /// </remarks>
    [Fact]
    public void LeavesTheBodyAlone() {
        var result = GlslTranslator.Translate(Fragment, ShaderStage.Fragment, GlProfile.Core45, Plan());

        Assert.Contains(
            "colour = texture(albedo, vec2(0.0)) * material.tint;",
            result.Source,
            StringComparison.Ordinal
        );
    }
}
