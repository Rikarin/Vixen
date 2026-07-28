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

        body = QualifierPattern().Replace(
            body,
            match => Rewrite(match, profile, plan, slotOf, named)
        );

        var builder = new StringBuilder();
        builder.AppendLine(profile.ShaderVersion());

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
        var slot = match.Groups["set"].Success
            ? (DescriptorSetSlot)int.Parse(match.Groups["set"].Value)
            : fallback;

        var binding = uint.Parse(match.Groups["binding"].Value);
        var declaration = match.Groups["declaration"].Value;

        var resolved = plan.Resolve(slot, binding)
            ?? throw new InvalidOperationException(
                $"A shader declares (set = {(int)slot}, binding = {binding}) and the pipeline layout it "
                + "was compiled against does not. The layout and the shader come from one BindingPlan in "
                + "Raven (docs/plan/07 § C), so this means they have drifted apart."
            );

        if (profile.HasExplicitBindings()) {
            return $"layout(binding = {resolved.Index}) {declaration}";
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
        return declaration;
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
    ///     A Vulkan-style binding qualifier and the declaration it introduces, up to the opening
    ///     brace or the semicolon.
    /// </summary>
    [GeneratedRegex(
        @"layout\s*\(\s*(?:set\s*=\s*(?<set>\d+)\s*,\s*)?binding\s*=\s*(?<binding>\d+)\s*(?:,[^)]*)?\)\s*(?<declaration>[^;{]*[;{])",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex QualifierPattern();

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
