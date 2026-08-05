// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Raven.IR;
using Vixen.Raven.Reflection;
using Vixen.Raven.Symbols;

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
            IrTypeKind.Int64 => Long(true),
            IrTypeKind.UInt64 => Long(false),
            _ => Float
        };

    uint Double() {
        module.AddCapability(SpirvCapability.Float64);
        return module.Intern("f64", () => module.AddDeclaration(SpirvOp.TypeFloat, null, SpirvOperand.Literal(64)));
    }

    /// <summary>
    ///     A 64-bit integer, and the capability that makes one legal.
    /// </summary>
    /// <remarks>
    ///     Declared here rather than by whoever emits the atomic, because the type is what needs it:
    ///     a module holding an <c>OpTypeInt 64</c> requires <c>Int64</c> whether or not anything
    ///     atomic touches it, and <c>spirv-val</c> says so.
    /// </remarks>
    uint Long(bool signed) {
        module.AddCapability(SpirvCapability.Int64);

        return module.Intern(
            signed ? "i64" : "u64",
            () => module.AddDeclaration(
                SpirvOp.TypeInt,
                null,
                SpirvOperand.Literal(64),
                SpirvOperand.Literal(signed ? 1 : 0)
            )
        );
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
    ///     A runtime-sized array — a storage buffer's contents, and one of the two arrays with no
    ///     extent.
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

    /// <summary>
    ///     A descriptor array — the other array with no extent, and the one with no layout either.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The same <c>OpTypeRuntimeArray</c> and deliberately not the same method. A storage
    ///         buffer's runtime array is <em>memory</em>: it carries an <c>ArrayStride</c>, the host
    ///         computes a byte offset from it, and the whole thing lives inside a block. A descriptor
    ///         array is a range of the descriptor set — there is no stride, nothing is packed, and
    ///         decorating one with a stride is a validation error rather than a harmless extra.
    ///     </para>
    ///     <para>
    ///         <c>RuntimeDescriptorArray</c> is the capability that makes the variable legal at all,
    ///         and <c>ShaderNonUniform</c> is what lets the index vary across a subgroup — which is
    ///         the entire reason to have one, since a merged draw is fragments of different materials
    ///         in one dispatch. Both come from <c>SPV_EXT_descriptor_indexing</c> below SPIR-V 1.5.
    ///     </para>
    /// </remarks>
    internal uint DescriptorArray(IrArrayType array) {
        ArgumentNullException.ThrowIfNull(array);

        module.AddExtension(SpirvExtensions.DescriptorIndexing);
        module.AddCapability(SpirvCapability.RuntimeDescriptorArray);
        module.AddCapability(SpirvCapability.ShaderNonUniform);

        // ⚠ And the one for what the array holds. `ShaderNonUniform` allows the decoration to exist;
        // this allows a sampled image array to be indexed by it, and Vulkan requires it of any module
        // that does. Nothing checks: spirv-val and the layers both accept a module without it. See
        // the remarks on the capability, including what it did *not* fix.
        //
        // Unconditional because `IsDescriptorArray` admits exactly one element type. When it admits
        // storage images or buffers, this needs the capability that goes with each of them.
        module.AddCapability(SpirvCapability.ShaderSampledImageArrayNonUniformIndexing);

        return module.Intern(
            $"descriptors {array.Element.Name}",
            () => module.AddDeclaration(SpirvOp.TypeRuntimeArray, null, SpirvOperand.Id(Type(array.Element)))
        );
    }

    /// <summary>A runtime array of one record struct, with the stride the host writes.</summary>
    /// <param name="element">The record's type id.</param>
    /// <param name="stride">One record's size in bytes, laid out std430.</param>
    /// <remarks>
    ///     Distinct from <see cref="RuntimeArray" />, which takes an <see cref="IrArrayType" /> and
    ///     computes the stride from its element. A material record has no <see cref="IrArrayType" />
    ///     at all — it is a struct the plan assembled out of a set's uniforms, and the array around
    ///     it exists only because SPIR-V has no bare runtime-array variable.
    /// </remarks>
    internal uint RecordArray(uint element, int stride) {
        var id = module.AddDeclaration(SpirvOp.TypeRuntimeArray, null, SpirvOperand.Id(element));
        module.Decorate(id, SpirvDecoration.ArrayStride, SpirvOperand.Literal(stride));
        return id;
    }

    /// <summary>Whether this is a descriptor array rather than a laid-out one.</summary>
    /// <param name="type">The type to ask about.</param>
    /// <remarks>
    ///     One place, because three of them ask — the type emitter, the variable declaration and the
    ///     access chain that indexes it — and the three have to agree or the module has a runtime
    ///     array nothing decorated, or a decoration on an array that is not one.
    /// </remarks>
    internal static bool IsDescriptorArray(IrType type) =>
        type is IrArrayType { Length: null, Element: IrTextureType };

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

        // ⚠ The layout is part of the name, and it has to be. One Raven struct in both a uniform
        // block and a storage buffer becomes *two* SPIR-V types here — that is what the key of this
        // cache says — and naming them both `PunctualLight` is a collision for anything downstream
        // that keys on the debug name. A translator to a language with one namespace of struct
        // definitions, which is every one of them, then emits a single definition and gives it to
        // both: on Metal, the uniform block's `float3` (sixteen bytes) silently displaced the storage
        // buffer's tight one, so every member after the first `float3` was read four bytes late.
        // Nothing in the SPIR-V was wrong, which is why it took a picture to find.
        module.AddName(id, layout is { } named ? $"{structType.Name}.{named}" : structType.Name);

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

    /// <summary>
    ///     A storage image: <c>Sampled = 2</c> and a stated <c>ImageFormat</c>.
    /// </summary>
    /// <remarks>
    ///     Both differences from <see cref="Image" /> matter. <c>Sampled = 2</c> is what says this
    ///     image is read and written directly rather than through a sampler, and a known format is
    ///     what keeps the module from needing <c>StorageImageReadWithoutFormat</c> — a capability
    ///     not every device offers. The enumerant comes off <see cref="ImageFormats" />, the same
    ///     table the GLSL layout qualifier is spelled from.
    /// </remarks>
    uint StorageImage(IrStorageImageType image) {
        var dimension = image.Dimension == IrTextureDimension.Texture3D ? SpirvDim.Dim3D : SpirvDim.Dim2D;
        var format = ImageFormats.Lookup(image.Format);

        return module.Intern(
            $"storage image {dimension} {image.TexelType.ComponentType.Name} {image.Format}",
            () => module.AddDeclaration(
                SpirvOp.TypeImage,
                null,
                SpirvOperand.Id(Scalar(image.TexelType.ComponentType)),
                SpirvOperand.Enumerant(dimension),
                SpirvOperand.Literal(0),
                SpirvOperand.Literal(0),
                SpirvOperand.Literal(0),
                SpirvOperand.Literal(2),
                SpirvOperand.Literal(format?.SpirvValue ?? 0)
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

            case IrArrayType unsized when IsDescriptorArray(unsized):
                return DescriptorArray(unsized);

            case IrArrayType unsized:
                // A runtime-sized array is legal as the last member of a storage buffer — which
                // EmitStorageBuffer reaches through RuntimeArray rather than through here — and as a
                // descriptor array of textures, which the case above took. Anything else has no
                // extent and no descriptor to be one of.
                unsupported(unsized, "an unsized array");
                return Float;

            case IrStructType structType:
                return Struct(structType, layout);

            case IrTextureType texture:
                return Image(texture);

            case IrStorageImageType image:
                return StorageImage(image);

            case IrSamplerType:
                return module.Intern("sampler", () => module.AddDeclaration(SpirvOp.TypeSampler, null));

            case IrAccelerationStructureType:
                return AccelerationStructure();

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

    /// <summary>
    ///     The acceleration structure type, and the capability and extension that make it legal.
    /// </summary>
    /// <remarks>
    ///     Declared with the type rather than by whoever traces, the same rule <see cref="Long" />
    ///     states: a module holding an <c>OpTypeAccelerationStructureKHR</c> requires
    ///     <c>RayQueryKHR</c> whether or not anything queries it, and <c>spirv-val</c> says so.
    /// </remarks>
    uint AccelerationStructure() {
        module.AddCapability(SpirvCapability.RayQueryKHR);
        module.AddExtension(SpirvExtensions.RayQuery);

        return module.Intern(
            "acceleration-structure",
            () => module.AddDeclaration(SpirvOp.TypeAccelerationStructureKHR, null)
        );
    }

    /// <summary>
    ///     The ray query type — the one opaque type that lives in <c>Function</c> storage.
    /// </summary>
    /// <remarks>
    ///     Never reached from an <see cref="IrType" />: no Raven value has this type, which is the
    ///     language decision <c>BuiltInTypes.AccelerationStructure</c> documents. Only the
    ///     emitter's own synthesized traversal asks for it.
    /// </remarks>
    internal uint RayQuery() {
        module.AddCapability(SpirvCapability.RayQueryKHR);
        module.AddExtension(SpirvExtensions.RayQuery);
        return module.Intern("ray-query", () => module.AddDeclaration(SpirvOp.TypeRayQueryKHR, null));
    }

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

    /// <summary>A 64-bit integer constant, which SPIR-V spells as two literal words.</summary>
    internal uint ConstantLong(long value) =>
        module.Intern(
            $"const i64 {value}",
            () => module.AddDeclaration(
                SpirvOp.Constant,
                Scalar(IrScalarType.Int64),
                SpirvOperand.Literal64(unchecked((ulong)value))
            )
        );

    internal uint ConstantULong(ulong value) =>
        module.Intern(
            $"const u64 {value}",
            () => module.AddDeclaration(
                SpirvOp.Constant,
                Scalar(IrScalarType.UInt64),
                SpirvOperand.Literal64(value)
            )
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
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>void</c> is refused rather than nulled, and it is the one type here that has
    ///         no zero.</b> SPIR-V says so directly — <c>OpConstantNull</c>'s result type must be one
    ///         that <i>has</i> a null value, and <c>OpTypeVoid</c> does not — so the instruction this
    ///         would otherwise intern is invalid however it was reached.
    ///     </para>
    ///     <para>
    ///         It was reached by a call to something that is not a function: a namespace named as a
    ///         callee bound with nothing reported (see <c>Binder.BindInvocation</c>), lowered to a
    ///         void-typed value, and arrived here through <c>Value</c>'s fallback for a value with no
    ///         id. That front-end hole is closed, which is what makes this unreachable from source —
    ///         and is exactly why it is worth a diagnostic rather than a comment: the next lowering
    ///         defect that produces a void-typed operand should be told to somebody, not quietly
    ///         turned into a module a driver rejects. Same treatment an unsized array gets in
    ///         <c>Type</c>: report, then hand back something well-formed so the diagnostic is what
    ///         stops the build.
    ///     </para>
    /// </remarks>
    internal uint ConstantNull(IrType type) =>
        type switch {
            { IsVoid: true } => Reject(type),
            { Kind: IrTypeKind.Bool } => ConstantBool(false),
            { Kind: IrTypeKind.Int } => ConstantInt(0),
            { Kind: IrTypeKind.UInt } => ConstantUInt(0),
            { Kind: IrTypeKind.Int64 } => ConstantLong(0),
            { Kind: IrTypeKind.UInt64 } => ConstantULong(0),
            { Kind: IrTypeKind.Float } => ConstantFloat(0),
            { Kind: IrTypeKind.Double } => ConstantDouble(0),
            _ => module.Intern(
                $"null {Type(type)}",
                () => module.AddDeclaration(SpirvOp.ConstantNull, Type(type))
            )
        };

    /// <summary>Reports a type that has no constant, and substitutes one that is well-formed.</summary>
    uint Reject(IrType type) {
        unsupported(type, $"a constant of type '{type.Name}'");

        return ConstantInt(0);
    }

    /// <summary>A boxed IR constant as a SPIR-V constant of the given type.</summary>
    internal uint Constant(object? value, IrType type) =>
        value switch {
            null => ConstantNull(type),
            bool flag => ConstantBool(flag),
            int number => ConstantInt(number),
            uint number => ConstantUInt(number),
            long number => ConstantLong(number),
            ulong number => ConstantULong(number),
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
