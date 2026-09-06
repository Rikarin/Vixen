// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Editor.Testing;

/// <summary>
///     Doc 48's exit criterion 11 — "a device is confirmed by name in every GPU test in this area" —
///     as one walk both of the area's test projects take, rather than as one walk and a convention.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The criterion's area is two test projects and the roll call covered one</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/883">#883</a>). That is the third time in
///         this workstream a rule's subject set has been narrower than the rule
///         (<a href="https://github.com/Rikarin/Vixen/issues/814">#814</a>,
///         <a href="https://github.com/Rikarin/Vixen/issues/872">#872</a>), and the shape that fixes
///         it is the one <c>PluginReferenceRule</c> uses: the walk takes its subject as a parameter
///         and both projects call it. Two callers of one function, not two transcriptions of one
///         idea.
///     </para>
///     <para>
///         ⚠ <b>This is the weaker of the two mechanisms and it is kept because it covers a hole the
///         stronger one cannot.</b> A harness that names the adapter itself makes an anonymous device
///         impossible for anything that goes <em>through</em> the harness; a file calling
///         <c>VulkanDevice.TryCreate</c> directly bypasses it entirely, and this walk is what notices
///         that the file opened a device without naming one. Both projects now have both halves.
///     </para>
///     <para>
///         ⚠ <b>A walk that reads no files reports every file compliant, which is the failure
///         criterion 11 exists because of</b> — eighteen golden files <em>passed</em> rather than
///         skipped without a device until 2026-08-21. So <see cref="Take" /> refuses before it
///         reports: the directory has to exist, it has to contain the caller's own anchor file, the
///         harness has to be one of the matches, and the match set has to be big enough to be the
///         device suites rather than a stray file.
///     </para>
/// </remarks>
static class DeviceRollCall {
    /// <summary>What one file looked like to the walk.</summary>
    /// <param name="Name">Its file name.</param>
    /// <param name="Text">Its whole text, which is what both detectors are matched against.</param>
    public sealed record Source(string Name, string Text);

    /// <summary>Every file in a project's own directory, read once.</summary>
    /// <param name="directory">The directory, from a caller's <c>[CallerFilePath]</c>.</param>
    /// <param name="anchor">The calling file's own name, which the directory has to contain.</param>
    /// <returns>One entry per <c>.cs</c> file directly in the directory.</returns>
    /// <remarks>
    ///     ⚠ <b>Top level only, and anchored at the caller rather than at the repository root.</b>
    ///     <c>.claude/worktrees</c> holds a whole checkout per agent, so a walk that climbs would be
    ///     comparing other people's copies of these files with each other — the trap the golden walk
    ///     and <c>CheckStrings</c> each hit once.
    /// </remarks>
    public static Source[] Read(string directory, string anchor) {
        Assert.True(
            Directory.Exists(directory),
            $"'{directory}' does not exist, so this roll call read no files at all. It is anchored at the "
            + "calling file's compiled path; a run whose sources are not on the machine cannot take it."
        );

        var sources = Directory.GetFiles(directory, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(path => new Source(Path.GetFileName(path), File.ReadAllText(path)))
            .ToArray();

        Assert.Contains(sources, source => string.Equals(source.Name, anchor, StringComparison.Ordinal));

        return sources;
    }

    /// <summary>
    ///     ⚠ Every file that opens a device names the adapter, or is on a written list.
    /// </summary>
    /// <param name="sources">The project's files, from <see cref="Read" />.</param>
    /// <param name="opens">The text a file uses to open a device — there is one way per project.</param>
    /// <param name="names">The text that naming the adapter looks like.</param>
    /// <param name="harness">The file that owns <paramref name="opens" />, required to be a match.</param>
    /// <param name="least">
    ///     The smallest match set that can still be the device suites. Below it the detector has
    ///     drifted and the walk is reading an almost empty set, which is a pass that means nothing.
    /// </param>
    /// <param name="anonymous">
    ///     The files excused from naming the adapter, each with the reason. Checked from both ends: a
    ///     name here whose file names the adapter now is an allowance that has outlived what it
    ///     excused, so the list can only shrink.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>The detector is anchored to the harness, and the roll call checks the anchor.</b> If
    ///     the opening call is renamed, every file stops matching and the roll call would pass over an
    ///     empty set — the silent-success failure this whole file is about — so the harness itself is
    ///     required to match, and the failure says what to update.
    /// </remarks>
    public static void Take(
        Source[] sources,
        string opens,
        string names,
        string harness,
        int least,
        (string File, string Reason)[] anonymous
    ) {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(anonymous);

        var opening = sources
            .Where(source => source.Text.Contains(opens, StringComparison.Ordinal))
            .ToArray();

        Assert.Contains(opening, source => string.Equals(source.Name, harness, StringComparison.Ordinal));

        Assert.True(
            opening.Length >= least,
            $"Only {opening.Length} files here contain '{opens}', and there were at least {least} when this was "
            + "written. The way a device is opened in this project has changed and this roll call is now reading "
            + "an almost empty set — which is the failure it exists to prevent, not a pass."
        );

        var unnamed = opening
            .Where(source => !source.Text.Contains(names, StringComparison.Ordinal))
            .Select(source => source.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var excused = anonymous.Select(entry => entry.File).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(excused, unnamed);

        Assert.All(excused, name => Assert.Contains(opening, source => string.Equals(source.Name, name, StringComparison.Ordinal)));
        Assert.All(anonymous, entry => Assert.True(entry.Reason.Length > 40, entry.File));
    }
}
