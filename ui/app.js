'use strict';

/* ================= helpers ================= */
const $ = (s, el = document) => el.querySelector(s);
const el = (tag, cls, html) => { const e = document.createElement(tag); if (cls) e.className = cls; if (html != null) e.innerHTML = html; return e; };
const esc = s => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const uid = () => Date.now().toString(36) + Math.random().toString(36).slice(2, 8);
// wrapped in a bidi isolate so "1.5 GB" doesn't flip inside Hebrew text
const fmtGB = b => '⁦' + (b / 1073741824).toFixed(b >= 10737418240 ? 0 : 1) + ' GB⁩';

async function api(path, opts = {}) {
  const r = await fetch('/api/' + path, {
    method: opts.method || 'GET',
    headers: opts.body != null ? { 'Content-Type': 'application/json' } : undefined,
    body: opts.body != null ? JSON.stringify(opts.body) : undefined,
  });
  const text = await r.text();
  let data = null; try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  if (!r.ok) throw new Error((data && data.error) || ('HTTP ' + r.status));
  return data;
}

let toastTimer;
function toast(msg, ms = 2600) {
  const t = $('#toast'); t.textContent = msg; t.classList.remove('hidden');
  clearTimeout(toastTimer); toastTimer = setTimeout(() => t.classList.add('hidden'), ms);
}

const ICONS = {
  copy: '<svg viewBox="0 0 24 24"><rect x="9" y="9" width="12" height="12" rx="2"/><path d="M5 15V5a2 2 0 0 1 2-2h10"/></svg>',
  check: '<svg viewBox="0 0 24 24"><path d="M20 6 9 17l-5-5"/></svg>',
  retry: '<svg viewBox="0 0 24 24"><path d="M3 12a9 9 0 0 1 15.5-6.2L21 8M21 3v5h-5M21 12a9 9 0 0 1-15.5 6.2L3 16M3 21v-5h5"/></svg>',
  edit: '<svg viewBox="0 0 24 24"><path d="M12 20h9M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z"/></svg>',
  dots: '<svg viewBox="0 0 24 24"><circle cx="5" cy="12" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/></svg>',
  spark: '<svg viewBox="0 0 24 24"><path d="M12 3l1.9 5.8L20 10.7l-5 3.6 1.9 5.7-4.9-3.6L7.1 20 9 14.3l-5-3.6 6.1-1.9z"/></svg>',
  x: '<svg viewBox="0 0 24 24"><path d="M18 6 6 18M6 6l12 12"/></svg>',
  trash: '<svg viewBox="0 0 24 24"><path d="M3 6h18M8 6V4h8v2M19 6l-1 14H6L5 6"/></svg>',
  pin: '<svg viewBox="0 0 24 24"><path d="M12 17v5M9 3h6l-1 7 4 3v2H6v-2l4-3z"/></svg>',
  folder: '<svg viewBox="0 0 24 24"><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/></svg>',
  refresh: '<svg viewBox="0 0 24 24"><path d="M21 12a9 9 0 1 1-3-6.7L21 8M21 3v5h-5"/></svg>',
  power: '<svg viewBox="0 0 24 24"><path d="M12 2v10M18.4 6.6a9 9 0 1 1-12.8 0"/></svg>',
  sun: '<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/></svg>',
  image: '<svg viewBox="0 0 24 24"><rect x="3" y="3" width="18" height="18" rx="3"/><circle cx="9" cy="9" r="2"/><path d="m21 15-3.1-3.1a2 2 0 0 0-2.8 0L6 21"/></svg>',
  download: '<svg viewBox="0 0 24 24"><path d="M12 3v12M7 10l5 5 5-5M5 21h14"/></svg>',
  moon: '<svg viewBox="0 0 24 24"><path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/></svg>',
};

/* ================= state ================= */
const DEFAULTS = {
  system: '', temperature: 0.7, top_p: 0.95, max_tokens: 0, repeat_penalty: 1.05,
  ctx: 8192, threads: 0, font: 15.5, enterSends: 1, theme: 'dark', sideHidden: false,
  mode: 'chat', negative: '', cfg: 0, translate: 1, imgSize: 'auto', imgSteps: 0, imgCount: 1,
};
let S = { ...DEFAULTS };          // settings
let engine = { state: 'idle' };   // /api/state
let models = [];
let imageModels = [];
const imgEngine = () => engine.image || { state: 'idle' };
let chats = [];                   // index
let chat = null;                  // current chat
let streaming = null;             // { ctrl, msg }
let pendingImages = [];

/* ================= markdown ================= */
const renderer = new marked.Renderer();
renderer.code = function (token) {
  const text = typeof token === 'object' ? token.text : token;
  const lang = ((typeof token === 'object' ? token.lang : arguments[1]) || '').trim().split(/\s+/)[0];
  return `<div class="codeblock"><div class="code-head"><span>${esc(lang || 'קוד')}</span>` +
    `<button class="copy-code" type="button">${ICONS.copy}<span>העתקה</span></button></div>` +
    `<pre><code class="${lang ? 'language-' + esc(lang) : ''}">${esc(text)}</code></pre></div>`;
};
marked.setOptions({ renderer, gfm: true, breaks: true });

function renderMarkdown(target, text, live) {
  let html;
  try { html = DOMPurify.sanitize(marked.parse(text || '')); }
  catch { html = '<p>' + esc(text) + '</p>'; }
  target.innerHTML = html;
  target.querySelectorAll('p,li,h1,h2,h3,h4,h5,h6,blockquote,td,th,ul,ol').forEach(n => n.setAttribute('dir', 'auto'));
  target.querySelectorAll('a').forEach(a => { a.target = '_blank'; a.rel = 'noopener'; });
  target.querySelectorAll('pre code').forEach(c => {
    if (c.textContent.length < 20000) { try { hljs.highlightElement(c); } catch { } }
  });
  if (live) {
    // put the blinking cursor at the very end of the last text block
    let last = target.lastElementChild;
    while (last && last.lastElementChild && !/^(PRE|CODE|TABLE)$/.test(last.lastElementChild.tagName) && last.tagName !== 'PRE') last = last.lastElementChild;
    if (last && last.tagName !== 'DIV') last.classList.add('cursor');
    else target.appendChild(el('span', 'cursor'));
  }
}

/** Splits inline <think>…</think> (if the server did not separate it). */
function splitThink(content, reasoning) {
  if (reasoning) return { think: reasoning, text: content };
  const m = /^\s*<think>([\s\S]*?)(<\/think>|$)/.exec(content || '');
  if (!m) return { think: '', text: content };
  return { think: m[1], text: content.slice(m[0].length), open: !m[2] };
}

