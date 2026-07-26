// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Platform;

/// <summary>Something a graphics backend can present to.</summary>
/// <remarks>
///     Separate from <see cref="IWindow" /> because the two do not always come together: a browser
///     canvas is a surface with no window around it, an offscreen render target is a window with no
///     surface, and a backend only ever needs this half.
/// </remarks>
public interface ISurface {
    /// <summary>The native handles, or <see cref="SurfaceHandle.None" /> when there is nothing to
    /// present to.</summary>
    SurfaceHandle Handle { get; }

    /// <summary>The surface's size in physical pixels, which is the size a swapchain is built
    /// at.</summary>
    /// <remarks>
    ///     Not the window's logical size. On a HiDPI display the two differ by the scale factor,
    ///     and confusing them renders a quarter of the window or four times too much of it.
    /// </remarks>
    Int2 PixelSize { get; }
}
