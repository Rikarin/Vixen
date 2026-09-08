// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;

namespace Vixen.Graphics.OpenGL;

/// <summary>A resource declaration that has to be bound by name after the link.</summary>
/// <param name="Name">The GLSL identifier — a block name for a buffer, a variable name for a
/// sampler.</param>
/// <param name="Kind">What it binds.</param>
/// <param name="Index">The flat GL index the plan gave it.</param>
readonly record struct GlNamedBinding(string Name, DescriptorKind Kind, uint Index);

/// <summary>A shader after translation.</summary>
/// <param name="Source">What to hand <c>glShaderSource</c>.</param>
/// <param name="Bindings">Declarations the profile could not carry in the source.</param>
readonly record struct TranslatedShader(string Source, IReadOnlyList<GlNamedBinding> Bindings);

/// <summary>Turns the engine's GL-dialect GLSL into what a particular profile will accept.</summary>
/// <remarks>
///     <para>
///         <b>What this is not.</b> It is not a compiler and it does not parse GLSL. The RHI never
///         parses shader source (<c>docs/plan/05</c> § Shader interface) and this does not either —
///         it rewrites <em>declaration qualifiers</em> and wraps <c>main</c>, and everything between
///         is passed through untouched. Raven produces the source; this makes the three profiles
///         agree about where things bind and which way up the world is.
///     </para>
///     <para>
///         ⚠ <b>What it therefore cannot take.</b> Raven's own GLSL is <em>Vulkan</em> GLSL, and one
///         thing in it is not a qualifier: a texture and a sampler declared apart, sampled through
///         <c>sampler2D(albedo, albedoSampler)</c> at every use. Combining them is a rewrite of the
///         use sites and a question about the module — one texture read through two samplers is two
///         combined uniforms — so it belongs to <c>Vixen.Raven.Transpile</c>, offline, through
///         SPIRV-Cross. This refuses such a source by name rather than handing the driver a syntax
///         error. Until the content build carries an <c>essl</c> variant, that refusal is what the
///         GL and WebGL2 backends do with most of the shipped library.
///     </para>
///     <para>
///         <b>Two jobs, and both of them are places the RHI's Vulkan shape shows through.</b>
///     </para>
///     <para>
///         <em>Bindings.</em> The engine's GLSL declares resources the way Vulkan does —
///         <c>layout(set = 2, binding = 1)</c> — because that is the vocabulary the RHI, Raven's
///         reflection and the descriptor-set layouts all share. GL has no sets. Where the profile
///         has <c>layout(binding = …)</c> (GL 4.2, GLES 3.1) the pair is folded into the flat index
///         <see cref="GlBindingPlan" /> computed; where it does not (GLES 3.0, WebGL2) the qualifier
///         is removed entirely and the declaration's <em>name</em> is kept, so the binding can be
///         assigned after the link with <c>glUniformBlockBinding</c> or <c>glUniform1i</c>. That
///         second path is why this returns names at all.
///     </para>
///     <para>
///         <em>Clip space.</em> The engine is <b>+Y up</b> with reversed depth in <c>[0, 1]</c>
///         (<c>Core/Vixen.Core.Mathematics/Conventions.md</c>) — which is neither API's default.
///         Vulkan is <c>y</c> down and <c>[0, 1]</c>, so the reference backend renders through a
///         negative-height viewport. GL is <c>y</c> up and <c>[-1, 1]</c>, so the depth range is
///         what has to change here — and the <c>y</c> axis has to change <em>too</em>, so that clip
///         <c>y = +1</c> reaches texel row zero as it does on Vulkan rather than the opposite end of
///         the image.
///     </para>
///     <para>
///         On GL 4.5 both are one call: <c>glClipControl(GL_UPPER_LEFT, GL_ZERO_TO_ONE)</c>.
///         Everywhere else the vertex shader does it, so <c>main</c> is renamed and a new one wraps
///         it. The alternative — asking every shader in the engine to write the fixup itself — is
///         the sort of thing that is right in eleven shaders and forgotten in the twelfth.
///     </para>
/// </remarks>
static partial class GlslTranslator {
    /// <summary>The uniform array push constants arrive in.</summary>
    /// <remarks>
    ///     GL has no push constants and no equivalent. A <c>vec4</c> array is the cheapest stand-in:
    ///     one <c>glUniform4fv</c> per change, no buffer, no allocation, and the same 128-byte floor
    ///     the RHI guarantees everywhere.
    /// </remarks>
    public const string PushConstantUniform = "vixen_PushConstants";

