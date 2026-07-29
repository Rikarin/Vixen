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

    /// <summary>The device these effects are created on.</summary>
    public IGraphicsDevice Device { get; } = device;

    /// <summary>How many distinct set layouts have been created.</summary>
    /// <remarks>Observable so a test can assert the sharing above actually happens.</remarks>
    public int LayoutCount => layouts.Count;

    /// <summary>
    ///     How many descriptors an unbounded binding in set 4 holds.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The shader says a table has no length and the <em>layout</em> has to say one anyway,
    ///         because a descriptor pool is sized from it. This is where that number comes from, and
    ///         it has to be the same one the <c>BindlessTable</c> was built with: the table hands out
    ///         indices up to its own capacity and the set has to have a descriptor at every one of
    ///         them.
    ///     </para>
    ///     <para>
    ///         ⚠ Four thousand and ninety-six rather than the device's ceiling, which is what a
    ///         <c>DescriptorSetLayoutDescription</c> falls back to when nobody says. A desktop driver
    ///         reports a million; reserving a million descriptors to hold a scene's worth of textures
    ///         is hundreds of megabytes of descriptor memory for a table that will hold a few
    ///         thousand. A project that genuinely needs more says so here and builds its table to
    ///         match.
    ///     </para>
    /// </remarks>
    public int BindlessCapacity { get; set; } = 4096;

    /// <summary>Creates the effect one record describes.</summary>
    public Effect Load(EffectData data) {
        ArgumentNullException.ThrowIfNull(data);

        var key = data.ToKey();
        var count = data.Bindings.Any(binding => binding.Set == DescriptorSetSlot.Bindless)
            ? BindlessSetCount
            : SetCount;

        var sets = new DescriptorSetLayoutHandle[count];

        for (var slot = 0; slot < count; slot++) {
            sets[slot] = LayoutOf(data, (DescriptorSetSlot)slot, key.ShaderName);
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
            Layout = Device.CreatePipelineLayout(new(sets, [.. Pushed(data)], key.ShaderName)),
            ConstantBufferSize = data.ConstantBufferSize,
            Parameters = parameters.ToImmutable(),
            Bindings = bindings.ToImmutable(),
            PushConstants = [.. Pushed(data).Select(range => new EffectPushConstant(range.Stages, range.Offset, range.Size))],
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

    /// <summary>Forgets every cached layout, without destroying anything.</summary>
    /// <remarks>
    ///     For a device that has gone away. The handles belonged to it and went with it; keeping them
    ///     would hand a new device something the old one made.
    /// </remarks>
    public void Clear() => layouts.Clear();

    /// <summary>
    ///     The layout for one set, created once per distinct shape.
    /// </summary>
    /// <remarks>
    ///     A set with no bindings still gets a layout rather than a null handle, because set indices
    ///     are positional: a shader that binds only the per-material set still binds it at index two,
    ///     and a pipeline layout that skipped the two empty ones would put it at index zero and every
    ///     descriptor set in the frame would land in the wrong place.
    /// </remarks>
    DescriptorSetLayoutHandle LayoutOf(EffectData data, DescriptorSetSlot slot, string shaderName) {
        List<DescriptorBinding> bindings = [];

        foreach (var binding in data.Bindings) {
            if (binding.Set == slot) {
                bindings.Add(new(binding.Binding, KindOf(binding), binding.Stages, binding.Count));
            }
        }

        bindings.Sort(static (left, right) => left.Binding.CompareTo(right.Binding));

        // Only where an unbounded binding could be, so the cache key of every other set is what it
        // has always been and the shapes a project already shares keep sharing.
        var capacity = slot == DescriptorSetSlot.Bindless ? BindlessCapacity : 0;
        var shape = Shape(slot, bindings, capacity);

        if (layouts.TryGetValue(shape, out var existing)) {
            return existing;
        }

        var description = new DescriptorSetLayoutDescription(slot, [.. bindings], $"{shaderName}.{slot}", capacity);
        description.Validate();

        var created = Device.CreateDescriptorSetLayout(description);
        layouts[shape] = created;
        return created;
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
    ///         <b>Graphics stages only, and the exception is not a special case.</b> "Per draw" is a
    ///         claim about draws; a compute dispatch has none, so a compute shader marking a block
    ///         <c>[PerDraw]</c> is using the set index for storage rather than saying its contents
    ///         change between draws. <c>BindlessProbe</c> is exactly that, and binds its block once.
    ///     </para>
    ///     <para>
    ///         ⚠ It has to be applied <i>here</i>, where both the set layout and
    ///         <see cref="Effect.Bindings" /> are built, because the two have to agree. A layout that
    ///         says dynamic and a plan that says plain writes the wrong descriptor type into a correct
    ///         layout — which the RHI refuses outright, and which is how the compute case above was
    ///         found rather than shipped.
    ///     </para>
    ///     <para>
    ///         <b>This is a convention read off a set index, and the shader ought to say it instead.</b>
    ///         Raven has no way to mark a block as bound at an offset; until it does, this is inferred.
    ///         The inference is safe in the direction that matters: getting it wrong is a refusal at
    ///         the write with a message naming both kinds, never a shader quietly reading the wrong
    ///         bytes.
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
        binding is { Set: DescriptorSetSlot.PerDraw, Kind: DescriptorKind.UniformBuffer }
        && (binding.Stages & ~ShaderStage.Compute) != ShaderStage.None
            ? DescriptorKind.DynamicUniformBuffer
            : binding.Kind;

    /// <summary>The cache key for a set layout: everything a backend builds one from, and nothing else.</summary>
    /// <remarks>
    ///     The name is left out on purpose. Two shaders describing the same per-frame set differ only
    ///     in what they call it, and keying on the name would defeat the whole point of the cache
    ///     while looking like it worked.
    /// </remarks>
    static string Shape(DescriptorSetSlot slot, List<DescriptorBinding> bindings, int capacity) {
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
                .Append(binding.Count);
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