/* ================= theme & settings ================= */
function applySettings() {
  const theme = S.theme === 'light' ? 'light' : 'dark';
  document.documentElement.dataset.theme = theme;
  $('#hljs-dark').disabled = theme === 'light';
  $('#hljs-light').disabled = theme !== 'light';
  $('#themeBtn').innerHTML = theme === 'light' ? ICONS.moon : ICONS.sun;
  document.documentElement.style.setProperty('--fs', S.font + 'px');
  $('#app').classList.toggle('side-hidden', !!S.sideHidden);
  const img = S.mode === 'image';
  $('#composer').classList.toggle('image-mode', img);
  $('#modeBtn').classList.toggle('on', img);
  $('#modeBtn').title = img ? 'חזרה לשיחה' : 'יצירת תמונה';
  $('#input').placeholder = img ? 'תארו את התמונה שתרצו ליצור…' : 'כתבו הודעה…';
  $('#imgSize').value = S.imgSize; $('#imgSteps').value = String(S.imgSteps); $('#imgCount').value = String(S.imgCount);
}

async function saveSettings(patch) {
  Object.assign(S, patch);
  applySettings();
  try { await api('settings', { method: 'PUT', body: patch }); } catch (e) { toast('שמירת ההגדרות נכשלה'); }
}

function openSettings() {
  $('#setSystem').value = S.system || '';
  $('#setTemp').value = S.temperature; $('#tempVal').textContent = S.temperature;
  $('#setTopP').value = S.top_p; $('#topPVal').textContent = S.top_p;
  $('#setMaxTok').value = S.max_tokens;
  $('#setRepeat').value = S.repeat_penalty;
  $('#setCtx').value = String(S.ctx);
  if (!$('#setCtx').value) { $('#setCtx').append(new Option(S.ctx, S.ctx)); $('#setCtx').value = String(S.ctx); }
  $('#setThreads').value = S.threads;
  $('#setFont').value = String(S.font);
  $('#setEnter').value = String(S.enterSends);
  $('#setNeg').value = S.negative || '';
  $('#setCfg').value = S.cfg;
  $('#setTranslate').value = String(S.translate);
  showModal('settings');
}

function showModal(kind, html) {
  const m = $('#modal');
  m.dataset.kind = kind;
  const body = $('.modal-body', m);
  if (!body.dataset.orig) body.dataset.orig = body.innerHTML;
  if (kind === 'settings') {
    if (body.dataset.swapped) { body.innerHTML = body.dataset.orig; delete body.dataset.swapped; bindSettingsInputs(); openSettings(); return; }
    $('.modal-head h2', m).textContent = 'הגדרות';
    $('.modal-foot', m).classList.remove('hidden');
  } else {
    body.innerHTML = html; body.dataset.swapped = '1';
    $('.modal-head h2', m).textContent = kind === 'log' ? 'יומן מנוע' : '';
    $('.modal-foot', m).classList.add('hidden');
  }
  m.classList.remove('hidden');
}
function closeModal() { $('#modal').classList.add('hidden'); }

function bindSettingsInputs() {
  $('#setTemp').oninput = e => $('#tempVal').textContent = e.target.value;
  $('#setTopP').oninput = e => $('#topPVal').textContent = e.target.value;
}

async function submitSettings() {
  const num = (v, d) => { const n = parseFloat(v); return isFinite(n) ? n : d; };
  const patch = {
    system: $('#setSystem').value.trim(),
    temperature: num($('#setTemp').value, 0.7),
    top_p: num($('#setTopP').value, 0.95),
    max_tokens: Math.max(0, Math.round(num($('#setMaxTok').value, 0))),
    repeat_penalty: num($('#setRepeat').value, 1.05),
    ctx: Math.round(num($('#setCtx').value, 8192)),
    threads: Math.max(0, Math.round(num($('#setThreads').value, 0))),
    font: num($('#setFont').value, 15.5),
    enterSends: +$('#setEnter').value,
    negative: $('#setNeg').value.trim(),
    cfg: Math.max(0, num($('#setCfg').value, 0)),
    translate: +$('#setTranslate').value,
  };
  const engineChanged = patch.ctx !== S.ctx || patch.threads !== S.threads;
  await saveSettings(patch);
  closeModal();
  if (engineChanged && engine.model && engine.state !== 'idle') {
    if (confirm('לטעון מחדש את המודל עם הגדרות המנוע החדשות?')) loadModel(engine.model);
  } else toast('נשמר');
}

/* ================= engine / models ================= */
let pollTimer;
async function pollState() {
  clearTimeout(pollTimer);
  try {
    const prev = engine.state;
    engine = await api('state');
    renderEngine();
    if (prev === 'loading' && engine.state === 'ready') toast('המודל מוכן');
    if (prev === 'loading' && engine.state === 'error') renderWelcome();
    const ip = pollState.imgPrev, is = imgEngine().state;
    if (ip === 'loading' && is === 'ready') toast('מודל התמונות מוכן');
    if (ip === 'loading' && is === 'error') toast('טעינת מודל התמונות נכשלה: ' + (imgEngine().error || ''), 6000);
    pollState.imgPrev = is;
  } catch { engine = { state: 'down' }; renderEngine(); }
  pollTimer = setTimeout(pollState, engine.state === 'loading' || imgEngine().state === 'loading' ? 700 : 4000);
}

function modelName(file) {
  const m = models.find(x => x.file === file);
  return m ? m.name : (file || '').split('\\').pop().replace(/\.gguf$/i, '');
}

function renderEngine() {
  const imgMode = S.mode === 'image';
  const e = imgMode && engine.state !== 'down' ? imgEngine() : engine;
  const dot = $('#stateDot');
  dot.className = 'dot ' + (e.state === 'ready' ? 'ready' : e.state === 'loading' ? 'loading' : e.state === 'error' || e.state === 'down' ? 'error' : '');
  const name = $('#modelName');
  if (engine.state === 'down') name.textContent = 'התוכנה נסגרה';
  else if (e.model) name.textContent = (imgMode ? imageModelName(e.model) : modelName(e.model)) + (e.state === 'loading' ? ' · טוען…' : '');
  else if (imgMode) name.textContent = 'בחרו מודל תמונות';
  else name.textContent = models.length ? 'בחרו מודל' : 'אין מודלים';
  name.parentElement.title = e.error || '';
  let chip = $('.img-chip');
  if (imgMode) { if (!chip) { chip = el('span', 'img-chip', 'תמונות'); name.before(chip); } } else if (chip) chip.remove();

  const bar = $('#loadBar');
  bar.classList.toggle('hidden', e.state !== 'loading');
  $('#loadFill').style.width = Math.max(5, e.progress || 0) + '%';

  $('#attachBtn').classList.toggle('hidden', !(engine.state === 'ready' && engine.vision));
  if (engine.ram) $('#ramInfo').textContent = `זיכרון פנוי: ${fmtGB(engine.ram.free)} מתוך ${fmtGB(engine.ram.total)}`;
  updateSendBtn();
  if (!chat || !chat.messages.length) renderWelcome();
}

