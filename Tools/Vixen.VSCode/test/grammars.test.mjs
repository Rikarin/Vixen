// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

// What the three grammars actually produce, asserted rather than eyeballed.
//
// ⚠ **Every case here is one a screenshot would not have caught.** A grammar that is subtly wrong
// still colours the file — it colours it *plausibly*, which is worse than not colouring it — and the
// two bugs these tests were written after were exactly that shape: `<Menu>` reading as an element
// rather than a component, and an attribute rule that stopped at the name so the stray quote of
// `change:Value="@(v => …)"` opened a string and ate the next two attributes.
//
// The scopes asserted are prefixes, because a token carries its whole stack and only the last part
// is this grammar's claim.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { tokenise } from './tokenise.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const repository = path.resolve(here, '../../..');

/**
 * Asserts that every token covering `fragment` carries a scope starting with `scope`.
 *
 * ⚠ For an *embedded* region rather than a token: the C# and CSS grammars are stubbed here and the
 * stub matches one character at a time, so `clicks.Value` is twelve tokens and an exact-token
 * assertion finds none of them. What matters about an embedded region is that the whole of it was
 * handed over, which is what this checks.
 */
async function covers(grammar, text, fragment, scope) {
    const tokens = await tokenise(grammar, text);
    const at = text.indexOf(fragment);

    assert.ok(at >= 0, `${JSON.stringify(fragment)} is not in the test input`);

    const before = text.slice(0, at);
    const line = before.split('\n').length - 1;
    const column = at - (before.lastIndexOf('\n') + 1);

    const covering = tokens.filter(
        (token) => token.line === line && token.end > column && token.start < column + fragment.length
    );

    assert.ok(covering.length > 0, `nothing covers ${JSON.stringify(fragment)}`);
    assert.ok(
        covering.every((token) => token.scopes.some((each) => each.startsWith(scope))),
        `${JSON.stringify(fragment)} is not entirely ${scope}: ${JSON.stringify(covering.map((t) => t.scopes))}`
    );
}

/** Asserts that `text` produces a token whose scope stack contains one starting with `scope`. */
async function scoped(grammar, text, fragment, scope) {
    const tokens = await tokenise(grammar, text);
    const found = tokens.filter((token) => token.text === fragment);

    assert.ok(found.length > 0, `no token was exactly ${JSON.stringify(fragment)} in ${JSON.stringify(text)}`);
    assert.ok(
        found.some((token) => token.scopes.some((each) => each.startsWith(scope))),
        `${JSON.stringify(fragment)} has ${JSON.stringify(found[0].scopes)}, wanted one starting ${scope}`
    );
}

test('vxml: the file headers', async () => {
    await scoped('text.vxml', '@component Shell', '@component', 'keyword.control.directive');
    await scoped('text.vxml', '@component Shell', 'Shell', 'entity.name.type');
    await scoped('text.vxml', '@namespace Vixen.Samples', 'Vixen.Samples', 'entity.name.namespace');
    // ⚠ @tag's argument is CSS's vocabulary rather than C#'s: it can and usually does have a hyphen.
    await scoped('text.vxml', '@tag app-shell', 'app-shell', 'entity.name.tag');
});

test('vxml: an uppercase tag is a component and a lowercase one is an element', async () => {
    // The React and Blazor rule, chosen because it is decidable from the characters — a parser
    // cannot consult a registry of the types being compiled beside it.
    await scoped('text.vxml', '<Menu Label="File">', 'Menu', 'entity.name.type.component');
    await scoped('text.vxml', '<hud-top class="flex">', 'hud-top', 'entity.name.tag');
});

test('vxml: an attribute rule runs through its value', async () => {
    // ⚠ The regression this file exists for. Stopping at the name leaves `="…"` unscoped, and the
    // stray quote opens a string that swallows every attribute after it.
    const line = '<Slider bind:Value="@Model.Detail.Value" change:Value="@(v => Write(v))" ref="@slider" />';

    await scoped('text.vxml', line, 'bind', 'keyword.control.binding');
    await scoped('text.vxml', line, 'change', 'keyword.control.binding');
    await scoped('text.vxml', line, 'ref', 'keyword.other.attribute');
    await scoped('text.vxml', line, '/>', 'punctuation.definition.tag.end');

    // ⚠ **The assertions that actually catch it**, and the four above do not: with the name-only
    // rule the value is simply unscoped text, the tag still ends where it ends, and every scope
    // named above is still correct. What is missing is that the value was handed to C# at all —
    // so that is what to assert. Sabotage-checked by putting the old `begin` back.
    await covers('text.vxml', line, 'Model.Detail.Value', 'meta.embedded.block.csharp');
    await covers('text.vxml', line, 'v => Write(v)', 'meta.embedded.block.csharp');
    await covers('text.vxml', line, 'slider', 'meta.embedded.block.csharp');
});

test('vxml: event modifiers are part of the attribute name', async () => {
    const line = '<Button on:click.stop="@Count" />';

    await scoped('text.vxml', line, 'click', 'entity.other.attribute-name.event');
    await scoped('text.vxml', line, '.stop', 'support.function.modifier');
});

test('vxml: both interpolation forms reach C#', async () => {
    // ⚠ `@a + b` interpolates only `a` — Razor's implicit-expression rule stops at the first
    // character that cannot continue a member access — so the explicit form is the one with an
    // operator in it, and it has to be tried first or the `(` is content.
    await covers('text.vxml', 'Clicked @clicks.Value times', 'clicks.Value', 'meta.embedded.block.csharp');
    await scoped('text.vxml', 'total @(a + b) items', '(', 'punctuation.section.parens.begin');
    await scoped('text.vxml', 'an @@literal at-sign', '@@', 'constant.character.escape');
});

