// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Editor.ShaderGraph;
using Vixen.Graphics.Null;
using Xunit;

namespace Tests;

/// <summary>
///     ⚠ What the preview renderer <em>holds</em>, which is a different question from what it draws.
/// </summary>
/// <remarks>
///     <para>
///         <a href="https://github.com/Rikarin/Vixen/issues/1111">#1111</a> asked for a sweep of
///         every <c>EffectLoader</c> caller for whether it frees what the loader took, and this is
///         one of the two in the editor. It did not: an <c>Effect</c>'s descriptor set layouts and
///         its pipeline layout belong to the loader and are shared by binding <em>shape</em> across
///         every preview it has ever compiled, so no per-entry teardown could have freed them —
///         <c>Release(Entry)</c> destroys a pipeline, two modules, a set, a buffer and a vertex
///         buffer, and there is no seventh line it could have had.
///     </para>
///     <para>
///         ⚠ <b>On the Null device, because a real one cannot answer this.</b>
///         <see cref="NullDevice.LiveResourceCount" /> is the tree's only count of objects created
///         and not destroyed; Vulkan exposes nothing of the sort, and a picture is exactly as
///         correct with a leak as without one. Nothing here reads a texel — the pictures are
///         <c>ShaderGraphPreviewDeviceTests</c>' business, and that file skips where there is no
///         adapter.
///     </para>
/// </remarks>
public sealed class ShaderGraphPreviewLeakTests {
    static NodeTypeRegistry Library() {
        var registry = new NodeTypeRegistry();

        NodeTypes.Register(registry);

        return registry;
    }

    /// <summary>Everything a preview took goes back when the renderer is disposed.</summary>
    /// <remarks>
    ///     ⚠ <b>The growth is asserted before the return is, and that is the half that makes this a
    ///     leak test.</b> A renderer that compiled nothing — a refused variant, a graph that did not
    ///     bind, an <c>Update</c> that never ran — returns to the same number it started at and
    ///     satisfies the last line perfectly. So the middle reading has to be higher, and the
    ///     compilation count has to say a variant was actually built.
    /// </remarks>
    [Fact]
    public void A_disposed_preview_renderer_gives_every_device_object_back() {
        using var device = new NullDevice(new());

        var before = device.LiveResourceCount;
        var registry = Library();
        var graph = new NodeGraphModel { Name = "Leaky" };
        var add = graph.Add("Math/Add");

        int held;

        using (var previews = new ShaderGraphPreviewRenderer(device, registry)) {
            previews.TryGet(graph, add, registry.Get(add.Type), out _);

            device.BeginFrame();
            previews.Update();
            device.EndFrame();

            Assert.Equal(0, previews.Refusals);
            Assert.True(previews.Created > 0, "no preview target was made, so nothing was taken and nothing is being tested");

            held = device.LiveResourceCount;

            Assert.True(held > before, $"the renderer took no device object at all ({before} before, {held} after)");
        }

        Assert.Equal(before, device.LiveResourceCount);
    }
}
