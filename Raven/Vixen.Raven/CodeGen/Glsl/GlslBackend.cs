// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Symbols;
using Vixen.Core.Syntax.Diagnostics;

namespace Vixen.Raven.CodeGen.Glsl;

/// <summary>
///     Generates GLSL from the Raven IR — one translation unit per entry point,
///     because a GLSL program is compiled a stage at a time.
/// </summary>
public sealed class GlslBackend(GlslOptions? options = null) : ITargetBackend {
    readonly GlslOptions options = options ?? new GlslOptions();

    public string Name => "glsl";

    public string FileExtension => ".glsl";

    public IReadOnlyList<GeneratedSource> Generate(IrModule irModule, DiagnosticBag diagnostics) {
        List<GeneratedSource> generated = [];

        foreach (var shader in irModule.Shaders) {
            // A property of the shader, not of any one stage, so it is said once
            // however many translation units come out of it.
            foreach (var sampler in shader.Bindings.Where(b => b.Kind == IrBindingKind.Sampler)) {
                diagnostics.Add(
                    BackendDiagnostics.Dropped,
                    Location.None,
                    $"GLSL has no standalone sampler object, so binding '{sampler.Name}' is folded into the "
                    + "textures it is used with"
                );
            }

            foreach (var entryPoint in shader.EntryPoints) {
                if (entryPoint.Stage == ShaderStage.Compute) {
                    // A compute stage needs a workgroup size, which nothing in the
                    // language declares yet.
                    diagnostics.Add(BackendDiagnostics.NotImplemented, Location.None, "The compute stage", "GLSL");
                    continue;
                }

                var emitter = new GlslEmitter(irModule, shader, entryPoint, options, diagnostics);
                generated.Add(new($"{shader.Name}.{StageSuffix(entryPoint.Stage)}", entryPoint.Stage, emitter.Emit()));
            }
        }

        return generated;
    }

    /// <summary>The conventional file-name suffix for a stage.</summary>
    public static string StageSuffix(ShaderStage stage) => ShaderStageNames.Suffix(stage);
}
