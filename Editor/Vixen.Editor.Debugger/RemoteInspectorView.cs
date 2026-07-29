// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.Debugger;

/// <summary>A running build's entities, its counters, and a way to write a value back.</summary>
/// <remarks>
///     <para>
///         <b>Doc 13 calls this "how mobile and console debugging actually happens", and doc 20's E4
///         asks for an entity to be mutated live.</b> The entity tree is the far end's — the editor
///         has no idea what those entities are or which scene they came from — which is why the rows
///         carry a name and a component list rather than anything this process could inspect.
///     </para>
///     <para>
///         ⚠ <b>The write box takes <c>Component.Member</c> and a text value rather than drawing the
///         inspector.</b> Drawing a real inspector needs the far end's type descriptors, which is a
///         second protocol and a schema negotiation between two builds that may differ. What a text
///         field buys is that the exit criterion — an entity mutated live — is met today, over a
///         protocol both ends can implement in an afternoon.
///     </para>
///     <para>
///         ⚠ <b>Polled from the panel's tick rather than from a thread.</b> That is the transport's
///         own contract: nothing is delivered outside <c>Poll</c>, so the entity list is only ever
///         touched on the frame thread and the panel needs no lock.
///     </para>
/// </remarks>
public sealed partial class RemoteInspectorView : Control {
    readonly List<UiElement> counterRows = [];

    Action<RemoteInspectorClient>? onChanged;
    RemoteInspectorClient? client;

    /// <inheritdoc />
    protected override string TagName => "remote-inspector";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The strip along the top.</summary>
    public UiElement Toolbar { get; private set; } = null!;

    /// <summary>Attaches and detaches.</summary>
    public Button AttachButton { get; private set; } = null!;

    /// <summary>Asks for the tree again.</summary>
    public Button RefreshButton { get; private set; } = null!;

    /// <summary>What the connection is doing.</summary>
    public UiElement Status { get; private set; } = null!;

    /// <summary>The entities.</summary>
    public TreeView Tree { get; private set; } = null!;

    /// <summary>The live numbers.</summary>
    public UiElement CounterPane { get; private set; } = null!;

    /// <summary>Which member to write, as <c>Component.Member</c>.</summary>
    public TextBox Member { get; private set; } = null!;

    /// <summary>What to write into it.</summary>
    public TextBox Value { get; private set; } = null!;

    /// <summary>Writes it.</summary>
    public Button Apply { get; private set; } = null!;

    /// <summary>Which entity is selected, or <see langword="null" />.</summary>
    public RemoteEntity? Selected { get; private set; }

    /// <summary>Points the panel at a client.</summary>
    /// <param name="inspector">The client.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inspector" /> is null.</exception>
    public void Show(RemoteInspectorClient inspector) {
        ArgumentNullException.ThrowIfNull(inspector);

        if (client is not null && onChanged is not null) {
            client.Changed -= onChanged;
        }

        client = inspector;
        onChanged ??= _ => Restate();

        inspector.Changed += onChanged;
        Restate();
    }

    /// <summary>Advances the connection.</summary>
    /// <param name="elapsed">How much time has passed.</param>
    public void Tick(TimeSpan elapsed) => client?.Poll(elapsed);

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Toolbar = Part("remote-toolbar");

        AttachButton = Toolbar.Add<Button>();
        AttachButton.Size = ControlSize.Small;
        AttachButton.Variant = ControlVariant.Primary;
        AttachButton.Label = "Attach";

        AttachButton.Clicked += _ => {
            if (client is null) {
                return;
            }

            if (client.State is RemoteState.Detached) {
                client.Attach();
            } else {
                client.Detach();
            }
        };

        RefreshButton = Toolbar.Add<Button>();
        RefreshButton.Size = ControlSize.Small;
        RefreshButton.Variant = ControlVariant.Subtle;
        RefreshButton.Label = "Refresh";
        RefreshButton.Clicked += _ => client?.Refresh();

        Status = Part("remote-status");

        var body = Part("remote-body");

        Tree = body.Add<TreeView>();

        Tree.SelectionChanged += tree => {
            Selected = null;

            foreach (var node in tree.Selection) {
                if (node.Tag is RemoteEntity entity) {
                    Selected = entity;
                    break;
                }
            }

            Editable();
        };

        CounterPane = body.Add<UiElement>("remote-counters");

        var write = Part("remote-write");

