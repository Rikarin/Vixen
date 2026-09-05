// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Xunit;

namespace Tests;

/// <summary>
///     <c>PortKind.Image</c>: what it answers to the port model's two questions, and the wire it
///     refuses.
/// </summary>
/// <remarks>
///     <para>
///         <b>The refusal is the whole reason it is a new member rather than a second meaning for
///         <c>Texture</c>.</b> Doc 48 § Part 4 asks for <c>Image</c> and § D2 says why: a texture
///         graph's nodes read a <i>neighbourhood</i> of a raster and a shader graph's read one texel
///         of a bound resource, so a node vocabulary spanning both would be one where two thirds of
///         the nodes are invalid wherever you happen to be. Riding <c>Texture</c> would have made
///         that invisible to the type system: <c>PortFilter</c> would offer a shader graph's sampler
///         when a wire is dropped off a generation node's output, and the wire would be made.
///     </para>
///     <para>
///         Nothing here needs a device, a document or a registered library, because none of it is
///         about pixels — <c>PortKinds</c> is arithmetic over an enum and <c>PortFilter</c> is
///         arithmetic over a port list.
///     </para>
/// </remarks>
public class PortKindImageTests {
    static PortDefinition Port(string name, PortDirection direction, PortKind kind) =>
        new(name, direction, kind);

    static NodeTypeDefinition Type(string path, params PortDefinition[] ports) =>
        new(path, [.. ports], static () => new TestConstantNode());

    [Fact]
    public void An_image_is_neither_a_vector_nor_a_number_typed_into_a_box() {
        // ⚠ Both zero, and they are two different questions with the same answer here. `Lanes` is
        // "how wide is this value in the emitted source" — a dispatch over a storage image is not an
        // expression with lanes. `Fields` is "how many boxes does an author type into" — there is no
        // literal raster, so an unconnected image input is a hole a source node fills.
        Assert.Equal(0, PortKinds.Lanes(PortKind.Image));
        Assert.Equal(0, PortKinds.Fields(PortKind.Image));
        Assert.False(PortKinds.IsVector(PortKind.Image));

        // The pair it belongs with, against the pair it does not: `Bool`, `Int` and `Dynamic` also
        // occupy no lane and still take one box, which is the mistake that left every maths node in
        // the shader graph showing the word "Dynamic" where its numbers should have been.
        Assert.Equal(0, PortKinds.Fields(PortKind.Texture));
        Assert.Equal(0, PortKinds.Fields(PortKind.Flow));
        Assert.Equal(1, PortKinds.Fields(PortKind.Dynamic));
    }

    [Fact]
    public void An_image_port_takes_an_image_and_nothing_else() {
        Assert.True(PortKinds.Accepts(PortKind.Image, PortKind.Image));

        // The connection doc 48 says would otherwise be permitted, in both directions.
        Assert.False(PortKinds.Accepts(PortKind.Image, PortKind.Texture));
        Assert.False(PortKinds.Accepts(PortKind.Texture, PortKind.Image));
        Assert.False(PortKinds.Accepts(PortKind.Image, PortKind.Sampler));
        Assert.False(PortKinds.Accepts(PortKind.Sampler, PortKind.Image));

        // And it is not a vector either way, so the widening rule never reaches it.
        Assert.False(PortKinds.Accepts(PortKind.Image, PortKind.Float4));
        Assert.False(PortKinds.Accepts(PortKind.Float, PortKind.Image));
    }

    [Fact]
    public void A_dynamic_port_does_not_widen_to_an_image() {
        // Resolution is `Max` over the *vector* kinds, and `Image` is a larger number than every one
        // of them — so a `Resolve` that stopped gating on `IsVector` would silently make every
        // unconnected dynamic node an image. It does not.
        Assert.Equal(PortKind.Float3, PortKinds.Resolve([PortKind.Image, PortKind.Float3]));
        Assert.Equal(PortKind.Float, PortKinds.Resolve([PortKind.Image]));
    }

