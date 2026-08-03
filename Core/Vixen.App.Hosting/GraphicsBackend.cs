// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.App;

/// <summary>A graphics API an application would like to be drawn with.</summary>
/// <remarks>
///     <para>
///         <b>A vocabulary for a preference, not a capability.</b> Naming a backend here says what
///         to <i>try</i>; whether it can be opened on the machine in front of you is answered at
///         boot, by the <see cref="IGraphicsBackend" /> the head installed. See
///         <see cref="GraphicsOptions.Backends" />.
///     </para>
///     <para>
///         It lives beside <see cref="GraphicsOptions" /> rather than in <c>Vixen.Graphics</c>
///         deliberately. <c>Vixen.Graphics</c> is the contract every backend implements, and a
///         contract that enumerated its own implementations would have to be edited to add one —
///         which is the coupling <see cref="IGraphicsBackend" /> exists to avoid. What this is, is
///         a configuration value: something a <c>.vxproj</c> setting, an <c>OnConfigure</c> line
///         and <c>--vixen-backend</c> all have to be able to spell.
///     </para>
/// </remarks>
public enum GraphicsBackend : byte {
    /// <summary>Not a backend.</summary>
    /// <remarks>
    ///     ⚠ <b>Zero is deliberately not a real answer.</b> It is what
    ///     <c>default(GraphicsBackend)</c>, a zeroed struct field and a failed parse all leave
    ///     behind, and every one of those is a mistake rather than a request for a particular API.
    ///     A chain that treated zero as "Vulkan" would turn a typo in a launch script into a silent
    ///     success on the developer's machine and a silent failure everywhere else.
    /// </remarks>
    Unknown = 0,

    /// <summary>Vulkan — the reference backend (ADR-001).</summary>
    Vulkan = 1,

    /// <summary>WebGPU, through Dawn or wgpu-native.</summary>
    /// <remarks>
    ///     Opens only where one of those libraries is installed, which is why it reports and moves
    ///     on rather than throwing. In the browser it is the <c>Vixen.Graphics.WebGPU.Browser</c>
    ///     binding over the same device.
    /// </remarks>
    WebGpu = 2,

    /// <summary>OpenGL — GL 4.5 core, or GLES 3.0/3.2.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Has to be first in the list to work at all.</b> A GL device draws into the
    ///         window's own default framebuffer, so the window must have been created for OpenGL —
    ///         and a window's graphics API is fixed when it is made, with the OpenGL and Vulkan
    ///         flags mutually exclusive. The platform reads this list to choose, and only the first
    ///         entry that wants a window of its own kind is consulted: <c>[OpenGl, Null]</c> works,
    ///         <c>[Vulkan, OpenGl, Null]</c> gets a Vulkan window and OpenGL then refuses. Falling
    ///         back <i>across</i> window APIs would mean destroying and recreating the window, which
    ///         nothing does yet.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>4.5 core or GLES 3.0, and nothing below.</b> <c>glClipControl</c> arrived in 4.5
    ///         and is what makes GL's clip space match Vulkan's; without it every shader this engine
    ///         compiles would need the fixup path only the GLES profiles carry. A 4.1 context is not
    ///         nearly-4.5, it is a different target, and selection refuses it by name.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not available on macOS.</b> Apple caps OpenGL at 4.1 and has deprecated it, and
    ///         SDL there builds Metal-backed windows that reject <c>SDL_GL_CreateContext</c>
    ///         outright. Linux and Windows are where this backend runs; per ADR-001 its wider job is
    ///         being the RHI's abstraction validator, which the tests do against a supplied
    ///         <c>IGlApi</c> and no context at all.
    ///     </para>
    /// </remarks>
    OpenGl = 3,

    /// <summary>The device that records a whole frame and draws none of it.</summary>
    /// <remarks>
    ///     A shipping backend, not only a test one — <c>docs/plan/17</c>'s dedicated server runs on
    ///     it, and it is what makes the renderer testable on a machine with no GPU. It never fails
    ///     to open, so it is the only sensible last entry in a chain.
    /// </remarks>
    Null = 4
}
