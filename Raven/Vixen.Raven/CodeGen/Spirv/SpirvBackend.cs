// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Symbols;

namespace Vixen.Raven.CodeGen.Spirv;

/// <summary>
///     Generates SPIR-V from the Raven IR — one module per entry point, because a
///     Vulkan pipeline stage is created from one module with one <c>main</c>.
/// </summary>
/// <remarks>
///     Each unit carries both forms: the bytes a driver consumes, and an assembly
///     listing rendered from the very same instructions, so what a golden file shows
///     is what the binary holds.
/// </remarks>
internal sealed class SpirvBackend(SpirvOptions? options = null) : ITargetBackend {
    readonly SpirvOptions options = options ?? new SpirvOptions();

    public string Name => "spirv";

    public string FileExtension => ".spv";

    public IReadOnlyList<GeneratedSource> Generate(IrModule irModule, DiagnosticBag diagnostics) {
        List<GeneratedSource> generated = [];

        foreach (var shader in irModule.Shaders) {
            if (shader.Initializer.Statements.Count > 0) {
                // A descriptor-backed variable cannot carry an initializer, so a
                // binding's declared default stays host-side data. Said once per
                // shader, not once per stage.
                diagnostics.Add(
                    BackendDiagnostics.Dropped,
                    Location.None,
                    $"Binding defaults in shader '{shader.Name}' stay host-side data: a SPIR-V uniform "
                    + "cannot carry an initializer"
                );
            }

            foreach (var entryPoint in shader.EntryPoints) {
                var built = new SpirvEmitter(irModule, shader, entryPoint, options, diagnostics).Emit();

                generated.Add(
                    new(
                        $"{shader.Name}.{ShaderStageNames.Suffix(entryPoint.Stage)}",
                        entryPoint.Stage,
                        built.ToAssembly(),
                        built.ToBytes()
                    )
                );
            }
        }

        return generated;
    }
}
