// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Core.Diagnostics;
using Vixen.Core.IO;
using Vixen.Core.Yaml;

namespace Vixen.App;

/// <summary>
///     <c>vixen.log.yaml</c>: the per-category log levels doc 13 § Discipline asks for, read from a
///     file rather than compiled in.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The engine half of this was already finished and nothing fed it.</b>
///         <see cref="LogFilter" /> has carried <c>SetCategoryLevel</c>, longest-prefix-first
///         matching and a lock since it was written, and <c>AppBuilder</c> has handed one filter to
///         every sink — so "turn on verbose asset loading without drowning in render spam" has been
///         one call away the whole time and had no caller outside two test files. What was missing
///         was somewhere to say it that is not a rebuild. This is that.
///     </para>
///     <para>
///         Two files, applied in order and both optional: <c>/app/vixen.log.yaml</c> ships with the
///         build and is what a project commits, and <c>/data/vixen.log.yaml</c> is the machine's,
///         written by whoever is chasing the bug. The second wins where they name the same prefix,
///         which is the point of there being two — a support build should not have to be rebuilt to
///         say more.
///     </para>
///     <para>
///         ⚠ <b><c>--vixen-log-level</c> beats the file's <c>minimumLevel</c>, and the file's
///         per-category rules always apply.</b> Those are not the same decision: the flag and the
///         key say the same thing, so the more explicit one has to win, while the command line has
///         no per-category form at all and so has nothing to disagree with.
///     </para>
///     <para>
///         A malformed file is a warning and not a stop. It is read before anything can go wrong
///         with it, on a path where the alternative is a game that refuses to start because
///         somebody mistyped a log level.
///     </para>
/// </remarks>
static class LogConfigFile {
    /// <summary>What the file is called in each of the two locations.</summary>
    public const string FileName = "vixen.log.yaml";

    /// <summary>The key holding the level that applies to every category without a rule.</summary>
    const string MinimumLevelKey = "minimumLevel";

    /// <summary>The key holding the prefix-to-level map.</summary>
    const string CategoriesKey = "categories";

    /// <summary>
    ///     Applies one document to a filter.
    /// </summary>
    /// <param name="yaml">The file's text.</param>
    /// <param name="filter">The filter every sink shares.</param>
    /// <param name="honourMinimumLevel">
    ///     Whether <c>minimumLevel</c> is allowed to move <see cref="LogFilter.MinimumLevel" />.
    ///     False when <c>--vixen-log-level</c> was given, which is more explicit than a file.
    /// </param>
    /// <returns>How many per-category rules the document set.</returns>
    /// <exception cref="FormatException">
    ///     The document is not a mapping, or a value is not a <see cref="LogLevel" />. The message
    ///     names the key and the value, because "the log config is broken" is not a fix.
    /// </exception>
    public static int Apply(string yaml, LogFilter filter, bool honourMinimumLevel) {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(filter);

        YamlNode document;

        try {
            document = YamlReader.Read(yaml);
        } catch (YamlParseException failure) {
            throw new FormatException($"{FileName} is not readable as YAML: {failure.Message}", failure);
        }

        // An empty file is a legitimate thing to commit — it is how a project says "this exists and
        // says nothing yet" — and the reader gives back an empty scalar for it rather than a mapping.
        if (document is YamlScalar { Value.Length: 0 }) {
            return 0;
        }

        if (document is not YamlMapping mapping) {
            throw new FormatException(
                $"{FileName} must be a mapping with '{MinimumLevelKey}' and '{CategoriesKey}' keys, "
                + $"and this one is a {Describe(document)}."
            );
        }

        if (mapping.TryGet(MinimumLevelKey, out var minimum) && honourMinimumLevel) {
            filter.MinimumLevel = Level(minimum, MinimumLevelKey);
        }

        if (!mapping.TryGet(CategoriesKey, out var categories)) {
            return 0;
        }

        if (categories is not YamlMapping rules) {
            throw new FormatException(
                $"{FileName}'s '{CategoriesKey}' must be a mapping of category prefix to level, "
                + $"and this one is a {Describe(categories)}."
            );
        }

        var applied = 0;

        foreach (var (prefix, level) in rules) {
            if (prefix.Length == 0) {
                throw new FormatException($"{FileName} has a '{CategoriesKey}' entry with an empty prefix.");
            }

            filter.SetCategoryLevel(prefix, Level(level, $"{CategoriesKey}.{prefix}"));
            applied++;
        }

        return applied;
    }

    /// <summary>
    ///     Reads both standard locations and applies whichever exist, shipped first and the
    ///     machine's second.
    /// </summary>
    /// <param name="files">The mounted file system.</param>
    /// <param name="filter">The filter every sink shares.</param>
    /// <param name="honourMinimumLevel">Whether the file may move the global minimum level.</param>
    /// <param name="log">Where the outcome is said. Every branch says something.</param>
    /// <remarks>
    ///     ⚠ <b>A file that was found says so, and so does one that could not be read.</b> The
    ///     failure this shape has is the one this repository keeps meeting — a configuration file
    ///     nobody reads and nothing reports, indistinguishable from one that was read and agreed
    ///     with the defaults. A run whose log does not mention this file did not find one.
    /// </remarks>
    public static void ApplyStandardLocations(
        VirtualFileSystem files,
        LogFilter filter,
        bool honourMinimumLevel,
        ILogger log
    ) {
        foreach (var mount in (ReadOnlySpan<VirtualPath>)[MountPoints.App, MountPoints.Data]) {
            var path = mount / FileName;

            if (!files.Exists(path)) {
                continue;
            }

            try {
                using var stream = files.OpenRead(path);
                using var reader = new StreamReader(stream);

                var rules = Apply(reader.ReadToEnd(), filter, honourMinimumLevel);

                HostLog.LogConfigApplied(log, path, rules, filter.MinimumLevel);
            } catch (Exception failure) when (failure is FormatException or IOException
                                              or UnauthorizedAccessException) {
                HostLog.LogConfigUnreadable(log, path, failure.Message);
            }
        }
    }

    /// <summary>Turns a scalar into a level, or says exactly which key was not one.</summary>
    static LogLevel Level(YamlNode node, string key) {
        if (node is YamlScalar scalar && Enum.TryParse<LogLevel>(scalar.Value, ignoreCase: true, out var level)) {
            return level;
        }

        throw new FormatException(
            $"{FileName}'s '{key}' is '{node}', which is not a log level. Use one of "
            + $"{string.Join(", ", Enum.GetNames<LogLevel>())}."
        );
    }

    /// <summary>What a node is, in words a person editing the file can act on.</summary>
    static string Describe(YamlNode node) =>
        node switch {
            YamlSequence => "list",
            YamlScalar => "single value",
            _ => "value of an unexpected kind"
        };
}
