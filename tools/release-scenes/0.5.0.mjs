// Scene per le note di rilascio della 0.5.0 (Branch/Tag, Shelve, scelta del modello AI dal provider).
// Nessun tag v0.5.0 al momento delle immagini: lo script usa docs/demo/svnassist-demo.html del repository.
// Le scene "ui" sono la UI vera catturata dalla demo.
import { boxOf, collapseConflicts, menuItem } from './_common.mjs';

export const scenes = [
  {
    id: 'branch-tag', file: '01-branch-tag.png', kind: 'ui',
    desc: 'SVN Branch/Tag...: dialogo con Branch o Tag, nome, origine, messaggio di log e Passa subito al nuovo branch',
    async run({ ui, page, shot, T }) {
      await ui.openChanges();
      await menuItem(ui, page, T, /Branch\/Tag/);
      return shot(await boxOf(ui.frame().locator('[role=dialog]').first(), 4));
    },
  },
  {
    id: 'shelve', file: '02-shelve.png', kind: 'ui',
    desc: 'Shelve...: nome e opzione Tieni le modifiche nella working copy',
    async run({ ui, page, shot, T }) {
      await ui.openChanges();
      await collapseConflicts(ui, page);
      const f = ui.frame();
      await f.locator('[role=treeitem]', { hasText: 'OrderService.cs' }).first().click({ button: 'right' });
      await page.waitForTimeout(600);
      await f.locator('[role=menuitem], button', { hasText: /Shelve/ }).first().click();
      await page.waitForTimeout(1000);
      return shot(await boxOf(ui.frame().locator('[role=dialog]').first(), 4));
    },
  },
  {
    id: 'modelli', file: '03-modelli-ai.png', kind: 'ui',
    desc: 'Procedura AI, passo Model con Claude Code: elenco dal provider, Altro..., Aggiorna elenco',
    async run({ ui, shot, T }) {
      await ui.openChanges();
      const f = ui.frame();
      await f.locator(`[title^="${T('Altre azioni', 'More actions')}"]`).first().click();
      await f.locator(`text=${T('Cambia provider AI', 'Change AI Provider')}`).click();
      await ui.settle();
      await f.locator('[data-cmd=wizardPick]', { hasText: 'Claude Code' }).click(); await ui.settle();
      for (let i = 0; i < 3; i++) { await f.locator('[data-cmd=wizardNext]:not([disabled])').click(); await ui.settle(); }
      return shot(await boxOf(f.locator('[role=dialog]').first(), 4));
    },
  },
];

export const set = ['branch-tag', 'shelve', 'modelli'];
