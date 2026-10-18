// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.AssetEditors.Importing;
using Vixen.Editor.Assets.Tests;
using Vixen.Editor.Testing;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Ui;
using Vixen.Ui.Desktop;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>
///     The texture editor's channel buttons and mip ladder, read back off the device texture the
///     preview image names â€” <a href="https://github.com/Rikarin/Vixen/issues/610">#610</a>.
/// </summary>
/// <remarks>
///     <para>
///         âš  <b><c>TexturePreviewWiringTests</c> asserts the bytes handed to a recording surface, and
///         that is not the picture.</b> A recording surface accepts anything and draws nothing, so
///         those tests cannot see an upload that was made and never submitted â€” which is exactly how
///         <c>ThumbnailSurface.Upload</c> once dropped every copy on the floor with every structural
///         claim true. Here the editor is given a real <see cref="ThumbnailSurface" /> on a real
///         Vulkan device, the frame is flushed where <c>EditorHost.Present</c> flushes it, and the
///         texture behind <c>Preview.Texture</c> is copied back.
///     </para>
///     <para>
///         <b>Closed-form, not eyeballed.</b> Hiding blue makes the blue byte of every texel exactly
///         zero while red and green keep the file's values; level three of an 8Ã—8 is exactly 1Ã—1. A
///         preview showing the source, the wrong level, or nothing at all fails one of those.
///     </para>
///     <para>
///         Skips when there is no Vulkan, and <c>VIXEN_REQUIRE_VULKAN=1</c> turns that into a failure.
///     </para>
/// </remarks>
public sealed class TexturePreviewDeviceTests {
    const int Side = 8;

    const byte Red = 200;
    const byte Green = 100;
    const byte Blue = 50;

    /// <summary>Blue hidden, then the ladder moved to its last level, each read back off the device.</summary>
    [Fact]
    public void Hiding_blue_and_choosing_a_level_reach_the_texture_the_preview_draws() {
        using var device = Open();

        VulkanDiagnostics.Reset();

        // Which adapter the picture came from, so a run on a software or translated device says so.
        Console.WriteLine($"Texture preview read back on '{device.Adapter.Name}' ({device.Adapter.Kind}).");

        var renderer = new UiRenderer(device, UiShaderLibrary.Load(device), new RenderOutput([PixelFormat.Bgra8UNorm]));

        using var surface = new ThumbnailSurface(device, renderer);
        using var session = EditorSession.Start();

        session.Editor.ThumbnailSurface = surface;

        var view = Open(session);

        view.SetChannel(TextureChannels.Blue, shown: false);
        session.Frames(1);

        var (width, height, pixels) = Read(device, surface, view.Preview.Texture);

        Assert.Equal((Side, Side), (width, height));

        for (var at = 0; at < pixels.Length; at += 4) {
            Assert.Equal(Red, pixels[at]);
            Assert.Equal(Green, pixels[at + 1]);
            Assert.Equal(0, pixels[at + 2]);
            Assert.Equal(byte.MaxValue, pixels[at + 3]);
        }

        view.SetMipLevel(3);
        session.Frames(1);

        Assert.Equal(3, view.MipLevel);

        var (levelWidth, levelHeight, level) = Read(device, surface, view.Preview.Texture);

        Assert.Equal((1, 1), (levelWidth, levelHeight));

        // A flat file averages to itself at every level, give or take the linear-light round trip,
        // and the hidden channel stays exactly hidden.
        Assert.InRange(level[0], Red - 2, Red + 2);
        Assert.InRange(level[1], Green - 2, Green + 2);
        Assert.Equal(0, level[2]);

        Assert.True(
            VulkanDiagnostics.ErrorCount == 0,
            "the preview's uploads produced validation errors, so the picture means nothing: "
            + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
        );
    }

    /// <summary>A device, or a skip â€” or, when one was required, a failure.</summary>
    static VulkanDevice Open() {
        if (VulkanDevice.TryCreate(new(), out var device, out var reason)) {
            return device!;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");

        throw new InvalidOperationException("unreachable");
    }

    /// <summary>Submits what the surface is holding, inside a frame, and copies one image's texture back.</summary>
    /// <remarks>
    ///     âš  <b><c>Flush</c> inside the frame and the uploads outside it</b>, which is where the editor
    ///     has each: the panel uploads from the application's update, and <c>EditorHost.Present</c>
    ///     drains the surface after <c>BeginFrame</c>.
    /// </remarks>
    static (int Width, int Height, byte[] Pixels) Read(VulkanDevice device, ThumbnailSurface surface, ulong image) {
        Assert.NotEqual(0ul, image);

        device.BeginFrame();
        surface.Flush();

        var texture = surface.TextureOf(image);

        Assert.True(texture.IsValid, "the surface has no texture for the image the preview names");

        var (width, height) = surface.ExtentOf(image);
        var bytes = width * height * 4;
        var readback = device.CreateBuffer(
            new(bytes, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "texture preview readback")
        );

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "texture preview readback")) {
            commands.Barrier(
                new BarrierGroup([], [new TextureBarrier(texture, ResourceState.ShaderRead, ResourceState.CopySource)])
            );

            commands.CopyTextureToBuffer(new TextureRegion(texture), new(width, height, 1), readback, 0);

            commands.Barrier(
                new BarrierGroup([], [new TextureBarrier(texture, ResourceState.CopySource, ResourceState.ShaderRead)])
            );

            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        var pixels = new byte[bytes];

        device.Read(readback, 0, pixels);
        device.Destroy(readback);

        return (width, height, pixels);
    }

    /// <summary>Writes a flat opaque PNG and opens it the way a double-click in the browser does.</summary>
    static TextureImportView Open(EditorSession session) {
        const string Relative = "Assets/flat.png";

        var absolute = session.Project.Paths.Absolute(Relative);
        var pixels = new byte[Side * Side * 4];

        for (var texel = 0; texel < pixels.Length; texel += 4) {
            pixels[texel] = Red;
            pixels[texel + 1] = Green;
            pixels[texel + 2] = Blue;
            pixels[texel + 3] = byte.MaxValue;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllBytes(absolute, MinimalPng.Write(Side, Side, pixels));

        session.Project.Assets.Scan();

        Assert.True(session.Project.Assets.TryGetByPath(Relative, out var entry));

        session.Editor.OpenAsset(entry.Guid);
        session.Frames(2);

        var view = Descendants(session.Document.Root).OfType<TextureImportView>().FirstOrDefault();

        Assert.NotNull(view);

        return view;
    }

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var descendant in Descendants(child)) {
                yield return descendant;
            }
        }
    }
}
