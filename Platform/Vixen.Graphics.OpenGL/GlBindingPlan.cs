// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.OpenGL;

/// <summary>Which flat GL index one descriptor binding resolves to.</summary>
/// <param name="Kind">What it binds.</param>
/// <param name="Index">The GL binding point, texture unit or image unit.</param>
/// <remarks>
///     No name here, deliberately. A descriptor-set layout describes shapes and carries no GLSL
///     identifiers, and inventing a convention name would be a second source of truth that agreed
///     with the shader only by luck. <see cref="GlslTranslator" /> reads the real names out of the
///     source it is rewriting, which is the only place they exist.
/// </remarks>
readonly record struct GlBindingSlot(DescriptorKind Kind, uint Index);

/// <summary>Four descriptor sets flattened into GL's one flat namespace per resource class.</summary>
/// <remarks>
///     <para>
///         <b>The translation ADR-001 is really asking about.</b> Vulkan and D3D12 both have a
///         two-level binding model — a set (or root table) and an index within it — and GL has one
///         flat sequence per resource class: uniform buffer binding points, storage buffer binding
///         points, texture units, image units. So the layout is walked once at creation, in slot
///         order, and every binding is given the next index of its class. Nothing is computed per
///         draw.
///     </para>
///     <para>
///         Slot order is what makes it stable: the RHI's four sets are ordered by how often they
///         change (<see cref="DescriptorSetSlot" />), so per-frame bindings land at low indices and
///         per-draw ones at high, and two pipelines that share a per-frame set agree about where it
///         lives. A plan that numbered bindings in declaration order instead would give the same set
///         different indices in two pipelines, and the bind cache would be wrong in a way that only
///         shows up when the two are used in the same frame.
///     </para>
///     <para>
///         Class-separate counters, not one shared counter. GL's namespaces are independent — a
///         uniform buffer at binding 0 and a texture at unit 0 do not collide — and sharing a
///         counter would waste both and mean a set with many textures pushed uniform blocks past
///         the driver's limit for no reason.
///     </para>
/// </remarks>
sealed class GlBindingPlan {
    readonly Dictionary<(DescriptorSetSlot Slot, uint Binding), GlBindingSlot> slots = [];

    GlBindingPlan(int pushConstantVectors) => PushConstantVectors = pushConstantVectors;

    /// <summary>How many <c>vec4</c>s of push constants the layout declares.</summary>
    /// <remarks>
    ///     GL has no push constants. They arrive as <c>uniform vec4 vixen_PushConstants[n]</c>,
    ///     uploaded with one <c>glUniform4fv</c> — which is a real uniform and therefore program
    ///     state, so a pipeline change invalidates it and the state cache has to re-upload. That is
    ///     the whole of the emulation, and it is why <c>MaxPushConstantSize</c> stays at the RHI's
    ///     128-byte floor here rather than at what a desktop driver would allow.
    /// </remarks>
    public int PushConstantVectors { get; }

    /// <summary>Every binding the plan resolved, for the link-time pass and for tests.</summary>
    public IReadOnlyDictionary<(DescriptorSetSlot Slot, uint Binding), GlBindingSlot> Slots => slots;

    /// <summary>Builds the plan for a pipeline layout.</summary>
    /// <param name="sets">The set layouts, as declared.</param>
    /// <param name="pushConstantBytes">The total push-constant block size.</param>
    /// <remarks>
    ///     Sets are sorted by slot rather than taken in array order. A caller is free to hand the
    ///     layouts over in any order — <c>PipelineLayoutDescription</c> says "in slot order" and
    ///     nothing enforces it — and a plan that trusted the array would be quietly
    ///     caller-dependent.
    /// </remarks>
    public static GlBindingPlan Build(
        IReadOnlyList<(DescriptorSetSlot Slot, DescriptorBinding[] Bindings, string Name)> sets,
        int pushConstantBytes
    ) {
        var plan = new GlBindingPlan((pushConstantBytes + 15) / 16);
        uint uniforms = 0, storages = 0, textures = 0, images = 0, samplers = 0;

        foreach (var set in sets.OrderBy(entry => entry.Slot)) {
            foreach (var binding in (set.Bindings ?? []).OrderBy(entry => entry.Binding)) {
                // An array binding takes a contiguous run, because that is what GLSL's
                // `uniform sampler2D atlas[4]` occupies: units n..n+3, consecutively.
                var count = (uint)Math.Max(1, binding.Count);

                var index = binding.Kind switch {
                    DescriptorKind.UniformBuffer or DescriptorKind.DynamicUniformBuffer => Take(ref uniforms, count),
                    DescriptorKind.StorageBuffer or DescriptorKind.DynamicStorageBuffer => Take(ref storages, count),
                    DescriptorKind.SampledTexture => Take(ref textures, count),
                    DescriptorKind.StorageTexture => Take(ref images, count),
                    DescriptorKind.Sampler => Take(ref samplers, count),

                    // Named rather than left to the catch-all: a layout carrying one is a caller
                    // that skipped the capability check, and "no binding class" would send it
                    // hunting for a GL feature that does not exist to enable.
                    DescriptorKind.AccelerationStructure => throw new NotSupportedException(
                        "A descriptor set layout declares an acceleration structure on the OpenGL "
                        + "backend, which has no ray tracing. Ask Features.HasRayTracing and take "
                        + "the distance-field tracer."
                    ),
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(sets),
                        binding.Kind,
                        "This descriptor kind has no OpenGL binding class."
                    )
                };

                plan.slots[(set.Slot, binding.Binding)] = new(binding.Kind, index);
            }
        }

        return plan;

        static uint Take(ref uint counter, uint count) {
            var first = counter;
            counter += count;
            return first;
        }
    }

    /// <summary>Where a binding lives, or <see langword="null" /> if the layout does not declare it.</summary>
    /// <remarks>
    ///     Returning nothing rather than throwing: a descriptor set may legitimately be bound to a
    ///     pipeline whose layout uses only part of it — that is the whole point of ordering sets by
    ///     change frequency — and a per-frame set bound to a pipeline that reads none of it is
    ///     ordinary, not an error.
    /// </remarks>
    public GlBindingSlot? Resolve(DescriptorSetSlot slot, uint binding) =>
        slots.TryGetValue((slot, binding), out var found) ? found : null;
}
