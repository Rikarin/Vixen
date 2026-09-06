// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Xunit;

namespace Tests;

/// <summary>
///     <a href="https://github.com/Rikarin/Vixen/issues/775">#775</a> as two tripwires: the evaluator
///     drives the device's frame loop, nothing may evaluate a plan inside a host frame, and nothing
///     says so or checks.
/// </summary>
/// <remarks>
///     <para>
///         <b>What was measured, and it is worse than the issue says.</b>
///         <c>TexturePlanEvaluator.Run</c> calls <c>BeginFrame</c>, <c>EndFrame</c> and
///         <c>WaitIdle</c> itself. On the Vulkan backend a nested <c>BeginFrame</c> waits every
///         queue's fence for the <em>current</em> slot, runs that slot's retiring actions, and resets
///         every command pool filed under it — the pool the host's in-flight lists for this very
///         frame were allocated from. The inner <c>EndFrame</c> then signals each queue's fence for
///         that slot and <em>advances <c>frame</c></em> (<c>VulkanDevice.EndFrame</c>), so the host's
///         own <c>EndFrame</c> signals a different slot's fence and its next <c>BeginFrame</c> waits
///         on one nothing signalled. #775 describes the reset pool; the slot/fence desynchronisation
///         is the half it does not, and it outlives the frame it happened in.
///     </para>
///     <para>
///         ⚠ <b>The issue's blocker is confirmed at the interface and refuted at the backend.</b>
///         There is genuinely nothing on <see cref="IGraphicsDevice" /> a caller can ask — the four
///         frame members are <c>BeginFrame</c>, <c>EndFrame</c>, <c>FrameCount</c> and
///         <c>FramesInFlight</c>, and <c>FrameCount</c>'s own remarks forbid deriving it ("some
///         increment on <c>BeginFrame</c> and some on <c>EndFrame</c>"). But the backend that
///         actually corrupts already tracks the bit: <c>VulkanDevice</c> has a private
///         <c>recording</c> field, set in <c>BeginFrame</c> and cleared in <c>EndFrame</c>, and
///         <c>Retire</c> already reads it to decide which slot a destroy belongs to. So the
///         expensive option #775 lists third — "something on <c>IGraphicsDevice</c> a caller can
///         ask" — is one property on the one backend where the failure is real, and a stored
///         <c>bool</c> on the three where it is not. That is a materially cheaper answer than the
///         issue assumes, and it is the finding this file exists to record.
///     </para>
///     <para>
///         <b>Both tests below are tripwires and both name the issue that changes them.</b> They
///         assert a limitation, which is only worth doing when the assertion says what would remove
///         it: the day a device can be asked, or the day evaluating inside a frame is refused, these
///         go red and #775 is what closes them.
///     </para>
/// </remarks>
public class TextureFrameLoopTests {
    /// <summary>Every member of the device contract whose name is about frames, today.</summary>
    /// <remarks>
    ///     ⚠ <b>An exact equality over a surface this slice does not own, deliberately.</b> That
    ///     shape has gone red on a merge five times in this workstream and every one of them was a
    ///     roll call pretending to be a definition. This one is the opposite: it is a
    ///     <em>tripwire</em>, its whole purpose is to fail when the set grows, and the failure
    ///     message says which issue the growth belongs to.
    /// </remarks>
    static readonly string[] FrameSurface = [
        "BeginFrame",
        "EndFrame",
        "FrameCount",
        "FramesInFlight",
        "IsFrameOpen"
    ];

    /// <summary>
    ///     ⚠ The device can be asked whether a frame is open — #775's blocker, since removed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This was the claim the issue rested on, and it is no longer true.</b> A caller that
    ///         wants to be safe — the plugin's preview pane, a thumbnail pump, a render feature's
    ///         extraction — had nothing to branch on: it could not ask, and it could not derive the
    ///         answer from <c>FrameCount</c>, whose contract says only that two reads inside one
    ///         frame agree. <see cref="IGraphicsDevice.IsFrameOpen" /> is the member that closed it,
    ///         and this test now holds the surface at five names rather than four.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Kept as a tripwire rather than deleted, and the name is now load-bearing.</b> A
    ///         backend that answered <see langword="false" /> unconditionally would satisfy every
    ///         caller and protect nobody, which is why the contract has no default implementation —
    ///         see <see cref="IGraphicsDevice.IsFrameOpen" />'s own remarks and
    ///         <see cref="Every_device_tracks_whether_a_frame_is_open" /> below.
    ///     </para>
    ///     <para>
    ///         <b>The instrument: the set is read off the interface rather than counted.</b> A
    ///         reflection call that came back empty would make an <c>Assert.Equal</c> against an
    ///         empty expectation pass, so the four names are spelled out and the comparison is an
    ///         equality in both directions.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Nothing_on_the_graphics_device_can_be_asked_whether_a_frame_is_open() {
        var members = typeof(IGraphicsDevice)
            .GetMembers()

