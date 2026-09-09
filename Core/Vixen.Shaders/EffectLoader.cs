// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Vixen.Core.Mathematics;
using Vixen.Graphics;

namespace Vixen.Shaders;

/// <summary>
///     Turns a baked <see cref="EffectData" /> into a live <see cref="Effect" /> on one device.
/// </summary>
/// <remarks>
///     <para>
///         The one step between "bytes that came from somewhere" and "a thing a pipeline can be built
///         from", and the only step in the whole chain that needs a device. Every tier — the disk
///         cache, the baked bundle, the dev machine on the other end of a socket — hands over the
///         same record and this creates the same handles from it, so where a variant came from
///         changes nothing about what it is.
///     </para>
///     <para>
///         <strong>Set layouts are shared between effects, and that is not just an economy.</strong>
///         Every shader that binds the per-frame set describes the same layout, and a device handed
///         four hundred structurally identical layouts creates four hundred objects that a descriptor
///         set allocated against one cannot be used with the other. Caching by shape means a
///         per-frame set is allocated once and bound to every pipeline in the frame.
///     </para>
///     <para>
///         ⚠ <strong>And so is the pipeline layout, which for a long time was not.</strong> It is a
///         function of the set layouts and the push-constant ranges and of nothing else, so it caches
///         by exactly the same argument — see <see cref="PipelineLayoutOf" />. Until
///         <a href="https://github.com/Rikarin/Vixen/issues/1111">#1111</a> a fresh one was created
///         per <see cref="Load" />, owned by nobody, and freed by one caller out of five.
///     </para>
/// </remarks>
public sealed class EffectLoader(IGraphicsDevice device) {
    /// <summary>How many descriptor sets a pipeline layout has. See the convention in docs/plan/05.</summary>
    const int SetCount = 4;

    /// <summary>
    ///     And a fifth, for a shader that declares a bindless table.
    /// </summary>
    /// <remarks>
    ///     ⚠ Only for a shader that declares one. Vulkan guarantees four bound descriptor sets and no
    ///     more, so giving every pipeline layout a fifth would refuse to create a pipeline on a device
    ///     that is perfectly able to run the shader — for a set the shader never mentions. A variant
    ///     compiled without the table is a four-set layout exactly as it was.
    /// </remarks>
    const int BindlessSetCount = 5;

    readonly Dictionary<string, DescriptorSetLayoutHandle> layouts = new(StringComparer.Ordinal);
    readonly Dictionary<string, PipelineLayoutHandle> pipelineLayouts = new(StringComparer.Ordinal);

    /// <summary>The device these effects are created on.</summary>
    public IGraphicsDevice Device { get; } = device;

    /// <summary>How many distinct set layouts have been created.</summary>
    /// <remarks>Observable so a test can assert the sharing above actually happens.</remarks>
    public int LayoutCount => layouts.Count;

    /// <summary>How many distinct pipeline layouts have been created.</summary>
    /// <remarks>
    ///     The sibling counter <see cref="LayoutCount" /> had and
    ///     <a href="https://github.com/Rikarin/Vixen/issues/1111">#1111</a> asked for, and the only
    ///     thing anywhere that can tell a loader sharing pipeline layouts from one minting a fresh
    ///     handle per <see cref="Load" />: an <see cref="Effect" /> looks identical either way, and
    ///     the difference is only visible as a device's tally rising for the life of the process.
    /// </remarks>
    public int PipelineLayoutCount => pipelineLayouts.Count;

    /// <summary>
    ///     How many descriptors an unbounded binding holds.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The shader says a table has no length and the <em>layout</em> has to say one anyway,
    ///         because a descriptor pool is sized from it. This is where that number comes from, and
    ///         it has to be the same one the <see cref="BindlessTable" /> was built with: the table
    ///         hands out indices up to its own capacity and the set has to have a descriptor at every
    ///         one of them.
    ///     </para>
    ///     <para>
    ///         ⚠ <see cref="BindlessTable.ConventionalCapacity" /> rather than the device's ceiling,
    ///         which is what a <c>DescriptorSetLayoutDescription</c> falls back to when nobody says —
    ///         and read from there rather than written again here, because a second copy of a number
    ///         two sides have to agree on is a way for them to stop agreeing. See that constant for
    ///         why it is four thousand. A project that genuinely needs more sets this and builds its
    ///         table to match.
    ///     </para>
    ///     <para>
    ///         An <em>ask</em> rather than the answer: what a variant's layouts actually state is
    ///         this, clamped so the whole pipeline layout fits the device — see
    ///         <see cref="CapacityFor" /> for the arithmetic and the terrain shaders for why the
    ///         clamp exists.
    ///     </para>
    /// </remarks>
    public int BindlessCapacity { get; set; } = BindlessTable.ConventionalCapacity;

