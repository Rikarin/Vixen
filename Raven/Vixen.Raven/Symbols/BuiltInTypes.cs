// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Syntax;

namespace Vixen.Raven.Symbols;

/// <summary>
///     The compiler's intrinsic type table: scalars, vectors, matrices and the GPU
///     resource types — everything Raven has, because everything Raven has must
///     exist on a GPU. All instances are singletons, so reference equality is type
///     identity for everything in here.
/// </summary>
public static class BuiltInTypes {
    public static readonly PrimitiveTypeSymbol Void = new("void", SpecialType.Void, TypeKind.Void);

    public static readonly PrimitiveTypeSymbol Bool = new("bool", SpecialType.Bool, TypeKind.Scalar);
    public static readonly PrimitiveTypeSymbol Int = new("int", SpecialType.Int, TypeKind.Scalar);
    public static readonly PrimitiveTypeSymbol UInt = new("uint", SpecialType.UInt, TypeKind.Scalar);
    public static readonly PrimitiveTypeSymbol Float = new("float", SpecialType.Float, TypeKind.Scalar);
    public static readonly PrimitiveTypeSymbol Double = new("double", SpecialType.Double, TypeKind.Scalar);

    /// <summary>
    ///     The 64-bit integers.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Spelled <c>int64</c> and <c>uint64</c> — names rather than keywords, which is why
    ///         they cost the lexer, the parser and the grammar oracle nothing: the scope holds every
    ///         intrinsic type, so <c>Texture2D</c> already resolves this way and so do these.
    ///     </para>
    ///     <para>
    ///         Optional on every target: <c>VK_KHR_shader_atomic_int64</c> on Vulkan, SM6.6 on
    ///         D3D12, and absent from WebGPU entirely. So a shader that uses one says so through
    ///         <c>IrCapability.Int64</c> and the host gates the pipeline on it, which is the whole
    ///         reason capabilities are reported per shader.
    ///     </para>
    /// </remarks>
    public static readonly PrimitiveTypeSymbol Int64 = new("int64", SpecialType.Int64, TypeKind.Scalar);

    public static readonly PrimitiveTypeSymbol UInt64 = new("uint64", SpecialType.UInt64, TypeKind.Scalar);

    public static readonly PrimitiveTypeSymbol Bool2 = Vec("bool2", SpecialType.Bool2, SpecialType.Bool, 2);
    public static readonly PrimitiveTypeSymbol Bool3 = Vec("bool3", SpecialType.Bool3, SpecialType.Bool, 3);
    public static readonly PrimitiveTypeSymbol Bool4 = Vec("bool4", SpecialType.Bool4, SpecialType.Bool, 4);
    public static readonly PrimitiveTypeSymbol Int2 = Vec("int2", SpecialType.Int2, SpecialType.Int, 2);
    public static readonly PrimitiveTypeSymbol Int3 = Vec("int3", SpecialType.Int3, SpecialType.Int, 3);
    public static readonly PrimitiveTypeSymbol Int4 = Vec("int4", SpecialType.Int4, SpecialType.Int, 4);
    public static readonly PrimitiveTypeSymbol UInt2 = Vec("uint2", SpecialType.UInt2, SpecialType.UInt, 2);
    public static readonly PrimitiveTypeSymbol UInt3 = Vec("uint3", SpecialType.UInt3, SpecialType.UInt, 3);
    public static readonly PrimitiveTypeSymbol UInt4 = Vec("uint4", SpecialType.UInt4, SpecialType.UInt, 4);
    public static readonly PrimitiveTypeSymbol Float2 = Vec("float2", SpecialType.Float2, SpecialType.Float, 2);
    public static readonly PrimitiveTypeSymbol Float3 = Vec("float3", SpecialType.Float3, SpecialType.Float, 3);
    public static readonly PrimitiveTypeSymbol Float4 = Vec("float4", SpecialType.Float4, SpecialType.Float, 4);
    public static readonly PrimitiveTypeSymbol Double2 = Vec("double2", SpecialType.Double2, SpecialType.Double, 2);
    public static readonly PrimitiveTypeSymbol Double3 = Vec("double3", SpecialType.Double3, SpecialType.Double, 3);
    public static readonly PrimitiveTypeSymbol Double4 = Vec("double4", SpecialType.Double4, SpecialType.Double, 4);

