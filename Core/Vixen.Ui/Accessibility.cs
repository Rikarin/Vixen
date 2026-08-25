// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;

namespace Vixen.Ui;

/// <summary>What kind of thing an element is, in the vocabulary assistive technology already knows.</summary>
/// <remarks>
///     <para>
///         <b>The tokens are WAI-ARIA 1.2's role names, PascalCased, and not one of them was invented
///         here.</b> The list a reader can check this against is
///         <see href="https://www.w3.org/TR/wai-aria-1.2/#role_definitions">WAI-ARIA 1.2 § 5.4</see>;
///         the member name is the role token with its words capitalised — <c>menuitemcheckbox</c> is
///         <see cref="MenuItemCheckBox" />, <c>tablist</c> is <see cref="TabList" />. A role this
///         enum does not have yet is added <i>by its ARIA name</i> rather than approximated with a
///         neighbour.
///     </para>
///     <para>
///         ⚠ <b>Why a published vocabulary rather than one shaped to this control set.</b> Every
///         bridge this tree will ever have already maps ARIA: AT-SPI2 has a documented ARIA
///         correspondence, UIA's control types are mapped from ARIA by the HTML-AAM, and
///         <c>NSAccessibility</c>'s roles are what WebKit maps ARIA onto. An enum invented from the
///         controls that happen to exist here would be a third vocabulary that every bridge author
///         has to learn and that no specification can settle an argument about — and
///         <see href="../../docs/plan/09-ui-framework.md">09</see>'s own Testing table asks for an
///         "ARIA-role snapshot", so the name was already chosen.
///     </para>
///     <para>
///         ⚠ <b><see cref="None" /> is ARIA's <c>none</c>, and it is the default for every element on
///         purpose.</b> A layout <c>&lt;div&gt;</c>, a <c>Panel</c>, the box a control draws its
///         focus ring on — these are not things a screen reader should announce, and a tree that
///         reported them would read a form as forty nested groups. An element with
///         <see cref="None" /> is skipped and its children are read in its place, which is what
///         <c>role="none"</c> means on the web and what <see cref="UiElement.IsInAccessibilityTree" />
///         answers.
///     </para>
/// </remarks>
public enum AccessibleRole : ushort {
    /// <summary>ARIA <c>none</c>: not a node in the tree at all. Its children are read in its place.</summary>
    None = 0,

    /// <summary>ARIA <c>alert</c>.</summary>
    Alert,

    /// <summary>ARIA <c>alertdialog</c>.</summary>
    AlertDialog,

    /// <summary>ARIA <c>application</c>.</summary>
    Application,

    /// <summary>ARIA <c>article</c>.</summary>
    Article,

    /// <summary>ARIA <c>banner</c>.</summary>
    Banner,

    /// <summary>ARIA <c>button</c>.</summary>
    Button,

    /// <summary>ARIA <c>cell</c>.</summary>
    Cell,

    /// <summary>ARIA <c>checkbox</c>.</summary>
    CheckBox,

    /// <summary>ARIA <c>columnheader</c>.</summary>
    ColumnHeader,

    /// <summary>ARIA <c>combobox</c>.</summary>
    ComboBox,

    /// <summary>ARIA <c>complementary</c>.</summary>
    Complementary,

    /// <summary>ARIA <c>contentinfo</c>.</summary>
    ContentInfo,

    /// <summary>ARIA <c>dialog</c>.</summary>
    Dialog,

    /// <summary>ARIA <c>document</c>.</summary>
    Document,

    /// <summary>ARIA <c>feed</c>.</summary>
    Feed,

    /// <summary>ARIA <c>figure</c>.</summary>
    Figure,

    /// <summary>ARIA <c>form</c>.</summary>
    Form,

    /// <summary>ARIA <c>grid</c>.</summary>
    Grid,

    /// <summary>ARIA <c>gridcell</c>.</summary>
    GridCell,

    /// <summary>ARIA <c>group</c>.</summary>
    Group,

    /// <summary>ARIA <c>heading</c>.</summary>
    Heading,

    /// <summary>ARIA <c>img</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>Spelled as ARIA spells it, and not <c>Image</c>.</b> The rule this enum keeps is that
    ///     the member name is the role token with its words capitalised and nothing else — which
    ///     makes <c>role.ToString().ToLowerInvariant()</c> the token, exactly, for every member with
    ///     no table of exceptions to fall out of step. One readable name is a cheaper price than a
    ///     mapping that the next role added forgets to appear in.
    /// </remarks>
    Img,

