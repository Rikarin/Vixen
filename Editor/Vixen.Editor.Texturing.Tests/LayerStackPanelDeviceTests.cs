// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.Texturing.Layers;
using Vixen.Graphics.Vulkan;
using Vixen.Ui;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>A layer stack becomes a picture in a panel, on a real adapter.</summary>
/// <remarks>
///     <para>
///         <b>The half of <a href="https://github.com/Rikarin/Vixen/issues/806">#806</a> a
///         device-free suite cannot settle.</b> The registration is worth nothing if the panel it
///         opens shows nothing, and "the document opened" is satisfied by a preview that evaluates
///         no plan at all. This runs the whole route — activate, select, run the verb, compile the
///         stack through the public <c>TextureGraphCompiler</c>, evaluate, upload, show — and reads
///         the texels back.
///     </para>
///     <para>
///         ⚠ <b>A real adapter or a loud skip.</b> Without one a headless run falls back to the Null
///         device on every platform and exits 0, and every assertion below would then be a claim
///         about an empty image. The adapter is named in every message and
///         <c>VIXEN_REQUIRE_VULKAN=1</c> turns the skip into a failure —
///         <c>TexturePreviewDeviceTests</c>' own arrangement, unchanged.
///     </para>
///     <para>
///         ⚠ <b>And the authored colour is <em>not</em> the channel default, which is the whole
///         instrument.</b> <c>LayerStackDocument.DefaultChannels</c> starts base colour at a mid
///         grey; a stack whose fill never reached the plan, a plan that never ran and an image nobody
///         wrote all produce a flat picture, and two of the three produce a plausible one. Three
///         ordered components that are not each other is a closed form no flat answer satisfies.
///     </para>
/// </remarks>
public class LayerStackPanelDeviceTests {
    [Fact]
    public void Opening_a_stack_bakes_it_and_the_pane_shows_the_map() {
        using var device = Open();
        using var fixture = new TexturingFixture(device);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, "Hull"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var document = Assert.IsType<LayerStackDocument>(fixture.Project.Documents.Single());

        // A quarter, a half and three quarters — ordered, distinct, and none of them the channel's
        // own default. 256² rather than the 1024² a new stack declares, because this bakes for real
        // and the oracle does not get better with sixteen times the texels.
        document.Document = Painted(256);

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var panel = fixture.Shell.Workspace.Open(TexturingModule.StackPanel);
        var view = Find<ImageView>(panel!);

        Assert.NotNull(view);
        Assert.NotNull(fixture.Graphics);
        Assert.NotEmpty(fixture.Graphics.Uploads);

        var picture = fixture.Graphics.Uploads[^1];

        Assert.Equal(256, picture.Width);
        Assert.Equal(256, picture.Height);

        // ⚠ The pane draws the number that was uploaded. Pixels that reached the host and a view
        // still showing zero is an empty pane with a green test behind it.
        Assert.Equal(picture.Image, view.Image);
        Assert.Equal(256, view.ImageWidth);

        var (red, green, blue) = (picture.Pixels[0], picture.Pixels[1], picture.Pixels[2]);

        Assert.True(
            red < green && green < blue,
            $"{Adapter(device)}: the baked base colour's first texel is ({red}, {green}, {blue}), and the "
            + "stack's one fill layer authored an ordered quarter/half/three-quarters. A flat picture is what "
            + "a plan that never ran, a fill that never reached it and an unwritten image all produce."
        );

        // A constant fill is constant: any variation means something else wrote into this image.
        for (var index = 0; index < picture.Pixels.Length; index += 4) {
            Assert.Equal(red, picture.Pixels[index]);
            Assert.Equal(green, picture.Pixels[index + 1]);
            Assert.Equal(blue, picture.Pixels[index + 2]);
        }
    }