    /// <summary>Creates the effect one record describes.</summary>
    public Effect Load(EffectData data) {
        ArgumentNullException.ThrowIfNull(data);

        var key = data.ToKey();
        var count = data.Bindings.Any(binding => binding.Set == DescriptorSetSlot.Bindless)
            ? BindlessSetCount
            : SetCount;

        var sets = new DescriptorSetLayoutHandle[count];
        var capacity = CapacityFor(data);

        for (var slot = 0; slot < count; slot++) {
            sets[slot] = LayoutOf(data, (DescriptorSetSlot)slot, key.ShaderName, capacity);
        }

        var stages = ImmutableArray.CreateBuilder<EffectStage>(data.Stages.Length);

        foreach (var stage in data.Stages) {
            stages.Add(new(stage.Stage, [.. stage.Bytecode], stage.EntryPoint));
        }

        var parameters = ImmutableArray.CreateBuilder<EffectParameter>(data.Parameters.Length);

        foreach (var parameter in data.Parameters) {
            if (KeyOf(parameter) is { } parameterKey) {
                parameters.Add(new(parameterKey, parameter.Offset, parameter.Size) { Set = parameter.Set });
            }
        }

        var bindings = ImmutableArray.CreateBuilder<EffectBinding>(data.Bindings.Length);

        foreach (var binding in data.Bindings) {
            bindings.Add(
                new(binding.Name, binding.Set, binding.Binding, KindOf(binding)) {
                    Size = binding.Size,
                    Count = binding.Count
                }
            );
        }

        var permutations = ImmutableArray.CreateBuilder<ParameterKey>(data.Permutations.Length);

        foreach (var permutation in data.Permutations) {
            permutations.Add(PermutationKeyOf(permutation));
        }

        return new() {
            Key = key,
            Stages = stages.ToImmutable(),
            SetLayouts = [.. sets],
            Layout = PipelineLayoutOf(sets, [.. Pushed(data)], key.ShaderName),
            ConstantBufferSize = data.ConstantBufferSize,
            Parameters = parameters.ToImmutable(),
            Bindings = bindings.ToImmutable(),
            PushConstants = [.. PushedConstants(data)],
            VertexInputs = [
                .. data.VertexInputs.Select(input => new EffectVertexInput(input.Name, input.Location, input.Kind))
            ],
            UsedPermutationKeys = permutations.ToImmutable()
        };
    }

    /// <summary>
    ///     The push-constant ranges a variant's pipeline layout declares.
    /// </summary>
    /// <remarks>
    ///     A layout was created with none of them for a while, which is the sort of omission that
    ///     produces no error and no picture: a push against a layout that declares no range is dropped
    ///     by a release driver and refused by the validation layers, and what
    ///     <c>ForwardPlus.rvn</c> pushes is the world matrix — so every object in the frame draws at
    ///     the origin.
    /// </remarks>
    static IEnumerable<PushConstantRange> Pushed(EffectData data) {
        foreach (var range in data.PushConstants) {
            if (range is { Size: > 0, Stages: not ShaderStage.None }) {
                yield return new(range.Stages, range.Offset, range.Size);
            }
        }
    }

    /// <summary>The same ranges, with the members a caller finds an offset by name in.</summary>
    /// <remarks>
    ///     Separate from <see cref="Pushed" /> because that one feeds the pipeline layout, which has
    ///     no interest in what is inside a range — the driver is told bytes. This one feeds a host
    ///     that has a value called <c>materialIndex</c> and no business knowing it is at 64.
    /// </remarks>
    static IEnumerable<EffectPushConstant> PushedConstants(EffectData data) {
        foreach (var range in data.PushConstants) {
            if (range is { Size: > 0, Stages: not ShaderStage.None }) {
                yield return new(range.Stages, range.Offset, range.Size) {
                    Members = [
                        .. (range.Members ?? []).Select(
                            member => new EffectPushConstantMemberInfo(member.Name, member.Offset, member.Size)
                        )
                    ]
                };
            }
        }
    }