        Member = write.Add<TextBox>();
        Member.Placeholder = "Component.Member";
        Member.Size = ControlSize.Small;

        Value = write.Add<TextBox>();
        Value.Placeholder = "Value";
        Value.Size = ControlSize.Small;

        Apply = write.Add<Button>();
        Apply.Size = ControlSize.Small;
        Apply.Label = "Set";

        Apply.Clicked += _ => {
            if (client is { } inspector && Selected is { } entity && Member.Value is { Length: > 0 } member) {
                inspector.SetValue(entity.Id, member, Value.Value ?? string.Empty);
            }
        };

        Member.ValueChanged += (_, _) => Editable();
        Restate();
    }

    /// <inheritdoc />
    protected override void OnRemoved() {
        if (client is not null && onChanged is not null) {
            client.Changed -= onChanged;
        }

        base.OnRemoved();
    }

    void Restate() {
        var attached = client?.State is RemoteState.Attached;

        AttachButton.Label = client?.State is RemoteState.Detached ? "Attach" : "Detach";
        RefreshButton.Disabled = !attached;

        Status.Text = Sentence();

        Entities();
        Counters();
        Editable();
    }

    string Sentence() {
        if (client is not { } inspector) {
            return "No connection configured.";
        }

        return inspector.State switch {
            RemoteState.Detached => "Detached.",
            RemoteState.Attaching => "Attaching…",
            RemoteState.Incompatible => string.Create(
                CultureInfo.InvariantCulture,
                $"'{inspector.BuildName}' speaks protocol {inspector.BuildVersion}; this editor speaks "
                + $"{InspectorProtocol.Version}."
            ),
            _ => string.Create(
                CultureInfo.InvariantCulture,
                $"Attached to {inspector.BuildName} — {inspector.Entities.Count:N0} entit(ies){(inspector.IsFetching ? ", fetching…" : "")}"
            )
        };
    }

    /// <summary>Rebuilds the entity tree from the far end's flat list.</summary>
    /// <remarks>
    ///     ⚠ <b>Parents are resolved by id, and an entity whose parent has not arrived is a root.</b>
    ///     The far end sends its entities in whatever order it walks them, and a build streaming a
    ///     large scene may not send a parent before its child — so a rebuild that waited for the
    ///     parent would drop half the tree.
    /// </remarks>
    void Entities() {
        while (Tree.Root.Children.Count > 0) {
            Tree.Root.Remove(Tree.Root.Children[^1]);
        }

        Selected = null;

        if (client is not { } inspector) {
            Tree.Refresh();
            return;
        }

        Dictionary<ulong, TreeNode> byId = [];

        foreach (var entity in inspector.Entities) {
            byId[entity.Id] = new(Label(entity), entity);
        }

        foreach (var entity in inspector.Entities) {
            var node = byId[entity.Id];

            if (entity.Parent != 0 && byId.TryGetValue(entity.Parent, out var parent)) {
                parent.Add(node);
            } else {
                Tree.Root.Add(node);
            }
        }

        foreach (var node in byId.Values) {
            if (node.HasChildren) {
                Tree.Expand(node);
            }
        }

        Tree.Refresh();
    }

    void Counters() {
        var slot = 0;

        if (client is { } inspector) {
            // Sorted by name, because a dictionary's order is its hashing and a pane of live numbers
            // that reordered itself as values arrived would be unreadable.
            foreach (var name in inspector.Counters.Keys.Order(StringComparer.Ordinal)) {
                while (counterRows.Count <= slot) {
                    var built = CounterPane.Add<UiElement>("remote-counter");

                    built.Add<UiElement>("counter-label");
                    built.Add<UiElement>("counter-value");

                    counterRows.Add(built);
                }

                var row = counterRows[slot++];

                row.RemoveClass("parked");
                row.Children[0].Text = name;
                row.Children[1].Text = inspector.Counters[name].ToString("0.###", CultureInfo.InvariantCulture);
            }
        }

        for (var index = slot; index < counterRows.Count; index++) {
            counterRows[index].AddClass("parked");
        }
    }

    void Editable() =>
        Apply.Disabled = client?.State is not RemoteState.Attached
            || Selected is null
            || Member.Value is not { Length: > 0 };

    static string Label(RemoteEntity entity) =>
        entity.Components.Count == 0
            ? entity.Name
            : entity.Name + "  ·  " + string.Join(", ", entity.Components);
}