    public static readonly PrimitiveTypeSymbol Mat2 = Mat("mat2", SpecialType.Mat2, 2, 2);
    public static readonly PrimitiveTypeSymbol Mat2x3 = Mat("mat2x3", SpecialType.Mat2x3, 2, 3);
    public static readonly PrimitiveTypeSymbol Mat2x4 = Mat("mat2x4", SpecialType.Mat2x4, 2, 4);
    public static readonly PrimitiveTypeSymbol Mat3 = Mat("mat3", SpecialType.Mat3, 3, 3);
    public static readonly PrimitiveTypeSymbol Mat3x2 = Mat("mat3x2", SpecialType.Mat3x2, 3, 2);
    public static readonly PrimitiveTypeSymbol Mat3x4 = Mat("mat3x4", SpecialType.Mat3x4, 3, 4);
    public static readonly PrimitiveTypeSymbol Mat4 = Mat("mat4", SpecialType.Mat4, 4, 4);
    public static readonly PrimitiveTypeSymbol Mat4x2 = Mat("mat4x2", SpecialType.Mat4x2, 4, 2);
    public static readonly PrimitiveTypeSymbol Mat4x3 = Mat("mat4x3", SpecialType.Mat4x3, 4, 3);

    public static readonly BuiltInNamedTypeSymbol Sampler =
        new("Sampler", SpecialType.Sampler, TypeKind.Resource, ResourceKind.Sampler);

    public static readonly BuiltInNamedTypeSymbol Texture2D =
        new("Texture2D", SpecialType.Texture2D, TypeKind.Resource, ResourceKind.Texture);

    public static readonly BuiltInNamedTypeSymbol Texture3D =
        new("Texture3D", SpecialType.Texture3D, TypeKind.Resource, ResourceKind.Texture);

    public static readonly BuiltInNamedTypeSymbol TextureCube =
        new("TextureCube", SpecialType.TextureCube, TypeKind.Resource, ResourceKind.Texture);

    /// <summary>
    ///     A shadow map: a depth texture, read only by comparison.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>No <c>Sample</c> and no <c>Load</c>, deliberately.</b> A depth texture's whole
    ///         point is the fixed-function compare — <c>sampler2DShadow</c> in GLSL,
    ///         <c>OpImageSampleDref*</c> in SPIR-V, <c>texture_depth_2d</c> in WGSL — and every one
    ///         of those returns the <em>result of the comparison</em>, a float in [0, 1] that PCF
    ///         has already averaged over the filter footprint. There is no texel to hand back, so a
    ///         <c>Sample</c> here would be a member no target can implement. A shader that wants the
    ///         stored depth itself binds the same view as a plain <c>Texture2D</c>, which stays
    ///         legal and is what the library does today.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>SampleCompare</c> needs a fragment stage and <c>SampleCompareLevelZero</c>
    ///         does not</b>, the same split <c>Sample</c>/<c>SampleLevel</c> has and for the same
    ///         reason: an implicit level of detail is derived from the quad's derivatives, and only
    ///         a fragment stage has a quad. A shadow lookup in a compute pass — a screen-space
    ///         shadow mask, a probe bake — is the level-zero one.
    ///     </para>
    /// </remarks>
    public static readonly BuiltInNamedTypeSymbol DepthTexture2D =
        new("DepthTexture2D", SpecialType.DepthTexture2D, TypeKind.Resource, ResourceKind.Texture);

    /// <summary>
    ///     The sampler a <see cref="DepthTexture2D" /> is read through: a compare function and a
    ///     filter, with no way to return a texel.
    /// </summary>
    /// <remarks>
    ///     ⚠ Not interchangeable with <see cref="Sampler" /> in either direction. The reference
    ///     value a comparison sampler needs has no meaning to a filtering one, and Vulkan's
    ///     <c>compareEnable</c>, WebGPU's <c>comparison</c> binding type and GLSL's
    ///     <c>samplerShadow</c> are each a distinct object the host creates on purpose. Which is
    ///     why the pairing is checked by the type system here rather than left to a validation
    ///     layer three hours later.
    /// </remarks>
    public static readonly BuiltInNamedTypeSymbol ComparisonSampler =
        new("ComparisonSampler", SpecialType.ComparisonSampler, TypeKind.Resource, ResourceKind.Sampler);

