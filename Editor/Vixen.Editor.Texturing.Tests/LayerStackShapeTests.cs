// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections;
using System.Reflection;
using Vixen.Editor.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>What a <c>.vxlayers</c> is allowed to hold, and what it reads back as.</summary>
public class LayerStackShapeTests {
    /// <summary>The types a stack's shape may be made of, and nothing else.</summary>
    /// <remarks>
    ///     ⚠ <b><c>float[]</c> is on this list and <c>byte[]</c> is not, which is the whole of the
    ///     rule.</b> A colour is four numbers and a texel buffer is bytes; allowing the first and
    ///     refusing the second is what makes "and no pixels" checkable rather than a sentence in a
    ///     comment. A member added later whose type is not here fails this test and its author has
    ///     to say which of the two it is.
    /// </remarks>
    static readonly HashSet<Type> Allowed = [
        typeof(string), typeof(int), typeof(uint), typeof(float), typeof(bool), typeof(float[])
    ];

    /// <summary>Doc 48 Part 5: a stack holds layers, masks, anchors and parameters — and no pixels.</summary>
    /// <remarks>
    ///     ⚠ <b>The invariant the two-file split exists for, asserted over the type closure rather
    ///     than over one document.</b> A test that serialised a stack and looked for base64 would
    ///     pass on the day somebody added a <c>byte[] Pixels</c> that happened to be empty; this
    ///     walks every property of every record reachable from <c>LayerStackAsset</c> and refuses
    ///     the member itself.
    /// </remarks>
    [Fact]
    public void The_stack_shape_can_hold_no_pixels() {
        HashSet<Type> seen = [];
        List<string> refused = [];

        Walk(typeof(LayerStackAsset), seen, refused);

        Assert.True(
            refused.Count == 0,
            "A .vxlayers is a file people merge and a paint layer is not — doc 48 Part 5. These members could "
            + "carry texels:\n  " + string.Join("\n  ", refused)
        );

        // And the walk reached something, so a refactor that made the records unreachable does not
        // read as a pass. ⚠ "Verify the instrument first": this assertion is what a walk that has
        // gone blind fails on.
        Assert.Contains(typeof(LayerAsset), seen);
        Assert.Contains(typeof(MaskAsset), seen);
        Assert.Contains(typeof(ChannelAsset), seen);
    }

    /// <summary>A painted layer names a file, and the file is the only place pixels can be.</summary>
    [Fact]
    public void A_paint_layer_names_a_file_beside_the_stack() {
        Assert.Equal(".vxpaint", LayerPaint.Extension);
        Assert.Equal("Hero.Body.rust.vxpaint", LayerPaint.NameFor("Hero", "Body", "rust"));
        Assert.Equal("Hero.Body.rust.mask.vxpaint", LayerPaint.NameFor("Hero", "Body", "rust", mask: true));

        // ⚠ Separate files, and separate from the stack. A stack whose paint went into the same file
        // would make every stroke a whole-file merge conflict.
        Assert.NotEqual(LayerStackDocument.Extension, LayerPaint.Extension);
    }

    /// <summary>A stack written and read back is the stack that was written.</summary>
    [Fact]
    public void A_saved_stack_reads_back_as_the_stack_that_was_saved() {
        var stack = LayerStackDifferential.Stack();
        var yaml = LayerStackYaml.Write(stack);
        var read = LayerStackYaml.Read(yaml);

        Assert.Equal(stack.Name, read.Name);
        Assert.Equal(stack.BaseWidth, read.BaseWidth);
        Assert.Equal(stack.Seed, read.Seed);

        // ⚠ The whole tree, through the compiler rather than member by member. Two stacks that
        // compile to the same plan are the same stack for every purpose this document has, and a
        // hand-written comparison would be a third declaration of the shape that can go stale.
        var before = LayerStackCompiler.Compile(stack, stack.Sets[0]);
        var after = LayerStackCompiler.Compile(read, read.Sets[0]);

        Assert.NotNull(before.Plan);
        Assert.NotNull(after.Plan);
        LayerStackDifferential.AssertSamePlan(before.Plan, after.Plan);
    }

    /// <summary>A group's children survive the round trip, which is the one recursive member.</summary>
    [Fact]
    public void A_groups_children_survive_the_file() {
        var stack = LayerStackDifferential.Stack();
        var read = LayerStackYaml.Read(LayerStackYaml.Write(stack));
        var group = read.Sets[0].Layers.Single(layer => layer.Kind == LayerKind.Group);

        Assert.Equal(2, group.Children.Count);
        Assert.Equal("grime-fill", group.Children[0].Id);
        Assert.Equal(LayerBlendMode.Overlay, group.Children[0].Blend);
    }

    /// <summary>An anchor survives the round trip as the id it names.</summary>
    [Fact]
    public void An_anchor_survives_the_file() {
        var stack = LayerStackDifferential.Stack();
        var read = LayerStackYaml.Read(LayerStackYaml.Write(stack));
        var anchored = read.Sets[0].Layers.Single(layer => layer.Id == "anchored");

        Assert.Equal(LayerMaskSource.Anchor, anchored.Mask.Source);
        Assert.Equal("base", anchored.Mask.Anchor);
    }

