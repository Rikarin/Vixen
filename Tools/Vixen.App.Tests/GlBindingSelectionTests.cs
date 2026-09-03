// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics.OpenGL;
using Vixen.Platform;
using Xunit;

namespace Vixen.App.Tests;

/// <summary>Which context is asked for, and which of the two GL bindings the answer is loaded through.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The GLES half of <c>Vixen.Graphics.OpenGL</c> was unreachable from a head, and both
///         halves of the reason are asserted here.</b> <c>GraphicsHost</c> asked its window for one
///         context and it was 4.5 core — so <c>ProfileOf</c>'s <c>Es30</c> and <c>Es32</c> arms could
///         never fire — and then loaded whatever came back through <c>SilkGlApi</c>, the binding over
///         <c>libGL</c>, whatever profile it had just decided on.
///     </para>
///     <para>
///         <b>Two entry points rather than <c>Create</c>, because the rest of that path needs a
///         driver.</b> <c>GlDevice</c>'s constructor calls <c>glGetString</c> before it returns
///         anything, so a fake proc address that answers zero crashes rather than reporting — which
///         is GL's rule, not a shortcoming of the fake. What is decidable with no driver is which
///         request was made and which binding type was built, and that is what these check.
///     </para>
/// </remarks>
public sealed class GlBindingSelectionTests {
    /// <summary>Desktop core is asked for first, and a window that has it is asked once.</summary>
    [Fact]
    public void ADesktopWindowIsAskedForCoreAndNothingElse() {
        var source = new RecordingGlWindow(embeddedOnly: false);

        Assert.True(GraphicsHost.TryCreateContext(source, out var context, out var reason));
        Assert.NotNull(context);
        Assert.Null(reason);

        var request = Assert.Single(source.Requests);

        Assert.False(request.UseEmbedded);
        Assert.Equal((4, 5), (request.MajorVersion, request.MinorVersion));
    }

    /// <summary>
    ///     A window that has no desktop GL — a phone, ANGLE, a GLES-only Mesa — is asked for GLES 3.0
    ///     next, and gets it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the case that had no code at all.</b> Without the second request the whole
    ///     GLES path — <c>SilkGlesApi</c>, <c>EglContext</c>, every profile branch in
    ///     <c>GlslTranslator</c> and <c>GlProfiles</c> — is dead from a production head, whatever its
    ///     own tests say.
    /// </remarks>
    [Fact]
    public void AWindowWithNoDesktopGlIsAskedForGlesNext() {
        var source = new RecordingGlWindow(embeddedOnly: true);

        Assert.True(GraphicsHost.TryCreateContext(source, out var context, out _));
        Assert.NotNull(context);
        Assert.True(context.IsEmbedded);

        Assert.Equal(2, source.Requests.Count);
        Assert.False(source.Requests[0].UseEmbedded);
        Assert.True(source.Requests[1].UseEmbedded);
        Assert.Equal((3, 0), (source.Requests[1].MajorVersion, source.Requests[1].MinorVersion));
    }

    /// <summary>Core is tried before GLES, so a driver with both keeps <c>glClipControl</c>.</summary>
    [Fact]
    public void CoreIsPreferredOverGles() {
        var source = new RecordingGlWindow(embeddedOnly: false);

        Assert.True(GraphicsHost.TryCreateContext(source, out var context, out _));
        Assert.NotNull(context);
        Assert.False(context.IsEmbedded);
    }

    /// <summary>When neither is available, both refusals are in the reason.</summary>
    /// <remarks>
    ///     ⚠ Reporting only the second would name GLES on a machine that refused core for an
    ///     unrelated reason, which sends the reader after the wrong driver — the same argument
    ///     <c>GraphicsHost.Create</c> makes about joining every backend's refusal rather than the
    ///     last one's.
    /// </remarks>
    [Fact]
    public void NeitherContextLeavesBothRefusalsInTheReason() {
        var source = new RecordingGlWindow(embeddedOnly: false) { Refuse = true };

        Assert.False(GraphicsHost.TryCreateContext(source, out var context, out var reason));
        Assert.Null(context);
        Assert.NotNull(reason);

        Assert.Contains("core-refused", reason, StringComparison.Ordinal);
        Assert.Contains("gles-refused", reason, StringComparison.Ordinal);
    }

    /// <summary>A GLES profile loads through <c>libGLESv2</c>'s binding, not <c>libGL</c>'s.</summary>
    /// <remarks>
    ///     ⚠ <b>The defect this pins down is silent on a desktop and fatal on a phone.</b> The two
    ///     bindings are two generated classes over two libraries; loading an embedded context's
    ///     entry points out of <c>libGL</c> resolves against a library Android does not ship at all.
    ///     Nothing above <c>IGlApi</c> would have noticed, because the <c>GlProfile</c> handed to
    ///     <c>GlDevice</c> was the right one throughout.
    /// </remarks>
    [Theory]
    [InlineData(GlProfile.Es30)]
    [InlineData(GlProfile.Es32)]
    public void AGlesProfileLoadsTheGlesBinding(GlProfile profile) {
        using var api = (IDisposable)GraphicsHost.BindingFor(profile, _ => 0);

        Assert.IsType<SilkGlesApi>(api);
        Assert.Equal(profile, ((SilkGlesApi)api).Profile);
    }

    /// <summary>And a desktop profile still loads the desktop one.</summary>
    [Fact]
    public void TheDesktopProfileLoadsTheDesktopBinding() {
        using var api = (IDisposable)GraphicsHost.BindingFor(GlProfile.Core45, _ => 0);

        Assert.IsType<SilkGlApi>(api);
    }

    /// <summary>A window that answers <see cref="IGlContextSource" /> and records what it was asked.</summary>
    /// <param name="embeddedOnly">
    ///     Whether it refuses a desktop context, which is what a phone, ANGLE and a GLES-only Mesa
    ///     build all do.
    /// </param>
    sealed class RecordingGlWindow(bool embeddedOnly) : IGlContextSource {
        public List<GlContextRequest> Requests { get; } = [];

        /// <summary>Whether to refuse both, as a window made for Vulkan does.</summary>
        public bool Refuse { get; init; }

        public bool TryCreateGlContext(
            in GlContextRequest request,
            out IGlContext? context,
            out string? reason
        ) {
            Requests.Add(request);

            if (Refuse) {
                context = null;
                reason = request.UseEmbedded ? "gles-refused" : "core-refused";

                return false;
            }

            if (embeddedOnly && !request.UseEmbedded) {
                context = null;
                reason = "core-refused";

                return false;
            }

            context = new FakeGlContext(request);
            reason = null;

            return true;
        }
    }

    /// <summary>A context that resolves nothing, which is all these assertions need of one.</summary>
    sealed class FakeGlContext(GlContextRequest request) : IGlContext {
        public int SwapInterval { get; set; }

        public bool IsEmbedded => request.UseEmbedded;

        public int MajorVersion => request.MajorVersion;

        public int MinorVersion => request.MinorVersion;

        public nint GetProcAddress(string name) => 0;

        public void MakeCurrent() { }

        public void SwapBuffers() { }

        public void Dispose() { }
    }
}