            // A property is a member and its getter is another one, and `get_FrameCount` is not a
            // second thing a caller can ask.
            .Where(member => member is not MethodInfo { IsSpecialName: true })
            .Select(member => member.Name)
            .Where(name => name.Contains("Frame", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(members);

        Assert.True(
            FrameSurface.SequenceEqual(members, StringComparer.Ordinal),
            $"IGraphicsDevice's frame surface is now [{string.Join(", ", members)}] rather than "
            + $"[{string.Join(", ", FrameSurface)}]. If what was added answers 'is a frame open', this test has "
            + "done its job: https://github.com/Rikarin/Vixen/issues/775 is the issue, TexturePlanEvaluator is "
            + "the caller that asks, and a member that answers a different question needs its own test."
        );
    }

    /// <summary>
    ///     ⚠ A plan evaluated inside a host's own frame is refused — #775's trap, now a throw.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The other half of the issue, and the half that is a behaviour rather than a
    ///         surface.</b> This used to assert the opposite: the nesting was accepted in silence,
    ///         the second <c>BeginFrame</c> taken as an ordinary one, and the only evidence was the
    ///         frame counter advancing twice across one host frame. What made it a trap rather than
    ///         a rule was that a caller could not have checked either — see the test above.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Asserted on the Null device, where the nesting is harmless, and that is the
    ///         point.</b> On Vulkan the same two calls reset the command pools of the slot the caller
    ///         is recording into and leave its fences a slot behind for the rest of the session, so
    ///         no test could safely demonstrate the damage — which is precisely the argument for a
    ///         refusal rather than a comment saying not to. It also means this device must track the
    ///         bit honestly rather than answer <see langword="false" />, because every test of a
    ///         caller's frame discipline runs here.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The frame counter is read after the refusal, and that is the instrument.</b> A
    ///         guard that threw <em>after</em> the evaluator had opened its own frame would satisfy
    ///         an <c>Assert.Throws</c> and leave the caller's slot already reset — the damage done
    ///         and reported. Nothing may have advanced.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Evaluating_a_plan_inside_a_host_frame_is_refused_before_anything_is_recorded() {
        using var device = new NullDevice(new() { Record = true });

        var source = device.CreateTexture(
            new(
                PixelFormat.Rgba8UNorm,
                16,
                16,
                TextureUsage.Sampled | TextureUsage.CopyDestination,
                Name: "frame loop source"
            )
        );

        var plan = Plan();

        using var evaluator = new TexturePlanEvaluator(device);

        // The host opens its frame, exactly as EditorHost.Present does before it builds a pane.
        device.BeginFrame();

        var inside = device.FrameCount;

        var refused = Assert.Throws<InvalidOperationException>(
            () => evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = source })
        );

        Assert.Contains("inside a frame", refused.Message, StringComparison.Ordinal);

        Assert.True(
            device.FrameCount == inside,
            $"The refusal happened after the evaluator had advanced the frame counter by "
            + $"{device.FrameCount - inside}. A guard that fires once the nested BeginFrame has run has "
            + "already reset the caller's command pools, which is the damage "
            + "https://github.com/Rikarin/Vixen/issues/775 is about — reported rather than prevented."
        );

        device.EndFrame();

        // And outside the frame the very same call succeeds, which is what says the refusal is about
        // the nesting rather than about the plan, the device or the externals.
        using (var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = source })) {
            Assert.Equal(1, bake.Dispatches);
        }

        device.Destroy(source);
    }

    /// <summary>⚠ Every device answers the question, rather than three of them answering "no".</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The instrument check for the member the two tests above rest on.</b>
    ///         <see cref="IGraphicsDevice.IsFrameOpen" /> is declared without a default
    ///         implementation for exactly this reason, and the compiler is what enforces that across
    ///         the four backends — but the compiler cannot tell a stored bit from a
    ///         <c>=&gt; false</c>, and a backend that returned a constant would make every frame
    ///         discipline test in the repository pass whatever its caller did.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only the Null device is reachable from here</b>, which is the one that matters:
    ///         it is the device the tests run on. The Vulkan, GL and WebGPU implementations are read
    ///         by <c>CheckApi</c>'s baselines and by their own suites; what this file can assert is
    ///         that the device under every editor test tracks it truthfully.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_device_tracks_whether_a_frame_is_open() {
        using var device = new NullDevice(new() { Record = true });

        Assert.False(device.IsFrameOpen);

        device.BeginFrame();

        Assert.True(device.IsFrameOpen);

        device.EndFrame();

        Assert.False(device.IsFrameOpen);
    }

    /// <summary>One op over one external image, which is the smallest plan that dispatches.</summary>
    /// <returns>The plan.</returns>
    static TexturePlan Plan() =>
        new() {
            BaseWidth = 16,
            BaseHeight = 16,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
            Ops = [
                new() {
                    Kernel = "Invert",
                    Output = 1,
                    Inputs = [0],
                    Parameters = [
                        new("invertR", 1f),
                        new("invertG", 1f),
                        new("invertB", 1f),
                        new("invertA", 0f)
                    ]
                }
            ],
            Outputs = [1]
        };
}