async function refreshModels() {
  try { [models, imageModels] = await Promise.all([api('models'), api('image-models')]); } catch { models = models || []; imageModels = imageModels || []; }
}

function imageModelName(file) {
  const m = imageModels.find(x => x.file === file);
  return m ? m.name : (file || '').split('\\').pop().replace(/\.[^.]+$/, '');
}

async function loadImageModel(file) {
  closeMenus();
  try {
    await api('image-load', { method: 'POST', body: { file, threads: S.threads } });
    engine.image = { ...imgEngine(), state: 'loading', model: file, progress: 0, error: null };
    if (S.mode !== 'image') saveSettings({ mode: 'image' });
    renderEngine();
    pollState();
  } catch (e) { toast('שגיאה: ' + e.message, 5000); }
}

async function loadModel(file) {
  closeMenus();
  try {
    await api('load', { method: 'POST', body: { file, ctx: S.ctx, threads: S.threads } });
    engine.state = 'loading'; engine.model = file; engine.progress = 0; engine.error = null;
    renderEngine();
    pollState();
  } catch (e) { toast('שגיאה: ' + e.message, 5000); }
}

async function toggleModelMenu() {
  const menu = $('#modelMenu');
  if (!menu.classList.contains('hidden')) { closeMenus(); return; }
  await refreshModels();
  menu.innerHTML = '';
  menu.append(el('div', 'menu-head', 'מודלי שיחה'));
  if (!models.length) {
    menu.append(el('div', 'menu-note', 'לא נמצאו קבצי <b>.gguf</b> בתיקייה <b>מודלים</b> שליד התוכנה.'));
  }
  const ram = engine.ram ? engine.ram.total : 0;
  for (const m of models) {
    const b = el('button', 'menu-item');
    const tooBig = ram && m.size > ram * 0.85;
    b.innerHTML = `<div class="mi-main"><div class="mi-name" title="${esc(m.file)}">${esc(m.name)}</div>
      <div class="mi-sub">${fmtGB(m.size)} ${m.vision ? '<span class="tag">תמונות</span>' : ''} ${tooBig ? '<span class="tag warn">גדול מדי לזיכרון</span>' : ''}</div></div>
      ${engine.model === m.file && engine.state !== 'idle' ? '<span class="check">' + ICONS.check + '</span>' : ''}`;
    b.onclick = () => { if (S.mode === 'image') saveSettings({ mode: 'chat' }); loadModel(m.file); };
    menu.append(b);
  }
  menu.append(el('div', 'menu-sep'));
  menu.append(el('div', 'menu-head', 'מודלי תמונות'));
  const ie = imgEngine();
  if (ie.available === false) menu.append(el('div', 'menu-note', 'רכיב יצירת התמונות חסר (engine\\sd).'));
  else if (!imageModels.length) menu.append(el('div', 'menu-note', 'שימו מודל תמונות (.safetensors / .gguf) בתיקייה <b>מודלים\\תמונות</b>.'));
  for (const m of imageModels) {
    const b = el('button', 'menu-item');
    const tooBig = ram && m.size > ram * 0.85;
    b.innerHTML = `<div class="mi-main"><div class="mi-name" title="${esc(m.file)}">${esc(m.name)}</div>
      <div class="mi-sub">${fmtGB(m.size)} ${m.fast ? '<span class="tag">מהיר</span>' : ''} ${tooBig ? '<span class="tag warn">גדול מדי לזיכרון</span>' : ''}</div></div>
      ${ie.model === m.file && ie.state !== 'idle' ? '<span class="check">' + ICONS.check + '</span>' : ''}`;
    b.onclick = () => loadImageModel(m.file);
    menu.append(b);
  }
  menu.append(el('div', 'menu-sep'));
  const add = (icon, text, fn, cls) => { const b = el('button', 'menu-item ' + (cls || ''), icon + '<span class="mi-main">' + text + '</span>'); b.onclick = () => { closeMenus(); fn(); }; menu.append(b); };
  add(ICONS.folder, 'פתיחת תיקיית המודלים', () => api('open-models', { method: 'POST' }));
  add(ICONS.refresh, 'רענון הרשימה', () => toggleModelMenu());
  if (engine.state !== 'idle' && engine.model) add(ICONS.power, 'פריקת מודל השיחה מהזיכרון', async () => { if (S.mode !== 'image') stopStreaming(); await api('unload', { method: 'POST' }); pollState(); }, 'danger');
  if (ie.state !== 'idle' && ie.model) add(ICONS.power, 'פריקת מודל התמונות מהזיכרון', async () => { if (S.mode === 'image') stopStreaming(); await api('image-unload', { method: 'POST' }); pollState(); }, 'danger');
  menu.classList.remove('hidden');
}

function closeMenus() {
  $('#modelMenu').classList.add('hidden');
  document.querySelectorAll('.ctx-menu').forEach(m => m.remove());
}

/* ================= chats ================= */
async function refreshChats() {
  try { chats = await api('chats') || []; } catch { chats = []; }
  renderChatList();
}

function groupOf(ts) {
  const d = new Date(ts), now = new Date();
  const day = 864e5, start = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
  if (ts >= start) return 'היום';
  if (ts >= start - day) return 'אתמול';
  if (ts >= start - 7 * day) return '7 הימים האחרונים';
  if (ts >= start - 30 * day) return '30 הימים האחרונים';
  return d.toLocaleDateString('he-IL', { month: 'long', year: 'numeric' });
}

function renderChatList() {
  const list = $('#chatList');
  const q = $('#search').value.trim().toLowerCase();
  list.innerHTML = '';
  const items = chats.filter(c => !q || (c.title || '').toLowerCase().includes(q));
  if (!items.length) { list.append(el('div', 'empty-list', q ? 'לא נמצאו שיחות' : 'השיחות שלכם יופיעו כאן')); return; }
  const pinned = items.filter(c => c.pinned);
  const rest = items.filter(c => !c.pinned);
  const groups = [];
  if (pinned.length) groups.push(['מוצמדות', pinned]);
  for (const c of rest) {
    const g = groupOf(c.updated);
    let grp = groups.find(x => x[0] === g);
    if (!grp) groups.push(grp = [g, []]);
    grp[1].push(c);
  }
  for (const [title, arr] of groups) {
    list.append(el('div', 'group-title', esc(title)));
    for (const c of arr) {
      const it = el('div', 'chat-item' + (chat && chat.id === c.id ? ' active' : ''));
      it.innerHTML = `<span class="t" dir="auto">${esc(c.title || 'שיחה חדשה')}</span><button class="icon-btn more" title="אפשרויות">${ICONS.dots}</button>`;
      it.onclick = e => { if (!e.target.closest('.more')) openChat(c.id); };
      $('.more', it).onclick = e => { e.stopPropagation(); chatMenu(c, it); };
      it.ondblclick = () => renameChat(c, it);
      list.append(it);
    }
  }
}

