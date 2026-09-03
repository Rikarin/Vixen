// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Vixen.Graphics;

/// <summary>One adapter a backend must not be used on, and why.</summary>
/// <param name="Adapter">
///     Part of the adapter's name, matched case-insensitively. <c>*</c> matches every adapter, which
///     is only meaningful together with a <paramref name="DriverVersion" /> that does not.
/// </param>
/// <param name="DriverVersion">
///     The driver version this applies to, matched case-insensitively as a substring, or <c>*</c>
///     for every version of that adapter's driver.
/// </param>
/// <param name="Reason">
///     What goes wrong, in words a log reader can act on. Not optional — a rule nobody can review is
///     not a deny-list, it is a device that stopped working for no stated cause.
/// </param>
/// <remarks>
///     <para>
///         <b>A substring rather than an exact name, because a driver's name for itself is not a
///         stable identifier.</b> The same GPU reports <c>Mali-G78</c> on one device and
///         <c>Mali-G78 MC14</c> on another, and Adreno adds and drops the vendor prefix between
///         driver branches. A rule keyed on equality is a rule that stops matching after an OTA
///         update, which is worse than no rule because it looks like coverage.
///     </para>
///     <para>
///         ⚠ <b>The driver version is a substring too, and that is a deliberate blunt instrument.</b>
///         <see cref="IGraphicsAdapter.DriverVersion" /> is a string each backend formats for
///         humans; there is no ordering to compare against and pretending there is one would mean
///         parsing a different scheme per vendor. A range is expressed as several rules, which is
///         verbose and honest.
///     </para>
/// </remarks>
public readonly record struct GpuDenyRule(string Adapter, string? DriverVersion, string Reason) {
    /// <summary>Whether this rule refuses a given adapter.</summary>
    /// <param name="name">The adapter's name.</param>
    /// <param name="driverVersion">Its driver version.</param>
    /// <returns>Whether the rule matches.</returns>
    public bool Matches(string name, string driverVersion) =>
        (Adapter == GpuDenyList.Any
            || (name is not null
                && name.Contains(Adapter, StringComparison.OrdinalIgnoreCase)))
        && (DriverVersion is null
            || DriverVersion == GpuDenyList.Any
            || (driverVersion is not null
                && driverVersion.Contains(DriverVersion, StringComparison.OrdinalIgnoreCase)));
}

/// <summary>The adapters a backend is known to be broken on, keyed on GPU and driver version.</summary>
/// <remarks>
///     <para>
///         <b>What <a href="../../docs/plan/10-platforms.md">doc 10</a> § Android asks for by name:
///         "the device-capability database (a curated deny-list keyed on GPU/driver version, shipped
///         as content and updatable)".</b> Android driver fragmentation is the reason — a device
///         reports Vulkan, passes every capability query the engine knows how to ask, and then fails
///         on a specific extension in a specific driver branch. There is no query for "this driver
///         lies"; there is only a list somebody wrote down.
///     </para>
///     <para>
///         ⚠ <b>It refuses an <em>adapter</em>, not a feature.</b> A denied adapter is one the
///         backend must not be selected on at all, which is what makes it fall through to the next
///         entry in the head's preference list — Vulkan denied, so OpenGL, so Null. A capability
///         that a device reports and does not have is a different problem with a different answer:
///         <see cref="GraphicsDeviceFeatures" /> is where a renderer asks, and a deny-list that
///         cleared a bit there would be lying in the other direction.
///     </para>
///     <para>
///         ⚠ <b>Empty denies nothing, and that is the state every machine is in until content says
///         otherwise.</b> The failure mode this whole type has to avoid is a deny-list that refuses
///         a device nobody meant to refuse, because the symptom is a black screen on hardware that
///         works — so <see cref="Parse" /> reports a malformed line rather than dropping it, and
///         refuses a rule that matches every adapter and every driver.
///     </para>
///     <para>
///         <b>The format is one rule per line</b>, three fields separated by <c>|</c>, with
///         <c>#</c> starting a comment:
///     </para>
///     <code>
///     # adapter | driver version | reason
///     Mali-G72  | *              | VK_KHR_dynamic_rendering is advertised and unimplemented
///     Adreno    | 512.502        | crashes in vkCreateSwapchainKHR on rotation
///     </code>
///     <para>
///         ⚠ <b>What is not here is where the file comes from.</b> Doc 10 wants it shipped as
///         content and updatable, so a game can be fixed on a device the build predates; loading it
///         is the head's job and is owed. Until then a head builds one in code, which is enough to
///         make the decision reachable rather than theoretical.
///     </para>
/// </remarks>
public sealed class GpuDenyList {
    /// <summary>The wildcard, in both fields.</summary>
    public const string Any = "*";

    readonly GpuDenyRule[] rules;