test('vxml: a @code block is C#', async () => {
    const text = '@code {\n    int x = 1;\n}';

    await scoped('text.vxml', text, '@code', 'keyword.control.directive');
    const tokens = await tokenise('text.vxml', text);
    assert.ok(
        tokens.some((token) => token.scopes.some((each) => each.startsWith('meta.embedded.block.csharp'))),
        'the @code body was not handed to the C# grammar'
    );
});

test('vcss: the Vixen at-rules', async () => {
    await scoped('source.vcss', '@theme {\n--color-brand: red;\n}', '@theme', 'keyword.control.at-rule.theme');
    await scoped('source.vcss', '@theme {\n--color-brand: red;\n}', '--color-brand', 'variable.other.definition.token');
    // `--color-*: initial` is how a project clears a whole namespace the engine ships. The star is
    // part of the name.
    await scoped('source.vcss', '@theme {\n--color-*: initial;\n}', '--color-*', 'variable.other.definition.token');
    await scoped('source.vcss', '@apply flex flex-col;', '@apply', 'keyword.control.at-rule.apply');
    await scoped('source.vcss', '@layer base, components, utilities;', '@layer', 'keyword.control.at-rule.layer');
});

test('vcss: a hyphenated type selector is an element name', async () => {
    // ⚠ The one place VCSS looks wrong in a plain CSS editor: Vixen's element names are the control
    // library's, so almost every type selector is a name CSS's own grammar does not know.
    await scoped('source.vcss', 'menu-bar {\n}', 'menu-bar', 'entity.name.tag');
    await scoped('source.vcss', 'progress-bar {\n}', 'progress-bar', 'entity.name.tag');
});

test('raven: declarations and the two attribute families', async () => {
    await scoped('source.raven', 'shader UiBlur {', 'shader', 'keyword.declaration');
    await scoped('source.raven', 'shader UiBlur {', 'UiBlur', 'entity.name.type');
    await scoped('source.raven', '[VertexShader]', 'VertexShader', 'storage.type.annotation.stage');
    await scoped('source.raven', '[PushConstant] var scale: float2', 'PushConstant', 'storage.modifier.annotation.binding');
    // ⚠ A project's own attributes are matched by shape, not by a list — a closed list would grey
    // out the permutation keys a game declares.
    await scoped('source.raven', '[MaxLights] var n: int', 'MaxLights', 'entity.name.function.annotation');
});

test('raven: a stream is not an ordinary variable', async () => {
    // Its location is its index in the declaration list, so adding one to a shader and not to its
    // partners moves every location after it — and what that draws is the next value along.
    await scoped('source.raven', 'stream var uv: float2', 'stream', 'storage.modifier.stream');
    await scoped('source.raven', 'stream var uv: float2', 'uv', 'variable.other.stream');
    await scoped('source.raven', 'var uv: float2', 'uv', 'variable.other.declaration');
});

test('raven: types, ranges and swizzles', async () => {
    await scoped('source.raven', 'var x: float4', 'float4', 'storage.type.primitive');
    await scoped('source.raven', 'var m: Buffer<MaskEntry>', 'Buffer', 'support.type.resource');
    // ⚠ Ascending only, which is why a bottom-up walk is a descending subscript over an ascending
    // index.
    await scoped('source.raven', 'for (i in 1 .. reach) {', '..', 'keyword.operator.range');
    await scoped('source.raven', 'val c = colour.rgb', 'rgb', 'variable.other.member.swizzle');
    await scoped('source.raven', 'val d = saturate(x)', 'saturate', 'support.function.builtin');
    await scoped('source.raven', '/// Prose.', '///', 'comment.line.documentation');
});

test('every file the repository ships tokenises with no unscoped runs of code', async () => {
    // ⚠ **A weak assertion on purpose, and it is the one that catches a grammar falling over.** It
    // does not say the colours are right — the theories above do that — it says no file drops into
    // one giant token, which is what a runaway begin/end looks like and what a screenshot of a
    // half-coloured file is.
    const cases = [
        ['text.vxml', 'Samples/02-HelloUi/Shell.vxml'],
        ['text.vxml', 'Samples/02-HelloUi/Panels/Gallery.vxml'],
        ['text.vxml', 'Samples/14-Mmo/Mmo.Ui/Hud.vxml'],
        ['source.vcss', 'Samples/02-HelloUi/Theme/shell.vcss'],
        ['source.vcss', 'Samples/02-HelloUi/Theme/vixen.ui.vcss'],
        ['source.vcss', 'Core/Vixen.Ui.Controls/ControlTheme.vcss'],
        ['source.raven', 'Platform/Vixen.Ui.Desktop/Shaders/Ui.rvn'],
        ['source.raven', 'Raven/Library/Ui/Gradient.rvn'],
    ];

    for (const [grammar, relative] of cases) {
        const file = path.join(repository, relative);

        if (!fs.existsSync(file)) {
            assert.fail(`${relative} is missing — the paths in this test have gone stale.`);
        }

        const text = fs.readFileSync(file, 'utf8');
        const tokens = await tokenise(grammar, text);

        assert.ok(tokens.length > text.split('\n').length, `${relative} produced ${tokens.length} tokens, which is barely more than its line count — a rule is running away.`);
    }
});
