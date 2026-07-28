// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.Versioning;
using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Platform.Web;

/// <summary>How a graphics backend finds the canvas a <see cref="SurfaceHandle" /> names.</summary>
/// <remarks>
///     <para>
///         <b>This exists because a backend cannot ask this assembly.</b>
///         <c>docs/plan/00 § Layer discipline</c> keeps <c>Vixen.Graphics.*</c> from referencing
///         <c>Vixen.Platform</c>, so everything a swapchain gets is
///         <see cref="SurfaceHandle" />: two <see cref="nint" />s and a discriminant. A canvas has
///         no pointer to put in one — the browser addresses elements by CSS selector — so the
///         handle is a small integer and the selector is <em>derived</em> from it:
///     </para>
///     <code>[data-vixen-canvas="7"]</code>
///     <para>
///         which is what <c>emscripten_webgl_create_context</c> and
///         <c>canvas.getContext("webgpu")</c> both take. The attribute is stamped on the element
///         when the window is created, it never collides with the page's own ids, and it means a
///         backend that knows only the number can find the element — with this helper if it happens
///         to reference this assembly, and by string concatenation if it does not.
///     </para>
///     <para>
///         The format is part of the contract between this project and every browser backend.
///         Changing it is a breaking change for <c>Vixen.Graphics.WebGPU</c> and for
///         <c>Vixen.Graphics.OpenGL</c>'s WebGL2 profile, which is why it is stated here rather than
///         being an implementation detail of the JavaScript.
///     </para>
/// </remarks>
public static class WebCanvas {
    /// <summary>The attribute the platform stamps on every canvas it owns.</summary>
    public const string Attribute = "data-vixen-canvas";

    /// <summary>The CSS selector for a canvas handle.</summary>
    /// <param name="handle">The <see cref="SurfaceHandle.Handle" /> of a
    /// <see cref="SurfaceKind.Web" /> surface.</param>
    /// <returns>A selector matching exactly that canvas.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="handle" /> is not a canvas
    /// handle. Zero is <see cref="SurfaceHandle.None" />, which has no element.</exception>
    public static string SelectorFor(nint handle) {
        ArgumentOutOfRangeException.ThrowIfLessThan(handle, 1);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"[{Attribute}=\"{(long)handle}\"]"
        );
    }

    /// <summary>The selector for a surface, if it is a canvas.</summary>
    /// <param name="surface">The surface handle a window reported.</param>
    /// <param name="selector">The CSS selector.</param>
    /// <returns><see langword="false" /> if this is not a <see cref="SurfaceKind.Web" /> surface, or
    /// has nothing to present to.</returns>
    public static bool TryGetSelector(in SurfaceHandle surface, out string selector) {
        if (surface.Kind != SurfaceKind.Web || surface.Handle < 1) {
            selector = string.Empty;
            return false;
        }

        selector = SelectorFor(surface.Handle);
        return true;
    }
}

/// <summary>A canvas, as the thing a graphics backend presents to.</summary>
/// <remarks>
///     Its <see cref="PixelSize" /> is the canvas's backing store, which is the CSS box times
///     <c>devicePixelRatio</c> as the browser rounded it — read back rather than recomputed, because
///     being one pixel out is a swapchain that does not match its framebuffer.
/// </remarks>
[SupportedOSPlatform("browser")]
internal sealed class WebSurface(int canvas) : ISurface {
    /// <inheritdoc />
    public SurfaceHandle Handle { get; } = new(SurfaceKind.Web, Display: 0, Handle: canvas);

    /// <inheritdoc />
    public Int2 PixelSize => new(WebInterop.PixelWidth(canvas), WebInterop.PixelHeight(canvas));
}
