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
    const string Fragment = """
        #version 450 core
        layout(set = 2, binding = 0) uniform Material { vec4 tint; } material;
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
                    new(1, DescriptorKind.SampledTexture, ShaderStage.Fragment)
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
        Assert.Contains("layout(binding = 0) uniform Material", result.Source, StringComparison.Ordinal);
        Assert.Contains("layout(binding = 0) uniform sampler2D albedo", result.Source, StringComparison.Ordinal);
        Assert.Empty(result.Bindings);
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
