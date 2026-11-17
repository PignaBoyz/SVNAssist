#!/usr/bin/env node
// Genera le immagini per le note di rilascio (Playwright + Chromium), in italiano e in inglese.
//
// Uso:
//   node tools/screenshots-release.mjs --version 0.2.0                  (tutte le scene, it + en)
//   node tools/screenshots-release.mjs --version 0.2.0 --lang en --scenes changes,log
//   node tools/screenshots-release.mjs --list
//
// Opzioni:
//   --version X.Y.Z   versione delle note; le immagini vanno in docs/release-notes/img/X.Y.Z/<lang>/
//   --lang it|en      lingua dell'interfaccia (default: it,en). Si imposta con il selettore di lingua della
//                     demo (mock bridge: stesso campo "lang" dello stato che manda l'host) e lo script verifica
//                     che i testi cambino davvero.
//   --scenes a,b,c    elenco delle scene (default: l'insieme definito per la versione in SETS, altrimenti tutte)
//   --demo <file>     demo HTML da usare. Default: la demo del tag vX.Y.Z (costruita in una cartella
//                     temporanea con `git worktree`), cosi' le immagini mostrano solo cio' che esisteva
//                     in quella versione. Se il tag non esiste si usa docs/demo/svnassist-demo.html.
//   --out <dir>       cartella base (default: docs/release-notes/img/<versione>); dentro, una cartella per lingua
//   --scale N         deviceScaleFactor (default 2)
//   --list            elenca le scene e termina
//
// Due tipi di scene:
//   - UI vera (kind 'ui'): catturate dalla demo (stessa UI web dell'estensione). Le altre parti della cornice
//     finta di Visual Studio non compaiono: si ritaglia solo il pannello/documento.
//   - Illustrazioni (kind 'illus', ricostruzioni di Visual Studio): pagina docs/demo/src/illustrations.html, con
//     tema scuro, cornice sfocata e scurita intorno alla parte rilevante. NON sono screenshot di Visual Studio:
//     nelle note vanno con la didascalia "Illustrazione:" / "Illustration:" (vedi docs/release-notes/GUIDA.md).
//
// Per aggiungere una scena: una voce in SCENES (id, file, kind, funzione) e, se serve, l'id in SETS per la versione.
import { createRequire } from 'node:module';
import { execFileSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, existsSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const require = createRequire(import.meta.url);
const { chromium } = require('playwright');
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const illusPage = pathToFileURL(path.join(root, 'docs/demo/src/illustrations.html')).href;

// ---------- argomenti ----------
const args = {};
for (let i = 2; i < process.argv.length; i++) {
  const a = process.argv[i];
  if (!a.startsWith('--')) continue;
  const k = a.slice(2), nx = process.argv[i + 1];
  if (nx === undefined || nx.startsWith('--')) args[k] = true; else { args[k] = nx; i++; }
}

// ---------- scene ----------
// Ogni scena riceve { page, ui, shot, composite, lang, T, illus }:
//   page      pagina della demo; ui: helper di navigazione; shot(clip) -> Buffer PNG; composite(buffers, opts) -> Buffer PNG
//   lang      'it' | 'en'; T(it, en) sceglie il testo nella lingua corrente (per i selettori)
//   illus(scene, setup?) -> Buffer PNG di una scena di docs/demo/src/illustrations.html; setup(page) prepara la pagina
const SCENES = [
  {
    id: 'changes', file: '01-changes.png', kind: 'ui',
    desc: 'Pannello SVN Changes con Staged, Modifiche, Non versionati (due file in Staged)',
    async run({ ui, shot, T }) {
      await ui.openChanges();
      const f = ui.frame();
      // sposta due file in Staged con il pulsante "+" della riga
      for (const name of ['Coupon.cs', 'OrderService.cs']) {
        const row = f.locator('.row, li, [role=treeitem]', { hasText: name }).first();
        await row.hover();
        await row.locator(`button[title^="${T('Aggiungi ai file', 'Add to the files')}"]`).click();
        await ui.settle();
      }
      return shot(await ui.box('#dock', 0, 0, 0, 830));
    },
  },
  {
    id: 'log', file: '02-log.png', kind: 'ui',
    desc: 'Log come documento: filtro per autore e file della revisione selezionata',
    async run({ ui, shot, T }) {
      await ui.openChanges();
      await ui.openLog();
      const f = ui.logFrame();
      await f.locator('input[placeholder="mrossi"]').fill('mrossi');
      await f.locator('button', { hasText: T('Filtra', 'Filter') }).first().click();
      await ui.settle();
      await f.locator('.lr[data-rev="123"]').click();
      await ui.settle();
      return shot(await ui.box('#docBody'));
    },
  },
  {
    id: 'log-confronto', file: '03-log-confronto.png', kind: 'ui',
    desc: 'Log: due revisioni selezionate con Ctrl+clic e file cambiati',
    async run({ ui, shot }) {
      await ui.openChanges();
      await ui.openLog();
      const f = ui.logFrame();
      await f.locator('.lr[data-rev="122"]').click();
      await ui.settle();
      await f.locator('.lr[data-rev="124"]').click({ modifiers: ['Control'] });
      await ui.settle();
      return shot(await ui.box('#docBody', 0, 0, 0, 620));
    },
  },
  {
    id: 'wizard', file: '04-procedura-ai.png', kind: 'ui',
    desc: 'Procedura guidata AI: passi Provider, Prerequisites, Sign-in, Model (2x2)',
    async run({ ui, shot, composite, T }) {
      await ui.openChanges();
      const f = ui.frame();
      await f.locator(`[title^="${T('Altre azioni', 'More actions')}"]`).first().click();
      await f.locator(`text=${T('Cambia provider AI', 'Change AI Provider')}`).click();
      await ui.settle();
      await f.locator('[data-cmd=wizardPick]', { hasText: 'Claude Code' }).click(); await ui.settle();
      const wiz = async () => { const b = await f.locator('[role=dialog]').first().boundingBox(); const m = 4; return shot({ x: b.x - m, y: b.y - m, width: b.width + 2 * m, height: b.height + 2 * m }); };
      const clips = [];
      clips.push(await wiz());                       // 1 Provider
      await f.locator('[data-cmd=wizardNext]:not([disabled])').click(); await ui.settle();
      clips.push(await wiz());                       // 2 Prerequisites
      await f.locator('[data-cmd=wizardNext]:not([disabled])').click(); await ui.settle();
      clips.push(await wiz());                       // 3 Sign-in
      await f.locator('[data-cmd=wizardNext]:not([disabled])').click(); await ui.settle();
      clips.push(await wiz());                       // 4 Model
      return composite(clips, { columns: 2, gap: 16 });
    },
  },
  {
    id: 'temi', file: '05-temi.png', kind: 'ui',
    desc: 'Pannello SVN Changes con tema Light, Dark, Blue (affiancati, solo il pannello)',
    async run({ ui, shot, composite }) {
      await ui.openChanges();
      const clips = [];
      for (const t of ['light', 'dark', 'blue']) {
        await ui.setVsTheme(t);
        clips.push(await shot(await ui.box('#dock', 0, 0, 0, 600)));
      }
      await ui.setVsTheme('dark');
      return composite(clips, { columns: 3, gap: 12 });
    },
  },
  {
    id: 'menu-changes', file: '06-menu-changes.png', kind: 'ui',
    desc: 'Menu "..." di SVN Changes: Update to revision, Cleanup, vista, tema, Change AI Provider',
    async run({ ui, shot, T }) {
      await ui.openChanges();
      const f = ui.frame();
      await f.locator(`[title^="${T('Altre azioni', 'More actions')}"]`).first().click();
      await ui.settle();
      return shot(await ui.box('#dock', 0, 0, 0, 590));
    },
  },
  // ---- illustrazioni (ricostruzioni di Visual Studio, non screenshot veri) ----
  {
    id: 'log-documento', file: '07-log-documento-vs.png', kind: 'illus',
    desc: 'Illustrazione: il Log come scheda documento "Log: Acme.Shop" nell\'area editor (Log vero dalla demo dentro la cornice)',
    async run({ page, ui, shot, illus }) {
      await ui.openChanges();
      await ui.openLog();
      // si restringe la finestra finche' l'area del documento e' larga quanto quella della cornice (680 px): niente scala, testo leggibile
      const w = (await page.locator('#docBody').boundingBox()).width, vp = page.viewportSize();
      await page.setViewportSize({ width: Math.round(vp.width - (w - 680)), height: vp.height });
      await page.waitForTimeout(800);
      const b = await page.locator('#docBody').boundingBox();
      const png = await shot({ x: b.x, y: b.y, width: Math.min(b.width, 680), height: 330 });
      return illus('log-doc', async p => { await p.evaluate(u => window.setLogImage(u), 'data:image/png;base64,' + png.toString('base64')); });
    },
  },
  {
    id: 'diff-nativo', file: '08-diff-nativo-vs.png', kind: 'illus',
    desc: 'Illustrazione: diff nativo di Visual Studio, BASE contro working copy (titoli come nell\'host)',
    run: ({ illus }) => illus('diff'),
  },
  {
    id: 'menu-esplora-soluzioni', file: '09-menu-esplora-soluzioni.png', kind: 'illus',
    desc: 'Illustrazione: menu del tasto destro in Esplora soluzioni, sottomenu SVN (voci di SVNAssist.vsct)',
    run: ({ illus }) => illus('se-menu'),
  },
  {
    id: 'login-svn', file: '10-login-svn.png', kind: 'illus',
    desc: 'Illustrazione: popup di login SVN (utente, password, Ricorda)',
    run: ({ illus }) => illus('login'),
  },
  {
    id: 'lingua-menu', file: '11-lingua-menu.png', kind: 'illus',
    desc: 'Illustrazione: Extensions > SVNAssist > Language / Lingua',
    run: ({ illus }) => illus('ext-menu'),
  },
];

// scene predefinite per versione (l'ordine e' quello dei file)
const SETS = {
  '0.2.0': ['changes', 'log', 'log-confronto', 'wizard', 'temi', 'menu-changes', 'log-documento', 'diff-nativo', 'menu-esplora-soluzioni', 'login-svn', 'lingua-menu'],
};

if (args.list) {
  for (const s of SCENES) console.log(`${s.id.padEnd(24)} ${s.file.padEnd(30)} ${s.kind.padEnd(6)} ${s.desc}`);
  process.exit(0);
}

// ---------- configurazione ----------
const version = args.version;
if (!version || version === true) { console.error('Manca --version X.Y.Z (vedi --list)'); process.exit(1); }
const scale = Number(args.scale || 2);
const outBase = path.resolve(args.out && args.out !== true ? args.out : path.join(root, 'docs/release-notes/img', version));
const langs = (args.lang && args.lang !== true ? String(args.lang) : 'it,en').split(',').map(s => s.trim());
for (const l of langs) if (l !== 'it' && l !== 'en') { console.error('--lang deve essere it, en o it,en'); process.exit(1); }
const ids = args.scenes && args.scenes !== true ? String(args.scenes).split(',') : (SETS[version] || SCENES.map(s => s.id));
const scenes = ids.map(id => SCENES.find(s => s.id === id) || (() => { throw new Error('Scena sconosciuta: ' + id); })());

// demo: dal tag della versione se esiste, altrimenti quella del repo (serve solo se ci sono scene dell'interfaccia vera o il log-documento)
const needDemo = scenes.some(s => s.kind === 'ui' || s.id === 'log-documento');
let demoPath = args.demo && args.demo !== true ? path.resolve(args.demo) : null;
let tmpWt = null;
if (needDemo && !demoPath) {
  const tag = 'v' + version;
  let hasTag = false;
  try { execFileSync('git', ['rev-parse', '--verify', '--quiet', 'refs/tags/' + tag], { cwd: root, stdio: 'ignore' }); hasTag = true; } catch { }
  if (hasTag) {
    tmpWt = path.join(mkdtempSync(path.join(tmpdir(), 'svnassist-demo-')), 'wt');
    execFileSync('git', ['worktree', 'add', '--detach', tmpWt, tag], { cwd: root, stdio: 'ignore' });
    execFileSync('node', ['tools/build-demo.mjs'], { cwd: tmpWt, stdio: 'ignore' });
    demoPath = path.join(tmpWt, 'docs/demo/svnassist-demo.html');
    console.log(`Demo costruita dal tag ${tag}`);
  } else {
    demoPath = path.join(root, 'docs/demo/svnassist-demo.html');
    console.log(`Tag ${tag} non trovato: uso ${path.relative(root, demoPath)} (potrebbe mostrare funzioni piu' recenti)`);
  }
}
if (needDemo && !existsSync(demoPath)) { console.error('Demo non trovata: ' + demoPath); process.exit(1); }

// ---------- esecuzione ----------
const browser = await chromium.launch();
try {
  for (const lang of langs) {
    const out = path.join(outBase, lang);
    mkdirSync(out, { recursive: true });
    const T = (it, en) => lang === 'en' ? en : it;
    for (const sc of scenes) {
      const ctx = await browser.newContext({ viewport: { width: 1500, height: 1150 }, deviceScaleFactor: scale, locale: lang === 'en' ? 'en-US' : 'it-IT', colorScheme: 'dark' });
      const page = await ctx.newPage();
      const ui = helpers(page, lang);
      if (sc.kind === 'ui' || sc.id === 'log-documento') {
        await page.goto(pathToFileURL(demoPath).href);
        await page.waitForTimeout(500);
        await page.click('text=Esplora liberamente');
        await ui.setLang(lang);
      }
      const shot = async clip => page.screenshot({ clip });
      const composite = (bufs, o) => composeImages(ctx, bufs, o);
      const illus = async (scene, setup) => {
        const p = await ctx.newPage();
        p.on('pageerror', e => { throw e; });
        await p.goto(`${illusPage}?scene=${scene}&lang=${lang}`);
        await p.waitForFunction(() => window.__ready === true);
        if (setup) await setup(p);
        await p.waitForTimeout(300);
        const png = await p.locator('#stage').screenshot();
        await p.close();
        return png;
      };
      const png = await sc.run({ page, ui, shot, composite, lang, T, illus });
      writeFileSync(path.join(out, sc.file), png);
      console.log('OK ' + path.relative(root, path.join(out, sc.file)));
      await ctx.close();
    }
  }
} finally {
  await browser.close();
  if (tmpWt) { try { execFileSync('git', ['worktree', 'remove', '--force', tmpWt], { cwd: root, stdio: 'ignore' }); } catch { } }
}

// ---------- helper ----------
function helpers(page, lang) {
  const ui = {
    frame: () => page.frameLocator('#dock iframe').first(),
    logFrame: () => page.frameLocator('#docFrames iframe').first(),
    settle: () => page.waitForTimeout(700),
    // lingua dell'interfaccia: la demo la tiene nel mock bridge (come "lang" dello stato mandato dall'host)
    async setLang(l) {
      await page.evaluate(x => window.__mockBridge.setLang(x), l);
      await page.waitForTimeout(500);
    },
    async openChanges() {
      await page.click('[data-act=cmd][data-arg=status]');
      await page.waitForTimeout(2500);
      // verifica che la lingua sia davvero quella richiesta (altrimenti le immagini sarebbero nella lingua sbagliata)
      const want = lang === 'en' ? 'Generate with AI' : 'Genera con AI', other = lang === 'en' ? 'Genera con AI' : 'Generate with AI';
      const txt = await ui.frame().locator('body').innerText();
      if (!txt.includes(want) || txt.includes(other)) throw new Error(`La demo non e' in lingua ${lang} (atteso "${want}")`);
    },
    async openLog() {
      await ui.frame().locator('text=Show Log').click();
      await page.waitForTimeout(1500);
    },
    async setVsTheme(t) {
      await page.click('#btnPanel');
      await page.selectOption('select[data-act=vsTheme]', t);
      await page.waitForTimeout(500);
      await page.click('#btnPanel');
      await page.waitForTimeout(600);
    },
    // riquadro di un elemento (opzionalmente ristretto: dx, dy, larghezza, altezza; 0 = invariato)
    async box(sel, dx = 0, dy = 0, w = 0, h = 0) {
      const b = await page.locator(sel).boundingBox();
      return { x: b.x + dx, y: b.y + dy, width: w || b.width - dx, height: h || b.height - dy };
    },
  };
  return ui;
}

// affianca piu' PNG in una griglia con una pagina HTML temporanea e ne cattura lo screenshot
async function composeImages(ctx, bufs, { columns = 2, gap = 16 } = {}) {
  const p = await ctx.newPage();
  const imgs = bufs.map(b => `<img src="data:image/png;base64,${b.toString('base64')}">`).join('');
  await p.setContent(`<html><body style="margin:0;background:#1e1e1e"><div id="g" style="display:inline-grid;grid-template-columns:repeat(${columns},auto);gap:${gap}px;padding:${gap}px;align-items:start">${imgs}</div></body></html>`);
  await p.waitForFunction(() => [...document.images].every(i => i.complete && i.naturalWidth));
  // le immagini hanno gia' dimensioni in pixel del dispositivo: le si riporta a pixel CSS
  await p.evaluate(s => document.querySelectorAll('img').forEach(i => { i.style.width = (i.naturalWidth / s) + 'px'; }), scale);
  const png = await p.locator('#g').screenshot();
  await p.close();
  return png;
}
