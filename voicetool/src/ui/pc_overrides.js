/* Windows additions to ui/index.html. app_window_win.py adds this file to the end of the page when it opens the window.
   Two jobs:
   1. wait for the pywebview bridge before the page's first request;
   2. a few layout touches for a normal Windows title bar.
   Everything is guarded: if the page changes, a missing element is simply skipped. */
(function () {
  "use strict";

  // 1. bridge ---------------------------------------------------------------------------------------------------
  const bridge = new Promise(resolve => {
    if (window.pywebview && window.pywebview.api) resolve();
    else window.addEventListener("pywebviewready", () => resolve(), { once: true });
  });
  if (window.vt && typeof window.vt.call === "function") {
    const first = window.vt.call.bind(window.vt);
    window.vt.call = async (name, args = {}) => { await bridge; return first(name, args); };
  }

  // 2. layout ---------------------------------------------------------------------------------------------------
  const css = document.createElement("style");
  css.textContent = `
    nav { padding-top: 18px; }              /* the Mac leaves room for its traffic-light buttons */
    main { padding-top: 32px; }
    .status-pill { margin-bottom: 4px; }
    button, nav button, .btn, .chip { cursor: pointer !important; }
    * { scrollbar-color: #33333d transparent; }
  `;
  document.head.appendChild(css);
  // no browser right-click menu (Reload, Inspect...), except where there is text to cut, copy or paste
  window.addEventListener("contextmenu", e => {
    if (!e.target.closest("input, textarea, .what, .said, .styletext, .notes")) e.preventDefault();
  });
})();
