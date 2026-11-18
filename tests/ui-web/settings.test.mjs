// Pagina Impostazioni (Tools > Options): ogni chiave della vista e' accettata dall'host (TrySet), le API key non passano mai nello stato.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { read } from './helpers.mjs';

const js = read('src/SVNAssist.UI.Web/js/settings.js');
const opts = read('src/SVNAssist.Host/OptionsPage.cs');
const host = read('src/SVNAssist.Host/HostService.cs');
const viewKeys = [...js.matchAll(/(?:sel|txt|num|chk|key)\('(\w+)'/g)].map(m => m[1]);
const secrets = ['anthropicApiKey', 'aiApiKey'];

test('ogni chiave della vista esiste in TrySet', () => {
  assert.ok(viewKeys.length >= 15, 'estrazione fallita: ' + viewKeys.length);
  const trySet = opts.slice(opts.indexOf('internal bool TrySet'));
  const missing = viewKeys.filter(k => !trySet.includes(`case "${k}"`));
  assert.deepEqual(missing, [], 'chiavi senza case in TrySet');
});

test('le API key sono solo "impostata o no": mai lette dai valori, mai inviate alla pagina', () => {
  for (const k of secrets) assert.ok(!js.includes(`values.${k}`) && !js.includes(`values['${k}']`), k + ' letta dai valori');
  assert.match(host, /SecretKeys\.Contains\(kv\.Key, StringComparer\.OrdinalIgnoreCase\)/); // tolte dallo stato
  assert.match(host, /name == "settingsSecret" && secret/); // le chiavi solo col comando dedicato
  assert.match(host, /name == "settingsSet" && !secret/);
});

test('settings.js e\' JavaScript valido', async () => {
  const { Script } = await import('node:vm');
  assert.doesNotThrow(() => new Script(js, { filename: 'settings.js' }));
});
