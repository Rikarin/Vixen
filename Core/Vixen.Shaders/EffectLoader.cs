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

    readonly Dictionary<string, DescriptorSetLayoutHandle> layouts = new(StringComparer.Ordinal);

    /// <summary>The device these effects are created on.</summary>
    public IGraphicsDevice Device { get; } = device;

    /// <summary>How many distinct set layouts have been created.</summary>
    /// <remarks>Observable so a test can assert the sharing above actually happens.</remarks>
    public int LayoutCount => layouts.Count;

    /// <summary>Creates the effect one record describes.</summary>
    public Effect Load(EffectData data) {
        ArgumentNullException.ThrowIfNull(data);

        var key = data.ToKey();
        var sets = new DescriptorSetLayoutHandle[SetCount];

        for (var slot = 0; slot < SetCount; slot++) {
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
                new(binding.Name, binding.Set, binding.Binding, binding.Kind) {
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
                bindings.Add(new(binding.Binding, binding.Kind, binding.Stages, binding.Count));
            }
        }

        bindings.Sort(static (left, right) => left.Binding.CompareTo(right.Binding));

        var shape = Shape(slot, bindings);

        if (layouts.TryGetValue(shape, out var existing)) {
            return existing;
        }

        var description = new DescriptorSetLayoutDescription(slot, [.. bindings], $"{shaderName}.{slot}");
        description.Validate();

        var created = Device.CreateDescriptorSetLayout(description);
        layouts[shape] = created;
        return created;
    }

    /// <summary>The cache key for a set layout: everything a backend builds one from, and nothing else.</summary>
    /// <remarks>
    ///     The name is left out on purpose. Two shaders describing the same per-frame set differ only
    ///     in what they call it, and keying on the name would defeat the whole point of the cache
    ///     while looking like it worked.
    /// </remarks>
    static string Shape(DescriptorSetSlot slot, List<DescriptorBinding> bindings) {
        var builder = new StringBuilder().Append((int)slot);

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
