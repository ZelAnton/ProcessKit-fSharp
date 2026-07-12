// Reserved implementation links and indicator for the guides sidebar.
//
// mdBook's SUMMARY.md cannot express a sidebar entry that points at an external
// URL: a list item's link target must be a chapter file in `src`, and a raw URL
// makes the build fail ("failed to read chapter https://..."). The three
// implementation entries are therefore carried in SUMMARY.md as *draft chapters*
// (empty `()` links) without chapter files. mdBook renders each draft title in a
// <span> inside `.chapter-link-wrapper`; this script upgrades the two external
// entries at render time and marks the local implementation:
//
//   * "Rust crate"     -> a live external link to the Rust implementation's site.
//   * "Python wrapper" -> a live external link to the Python wrapper's docs site.
//   * ".NET version"   -> a non-clickable indicator for this implementation.
//
// Without JS the entries degrade to plain greyed draft items — never a broken or
// misdirected link.
(function () {
  "use strict";

  var ENTRIES = {
    "Rust crate": { href: "https://zelanton.github.io/processkit-rs" },
    "Python wrapper": { href: "https://zelanton.github.io/processkit-py" },
    ".NET version": { placeholder: "Current implementation" }
  };

  function apply() {
    var drafts = document.querySelectorAll(
      ".sidebar .chapter li.chapter-item > .chapter-link-wrapper > span"
    );

    Array.prototype.forEach.call(drafts, function (entry) {
      var title = entry.textContent.replace(/^\s*\d+\.\s*/, "").trim();
      var spec = ENTRIES[title];
      if (!spec) {
        return;
      }

      if (spec.href) {
        var link = document.createElement("a");
        link.href = spec.href;
        link.rel = "noopener";
        while (entry.firstChild) {
          link.appendChild(entry.firstChild);
        }
        entry.replaceWith(link);
      } else if (spec.placeholder) {
        entry.classList.add("current-implementation");
        entry.title = spec.placeholder;
        entry.setAttribute("aria-label", title + " — " + spec.placeholder);
      }
    });
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", apply);
  } else {
    apply();
  }
})();