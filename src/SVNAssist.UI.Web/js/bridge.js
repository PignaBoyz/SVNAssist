'use strict';
/* Ponte verso l'host. Reale: window.chrome.webview (WebView2). Demo/test: window.__mockBridge (anche del frame padre).
   Protocollo: docs/ui-protocol.md */
(function (w) {
  const U = w.SVNUI; let seq = 0;
  const host = w.chrome && w.chrome.webview;
  let mock = null;
  if (!host) { try { mock = w.__mockBridge || (w.parent !== w && w.parent.__mockBridge) || null; } catch (e) { mock = null; } }
  const post = m => { if (host) host.postMessage(m); else if (mock) mock.postMessage(m, w); };
  U.bridge = {
    mode: host ? 'webview2' : mock ? 'mock' : 'none',
    // JS -> host: comando. Ritorna l'id per correlare la risposta {type:"reply"}.
    send(name, args) { const id = ++seq; post({ v: 1, id, type: 'cmd', name, args: args || {} }); return id; },
    ready(view, id) { post({ v: 1, type: 'ready', view, id: id || null }); },
    onMessage(fn) { if (host) host.addEventListener('message', e => fn(e.data)); else if (mock) mock.attach(w, fn); },
  };
})(window);
