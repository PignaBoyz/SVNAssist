// Scene per le note di rilascio della 0.3.0 (Switch, Merge, Conflitti, Recenti, indicatore della revisione).
// La demo e' quella del tag v0.3.0; nel menu "..." di SVN Changes compaiono SVN Switch... e SVN Merge...
// Le scene "ui" sono la UI vera catturata dalla demo; "illus" sono ricostruzioni (vedi docs/release-notes/GUIDA.md).
import { boxOf, collapseConflicts, menuItem, swSearch, widenDock } from './_common.mjs';

const BRANCH = 'feature-coupon';

// apre SVN Merge..., sceglie il branch e conferma: il Log si apre come documento in modalita' merge
async function openMerge({ ui, page, T }, branch = BRANCH) {
  await menuItem(ui, page, T, /SVN Merge/);
  await swSearch(ui, page, branch);
  await ui.frame().locator('.sw-row', { hasText: branch }).first().click();
  await page.waitForTimeout(300);
  await ui.frame().locator('[data-sw-ok]').click();
  await page.waitForTimeout(2500);
}

export const scenes = [
  {
    id: 'switch', file: '01-switch.png', kind: 'ui',
    desc: 'SVN Switch: selettore rapido con gli ultimi usati e la ricerca',
    async run(ctx) {
      const { ui, page, shot } = ctx;
      await ui.openChanges();
      await openMerge(ctx);                       // il branch scelto entra tra i recenti
      await ui.frame().locator('.branch').click();
      await page.waitForTimeout(1500);
      return shot(await boxOf(ui.frame().locator('[role=dialog]').first()));
    },
  },
  {
    id: 'switch-esteso', file: '02-switch-esteso.png', kind: 'ui',
    desc: 'SVN Switch: vista estesa con filtri per tipo e autore e ordinamento per data',
    async run({ ui, page, shot }) {
      await ui.openChanges();
      await widenDock(page, 800);
      await page.setViewportSize({ width: 1500, height: 820 });
      await page.waitForTimeout(500);
      await ui.frame().locator('.branch').click();
      await page.waitForTimeout(1500);
      await ui.frame().locator('[data-sw-ext]').click();
      await page.waitForTimeout(800);
      await ui.frame().locator('#sw-a').selectOption({ label: 'mrossi' });
      await page.waitForTimeout(500);
      await ui.frame().locator('[data-sw-sort=date]').click();
      await page.waitForTimeout(600);
      return shot(await boxOf(ui.frame().locator('[role=dialog]').first()));
    },
  },
  {
    id: 'merge-bloccato', file: '03-merge-bloccato.png', kind: 'ui',
    desc: 'SVN Merge con modifiche locali: il messaggio spiega il blocco e offre SVN Revert di tutto e Vai a Changes',
    async run(ctx) {
      const { ui, page, shot } = ctx;
      await ui.openChanges();
      await page.setViewportSize({ width: 1500, height: 640 });   // finestra bassa: il pannello del merge sta sotto la tabella senza vuoto in mezzo
      await openMerge(ctx);
      return shot(await ui.box('#docBody'));
    },
  },
  {
    id: 'merge-revisioni', file: '04-merge-revisioni.png', kind: 'ui',
    desc: "SVN Merge: revisioni da unire, due selezionate, esito dell'Anteprima",
    async run(ctx) {
      const { ui, page, shot } = ctx;
      await ui.openChanges();
      await page.setViewportSize({ width: 1500, height: 760 });
      await openMerge(ctx);
      await revertAll(ctx);
      const f = ui.logFrame();
      for (const r of [128, 131]) { await f.locator(`.lr[data-rev="${r}"]`).click(); await page.waitForTimeout(400); }
      await f.locator('[data-cmd=mergePreview]').click();
      await page.waitForTimeout(2500);
      return shot(await ui.box('#docBody'));
    },
  },
  {
    id: 'merge-esito', file: '05-merge-esito.png', kind: 'ui',
    desc: 'Dopo Merge tutto: banner con Annulla merge e riquadro Conflitti con le azioni per file',
    async run(ctx) {
      const { ui, page, shot } = ctx;
      await ui.openChanges();
      await openMerge(ctx);
      await revertAll(ctx);
      await ui.logFrame().locator('[data-cmd=mergeAll]').click();
      await page.waitForTimeout(3000);
      return shot(await ui.box('#dock', 0, 34, 0, 652));
    },
  },
  {
    id: 'recenti', file: '06-recenti.png', kind: 'ui',
    desc: 'Pannello Recent: ultimi messaggi di commit con ricerca',
    async run({ ui, page, shot }) {
      await ui.openChanges();
      await ui.frame().locator('[data-recent]').click();
      await page.waitForTimeout(1000);
      return shot(await ui.box('#dock', 0, 140, 0, 472));
    },
  },
  {
    id: 'indicatore-revisione', file: '07-indicatore-revisione.png', kind: 'ui',
    desc: 'Indicatore della revisione in fondo a SVN Changes: verde (aggiornata) e arancione (disponibile rN)',
    async run({ ui, page, shot, composite }) {
      await ui.openChanges();
      // solo la pastiglia della revisione: il resto della riga di stato della demo non e' tradotto in inglese
      const chip = async () => boxOf(ui.frame().locator('.rev').first(), 3);
      const green = await shot(await chip());
      // "Simula: un collega ha committato" (pannello simulazione della demo): la revisione disponibile e' piu' recente della working copy
      await page.click('#btnPanel');
      await page.locator('input[data-act=env][data-arg=colleague]').check();
      await page.click('#btnPanel');
      await page.waitForTimeout(3500);   // il controllo della demo e' periodico
      const orange = await shot(await chip());
      return composite([green, orange], { columns: 1, gap: 8 });
    },
  },
  {
    id: 'menu-estensioni', file: '08-menu-estensioni.png', kind: 'illus',
    desc: 'Illustrazione: Extensions > SVNAssist con Switch..., Merge..., Next/Previous Change in Diff (SVNAssist.vsct 0.3.0)',
    run: ({ illus }) => illus('ext-menu-actions'),
  },
];

// "SVN Revert di tutto..." dal messaggio di blocco: conferma nel dialogo della demo e attende il ricalcolo
async function revertAll({ ui, page }) {
  await ui.logFrame().locator('[data-cmd=mergeRevertAll]').click();
  await page.waitForTimeout(600);
  await page.getByRole('button', { name: 'OK', exact: true }).click();
  await page.waitForTimeout(2500);
}

export const set = ['switch', 'switch-esteso', 'merge-bloccato', 'merge-revisioni', 'merge-esito', 'recenti', 'indicatore-revisione', 'menu-estensioni'];
