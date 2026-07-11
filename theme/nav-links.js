// Reserved external "menu items" for the guides sidebar.
//
// mdBook's SUMMARY.md cannot express a sidebar entry that points at an external
// URL: a list item's link target must be a chapter file in `src`, and a raw URL
// makes the build fail ("failed to read chapter https://..."). So the two
// reserved entries are carried in SUMMARY.md as *draft chapters* (an empty `()`
// link) — native, non-clickable placeholders that appear in the sidebar in the
// right order and position without a chapter file of their own — and this script
// upgrades them at render time:
//
//   * "Rust crate"     -> a live external link to the Rust implementation's site.
//   * "Python wrapper" -> stays a marked, non-clickable placeholder (its docs
//                         repository is not published yet); this only adds a
//                         "coming soon" tooltip so a visitor sees it is reserved,
//                         not broken. When its URL is known, give it an href here
//                         and drop the TODO note in SUMMARY.md.
//
// Without JS the entries degrade to plain greyed draft items — never a broken or
// misdirected link.
(function () {
  "use strict";

  var ENTRIES = {
    "Rust crate": { href: "https://zelanton.github.io/ProcessKit-rs/" },
    "Python wrapper": {
      placeholder: "Documentation coming soon — repository not published yet."
    }
  };

  function apply() {
    // A draft chapter renders its title inside a <div> (real chapters use <a>),
    // so this selects exactly the reserved placeholder items and nothing else.
    var drafts = document.querySelectorAll(".sidebar .chapter li.chapter-item > div");
    Array.prototype.forEach.call(drafts, function (div) {
      var title = div.textContent.replace(/^\s*\d+\.\s*/, "").trim();
      var spec = ENTRIES[title];
      if (!spec) {
        return;
      }
      if (spec.href) {
        var a = document.createElement("a");
        a.href = spec.href;
        a.rel = "noopener";
        while (div.firstChild) {
          a.appendChild(div.firstChild);
        }
        div.replaceWith(a);
      } else if (spec.placeholder) {
        div.title = spec.placeholder;
        div.setAttribute("aria-label", title + " — " + spec.placeholder);
      }
    });
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", apply);
  } else {
    apply();
  }
})();
