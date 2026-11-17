// CHANGELOG e note .user sono due copie: le voci "- **Titolo.**" di ogni versione devono coincidere (docs/release-notes/REGOLE-CHANGELOG.md).
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';

const items = (text) => text.split(/\r?\n/).filter((l) => l.startsWith('- **')).map((l) => l.trim());

for (const [changelog, suffix] of [['CHANGELOG.md', '.user.md'], ['CHANGELOG.en.md', '.user.en.md']]) {
  const sections = readFileSync(changelog, 'utf8').split(/^## /m).slice(1);
  for (const s of sections) {
    const v = /^(\d+\.\d+\.\d+)/.exec(s)?.[1];
    const file = `docs/release-notes/v${v}${suffix}`;
    if (!v || !existsSync(file)) continue;
    test(`${changelog} ${v} = ${file}`, () => assert.deepEqual(items(s), items(readFileSync(file, 'utf8'))));
  }
}
