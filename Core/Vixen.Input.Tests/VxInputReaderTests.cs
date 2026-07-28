// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Input.Tests;

/// <summary>The <c>.vxinput</c> dialect: what it reads, and what it refuses.</summary>
public class VxInputReaderTests {
    [Fact]
    public void ReadsAMappingOfScalars() {
        var root = Assert.IsType<VxInputMapping>(VxInputReader.Read("name: Game\nversion: 1\n"));

        Assert.Equal(2, root.Entries.Count);
        Assert.True(root.TryGet("name", out var name));
        Assert.Equal("Game", Assert.IsType<VxInputScalar>(name).Value);
    }

    [Fact]
    public void MatchesKeysWithoutRegardToCase() {
        var root = Assert.IsType<VxInputMapping>(VxInputReader.Read("ControlSchemes: none\n"));

        Assert.True(root.TryGet("controlSchemes", out _));
    }

    [Fact]
    public void ReadsASequenceOfMappings() {
        var root = Assert.IsType<VxInputMapping>(
            VxInputReader.Read(
                """
                maps:
                  - name: Player
                    kind: gameplay
                  - name: Menu
                """
            )
        );

        Assert.True(root.TryGet("maps", out var node));
        var maps = Assert.IsType<VxInputSequence>(node);
        Assert.Equal(2, maps.Items.Count);

        var first = Assert.IsType<VxInputMapping>(maps.Items[0]);
        Assert.True(first.TryGet("kind", out var kind));
        Assert.Equal("gameplay", Assert.IsType<VxInputScalar>(kind).Value);
    }

    [Fact]
    public void ReadsNestingThreeDeep() {
        var root = Assert.IsType<VxInputMapping>(
            VxInputReader.Read(
                """
                maps:
                  - name: Player
                    actions:
                      - name: Move
                        bindings:
                          - path: <Keyboard>/w
                """
            )
        );

        var maps = Assert.IsType<VxInputSequence>(Get(root, "maps"));
        var player = Assert.IsType<VxInputMapping>(maps.Items[0]);
        var actions = Assert.IsType<VxInputSequence>(Get(player, "actions"));
        var move = Assert.IsType<VxInputMapping>(actions.Items[0]);
        var bindings = Assert.IsType<VxInputSequence>(Get(move, "bindings"));
        var binding = Assert.IsType<VxInputMapping>(bindings.Items[0]);

        Assert.Equal("<Keyboard>/w", Assert.IsType<VxInputScalar>(Get(binding, "path")).Value);
    }

    [Fact]
    public void ReadsASequenceOfScalars() {
        var root = Assert.IsType<VxInputMapping>(VxInputReader.Read("devices:\n  - keyboard\n  - mouse\n"));
        var devices = Assert.IsType<VxInputSequence>(Get(root, "devices"));

        Assert.Equal(["keyboard", "mouse"], devices.Items.Select(item => ((VxInputScalar)item).Value));
    }

    [Fact]
    public void KeepsAColonThatIsNotAKeySeparator() {
        // The one that would break every control path if the split were on the first colon.
        var root = Assert.IsType<VxInputMapping>(VxInputReader.Read("path: <Keyboard>/w\nnote: a:b\n"));

        Assert.Equal("a:b", Assert.IsType<VxInputScalar>(Get(root, "note")).Value);
    }

    [Fact]
    public void DropsCommentsAndBlankLines() {
        var root = Assert.IsType<VxInputMapping>(
            VxInputReader.Read("# a header\n\nname: Game   # trailing\n\n# and a footer\n")
        );

        Assert.Single(root.Entries);
        Assert.Equal("Game", Assert.IsType<VxInputScalar>(Get(root, "name")).Value);
    }

    [Fact]
    public void KeepsAHashThatIsNotAComment() {
        var root = Assert.IsType<VxInputMapping>(VxInputReader.Read("name: Player#2\n"));

        Assert.Equal("Player#2", Assert.IsType<VxInputScalar>(Get(root, "name")).Value);
    }

    [Theory]
    [InlineData("name: 'a b'", "a b")]
    [InlineData("name: \"a b\"", "a b")]
    [InlineData("name: 'it''s'", "it's")]
    [InlineData("name: \"a\\nb\"", "a\nb")]
    [InlineData("name: '# not a comment'", "# not a comment")]
    [InlineData("name: ''", "")]
    public void ReadsQuotedScalars(string document, string expected) {
        var root = Assert.IsType<VxInputMapping>(VxInputReader.Read(document + "\n"));

        Assert.Equal(expected, Assert.IsType<VxInputScalar>(Get(root, "name")).Value);
    }

    [Fact]
    public void ReadsAKeyWithNothingUnderItAsEmpty() {
        // What an editor writes for a map whose actions have all been deleted, and it must load.
        var root = Assert.IsType<VxInputMapping>(VxInputReader.Read("name: Game\nmaps:\n"));

        Assert.Equal(string.Empty, Assert.IsType<VxInputScalar>(Get(root, "maps")).Value);
    }

    [Fact]
    public void RefusesATab() {
        var failure = Assert.Throws<VxInputParseException>(() => VxInputReader.Read("maps:\n\t- name: Player\n"));

        Assert.Equal(2, failure.Line);
        Assert.Contains("tab", failure.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("name: &anchor\n")]
    [InlineData("name: *alias\n")]
    [InlineData("name: !Tag\n")]
    [InlineData("name: [a, b]\n")]
    [InlineData("name: {a: b}\n")]
    [InlineData("name: |\n")]
    public void RefusesYamlItDoesNotSupport(string document) {
        var failure = Assert.Throws<VxInputParseException>(() => VxInputReader.Read(document));

        Assert.Contains("dialect", failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAnUnterminatedQuote() {
        var failure = Assert.Throws<VxInputParseException>(() => VxInputReader.Read("name: 'Game\n"));

        Assert.Contains("closing quote", failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesALineThatIsNotAKeyValuePair() {
        var failure = Assert.Throws<VxInputParseException>(() => VxInputReader.Read("name: Game\nnonsense\n"));

        Assert.Equal(2, failure.Line);
    }

    [Fact]
    public void ReportsThePositionOfTheProblem() {
        var failure = Assert.Throws<VxInputParseException>(
            () => VxInputReader.Read("name: Game\nmaps:\n  - name: Player\n\tbroken: yes\n")
        );

        Assert.Equal(4, failure.Line);
        Assert.Contains("(4,", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToleratesCarriageReturns() {
        var root = Assert.IsType<VxInputMapping>(VxInputReader.Read("name: Game\r\nversion: 1\r\n"));

        Assert.Equal("Game", Assert.IsType<VxInputScalar>(Get(root, "name")).Value);
    }

    static VxInputNode Get(VxInputMapping mapping, string key) {
        Assert.True(mapping.TryGet(key, out var node), $"the document has no '{key}'");
        return node!;
    }
}