    /// <summary>ARIA <c>link</c>.</summary>
    Link,

    /// <summary>ARIA <c>list</c>.</summary>
    List,

    /// <summary>ARIA <c>listbox</c>.</summary>
    ListBox,

    /// <summary>ARIA <c>listitem</c>.</summary>
    ListItem,

    /// <summary>ARIA <c>log</c>.</summary>
    Log,

    /// <summary>ARIA <c>main</c>.</summary>
    Main,

    /// <summary>ARIA <c>menu</c>.</summary>
    Menu,

    /// <summary>ARIA <c>menubar</c>.</summary>
    MenuBar,

    /// <summary>ARIA <c>menuitem</c>.</summary>
    MenuItem,

    /// <summary>ARIA <c>menuitemcheckbox</c>.</summary>
    MenuItemCheckBox,

    /// <summary>ARIA <c>menuitemradio</c>.</summary>
    MenuItemRadio,

    /// <summary>ARIA <c>navigation</c>.</summary>
    Navigation,

    /// <summary>ARIA <c>note</c>.</summary>
    Note,

    /// <summary>ARIA <c>option</c>.</summary>
    Option,

    /// <summary>ARIA <c>progressbar</c>.</summary>
    ProgressBar,

    /// <summary>ARIA <c>radio</c>.</summary>
    Radio,

    /// <summary>ARIA <c>radiogroup</c>.</summary>
    RadioGroup,

    /// <summary>ARIA <c>region</c>.</summary>
    Region,

    /// <summary>ARIA <c>row</c>.</summary>
    Row,

    /// <summary>ARIA <c>rowgroup</c>.</summary>
    RowGroup,

    /// <summary>ARIA <c>rowheader</c>.</summary>
    RowHeader,

    /// <summary>ARIA <c>scrollbar</c>.</summary>
    ScrollBar,

    /// <summary>ARIA <c>search</c>.</summary>
    Search,

    /// <summary>ARIA <c>searchbox</c>.</summary>
    SearchBox,

    /// <summary>ARIA <c>separator</c>.</summary>
    Separator,

    /// <summary>ARIA <c>slider</c>.</summary>
    Slider,

    /// <summary>ARIA <c>spinbutton</c>.</summary>
    SpinButton,

    /// <summary>ARIA <c>status</c>.</summary>
    Status,

    /// <summary>ARIA <c>switch</c>.</summary>
    Switch,

    /// <summary>ARIA <c>tab</c>.</summary>
    Tab,

    /// <summary>ARIA <c>table</c>.</summary>
    Table,

    /// <summary>ARIA <c>tablist</c>.</summary>
    TabList,

    /// <summary>ARIA <c>tabpanel</c>.</summary>
    TabPanel,

    /// <summary>ARIA <c>textbox</c>.</summary>
    TextBox,

    /// <summary>ARIA <c>timer</c>.</summary>
    Timer,

    /// <summary>ARIA <c>toolbar</c>.</summary>
    Toolbar,

    /// <summary>ARIA <c>tooltip</c>.</summary>
    Tooltip,

    /// <summary>ARIA <c>tree</c>.</summary>
    Tree,

    /// <summary>ARIA <c>treegrid</c>.</summary>
    TreeGrid,

    /// <summary>ARIA <c>treeitem</c>.</summary>
    TreeItem
}

