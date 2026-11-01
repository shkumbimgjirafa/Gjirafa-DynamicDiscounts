'use strict';
// HACKTRACK hook for JS / TS (Node 18+). Copy this one file into your project. No dependencies.
//
//   const hacktrack = require('./hook.js');        // ESM project: save as hook.cjs, `import hacktrack from './hook.cjs'`
//   hacktrack.track({ kind: 'run', task: 'summarise invoice', ai_requests: 1, tokens_in: 812, tokens_out: 240 });
//
// or time a block:
//   const answer = await hacktrack.run('summarise invoice', async (t) => { t.ai_requests++; return callModel(); });
//
// Config from env (or hacktrack.configure({...})):
//   HACKTRACK_URL       collector endpoint, e.g. https://hx-usage.apps.gjirafa.ai/api/events
//                       (a URL with no path gets /api/events appended)
//   HACKTRACK_TOKEN     your team's token (write-only)
//   HACKTRACK_SOLUTION  your solution's name
//   HACKTRACK_USER      the Gjirafa email of the person using / building it
//   HACKTRACK_ECHO=1    print every queued event / card on stderr (to check an integration)
//
// v3 outcome fields (all optional), inside run() or passed to track():
//   t.units = 12;                                    how many units (orders, invoices, ...) this run handled, 0..1,000,000
//   t.findings.push({ id: 'vat-missing-4711', title: 'Invoice without VAT' });   Track B: problems found (id stable)
//   t.source = { system: 'warehouse', name: 'dwh', tables: ['sales'], rows_read: 5400 };   system: warehouse | shop-sql | other
//   run(task, fn, { input: theRequest })             input is hashed here (sha256 of canonical JSON) into input_hash;
//                                                    the input itself is never sent or stored
// A bad field is dropped (one warning on stderr); the event is still sent.
// Task card (one per core task; queued and sent like events):
//   hacktrack.card({ task: 'summarise invoice', track: 'A', unit: 'invoice', manual_minutes_per_unit: 6, ... })
//   node hook.js card '<json>'
//
// track() never throws and returns in about a millisecond: the event is written to an on-disk
// queue first, then sent in the background. If the collector is down, the queue is retried
// with backoff (and on the next start of your app) until it gets through. Nothing is lost.
// One process at a time sends a given queue (lock file in it), at most one event per second.

const fs = require('fs');
const os = require('os');
const path = require('path');
const http = require('http');
const https = require('https');
const crypto = require('crypto');

const overrides = {};
const state = { busy: false, nextTry: 0, backoff: 0, warned: new Set(), timer: null, hold: false, refused: new Map() };

function conf() {
  const env = process.env;
  return {
    url: overrides.url || env.HACKTRACK_URL || 'http://127.0.0.1:8787',
    token: overrides.token || env.HACKTRACK_TOKEN || '',
    solution: overrides.solution || env.HACKTRACK_SOLUTION || '',
    user: overrides.user || env.HACKTRACK_USER || '',
    cardUrl: overrides.cardUrl || env.HACKTRACK_CARD_URL || '',
    queueDir: overrides.queueDir || env.HACKTRACK_QUEUE_DIR || path.join(os.homedir(), '.hacktrack-queue'),
    paceMs: Number(overrides.paceMs ?? env.HACKTRACK_PACE_MS ?? 1050),
    timeoutMs: Number(overrides.timeoutMs ?? env.HACKTRACK_TIMEOUT_MS ?? 3000),
    defer: !!overrides.defer,
    echo: overrides.echo ?? env.HACKTRACK_ECHO === '1',
  };
}

function configure(opts) { Object.assign(overrides, opts || {}); resolvedDir = null; }

function warnOnce(key, msg) {
  if (state.warned.has(key)) return;
  state.warned.add(key);
  try { process.stderr.write(`[hacktrack] ${msg}\n`); } catch {}
}

function iso(v) {
  if (v === undefined || v === null) return undefined;
  if (v instanceof Date) return v.toISOString();
  if (typeof v === 'number') return new Date(v).toISOString();
  return String(v);
}

