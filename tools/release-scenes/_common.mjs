// Funzioni comuni alle scene delle versioni (tools/release-scenes/<versione>.mjs).
// Ogni scena riceve { page, ui, shot, composite, lang, T, illus } da tools/screenshots-release.mjs.

// riquadro di un elemento con un margine (come nella scena "wizard" della 0.2.0)
export async function boxOf(locator, m = 0) {
  const b = await locator.boundingBox();
  return { x: b.x - m, y: b.y - m, width: b.width + 2 * m, height: b.height + 2 * m };
}

// comprime il riquadro Conflitti del pannello Changes (la demo parte con tre conflitti); lascia l'intestazione
export async function collapseConflicts(ui, page) {
  const t = ui.frame().locator('[data-cfl-toggle]').first();
  if (await t.count() && (await t.getAttribute('aria-expanded')) === 'true') { await t.click(); await page.waitForTimeout(500); }
}

// menu "..." del pannello Changes: titolo "More actions" / "Altre azioni"
export const moreActions = (ui, T) => ui.frame().locator(`[title^="${T('Altre azioni', 'More actions')}"]`).first();

// la voce di menu "SVN Merge…" / "SVN Switch…" ecc.; il testo e' lo stesso nelle due lingue
export async function menuItem(ui, page, T, re) {
  await moreActions(ui, T).click();
  await page.waitForTimeout(400);
  await ui.frame().locator('[role=menuitem], .ctx button, button', { hasText: re }).first().click();
  await page.waitForTimeout(1500);
}

// selettore di Switch/Merge: cerca per nome (campo di ricerca della vista rapida)
export async function swSearch(ui, page, text) {
  const q = ui.frame().locator('#sw-q');
  await q.fill(text);
  await page.waitForTimeout(600);
}

// risolve i conflitti della demo (un conflitto di testo con "Usa la mia" e i conflitti ad albero con "Segna tutti come risolti");
// le conferme sono i dialoghi della cornice della demo, fuori dal pannello
export async function resolveConflicts({ ui, page }) {
  const ok = async () => { await page.waitForTimeout(500); await page.getByRole('button', { name: 'OK', exact: true }).click(); await page.waitForTimeout(1800); };
  await ui.frame().locator('[data-cmd=conflictOp]', { hasText: /Use mine|Usa la mia/ }).first().click();
  await ok();
  await ui.frame().locator('[data-cmd=resolveTreeConflicts]').first().click();
  await ok();
}

// allarga il pannello (come ridimensionare la finestra degli strumenti); serve alle viste larghe
export async function widenDock(page, px) {
  await page.addStyleTag({ content: `.dock{width:${px}px !important}` });
  await page.waitForTimeout(600);
}
