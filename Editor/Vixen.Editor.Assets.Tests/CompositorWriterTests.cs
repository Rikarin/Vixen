// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Editor.Assets.Compositors;
using Vixen.Graphics;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.PostFx;
using Xunit;

namespace Tests;

/// <summary>
///     <see cref="CompositorWriter" />: the text it emits reads back as the asset that produced it.
/// </summary>
/// <remarks>
///     <para>
///         The round trip is the acceptance bar, and it is tested on the hardest document there is:
///         the <c>!StandardFrame</c> expansion at full knobs, because that is the one producer of a
///         document using every node kind, every stage state and every seat line at once — and
///         because the explode path this writer exists for writes exactly that document.
///     </para>
///     <para>
///         Structure is compared piecewise — records with arrays do not compare by value — and then
///         by the fixed point: writing what was read back must reproduce the text byte for byte,
///         which is the property that makes an exploded file diff-stable from its first save.
///     </para>
/// </remarks>
public class CompositorWriterTests {
    /// <summary>Every knob at its ceiling — the configuration that emits every node kind.</summary>
    static StandardFrameAsset AllOn => new() {
        Quality = QualityTier.Epic,
        Shadows = ShadowMode.Virtual,
        Gi = GiMode.Probes,
        Reflections = ReflectionsMode.Screen,
        Antialiasing = AntialiasingMode.TaaFxaa,
        Exposure = ExposureMode.Automatic,
        Particles = true
    };

    static GraphicsCompositorAsset Exploded(out IReadOnlyDictionary<object, string> notes) =>
        PostEffectFactory.Transform(new() { Game = AllOn }, out notes);

    static string[] Names(GraphicsCompositorAsset document) =>
        [.. Assert.IsType<SequenceAsset>(document.Game).Children.Select(child => child.Name)];

    [Fact]
    public void The_full_expansion_reads_back_structurally_identical() {
        var expanded = Exploded(out _);
        var text = CompositorWriter.Write(expanded);
        var reread = YamlSerializer.Parse<GraphicsCompositorAsset>(text);

        Assert.Equal(expanded.Version, reread.Version);
        Assert.Equal(expanded.Stages, reread.Stages);
        Assert.Equal(expanded.Resources, reread.Resources);
        Assert.Equal(Names(expanded), Names(reread));

        Assert.Equal(
            Assert.IsType<SequenceAsset>(expanded.Game).Children.Select(child => child.GetType()),
            Assert.IsType<SequenceAsset>(reread.Game).Children.Select(child => child.GetType())
        );

        // The fixed point: what was read back writes the same bytes, so the deep structure —
        // every node's members, not only the flat lists above — survived the trip.
        Assert.Equal(text, CompositorWriter.Write(reread));
    }

    /// <summary>The schema's corners the expansion does not reach: buffers, blocks, dispatches.</summary>
    [Fact]
    public void A_hand_built_document_reads_back_structurally_identical() {
        var document = new GraphicsCompositorAsset {
            Stages = [new() { Name = "Overlay", Blend = BlendPreset.AlphaBlend, Depth = DepthPreset.Disabled }],
            Resources = [
                new() { Name = "Half", Format = PixelFormat.Rg11B10Float, Scale = 0.5f },
                new() { Name = "Mask", Format = PixelFormat.R32UInt, Width = 256, Height = 128 }
            ],
            Buffers = [new() { Name = "Histogram", Size = 1024, Usage = BufferUsage.Storage | BufferUsage.CopySource }],
            ViewBlock = new() {
                Size = 96,
                Members = [new() { Name = "Vixen.ViewProjection", Offset = 0, Size = 64 }]
            },
            GpuDriven = new() { MaterialRecords = true },
            Game = new SequenceAsset {
                Name = "Frame",
                Children = [
                    new ComputeAsset {
                        Name = "Count",
                        Shader = "Histogram",
                        Writes = ["Mask"],
                        BufferWrites = ["Histogram"],
                        GroupsX = 8,
                        GroupsY = 4
                    },
                    new RenderPassAsset {
                        Name = "Main",
                        ColourTargets = ["Half"],
                        ClearColour = new(0.25f, 0.5f, 0.75f),
                        Children = [new SingleStageAsset { Name = "Overlay", View = "Camera", Stage = "Overlay" }]
                    },
                    new FullScreenAsset {
                        Name = "Resolve",
                        Shader = "Resolve",
                        ColourTargets = ["Mask"],
                        Reads = ["Half"],
                        ConstantBinding = 0,
                        Bindings = [
                            new() { Name = "source", Resource = "Half", Sampler = SamplerPreset.LinearClamp }
                        ]
                    },
                    new BufferUploadAsset { Name = "Seed", Buffer = "Histogram", Offset = 16 },
                    new BufferReadbackAsset { Name = "Reap", Buffer = "Histogram", Size = 64, Latency = 3 }
                ]
            }
        };

        var text = CompositorWriter.Write(document);
        var reread = YamlSerializer.Parse<GraphicsCompositorAsset>(text);

        Assert.Equal(document.Stages, reread.Stages);
        Assert.Equal(document.Resources, reread.Resources);
        Assert.Equal(document.Buffers, reread.Buffers);
        Assert.Equal(document.GpuDriven, reread.GpuDriven);
        Assert.Equal(document.ViewBlock!.Size, reread.ViewBlock!.Size);
        Assert.Equal(document.ViewBlock.Members, reread.ViewBlock.Members);
        Assert.Equal(text, CompositorWriter.Write(reread));

        var pass = Assert.IsType<SequenceAsset>(reread.Game).Children.OfType<RenderPassAsset>().Single();

        Assert.Equal(new Color3(0.25f, 0.5f, 0.75f), pass.ClearColour);
    }