function chatMenu(c, item) {
  closeMenus();
  const m = el('div', 'menu ctx-menu');
  m.style.position = 'fixed';
  const r = item.getBoundingClientRect();
  m.style.top = (r.bottom + 4) + 'px';
  m.style.left = Math.max(8, r.left) + 'px';
  m.style.minWidth = '180px';
  const add = (icon, text, fn, cls) => { const b = el('button', 'menu-item ' + (cls || ''), icon + '<span class="mi-main">' + text + '</span>'); b.onclick = e => { e.stopPropagation(); closeMenus(); fn(); }; m.append(b); };
  add(ICONS.edit, 'שינוי שם', () => renameChat(c, item));
  add(ICONS.pin, c.pinned ? 'ביטול הצמדה' : 'הצמדה', () => updateChatMeta(c.id, { pinned: !c.pinned }));
  add(ICONS.trash, 'מחיקה', () => deleteChat(c), 'danger');
  document.body.append(m);
  const mr = m.getBoundingClientRect();
  if (mr.bottom > innerHeight - 8) m.style.top = (r.top - mr.height - 4) + 'px';
}

function renameChat(c, item) {
  const span = $('.t', item);
  const inp = el('input'); inp.value = c.title || ''; inp.dir = 'auto';
  span.replaceWith(inp); inp.focus(); inp.select();
  let done = false;
  const finish = ok => {
    if (done) return; done = true;
    const v = inp.value.trim();
    if (ok && v && v !== c.title) updateChatMeta(c.id, { title: v });
    else renderChatList();
  };
  inp.onkeydown = e => { if (e.key === 'Enter') finish(true); if (e.key === 'Escape') finish(false); };
  inp.onblur = () => finish(true);
  inp.onclick = e => e.stopPropagation();
}

async function updateChatMeta(id, patch) {
  let full = chat && chat.id === id ? chat : null;
  if (!full) { try { full = await api('chats/' + id); } catch { return; } }
  Object.assign(full, patch);
  await persist(full, false);
}

async function deleteChat(c) {
  if (!confirm(`למחוק את השיחה "${c.title}"?`)) return;
  await api('chats/' + c.id, { method: 'DELETE' });
  if (chat && chat.id === c.id) newChat();
  refreshChats();
}

function newChat() {
  stopStreaming();
  chat = { id: uid(), title: 'שיחה חדשה', created: Date.now(), updated: Date.now(), pinned: false, messages: [] };
  renderMessages(); renderChatList();
  $('#input').focus();
}

async function openChat(id) {
  if (chat && chat.id === id) return;
  stopStreaming();
  try { chat = await api('chats/' + id); } catch { toast('לא ניתן לפתוח את השיחה'); return; }
  chat.messages = chat.messages || [];
  renderMessages(true); renderChatList();
  if (innerWidth < 760) saveSettings({ sideHidden: true });
}

let saveChain = Promise.resolve();
function persist(c = chat, touch = true) {
  if (!c || !c.messages.length) return saveChain;
  if (touch) c.updated = Date.now();
  const snapshot = JSON.stringify(c);
  saveChain = saveChain.then(() => fetch('/api/chats/' + c.id, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: snapshot }))
    .then(() => refreshChats()).catch(() => toast('שמירת השיחה נכשלה'));
  return saveChain;
}

/* ================= rendering messages ================= */
function renderWelcome() {
  const w = $('#welcome');
  const show = !chat || !chat.messages.length;
  w.classList.toggle('hidden', !show);
  if (!show) return;
  if (!models.length) {
    w.innerHTML = `<img class="logo" src="/logo.png" alt=""><h1>ברוכים הבאים</h1><p>כדי להתחיל צריך מודל אחד לפחות.</p>
      <div class="setup"><b>איך מוסיפים מודל:</b><ol>
      <li>מורידים קובץ מודל בפורמט <code>.gguf</code> (למשל מ-Hugging Face).</li>
      <li>שמים אותו בתיקייה <code>מודלים</code> שליד התוכנה.</li>
      <li>לוחצים "רענון" ובוחרים את המודל בתפריט למעלה.</li></ol>
      <div class="row"><button class="btn primary" id="wOpen">פתיחת תיקיית המודלים</button><button class="btn" id="wRefresh">רענון</button></div></div>`;
    $('#wOpen').onclick = () => api('open-models', { method: 'POST' });
    $('#wRefresh').onclick = async () => { await refreshModels(); renderEngine(); if (models.length) toggleModelMenu(); };
    return;
  }
  const imgMode = S.mode === 'image';
  const e = imgMode ? imgEngine() : engine;
  let sub = imgMode ? 'תארו תמונה ואצור אותה.' : 'במה אפשר לעזור היום?';
  if (e.state === 'idle' || !e.model) sub = imgMode ? 'בחרו מודל תמונות בתפריט למעלה.' : 'בחרו מודל בתפריט למעלה כדי להתחיל.';
  else if (e.state === 'loading') sub = 'המודל נטען לזיכרון… אפשר כבר לכתוב.';
  else if (e.state === 'error') sub = 'טעינת המודל נכשלה.';
  const sugg = imgMode ? [
    ['חתול ג\'ינג\'י', 'ישן על אדן חלון בשקיעה, צילום'],
    ['נוף הררי', 'עם אגם ושלג, ציור שמן'],
    ['בית קפה', 'ברחוב ירושלמי עתיק, אור בוקר'],
    ['רובוט חמוד', 'משחק בכדור, איור תלת-ממדי'],
  ] : [
    ['הסבר לי', 'איך עובד מודל שפה, בפשטות'],
    ['כתוב לי', 'מכתב תודה קצר למורה'],
    ['עזור לי בקוד', 'פונקציית פייתון שממיינת רשימה של מילונים'],
    ['תרגם', 'את המשפט "Knowledge is power" לעברית'],
  ];
  w.innerHTML = `<img class="logo" src="/logo.png" alt=""><h1>${e.state === 'ready' ? (imgMode ? 'יצירת תמונות 🎨' : 'שלום! 👋') : 'צ\'אט מקומי'}</h1><p>${esc(sub)}</p>` +
    (e.state === 'error' ? `<div class="err-box" style="text-align:start;margin-bottom:16px">${esc(e.error || '')}</div>` : '') +
    (e.state !== 'idle' && e.model ? '<div class="suggestions">' + sugg.map(s => `<button class="sugg"><b>${esc(s[0])}</b><span>${esc(s[1])}</span></button>`).join('') + '</div>' : '');
  w.querySelectorAll('.sugg').forEach((b, i) => b.onclick = () => { $('#input').value = sugg[i][0] + ' ' + sugg[i][1]; autosize(); send(); });
}