    /// <summary>Builds a list from rules.</summary>
    /// <param name="rules">The rules, in the order they should be tried.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rules" /> is null.</exception>
    /// <exception cref="ArgumentException">A rule matches every adapter and every driver.</exception>
    public GpuDenyList(IEnumerable<GpuDenyRule> rules) {
        ArgumentNullException.ThrowIfNull(rules);

        this.rules = [.. rules];

        foreach (var rule in this.rules) {
            if (IsCatchAll(rule)) {
                throw new ArgumentException(CatchAll, nameof(rules));
            }
        }
    }

    /// <summary>The list that refuses nothing, which is what a head has until content says otherwise.</summary>
    public static GpuDenyList Empty { get; } = new([]);

    /// <summary>The rules, in order.</summary>
    public IReadOnlyList<GpuDenyRule> Rules => rules;

    /// <summary>Reads a deny-list from its text form.</summary>
    /// <param name="text">The file's contents.</param>
    /// <returns>The list.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text" /> is null.</exception>
    /// <exception cref="FormatException">
    ///     A line is neither blank, nor a comment, nor three <c>|</c>-separated fields; the message
    ///     names the line number.
    /// </exception>
    /// <remarks>
    ///     ⚠ <b>Throws rather than skipping, which is the whole reason it is worth having a parser
    ///     at all.</b> A deny-list is read to protect a device from a driver, and the one outcome
    ///     nobody can see is a rule that was silently dropped: the run is green, the log is quiet,
    ///     and the device it was written for is exactly as broken as before. A file that will not
    ///     parse is a file somebody has to look at.
    /// </remarks>
    public static GpuDenyList Parse(string text) {
        ArgumentNullException.ThrowIfNull(text);

        var parsed = new List<GpuDenyRule>();
        var lines = text.Split('\n');

        for (var index = 0; index < lines.Length; index++) {
            var line = lines[index].Trim();
            var comment = line.IndexOf('#', StringComparison.Ordinal);

            if (comment >= 0) {
                line = line[..comment].Trim();
            }

            if (line.Length == 0) {
                continue;
            }

            var fields = line.Split('|');

            if (fields.Length != 3) {
                throw Malformed(
                    index + 1,
                    $"expected 'adapter | driver version | reason' and found {fields.Length} field(s)"
                );
            }

            var rule = new GpuDenyRule(fields[0].Trim(), fields[1].Trim(), fields[2].Trim());

            if (rule.Adapter.Length == 0) {
                throw Malformed(index + 1, $"the adapter field is empty; write '{Any}' to mean every adapter");
            }

            if (rule.Reason.Length == 0) {
                throw Malformed(index + 1, "the reason field is empty, and a rule nobody can review is not one");
            }

            if (IsCatchAll(rule)) {
                throw Malformed(index + 1, CatchAll);
            }

            parsed.Add(rule);
        }

        return new(parsed);
    }

    /// <summary>Whether a backend must not be used on an adapter.</summary>
    /// <param name="adapter">The adapter.</param>
    /// <param name="reason">The matching rule's reason, when one matched.</param>
    /// <returns>Whether it is denied.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="adapter" /> is null.</exception>
    public bool IsDenied(IGraphicsAdapter adapter, [NotNullWhen(true)] out string? reason) {
        ArgumentNullException.ThrowIfNull(adapter);
        return IsDenied(adapter.Name, adapter.DriverVersion, out reason);
    }

    /// <summary>The same question, for a caller that has the two strings and no adapter yet.</summary>
    /// <param name="name">The adapter's name.</param>
    /// <param name="driverVersion">Its driver version.</param>
    /// <param name="reason">The matching rule's reason, when one matched.</param>
    /// <returns>Whether it is denied.</returns>
    /// <remarks>
    ///     ⚠ <b>This overload is the one selection actually calls, and that is not an accident.</b>
    ///     A backend decides which physical device to use before it has created anything, so what it
    ///     holds at that moment is a name and a version string rather than an
    ///     <see cref="IGraphicsAdapter" /> — and asking after the device exists would mean creating
    ///     a device on the driver the list exists to stay away from.
    /// </remarks>
    public bool IsDenied(string name, string driverVersion, [NotNullWhen(true)] out string? reason) {
        foreach (var rule in rules) {
            if (rule.Matches(name, driverVersion)) {
                reason = $"'{name}' (driver {driverVersion}) is on the device deny-list: {rule.Reason}";
                return true;
            }
        }

        reason = null;
        return false;
    }

    const string CatchAll =
        "a rule matching every adapter and every driver version denies every device on every "
        + "machine, which is one typo away from a content update that black-screens a shipped game. "
        + "Name the adapter or the driver version.";

    static bool IsCatchAll(in GpuDenyRule rule) =>
        rule.Adapter == Any && rule.DriverVersion is null or Any;

    static FormatException Malformed(int line, string what) => new(
        string.Format(CultureInfo.InvariantCulture, "Deny-list line {0}: {1}.", line, what)
    );
}