    /// <summary>What <c>main</c> is renamed to when the clip-space fixup is needed.</summary>
    public const string WrappedEntryPoint = "vixen_main";

    /// <summary>Translates one shader.</summary>
    /// <param name="source">The engine's GL-dialect GLSL.</param>
    /// <param name="stage">Which stage it is.</param>
    /// <param name="profile">Which dialect to produce.</param>
    /// <param name="plan">Where the pipeline layout put each binding.</param>
    /// <param name="slotOf">
    ///     Which descriptor set a bare <c>layout(binding = n)</c> belongs to when the declaration
    ///     names no set. Per-material, matching the RHI's rule for an unmarked binding.
    /// </param>
    public static TranslatedShader Translate(
        string source,
        ShaderStage stage,
        GlProfile profile,
        GlBindingPlan plan,
        DescriptorSetSlot slotOf = DescriptorSetSlot.PerMaterial
    ) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(plan);

        var named = new List<GlNamedBinding>();
        var body = StripVersion(source);

        RefuseSeparateTextureAndSampler(body);

        body = QualifierPattern().Replace(
            body,
            match => Rewrite(match, profile, plan, slotOf, named)
        );

        var builder = new StringBuilder();
        builder.AppendLine(profile.ShaderVersion());

        // ⚠ GLSL ES has no default precision for `float` in a fragment or compute stage, so a source
        // that declares none is "'float' : type requires declaration of default precision qualifier"
        // — on the first declaration, which is nowhere near the cause. Raven emits none: it emits
        // Vulkan GLSL, where precision qualifiers are accepted and ignored. `highp` rather than
        // `mediump` because the engine's world-space maths is metres in a float and mediump is ten
        // bits of mantissa on a phone; a renderer that quietly halved its precision on GLES would
        // show up as z-fighting rather than as a message.
        if (profile < GlProfile.Core45) {
            builder.AppendLine("precision highp float;");
            builder.AppendLine("precision highp int;");
        }

        if (plan.PushConstantVectors > 0) {
            builder.AppendLine(
                $"uniform vec4 {PushConstantUniform}[{plan.PushConstantVectors}];"
            );
        }

        var wraps = stage == ShaderStage.Vertex && !profile.HasClipControl();

        if (wraps) {
            builder.AppendLine($"#define main {WrappedEntryPoint}");
        }

        builder.AppendLine(body.TrimEnd());

        if (wraps) {
            builder.AppendLine("#undef main");
            builder.AppendLine("void main() {");
            builder.AppendLine($"    {WrappedEntryPoint}();");

            // The same axis change glClipControl(GL_UPPER_LEFT, …) makes on desktop, so that clip
            // y = +1 lands at texel row zero on both. It reverses triangle winding, which is why
            // GlEnums.Winding inverts the front face as well; the two are one change and are wrong
            // separately.
            builder.AppendLine("    gl_Position.y = -gl_Position.y;");

            // Depth [0, 1] to [-1, 1]. In clip space, before the perspective divide, so the scale
            // is against w and not against 1 — doing it after the divide is the classic version of
            // this bug and produces depth that is correct only where w happens to be 1.
            builder.AppendLine("    gl_Position.z = 2.0 * gl_Position.z - gl_Position.w;");
            builder.AppendLine("}");
        }

