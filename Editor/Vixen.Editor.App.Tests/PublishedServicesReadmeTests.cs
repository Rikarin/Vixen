// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Vixen.Editor.Testing;
using Vixen.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The plugin README's list of what the editor publishes is the list the editor publishes (#1311).</summary>
/// <remarks>
///     ⚠ <b>It was not, and nothing could see it.</b> Doc 50 § D6 asked that the sentence in
///     <c>Editor/Vixen.Editor.Plugin/README.md</c> be updated in the same commits that publish a service,
///     and five services — <c>IContentLibrary</c>, <c>IProjectSession</c>, <c>IPlaySession</c>,
///     <c>IActivePlaySession</c> and <c>ISceneEditing</c> — were published by three batches and named by
///     none. A sentence is a snapshot of the day it was typed; this compares it with the editor's own
///     <c>PluginServices.Available</c> after every module has been unloaded, which is what
///     <c>PluginPoints()</c> published and nothing a module added.
/// </remarks>
public class PublishedServicesReadmeTests {
    const string Readme = "Editor/Vixen.Editor.Plugin/README.md";

    static readonly Regex Sentence = new(
        @"What `Vixen\.Editor\.App` publishes today is (?<list>.*?)\s+itself\.",
        RegexOptions.Singleline | RegexOptions.CultureInvariant
    );

    static readonly Regex Named = new("`([A-Za-z][A-Za-z0-9]*)`", RegexOptions.CultureInvariant);

    [Fact]
    public void The_readme_names_exactly_what_the_editor_publishes() {
        var text = File.ReadAllText(Path.Combine(RepositoryFiles.Root, Readme.Replace('/', Path.DirectorySeparatorChar)));
        var sentence = Sentence.Match(text);

        Assert.True(sentence.Success, $"{Readme} no longer has the sentence beginning \"What `Vixen.Editor.App` publishes today is\".");

        var named = Named.Matches(sentence.Groups["list"].Value)
            .Select(static match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        using var editor = EditorSession.Start();

        editor.Plugins.UnloadAll();

        var published = editor.Plugins.Services.Available
            .Select(static type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

        // The instrument first: a sentence that matched nothing and an editor that published nothing agree.
        Assert.Contains("EditorProject", published);
        Assert.Contains("EditorProject", named);

        var unnamed = published.Except(named).Order(StringComparer.Ordinal).ToList();
        var stale = named.Except(published).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            unnamed.Count == 0 && stale.Count == 0,
            $"Published and not in {Readme}: [{string.Join(", ", unnamed)}]. "
            + $"Named there and not published: [{string.Join(", ", stale)}]."
        );
    }
}