let resolvedDir = null;
function queueDir() {
  if (resolvedDir) return resolvedDir;
  for (const d of [conf().queueDir, path.join(os.tmpdir(), 'hacktrack-queue')]) {
    try { fs.mkdirSync(path.join(d, 'dead'), { recursive: true, mode: 0o700 }); fs.accessSync(d, fs.constants.W_OK); return (resolvedDir = d); } catch {}
  }
  throw new Error('no writable queue directory');
}

// Events are *.json; task cards are *.card (an older hook sharing the queue ignores them instead of posting them as events).
function pending() {
  const d = queueDir();
  return fs.readdirSync(d).filter((f) => f.endsWith('.json') || f.endsWith('.card')).sort().map((f) => path.join(d, f));
}

// ---------- v3 outcome fields ----------

const SOURCE_SYSTEMS = ['warehouse', 'shop-sql', 'other'];
const isInt = (v, min, max) => Number.isInteger(v) && v >= min && v <= max;
const isStr = (v, min, max) => typeof v === 'string' && v.trim().length >= min && v.length <= max;

// Canonical JSON: object keys sorted, no spaces (hook.py builds the same bytes), so the same input gives the same hash.
function canonical(v) {
  if (v === null || typeof v !== 'object') return JSON.stringify(v === undefined ? null : v) ?? 'null';
  if (typeof v.toJSON === 'function') return canonical(v.toJSON());
  if (Array.isArray(v)) return `[${v.map((x) => (x === undefined || typeof x === 'function' ? 'null' : canonical(x))).join(',')}]`;
  const keys = Object.keys(v).filter((k) => v[k] !== undefined && typeof v[k] !== 'function').sort();
  return `{${keys.map((k) => `${JSON.stringify(k)}:${canonical(v[k])}`).join(',')}}`;
}
const inputHash = (input) => crypto.createHash('sha256').update(canonical(input), 'utf8').digest('hex');

// Same limits as the collector. Returns the fields to send; a bad one is left out (warned once), never the event.
function outcome(ev) {
  const o = {};
  const bad = (k, msg) => warnOnce(`field-${k}`, `${msg}; that field is left out of the event (the event is still sent)`);
  if (ev.units !== undefined && ev.units !== null) {
    if (isInt(ev.units, 0, 1000000)) o.units = ev.units; else bad('units', 'units must be a whole number from 0 to 1000000');
  }
  if (ev.findings !== undefined && ev.findings !== null && !(Array.isArray(ev.findings) && !ev.findings.length)) {
    const ok = Array.isArray(ev.findings) && ev.findings.length <= 50
      && ev.findings.every((f) => f && typeof f === 'object' && isStr(f.id, 1, 80) && isStr(f.title, 1, 140));
    if (ok) o.findings = ev.findings.map((f) => ({ id: f.id, title: f.title }));
    else bad('findings', 'findings must be at most 50 items of {id: 1-80 chars, title: 1-140 chars}');
  }
  if (ev.source !== undefined && ev.source !== null) {
    const v = ev.source;
    const ok = v && typeof v === 'object' && !Array.isArray(v) && SOURCE_SYSTEMS.includes(v.system)
      && Object.keys(v).every((k) => ['system', 'name', 'tables', 'rows_read'].includes(k))
      && (v.name === undefined || v.name === null || isStr(v.name, 1, 100))
      && (v.tables === undefined || v.tables === null || (Array.isArray(v.tables) && v.tables.length <= 10 && v.tables.every((t) => isStr(t, 1, 100))))
      && (v.rows_read === undefined || v.rows_read === null || isInt(v.rows_read, 0, 1e12));
    if (ok) {
      o.source = { system: v.system };
      for (const k of ['name', 'tables', 'rows_read']) if (v[k] !== undefined && v[k] !== null) o.source[k] = v[k];
    } else bad('source', 'source must be {system: warehouse | shop-sql | other, name <= 100, tables <= 10 x <= 100, rows_read >= 0}');
  }
  if (Object.hasOwn(ev, 'input')) {
    try { o.input_hash = inputHash(ev.input); } catch { bad('input', 'input could not be hashed (not JSON-serialisable)'); }
  } else if (ev.input_hash !== undefined && ev.input_hash !== null) {
    if (typeof ev.input_hash === 'string' && /^[0-9a-f]{64}$/i.test(ev.input_hash)) o.input_hash = ev.input_hash.toLowerCase();
    else bad('input_hash', 'input_hash must be 64 hex chars');
  }
  return o;
}

