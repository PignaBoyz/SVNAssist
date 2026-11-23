// Scene per le note di rilascio della 0.7.0.
// Le scene sono tutte UI reale (UI.Web / demo), catturate in italiano e in inglese.
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { collapseConflicts, swSearch } from './_common.mjs';

const REPO_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const SETTINGS_URL = pathToFileURL(path.join(REPO_ROOT, 'src/SVNAssist.UI.Web/index.html')).href + '?view=settings';

export const scenes = [
  {
    id: 'changes-070', file: '01-changes-070.png', kind: 'ui',
    desc: 'Changes: Merge nell\'intestazione, percorso WC cliccabile, chip provider AI e Conflitti con Risolvi come',
    async run({ ui, page, shot }) {
      await ui.openChanges();
      const b = await page.locator('#dock').boundingBox();
      const cfl = await ui.frame().locator('.cfl').first().boundingBox();
      const h = cfl ? Math.min(b.height, (cfl.y + cfl.height - b.y) + 8) : Math.min(b.height, 715);
      return shot({ x: b.x, y: b.y, width: b.width, height: h });
    },
  },
  {
    id: 'conflitti-menu', file: '02-conflitti-menu.png', kind: 'ui',
    desc: 'Riquadro Conflitti con menu Risolvi come aperto sulle strategie per file',
    async run({ ui, page, shot }) {
      await ui.openChanges();
      const f = ui.frame();
      await f.locator('[data-cf-resolve]').first().click();
      await page.waitForTimeout(400);
      const dock = await page.locator('#dock').boundingBox();
      const cfl = await f.locator('.cfl').first().boundingBox();
      const menu = await f.locator('.menu[role=menu]').first().boundingBox();
      const top = Math.max(dock.y, cfl.y - 8);
      const bottom = Math.min(dock.y + dock.height, Math.max(cfl.y + cfl.height, menu ? menu.y + menu.height : 0) + 12);
      return shot({ x: dock.x, y: top, width: dock.width, height: bottom - top });
    },
  },
  {
    id: 'shelved-inline', file: '03-shelved-inline.png', kind: 'ui',
    desc: 'Sezione Shelved (1) espansa con pulsanti diretti Unshelve ed Elimina',
    async run({ ui, page, shot }) {
      await ui.openChanges();
      await collapseConflicts(ui, page);
      const f = ui.frame();
      await f.locator('[role=treeitem]', { hasText: 'OrderService.cs' }).first().click({ button: 'right' });
      await page.waitForTimeout(500);
      await f.locator('.menu button', { hasText: /Shelve/ }).first().click();
      await page.waitForTimeout(500);
      await f.locator('[data-sh-ok]').first().click();
      await f.locator('[data-shl-toggle]').waitFor({ state: 'visible', timeout: 6000 });
      await f.locator('[data-shl-toggle]').click();
      await page.waitForTimeout(400);
      const dock = await page.locator('#dock').boundingBox();
      const flt = await f.locator('.flt').first().boundingBox();
      const top = Math.max(dock.y, flt.y - 8);
      return shot({ x: dock.x, y: top, width: dock.width, height: (dock.y + dock.height) - top });
    },
  },
  {
    id: 'merge-compatto', file: '04-merge-compatto.png', kind: 'ui',
    desc: 'Log in modalità Merge con testata allineata a titolo e pulsanti sulla stessa riga',
    async run({ ui, page, shot }) {
      await ui.openChanges();
      await page.setViewportSize({ width: 1500, height: 700 });
      const f = ui.frame();
      await f.locator('[data-cmd=mergeOpen]').click();
      await page.waitForTimeout(1500);
      await swSearch(ui, page, 'feature-coupon');
      await f.locator('.sw-row', { hasText: 'feature-coupon' }).first().click();
      await page.waitForTimeout(300);
      await f.locator('[data-sw-ok]').click();
      await page.waitForTimeout(2500);
      await ui.logFrame().locator('[data-cmd=mergeRevertAll]').click();
      await page.waitForTimeout(600);
      await page.getByRole('button', { name: 'OK', exact: true }).click();
      await page.waitForTimeout(2500);
      await ui.logFrame().locator('.lr[data-rev="128"]').click();
      await page.waitForTimeout(400);
      const b = await page.locator('#docBody').boundingBox();
      return shot({ x: b.x, y: b.y, width: b.width, height: Math.min(b.height, 440) });
    },
  },
  {
    id: 'options-070', file: '05-options-070.png', kind: 'ui',
    desc: 'Options: Testa chiave con provider Gemini e opzione Modalità leggera (PC poco potenti)',
    async run({ page, lang }) {
      const p = await page.context().newPage();
      try {
        await p.setViewportSize({ width: 900, height: 860 });
        const st = {
          values: {
            aiProvider: 'gemini',
            defaultLanguage: 'auto',
            aiShowUsage: 'false',
            aiExplainIncoming: 'false',
            claudeModel: 'claude-sonnet-5-5',
            copilotModel: 'default',
            aiEndpoint: 'https://models.github.ai/inference/chat/completions',
            aiModel: 'openai/gpt-4o-mini',
            geminiModel: 'gemini-2.5-flash',
            workingCopyPath: '',
            repoLayout: 'trunk/branches/tags',
            headCheckMinutes: '5',
            lightweightMode: 'true',
            windowTheme: 'auto',
            uiLanguage: 'auto',
            showSccGlyphs: 'true',
            nextChangeShortcut: 'Alt+F8',
            previousChangeShortcut: 'Shift+Alt+F8',
          },
          hasAnthropicKey: false,
          hasAiKey: false,
          hasGeminiKey: true,
          testResult: {
            provider: 'gemini',
            busy: false,
            ok: true,
            msg: lang === 'en' ? 'Key valid (38 models available).' : 'Chiave valida (38 modelli disponibili).',
          },
        };
        await p.addInitScript(({ state, l }) => {
          let fn = null;
          window.__mockBridge = {
            attach(_w, f) { fn = f; },
            postMessage(m) {
              if (!m || m.type !== 'ready') return;
              queueMicrotask(() => {
                if (!fn) return;
                fn({ v: 1, type: 'theme', kind: 'dark', vars: {} });
                fn({ v: 1, type: 'state', view: 'settings', lang: l, rev: 1, state });
              });
            },
          };
        }, { state: st, l: lang });
        await p.goto(SETTINGS_URL);
        await p.locator('.set-test.ok').waitFor({ state: 'visible', timeout: 5000 });
        await p.evaluate(() => {
          const el = document.querySelector('.settings');
          const cards = el.querySelectorAll('.set-card');
          if (cards[1]) el.scrollTop = Math.max(0, cards[1].offsetTop - 10);
        });
        await p.waitForTimeout(300);
        const card3 = await p.locator('.set-card').nth(2).boundingBox();
        const card2 = await p.locator('.set-card').nth(1).boundingBox();
        const top = Math.max(0, card2.y - 12);
        const bottom = Math.min(860, card3.y + card3.height + 14);
        return await p.screenshot({ clip: { x: 0, y: top, width: 900, height: bottom - top } });
      } finally {
        await p.close();
      }
    },
  },
];

export const set = ['changes-070', 'conflitti-menu', 'shelved-inline', 'merge-compatto', 'options-070'];
