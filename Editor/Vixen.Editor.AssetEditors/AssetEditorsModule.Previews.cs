// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.AssetEditors.Shading;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.ShaderGraph;
using Vixen.Graphics;

namespace Vixen.Editor.AssetEditors;

/// <summary>The shader graph's node previews, built by the module that owns the shader graph (#1299).</summary>
/// <remarks>
///     <para>
///         <b>Decision (a) on #1299.</b> <c>EditorHost</c> built the <see cref="ShaderGraphPreviewRenderer" />
///         itself, registered its targets through a sink in the application, updated it inside its
///         frame and handed it to every open graph through an internal property — so the host and the
///         application both knew the shader graph existed. The module builds it now from what any
///         plugin is handed: <see cref="IEditorGraphics.Device" /> to draw on,
///         <see cref="IEditorGraphics.Show" /> to name a target, <see cref="PluginContext.OnFrame" /> to
///         draw inside the host's frame, and <see cref="PluginContext.OnDeviceLost" /> to give it all
///         back.
///     </para>
///     <para>
///         ⚠ <b>Built in <see cref="PluginContext.OnUpdate" />, because the device arrives after
///         activation</b> — the editor builds its plugin host in its constructor and is handed a device
///         when the window can present. ⚠ <b>Drawn in <see cref="PluginContext.OnFrame" />, not in
///         <c>OnUpdate</c></b>: the renderer records and submits a command list of its own, and one
///         recorded outside <c>BeginFrame</c>/<c>EndFrame</c> races the next frame's pool reset.
///     </para>
/// </remarks>
public sealed partial class AssetEditorsModule {
    ShaderGraphPreviewRenderer? previews;

    /// <summary>The renderer the open shader graphs preview through, once there is a device.</summary>
    internal ShaderGraphPreviewRenderer? Previews => previews;

    void ShaderGraphPreviews(PluginContext context, AssetEditorRegistry editors) {
        if (!context.Services.TryGet<IEditorGraphics>(out var graphics)) {
            return;
        }

        var project = context.Services.Require<EditorProject>();

        void Opened(EditorDocument document) {
            if (document is ShaderGraphDocument shader) {
                shader.PreviewSource = previews;
            }
        }

        void Hand(ShaderGraphPreviewRenderer? source) {
            previews = source;

            // Every graph that is already open as well as the later ones: a restored session opens
            // its tabs before the first frame, so the ones that matter most are the early ones.
            foreach (var document in project.Documents) {
                if (document is ShaderGraphDocument shader) {
                    shader.PreviewSource = source;
                }
            }
        }

        void Release() {
            if (previews is not { } going) {
                return;
            }

            // Every graph is told before the targets go, so nothing asks a disposed renderer.
            Hand(null);
            going.Dispose();
        }

        editors.Opened += Opened;

        context.OnUpdate(_ => {
            if (previews is null && graphics.Device is { } device) {
                Hand(new ShaderGraphPreviewRenderer(device, ShaderNodeLibrary.Create(), new ShownImages(graphics)));
            }
        });

        context.OnFrame(_ => previews?.Update());
        context.OnDeviceLost(_ => Release());

        context.OnUnload(() => {
            editors.Opened -= Opened;
            Release();
        });
    }

    /// <summary>A preview target's number, from <see cref="IEditorGraphics.Show" />.</summary>
    /// <remarks>
    ///     Zero while the host cannot show a view — before its surface exists — which the renderer
    ///     asks again about rather than keeping.
    /// </remarks>
    sealed class ShownImages(IEditorGraphics graphics) : IPreviewImages {
        readonly Dictionary<ulong, IEditorImage> shown = [];

        public ulong Register(TextureViewHandle view) {
            if (graphics.Show(view) is not { } image) {
                return 0;
            }

            shown[image.Image] = image;

            return image.Image;
        }

        public void Release(ulong image) {
            if (shown.Remove(image, out var held)) {
                held.Dispose();
            }
        }
    }
}