// HACKTRACK_ECHO=1: print what is queued on stderr (local only; for checking an integration). An input is already a hash here.
function enqueue(obj, ext, later) {
  if (conf().echo) { try { process.stderr.write(`[hacktrack] queued ${ext === 'card' ? 'card' : 'event'}: ${JSON.stringify(obj)}\n`); } catch {} }
  const d = queueDir();
  const name = `${later ? 'L' : ''}${Date.now().toString().padStart(14, '0')}-${obj.id}.${ext}`;
  const tmp = path.join(d, `.${name}.tmp`);
  fs.writeFileSync(tmp, JSON.stringify(obj), { mode: 0o600 });
  fs.renameSync(tmp, path.join(d, name));
}

// Record one event. Returns its id (or null if even the disk write failed).
// opts.later: queue it behind everything else (bulk backfills), so fresh events are not held up.
function track(ev = {}, opts = {}) {
  try {
    const c = conf();
    const now = new Date().toISOString();
    const e = {
      id: ev.id || crypto.randomUUID(),
      solution: ev.solution || c.solution,
      user: ev.user || c.user,
      kind: ev.kind || 'run',
      task: ev.task || 'task',
      started_at: iso(ev.started_at) || now,
      ended_at: iso(ev.ended_at) || now,
      ai_requests: ev.ai_requests ?? 0,
      tokens_in: ev.tokens_in ?? 0,
      tokens_out: ev.tokens_out ?? 0,
      ok: ev.ok !== false,
    };
    if (Array.isArray(ev.prompts) && ev.prompts.length) {
      e.prompts = ev.prompts.map((p) => ({ text: String(p.text ?? ''), reply_chars: p.reply_chars ?? 0, at: iso(p.at) || now }));
    }
    Object.assign(e, outcome(ev));
    enqueue(e, 'json', opts.later);
    kick();
    return e.id;
  } catch (err) {
    warnOnce('track', `could not record event: ${err.message}`);
    return null;
  }
}

// Time an async block as a `run` (or other kind). Records ok=false if it throws, then rethrows.
// Inside fn: t.ai_requests / t.tokens_in / t.tokens_out / t.prompts as before; v3: t.units, t.findings.push(...), t.source,
// t.input. extra may carry the same (input is hashed locally, never sent).
async function run(task, fn, extra = {}) {
  const t = { ai_requests: 0, tokens_in: 0, tokens_out: 0, prompts: [], findings: [], ...extra };
  const started = new Date();
  let ok = true;
  try { return await fn(t); }
  catch (e) { ok = false; throw e; }
  finally { track({ kind: 'run', ...t, task, started_at: started, ended_at: new Date(), ok: t.ok === false ? false : ok }); }
}

// ---------- v3 task card ----------

const CARD_FIELDS = ['solution', 'user', 'task', 'track', 'unit', 'manual_minutes_per_unit', 'minutes_source', 'euro_per_unit',
  'euro_source', 'data_source', 'sheet_row', 'mocked'];
const num = (v) => typeof v === 'number' && Number.isFinite(v);

