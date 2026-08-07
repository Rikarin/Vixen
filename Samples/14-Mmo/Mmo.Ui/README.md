# Mmo.Ui

The game's interface: eight components in VXML, one theme file, and a stylesheet that contains only
the utilities the markup actually mentions.

This is doc 28's **G-Q2** and task #38 — the last of `Samples/14-Mmo`'s four deliverables, and the
first assembly in the repository outside the editor whose interface is written in the markup language
the UI framework is for.

## What is here

| | |
|---|---|
| `Theme/vixen.ui.vcss` | The design tokens, as an `@theme` block layered over the palette the engine ships. One file, and "the accent is teal now" is one line of it. |
| `Theme/hud.vcss` | The handful of rules a utility class cannot say, and nothing else. |
| `Theme/MmoStyles.cs` | Tokens + scan + generate, into one sheet. |
| `HudModel.cs` | What the panels bind to. Signals all the way down. |
| `UnitFrame` | Name, level, health, resource, an elite mark and a cast bar. Used for the player and for their target. |
| `ActionBar` | Twelve slots with keybinds, costs and a cooldown sweep. |
| `QuestTracker` | Tracked quests, their stage, and objectives with counts. |
| `BagGrid` | Squares with a rarity border and a stack count. |
| `VendorPanel` | Stock with a price, an affordability check and a requirement gate. |
| `ChatPanel` | Channels, speakers, lines, and a bounded log. |
| `LootRoll` | Need, greed, pass, and a clock. |
| `Hud` | The composition, and the only file that knows the layout. |

Seventy tests, over a real `UiDocument`, with no GPU and no window.

## The Tailwind half

**Everything a class name can say is a class name.** The utility system is
[`Vixen.Ui.Styling.Utilities`](../../../Core/Vixen.Ui.Styling.Utilities/README.md), and this is its
first consumer — the editor does not use it yet. What that bought, concretely: `hud.vcss` is sixty
lines and every one of them is a rule that could not have been a class.

**Only what is used is emitted.** Every family crossed with every token is a stylesheet in the tens
of megabytes, so the generator is given the class names the markup mentions and emits those.

⚠ **The scan happens at startup here and belongs in a build step.** The utilities README lists build
integration as waiting on the asset pipeline; until it lands, something has to glob the source. This
sample embeds its own `.vxml` and scans those with the same `CandidateScanner` a build step would
use. It costs a few milliseconds, once.

⚠ **A class name assembled at run time is invisible to the scanner, and that is the one place the
design costs something.** `$"border-{rarity}"` is `border-` and a variable; `border-storied` appears
nowhere and no rule is emitted for it. Two answers, both used here:

- **Write the whole name in a switch.** `ActionBar.Cell` returns three complete class lists rather
  than composing one from a state — longer, and the scanner can read it.
- **Safelist it.** `MmoStyles.Safelist` names the five rarity colours, because four different panels
  colour by them and a closed set is exactly what a safelist is for.

⚠ **The scanner is over-inclusive on purpose**, so `UtilityGenerator.Unrecognised` is mostly prose
out of comments and cannot be asserted on directly. What *can* be asserted is the set actually
written in a `class` attribute: `StylesheetTests` pulls those out and requires each one to be either
a utility the theme can emit or a rule in `hud.vcss`. It caught `rounded-t-md` — a real Tailwind
utility, not one this engine has — which would have silently done nothing.

## Five things VXML will teach you in the first hour

**A component parameter must be signal-backed.** `BuildContext.Child<T>` constructs a component,
mounts it — which runs `Build` — and assigns its parameters *afterwards*. So every effect has already
read the property once, with the default, and a plain `{ get; set; }` assigned later notifies nobody:
the panel draws an empty model for ever, silently, with no error anywhere. Every parameter here is

```csharp
readonly Signal<UnitModel> unit = new(new());

public UnitModel Unit {
    get => unit.Value;
    set => unit.Value = value;
}
```

which is the same shape `Vixen.Editor.Ui`'s `TaskCenter` uses, for the same reason.

