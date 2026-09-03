// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven.CodeGen;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Symbols;

namespace Vixen.Raven.Transpile;

/// <summary>
///     The <c>essl</c> target: SPIR-V, then SPIRV-Cross, then GLSL ES.
/// </summary>
/// <remarks>
///     <para>
///         <b>A backend rather than a separate command</b>, so that everything the CLI already does
///         per target — <c>--shader</c>, <c>--define</c>, <c>--compose</c>, the file naming, the
///         diagnostic formatting — applies without a second copy of it. It implements
///         <see cref="ITargetBackend" /> and consumes an <see cref="IrModule" /> like the other two;
///         what makes it different is that it does not walk the IR at all. It runs the SPIR-V
///         backend and translates its output, which is ADR-012 written down as code: the canonical
///         module is the one thing this compiler emits by hand, and a dialect is a translation of
///         it.
///     </para>
///     <para>
///         ⚠ <b>Registered from <c>Vixen.Raven.Cli</c>, not from <c>TargetBackends</c>' own
///         table.</b> <c>Vixen.Raven</c> is a shipped package and this project drags SPIRV-Cross's
///         native binaries in; a static reference the other way would put them in the restore graph
///         of every consumer of the compiler library. <see cref="Register" /> is the seam, and the
///         CLI's entry point is the one caller.
///     </para>
/// </remarks>
sealed class EsslBackend(GlslDialect dialect = GlslDialect.Essl300) : ITargetBackend {
    /// <summary>The name the CLI accepts for this target.</summary>
    public const string TargetName = "essl";

    /// <inheritdoc />
    public string Name => TargetName;

    /// <inheritdoc />
    /// <remarks>
    ///     <c>.glsl</c> like the Vulkan-GLSL backend, because the file is GLSL and every tool that
    ///     reads one keys off that. Which dialect it is, is the first line of the file.
    /// </remarks>
    public string FileExtension => ".glsl";

    /// <summary>Adds this backend to <see cref="TargetBackends" />, once.</summary>
    /// <remarks>
    ///     Idempotent, so a host that registers on every compile rather than once at startup is not
    ///     a bug. See the type's remarks for why this is a call rather than a table entry.
    /// </remarks>
    public static void Register() => TargetBackends.Register(TargetName, () => new EsslBackend());

    /// <inheritdoc />
    public IReadOnlyList<GeneratedSource> Generate(IrModule irModule, DiagnosticBag diagnostics) {
        ArgumentNullException.ThrowIfNull(irModule);
        ArgumentNullException.ThrowIfNull(diagnostics);

        // The canonical module first. Anything the SPIR-V backend refuses is refused here in its
        // own words rather than translated into a worse message from a later phase — and if it
        // produced nothing, there is nothing to cross-compile and the diagnostics already say why.
        // Through the factory rather than `new SpirvBackend()`: the backend classes are internal
        // to Vixen.Raven, and reaching past that with an InternalsVisibleTo would make this project
        // a second thing that has to be edited when the SPIR-V backend's constructor changes.
        var spirv = TargetBackends.Create("spirv")
            ?? throw new InvalidOperationException("The spirv backend is not registered, so essl has no input.");

        var modules = spirv.Generate(irModule, diagnostics);

        // ⚠ Keyed by the name the SPIR-V backend writes — `<shader>.<stage suffix>` — because a
        // GeneratedSource carries a name and a stage and not the shader it came from. Both sides
        // build that name from `ShaderStageNames.Suffix`, which is why this is a lookup rather than
        // a parse: a shader whose own name contains a dot would defeat splitting on one.
        var shaders = irModule.Shaders
            .SelectMany(
                shader => shader.EntryPoints,
                (shader, entryPoint) => (Key: $"{shader.Name}.{ShaderStageNames.Suffix(entryPoint.Stage)}", Shader: shader)
            )
            .ToDictionary(pair => pair.Key, pair => pair.Shader, StringComparer.Ordinal);

        List<GeneratedSource> generated = [];

        foreach (var module in modules) {
            if (Refuses(module, shaders.GetValueOrDefault(module.Name)) is { } reason) {
                // RVN4001 rather than a transpile failure: the module is valid and SPIRV-Cross would
                // very likely produce *something*. It is the target version that has no such thing.
                diagnostics.Add(BackendDiagnostics.NotExpressible, Location.None, reason, dialect.Describe());

                continue;
            }

            if (module.Binary is not { } binary) {
                // Cannot happen through SpirvBackend, which is a binary target — but a listing
                // reaching here would be silently dropped, and a dropped stage is a program that
                // links against a stale shader.
                diagnostics.Add(
                    BackendDiagnostics.NotImplemented,
                    Location.None,
                    $"A source-level module ('{module.Name}')",
                    TargetName
                );

                continue;
            }

            try {
                var transpiled = SpirvCrossTranspiler.Transpile(binary, dialect);
                generated.Add(new(module.Name, module.Stage, Annotate(transpiled, module)));
            } catch (SpirvCrossException exception) {
                diagnostics.Add(
                    BackendDiagnostics.NotImplemented,
                    Location.None,
                    $"'{module.Name}' ({exception.Message})",
                    TargetName
                );
            }
        }

        return generated;
    }

