// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;
using Xunit;

namespace Tests;

/// <summary>A group or note keeps one number, in the file and through every edit, and no two share it.</summary>
public class GraphFurnitureIdentityTests {
    [Fact]
    public void A_file_with_no_identities_gets_them_and_the_next_read_keeps_them() {
        NodeGraphAsset old = new() {
            Groups = [new() { Title = "A" }, new() { Title = "B" }],
            Comments = [new() { Text = "why" }]
        };

        var first = NodeGraphDocument.Save(NodeGraphDocument.Load(old, out _));
        var second = NodeGraphDocument.Save(NodeGraphDocument.Load(first, out _));

        int[] ids = [.. first.Groups.Select(static group => group.Id), .. first.Comments.Select(static note => note.Id)];

        Assert.All(ids, static id => Assert.True(id > 0));
        Assert.Equal(3, ids.Distinct().Count());
        Assert.Equal(first.Groups.Select(static group => group.Id), second.Groups.Select(static group => group.Id));
        Assert.Equal(first.Comments.Select(static note => note.Id), second.Comments.Select(static note => note.Id));
    }

    [Fact]
    public void A_repeated_identity_from_a_bad_merge_is_given_a_new_one() {
        NodeGraphAsset merged = new() {
            Groups = [new() { Id = 4, Title = "A" }, new() { Id = 4, Title = "B" }],
            Comments = [new() { Id = 4, Text = "why" }]
        };

        var saved = NodeGraphDocument.Save(NodeGraphDocument.Load(merged, out _));

        Assert.Equal(4, saved.Groups[0].Id);
        Assert.Equal(3, new[] { saved.Groups[0].Id, saved.Groups[1].Id, saved.Comments[0].Id }.Distinct().Count());
    }

    [Fact]
    public void A_number_taken_away_by_an_undo_is_not_given_to_the_next_group() {
        NodeGraphModel graph = new();
        AddGroupCommand first = new(graph, "First", []);
        first.Do(new Touched());
        var held = NodeGraphDocument.Save(graph).Groups[0].Id;

        first.Undo(new Touched());
        new AddGroupCommand(graph, "Second", []).Do(new Touched());

        Assert.NotEqual(held, NodeGraphDocument.Save(graph).Groups[0].Id);

        first.Do(new Touched());
        Assert.Contains(NodeGraphDocument.Save(graph).Groups, group => group.Id == held && group.Title == "First");
    }

    /// <summary>The model strips a node from its groups and does not say where from, so the command has to.</summary>
    [Fact]
    public void Undoing_an_extraction_puts_its_nodes_back_where_they_were_in_their_groups() {
        NodeGraphModel graph = new();
        var first = graph.Add("A", default);
        var second = graph.Add("B", default);
        var third = graph.Add("C", default);
        GraphGroup group = new() { Title = "Frame" };

        group.Nodes.AddRange([first.Id, second.Id, third.Id]);
        graph.Groups.Add(group);

        ExtractSubGraphCommand command = new(graph, SubGraphs.Extract(graph, [second.Id], "Inner"), "Inner", default);

        command.Do(new Touched());
        Assert.DoesNotContain(second.Id, group.Nodes);

        command.Undo(new Touched());
        Assert.Equal([first.Id, second.Id, third.Id], group.Nodes);
    }

    sealed class Touched : Vixen.Authoring.ICommandContext {
        public void Touch(Vixen.Authoring.IAuthoredDocument document) { }
    }
}
