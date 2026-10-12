// Applied before first paint to avoid a flash of the wrong theme.
(function () {
  try {
    var t = localStorage.getItem('crm-theme');
    if (!t) t = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    document.documentElement.setAttribute('data-bs-theme', t);
  } catch (e) { /* storage unavailable: keep default */ }
})();
