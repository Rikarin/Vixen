// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Platform.Headless;
using Vixen.Ui;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Platform.Ui.Tests;

/// <summary>The user's accessibility settings reach the document, which for their whole life they did not.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two tested halves and an untested join, which is this repository's commonest
///         defect.</b> <c>MediaQuery</c> has answered <c>prefers-reduced-motion</c> and
///         <c>forced-colors</c> since the features landed, <c>Animator.ReduceMotion</c> has silently
///         dropped transitions and keyframes since the same day, and every writer of
///         <c>MediaPreferences</c> in the tree was a test. Nothing between an operating system and
///         either of them existed — so a stylesheet's <c>@media (prefers-reduced-motion)</c> block
///         was unreachable in every shipped application and no assertion anywhere went red about it.
///     </para>
///     <para>
///         ⚠ <b>The assertions are about <c>Animator.ReduceMotion</c> and about the media context,
///         not about the property that was written.</b> A test that read
///         <c>UiSurface.Preferences</c> back would pass against a wire that reached the surface and
///         stopped there, which is exactly one join short of the behaviour anybody wanted.
///     </para>
/// </remarks>
public class AccessibilityWiringTests {
    [Fact]
    public void A_platform_that_says_reduce_motion_stops_the_animator() {
        using var document = new UiDocument(200f, 100f);

        Assert.False(document.Styles.Animations.ReduceMotion);

        PlatformInput.ApplyAccessibility(document, new SystemAccessibility(ReduceMotion: true));

        Assert.True(document.Styles.Animations.ReduceMotion);
        Assert.Equal(MotionPreference.Reduce, document.Primary.Media.Preferences.Motion);
    }

    /// <summary>
    ///     ⚠ An axis the platform could not read is <c>no-preference</c> and never the "on" value. A
    ///     host that read <c>null</c> as "the user wants reduced motion" would take the animation off
    ///     every headless run and every Linux desktop with no settings daemon.
    /// </summary>
    [Fact]
    public void An_unknown_setting_expresses_no_preference() {
        using var document = new UiDocument(200f, 100f);

        PlatformInput.ApplyAccessibility(document, SystemAccessibility.Unknown);

        Assert.False(document.Styles.Animations.ReduceMotion);
        Assert.Equal(MotionPreference.NoPreference, document.Primary.Media.Preferences.Motion);
        Assert.False(document.Primary.Media.Preferences.ForcedColors);
        Assert.Equal(ContrastPreference.NoPreference, document.Primary.Media.Preferences.Contrast);
    }

    /// <summary>
    ///     ⚠ High contrast is one platform switch and two CSS questions. Windows' high-contrast mode
    ///     and macOS's Increase Contrast both replace the palette <i>and</i> raise the contrast, so a
    ///     sheet that asked only <c>(prefers-contrast: more)</c> would do nothing on the platforms
    ///     where the setting is most used. The converse is deliberately not wired: asking for more
    ///     contrast is not asking for the palette to be taken away.
    /// </summary>
    [Fact]
    public void High_contrast_answers_both_forced_colors_and_prefers_contrast() {
        using var document = new UiDocument(200f, 100f);

        PlatformInput.ApplyAccessibility(document, new SystemAccessibility(HighContrast: true));

        var preferences = document.Primary.Media.Preferences;

        Assert.True(preferences.ForcedColors);
        Assert.Equal(ContrastPreference.More, preferences.Contrast);
        Assert.Equal(MotionPreference.NoPreference, preferences.Motion);
    }

    /// <summary>
    ///     Every surface, on the same terms the appearance is applied to every surface: these are
    ///     settings of the machine, so a torn-off panel cannot be running under different ones.
    /// </summary>
    [Fact]
    public void Every_surface_learns_it_and_not_only_the_primary_one() {
        using var document = new UiDocument(200f, 100f);
        var second = document.CreateSurface(120f, 80f);

        PlatformInput.ApplyAccessibility(document, new SystemAccessibility(true, true));

        Assert.Equal(MotionPreference.Reduce, second.Media.Preferences.Motion);
        Assert.True(second.Media.Preferences.ForcedColors);
    }

    /// <summary>
    ///     ⚠ The platform's own half: a change owes an event, because a host that only read the
    ///     value once at boot would never notice the switch being flipped while it ran. The headless
    ///     platform posts it on exactly the terms a desktop's poll does, so a host wired to the event
    ///     is exercised by the real code path.
    /// </summary>
    [Fact]
    public void A_setting_that_moves_posts_an_event_and_one_that_does_not_posts_nothing() {
        using var platform = new HeadlessPlatform(new() { Organisation = "Vixen", Application = "Test" });

        platform.PumpEvents();

        platform.Accessibility = new SystemAccessibility(ReduceMotion: true);

        Assert.Contains(
            platform.PumpEvents().ToArray(),
            posted => posted.Kind == PlatformEventKind.SystemAccessibilityChanged
        );

        platform.Accessibility = new SystemAccessibility(ReduceMotion: true);

        Assert.DoesNotContain(
            platform.PumpEvents().ToArray(),
            posted => posted.Kind == PlatformEventKind.SystemAccessibilityChanged
        );
    }
}