    /// <summary>
    ///     An exploded file reads like sample 13, not like an object dump: a member the record
    ///     already defaults is never written down.
    /// </summary>
    [Fact]
    public void Members_at_their_defaults_are_omitted() {
        var text = CompositorWriter.Write(Exploded(out _));

        // Every node is enabled, which is the default; the caster nodes keep their own splitLambda
        // and biases; nothing multisamples. None of that belongs in the file.
        Assert.DoesNotContain("enabled:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("splitLambda:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sampleCount:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("constantBias:", text, StringComparison.Ordinal);

        // And what differs from the defaults is exactly what appears: the version statement first,
        // the tagged root, the caster stage's structural overrides.
        Assert.StartsWith("version: 2", text, StringComparison.Ordinal);
        Assert.Contains("game: !Sequence", text, StringComparison.Ordinal);
        Assert.Contains("depthClamp: true", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_version_is_written_even_at_its_default() {
        Assert.StartsWith("version: 2", CompositorWriter.Write(new()), StringComparison.Ordinal);
    }

    [Fact]
    public void The_expansions_notes_come_out_as_comments_above_what_they_explain() {
        var expanded = Exploded(out var notes);
        var text = CompositorWriter.Write(expanded, notes, "One-way; the knobs are gone.");

        Assert.StartsWith("# One-way; the knobs are gone.", text, StringComparison.Ordinal);

        // A resource's note sits directly above its entry, and a node's above its tag line.
        var lines = text.Split('\n');
        var hdr = Array.FindIndex(lines, line => line.TrimStart().StartsWith("- name: SceneHdr", StringComparison.Ordinal));

        Assert.True(hdr > 0, "the SceneHdr resource is in the text");
        Assert.Contains("#", lines[hdr - 1], StringComparison.Ordinal);
        Assert.Contains("What the scene is drawn into", text, StringComparison.Ordinal);
        Assert.Contains("Fog is light in the scene", text, StringComparison.Ordinal);

        // The notes wrap rather than running to one enormous line.
        Assert.All(lines.Where(line => line.TrimStart().StartsWith('#')), line => Assert.True(line.Length <= 100, line));
    }

    /// <summary>
    ///     The whole promise in one test: a seven-knob document and its exploded text build the same
    ///     frame — same nodes, same order, same stages — through the same builder and factory.
    /// </summary>
    [Fact]
    public void An_exploded_document_builds_identically_to_its_knobs() {
        var knobs = new GraphicsCompositorAsset { Game = AllOn };
        var exploded = PostEffectFactory.Transform(knobs, out var notes);
        var reread = YamlSerializer.Parse<GraphicsCompositorAsset>(CompositorWriter.Write(exploded, notes));

        using var fromKnobs = new RenderSystem();
        using var fromText = new RenderSystem();

        var original = Build(fromKnobs, knobs);
        var ejected = Build(fromText, reread);

        Assert.Equal(original.Nodes.Keys, ejected.Nodes.Keys);
        Assert.Equal(original.Stages.Keys, ejected.Stages.Keys);
    }

    static CompositorBuilder Build(RenderSystem system, GraphicsCompositorAsset document) {
        var builder = new CompositorBuilder(system);

        builder.Factories.Add(new PostEffectFactory());

        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, -1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        builder.Views["Camera"] = new("camera") { Position = Vector3.Zero, Frustum = new(view * projection) };
        builder.Build(document);

        return builder;
    }
}
