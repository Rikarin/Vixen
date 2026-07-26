// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Raven.IR;
using Vixen.Raven.Reflection;

namespace Vixen.Raven.CodeGen.Spirv;

/// <summary>
///     Maps Raven IR types and constants onto SPIR-V type and constant ids, interning
///     as it goes. SPIR-V requires type uniqueness — two <c>OpTypeFloat 32</c>
///     instructions are invalid, not merely wasteful — so every lookup goes through
///     here.
/// </summary>
/// <remarks>
///     Types come in three flavours, not two. A type reached through a descriptor is
///     <em>explicitly laid out</em>: its structs carry <c>Offset</c> on every member and its arrays
///     an <c>ArrayStride</c>. The same struct used as a local carries none of that, and Vulkan will
///     not accept one type serving both roles. The third flavour is the reason this is a
///     <see cref="LayoutRule" /> and not a flag: a uniform block packs std140 and a storage buffer
///     packs std430, so the <em>same</em> Raven struct has two different sets of offsets. Each is
///     interned separately and the rule propagates down through members.
/// </remarks>
sealed class SpirvTypes {
    readonly Dictionary<(IrStructType, LayoutRule?), uint> structs = [];
    readonly SpirvModule module;
    readonly Action<IrType, string> unsupported;

    internal uint Void { get; }
    internal uint Bool { get; }
    internal uint Int { get; }
    internal uint UInt { get; }
    internal uint Float { get; }

    internal SpirvTypes(SpirvModule module, Action<IrType, string> unsupported) {
        this.module = module;
        this.unsupported = unsupported;

        Void = module.Intern("void", () => module.AddDeclaration(SpirvOp.TypeVoid, null));
        Bool = module.Intern("bool", () => module.AddDeclaration(SpirvOp.TypeBool, null));
        Int = Integer(true);
        UInt = Integer(false);
        Float = module.Intern("f32", () => module.AddDeclaration(SpirvOp.TypeFloat, null, SpirvOperand.Literal(32)));
    }

    // --- Building blocks ---------------------------------------------------

    uint Scalar(IrScalarType scalar) =>
        scalar.Kind switch {
            IrTypeKind.Void => Void,
            IrTypeKind.Bool => Bool,
            IrTypeKind.Int => Int,
            IrTypeKind.UInt => UInt,
            IrTypeKind.Float => Float,
            IrTypeKind.Double => Double(),
            _ => Float
        };

    uint Double() {
        module.AddCapability(SpirvCapability.Float64);
        return module.Intern("f64", () => module.AddDeclaration(SpirvOp.TypeFloat, null, SpirvOperand.Literal(64)));
    }

    uint Integer(bool signed) =>
        module.Intern(
            signed ? "i32" : "u32",
            () => module.AddDeclaration(
                SpirvOp.TypeInt,
                null,
                SpirvOperand.Literal(32),
                SpirvOperand.Literal(signed ? 1 : 0)
            )
        );

    uint Array(IrArrayType array, int length, LayoutRule? layout) {
        // The length is an operand id, not a literal: SPIR-V spells array extents
        // as constants so they can be specialization constants.
        var id = module.AddDeclaration(
            SpirvOp.TypeArray,
            null,
            SpirvOperand.Id(Type(array.Element, layout)),
            SpirvOperand.Id(ConstantUInt((uint)length))
        );

        if (layout is { } rule) {
            module.Decorate(
                id,
                SpirvDecoration.ArrayStride,
                SpirvOperand.Literal(ShaderLayout.ArrayStride(array, rule))
            );
        }

        return id;
    }

    /// <summary>
    ///     A runtime-sized array — a storage buffer's contents, and the only array with no extent.
    /// </summary>
    internal uint RuntimeArray(IrArrayType array, LayoutRule rule) {
        ArgumentNullException.ThrowIfNull(array);

        var id = module.AddDeclaration(
            SpirvOp.TypeRuntimeArray,
            null,
            SpirvOperand.Id(Type(array.Element, rule))
        );

        module.Decorate(id, SpirvDecoration.ArrayStride, SpirvOperand.Literal(ShaderLayout.ArrayStride(array, rule)));
        return id;
    }

