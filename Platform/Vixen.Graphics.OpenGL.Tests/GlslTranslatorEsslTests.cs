// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Graphics.OpenGL.Tests;

/// <summary>
///     What <see cref="GlslTranslator" /> emits, handed to a real GLSL ES front end.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing compiled this backend's output before this file.</b> Every other assertion
///         about the translator is a substring match on the string it returned, and a substring match
///         cannot tell a shader a driver accepts from one it does not — which is how a source with
///         <c>set = 2</c> still in it and no <c>precision</c> line was green for as long as it was.
///         <c>GlProfile.ShaderVersion</c> names <c>#version 300 es</c> and <c>#version 320 es</c>;
///         GLSL ES is materially stricter than desktop GLSL and is stricter in exactly the places a
///         Vulkan-shaped emitter gets wrong, so compiling this against desktop GLSL 450 would prove
///         nothing at all.
///     </para>
///     <para>
///         The input is what reaches the backend <em>after</em> <c>Vixen.Raven.Transpile</c> —
///         combined samplers, because GL has no other kind — and what is under test is everything the
///         translator does on top of that.
///     </para>
/// </remarks>
public sealed class GlslTranslatorEsslTests {
    /// <summary>
    ///     The front end is installed, so everything else in this file means something.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>First, and a hard failure rather than a skip.</b> Every other case here calls
    ///     <c>EsslFrontEnd.Validate</c>, which asserts the tool is present; without this one, a
    ///     machine with no glslang would fail several tests with several confusing messages, and one
    ///     where the tool was found but did nothing would pass. This says which, in one line.
    /// </remarks>
    [Fact]
    public void TheFrontEndIsInstalledSoThisFileMeansSomething() =>
        Assert.True(EsslFrontEnd.Validator is not null, EsslFrontEnd.HowToInstall);

    /// <summary>A fragment shader, translated for GLES 3.2, is accepted by an ES front end.</summary>
    /// <remarks>
    ///     Three separate reasons it would not have been before: the <c>set = 2</c> the qualifier
    ///     pattern never matched, the absent default precision, and a <c>std140</c> that went out
    ///     with the binding.
    /// </remarks>
    [Fact]
    public void ATranslatedFragmentShaderCompilesAsGlslEs() {
        var result = GlslTranslator.Translate(Fragment, ShaderStage.Fragment, GlProfile.Es32, Plan());
        var (accepted, log) = EsslFrontEnd.Validate(result.Source, ShaderStage.Fragment);

        Assert.True(accepted, $"The translated fragment shader was refused:\n{log}\n\n{result.Source}");
    }

    /// <summary>And a vertex shader, whose <c>main</c> is wrapped on every profile below 4.5.</summary>
    /// <remarks>
    ///     ⚠ The wrap is the part most likely to be a syntax error rather than a wrong picture: it is
    ///     a <c>#define</c>, the body, an <c>#undef</c> and a second <c>main</c>, assembled by string
    ///     concatenation. A picture-level test cannot run without a device and would not have caught
    ///     a missing brace; this does, on a desk.
    /// </remarks>
    [Fact]
    public void AWrappedVertexShaderCompilesAsGlslEs() {
        var result = GlslTranslator.Translate(Vertex, ShaderStage.Vertex, GlProfile.Es32, Plan(64));

        Assert.Contains($"#define main {GlslTranslator.WrappedEntryPoint}", result.Source, StringComparison.Ordinal);

        var (accepted, log) = EsslFrontEnd.Validate(result.Source, ShaderStage.Vertex);

        Assert.True(accepted, $"The wrapped vertex shader was refused:\n{log}\n\n{result.Source}");
    }