/// <summary>The conditions a screen reader announces alongside a role and a name.</summary>
/// <remarks>
///     <para>
///         <b>Named after .NET's own <c>System.Windows.Forms.AccessibleStates</c> and populated from
///         ARIA's state attributes</b> — <c>aria-checked</c>, <c>aria-expanded</c>,
///         <c>aria-selected</c>, <c>aria-readonly</c>, <c>aria-required</c>, <c>aria-invalid</c>,
///         <c>aria-busy</c>, <c>aria-modal</c>, <c>aria-multiselectable</c>, <c>aria-multiline</c>,
///         <c>aria-pressed</c>. The four with no ARIA attribute behind them — <see cref="Focused" />,
///         <see cref="Focusable" />, <see cref="Editable" /> and <see cref="Expandable" /> — are
///         AT-SPI2 state names, because ARIA expresses those three by <i>omitting</i> an attribute
///         and an in-process tree has to be able to say them.
///     </para>
///     <para>
///         ⚠ <b><see cref="Expandable" /> is separate from <see cref="Expanded" /> because
///         <c>aria-expanded</c> has three values and a flag has two.</b> Absent means "this does not
///         expand"; <c>false</c> means "it does and it is closed". Folding them would make every
///         button in the document report itself as a collapsed disclosure.
///     </para>
///     <para>
///         ⚠ <b><see cref="Checked" /> and <see cref="Pressed" /> are not the same state and the
///         difference is not cosmetic.</b> A checkbox is <c>aria-checked</c>; a toggle button in a
///         toolbar is <c>aria-pressed</c>, and a screen reader says "pressed"/"not pressed" for one
///         and "ticked"/"unticked" for the other. Both are <see cref="ElementState.Checked" /> in the
///         cascade, which is why the accessible state is <i>computed</i> by the control rather than
///         read off the style flags.
///     </para>
/// </remarks>
[Flags]
public enum AccessibleStates : uint {
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>It refuses input. ARIA <c>aria-disabled</c>.</summary>
    Disabled = 1 << 0,

    /// <summary>The keyboard focus is on it. AT-SPI <c>FOCUSED</c>.</summary>
    Focused = 1 << 1,

    /// <summary>The keyboard focus could be on it. AT-SPI <c>FOCUSABLE</c>.</summary>
    Focusable = 1 << 2,

    /// <summary>A checkbox, radio or switch that is on. ARIA <c>aria-checked="true"</c>.</summary>
    Checked = 1 << 3,

    /// <summary>A checkbox whose parts disagree. ARIA <c>aria-checked="mixed"</c>.</summary>
    Mixed = 1 << 4,

    /// <summary>A toggle button that is in. ARIA <c>aria-pressed="true"</c>.</summary>
    Pressed = 1 << 5,

    /// <summary>A tab, option or row that is the chosen one. ARIA <c>aria-selected="true"</c>.</summary>
    Selected = 1 << 6,

    /// <summary>It has an <c>aria-expanded</c> at all — a disclosure, a combo box, a tree item.</summary>
    Expandable = 1 << 7,

    /// <summary>And it is open. ARIA <c>aria-expanded="true"</c>. Meaningless without <see cref="Expandable" />.</summary>
    Expanded = 1 << 8,

    /// <summary>Text can be typed into it. AT-SPI <c>EDITABLE</c>.</summary>
    Editable = 1 << 9,

    /// <summary>Its value can be read and copied but not changed. ARIA <c>aria-readonly</c>.</summary>
    ReadOnly = 1 << 10,

    /// <summary>A value must be supplied. ARIA <c>aria-required</c>.</summary>
    Required = 1 << 11,

    /// <summary>The value it holds is not acceptable. ARIA <c>aria-invalid</c>.</summary>
    Invalid = 1 << 12,

    /// <summary>It is being filled in and should not be announced yet. ARIA <c>aria-busy</c>.</summary>
    Busy = 1 << 13,

    /// <summary>Nothing outside it can be reached while it is up. ARIA <c>aria-modal</c>.</summary>
    Modal = 1 << 14,

    /// <summary>More than one of its children can be chosen at once. ARIA <c>aria-multiselectable</c>.</summary>
    MultiSelectable = 1 << 15,

    /// <summary>Its value may contain newlines. ARIA <c>aria-multiline</c>.</summary>
    MultiLine = 1 << 16
}

/// <summary>The kinds of link between two elements that the tree cannot work out for itself.</summary>
/// <remarks>
///     <para>
///         <b>Every one of these is a pairing that parent-and-child is the wrong shape for</b>, and
///         that is the whole reason relations exist as a separate thing. A tab is in a strip and the
///         panel it shows is somewhere else entirely; a <c>Select</c>'s list of options is a child of
///         the document <i>root</i>, because a popover inside the field that opens it would be
///         clipped by every scrolling ancestor between the two. No walk over
///         <see cref="UiElement.Parent" /> can recover either fact.
///     </para>
///     <para>
///         The names are ARIA's relationship attributes with the <c>aria-</c> prefix dropped:
///         <c>aria-labelledby</c>, <c>aria-describedby</c>, <c>aria-controls</c>, <c>aria-owns</c>,
///         <c>aria-flowto</c>, <c>aria-activedescendant</c>.
///     </para>
/// </remarks>
public enum AccessibleRelation : byte {
    /// <summary>The target's text is this element's name. ARIA <c>aria-labelledby</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>Read by <see cref="UiElement.AccessibleName" /> rather than merely reported.</b> A
    ///     text field has no name of its own — the words beside it are somebody else's element — so
    ///     this relation is how a field gets one, and it is the only relation with an effect on
    ///     another property.
    /// </remarks>
    LabelledBy,

