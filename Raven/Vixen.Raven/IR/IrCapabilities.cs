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

    /// <summary>64-bit integers (<c>int64</c> and <c>uint64</c>).</summary>
    /// <remarks>
    ///     Optional everywhere: <c>shaderInt64</c> on Vulkan, SM6.6 on D3D12, and absent from
    ///     WebGPU. A host that cannot offer it has to pick a different variant rather than fail the
    ///     pipeline, which is why this is reported per shader.
    /// </remarks>
    public const string Int64 = "Int64";

    /// <summary>
    ///     An atomic read-modify-write on a 64-bit integer.
    /// </summary>
    /// <remarks>
    ///     Separate from <see cref="Int64" /> because the devices are: <c>VK_KHR_shader_atomic_int64</c>
    ///     is its own extension with its own feature bits, and a device may support the type without
    ///     supporting an atomic on it. Reporting only the type would be a pipeline that creates and
    ///     a dispatch that does not run.
    /// </remarks>
    public const string Int64Atomics = "Int64Atomics";

    /// <summary>Sampling a 3D texture.</summary>
    public const string Texture3D = "Texture3D";

    /// <summary>Sampling a cube texture.</summary>
    public const string TextureCube = "TextureCube";

    /// <summary>A geometry stage.</summary>
    public const string Geometry = "Geometry";

    /// <summary>A compute stage.</summary>
    public const string Compute = "Compute";

    /// <summary>
    ///     Writing into a texture — a storage image rather than a sampled one.
    /// </summary>
    /// <remarks>
    ///     Worth gating on even though every desktop and modern mobile GPU has it: the host has to
    ///     create the view with storage usage, and on a tiler the format list that supports it is
    ///     narrower than the one that supports sampling.
    /// </remarks>
    public const string StorageImage = "StorageImage";

    /// <summary>
    ///     Tracing rays inline — an acceleration structure binding, or a <c>Trace</c> on one.
    /// </summary>
    /// <remarks>
    ///     <c>VK_KHR_ray_query</c> on Vulkan, and absent from whole device generations, which is
    ///     exactly why <c>RayQueryField.rvn</c> is a <c>compose</c> implementation rather than the
    ///     only tracer: a host that cannot offer this picks the clipmap field instead of failing
    ///     the pipeline.
    /// </remarks>
    public const string RayQuery = "RayQuery";
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

        // Workgroup storage is not a binding, so it is not in the list above — and its type is
        // just as capable of being a `double[]` the device has to support.
        foreach (var shared in shader.SharedVariables) {
            CollectType(shared.Variable.Type, required);
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

            foreach (var output in entryPoint.Outputs) {
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

                // The one instruction whose *opcode* asks for something its types do not. A 64-bit
                // atomic needs both `Int64` — which the result type below already gives — and
                // `Int64Atomics`, which nothing about the type could say, because a shader may pack
                // a 64-bit word and never touch it atomically.
                case IrAtomicInstruction { Place.Type: IrScalarType { Is64Bit: true } } atomic:
                    required.Add(IrCapability.Int64Atomics);
                    CollectType(atomic.Result.Type, required);
                    break;

                // The other opcode-not-type requirement, for the same reason with the operands
                // swapped: the *structure* argument's type already says RayQuery when the shader
                // declares the binding itself, but a helper that receives it as a parameter and
                // traces still needs the device feature — and its result is only a float4.
                case IrIntrinsicInstruction { Intrinsic: IrIntrinsic.TraceRayQuery } trace:
                    required.Add(IrCapability.RayQuery);

                    if (trace.Result is { } answer) {
                        CollectType(answer.Type, required);
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

            case IrScalarType { Kind: IrTypeKind.Int64 or IrTypeKind.UInt64 }:
                required.Add(IrCapability.Int64);
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

            case IrStorageImageType image:
                required.Add(IrCapability.StorageImage);

                if (image.Dimension == IrTextureDimension.Texture3D) {
                    required.Add(IrCapability.Texture3D);
                }

                CollectType(image.TexelType, required);
                break;

            case IrAccelerationStructureType:
                required.Add(IrCapability.RayQuery);
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