    /// <summary>
    ///     The scene's ray-tracing hierarchy, opened with one method: <c>Trace</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Deliberately <em>without</em> a language-level ray query object. A
    ///         <c>rayQueryEXT</c> is a mutable opaque local — a kind of value Raven has nowhere
    ///         else and would have to invent assignment, storage and verifier rules for. So
    ///         <c>Trace</c> is the whole surface, and each backend synthesizes the query, the
    ///         proceed loop and the committed-hit read internally, where mutable opaque state is
    ///         already its business.
    ///     </para>
    ///     <para>
    ///         The price is that only this one policy is expressible — opaque geometry, every
    ///         instance, traced to completion. That is the policy every current caller wants
    ///         (<c>Library/DistanceFields/RayQueryField.rvn</c>), and a second method is cheaper
    ///         than a query object the day one wants another.
    ///     </para>
    /// </remarks>
    public static readonly BuiltInNamedTypeSymbol AccelerationStructure =
        new(
            "AccelerationStructure",
            SpecialType.AccelerationStructure,
            TypeKind.Resource,
            ResourceKind.AccelerationStructure
        );

    static readonly Dictionary<string, NamedTypeSymbol> ByName;
    static readonly Dictionary<SpecialType, PrimitiveTypeSymbol> BySpecialType;
    static readonly Dictionary<SyntaxKind, PrimitiveTypeSymbol> ByKeyword;

    /// <summary>Every intrinsic type, for scope population and tests.</summary>
    public static IReadOnlyCollection<NamedTypeSymbol> All => ByName.Values;

    static BuiltInTypes() {
        PrimitiveTypeSymbol[] primitives = [
            Void, Bool, Int, UInt, Float, Double, Int64, UInt64,
            Bool2, Bool3, Bool4, Int2, Int3, Int4, UInt2, UInt3, UInt4,
            Float2, Float3, Float4, Double2, Double3, Double4,
            Mat2, Mat2x3, Mat2x4, Mat3, Mat3x2, Mat3x4, Mat4, Mat4x2, Mat4x3
        ];

        NamedTypeSymbol[] named = [
            Sampler, Texture2D, Texture3D, TextureCube, AccelerationStructure,
            DepthTexture2D, ComparisonSampler
        ];

        ByName = new(StringComparer.Ordinal);
        BySpecialType = [];

        foreach (var type in primitives) {
            ByName[type.Name] = type;
            BySpecialType[type.SpecialType] = type;
        }

        foreach (var type in named) {
            ByName[type.Name] = type;
        }

        ByKeyword = new() {
            [SyntaxKind.BoolKeyword] = Bool,
            [SyntaxKind.Bool2Keyword] = Bool2,
            [SyntaxKind.Bool3Keyword] = Bool3,
            [SyntaxKind.Bool4Keyword] = Bool4,
            [SyntaxKind.IntKeyword] = Int,
            [SyntaxKind.Int2Keyword] = Int2,
            [SyntaxKind.Int3Keyword] = Int3,
            [SyntaxKind.Int4Keyword] = Int4,
            [SyntaxKind.UIntKeyword] = UInt,
            [SyntaxKind.UInt2Keyword] = UInt2,
            [SyntaxKind.UInt3Keyword] = UInt3,
            [SyntaxKind.UInt4Keyword] = UInt4,
            [SyntaxKind.FloatKeyword] = Float,
            [SyntaxKind.Float2Keyword] = Float2,
            [SyntaxKind.Float3Keyword] = Float3,
            [SyntaxKind.Float4Keyword] = Float4,
            [SyntaxKind.DoubleKeyword] = Double,
            [SyntaxKind.Double2Keyword] = Double2,
            [SyntaxKind.Double3Keyword] = Double3,
            [SyntaxKind.Double4Keyword] = Double4,
            [SyntaxKind.Mat2Keyword] = Mat2,
            [SyntaxKind.Mat2x3Keyword] = Mat2x3,
            [SyntaxKind.Mat2x4Keyword] = Mat2x4,
            [SyntaxKind.Mat3Keyword] = Mat3,
            [SyntaxKind.Mat3x2Keyword] = Mat3x2,
            [SyntaxKind.Mat3x4Keyword] = Mat3x4,
            [SyntaxKind.Mat4Keyword] = Mat4,
            [SyntaxKind.Mat4x2Keyword] = Mat4x2,
            [SyntaxKind.Mat4x3Keyword] = Mat4x3
        };

        AddResourceMembers();
    }