function renderMessages(scrollEnd) {
  const box = $('#messages');
  box.innerHTML = '';
  if (chat) chat.messages.forEach((m, i) => box.append(buildMsg(m, i)));
  renderWelcome();
  markLast();
  if (scrollEnd !== false) scrollToEnd(true);
}

function markLast() {
  const all = $('#messages').children;
  [...all].forEach((n, i) => n.classList.toggle('last', i === all.length - 1));
}

function buildMsg(m, idx) {
  if (m.role === 'user') {
    const w = el('div', 'msg user');
    if (m.images && m.images.length) {
      const im = el('div', 'imgs');
      m.images.forEach(src => { const i = el('img'); i.src = src; im.append(i); });
      w.append(im);
    }
    const b = el('div', 'bubble'); b.dir = 'auto'; b.textContent = m.content; w.append(b);
    const acts = el('div', 'msg-actions');
    acts.append(actBtn(ICONS.copy, 'העתקה', btn => copyText(m.content, btn)));
    acts.append(actBtn(ICONS.edit, 'עריכה', () => editMessage(idx, w)));
    w.append(acts);
    return w;
  }
  const w = el('div', 'msg assistant');
  w.append(el('div', 'avatar', m.kind === 'image' ? ICONS.image : ICONS.spark));
  const body = el('div', 'body');
  w.append(body);
  if (m.kind === 'image') fillImage(body, m, false); else fillAssistant(body, m, false);
  return w;
}

function actBtn(icon, title, fn) {
  const b = el('button', 'icon-btn', icon); b.title = title;
  b.onclick = () => fn(b);
  return b;
}

function fillAssistant(body, m, live) {
  body.innerHTML = '';
  const { think, text, open } = splitThink(m.content, m.reasoning);
  if (think && think.trim()) {
    const d = el('details', 'think');
    const thinking = live && (!text || open);
    d.innerHTML = `<summary>${thinking ? 'חושב…' : 'מחשבות'}</summary><div class="think-body" dir="auto"></div>`;
    $('.think-body', d).textContent = think.trim();
    if (thinking) d.open = true;
    body.append(d);
  }
  const md = el('div', 'md');
  body.append(md);
  if (m.error) {
    if (text) renderMarkdown(md, text, false);
    body.append(el('div', 'err-box', esc(m.error)));
  } else if (live && !text && !(think && think.trim())) {
    md.innerHTML = '<span class="typing"><i></i><i></i><i></i></span>';
  } else renderMarkdown(md, text, live && !!text);

  if (!live) {
    const acts = el('div', 'msg-actions');
    acts.append(actBtn(ICONS.copy, 'העתקה', btn => copyText(text, btn)));
    const idx = chat.messages.indexOf(m);
    if (idx === chat.messages.length - 1) acts.append(actBtn(ICONS.retry, 'תשובה חדשה', () => regenerate()));
    if (m.stats) acts.append(el('span', 'stats', esc(m.stats)));
    body.append(acts);
  }
}

function fillImage(body, m, live) {
  body.innerHTML = '';
  if (m.images && m.images.length) {
    const g = el('div', 'gen-images' + (m.images.length === 1 ? ' one' : ''));
    m.images.forEach(u => { const i = el('img'); i.src = u; i.loading = 'lazy'; i.onclick = () => openLightbox(u); g.append(i); });
    body.append(g);
  } else if (live) {
    const wbox = el('div', 'gen-wait');
    const [w, h] = (m.size || '512x512').split('x').map(Number);
    wbox.style.aspectRatio = w + ' / ' + h;
    if (m.preview) { const i = el('img'); i.src = m.preview; wbox.append(i); }
    wbox.append(el('div', 'shimmer'), el('div', 'gen-status', esc(m.status || 'מתחיל…')));
    body.append(wbox);
  }
  if (m.error) body.append(el('div', 'err-box', esc(m.error)));
  if (m.prompt) body.append(el('div', 'gen-prompt', esc(m.prompt)));
  if (!live) {
    const acts = el('div', 'msg-actions');
    (m.images || []).forEach((u, i) => {
      const a = el('a', 'icon-btn', ICONS.download); a.href = u; a.download = 'image-' + (i + 1) + '.png'; a.title = 'הורדה';
      acts.append(a);
    });
    if (m.prompt) acts.append(actBtn(ICONS.copy, 'העתקת התיאור', btn => copyText(m.prompt, btn)));
    const idx = chat.messages.indexOf(m);
    if (idx === chat.messages.length - 1) acts.append(actBtn(ICONS.retry, 'תמונה חדשה', () => regenerate()));
    if (m.stats) acts.append(el('span', 'stats', esc(m.stats)));
    body.append(acts);
  }
}

function openLightbox(src) {
  const lb = $('#lightbox'); $('img', lb).src = src; lb.classList.remove('hidden');
}

async function copyText(text, btn) {
  try { await navigator.clipboard.writeText(text || ''); }
  catch {
    const t = el('textarea'); t.value = text || ''; document.body.append(t); t.select(); document.execCommand('copy'); t.remove();
  }
  if (btn) { const old = btn.innerHTML; btn.innerHTML = ICONS.check; setTimeout(() => btn.innerHTML = old, 1400); }
}

function editMessage(idx, w) {
  if (streaming) return;
  const m = chat.messages[idx];
  const box = el('div', 'edit-box');
  box.innerHTML = '<textarea dir="auto"></textarea><div class="row"><button class="btn primary">שליחה</button><button class="btn">ביטול</button></div>';
  const ta = $('textarea', box); ta.value = m.content;
  const [ok, cancel] = box.querySelectorAll('button');
  w.querySelector('.bubble').replaceWith(box);
  w.querySelector('.msg-actions').classList.add('hidden');
  ta.focus(); ta.setSelectionRange(ta.value.length, ta.value.length);
  cancel.onclick = () => renderMessages(false);
  ok.onclick = () => {
    const v = ta.value.trim(); if (!v) return;
    chat.messages = chat.messages.slice(0, idx);
    chat.messages.push({ role: 'user', content: v, images: m.images, kind: m.kind });
    renderMessages(false);
    m.kind === 'image' ? generateImage() : generate();
  };
  ta.onkeydown = e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); ok.click(); } if (e.key === 'Escape') cancel.click(); };
}

