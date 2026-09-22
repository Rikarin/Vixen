# Fonts

`TestMono.ttf` **is Vixen's own**, unlike the twenty-two in `Core/Vixen.Ui.Text.Tests/Fonts/`. It is
a synthetic fixed-pitch TrueType face — ninety-five printable ASCII glyphs, every one a hollow box
1229 units wide at 2048 per em, no `GSUB`, no `GPOS`, no `kern` — built by `TestMono.py` beside it
with [fontTools](https://github.com/fonttools/fonttools) and committed rather than generated on the
fly, because a conformance fixture that is missing when its generator is not installed is not a
fixture. Six kilobytes. Apache-2.0 with the rest of the tree; there is no third-party holder.

It exists for one reason. `AdvancedTheme.vcss` says `font-family: monospace` on `code-editor`, and
`CodeEditor` turns a column into an x by multiplying a one-digit probe. Until #1259 this suite
registered one family, the proportional `TestShapeLana`, so the declaration resolved to `Default`
and every geometry test ran in the state the theme's own comment forbids — and could not notice,
because every expected x was `column × CharacterWidth`, which is how the control computes it. An
`i` measured 3.625 against a 10.9375 cell. `AdvancedFixture` now registers this face under the
family name the sheet uses, and `MonospaceFixtureTests` reads a shaped run's width back from the
text pipeline rather than multiplying.

⚠ **The glyphs are boxes, not letters.** A screenshot of a code editor in this suite reads as rows of
cells. That is enough to tell a wide row from a narrow one and is otherwise decoration; anything that
wants to see letters wants a real face, and a real fixed-pitch face is the editor's to ship (#1315).

⚠ **It is linked, not copied, by a second project.** `Vixen.Editor.Testing` embeds this same file
as `Vixen.Editor.Testing.Fonts.TestMono.ttf` and registers it under `monospace` on every editor a
test starts, for the same reason and against three more stylesheets — see `HarnessFonts`. Two files
that have to measure the same are one file, so a regeneration here moves both.

Regenerate with `py -3 TestMono.py` (any Python 3 with `fonttools`). The script pins the `head`
timestamps, so a regeneration that changed nothing produces the same bytes.