    /// <summary>Resolves an intrinsic type by its source name (<c>float3</c>, <c>Texture2D</c>).</summary>
    public static NamedTypeSymbol? Lookup(string name) => ByName.GetValueOrDefault(name);

    /// <summary>The primitive type for a <see cref="SpecialType" />; throws for non-primitives.</summary>
    public static PrimitiveTypeSymbol FromSpecialType(SpecialType specialType) => BySpecialType[specialType];

    /// <summary>The type behind a <c>PredefinedType</c> keyword token, or null.</summary>
    public static PrimitiveTypeSymbol? FromKeyword(SyntaxKind kind) => ByKeyword.GetValueOrDefault(kind);

    /// <summary>The vector of <paramref name="component" /> with this many lanes, or null if there is none.</summary>
    public static PrimitiveTypeSymbol? Vector(SpecialType component, int count) {
        if (count == 1) {
            return BySpecialType.GetValueOrDefault(component);
        }

        var name = component switch {
            SpecialType.Bool => "bool",
            SpecialType.Int => "int",
            SpecialType.UInt => "uint",
            SpecialType.Float => "float",
            SpecialType.Double => "double",
            _ => null
        };

        return name is null || count is < 2 or > 4
            ? null
            : (PrimitiveTypeSymbol?)ByName.GetValueOrDefault(name + count);
    }

    static PrimitiveTypeSymbol Vec(string name, SpecialType specialType, SpecialType component, int count) =>
        new(name, specialType, TypeKind.Vector, component, count);

    static PrimitiveTypeSymbol Mat(string name, SpecialType specialType, int rows, int columns) =>
        new(name, specialType, TypeKind.Matrix, SpecialType.Float, rows, columns);

