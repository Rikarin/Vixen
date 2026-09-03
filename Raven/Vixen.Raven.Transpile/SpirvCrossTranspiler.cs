// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.InteropServices;
using Silk.NET.SPIRV.Cross;

namespace Vixen.Raven.Transpile;

/// <summary>
///     SPIRV-Cross said "no", with its own words.
/// </summary>
/// <remarks>
///     An exception rather than a diagnostic, because this type knows nothing about which shader it
///     is translating. <see cref="EsslBackend" /> is the layer that has a name and a stage to put in
///     front of the message, and it is where this becomes an <c>RVN4002</c>.
/// </remarks>
sealed class SpirvCrossException : Exception {
    public SpirvCrossException(string message) : base(message) { }

    public SpirvCrossException(string message, Exception innerException) : base(message, innerException) { }

    public SpirvCrossException() { }
}

/// <summary>
///     Cross-compiles a SPIR-V module to GLSL through SPIRV-Cross.
/// </summary>
/// <remarks>
///     <para>
///         <b>ADR-012's other half.</b> Raven's own backends are SPIR-V and Vulkan GLSL; every
///         further dialect is this tool's job rather than a fifth emitter. The decision is worth
///         re-reading before adding one: one well-tested backend beats five half-tested ones, and
///         SPIRV-Cross is Khronos's own and is what MoltenVK uses underneath in any case.
///     </para>
///     <para>
///         ⚠ <b>What Raven's Vulkan GLSL cannot do, and why this is not redundant with it.</b>
///         The GLSL <c>Vixen.Raven</c> emits is Vulkan GLSL and only that. Three separate things in
///         it are rejected by a GL or GLES front end, each measured against
///         <c>glslangValidator</c> rather than argued:
///     </para>
///     <list type="number">
///         <item>
///             <description>
///                 <c>uniform texture2D albedo; uniform sampler albedoSampler;</c> and the
///                 <c>sampler2D(albedo, albedoSampler)</c> that reads them are
///                 <c>GL_KHR_vulkan_glsl</c> constructs. On desktop GLSL they are a
///                 <em>syntax error</em>; there is no version of GL where they parse.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <c>layout(set = 2, …)</c> is <i>"only allowed when using GLSL for Vulkan"</i>.
///             </description>
///         </item>
///         <item>
///             <description>
///                 GLSL ES has no default precision for <c>float</c> in a fragment shader, so the
///                 first declaration is <i>"type requires declaration of default precision
///                 qualifier"</i>. Raven emits no <c>precision</c> line anywhere.
///             </description>
///         </item>
///     </list>
///     <para>
///         SPIRV-Cross fixes all three, and fixes them from the <em>module</em> rather than from the
///         text — the combined sampler is built by rewriting the SPIR-V, which is why it can tell a
///         host which pairs it made (<see cref="CombinedSampler" />) and a regex over the
///         source could not.
///     </para>
///     <para>
///         <b>What it deliberately does not do:</b> the clip-space and depth-range fixup. The
///         engine is +Y up with reversed depth in <c>[0, 1]</c> and GL is neither; that is
///         <c>GlslTranslator</c>'s wrapped <c>main</c> on the runtime side, a convention rather than
///         a dialect, and putting it here would apply it twice on any profile that has
///         <c>glClipControl</c>.
///     </para>
/// </remarks>
static unsafe class SpirvCrossTranspiler {
    /// <summary>Cross-compiles one SPIR-V module.</summary>
    /// <param name="spirv">The module, as the words <c>SpirvBackend</c> wrote.</param>
    /// <param name="dialect">Which GLSL to produce.</param>
    /// <returns>The source, and the texture/sampler pairs that had to be combined to get it.</returns>
    /// <exception cref="SpirvCrossException">SPIRV-Cross refused the module.</exception>
    public static TranspiledShader Transpile(ReadOnlySpan<byte> spirv, GlslDialect dialect) {
        if (spirv.Length == 0 || spirv.Length % 4 != 0) {
            throw new SpirvCrossException(
                $"A SPIR-V module is a whole number of 32-bit words; this is {spirv.Length} bytes."
            );
        }

        var words = new uint[spirv.Length / 4];
        MemoryMarshal.Cast<byte, uint>(spirv).CopyTo(words);

        var cross = Cross.GetApi();
        Context* context = null;

        try {
            if (cross.ContextCreate(&context) != Result.Success) {
                throw new SpirvCrossException("SPIRV-Cross would not create a context.");
            }

            ParsedIr* ir;

            fixed (uint* first = words) {
                ParsedIr* parsed = null;
                Check(cross, context, cross.ContextParseSpirv(context, first, (nuint)words.Length, &parsed), "parse");
                ir = parsed;
            }

            Compiler* compiler = null;

            Check(
                cross,
                context,
                cross.ContextCreateCompiler(context, Backend.Glsl, ir, CaptureMode.TakeOwnership, &compiler),
                "create the GLSL compiler for"
            );

            Configure(cross, context, compiler, dialect);

            // ⚠ BEFORE the combining pass, and it is not optional. A `texture.Load(…)` lowers to
            // OpImageFetch on a bare OpTypeImage with no sampler at all — which is legal SPIR-V and
            // has no GLSL spelling, because `texelFetch` takes a sampler2D. SPIRV-Cross refuses the
            // whole module for it — "texelFetch without sampler was found, but no dummy sampler has
            // been created" — rather than emitting something wrong, so this creates the sampler that
            // fetch will be combined with. Found by the library sweep and not by reading the header:
            // three shipped shaders take this path (`Culling`, `HiZReduce`, `NearestReduce`) and
            // every fixture with only `Sample` in it passes without this line.
            uint dummy = 0;

            Check(
                cross,
                context,
                cross.CompilerBuildDummySamplerForCombinedImages(compiler, &dummy),
                "create the fetch sampler for"
            );

            // ⚠ Before the compile and not after: this REWRITES the module, replacing every
            // (image, sampler) pair the code actually uses with one combined object. Asking for the
            // list afterwards would return what a second, unrewritten module contains — nothing.
            Check(cross, context, cross.CompilerBuildCombinedImageSamplers(compiler), "combine the samplers of");

            var combined = NameCombinedSamplers(cross, context, compiler);

            byte* source = null;
            Check(cross, context, cross.CompilerCompile(compiler, &source), "compile");

            return new(
                Marshal.PtrToStringUTF8((nint)source)
                ?? throw new SpirvCrossException("SPIRV-Cross returned no source and no error."),
                combined
            );
        } finally {
            if (context is not null) {
                cross.ContextDestroy(context);
            }
        }
    }

