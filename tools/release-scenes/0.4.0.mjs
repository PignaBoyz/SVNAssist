// Scene per le note di rilascio della 0.4.0 (selezione multipla, Commit All con file nuovi, Show Log con opzioni,
// Cleanup con opzioni, avviso prima dello Switch, finestra di avanzamento, Solution Explorer).
// La demo e' quella del tag v0.4.0. Le scene "ui" sono la UI vera catturata dalla demo; "illus" sono ricostruzioni.
// Le scene con `langs: ['it']` esistono solo in italiano: la demo del tag non traduce quei testi.
import { boxOf, collapseConflicts, menuItem, swSearch, resolveConflicts, widenDock } from './_common.mjs';

// avvia lo switch verso release-2.1 fino all'avviso (la working copy ha modifiche locali)
async function openSwitchWarning({ ui, page }) {
  await ui.frame().locator('.branch').click();
  await page.waitForTimeout(1500);
  await swSearch(ui, page, 'release');
  await ui.frame().locator('.sw-row', { hasText: 'release-2.1' }).first().click();
  await page.waitForTimeout(300);
  await ui.frame().locator('[data-sw-ok]').click();
  await page.waitForTimeout(2000);
}

export const scenes = [
  {
    id: 'selezione-multipla', file: '01-selezione-multipla.png', kind: 'ui',
    desc: 'Changes: tre file selezionati con Ctrl+clic e menu del tasto destro con il conteggio',
    async run({ ui, page, shot }) {
      await ui.openChanges();
      await collapseConflicts(ui, page);
      await widenDock(page, 640);               // pannello piu' largo: il menu si apre a destra dei nomi dei file
      const f = ui.frame();
      const row = n => f.locator('[role=treeitem]', { hasText: n }).first();
      await row('Coupon.cs').click();
      await row('OrderService.cs').click({ modifiers: ['Control'] });
      await row('PaymentGateway.cs').click({ modifiers: ['Control'] });
      const rb = await row('OrderService.cs').boundingBox();
      await page.mouse.click(rb.x + 330, rb.y + rb.height / 2, { button: 'right' });   // a destra dei nomi, vicino all'inizio delle azioni
      await page.waitForTimeout(800);
      return shot(await ui.box('#dock', 0, 274, 0, 482));
    },
  },
  {
    id: 'commit-file-nuovi', file: '02-commit-file-nuovi.png', kind: 'ui',
    desc: 'Commit All con file non versionati: domanda "Aggiungerli al commit?" con le tre scelte',
    async run(ctx) {
      const { ui, page, shot, T } = ctx;
      await ui.openChanges();
      await resolveConflicts(ctx);              // la demo parte con tre conflitti, che bloccano il commit
      await ui.frame().locator('#msg').fill(T('Calcolo del totale con coupon', 'Coupon calculation in the order total'));
      await page.waitForTimeout(1000);          // il testo arriva alla demo con un piccolo ritardo
      await ui.frame().locator('[data-cmd=commit]').click();
      await page.waitForTimeout(1500);
      return shot(await boxOf(ui.frame().locator('[role=dialog]').first()));
    },
  },
  {
    id: 'log-opzioni', file: '03-log-opzioni.png', kind: 'ui',
    desc: 'Show Log con Include merged revisions: le revisioni unite sono annidate sotto il merge, il reverse merge e\' segnalato',
    async run({ ui, page, shot }) {
      await ui.openChanges();
      await ui.openLog();
      await ui.logFrame().locator('input[name=merged]').check();
      await page.waitForTimeout(2000);
      return shot(await ui.box('#docBody', 0, 104, 0, 326));
    },
  },
  {
    id: 'cleanup', file: '04-cleanup.png', kind: 'ui',
    desc: 'SVN Cleanup con opzioni: normali, Vacuum pristines, Include externals e azioni distruttive in rosso',
    async run({ ui, page, shot, T }) {
      await ui.openChanges();
      await menuItem(ui, page, T, /SVN Cleanup/);
      return shot(await boxOf(ui.frame().locator('[role=dialog]').first()));
    },
  },
  {
    id: 'switch-avviso', file: '05-switch-avviso.png', kind: 'ui',
    desc: 'Avviso prima dello Switch: conteggi per tipo e scelte Annulla / Apri Cleanup / Continua comunque',
    async run(ctx) {
      const { ui, shot } = ctx;
      await ui.openChanges();
      await openSwitchWarning(ctx);
      return shot(await boxOf(ui.frame().locator('[role=dialog]').first()));
    },
  },
  {
    id: 'avanzamento', file: '06-avanzamento.png', kind: 'ui',
    desc: 'Finestra di avanzamento dello Switch: fase, contatore, righe di svn e pulsante Annulla',
    async run(ctx) {
      const { ui, page, shot } = ctx;
      await ui.openChanges();
      await openSwitchWarning(ctx);
      await ui.frame().locator('[data-cmd=switchWarnContinue]').click();
      await page.waitForTimeout(1300);          // l'avanzamento e' in corso: la demo emette una riga ogni 60 ms
      return shot(await boxOf(ui.frame().locator('[role=dialog]').first()));
    },
  },
  {
    id: 'switch-annullato', file: '07-switch-annullato.png', kind: 'ui', langs: ['it'],
    desc: 'Switch annullato: avviso sulla working copy a meta\' e scelte Cleanup... / Chiudi (solo italiano: la demo non traduce questo riepilogo)',
    async run(ctx) {
      const { ui, page, shot } = ctx;
      await ui.openChanges();
      await openSwitchWarning(ctx);
      await ui.frame().locator('[data-cmd=switchWarnContinue]').click();
      await page.waitForTimeout(700);
      // l'avanzamento ridisegna la finestra ogni 60 ms: un clic di Playwright aspetterebbe la fine, quindi si clicca alle coordinate
      let b = null;                             // la finestra viene ridisegnata di continuo: si riprova finche' il pulsante ha una posizione
      for (let i = 0; i < 20 && !b; i++) { b = await ui.frame().locator('[data-cmd=opCancel]').boundingBox().catch(() => null); if (!b) await page.waitForTimeout(50); }
      await page.mouse.click(b.x + b.width / 2, b.y + b.height / 2);
      await page.waitForTimeout(3000);
      return shot(await boxOf(ui.frame().locator('[role=dialog]').first()));
    },
  },
  {
    id: 'solution-explorer', file: '08-solution-explorer.png', kind: 'illus',
    desc: 'Illustrazione: Solution Explorer con le icone di stato e il menu (Show Log, SVN Add, Check for Modifications)',
    run: ({ illus }) => illus('se-menu-scc'),
  },
];

export const set = ['selezione-multipla', 'commit-file-nuovi', 'log-opzioni', 'cleanup', 'switch-avviso', 'avanzamento', 'switch-annullato', 'solution-explorer'];
