/* EMINOVA hosting shim — loaded by the patched HR & Payroll Workbench before its own script.
 * Replaces browser-only localStorage persistence with the Eminova.Host API:
 *   init()  GET  /api/state  → window.EminovaApi.state (null when the tenant has no saved state yet) + ETag version
 *   save(S) PUT  /api/state  with If-Match (debounced 800 ms; flushed with sendBeacon on page unload)
 *   409     another user saved first → show a banner and reload the latest state (no silent overwrite)
 * localStorage is kept as an offline cache so the Workbench keeps working through a short outage. */
window.EminovaApi = (function () {
  const meta = n => (document.querySelector('meta[name="' + n + '"]') || {}).content;
  const base = meta('eminova-api') || '/api';
  const tenant = meta('eminova-tenant') || 'default';
  const headers = { 'Content-Type': 'application/json', 'X-Eminova-Tenant': tenant };
  let timer = null, pending = null;

  const api = { state: null, online: false, version: null, base, tenant, init, save, flush };

  async function init() {
    try {
      const r = await fetch(base + '/state', { headers, credentials: 'same-origin', cache: 'no-store' });
      if (r.status === 204) api.state = null;              // first visit for this tenant: the Workbench seeds itself
      else if (r.ok) { api.state = await r.json(); api.version = (r.headers.get('ETag') || '').replace(/"/g, '') || null; }
      else console.warn('EMINOVA API: GET /state returned', r.status);
      api.online = r.ok || r.status === 204;
    } catch (e) {
      api.online = false; console.warn('EMINOVA API unreachable – using local cache', e);
    }
    badge();
  }

  function save(S) {
    pending = JSON.stringify(S);
    clearTimeout(timer);
    timer = setTimeout(flush, 800);
  }

  async function flush() {
    if (!pending) return;
    const body = pending; pending = null;
    const h = Object.assign({}, headers); if (api.version) h['If-Match'] = '"' + api.version + '"';
    try {
      const r = await fetch(base + '/state', { method: 'PUT', headers: h, credentials: 'same-origin', body });
      if (r.status === 409) { conflict(); return; }
      api.online = r.ok;
      if (r.ok) { const v = (r.headers.get('ETag') || '').replace(/"/g, ''); if (v) api.version = v; }
      else console.warn('EMINOVA API: PUT /state returned', r.status);
    } catch (e) { api.online = false; pending = pending || body; }   // keep the change; retried on the next save
    badge();
  }

  function conflict() {
    const d = document.createElement('div');
    d.setAttribute('role', 'alert');
    d.style.cssText = 'position:fixed;left:50%;top:12px;transform:translateX(-50%);z-index:9999;background:#B42318;color:#fff;padding:10px 16px;border-radius:8px;font:600 14px system-ui';
    d.textContent = 'Another user saved payroll changes a moment ago — loading the latest version. Please repeat your last action.';
    document.body.appendChild(d);
    setTimeout(() => location.reload(), 2500);
  }

  function badge() { document.documentElement.setAttribute('data-eminova-sync', api.online ? 'online' : 'offline'); }

  window.addEventListener('beforeunload', () => {
    if (pending && navigator.sendBeacon) {
      navigator.sendBeacon(base + '/state?tenant=' + encodeURIComponent(tenant), new Blob([pending], { type: 'application/json' }));
      pending = null;
    }
  });
  return api;
})();