    /// <summary>The target's text is this element's description. ARIA <c>aria-describedby</c>.</summary>
    DescribedBy,

    /// <summary>Operating this element changes the target. ARIA <c>aria-controls</c>.</summary>
    Controls,

    /// <summary>The target is a child of this element in the accessibility tree but not in the element tree. ARIA <c>aria-owns</c>.</summary>
    Owns,

    /// <summary>Where the reading order goes next, when it is not the next sibling. ARIA <c>aria-flowto</c>.</summary>
    FlowsTo,

    /// <summary>The target is what the focus is "on" while this element holds it. ARIA <c>aria-activedescendant</c>.</summary>
    ActiveDescendant
}

/// <summary>One relation and the element on the other end of it.</summary>
/// <param name="Relation">What kind of link it is.</param>
/// <param name="Target">The element it points at.</param>
public readonly record struct AccessibleRelationship(AccessibleRelation Relation, UiElement Target);

/// <summary>Everything an element carries for the accessibility tree, allocated only if it carries any.</summary>
/// <remarks>
///     ⚠ <b>One object behind one nullable reference, on <c>CommandBindings</c>' terms and for its
///     reasons.</b> A real interface is 10⁴ elements and the overwhelming majority of them declare
///     nothing here at all — a control's role, name, value and state come from
///     <see cref="UiElement.NativeRole" /> and its three companions, which are virtual members and
///     cost no storage whatever. So the price of the feature on an element that never uses it is the
///     eight bytes of the reference, and there is no allocation.
/// </remarks>
sealed class AccessibleNode {
    public AccessibleRole? Role;
    public string? Name;
    public string? Description;
    public string? Value;
    public AccessibleStates Declared;
    public List<AccessibleRelationship>? Relations;
}

public partial class UiElement {
    // ⚠ One nullable reference, and everything an *author* can declare about accessibility lives
    // behind it. What a *control* declares lives in the four virtual members below and costs
    // nothing at all — see `AccessibleNode`.
    AccessibleNode? accessible;

    /// <summary>What kind of thing this element is, for assistive technology.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Defaults to <see cref="NativeRole" />, which is what makes the control set work
    ///         without an application writing anything.</b> A <c>Button</c> is
    ///         <see cref="AccessibleRole.Button" /> because it is a <c>Button</c>, not because
    ///         somebody remembered. Setting this overrides the native role exactly as the web's
    ///         <c>role</c> attribute overrides an element's implicit one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Setting it to <see cref="AccessibleRole.None" /> is a real answer, not a
    ///         clear.</b> "This <c>Card</c> is decoration, read straight through it" has to be
    ///         sayable, and it is different from "take the native role back" — which is what
    ///         <see cref="ClearRole" /> is for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not a <c>[UiProperty]</c>, on <see cref="CommandScope" />'s terms.</b> A role is
    ///         not something a stylesheet has any business selecting on or setting, and an
    ///         inheriting UI property would cost every element in the document a value field and an
    ///         is-set flag for something almost none of them override.
    ///     </para>
    /// </remarks>
    public AccessibleRole Role {
        get => accessible?.Role ?? NativeRole;
        set {
            if (accessible?.Role == value) {
                return;
            }

            (accessible ??= new AccessibleNode()).Role = value;
            InvalidateAccessibility();
        }
    }

    /// <summary>Hands the role back to <see cref="NativeRole" />.</summary>
    /// <remarks>Distinct from assigning <see cref="AccessibleRole.None" />, which is itself a role.</remarks>
    public void ClearRole() {
        if (accessible?.Role is null) {
            return;
        }

        accessible.Role = null;
        InvalidateAccessibility();
    }

    /// <summary>Whether this element is a node a screen reader sees.</summary>
    /// <remarks>
    ///     <b>Everything a layout is made of answers <c>false</c>, and that is the design rather than
    ///     an oversight.</b> A row, a column, a <c>Panel</c>, the part a control draws its focus ring
    ///     on — a tree that reported all of those would read a four-field form as thirty nested
    ///     groups, which is the commonest way an accessibility tree is technically complete and
    ///     useless. A bridge walks the element tree and emits only these, hoisting the children of
    ///     everything else.
    /// </remarks>
    public bool IsInAccessibilityTree => Role != AccessibleRole.None;