    /// <summary>
    ///     The methods a texture carries.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>Sample</c> takes the level of detail from the fragment stage's derivatives, so it
    ///         means nothing outside one; <c>SampleLevel</c> states the level and works in every
    ///         stage. That is why a vertex shader reading a heightmap needs it, and why the backends
    ///         do not have to guess a level for a stage that has no derivatives.
    ///     </para>
    ///     <para>
    ///         <c>SampleGrad</c> is the third form, and it exists for the case the other two cannot
    ///         serve: a resolve pass, where the fragment quad's derivatives are meaningless because
    ///         the neighbouring pixel may belong to a different triangle, and where one stated level
    ///         would throw away the anisotropy that keeps a grazing floor sharp. The gradients are
    ///         the caller's — derived from the triangle plane rather than from the quad — and they
    ///         are <em>UV-space</em> gradients, the same as what <c>ddx(uv)</c> would have produced,
    ///         so a fragment shader can pass those directly and get exactly <c>Sample</c> back.
    ///     </para>
    ///     <para>
    ///         <c>GetDimensions</c> returns its answer rather than filling <c>out</c> parameters the
    ///         way HLSL does — Raven has no by-reference arguments, and both targets' query
    ///         instructions return a value anyway. Signed, because that is what GLSL's
    ///         <c>textureSize</c> hands back, and a conversion nobody wrote is worse than a sign.
    ///     </para>
    /// </remarks>
    static void AddResourceMembers() {
        Texture2D.SetMembers(
            [
                new SynthesizedMethodSymbol(Texture2D, "Sample", Float4, [("sampler", Sampler), ("uv", Float2)]),
                new SynthesizedMethodSymbol(
                    Texture2D,
                    "SampleLevel",
                    Float4,
                    [("sampler", Sampler), ("uv", Float2), ("lod", Float)]
                ),
                new SynthesizedMethodSymbol(
                    Texture2D,
                    "SampleGrad",
                    Float4,
                    [("sampler", Sampler), ("uv", Float2), ("ddx", Float2), ("ddy", Float2)]
                ),
                new SynthesizedMethodSymbol(Texture2D, "Load", Float4, [("coordinate", Int3)]),
                new SynthesizedMethodSymbol(Texture2D, "GetDimensions", Int2, [("lod", Int)])
            ]
        );

        Texture3D.SetMembers(
            [
                new SynthesizedMethodSymbol(Texture3D, "Sample", Float4, [("sampler", Sampler), ("uvw", Float3)]),
                new SynthesizedMethodSymbol(
                    Texture3D,
                    "SampleLevel",
                    Float4,
                    [("sampler", Sampler), ("uvw", Float3), ("lod", Float)]
                ),
                new SynthesizedMethodSymbol(
                    Texture3D,
                    "SampleGrad",
                    Float4,
                    [("sampler", Sampler), ("uvw", Float3), ("ddx", Float3), ("ddy", Float3)]
                ),
                new SynthesizedMethodSymbol(Texture3D, "GetDimensions", Int3, [("lod", Int)])
            ]
        );

        TextureCube.SetMembers(
            [
                new SynthesizedMethodSymbol(
                    TextureCube,
                    "Sample",
                    Float4,
                    [("sampler", Sampler), ("direction", Float3)]
                ),
                new SynthesizedMethodSymbol(
                    TextureCube,
                    "SampleLevel",
                    Float4,
                    [("sampler", Sampler), ("direction", Float3), ("lod", Float)]
                ),
                new SynthesizedMethodSymbol(
                    TextureCube,
                    "SampleGrad",
                    Float4,
                    [("sampler", Sampler), ("direction", Float3), ("ddx", Float3), ("ddy", Float3)]
                ),

                // A cube's faces are square and all six are the same size, so its size is two
                // numbers, not three — which is also what textureSize(samplerCube) returns.
                new SynthesizedMethodSymbol(TextureCube, "GetDimensions", Int2, [("lod", Int)])
            ]
        );

        // The reference is the last argument rather than folded into the coordinate, which is what
        // HLSL, WGSL and SPIR-V all do. ⚠ GLSL is the odd one out — `texture(sampler2DShadow, vec3)`
        // packs the reference into P.z — and that repacking is the GLSL backend's business, not a
        // shape the language should inherit from one target.
        DepthTexture2D.SetMembers(
            [
                new SynthesizedMethodSymbol(
                    DepthTexture2D,
                    "SampleCompare",
                    Float,
                    [("sampler", ComparisonSampler), ("uv", Float2), ("reference", Float)]
                ),
                new SynthesizedMethodSymbol(
                    DepthTexture2D,
                    "SampleCompareLevelZero",
                    Float,
                    [("sampler", ComparisonSampler), ("uv", Float2), ("reference", Float)]
                ),

                // The one member shared with a plain Texture2D, and the one a shadow lookup cannot
                // do without: a texel size is what a PCF kernel steps by.
                new SynthesizedMethodSymbol(DepthTexture2D, "GetDimensions", Int2, [("lod", Int)])
            ]
        );

        // One method, and the answer packs into a float4 so no result struct has to exist in
        // every backend: (t, primitive index, instance id, 1) for a committed triangle hit,
        // (maxDistance, -1, -1, 0) for a miss. The indices survive the float exactly below 2^24,
        // which is more primitives than a BLAS is allowed to hold.
        AccelerationStructure.SetMembers(
            [
                new SynthesizedMethodSymbol(
                    AccelerationStructure,
                    "Trace",
                    Float4,
                    [
                        ("origin", Float3),
                        ("minDistance", Float),
                        ("direction", Float3),
                        ("maxDistance", Float)
                    ]
                )
            ]
        );
    }
}