// Same rules as the collector. Returns an error message, or null when the card is fine.
function cardError(c) {
  const extra = Object.keys(c).find((k) => k !== 'id' && !CARD_FIELDS.includes(k));
  if (extra) return `${extra} is not a card field (${CARD_FIELDS.join(', ')})`;
  if (!isStr(c.solution, 1, 100)) return 'solution is required (HACKTRACK_SOLUTION), at most 100 chars';
  if (!isStr(c.user, 1, 200) || !/^[^@\s]+@([a-z0-9-]+\.)+[a-z]{2,}$/i.test(c.user.trim())) return 'user must be your email (HACKTRACK_USER)';
  if (!isStr(c.task, 1, 200)) return 'task is required: the exact task name your run() calls use (at most 200 chars)';
  if (c.track !== 'A' && c.track !== 'B') return "track must be 'A' or 'B'";
  if (!isStr(c.unit, 1, 60)) return 'unit is required, at most 60 chars (e.g. "order", "invoice")';
  if (!num(c.manual_minutes_per_unit) || c.manual_minutes_per_unit <= 0 || c.manual_minutes_per_unit > 600) return 'manual_minutes_per_unit must be a number above 0 and at most 600';
  if (!isStr(c.minutes_source, 1, 300)) return 'minutes_source is required, at most 300 chars (where the minutes per unit come from)';
  const euro = c.euro_per_unit;
  if (euro === undefined || euro === null) { if (c.track === 'A') return 'euro_per_unit is required for track A'; }
  else if (!num(euro) || euro < 0) return 'euro_per_unit must be a number >= 0';
  if (c.euro_source !== undefined && c.euro_source !== null && c.euro_source !== '' && !isStr(c.euro_source, 1, 300)) return 'euro_source must be at most 300 chars';
  if (euro !== undefined && euro !== null && !isStr(c.euro_source, 1, 300)) return 'euro_source is required with euro_per_unit (where the euros per unit come from)';
  const ds = c.data_source;
  if (!ds || typeof ds !== 'object' || Array.isArray(ds) || !SOURCE_SYSTEMS.includes(ds.system) || !isStr(ds.name, 1, 100)
    || Object.keys(ds).some((k) => k !== 'system' && k !== 'name')) return 'data_source must be {system: warehouse | shop-sql | other, name: 1-100 chars}';
  if (c.sheet_row !== undefined && c.sheet_row !== null && c.sheet_row !== '' && !isStr(c.sheet_row, 1, 100)) return 'sheet_row must be at most 100 chars';
  if (c.mocked !== undefined && c.mocked !== null && (typeof c.mocked !== 'string' || c.mocked.length > 300)) return 'mocked must be a string of at most 300 chars';
  return null;
}

// Queue the team's task card for a core task. Returns { id } or { error } (nothing queued then). Never throws.
function card(fields = {}) {
  try {
    const cf = conf();
    const c = { ...fields, solution: fields.solution || cf.solution, user: fields.user || cf.user };
    for (const k of ['euro_source', 'sheet_row']) if (c[k] === '' || c[k] === null) delete c[k];
    const err = cardError(c);
    if (err) { warnOnce(`card-${err}`, `card not sent: ${err}`); return { error: err }; }
    c.id = typeof fields.id === 'string' && /^[A-Za-z0-9_-]{8,64}$/.test(fields.id) ? fields.id : crypto.randomUUID();
    enqueue(c, 'card', false);
    kick();
    return { id: c.id };
  } catch (err) {
    warnOnce('card', `could not record card: ${err.message}`);
    return { error: err.message };
  }
}

// HACKTRACK_URL may be the full endpoint; with no path, the default /api/events is used.
function endpoint(url) {
  const u = new URL(url);
  if (u.pathname === '' || u.pathname === '/') u.pathname = '/api/events';
  return u;
}

// Cards go to /api/cards beside the event path (…/events → …/cards; any other path → /api/cards), or HACKTRACK_CARD_URL.
function cardEndpoint(url, cardUrl) {
  if (cardUrl) return new URL(cardUrl);
  const u = endpoint(url);
  u.pathname = /\/events$/.test(u.pathname) ? u.pathname.replace(/\/events$/, '/cards') : '/api/cards';
  return u;
}

function post(body, isCard = false) {
  return new Promise((resolve) => {
    const c = conf();
    let u;
    try { u = isCard ? cardEndpoint(c.url, c.cardUrl) : endpoint(c.url); } catch { return resolve({ error: 'bad HACKTRACK_URL' }); }
    const lib = u.protocol === 'https:' ? https : http;
    const req = lib.request(u, {
      method: 'POST',
      timeout: c.timeoutMs,
      headers: { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body), Authorization: `Bearer ${c.token}` },
    }, (res) => {
      // Keep the start of the answer: a refused card's reason is shown to whoever sent it.
      let text = '';
      res.on('data', (d) => { if (text.length < 2048) text += d; });
      res.on('end', () => resolve({ status: res.statusCode, text }));
      res.on('error', () => resolve({ error: 'response' }));
    });
    req.on('timeout', () => req.destroy(new Error('timeout')));
    req.on('error', (e) => resolve({ error: e.code || e.message }));
    req.end(body);
  });
}