/* ================= scrolling ================= */
const scroller = () => $('#scroller');
let stick = true;
function nearBottom() { const s = scroller(); return s.scrollHeight - s.scrollTop - s.clientHeight < 80; }
function scrollToEnd(force) {
  if (!force && !stick) return;
  const s = scroller(); s.scrollTop = s.scrollHeight;
}

/* ================= sending ================= */
function updateSendBtn() {
  const b = $('#sendBtn');
  if (streaming) { b.classList.add('stop'); b.disabled = false; b.title = 'עצירה (Esc)'; return; }
  b.classList.remove('stop'); b.title = 'שליחה';
  b.disabled = !($('#input').value.trim() || pendingImages.length);
}

function autosize() {
  const t = $('#input');
  t.style.height = 'auto';
  t.style.height = Math.min(t.scrollHeight, innerHeight * 0.4) + 'px';
  updateSendBtn();
}

async function send() {
  if (streaming) return;
  const text = $('#input').value.trim();
  if (!text && !pendingImages.length) return;
  if (S.mode === 'image') return sendImage(text);
  if (!engine.model || engine.state === 'idle') { toast('קודם בוחרים מודל'); toggleModelMenu(); return; }
  if (engine.state === 'error') { toast('המודל לא נטען. בחרו מודל אחר או טענו מחדש.'); return; }
  if (!chat) newChat();
  const msg = { role: 'user', content: text };
  if (pendingImages.length) msg.images = pendingImages.slice();
  chat.messages.push(msg);
  if (chat.messages.length === 1 || chat.title === 'שיחה חדשה') chat.title = makeTitle(text);
  $('#input').value = ''; pendingImages = []; renderAttachments(); autosize();
  renderMessages();
  persist();
  generate();
}

function sendImage(text) {
  const ie = imgEngine();
  if (!text) return;
  if (!ie.model || ie.state === 'idle') { toast('קודם בוחרים מודל תמונות'); toggleModelMenu(); return; }
  if (ie.state === 'error') { toast('מודל התמונות לא נטען. בחרו מודל אחר.'); return; }
  if (!chat) newChat();
  chat.messages.push({ role: 'user', content: text, kind: 'image' });
  if (chat.messages.length === 1 || chat.title === 'שיחה חדשה') chat.title = '🖼 ' + makeTitle(text);
  $('#input').value = ''; autosize();
  renderMessages();
  persist();
  generateImage();
}

function makeTitle(t) {
  t = t.replace(/\s+/g, ' ').trim();
  return t.length > 48 ? t.slice(0, 46) + '…' : (t || 'שיחה עם תמונה');
}

function regenerate() {
  if (streaming) return;
  const last = chat.messages[chat.messages.length - 1];
  if (last && last.role === 'assistant') chat.messages.pop();
  renderMessages(false);
  const u = chat.messages[chat.messages.length - 1];
  u && u.kind === 'image' ? generateImage() : generate();
}

function estimateTokens(m) {
  // Hebrew tokenizes poorly in most models: be conservative.
  const c = typeof m.content === 'string' ? m.content : '';
  return Math.ceil(c.length / 2.6) + 8 + (m.images ? m.images.length * 700 : 0);
}

function buildApiMessages() {
  const reserve = S.max_tokens > 0 ? Math.min(S.max_tokens, engine.ctx / 2) : Math.min(2048, engine.ctx / 3);
  let budget = (engine.ctx || S.ctx) - reserve;
  const out = [];
  const src = chat.messages.filter(m => m.kind !== 'image' && !(m.role === 'assistant' && (m.error && !m.content)));
  for (let i = src.length - 1; i >= 0; i--) {
    const m = src[i];
    const cost = estimateTokens(m);
    if (cost > budget && out.length) break;
    budget -= cost;
    let content = m.role === 'assistant' ? splitThink(m.content, m.reasoning).text : m.content;
    if (m.images && m.images.length && engine.vision) {
      content = [{ type: 'text', text: m.content || '' }, ...m.images.map(u => ({ type: 'image_url', image_url: { url: u } }))];
    }
    out.unshift({ role: m.role, content });
  }
  while (out.length && out[0].role === 'assistant') out.shift();
  if (S.system) out.unshift({ role: 'system', content: S.system });
  return out;
}

async function waitReady() {
  if (engine.state === 'ready') return true;
  while (engine.state === 'loading') {
    await new Promise(r => setTimeout(r, 600));
    try { engine = await api('state'); } catch { }
  }
  renderEngine();
  return engine.state === 'ready';
}

