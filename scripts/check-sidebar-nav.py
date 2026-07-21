#!/usr/bin/env python3
"""Regression check for the mdBook sidebar "pinned title" CSS selector.

Why this exists: theme/custom.css styles the "Overview" sidebar entry as a
pinned, low-key section label. Under the CI-pinned mdBook version (see
.github/workflows/docs.yml), every prefix/affix chapter -- including
"Overview" itself -- still carries the `chapter-item` class (see the K-005
pitfall / docs/SUMMARY.md's comment), so a naive `li:not(.chapter-item)`
selector silently matches nothing. A more recent, ambient mdBook install
(0.5.x) renders a different DOM (client-rendered, wrapped in
`.chapter-link-wrapper`) and is NOT a valid oracle for this check -- only the
exact CI-pinned binary's output is.

This script parses the sidebar `<ol class="chapter">` of a built
book/index.html (or any other built chapter page) using the same implicit
`<li>`-closing rule real browsers apply (HTML5 SS13.2.6.4.7: a new `<li>`
start tag closes a still-open `<li>` -- Python's stdlib html.parser does not
do this on its own, and mdBook v0.4.40's generated markup for the
spacer/part-title transition relies on it), then asserts theme/custom.css's
pinned-title selector

    .sidebar .chapter li.spacer + li.chapter-item.affix > a

matches exactly the "Overview" entry, across all three sidebar entry classes:
draft entries (the implementation-switcher placeholders, rendered as a bare
`<div>` with no `<a>`), regular numbered chapters (`chapter-item` without
`affix`), and prefix/affix chapters (`chapter-item.affix`, of which
"Overview" is the only one immediately preceded by a divider).

Usage:
    python3 scripts/check-sidebar-nav.py [path/to/book/index.html]

Exits 0 and prints a short summary on success; exits 1 with a diagnostic on
any mismatch (selector matches something unexpected, or fails to match
"Overview").
"""

from __future__ import annotations

import sys
from html.parser import HTMLParser
from pathlib import Path

_VOID_ELEMENTS = {"br", "hr", "img", "input", "meta", "link"}

# The expected pinned title, taken from docs/SUMMARY.md's "Overview" entry.
_EXPECTED_PINNED_TITLE = "Overview"

# The implementation-switcher's draft entries (docs/SUMMARY.md) -- these must
# never pick up the pinned-title style, with or without theme/nav-links.js
# having run (this script checks the pre-JS, server-rendered markup only).
_DRAFT_TITLES = {"Rust version", "Python wrapper", ".NET version"}