    /// <summary>What this element is called, as a screen reader would say it.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Three sources, in the order ARIA's accessible-name computation asks them.</b> An
    ///         explicit name set here wins; failing that the text of whatever this element is
    ///         <see cref="AccessibleRelation.LabelledBy" />; failing that
    ///         <see cref="NativeAccessibleName" />, which for a button is its label and for a plain
    ///         element is its own <see cref="Text" />. The full algorithm in
    ///         <see href="https://www.w3.org/TR/accname-1.2/">accname 1.2</see> has more steps than
    ///         these and they are the three that decide the answer for a control set.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A field's name is somebody else's element, which is why the relation is in the
    ///         middle of this and not an afterthought.</b> A <c>TextBox</c> has no words of its own —
    ///         its placeholder is not a name, and treating it as one is how a form ends up announcing
    ///         four fields all called "0.00". So a labelled field is one
    ///         <c>AddAccessibleRelation(AccessibleRelation.LabelledBy, label)</c>, and an unlabelled
    ///         one honestly reports <c>null</c> so that a gate can fail it.
    ///     </para>
    /// </remarks>
    public string? AccessibleName {
        get {
            if (accessible?.Name is { } declared) {
                return declared;
            }

            if (RelationText(AccessibleRelation.LabelledBy) is { } labelled) {
                return labelled;
            }

            return NativeAccessibleName;
        }
        set {
            if (accessible is null && value is null) {
                return;
            }

            if (string.Equals(accessible?.Name, value, StringComparison.Ordinal)) {
                return;
            }

            (accessible ??= new AccessibleNode()).Name = value;
            InvalidateAccessibility();
        }
    }

    /// <summary>The longer sentence a screen reader reads after the name, if there is one.</summary>
    /// <remarks>
    ///     Falls back to the text of whatever this element is
    ///     <see cref="AccessibleRelation.DescribedBy" />, which is how a field's help text reaches it
    ///     without being part of its name.
    /// </remarks>
    public string? AccessibleDescription {
        get => accessible?.Description ?? RelationText(AccessibleRelation.DescribedBy);
        set {
            if (accessible is null && value is null) {
                return;
            }

            if (string.Equals(accessible?.Description, value, StringComparison.Ordinal)) {
                return;
            }

            (accessible ??= new AccessibleNode()).Description = value;
            InvalidateAccessibility();
        }
    }

    /// <summary>What this element currently holds, for the kinds of element that hold something.</summary>
    /// <remarks>
    ///     <para>
    ///         A text field's text, a slider's number, the label of the option a <c>Select</c> is
    ///         showing. <c>null</c> for everything that is an action rather than a value — a button
    ///         does not have one, and reporting the empty string would make a screen reader say so.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A string, not an object.</b> This is what is <i>announced</i>, and every
    ///         platform's accessibility API takes text here. A slider that wants "40 percent" rather
    ///         than "0.4" formats it in <see cref="NativeAccessibleValue" />, which is where the
    ///         control's own knowledge of its units is.
    ///     </para>
    /// </remarks>
    public string? AccessibleValue {
        get => accessible?.Value ?? NativeAccessibleValue;
        set {
            if (accessible is null && value is null) {
                return;
            }

            if (string.Equals(accessible?.Value, value, StringComparison.Ordinal)) {
                return;
            }

            (accessible ??= new AccessibleNode()).Value = value;
            InvalidateAccessibility();
        }
    }

