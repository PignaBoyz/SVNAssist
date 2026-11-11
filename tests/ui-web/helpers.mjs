// Helper dei test: mini validatore JSON Schema (solo le parole chiave usate in contract/) e caricamento della demo in un contesto vm.
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const root = new URL('../../', import.meta.url);
export const read = p => readFileSync(new URL(p, root), 'utf8').replace(/\r\n/g, '\n');
export const readJson = p => JSON.parse(read(p));
export const contract = n => readJson('src/SVNAssist.UI.Web/contract/' + n);

const typeOk = (t, v) => ({ string: typeof v === 'string', boolean: typeof v === 'boolean', integer: Number.isInteger(v), number: typeof v === 'number', null: v === null, object: v !== null && typeof v === 'object' && !Array.isArray(v), array: Array.isArray(v) })[t];

// ritorna la lista degli errori (vuota = valido)
export function validate(schema, value, rootSchema = schema, path = '$') {
  const err = [];
  if (schema.$ref) { const t = schema.$ref.replace('#/', '').split('/').reduce((o, k) => o[k], rootSchema); return validate(t, value, rootSchema, path); }
  if (schema.oneOf) {
    const ok = schema.oneOf.filter(s => validate(s, value, rootSchema, path).length === 0).length;
    if (ok !== 1) err.push(`${path}: oneOf soddisfatto da ${ok} schemi`);
  }
  if ('const' in schema && value !== schema.const) err.push(`${path}: atteso ${JSON.stringify(schema.const)}, trovato ${JSON.stringify(value)}`);
  if (schema.enum && !schema.enum.includes(value)) err.push(`${path}: ${JSON.stringify(value)} non in ${JSON.stringify(schema.enum)}`);
  if (schema.type) { const ts = [].concat(schema.type); if (!ts.some(t => typeOk(t, value))) { err.push(`${path}: tipo atteso ${ts}, trovato ${value === null ? 'null' : typeof value}`); return err; } }
  if (typeof value === 'number') { if (schema.minimum != null && value < schema.minimum) err.push(`${path}: < ${schema.minimum}`); if (schema.maximum != null && value > schema.maximum) err.push(`${path}: > ${schema.maximum}`); }
  if (typeof value === 'string' && schema.pattern && !new RegExp(schema.pattern).test(value)) err.push(`${path}: non rispetta ${schema.pattern}`);
  if (Array.isArray(value) && schema.items) value.forEach((v, i) => err.push(...validate(schema.items, v, rootSchema, `${path}[${i}]`)));
  if (typeOk('object', value) && (schema.properties || schema.required || schema.additionalProperties !== undefined)) {
    for (const k of schema.required || []) if (!(k in value)) err.push(`${path}: manca "${k}"`);
    for (const [k, v] of Object.entries(value)) {
      if (schema.properties && k in schema.properties) err.push(...validate(schema.properties[k], v, rootSchema, `${path}.${k}`));
      else if (schema.additionalProperties === false) err.push(`${path}: proprieta' non prevista "${k}"`);
      else if (typeof schema.additionalProperties === 'object') err.push(...validate(schema.additionalProperties, v, rootSchema, `${path}.${k}`));
    }
  }
  return err;
}

// contesto con DOM "nullo": ogni accesso ritorna un oggetto inerte, cosi' demo.js gira senza browser
export async function loadDemo() {
  const nul = new Proxy(function () { }, { get: (t, k) => (k === Symbol.toPrimitive ? () => '' : k === Symbol.iterator ? () => [][Symbol.iterator]() : nul), set: () => true, apply: () => nul, construct: () => nul, has: () => true });
  const ctx = vm.createContext({ document: nul, localStorage: nul, innerWidth: 1500, innerHeight: 900, console, setInterval: () => 0, setTimeout: (f) => { f(); return 0; }, queueMicrotask, clearTimeout() { } });
  ctx.window = ctx;
  const run = (code, name) => vm.runInContext(code, ctx, { filename: name });
  run(read('docs/demo/src/mock-bridge.js'), 'mock-bridge.js');
  try { run(read('docs/demo/src/demo.js'), 'demo.js'); } catch (e) { if (!/modal|overlay|Cannot|not a function/i.test(String(e))) throw e; }
  return { ctx, run, bridge: () => ctx.__mockBridge };
}

// carica nel contesto gli script puri della UI.Web (niente DOM)
export function loadUi(files = ['util', 'diff', 'tree', 'prereq', 'log']) {
  const ctx = vm.createContext({ console });
  ctx.window = ctx;
  for (const f of files) vm.runInContext(read(`src/SVNAssist.UI.Web/js/${f}.js`), ctx, { filename: f + '.js' });
  return ctx.SVNUI;
}