async function generate() {
  const target = chat;
  const msg = { role: 'assistant', content: '', reasoning: '' };
  target.messages.push(msg);
  const ctrl = new AbortController();
  streaming = { ctrl, msg };
  updateSendBtn();
  const box = $('#messages');
  const node = buildMsg(msg, target.messages.length - 1);
  box.append(node); markLast();
  const body = $('.body', node);
  fillAssistant(body, msg, true);
  stick = true; scrollToEnd(true);

  let raf = 0;
  const paint = () => { raf = 0; if (chat === target) { fillAssistant(body, msg, true); scrollToEnd(); } };
  const schedule = () => { if (!raf) raf = requestAnimationFrame(paint); };
  const t0 = performance.now();
  let timings = null, usage = null;

  try {
    if (!(await waitReady())) throw new Error(engine.error || 'המודל אינו מוכן.');
    if (ctrl.signal.aborted) throw new DOMException('', 'AbortError');
    const payload = {
      messages: buildApiMessages(),
      stream: true,
      temperature: S.temperature,
      top_p: S.top_p,
      repeat_penalty: S.repeat_penalty,
      cache_prompt: true,
      stream_options: { include_usage: true },
    };
    if (S.max_tokens > 0) payload.max_tokens = S.max_tokens;
    const r = await fetch(`http://127.0.0.1:${engine.port}/v1/chat/completions`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload), signal: ctrl.signal,
    });
    if (!r.ok) {
      let t = await r.text(), m = t;
      try { const j = JSON.parse(t); m = (j.error && (j.error.message || j.error)) || t; } catch { }
      if (/context|exceed/i.test(m)) m = 'השיחה ארוכה מדי לגודל ההקשר של המודל. פתחו שיחה חדשה או הגדילו את "גודל ההקשר" בהגדרות.\n\n' + m;
      throw new Error(m);
    }
    const reader = r.body.getReader();
    const dec = new TextDecoder();
    let buf = '';
    for (; ;) {
      const { done, value } = await reader.read();
      if (done) break;
      buf += dec.decode(value, { stream: true });
      let nl;
      while ((nl = buf.indexOf('\n')) >= 0) {
        const line = buf.slice(0, nl).trim(); buf = buf.slice(nl + 1);
        if (!line.startsWith('data:')) continue;
        const data = line.slice(5).trim();
        if (data === '[DONE]') continue;
        let j; try { j = JSON.parse(data); } catch { continue; }
        if (j.error) throw new Error(j.error.message || JSON.stringify(j.error));
        const d = j.choices && j.choices[0] && j.choices[0].delta;
        if (d) {
          if (d.reasoning_content) msg.reasoning += d.reasoning_content;
          if (d.content) msg.content += d.content;
          schedule();
        }
        if (j.timings) timings = j.timings;
        if (j.usage) usage = j.usage;
      }
    }
  } catch (e) {
    if (e.name !== 'AbortError') {
      msg.error = e.message === 'Failed to fetch' ? 'אין חיבור למנוע. ייתכן שהמודל נסגר בגלל חוסר בזיכרון.' : e.message;
    }
  }
  if (raf) cancelAnimationFrame(raf);
  if (timings && timings.predicted_per_second) {
    msg.stats = `${timings.predicted_per_second.toFixed(1)} טוקנים/שנ' · ${timings.predicted_n} טוקנים`;
  } else if (usage && usage.completion_tokens) {
    msg.stats = `${usage.completion_tokens} טוקנים · ${((performance.now() - t0) / 1000).toFixed(1)} שנ'`;
  }
  if (!msg.reasoning) delete msg.reasoning;
  if (!msg.content && !msg.reasoning && !msg.error) msg.error = 'התשובה נעצרה.';
  streaming = null;
  updateSendBtn();
  if (chat === target) { fillAssistant(body, msg, false); markLast(); scrollToEnd(); }
  persist(target);
}

function stopStreaming() {
  if (!streaming) return;
  streaming.ctrl.abort();
  if (streaming.jobId) api('img/cancel/' + streaming.jobId, { method: 'POST' }).catch(() => { });
}

const HEB = /[֐-׿]/;

