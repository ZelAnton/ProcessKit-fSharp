// Reserved implementation links and indicator for the guides sidebar.
//
// mdBook's SUMMARY.md cannot express a sidebar entry that points at an external
// URL: a list item's link target must be a chapter file in `src`, and a raw URL
// makes the build fail ("failed to read chapter https://..."). The three
// implementation entries are therefore carried in SUMMARY.md as *draft prefix
// chapters* (bare `[Title]()` links, no chapter files) pinned above
// `[Overview](README.md)` in the same un-numbered prefix block. mdBook v0.4.40
// renders them as <li class="chapter-item expanded affix "><div>...</div></li>,
// with no wrapper element and, because prefix/affix chapters are never
// numbered, no leading "N." in the text.
// This script upgrades the two external entries to live links and marks the local
// implementation as a non-clickable indicator:
//
//   * "Rust version"   -> a live external link to the Rust implementation's site.
//   * "Python wrapper" -> a live external link to the Python wrapper's docs site.
//   * ".NET version"   -> a non-clickable indicator for this implementation.
//
// Without JS the entries degrade to plain greyed draft items — never a broken or
// misdirected link.
(function () {
  "use strict";

  var ENTRIES = {
    "Rust version": { href: "https://zelanton.github.io/processkit-rs" },
    "Python wrapper": { href: "https://zelanton.github.io/processkit-py" },
    ".NET version": { placeholder: "Current implementation" }
  };

  function apply() {
    // mdBook v0.4.40 renders draft prefix chapters as:
    // <li class="chapter-item expanded affix "><div>Rust version</div></li>
    // (no nested wrapper or <span>; no leading "N." since prefix chapters are
    // never numbered — the digit-stripping regex below is just defensive)
    var drafts = document.querySelectorAll(
      ".sidebar .chapter li.chapter-item > div"
    );

    Array.prototype.forEach.call(drafts, function (divEntry) {
      var textContent = divEntry.textContent || "";
      var title = textContent.replace(/^\s*\d+\.\s*/, "").trim();
      var spec = ENTRIES[title];
      if (!spec) {
        return;
      }

      if (spec.href) {
        var link = document.createElement("a");
        link.href = spec.href;
        link.rel = "noopener";
        while (divEntry.firstChild) {
          link.appendChild(divEntry.firstChild);
        }
        divEntry.replaceWith(link);
      } else if (spec.placeholder) {
        divEntry.classList.add("current-implementation");
        divEntry.title = spec.placeholder;
        divEntry.setAttribute("aria-label", title + " — " + spec.placeholder);
      }
    });
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", apply);
  } else {
    apply();
  }
})();