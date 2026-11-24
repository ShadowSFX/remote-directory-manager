// RDMan terminal bridge: xterm.js ⇄ WebView2 host (TerminalHost.cs).
// Host → page: strings starting with "o" are terminal output; anything else is a JSON command.
// Page → host: JSON objects { t: "...", ... } through chrome.webview.postMessage.
(() => {
  'use strict';

  const host = window.chrome && window.chrome.webview;
  const post = (msg) => { if (host) host.postMessage(msg); };
  const $ = (id) => document.getElementById(id);

  let term = null;
  let fit = null;
  let webgl = null;
  let search = null;
  let serialize = null;
  let options = {};
  let strings = {};
  let renderer = 'gpu';
  let smartCtrlC = true;
  let copyOnSelect = false;
  let rightClick = 'menu';
  let bell = 'visual';
  let baseFontSize = 13;
  let zoom = 0;
  let themeContrast = 1;
  let resizeTimer = 0;
  let lastSize = '';
  let pendingAck = 0;
  let ackTimer = 0;
  const prompts = []; // { marker, exit }

  const T = (key, fallback) => strings[key] || fallback || key;

  // ───────────── Output (with flow-control acks) ─────────────
  function write(text) {
    if (!term) return;
    term.write(text, () => {
      pendingAck += text.length;
      if (!ackTimer) ackTimer = setTimeout(flushAck, 8);
    });
  }

  function flushAck() {
    ackTimer = 0;
    if (pendingAck > 0) {
      post({ t: 'ack', n: pendingAck });
      pendingAck = 0;
    }
  }

  // ───────────── Setup ─────────────
  async function init(msg) {
    options = msg.options || {};
    strings = msg.strings || {};
    applyTheme(msg.theme);
    applyUiStrings();

    const family = options.fontFamily || 'Cascadia Code NF';
    try {
      await document.fonts.load(`${options.fontSize || 13}px "Cascadia Code NF"`);
      await document.fonts.load(`italic ${options.fontSize || 13}px "Cascadia Code NF"`);
    } catch { /* fall back to whatever is available */ }

    baseFontSize = options.fontSize || 13;
    term = new Terminal({
      allowProposedApi: true,
      fontFamily: `"${family}", "Cascadia Code NF", "Cascadia Mono", Consolas, monospace`,
      fontSize: baseFontSize,
      lineHeight: options.lineHeight || 1.15,
      letterSpacing: options.letterSpacing || 0,
      fontWeight: options.fontWeight || 400,
      fontWeightBold: options.fontWeightBold || 700,
      cursorStyle: options.cursor || 'bar',
      cursorBlink: options.cursorBlink !== false,
      cursorInactiveStyle: 'outline',
      scrollback: options.scrollback ?? 10000,
      minimumContrastRatio: Math.max(options.minimumContrast || 1, (msg.theme && msg.theme.minimumContrast) || 1),
      wordSeparator: options.wordSeparators,
      customGlyphs: true,
      rescaleOverlappingGlyphs: true,
      drawBoldTextInBrightColors: false,
      macOptionIsMeta: false,
      rightClickSelectsWord: false,
      smoothScrollDuration: 0,
      overviewRuler: { width: 8 },
      theme: msg.theme && msg.theme.xterm,
      linkHandler: {
        activate: (e, uri) => { if (e.ctrlKey) post({ t: 'link', url: uri }); },
        allowNonHttpProtocols: false,
      },
    });

    fit = new FitAddon.FitAddon();
    term.loadAddon(fit);
    search = new SearchAddon.SearchAddon({ highlightLimit: 2000 });
    term.loadAddon(search);
    serialize = new SerializeAddon.SerializeAddon();
    term.loadAddon(serialize);
    term.loadAddon(new WebLinksAddon.WebLinksAddon((e, uri) => { if (e.ctrlKey) post({ t: 'link', url: uri }); }));
    const unicode = new Unicode11Addon.Unicode11Addon();
    term.loadAddon(unicode);
    term.unicode.activeVersion = '11';
    term.loadAddon(new ImageAddon.ImageAddon({ sixelSizeLimit: 25000000, storageLimit: 64 }));
    term.loadAddon(new ClipboardAddon.ClipboardAddon(undefined, {
      // OSC 52: programs may ask to set the clipboard (the host decides); reading it is never allowed.
      readText: () => '',
      writeText: (_sel, text) => { post({ t: 'osc52', text }); },
    }));
    const progress = new ProgressAddon.ProgressAddon();
    term.loadAddon(progress);
    progress.onChange((p) => post({ t: 'progress', state: p.state, value: p.value }));

    term.open($('term'));
    setRenderer(options.renderer || 'gpu');
    applyBehavior(options);

    term.onData((d) => post({ t: 'input', d }));
    term.onBinary((d) => post({ t: 'bin', d }));
    term.onTitleChange((v) => post({ t: 'title', v }));
    term.onBell(() => ring());
    term.onSelectionChange(() => {
      if (copyOnSelect && term.hasSelection()) post({ t: 'copy', text: term.getSelection() });
    });
    term.attachCustomKeyEventHandler(onKey);
    registerShellIntegration();

    search.onDidChangeResults((r) => {
      const c = $('search-count');
      if (!r || r.resultCount === 0) {
        c.textContent = $('search-input').value ? T('Term.Search.None', 'No results') : '';
        $('search-input').classList.toggle('nomatch', !!$('search-input').value);
      } else {
        c.textContent = `${r.resultIndex + 1} / ${r.resultCount}`;
        $('search-input').classList.remove('nomatch');
      }
    });

    new ResizeObserver(scheduleFit).observe($('term'));
    $('term').addEventListener('wheel', onWheel, { passive: false });
    $('term').addEventListener('contextmenu', onContextMenu);
    document.addEventListener('pointerdown', (e) => {
      if (!$('menu').contains(e.target)) hideMenu();
      post({ t: 'activate' });
    }, true);
    window.addEventListener('focus', () => post({ t: 'activate' }));
    wireSearch();

    doFit(true);
    term.focus();
    post({ t: 'ready', cols: term.cols, rows: term.rows });
  }

  function setRenderer(kind) {
    renderer = kind;
    if (kind === 'gpu' && !webgl) {
      try {
        webgl = new WebglAddon.WebglAddon();
        webgl.onContextLoss(() => {
          // GPU reset or driver update: drop to the DOM renderer instead of a blank screen.
          webgl.dispose();
          webgl = null;
          post({ t: 'webgl-lost' });
        });
        term.loadAddon(webgl);
      } catch {
        webgl = null;
        post({ t: 'webgl-lost' });
      }
    } else if (kind !== 'gpu' && webgl) {
      webgl.dispose();
      webgl = null;
    }
  }

  function applyBehavior(o) {
    smartCtrlC = o.smartCtrlC !== false;
    copyOnSelect = !!o.copyOnSelect;
    rightClick = o.rightClick || 'menu';
    bell = o.bell || 'visual';
  }

  function applyOptions(o) {
    options = Object.assign(options, o);
    if (!term) return;
    const family = options.fontFamily || 'Cascadia Code NF';
    baseFontSize = options.fontSize || 13;
    term.options.fontFamily = `"${family}", "Cascadia Code NF", "Cascadia Mono", Consolas, monospace`;
    term.options.fontSize = Math.max(6, Math.min(72, baseFontSize + zoom));
    term.options.lineHeight = options.lineHeight || 1.15;
    term.options.letterSpacing = options.letterSpacing || 0;
    term.options.fontWeight = options.fontWeight || 400;
    term.options.fontWeightBold = options.fontWeightBold || 700;
    term.options.cursorStyle = options.cursor || 'bar';
    term.options.cursorBlink = options.cursorBlink !== false;
    term.options.scrollback = options.scrollback ?? 10000;
    term.options.minimumContrastRatio = Math.max(options.minimumContrast || 1, themeContrast);
    if (options.wordSeparators !== undefined) term.options.wordSeparator = options.wordSeparators;
    setRenderer(options.renderer || 'gpu');
    applyBehavior(options);
    doFit(true);
  }

  // ───────────── Theme (terminal palette + RDMan UI tokens) ─────────────
  function applyTheme(theme) {
    if (!theme) return;
    const root = document.documentElement.style;
    for (const [k, v] of Object.entries(theme.css || {})) root.setProperty(k, v);
    themeContrast = theme.minimumContrast || 1;
    if (term && theme.xterm) {
      term.options.theme = theme.xterm;
      term.options.minimumContrastRatio = Math.max(options.minimumContrast || 1, themeContrast);
    }
  }

  function applyUiStrings() {
    $('search-input').placeholder = T('Term.Search.Placeholder', 'Find');
    $('search-case').title = T('Term.Search.Case', 'Match case');
    $('search-word').title = T('Term.Search.Word', 'Whole word');
    $('search-regex').title = T('Term.Search.Regex', 'Regular expression');
    $('search-prev').title = T('Term.Search.Prev', 'Previous (Shift+Enter)');
    $('search-next').title = T('Term.Search.Next', 'Next (Enter)');
    $('search-close').title = T('Term.Search.Close', 'Close (Esc)');
  }

  // ───────────── Size ─────────────
  function scheduleFit() {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(() => doFit(false), 80);
  }

  function doFit(force) {
    if (!term || !fit || document.hidden) return;
    const el = $('term');
    if (el.clientWidth < 20 || el.clientHeight < 10) return; // hidden or collapsed pane
    try { fit.fit(); } catch { return; }
    if (term.cols < 2 || term.rows < 1) return;
    const dims = term._core && term._core._renderService && term._core._renderService.dimensions;
    const cw = dims ? Math.round(dims.css.canvas.width) : 0;
    const ch = dims ? Math.round(dims.css.canvas.height) : 0;
    const key = `${term.cols}x${term.rows}`;
    if (force || key !== lastSize) {
      lastSize = key;
      post({ t: 'resize', cols: term.cols, rows: term.rows, w: cw, h: ch });
    }
  }

  document.addEventListener('visibilitychange', () => { if (!document.hidden) scheduleFit(); });

  // ───────────── Keyboard ─────────────
  function onKey(e) {
    if (e.type !== 'keydown') return true;
    const ctrl = e.ctrlKey, shift = e.shiftKey, alt = e.altKey;
    const k = e.key.length === 1 ? e.key.toLowerCase() : e.key;

    if (ctrl && shift && !alt && k === 'c') { copySelection(); return false; }
    if (ctrl && !shift && !alt && k === 'c' && smartCtrlC && term.hasSelection()) { copySelection(); term.clearSelection(); return false; }
    if ((ctrl && shift && !alt && k === 'v') || (shift && !ctrl && !alt && k === 'Insert')) { post({ t: 'paste-request' }); return false; }
    if (ctrl && !shift && !alt && k === 'Insert') { copySelection(); return false; }
    if (ctrl && shift && !alt && k === 'f') { openSearch(); return false; }
    if (ctrl && shift && !alt && k === 'a') { term.selectAll(); return false; }
    if (ctrl && !shift && !alt && (k === '=' || k === '+')) { setZoom(zoom + 1); return false; }
    if (ctrl && !shift && !alt && k === '-') { setZoom(zoom - 1); return false; }
    if (ctrl && !shift && !alt && k === '0') { setZoom(0); return false; }
    if (ctrl && shift && !alt && k === 'ArrowUp') { jumpPrompt(-1); return false; }
    if (ctrl && shift && !alt && k === 'ArrowDown') { jumpPrompt(1); return false; }
    if (shift && !ctrl && !alt && k === 'PageUp') { term.scrollPages(-1); return false; }
    if (shift && !ctrl && !alt && k === 'PageDown') { term.scrollPages(1); return false; }
    if (ctrl && shift && !alt && k === 'Home') { term.scrollToTop(); return false; }
    if (ctrl && shift && !alt && k === 'End') { term.scrollToBottom(); return false; }
    return true;
  }

  function copySelection() {
    if (term.hasSelection()) post({ t: 'copy', text: term.getSelection() });
  }

  function setZoom(z) {
    zoom = Math.max(-6, Math.min(24, z));
    term.options.fontSize = Math.max(6, Math.min(72, baseFontSize + zoom));
    doFit(false);
    post({ t: 'zoom', size: term.options.fontSize });
  }

  function onWheel(e) {
    if (!e.ctrlKey) return;
    e.preventDefault();
    setZoom(zoom + (e.deltaY < 0 ? 1 : -1));
  }

  // ───────────── Shell integration (OSC 133 marks, OSC 7 directory) ─────────────
  function registerShellIntegration() {
    term.parser.registerOscHandler(133, (data) => {
      const [kind, arg] = data.split(';');
      if (kind === 'A') {
        post({ t: 'prompt' });
        const marker = term.registerMarker(0);
        if (marker) {
          prompts.push({ marker, exit: null });
          if (prompts.length > 2000) prompts.shift().marker.dispose();
        }
      } else if (kind === 'D' && prompts.length > 0) {
        const last = prompts[prompts.length - 1];
        const code = parseInt(arg, 10);
        if (last.exit === null && !Number.isNaN(code)) {
          last.exit = code;
          if (code !== 0) {
            term.registerDecoration({
              marker: last.marker,
              overviewRulerOptions: { color: getComputedStyle(document.documentElement).getPropertyValue('--ui-danger').trim() || '#F0605D', position: 'full' },
            });
          }
        }
      }
      return true;
    });
    term.parser.registerOscHandler(7, (data) => {
      try {
        const url = new URL(data);
        if (url.protocol === 'file:') post({ t: 'cwd', v: decodeURIComponent(url.pathname) });
      } catch { /* ignore malformed */ }
      return true;
    });
  }

  function jumpPrompt(dir) {
    const live = prompts.filter((p) => !p.marker.isDisposed && p.marker.line >= 0);
    if (live.length === 0) return;
    const top = term.buffer.active.viewportY;
    let target = null;
    if (dir < 0) {
      for (let i = live.length - 1; i >= 0; i--) if (live[i].marker.line < top) { target = live[i]; break; }
    } else {
      for (const p of live) if (p.marker.line > top) { target = p; break; }
    }
    if (target) term.scrollToLine(target.marker.line);
    else if (dir > 0) term.scrollToBottom();
  }

  // ───────────── Bell ─────────────
  function ring() {
    post({ t: 'bell' });
    if (bell !== 'visual') return;
    const f = $('flash');
    f.classList.add('on');
    requestAnimationFrame(() => requestAnimationFrame(() => f.classList.remove('on')));
  }

  // ───────────── Search ─────────────
  const searchState = { caseSensitive: false, wholeWord: false, regex: false };

  function searchOptions() {
    const css = getComputedStyle(document.documentElement);
    const accent = css.getPropertyValue('--ui-accent').trim() || '#7480FF';
    const warn = css.getPropertyValue('--ui-warn').trim() || '#E0AE45';
    return {
      caseSensitive: searchState.caseSensitive,
      wholeWord: searchState.wholeWord,
      regex: searchState.regex,
      decorations: {
        matchBackground: accent + '55',
        matchOverviewRuler: accent,
        activeMatchBackground: warn + 'AA',
        activeMatchColorOverviewRuler: warn,
      },
    };
  }

  function openSearch() {
    $('search').hidden = false;
    const input = $('search-input');
    if (term.hasSelection() && !term.getSelection().includes('\n')) input.value = term.getSelection();
    input.focus();
    input.select();
    if (input.value) search.findNext(input.value, Object.assign(searchOptions(), { incremental: true }));
  }

  function closeSearch() {
    $('search').hidden = true;
    search.clearDecorations();
    $('search-count').textContent = '';
    term.focus();
  }

  function wireSearch() {
    const input = $('search-input');
    input.addEventListener('input', () => {
      if (!input.value) { search.clearDecorations(); $('search-count').textContent = ''; input.classList.remove('nomatch'); return; }
      try { search.findNext(input.value, Object.assign(searchOptions(), { incremental: true })); }
      catch { input.classList.add('nomatch'); }
    });
    input.addEventListener('keydown', (e) => {
      if (e.key === 'Escape') { e.preventDefault(); closeSearch(); }
      else if (e.key === 'Enter') {
        e.preventDefault();
        if (e.shiftKey) search.findPrevious(input.value, searchOptions());
        else search.findNext(input.value, searchOptions());
      }
    });
    const toggle = (id, key) => $(id).addEventListener('click', () => {
      searchState[key] = !searchState[key];
      $(id).classList.toggle('on', searchState[key]);
      input.dispatchEvent(new Event('input'));
      input.focus();
    });
    toggle('search-case', 'caseSensitive');
    toggle('search-word', 'wholeWord');
    toggle('search-regex', 'regex');
    $('search-prev').addEventListener('click', () => search.findPrevious(input.value, searchOptions()));
    $('search-next').addEventListener('click', () => search.findNext(input.value, searchOptions()));
    $('search-close').addEventListener('click', closeSearch);
  }

  // ───────────── Banner / card / menu ─────────────
  function showBanner(b) {
    const el = $('banner');
    if (!b) { el.hidden = true; return; }
    el.className = b.kind || 'info';
    $('banner-icon').textContent = b.icon || '';
    $('banner-icon').classList.toggle('spin', !!b.spin);
    $('banner-text').textContent = b.text || '';
    const actions = $('banner-actions');
    actions.replaceChildren(...(b.actions || []).map((a) => button(a, () => post({ t: 'action', id: a.id }))));
    el.hidden = false;
  }

  function button(a, onClick) {
    const btn = document.createElement('button');
    btn.className = 'btn ' + (a.style || 'link');
    btn.textContent = a.label;
    btn.addEventListener('click', onClick);
    return btn;
  }

  let cardKeyHandler = null;

  function showCard(c) {
    const scrim = $('scrim');
    if (cardKeyHandler) { document.removeEventListener('keydown', cardKeyHandler, true); cardKeyHandler = null; }
    if (!c) { scrim.hidden = true; term && term.focus(); return; }
    $('card').className = c.kind || '';
    $('card-icon').textContent = c.icon || '';
    $('card-title').textContent = c.title || '';
    $('card-body').textContent = c.body || '';

    const detail = $('card-detail');
    detail.replaceChildren();
    for (const [k, v] of c.rows || []) {
      const dt = document.createElement('dt'); dt.textContent = k;
      const dd = document.createElement('dd'); dd.textContent = v;
      detail.append(dt, dd);
    }
    detail.hidden = !(c.rows && c.rows.length);

    const pre = $('card-pre');
    pre.textContent = c.pre || '';
    pre.className = c.preKind || '';
    pre.hidden = !c.pre;

    // Inputs (passwords, one-time codes) and an optional checkbox; their values travel with the chosen action.
    const form = $('card-form');
    form.replaceChildren();
    const inputs = [];
    for (const f of c.fields || []) {
      const label = document.createElement('label');
      label.className = 'field';
      const span = document.createElement('span');
      span.textContent = f.label || '';
      const input = document.createElement('input');
      input.type = f.type === 'password' ? 'password' : 'text';
      input.value = f.value || '';
      input.spellcheck = false;
      input.autocomplete = 'off';
      input.dataset.id = f.id;
      label.append(span, input);
      form.append(label);
      inputs.push(input);
    }
    let check = null;
    if (c.check) {
      const label = document.createElement('label');
      label.className = 'check';
      check = document.createElement('input');
      check.type = 'checkbox';
      check.checked = !!c.check.checked;
      check.dataset.id = c.check.id;
      const span = document.createElement('span');
      span.textContent = c.check.label || '';
      label.append(check, span);
      form.append(label);
    }
    form.hidden = inputs.length === 0 && !check;

    const values = () => {
      const v = {};
      for (const i of inputs) v[i.dataset.id] = i.value;
      if (check) v[check.dataset.id] = check.checked;
      return v;
    };
    const run = (a) => {
      const v = a.cancel ? {} : values();
      for (const i of inputs) i.value = ''; // don't keep secrets in the page
      showCard(null);
      post({ t: 'action', id: a.id, values: v });
    };

    const actions = $('card-actions');
    const buttons = (c.actions || []).map((a) => button(a, () => run(a)));
    actions.replaceChildren(...buttons);
    scrim.hidden = false;

    // Keyboard: Esc = the cancel action, Enter = the default one; focus starts on the first input or the safe choice.
    const cancel = (c.actions || []).find((a) => a.cancel);
    const primary = (c.actions || []).find((a) => a.default);
    cardKeyHandler = (e) => {
      if (e.key === 'Escape' && cancel) { e.preventDefault(); e.stopPropagation(); run(cancel); }
      else if (e.key === 'Enter' && primary && !(e.target instanceof HTMLButtonElement) && !(e.target instanceof HTMLInputElement && e.target.type === 'checkbox')) {
        e.preventDefault(); e.stopPropagation(); run(primary);
      }
    };
    document.addEventListener('keydown', cardKeyHandler, true);
    if (inputs.length > 0) {
      const empty = inputs.find((i) => !i.value) || inputs[0];
      empty.focus();
    } else {
      const focusIdx = (c.actions || []).findIndex((a) => a.focus);
      (buttons[focusIdx >= 0 ? focusIdx : buttons.length - 1] || $('card')).focus();
    }
  }

  function onContextMenu(e) {
    e.preventDefault();
    if (rightClick === 'copypaste') {
      if (term.hasSelection()) { copySelection(); term.clearSelection(); }
      else post({ t: 'paste-request' });
      return;
    }
    const has = term.hasSelection();
    const items = [
      { icon: '', label: T('Term.Menu.Copy', 'Copy'), kbd: 'Ctrl+Shift+C', disabled: !has, run: copySelection },
      { icon: '', label: T('Term.Menu.Paste', 'Paste'), kbd: 'Ctrl+Shift+V', run: () => post({ t: 'paste-request' }) },
      { icon: '', label: T('Term.Menu.SelectAll', 'Select all'), kbd: 'Ctrl+Shift+A', run: () => term.selectAll() },
      null,
      { icon: '', label: T('Term.Menu.Find', 'Find'), kbd: 'Ctrl+Shift+F', run: openSearch },
      { icon: '', label: T('Term.Menu.Save', 'Save output…'), run: () => post({ t: 'save-request' }) },
      { icon: '', label: T('Term.Menu.Clear', 'Clear history'), run: () => { term.clear(); prompts.length = 0; } },
    ];
    const menu = $('menu');
    menu.replaceChildren(...items.map((it) => {
      if (!it) return document.createElement('hr');
      const b = document.createElement('button');
      b.disabled = !!it.disabled;
      b.innerHTML = '<span class="icon"></span><span class="label"></span><span class="kbd"></span>';
      b.children[0].textContent = it.icon;
      b.children[1].textContent = it.label;
      b.children[2].textContent = it.kbd || '';
      b.addEventListener('click', () => { hideMenu(); it.run(); term.focus(); });
      return b;
    }));
    menu.hidden = false;
    const r = menu.getBoundingClientRect();
    menu.style.left = Math.min(e.clientX, window.innerWidth - r.width - 4) + 'px';
    menu.style.top = Math.min(e.clientY, window.innerHeight - r.height - 4) + 'px';
    const first = menu.querySelector('button:not(:disabled)');
    if (first) first.focus();
  }

  function hideMenu() { $('menu').hidden = true; }

  document.addEventListener('keydown', (e) => {
    if ($('menu').hidden) return;
    if (e.key === 'Escape') { e.preventDefault(); hideMenu(); term.focus(); }
    else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      const all = [...$('menu').querySelectorAll('button:not(:disabled)')];
      const i = all.indexOf(document.activeElement);
      const next = all[(i + (e.key === 'ArrowDown' ? 1 : all.length - 1)) % all.length];
      if (next) next.focus();
    }
  }, true);

  // ───────────── Host messages ─────────────
  function onMessage(e) {
    const data = e.data;
    if (typeof data !== 'string') return;
    if (data.charCodeAt(0) === 111 /* o */) { write(data.substring(1)); return; }
    let m;
    try { m = JSON.parse(data); } catch { return; }
    switch (m.t) {
      case 'init': init(m); break;
      case 'options': applyOptions(m.options || {}); break;
      case 'theme': applyTheme(m.theme); break;
      case 'strings': strings = m.strings || {}; applyUiStrings(); break;
      case 'focus': if (term && $('scrim').hidden) term.focus(); break;
      case 'banner': showBanner(m.banner); break;
      case 'card': showCard(m.card); break;
      case 'paste': if (term) term.paste(m.text || ''); break;
      case 'search': if (term) openSearch(); break;
      case 'clear': if (term) { term.clear(); prompts.length = 0; } break;
      case 'reset': if (term) { term.reset(); prompts.length = 0; } break;
      case 'fit': doFit(true); break;
      case 'zoom': if (term) setZoom(m.reset ? 0 : zoom + (m.delta || 0)); break;
      case 'select-all': if (term) term.selectAll(); break;
      case 'copy': if (term) copySelection(); break;
      case 'serialize':
        if (term) post({ t: 'serialized', id: m.id, data: m.format === 'html' ? serialize.serializeAsHTML({ includeGlobalBackground: true }) : bufferText() });
        break;
      case 'last-output':
        if (term) post({ t: 'serialized', id: m.id, data: lastCommandOutput() });
        break;
    }
  }

  function bufferText() {
    const b = term.buffer.active;
    const lines = [];
    for (let i = 0; i < b.length; i++) {
      const line = b.getLine(i);
      if (!line) continue;
      if (line.isWrapped && lines.length) lines[lines.length - 1] += line.translateToString(true);
      else lines.push(line.translateToString(true));
    }
    while (lines.length && lines[lines.length - 1] === '') lines.pop();
    return lines.join('\n') + '\n';
  }

  /** Text between the last two prompts (needs RDMan's shell integration). */
  function lastCommandOutput() {
    const live = prompts.filter((p) => !p.marker.isDisposed);
    if (live.length < 2) return '';
    const from = live[live.length - 2].marker.line + 1;
    const to = live[live.length - 1].marker.line;
    const b = term.buffer.active;
    const lines = [];
    for (let i = from; i < to; i++) {
      const line = b.getLine(i);
      if (line) lines.push(line.translateToString(true));
    }
    return lines.join('\n');
  }

  if (host) host.addEventListener('message', onMessage);
  post({ t: 'loaded' });
})();
