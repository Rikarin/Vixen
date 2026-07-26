// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Symbols;

namespace Vixen.Raven.IR;

/// <summary>
///     Names of the target features a module can require, so the host can gate on them.
/// </summary>
/// <remarks>
///     Strings rather than an enum: the set grows with the backends, and a host matching on
///     names does not have to be recompiled against a new Raven to understand one it has not
///     seen. The names follow SPIR-V/Vulkan spelling where there is one.
/// </remarks>
public static class IrCapability {
    /// <summary>64-bit floating point (<c>double</c> and its vectors and matrices).</summary>
    public const string Float64 = "Float64";

    /// <summary>Sampling a 3D texture.</summary>
    public const string Texture3D = "Texture3D";

    /// <summary>Sampling a cube texture.</summary>
    public const string TextureCube = "TextureCube";

    /// <summary>A geometry stage.</summary>
    public const string Geometry = "Geometry";

    /// <summary>A compute stage.</summary>
    public const string Compute = "Compute";
}

/// <summary>
///     Works out which target features a lowered module actually needs.
/// </summary>
/// <remarks>
///     <para>
///         Collected from the IR rather than from the symbols, and that is the point: by the
///         time lowering is done, a branch behind a false <c>[Permutation]</c> is gone and an
///         unbound <c>compose</c> implementation was never pulled in. A variant that does not
///         reach the <c>double</c> maths does not require <c>Float64</c>, so the host is not
///         asked for a feature this build has no use for.
///     </para>
///     <para>
///         Reported per shader as well as per module, because an engine gates a
///         <em>pipeline</em>: whether one shader needs a feature says nothing about another
///         in the same module.
///     </para>
/// </remarks>
public static class IrCapabilities {
    /// <summary>Everything the whole module requires.</summary>
    public static IReadOnlyCollection<string> Of(IrModule module) {
        ArgumentNullException.ThrowIfNull(module);

        var required = NewSet();
        foreach (var shader in module.Shaders) {
            Collect(shader, required);
        }

        // A free function is only reachable through some shader, so it is covered above.
        // Struct fields are not: a struct can be declared and used only through a uniform.
        foreach (var structType in module.Structs) {
            foreach (var field in structType.Fields) {
                CollectType(field.Type, required);
            }
        }

        return required;
    }

    /// <summary>What one shader requires — the granularity a pipeline is gated at.</summary>
    public static IReadOnlyCollection<string> Of(IrShader shader) {
        ArgumentNullException.ThrowIfNull(shader);

        var required = NewSet();
        Collect(shader, required);
        return required;
    }

    static SortedSet<string> NewSet() => new(StringComparer.Ordinal);

    static void Collect(IrShader shader, SortedSet<string> required) {
        foreach (var binding in shader.Bindings) {
            CollectType(binding.Variable.Type, required);
        }

        foreach (var entryPoint in shader.EntryPoints) {
            switch (entryPoint.Stage) {
                case ShaderStage.Geometry:
                    required.Add(IrCapability.Geometry);
                    break;
                case ShaderStage.Compute:
                    required.Add(IrCapability.Compute);
                    break;
                default:
                    break;
            }

            foreach (var io in entryPoint.Inputs) {
                CollectType(io.Type, required);
            }

            if (entryPoint.Output is { } output) {
                CollectType(output.Type, required);
            }

            // Only what this entry point can actually reach: an unreferenced helper does
            // not make the pipeline require anything.
            foreach (var function in CodeGen.CallGraph.Reachable(entryPoint.Function)) {
                CollectFunction(function, required);
            }
        }
    }

    static void CollectFunction(IrFunction function, SortedSet<string> required) {
        CollectType(function.ReturnType, required);

        foreach (var parameter in function.Parameters) {
            CollectType(parameter.Type, required);
        }

        foreach (var local in function.Locals) {
            CollectType(local.Type, required);
        }

        CollectBlock(function.Body, required);
    }

    static void CollectBlock(IrBlock block, SortedSet<string> required) {
        foreach (var statement in block.Statements) {
            switch (statement) {
                case IrBlock nested:
                    CollectBlock(nested, required);
                    break;

                case IrIfStatement conditional:
                    CollectBlock(conditional.Then, required);
                    if (conditional.Else is { } otherwise) {
                        CollectBlock(otherwise, required);
                    }

                    break;

                case IrLoopStatement loop:
                    CollectBlock(loop.Condition, required);
                    CollectBlock(loop.Body, required);
                    if (loop.Continue is { } step) {
                        CollectBlock(step, required);
                    }

                    break;

                case IrInstruction instruction:
                    // Every value a computation produces is typed, so the result type is
                    // enough — an operand was itself some instruction's result.
                    if (instruction.Result is { } result) {
                        CollectType(result.Type, required);
                    }

                    break;

                default:
                    break;
            }
        }
    }

    static void CollectType(IrType type, SortedSet<string> required) {
        switch (type) {
            case IrScalarType { Kind: IrTypeKind.Double }:
                required.Add(IrCapability.Float64);
                break;

            case IrVectorType vector:
                CollectType(vector.Component, required);
                break;

            case IrMatrixType matrix:
                CollectType(matrix.Component, required);
                break;

            case IrArrayType array:
                CollectType(array.Element, required);
                break;

            case IrTextureType texture:
                switch (texture.Dimension) {
                    case IrTextureDimension.Texture3D:
                        required.Add(IrCapability.Texture3D);
                        break;
                    case IrTextureDimension.Cube:
                        required.Add(IrCapability.TextureCube);
                        break;
                    default:
                        break;
                }

                CollectType(texture.SampledType, required);
                break;

            case IrStructType structType:
                // Not recursed into: a struct's own fields are collected once from
                // IrModule.Structs, and recursing here would loop on a self-referential one.
                break;

            default:
                break;
        }
    }
}