    /// <summary>Forgets every cached layout, without destroying anything.</summary>
    /// <remarks>
    ///     For a device that has gone away. The handles belonged to it and went with it; keeping them
    ///     would hand a new device something the old one made. ⚠ Both caches, since
    ///     <a href="https://github.com/Rikarin/Vixen/issues/1111">#1111</a>: a pipeline layout is as
    ///     dead as a set layout when the device is, and one kept across a device change is a handle
    ///     into another device's table — which is not a leak but a use-after-free.
    /// </remarks>
    public void Clear() {
        layouts.Clear();
        pipelineLayouts.Clear();
    }

    /// <summary>Destroys every layout this loader created, and forgets them.</summary>
    /// <remarks>
    ///     <para>
    ///         What <see cref="Clear" /> is for a device that is <em>still there</em>, and the half
    ///         that did not exist: the loader is the only thing that knows how many distinct layouts
    ///         it made, so it is the only thing that can give them back.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every effect this loader has produced is dead afterwards</b>, and so is every
    ///         pipeline built from one — destroy those first, and after the device is idle. This is
    ///         a teardown, not an eviction: there is no reference counting here and a caller that
    ///         calls it while a variant is still bound has freed a layout the frame is using.
    ///     </para>
    /// </remarks>
    public void Release() {
        foreach (var layout in pipelineLayouts.Values) {
            Device.Destroy(layout);
        }

        foreach (var layout in layouts.Values) {
            Device.Destroy(layout);
        }

        Clear();
    }

    /// <summary>
    ///     How many descriptors one variant's unbounded bindings each get: the ask, clamped to fit
    ///     the device.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>Every unbounded binding in the variant gets this many, and the total is what
    ///         the device judges.</strong> Update-after-bind sampled images are budgeted per
    ///         <em>pipeline layout</em> — <c>maxPerStageDescriptorUpdateAfterBindSampledImages</c> and
    ///         its whole-set sibling, which
    ///         <see cref="GraphicsDeviceFeatures.MaxBindlessDescriptors" /> already reports as one
    ///         number. A shader with three unbounded arrays that were each handed the full ceiling
    ///         asks for three ceilings, and <c>vkCreatePipelineLayout</c> refuses — which is exactly
    ///         what the terrain shaders did on MoltenVK, where the ceiling is a million: three
    ///         million-entry splat arrays and two ordinary textures came to 3,000,002 against a limit
    ///         of 1,000,000.
    ///     </para>
    ///     <para>
    ///         So the budget is shared: the ceiling, less the bounded sampled images the same variant
    ///         binds beside the arrays, split evenly across the unbounded bindings, and never more
    ///         than <see cref="BindlessCapacity" /> each. On a desktop driver the clamp is invisible —
    ///         a million split three ways still dwarfs the ask — and on the device where it bites, the
    ///         alternative was a layout the driver refuses. A write past the clamped length is refused
    ///         by <see cref="IGraphicsDevice.UpdateDescriptorSet" /> with the length in the message,
    ///         so a project that outgrows the number finds it at the write, not by wrapping.
    ///     </para>
    /// </remarks>
    int CapacityFor(EffectData data) {
        var unbounded = 0;
        var bounded = 0;

        foreach (var binding in data.Bindings) {
            // The same reading DescriptorBindingExtensions.IsUnbounded makes: zero on a texture or a
            // sampler is an unbounded array, zero on a buffer is a runtime-sized block and one
            // descriptor.
            if (new DescriptorBinding(binding.Binding, binding.Kind, binding.Stages, binding.Count).IsUnbounded()) {
                unbounded++;
            } else if (binding.Kind == DescriptorKind.SampledTexture) {
                bounded += Math.Max(1, binding.Count);
            }
        }

        // No unbounded binding means the number reaches no layout at all; without the capability the
        // device refuses the layout with the capability's name, which says more than a zero here
        // would.
        if (unbounded == 0 || Device.Features.MaxBindlessDescriptors <= 0) {
            return BindlessCapacity;
        }

        var budget = Math.Max(1, (Device.Features.MaxBindlessDescriptors - bounded) / unbounded);

        return Math.Min(BindlessCapacity, budget);
    }

