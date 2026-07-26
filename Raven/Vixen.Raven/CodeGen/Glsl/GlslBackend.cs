// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Symbols;

namespace Vixen.Raven.CodeGen.Glsl;

/// <summary>
///     Generates Vulkan GLSL from the Raven IR — one translation unit per entry point,
///     because a GLSL program is compiled a stage at a time.
/// </summary>
public sealed class GlslBackend(GlslOptions? options = null) : ITargetBackend {
    readonly GlslOptions options = options ?? new GlslOptions();

    public string Name => "glsl";

    public string FileExtension => ".glsl";

    public IReadOnlyList<GeneratedSource> Generate(IrModule irModule, DiagnosticBag diagnostics) {
        List<GeneratedSource> generated = [];

        foreach (var shader in irModule.Shaders) {
            foreach (var entryPoint in shader.EntryPoints) {
                var emitter = new GlslEmitter(irModule, shader, entryPoint, options, diagnostics);
                generated.Add(new($"{shader.Name}.{StageSuffix(entryPoint.Stage)}", entryPoint.Stage, emitter.Emit()));
            }
        }

        return generated;
    }

    /// <summary>The conventional file-name suffix for a stage.</summary>
    public static string StageSuffix(ShaderStage stage) => ShaderStageNames.Suffix(stage);
}
