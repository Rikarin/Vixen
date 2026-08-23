// Loads the three grammars the way VS Code does and prints the scopes for a line.
// Used by `npm test`, and by hand when a rule is not doing what it looks like it does.
import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

// ⚠ `createRequire` rather than `import *`. Both packages are CommonJS, and Node's ESM interop
// exposes their exports inconsistently across versions — `loadWASM` came back undefined from a
// namespace import on Node 26. Requiring them is the shape they were published in.
const require = createRequire(import.meta.url);
const oniguruma = require('vscode-oniguruma');
const textmate = require('vscode-textmate');

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..');

const wasm = fs.readFileSync(path.join(root, 'node_modules/vscode-oniguruma/release/onig.wasm'));
await oniguruma.loadWASM(wasm.buffer);

const grammars = {
    'text.vxml': 'syntaxes/vxml.tmLanguage.json',
    'source.vcss': 'syntaxes/vcss.tmLanguage.json',
    'source.raven': 'syntaxes/raven.tmLanguage.json',
};

// ⚠ **`source.cs` and `source.css` are VS Code's own, and they are stubbed here rather than left
// unresolved — because an unresolved include is not a no-op.** vscode-textmate drops a begin/end
// rule whose `patterns` contain *only* an include it cannot resolve, so `@(…)` came back as one
// unscoped token and looked exactly like a broken regex. It is not: in VS Code the include resolves
// and the rule runs. The stubs scope their whole content so a test can assert the region was
// entered without asserting anything about how C# colours it.
export const registry = new textmate.Registry({
    onigLib: Promise.resolve({
        createOnigScanner: (sources) => new oniguruma.OnigScanner(sources),
        createOnigString: (str) => new oniguruma.OnigString(str),
    }),
    loadGrammar: async (scope) => {
        const file = grammars[scope];
        if (file) {
            return textmate.parseRawGrammar(fs.readFileSync(path.join(root, file), 'utf8'), file);
        }

        if (scope === 'source.cs' || scope === 'source.css') {
            return textmate.parseRawGrammar(
                JSON.stringify({
                    scopeName: scope,
                    // ⚠ One character at a time, not `[\\s\\S]+`. A greedy stub swallows the
                    // enclosing rule's terminator — the `)` of an `@(…)` — so the region never
                    // closes and every test downstream of it reads as broken grammar.
                    patterns: [{ match: '.', name: `stub.${scope}` }],
                }),
                `${scope}.stub.json`
            );
        }

        return null;
    },
});

export async function tokenise(scope, text) {
    const grammar = await registry.loadGrammar(scope);
    if (!grammar) throw new Error(`no grammar for ${scope}`);

    const out = [];
    let state = textmate.INITIAL;
    let number = 0;

    for (const line of text.split('\n')) {
        const result = grammar.tokenizeLine(line, state);
        for (const token of result.tokens) {
            out.push({
                text: line.slice(token.startIndex, token.endIndex),
                scopes: token.scopes,
                line: number,
                start: token.startIndex,
                end: token.endIndex,
            });
        }
        state = result.ruleStack;
        number++;
    }

    return out;
}

if (process.argv[2] && process.argv[3]) {
    for (const token of await tokenise(process.argv[2], process.argv[3])) {
        if (token.text.trim()) console.log(JSON.stringify(token.text).padEnd(28), token.scopes.slice(1).join(' '));
    }
}