    /// <summary>
    ///     The layout for one set, created once per distinct shape.
    /// </summary>
    /// <remarks>
    ///     A set with no bindings still gets a layout rather than a null handle, because set indices
    ///     are positional: a shader that binds only the per-material set still binds it at index two,
    ///     and a pipeline layout that skipped the two empty ones would put it at index zero and every
    ///     descriptor set in the frame would land in the wrong place.
    /// </remarks>
    DescriptorSetLayoutHandle LayoutOf(EffectData data, DescriptorSetSlot slot, string shaderName, int capacity) {
        List<DescriptorBinding> bindings = [];

        foreach (var binding in data.Bindings) {
            if (binding.Set == slot) {
                bindings.Add(
                    new(binding.Binding, KindOf(binding), binding.Stages, binding.Count, binding.SampleType)
                );
            }
        }

        bindings.Sort(static (left, right) => left.Binding.CompareTo(right.Binding));

        // Only where an unbounded binding actually is, so the cache key of every other set is what
        // it has always been and the shapes a project already shares keep sharing. Any slot, not
        // just the table's: the terrain shaders declare unsized splat arrays in the per-material
        // set, and a set whose capacity nobody states falls back to the device's ceiling — once per
        // array, which is how three arrays came to ask for three ceilings.
        var stated = bindings.Any(static binding => binding.IsUnbounded()) ? capacity : 0;
        var shape = Shape(slot, bindings, stated);

        if (layouts.TryGetValue(shape, out var existing)) {
            return existing;
        }

        var description = new DescriptorSetLayoutDescription(slot, [.. bindings], $"{shaderName}.{slot}", stated);
        description.Validate();

        var created = Device.CreateDescriptorSetLayout(description);
        layouts[shape] = created;
        return created;
    }

    /// <summary>
    ///     The pipeline layout for one variant, created once per distinct shape.
    /// </summary>
    /// <param name="sets">The set layouts, already shared by <see cref="LayoutOf" />.</param>
    /// <param name="pushed">The push-constant ranges the layout declares.</param>
    /// <param name="shaderName">What to call it, for a capture and the validation layers.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Shared for the same reason the set layouts are, and unshared it was a leak
    ///         rather than an economy</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1111">#1111</a>. A
    ///         <c>PipelineLayoutHandle</c> minted per <see cref="Load" /> is owned by nobody: an
    ///         <see cref="Effect" /> is a plain record with no disposal, every caller in the tree
    ///         held one without freeing it, and a long editor session that compiles a variant per
    ///         keystroke therefore grew the device's object count for the life of the process. The
    ///         alternative — distributing a <c>Destroy</c> to every caller — makes an ownership rule
    ///         out of a field whose existence is not obvious from the record.
    ///     </para>
    ///     <para>
    ///         <b>A pipeline layout is a function of exactly two things</b>, the set layouts and the
    ///         push-constant ranges, so two variants that agree on both are interchangeable in the
    ///         only sense a driver cares about: a descriptor set allocated against one, and a
    ///         pipeline built from the other, are compatible because the layouts are compatible.
    ///         The set handles can go in the key <em>as handles</em> because they are already shared
    ///         by shape — two variants describing the same per-frame set have the same handle by the
    ///         time they reach here, so the key does not have to re-derive the shape.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The name belongs to whichever variant needed the shape first</b>, which is the
    ///         one thing sharing costs: a capture shows <c>ForwardPlus</c> against a layout that
    ///         forty shaders are using. The set layouts already made that trade under
    ///         <c>$"{shaderName}.{slot}"</c> and it has never cost a diagnosis.
    ///     </para>
    /// </remarks>
    PipelineLayoutHandle PipelineLayoutOf(DescriptorSetLayoutHandle[] sets, PushConstantRange[] pushed, string shaderName) {
        var shape = PipelineShape(sets, pushed);

        if (pipelineLayouts.TryGetValue(shape, out var existing)) {
            return existing;
        }

        var created = Device.CreatePipelineLayout(new(sets, pushed, shaderName));
        pipelineLayouts[shape] = created;
        return created;
    }

