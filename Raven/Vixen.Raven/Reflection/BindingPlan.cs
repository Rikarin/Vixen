// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Raven.IR;
using Vixen.Raven.Symbols;

namespace Vixen.Raven.Reflection;

/// <summary>
///     One descriptor binding: either a set's uniform block or one opaque resource.
/// </summary>
/// <param name="Set">The descriptor set it belongs to.</param>
/// <param name="Binding">Binding index within that set.</param>
/// <param name="Kind">What sort of binding this is.</param>
/// <param name="Name">
///     The name to emit. A block is named for its shader and set — <c>LitPerMaterialUniforms</c>
///     — so a frame debugger showing two sets shows which is which; a resource keeps its
///     declared name.
/// </param>
/// <param name="Members">
///     The uniforms gathered into this block, in declaration order. Empty for an opaque
///     resource.
/// </param>
/// <param name="Resource">The opaque resource. Null for a block.</param>
public sealed record PlannedBinding(
    ResourceSet Set,
    int Binding,
    IrBindingKind Kind,
    string Name,
    ImmutableArray<IrBinding> Members,
    IrBinding? Resource
) {
    /// <summary>True when this is a set's uniform block rather than an opaque resource.</summary>
    public bool IsBlock => Resource is null;
}

/// <summary>
///     Assigns every binding its <c>(set, binding)</c> pair.
/// </summary>
/// <remarks>
///     <para>
///         The single place the descriptor layout is decided. Both emitters and
///         <see cref="ReflectionBuilder" /> read this plan, so the SPIR-V decorations, the GLSL
///         <c>layout(set = …, binding = …)</c> and the numbers the engine binds against cannot
///         disagree — there is nothing to keep in step. The differential oracle checks that,
///         but the plan is what makes it true.
///     </para>
///     <para>
///         The same reasoning as <see cref="ShaderLayout" />, one level up: two copies of a rule
///         is how two backends come to differ.
///     </para>
/// </remarks>
public static class BindingPlan {
    /// <summary>
    ///     The plan for one shader: sets in ascending index order, and within each set the
    ///     uniform block first, then textures, then samplers, each in declaration order.
    /// </summary>
    /// <remarks>
    ///     The block goes first so that adding a texture never renumbers the block, and
    ///     bindings restart at 0 in each set because that is what a Vulkan descriptor set
    ///     layout is — one namespace per set.
    /// </remarks>
    public static ImmutableArray<PlannedBinding> Of(IrShader shader) {
        ArgumentNullException.ThrowIfNull(shader);

        var plan = ImmutableArray.CreateBuilder<PlannedBinding>();

        foreach (var set in shader.Bindings.Select(b => b.Set).Distinct().Order()) {
            var inSet = shader.Bindings.Where(b => b.Set == set).ToArray();
            var binding = 0;

            if (inSet.Where(b => b.Kind == IrBindingKind.Uniform).ToImmutableArray() is { IsEmpty: false } uniforms) {
                plan.Add(
                    new(set, binding++, IrBindingKind.Uniform, BlockName(shader, set), uniforms, null)
                );
            }

            foreach (var kind in (IrBindingKind[])[IrBindingKind.Texture, IrBindingKind.Sampler]) {
                foreach (var resource in inSet.Where(b => b.Kind == kind)) {
                    plan.Add(new(set, binding++, kind, resource.Name, [], resource));
                }
            }
        }

        return plan.ToImmutable();
    }

    /// <summary>The name of a set's uniform block.</summary>
    public static string BlockName(IrShader shader, ResourceSet set) {
        ArgumentNullException.ThrowIfNull(shader);
        return $"{shader.Name}{set}Uniforms";
    }
}