    /// <summary>The status line says what was baked rather than what cannot be.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the assertion that separates the stack's pane from the graph's.</b>
    ///     <c>TextureGraphPreview</c> still evaluates a fixed checkerboard, and until
    ///     <a href="https://github.com/Rikarin/Vixen/issues/816">#816</a>
    ///     <c>TexturePreview.Describe</c> said "⚠ Not the wired graph — TextureGraphCompiler is
    ///     internal … (#738)". #738 is closed and that compiler is <c>public</c>; the graph's line
    ///     now names <a href="https://github.com/Rikarin/Vixen/issues/792">#792</a>, the missing
    ///     caller. The stack's pane compiles the real document, so its sentence must not be the
    ///     graph's — and this test is the tripwire: when the graph's pane is fixed, the two sentences
    ///     converge and the second assertion here is what says so.
    /// </remarks>
    [Fact]
    public void The_pane_says_it_compiled_the_stack_rather_than_a_base_layer() {
        using var device = Open();
        using var fixture = new TexturingFixture(device);

        using LayerStackPreview preview = new(fixture.Graphics!);

        var document = new LayerStackDocument(
            fixture.Project,
            LayerStackPanelTests.AddStack(fixture, "Hull"),
            fixture.Paths.Absolute("Assets/Hull" + LayerStackDocument.Extension)
        ) {
            Document = Painted(64)
        };

        var picture = preview.Evaluate(document);

        Assert.NotNull(picture.Image);
        Assert.Equal(1, preview.Evaluations);
        Assert.Contains("baseColor", picture.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("base layer", picture.Status, StringComparison.Ordinal);
        // ⚠ #792, not #738. The tripwire guards against the stack's pane falling back to the graph
        // pane's sentence, so it has to name a string that sentence actually contains — and the
        // sweep that corrected every stale "#738" out of the tree left this assertion looking for a
        // number no runtime string holds any more, which is a tripwire nothing can trip.
        Assert.DoesNotContain("#792", picture.Status, StringComparison.Ordinal);
    }

    /// <summary>⚠ Re-evaluating releases the picture it replaces: one live upload, however many runs.</summary>
    [Fact]
    public void Re_evaluating_gives_the_previous_picture_back() {
        using var device = Open();
        using var fixture = new TexturingFixture(device);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, "Hull"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));
        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        Assert.NotNull(fixture.Graphics);
        Assert.True(fixture.Graphics.Uploads.Count > 1, "the second open did not re-evaluate");