    /// <summary>The cache key for a pipeline layout: the set handles and the ranges, and nothing else.</summary>
    /// <param name="sets">The set layouts, in slot order — order is part of the key, because it is a layout.</param>
    /// <param name="pushed">The push-constant ranges.</param>
    /// <remarks>
    ///     ⚠ The ranges are in the key and leaving them out would be silent: two variants over one
    ///     set of descriptors, one pushing a world matrix and one pushing nothing, would share the
    ///     first one's layout — and a push against a layout that declares no range is dropped by a
    ///     release driver, which is <see cref="Pushed" />'s own story of every object drawing at the
    ///     origin, arriving by way of a cache instead.
    /// </remarks>
    internal static string PipelineShape(DescriptorSetLayoutHandle[] sets, PushConstantRange[] pushed) {
        var builder = new StringBuilder();

        foreach (var set in sets) {
            builder.Append(set.Value).Append('|');
        }

        foreach (var range in pushed) {
            builder.Append('#').Append((int)range.Stages).Append(':').Append(range.Offset).Append(':').Append(range.Size);
        }

        return builder.ToString();
    }

    /// <summary>
    ///     What a binding is once the four-set convention is applied to it.
    /// </summary>
    /// <param name="binding">The binding as the reflection describes it.</param>
    /// <returns>Its kind.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>A block that varies per draw is bound at an offset per draw, so its descriptor is a
    ///         dynamic one.</b> The alternative is a descriptor set per object, which is the single
    ///         most common reason a Vulkan renderer ends up slower than the D3D11 one it replaced —
    ///         see <c>ForwardLightingRenderFeature</c>, which writes one buffer and moves an offset.
    ///     </para>
    ///     <para>
    ///         <b>The shader says so now, and this used to infer it.</b> Raven's
    ///         <c>[DynamicOffset]</c> marks a block as bound at a moving offset, and
    ///         <see cref="EffectBindingData.DynamicOffset" /> carries the answer here. ⚠ The
    ///         inference it replaces read the claim off the <i>set index</i> — a uniform block in
    ///         <see cref="DescriptorSetSlot.PerDraw" /> used by any non-compute stage — so one number
    ///         carried two different claims at once: where a binding lives, and whether its contents
    ///         change between draws. A shader that wanted the first without the second had no way to
    ///         say so, which is why the rule needed a compute carve-out ("per draw" is a claim about
    ///         draws and a dispatch has none, so <c>BindlessProbe</c>'s per-draw block is storage
    ///         rather than a claim) and why the next shader wanting a plain per-draw block would have
    ///         met a refusal it could not fix in the shader. Both disappear into the declaration.
    ///     </para>
    ///     <para>
    ///         <b>The inference is gone rather than kept as a fallback</b>, because a fallback is the
    ///         same two-claims-one-number rule wearing a different name — every unmarked block would
    ///         still be answered by its set index, and the shader that wants a plain per-draw block
    ///         still could not say so. ⚠ What that costs is an <see cref="EffectData" /> baked before
    ///         the attribute existed: its blocks read as plain, and a host that offsets one is
    ///         refused at the write with a message naming both kinds. Loud, and in the safe
    ///         direction — never a shader quietly reading the wrong bytes — which is the property
    ///         that makes dropping the inference affordable at all.
    ///     </para>
    ///     <para>
    ///         ⚠ It has to be applied <i>here</i>, where both the set layout and
    ///         <see cref="Effect.Bindings" /> are built, because the two have to agree. A layout that
    ///         says dynamic and a plan that says plain writes the wrong descriptor type into a correct
    ///         layout — which the RHI refuses outright, and which is how the compute case above was
    ///         found rather than shipped.
    ///     </para>
    ///     <para>
    ///         Before this, <c>ForwardLightingRenderFeature</c> made a layout of its own that said
    ///         dynamic while the pipeline's said plain — incompatible, a validation error at the draw,
    ///         and a GPU fault. Nothing found it because the only device test drawing the forward pass
    ///         uses the clustered variant, which never statically uses set 3 and therefore need not
    ///         bind it at all.
    ///     </para>
    /// </remarks>
    static DescriptorKind KindOf(EffectBindingData binding) =>
        binding is { Kind: DescriptorKind.UniformBuffer, DynamicOffset: true }
            ? DescriptorKind.DynamicUniformBuffer
            : binding.Kind;

