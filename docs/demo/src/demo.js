'use strict';
/* =====================================================================
   SVNAssist - demo interattiva. Tutto e' simulato in memoria.
   Organizzazione:  utilita' -> dati di esempio -> diff -> stato (S)
   -> livello "svn" simulato -> AI -> view model -> render -> azioni -> tour
   ===================================================================== */

// ---------- utilita' ----------
const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => Array.from(r.querySelectorAll(s));
const esc = s => String(s == null ? '' : s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const sleep = ms => new Promise(r => setTimeout(r, ms));
const pad = n => String(n).padStart(2, '0');
const fmtDate = d => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
const WC = 'C:\\dev\\Acme.Shop';
const USER = 'andre';
const GH_MODELS = 'https://models.github.ai/inference/chat/completions';
const base = p => p.split('\\').pop();
const parentDir = p => { const i = p.lastIndexOf('\\'); return i < 0 ? '.' : p.slice(0, i); };
const fwd = p => p.replace(/\\/g, '/');

// ---------- contenuti dei file di esempio ----------
const T = {};
T.orderBase = `using System;
using System.Linq;

namespace Acme.Shop.Core.Services
{
    public class OrderService
    {
        private readonly IInventoryService _inventory;

        public OrderService(IInventoryService inventory)
        {
            _inventory = inventory;
        }

        public decimal CalculateTotal(Order order)
        {
            return order.Lines.Sum(l => l.Price * l.Quantity);
        }
    }
}`;
T.orderMod = `using System;
using System.Linq;
using Acme.Shop.Core.Models;

namespace Acme.Shop.Core.Services
{
    public class OrderService
    {
        private readonly IInventoryService _inventory;

        public OrderService(IInventoryService inventory)
        {
            _inventory = inventory;
        }

        public decimal CalculateTotal(Order order)
        {
            var subtotal = order.Lines.Sum(l => l.Price * l.Quantity);
            if (order.Coupon == null)
                return subtotal;

            // il coupon non puo' mai portare il totale sotto zero
            return Math.Max(0, order.Coupon.Apply(subtotal));
        }
    }
}`;
T.cartBase = `using Microsoft.AspNetCore.Mvc;

namespace Acme.Shop.Web.Controllers
{
    public class CartController : Controller
    {
        private readonly OrderService _orders;

        public CartController(OrderService orders) => _orders = orders;

        public IActionResult Index()
        {
            var order = _orders.GetCurrent(User);
            return View(order);
        }
    }
}`;
T.cartMod = `using Microsoft.AspNetCore.Mvc;

namespace Acme.Shop.Web.Controllers
{
    public class CartController : Controller
    {
        private readonly OrderService _orders;

        public CartController(OrderService orders) => _orders = orders;

        public IActionResult Index()
        {
            var order = _orders.GetCurrent(User);
            return View(order);
        }

        [HttpPost]
        public IActionResult ApplyCoupon(string code)
        {
            var ok = _orders.TryApplyCoupon(User, code);
            TempData["Coupon"] = ok ? "Coupon applicato" : "Coupon non valido";
            return RedirectToAction(nameof(Index));
        }
    }
}`;
T.checkoutBase = `using Microsoft.AspNetCore.Mvc;

namespace Acme.Shop.Web.Controllers
{
    public class CheckoutController : Controller
    {
        private readonly PaymentGateway _gateway;

        public CheckoutController(PaymentGateway gateway) => _gateway = gateway;

        [HttpPost]
        public IActionResult Pay(PaymentRequest request)
        {
            var result = _gateway.Charge(request);
            return result.Success ? RedirectToAction("Done") : View("Error");
        }
    }
}`;
T.checkoutConf = `using Microsoft.AspNetCore.Mvc;

namespace Acme.Shop.Web.Controllers
{
    public class CheckoutController : Controller
    {
        private readonly PaymentGateway _gateway;

        public CheckoutController(PaymentGateway gateway) => _gateway = gateway;

        [HttpPost]
        public IActionResult Pay(PaymentRequest request)
        {
<<<<<<< .mine
            var result = _gateway.Charge(request, retries: 3);
            _log.Info($"Pagamento {request.OrderId}: {result.Success}");
=======
            var result = _gateway.ChargeAsync(request).GetAwaiter().GetResult();
>>>>>>> .r125
            return result.Success ? RedirectToAction("Done") : View("Error");
        }
    }
}`;
T.checkoutMerged = `using Microsoft.AspNetCore.Mvc;

namespace Acme.Shop.Web.Controllers
{
    public class CheckoutController : Controller
    {
        private readonly PaymentGateway _gateway;

        public CheckoutController(PaymentGateway gateway) => _gateway = gateway;

        [HttpPost]
        public IActionResult Pay(PaymentRequest request)
        {
            var result = _gateway.ChargeAsync(request, retries: 3).GetAwaiter().GetResult();
            _log.Info($"Pagamento {request.OrderId}: {result.Success}");
            return result.Success ? RedirectToAction("Done") : View("Error");
        }
    }
}`;
T.checkoutUpstream = T.checkoutBase.replace('var result = _gateway.Charge(request);', 'var result = _gateway.ChargeAsync(request).GetAwaiter().GetResult();');
T.payBase = `namespace Acme.Shop.Core.Services
{
    public class PaymentGateway
    {
        public PaymentResult Charge(PaymentRequest request)
        {
            // vecchia integrazione sincrona
            return LegacyClient.Send(request.Amount, request.CardToken);
        }
    }
}`;
T.payNew = `using System.Net.Http;
using System.Threading.Tasks;

namespace Acme.Shop.Core.Services
{
    public class PaymentGateway
    {
        private readonly HttpClient _http;

        public PaymentGateway(HttpClient http) => _http = http;

        public async Task<PaymentResult> ChargeAsync(PaymentRequest request, int retries = 3)
        {
            for (var i = 0; i < retries; i++)
            {
                var res = await _http.PostAsJsonAsync("/v2/charges", request);
                if (res.IsSuccessStatusCode)
                    return await res.Content.ReadFromJsonAsync<PaymentResult>();
            }
            return PaymentResult.Failed;
        }
    }
}`;
T.legacyBase = `namespace Acme.Shop.Core.Services
{
    [Obsolete("Usare OrderService.CalculateTotal")]
    public static class LegacyPriceCalculator
    {
        public static decimal Total(decimal[] prices)
        {
            decimal t = 0;
            foreach (var p in prices) t += p;
            return t;
        }
    }
}`;
T.couponNew = `namespace Acme.Shop.Core.Models
{
    public class Coupon
    {
        public string Code { get; set; }
        public decimal Percent { get; set; }
        public DateTime ExpiresOn { get; set; }

        public decimal Apply(decimal subtotal)
        {
            if (DateTime.UtcNow > ExpiresOn)
                return subtotal;
            return subtotal - subtotal * Percent / 100m;
        }
    }
}`;
T.cshtmlBase = `@model Acme.Shop.Core.Models.Order

<h2>Il tuo carrello</h2>
<table class="table">
@foreach (var l in Model.Lines)
{
  <tr><td>@l.Name</td><td>@l.Quantity</td><td>@l.Price</td></tr>
}
</table>`;
T.invBase = `namespace Acme.Shop.Core.Services
{
    public interface IInventoryService
    {
        int Available(string sku);
        void Reserve(string sku, int qty);
    }

    public class InventoryService : IInventoryService
    {
        private readonly IStockRepository _repo;
        public InventoryService(IStockRepository repo) => _repo = repo;

        public int Available(string sku) => _repo.GetQty(sku);
        public void Reserve(string sku, int qty) => _repo.Decrease(sku, qty);
    }
}`;
T.progBase = `var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllersWithViews();
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<IInventoryService, InventoryService>();

var app = builder.Build();
app.UseStaticFiles();
app.MapDefaultControllerRoute();
app.Run();`;
T.orderModel = `namespace Acme.Shop.Core.Models
{
    public class Order
    {
        public int Id { get; set; }
        public List<OrderLine> Lines { get; set; } = new();
        public Coupon Coupon { get; set; }
    }
}`;
T.notes = `TODO (appunti locali)
- chiedere a Marco il formato dei coupon
- verificare scadenze in UTC
- aggiungere test per Coupon.Apply`;
T.export = `param([string]$Out = ".\\orders.csv")
Get-Content .\\data\\orders.json | ConvertFrom-Json | Export-Csv $Out -NoTypeInformation
Write-Host "Esportato in $Out"`;
T.sln = `Microsoft Visual Studio Solution File, Format Version 12.00
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Acme.Shop.Core", "Core\\Acme.Shop.Core.csproj", "{1}"
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Acme.Shop.Web", "Web\\Acme.Shop.Web.csproj", "{2}"
EndProject`;

// ---------- diff unificato (LCS sulle righe) ----------
function lcsOps(a, b) {
  const n = a.length, m = b.length;
  const dp = Array.from({ length: n + 1 }, () => new Int32Array(m + 1));
  for (let i = n - 1; i >= 0; i--) for (let j = m - 1; j >= 0; j--)
    dp[i][j] = a[i] === b[j] ? dp[i + 1][j + 1] + 1 : Math.max(dp[i + 1][j], dp[i][j + 1]);
  const ops = []; let i = 0, j = 0;
  while (i < n && j < m) {
    if (a[i] === b[j]) { ops.push({ t: ' ', s: a[i] }); i++; j++; }
    else if (dp[i + 1][j] >= dp[i][j + 1]) { ops.push({ t: '-', s: a[i++] }); }
    else { ops.push({ t: '+', s: b[j++] }); }
  }
  while (i < n) ops.push({ t: '-', s: a[i++] });
  while (j < m) ops.push({ t: '+', s: b[j++] });
  return ops;
}
function unifiedHunks(a, b, ctx = 3) {
  const ops = lcsOps(a, b), ch = [];
  ops.forEach((o, i) => { if (o.t !== ' ') ch.push(i); });
  if (!ch.length) return '';
  const groups = []; let s = ch[0], e = ch[0];
  for (let k = 1; k < ch.length; k++) { if (ch[k] - e <= 2 * ctx + 1) e = ch[k]; else { groups.push([s, e]); s = e = ch[k]; } }
  groups.push([s, e]);
  const out = [];
  for (const [gs, ge] of groups) {
    const from = Math.max(0, gs - ctx), to = Math.min(ops.length - 1, ge + ctx);
    let os = 0, ns = 0;
    for (let i = 0; i < from; i++) { if (ops[i].t !== '+') os++; if (ops[i].t !== '-') ns++; }
    let oc = 0, nc = 0; const lines = [];
    for (let i = from; i <= to; i++) { const o = ops[i]; if (o.t !== '+') oc++; if (o.t !== '-') nc++; lines.push(o.t + o.s); }
    out.push(`@@ -${oc === 0 ? os : os + 1},${oc} +${nc === 0 ? ns : ns + 1},${nc} @@`, ...lines);
  }
  return out.join('\n');
}
const lines = t => (t == null || t === '' ? [] : t.split('\n'));
function mkDiff(path, a, b, revOld, revNew, ctx = 3) {
  const hunks = unifiedHunks(lines(a), lines(b), ctx);
  if (!hunks) return '';
  const p = fwd(path);
  const o = a == null ? '(nonexistent)' : `(revision ${revOld})`;
  const n = b == null ? '(nonexistent)' : (revNew ? `(revision ${revNew})` : '(working copy)');
  return `Index: ${p}\n${'='.repeat(67)}\n--- ${p}\t${o}\n+++ ${p}\t${n}\n${hunks}\n`;
}

// ---------- stato dell'applicazione ----------
let S;
function F(path, st, base_, text, it, en) { return { path, st, base: base_, text, it, en }; }

function freshFiles() {
  return [
    F('Acme.Shop.sln', 'N', T.sln, T.sln),
    F('Core\\Models\\Coupon.cs', 'A', null, T.couponNew, 'la classe Coupon', 'the Coupon class'),
    F('Core\\Models\\Order.cs', 'N', T.orderModel, T.orderModel),
    F('Core\\Services\\InventoryService.cs', 'N', T.invBase, T.invBase),
    F('Core\\Services\\LegacyPriceCalculator.cs', 'D', T.legacyBase, null, 'il vecchio calcolatore prezzi', 'the legacy price calculator'),
    F('Core\\Services\\OrderService.cs', 'M', T.orderBase, T.orderMod, 'il calcolo del totale con coupon', 'order total with coupon support'),
    F('Core\\Services\\PaymentGateway.cs', 'R', T.payBase, T.payNew, 'il gateway di pagamento (ora asincrono)', 'the payment gateway (now async)'),
    F('Web\\Controllers\\CartController.cs', 'M', T.cartBase, T.cartMod, "l'azione ApplyCoupon nel carrello", 'the ApplyCoupon cart action'),
    F('Web\\Controllers\\CheckoutController.cs', 'C', T.checkoutBase, T.checkoutConf, 'il checkout (conflitto con la r125)', 'checkout (conflict with r125)'),
    F('Web\\Program.cs', 'N', T.progBase, T.progBase),
    F('Web\\Views\\Cart\\Index.cshtml', '!', T.cshtmlBase, null, 'la vista del carrello', 'the cart view'),
    F('docs\\notes.txt', '?', null, T.notes, 'gli appunti su coupon e scadenze', 'notes about coupons and expiry'),
    F('tools\\export-orders.ps1', '?', null, T.export, 'lo script di esportazione ordini', 'the order export script'),
    Object.assign(F('Web\\obj', '?', null, null), { dir: true }),
  ];
}

function seedLog() {
  const d = (days, h, m) => { const x = new Date(); x.setDate(x.getDate() - days); x.setHours(h, m, 0, 0); return x; };
  const H = (p, a, b, r) => mkDiff(p, a, b, r - 1, r);
  return [
    { rev: 124, author: 'lbianchi', date: d(1, 17, 42), msg: 'Corregge arrotondamento IVA nel riepilogo ordine', paths: [{ a: 'M', p: '/trunk/Core/Services/OrderService.cs' }],
      diff: H('Core\\Services\\OrderService.cs', 'return Math.Round(total, 1);', 'return Math.Round(total, 2, MidpointRounding.AwayFromZero);', 124) },
    { rev: 123, author: 'mrossi', date: d(2, 11, 5), msg: 'Aggiunge log delle richieste di pagamento', paths: [{ a: 'M', p: '/trunk/Core/Services/PaymentGateway.cs' }, { a: 'A', p: '/trunk/Core/Logging/PaymentLog.cs' }],
      diff: H('Core\\Services\\PaymentGateway.cs', 'public PaymentResult Charge(PaymentRequest r)\n{\n    return LegacyClient.Send(r.Amount, r.CardToken);\n}', 'public PaymentResult Charge(PaymentRequest r)\n{\n    PaymentLog.Write(r);\n    return LegacyClient.Send(r.Amount, r.CardToken);\n}', 123) + '\n' + H('Core\\Logging\\PaymentLog.cs', null, 'public static class PaymentLog\n{\n    public static void Write(PaymentRequest r) => File.AppendAllText("pay.log", r.OrderId + "\\n");\n}', 123) },
    { rev: 122, author: 'andre', date: d(4, 9, 30), msg: 'Refactoring InventoryService: usa IStockRepository', paths: [{ a: 'M', p: '/trunk/Core/Services/InventoryService.cs' }],
      diff: H('Core\\Services\\InventoryService.cs', 'private readonly SqlConnection _db;\npublic InventoryService(SqlConnection db) => _db = db;', 'private readonly IStockRepository _repo;\npublic InventoryService(IStockRepository repo) => _repo = repo;', 122) },
    { rev: 121, author: 'gverdi', date: d(6, 15, 12), msg: 'Aggiorna dipendenze NuGet e versione .NET', paths: [{ a: 'M', p: '/trunk/Core/Acme.Shop.Core.csproj' }, { a: 'M', p: '/trunk/Web/Acme.Shop.Web.csproj' }],
      diff: H('Core\\Acme.Shop.Core.csproj', '<TargetFramework>net6.0</TargetFramework>', '<TargetFramework>net8.0</TargetFramework>', 121) },
    { rev: 120, author: 'mrossi', date: d(9, 10, 1), msg: 'Aggiunge pagina carrello', paths: [{ a: 'A', p: '/trunk/Web/Views/Cart/Index.cshtml' }, { a: 'M', p: '/trunk/Web/Controllers/CartController.cs' }],
      diff: H('Web\\Views\\Cart\\Index.cshtml', null, T.cshtmlBase, 120) },
    { rev: 119, author: 'andre', date: d(12, 16, 55), msg: 'Rimuove codice morto dal checkout', paths: [{ a: 'M', p: '/trunk/Web/Controllers/CheckoutController.cs' }],
      diff: H('Web\\Controllers\\CheckoutController.cs', 'var tmp = new List<string>();\nreturn View();', 'return View();', 119) },
    { rev: 118, author: 'lbianchi', date: d(15, 8, 20), msg: 'Import iniziale della solution Acme.Shop', paths: [{ a: 'A', p: '/trunk/Acme.Shop.sln' }],
      diff: H('Acme.Shop.sln', null, T.sln, 118) },
  ];
}

function fillerLog() {
  const au = ['lbianchi', 'mrossi', 'andre', 'gverdi'], msgs = ['Aggiorna la documentazione', 'Corregge un refuso nei messaggi', 'Piccolo refactoring dei servizi', 'Aggiunge test per il carrello', 'Aggiorna le dipendenze', 'Migliora i log di errore', 'Sistemazione degli stili della pagina ordini'], out = [];
  for (let r = 117; r >= 50; r--) { const d = new Date(); d.setDate(d.getDate() - (16 + (117 - r))); d.setHours(9 + (r % 8), (r * 7) % 60, 0, 0); out.push({ rev: r, author: au[r % 4], date: d, msg: msgs[r % msgs.length], paths: [{ a: 'M', p: '/trunk/docs/CHANGELOG.md' }], diff: mkDiff('docs\\CHANGELOG.md', '- voce ' + (r - 1), '- voce ' + (r - 1) + '\n- voce ' + r, r - 1, r) }); }
  return out;
}
function freshEnv() {
  return { svn: true, ghInstalled: true, winget: true, tortoise: false, gh: true, copilotRuntime: false, claudeInstalled: true, claudeLogin: true, anthropicKey: false, failFirst: false, bigDiff: false };
}
function freshSettings() {
  return { aiprovider: 'auto', claudemodel: 'claude-sonnet-5-5', anthropicapikey: '', copilotmodel: 'default', aiendpoint: GH_MODELS, aiapikey: '', aimodel: 'openai/gpt-4o-mini', defaultlanguage: 'auto', workingcopypath: '' };
}
function freshVM() {
  return { statusAuto: true, autoDetected: false, lang: null, view: 'tree', collapsed: {}, wcPath: '', showInput: true, statusMsg: 'Caricamento in corso...', err: false, aiMsg: '', branch: '', files: [], header: 'Changes', diffVisible: false, diffTitle: '', diffContent: '', commitMsg: '', aiBusy: false, trace: [], dl: null, loading: false };
}
function freshLVM() {
  return { path: '', limit: 50, canMore: false, msg: 'Inserisci il percorso della working copy e premi Carica log.', entries: [], sel: null, diff: '' };
}
function freshState(mode) {
  const free = mode !== 'first';
  const s = {
    mode,
    vsInstalled: free, restarting: false,
    solution: free ? { name: 'Acme.Shop', dir: WC, inWc: true } : null,
    env: freshEnv(), settings: freshSettings(),
    sess: { svnPrompted: false, aiPrompted: false },   // flag "una volta per sessione" (si azzerano con "Riavvia VS")
    files: freshFiles(), ign: { '.': ['*.suo', '.vs'], 'Web': ['bin'] }, ignBase: { '.': ['*.suo', '.vs'], 'Web': ['bin'] },
    log: seedLog().concat(fillerLog()), headRev: 124, wcRev: 124, branch: 'trunk',
    scn: 0, simStep: 0, cmd: [],
    ui: { menu: null, sub: false, open: [], active: null, statusOpen: false, logOpen: false, buf: {}, selFile: null, panelOpen: false, view: 'dock', seOpen: false, prereq: false, diffs: [] },
    vm: freshVM(), lvm: freshLVM(),
    watch: { pending: false, last: 0, fire: 0, note: '' },
    f: { svnPromptDone: false, svnPromptChoice: null, wcLoaded: false, aiNone: false, aiPromptShown: false, tourCommits: 0, optionsSaved: false },
    tour: { on: false, step: 0, done: false },
    wiz: freshWiz(), toasts: [], pending: {}, prq: { snap: null, feedback: '', init: false, busy: false },
  };
  if (!free) {  // primo utilizzo: macchina "vergine"
    Object.assign(s.env, { svn: false, ghInstalled: false, gh: false, claudeInstalled: false, claudeLogin: false, anthropicKey: false, copilotRuntime: false, tortoise: false });
  }
  return s;
}

// ---------- livello "svn" simulato ----------
function logCmd(c) { S.cmd.push(c); if (S.cmd.length > 80) S.cmd.shift(); }
async function svn(cmd, ms = 450) {
  logCmd('svn ' + cmd);
  await sleep(ms);
  if (!S.env.svn) throw new Error('Impossibile trovare svn.exe: il sistema non riesce a trovare il file specificato.');
}
function isVersioned(f) { return f.st !== '?'; }
function propDirs() { return Object.keys(S.ign).filter(d => (S.ign[d] || []).join('|') !== (S.ignBase[d] || []).join('|')); }
function statusList() {
  const list = S.files.filter(f => f.st !== 'N').map(f => ({ path: f.path, st: f.st, dir: !!f.dir, prop: false }));
  propDirs().forEach(d => { if (!list.some(x => x.path === d)) list.push({ path: d, st: 'M', dir: true, prop: true }); });
  return list.sort((a, b) => (a.path === '.' ? -1 : b.path === '.' ? 1 : a.path.toLowerCase().localeCompare(b.path.toLowerCase())));
}
const fileBy = p => S.files.find(f => f.path === p);
function propDiff(d) {
  const a = S.ignBase[d] || [], b = S.ign[d] || [];
  const p = fwd(d);
  return `Index: ${p}\n${'='.repeat(67)}\n--- ${p}\t(revision ${S.wcRev})\n+++ ${p}\t(working copy)\n\nProperty changes on: ${p}\n${'_'.repeat(67)}\nModified: svn:ignore\n## -${a.length === 0 ? 0 : 1},${a.length} +${b.length === 0 ? 0 : 1},${b.length} ##\n` + lcsOps(a, b).map(o => o.t + o.s).join('\n') + '\n';
}
function fileDiffText(path, ctx = 3) {
  const f = fileBy(path);
  if (!f) return S.ign[path] !== undefined && propDirs().includes(path) ? propDiff(path) : '';
  if (f.dir) return propDirs().includes(path) ? propDiff(path) : '';
  switch (f.st) {
    case 'M': case 'R': case 'C': return mkDiff(f.path, f.base, f.text, S.wcRev, null, ctx);
    case 'A': return mkDiff(f.path, null, f.text, S.wcRev, null, ctx);
    case 'D': return mkDiff(f.path, f.base, null, S.wcRev, null, ctx);
    default: return '';
  }
}
function wcDiffText() {   // svn diff dell'INTERA working copy (solo file versionati)
  let out = S.files.filter(f => ['M', 'A', 'D', 'C', 'R'].includes(f.st) && !f.dir).map(f => fileDiffText(f.path)).join('\n');
  propDirs().forEach(d => { out += '\n' + propDiff(d); });
  return out.trim();
}
function svnAdd(path) {
  const f = fileBy(path); if (!f || f.st !== '?') throw new Error('svn: E200009: Could not add \'' + path + '\'');
  f.st = 'A'; logCmd('A         ' + fwd(path));
}
function svnDelete(path) {
  const f = fileBy(path);
  if (!f) { throw new Error(`svn: E165001: Impossibile eliminare '${fwd(path)}': la cartella ha modifiche locali (usare --force)`); }
  if (f.dir && f.st !== '?') throw new Error(`svn: E165001: Impossibile eliminare la cartella '${fwd(path)}' (usare --force)`);
  if (f.st === 'A') { S.files = S.files.filter(x => x !== f); }
  else { f.st = 'D'; f.text = null; }
  logCmd('D         ' + fwd(path));
}
function svnRevert(path) {
  const f = fileBy(path);
  if (!f) { if (propDirs().includes(path)) { S.ign[path] = (S.ignBase[path] || []).slice(); logCmd("Reverted '" + fwd(path) + "'"); return; } return; }
  if (f.st === 'A') { f.st = '?'; }                       // un file Added torna unversioned (il file resta su disco)
  else { f.st = 'N'; f.text = f.base; if (f.dir) f.dir = true; }
  logCmd("Reverted '" + fwd(path) + "'");
}
function pickSide(t, side) {
  const out = []; let st = 0;
  for (const l of t.split('\n')) {
    if (l.startsWith('<<<<<<<')) { st = 1; continue; }
    if (l.startsWith('=======') && st === 1) { st = 2; continue; }
    if (l.startsWith('>>>>>>>') && st === 2) { st = 0; continue; }
    if (st === 0 || st === side) out.push(l);
  }
  return out.join('\n');
}
function svnResolve(path, mode = 'working') {
  const f = fileBy(path);
  if (mode === 'working' && /^<<<<<<< /m.test(f.text || '')) throw new Error('Il file contiene ancora i marcatori di conflitto (<<<<<<<, =======, >>>>>>>). Sistemalo a mano e rimuovili, oppure scegli di tenere la versione locale o quella del server.');
  if (mode === 'mine-full') f.text = pickSide(f.text, 1); else if (mode === 'theirs-full') f.text = pickSide(f.text, 2); else if (mode === 'base') f.text = f.base;
  f.st = f.text === f.base ? 'N' : 'M'; delete f.merged; S.ui.buf[path] = undefined;
  logCmd(`svn resolve --accept ${mode} "${fwd(path)}"`); logCmd("Resolved conflicted state of '" + fwd(path) + "'");
}
function svnIgnore(path) {
  const d = parentDir(path), name = base(path);
  if (!S.ign[d]) { S.ign[d] = []; S.ignBase[d] = []; }
  if (!S.ign[d].includes(name)) S.ign[d].push(name);
  const f0 = fileBy(path);
  if (f0 && f0.st === '?') S.files = S.files.filter(x => x.path !== path && !x.path.startsWith(path + '\\'));
  logCmd(`property 'svn:ignore' set on '${fwd(d)}'`);
}
function svnCommit(paths, message) {
  const errs = [];
  const sel = paths.map(p => ({ p, f: fileBy(p) }));
  for (const { p, f } of sel) {
    if (f && f.st === '?') errs.push(`svn: E200009: Could not commit: '${fwd(p)}' is not under version control`);
    else if (f && f.st === 'C') errs.push(`svn: E155015: Aborting commit: '${fwd(p)}' remains in conflict`);
    else if (f && f.st === '!') errs.push(`svn: E155010: The node '${fwd(p)}' was not found.`);
  }
  if (errs.length) throw new Error(errs[0]);
  const rev = S.headRev + 1, changedPaths = [], diffs = [], pr = '/' + S.branch;
  for (const { p, f } of sel) {
    if (!f) { if (propDirs().includes(p)) { diffs.push(propDiff(p)); changedPaths.push({ a: 'M', p: pr + (p === '.' ? '' : '/' + fwd(p)) }); S.ignBase[p] = (S.ign[p] || []).slice(); } continue; }
    const d = fileDiffText(p); if (d) diffs.push(d.replace(/\(revision \d+\)\n\+\+\+/, `(revision ${rev - 1})\n+++`).replace(/\(working copy\)/, `(revision ${rev})`));
    changedPaths.push({ a: f.st === 'R' ? 'R' : f.st, p: pr + '/' + fwd(p) });
    if (f.st === 'D') S.files = S.files.filter(x => x !== f);
    else { f.st = 'N'; f.base = f.text; }
  }
  S.headRev = rev; S.wcRev = rev;
  S.log.unshift({ rev, author: USER, date: new Date(), msg: message, paths: changedPaths, diff: diffs.join('\n') });
  logCmd(`Committed revision ${rev}.`);
  return rev;
}

// ---------- update: modifiche in arrivo da un collega ----------
function inc(rev, author, msg, paths, diff) { return { rev, author, date: new Date(), msg, paths, diff }; }
function svnUpdate() {
  const out = []; const r = S.headRev + 1;
  const iv = fileBy('Core\\Services\\InventoryService.cs');
  const cart = fileBy('Web\\Controllers\\CartController.cs');
  const order = fileBy('Core\\Models\\Order.cs');
  const ck = fileBy('Web\\Controllers\\CheckoutController.cs');
  const k = S.scn;
  if (k === 0) {
    const nt = iv.base.replace('public int Available(string sku) => _repo.GetQty(sku);', 'public int Available(string sku) => Math.Max(0, _repo.GetQty(sku));');
    const diff = mkDiff(iv.path, iv.base, nt, r - 1, r);
    iv.base = nt; if (iv.st === 'N') iv.text = nt;
    S.log.unshift(inc(r, 'mrossi', 'InventoryService: la giacenza non puo\' essere negativa', [{ a: 'M', p: '/' + S.branch + '/Core/Services/InventoryService.cs' }], diff));
    out.push('U    Core/Services/InventoryService.cs'); S.headRev = r;
  } else if (k === 1) {
    const nt = cart.base.replace('return View(order);', 'return View(order ?? new Order());');
    const diff = mkDiff(cart.path, cart.base, nt, r - 1, r);
    const oldBase = cart.base; cart.base = nt;
    S.log.unshift(inc(r, 'lbianchi', 'Carrello: gestisce ordine nullo', [{ a: 'M', p: '/' + S.branch + '/Web/Controllers/CartController.cs' }], diff));
    S.headRev = r;
    if (cart.st === 'M') {
      cart.merged = cart.text.replace('return View(order);', 'return View(order ?? new Order());');
      cart.text = cart.text.replace('            return View(order);', '<<<<<<< .mine\n            return View(order);\n=======\n            return View(order ?? new Order());\n>>>>>>> .r' + r);
      cart.st = 'C'; out.push('C    Web/Controllers/CartController.cs');
    } else { cart.text = nt; out.push('U    Web/Controllers/CartController.cs'); }
    void oldBase;
  } else if (k === 2) {
    const nt = T.orderModel.replace('public int Id { get; set; }', 'public int Id { get; set; }\n        public string CustomerEmail { get; set; }');
    const diff = mkDiff(order.path, order.base, nt, r - 1, r);
    order.base = nt; order.text = nt;
    const cu = F('Core\\Models\\Customer.cs', 'N', 'namespace Acme.Shop.Core.Models\n{\n    public class Customer { public string Email { get; set; } }\n}', 'namespace Acme.Shop.Core.Models\n{\n    public class Customer { public string Email { get; set; } }\n}');
    S.files.push(cu);
    S.log.unshift(inc(r, 'gverdi', 'Aggiunge Customer e CustomerEmail sull\'ordine', [{ a: 'A', p: '/' + S.branch + '/Core/Models/Customer.cs' }, { a: 'M', p: '/' + S.branch + '/Core/Models/Order.cs' }], diff + '\n' + mkDiff(cu.path, null, cu.text, r)));
    out.push('A    Core/Models/Customer.cs', 'U    Core/Models/Order.cs'); S.headRev = r;
  } else {
    void ck;
    S.headRev = r;
    S.log.unshift(inc(r, 'mrossi', 'Aggiorna README e commenti', [{ a: 'M', p: '/' + S.branch + '/Acme.Shop.sln' }], mkDiff('Acme.Shop.sln', T.sln, T.sln + '\n# VisualStudioVersion = 17', r - 1, r)));
    out.push('U    Acme.Shop.sln');
  }
  S.scn++; S.wcRev = S.headRev;
  out.push(`Updated to revision ${S.headRev}.`);
  out.forEach(l => logCmd(l));
  return out;
}
﻿
// ---------- modali (stile message box di VS) ----------
const overlay = () => $('#overlay');
const mstack = [];
function modal(o) {
  return new Promise(resolve => {
    const el = document.createElement('div');
    el.className = 'modal' + (o.wide ? ' wide' : '');
    el.setAttribute('role', 'dialog'); el.setAttribute('aria-modal', 'true');
    const btns = (o.buttons || [{ label: 'OK', value: true, primary: true }]);
    el.innerHTML = `${o.banner ? `<div class="banner">${o.banner}</div>` : ''}
      <div class="m-title"><span>${esc(o.title || 'SVNAssist')}</span><button class="btn ghost sm" data-m="x" aria-label="Chiudi">&#10005;</button></div>
      <div class="m-body ${o.icon ? 'msg' : ''}">${o.icon ? `<div class="m-ico">${o.icon}</div>` : ''}<div style="flex:1;min-width:0">${o.body || ''}</div></div>
      <div class="m-foot">${o.footLeft ? `<span class="l">${o.footLeft}</span>` : ''}${btns.map((b, i) => `<button class="btn ${b.primary ? 'primary' : ''}" data-m="${i}" ${b.disabled ? 'disabled' : ''}>${esc(b.label)}</button>`).join('')}</div>`;
    const entry = { el, close: null };
    const close = v => {
      const i = mstack.indexOf(entry); if (i >= 0) mstack.splice(i, 1);
      el.remove(); syncModals(); resolve(v);
    };
    entry.close = close;
    entry.cancel = () => close(o.cancel !== undefined ? o.cancel : (btns.length === 1 ? btns[0].value : false));
    el.addEventListener('click', e => {
      const b = e.target.closest('[data-m]'); if (!b) return;
      if (b.dataset.m === 'x') return entry.cancel();
      const btn = btns[+b.dataset.m];
      if (btn.onClick) { const r = btn.onClick(el, close); if (r === false) return; }
      close(btn.value);
    });
    overlay().appendChild(el); mstack.push(entry); syncModals();
    if (o.onOpen) o.onOpen(el, close);
    const f = el.querySelector('.btn.primary') || el.querySelector('input,textarea,select,button[data-m]');
    if (f) f.focus();
  });
}
function syncModals() {
  const ov = overlay();
  ov.classList.toggle('on', mstack.length > 0);
  mstack.forEach((m, i) => m.el.style.display = i === mstack.length - 1 ? '' : 'none');
}
const prompt = (text, kind = 'ok', title) => modal({
  title: title || 'SVNAssist', icon: kind === 'okcancel' ? '&#10067;' : '&#8505;&#65039;', body: `<p>${esc(text)}</p>`,
  buttons: kind === 'okcancel' ? [{ label: 'OK', value: true, primary: true }, { label: 'Annulla', value: false }] : [{ label: 'OK', value: true, primary: true }],
  cancel: kind === 'okcancel' ? false : true,
});
function toast(msg, ms = 3200) {
  const t = document.createElement('div'); t.className = 'toast'; t.textContent = msg; $('#toasts').appendChild(t);
  setTimeout(() => t.remove(), ms);
}

// ---------- prima verifica svn.exe (SvnSetupAssistant) ----------
async function ensureSvn() {
  if (S.env.svn) return true;
  if (S.sess.svnPrompted) return false;
  S.sess.svnPrompted = true;
  const ok = await prompt('SVNAssist non ha trovato il client a riga di comando di Subversion (svn.exe) sul sistema.\n\nÈ necessario per tutte le operazioni SVN. Vuoi installarlo ora?', 'okcancel');
  S.f.svnPromptDone = true; S.f.svnPromptChoice = ok ? 'ok' : 'cancel';
  if (!ok) { render(); return false; }
  if (S.env.winget) {
    const lines = ['> winget install --id Slik.Subversion -e --accept-package-agreements --accept-source-agreements', 'Trovato Subversion [Slik.Subversion] Versione 1.14.2', 'Download in corso: https://sliksvn.com/pub/Slik-Subversion-1.14.2-x64.msi', '  ██████████████████████████████  5.8 MB / 5.8 MB', 'Hash del programma di installazione verificato', 'Avvio dell\'installazione del pacchetto...', 'Installazione completata'];
    let box;
    const done = modal({ title: 'winget', wide: false, body: '<pre id="wg" style="margin:0;background:#0c0c0c;border:1px solid var(--bd);padding:8px;min-height:150px;font:11.5px/1.5 var(--mono);white-space:pre-wrap"></pre>', buttons: [{ label: 'Chiudi', value: true, primary: true, disabled: true }], onOpen: el => { box = el; } });
    await sleep(100);
    const pre = box.querySelector('#wg');
    for (const l of lines) { pre.textContent += l + '\n'; await sleep(650); }
    S.pending.svn = true; logCmd('[winget] installazione di svn.exe terminata (da ricontrollare)');
    box.querySelector('[data-m="0"]').disabled = false; box.querySelector('[data-m="0"]').focus();
    await done;
  } else {
    toast('winget non disponibile: apertura di https://sliksvn.com/download/ nel browser', 4500);
  }
  await prompt('Una volta completata l\'installazione di SVN, riavvia Visual Studio (o riapri il pannello SVN) per usare SVNAssist.');
  render();
  return false;
}

// ---------- catena provider AI (AiServiceFactory) ----------
const isGhModels = e => { try { const u = new URL(e); return u.protocol === 'https:' && u.hostname.toLowerCase() === 'models.github.ai'; } catch (e2) { return false; } };
function resolveChain() {
  const st = S.settings, p = (st.aiprovider || '').trim().toLowerCase();
  const use = n => p === n || p === 'auto' || p === '';
  const all = [], c = [];
  // Copilot
  if (p === 'copilot' || (use('copilot') && S.env.copilotRuntime)) {
    if (S.env.gh || p === 'copilot') { c.push({ k: 'copilot', name: 'GitHub Copilot' }); all.push({ k: 'copilot', name: 'GitHub Copilot', ok: true }); }
  } else if (use('copilot')) all.push({ k: 'copilot', name: 'GitHub Copilot', ok: false, why: 'runtime non scaricato (in auto non parte il download)' });
  if (use('claudecode')) { if (S.env.claudeInstalled) { c.push({ k: 'claudecode', name: 'Claude Code' }); all.push({ k: 'claudecode', name: 'Claude Code', ok: true }); } else all.push({ k: 'claudecode', name: 'Claude Code', ok: false, why: 'claude.exe non trovato' }); }
  if (use('claude')) { if (st.anthropicapikey.trim() || S.env.anthropicKey) { c.push({ k: 'claude', name: 'Claude API' }); all.push({ k: 'claude', name: 'Claude API', ok: true }); } else all.push({ k: 'claude', name: 'Claude API', ok: false, why: 'nessuna API key' }); }
  if (use('openai')) {
    let key = st.aiapikey.trim(), viaGh = false;
    if (!key && isGhModels(st.aiendpoint) && S.env.gh) { key = 'gh'; viaGh = true; }
    if (key) { c.push({ k: 'openai', name: 'OpenAI-compatibile', viaGh }); all.push({ k: 'openai', name: 'OpenAI-compatibile', ok: true }); }
    else all.push({ k: 'openai', name: 'OpenAI-compatibile', ok: false, why: 'nessuna key' + (!isGhModels(st.aiendpoint) ? ' (token GitHub NON inviato a un endpoint non GitHub)' : '') });
  }
  return { chain: c, all, auto: p === 'auto' || p === '' };
}
async function downloadCopilot(setDl) {
  setDl({ pct: 0, label: 'Download di SHA256SUMS.txt dalla release GitHub...' }); await sleep(600);
  for (let i = 0; i <= 25; i++) { const pct = i * 4; setDl({ pct, label: `Download github-copilot-<versione>-win32-x64.tgz: ${pct} / 100 MB` }); await sleep(110); }
  setDl({ pct: 100, label: 'Verifica SHA-256 del file scaricato...' }); await sleep(900);
  setDl({ pct: 100, label: 'Hash corretto. Estrazione in %LOCALAPPDATA%\\SVNAssist\\copilot-runtime...' }); await sleep(900);
  setDl({ pct: 100, label: 'Pulizia parti inutilizzate (~250 MB su disco)...' }); await sleep(500);
  S.env.copilotRuntime = true; logCmd('[copilot] runtime installato');
  setDl(null);
}

// ---------- generazione messaggi (simulata, derivata dalle modifiche vere) ----------
function langOf(l) { l = (l || 'auto').toLowerCase(); return l === 'english' ? 'en' : 'it'; }
const VERB = { it: { A: 'Aggiunge', M: 'Aggiorna', D: 'Rimuove', R: 'Riscrive', C: 'Risolve il conflitto in' }, en: { A: 'Add', M: 'Update', D: 'Remove', R: 'Rewrite', C: 'Resolve conflict in' } };
function changedForAi() {
  const sel = new Set(S.vm.files.filter(x => x.sel).map(x => x.path));
  const fs = S.files.filter(f => ['A', 'M', 'D', 'R', 'C'].includes(f.st) && !f.dir && sel.has(f.path));
  const pr = { A: 0, R: 1, M: 2, C: 3, D: 4 };
  return fs.sort((a, b) => pr[a.st] - pr[b.st]);
}
function descOf(f, l) { return (l === 'en' ? f.en : f.it) || base(f.path); }
function genMessage(lang) {
  const l = langOf(lang), fs = changedForAi();
  if (!fs.length) return l === 'en' ? 'Update configuration' : 'Aggiorna la configurazione';
  const v = VERB[l], and = l === 'en' ? ' and ' : ' e ';
  const cl = f => `${v[f.st]} ${descOf(f, l)}`;
  let title = cl(fs[0]);
  if (fs[1]) { const t2 = title + and + cl(fs[1]).replace(/^(\S+)/, m => m.toLowerCase()); if (t2.length <= 72) title = t2; }
  const body = fs.slice(0, 6).map(f => `- ${v[f.st]} ${base(f.path)}: ${descOf(f, l)}`).join('\n');
  return title + '\n\n' + body;
}
function improveMessage(draft, lang) {
  const l = langOf(lang); let ls = draft.trim().split('\n');
  let t = ls[0].trim().replace(/[.\s]+$/, '');
  const map = l === 'en' ? [[/^fix(ed)?\b/i, 'Fix'], [/^add(ed)?\b/i, 'Add'], [/^updat(ed|e)\b/i, 'Update']] : [[/^fix(ed)?\b/i, 'Corregge'], [/^(added|aggiunto|aggiunta)\b/i, 'Aggiunge'], [/^(updated|aggiornato|aggiornata)\b/i, 'Aggiorna']];
  for (const [re, r] of map) if (re.test(t)) { t = t.replace(re, r); break; }
  t = t.charAt(0).toUpperCase() + t.slice(1);
  let extra = '';
  if (t.length > 72) { const cut = t.lastIndexOf(' ', 69); extra = t.slice(cut + 1); t = t.slice(0, cut) + '...'; }
  let body = ls.slice(1).map(x => x.trim()).filter(Boolean).map(x => x.startsWith('-') ? x : '- ' + x);
  if (extra) body.unshift('- ...' + extra);
  if (!body.length) { const v = VERB[l]; body = changedForAi().slice(0, 4).map(f => `- ${v[f.st]} ${base(f.path)}`); }
  return t + (body.length ? '\n\n' + body.join('\n') : '');
}

// ---------- esecuzione AI condivisa (tool window + dialog) ----------
async function aiCore(kind, ui) {
  const r = resolveChain();
  if (!r.chain.length) {
    ui.trace([]); S.f.aiNone = true; S.f.aiPromptShown = true;
    ui.status('Nessun provider AI configurato: completa la procedura guidata.'); wizOpen(true); return;
  }
  if (kind === 'improve' && !ui.draft().trim()) { ui.status(ui.noDraft); return; }
  if (!S.vm.wcPath) { ui.status('Nessuna working copy configurata.'); return; }
  const selAll = S.vm.files.filter(f => f.sel);
  if (!selAll.length) { ui.status(kind === 'improve' ? 'Metti almeno un file in Staged per migliorare il messaggio.' : 'Metti almeno un file in Staged per generare il messaggio.'); return; }
  const selPaths = selAll.filter(f => !f.dir && ['M', 'A', 'D', 'R', 'C'].includes(f.st)).map(f => f.path);
  ui.busy(true);
  const trace = r.auto ? r.all.map(a => ({ name: a.name, k: a.k, state: a.ok ? 'wait' : 'skip', detail: a.ok ? '' : a.why })) : r.chain.map(a => ({ name: a.name, k: a.k, state: 'wait', detail: '' }));
  ui.trace(trace);
  try {
    ui.status(kind === 'improve' ? ui.improving : ui.generating);
    let diff;
    await svn('diff -x "-U 0 -p" --no-diff-deleted ' + selPaths.map(p => '"' + fwd(p) + '"').join(' '), 500); diff = selPaths.map(p => fileDiffText(p, 0)).filter(Boolean).join('\n').trim();
    if (!diff.trim() && !S.env.bigDiff && kind !== 'improve') { ui.status(ui.noDiff); ui.trace([]); return; }
    const size = S.env.bigDiff ? 54000 : diff.length;
    if (size > 28000 - 1000) {
      const n = Math.ceil(size / 12000);
      for (let i = 1; i <= n; i++) { ui.status(`Diff grande (${Math.round(size / 1000)} KB): riassunto blocco ${i}/${n}...`); await sleep(450); }
    }
    const errors = []; let result = null, first = true, usedName = '';
    for (const c of r.chain) {
      const t = trace.find(x => x.k === c.k); t.state = 'run'; ui.trace(trace);
      try {
        if (S.env.failFirst && first) { await sleep(800); throw new Error(c.k === 'claude' ? 'Anthropic API 529: overloaded_error' : 'AI API error 429 TooManyRequests: rate limit exceeded'); }
        if (c.k === 'copilot') {
          if (!S.env.copilotRuntime) await downloadCopilot(d => ui.dl(d));
          if (!S.env.gh) throw new Error("Sessione Copilot non autenticata: esegui 'gh auth login'");
        } else if (c.k === 'claudecode') {
          await sleep(500);
          if (!S.env.claudeLogin) throw new Error('Claude Code fallito (exit code 1): Not logged in - Please run /login');
        }
        await sleep(1000);
        result = kind === 'improve' ? improveMessage(ui.draft(), ui.lang()) : genMessage(ui.lang());
        t.state = 'ok'; usedName = c.name; ui.trace(trace); break;
      } catch (e) { t.state = 'fail'; t.detail = e.message; errors.push(`${c.name === 'Copilot' ? 'GitHub Copilot' : c.name}: ${e.message}`); ui.trace(trace); }
      first = false;
    }
    if (result == null) throw new Error(r.chain.length > 1 ? 'Nessun provider AI ha risposto.\n' + errors.join('\n') : errors[0].replace(/^[^:]+: /, ''));
    ui.set(result);
    ui.status((kind === 'improve' ? 'Messaggio migliorato' : 'Messaggio generato con AI') + ` (${usedName}).`);
  } catch (e) { ui.status(ui.err + e.message); }
  finally { ui.busy(false); render(); ui.after && ui.after(); }
}
const vmAiUi = () => ({
  wizard: true, noProv: 'Nessun provider AI disponibile (Copilot, Claude o token GitHub).', noDraft: 'Scrivi prima una bozza del messaggio da migliorare.', noDiff: 'Nessun diff disponibile.',
  generating: 'Generazione in corso...', improving: 'Miglioramento in corso...', generated: 'Messaggio generato con AI.', improved: 'Messaggio migliorato.', err: 'Errore AI: ',
  status: t => { S.vm.aiMsg = t; S.vm.aiSeq = (S.vm.aiSeq || 0) + 1; render(); }, busy: b => { S.vm.aiBusy = b; render(); }, draft: () => S.vm.commitMsg, set: t => { S.vm.commitMsg = t; },
  lang: () => S.vm.lang || S.settings.defaultlanguage, trace: t => { S.vm.trace = t.map(x => ({ ...x })); render(); }, dl: d => { S.vm.dl = d; render(); },
});

// ---------- view model: SVN Changes (specchio di SvnStatusToolWindowData) ----------
const DEFAULT_SEL = ['M', 'A', 'D', 'R', '!'];   // i nuovi elementi: modificati/aggiunti/eliminati/sostituiti/mancanti spuntati; conflitti e non versionati no
const LOCKS = new Set();   // file bloccati (svn lock) nella demo
function mkItem(e, old) {
  const st = e.st;
  return { path: e.path, st, action: STNAME[st] || 'Sconosciuto', sel: old && old.has(e.path) ? old.get(e.path) : DEFAULT_SEL.includes(st), dir: e.dir, prop: e.prop,
    canAdd: st === '?', canIgn: st === '?', canDel: st !== '?', canRev: ['M', 'A', 'D', 'C', '!', 'R'].includes(st), canRes: st === 'C', canDiff: st !== '?', locked: LOCKS.has(e.path) };
}
function setStatus(m) { S.vm.statusAuto = false; S.vm.statusMsg = m; S.vm.err = false; }
function vmSummary() {
  const f = S.vm.files; if (!f.length) return 'Nessuna modifica.';
  const c = f.filter(x => x.st === 'C').length, m = f.filter(x => x.st !== 'C' && x.st !== '?').length, u = f.filter(x => x.st === '?').length, p = [];
  if (c) p.push(`${c} in conflitto`); if (m) p.push(`${m} modificati`); if (u) p.push(`${u} non versionati`);
  return p.join(', ') + '.';
}
async function vmRefresh(statusMsg, reloadBranch) {
  const vm = S.vm;
  if (!vm.wcPath.trim() && S.settings.workingcopypath.trim()) vm.wcPath = S.settings.workingcopypath.trim();
  if (!vm.wcPath.trim()) { vm.showInput = true; vm.statusMsg = 'Inserisci il percorso della working copy.'; render(); return; }
  vm.showInput = false; vm.loading = true; render();
  try {
    if (reloadBranch || !vm.branch) await svn('info --show-item relative-url', 200);
    await svn('status --xml', 350);
    if (vm.wcPath.trim().replace(/\\$/, '').toLowerCase() !== WC.toLowerCase()) throw new Error(`svn: E155007: '${vm.wcPath}' is not a working copy`);
    vm.branch = S.branch;
    const old = new Map(vm.files.map(f => [f.path, f.sel]));
    vm.files = statusList().map(e => mkItem(e, old));
    if (statusMsg != null) setStatus(statusMsg); else if (vm.statusAuto) vm.statusMsg = vmSummary();
    S.f.wcLoaded = true;
  } catch (e) { vm.statusMsg = 'Errore: ' + e.message; vm.showInput = true; }
  vm.loading = false; render();
}
async function autoDetect() {
  const vm = S.vm, sol = S.solution, root = sol && sol.inWc ? WC : null;
  if (!root) {
    if (vm.autoDetected) { vm.autoDetected = false; vm.wcPath = ''; vm.branch = ''; vm.files = []; vm.showInput = true; vm.diffVisible = false; vm.diffTitle = ''; vm.diffContent = ''; }
    if (!vm.wcPath.trim()) { vm.showInput = true; vm.statusMsg = sol ? 'La solution corrente non è una working copy SVN.' : 'Inserisci il percorso della working copy e premi Refresh.'; }
    render(); return;
  }
  if (vm.wcPath.toLowerCase() === root.toLowerCase()) return;
  vm.autoDetected = true; vm.wcPath = root; vm.showInput = false; vm.statusAuto = true; vm.statusMsg = 'Working copy SVN rilevata automaticamente, aggiornamento...'; render();
  await sleep(600); await vmRefresh(undefined, true);
}
async function openStatus() {
  S.ui.statusOpen = true; S.ui.view = 'dock'; S.ui.seOpen = false; S.ui.menu = null; render();
  await ensureSvn();
  prqInit();
  await autoDetect();
  if (S.vm.wcPath && S.vm.err === false && !S.vm.files.length && !S.f.wcLoaded) await vmRefresh(undefined, true);
}
async function vmCommit() {
  const vm = S.vm, sel = vm.files.filter(f => f.sel);
  if (!sel.length) { setStatus('Seleziona almeno un file per il commit.'); return render(); }
  if (!vm.commitMsg.trim()) { setStatus('Scrivi un messaggio di commit.'); return render(); }
  const conf = sel.filter(f => f.st === 'C');
  if (conf.length) { setStatus(`Risolvi prima i conflitti (${conf.length} file) o deselezionali: svn non committa file in conflitto.`); return render(); }
  try {
    setStatus('Commit in corso...'); render();
    const chosen = new Set(sel.map(f => f.path)), parents = [];
    sel.forEach(e => { let p = parentDir(e.path); while (p && p !== '.') { const pe = vm.files.find(x => x.path === p); if (pe && !chosen.has(p) && ['A', '?', 'R'].includes(pe.st) && !parents.includes(pe)) parents.push(pe); p = parentDir(p); } });
    const entries = parents.concat(sel);
    const toAdd = entries.filter(e => e.st === '?');
    const whole = toAdd.filter(e => e.dir).flatMap(e => wcUnder(e.path).filter(f => f.st === '?' && f.path !== e.path).map(f => f.path));
    if (toAdd.length) { await svn(`add --force --targets (${toAdd.length} elementi)`, 350); toAdd.forEach(e => svnAdd(e.path)); whole.forEach(p => { if (fileBy(p) && fileBy(p).st === '?') svnAdd(p); }); }
    const toDel = entries.filter(e => e.st === '!');
    if (toDel.length) { await svn(`delete --targets (${toDel.length} elementi)`, 350); toDel.forEach(e => svnDelete(e.path)); }
    const paths = [...new Set(entries.map(e => e.path).concat(whole))];
    const first = vm.commitMsg.trim().split('\n')[0];
    await svn(`commit --depth empty -m "${first}" --targets (${paths.length} elementi)`, 900);
    const rev = svnCommit(paths, vm.commitMsg.trim());
    vm.commitMsg = ''; vm.aiMsg = ''; vm.trace = [];
    if (S.tour.on) S.f.tourCommits++;
    toast(`Creata la revisione r${rev} (visibile in Log / History)`);
    await vmRefresh(`Commit completato: revisione ${rev} (${sel.length} elementi).`, true);
  } catch (e) { await vmRefresh(`Errore commit: ${e.message}`); }
}
function updMsg() { const n = S.files.filter(f => f.st === 'C').length; return `Aggiornato alla revisione ${S.headRev}` + (n ? `: ${n} file in conflitto.` : '.'); }
async function vmUpdate() {
  const vm = S.vm;
  if (!vm.wcPath.trim()) { setStatus('Nessuna working copy configurata.'); return render(); }
  try {
    setStatus('Update in corso...'); render();
    await svn('update', 900);
    const out = svnUpdate();
    toast('svn update (output demo): ' + out.join(' | '), 5500);
    await vmRefresh(updMsg(), true);
  } catch (e) { setStatus('Errore update: ' + e.message); render(); }
}

async function vmUpdateRev() {
  if (!S.vm.wcPath.trim()) { setStatus('Nessuna working copy configurata.'); return render(); }
  if (!await confirmBox('Aggiorna alla revisione HEAD? (nel prodotto un dialogo chiede il numero di revisione: intero positivo oppure HEAD)')) return;
  try {
    setStatus('Update alla revisione HEAD in corso...'); render();
    await svn('update -r HEAD', 900);
    await vmRefresh(updMsg(), true);
  } catch (e) { setStatus('Errore update: ' + e.message); render(); }
}
async function vmCleanup() {
  try {
    setStatus('Cleanup in corso...'); render();
    await svn('cleanup', 700);
    await vmRefresh('Cleanup completato.');
  } catch (e) { setStatus('Errore cleanup: ' + e.message); render(); }
}

// ---------- view model: Log / History (specchio di SvnLogToolWindowData) ----------
async function lvmLoad(limit) {
  const l = S.lvm; limit = limit || 50;
  if (!l.path.trim()) { l.msg = 'Inserisci il percorso della working copy.'; return render(); }
  try {
    l.msg = 'Caricamento log...'; render();
    await svn(`log --xml -l ${limit}`, 700);
    if (l.path.trim().replace(/\\$/, '').toLowerCase() !== WC.toLowerCase()) throw new Error(`svn: E155007: '${l.path}' is not a working copy`);
    l.limit = limit; l.entries = S.log.slice(0, limit); l.canMore = l.entries.length >= limit;
    l.msg = l.entries.length ? `${l.entries.length} revisioni caricate.` : 'Nessuna revisione trovata.';
  } catch (e) { l.msg = 'Errore: ' + e.message; }
  render();
}
const lvmMore = () => lvmLoad(Math.max(S.lvm.limit, 50) + 50);
async function lvmSelect(rev) {
  const l = S.lvm; l.sel = rev; l.diff = 'Caricamento diff...'; l.msg = `Caricamento diff per revisione ${rev}...`; render();
  try { await svn(`diff -c ${rev}`, 450); const e = S.log.find(x => x.rev === rev); l.diff = e && e.diff.trim() ? e.diff : '(nessuna differenza testuale per questa revisione)'; l.msg = `Diff revisione ${rev} caricato.`; }
  catch (e) { l.diff = 'Errore nel caricamento del diff: ' + e.message; l.msg = 'Errore diff: ' + e.message; }
  render();
}
async function openLog() {
  S.ui.logOpen = true; S.ui.active = '__log'; S.ui.view = 'doc'; S.ui.seOpen = false; S.ui.menu = null; render();
  if (S.solution && S.solution.inWc && S.lvm.path.toLowerCase() !== WC.toLowerCase()) { S.lvm.path = WC; await lvmLoad(50); }   // auto-detect della working copy
}

// ---------- file watcher (FileSystemWatcher + polling 1 s, debounce 800 ms) ----------
function fsEvent(path, what) {
  S.watch.pending = true; S.watch.last = Date.now(); S.watch.fire = Date.now(); S.watch.note = `${what}: ${path}`; render();
}
setInterval(async () => {
  const w = S.watch;
  if (w.pending && Date.now() - w.last >= 800 && S.ui.statusOpen && S.vm.wcPath && !S.vm.loading) { w.pending = false; logCmd('[watcher] modifica rilevata, refresh automatico'); await vmRefresh(); }
  if (w.fire && Date.now() - w.fire > 1800) { w.fire = 0; render(); }
}, 400);
function saveFile(path) {
  const f = fileBy(path), buf = S.ui.buf[path]; if (!f || buf == null) return;
  if (f.text === buf) return toast('Nessuna differenza da salvare');
  f.text = buf;
  if (f.st === 'N' && f.text !== f.base) f.st = 'M'; else if (f.st === 'M' && f.text === f.base) f.st = 'N';
  toast('File salvato su disco: ' + path);
  fsEvent(path, 'FileSystemWatcher: modificato');
}
async function simulateChange() {
  const k = S.simStep++ % 3;
  if (k === 0) {
    const f = S.files.find(x => x.st === 'N' && x.path.endsWith('.cs')) || S.files.find(x => x.st === 'M' && x.path.endsWith('.cs'));
    f.text = (f.text || '') + `\n// modifica simulata ${pad(new Date().getMinutes())}:${pad(new Date().getSeconds())}`;
    if (f.st === 'N') f.st = 'M'; S.ui.buf[f.path] = undefined; fsEvent(f.path, 'modificato');
  } else if (k === 1) {
    const n = 'tools\\debug-' + (S.simStep) + '.log';
    S.files.push(F(n, '?', null, 'log temporaneo'));
    fsEvent(n, 'creato');
  } else {
    const f = S.files.find(x => x.st === 'N' && x.path !== 'Acme.Shop.sln' && !x.path.endsWith('.cs')) || S.files.find(x => x.st === 'N' && x.path !== 'Acme.Shop.sln');
    if (f) { f.st = '!'; f.text = null; fsEvent(f.path, 'eliminato da disco'); } else toast('Nessun file da eliminare');
  }
}

// =====================================================================
//  RENDER
// =====================================================================
function saveFocus() {
  const a = document.activeElement; if (!a || a === document.body) return null;
  return { id: a.id, act: a.dataset && a.dataset.act, arg: a.dataset && a.dataset.arg, model: a.dataset && a.dataset.model, s: a.selectionStart, e: a.selectionEnd };
}
function restoreFocus(f) {
  if (!f) return; let el = null;
  if (f.id) el = document.getElementById(f.id);
  if (!el && f.act) el = $$(`[data-act="${f.act}"]`).find(x => x.dataset.arg === f.arg);
  if (el && el !== document.activeElement && !el.disabled) { el.focus(); try { if (f.s != null) el.setSelectionRange(f.s, f.e); } catch (e) { } }
}
function render() {
  if (mstack.length && mstack.some(m => m.el.querySelector('input:focus,textarea:focus,select:focus'))) { /* modali non vengono ricostruite */ }
  const f = saveFocus();
  document.body.classList.toggle('panel-closed', !S.ui.panelOpen);
  $('#btnPanel').setAttribute('aria-pressed', S.ui.panelOpen);
  langBtn();
  $('#vsBody').className = 'vs-body v-' + S.ui.view + (S.ui.seOpen ? ' se-open' : '');
  renderMenu(); renderSE(); renderDoc(); renderDock(); renderStatusBar(); renderSim(); renderTour(); renderSplash(); applyTarget();
  restoreFocus(f);
  document.documentElement.dataset.theme = __mockBridge.theme.vs; __mockBridge.pushAll();
  $$('input[data-indet]').forEach(e => { e.indeterminate = true; });
}
function renderSplash() {
  let sp = $('#splash');
  if (S.restarting) {
    if (!sp) { sp = document.createElement('div'); sp.id = 'splash'; sp.style.cssText = 'position:absolute;inset:0;background:#2b1a40;z-index:80;display:grid;place-items:center;color:#fff;text-align:center'; sp.innerHTML = '<div><div style="font-size:26px;font-weight:300">Visual Studio</div><p><span class="spin"></span>Riavvio in corso...</p></div>'; $('#vs').style.position = 'relative'; $('#vs').appendChild(sp); }
  } else if (sp) sp.remove();
}
const MENUS = [['file', 'File'], ['edit', 'Modifica'], ['view', 'Visualizza'], ['proj', 'Progetto'], ['build', 'Compila'], ['dbg', 'Debug'], ['test', 'Test'], ['tools', 'Strumenti'], ['ext', 'Estensioni'], ['win', 'Finestra'], ['help', '?']];
function renderMenu() {
  const m = S.ui.menu;
  $('#menubar').innerHTML = MENUS.map(([id, label]) => {
    let dd = '';
    if (m === id) {
      if (id === 'file') dd = `<div class="dd" role="menu"><button data-act="openSolDlg" ${S.solution ? 'disabled' : ''}>Apri progetto/soluzione...<kbd>Ctrl+Maiusc+O</kbd></button><button data-act="closeSol" ${S.solution ? '' : 'disabled'}>Chiudi soluzione</button></div>`;
      if (id === 'tools') dd = `<div class="dd" role="menu"><button data-act="optionsOpen">Opzioni...</button></div>`;
      if (id === 'ext') dd = `<div class="dd" role="menu"><button data-act="vsixOpen">Gestisci estensioni...</button><hr>` +
        (S.vsInstalled ? `<div class="sub"><button data-act="subToggle" aria-haspopup="true" aria-expanded="${S.ui.sub}">SVNAssist <span>&#9656;</span></button>${S.ui.sub ? `<div class="dd" role="menu">
          <button data-act="cmd" data-arg="status">SVN Changes</button><button data-act="cmd" data-arg="commit">Commit...</button><hr>
          <button data-act="cmd" data-arg="update">Update</button><button data-act="cmd" data-arg="log">Log / History</button></div>` : ''}</div>`
          : `<button disabled>SVNAssist (non installato)</button>`) + '</div>';
    }
    return `<div class="${['file', 'tools', 'ext'].includes(id) ? '' : 'dummy'}"><button data-act="menu" data-arg="${id}" aria-haspopup="true" aria-expanded="${m === id}">${label}</button>${dd}</div>`;
  }).join('');
  $('#solName').textContent = S.solution ? `${S.solution.name} · ${S.solution.dir}` : 'Nessuna soluzione';
}
function renderSE() {
  const el = $('#seTree');
  if (!S.solution) { el.innerHTML = `<div class="se-empty">Nessuna soluzione aperta.<br><br><button class="btn primary" data-act="openSolDlg">Apri una soluzione...</button></div>`; $('#seNote').textContent = ''; return; }
  const vis = S.files.filter(f => f.text !== null || f.dir).sort((a, b) => a.path.toLowerCase().localeCompare(b.path.toLowerCase()));
  const tree = {};
  vis.forEach(f => { const seg = f.path.split('\\'); let n = tree; seg.forEach((s, i) => { if (i === seg.length - 1 && !f.dir) n[s] = f; else n = (n[s] = n[s] && !n[s].path ? n[s] : {}); }); });
  const draw = (n, depth, pre) => Object.keys(n).sort((a, b) => ((n[a].path ? 1 : 0) - (n[b].path ? 1 : 0)) || a.localeCompare(b)).map(k => {
    const v = n[k], pad_ = 6 + depth * 14;
    if (v.path) return `<button class="node ${S.ui.selFile === v.path ? 'sel' : ''}" style="padding-left:${pad_ + 14}px" data-act="openFile" data-arg="${esc(v.path)}" data-sectx="file|${esc(v.path)}"><span class="ic ${k.endsWith('.cs') ? 'cs' : 'cfg'}">${k.endsWith('.cs') ? 'C#' : '&#9679;'}</span>${esc(k)}</button>`;
    const fp = pre ? pre + '\\' + k : k; return `<div class="node" style="padding-left:${pad_}px" data-sectx="${depth === 1 && ['Core', 'Web'].includes(k) ? 'project' : 'folder'}|${esc(fp)}"><span class="ic fld">&#9662;</span><span class="fld">&#128193;</span> ${esc(k)}</div>` + draw(v, depth + 1, fp);
  }).join('');
  el.innerHTML = `<div class="node" data-sectx="solution|" style="padding-left:6px"><span class="ic">&#9662;</span><b>Soluzione '${esc(S.solution.name)}'</b></div>` + draw(tree, 1, '');
  $('#seNote').textContent = '';
}
function renderDoc() {
  const open = S.ui.open.filter(p => { const f = fileBy(p); return f && f.text !== null; });
  S.ui.open = open;
  const x = id => `<span data-act="closeTab" data-arg="${esc(id)}" title="Chiudi" style="margin-left:6px;color:var(--fg3)">&#10005;</span>`;
  const tab = (id, label) => `<button role="tab" aria-selected="${S.ui.active === id}" data-act="activate" data-arg="${esc(id)}">${esc(label)} ${x(id)}</button>`;
  $('#docTabs').innerHTML = open.map(p => tab(p, base(p))).join('') + (S.ui.logOpen ? tab('__log', 'SVN Log') : '') + S.ui.diffs.map(d => tab(d.id, d.title)).join('');
  const body = $('#docBody');
  if (!body.firstChild) body.innerHTML = '<div id="docText" style="flex:1;display:flex;flex-direction:column;min-height:0"></div><div class="doc-frames" id="docFrames" hidden></div>';
  const fr = (S.ui.logOpen ? [['__log', 'log']] : []).concat(S.ui.diffs.map(d => [d.id, 'diff']));
  __mockBridge.syncFrames($('#docFrames'), fr, S.ui.active);
  const showFrame = fr.some(([id]) => id === S.ui.active);
  $('#docFrames').hidden = !showFrame; $('#docText').hidden = showFrame;
  if (showFrame) return;
  const text = $('#docText');
  const f = open.includes(S.ui.active) ? fileBy(S.ui.active) : null;
  if (!f) {
    text.innerHTML = `<div class="welcome"><h2>Visual Studio</h2><p>${S.solution ? 'Apri un file da Esplora soluzioni per modificarlo: salvandolo, il FileSystemWatcher di SVNAssist aggiorna da solo il pannello SVN Changes.' : 'Apri una soluzione dal menu File per iniziare.'}</p>${S.vsInstalled ? '<p class="small">Menu <b>Estensioni &gt; SVNAssist</b>: SVN Changes, Commit..., Update, Log / History.</p>' : '<p class="small">Installa prima l\'estensione SVNAssist.</p>'}</div>`;
    return;
  }
  const txt = S.ui.buf[f.path] != null ? S.ui.buf[f.path] : f.text;
  const n = txt.split('\n').length;
  text.innerHTML = `<div class="editor"><div class="gutter" id="gut">${Array.from({ length: n }, (_, i) => i + 1).join('\n')}</div><textarea id="edtext" spellcheck="false" aria-label="Editor ${esc(f.path)}">${esc(txt)}</textarea></div>
   <div class="ed-bar"><button class="btn sm" data-act="saveFile" data-arg="${esc(f.path)}">Salva (Ctrl+S)</button><span>${esc(f.path)} · stato SVN: <b style="color:var(--st-${f.st === '?' ? 'U' : f.st === '!' ? 'X' : f.st === 'N' ? 'U' : f.st})">${f.st === 'N' ? 'invariato' : f.st}</b></span></div>`;
}
function traceHTML(v) {
  let h = '';
  if (v.dl) h += `<div class="dl"><b>GitHub Copilot: primo utilizzo</b> <span class="muted">(runtime ~100 MB, scaricato una volta sola)</span><div class="pbar"><i style="width:${v.dl.pct}%"></i></div>${esc(v.dl.label)}</div>`;
  if (v.trace.length) {
    h += `<div class="trace"><span class="muted">Catena provider <span class="tag info">dettaglio demo</span>:</span>` + v.trace.map((t, i) => {
      const cls = t.state === 'ok' ? 'ok' : t.state === 'fail' ? 'fail' : t.state === 'skip' ? 'skip' : t.state === 'run' ? 'run' : '';
      const txt = t.state === 'ok' ? `${t.name}: OK` : t.state === 'fail' ? `${t.name}: errore` : t.state === 'skip' ? `${t.name}: saltato (${t.detail})` : t.state === 'run' ? `<span class="spin"></span>${t.name}...` : t.name;
      return (i ? '<span class="muted">&rarr;</span>' : '') + `<span class="chip ${cls}" title="${esc(t.detail || '')}">${txt}</span>`;
    }).join('') + '</div>';
    const f = v.trace.filter(t => t.state === 'fail'); if (f.length) h += `<div class="small" style="color:var(--err)">${f.map(t => esc(t.name + ': ' + t.detail)).join('<br>')}</div>`;
  }
  return h;
}

function renderDock() {
  const d = $('#dock'), key = S.ui.statusOpen ? 'open' : S.vsInstalled ? 'closed-i' : 'closed';
  if (d.__k === key) return;
  d.__k = key;
  if (!S.ui.statusOpen) { __mockBridge.syncFrames(null, [], null); d.innerHTML = `<div class="dock-empty">Nessuna finestra strumenti aperta.<br><br>${S.vsInstalled ? 'Usa <b>Estensioni &gt; SVNAssist &gt; SVN Changes</b>.' : 'Installa prima l\'estensione SVNAssist.'}<br><br>${S.vsInstalled ? '<button class="btn" data-act="cmd" data-arg="status">Apri SVN Changes</button>' : ''}</div>`; return; }
  d.innerHTML = '<div class="dock-tabs"><button role="tab" aria-selected="true">SVN Changes</button><button class="x" data-act="statusClose" aria-label="Chiudi SVN Changes">&#10005;</button></div><div class="dock-body" id="dockBody"></div>';
  __mockBridge.makeFrame($('#dockBody'), 'changes', null);
}
function renderStatusBar() {
  const b = $('#vsStatus'), inWc = S.solution && S.solution.inWc;
  b.className = 'vs-status';
  b.innerHTML = `<span>${S.vm.loading || S.vm.aiBusy ? '<span class="spin"></span>Operazione in corso...' : 'Pronto'}</span><span class="sp"></span>${inWc ? `<span>&#9095; ${esc(S.branch)} · r${S.wcRev}</span>` : ''}<span>${S.vsInstalled ? 'SVNAssist 0.2.0' : 'SVNAssist non installato'}</span>`;
}
function renderSim() {
  const e = S.env, chain = resolveChain();
  const tg = (k, label, hint) => `<label class="tg"><input type="checkbox" data-act="env" data-arg="${k}" ${e[k] ? 'checked' : ''}><span>${label}${hint ? `<span class="hint">${hint}</span>` : ''}</span></label>`;
  const ghOnly = S.settings.aiendpoint && !isGhModels(S.settings.aiendpoint) ? `Il token GitHub NON viene inviato a ${(() => { try { return new URL(S.settings.aiendpoint).host; } catch (x) { return 'questo endpoint'; } })()}` : 'Il token GitHub va solo a Copilot e a models.github.ai';
  $('#simPanel').innerHTML = `
   <h3>Ambiente simulato</h3>
   ${tg('svn', 'SVN installato (svn.exe)', 'se no: al primo uso compare il prompt di installazione')}
   ${tg('winget', 'winget disponibile', 'se no: si apre la pagina di download')}
   ${tg('tortoise', 'TortoiseSVN installato', 'Show Diff apre TortoiseSVN invece del diff inline')}
   ${tg('ghInstalled', 'GitHub CLI installato (gh)')}
   ${tg('gh', 'GitHub CLI loggato (gh auth login)', 'token GitHub rilevato in automatico')}
   ${tg('copilotRuntime', 'Runtime Copilot già scaricato', 'in auto Copilot parte solo se già scaricato')}
   ${tg('claudeInstalled', 'Claude Code installato (claude.exe)')}
   ${tg('claudeLogin', 'Claude Code loggato')}
   ${tg('anthropicKey', 'Variabile ANTHROPIC_API_KEY impostata')}
   ${tg('failFirst', 'Simula errore del primo provider', 'mostra il fallback sul successivo')}
   ${tg('bigDiff', 'Simula diff molto grande', 'riassunto a blocchi da 12.000 caratteri')}
   <div class="note">&#128274; ${esc(ghOnly)}. Ogni credenziale va solo al proprio servizio.</div>
   <h3>Provider risolti (AiServiceFactory)</h3>
   <div class="chain">${chain.chain.length ? chain.chain.map(c => `<span class="chip ok">${esc(c.name)}</span>`).join(' &rarr; ') : '<span class="chip fail">nessun provider disponibile</span>'}<br><span class="small">AI Provider = <b>${esc(S.settings.aiprovider || '(vuoto)')}</b> · lingua = <b>${esc(S.settings.defaultlanguage)}</b></span></div>
   <h3>Aspetto</h3>
   <label class="tg">Tema di Visual Studio:&nbsp;<select data-act="vsTheme" style="width:auto">${[['dark', 'Scuro'], ['light', 'Chiaro'], ['blue', 'Blu']].map(([v, l]) => `<option value="${v}" ${__mockBridge.theme.vs === v ? 'selected' : ''}>${l}</option>`).join('')}</select></label>
   <div class="note">&#9432; Nel menu &quot;&#8943;&quot; di SVN Changes puoi scegliere il tema della sola finestra (Auto = segue Visual Studio).</div>
   <h3>Azioni</h3>
   <button class="btn" data-act="simChange">Simula modifica di un file</button>
   <button class="btn" data-act="restart">Riavvia Visual Studio</button>
   <button class="btn" data-act="reset">Reset demo</button>
   <label class="tg" style="margin-top:6px">Branch della working copy:&nbsp;<select data-act="branchSel" style="width:auto"><option ${S.branch === 'trunk' ? 'selected' : ''}>trunk</option><option ${S.branch === 'branches/feature-coupon' ? 'selected' : ''}>branches/feature-coupon</option></select></label>
   <div class="note">&#9432; "Riavvia VS" azzera i flag "una volta per sessione" (prompt svn.exe e prompt AI) senza cambiare i dati.<br>&#9432; Genera con AI / Migliora inviano all'AI solo le modifiche (<code>svn diff -U 0</code>) dei file <b>spuntati</b>.</div>
   <h3>Ultima catena provider (demo)</h3>
   <div class="chain">${traceHTML(S.vm) || '<span class="muted small">nessuna richiesta AI</span>'}</div>
   <h3>Comandi svn (log demo)</h3>
   <div class="cmdlog" id="cmdlog">${S.cmd.map(l => `<div>${l.startsWith('svn ') ? '<span class="c">$</span> ' : ''}${esc(l)}</div>`).join('')}</div>`;
  const cl = $('#cmdlog'); if (cl) cl.scrollTop = cl.scrollHeight;
}

// ---------- tour ----------
const STEPS = [
  { t: 'Installa il VSIX', p: 'SVNAssist si distribuisce come file .vsix. Un doppio clic apre il VSIX Installer; poi Visual Studio va riavviato.', doLabel: 'Doppio clic sul .vsix', done: () => S.vsInstalled, doIt: () => vsixFlow() },
  { t: 'Apri una solution', p: 'Menu File > Apri progetto/soluzione. Scegli Acme.Shop (dentro una working copy SVN).', hint: 'Prova anche l\'altra solution: fuori da una working copy il pannello lo segnala.', doLabel: 'Apri Acme.Shop', done: () => !!S.solution, doIt: () => openSolDlg() },
  { t: 'Primo comando: manca svn.exe', p: 'Apri Estensioni > SVNAssist > SVN Changes. Se svn.exe non c\'è, SVNAssist chiede (una volta per sessione) se installarlo. Prova OK (winget) oppure Annulla.', doLabel: 'Apri SVN Changes', done: () => S.f.svnPromptDone, doIt: () => openStatus(), extra: () => S.f.svnPromptChoice === 'cancel' ? `<div class="hint">Hai scelto Annulla: il pannello resta vuoto e il prompt non ricompare nella sessione. <button class="btn sm" data-act="tourRetrySvn">Riprova con OK</button></div>` : '' },
  { t: 'Working copy rilevata', p: "Con svn installato premi Ricontrolla nei Prerequisiti (il pannello si apre da solo se svn manca): SVNAssist trova la working copy risalendo dalla solution fino alla cartella .svn e mostra branch e file modificati.", doLabel: 'Ricontrolla', done: () => S.f.wcLoaded, doIt: async () => { await openStatus(); if (Object.keys(S.pending).length) await prqRecheck(); } },
  { t: 'Primo clic su Genera con AI', p: 'Senza nessun provider AI configurato si apre la procedura guidata: provider, prerequisiti, accesso e modello (si riapre anche da Cambia provider AI...).', doLabel: 'Clicca Genera con AI', done: () => S.f.aiNone, doIt: () => vmAi('gen') },
  { t: 'Configura un provider in Tools > Options', p: 'Apri Strumenti > Opzioni > SVNAssist. Esempio: AI Provider = claude e una Anthropic API Key qualsiasi (la demo non la invia da nessuna parte). Oppure imposta "GitHub CLI loggato" nel Pannello simulazione.', doLabel: 'Apri Opzioni', done: () => resolveChain().chain.length > 0, doIt: () => optionsDlg() },
  { t: 'Genera il messaggio e committa', p: 'Premi Genera con AI: il messaggio nasce dai file realmente modificati. Poi, con i file giusti nella sezione Staged (il + li sposta), premi Commit e controlla la nuova revisione in Log / History.', hint: 'I file in conflitto non possono andare in Staged: risolvili (clic destro > Risolvi conflitto...) prima di committare. I non versionati messi in Staged vengono aggiunti e i mancanti eliminati in automatico.', doLabel: 'Genera con AI', done: () => S.f.tourCommits > 0, doIt: () => vmAi('gen') },
];
const TARGETS = [null, '[data-act="menu"][data-arg="file"]', '[data-act="menu"][data-arg="ext"]', '#dock', 'frame:[data-cmd="genAi"]', '[data-act="menu"][data-arg="tools"]', 'frame:[data-cmd="commit"]'];
function applyTarget() {
  $$('.tour-target').forEach(e => e.classList.remove('tour-target'));
  const fr = $('#dock iframe'), fd = fr && fr.contentDocument;
  if (fd) fd.querySelectorAll('.tour-target').forEach(e => e.classList.remove('tour-target'));
  if (!S.tour.on || S.tour.finished || S.tour.min) return;
  const sel = TARGETS[S.tour.step]; if (!sel) return;
  if (sel.startsWith('frame:')) {
    if (!fd || !fd.head) return;
    if (!fd.getElementById('tour-css')) { const st = fd.createElement('style'); st.id = 'tour-css'; st.textContent = '.tour-target{outline:2px solid #ffd24a!important;outline-offset:2px;animation:pulse 1.2s ease-in-out infinite}@keyframes pulse{50%{outline-color:#ffd24a00}}'; fd.head.appendChild(st); }
    const el = fd.querySelector(sel.slice(6)); if (el) el.classList.add('tour-target'); return;
  }
  const el = $(sel); if (el && el.offsetParent !== null) el.classList.add('tour-target');
}
function renderTour() {
  const t = $('#tour'), T_ = S.tour;
  if (!T_.on) { t.hidden = true; return; }
  t.hidden = false;
  if (T_.finished) { t.innerHTML = `<div class="tcard"><div class="tl"><div class="th2"><b>Tour completato</b></div><h4>Hai completato il primo utilizzo</h4><p>Ora esplora liberamente: Update (con possibili conflitti), Add to Ignore List, Log / History, i Prerequisiti, il Pannello simulazione e la scheda Prossimamente.</p></div><div class="tr"><button class="btn primary" data-act="tourSkip">Chiudi il tour</button></div></div>`; return; }
  const s = STEPS[T_.step], done = s.done();
  if (T_.min) { t.innerHTML = `<div class="tcard"><div class="tl"><div class="th2"><b>Passo ${T_.step + 1} di ${STEPS.length}</b> <span>${esc(s.t)}</span></div></div><div class="tr"><button class="btn sm" data-act="tourMin">Espandi</button><button class="btn sm ghost" data-act="tourSkip">Salta tour</button></div></div>`; return; }
  const prq = [2, 5].includes(T_.step) ? `<div class="hint">Mancano dei prerequisiti? <button class="btn sm" data-act="prereqOpen">Apri Prerequisiti</button></div>` : '';
  t.innerHTML = `<div class="tcard"><div class="tl"><div class="th2"><b>Passo ${T_.step + 1} di ${STEPS.length}</b><span class="dots">${STEPS.map((_, i) => `<i class="${i < T_.step ? 'on' : i === T_.step ? 'cur' : ''}"></i>`).join('')}</span></div>
   <h4>${esc(s.t)}</h4><p>${esc(s.p)}</p>${TARGETS[T_.step] ? '<div class="ptr">&#9650; L\'elemento da usare è evidenziato in giallo nell\'interfaccia.</div>' : ''}${s.hint ? `<div class="hint">${esc(s.hint)}</div>` : ''}${s.extra ? s.extra() : ''}${prq}${done ? '<p class="done">&#10003; Passo completato</p>' : ''}</div>
   <div class="tr"><button class="btn" data-act="tourPrev" ${T_.step === 0 ? 'disabled' : ''}>Indietro</button>${done ? '' : `<button class="btn" data-act="tourDo">${esc(s.doLabel)}</button>`}
   <button class="btn primary" data-act="tourNext" ${done ? '' : 'disabled'}>${T_.step === STEPS.length - 1 ? 'Fine' : 'Avanti'}</button><button class="btn ghost" data-act="tourMin">Riduci</button><button class="btn ghost" data-act="tourSkip">Salta tour</button></div></div>`;
}

// =====================================================================
//  PREREQUISITI (specchio di PrerequisitesService / PrerequisitesViewModel)
// =====================================================================
const WG = ' --accept-package-agreements --accept-source-agreements';
const P_SVN = 'C:\\Program Files\\SlikSvn\\bin\\svn.exe', P_TSVN = 'C:\\Program Files\\TortoiseSVN\\bin\\TortoiseProc.exe', P_GH = 'C:\\Program Files\\GitHub CLI\\gh.exe', P_CLAUDE = 'C:\\Users\\' + USER + '\\.local\\bin\\claude.exe';
// finestre console lanciate da ConsoleLauncher (simulate): comando esatto + output
const CONSOLES = {
  svn: { shell: 'cmd', cmd: 'winget install --id Slik.Subversion -e' + WG, wg: true, lines: ['Trovato Subversion [Slik.Subversion] Versione 1.14.2', 'Download in corso...', '  ██████████████████████████████  5.8 MB / 5.8 MB', 'Hash del programma di installazione verificato', 'Installazione completata'], apply: () => { S.env.svn = true; } },
  ghcli: { shell: 'cmd', cmd: 'winget install --id GitHub.cli -e' + WG, wg: true, lines: ['Trovato GitHub CLI [GitHub.cli] Versione 2.60.0', 'Download in corso...', 'Installazione completata'], apply: () => { S.env.ghInstalled = true; } },
  ghlogin: { shell: 'cmd', cmd: 'gh auth login --hostname github.com --git-protocol https --web', lines: ['! First copy your one-time code: 1A2B-3C4D', '- Press Enter to open github.com in your browser...'],
    inter: [{ label: 'Premi Invio (apre il browser)', lines: ['Apertura di https://github.com/login/device ...', 'Conferma il codice 1A2B-3C4D nel browser'] }, { label: 'Ho confermato nel browser', lines: ['✓ Authentication complete.', '- gh config set -h github.com git_protocol https', '✓ Configured git protocol', '✓ Logged in as ' + USER] }], apply: () => { S.env.gh = true; } },
  claudeinstall: { shell: 'ps', cmd: 'irm https://claude.ai/install.ps1 | iex', lines: ['Download di Claude Code...', 'Installazione in %USERPROFILE%\\.local\\bin', 'claude.exe installato'], apply: () => { S.env.claudeInstalled = true; } },
  claudelogin: { shell: 'cmd', cmd: '"' + P_CLAUDE + '"', lines: ['Welcome to Claude Code!', 'Select login method:', '  1. Claude account with subscription', '  2. Anthropic Console account (API usage billing)'],
    inter: [{ label: 'Scegli 1 e conferma nel browser', lines: ['Apertura del browser per il login...', '✓ Login successful.'] }], apply: () => { S.env.claudeLogin = true; }, immediate: true },
};
function prqCompute() {
  const e = S.env, ch = resolveChain(), ghOk = e.ghInstalled, G1 = 'Obbligatori', G2 = 'Opzionali', G3 = 'Provider AI (ne basta uno)';
  const akey = S.settings.anthropicapikey.trim() ? 'Tools > Options' : e.anthropicKey ? 'variabile ANTHROPIC_API_KEY' : null;
  const items = [
    { id: 'svn', g: G1, title: 'Client Subversion (svn.exe)', ok: e.svn, detail: e.svn ? P_SVN : 'svn.exe non trovato: serve per tutte le operazioni SVN.',
      instr: 'Si apre una finestra con:  winget install --id Slik.Subversion -e\nOppure installa TortoiseSVN e, nell\'installer, abilita "command line client tools".\nPoi premi Ricontrolla (se svn non viene trovato, riavvia Visual Studio).', p1: 'Installa con winget', p2: 'Pagina download' },
    { id: 'tortoisesvn', g: G2, title: 'TortoiseSVN (diff nativo)', ok: e.tortoise, detail: e.tortoise ? P_TSVN : 'Opzionale: senza, il diff si vede nel pannello inline.',
      instr: 'Scarica TortoiseSVN da https://tortoisesvn.net/downloads.html\nNell\'installer abilita anche "command line client tools".', p1: 'Pagina download' },
    { id: 'gh-cli', g: G3, title: 'GitHub CLI (gh)', ok: ghOk, detail: ghOk ? P_GH : 'gh.exe non trovato (per Copilot e GitHub Models).',
      instr: 'Si apre una finestra con:  winget install --id GitHub.cli -e\nPoi premi Ricontrolla. Se gh non viene trovato, riavvia Visual Studio (il PATH non si aggiorna da solo).', p1: ghOk ? null : 'Installa GitHub CLI' },
    { id: 'gh-login', g: G3, title: 'GitHub CLI: login', ok: ghOk && e.gh, detail: !ghOk ? 'Installa prima GitHub CLI.' : e.gh ? 'login effettuato' : 'non hai ancora fatto login.',
      instr: 'Si apre una finestra con:  gh auth login --hostname github.com --git-protocol https --web\nConferma il codice nel browser, poi premi Ricontrolla.', p1: ghOk && !e.gh ? 'Accedi a GitHub' : null },
    { id: 'claude-code', g: G3, title: 'Claude Code', ok: e.claudeInstalled, detail: !e.claudeInstalled ? 'claude.exe non trovato.' : `${P_CLAUDE} (il login non è verificabile: premi "Accedi a Claude Code" se non l'hai mai fatto)`,
      instr: 'Si apre una finestra PowerShell con:  irm https://claude.ai/install.ps1 | iex\nPoi premi Ricontrolla ed esegui "Accedi a Claude Code" (nella finestra che si apre, /login con il tuo abbonamento).', p1: e.claudeInstalled ? 'Accedi a Claude Code' : 'Installa Claude Code', p2: e.claudeInstalled ? null : 'Pagina documentazione' },
    { id: 'anthropic-key', g: G3, title: 'Anthropic API Key', ok: !!akey, detail: akey ? `chiave disponibile (${akey})` : 'nessuna chiave',
      instr: 'Incolla la chiave in Tools > Options > SVNAssist > Anthropic API Key,\noppure imposta la variabile d\'ambiente ANTHROPIC_API_KEY e riavvia Visual Studio.' },
    { id: 'copilot-runtime', g: G3, title: 'Runtime GitHub Copilot (~100 MB)', ok: e.copilotRuntime, detail: e.copilotRuntime ? 'scaricato' : 'si scarica da solo al primo uso con AI Provider = copilot.',
      instr: 'Premi "Scarica ora" oppure imposta AI Provider = copilot in Tools > Options > SVNAssist.\nRichiede anche il login a GitHub (gh auth login).', p1: e.copilotRuntime ? null : 'Scarica ora (~100 MB)' },
  ];
  const provider = (S.settings.aiprovider || '').trim().toLowerCase() || 'auto', usable = ch.chain.map(c => c.name);
  const problems = (e.svn ? 0 : 1) + (usable.length ? 0 : 1);
  return { items, provider, usable, svnMissing: !e.svn, summary: problems === 0 ? 'Prerequisiti ✓' : `Prerequisiti (${problems} da sistemare)`,
    ai: usable.length ? `AI Provider: ${provider}. Utilizzabili: ${usable.join(' → ')}` : `AI Provider: ${provider}. Nessun provider AI utilizzabile: configurane almeno uno.` };
}
const prqSnap = () => S.prq.snap || (S.prq.snap = prqCompute());
function prqRefresh() { S.prq.snap = prqCompute(); }
function prqInit() { if (S.prq.init) return; S.prq.init = true; prqRefresh(); if (S.prq.snap.svnMissing) S.ui.prereq = true; }   // si apre da sola se manca svn
const openUrl = url => toast('Apertura nel browser: ' + url, 3500);
// finestra console simulata: comando esatto; l'effetto si vede solo con "Ricontrolla"
async function consoleRun(key) {
  const p = CONSOLES[key], prompt_ = p.shell === 'ps' ? 'PS C:\\Users\\' + USER + '>' : 'C:\\Users\\' + USER + '>';
  let box, finished = false;
  const done = modal({
    title: (p.shell === 'ps' ? 'Windows PowerShell' : 'Prompt dei comandi'), cancel: true, banner: 'Finestra esterna simulata (la vera estensione apre cmd.exe / powershell.exe)',
    body: `<pre id="cs" style="margin:0;background:#0c0c0c;border:1px solid var(--bd);padding:8px;min-height:170px;font:11.5px/1.5 var(--mono);white-space:pre-wrap;word-break:break-all"></pre><div id="cst" style="margin-top:8px"></div>`,
    buttons: [{ label: 'Chiudi finestra', value: true, primary: true, disabled: true }], onOpen: el => { box = el; },
  });
  await sleep(80);
  const pre = box.querySelector('#cs'), live = () => box.isConnected;
  pre.textContent = prompt_ + ' ' + p.cmd + '\n'; logCmd('[console] ' + p.cmd); await sleep(500);
  const stream = async ls => { for (const l of ls) { if (!live()) return; pre.textContent += l + '\n'; await sleep(450); } };
  if (p.wg && !S.env.winget) { await stream(["'winget' non è riconosciuto come comando interno o esterno,", 'un programma eseguibile o un file batch.']); }
  else {
    await stream(p.lines);
    for (const st of (p.inter || [])) {
      if (!live()) return;
      const holder = box.querySelector('#cst'); holder.innerHTML = `<button class="btn primary" id="cstep">${esc(st.label)}</button>`;
      await new Promise(r => { const b = holder.querySelector('#cstep'); b.focus(); b.onclick = r; });
      holder.innerHTML = ''; await stream(st.lines);
    }
    if (!live()) return;
    finished = true;
  }
  pre.textContent += '\n' + prompt_ + ' _\n';
  const b = box.querySelector('[data-m="0"]'); b.disabled = false; b.focus();
  await done;
  if (finished) { if (p.immediate) p.apply(); else S.pending[key] = true; }
  render();
}
function prqLaunch(key, msg) { S.prq.feedback = msg; render(); return consoleRun(key); }
async function prqRun(arg) {
  const [id, w] = arg.split('|'), primary = w === '1', it = prqSnap().items.find(x => x.id === id), P = S.prq;
  if (P.busy || !it) return; P.busy = true;
  try {
    if (id === 'svn') { if (primary) await prqLaunch('svn', "Si è aperta una finestra con l'installazione di SVN: al termine premi Ricontrolla."); else openUrl('https://sliksvn.com/download/'); }
    else if (id === 'tortoisesvn') openUrl('https://tortoisesvn.net/downloads.html');
    else if (id === 'gh-cli') await prqLaunch('ghcli', "Si è aperta una finestra con l'installazione di GitHub CLI: al termine premi Ricontrolla (se gh non viene trovato, riavvia VS).");
    else if (id === 'gh-login') await prqLaunch('ghlogin', 'Si è aperta una finestra: conferma il codice nel browser, poi premi Ricontrolla.');
    else if (id === 'claude-code') {
      if (primary && it.p1.startsWith('Accedi')) await prqLaunch('claudelogin', 'Si è aperta una finestra con Claude Code: se richiesto esegui /login, poi premi Ricontrolla.');
      else if (primary) await prqLaunch('claudeinstall', "Si è aperta una finestra PowerShell con l'installazione di Claude Code: al termine premi Ricontrolla.");
      else openUrl('https://docs.claude.com/en/docs/claude-code/overview');
    } else if (id === 'copilot-runtime') {
      P.feedback = 'Download del runtime Copilot in corso (~100 MB)...'; render();
      let box; const done = modal({ title: 'Runtime GitHub Copilot', body: '<div class="dl"><div class="pbar"><i id="cb"></i></div><div id="ct">Avvio...</div></div>', buttons: [{ label: 'Chiudi', value: true, primary: true, disabled: true }], onOpen: el => { box = el; } });
      await sleep(80);
      await downloadCopilot(d => { if (d) { box.querySelector('#cb').style.width = d.pct + '%'; box.querySelector('#ct').textContent = d.label; } });
      const b = box.querySelector('[data-m="0"]'); b.disabled = false; b.focus(); await done;
      P.feedback = 'Runtime Copilot scaricato.'; prqRefresh();
    }
  } catch (e) { P.feedback = 'Errore: ' + e.message; } finally { P.busy = false; }
  render();
}
async function prqRecheck() {
  const was = prqSnap().svnMissing;
  Object.keys(S.pending).forEach(k => CONSOLES[k].apply()); S.pending = {};
  prqRefresh();
  if (was && !S.prq.snap.svnMissing) { if (S.vm.wcPath) await vmRefresh(undefined, true); else await autoDetect(); }
  render();
}
function prereqOpen() { S.ui.statusOpen = true; S.ui.prereq = true; S.ui.view = 'dock'; prqInit(); render(); const d = $('.prq'); if (d) d.scrollIntoView({ block: 'nearest' }); }

// ---------- procedura guidata (specchio di WizardStore del backend) ----------
const WIZ_PROV = [
  { id: 'copilot', title: 'GitHub Copilot', cost: 'GitHub Copilot: serve un abbonamento Copilot (le richieste possono consumare "premium request"), GitHub CLI con login (gh auth login) e il runtime Copilot (~100 MB, scaricato al primo uso).' },
  { id: 'claudecode', title: 'Claude Code (abbonamento Claude)', cost: 'Claude Code: serve Claude Code installato e il login (/login) con un abbonamento Claude Pro/Max; usa la quota del tuo abbonamento.' },
  { id: 'claude', title: 'Claude API (API key, a consumo)', cost: 'Claude API: serve una API key Anthropic; l\'uso è a pagamento a consumo (Haiku costa meno di Sonnet, Sonnet meno di Opus).' },
  { id: 'openai', title: 'GitHub Models / OpenAI-compatibile', cost: 'OpenAI-compatibile: con GitHub Models (default) basta il login GitHub (gh auth login) o un token, gratis entro i limiti; con altri endpoint serve la loro API key (a pagamento, tranne Ollama in locale).' },
];
const WIZ_MODELS = {
  claude: [['claude-sonnet-5-5', 'Sonnet (consigliato, equilibrato)'], ['claude-haiku-5-5', 'Haiku (il più economico e veloce)'], ['claude-opus-5-5', 'Opus (il più potente, costoso)']],
  copilot: [['default', 'Predefinito di Copilot (consigliato)'], ['gpt-4.1', 'GPT-4.1'], ['gpt-5-mini', 'GPT-5 mini'], ['claude-sonnet-4.5', 'Claude Sonnet 4.5']],
  openai: [['openai/gpt-4o-mini', 'GPT-4o mini (consigliato, gratuito su GitHub Models)'], ['openai/gpt-4o', 'GPT-4o'], ['openai/gpt-4.1-mini', 'GPT-4.1 mini'], ['openai/gpt-4.1', 'GPT-4.1']],
};
WIZ_MODELS.claudecode = WIZ_MODELS.claude;
const WIZ_MODEL_KEY = { copilot: 'copilotmodel', claude: 'claudemodel', claudecode: 'claudemodel', openai: 'aimodel' };
const WIZ_INSTALL = { copilot: ['gh-cli', 'copilot-runtime'], claudecode: ['claude-code'], claude: [], openai: [] };
const freshWiz = () => ({ open: false, mode: 'ai', step: 1, from: false, provider: '', busy: false, model: '' });
const wizItem = i => ({ id: i.id, group: i.g, title: i.title, ok: !!i.ok, detail: i.detail, instructions: i.ok ? '' : (i.instr || ''), primary: i.ok ? '' : (i.p1 || ''), secondary: i.ok ? '' : (i.p2 || '') });
function wizCompute() {
  const W = S.wiz, P = prqCompute(), byId = id => P.items.find(i => i.id === id), e = S.env;
  if (W.mode === 'prereq') {
    const ai = P.usable.length > 0, items = [wizItem(byId('svn')), { id: 'ai', group: 'Provider AI', title: 'Provider AI per i commit message', ok: ai, detail: ai ? P.usable.join(' → ') : 'Nessun provider AI utilizzabile: serve per generare i messaggi di commit.', instructions: ai ? '' : 'La procedura guidata ti accompagna nella scelta e nell\'accesso.', primary: ai ? '' : 'Configura l\'AI', secondary: '' }];
    return { items, auth: '', authOk: false, authDetail: '', canNext: items.every(i => i.ok) };
  }
  const items = W.step === 2 ? (WIZ_INSTALL[W.provider] || []).map(id => wizItem(byId(id))) : [];
  const all = (WIZ_INSTALL[W.provider] || []).map(id => byId(id).ok);
  const ghModels = isGhModels(S.settings.aiendpoint);
  const authOk = { copilot: byId('gh-login').ok, claude: byId('anthropic-key').ok, openai: !!S.settings.aiapikey.trim() || (ghModels && e.gh), claudecode: false }[W.provider] || false;
  const authDetail = W.provider === 'claudecode' ? 'Il login a Claude Code non si può verificare da qui: se non l\'hai mai fatto, aprilo e digita /login.'
    : W.provider === 'copilot' ? byId('gh-login').detail : W.provider === 'claude' ? byId('anthropic-key').detail : (S.settings.aiapikey.trim() ? 'chiave in Tools > Options' : authOk ? 'token GitHub rilevato (GitHub Models)' : 'nessuna chiave né token GitHub.');
  const canNext = !W.busy && ({ 1: !!W.provider, 2: all.every(Boolean), 3: authOk || W.provider === 'claudecode', 4: !!W.model }[W.step]);
  return { items, auth: { copilot: 'github', claudecode: 'claudecode', claude: 'key', openai: 'openai' }[W.provider] || '', authOk, authDetail, canNext };
}
function wizardSnap() {
  const W = S.wiz, c = W.open ? wizCompute() : { items: [], auth: '', authOk: false, authDetail: '', canNext: false };
  return { open: W.open, mode: W.mode, step: W.step, fromGenerate: W.from, provider: W.provider, busy: W.busy, feedback: W.open ? (S.prq.feedback || '') : '',
    providers: WIZ_PROV.map(p => ({ ...p })), items: c.items, auth: c.auth, authOk: c.authOk, authDetail: c.authDetail, endpoint: S.settings.aiendpoint,
    models: W.step === 4 && W.mode === 'ai' ? (WIZ_MODELS[W.provider] || []).map(([value, label]) => ({ value, label })) : [], model: W.model, canNext: !!c.canNext };
}
function wizOpen(from) { const p = (S.settings.aiprovider || '').toLowerCase(); S.wiz = Object.assign(freshWiz(), { open: true, from: !!from, provider: WIZ_INSTALL[p] ? p : '' }); S.prq.feedback = ''; render(); }
function wizOpenPrereq() { S.wiz = Object.assign(freshWiz(), { open: true, mode: 'prereq' }); S.prq.feedback = ''; render(); }
function wizApplyPending() { Object.keys(S.pending).forEach(k => CONSOLES[k].apply()); S.pending = {}; prqRefresh(); }
const WIZ = {
  wizardOpen: () => wizOpen(false), wizardOpenPrereq: () => wizOpenPrereq(),
  wizardCancel: () => { S.wiz.open = false; render(); },
  wizardPick: a => { if (!WIZ_INSTALL[a.provider]) throw new Error('Provider sconosciuto: ' + a.provider); if (S.wiz.step === 1) S.wiz.provider = a.provider; render(); },
  wizardBack: () => { if (S.wiz.step > 1) S.wiz.step--; render(); },
  wizardNext: () => {
    const W = S.wiz; if (W.step === 2 || W.step === 3) wizApplyPending();
    if (!wizCompute().canNext || W.step >= 4) return render();
    W.step++; if (W.step === 4) W.model = WIZ_MODELS[W.provider][0][0]; render();
  },
  wizardRecheck: () => { wizApplyPending(); render(); },
  wizardRun: async a => {
    const W = S.wiz, it = wizCompute().items.find(x => x.id === a.id); if (!it || W.busy) return;
    if (a.id === 'ai') { wizOpen(false); return; }
    W.busy = true; render();
    try { await prqRun(a.id + '|' + (a.primary ? 1 : 2)); wizApplyPending(); } finally { W.busy = false; render(); }
  },
  wizardLogin: async () => {
    const W = S.wiz; W.busy = true; render();
    try {
      if (W.provider === 'claudecode') await prqLaunch('claudelogin', 'Si è aperto un terminale con Claude Code: se richiesto esegui /login, poi torna qui.');
      else if (S.env.ghInstalled) await prqLaunch('ghlogin', 'Si è aperto un terminale: conferma il codice nel browser, poi torna qui.');
      else await prqLaunch('ghcli', 'Si è aperto un terminale con l\'installazione di GitHub CLI: al termine premi di nuovo Accedi (se gh non viene trovato, riavvia VS).');
    } finally { W.busy = false; render(); }
  },
  wizardSetAuth: a => {
    const W = S.wiz, ep = String(a.endpoint || '').trim(), key = String(a.key || '').trim();
    if (W.provider === 'openai' && ep) {
      let ok = false; try { const u = new URL(ep); ok = u.protocol === 'https:' || (u.protocol === 'http:' && ['localhost', '127.0.0.1', '[::1]'].includes(u.hostname)); } catch (e) { ok = false; }
      if (!ok) throw new Error('L\'endpoint deve essere un URL https (o http://localhost).');
      S.settings.aiendpoint = ep;
    }
    if (key && W.provider === 'claude') S.settings.anthropicapikey = key;
    if (key && W.provider === 'openai') S.settings.aiapikey = key;
    prqRefresh(); render();
  },
  wizardFinish: async a => {
    const W = S.wiz; if (W.step !== 4) throw new Error('La procedura non è arrivata al passo del modello.');
    if (!WIZ_MODELS[W.provider].some(m => m[0] === a.model)) throw new Error('Modello non valido: ' + a.model);
    S.settings.aiprovider = W.provider; S.settings[WIZ_MODEL_KEY[W.provider]] = a.model; S.f.optionsSaved = true;
    const from = W.from; W.open = false; prqRefresh(); render();
    if (from) await vmAi('gen');
  },
  openOptions: () => { S.wiz.open = false; render(); return optionsDlg(); },
};

// =====================================================================
//  ELENCO FILE STILE "GIT CHANGES" (come ChangeRowFactory / ChangesTreeBuilder)
//  Lista APPIATTITA di righe (sezione / cartella / file), non un TreeView annidato.
// =====================================================================
const SEC_NAMES = { staged: 'Staged', changes: 'Modifiche', unversioned: 'Non versionati' };
const STNAME = { M: 'Modificato', A: 'Aggiunto', D: 'Eliminato', C: 'Conflitto', '?': 'Non versionato', '!': 'Mancante', R: 'Sostituito', N: 'Normale' };
const secOf = f => f.sel ? 'staged' : f.st === '?' ? 'unversioned' : 'changes';   // sel = "staged"
const secItems = sec => S.vm.files.filter(f => secOf(f) === sec);
const under = (items, key) => key ? items.filter(f => f.path === key || f.path.startsWith(key + '\\')) : items;
const byLabel = (a, b) => a.label.localeCompare(b.label, 'it', { sensitivity: 'base' });
const fname = f => base(f.path) + (f.dir ? '\\' : '');
function buildTree(items) {
  const root = { label: '', path: '', dirs: [], files: [] };
  for (const it of items) {
    const segs = it.path.split('\\'); let n = root;
    for (let i = 0; i < segs.length - 1; i++) {
      let d = n.dirs.find(x => x.label.toLowerCase() === segs[i].toLowerCase());
      if (!d) { d = { label: segs[i], path: n.path ? n.path + '\\' + segs[i] : segs[i], dirs: [], files: [] }; n.dirs.push(d); }
      n = d;
    }
    n.files.push(it);
  }
  return root;
}
function compactNode(d) { let cur = d, label = d.label; while (cur.files.length === 0 && cur.dirs.length === 1) { cur = cur.dirs[0]; label += '\\' + cur.label; } return { cur, label }; }
// risolve la chiave di riga del protocollo (docs/ui-protocol.md): file | folder | section
function getRow(key) {
  const i = key.indexOf(':'), sec = key.slice(0, i), p = key.slice(i + 1).replace(/\//g, '\\');
  if (sec === 'section') return { kind: 'section', section: p, key: '', entries: secItems(p), name: SEC_NAMES[p] };
  const all = secItems(sec), f = all.find(x => x.path === p);
  if (f) return { kind: 'file', entries: [f], file: f, section: sec };
  return { kind: 'folder', section: sec, key: p, entries: under(all, p), name: p };
}
const confirmBox = text => modal({ title: 'SVNAssist', icon: '&#10067;', cancel: false, body: `<p>${esc(text)}</p>`, buttons: [{ label: 'OK', value: true, primary: true }, { label: 'Annulla', value: false }] });
const listForConfirm = es => es.slice(0, 8).map(e => `• ${e.path} (${STNAME[e.st]})`).join('\n') + (es.length > 8 ? `\n… e altri ${es.length - 8}` : '');
const IS_REV = ['M', 'A', 'D', 'C', '!', 'R'];
async function rowOp(op0, key) {
  const op = { add: 'add', deleteFile: 'del', revert: 'rev', resolve: 'res', ignore: 'ign', lock: 'lock', unlock: 'unlock' }[op0];
  const vm = S.vm, row = getRow(key); if (!row || !op) return;
  const done = async msg => { await vmRefresh(msg); };
  try {
    if (op === 'add') {
      const aff = row.entries.filter(f => f.st === '?');
      if (!aff.length) { setStatus('Nessun elemento non versionato da aggiungere.'); return render(); }
      setStatus('Aggiunta a SVN in corso...'); render();
      await svn(`add --force ${row.kind === 'folder' && row.section === 'unversioned' ? '"' + fwd(row.key) + '"' : '--targets (' + aff.length + ' elementi)'}`, 500);
      aff.forEach(f => svnAdd(f.path)); aff.forEach(f => { f.sel = true; });
      await done(`Aggiunti a SVN: ${aff.length} elementi.`);
    } else if (op === 'del') {   // Canc su un file: elimina dal filesystem (con conferma) come Git Changes
      const f = row.file; if (!f || f.dir) { setStatus('Seleziona un file da eliminare.'); return render(); }
      let msg = f.st === '?' ? `Eliminare "${f.path}" dal disco?\n\nIl file non è versionato: l'operazione non è annullabile.` : `Eliminare "${f.path}"?\n\nIl file verrà eliminato dalla cartella di lavoro e rimosso da SVN al prossimo commit.`;
      const force = ['M', 'R', 'C', 'A'].includes(f.st);
      if (force) msg += '\n\nATTENZIONE: il file ha modifiche locali non committate, che andranno perse.';
      if (!await confirmBox(msg)) return;
      setStatus('Eliminazione in corso...'); render();
      if (f.st === '?') { await svn(`(rimozione dal disco) "${fwd(f.path)}"`, 300); S.files = S.files.filter(x => x.path !== f.path); }
      else if (f.st === '!') { await svn(`delete "${fwd(f.path)}"`, 400); svnDelete(f.path); }
      else { await svn(force ? `delete --force "${fwd(f.path)}"` : `delete "${fwd(f.path)}"`, 500); svnDelete(f.path); }
      await done(`Eliminato: ${f.path}.`);
    } else if (op === 'lock') {
      const f = row.file; if (!f || f.dir || f.locked || f.st !== 'M') { setStatus('Seleziona un file modificato non ancora bloccato.'); return render(); }
      setStatus('Lock in corso...'); render();
      await svn(`lock "${fwd(f.path)}"`, 400); LOCKS.add(f.path);
      await done(`Bloccato (lock): ${f.path}`);
    } else if (op === 'unlock') {
      const f = row.file; if (!f || !f.locked) { setStatus('Seleziona un file bloccato da te.'); return render(); }
      setStatus('Unlock in corso...'); render();
      await svn(`unlock "${fwd(f.path)}"`, 400); LOCKS.delete(f.path);
      await done(`Sbloccato (unlock) completato: ${f.path}`);
    } else if (op === 'rev') {
      const t = row.entries.filter(f => IS_REV.includes(f.st));
      if (!t.length) { setStatus('Nessun elemento idoneo per SVN Revert.'); return render(); }
      const scope = row.kind === 'folder' ? ` nella cartella "${row.key}"` : row.kind === 'section' ? ` (${SEC_NAMES[row.section]})` : '';
      if (!await confirmBox(`Annullare le modifiche locali su ${t.length} elementi${scope}?\n\n${listForConfirm(t)}\n\nLe modifiche andranno perse e l'operazione non è annullabile.`)) return;
      setStatus('Revert in corso...'); render();
      await svn(`revert --depth infinity --targets (${t.length} elementi)`, 500); t.forEach(f => svnRevert(f.path));
      await done(`Revert completato su ${t.length} elementi.`);
    } else if (op === 'res') {
      const f = row.file; if (!f || f.st !== 'C') { setStatus('Seleziona un file in conflitto da risolvere.'); return render(); }
      const pick = await modal({
        title: 'SVNAssist', icon: '&#10067;', cancel: null,
        body: `<p>Come vuoi risolvere il conflitto su "${esc(base(f.path))}"?\n\n• Ho risolto a mano: tieni il file com'è (sistemato a mano, senza marcatori &lt;&lt;&lt;&lt;&lt;&lt;&lt;)\n• Tieni il mio file: scarta le modifiche del server, vale il tuo file intero\n• Prendi quello del server: scarta le TUE modifiche locali\n• Torna alla base: scarta le modifiche di entrambi</p>`,
        buttons: [{ label: 'Ho risolto a mano', value: 'working', primary: true }, { label: 'Tieni il mio file', value: 'mine-full' }, { label: 'Prendi quello del server', value: 'theirs-full' }, { label: 'Torna alla base', value: 'base' }, { label: 'Annulla', value: null }],
      });
      if (!pick) return;
      setStatus('Risoluzione conflitto in corso...'); render();
      await svn(`resolve --accept ${pick} "${fwd(f.path)}"`, 400); svnResolve(f.path, pick);
      await done(`Conflitto risolto: ${f.path}`);
    } else if (op === 'ign') {
      const t = row.entries.filter(f => f.st === '?');
      if (!t.length) { setStatus('Add to Ignore List è disponibile solo per elementi non versionati.'); return render(); }
      if (row.kind !== 'file' && !await confirmBox(`Aggiungere ${t.length} elementi non versionati alla ignore list (svn:ignore) delle rispettive cartelle?\n\n${listForConfirm(t)}`)) return;
      setStatus('Aggiornamento ignore list in corso...'); render();
      await svn('propset svn:ignore', 400); t.forEach(f => { if (fileBy(f.path)) svnIgnore(f.path); });
      await done(`Aggiunti alla ignore list: ${t.length} elementi.`);
    }
  } catch (e) {
    const lab = { add: 'add', del: 'delete', rev: 'revert', res: 'resolve', ign: 'ignore list' }[op];
    setStatus(`Errore ${lab}: ${e.message}`); render();
  }
}

// =====================================================================
//  MENU "SVN" nel tasto destro di Esplora soluzioni (SolutionExplorerMenu / SvnContextCommands)
//  I comandi rispondono con message box (ShowPromptAsync), come la vera estensione.
// =====================================================================
const wcUnder = path => S.files.filter(f => !path || f.path === path || f.path.startsWith(path + '\\'));
const EN_ST = { M: 'Modified', A: 'Added', D: 'Deleted', C: 'Conflicted', '?': 'Unversioned', '!': 'Missing', R: 'Replaced' };
const CONTAINER_EXT = ['.sln', '.slnx', '.csproj', '.vbproj', '.fsproj', '.vcxproj', '.proj', '.shproj', '.wapproj'];
const showText = text => modal({ title: 'SVNAssist', wide: true, cancel: true, body: `<pre class="cmd" style="max-height:55vh;overflow:auto;white-space:pre">${esc(text)}</pre>` });
// equivalente di SvnTarget.Resolve: .sln/.csproj -> cartella che li contiene
function seTarget(type, path) {
  let p = path, isDir = true;
  if (type === 'file') { const m = path.match(/\.[^.\\]+$/); if (m && CONTAINER_EXT.includes(m[0].toLowerCase())) p = parentDir(path) === '.' ? '' : parentDir(path); else isDir = false; }
  const full = p ? WC + '\\' + p : WC, pd = parentDir(p);
  return { path: p, full, isDir, inWc: !!(S.solution && S.solution.inWc), workDir: isDir ? full : (pd === '.' || !p ? WC : WC + '\\' + pd), workRel: isDir ? p : (pd === '.' ? '' : pd) };
}
const ERR_TITLE = { update: "Errore durante l'update", commit: "Errore nell'apertura di SVN Changes", diff: 'Errore durante il diff', log: "Errore nell'apertura del log", add: "Errore durante l'add", revert: 'Errore durante il revert', ign: "Errore durante l'aggiunta alla ignore list" };
async function seOp(kind, arg) {
  const i = arg.indexOf('|'), type = arg.slice(0, i), path = arg.slice(i + 1);
  if (!(await ensureSvn()) && !S.env.svn) return;
  if (!S.solution) return;
  const t = seTarget(type, path);
  if (!t.inWc) { await prompt(`Non è una working copy SVN:\n${t.full}`); return; }
  let refresh = false;
  try {
    if (kind === 'update') {
      await svn(`update "${fwd(t.workRel || '.')}"`, 900); svnUpdate();
      const conf = S.files.filter(f => f.st === 'C').map(f => f.path);
      let summary = `Aggiornato alla revisione ${S.headRev}`;
      if (conf.length) summary += `\n\n${conf.length} elementi in conflitto:\n${conf.slice(0, 15).join('\n')}${conf.length > 15 ? `\n... e altri ${conf.length - 15}` : ''}`;
      await prompt(`${summary}\n\n${t.workDir}${t.isDir ? '' : '\n(aggiornata la cartella che contiene il file)'}`);
      refresh = true;
    } else if (kind === 'commit') {
      await openStatus();
    } else if (kind === 'diff') {
      if (!t.isDir && S.env.tortoise) { toast('Diff aperto in TortoiseSVN: ' + t.full); logCmd('TortoiseProc.exe /command:diff /path:"' + t.full + '"'); return; }
      await svn(`diff "${fwd(t.path || '.')}"`, 400);
      let diff = t.isDir ? wcUnder(t.path).filter(f => !f.dir && ['M', 'A', 'D', 'R', 'C'].includes(f.st)).map(f => fileDiffText(f.path)).join('\n') : fileDiffText(t.path);
      if (!diff.trim()) { await prompt(`Nessuna modifica rispetto a BASE:\n${t.full}`); return; }
      if (diff.length > 4000) diff = diff.slice(0, 4000) + '\n... (diff troncato: usa SVN Changes o TortoiseSVN per vederlo per intero)';
      await showText(diff);
    } else if (kind === 'log') {
      await openLog();
    } else if (kind === 'add') {
      const us = wcUnder(t.path).filter(f => f.st === '?');
      await svn(`add --force "${fwd(t.path || '.')}"`, 500);
      if (!us.length) throw new Error(`svn: warning: W150002: '${t.full}' is already under version control`);
      us.forEach(f => svnAdd(f.path));
      await prompt(`Aggiunto a SVN:\n${t.full}`); refresh = true;
    } else if (kind === 'revert') {
      let q;
      if (t.isDir) {
        const ch = wcUnder(t.path).filter(f => f.st !== 'N'), pd = propDirs().filter(d => !t.path || d === t.path || d.startsWith(t.path + '\\'));
        const names = ch.map(f => `${EN_ST[f.st]}  ${f.path}`).concat(pd.map(d => `Modified  ${d}`));
        if (!names.length) { await prompt(`Nessuna modifica da annullare in:\n${t.full}`); return; }
        q = `Annullare le modifiche locali di ${names.length} elementi in\n${t.full}\n\n${names.slice(0, 15).join('\n')}${names.length > 15 ? `\n... e altri ${names.length - 15}` : ''}`;
      } else q = `Annullare le modifiche locali di:\n${t.full}`;
      if (!await confirmBox(q + '\n\nL\'operazione non è reversibile.')) return;
      await svn(`revert --depth infinity "${fwd(t.path || '.')}"`, 500);
      wcUnder(t.path).filter(f => ['M', 'A', 'D', 'C', '!', 'R'].includes(f.st)).forEach(f => svnRevert(f.path));
      propDirs().filter(d => !t.path || d === t.path || d.startsWith(t.path + '\\')).forEach(d => svnRevert(d));
      await prompt(`Revert completato:\n${t.full}`); refresh = true;
    } else if (kind === 'ign') {
      if (!t.path) throw new Error("svn: E155007: la radice della working copy non si può ignorare (serve la cartella padre)");
      await svn('propset svn:ignore', 400); svnIgnore(t.path);
      await prompt(`Aggiunto a svn:ignore:\n${t.full}\n\nLa modifica alla proprietà va committata sulla cartella padre.`); refresh = true;
    }
  } catch (e) { await prompt(`${ERR_TITLE[kind]}:\n${e.message}`); }
  render(); if (refresh && S.ui.statusOpen && S.vm.wcPath) await vmRefresh(undefined, true);
}

// =====================================================================
//  DIALOGHI E FLUSSI
// =====================================================================
async function vsixFlow() {
  if (S.vsInstalled) return toast('SVNAssist è già installato');
  const go = await modal({
    title: 'VSIX Installer', body: `<div class="vsix"><div class="row"><div class="logo">SVN</div><div><b>SVNAssist</b><div class="muted">Estensione per Visual Studio 2026 · Subversion + commit message con AI</div></div></div>
      <table style="margin-top:12px;font-size:12.5px;border-collapse:collapse"><tr><td class="muted" style="padding:2px 14px 2px 0">Editore</td><td>Andrea Pinelli</td></tr><tr><td class="muted" style="padding:2px 14px 2px 0">Versione</td><td>0.2.0</td></tr><tr><td class="muted" style="padding:2px 14px 2px 0">Firma digitale</td><td>nessuna</td></tr></table>
      <p class="small muted" style="margin-top:12px">Prodotti di destinazione: Visual Studio Community / Professional / Enterprise 2026. Estensione non firmata: Windows può mostrare un avviso. Dopo l'installazione sarà necessario riavviare Visual Studio.</p></div>`,
    buttons: [{ label: 'Installa', value: true, primary: true }, { label: 'Annulla', value: false }], cancel: false,
  });
  if (!go) return;
  let box; const p = modal({ title: 'VSIX Installer', body: '<div class="vsix"><p id="vx">Installazione in corso...</p><div class="pbar"><i id="vb" style="width:0"></i></div></div>', buttons: [{ label: 'Chiudi', value: true, primary: true, disabled: true }], onOpen: el => box = el });
  await sleep(80);
  for (let i = 1; i <= 10; i++) { box.querySelector('#vb').style.width = i * 10 + '%'; await sleep(160); }
  box.querySelector('#vx').textContent = 'Installazione completata. Riavvia Visual Studio per usare SVNAssist.';
  const b = box.querySelector('[data-m="0"]'); b.textContent = 'Riavvia Visual Studio'; b.disabled = false; b.focus();
  await p;
  S.vsInstalled = true; restartVS();
}
async function restartVS() {
  S.restarting = true; render(); await sleep(1400); S.restarting = false;
  S.sess.svnPrompted = false; S.sess.aiPrompted = false;
  S.ui.statusOpen = false; S.ui.menu = null; S.vm = freshVM(); S.lvm = freshLVM(); S.prq = { snap: null, feedback: '', init: false, busy: false }; S.watch.pending = false;
  render();
}
async function openSolDlg() {
  S.ui.menu = null;
  if (S.solution) return toast('Una soluzione è già aperta');
  const r = await modal({
    title: 'Apri progetto/soluzione', body: `<p class="muted small">Scegli una soluzione:</p>
      <label class="tg"><input type="radio" name="sol" value="a" checked><span><b>Acme.Shop.sln</b> <span class="tag ok">working copy SVN</span><span class="hint">${esc(WC)}</span></span></label>
      <label class="tg"><input type="radio" name="sol" value="b"><span><b>Appunti.sln</b> <span class="tag">non versionata</span><span class="hint">C:\\temp\\Appunti</span></span></label>`,
    buttons: [{ label: 'Apri', value: 'ok', primary: true, onClick: (el, close) => { close(el.querySelector('input:checked').value); return false; } }, { label: 'Annulla', value: null }], cancel: null,
  });
  if (!r) return;
  S.solution = r === 'a' ? { name: 'Acme.Shop', dir: WC, inWc: true } : { name: 'Appunti', dir: 'C:\\temp\\Appunti', inWc: false };
  render();
  if (S.ui.statusOpen) { toast('Cambio solution rilevato (polling ogni 5 s)'); await sleep(1200); await autoDetect(); }
}
async function closeSol() { S.ui.menu = null; S.solution = null; render(); if (S.ui.statusOpen) { await sleep(800); await autoDetect(); } }

const SETDEF = [
  ['aiprovider', 'AI Provider (auto / copilot / claudecode / claude / openai) - copilot scarica ~100 MB al primo uso'],
  ['claudemodel', 'Claude Model (Claude Code e Claude API: claude-sonnet-5-5 consigliato, claude-haiku-5-5, claude-opus-5-5)'],
  ['anthropicapikey', 'Anthropic API Key (provider Claude API; vuota = ANTHROPIC_API_KEY)'],
  ['copilotmodel', 'GitHub Copilot Model (vuoto = predefinito di Copilot)'],
  ['aiendpoint', 'AI Endpoint URL (provider openai; default: GitHub Models)'],
  ['aiapikey', 'AI API Key (provider openai: GitHub PAT per GitHub Models, oppure API key OpenAI)'],
  ['aimodel', 'AI Model (provider openai, es. openai/gpt-4o-mini)'],
  ['defaultlanguage', 'Default Language (auto / italiano / english)'],
  ['workingcopypath', 'Working Copy Path'],
];
const PROV_HINT = { auto: 'Ordine: Copilot (se runtime già scaricato) → Claude Code → Claude API → OpenAI-compatibile. Se uno fallisce si passa al successivo.', copilot: "Serve 'gh auth login'. Al primo uso scarica il runtime (~100 MB, verifica SHA-256).", claudecode: 'Serve Claude Code installato e con login (esegue "claude -p").', claude: 'Serve una Anthropic API Key (qui sotto) oppure la variabile ANTHROPIC_API_KEY.', openai: "Serve una AI API Key; per GitHub Models basta il token di 'gh auth login'." };
async function optionsDlg() {
  S.ui.menu = null;
  const st = S.settings;
  const LBL = { aiprovider: 'Provider AI per i commit message', claudemodel: 'Modello Claude (solo Claude Code e Claude API)', anthropicapikey: 'API key Anthropic (solo Claude API; vuota = variabile ANTHROPIC_API_KEY)', copilotmodel: "Modello Copilot (solo Copilot). Serve un abbonamento Copilot e 'gh auth login'", aiendpoint: 'URL endpoint (solo OpenAI-compatibile; default GitHub Models)', aiapikey: 'API key o GitHub PAT (solo OpenAI-compatibile)', aimodel: 'Modello (solo OpenAI-compatibile / GitHub Models)', defaultlanguage: 'Lingua dei commit message', workingcopypath: 'Working Copy Path' };
  const PROVS = [['auto', 'Automatico (consigliato)'], ['copilot', 'GitHub Copilot (abbonamento)'], ['claudecode', 'Claude Code (abbonamento Claude)'], ['claude', 'Claude API (API key Anthropic)'], ['openai', 'OpenAI-compatibile / GitHub Models']];
  const LANGS = [['auto', 'Automatica'], ['italiano', 'Italiano'], ['english', 'English']];
  const opts = (list, cur) => list.map(([v, t]) => `<option value="${v}" ${String(cur).toLowerCase() === v ? 'selected' : ''}>${t}</option>`).join('');
  const rows = SETDEF.map(([k]) => `<div class="opt-row"><label for="opt-${k}">${esc(LBL[k])}</label>${k === 'aiprovider' ? `<select id="opt-${k}">${opts(PROVS, st[k])}</select>` : k === 'defaultlanguage' ? `<select id="opt-${k}">${opts(LANGS, st[k])}</select>` : `<input type="text" id="opt-${k}" value="${esc(st[k])}" autocomplete="off">`}</div>`).join('');
  const r = await modal({
    title: 'Opzioni', wide: true, cancel: false,
    body: `<div class="opt-grid"><div class="opt-tree"><div>&#9656; Ambiente</div><div>&#9656; Progetti e soluzioni</div><div>&#9656; Editor di testo</div><div class="on">&#9662; SVNAssist</div></div><div class="opt-form">${rows}<div class="small muted">Le chiavi inserite qui sono solo di prova: la demo non le invia a nessun servizio.</div></div></div>`,
    buttons: [{ label: 'OK', value: true, primary: true, onClick: el => { SETDEF.forEach(([k]) => { st[k] = el.querySelector('#opt-' + k).value; }); S.f.optionsSaved = true; prqRefresh(); } }, { label: 'Annulla', value: false }],
  });
  if (r) { toast('Impostazioni salvate'); render(); }
}

async function vmAi(kind) { if (S.vm.aiBusy) return; await aiCore(kind === 'improve' ? 'improve' : 'gen', vmAiUi()); }

// mini wizard riusabile dalle anteprime "Prossimamente"
function wizard(title, steps) {
  let i = 0, elRef;
  const draw = () => {
    const s = steps[i]; elRef.querySelector('.m-body > div').innerHTML = s.body;
    const nb = elRef.querySelector('[data-m="1"]'); nb.textContent = s.next || (i === steps.length - 1 ? 'Fine' : 'Avanti'); nb.disabled = false;
    if (s.onEnter) s.onEnter(elRef, nb);
  };
  return modal({
    title: title + ' (anteprima)', wide: true, banner: 'ANTEPRIMA — funzione NON ancora presente nell\'estensione', cancel: false, body: '',
    buttons: [{ label: 'Annulla', value: false }, { label: 'Avanti', value: true, primary: true, onClick: (el, close) => { if (i < steps.length - 1) { i++; draw(); return false; } } }],
    onOpen: el => { elRef = el; draw(); },
  });
}
const SOON = {
  switch: () => wizard('Switch branch', [
    { body: `<p>Scegli il branch a cui passare (<code>svn switch</code>). Branch attuale: <b>${esc(S.branch)}</b></p>${['trunk', 'branches/feature-coupon', 'branches/release-2.1', 'tags/v2.0'].map((b, k) => `<label class="tg"><input type="radio" name="br" value="${b}" ${k === 1 ? 'checked' : ''}><span>^/${b}</span></label>`).join('')}`, next: 'Switch' },
    { body: '<p id="swo" style="font-family:var(--mono);font-size:12px;white-space:pre-wrap"></p>', next: 'Fine', onEnter: async (el, nb) => { nb.disabled = true; const b = 'branches/…'; const o = el.querySelector('#swo'); for (const l of ['$ svn switch ^/' + b, 'U    Core/Services/OrderService.cs', 'U    Web/Controllers/CartController.cs', 'Updated to revision 126.']) { o.textContent += l + '\n'; await sleep(450); } nb.disabled = false; } },
  ]),
  merge: () => wizard('Merge con risoluzione conflitti', [
    { body: '<p>Esegui il merge di <b>branches/feature-coupon</b> in <b>trunk</b> (<code>svn merge</code>).</p><label class="tg"><input type="checkbox" checked><span>Esegui prima un dry-run</span></label>', next: 'Avvia merge' },
    { body: '<p id="mo" style="font-family:var(--mono);font-size:12px;white-space:pre-wrap"></p>', onEnter: async (el, nb) => { nb.disabled = true; const o = el.querySelector('#mo'); for (const l of ['--- Merging r126 through r131 into \'.\':', 'U    Core/Models/Coupon.cs', 'U    Core/Services/OrderService.cs', 'C    Web/Controllers/CheckoutController.cs', 'Summary of conflicts: Text conflicts: 1']) { o.textContent += l + '\n'; await sleep(420); } nb.disabled = false; nb.textContent = 'Risolvi il conflitto'; } },
    { body: `<p>Conflitto in <b>Web/Controllers/CheckoutController.cs</b>: scegli come risolvere.</p><div class="three"><div><h5>Mia (trunk)</h5><pre id="c1">var result = _gateway.Charge(request, retries: 3);\n_log.Info(...);</pre></div><div><h5>Loro (feature-coupon)</h5><pre id="c2">var result = _gateway.ChargeAsync(request)\n   .GetAwaiter().GetResult();</pre></div><div><h5>Risultato</h5><pre id="c3" contenteditable="true">var result = _gateway.ChargeAsync(request, retries: 3)\n   .GetAwaiter().GetResult();\n_log.Info(...);</pre></div></div><p style="margin-top:8px"><button class="btn sm" data-r="1">Usa mia</button> <button class="btn sm" data-r="2">Usa loro</button> <button class="btn sm" data-r="3">Unisci entrambe</button></p>`, next: 'Segna come risolto',
      onEnter: el => el.querySelectorAll('[data-r]').forEach(b => b.onclick = () => { el.querySelector('#c3').textContent = el.querySelector('#c' + (b.dataset.r === '3' ? '3' : b.dataset.r)).textContent; toast('Anteprima: risultato aggiornato'); }) },
    { body: '<p><b>Merge completato.</b> 1 conflitto risolto. Le modifiche sono nella working copy e compaiono in SVN Changes: ora puoi fare Commit.</p>' },
  ]),
  branchtag: () => wizard('Crea Branch / Tag', [
    { body: `<div class="opt-row"><label>Tipo</label><select id="bt"><option>Branch</option><option>Tag</option></select></div><div class="opt-row"><label>Nome</label><input type="text" id="bn" value="feature-wishlist"></div><div class="opt-row"><label>Messaggio</label><input type="text" value="Crea il branch per la wishlist"></div><div class="note" id="bc"></div>`, next: 'Crea',
      onEnter: el => { const u = () => el.querySelector('#bc').textContent = `svn copy ^/${S.branch} ^/${el.querySelector('#bt').value === 'Tag' ? 'tags' : 'branches'}/${el.querySelector('#bn').value} -m "..."`; el.addEventListener('input', u); u(); } },
    { body: '<p><b>Creato.</b> Vuoi passare subito al nuovo branch (switch)?</p><label class="tg"><input type="checkbox" checked><span>Switch automatico</span></label>' },
  ]),
  cleanup: () => wizard('Cleanup', [
    { body: '<p>Pulisci la working copy (<code>svn cleanup</code>):</p>' + ['Rimuovi i lock', 'Rimuovi file non versionati', 'Rimuovi file ignorati', 'Ripristina i file mancanti'].map((x, i) => `<label class="tg"><input type="checkbox" ${i === 0 ? 'checked' : ''}><span>${x}</span></label>`).join(''), next: 'Esegui' },
    { body: '<p id="co" style="font-family:var(--mono);font-size:12px;white-space:pre-wrap"></p>', onEnter: async el => { const o = el.querySelector('#co'); for (const l of ['$ svn cleanup', 'Rimossi 2 lock', 'Working copy pulita.']) { o.textContent += l + '\n'; await sleep(400); } } },
  ]),
  blame: () => {
    const ls = T.orderMod.split('\n'), bs = T.orderBase.split('\n'), au = ['lbianchi', 'mrossi', 'andre', 'gverdi'], msg = ['Import iniziale', 'Aggiunge log pagamenti', 'Refactoring InventoryService', 'Corregge IVA'];
    const rows = ls.map((l, i) => { const inb = bs.includes(l); const k = i % 4; return `<tr title="${inb ? 'r' + (118 + k * 2) + ' · ' + msg[k] : 'modifica locale non ancora committata'}"><td>${i + 1}</td><td>${inb ? 'r' + (118 + k * 2) : '—'}</td><td>${inb ? au[k] : USER + ' (locale)'}</td><td>${esc(l)}</td></tr>`; }).join('');
    return modal({ title: 'Blame: Core/Services/OrderService.cs (anteprima)', wide: true, banner: 'ANTEPRIMA — funzione NON ancora presente nell\'estensione', body: `<div style="overflow:auto;max-height:55vh"><table class="blame">${rows}</table></div><p class="small muted">Passa il mouse su una riga per vedere il messaggio della revisione.</p>` });
  },
  auto: () => modal({ title: 'Autocompletamento messaggio (anteprima)', wide: true, banner: 'ANTEPRIMA — funzione NON ancora presente nell\'estensione', body: `<p class="small muted">Scrivi <code>#</code> per i ticket, oppure inizia con "Fix", "Agg"… Tab o clic per accettare.</p><div style="position:relative"><textarea id="ac" style="min-height:110px" placeholder="Messaggio di commit"></textarea><div class="sugg" id="acs" hidden></div></div>`,
    onOpen: el => {
      const ta = el.querySelector('#ac'), box = el.querySelector('#acs');
      const issues = ['#1423 Coupon scaduti applicati per errore', '#1431 Checkout lento con carrello grande', '#1440 Aggiungere wishlist'];
      const recent = S.log.slice(0, 4).map(l => l.msg);
      const files = ['OrderService', 'CartController', 'CheckoutController', 'PaymentGateway'];
      let cur = [];
      const upd = () => {
        const v = ta.value, w = (v.split(/\s/).pop() || ''), lastLine = v.split('\n').pop();
        cur = [];
        if (w.startsWith('#')) cur = issues.filter(x => x.startsWith(w)).map(x => ({ label: x, rep: w, ins: x.split(' ')[0] + ' ' }));
        else if (w.length >= 2) cur = files.filter(x => x.toLowerCase().startsWith(w.toLowerCase())).map(x => ({ label: x + ' (file modificato)', rep: w, ins: x }));
        if (!cur.length && lastLine.length >= 3 && !v.includes('\n')) cur = recent.filter(x => x.toLowerCase().startsWith(lastLine.toLowerCase())).map(x => ({ label: x + '  (da cronologia)', rep: lastLine, ins: x }));
        box.hidden = !cur.length; box.style.top = '100%'; box.innerHTML = cur.map((c, i) => `<button class="${i === 0 ? 'on' : ''}" data-i="${i}">${esc(c.label)}</button>`).join('');
      };
      const acc = i => { const c = cur[i]; if (!c) return; ta.value = ta.value.slice(0, ta.value.length - c.rep.length) + c.ins; box.hidden = true; ta.focus(); };
      ta.addEventListener('input', upd);
      ta.addEventListener('keydown', e => { if (e.key === 'Tab' && !box.hidden) { e.preventDefault(); e.stopPropagation(); acc(0); } });
      box.addEventListener('click', e => { const b = e.target.closest('button'); if (b) acc(+b.dataset.i); });
    } }),
};
const SOON_CARDS = [
  ['switch', 'Switch branch', 'Passa da trunk a un branch o a un tag direttamente dall\'IDE (svn switch), con aggiornamento della working copy.', ['Elenco branch/tag del repository', 'Avviso se ci sono modifiche locali']],
  ['merge', 'Merge con risoluzione dei conflitti', 'Merge tra branch con riepilogo e un risolutore dei conflitti a tre pannelli (mia / loro / risultato).', ['Dry-run prima del merge', 'Risoluzione guidata di ogni conflitto']],
  ['branchtag', 'Creazione di Branch e Tag', 'Crea branch e tag (svn copy) con un modulo, con switch automatico opzionale.', ['Anteprima del comando', 'Messaggio di commit predefinito']],
  ['blame', 'Blame / Annotate', 'Chi ha scritto ogni riga e in quale revisione, con il messaggio al passaggio del mouse.', ['Vista affiancata all\'editor']],
  ['cleanup', 'Cleanup', 'Pulizia della working copy: lock, file non versionati e ignorati, file mancanti.', ['Opzioni selezionabili', 'Output del comando']],
  ['auto', 'Autocompletamento nel messaggio di commit', 'Suggerimenti mentre scrivi: ticket, nomi dei file modificati, messaggi recenti.', ['Tab per accettare']],
];
function renderSoon() {
  $('#view-soon').innerHTML = `<div class="soon-wrap"><h2>Prossimamente <span class="tag soon">NON ancora implementato</span></h2><p class="muted">Queste funzioni <b>non esistono ancora</b> nell'estensione: sono anteprime cliccabili per discutere cosa costruire dopo. Nulla qui modifica la working copy simulata.</p>
   <div class="soon-grid">${SOON_CARDS.map(([id, t, d, l]) => `<div class="card"><span class="tag soon">Non ancora nell'estensione</span><h3>${esc(t)}</h3><p>${esc(d)}</p><ul>${l.map(x => `<li>${esc(x)}</li>`).join('')}</ul><button class="btn" data-act="soon" data-arg="${id}">Apri anteprima</button></div>`).join('')}</div></div>`;
}

// =====================================================================
//  AZIONI (event delegation)
// =====================================================================
function ctxMenu(x, y, items, append) {
  const h = $('#ctxhost');
  const html = `<div class="ctx${append ? ' subm' : ''}" role="menu" style="left:${Math.max(0, Math.min(x, innerWidth - 210))}px;top:${Math.max(0, Math.min(y, innerHeight - items.length * 28))}px;z-index:400">${items.map(i => i === '-' ? '<hr>' : `<button role="menuitem" ${i.off ? 'disabled' : ''} data-act="${i.act}" data-arg="${esc(i.arg || '')}" data-arg2="${esc(i.arg2 || '')}">${esc(i.label)}</button>`).join('')}</div>`;
  if (append) { h.querySelectorAll('.subm').forEach(e => e.remove()); h.insertAdjacentHTML('beforeend', html); } else h.innerHTML = html;
  const f = h.querySelector('.ctx:last-child button:not(:disabled)'); if (f) f.focus();
}
const closeCtx = () => { $('#ctxhost').innerHTML = ''; };
function runCmd(c) {
  S.ui.menu = null; S.ui.sub = false;
  if (c === 'status' || c === 'commit') return openStatus();
  if (c === 'log') return openLog();
  if (c === 'update') return menuUpdate();
}
async function menuUpdate() {
  render();
  if (!await ensureSvn()) return;
  if (!S.solution) return prompt('Nessuna solution aperta. Apri una solution in una working copy SVN.');
  try {
    if (!S.solution.inWc) { await prompt(`La solution non si trova in una working copy SVN:\n${S.solution.dir}`); return; }
    await svn('update', 900); const out = svnUpdate(); toast('svn update (output demo): ' + out.join(' | '), 5000);
    const n = S.files.filter(f => f.st === 'C').length;
    await prompt(`Aggiornato alla revisione ${S.headRev}${n ? ` (${n} file in conflitto)` : ''}:\n${WC}`);
    if (S.ui.statusOpen && S.vm.wcPath) await vmRefresh(undefined, true);
  } catch (e) { await prompt(`Errore durante l'update:\n${e.message}`); }
  render();
}
function langBtn() { const b = $('#btnLang'); if (b) b.textContent = __mockBridge.getLang() === 'it' ? 'Lingua: IT' : 'Language: EN'; }
const ACT = {
  tab: a => { $$('.tabs button').forEach(b => b.setAttribute('aria-selected', b.dataset.arg === a)); $('#view-sim').hidden = a !== 'sim'; $('#view-soon').hidden = a !== 'soon'; try { localStorage.setItem('svnassist-demo-tab', a); } catch (e) { } if (a === 'soon') renderSoon(); },
  togglePanel: () => { S.ui.panelOpen = !S.ui.panelOpen; render(); },
  reset: () => { startMode('free', true); },
  lang: () => { __mockBridge.setLang(__mockBridge.getLang() === 'it' ? 'en' : 'it'); langBtn(); },
  tourStart: () => startMode('first'),
  tourNext: () => { const T_ = S.tour; if (T_.step >= STEPS.length - 1) T_.finished = true; else T_.step++; render(); },
  tourPrev: () => { if (S.tour.step > 0) S.tour.step--; render(); },
  tourSkip: () => { S.tour.on = false; render(); },
  tourDo: () => STEPS[S.tour.step].doIt(),
  tourRetrySvn: () => { S.env.svn = false; S.sess.svnPrompted = false; S.f.svnPromptDone = false; S.f.svnPromptChoice = null; S.ui.statusOpen = false; S.vm = freshVM(); S.prq = { snap: null, feedback: '', init: false, busy: false }; render(); toast('Riapri SVN Changes e scegli OK'); },
  menu: a => { S.ui.menu = S.ui.menu === a ? null : a; S.ui.sub = false; if (!['file', 'tools', 'ext'].includes(a)) { S.ui.menu = null; toast('Menu non simulato: qui contano solo File, Strumenti ed Estensioni'); } render(); },
  subToggle: () => { S.ui.sub = !S.ui.sub; render(); },
  cmd: a => runCmd(a),
  vsixOpen: () => { S.ui.menu = null; render(); vsixFlow(); },
  openSolDlg: () => openSolDlg(), closeSol: () => closeSol(), optionsOpen: () => optionsDlg(),
  openFile: a => { if (!S.ui.open.includes(a)) S.ui.open.push(a); S.ui.active = a; S.ui.selFile = a; S.ui.view = 'doc'; S.ui.seOpen = false; render(); },
  nvSe: () => { S.ui.seOpen = !S.ui.seOpen; render(); }, nvView: a => { S.ui.view = a; S.ui.seOpen = false; render(); },
  prereqToggle: () => { S.ui.prereq = !S.ui.prereq; if (S.ui.prereq) prqRefresh(); render(); }, prereqOpen: () => prereqOpen(), prqRecheck: () => prqRecheck(), prqRun: a => prqRun(a),
  tourMin: () => { S.tour.min = !S.tour.min; render(); },
  activate: a => { S.ui.active = a; render(); },
  closeTab: a => { S.ui.open = S.ui.open.filter(x => x !== a); S.ui.diffs = S.ui.diffs.filter(d => d.id !== a); if (a === '__log') S.ui.logOpen = false; if (S.ui.active === a) S.ui.active = S.ui.open[0] || (S.ui.diffs[0] && S.ui.diffs[0].id) || (S.ui.logOpen ? '__log' : null); render(); },
  vsTheme: (a, el) => { __mockBridge.theme.vs = el.value; render(); },
  saveFile: a => saveFile(a),
  statusClose: () => { S.ui.statusOpen = false; render(); },
  env: (a, el) => { S.env[a] = el.checked; if (a === 'gh' && el.checked) S.env.ghInstalled = true; if (a === 'ghInstalled' && !el.checked) S.env.gh = false; prqRefresh(); render(); },
  simChange: () => simulateChange(),
  restart: () => restartVS(),
  branchSel: (a, el) => { S.branch = el.value; if (S.vm.wcPath) vmRefresh(); render(); },
  soon: a => SOON[a](),
  seSub: (a, el) => { const r = el.getBoundingClientRect(); ctxMenu(r.right - 4, r.top, [{ label: 'SVN Update', act: 'seOp', arg: 'update', arg2: a }, { label: 'SVN Commit...', act: 'seOp', arg: 'commit', arg2: a }, '-', { label: 'Diff con BASE', act: 'seOp', arg: 'diff', arg2: a }, { label: 'Mostra log', act: 'seOp', arg: 'log', arg2: a }, '-', { label: 'SVN Add', act: 'seOp', arg: 'add', arg2: a }, { label: 'SVN Revert', act: 'seOp', arg: 'revert', arg2: a }, { label: 'Aggiungi a ignore list', act: 'seOp', arg: 'ign', arg2: a }], true); },
  seOp: (a, el) => { closeCtx(); seOp(a, el.dataset.arg2); },
  sectx: (a, el) => { closeCtx(); toast('Anteprima: "' + el.textContent + '" — non ancora implementato'); },
};
document.addEventListener('click', e => {
  const inCtx = e.target.closest('.ctx'); if (!inCtx) closeCtx();
  if (!e.target.closest('.menubar') && S.ui.menu) { S.ui.menu = null; S.ui.sub = false; render(); }
  const el = e.target.closest('[data-act]'); if (!el || el.tagName === 'SELECT') return;
  if (el.tagName === 'A') e.preventDefault();
  const fn = ACT[el.dataset.act]; if (fn) { if (el.dataset.act === 'closeTab') e.stopPropagation(); fn(el.dataset.arg, el, e); }
});
document.addEventListener('change', e => { const el = e.target; if (el.tagName === 'SELECT' && el.dataset.act) ACT[el.dataset.act](el.dataset.arg, el, e); });
document.addEventListener('input', e => {
  const el = e.target;
  if (el.id === 'edtext') { const f = S.ui.active; S.ui.buf[f] = el.value; const g = $('#gut'); if (g) g.textContent = Array.from({ length: el.value.split('\n').length }, (_, i) => i + 1).join('\n'); return; }
  const m = el.dataset && el.dataset.model; if (!m) return;
  const [o, k] = m.split('.'); S[o][k] = el.type === 'number' ? (+el.value || 0) : el.value;
});
document.addEventListener('contextmenu', e => {
  const se = e.target.closest('[data-sectx]');
  if (se) { e.preventDefault(); const t = se.dataset.sectx; return ctxMenu(e.clientX, e.clientY, [...(t.startsWith('file|') ? [{ label: 'Apri', act: 'openFile', arg: t.slice(5) }, '-'] : []), { label: 'Taglia', act: 'sectx', off: true }, { label: 'Elimina', act: 'sectx', off: true }, '-', { label: 'SVN  ▸', act: 'seSub', arg: t }]); }
  const mini = e.target.closest('[data-minictx]');
  if (mini) { e.preventDefault(); ctxMenu(e.clientX, e.clientY, [{ label: 'Apri', act: 'sectx' }, '-', { label: 'SVN Update', act: 'sectx' }, { label: 'SVN Commit...', act: 'sectx' }, { label: 'SVN Diff', act: 'sectx' }, { label: 'SVN Revert', act: 'sectx' }, { label: 'SVN Blame', act: 'sectx' }, { label: 'SVN Log', act: 'sectx' }]); }
});
document.addEventListener('keydown', e => {
  if (e.key === 'Escape') {
    if ($('#ctxhost').firstChild) return closeCtx();
    if (mstack.length) { mstack[mstack.length - 1].cancel(); return; }
    if (S.ui.menu) { S.ui.menu = null; S.ui.sub = false; render(); }
    return;
  }
  if (e.key === 'Tab' && mstack.length) {
    const el = mstack[mstack.length - 1].el, f = $$('button:not(:disabled),input,select,textarea,[contenteditable]', el); if (!f.length) return;
    const i = f.indexOf(document.activeElement);
    if (e.shiftKey && i <= 0) { e.preventDefault(); f[f.length - 1].focus(); } else if (!e.shiftKey && i === f.length - 1) { e.preventDefault(); f[0].focus(); }
  }
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's' && e.target.id === 'edtext') { e.preventDefault(); saveFile(S.ui.active); }
});

// ---------- avvio ----------
function startMode(mode, welcome) {
  const panelOpen = S && S.ui ? S.ui.panelOpen : innerWidth >= 1500;
  while (mstack.length) { const m = mstack.pop(); m.el.remove(); } syncModals(); closeCtx();
  S = freshState(mode); S.ui.panelOpen = panelOpen;
  if (mode === 'first') S.tour = { on: true, step: 0 };
  render();
  if (welcome) showWelcome();
}
async function showWelcome() {
  const r = await modal({
    title: 'SVNAssist - demo interattiva', cancel: 'free', wide: false, body: `<p><b>Benvenuto.</b> Questa pagina simula Visual Studio con l'estensione SVNAssist: nulla è reale, tutto vive in memoria nel browser.</p>
    <p>Puoi seguire il <b>tour di primo utilizzo</b> (installazione, svn.exe mancante, configurazione dell'AI, primo commit) oppure esplorare liberamente con tutto già pronto.</p><p class="small muted">Suggerimento: il pulsante "Pannello simulazione" in alto permette di cambiare l'ambiente (svn, TortoiseSVN, provider AI...).</p>`,
    buttons: [{ label: 'Avvia il tour di primo utilizzo', value: 'first', primary: true }, { label: 'Esplora liberamente', value: 'free' }],
  });
  if (r === 'first') startMode('first');
}
S = freshState('free');
startMode('free');
let savedTab = null;
try { savedTab = localStorage.getItem('svnassist-demo-tab'); } catch (e) { }
if (savedTab === 'soon') ACT.tab('soon'); else showWelcome();
