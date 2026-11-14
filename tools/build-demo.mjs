#!/usr/bin/env node
// Costruisce docs/demo/svnassist-demo.html (file unico, apribile da file://) inlineando:
//   src/SVNAssist.UI.Web (index.html + css + js)  ->  incorporata come srcdoc negli iframe della shell finta
//   docs/demo/src/{shell.html, shell.css, mock-bridge.js, demo.js}
// Uso: node tools/build-demo.mjs
import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('..', import.meta.url));
const ui = root + 'src/SVNAssist.UI.Web/', dm = root + 'docs/demo/';
const read = p => readFileSync(p, 'utf8').replace(/\r\n/g, '\n');
const safe = (name, txt) => { if (/<\/script/i.test(txt)) throw new Error(name + ': contiene </script'); return txt; };

// 1) pagina UI.Web in un solo documento
let page = read(ui + 'index.html');
page = page.replace(/<meta http-equiv="Content-Security-Policy"[^>]*>\n?/, '');   // nella demo non c'e' l'host che serve i file
let n = 0;
page = page.replace(/<link rel="stylesheet" href="([^"]+)">/g, (_, h) => { n++; return '<style>\n' + read(ui + h) + '</style>'; });
page = page.replace(/<script src="([^"]+)"><\/script>/g, (_, h) => { n++; return '<script>\n' + safe(h, read(ui + h)) + '</script>'; });
if (n !== 2 + 11 || /(src|href)="(js|css)\//.test(page)) throw new Error('index.html: risorse non inlineate (' + n + ')');
// literal JS valido dentro <script>: niente "</script" e niente "<!--"
const uidoc = 'window.__UI_DOC = ' + JSON.stringify(page).replace(/<\/script/gi, '<\\/script').replace(/<!--/g, '<\\!--') + ';';

// 2) shell
const put = (tpl, tag, txt) => { if (!tpl.includes(tag)) throw new Error('segnaposto mancante ' + tag); return tpl.replace(tag, () => txt); };
let out = read(dm + 'src/shell.html');
out = put(out, '/*TOKENS*/', read(ui + 'css/tokens.css'));
out = put(out, '/*SHELLCSS*/', read(dm + 'src/shell.css'));
out = put(out, '/*UIDOC*/', uidoc);
out = put(out, '/*MOCKBRIDGE*/', safe('mock-bridge.js', read(dm + 'src/mock-bridge.js')));
out = put(out, '/*DEMOJS*/', safe('demo.js', read(dm + 'src/demo.js')));
writeFileSync(dm + 'svnassist-demo.html', out);
console.log('svnassist-demo.html: ' + Math.round(out.length / 1024) + ' KB');
