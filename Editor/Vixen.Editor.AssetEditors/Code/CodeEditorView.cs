// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.Markup.Binding;

namespace Vixen.Editor.AssetEditors.Code;

/// <summary>A code editor over a document, with its diagnostics in the gutter.</summary>
/// <remarks>
///     <para>
///         <b>The pane, not the editor.</b> <see cref="CodeEditor" /> is the control — lines,
///         folding, completion, a caret — and this is what joins one to a
///         <see cref="CodeDocument" />: the buffer, the tokenizer, and putting the analysis back on
///         it after it runs.
///     </para>
///     <para>
///         ⚠ <b>Analysis is driven from here and is not automatic.</b> <see cref="Analyse" /> is
///         called by whatever decides when a pause has happened — the host's frame loop, a timer, a
///         Save — because parsing per keystroke is a parse per keystroke and a document that did it
///         for you could not be talked out of it.
///     </para>
/// </remarks>
public class CodeEditorView : Control {
    CodeDocument? document;

    /// <inheritdoc />
    protected override string TagName => "code-editor-pane";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The editor.</summary>
    public CodeEditor Editor { get; private set; } = null!;

    /// <summary>The document being edited, or <see langword="null" />.</summary>
    public CodeDocument? Edited => document;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Editor = Part<CodeEditor>();
    }

    /// <summary>Shows a document.</summary>
    /// <param name="code">The document.</param>
    public virtual void Show(CodeDocument code) {
        ArgumentNullException.ThrowIfNull(code);

        if (document is { } previous) {
            previous.Analysed -= Restate;
        }

        document = code;

        // The tokenizer before the buffer: assigning a buffer resets every cached highlighting
        // state, so setting it second would tokenize the whole file twice on open.
        Editor.Tokenizer = code.Tokenizer;
        Editor.Buffer = code.Buffer;

        code.Analysed += Restate;
        Analyse();
    }

    /// <summary>Runs the document's analysis and puts the result in the gutter.</summary>
    /// <returns>How many errors it found.</returns>
    public int Analyse() {
        if (document is not { } code) {
            return 0;
        }

        code.Reanalyse();
        return Errors;
    }

    /// <summary>How many errors the last analysis found.</summary>
    public int Errors {
        get {
            var count = 0;

            foreach (var diagnostic in document?.Diagnostics ?? []) {
                if (diagnostic.Severity == CodeSeverity.Error) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Called after the document has been analysed.</summary>
    /// <param name="code">The document.</param>
    protected virtual void Restate(CodeDocument code) {
        ArgumentNullException.ThrowIfNull(code);
        // A span, so the control does not have to be handed a list it might keep. The document's
        // own list is the one thing that outlives a repaint.
        Editor.SetDiagnostics([.. code.Diagnostics]);
    }
}

/// <summary>A code editor with a pane beside it showing what the file describes.</summary>
/// <remarks>
///     <para>
///         Doc 11's "<c>CodeEditor</c> + live preview pane", for the two file types that have
///         something to show. The two halves are genuinely different — see
///         <see cref="MarkupDocument" /> and <see cref="StyleSheetDocument" /> for why a stylesheet's
///         preview is the real cascade and a component's is its static structure — so the pane says
///         which of the two it is drawing rather than letting them look the same.
///     </para>
///     <para>
///         ⚠ <b>The preview has its own document, and it must.</b> Loading the file's stylesheet into
///         the editor's own <see cref="UiDocument" /> would restyle the editor with the user's game
///         CSS — an author testing a rule that hides everything would hide the editor. A nested
///         document is not something <c>Vixen.Ui</c> supports, so the preview is a subtree with the
///         sheet loaded at author origin and every selector scoped under the pane's own class, which
///         is the containment this can actually offer today. A rule with <c>!important</c> at
///         user-agent origin still escapes it; that is a real hole and the fix is a second
///         <c>UiDocument</c> rendered into a texture.
///     </para>
/// </remarks>
public sealed class PreviewCodeEditorView : CodeEditorView {
    int sheet = -1;

    /// <summary>The pane beside the editor.</summary>
    public UiElement Pane { get; private set; } = null!;

    /// <summary>Where the preview's own elements go.</summary>
    public UiElement Surface { get; private set; } = null!;

    /// <summary>What the pane says about what it is showing.</summary>
    public UiElement Status { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Pane = Part("preview-pane");
        Status = Pane.Add("preview-status");
        Surface = Pane.Add("preview-surface");
    }

    /// <inheritdoc />
    public override void Show(CodeDocument code) {
        base.Show(code);
        Rebuild();
    }

    /// <summary>Rebuilds the preview from the document as it stands.</summary>
    public void Rebuild() {
        while (Surface.Children.Count > 0) {
            Surface.Children[^1].Remove();
        }

        switch (Edited) {
            case MarkupDocument markup:
                Structure(markup);
                break;

            case StyleSheetDocument styles:
                Cascade(styles);
                break;

            default:
                Status.Text = "Nothing here has a preview.";
                break;
        }
    }

    /// <inheritdoc />
    protected override void Restate(CodeDocument code) {
        base.Restate(code);
        Rebuild();
    }

    void Structure(MarkupDocument markup) {
        if (markup.Component is not { } component) {
            Status.Text = "This file declares no component, so there is nothing to lay out.";
            Status.AddClass("errors");

            return;
        }

        Status.RemoveClass("errors");

        Status.Text = $"{component.Name} — structure only. Expressions and @code need the generator.";

        foreach (var node in component.Content) {
            Draw(Surface, node);
        }
    }

    /// <summary>Turns one bound node into elements.</summary>
    /// <remarks>
    ///     ⚠ <b>Every tag becomes a plain element with that tag name</b>, rather than a control of
    ///     that type. Resolving <c>&lt;Button&gt;</c> to <c>Vixen.Ui.Controls.Button</c> means a
    ///     tag-to-type table, and the binder deliberately does not have one — a component may use
    ///     types from any assembly the generated code will reference, which is a set nothing knows
    ///     until the project is compiled. A plain element with the right tag still takes the right
    ///     stylesheet rules, which is most of what the pane is being looked at for.
    /// </remarks>
    static void Draw(UiElement parent, BoundNode node) {
        switch (node) {
            case BoundText text:
                parent.Add("text").Text = text.Text;
                break;

            case BoundInterpolation interpolation:
                // The expression's source, in braces, rather than nothing: an author needs to see
                // that a value goes here and roughly how wide it will be.
                parent.Add("text").Text = "{" + interpolation.Expression.Text + "}";
                break;

            case BoundElement element: {
                var created = parent.Add(element.Tag);

                foreach (var attribute in element.Attributes) {
                    if (string.Equals(attribute.Name, "class", StringComparison.OrdinalIgnoreCase)
                        && attribute.Literal is { } classes) {
                        foreach (var name in classes.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
                            created.AddClass(name);
                        }
                    }
                }

                foreach (var child in element.Children) {
                    Draw(created, child);
                }

                break;
            }

            case BoundIf branch: {
                // Every branch drawn, one after another, because which one is taken is a run-time
                // question and showing none of them would hide most of a conditional component.
                foreach (var arm in branch.Branches) {
                    foreach (var child in arm.Body) {
                        Draw(parent, child);
                    }
                }

                foreach (var child in branch.Else) {
                    Draw(parent, child);
                }

                break;
            }

            case BoundFor loop: {
                // Once, not n times: the collection is a run-time value and repeating a body an
                // arbitrary number of times would be a picture of a guess.
                foreach (var child in loop.Body) {
                    Draw(parent, child);
                }

                break;
            }

            default:
                break;
        }
    }

    void Cascade(StyleSheetDocument styles) {
        Status.RemoveClass("errors");
        Status.Text = "Live cascade over the sample tree below.";

        // A small tree of the tags a stylesheet is most often written against, so there is something
        // for the rules to land on. A pane that showed an empty rectangle until the author wrote
        // markup somewhere else would not be a preview of anything.
        var card = Surface.Add("panel", null, "preview-sample");
        card.Add("text").Text = "Heading";

        var body = card.Add("panel");
        body.Add("text").Text = "Body text in the sample tree.";
        body.Add<Button>().Label = "Button";

        if (sheet < 0) {
            // ⚠ Fully qualified, and it has to be. `Ui.` here is relative to the enclosing `Vixen.`,
            // and the moment this assembly gained a reference that puts `Vixen.Editor.Ui` in scope it
            // bound to that instead — a compile error that names a namespace nobody edited. A
            // relative qualification is a name whose meaning depends on the project file.
            sheet = Document.Load(styles.Text, Vixen.Ui.Styling.StyleOrigin.Author);
        } else {
            // Replaced rather than loaded again: a sheet per keystroke is a style engine that grows
            // for as long as the file is open, and the rules of the ones before it never stop
            // applying.
            Document.Styles.Replace(sheet, styles.Text);
        }
    }

    /// <summary>How many elements the preview built.</summary>
    public int ElementCount => Count(Surface) - 1;

    static int Count(UiElement element) {
        var total = 1;

        foreach (var child in element.Children) {
            total += Count(child);
        }

        return total;
    }
}

/// <summary>Opens a Raven shader.</summary>
public sealed class ShaderEditorFactory : IAssetEditorFactory {
    /// <inheritdoc />
    public string Name => "Shader";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [ShaderDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        return new ShaderDocument(request.Project, request.Asset, request.Path);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        var view = panel.Add<CodeEditorView>();
        view.Show((CodeDocument) document);

        return view;
    }
}

/// <summary>Opens a VXML component or a VCSS stylesheet, with the preview pane beside it.</summary>
public sealed class MarkupEditorFactory : IAssetEditorFactory {
    /// <inheritdoc />
    public string Name => "UI";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [MarkupDocument.Extension, StyleSheetDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        return string.Equals(
            Path.GetExtension(request.Path),
            StyleSheetDocument.Extension,
            StringComparison.OrdinalIgnoreCase
        )
            ? new StyleSheetDocument(request.Project, request.Asset, request.Path)
            : new MarkupDocument(request.Project, request.Asset, request.Path);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        var view = panel.Add<PreviewCodeEditorView>();
        view.Show((CodeDocument) document);

        return view;
    }
}