    /// <summary>
    ///     ⚠ And on GLES 3.0 and WebGL2 it is still refused, for a reason that is not this
    ///     translator's to fix — pinned in both directions so it cannot rot.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The fourth defect, found by installing the front end rather than by reading.</b>
    ///         GLSL ES 3.00 allows <c>layout(location = …)</c> on a vertex <em>input</em> and a
    ///         fragment <em>output</em> and nowhere else: a vertex output or a fragment input carrying
    ///         one is <c>'location qualifier on output' : not supported in this stage</c>. Raven's
    ///         emitter writes a location on all four (<c>GlslEmitter.cs:560,588</c>), so the varyings
    ///         of every shipped shader are illegal at ES 3.00.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And stripping the qualifier here would be worse than leaving it.</b> Below ES 3.10
    ///         varyings link by <em>name</em>, and Raven names the same stream <c>out_uv</c> in the
    ///         producing stage and <c>in_uv</c> in the consuming one (<c>GlslEmitter.cs:550,567</c>) —
    ///         so a translator that removed the locations would turn a compile error, which names the
    ///         line, into a silent link failure, which does not. The two halves are one change and
    ///         they are in the emitter, or they are in the cross-compiler that renames both ends.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The cross-compiler half has since landed and this is still red, which is the
    ///         point.</b> <c>SpirvCrossTranspiler.NameVaryingsByLocation</c> now names both ends
    ///         <c>vary_&lt;location&gt;</c>, and ⚠ SPIRV-Cross had <em>not</em> been doing that on
    ///         its own — 31 of the library's 32 vertex/fragment pairs came out with mismatched
    ///         varying names, which no per-stage front end and not even <c>glslangValidator -l</c>
    ///         can see. What is left is #475's wiring: this backend is still handed Raven's own
    ///         Vulkan GLSL rather than the transpiled ESSL, so nothing that fix produces reaches
    ///         here yet.
    ///     </para>
    ///     <para>
    ///         Held in both directions: the day this starts compiling, this test fails and should be
    ///         deleted rather than adjusted.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(GlProfile.WebGl2)]
    [InlineData(GlProfile.Es30)]
    public void AVaryingsLocationIsStillIllegalBelowEs31(GlProfile profile) {
        var result = GlslTranslator.Translate(Fragment, ShaderStage.Fragment, profile, Plan());
        var (accepted, log) = EsslFrontEnd.Validate(result.Source, ShaderStage.Fragment);

        Assert.False(
            accepted,
            $"{profile} now accepts a fragment input carrying layout(location = …). If that is "
            + "genuinely true, delete this test — it exists to say the GLES 3.0 head is not finished."
            + $"\n\n{result.Source}"
        );

        Assert.Contains("location qualifier on input", log, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ And the same source untranslated is refused, so the case above is a differential rather
    ///     than a smoke test.
    /// </summary>
    /// <remarks>
    ///     One input, two paths, and the assertion is that they disagree. Without this half, a
    ///     translator that had quietly become the identity function would still be green above — the
    ///     fixture is legal desktop GLSL, and "it compiles" is not a claim about the translation.
    /// </remarks>
    [Fact]
    public void TheSameSourceWithOnlyItsVersionLineChangedIsRefused() {
        var reheadered = "#version 300 es\n"
            + Fragment[(Fragment.IndexOf('\n') + 1)..];

        var (accepted, log) = EsslFrontEnd.Validate(reheadered, ShaderStage.Fragment);

        Assert.False(
            accepted,
            "Re-headering the source is enough to make it GLSL ES, which would mean this translator "
            + $"has nothing to do:\n{reheadered}"
        );

        Assert.False(string.IsNullOrWhiteSpace(log), "The ES front end refused it and said nothing.");
    }

    /// <summary>
    ///     ⚠ <c>noperspective</c> is dropped for an ES head, because GLSL ES has no such qualifier at
    ///     any version — which is #1222.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The input is what Raven's <c>[Interpolation("noperspective")]</c> emits.</b>
    ///         <c>GlslEmitter.Qualifier</c> writes the word on both ends of the varying, and its own
    ///         remark said the qualifier "reaches a GLES head through SPIRV-Cross rather than through
    ///         this emitter's text". It reaches one through this translator too: the qualifier sits
    ///         <em>outside</em> the <c>layout(…)</c> parentheses, and until #1222 that was the only
    ///         thing this file rewrote.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>Es32</c> and not <c>Es30</c>, and the choice is the instrument.</b> At ES 3.00
    ///         a varying carrying <c>layout(location = …)</c> is refused for its own reason — see
    ///         <see cref="AVaryingsLocationIsStillIllegalBelowEs31" /> — so a case there would go
    ///         green the day this translator stopped dropping the word and the front end would still
    ///         be refusing the shader. <c>#version 320 es</c> accepts the location and refuses
    ///         <c>noperspective</c>, so acceptance here is a statement about this change alone.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And <c>Es32</c> refuses it at all is itself worth pinning</b>: #1222 says
    ///         "GLSL ES 3.0 has no such qualifier", which reads as though 3.10 or 3.20 might. None
    ///         of them do — it is a <em>reserved word</em> in every ES version — which is why
    ///         <c>GlProfiles.HasNoPerspective</c> is <c>Core45</c> and not <c>Es32</c>.
    ///     </para>
    /// </remarks>
    [Fact]
    public void NoPerspectiveIsDroppedForAnEsHead() {
        var translated = GlslTranslator.Translate(NoPerspectiveFragment, ShaderStage.Fragment, GlProfile.Es32, Plan());

        Assert.DoesNotContain("noperspective", translated.Source, StringComparison.Ordinal);

        var (accepted, log) = EsslFrontEnd.Validate(translated.Source, ShaderStage.Fragment);

        Assert.True(accepted, $"The translated fragment shader was refused:\n{log}\n\n{translated.Source}");

        // ⚠ The other half, so this is a differential rather than a smoke test: the same source with
        // only its version line changed is refused, and refused for this word. Without it a
        // translator that had become the identity function would still be green above the day
        // glslang stopped caring.
        var reheadered = "#version 320 es\nprecision highp float;\n"
            + NoPerspectiveFragment[(NoPerspectiveFragment.IndexOf('\n') + 1)..];

        var (stillAccepted, refusal) = EsslFrontEnd.Validate(reheadered, ShaderStage.Fragment);

        Assert.False(stillAccepted, $"An ES front end now accepts 'noperspective':\n{reheadered}");
        Assert.Contains("noperspective", refusal, StringComparison.Ordinal);
    }

    /// <summary>And on the desktop profile the word is kept, because there it is legal and load-bearing.</summary>
    /// <remarks>
    ///     ⚠ The half that makes the case above a translation rather than a deletion. A rewriter that
    ///     dropped <c>noperspective</c> unconditionally would satisfy every ES assertion in this file
    ///     and would silently make every desktop GL shader interpolate a screen-space value
    ///     perspective-correctly — a wrong picture with nothing to report it.
    /// </remarks>
    [Fact]
    public void NoPerspectiveSurvivesTheDesktopHead() {
        var translated = GlslTranslator.Translate(
            NoPerspectiveFragment,
            ShaderStage.Fragment,
            GlProfile.Core45,
            Plan()
        );

        Assert.Contains("noperspective in vec2 in_screenUv;", translated.Source, StringComparison.Ordinal);
    }

    // --- The shaders and the plumbing ---------------------------------------

    /// <summary>
    ///     What <c>[Interpolation("noperspective")]</c> reaches this backend as.
    /// </summary>
    /// <remarks>
    ///     The exact string <c>Raven/Vixen.Raven.Tests/InterpolationTests.cs:125</c> asserts the
    ///     emitter writes, with the location Raven puts on every varying. No sampler and no uniform
    ///     block: the two the other fixture carries are this file's other subject and would refuse
    ///     the shader for their own reasons on an ES head.
    /// </remarks>
    const string NoPerspectiveFragment = """
        #version 450 core
        layout(location = 0) noperspective in vec2 in_screenUv;
        layout(location = 0) out vec4 out_result;
        void main() { out_result = vec4(in_screenUv, 0.0, 1.0); }
        """;

    /// <summary>
    ///     What reaches this backend: Raven's bindings, with the texture and sampler already
    ///     combined by <c>Vixen.Raven.Transpile</c>.
    /// </summary>
    /// <remarks>
    ///     The uniform block is written <c>std140</c>-first because that is how Raven writes it — see
    ///     <c>Raven/Vixen.Raven.Tests/Fixtures/lambert.frag.glsl</c>. It is legal desktop GLSL and
    ///     illegal GLSL ES twice over, which is the whole point of the pair of cases above.
    /// </remarks>
    const string Fragment = """
        #version 450 core
        layout(std140, set = 2, binding = 0) uniform Material { vec4 tint; } material;
        layout(set = 2, binding = 1) uniform sampler2D albedo;
        layout(location = 0) in vec2 in_uv;
        layout(location = 0) out vec4 out_result;
        void main() { out_result = texture(albedo, in_uv) * material.tint; }
        """;

    const string Vertex = """
        #version 450 core
        layout(std140, set = 2, binding = 0) uniform Material { vec4 tint; } material;
        layout(location = 0) in vec3 in_position;
        layout(location = 0) out vec2 out_uv;
        void main() {
            out_uv = in_position.xy;
            gl_Position = vec4(in_position, 1.0) * material.tint;
        }
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
}
