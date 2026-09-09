// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Platform;
using Vixen.Platform.Headless;

namespace Vixen.App.Tests;

/// <summary>
///     The headless platform with one thing changed: its windows have a surface a swapchain can be
///     built on.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing else in the tree can produce this state, which is why the defect it covers
///         survived.</b> <c>HeadlessSurface.Handle</c> is <c>SurfaceHandle.None</c> unconditionally,
///         so every test that has ever run the host has run it offscreen — and "offscreen" is the
///         one branch of <c>AppGraphics.RequestCapture</c> that writes a picture. A desktop run is
///         the other branch, and it had no coverage at all.
///     </para>
///     <para>
///         The surface reported is <see cref="SurfaceHandle.Windowless" /> rather than an invented
///         Win32 or Metal handle: it is the one kind that both answers <c>CanPresent</c> and names
///         no native object, so a device that looked at the handles would find zeros rather than a
///         pointer this process does not own. What the host reads is <c>CanPresent</c>, which is the
///         whole of what is being stood in for.
///     </para>
/// </remarks>
sealed class PresentingPlatform : IPlatform {
    readonly HeadlessPlatform inner;
    readonly List<IWindow> windows = [];

    /// <summary>Wraps a headless platform.</summary>
    /// <param name="inner">The platform to forward everything but the surface to.</param>
    public PresentingPlatform(HeadlessPlatform inner) {
        ArgumentNullException.ThrowIfNull(inner);

        this.inner = inner;
    }

    /// <inheritdoc />
    public string Name => inner.Name;

    /// <inheritdoc />
    public PlatformCapabilities Capabilities => inner.Capabilities;

    /// <inheritdoc />
    public IReadOnlyList<IWindow> Windows => windows;

    /// <inheritdoc />
    public IDisplayInfo Displays => inner.Displays;

    /// <inheritdoc />
    public SystemColorScheme ColorScheme => inner.ColorScheme;

    /// <inheritdoc />
    public SystemAccessibility Accessibility => inner.Accessibility;

    /// <inheritdoc />
    public IFileSystemHost FileSystem => inner.FileSystem;

    /// <inheritdoc />
    public IClipboard Clipboard => inner.Clipboard;

    /// <inheritdoc />
    public INativeDialogs Dialogs => inner.Dialogs;

    /// <inheritdoc />
    public ILifecycle Lifecycle => inner.Lifecycle;

    /// <inheritdoc />
    public IInputSource Input => inner.Input;

    /// <inheritdoc />
    public ITextInput TextInput => inner.TextInput;

    /// <inheritdoc />
    public IPowerInfo Power => inner.Power;

    /// <inheritdoc />
    public IProcessorTopology Processors => inner.Processors;

    /// <inheritdoc />
    public IWindow CreateWindow(in WindowOptions options) {
        var window = new PresentingWindow(inner.CreateWindow(options));
        windows.Add(window);

        return window;
    }

    /// <inheritdoc />
    public bool TryGetWindow(uint id, [NotNullWhen(true)] out IWindow? window) {
        foreach (var candidate in windows) {
            if (candidate.Id == id) {
                window = candidate;

                return true;
            }
        }

        window = null;

        return false;
    }

    /// <inheritdoc />
    public ReadOnlySpan<PlatformEvent> PumpEvents() => inner.PumpEvents();

    /// <inheritdoc />
    public bool TryOpenUrl(string url) => inner.TryOpenUrl(url);

    /// <inheritdoc />
    public void Dispose() => inner.Dispose();

    /// <summary>A headless window that says it can be presented to.</summary>
    sealed class PresentingWindow(IWindow inner) : IWindow {
        /// <inheritdoc />
        public uint Id => inner.Id;

        /// <inheritdoc />
        public string Title {
            get => inner.Title;
            set => inner.Title = value;
        }

        /// <inheritdoc />
        public Int2 ClientSize {
            get => inner.ClientSize;
            set => inner.ClientSize = value;
        }

        /// <inheritdoc />
        public Int2 FramebufferSize => inner.FramebufferSize;

        /// <inheritdoc />
        public Int2 Position {
            get => inner.Position;
            set => inner.Position = value;
        }

        /// <inheritdoc />
        public float DpiScale => inner.DpiScale;

        /// <inheritdoc />
        public WindowMode Mode {
            get => inner.Mode;
            set => inner.Mode = value;
        }

        /// <inheritdoc />
        public bool IsResizable {
            get => inner.IsResizable;
            set => inner.IsResizable = value;
        }

        /// <inheritdoc />
        public bool IsVisible => inner.IsVisible;

        /// <inheritdoc />
        public bool IsFocused => inner.IsFocused;

        /// <inheritdoc />
        public bool IsMinimised => inner.IsMinimised;

        /// <inheritdoc />
        public bool IsClosed => inner.IsClosed;

        /// <inheritdoc />
        public int DisplayIndex => inner.DisplayIndex;

        /// <inheritdoc />
        public CursorMode CursorMode {
            get => inner.CursorMode;
            set => inner.CursorMode = value;
        }

        /// <inheritdoc />
        public CursorShape CursorShape {
            get => inner.CursorShape;
            set => inner.CursorShape = value;
        }

        /// <inheritdoc />
        public ISurface Surface { get; } = new PresentingSurface(inner);

        /// <inheritdoc />
        public void Show() => inner.Show();

        /// <inheritdoc />
        public void Hide() => inner.Hide();

        /// <inheritdoc />
        public void Focus() => inner.Focus();

        /// <inheritdoc />
        public void Centre() => inner.Centre();

        /// <inheritdoc />
        public void RequestAttention() => inner.RequestAttention();

        /// <inheritdoc />
        public void SetIcon(ReadOnlySpan<byte> pixels, Int2 size) => inner.SetIcon(pixels, size);

        /// <inheritdoc />
        public void Dispose() => inner.Dispose();
    }

    /// <summary>The window's size, and a handle that answers <c>CanPresent</c>.</summary>
    sealed class PresentingSurface(IWindow window) : ISurface {
        /// <inheritdoc />
        public SurfaceHandle Handle => SurfaceHandle.Windowless;

        /// <inheritdoc />
        public Int2 PixelSize => window.FramebufferSize;
    }
}