    /// <summary>The conditions to announce with this element: what it is doing, plus whatever was declared on it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Computed on every read, and never stored — which is the single most important
    ///         decision on this type.</b> Three of these bits are facts the framework already knows
    ///         and the control does not: <see cref="AccessibleStates.Disabled" /> is
    ///         <see cref="ElementState.Disabled" />, <see cref="AccessibleStates.Focused" /> is
    ///         <see cref="ElementState.Focus" />, and <see cref="AccessibleStates.Focusable" /> is
    ///         <see cref="Focusable" />. A design in which each control mirrored those into a field
    ///         of its own would be a second copy of the truth, updated by whichever of the fifty
    ///         controls remembered — and the symptom of a missed one is a screen reader saying a
    ///         greyed button is available, which nobody writing the control would ever see.
    ///     </para>
    ///     <para>
    ///         The rest is <see cref="NativeAccessibleState" />, which each control computes from
    ///         what it already holds, or'd with <see cref="DeclaredAccessibleState" />, which is what
    ///         an application says about an element the framework knows nothing about.
    ///     </para>
    /// </remarks>
    public AccessibleStates AccessibleState {
        get {
            var states = NativeAccessibleState | (accessible?.Declared ?? AccessibleStates.None);
            var element = State;

            if ((element & ElementState.Disabled) != 0) {
                states |= AccessibleStates.Disabled;
            }

            if ((element & ElementState.Focus) != 0) {
                states |= AccessibleStates.Focused;
            }

            if (Focusable) {
                states |= AccessibleStates.Focusable;
            }

            return states;
        }
    }

    /// <summary>The bits an application declared on this element itself.</summary>
    /// <remarks>
    ///     For the states no control can know about: a field an application has decided is
    ///     <see cref="AccessibleStates.Required" />, a panel it has marked
    ///     <see cref="AccessibleStates.Busy" /> while it loads. Or'd into
    ///     <see cref="AccessibleState" />; never subtracted from it, because a control saying it is
    ///     read-only is not a thing a caller should be able to deny.
    /// </remarks>
    public AccessibleStates DeclaredAccessibleState {
        get => accessible?.Declared ?? AccessibleStates.None;
        set {
            if (accessible is null && value == AccessibleStates.None) {
                return;
            }

            if (accessible?.Declared == value) {
                return;
            }

            (accessible ??= new AccessibleNode()).Declared = value;
            InvalidateAccessibility();
        }
    }

    /// <summary>Every relation declared on this element, in the order they were added.</summary>
    /// <remarks>An element with none answers an empty list without allocating one.</remarks>
    public IReadOnlyList<AccessibleRelationship> AccessibleRelationships =>
        accessible?.Relations ?? (IReadOnlyList<AccessibleRelationship>) [];

    /// <summary>The first element this one is related to in a given way, if there is one.</summary>
    /// <param name="relation">The kind of link.</param>
    /// <returns>The target, or <c>null</c>.</returns>
    /// <remarks>
    ///     The question nearly every caller has — <c>aria-controls</c>, <c>aria-activedescendant</c>
    ///     and <c>aria-owns</c> are one target in practice — and it allocates nothing.
    /// </remarks>
    public UiElement? AccessibleRelationTarget(AccessibleRelation relation) {
        if (accessible?.Relations is not { } relations) {
            return null;
        }

        foreach (var relationship in relations) {
            if (relationship.Relation == relation) {
                return relationship.Target;
            }
        }

        return null;
    }

    /// <summary>Says that this element is related to another one in a way the tree does not show.</summary>
    /// <param name="relation">The kind of link.</param>
    /// <param name="target">The element on the other end.</param>
    /// <exception cref="ArgumentNullException"><paramref name="target" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="target" /> is this element.</exception>
    /// <remarks>
    ///     ⚠ <b>Adding the same relation twice is a no-op rather than a duplicate.</b> A control that
    ///     re-establishes its relations when something about it changes — a <c>Select</c> pointing at
    ///     the option under the keyboard — would otherwise grow a list for as long as the user held
    ///     an arrow key down.
    /// </remarks>
    public void AddAccessibleRelation(AccessibleRelation relation, UiElement target) {
        ArgumentNullException.ThrowIfNull(target);

        if (ReferenceEquals(target, this)) {
            throw new ArgumentException(
                "an element cannot be related to itself — a relation is for the pairings the tree does not already show",
                nameof(target)
            );
        }

        var node = accessible ??= new AccessibleNode();
        var relations = node.Relations ??= [];
        var relationship = new AccessibleRelationship(relation, target);

        if (relations.Contains(relationship)) {
            return;
        }

        relations.Add(relationship);
        InvalidateAccessibility();
    }

    /// <summary>Stops saying so.</summary>
    /// <param name="relation">The kind of link.</param>
    /// <param name="target">The element on the other end.</param>
    /// <returns>Whether the relation was there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target" /> is null.</exception>
    public bool RemoveAccessibleRelation(AccessibleRelation relation, UiElement target) {
        ArgumentNullException.ThrowIfNull(target);

        if (accessible?.Relations is not { } relations) {
            return false;
        }

        if (!relations.Remove(new AccessibleRelationship(relation, target))) {
            return false;
        }

        InvalidateAccessibility();

        return true;
    }

