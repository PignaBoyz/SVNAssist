#!/usr/bin/env node
// Costruisce il corpo di una GitHub Release (italiano + inglese, con <details>).
// Uso: node tools/build-release-body.mjs <tag> <sha> [--out file] [--dir cartella-note]
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const RAW_MAIN = 'https://raw.githubusercontent.com/PignaBoyz/SVNAssist/main/';
const PLACEHOLDER = /^\s*>\s*\[(IMMAGINE DA FORNIRE|IMAGE TO BE PROVIDED)/;

function hideComment(line) {
  return `<!-- ${line.trim().replace(/--/g, '- -')} -->`;
}

export function transformNote(text, sha) {
  let t = text.replace(/\r\n/g, '\n');
  // (c) titolo iniziale "# SVNAssist X.Y.Z"
  t = t.replace(/^\s*# SVNAssist[^\n]*\n(\s*\n)?/, '');
  // (a) immagini per commit
  t = t.split(RAW_MAIN).join(`https://raw.githubusercontent.com/PignaBoyz/SVNAssist/${sha}/`);
  // (b) segnaposto -> commenti HTML, riga per riga
  const lines = t.split('\n');
  const out = [];
  for (let i = 0; i < lines.length; i++) {
    if (!PLACEHOLDER.test(lines[i])) { out.push(lines[i]); continue; }
    out.push(hideComment(lines[i]));
    let closed = lines[i].includes(']');
    while (!closed && i + 1 < lines.length && /^\s*>/.test(lines[i + 1])) {
      i++;
      out.push(hideComment(lines[i]));
      closed = lines[i].includes(']');
    }
  }
  return out.join('\n').replace(/^\s*\n/, '').replace(/\s+$/, '');
}

export function buildBody({ it, en, sha }) {
  const parts = [`<details open><summary><b>Italiano</b></summary>\n\n${transformNote(it, sha)}\n\n</details>`];
  if (en != null) {
    parts.push(`<details><summary><b>English</b></summary>\n\n${transformNote(en, sha)}\n\n</details>`);
  }
  return parts.join('\n\n') + '\n';
}

function main(argv) {
  const args = [];
  let outFile = null;
  let dir = join(dirname(fileURLToPath(import.meta.url)), '..', 'docs', 'release-notes');
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--out') outFile = argv[++i];
    else if (argv[i] === '--dir') dir = argv[++i];
    else args.push(argv[i]);
  }
  const [tag, sha] = args;
  if (!tag || !sha) {
    console.error('Uso: build-release-body.mjs <tag> <sha> [--out file] [--dir cartella]');
    return 2;
  }
  const itPath = join(dir, `${tag}.user.md`);
  const enPath = join(dir, `${tag}.user.en.md`);
  if (!existsSync(itPath)) {
    console.error(`File italiano mancante: ${itPath}`);
    return 1;
  }
  const it = readFileSync(itPath, 'utf8');
  let en = null;
  if (existsSync(enPath)) en = readFileSync(enPath, 'utf8');
  else console.error(`Avviso: file inglese mancante (${enPath}); il corpo conterra solo l'italiano.`);
  const body = buildBody({ it, en, sha });
  if (outFile) writeFileSync(outFile, body);
  else process.stdout.write(body);
  return 0;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  process.exit(main(process.argv.slice(2)));
}
