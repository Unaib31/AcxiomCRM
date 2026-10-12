(function () {
  'use strict';

  /* ---- unobtrusive validation adapters for the business rules ------------ */
  if (window.jQuery && jQuery.validator && jQuery.validator.unobtrusive) {
    var $ = jQuery, v = $.validator, adapters = v.unobtrusive.adapters;
    function skipped(el, p) {
      if (!p || !p.unlessfield) return false;
      var other = $(el.form).find('[name="' + p.unlessfield + '"]').val();
      return (p.unlessvalues || '').split(',').indexOf(other) >= 0;
    }
    v.addMethod('crmnotpast', function (value, el, p) {
      if (!value || skipped(el, p)) return true;
      return value >= p.today; // ISO yyyy-MM-dd strings compare chronologically
    });
    adapters.add('crmnotpast', ['today', 'unlessfield', 'unlessvalues'], function (o) {
      o.rules.crmnotpast = { today: o.params.today, unlessfield: o.params.unlessfield, unlessvalues: o.params.unlessvalues };
      o.messages.crmnotpast = o.message;
    });
    v.addMethod('crmpositive', function (value, el, p) {
      if (value === '' || value == null) return true;
      var n = Number(String(value).replace(/,/g, ''));
      if (isNaN(n)) return false;
      return skipped(el, p) ? n >= 0 : n > 0;
    });
    adapters.add('crmpositive', ['unlessfield', 'unlessvalues'], function (o) {
      o.rules.crmpositive = { unlessfield: o.params.unlessfield, unlessvalues: o.params.unlessvalues };
      o.messages.crmpositive = o.message;
    });
    // Re-validate dependent fields when the stage/status they depend on changes.
    $(document).on('change', 'select[name="Stage"], select[name="Status"]', function () {
      $(this.form).find('[data-val-crmnotpast],[data-val-crmpositive]').each(function () { $(this).valid(); });
    });
  }

  /* ---- theme toggle ------------------------------------------------------- */
  var themeBtn = document.getElementById('theme-toggle');
  if (themeBtn) themeBtn.addEventListener('click', function () {
    var next = document.documentElement.getAttribute('data-bs-theme') === 'dark' ? 'light' : 'dark';
    document.documentElement.setAttribute('data-bs-theme', next);
    try { localStorage.setItem('crm-theme', next); } catch (e) { }
    renderCharts();
  });

  /* ---- mobile sidebar ----------------------------------------------------- */
  var sb = document.getElementById('app-sidebar'), bd = document.getElementById('sidebar-backdrop');
  var sidebarTrigger = null;
  function toggleSidebar(open) {
    if (!sb) return;
    sb.classList.toggle('open', open);
    sb.inert = !open && window.matchMedia('(max-width: 991.98px)').matches;
    document.body.classList.toggle('sidebar-open', open);
    if (bd) bd.classList.toggle('show', open);
    document.querySelectorAll('[data-sidebar-toggle]').forEach(function (b) { b.setAttribute('aria-expanded', String(open)); });
    if (open) { sidebarTrigger = document.activeElement; sb.querySelector('a').focus(); }
    else if (sidebarTrigger) { sidebarTrigger.focus(); sidebarTrigger = null; }
  }
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape' && sb && sb.classList.contains('open')) toggleSidebar(false);
    if (e.key === 'Tab' && sb && sb.classList.contains('open')) {
      var links = sb.querySelectorAll('a[href], button');
      var first = links[0], last = links[links.length - 1];
      if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
      else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
    }
    var typing = /INPUT|TEXTAREA|SELECT/.test(e.target.tagName) || e.target.isContentEditable;
    if (e.key === '/' && !typing && !e.metaKey && !e.ctrlKey && !e.altKey) {
      var search = document.querySelector('.global-search input');
      if (search) { e.preventDefault(); search.focus(); }
    }
  });
  window.matchMedia('(min-width: 992px)').addEventListener('change', function (e) {
    if (!sb) return;
    if (e.matches && sb.classList.contains('open')) toggleSidebar(false);
    sb.inert = !e.matches && !sb.classList.contains('open');
  });
  if (sb) sb.inert = window.matchMedia('(max-width: 991.98px)').matches;
  document.querySelectorAll('.app-sidebar .nav-link.active').forEach(function (a) { a.setAttribute('aria-current', 'page'); });
  document.querySelectorAll('[data-sidebar-toggle]').forEach(function (b) { b.addEventListener('click', function () { toggleSidebar(!sb.classList.contains('open')); }); });
  if (bd) bd.addEventListener('click', function () { toggleSidebar(false); });

  /* ---- confirmation dialog for destructive actions ------------------------ */
  var modalEl = document.getElementById('confirm-modal'), pendingForm = null;
  document.addEventListener('submit', function (e) {
    var f = e.target;
    if (f.dataset && f.dataset.confirm && !f.dataset.confirmed && modalEl && window.bootstrap) {
      e.preventDefault();
      pendingForm = f;
      modalEl.querySelector('[data-role="message"]').textContent = f.dataset.confirm;
      modalEl.querySelector('[data-role="title"]').textContent = f.dataset.confirmTitle || 'Please confirm';
      var ok = modalEl.querySelector('[data-role="ok"]');
      ok.textContent = f.dataset.confirmOk || 'Confirm';
      ok.className = 'btn ' + (f.dataset.confirmClass || 'btn-danger');
      bootstrap.Modal.getOrCreateInstance(modalEl).show();
    }
  });
  if (modalEl) modalEl.querySelector('[data-role="ok"]').addEventListener('click', function () {
    if (pendingForm) { pendingForm.dataset.confirmed = '1'; bootstrap.Modal.getOrCreateInstance(modalEl).hide(); pendingForm.requestSubmit ? pendingForm.requestSubmit() : pendingForm.submit(); }
  });

  /* ---- prevent double submits -------------------------------------------- */
  document.addEventListener('submit', function (e) {
    var f = e.target;
    if (e.defaultPrevented) return;
    if (window.jQuery && jQuery(f).data('validator') && !jQuery(f).valid()) return;
    f.querySelectorAll('button[type=submit]:not([data-no-lock])').forEach(function (b) { setTimeout(function () { b.disabled = true; }, 0); });
  });

  /* ---- stage -> default probability on the opportunity form -------------- */
  var stage = document.querySelector('form select[name="Stage"][data-defaults]');
  if (stage) stage.addEventListener('change', function () {
    var map = JSON.parse(stage.dataset.defaults), prob = document.querySelector('input[name="Probability"]');
    if (prob && map[stage.value] !== undefined) { prob.value = map[stage.value]; if (window.jQuery) jQuery(prob).valid(); }
    var closed = stage.value === 'Won' || stage.value === 'Lost';
    var notes = document.getElementById('outcome-group'); if (notes) notes.classList.toggle('d-none', !closed);
  });

  /* ---- follow-up form: related record picker ------------------------------ */
  var relType = document.querySelector('select[name="RelatedType"]'), relId = document.querySelector('select[name="RelatedId"]');
  function filterRelated() {
    if (!relType || !relId) return;
    var t = relType.value, first = null;
    Array.prototype.forEach.call(relId.options, function (o) {
      if (!o.dataset.type) return;
      var show = o.dataset.type === t; o.hidden = !show; o.disabled = !show;
      if (show && !first) first = o;
    });
    var cur = relId.options[relId.selectedIndex];
    if (!cur || cur.dataset.type !== t) relId.value = '';
  }
  if (relType) { relType.addEventListener('change', filterRelated); filterRelated(); }

  /* ---- auto-dismiss success alerts --------------------------------------- */
  document.querySelectorAll('.alert[data-autodismiss]').forEach(function (a) {
    setTimeout(function () { if (window.bootstrap) bootstrap.Alert.getOrCreateInstance(a).close(); }, 6000);
  });

  /* ---- copy to clipboard -------------------------------------------------- */
  document.querySelectorAll('[data-copy]').forEach(function (b) {
    b.addEventListener('click', function () {
      var t = document.querySelector(b.dataset.copy);
      if (t && navigator.clipboard) navigator.clipboard.writeText(t.value || t.textContent).then(function () { b.textContent = 'Copied'; });
    });
  });

  /* ---- Chart.js ----------------------------------------------------------- */
  var charts = [];
  var palette = ['#2f7de1', '#0e9f8e', '#f59e0b', '#8b5cf6', '#ef4444', '#22c55e', '#64748b'];
  function cssVar(n) { return getComputedStyle(document.documentElement).getPropertyValue(n).trim(); }
  function renderCharts() {
    if (!window.Chart) return;
    charts.forEach(function (c) { c.destroy(); }); charts = [];
    var text = cssVar('--crm-muted') || '#64748b', grid = cssVar('--crm-border') || '#e3e9f0';
    Chart.defaults.font.family = getComputedStyle(document.body).fontFamily;
    Chart.defaults.color = text;
    document.querySelectorAll('canvas[data-chart]').forEach(function (cv) {
      var cfg; try { cfg = JSON.parse(cv.dataset.chart); } catch (e) { return; }
      var kind = cfg.type, empty = !cfg.datasets.some(function (d) { return d.data.some(function (x) { return x > 0; }); });
      var host = cv.parentElement;
      var note = host.querySelector('.chart-empty'); if (note) note.remove();
      if (empty) { cv.classList.add('d-none'); var p = document.createElement('div'); p.className = 'empty-state chart-empty'; p.innerHTML = '<i class="bi bi-bar-chart"></i>No data for this period.'; host.appendChild(p); return; }
      cv.classList.remove('d-none');
      var datasets = cfg.datasets.map(function (d, i) {
        var base = { label: d.label, data: d.data };
        if (kind === 'doughnut') { base.backgroundColor = cfg.colors || palette; base.borderWidth = 2; base.borderColor = cssVar('--crm-surface') || '#fff'; }
        else if (kind === 'line') { base.borderColor = (d.color || palette[i]); base.backgroundColor = (d.color || palette[i]) + '33'; base.fill = true; base.tension = .35; base.pointRadius = 3; }
        else { base.backgroundColor = d.color || (cfg.colors && cfg.colors.length > 1 && cfg.datasets.length === 1 ? cfg.colors : palette[i]); base.borderRadius = 4; base.maxBarThickness = 38; }
        return base;
      });
      var opts = { responsive: true, maintainAspectRatio: false, plugins: { legend: { display: kind === 'doughnut' || cfg.datasets.length > 1, position: 'bottom', labels: { usePointStyle: true, boxWidth: 8 } } } };
      if (kind !== 'doughnut') {
        opts.scales = { x: { grid: { display: false } }, y: { beginAtZero: true, grid: { color: grid }, ticks: { precision: 0, callback: cfg.money ? function (v) { return '₹' + new Intl.NumberFormat('en-IN', { notation: 'compact', maximumFractionDigits: 1 }).format(v); } : undefined } } };
        if (cfg.money) opts.plugins.tooltip = { callbacks: { label: function (c) { return c.dataset.label + ': ₹' + new Intl.NumberFormat('en-IN').format(c.parsed.y); } } };
      } else opts.cutout = '74%';
      opts.animation = window.matchMedia('(prefers-reduced-motion: reduce)').matches ? false : { duration: 350 };
      charts.push(new Chart(cv, { type: kind, data: { labels: cfg.labels, datasets: datasets }, options: opts }));
    });
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', renderCharts); else renderCharts();
})();