class Node:
    __slots__ = ("tag", "attrs", "children", "text")

    def __init__(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        self.tag = tag
        self.attrs = dict(attrs)
        self.children: list[Node] = []
        self.text = ""

    def classes(self) -> set[str]:
        return set((self.attrs.get("class") or "").split())

    def find_first(self, tag: str) -> "Node | None":
        """Depth-first search for the first descendant with the given tag."""
        for child in self.children:
            if child.tag == tag:
                return child
            found = child.find_first(tag)
            if found is not None:
                return found
        return None

    def find_direct_child(self, tag: str) -> "Node | None":
        """Returns the first *direct* child with the given tag, or None.

        Unlike `find_first`, this does not recurse into descendants -- it is
        what CSS's `>` child combinator actually means, and is required to
        validate `li.chapter-item.affix > a` (an `<a>` nested deeper, e.g.
        inside a `<span>`, must NOT be treated as a match)."""
        for child in self.children:
            if child.tag == tag:
                return child
        return None

    def text_content(self) -> str:
        parts = [self.text]
        for child in self.children:
            parts.append(child.text_content())
        return "".join(parts).strip()


class ChapterTreeBuilder(HTMLParser):
    """Builds a DOM subtree for the first <ol class="chapter"> ... </ol>.

    Applies the HTML5 implicit-<li>-closing rule real browsers use: a new
    <li> start tag closes an already-open <li> sibling. Python's html.parser
    does not implement this itself, so without it, mdBook v0.4.40's
    unclosed-<li> markup before each `<li class="spacer">` would be
    (incorrectly) parsed as nesting the spacer INSIDE the preceding
    chapter-item, breaking sibling-adjacency checks that browsers -- and this
    project's CSS selector -- rely on.
    """

    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.root: Node | None = None
        self._stack: list[Node] = []
        self._capturing = False

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        attrs_dict = dict(attrs)
        if not self._capturing:
            if tag == "ol" and attrs_dict.get("class") == "chapter":
                self._capturing = True
                self.root = Node(tag, attrs)
                self._stack = [self.root]
            return

        if tag == "li" and self._stack and self._stack[-1].tag == "li":
            # Implicit close: a new <li> ends any still-open <li> sibling.
            self._stack.pop()

        node = Node(tag, attrs)
        self._stack[-1].children.append(node)
        if tag not in _VOID_ELEMENTS:
            self._stack.append(node)

    def handle_endtag(self, tag: str) -> None:
        if not self._capturing:
            return
        for i in range(len(self._stack) - 1, -1, -1):
            if self._stack[i].tag == tag:
                del self._stack[i:]
                break
        if tag == "ol" and not self._stack:
            self._capturing = False

    def handle_data(self, data: str) -> None:
        if self._capturing and self._stack:
            self._stack[-1].text += data


def pinned_title_matches(ol: Node) -> list[Node]:
    """Returns the <a> elements matched by
    `.sidebar .chapter li.spacer + li.chapter-item.affix > a`
    against the given <ol class="chapter"> node's direct <li> children.

    Uses `find_direct_child`, not `find_first`: the selector's `> a` combinator
    requires an *immediate* child <a>, so a nested `<li><span><a>` must NOT be
    treated as a match here -- that would let a broken (non-immediate-child)
    selector pass validation by accident."""
    matches: list[Node] = []
    previous: Node | None = None
    for li in ol.children:
        if li.tag != "li":
            continue
        classes = li.classes()
        if (
            previous is not None
            and previous.tag == "li"
            and "spacer" in previous.classes()
            and "chapter-item" in classes
            and "affix" in classes
        ):
            a = li.find_direct_child("a")
            if a is not None:
                matches.append(a)
        previous = li
    return matches


def classify_entries(ol: Node) -> dict[str, list[str]]:
    """Buckets every direct <li> child's title into draft / regular /
    prefix-affix, for the assertions below (and for a readable summary)."""
    buckets: dict[str, list[str]] = {"draft": [], "regular": [], "prefix-affix": []}
    for li in ol.children:
        if li.tag != "li" or "spacer" in li.classes():
            continue
        classes = li.classes()
        a = li.find_first("a")
        div = li.find_first("div")
        if "chapter-item" in classes and "affix" in classes:
            title = (a or div).text_content() if (a or div) else ""
            if a is not None:
                buckets["prefix-affix"].append(title)
            elif div is not None:
                buckets["draft"].append(title)
        elif "chapter-item" in classes and a is not None:
            buckets["regular"].append(a.text_content())
    return buckets


def _self_test_rejects_nested_anchor() -> str | None:
    """Negative fixture: builds a synthetic `<li class="spacer">` followed by
    a `<li class="chapter-item affix">` whose `<a>` is nested one level deep
    inside a `<span>` (i.e. NOT an immediate child), and asserts
    `pinned_title_matches` does not match it.

    This guards against a regression back to `find_first` (depth-first,
    "anywhere in the subtree") in place of `find_direct_child` -- the CSS
    selector under test requires `li.chapter-item.affix > a` (immediate
    child), so a nested anchor must be rejected. Returns an error message on
    failure, or None on success."""
    ol = Node("ol", [("class", "chapter")])

    spacer = Node("li", [("class", "spacer")])
    ol.children.append(spacer)

    affix_li = Node("li", [("class", "chapter-item affix")])
    span = Node("span", [])
    nested_a = Node("a", [("href", "overview.html")])
    nested_a.text = "Overview"
    span.children.append(nested_a)
    affix_li.children.append(span)
    ol.children.append(affix_li)

    matches = pinned_title_matches(ol)
    if matches:
        return (
            "self-test failed: pinned_title_matches() matched an <a> nested "
            "inside a <span> (not an immediate child of the affix <li>) -- "
            "the CSS selector's `> a` child combinator requires an immediate "
            "child; use find_direct_child(), not find_first(), to validate it."
        )
    return None


def main(argv: list[str]) -> int:
    self_test_error = _self_test_rejects_nested_anchor()
    if self_test_error is not None:
        print(f"check-sidebar-nav: {self_test_error}", file=sys.stderr)
        return 1


    html_path = Path(argv[1]) if len(argv) > 1 else Path("book/index.html")
    if not html_path.exists():
        print(
            f"check-sidebar-nav: {html_path} not found -- run `mdbook build` "
            "with the CI-pinned mdBook version first (see .github/workflows/docs.yml).",
            file=sys.stderr,
        )
        return 1

    content = html_path.read_text(encoding="utf-8")
    builder = ChapterTreeBuilder()
    builder.feed(content)
    ol = builder.root
    if ol is None:
        print(
            f"check-sidebar-nav: no <ol class=\"chapter\"> found in {html_path} "
            "-- mdBook's sidebar markup structure has changed; update this "
            "script (and the K-005 pitfall) to match.",
            file=sys.stderr,
        )
        return 1

    buckets = classify_entries(ol)
    errors: list[str] = []

    if set(buckets["draft"]) != _DRAFT_TITLES:
        errors.append(
            f"expected draft entries {sorted(_DRAFT_TITLES)!r}, found {sorted(buckets['draft'])!r} "
            "-- docs/SUMMARY.md's implementation switcher changed; update _DRAFT_TITLES."
        )
    if not buckets["regular"]:
        errors.append("expected at least one regular numbered chapter, found none")
    if _EXPECTED_PINNED_TITLE not in buckets["prefix-affix"]:
        errors.append(
            f"expected {_EXPECTED_PINNED_TITLE!r} among prefix/affix chapters, "
            f"found {sorted(buckets['prefix-affix'])!r}"
        )

    matches = pinned_title_matches(ol)
    matched_titles = [a.text_content() for a in matches]

    if matched_titles != [_EXPECTED_PINNED_TITLE]:
        errors.append(
            "theme/custom.css's pinned-title selector "
            "'.sidebar .chapter li.spacer + li.chapter-item.affix > a' "
            f"matched {matched_titles!r} in {html_path}, expected exactly "
            f"[{_EXPECTED_PINNED_TITLE!r}]. This means either the selector is "
            "dead again (mdBook's DOM changed) or it over-matches a draft/"
            "regular entry -- diff against the K-005 pitfall's expected "
            "v0.4.40 structure."
        )

    if errors:
        print("check-sidebar-nav: FAILED", file=sys.stderr)
        for e in errors:
            print(f"  - {e}", file=sys.stderr)
        return 1

    print(
        "check-sidebar-nav: OK -- pinned-title selector matches exactly "
        f"{matched_titles!r}; draft={sorted(buckets['draft'])!r}; "
        f"regular={len(buckets['regular'])} numbered chapters; "
        f"prefix-affix={sorted(buckets['prefix-affix'])!r}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
