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
    static readonly string[] FrameSurface = ["BeginFrame", "EndFrame", "FrameCount", "FramesInFlight"];

    /// <summary>
    ///     ⚠ Nothing on the device says whether a frame is open — #775's blocker, confirmed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the claim the issue rests on, and it had never been checked.</b> A caller
    ///         that wanted to be safe — the plugin's preview pane, a thumbnail pump, a render
    ///         feature's extraction — has nothing to branch on: it cannot ask, and it cannot derive
    ///         the answer from <c>FrameCount</c>, whose contract says only that two reads inside one
    ///         frame agree.
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
            + "the caller that should now ask, and this assertion should be replaced by one that it does."
        );
    }

    /// <summary>
    ///     ⚠ A plan evaluated inside a host's own frame is accepted in silence — #775's trap.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The other half of the issue, and the half that is a behaviour rather than a
    ///         surface.</b> Nothing in the evaluator's API, its remarks or the device refuses the
    ///         nesting: the second <c>BeginFrame</c> is taken as an ordinary one. On this device that
    ///         is harmless, which is exactly why the assertion is made here — the Null device is
    ///         where a nested frame can be observed without corrupting the very pools the
    ///         observation would be read out of.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What the counter shows is the nesting itself.</b> Two frames advance across one
    ///         host frame that has not ended: the evaluator's, inside the caller's. On the Vulkan
    ///         backend the same two calls reset the caller's command pool and desynchronise its
    ///         fences, and no test can safely demonstrate that — which is the argument for the
    ///         device refusing it rather than for a comment saying not to.
    ///     </para>
    ///     <para>
    ///         <b>When this goes red</b>: because a guard was added, or because the evaluator stopped
    ///         driving the frame loop. Either is #775 closed, and the test to write in its place is
    ///         the one that asserts the refusal.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Evaluating_a_plan_inside_a_host_frame_is_neither_refused_nor_noticed() {
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

        var plan = new TexturePlan {
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

        using var evaluator = new TexturePlanEvaluator(device);

        // The host opens its frame, exactly as EditorHost.Present does before it builds a pane.
        device.BeginFrame();

        var inside = device.FrameCount;

        using (var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = source })) {
            Assert.Equal(1, bake.Dispatches);
        }

        Assert.True(
            device.FrameCount == inside + 1,
            $"Evaluating inside an open frame advanced the frame counter by {device.FrameCount - inside}. "
            + "One is the nesting https://github.com/Rikarin/Vixen/issues/775 is about — the evaluator's own "
            + "BeginFrame/EndFrame pair inside the caller's. Zero would mean the evaluator stopped driving the "
            + "frame loop, which closes the issue and makes this assertion the wrong one."
        );

        device.EndFrame();
        device.Destroy(source);
    }
}
