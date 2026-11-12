// WCAG AA (4.5:1) sui token di tokens.css, per ogni tema.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const css = readFileSync(new URL('../../src/SVNAssist.UI.Web/css/tokens.css', import.meta.url), 'utf8');
const themes = {};
for (const m of css.matchAll(/:root(?:, :root)?\[data-theme="(\w+)"\]\s*\{([^}]*)\}/g)) {
  themes[m[1]] = Object.fromEntries([...m[2].matchAll(/--([\w-]+):\s*(#[0-9a-fA-F]{6})/g)].map(x => [x[1], x[2]]));
}
const lum = h => { const c = [1, 3, 5].map(i => parseInt(h.slice(i, i + 2), 16) / 255).map(v => v <= .03928 ? v / 12.92 : ((v + .055) / 1.055) ** 2.4); return .2126 * c[0] + .7152 * c[1] + .0722 * c[2]; };
const ratio = (a, b) => { const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p); return (x + .05) / (y + .05); };

// [testo, sfondi]
const PAIRS = [
  ['fg', ['bg0', 'bg1', 'bg2', 'bg3', 'bg4', 'acc-bg']], ['fg2', ['bg0', 'bg1', 'bg2', 'bg3', 'acc-bg']], ['fg3', ['bg0', 'bg1', 'bg2', 'bg3']],
  ['acc-fg', ['acc', 'acc-h']], ['acc2', ['bg0', 'bg1', 'bg2', 'bg3']], ['ok', ['bg1', 'bg2']], ['warn', ['bg1', 'bg2']], ['err', ['bg1', 'bg2', 'bg3']],
  ['st-M', ['bg1', 'bg2', 'bg4']], ['st-A', ['bg1', 'bg2', 'bg4']], ['st-D', ['bg1', 'bg2', 'bg4']], ['st-C', ['bg1', 'bg2', 'bg4']],
  ['st-U', ['bg1', 'bg2', 'bg4']], ['st-X', ['bg1', 'bg2', 'bg4']], ['st-R', ['bg1', 'bg2', 'bg4']],
  ['add-fg', ['add-bg', 'add-hl']], ['del-fg', ['del-bg', 'del-hl']], ['hunk-fg', ['hunk-bg']],
  ['syn-kw', ['bg1', 'add-bg', 'del-bg']], ['syn-str', ['bg1', 'add-bg', 'del-bg']], ['syn-cm', ['bg1', 'add-bg', 'del-bg']], ['syn-num', ['bg1', 'add-bg', 'del-bg']], ['syn-ty', ['bg1', 'add-bg', 'del-bg']],
  ['chip-ok-fg', ['chip-ok-bg']], ['chip-fail-fg', ['chip-fail-bg']], ['chip-run-fg', ['chip-run-bg']], ['warn-fg', ['warn-bg']],
];

test('tre temi definiti', () => assert.deepEqual(Object.keys(themes).sort(), ['blue', 'dark', 'light']));
for (const [name, t] of Object.entries(themes)) {
  test(`contrasto AA tema ${name}`, () => {
    const bad = [];
    for (const [fg, bgs] of PAIRS) for (const bg of bgs) {
      assert.ok(t[fg] && t[bg], `token mancante ${fg}/${bg}`);
      const r = ratio(t[fg], t[bg]);
      if (r < 4.5) bad.push(`${fg} su ${bg}: ${r.toFixed(2)}`);
    }
    assert.deepEqual(bad, []);
  });
}