// While flush() is waiting the timer must keep the process alive (a CLI flush would otherwise exit mid-queue).
const sleep = (ms) => new Promise((r) => { const t = setTimeout(r, ms); if (t.unref && !state.hold) t.unref(); });

// One sender per queue directory: every process on this machine using the same queue (apps, Claude Code
// hooks, `flush`) takes .sender.lock before posting, and the pace is kept across processes (.last-sent),
// so one machine sends at most one event per paceMs and never posts the same file twice at once.
const LOCK_STALE_MS = 2 * 60 * 1000;
const lockFile = () => path.join(queueDir(), '.sender.lock');
function alive(pid) { try { process.kill(pid, 0); return true; } catch (e) { return e.code === 'EPERM'; } }
function takeLock() {
  const f = lockFile();
  for (let i = 0; i < 2; i++) {
    try { fs.writeFileSync(f, String(process.pid), { flag: 'wx', mode: 0o600 }); return true; }
    catch (e) {
      if (e.code !== 'EEXIST') return true; // cannot lock here at all: send unlocked, as before
      let pid = 0; let age = 0;
      try { pid = Number(fs.readFileSync(f, 'utf8')); age = Date.now() - fs.statSync(f).mtimeMs; } catch { continue; }
      if (pid && pid !== process.pid && alive(pid) && age < LOCK_STALE_MS) return false;
      try { fs.unlinkSync(f); } catch {}
    }
  }
  return false;
}
function touchLock() { try { const t = new Date(); fs.utimesSync(lockFile(), t, t); } catch {} }
function dropLock() { try { if (Number(fs.readFileSync(lockFile(), 'utf8')) === process.pid) fs.unlinkSync(lockFile()); } catch {} }
function lastSent() { try { return Number(fs.readFileSync(path.join(queueDir(), '.last-sent'), 'utf8')) || 0; } catch { return 0; } }
function markSent() { try { fs.writeFileSync(path.join(queueDir(), '.last-sent'), String(Date.now()), { mode: 0o600 }); } catch {} }

function scheduleRetry() {
  state.backoff = Math.min(60000, state.backoff ? state.backoff * 2 : 1000);
  const wait = state.backoff + Math.floor(Math.random() * 250);
  state.nextTry = Date.now() + wait;
  clearTimeout(state.timer);
  state.timer = setTimeout(() => { state.nextTry = 0; kick(); }, wait);
  if (state.timer.unref) state.timer.unref();
}

// Send everything queued, oldest first, one at a time, paced so one machine stays at or under
// one event per second. Stops at the first network/server failure and schedules a retry.
// Returns 'empty', 'retry', 'busy' (this process is already sending) or 'other' (another process holds the lock).
async function drain() {
  if (state.busy) return 'busy';
  state.busy = true;
  try {
    for (;;) {
      if (!takeLock()) return 'other';
      let r;
      try { r = await sendAll(); } finally { dropLock(); }
      if (r !== 'empty') return r;
      // Anything queued by a process that found the lock taken while we held it is ours to send.
      let more = 0; try { more = pending().length; } catch {}
      if (!more) return 'empty';
    }
  } finally { state.busy = false; }
}

