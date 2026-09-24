// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui;
using Vixen.Ui.Desktop;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>A frame recorded before any upload, on the backend where it used to fail from inside a bind (#1377).</summary>
/// <remarks>
///     ⚠ <b>The device half of <c>RecordBeforeUploadTests</c>, and the half that has a "before".</b>
///     The vertex ring is created by the first <c>Upload</c>, so a host that skipped it handed
///     <c>BindVertexBuffer</c> a handle naming nothing, and the Vulkan backend threw an
///     <see cref="ArgumentException" /> about a handle from the middle of a render graph — measured by
///     <c>InterfaceOverASceneDeviceTests</c> with the world renderer's prologue removed. What this
///     asserts is the exception the host now meets instead: an <see cref="InvalidOperationException" />
///     that names the missing call. Against the old renderer it is red, with the
///     <see cref="ArgumentException" /> as the actual.
/// </remarks>
[Collection("Vulkan")]
public sealed class UiRecordBeforeUploadDeviceTests {
    const int Side = Fixture.Side;

    [Fact]
    public void RecordingBeforeAnyUploadIsRefusedByNameOnTheDevice() {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
                Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
            }

            Assert.Skip(reason ?? "no Vulkan");
            return;
        }

        using var owned = fixture!;

        var list = new DrawList();
        list.BeginFrame();
        list.Add(new(DrawCommandKind.Rectangle, 16, 16, 64, 32, new Color4(1f, 0f, 1f, 1f), 0, 0));
        list.EndFrame();

        var cache = new GlyphFieldCache(new GlyphAtlas(64, 64));
        var geometry = new UiGeometryBuilder().Build(list, cache, new Rectangle(0, 0, Side, Side));

        Assert.NotEmpty(geometry.Indices);

        var renderer = new UiRenderer(
            owned.Device,
            UiShaderLibrary.Load(owned.Device),
            new Rendering.RenderOutput([PixelFormat.Rgba8UNorm])
        );

        owned.Owns(renderer.Dispose);

        var colour = owned.ColourTarget("ui-record-before-upload");
        Exception? thrown = null;

        owned.Graph.AddPass("ui-record-before-upload", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, new Color4(0f, 0f, 0f, 1f));
            pass.SideEffect();
            pass.Execute(context => {
                try {
                    renderer.Record(context.CommandList, geometry, new(Side, Side));
                } catch (Exception exception) {
                    // Caught here rather than around the render, so what is asserted is the renderer's
                    // own exception and not however the graph chooses to wrap one.
                    thrown = exception;
                }
            });
        });

        // ⚠ No `Upload` — that is the arrangement under test.
        owned.Render(colour, _ => { });

        Assert.NotNull(thrown);
        var refused = Assert.IsType<InvalidOperationException>(thrown);
        Assert.Contains("Upload", refused.Message, StringComparison.Ordinal);
    }
}
