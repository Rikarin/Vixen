// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;

namespace Vixen.App;

/// <summary>The <c>--vixen-*</c> arguments the host understands, and everything else untouched.</summary>
/// <remarks>
///     <para>
///         One prefix, reserved for the engine, so an application can define whatever arguments it
///         likes without a collision and without the host needing to know about them. Anything not
///         starting with <c>--vixen-</c> comes back in <see cref="Remaining" /> in its original
///         order.
///     </para>
///     <para>
///         Unrecognised <c>--vixen-*</c> arguments are collected rather than ignored. A typo in a
///         launch script that silently does nothing is how a QA build runs for a week without the
///         profiler somebody thought they had enabled.
///     </para>
/// </remarks>
public sealed record AppArguments {
    /// <summary>Arguments the host did not consume, in order.</summary>
    public IReadOnlyList<string> Remaining { get; private init; } = [];

    /// <summary>
    ///     <c>--vixen-*</c> arguments the host does not recognise, with their values.
    /// </summary>
    public IReadOnlyList<string> Unrecognised { get; private init; } = [];

    /// <summary>The variant named by <c>--vixen-variant</c>, or <see langword="null" />.</summary>
    public BuildVariant? Variant { get; private init; }

    /// <summary>Whether <c>--vixen-headless</c> was given.</summary>
    public bool Headless { get; private init; }

    /// <summary>The SDL video driver named by <c>--vixen-video-driver</c>, or
    /// <see langword="null" />.</summary>
    public string? VideoDriver { get; private init; }

    /// <summary>The worker count from <c>--vixen-workers</c>, or <see langword="null" />.</summary>
    public int? WorkerCount { get; private init; }

    /// <summary>The frame cap from <c>--vixen-frame-limit</c>, or <see langword="null" />.</summary>
    public int? FrameRateLimit { get; private init; }

    /// <summary>The level from <c>--vixen-log-level</c>, or <see langword="null" />.</summary>
    public LogLevel? LogLevel { get; private init; }

    /// <summary>
    ///     The directory from <c>--vixen-loose-content</c>, or <see langword="null" />.
    /// </summary>
    /// <remarks>
    ///     <c>docs/plan/17</c> Q5b: a release build may be pointed at loose files, deliberately, so
    ///     that a bug reproducible only in a shipping configuration can be poked at. The trade is
    ///     that "release reads only bundles" stops being an invariant, so it is not allowed to be
    ///     quiet — a build running this way warns on a timer and stamps its diagnostic overlay and
    ///     crash reports. The content system that honours it arrives in Phase 3; the argument is
    ///     parsed here because the host's argument contract is the host's to define, and adding it
    ///     later would be a breaking change to a command line people had already scripted.
    /// </remarks>
    public string? LooseContentPath { get; private init; }

    /// <summary>Reads the <c>--vixen-*</c> arguments out of a command line.</summary>
    /// <param name="arguments">The process arguments, without the executable name.</param>
    /// <returns>What the host understood, and what it left alone.</returns>
    public static AppArguments Parse(IReadOnlyList<string>? arguments) {
        if (arguments is null || arguments.Count == 0) {
            return new();
        }

        var remaining = new List<string>();
        var unrecognised = new List<string>();
        var parsed = new AppArguments();

        for (var index = 0; index < arguments.Count; index++) {
            var argument = arguments[index];

            if (!argument.StartsWith("--vixen-", StringComparison.Ordinal)) {
                remaining.Add(argument);
                continue;
            }

            // `--vixen-x=y` and `--vixen-x y` both work: the first is what a container's command
            // line looks like and the second is what a person types.
            var separator = argument.IndexOf('=', StringComparison.Ordinal);
            var name = separator < 0 ? argument : argument[..separator];
            var inlineValue = separator < 0 ? null : argument[(separator + 1)..];

            switch (name) {
                case "--vixen-headless":
                    parsed = parsed with { Headless = true };
                    continue;

                case "--vixen-variant" when Take(out var variant) && Enum.TryParse<BuildVariant>(variant, true, out var value):
                    parsed = parsed with { Variant = value };
                    continue;

                case "--vixen-video-driver" when Take(out var driver):
                    parsed = parsed with { VideoDriver = driver };
                    continue;

                case "--vixen-workers" when Take(out var workers) && int.TryParse(workers, out var count):
                    parsed = parsed with { WorkerCount = Math.Max(0, count) };
                    continue;

                case "--vixen-frame-limit" when Take(out var limit) && int.TryParse(limit, out var frames):
                    parsed = parsed with { FrameRateLimit = Math.Max(0, frames) };
                    continue;

                case "--vixen-log-level" when Take(out var level) && Enum.TryParse<LogLevel>(level, true, out var parsedLevel):
                    parsed = parsed with { LogLevel = parsedLevel };
                    continue;

                case "--vixen-loose-content" when Take(out var path):
                    parsed = parsed with { LooseContentPath = path };
                    continue;

                default:
                    unrecognised.Add(argument);
                    continue;
            }

            bool Take(out string taken) {
                if (inlineValue is not null) {
                    taken = inlineValue;
                    return true;
                }

                if (index + 1 < arguments.Count && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal)) {
                    taken = arguments[++index];
                    return true;
                }

                taken = string.Empty;
                return false;
            }
        }

        return parsed with { Remaining = remaining, Unrecognised = unrecognised };
    }
}