**There is no `[Parameter]` attribute.** The markup README's example shows one; nothing in the
repository defines it. A parameter is a settable property, and the emitter assigns it by name.

**A parameter cannot be `required … init`,** because `Child<T>` is constrained to `new()` and assigns
afterwards. A default instance is what makes a component built with no parameters draw an empty panel
rather than throw.

**A pattern variable does not cross a `@if`.** `@if (Model.Target.Value is { } target)` compiles the
condition into an effect and the body into a separate build, so `target` is not in scope inside the
branch. The C# compiler says so at the right character of the `.vxml` — which is the whole bargain of
emitting expressions under a `#line` rather than typechecking them on the markup side — but the fix
is a property, not a pattern.

**Text content is a child element.** `<frame-name>@Unit.Name.Value</frame-name>` emits
`ctx.Text(parent, …)`, which makes a `<text>` element *under* `frame-name`. A test asserts on
`frame-name text`, and a run of content with two expressions in it makes two of them. It is also why
colouring text from the parent works at all: `color` inherits, and the child is what draws.

## What the panels are careful about

⚠ **A vendor shows a price for one refusal and a reason for the other.** "You cannot afford it" is a
number a player can change today; "you are not Honoured enough with the Ashfen Company" is a reason.
Greying both out identically is the interface deciding the two failures are the same.

⚠ **Nothing here spends, awards or resolves.** The vendor panel raises `Bought`; the loot roll closes
its own window and tells the realm what was chosen. A client that took gold off a purse locally would
have invented a second economy beside doc 28's — *"every transaction is one balanced, idempotent
intent"* — and one that awarded the item would disagree with the server every time two people need at
once, which is most of the times a roll matters.

⚠ **A zero maximum is guarded rather than trusted.** `Vitals` arrives before whatever would have
filled it in at least once a session, and `Health / 0` is a bar whose fraction is `NaN` — which lays
out as a panel the width of the screen, exactly once, at the moment a player targets something.

⚠ **A cast bar exists only while something is being cast.** One that is always there and usually
empty is one players stop reading, and it is two more rectangles a frame for every nameplate in a
world that can have forty of them.

⚠ **A finished objective is coloured, not removed.** Removing it makes the list jump under the cursor
at the moment the player is reading it, and loses the feedback that says the last kill counted.

⚠ **The chat log is bounded.** It is the other unbounded set in a client — same shape as the realm's
idempotency-key horizon (#43), much smaller stakes: dropping the oldest line loses a joke rather than
an item.

## What is not here

**Nothing draws this.** `Vixen.Ui.Renderer` needs a device and a swapchain, and
[`Samples/02-HelloUi`](../../02-HelloUi/README.md) is the sample that stands one up in ninety lines
of `Program.cs`. Joining the two is a window, not a design — and keeping them apart is what lets this
assembly's whole test suite run in a third of a second on a machine with no GPU.

**Nothing connects it.** `Mmo.Client` is headless by design and says so; the binder that fills a
`HudModel` from replicated components and `MmoLibraries` is where the two would meet. The models are
deliberately shaped so that binder is a loop of assignments and nothing else.

**Golden images.** `Vixen.Ui.Testing` renders without a GPU and `Screenshot` commits pictures;
nothing here does yet, because a picture is worth committing once the layout has stopped moving.

## See also

- [`Vixen.Ui.Markup`](../../../Core/Vixen.Ui.Markup/README.md) — the language, and why the binder has
  no semantic model.
- [`Vixen.Ui.Styling.Utilities`](../../../Core/Vixen.Ui.Styling.Utilities/README.md) — the utility
  system, its families and its deliberate limits.
- [`Vixen.Ui.Testing`](../../../Core/Vixen.Ui.Testing/README.md) — why waiting is counted in frames.
- [`Vixen.Editor.Ui`](../../../Editor/Vixen.Editor.Ui/README.md) — the other VXML consumer, and where
  the signal-backed parameter pattern comes from.