async function sendAll() {
  for (;;) {
    let files;
    try { files = pending(); } catch { return 'empty'; }
    if (!files.length) { state.backoff = 0; return 'empty'; }
    const f = files[0];
    let body;
    try { body = fs.readFileSync(f); }
    catch (e) { if (e.code !== 'ENOENT') moveDead(f, 'unreadable'); continue; }
    if (!conf().token) { warnOnce('token', 'HACKTRACK_TOKEN is not set; events stay queued on disk'); scheduleRetry(); return 'retry'; }
    const wait = lastSent() + conf().paceMs - Date.now();
    if (wait > 0) await sleep(Math.min(wait, conf().paceMs));
    touchLock();
    const r = await post(body, f.endsWith('.card'));
    markSent();
    if (r.status >= 200 && r.status < 300) {
      try { fs.unlinkSync(f); } catch {}
      state.backoff = 0;
    } else if (r.status >= 400 && r.status < 500 && r.status !== 408 && r.status !== 429) {
      moveDead(f, r.status);
      if (f.endsWith('.card')) {
        let why = ''; try { why = JSON.parse(r.text).error || ''; } catch {}
        state.refused.set(path.basename(f), `HTTP ${r.status}${why ? `: ${why}` : ''}`);
        warnOnce(`card${r.status}${why}`, `collector refused a card (HTTP ${r.status}${why ? `: ${why}` : ''}); kept in ${path.join(queueDir(), 'dead')}`);
      } else warnOnce(`http${r.status}`, `collector refused an event (HTTP ${r.status}); kept in ${path.join(queueDir(), 'dead')}`);
    } else {
      scheduleRetry();
      return 'retry';
    }
  }
}

function moveDead(f, why) {
  const ext = path.extname(f);
  try { fs.renameSync(f, path.join(path.dirname(f), 'dead', `${path.basename(f, ext)}.${why}${ext}`)); } catch {}
}

function kick() {
  if (conf().defer || state.busy || Date.now() < state.nextTry) return;
  drain().catch(() => {});
}

// Await delivery of the queue (for scripts / tests). Resolves true when the queue is empty.
// While another process is sending this queue it waits; with { leave: true } it resolves null at once
// instead (that process sends everything queued before it lets go of the lock).
async function flush(timeoutMs = 30000, opts = {}) {
  const end = Date.now() + timeoutMs;
  state.nextTry = 0;
  state.hold = true;
  try {
    while (Date.now() < end) {
      if (!pending().length) return true;
      if (!state.busy && Date.now() >= state.nextTry) {
        if ((await drain()) === 'other') { if (opts.leave) return null; await new Promise((r) => setTimeout(r, 100)); }
      } else await new Promise((r) => setTimeout(r, 50));
    }
    return !pending().length;
  } finally { state.hold = false; }
}

// Pick up anything left queued by an earlier run of this app.
setTimeout(() => { try { if (pending().length) kick(); } catch {} }, 0).unref();

module.exports = { track, run, flush, configure, pending, endpoint, card, cardEndpoint, inputHash, canonical };

// CLI: node hook.js send '{"kind":"build","task":"commit abc123"}'   |   node hook.js card '{"task":...}'   |   node hook.js flush
if (require.main === module) {
  const [cmd, json] = process.argv.slice(2);
  if (cmd === 'send') {
    let ev = {};
    try { ev = JSON.parse(json || '{}'); } catch { console.error('send expects one JSON object'); process.exit(2); }
    const id = track(ev);
    flush(5000).then((done) => { console.log(`${id} ${done ? 'sent' : 'queued'}`); });
  } else if (cmd === 'card') {
    let fields = {};
    try { fields = JSON.parse(json || '{}'); } catch { console.error('card expects one JSON object'); process.exit(2); }
    if (!fields || typeof fields !== 'object' || Array.isArray(fields)) { console.error('card expects one JSON object'); process.exit(2); }
    const r = card(fields);
    if (r.error) { console.error(`card not sent: ${r.error}`); process.exit(2); }
    flush(5000).then((done) => {
      const refused = [...state.refused].find(([f]) => f.includes(r.id));
      if (refused) { console.error(`card refused (${refused[1]})`); process.exit(1); }
      console.log(`${r.id} ${done ? 'sent' : 'queued (sent when the collector answers)'}`);
    });
  } else if (cmd === 'flush') {
    flush(10 * 60 * 1000, { leave: true }).then((done) => {
      console.log(done === null ? 'another sender is delivering the queue' : done ? 'queue empty' : `${pending().length} still queued`);
      process.exit(done === false ? 1 : 0);
    });
  } else {
    console.error('usage: node hook.js send <json> | card <json> | flush');
    process.exit(2);
  }
}
