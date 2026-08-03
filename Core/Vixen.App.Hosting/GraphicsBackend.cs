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

    /// <summary>OpenGL — GL 4.5 core, GLES 3.0/3.2 or WebGL2.</summary>
    /// <remarks>
    ///     ⚠ <b>Not bootable by an app head today, and asking for it says so.</b> A GL device needs
    ///     entry points over a context that is already current on the calling thread, and no
    ///     <c>Vixen.Platform</c> implementation creates a GL context — there is no
    ///     <c>SDL_GL_CreateContext</c> path in <c>Vixen.Platform.Desktop</c> and nothing in
    ///     <c>WindowOptions</c> to ask for one. Per ADR-001 the backend exists as the RHI's
    ///     abstraction validator, exercised by tests against a supplied <c>IGlApi</c>.
    ///     <para>
    ///         It is in this list anyway, and that is the point: a chain that named it and silently
    ///         skipped it would be indistinguishable from one that tried and failed. Selection
    ///         reports the real reason, so the gap is visible from a log rather than from reading
    ///         this file.
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