    /// <summary>The options that make the output a GLSL ES program rather than a Vulkan one.</summary>
    /// <remarks>
    ///     Every one of these is load-bearing and was chosen against <c>glslangValidator</c>, not
    ///     from the header's comments.
    /// </remarks>
    static void Configure(Cross cross, Context* context, Compiler* compiler, GlslDialect dialect) {
        CompilerOptions* options = null;
        Check(cross, context, cross.CompilerCreateCompilerOptions(compiler, &options), "read the options of");

        cross.CompilerOptionsSetUint(options, CompilerOption.GlslVersion, dialect.Version());
        cross.CompilerOptionsSetBool(options, CompilerOption.GlslES, 1);

        // GL_ARB_shading_language_420pack is what lets desktop GLSL write `layout(binding = …)`
        // before 4.2. On ES it does not exist at any version, and SPIRV-Cross emits the qualifier
        // anyway if this is left on — which compiles on 310 es and is a syntax error on 300 es.
        cross.CompilerOptionsSetBool(options, CompilerOption.GlslEnable420PackExtension, 0);

        // The engine links a vertex and a fragment shader into one program (GlProgramCache), so the
        // separable form — which redeclares gl_PerVertex and adds `layout(location=)` to every
        // varying — buys nothing and costs ES 3.0 compatibility.
        cross.CompilerOptionsSetBool(options, CompilerOption.GlslSeparateShaderObjects, 0);

        // ⚠ highp rather than SPIRV-Cross's mediump default, and this is a correctness choice
        // rather than a quality one. `precision mediump float` is a 10-bit mantissa on a phone; the
        // renderer works in cd/m² (docs/plan/06), where a luminance of 1e4 rounded to mediump is
        // visibly banded, and a reversed-Z depth reconstruction in mediump is worthless. ES 3.0
        // guarantees highp in the fragment stage, so this is not a capability gamble.
        cross.CompilerOptionsSetBool(options, CompilerOption.GlslESDefaultFloatPrecisionHighp, 1);
        cross.CompilerOptionsSetBool(options, CompilerOption.GlslESDefaultIntPrecisionHighp, 1);

        Check(cross, context, cross.CompilerInstallCompilerOptions(compiler, options), "install options on");
    }

