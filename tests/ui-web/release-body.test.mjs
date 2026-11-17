import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const BUILDER = fileURLToPath(new URL('../../tools/build-release-body.mjs', import.meta.url));
const SHA = 'abc123def456';
const IMG = 'https://raw.githubusercontent.com/PignaBoyz/SVNAssist/main/docs/release-notes/img/9.9.9/01-a.png';

const IT = `# SVNAssist 9.9.9

## Novità

- Voce.

  <img src="${IMG}" width="420" alt="x">

  > [IMMAGINE DA FORNIRE: schermata
  > seconda riga] (file: docs/release-notes/img/9.9.9/02-b.png)

> [IMMAGINE DA FORNIRE: una riga] (file: x.png)

<details><summary>Dettagli tecnici</summary>

Interno.

</details>
`;
const EN = IT.replace('Novità', 'New').replace(/IMMAGINE DA FORNIRE/g, 'IMAGE TO BE PROVIDED');

function run(files) {
  const dir = mkdtempSync(join(tmpdir(), 'rb-'));
  for (const [n, c] of Object.entries(files)) writeFileSync(join(dir, n), c);
  const out = join(dir, 'body.md');
  const r = spawnSync(process.execPath, [BUILDER, 'v9.9.9', SHA, '--dir', dir, '--out', out], { encoding: 'utf8' });
  let body = null;
  try { body = readFileSync(out, 'utf8'); } catch { /* assente */ }
  return { r, body };
}

test('URL immagine riscritto sullo SHA', () => {
  const { body } = run({ 'v9.9.9.user.md': IT, 'v9.9.9.user.en.md': EN });
  assert.ok(!body.includes('/SVNAssist/main/'));
  assert.ok(body.includes(`/SVNAssist/${SHA}/docs/release-notes/img/9.9.9/01-a.png`));
});

test('segnaposto nascosti, anche su piu righe', () => {
  const { body } = run({ 'v9.9.9.user.md': IT, 'v9.9.9.user.en.md': EN });
  for (const l of body.split('\n')) {
    assert.ok(!/^>\s*\[(IMMAGINE|IMAGE)/.test(l), `visibile: ${l}`);
  }
  assert.ok(body.includes('<!-- > [IMMAGINE DA FORNIRE: schermata -->'));
  assert.ok(body.includes('<!--   > seconda riga] (file: docs/release-notes/img/9.9.9/02-b.png) -->')
    || body.includes('<!-- > seconda riga] (file: docs/release-notes/img/9.9.9/02-b.png) -->'));
  assert.ok(body.includes('<!-- > [IMAGE TO BE PROVIDED: una riga] (file: x.png) -->'));
});

test('struttura details e riga vuota dopo summary', () => {
  const { body } = run({ 'v9.9.9.user.md': IT, 'v9.9.9.user.en.md': EN });
  assert.ok(body.startsWith('<details open><summary><b>Italiano</b></summary>\n\n'));
  assert.ok(body.includes('\n\n</details>\n\n<details><summary><b>English</b></summary>\n\n'));
  assert.ok(body.trimEnd().endsWith('\n\n</details>'));
  assert.ok(!body.includes('# SVNAssist 9.9.9'));
  assert.ok(body.includes('<details><summary>Dettagli tecnici</summary>'));
});

test('file inglese mancante: solo italiano e avviso', () => {
  const { r, body } = run({ 'v9.9.9.user.md': IT });
  assert.equal(r.status, 0);
  assert.ok(/inglese mancante/i.test(r.stderr));
  assert.ok(!body.includes('English'));
  assert.ok(body.includes('Italiano'));
});

test('file italiano mancante: codice di uscita non zero', () => {
  const { r } = run({ 'v9.9.9.user.en.md': EN });
  assert.notEqual(r.status, 0);
});
