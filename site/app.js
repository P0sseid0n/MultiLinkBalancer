// Partes vivas da landing: o painel de demonstração do hero (liga/desliga, números variando)
// e as abas do simulador de "Como funciona" (trocam sozinhas a cada 6 s até alguém clicar).
(function () {
  'use strict';

  var reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  var state = { on: true, s1: 0, s2: 0, t1: 339, t2: 73, tab: 0, manual: false };

  var TAB_BASE = 'position: relative; min-height: 44px; padding: 0 18px; border-radius: 8px; font: inherit; font-size: 14px; font-weight: 500; cursor: pointer; transition: background-color .2s, color .2s, border-color .2s; ';
  var TAB_ON = 'background: #20262d; color: #e8ecef; border: 1px solid #3a434e;';
  var TAB_OFF = 'background: transparent; color: #9aa4ae; border: 1px solid #242a32;';
  var IFS = { 0: 'tNav', 1: 'tDl', 2: 'tUdp', 3: 'tOne' };

  function all(sel) { return Array.prototype.slice.call(document.querySelectorAll(sel)); }
  function setIf(name, visible) {
    all('[data-if="' + name + '"]').forEach(function (el) {
      if (el.hidden === visible) el.hidden = !visible;
    });
  }
  function setText(name, value) {
    all('[data-v="' + name + '"]').forEach(function (el) {
      if (el.textContent !== value) el.textContent = value;
    });
  }

  // ---------- painel do hero ----------
  function renderPanel() {
    var a = Math.round(state.s1), b = Math.round(state.s2), sum = a + b;
    var p1 = sum > 0 ? Math.round(a / sum * 100) : 0, p2 = sum > 0 ? 100 - p1 : 0;
    setIf('on', state.on);
    setIf('off', !state.on);
    setText('total', String(sum));
    setText('speed1', state.on && a > 0 ? a + ' Mbps' : '—');
    setText('speed2', state.on && b > 0 ? b + ' Mbps' : '—');
    setText('p1', state.on ? p1 + '%' : '—');
    setText('p2', state.on ? p2 + '%' : '—');
    all('[data-w="w1"]').forEach(function (el) { el.style.width = (state.on ? p1 : 0) + '%'; });
    all('[data-w="w2"]').forEach(function (el) { el.style.width = (state.on ? p2 : 0) + '%'; });
  }

  all('[data-action="toggle"]').forEach(function (btn) {
    btn.addEventListener('click', function () {
      state.on = !state.on;
      renderPanel();
      // o botão do outro estado acabou de aparecer: o foco vai para ele
      var next = all('[data-if="' + (state.on ? 'on' : 'off') + '"] [data-action="toggle"]')[0];
      if (next) next.focus();
    });
  });

  // ---------- abas de "Como funciona" ----------
  var tabs = all('[role="tab"][data-tab]');
  var panel = document.getElementById('sim-painel');
  // a troca automática pausa enquanto o mouse ou o foco estão nas abas ou no painel (WCAG 2.2.2)
  var hold = { mouse: false, focus: false };
  var TAB_MS = 6000, elapsed = 0;
  function held() { return hold.mouse || hold.focus || document.hidden; }
  document.addEventListener('visibilitychange', function () { syncBar(); });
  function syncBar() {
    var bar = document.querySelector('[role="tab"] .tabbar');
    if (bar) bar.style.animationPlayState = held() ? 'paused' : 'running';
  }
  function renderTabs() {
    elapsed = 0;
    tabs.forEach(function (btn) {
      var i = Number(btn.getAttribute('data-tab')), selected = i === state.tab;
      btn.setAttribute('aria-selected', selected ? 'true' : 'false');
      btn.tabIndex = selected ? 0 : -1;
      btn.style.cssText = TAB_BASE + (selected ? TAB_ON : TAB_OFF);
      var bar = btn.querySelector('.tabbar');
      if (bar) bar.remove();
      if (selected && !state.manual && !reduce) {
        bar = document.createElement('span');
        bar.className = 'tabbar';
        btn.appendChild(bar);
      }
      if (selected && panel) panel.setAttribute('aria-labelledby', btn.id);
    });
    syncBar();
    Object.keys(IFS).forEach(function (k) { setIf(IFS[k], Number(k) === state.tab); });
    all('[data-op="l2op"]').forEach(function (el) { el.style.opacity = state.tab === 3 ? '0.4' : '1'; });
    all('[data-op="l3op"]').forEach(function (el) { el.style.opacity = state.tab >= 2 ? '0.4' : '1'; });
  }
  tabs.forEach(function (btn) {
    btn.addEventListener('click', function () {
      state.tab = Number(btn.getAttribute('data-tab'));
      state.manual = true;
      renderTabs();
    });
    // setas, Home e End trocam de aba, como num tablist
    btn.addEventListener('keydown', function (e) {
      var n = tabs.length, i;
      if (e.key === 'ArrowRight') i = (state.tab + 1) % n;
      else if (e.key === 'ArrowLeft') i = (state.tab + n - 1) % n;
      else if (e.key === 'Home') i = 0;
      else if (e.key === 'End') i = n - 1;
      else return;
      state.tab = i;
      state.manual = true;
      renderTabs();
      tabs[i].focus();
      e.preventDefault();
    });
  });
  [tabs.length ? tabs[0].parentNode : null, panel].forEach(function (el) {
    if (!el) return;
    el.addEventListener('mouseenter', function () { hold.mouse = true; syncBar(); });
    el.addEventListener('mouseleave', function () { hold.mouse = false; syncBar(); });
    el.addEventListener('focusin', function () { hold.focus = true; syncBar(); });
    el.addEventListener('focusout', function (e) {
      var to = e.relatedTarget, inside = to && ((tabs[0] && tabs[0].parentNode.contains(to)) || (panel && panel.contains(to)));
      if (!inside) { hold.focus = false; syncBar(); }
    });
  });

  // ---------- tempo ----------
  if (reduce) {
    state.s1 = 339;
    state.s2 = 73;
    // sem movimento, cada pacote fica parado no ponto da pista em que estaria; o atraso extra de
    // 15% do ciclo tira de fora da pista os que começariam em 0
    all('.pk').forEach(function (el) {
      var cs = getComputedStyle(el), dur = parseFloat(cs.animationDuration) || 0, del = parseFloat(cs.animationDelay) || 0;
      el.style.animationDelay = (del - dur * 0.15) + 's';
    });
  } else {
    setInterval(function () {
      state.t1 = Math.round(339 * (0.9 + Math.random() * 0.18));
      state.t2 = Math.round(73 * (0.8 + Math.random() * 0.4));
    }, 1200);
    setInterval(function () {
      var g1 = state.on ? state.t1 : 0, g2 = state.on ? state.t2 : 0;
      var n1 = state.s1 + (g1 - state.s1) * 0.18, n2 = state.s2 + (g2 - state.s2) * 0.18;
      if (Math.abs(n1 - state.s1) > 0.3 || Math.abs(n2 - state.s2) > 0.3) {
        state.s1 = Math.abs(n1 - g1) < 0.5 ? g1 : n1;
        state.s2 = Math.abs(n2 - g2) < 0.5 ? g2 : n2;
        renderPanel();
      }
    }, 50);
    // conta só o tempo sem pausa, no mesmo ritmo da barrinha de progresso da aba
    setInterval(function () {
      if (state.manual || held()) return;
      elapsed += 100;
      if (elapsed < TAB_MS) return;
      state.tab = (state.tab + 1) % tabs.length;
      renderTabs();
    }, 100);
  }

  renderPanel();
  renderTabs();
})();
