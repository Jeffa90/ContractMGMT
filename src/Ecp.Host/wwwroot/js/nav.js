// Administration menu: a native <details> disclosure that also closes on Escape (returning focus to its summary) and when focus or a click leaves it. Works without this script.
(function () {
  var menu = document.getElementById('admin-menu');
  if (!menu) return;
  var summary = menu.querySelector('summary');
  summary.setAttribute('aria-controls', 'admin-panel');
  var panel = menu.querySelector('.admin-panel');
  if (panel) panel.id = 'admin-panel';
  function sync() { summary.setAttribute('aria-expanded', menu.open ? 'true' : 'false'); }
  menu.addEventListener('toggle', sync);
  sync();
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape' && menu.open) { menu.open = false; summary.focus(); }
  });
  document.addEventListener('click', function (e) { if (menu.open && !menu.contains(e.target)) menu.open = false; });
  menu.addEventListener('focusout', function (e) { if (menu.open && e.relatedTarget && !menu.contains(e.relatedTarget)) menu.open = false; });
})();