    uint Struct(IrStructType structType, LayoutRule? layout) {
        if (structs.TryGetValue((structType, layout), out var existing)) {
            return existing;
        }

        var members = structType.Fields.Select(f => f.Type).ToArray();
        var id = module.AddDeclaration(
            SpirvOp.TypeStruct,
            null,
            [.. members.Select(m => SpirvOperand.Id(Type(m, layout)))]
        );

        structs[(structType, layout)] = id;
        module.AddName(id, structType.Name);

        for (var i = 0; i < structType.Fields.Count; i++) {
            module.AddMemberName(id, i, structType.Fields[i].Name);
        }

        if (layout is { } rule) {
            DecorateLayout(id, members, rule);
        }

        return id;
    }

    uint Image(IrTextureType texture) {
        var dimension = texture.Dimension switch {
            IrTextureDimension.Texture3D => SpirvDim.Dim3D,
            IrTextureDimension.Cube => SpirvDim.Cube,
            _ => SpirvDim.Dim2D
        };

        return module.Intern(
            $"image {dimension} {texture.SampledType.ComponentType.Name}",
            () => module.AddDeclaration(
                SpirvOp.TypeImage,
                null,
                // Sampled type is the component, not the vector a sample returns.
                SpirvOperand.Id(Scalar(texture.SampledType.ComponentType)),
                SpirvOperand.Enumerant(dimension),
                // Not a depth image, not arrayed, not multisampled, and known to be
                // used with a sampler (1) rather than as storage (2).
                SpirvOperand.Literal(0),
                SpirvOperand.Literal(0),
                SpirvOperand.Literal(0),
                SpirvOperand.Literal(1),
                SpirvOperand.Enumerant(SpirvImageFormat.Unknown)
            )
        );
    }

    // --- Types -------------------------------------------------------------

    /// <param name="layout">
    ///     The packing rule to decorate with, or null for the plain type. A rule rather than a flag
    ///     because std140 and std430 are <em>different layouts of the same Raven type</em>: a
    ///     <c>float[4]</c> member has a 16-byte stride in a uniform block and a 4-byte one in a
    ///     storage buffer. One "laid out" variant would have silently given a storage buffer the
    ///     uniform block's offsets.
    /// </param>
    internal uint Type(IrType type, LayoutRule? layout = null) {
        switch (type) {
            case IrScalarType scalar:
                return Scalar(scalar);

            case IrVectorType vector:
                return module.Intern(
                    $"vec {vector.Component.Name} {vector.Size}",
                    () => module.AddDeclaration(
                        SpirvOp.TypeVector,
                        null,
                        SpirvOperand.Id(Scalar(vector.Component)),
                        SpirvOperand.Literal(vector.Size)
                    )
                );

            case IrMatrixType matrix:
                // A SPIR-V matrix is a repeated *column*, so Raven's R rows by C
                // columns becomes C columns each holding R components. That is the
                // same flip the GLSL backend makes when it writes `matCxR`, and it
                // is what keeps `m * v` meaning the same thing.
                module.AddCapability(SpirvCapability.Matrix);
                return module.Intern(
                    $"mat {matrix.Component.Name} {matrix.Rows} {matrix.Columns}",
                    () => module.AddDeclaration(
                        SpirvOp.TypeMatrix,
                        null,
                        SpirvOperand.Id(Type(new IrVectorType(matrix.Component, matrix.Rows))),
                        SpirvOperand.Literal(matrix.Columns)
                    )
                );

            case IrArrayType { Length: { } length } array:
                return module.Intern(
                    $"array{(layout is { } rule ? "." + rule : "")} {array.Element.Name} {length}",
                    () => Array(array, length, layout)
                );

            case IrArrayType unsized:
                // A runtime-sized array is only legal as the last member of a
                // storage buffer, which the IR has no way to express yet.
                unsupported(unsized, "an unsized array");
                return Float;

            case IrStructType structType:
                return Struct(structType, layout);

            case IrTextureType texture:
                return Image(texture);

            case IrSamplerType:
                return module.Intern("sampler", () => module.AddDeclaration(SpirvOp.TypeSampler, null));

            default:
                unsupported(type, type.Name);
                return Float;
        }
    }

    internal uint Pointer(SpirvStorageClass storage, uint pointee) =>
        module.Intern(
            $"ptr {storage} {pointee}",
            () => module.AddDeclaration(
                SpirvOp.TypePointer,
                null,
                SpirvOperand.Enumerant(storage),
                SpirvOperand.Id(pointee)
            )
        );

    internal uint Function(uint returnType, IReadOnlyList<uint> parameters) =>
        module.Intern(
            $"fn {returnType} ({string.Join(",", parameters)})",
            () => module.AddDeclaration(
                SpirvOp.TypeFunction,
                null,
                [SpirvOperand.Id(returnType), .. parameters.Select(SpirvOperand.Id)]
            )
        );

