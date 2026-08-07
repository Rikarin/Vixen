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
                                     using Vixen.Ui;
                                     using Vixen.Ui.Composition;

                                     public enum DialMode { Slow, Fast }

                                     // An element rather than a component, because a capitalised tag
                                     // may name either and a control is where properties that are
                                     // not strings actually live.
                                     public class Dial : UiElement {
                                         protected override string TagName => "dial";

                                         public DialMode Mode { get; set; }
                                         public float Ratio { get; set; }
                                         public int Steps { get; set; }
                                         public bool Loud { get; set; }
                                         public string? Caption { get; set; }
                                     }

                                     // What a `Control` is, in the one respect `class` cares about:
                                     // it names its own classes in `OnCreated`, before any markup
                                     // attribute is applied. The real one gives itself
                                     // `variant-default` and `size-md` there. Referencing
                                     // Vixen.Ui.Controls from here would buy a heavier test project
                                     // and no more coverage than the two AddClass calls.
                                     public class Gauge : UiElement {
                                         protected override string TagName => "gauge";

                                         protected override void OnCreated() {
                                             base.OnCreated();
                                             AddClass("variant-default");
                                             AddClass("size-md");
                                         }
                                     }

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
