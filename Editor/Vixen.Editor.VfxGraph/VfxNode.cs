// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.NodeGraph;
using Vixen.Vfx;

namespace Vixen.Editor.VfxGraph;

/// <summary>
///     What a VFX node contributes to the graph being built.
/// </summary>
/// <remarks>
///     <para>
///         <b>Blocks, not expressions.</b> A shader node emits a line of source; a VFX node adds an
///         <i>operation</i> to one of three lists, or names the renderer. That is the difference
///         between the two graphs, and it is the whole difference: everything else — the ports, the
///         typing, the ordering, the diagnostics — is the same framework.
///     </para>
///     <para>
///         <b>Parameters are numbers, not text.</b> A <see cref="VfxOperation" /> holds two
///         <c>Vector4</c>s, so a node reads its ports through
///         <see cref="NodeBinding.Value" /> rather than through the expression a shader node would
///         interpolate. That is why the framework hands over both forms.
///     </para>
/// </remarks>
public sealed class VfxGraphBuilder {
    internal VfxGraphBuilder() { }

    /// <summary>The spawners, in the order the graph produced them.</summary>
    public List<VfxSpawner> Spawners { get; } = [];

    /// <summary>The initializers.</summary>
    public List<VfxOperation> Initializers { get; } = [];

    /// <summary>The updaters.</summary>
    public List<VfxOperation> Updaters { get; } = [];

    /// <summary>How the particles are drawn, when a node has said.</summary>
    public VfxRenderer? Renderer { get; set; }

    /// <summary>The most particles that may be alive at once.</summary>
    /// <remarks>
    ///     A property of the effect rather than of any block, and the one number an author has to
    ///     choose: it is the memory budget, and the module refuses to guess it — see
    ///     <see cref="ParticleBuffer" />'s capacity policy.
    /// </remarks>
    public int Capacity { get; set; } = 1024;

    /// <summary>The custom attributes the graph declares.</summary>
    /// <remarks>
    ///     In slot order, which is the order the nodes that named them were walked. Appended through
    ///     <see cref="Custom" /> rather than directly, so two nodes naming one attribute get one slot.
    /// </remarks>
    public List<VfxCustomAttribute> Customs { get; } = [];

    /// <summary>
    ///     What a node found wrong while contributing, for the compiler to report against the graph.
    /// </summary>
    /// <remarks>
    ///     <b>Collected rather than thrown.</b> <c>Contribute</c> has no way to report — it is handed a
    ///     builder, not a diagnostic sink — and a node that threw would abandon the walk at the first
    ///     mistake, which is the thing <c>NodeGraphCompiler</c> deliberately does not do. So a problem
    ///     is left here and <c>VfxGraphCompiler.Finish</c> says it.
    /// </remarks>
    public List<string> Problems { get; } = [];

    /// <summary>
    ///     The attribute a ribbon output named, resolved to a slot once every node has contributed.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Deferred, because an output is not necessarily walked last.</b> A block nobody wired
    ///     contributes in insertion order, so a ribbon node dropped onto the canvas before the block
    ///     that writes its strip attribute would look up a name that has not been declared yet.
    ///     Resolving it after the walk makes the answer the same whatever order the graph was built in.
    /// </remarks>
    public string RibbonAttribute { get; set; } = "";

    /// <summary>The slot a named custom attribute occupies, declaring it if this is the first mention.</summary>
    /// <param name="name">What the author called it.</param>
    /// <param name="type">What it is made of, for the declaration this may be making.</param>
    /// <returns>Its slot, or zero when the name could not be used — in which case a problem was left.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Declared by being used.</b> There is no separate declaration node: an attribute
    ///         exists because something writes it, which is the same rule the built-in attributes
    ///         follow — <c>VfxCompiledGraph</c> derives their storage from what the operations touch
    ///         rather than from a list an author keeps in step.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two nodes naming one attribute with different widths is a problem, not a widening.</b>
    ///         Slot storage is allocated by lane count, so quietly promoting a one-lane attribute to
    ///         three would change what every other operation on it reads and writes — and the author
    ///         who typed the second width would never be told which one won.
    ///     </para>
    /// </remarks>
    public int Custom(string name, VfxAttributeType type) {
        var trimmed = (name ?? "").Trim();

        if (trimmed.Length == 0) {
            Problems.Add(
                "A custom attribute needs a name: it names a binding in the emitted shader, and a host "
                + "binds by that name. Type one into the node's Attribute setting."
            );

            return 0;
        }

        for (var slot = 0; slot < Customs.Count; slot++) {
            if (!string.Equals(Customs[slot].Name, trimmed, StringComparison.Ordinal)) {
                continue;
            }

            if (Customs[slot].Type != type) {
                Problems.Add(
                    $"`{trimmed}` is used as a {Customs[slot].Type} and as a {type}. One name is one slot and one "
                    + "slot is one width, so the two nodes cannot both be right — give one of them a different name."
                );
            }

            return slot;
        }

        Customs.Add(new(trimmed, type));

        return Customs.Count - 1;
    }

