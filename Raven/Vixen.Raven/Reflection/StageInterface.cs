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
/// </remarks>
public static class StageInterface {
    /// <summary>Whether a stage input or output may have this type.</summary>
    public static bool CanCarry(IrType type) =>
        type is IrScalarType { Kind: not IrTypeKind.Bool } or IrVectorType { Component.Kind: not IrTypeKind.Bool };

    /// <summary>
    ///     The reason a type cannot be carried, phrased for <c>RVN4001</c>'s <c>{0}</c>.
    /// </summary>
    public static string Describe(IrType type, string name, bool isInput) =>
        $"The type '{type.Name}' of stage {(isInput ? "input" : "output")} '{name}'";
}