    /// <summary>A new stack is one fill layer over the seven default channels.</summary>
    [Fact]
    public void A_new_stack_is_a_fill_over_the_default_channels() {
        var starter = LayerStackDocument.Starter("Hero");
        var set = Assert.Single(starter.Sets);

        Assert.Equal(7, set.Channels.Count);
        Assert.Single(set.Layers);

        // ⚠ Occlusion starts at white. A stack whose occlusion channel started at black would bake a
        // fully occluded surface, which reads as a lighting bug three subsystems away.
        var occlusion = set.Channels.Single(channel => channel.Usage == "occlusion");

        Assert.Equal([1f, 1f, 1f, 1f], occlusion.Default);

        var compilation = LayerStackCompiler.Compile(starter, set);

        Assert.Empty(compilation.Problems);
        Assert.Empty(compilation.Diagnostics);
        Assert.NotNull(compilation.Plan);
        Assert.Equal(7, compilation.Plan.Outputs.Length);
    }

    /// <summary>A <c>.vxlayers</c> written by the document reads back as what was written.</summary>
    [Fact]
    public void The_document_saves_and_opens_a_stack() {
        using var fixture = new TexturingFixture();

        var path = Path.Combine(fixture.Paths.Assets, "Hero" + LayerStackDocument.Extension);
        var written = new LayerStackDocument(fixture.Project, default, path) {
            Document = LayerStackDifferential.Stack()
        };

        written.Save();

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"), "the temporary the save moves from was left behind");

        var text = File.ReadAllText(path);

        Assert.DoesNotContain("\r\n", text, StringComparison.Ordinal);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);

        var read = new LayerStackDocument(fixture.Project, default, path);

        Assert.Empty(read.LoadDiagnostics);

        var before = LayerStackCompiler.Compile(written.Document, written.Document.Sets[0]);
        var after = LayerStackCompiler.Compile(read.Document, read.Document.Sets[0]);

        LayerStackDifferential.AssertSamePlan(before.Plan!, after.Plan!);
    }

    /// <summary>An unopened file opens as the starter stack rather than as nothing.</summary>
    [Fact]
    public void An_empty_file_opens_as_the_starter() {
        using var fixture = new TexturingFixture();

        var path = Path.Combine(fixture.Paths.Assets, "New" + LayerStackDocument.Extension);

        File.WriteAllText(path, LayerStackDocument.NewContents);

        var document = new LayerStackDocument(fixture.Project, default, path);

        Assert.Empty(document.LoadDiagnostics);
        Assert.Equal("New", document.Document.Name);
        Assert.Single(document.Document.Sets);
    }

    /// <summary>A file this build cannot read opens, and says why.</summary>
    /// <remarks>
    ///     ⚠ <b>Reported rather than thrown</b>, for <c>TextureGraphDocument</c>'s reason: a stack
    ///     this build cannot read has to open, or the panel that could show the problem is
    ///     unreachable. ⚠ And the value is <em>refused</em> rather than defaulted: a blend mode a
    ///     later build added would otherwise composite every layer that used it as a <c>Copy</c>,
    ///     which is a picture rather than an error.
    /// </remarks>
    [Fact]
    public void A_stack_this_build_cannot_read_opens_and_says_so() {
        using var fixture = new TexturingFixture();

        var path = Path.Combine(fixture.Paths.Assets, "Future" + LayerStackDocument.Extension);

        File.WriteAllText(
            path,
            "version: 2\nname: Future\nsets:\n  - name: S\n    layers:\n      - id: l\n        kind: Fill\n"
            + "        blend: Hologram\n"
        );

        var document = new LayerStackDocument(fixture.Project, default, path);
        var diagnostic = Assert.Single(document.LoadDiagnostics);

        Assert.Contains("Hologram", diagnostic, StringComparison.Ordinal);
        Assert.Contains("blend", diagnostic, StringComparison.Ordinal);
    }

    /// <summary>A member left at its default is not written, so a merge sees what somebody chose.</summary>
    [Fact]
    public void A_default_is_not_written() {
        var stack = LayerStackDocument.Starter("Hero");
        var text = LayerStackYaml.Write(stack);

        Assert.DoesNotContain("blend:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("projection:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("mask:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("children:", text, StringComparison.Ordinal);

        // And what was chosen is there.
        Assert.Contains("kind: Fill", text, StringComparison.Ordinal);
        Assert.Contains("baseColor", text, StringComparison.Ordinal);
    }

    static void Walk(Type type, HashSet<Type> seen, List<string> refused) {
        if (!seen.Add(type)) {
            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
            var member = property.PropertyType;

            if (Allowed.Contains(member) || member.IsEnum) {
                continue;
            }

            if (Element(member) is { } element) {
                if (Allowed.Contains(element) || element.IsEnum) {
                    continue;
                }

                if (element.Namespace?.StartsWith("Vixen.Editor.Texturing", StringComparison.Ordinal) == true) {
                    Walk(element, seen, refused);

                    continue;
                }

                refused.Add($"{type.Name}.{property.Name} is a collection of {element.Name}");

                continue;
            }

            if (member.Namespace?.StartsWith("Vixen.Editor.Texturing", StringComparison.Ordinal) == true) {
                Walk(member, seen, refused);

                continue;
            }

            refused.Add($"{type.Name}.{property.Name} is a {member.Name}");
        }
    }

    /// <summary>What one entry of a list or a dictionary holds, or null when it is neither.</summary>
    static Type? Element(Type type) {
        if (type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(type)) {
            return null;
        }

        if (type.IsArray) {
            return type.GetElementType();
        }

        if (!type.IsGenericType) {
            return null;
        }

        var arguments = type.GetGenericArguments();

        // A dictionary's key is a string in every member here; what could carry pixels is the value.
        return arguments[^1];
    }
}