    /// <summary>Drops every relation of one kind, whatever it pointed at.</summary>
    /// <param name="relation">The kind of link.</param>
    /// <returns>How many went.</returns>
    /// <remarks>
    ///     What a control calls before re-pointing a single-target relation — an
    ///     <see cref="AccessibleRelation.ActiveDescendant" /> follows the selection and must not
    ///     accumulate the elements it used to be.
    /// </remarks>
    public int ClearAccessibleRelations(AccessibleRelation relation) {
        if (accessible?.Relations is not { } relations) {
            return 0;
        }

        var removed = relations.RemoveAll(relationship => relationship.Relation == relation);

        if (removed > 0) {
            InvalidateAccessibility();
        }

        return removed;
    }

    /// <summary>The role this <i>kind</i> of element has when nobody says otherwise.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A virtual rather than a field assigned in <c>OnCreated</c>, and that is what makes
    ///         the role of every control in the set free.</b> A field would be four bytes on every
    ///         element in the document and a line in fifty constructors, one of which would be
    ///         forgotten. A virtual is a slot in a vtable that already exists, is answered by the
    ///         type rather than by the instance, and cannot be omitted by a control that forgot —
    ///         because the control that forgot inherits its base's answer, which for a
    ///         <c>ButtonBase</c> is already <see cref="AccessibleRole.Button" />.
    ///     </para>
    ///     <para>
    ///         <see cref="AccessibleRole.None" /> here, because a bare element is a box.
    ///     </para>
    /// </remarks>
    protected internal virtual AccessibleRole NativeRole => AccessibleRole.None;

    /// <summary>What this <i>kind</i> of element calls itself, from what it already holds.</summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="Text" /> here, which is ARIA's "name from content" for the simple case: an
    ///         element with words in it is named by its words. A control whose words are on a child
    ///         part — which is every <c>Vixen.Ui.Controls</c> control, because an element with text
    ///         may not have children — overrides this with the part it put them on.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Read, never written, and there is no invalidation to remember.</b> A button whose
    ///         label changes has a different accessible name from that instant, with nothing to
    ///         subscribe to and nothing to keep in step. What a bridge needs — that <i>something</i>
    ///         changed this frame — is <see cref="UiDocument.AccessibilityInvalidated" />, and a
    ///         label change reaches it through the restyle it was already causing.
    ///     </para>
    /// </remarks>
    protected internal virtual string? NativeAccessibleName => Text;

    /// <summary>What this <i>kind</i> of element currently holds, formatted the way it should be said.</summary>
    /// <remarks><c>null</c> here: a box holds nothing.</remarks>
    protected internal virtual string? NativeAccessibleValue => null;

    /// <summary>The states this <i>kind</i> of element computes from what it already holds.</summary>
    /// <remarks>
    ///     <para>
    ///         A checkbox reads its own <c>IsChecked</c>; a text field reads its own <c>ReadOnly</c>.
    ///         Neither stores an accessibility flag and neither can be caught out of step with
    ///         itself.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Do not return <see cref="AccessibleStates.Disabled" />,
    ///         <see cref="AccessibleStates.Focused" /> or <see cref="AccessibleStates.Focusable" />
    ///         from an override.</b> <see cref="AccessibleState" /> adds all three from the element's
    ///         own state and focus flags, for every element, whether or not its type overrode
    ///         anything.
    ///     </para>
    /// </remarks>
    protected internal virtual AccessibleStates NativeAccessibleState => AccessibleStates.None;

    /// <summary>The text of the first element this one points at with a given relation.</summary>
    /// <remarks>
    ///     ⚠ <b>The target's <see cref="AccessibleName" /> rather than its <see cref="Text" />.</b> A
    ///     label may itself be a control — a <c>TextBlock</c> keeps its words on a part — so reading
    ///     the raw text would get the empty string from exactly the elements most likely to be used
    ///     as labels. The recursion terminates because a label is not labelled by anything.
    /// </remarks>
    string? RelationText(AccessibleRelation relation) =>
        AccessibleRelationTarget(relation) is { } target ? target.AccessibleName : null;

