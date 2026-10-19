// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Ui.Documents;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>A view's durable state is the workspace's, one instance per view, outliving any panel.</summary>
public class ViewStateTests {
    sealed class Remembered {
        public int Value { get; set; }
    }

    [Fact]
    public void A_view_gets_the_same_state_every_time_it_asks() {
        IWorkspace workspace = new EditorWorkspace();

        workspace.ViewState<Remembered>("scene").Value = 7;

        Assert.Equal(7, workspace.ViewState<Remembered>("scene").Value);
        Assert.Same(workspace.ViewState<Remembered>("scene"), workspace.ViewState<Remembered>("scene"));
        Assert.Equal(0, workspace.ViewState<Remembered>("other").Value);
    }

    [Fact]
    public void Two_types_for_one_view_is_refused_rather_than_answered_with_a_second_copy() {
        IWorkspace workspace = new EditorWorkspace();

        workspace.ViewState<Remembered>("scene");

        Assert.Throws<InvalidOperationException>(() => workspace.ViewState<List<int>>("scene"));
    }
}
