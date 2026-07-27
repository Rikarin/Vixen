// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Markup.Tests;

/// <summary>Components the emitter fixtures instantiate, so a tag has to resolve to a real type.</summary>
/// <remarks>
///     Source rather than types in this assembly, because it is compiled <i>with</i> the generated
///     file. A tag that names a component and an attribute that names a parameter both become
///     ordinary C# identifiers in the output, and the whole point is that Roslyn resolves them the
///     way it resolves any other — against types in the compilation, not against a table here.
/// </remarks>
static class RuntimeContract {
    public const string Components = """
                                     using Vixen.Ui.Composition;

                                     public class Callout : Component {
                                         public string Kind { get; set; } = "";
                                         protected override void Build(BuildContext ctx) => ctx.Element(null, "callout-body");
                                     }

                                     public class Label : Component {
                                         public string Title { get; set; } = "";
                                         public int Step { get; set; }
                                         protected override void Build(BuildContext ctx) => ctx.Text(null, Title);
                                     }
                                     """;
}