    /// <summary>The cache key for a set layout: everything a backend builds one from, and nothing else.</summary>
    /// <remarks>
    ///     The name is left out on purpose. Two shaders describing the same per-frame set differ only
    ///     in what they call it, and keying on the name would defeat the whole point of the cache
    ///     while looking like it worked.
    /// </remarks>
    internal static string Shape(DescriptorSetSlot slot, List<DescriptorBinding> bindings, int capacity) {
        var builder = new StringBuilder().Append((int)slot).Append('#').Append(capacity);

        foreach (var binding in bindings) {
            builder
                .Append('|')
                .Append(binding.Binding)
                .Append(':')
                .Append((int)binding.Kind)
                .Append(':')
                .Append((int)binding.Stages)
                .Append(':')
                .Append(binding.Count)
                // ⚠ In the key, because it is in the layout. A shadow map and a colour map are the
                // same kind, the same count and the same stages; leaving the sample type out means
                // the first of the two to be loaded hands its layout to the second, and a
                // comparison sampler is then bound through a filtering entry.
                .Append(':')
                .Append((int)binding.SampleType);
        }

        return builder.ToString();
    }

    /// <summary>
    ///     The interned key for one parameter, or null for a type the engine cannot hold.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Null rather than a key of some fallback type. A parameter the generator also skipped has
    ///         no C# spelling, so there is no call site that could set it — and inventing a
    ///         <c>ParameterKey&lt;byte[]&gt;</c> for it would put a name in the interning table that the
    ///         next assembly to generate bindings would collide with.
    ///     </para>
    ///     <para>
    ///         And no default, through the overload that declares none:
    ///         <see cref="EffectParameterData" /> is a name, a kind, an offset and a size, because
    ///         that is what a uniform block looks like from the outside. The initialiser
    ///         <c>var exposure: float = 1f</c> is in the shader's source and reaches the generated
    ///         binding instead. Passing zero here would claim it as this shader's declared default and
    ///         beat the binding to the intern table whenever an effect loads first — which, effects
    ///         being data-driven, is a load-order accident rather than a decision. See
    ///         <see cref="ParameterKeys.New{T}(string)" />.
    ///     </para>
    /// </remarks>
    static ParameterKey? KeyOf(EffectParameterData parameter) =>
        parameter.Kind switch {
            ShaderValueKind.Bool => ParameterKeys.New<bool>(parameter.Name),
            ShaderValueKind.Int => ParameterKeys.New<int>(parameter.Name),
            ShaderValueKind.Int2 => ParameterKeys.New<Int2>(parameter.Name),
            ShaderValueKind.Int3 => ParameterKeys.New<Int3>(parameter.Name),
            ShaderValueKind.Int4 => ParameterKeys.New<Int4>(parameter.Name),
            ShaderValueKind.UInt => ParameterKeys.New<uint>(parameter.Name),
            ShaderValueKind.Float => ParameterKeys.New<float>(parameter.Name),
            ShaderValueKind.Float2 => ParameterKeys.New<Vector2>(parameter.Name),
            ShaderValueKind.Float3 => ParameterKeys.New<Vector3>(parameter.Name),
            ShaderValueKind.Float4 => ParameterKeys.New<Vector4>(parameter.Name),
            ShaderValueKind.Matrix3x3 => ParameterKeys.New<Matrix3x3>(parameter.Name),
            ShaderValueKind.Matrix4x4 => ParameterKeys.New<Matrix4x4>(parameter.Name),
            ShaderValueKind.Double => ParameterKeys.New<double>(parameter.Name),
            _ => null
        };

    /// <summary>The interned permutation key one stored value names.</summary>
    /// <exception cref="InvalidOperationException">
    ///     The name is already interned as something else — a value key, or a permutation of another
    ///     type. Thrown rather than swallowed: it means generated code and a baked artefact disagree
    ///     about what a name means, and the alternative is a variant selected by a value nobody
    ///     wrote.
    /// </exception>
    static ParameterKey PermutationKeyOf(EffectPermutationValue permutation) =>
        permutation.Kind switch {
            ShaderValueKind.Int => ParameterKeys.NewPermutation(
                int.TryParse(permutation.DefaultValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0,
                permutation.Name
            ),
            ShaderValueKind.UInt => ParameterKeys.NewPermutation(
                uint.TryParse(permutation.DefaultValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0u,
                permutation.Name
            ),
            _ => ParameterKeys.NewPermutation(
                string.Equals(permutation.DefaultValue, "true", StringComparison.OrdinalIgnoreCase),
                permutation.Name
            )
        };
}
