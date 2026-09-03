// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Net.Transport.WebSocket.Browser.Tests;

/// <summary>⚠ Verifying the instrument: that what is tested here is what ships.</summary>
/// <remarks>
///     <para>
///         Everything else in this project rests on one claim — that
///         <c>BrowserWebSocketFactory.cs</c> compiled for <c>net10.0</c> is the same code as
///         <c>BrowserWebSocketFactory.cs</c> compiled for <c>net10.0-browser</c>. The file is
///         source-linked, so that is true of the text; it stops being true of the *behaviour* the
///         moment anybody adds a <c>#if</c>.
///     </para>
///     <para>
///         ⚠ And it would stop silently. A <c>#if BROWSER</c> block is invisible here — the suite
///         would go on passing, testing the desktop arm of a file whose browser arm nobody had ever
///         run, which is a stronger version of exactly the failure the browser JavaScript suites
///         were written against: a double more permissive than the thing it doubles. So the
///         absence of conditional compilation is asserted rather than assumed.
///     </para>
///     <para>
///         The other reason to keep the file unconditional is that <c>net10.0-browser</c> defines
///         <c>BROWSER</c> automatically, so an arm guarded by it is one no gate in this repository
///         compiles: <c>Test</c>, <c>CheckFormat</c>, <c>CheckApi</c> and <c>Pack</c> never see the
///         browser project at all.
///     </para>
/// </remarks>
public sealed class LinkedSourceTests {
    [Fact]
    public void TheLinkedSourceHasNoConditionalCompilation() {
        var path = Path.Combine(
            RepositoryRoot(),
            "Core",
            "Vixen.Net.Transport.WebSocket.Browser",
            "BrowserWebSocketFactory.cs"
        );

        Assert.True(File.Exists(path), $"The linked source is not where this test expects it: {path}");

        var offenders = File.ReadAllLines(path)
            .Select((text, index) => (Line: index + 1, Text: text.TrimStart()))
            .Where(
                line => line.Text.StartsWith("#if", StringComparison.Ordinal)
                    || line.Text.StartsWith("#else", StringComparison.Ordinal)
                    || line.Text.StartsWith("#elif", StringComparison.Ordinal)
            )
            .Select(line => $"{line.Line}: {line.Text}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "BrowserWebSocketFactory.cs has conditional compilation in it, so this project no longer "
            + "tests the code that ships to a browser — it tests the net10.0 arm of it, and the "
            + "browser arm is compiled by no gate in this repository. Either remove the condition, "
            + "or move that code somewhere a published head exercises it (nuke BrowserSmoke).\n  "
            + string.Join("\n  ", offenders)
        );
    }

    /// <summary>
    ///     The browser project ships no JavaScript, which is the finding that made it small.
    /// </summary>
    /// <remarks>
    ///     ⚠ If a <c>wwwroot</c> ever appears here, this project has grown a <c>[JSImport]</c>
    ///     boundary — and a <c>[JSImport]</c> is a declaration no C# test can call, so it would need
    ///     the module-URL invariant <c>BrowserModuleUrlTests</c> asserts and a published head to
    ///     exercise it. Better to be told than to find out from a page that does nothing.
    /// </remarks>
    [Fact]
    public void TheBrowserTransportShipsNoJavaScript() {
        var project = Path.Combine(RepositoryRoot(), "Core", "Vixen.Net.Transport.WebSocket.Browser");

        Assert.Empty(Directory.GetFiles(project, "*.js", SearchOption.AllDirectories));

        // `[JSImport(` and not `JSImport`: the file's own remarks explain at length why there is no
        // [JSImport] here, and a check that cannot tell an attribute from the prose about it fails
        // on the documentation that makes the point.
        Assert.DoesNotContain(
            "[JSImport(",
            File.ReadAllText(Path.Combine(project, "BrowserWebSocketFactory.cs")),
            StringComparison.Ordinal
        );
    }

    /// <summary>Walks up from the test assembly until the repository root is recognisable.</summary>
    static string RepositoryRoot() {
        var directory = AppContext.BaseDirectory;

        while (directory is not null) {
            if (File.Exists(Path.Combine(directory, "Vixen.slnx"))) {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("No Vixen.slnx above the test assembly, so no repository root.");
    }
}
