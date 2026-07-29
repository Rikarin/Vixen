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

    /// <summary>
    ///     The other declarations of this same resource, for a <c>[Shared]</c> binding.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Empty for everything else. Several composed features may each declare the frame's
    ///         texture table, and they are one binding — but each feature's body refers to its
    ///         <em>own</em> variable, because that is what it was compiled against. So the plan hands
    ///         out one <c>(set, binding)</c> pair and lists the rest here, and each backend points
    ///         every listed variable at the one declaration it emitted.
    ///     </para>
    ///     <para>
    ///         Without this the second feature's variable resolves to nothing and the emitter reports
    ///         a variable it cannot reach — which is the correct failure and a useless one, since the
    ///         author wrote a declaration that is plainly there.
    ///     </para>
    /// </remarks>
    public ImmutableArray<IrBinding> Aliases { get; init; } = [];

    /// <summary>Every declaration this covers: the resource, then its aliases.</summary>
    public IEnumerable<IrBinding> Declarations {
        get {
            if (Resource is { } resource) {
                yield return resource;
            }

            foreach (var alias in Aliases) {
                yield return alias;
            }
        }
    }
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

        // Push constants are deliberately absent: they have no descriptor, so numbering them into
        // a set would give a host a (set, binding) pair to bind against that means nothing.
        var descriptors = shader.Bindings.Where(b => b.Kind != IrBindingKind.PushConstant).ToArray();

        foreach (var set in descriptors.Select(b => b.Set).Distinct().Order()) {
            var inSet = descriptors.Where(b => b.Set == set).ToArray();
            var binding = 0;

            if (inSet.Where(b => b.Kind == IrBindingKind.Uniform).ToImmutableArray() is { IsEmpty: false } uniforms) {
                plan.Add(
                    new(set, binding++, IrBindingKind.Uniform, BlockName(shader, set), uniforms, null)
                );
            }

            // Storage buffers last, after textures and samplers, for the same reason the uniform
            // block goes first: adding one must not renumber anything that already exists.
            foreach (var kind in (IrBindingKind[])[
                IrBindingKind.Texture,
                IrBindingKind.Sampler,
                IrBindingKind.StorageBuffer,
                IrBindingKind.StorageImage
            ]) {
                // A shared binding declared by several features is one binding, recognised by the
                // name they all wrote. Grouped rather than deduplicated in place so that the first
                // declaration keeps the slot and the rest become its aliases — every one of them has
                // a variable some feature's body refers to, and all of them have to resolve.
                foreach (var group in inSet.Where(b => b.Kind == kind).GroupBy(SharedKey)) {
                    var resource = group.First();

                    plan.Add(
                        new(set, binding++, kind, resource.Name, [], resource) {
                            Aliases = [.. group.Skip(1)]
                        }
                    );
                }
            }
        }

        return plan.ToImmutable();
    }

    /// <summary>What decides whether two bindings of one kind are the same resource.</summary>
    /// <param name="binding">The binding.</param>
    /// <remarks>
    ///     The declared name for a <c>[Shared]</c> binding, and the binding itself for everything
    ///     else — which is to say nothing is grouped unless it said so. Returning the object rather
    ///     than a name for the unshared case is what keeps two ordinary bindings that happen to be
    ///     called the same thing apart; they are already qualified by then, but relying on that would
    ///     make this correct only because of something two files away.
    /// </remarks>
    static object SharedKey(IrBinding binding) => binding.IsShared ? binding.Name : binding;

    /// <summary>The name of a set's uniform block.</summary>
    public static string BlockName(IrShader shader, ResourceSet set) {
        ArgumentNullException.ThrowIfNull(shader);
        return $"{shader.Name}{set}Uniforms";
    }

    /// <summary>
    ///     The shader's push constants, in declaration order — the members of its one
    ///     push-constant block.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>One block per shader, not one per marked field.</strong> Vulkan gives a
    ///         pipeline a single push-constant range, so several marked fields are several members
    ///         of one block rather than several blocks; a second block is not a thing a driver
    ///         could bind.
    ///     </para>
    ///     <para>
    ///         The bytes are guaranteed to be scarce — the Vulkan minimum is 128 — which is what
    ///         <c>RVN2121</c> warns about. That is a warning rather than an error because a device
    ///         may offer more and a shader that knows its target may legitimately use it.
    ///     </para>
    /// </remarks>
    public static ImmutableArray<IrBinding> PushConstants(IrShader shader) {
        ArgumentNullException.ThrowIfNull(shader);
        return [.. shader.Bindings.Where(b => b.Kind == IrBindingKind.PushConstant)];
    }

    /// <summary>The name of a shader's push-constant block.</summary>
    public static string PushConstantBlockName(IrShader shader) {
        ArgumentNullException.ThrowIfNull(shader);
        return $"{shader.Name}PushConstants";
    }
}
