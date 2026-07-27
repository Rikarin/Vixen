// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.CodeGen;
using Vixen.Raven.IR;

namespace Vixen.Raven.Lowering;

/// <summary>
///     Drops the linked-in library entities nothing in the compilation reached.
/// </summary>
/// <remarks>
///     <para>
///         A referenced library's whole IR has to be present before any body is lowered — a body
///         may call anything in it, and a struct may hold anything from it — but only what
///         something reached belongs in the output. Referencing <c>Math.rvnlib</c> to use
///         <c>Saturate</c> must not put the rest of it in every shader.
///     </para>
///     <para>
///         This is not the same pass as the backends' reachability walk, which is per entry point
///         and per stage: it decides what one translation unit emits, and it does not touch the
///         module. This one decides what the module <em>contains</em>, so that the IR dump, the
///         verifier and <c>IrCapabilities</c> all describe the shader that was compiled rather
///         than the library it borrowed from. Without it a variant that never reaches a
///         <c>double</c> would require <c>Float64</c> because some unused library function does,
///         which is the exact mistake docs/plan/07 § B set out to avoid.
///     </para>
/// </remarks>
internal static class ImportPruner {
    /// <summary>
    ///     Removes every imported struct and function that is not reachable from the compilation's
    ///     own code.
    /// </summary>
    /// <param name="module">The module being lowered.</param>
    /// <param name="importedStructs">Structs that came from a library.</param>
    /// <param name="importedFunctions">Functions that came from a library.</param>
    public static void Prune(
        IrModule module,
        IReadOnlySet<IrStructType> importedStructs,
        IReadOnlySet<IrFunction> importedFunctions
    ) {
        if (importedStructs.Count == 0 && importedFunctions.Count == 0) {
            return;
        }

        var keptFunctions = ReachableFunctions(module, importedFunctions);
        var keptStructs = UsedStructs(module, keptFunctions, importedStructs);

        module.Prune(
            structType => !importedStructs.Contains(structType) || keptStructs.Contains(structType),
            function => !importedFunctions.Contains(function) || keptFunctions.Contains(function)
        );
    }

    /// <summary>
    ///     Every function reachable from the compilation's own code, imports included.
    /// </summary>
    /// <remarks>
    ///     The roots are the functions the compilation declared — a library function is never a
    ///     root, however it was written, because nothing in a library is an entry point.
    /// </remarks>
    static HashSet<IrFunction> ReachableFunctions(IrModule module, IReadOnlySet<IrFunction> imported) {
        HashSet<IrFunction> reached = [];
        Queue<IrFunction> pending = new();

        foreach (var function in module.AllFunctions.Where(f => !imported.Contains(f))) {
            reached.Add(function);
            pending.Enqueue(function);
        }

        // A binding's declared default is a statement of the shader rather than of a function, and
        // it can call one.
        foreach (var shader in module.Shaders) {
            foreach (var called in CallGraph.Calls(shader.Initializer)) {
                pending.Enqueue(called);
            }
        }

        while (pending.Count > 0) {
            var function = pending.Dequeue();
            reached.Add(function);

            foreach (var called in CallGraph.Calls(function.Body)) {
                if (!reached.Contains(called)) {
                    pending.Enqueue(called);
                }
            }
        }

        return reached;
    }

    /// <summary>
    ///     Every struct mentioned by a kept function's signature, storage or values, by a struct the
    ///     module keeps, or by a shader's interface.
    /// </summary>
    static HashSet<IrStructType> UsedStructs(
        IrModule module,
        HashSet<IrFunction> keptFunctions,
        IReadOnlySet<IrStructType> imported
    ) {
        HashSet<IrStructType> used = [];

        void Note(IrType type) {
            switch (type) {
                case IrStructType structType:
                    // A struct's own fields drag their types in with it.
                    if (used.Add(structType)) {
                        foreach (var field in structType.Fields) {
                            Note(field.Type);
                        }
                    }

                    break;

                case IrArrayType array:
                    Note(array.Element);
                    break;

                case IrTextureType texture:
                    Note(texture.SampledType);
                    break;

                case IrStorageImageType image:
                    Note(image.TexelType);
                    break;
            }
        }

        foreach (var structType in module.Structs.Where(s => !imported.Contains(s))) {
            Note(structType);
        }

        foreach (var function in keptFunctions) {
            Note(function.ReturnType);

            foreach (var variable in function.Parameters.Concat(function.Locals)) {
                Note(variable.Type);
            }

            // A struct can also arrive as the type of a value — the result of a call to a function
            // that returns one — without ever being named by a declaration in this function.
            foreach (var value in Values(function.Body)) {
                Note(value.Type);
            }
        }

        foreach (var shader in module.Shaders) {
            foreach (var binding in shader.Bindings) {
                Note(binding.Type);
            }

            foreach (var entryPoint in shader.EntryPoints) {
                foreach (var input in entryPoint.Inputs) {
                    Note(input.Type);
                }

                foreach (var output in entryPoint.Outputs) {
                    Note(output.Type);
                }
            }
        }

        return used;
    }

    /// <summary>Every value a statement defines or reads, nested statements included.</summary>
    static IEnumerable<IrValue> Values(IrStatement statement) {
        switch (statement) {
            case IrBlock block: {
                foreach (var value in block.Statements.SelectMany(Values)) {
                    yield return value;
                }

                break;
            }

            case IrInstruction instruction: {
                if (instruction.Result is { } result) {
                    yield return result;
                }

                foreach (var operand in instruction.Operands) {
                    yield return operand;
                }

                break;
            }

            case IrIfStatement conditional: {
                yield return conditional.Condition;

                foreach (var value in Values(conditional.Then)) {
                    yield return value;
                }

                if (conditional.Else is { } otherwise) {
                    foreach (var value in Values(otherwise)) {
                        yield return value;
                    }
                }

                break;
            }

            case IrLoopStatement loop: {
                IrBlock?[] parts = [loop.Condition, loop.Body, loop.Continue];

                foreach (var value in parts.Where(p => p is not null).SelectMany(p => Values(p!))) {
                    yield return value;
                }

                yield return loop.ConditionValue;
                break;
            }

            case IrReturnStatement { Value: { } returned }:
                yield return returned;
                break;
        }
    }
}