    /// <summary>The slot a named custom attribute already occupies, without declaring one.</summary>
    /// <param name="name">What the author called it.</param>
    /// <returns>Its slot, or zero when nothing declared it — in which case a problem was left.</returns>
    /// <remarks>
    ///     For a node that <i>reads</i> an attribute rather than writing one — a ribbon output. An
    ///     attribute nothing writes is storage full of zeroes, so every particle would be in the same
    ///     strip: one tangled ribbon rather than the many the author drew.
    /// </remarks>
    public int SlotOf(string name) {
        var trimmed = (name ?? "").Trim();

        for (var slot = 0; slot < Customs.Count; slot++) {
            if (string.Equals(Customs[slot].Name, trimmed, StringComparison.Ordinal)) {
                return slot;
            }
        }

        Problems.Add(
            trimmed.Length == 0
                ? "This output reads a custom attribute and has not been told which. Type its name into the node's "
                + "Attribute setting, and give the graph a block that writes it."
                : $"Nothing in this graph writes the custom attribute `{trimmed}`. Add a Set Custom or Random Custom "
                + "block that names it, or the attribute is zero for every particle."
        );

        return 0;
    }

    /// <summary>What a lane count means as an attribute type.</summary>
    /// <param name="lanes">One, three or four.</param>
    /// <param name="type">The type, when there is one.</param>
    /// <returns><see langword="true" /> if the count is one a custom attribute can have.</returns>
    /// <remarks>
    ///     ⚠ <b>Two is missing on purpose.</b> <c>VfxAttributeType</c> has no <c>Float2</c>: the
    ///     shader declares one buffer element type per attribute and the set is the one the built-ins
    ///     already needed. A node that asked for two would be silently rounded to one by
    ///     <c>VfxAttributes.Lanes</c>, which is the kind of quiet wrong answer this refuses instead.
    /// </remarks>
    public static bool TypeOfLanes(int lanes, out VfxAttributeType type) {
        switch (lanes) {
            case 1:
                type = VfxAttributeType.Float;

                return true;

            case 3:
                type = VfxAttributeType.Float3;

                return true;

            case 4:
                type = VfxAttributeType.Float4;

                return true;

            default:
                type = VfxAttributeType.Float;

                return false;
        }
    }
}

/// <summary>
///     A node of a VFX graph: something that contributes a block.
/// </summary>
public abstract class VfxNode : Node {
    /// <summary>Adds whatever this node contributes.</summary>
    /// <param name="builder">What is being built.</param>
    protected internal abstract void Contribute(VfxGraphBuilder builder);

    /// <summary>One port's value, as a vector, padded from however many lanes it has.</summary>
    /// <param name="port">The port's name.</param>
    /// <returns>Its value.</returns>
    /// <remarks>
    ///     Padded rather than refused, because a three-lane port feeding a <c>Vector4</c> parameter is
    ///     the normal case — the fourth component of a position is not a component the author has an
    ///     opinion about.
    /// </remarks>
    protected Vector4 Vector(string port) {
        var lanes = Binding.Value(port);

        return new(
            lanes.Length > 0 ? lanes[0] : 0f,
            lanes.Length > 1 ? lanes[1] : 0f,
            lanes.Length > 2 ? lanes[2] : 0f,
            lanes.Length > 3 ? lanes[3] : 0f
        );
    }

    /// <summary>One port's value, as a number.</summary>
    /// <param name="port">The port's name.</param>
    /// <returns>Its first lane, or zero.</returns>
    protected float Number(string port) {
        var lanes = Binding.Value(port);

        return lanes.Length > 0 ? lanes[0] : 0f;
    }

    /// <summary>One setting's text, trimmed.</summary>
    /// <param name="setting">The setting's name.</param>
    /// <returns>What the author typed.</returns>
    /// <remarks>
    ///     Trimmed here rather than at every reader, for the reason <c>CompositorNode.Text</c> gives:
    ///     a name with a trailing space is a name a lookup will not find, and nobody can see one.
    /// </remarks>
    protected string Text(string setting) => Binding.Text(setting).Trim();

    /// <summary>
    ///     The slot for the custom attribute this node names, declaring it if nothing has yet.
    /// </summary>
    /// <param name="builder">What is being built.</param>
    /// <param name="setting">Which setting holds the name.</param>
    /// <param name="lanes">Which port holds the lane count.</param>
    /// <returns>The slot, or zero when the node cannot be used — in which case a problem was left.</returns>
    /// <remarks>
    ///     Shared by the three blocks that write one, so all three spell the width the same way and a
    ///     lane count they disagree about is impossible.
    /// </remarks>
    protected int Custom(VfxGraphBuilder builder, string setting = "Attribute", string lanes = "Lanes") {
        ArgumentNullException.ThrowIfNull(builder);

        var count = (int)MathF.Round(Number(lanes));

        if (!VfxGraphBuilder.TypeOfLanes(count, out var type)) {
            builder.Problems.Add(
                $"A custom attribute cannot have {count.ToString(System.Globalization.CultureInfo.InvariantCulture)} "
                + "lanes. It is a float, a float3 or a float4 — one, three or four — because those are the element "
                + "types the emitted shader declares a buffer of."
            );

            return 0;
        }

        return builder.Custom(Text(setting), type);
    }
}