    /// <summary>An image paired with a sampler, which is what a sample instruction takes.</summary>
    internal uint SampledImage(uint image) =>
        module.Intern(
            $"sampled-image {image}",
            () => module.AddDeclaration(SpirvOp.TypeSampledImage, null, SpirvOperand.Id(image))
        );

    // --- Constants ---------------------------------------------------------

    internal uint ConstantBool(bool value) =>
        module.Intern(
            $"const bool {value}",
            () => module.AddDeclaration(value ? SpirvOp.ConstantTrue : SpirvOp.ConstantFalse, Bool)
        );

    internal uint ConstantInt(int value) =>
        module.Intern(
            $"const i32 {value}",
            () => module.AddDeclaration(SpirvOp.Constant, Int, SpirvOperand.Literal(value))
        );

    internal uint ConstantUInt(uint value) =>
        module.Intern(
            $"const u32 {value}",
            () => module.AddDeclaration(SpirvOp.Constant, UInt, SpirvOperand.Literal(value))
        );

    internal uint ConstantFloat(float value) =>
        module.Intern(
            // Round-trip formatting, so -0.0 and 0.0 stay distinct constants.
            $"const f32 {value.ToString("R", CultureInfo.InvariantCulture)}",
            () => module.AddDeclaration(SpirvOp.Constant, Float, SpirvOperand.FloatLiteral(value))
        );

    internal uint ConstantDouble(double value) =>
        module.Intern(
            $"const f64 {value.ToString("R", CultureInfo.InvariantCulture)}",
            () => module.AddDeclaration(
                SpirvOp.Constant,
                Scalar(IrScalarType.Double),
                SpirvOperand.DoubleLiteral(value)
            )
        );

    /// <summary>The all-zero value of a type, for a constant with no explicit value.</summary>
    internal uint ConstantNull(IrType type) =>
        type switch {
            { Kind: IrTypeKind.Bool } => ConstantBool(false),
            { Kind: IrTypeKind.Int } => ConstantInt(0),
            { Kind: IrTypeKind.UInt } => ConstantUInt(0),
            { Kind: IrTypeKind.Float } => ConstantFloat(0),
            { Kind: IrTypeKind.Double } => ConstantDouble(0),
            _ => module.Intern(
                $"null {Type(type)}",
                () => module.AddDeclaration(SpirvOp.ConstantNull, Type(type))
            )
        };

    /// <summary>A boxed IR constant as a SPIR-V constant of the given type.</summary>
    internal uint Constant(object? value, IrType type) =>
        value switch {
            null => ConstantNull(type),
            bool flag => ConstantBool(flag),
            int number => ConstantInt(number),
            uint number => ConstantUInt(number),
            float number => ConstantFloat(number),
            double number => ConstantDouble(number),
            _ => ConstantNull(type)
        };

    /// <summary>Writes the offsets and matrix strides an explicitly laid out struct needs.</summary>
    internal void DecorateLayout(uint structId, IReadOnlyList<IrType> members, LayoutRule rule) {
        var (offsets, _) = ShaderLayout.Members(members, rule);

        for (var i = 0; i < members.Count; i++) {
            module.DecorateMember(structId, i, SpirvDecoration.Offset, SpirvOperand.Literal(offsets[i]));

            // A member that *is* a matrix and a member that is an array of matrices are both
            // "a matrix member" as far as the layout rules go — `Library/Pipeline/ShadowCaster.rvn`
            // found that the hard way, with its `mat4[256]` bone palette. Looking through the
            // arrays rather than only at the member's own type is what makes the two agree.
            if (MatrixIn(members[i]) is not { } matrix) {
                continue;
            }

            // The IR reads a matrix as rows; the SPIR-V type holds columns. ColMajor names how
            // the columns sit in memory, and the stride is the gap between them.
            module.DecorateMember(structId, i, SpirvDecoration.ColMajor);
            module.DecorateMember(
                structId,
                i,
                SpirvDecoration.MatrixStride,
                SpirvOperand.Literal(ShaderLayout.MatrixStride(matrix))
            );
        }
    }

    /// <summary>The matrix a block member is, or is an array of, however deeply nested.</summary>
    static IrMatrixType? MatrixIn(IrType type) =>
        type switch {
            IrMatrixType matrix => matrix,
            IrArrayType array => MatrixIn(array.Element),
            _ => null
        };
}
