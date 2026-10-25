/*
  Chandlery client helpers. Loaded synchronously in <head> so the theme is applied before first paint.
  Everything is event-delegated on document so it keeps working after Blazor's enhanced navigation
  patches the page.
*/
(function () {
  var root = document.documentElement;
  var THEMES = ['aurora', 'sunset', 'forest', 'orchid', 'graphite'];
  var KEY_THEME = 'chandlery.theme', KEY_MODE = 'chandlery.mode', KEY_NAV = 'chandlery.nav';
  var dark = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;

  function read(key, fallback) {
    try { return localStorage.getItem(key) || fallback; } catch (e) { return fallback; }
  }
  function write(key, value) {
    try { localStorage.setItem(key, value); } catch (e) { /* private mode: keep going without persistence */ }
  }

  function apply() {
    var theme = read(KEY_THEME, 'aurora');
    var pref = read(KEY_MODE, 'system');
    if (THEMES.indexOf(theme) < 0) theme = 'aurora';
    root.setAttribute('data-theme', theme);
    root.setAttribute('data-pref', pref);
    root.setAttribute('data-mode', pref === 'system' ? (dark && dark.matches ? 'dark' : 'light') : pref);
    root.setAttribute('data-nav', read(KEY_NAV, 'expanded'));
    if (!root.hasAttribute('data-drawer')) root.setAttribute('data-drawer', 'closed');

    var themeButtons = document.querySelectorAll('[data-set-theme]');
    for (var i = 0; i < themeButtons.length; i++)
      themeButtons[i].setAttribute('aria-pressed', String(themeButtons[i].getAttribute('data-set-theme') === theme));
    var modeButtons = document.querySelectorAll('[data-set-mode]');
    for (var j = 0; j < modeButtons.length; j++)
      modeButtons[j].setAttribute('aria-pressed', String(modeButtons[j].getAttribute('data-set-mode') === pref));
  }

  apply(); // before first paint

  document.addEventListener('DOMContentLoaded', apply);
  if (dark && dark.addEventListener) dark.addEventListener('change', apply);

  // Blazor enhanced navigation swaps the body; make sure the attributes and picker state survive it.
  function hookBlazor() {
    if (window.Blazor && window.Blazor.addEventListener) {
      window.Blazor.addEventListener('enhancedload', function () {
        root.setAttribute('data-drawer', 'closed');
        apply();
      });
    } else {
      setTimeout(hookBlazor, 200);
    }
  }
  hookBlazor();

  function closeMenus(except) {
    var open = document.querySelectorAll('details.menu[open]');
    for (var i = 0; i < open.length; i++) if (open[i] !== except) open[i].removeAttribute('open');
  }

  document.addEventListener('click', function (e) {
    var target = e.target;
    var el;

    if ((el = target.closest && target.closest('[data-set-theme]'))) {
      write(KEY_THEME, el.getAttribute('data-set-theme'));
      apply();
      return;
    }
    if ((el = target.closest && target.closest('[data-set-mode]'))) {
      write(KEY_MODE, el.getAttribute('data-set-mode'));
      apply();
      return;
    }
    if (target.closest && target.closest('[data-toggle-nav]')) {
      write(KEY_NAV, root.getAttribute('data-nav') === 'collapsed' ? 'expanded' : 'collapsed');
      apply();
      return;
    }
    if (target.closest && target.closest('[data-toggle-drawer]')) {
      root.setAttribute('data-drawer', root.getAttribute('data-drawer') === 'open' ? 'closed' : 'open');
      return;
    }
    if ((el = target.closest && target.closest('[data-fill-email]'))) {
      var email = document.querySelector('input[name="email"]');
      var password = document.querySelector('input[name="password"]');
      if (email) email.value = el.getAttribute('data-fill-email');
      if (password) { password.value = 'Passw0rd!'; password.focus(); }
      return;
    }
    if (target.closest && target.closest('.drawer-overlay')) {
      root.setAttribute('data-drawer', 'closed');
      return;
    }

    // Close any open dropdown when clicking elsewhere
    var inMenu = target.closest && target.closest('details.menu');
    closeMenus(inMenu);
  });

  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') {
      closeMenus(null);
      root.setAttribute('data-drawer', 'closed');
    }
  });

  function toast(tone, message) {
    var host = document.getElementById('toast-host');
    if (!host) {
      host = document.createElement('div');
      host.id = 'toast-host';
      host.setAttribute('aria-live', 'polite');
      document.body.appendChild(host);
    }
    var el = document.createElement('div');
    el.className = 'toast';
    el.setAttribute('data-tone', tone || 'info');
    el.textContent = message;
    host.appendChild(el);
    setTimeout(function () { el.classList.add('leaving'); }, 4200);
    setTimeout(function () { if (el.parentNode) el.parentNode.removeChild(el); }, 4700);
  }

  // Keeps the address bar in step with a grid's filters/sort/page without a navigation.
  function replaceQuery(query) {
    var url = location.pathname + (query ? '?' + query : '') + location.hash;
    history.replaceState(history.state, '', url);
  }

  function download(fileName, text) {
    // BOM so Excel reads UTF-8 correctly
    var blob = new Blob(['﻿' + text], { type: 'text/csv;charset=utf-8' });
    var a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    setTimeout(function () { URL.revokeObjectURL(a.href); a.remove(); }, 500);
  }

  // Command palette (Ctrl/Cmd+K): the Blazor component registers itself here.
  var palette = null;
  function registerPalette(ref) { palette = ref; }
  function openPalette() { if (palette) palette.invokeMethodAsync('Toggle'); }

  document.addEventListener('keydown', function (e) {
    if ((e.ctrlKey || e.metaKey) && (e.key === 'k' || e.key === 'K')) {
      e.preventDefault();
      openPalette();
    }
  });
  document.addEventListener('click', function (e) {
    if (e.target.closest && e.target.closest('[data-open-palette]')) openPalette();
  });

  function setMode(mode) { write(KEY_MODE, mode); apply(); }
  function setTheme(theme) { write(KEY_THEME, theme); apply(); }

  // On narrow screens the items grid sits below the category tree, so bring it into view after a selection.
  function scrollToOnSmall(id) {
    if (window.innerWidth >= 1024) return;
    var el = document.getElementById(id);
    if (el) el.scrollIntoView({ behavior: "smooth", block: "start" });
  }

  window.chandlery = {
    scrollToOnSmall: scrollToOnSmall,
    applyTheme: apply, toast: toast, replaceQuery: replaceQuery, download: download,
    registerPalette: registerPalette, setMode: setMode, setTheme: setTheme
  };
})();