    /// <summary>
    ///     What this dialect has no way to express, phrased as <c>RVN4001</c>'s <c>{0}</c>, or null
    ///     when it can express all of it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Asked here rather than left to SPIRV-Cross, which does not ask.</b> It will emit
    ///         a compute shader under <c>#version 300 es</c>, and a <c>layout(std430) buffer</c>
    ///         under it too — files that name things the version does not define, which fail at
    ///         <c>glCompileShader</c> on a device rather than at build time on a desk. Refusing here
    ///         turns "the frame is black on Android" into a message naming the shader and the
    ///         feature.
    ///     </para>
    ///     <para>
    ///         <b>The version each thing arrived in</b>, which is the whole content of this method:
    ///         compute, storage buffers and storage images are <c>GL_ES_VERSION_3_1</c>; geometry
    ///         and tessellation are 3.2; and a ray query, a <c>double</c> or a 64-bit integer are in
    ///         no version of GLSL ES at all, so they are refused at every dialect rather than
    ///         deferred to a higher one.
    ///     </para>
    ///     <para>
    ///         ⚠ A unit whose shader could not be found is <em>not</em> refused. The stage check
    ///         still applies, and letting an unrecognised name through to the transpiler is the safe
    ///         direction: the worst case is a file the ES front end rejects and a red test, where
    ///         the alternative is a shader silently dropped from a build.
    ///     </para>
    /// </remarks>
    string? Refuses(GeneratedSource module, IrShader? shader) {
        var minimum = module.Stage switch {
            ShaderStage.Compute => 310u,
            ShaderStage.Geometry => 320u,
            _ => 0u
        };

        if (dialect.Version() < minimum) {
            return $"The {module.Stage.ToString().ToLowerInvariant()} entry point '{module.Name}'";
        }

        if (shader is null) {
            return null;
        }

        foreach (var capability in IrCapabilities.Of(shader)) {
            var required = capability switch {
                IrCapability.Compute => 310u,
                IrCapability.StorageImage => 310u,
                IrCapability.Geometry => 320u,
                IrCapability.RayQuery or IrCapability.Float64 or IrCapability.Int64
                    or IrCapability.Int64Atomics => uint.MaxValue,
                _ => 0u
            };

            if (dialect.Version() < required) {
                return $"'{module.Name}', which needs {capability},";
            }
        }

        // Not a capability, because SPIR-V has no feature bit for it — a storage buffer is just a
        // block. GLSL ES does have a version for it, so it has to be asked separately.
        if (dialect.Version() < 310
            && shader.Bindings.Any(binding => binding.Kind == IrBindingKind.StorageBuffer)) {
            return $"'{module.Name}', which reads a storage buffer,";
        }

        // ⚠ And nor is an array of textures, which was found by the oracle rather than by reading a
        // spec — twice, at two different versions, which is why this is two rules.
        //
        // A SIZED array is legal from 3.2 and not before: GLSL ES until then may index a sampler
        // array only by a *constant* expression — `'variable indexing sampler array' : not
        // supported for this version` — and every array the engine declares is indexed by a
        // material or a draw index. The whole shape goes rather than only the dynamically-indexed
        // ones: nothing in the library indexes one by a literal, and the safe direction is refusing
        // a shader that would have worked over emitting one that fails on a device.
        //
        // An UNSIZED array — `Texture2D[]`, the bindless path — is not legal at ANY version. It
        // lowers to OpTypeRuntimeArray with `NonUniform` on the index, and SPIRV-Cross says so
        // flatly: "GL_EXT_nonuniform_qualifier is only supported in Vulkan GLSL". Bindless is a
        // capability the GL backend already reports as absent at every profile
        // (`GlProfile.Features` sets `HasBindless = false`), so this refusal agrees with the RHI
        // rather than adding a new limit — and a shader that has both a bindless and a
        // non-bindless variant will reach GLES through the other one.
        var textureArrays = shader.Bindings
            .Where(binding => binding.Kind == IrBindingKind.Texture)
            .Select(binding => binding.Variable.Type)
            .OfType<IrArrayType>()
            .ToArray();

        if (textureArrays.Any(array => array.Length is null)) {
            return $"'{module.Name}', which reads a bindless texture array,";
        }

        if (dialect.Version() < 320 && textureArrays.Length > 0) {
            return $"'{module.Name}', which indexes an array of textures,";
        }

        return null;
    }

    /// <summary>
    ///     Writes the combined-sampler list into the file as a comment.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Because the source alone does not say it.</b> A reader — or a host wiring bindings
    ///     by hand — sees <c>uniform highp sampler2D albedo;</c> and cannot tell that it stands for a
    ///     <em>pair</em>, nor which sampler is in it. That matters on the day one texture is read
    ///     through two samplers and two uniforms appear with names nothing in the .rvn contains. The
    ///     structured form is on <see cref="TranspiledShader" /> for a caller that wants to act on
    ///     it; this is for the person who opens the file.
    /// </remarks>
    static string Annotate(TranspiledShader transpiled, GeneratedSource module) {
        if (transpiled.CombinedSamplers.Count == 0) {
            return transpiled.Source;
        }

        var header = string.Join(
            Environment.NewLine,
            transpiled.CombinedSamplers.Select(
                pair => pair.Sampler.Length == 0
                    ? $"//   {pair.Name} = {pair.Image} (no sampler — a fetch)"
                    : $"//   {pair.Name} = {pair.Image} + {pair.Sampler}"
            )
        );

        // After the #version line, which has to be first in the file.
        var newline = transpiled.Source.IndexOf('\n');

        if (newline < 0) {
            return transpiled.Source;
        }

        return transpiled.Source[..(newline + 1)]
            + $"{Environment.NewLine}// Combined by SPIRV-Cross for '{module.Name}', which GL has no "
            + $"way to keep apart:{Environment.NewLine}{header}{Environment.NewLine}"
            + transpiled.Source[(newline + 1)..];
    }
}
