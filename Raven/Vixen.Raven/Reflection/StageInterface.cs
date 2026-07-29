// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.IR;

namespace Vixen.Raven.Reflection;

/// <summary>
///     What a pipeline stage's interface can carry.
/// </summary>
/// <remarks>
///     <para>
///         One predicate, for the same reason <see cref="BindingPlan" /> is one plan: both backends
///         and the reflection have to agree about what is expressible, and two copies of the rule is
///         how they come to differ. They did — SPIR-V refused an aggregate stage output as
///         <c>RVN4001</c> while GLSL emitted <c>out SomeStruct</c>, which is not valid GLSL at all.
///         <c>glslc</c> caught it; Raven reported nothing on the GLSL path.
///     </para>
///     <para>
///         The rule is Vulkan's rather than either language's. An interface variable gets one
///         <c>location</c>, so it has to be one scalar or vector; an aggregate would need a location
///         per leaf and a layout rule to assign them, and a boolean has no interface representation
///         at all because <c>OpTypeBool</c> has no size.
///     </para>
///     <para>
///         Multiple render targets are the case that proves the rule rather than the exception to
///         it: a fragment stage returning a struct is taken apart into one output <em>per member</em>
///         before it reaches here, and every one of those is asked this same question. So the check
///         never had to be relaxed — what changed was that an entry point may have several outputs.
///     </para>
///     <para>
///         <strong>A built-in is not asked at all</strong>, because the rule is about a
///         <em>location</em> and a built-in has none: <c>Location</c> and <c>BuiltIn</c> are
///         mutually exclusive decorations, and the type is the target's own rather than something a
///         host lays out. <c>SV_IsFrontFace</c> is the case that makes the distinction visible —
///         <c>gl_FrontFacing</c> and SPIR-V's <c>FrontFacing</c> are both declared <c>bool</c> by
///         the target itself, so asking this predicate about one would refuse a variable neither
///         backend was going to write a location for.
///     </para>
/// </remarks>
public static class StageInterface {
    /// <summary>Whether a stage input or output may have this type.</summary>
    public static bool CanCarry(IrType type) =>
        type is IrScalarType { Kind: not IrTypeKind.Bool } or IrVectorType { Component.Kind: not IrTypeKind.Bool };

    /// <summary>
    ///     Whether a varying of this type has to be flat rather than interpolated.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>Integers, and it is a rule rather than a preference.</strong> There is no
    ///         interpolation an integer could take — the rasteriser weights its inputs by barycentric
    ///         coordinates, which produces a fraction — so SPIR-V requires <c>Flat</c> on a
    ///         <c>Fragment</c> input of integer type and GLSL requires <c>flat</c> on the same. A
    ///         module without it is invalid, and a driver's answer to invalid ranges from a validation
    ///         error to a value that is right at one vertex of every triangle.
    ///     </para>
    ///     <para>
    ///         Asked of the type rather than declared by the shader, because there is no other legal
    ///         answer for an integer and no legal way to ask for it on a float. A qualifier would be a
    ///         syntax whose only correct value the compiler already knows.
    ///     </para>
    /// </remarks>
    public static bool MustBeFlat(IrType type) =>
        type is IrScalarType { Kind: IrTypeKind.Int or IrTypeKind.UInt }
            or IrVectorType { Component.Kind: IrTypeKind.Int or IrTypeKind.UInt };

    /// <summary>
    ///     The reason a type cannot be carried, phrased for <c>RVN4001</c>'s <c>{0}</c>.
    /// </summary>
    public static string Describe(IrType type, string name, bool isInput) =>
        $"The type '{type.Name}' of stage {(isInput ? "input" : "output")} '{name}'";
}
