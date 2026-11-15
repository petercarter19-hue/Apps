/* The PC's additions to ui/index.html. The Mac's page is used byte for byte as it was handed over; app_window_win.py
   adds this file to the end of it when it opens the window. Three jobs:
   1. wait for the pywebview bridge before the page's first request (the Mac's page asks at once);
   2. a few layout touches for a normal Windows title bar;
   3. PC wording where the Mac's page talks about the Mac (menu bar, Safari, Apple Shortcuts).
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
  // WebView2's shortcut keys are on (so Ctrl+C/V/X/A/Z work in every text box); the browser's own ones are kept away
  window.vtBrowserKey = e => {
    const k = (e.key || "").toLowerCase();
    if (["f1", "f3", "f5", "f6", "f7", "f11", "f12", "browserback", "browserforward", "browserrefresh",
         "browsersearch", "browserhome"].includes(k)) return true;
    if (e.altKey && (k === "arrowleft" || k === "arrowright" || k === "home")) return true;
    if (e.ctrlKey && !e.altKey && "dfghjlnoprstuw".includes(k) && k.length === 1) return true;
    if (e.ctrlKey && e.shiftKey && "cij".includes(k) && k.length === 1) return true;
    return false;
  };
  window.addEventListener("keydown", e => { if (window.vtBrowserKey(e)) e.preventDefault(); }, true);

})();