    /// <summary>
    ///     Gives every combined object the name of the texture it came from.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Without this the uniform is called <c>_112</c>.</b> SPIRV-Cross creates the combined
    ///     id with no name at all and falls back to the id number, and every GL profile the engine
    ///     targets below 3.1 binds its samplers <em>by name</em> after the link
    ///     (<c>glGetUniformLocation</c>) because it has no <c>layout(binding = …)</c> to read. So an
    ///     unnamed pair is a texture nothing can bind, on exactly the profiles that need this
    ///     translation most — and it fails silently, as unit zero.
    ///     <para>
    ///         The texture's own name is used where a texture has one pair, which is the ordinary
    ///         case and keeps the GLSL identifier equal to the name Raven's reflection reports.
    ///         Where a texture is read through two samplers there are two objects and one of them
    ///         has to differ, so both take <c>image_sampler</c> — deterministically, so a rebuild
    ///         does not rename them.
    ///     </para>
    /// </remarks>
    static List<CombinedSampler> NameCombinedSamplers(Cross cross, Context* context, Compiler* compiler) {
        CombinedImageSampler* pairs = null;
        nuint count = 0;

        Check(
            cross,
            context,
            cross.CompilerGetCombinedImageSamplers(compiler, &pairs, &count),
            "list the combined samplers of"
        );

        List<(uint Id, string Image, string Sampler)> raw = [];
        Dictionary<string, int> imageUses = [];

        for (nuint index = 0; index < count; index++) {
            var pair = pairs[index];
            var image = NameOf(cross, compiler, pair.ImageId, "texture", pair.ImageId);
            var sampler = pair.SamplerId == 0 ? "" : NameOf(cross, compiler, pair.SamplerId, "sampler", pair.SamplerId);

            raw.Add((pair.CombinedId, image, sampler));
            imageUses[image] = imageUses.GetValueOrDefault(image) + 1;
        }

        List<CombinedSampler> named = [];

        foreach (var (id, image, sampler) in raw) {
            var name = imageUses[image] == 1 || sampler.Length == 0 ? image : $"{image}_{sampler}";

            cross.CompilerSetName(compiler, id, name);
            named.Add(new(name, image, sampler));
        }

        return named;
    }

    /// <summary>A declaration's name, or a stable stand-in when the module stripped it.</summary>
    static string NameOf(Cross cross, Compiler* compiler, uint id, string kind, uint fallbackId) {
        var name = Marshal.PtrToStringUTF8((nint)cross.CompilerGetName(compiler, id));

        return string.IsNullOrEmpty(name)
            ? $"vixen_{kind}{fallbackId.ToString(CultureInfo.InvariantCulture)}"
            : name;
    }

    /// <summary>Turns a non-success result into SPIRV-Cross's own message.</summary>
    /// <remarks>
    ///     ⚠ The error string belongs to the <em>context</em> and not to the call, and it is
    ///     overwritten by the next failure — so it is read here, immediately, rather than at the
    ///     boundary where it would already have been replaced.
    /// </remarks>
    static void Check(Cross cross, Context* context, Result result, string what) {
        if (result == Result.Success) {
            return;
        }

        var message = Marshal.PtrToStringUTF8((nint)cross.ContextGetLastErrorString(context));

        throw new SpirvCrossException(
            $"SPIRV-Cross could not {what} this module ({result})"
            + (string.IsNullOrWhiteSpace(message) ? ", and said nothing about why." : $": {message}")
        );
    }
}