    /// <summary>Tells the document that something a bridge is showing may have changed.</summary>
    /// <remarks>
    ///     ⚠ <b><c>document</c> and not <see cref="Document" />.</b> Half of what a control declares
    ///     about itself is declared before the element is in a document at all — markup builds an
    ///     element and then binds it — and the property throws for exactly that case. There is
    ///     nothing to invalidate on a detached element and no document to tell.
    /// </remarks>
    void InvalidateAccessibility() => document?.InvalidateAccessibility();
}

public sealed partial class UiDocument {
    bool accessibilityDirty;

    /// <summary>Raised at most once a frame when anything in the accessibility tree may have changed.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Deliberately the same object as <see cref="CommandsInvalidated" />, one field over,
    ///         and it is a second instance of that pattern rather than a second mechanism.</b> A flag
    ///         set as often as anybody likes, read once from <see cref="Tick" />, cleared before the
    ///         handlers run. If the coalescing of one is ever wrong the coalescing of the other is
    ///         wrong in the same way, which is worth more than two designs that are each right for
    ///         now.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Coalescing is the entire point, and for this consumer more than for the
    ///         other.</b> AT-SPI is a chatty protocol over a bus: an event per mutation is a round
    ///         trip per node, and a panel that builds four hundred elements would post four hundred
    ///         of them before the first frame was drawn. One raise per frame is what lets a bridge
    ///         diff its cached tree once and send what actually differs.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It says <i>that</i> something changed and never <i>what</i>.</b> The set of
    ///         changed nodes would have to be accumulated per mutation, which is the allocation this
    ///         exists to avoid, and a bridge holds a cached tree it has to diff anyway — 45 § step 5
    ///         reached the same conclusion for the same reason, and it makes the API cheaper rather
    ///         than poorer.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Raised from <see cref="Tick" /> rather than from <see cref="Update" /></b>, on
    ///         <see cref="CommandsInvalidated" />'s terms: <c>Update</c> returns early when nothing
    ///         dirtied the document, and a focus move or an accessible name being set is not a thing
    ///         that dirties one. <c>Tick</c> is the call a host must make every frame whether
    ///         anything happened or not.
    ///     </para>
    ///     <para>
    ///         Subscribe in <c>OnCreated</c> and unsubscribe in <c>OnRemoved</c>, as
    ///         <see cref="Ticked" />'s remarks say.
    ///     </para>
    /// </remarks>
    public event Action<UiDocument>? AccessibilityInvalidated;

    /// <summary>Says that something a screen reader would announce may have changed.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Called for you by everything the framework can see</b>: setting
    ///         <see cref="UiElement.Role" />, <see cref="UiElement.AccessibleName" />,
    ///         <see cref="UiElement.AccessibleDescription" />, <see cref="UiElement.AccessibleValue" />
    ///         or <see cref="UiElement.DeclaredAccessibleState" />; adding or removing a relation;
    ///         attaching or detaching an element; and the focus moving.
    ///     </para>
    ///     <para>
    ///         What it cannot see is a control's own state changing — a checkbox being ticked is a
    ///         field on the checkbox, and <see cref="UiElement.NativeAccessibleState" /> reads it on
    ///         demand rather than being told. Those changes reach a bridge through the restyle they
    ///         already cause; a control whose accessible view changed without any of that happening
    ///         says so here, in one line.
    ///     </para>
    ///     <para>Free to call as often as you like: it sets a flag.</para>
    /// </remarks>
    public void InvalidateAccessibility() => accessibilityDirty = true;

    /// <summary>Raises the coalesced invalidation, if anything asked for one since the last frame.</summary>
    /// <remarks>
    ///     ⚠ The flag is cleared before the handlers run, for
    ///     <c>RaiseCommandsInvalidated</c>'s reason: a handler is entitled to invalidate again, and
    ///     clearing afterwards would swallow it.
    /// </remarks>
    void RaiseAccessibilityInvalidated() {
        if (!accessibilityDirty) {
            return;
        }

        accessibilityDirty = false;
        AccessibilityInvalidated?.Invoke(this);
    }

    /// <summary>Drops the invalidation subscribers, so a closed document holds nothing it was lent.</summary>
    /// <remarks>
    ///     <c>ReleaseCommandResponders</c>' second paragraph, exactly: the subscribers are controls,
    ///     a control reaches its subtree, and a host that kept a disposed document in a field would
    ///     otherwise keep the whole tree that was hung off it.
    /// </remarks>
    void ReleaseAccessibilitySubscribers() => AccessibilityInvalidated = null;
}