        // ⚠ Every upload but the live one, and the module's *two* previews share the counter — so the
        // graph pane must not have been evaluated here. Nothing in this test opens it.
        Assert.Equal(fixture.Graphics.Uploads.Count - 1, fixture.Graphics.Released);
    }

    /// <summary>And unloading the module gives the stack's last picture back too.</summary>
    /// <remarks>
    ///     The leak with no symptom: a plugin's registrations are undone by its scope and a plugin's
    ///     <em>textures</em> are registered with nothing. A second preview holding a second texture
    ///     is a second way to leak one.
    /// </remarks>
    [Fact]
    public void Unloading_gives_the_last_picture_back() {
        using var device = Open();
        using var fixture = new TexturingFixture(device);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, "Hull"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));
        Assert.NotNull(fixture.Graphics);
        Assert.NotEmpty(fixture.Graphics.Uploads);

        Assert.True(fixture.Host.Unload(TexturingModule.ModuleId));
        Assert.Equal(fixture.Graphics.Uploads.Count, fixture.Graphics.Released);
    }

    /// <summary>A plan's caution reaches the pane's sentence rather than stopping at the bake.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Why #801 was declined twice, closed here.</b> <c>TexturePlan.Check</c> has had a
    ///         third severity since <a href="https://github.com/Rikarin/Vixen/issues/692">#692</a> —
    ///         the plan bakes and does not draw what the graph describes — and every caution it
    ///         produced stopped at <c>TextureBake.Warnings</c>, which nothing in the editor or the
    ///         CLI read. A guard nobody reads is this repository's commonest defect, so the guard
    ///         landed with a reader.
    ///     </para>
    ///     <para>
    ///         <b>The caution is a real one and it is #692's own.</b> <c>Filters/Sharpen</c> loops to
    ///         8 texels at the base resolution and this mask effect asks for 32, so the picture is
    ///         sharpened by a quarter of what the stack says — which is exactly the class of defect
    ///         nothing anywhere used to mention.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_plans_caution_reaches_the_panes_sentence() {
        using var device = Open();
        using var fixture = new TexturingFixture(device);

        using LayerStackPreview preview = new(fixture.Graphics!);

        var stack = Painted(64);

        stack.Sets[0].Layers[0] = stack.Sets[0].Layers[0] with {
            Mask = new() {
                Source = LayerMaskSource.Constant,
                Value = 1f,
                Effects = [
                    new() {
                        Node = "Filters/Sharpen",
                        Values = { ["Radius"] = [32f] }
                    }
                ]
            }
        };

        var document = new LayerStackDocument(
            fixture.Project,
            LayerStackPanelTests.AddStack(fixture, "Hull"),
            fixture.Paths.Absolute("Assets/Hull" + LayerStackDocument.Extension)
        ) {
            Document = stack
        };

        var picture = preview.Evaluate(document);

        // It baked: a caution is a report about the picture and not a refusal of the plan.
        Assert.NotNull(picture.Image);

        Assert.Contains("Sharpen", picture.Status, StringComparison.Ordinal);
        Assert.Contains("32", picture.Status, StringComparison.Ordinal);

        // The instrument: a stack with no such effect says the same sentence without the caution, so
        // this cannot be passing on a pane that appends a warning whatever happened.
        var plain = new LayerStackDocument(
            fixture.Project,
            LayerStackPanelTests.AddStack(fixture, "Plain"),
            fixture.Paths.Absolute("Assets/Plain" + LayerStackDocument.Extension)
        ) {
            Document = Painted(64)
        };

        Assert.DoesNotContain("⚠", preview.Evaluate(plain).Status, StringComparison.Ordinal);
    }

    /// <summary>A texture-fill layer's imported picture is read out of the project and drawn.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><a href="https://github.com/Rikarin/Vixen/issues/818">#818</a>: the commonest
    ///         stack there is, and the pane used to answer it with a sentence.</b> A compiler must
    ///         not touch an <c>AssetDatabase</c> — it runs on every edit — so what crossed was the
    ///         reference, and nothing on the panel's side read it. Skipping the entry was never an
    ///         option: <c>TexturePlanEvaluator.Evaluate</c> refuses a plan whose external nothing
    ///         supplied, by throwing about an image index out of a panel build.
    ///     </para>
    ///     <para>
    ///         <b>The picture is asymmetric on purpose.</b> A flat fill would be drawn identically by
    ///         a pane that resolved the asset, one that uploaded a blank texture and one that filled
    ///         the image with a constant; the four corners of this one differ, so what is asserted is
    ///         that the <em>file's own</em> texels came back.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_texture_fill_layer_reads_the_picture_the_project_holds() {
        using var device = Open();
        using var fixture = new TexturingFixture(device);

        using LayerStackPreview preview = new(fixture.Graphics!);

        // A 2×2 whose top-left is red and whose bottom-right is blue, written as a real PNG through
        // the project's own codec and scanned in as an asset the database can resolve by path.
        Bitmap picture = new(2, 2, [
            255, 0, 0, 255, 0, 255, 0, 255,
            0, 0, 255, 255, 255, 255, 255, 255
        ]);

        Directory.CreateDirectory(Path.Combine(fixture.Paths.Assets, "Textures"));
        File.WriteAllBytes(
            Path.Combine(fixture.Paths.Assets, "Textures", "corners.png"),
            PngCodec.Encode(picture)
        );

        fixture.Project.Assets.Scan();

        var stack = Painted(2);

        stack.Sets[0].Layers[0] = stack.Sets[0].Layers[0] with {
            Fill = LayerFillSource.Texture,

            // ⚠ Restricted, because an empty channel list means *every* channel — so a texture fill
            // that named one picture would be refused six times for the six it did not.
            Channels = ["baseColor"],
            Textures = new Dictionary<string, string> { ["baseColor"] = "Assets/Textures/corners.png" }
        };

        var document = new LayerStackDocument(
            fixture.Project,
            LayerStackPanelTests.AddStack(fixture, "Hull"),
            fixture.Paths.Absolute("Assets/Hull" + LayerStackDocument.Extension)
        ) {
            Document = stack
        };

        // The instrument: the document really is the texture-fill stack, so a green run below is
        // about the resolution rather than about a constant fill that never needed one.
        Assert.Equal(LayerFillSource.Texture, document.Document.Sets[0].Layers[0].Fill);
        Assert.Single(document.Document.Sets[0].Layers[0].Textures);

        var shown = preview.Evaluate(document);

        Assert.True(shown.Image is not null, shown.Status);
        Assert.DoesNotContain("818", shown.Status, StringComparison.Ordinal);

        var uploaded = fixture.Graphics!.Uploads[^1];

        Assert.Equal(2, uploaded.Width);
        Assert.Equal(2, uploaded.Height);

        // The file's own corners, which is what separates "the asset was read" from "something drew
        // a picture". Bilinear at matching extents lands each texel on its own centre.
        Assert.True(
            uploaded.Pixels[0] > 200 && uploaded.Pixels[1] < 60,
            $"{Adapter(device)}: the first texel is ({uploaded.Pixels[0]}, {uploaded.Pixels[1]}, "
            + $"{uploaded.Pixels[2]}) and the imported picture's is red."
        );

        Assert.True(
            uploaded.Pixels[10] > 200 && uploaded.Pixels[8] < 60,
            $"{Adapter(device)}: the third texel is ({uploaded.Pixels[8]}, {uploaded.Pixels[9]}, "
            + $"{uploaded.Pixels[10]}) and the imported picture's is blue."
        );
    }

    /// <summary>A layer naming a picture the project has not got is a sentence, not a throw.</summary>
    /// <remarks>
    ///     ⚠ <b>The failure mode the resolution had to keep.</b> A preview runs on every edit, so
    ///     every way of not reading a file — deleted, never imported, a format nothing decodes — has
    ///     to come back as text under the pane. A throw here is a throw out of a panel build, which
    ///     is <a href="https://github.com/Rikarin/Vixen/issues/805">#805</a> one layer out.
    /// </remarks>
    [Fact]
    public void A_texture_fill_naming_nothing_the_project_holds_says_so_and_does_not_throw() {
        using var device = Open();
        using var fixture = new TexturingFixture(device);

        using LayerStackPreview preview = new(fixture.Graphics!);

        var stack = Painted(8);

        stack.Sets[0].Layers[0] = stack.Sets[0].Layers[0] with {
            Fill = LayerFillSource.Texture,
            Channels = ["baseColor"],
            Textures = new Dictionary<string, string> { ["baseColor"] = "Assets/Textures/nothing-here.png" }
        };

        var document = new LayerStackDocument(
            fixture.Project,
            LayerStackPanelTests.AddStack(fixture, "Hull"),
            fixture.Paths.Absolute("Assets/Hull" + LayerStackDocument.Extension)
        ) {
            Document = stack
        };

        var shown = preview.Evaluate(document);

        Assert.Null(shown.Image);
        Assert.Contains("nothing-here.png", shown.Status, StringComparison.Ordinal);
        Assert.Contains("not in this project's assets", shown.Status, StringComparison.Ordinal);
    }

    /// <summary>A stack whose one fill authors an ordered colour no default matches.</summary>
    /// <param name="side">How big to bake it.</param>
    /// <returns>The stack.</returns>
    static LayerStackAsset Painted(int side) {
        var stack = LayerStackDocument.Starter("Hull");

        stack.Sets[0].Layers[0].Values["baseColor"] = [0.25f, 0.5f, 0.75f, 1f];

        return stack with { BaseWidth = side, BaseHeight = side };
    }

    /// <summary>A device, or a loud skip — or, when one was required, a failure.</summary>
    static VulkanDevice Open() {
        if (VulkanDevice.TryCreate(new(), out var device, out var reason)) {
            return device!;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan device, so nothing here can be proved");

        throw new InvalidOperationException("unreachable");
    }

    static string Adapter(VulkanDevice device) =>
        $"{device.Adapter.Name} ({device.Adapter.Kind}, {device.Adapter.DriverVersion})";

    static T? Find<T>(UiElement element) where T : UiElement {
        if (element is T found) {
            return found;
        }

        foreach (var child in element.Children) {
            if (Find<T>(child) is { } inside) {
                return inside;
            }
        }

        return null;
    }
}