    [Fact]
    public void Search_to_create_from_an_image_output_refuses_a_shader_graphs_sampler() {
        // A wire dragged off a generation node's output is looking for an input that could take it.
        var filter = new PortFilter(PortKind.Image, PortDirection.Input);

        Assert.False(filter.Accepts(Port("Texture", PortDirection.Input, PortKind.Texture)));
        Assert.False(filter.Accepts(Port("Sampler", PortDirection.Input, PortKind.Sampler)));

        // ⚠ And not through the dynamic wildcard either: a dynamic port takes any *vector* and an
        // image is not one, so the escape hatch that makes `Lerp` offered for a colour is shut here.
        Assert.False(filter.Accepts(Port("A", PortDirection.Input, PortKind.Dynamic)));

        Assert.True(filter.Accepts(Port("In", PortDirection.Input, PortKind.Image)));
    }

    [Fact]
    public void The_create_menu_offers_only_the_texture_graphs_nodes_for_an_image_wire() {
        // Two libraries in one registry, which is the arrangement that makes the mistake possible:
        // an editor host registers whatever assemblies it loaded, and nothing but the port kind
        // stops a shader graph's node being offered inside a texture graph.
        var registry = new NodeTypeRegistry();

        registry.Add(
            Type(
                "Shader/Sample 2D",
                Port("Texture", PortDirection.Input, PortKind.Texture),
                Port("Sampler", PortDirection.Input, PortKind.Sampler),
                Port("Out", PortDirection.Output, PortKind.Float4)
            )
        );

        registry.Add(
            Type(
                "Texture/Blur",
                Port("In", PortDirection.Input, PortKind.Image),
                Port("Out", PortDirection.Output, PortKind.Image)
            )
        );

        var results = NodeSearch.Rank(registry, "", new PortFilter(PortKind.Image, PortDirection.Input));
        var offered = Assert.Single(results);

        Assert.Equal("Texture/Blur", offered.Type.Path);
        Assert.Equal("In", offered.Port);
    }

    [Fact]
    public void An_image_field_declares_an_image_port() {
        var registry = new NodeTypeRegistry();
        Vixen.Editor.NodeGraph.Tests.NodeTypes.Register(registry);

        var filter = registry.Get("Test/Image Filter");

        // The declared type *is* the port's kind — the generator reads the field and nothing else,
        // so a kind with no field type would be a kind no node could ever declare.
        Assert.Equal(PortKind.Image, filter.Port("In", PortDirection.Input)!.Kind);
        Assert.Equal(PortKind.Image, filter.Port("Out", PortDirection.Output)!.Kind);
        Assert.Equal(PortKind.Image, registry.Get("Test/Image Source").Port("Out", PortDirection.Output)!.Kind);
    }

    [Fact]
    public void An_image_port_has_no_inspector_row_to_make() {
        // The contract `NodePortMember` documents: callers gate on `Fields` and this throws for
        // anything that answers zero. An image answering `float` here would put a box of digits
        // beside a socket that carries a raster.
        var thrown = Assert.Throws<ArgumentException>(static () => NodePortMember.TypeOf(PortKind.Image));

        Assert.Contains("Image", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_kind_answers_both_questions_without_being_listed_anywhere() {
        // ⚠ The instrument check. `PortKind` is closed and `Fields` ends in `_ => Lanes(kind)`, so a
        // member added without a thought falls through to zero rather than to an exception — which is
        // right for `Image` and would be silently wrong for a kind that wanted a box. This asserts
        // the fall-through is a decision by pinning every member's pair, so the next member added has
        // to come back here and say which side it is on.
        Dictionary<PortKind, (int Lanes, int Fields)> expected = new() {
            [PortKind.None] = (0, 0),
            [PortKind.Bool] = (0, 1),
            [PortKind.Int] = (0, 1),
            [PortKind.Float] = (1, 1),
            [PortKind.Float2] = (2, 2),
            [PortKind.Float3] = (3, 3),
            [PortKind.Float4] = (4, 4),
            [PortKind.Texture] = (0, 0),
            [PortKind.Sampler] = (0, 0),
            [PortKind.Dynamic] = (0, 1),
            [PortKind.Flow] = (0, 0),
            [PortKind.Image] = (0, 0)
        };

        var members = Enum.GetValues<PortKind>();

        Assert.Equal(expected.Count, members.Length);

        foreach (var kind in members) {
            Assert.True(expected.TryGetValue(kind, out var pair), $"{kind} is not accounted for.");
            Assert.Equal(pair.Lanes, PortKinds.Lanes(kind));
            Assert.Equal(pair.Fields, PortKinds.Fields(kind));
        }
    }
}