/** Image models understand English: let the chat model rewrite a Hebrew request as an English prompt. */
async function toEnglishPrompt(text, signal) {
  if (!HEB.test(text) || !S.translate || engine.state !== 'ready') return text;
  const r = await fetch(`http://127.0.0.1:${engine.port}/v1/chat/completions`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, signal,
    body: JSON.stringify({
      messages: [
        { role: 'system', content: 'You turn requests into prompts for an image generation model. Reply with ONE short English prompt only: the subject, setting, style and lighting as comma-separated phrases. No quotes, no explanations.' },
        { role: 'user', content: text },
      ],
      temperature: 0.2, max_tokens: 120, stream: false, reasoning_format: 'none', chat_template_kwargs: { enable_thinking: false },
    }),
  });
  if (!r.ok) return text;
  const j = await r.json();
  let out = ((j.choices && j.choices[0].message && j.choices[0].message.content) || '').replace(/<think>[\s\S]*?<\/think>/g, '').trim();
  out = out.split('\n').filter(Boolean).pop() || '';
  out = out.replace(/^(prompt\s*:)/i, '').replace(/^["'\s]+|["'\s]+$/g, '');
  return out && !HEB.test(out) ? out : text;
}

function imageParams() {
  const ie = imgEngine();
  const info = imageModels.find(x => x.file === ie.model) || {};
  let size = S.imgSize;
  if (size === 'auto') size = info.xl ? '1024x1024' : '512x512';
  const [width, height] = size.split('x').map(Number);
  const steps = +S.imgSteps || (info.fast ? 4 : 20);
  const cfg = +S.cfg || (info.fast ? 1 : 7);
  return { width, height, steps, cfg, size, count: +S.imgCount || 1 };
}

async function generateImage() {
  const target = chat;
  const userMsg = target.messages[target.messages.length - 1];
  const p = imageParams();
  const msg = { role: 'assistant', kind: 'image', size: p.size, status: 'מכין…', images: [] };
  target.messages.push(msg);
  const ctrl = new AbortController();
  streaming = { ctrl, msg };
  updateSendBtn();
  const node = buildMsg(msg, target.messages.length - 1);
  $('#messages').append(node); markLast();
  const body = $('.body', node);
  const paint = () => { if (chat === target) { fillImage(body, msg, true); scrollToEnd(); } };
  paint();
  stick = true; scrollToEnd(true);
  const t0 = performance.now();

  try {
    if (HEB.test(userMsg.content) && S.translate && engine.state === 'ready') { msg.status = 'מתרגם את התיאור…'; paint(); }
    const prompt = await toEnglishPrompt(userMsg.content, ctrl.signal);
    msg.prompt = prompt;
    if (HEB.test(prompt)) msg.note = true;
    msg.status = 'מתחיל…'; paint();
    const job = await api('img/generate', {
      method: 'POST', body: {
        prompt, negative_prompt: S.negative || '',
        width: p.width, height: p.height, seed: -1, batch_count: p.count,
        sample_params: { sample_steps: p.steps, guidance: { txt_cfg: p.cfg } },
        vae_tiling_params: { enabled: p.width * p.height > 768 * 768 },
        preview: 'proj', preview_interval: 1,
        output_format: 'png',
      },
    });
    if (!job || !job.id) throw new Error((job && job.error && (job.error.message || job.error)) || 'שליחת הבקשה נכשלה');
    streaming.jobId = job.id;
    for (; ;) {
      if (ctrl.signal.aborted) throw new DOMException('', 'AbortError');
      await new Promise(r => setTimeout(r, 650));
      const j = await api('img/job/' + job.id);
      if (j.status === 'completed') { msg.images = j.images || []; break; }
      if (j.status === 'failed') throw new Error((j.error && (j.error.message || j.error)) || 'היצירה נכשלה');
      if (j.status === 'cancelled') throw new DOMException('', 'AbortError');
      if (j.status === 'queued') msg.status = 'בתור…';
      else if (j.preview && j.preview.total_steps) {
        msg.status = `צעד ${j.preview.step} מתוך ${j.preview.total_steps}` + (p.count > 1 ? ` · תמונה ${j.preview.pass}/${p.count}` : '');
        if (j.preview.b64_json) msg.preview = 'data:image/png;base64,' + j.preview.b64_json;
      } else msg.status = 'יוצר…';
      paint();
    }
    msg.stats = `${((performance.now() - t0) / 1000).toFixed(0)} שנ' · ${p.size} · ${p.steps} צעדים`;
  } catch (e) {
    msg.error = e.name === 'AbortError' ? 'היצירה בוטלה.' : (e.message === 'Failed to fetch' ? 'אין חיבור למנוע התמונות.' : e.message);
  }
  if (msg.note && !msg.error) msg.error = 'שימו לב: מודלי תמונות מבינים בעיקר אנגלית. טענו מודל שיחה כדי שהתיאור יתורגם אוטומטית.';
  delete msg.preview; delete msg.status; delete msg.note;
  streaming = null;
  updateSendBtn();
  if (chat === target) { fillImage(body, msg, false); markLast(); scrollToEnd(); }
  persist(target);
}

/* ================= attachments ================= */
function renderAttachments() {
  const box = $('#attachments'); box.innerHTML = '';
  pendingImages.forEach((src, i) => {
    const a = el('div', 'att'); const img = el('img'); img.src = src;
    const x = el('button', '', ICONS.x); x.title = 'הסרה';
    x.onclick = () => { pendingImages.splice(i, 1); renderAttachments(); updateSendBtn(); };
    a.append(img, x); box.append(a);
  });
}

function addImageFile(file) {
  if (!file || !file.type.startsWith('image/')) return;
  if (!(engine.state === 'ready' && engine.vision)) { toast('המודל הנוכחי לא תומך בתמונות'); return; }
  const img = new Image();
  const url = URL.createObjectURL(file);
  img.onload = () => {
    // downscale big photos: faster and fits the context
    const max = 1024, k = Math.min(1, max / Math.max(img.width, img.height));
    const c = document.createElement('canvas'); c.width = Math.round(img.width * k); c.height = Math.round(img.height * k);
    c.getContext('2d').drawImage(img, 0, 0, c.width, c.height);
    pendingImages.push(c.toDataURL('image/jpeg', 0.88));
    URL.revokeObjectURL(url); renderAttachments(); updateSendBtn();
  };
  img.src = url;
}

/* ================= wiring ================= */
function wire() {
  const input = $('#input');
  input.addEventListener('input', autosize);
  input.addEventListener('keydown', e => {
    if (e.key !== 'Enter' || e.isComposing) return;
    const sendIt = S.enterSends ? !e.shiftKey : (e.ctrlKey || e.metaKey);
    if (sendIt) { e.preventDefault(); send(); }
  });
  input.addEventListener('paste', e => {
    const files = [...(e.clipboardData && e.clipboardData.files || [])].filter(f => f.type.startsWith('image/'));
    if (files.length) { e.preventDefault(); files.forEach(addImageFile); }
  });
  $('#composer').addEventListener('dragover', e => e.preventDefault());
  $('#composer').addEventListener('drop', e => { e.preventDefault(); [...e.dataTransfer.files].forEach(addImageFile); });
  $('#sendBtn').onclick = () => streaming ? stopStreaming() : send();
  $('#attachBtn').onclick = () => $('#fileInput').click();
  $('#fileInput').onchange = e => { [...e.target.files].forEach(addImageFile); e.target.value = ''; };

  $('#newChat').onclick = newChat;
  $('#modeBtn').onclick = async () => {
    await saveSettings({ mode: S.mode === 'image' ? 'chat' : 'image' });
    renderEngine(); $('#input').focus();
    if (S.mode === 'image' && imgEngine().state === 'idle') toggleModelMenu();
  };
  $('#imgSize').onchange = e => saveSettings({ imgSize: e.target.value });
  $('#imgSteps').onchange = e => saveSettings({ imgSteps: +e.target.value });
  $('#imgCount').onchange = e => saveSettings({ imgCount: +e.target.value });
  $('#lightbox').onclick = () => $('#lightbox').classList.add('hidden');
  $('#showImgLog').onclick = async () => {
    let t = ''; try { t = await (await fetch('/api/image-log')).text(); } catch { }
    showModal('log', `<pre class="log">${esc(t || 'היומן ריק.')}</pre>`);
    const p = $('pre.log'); p.scrollTop = p.scrollHeight;
  };
  $('#search').oninput = renderChatList;
  $('#collapseSide').onclick = () => saveSettings({ sideHidden: true });
  $('#expandSide').onclick = () => saveSettings({ sideHidden: false });
  $('#themeBtn').onclick = () => saveSettings({ theme: S.theme === 'light' ? 'dark' : 'light' });
  $('#modelBtn').onclick = e => { e.stopPropagation(); toggleModelMenu(); };
  $('#openSettings').onclick = openSettings;
  $('#saveSettings').onclick = submitSettings;
  $('#reloadModel').onclick = () => { closeModal(); if (engine.model) loadModel(engine.model); else toast('אין מודל טעון'); };
  $('#showLog').onclick = async () => {
    let t = ''; try { t = await (await fetch('/api/log')).text(); } catch { }
    showModal('log', `<pre class="log">${esc(t || 'היומן ריק.')}</pre>`);
    const p = $('pre.log'); p.scrollTop = p.scrollHeight;
  };
  $('#modal').addEventListener('click', e => { if (e.target.id === 'modal' || e.target.closest('[data-close]')) closeModal(); });
  bindSettingsInputs();

  document.addEventListener('click', e => {
    if (!e.target.closest('.menu') && !e.target.closest('#modelBtn')) closeMenus();
    const cb = e.target.closest('.copy-code');
    if (cb) {
      const code = cb.closest('.codeblock').querySelector('code').innerText;
      copyText(code).then(() => { const s = $('span', cb); s.textContent = 'הועתק'; setTimeout(() => s.textContent = 'העתקה', 1400); });
    }
  });
  document.addEventListener('keydown', e => {
    if (e.key === 'Escape') {
      if (!$('#lightbox').classList.contains('hidden')) $('#lightbox').classList.add('hidden');
      else if (!$('#modal').classList.contains('hidden')) closeModal();
      else if (streaming) stopStreaming();
      else closeMenus();
    }
    if ((e.ctrlKey || e.metaKey) && e.shiftKey && e.key.toLowerCase() === 'o') { e.preventDefault(); newChat(); }
  });
  scroller().addEventListener('scroll', () => {
    stick = nearBottom();
    $('#toBottom').classList.toggle('hidden', stick);
  });
  $('#toBottom').onclick = () => { stick = true; scroller().scrollTo({ top: scroller().scrollHeight, behavior: 'smooth' }); };
  window.addEventListener('resize', autosize);
}

async function init() {
  wire();
  try { Object.assign(S, await api('settings') || {}); } catch { }
  applySettings();
  await refreshModels();
  newChat();
  await Promise.all([refreshChats(), pollState()]);
}

init();
