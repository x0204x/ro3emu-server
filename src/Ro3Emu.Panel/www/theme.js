// Runs before the page paints: the theme chosen with the sun/moon button, or none to follow the system setting.
function setTheme(theme) {
  if (theme) document.documentElement.dataset.theme = theme;
  try { if (theme) localStorage.setItem('theme', theme); } catch { /* private window: the choice lasts for this page */ }
  const dark = document.documentElement.dataset.theme === 'dark'
    || (!document.documentElement.dataset.theme && matchMedia('(prefers-color-scheme: dark)').matches);
  const meta = document.querySelector('meta[name="theme-color"]');
  if (meta) meta.content = dark ? '#141641' : '#dfe4fc';
}
try { setTheme(localStorage.getItem('theme')); } catch { /* no storage: follow the system */ }