        return new(builder.ToString(), named);
    }

    /// <summary>Whether a source already carries a version directive.</summary>
    /// <remarks>Public for the test that asserts the directive is replaced rather than duplicated —
    /// two <c>#version</c> lines is a compile error on every driver.</remarks>
    public static bool HasVersion(string source) => VersionPattern().IsMatch(source);

    static string StripVersion(string source) => VersionPattern().Replace(source, string.Empty, 1);

    static string Rewrite(
        Match match,
        GlProfile profile,
        GlBindingPlan plan,
        DescriptorSetSlot fallback,
        List<GlNamedBinding> named
    ) {
        var declaration = match.Groups["declaration"].Value;

        // ⚠ Everything in the qualifier list that is neither `set` nor `binding` is kept, and the
        // list is read rather than matched in order. Raven writes `layout(std140, set = 2,
        // binding = 0)`; a pattern that required the list to *begin* with `set` or `binding` matched
        // none of the engine's uniform blocks, left `set = 2` in the source — "'descriptor set' :
        // only allowed when using GLSL for Vulkan" on every profile — and never folded the block's
        // binding or reported its name. And `std140` is not decoration: dropping it leaves the block
        // at GLSL's `shared` layout, which the host's std140-packed upload does not match.
        var (set, binding, kept) = Qualifiers(match.Groups["qualifiers"].Value);

        // A layout carrying no binding at all — `layout(location = 0) in vec3 normal;` — is not this
        // rewriter's business and is passed through exactly as written.
        if (binding is not { } index) {
            return match.Value;
        }

        var slot = set is { } declared ? (DescriptorSetSlot)declared : fallback;

        var resolved = plan.Resolve(slot, index)
            ?? throw new InvalidOperationException(
                $"A shader declares (set = {(int)slot}, binding = {index}) and the pipeline layout it "
                + "was compiled against does not. The layout and the shader come from one BindingPlan in "
                + "Raven (docs/plan/07 § C), so this means they have drifted apart."
            );

        if (profile.HasExplicitBindings()) {
            var qualifiers = kept.Count == 0
                ? $"binding = {resolved.Index}"
                : $"{string.Join(", ", kept)}, binding = {resolved.Index}";

            return $"layout({qualifiers}) {declaration}";
        }

        // No explicit bindings: the qualifier has to go, and the name has to be kept so the binding
        // can be assigned after the link. A declaration whose name cannot be found is a hard error —
        // the alternative is a shader that links and samples texture unit zero forever.
        var name = NameOf(declaration)
            ?? throw new InvalidOperationException(
                $"Could not find the identifier to bind in '{declaration.Trim()}'. On {profile} a binding "
                + "is assigned by name after the link, so a declaration this translator cannot read is one "
                + "nothing could bind."
            );

        named.Add(new(name, resolved.Kind, resolved.Index));

        // The binding goes and the packing stays. `std140` is what the host's upload assumes and
        // GLSL's default without it is `shared`, whose offsets the driver is free to choose — so a
        // block that lost the qualifier along with the binding reads its own fields from the wrong
        // bytes, which is the sort of wrong that looks like bad content rather than like a bug.
        return kept.Count == 0 ? declaration : $"layout({string.Join(", ", kept)}) {declaration}";
    }

    /// <summary>Reads a layout qualifier list, whatever order it is written in.</summary>
    /// <returns>The set and binding it named, and every other qualifier, in source order.</returns>
    static (int? Set, uint? Binding, List<string> Kept) Qualifiers(string list) {
        int? set = null;
        uint? binding = null;
        List<string> kept = [];

        foreach (var raw in list.Split(',')) {
            var qualifier = raw.Trim();

            if (qualifier.Length == 0) {
                continue;
            }

            var pair = KeyValuePattern().Match(qualifier);

            switch (pair.Success ? pair.Groups["key"].Value : null) {
                case "set":
                    set = int.Parse(pair.Groups["value"].Value);
                    break;

                case "binding":
                    binding = uint.Parse(pair.Groups["value"].Value);
                    break;

                default:
                    kept.Add(qualifier);
                    break;
            }
        }

        return (set, binding, kept);
    }

    /// <summary>
    ///     Refuses the one shape of Raven's GLSL no re-headering can carry to GL.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A separate texture and sampler is a syntax error on every GL profile, not a
    ///         dialect wrinkle.</b> Raven emits Vulkan GLSL — <c>uniform texture2D albedo;</c>,
    ///         <c>uniform sampler albedoSampler;</c>, and
    ///         <c>texture(sampler2D(albedo, albedoSampler), uv)</c> at every use — which is
    ///         <c>GL_KHR_vulkan_glsl</c>. GL has only the combined object, so combining the pair is a
    ///         rewrite of every use site and of the declaration together, and which pairs exist is a
    ///         fact about the module rather than about the text: one texture sampled through two
    ///         samplers is two combined uniforms.
    ///     </para>
    ///     <para>
    ///         That is <c>Vixen.Raven.Transpile</c>'s job, offline, through SPIRV-Cross — and it is
    ///         deliberately not this one's. What this does is refuse the input by name rather than
    ///         hand the driver a shader whose first error is <c>syntax error, unexpected
    ///         IDENTIFIER</c> several declarations away from the cause, which is what happened to
    ///         every Raven shader with a texture in it before this check existed.
    ///     </para>
    /// </remarks>
    static void RefuseSeparateTextureAndSampler(string body) {
        var vulkan = VulkanOpaquePattern().Match(body);

        if (!vulkan.Success) {
            return;
        }

        throw new NotSupportedException(
            $"'{vulkan.Value.Trim()}' is Vulkan GLSL: a texture and a sampler declared apart. GL has "
            + "no such type at any profile, and combining the pair is a rewrite of every use site "
            + "rather than of the declaration, so this translator cannot do it and does not pretend "
            + "to. Cross-compile the shader with Vixen.Raven.Transpile (the compiler's 'essl' "
            + "target) and hand this backend the combined GLSL instead."
        );
    }

    /// <summary>The GLSL identifier a declaration binds by.</summary>
    /// <remarks>
    ///     A block — <c>uniform Material { … }</c> — is reached by its <em>block</em> name, and an
    ///     opaque uniform — <c>uniform sampler2D albedo;</c> — by its variable name. Two different
    ///     entry points want the two, and confusing them yields <c>GL_INVALID_INDEX</c> from one and
    ///     <c>-1</c> from the other, both of which GL then ignores silently.
    /// </remarks>
    static string? NameOf(string declaration) {
        var block = BlockPattern().Match(declaration);

        if (block.Success) {
            return block.Groups["name"].Value;
        }

        var variable = VariablePattern().Match(declaration);
        return variable.Success ? variable.Groups["name"].Value : null;
    }

    /// <summary>
    ///     A layout qualifier list and the declaration it introduces, up to the opening brace or the
    ///     semicolon.
    /// </summary>
    /// <remarks>
    ///     ⚠ The whole list is captured and read in <see cref="Qualifiers" /> rather than being
    ///     matched key by key. A pattern that spelled out the order it expected — <c>set</c> then
    ///     <c>binding</c> — silently matched nothing at all in the source Raven actually emits,
    ///     which begins <c>layout(std140, …</c>.
    /// </remarks>
    [GeneratedRegex(
        @"layout\s*\(\s*(?<qualifiers>[^)]*)\)\s*(?<declaration>[^;{]*[;{])",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex QualifierPattern();

    /// <summary>One <c>key = value</c> of a layout qualifier list.</summary>
    [GeneratedRegex(@"^(?<key>[A-Za-z_]\w*)\s*=\s*(?<value>\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyValuePattern();

    /// <summary>
    ///     A Vulkan-GLSL opaque declaration — a bare <c>sampler</c>, or a <c>texture…</c> type with
    ///     no sampler of its own.
    /// </summary>
    /// <remarks>
    ///     <c>sampler</c> and <c>samplerShadow</c> only: <c>sampler2D</c> is GL's combined object and
    ///     is exactly what this backend wants, so the word boundary after <c>sampler</c> is the whole
    ///     distinction and dropping it would refuse every shader instead of the Vulkan-shaped ones.
    /// </remarks>
    [GeneratedRegex(
        @"\buniform\s+(?:(?:high|medium|low)p\s+)?(?:[iu]?texture(?:1D|2D|3D|Cube|Buffer|2DMS)(?:Array)?|sampler(?:Shadow)?\b)\s+[A-Za-z_]\w*\s*(?:\[[^\]]*\])?\s*;",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex VulkanOpaquePattern();

    /// <summary>A version directive, wherever the source put it.</summary>
    [GeneratedRegex(@"^[ \t]*#version[^\r\n]*\r?\n?", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    /// <summary>An interface block — the name before the brace is what binds it.</summary>
    [GeneratedRegex(
        @"\b(?:uniform|buffer|readonly\s+buffer|writeonly\s+buffer)\s+(?<name>[A-Za-z_]\w*)\s*\{",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex BlockPattern();

    /// <summary>An opaque uniform — the last identifier before the semicolon binds it.</summary>
    [GeneratedRegex(
        @"\buniform\s+(?:highp\s+|mediump\s+|lowp\s+)?[A-Za-z_]\w*\s+(?<name>[A-Za-z_]\w*)\s*(?:\[[^\]]*\])?\s*;",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex VariablePattern();
}
