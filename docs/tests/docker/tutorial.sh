#!/bin/sh
# Follows the installation page and the tutorial: installs douze with the command of the installation page (unless
# DOUZE is set), builds the "first site" of that page, then the blog of the tutorial unit by unit, with the files
# extracted from the pages (fences with a title="<path>" meta), and checks the output of each unit, douze serve
# included.
#
#   tutorial.sh <pages folder>       the folder that holds guide/install.md and tutorial/*.md
#
# Environment:
#   DOUZE   a douze executable to test instead of installing one (a local build)
#   WORK    the folder to work in (default: a new temporary folder)
#   PORT    the port of douze serve (default 8080, as in the tutorial)
#
# Runs in the containers of validate.sh, and on any machine with sh, curl and awk.

set -u

PAGES="${1:?usage: tutorial.sh <pages folder>}"
PAGES="$(cd "$PAGES" && pwd)"
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="${WORK:-$(mktemp -d)}"
PORT="${PORT:-8080}"
EXTRACT="$HERE/extract-files.awk"

passed=0
failed=0
failures=""

ok() { passed=$((passed + 1)); printf '  ok    %s\n' "$1"; }
ko() { failed=$((failed + 1)); failures="$failures
  $1"; printf '  FAIL  %s\n' "$1"; }
section() { printf '\n== %s\n' "$1"; }

# check <description> <command...>
check() {
    description="$1"; shift
    if "$@" >/dev/null 2>&1; then ok "$description"; else ko "$description"; fi
}

has() { grep -qF -- "$2" "$1"; }
lacks() { ! grep -qF -- "$2" "$1"; }

# extract <page> <site folder>: writes the files of a page.
extract() {
    mkdir -p "$2"
    files="$(awk -v dest="$2" -f "$EXTRACT" "$1")" || return 1
    printf '%s\n' "$files" | sed 's/^/    wrote /'
}

# build <site folder> [options]: douze, as the tutorial runs it.
build() {
    site="$1"; shift
    (cd "$site" && "$DOUZE" --plain "$@") > "$WORK/build.log" 2>&1
    status=$?
    if [ $status -ne 0 ]; then sed 's/^/    | /' "$WORK/build.log"; fi
    return $status
}

serve_pid=""
# serve_start <site folder>: douze serve, waits until it answers.
serve_start() {
    (cd "$1" && exec "$DOUZE" serve --port "$PORT") > "$WORK/serve.log" 2>&1 &
    serve_pid=$!
    i=0
    while [ $i -lt 60 ]; do
        if curl -fs "http://localhost:$PORT/" >/dev/null 2>&1; then return 0; fi
        sleep 0.5
        i=$((i + 1))
    done
    sed 's/^/    | /' "$WORK/serve.log"
    return 1
}
serve_stop() {
    [ -n "$serve_pid" ] && kill "$serve_pid" 2>/dev/null && wait "$serve_pid" 2>/dev/null
    serve_pid=""
}
trap 'serve_stop' EXIT

# get <path> <file>: saves a page of the server.
get() { curl -s -o "$2" "http://localhost:$PORT$1"; }

# wait_for <path> <string>: until the served page has the string (a rebuild).
wait_for() {
    i=0
    while [ $i -lt 40 ]; do
        if curl -fs "http://localhost:$PORT$1" 2>/dev/null | grep -qF -- "$2"; then return 0; fi
        sleep 0.25
        i=$((i + 1))
    done
    return 1
}

echo "Pages: $PAGES"
echo "Work:  $WORK"

# --- installation page ---

section "Installation page"
INSTALL_PAGE="$PAGES/guide/install.md"
check "the installation page exists" test -f "$INSTALL_PAGE"

installed=0
if [ -z "${DOUZE:-}" ]; then
    installed=1
    # The command of the page, as a reader copies it.
    command="$(grep -m 1 '^curl -fsSL .*/install\.sh | sh -s -- --preview$' "$INSTALL_PAGE")"
    if [ -z "$command" ]; then
        ko "the installation page has the install.sh command"
        exit 1
    fi
    echo "  \$ $command"
    if sh -c "$command" > "$WORK/install.log" 2>&1; then
        ok "the install script ran"
    else
        sed 's/^/    | /' "$WORK/install.log"
        ko "the install script ran"
    fi
    sed 's/^/    | /' "$WORK/install.log"
    # "Open a new terminal": a new login shell, which reads the profile the installer changed.
    DOUZE="$(bash -lc 'command -v douze' 2>/dev/null || true)"
    if [ -n "$DOUZE" ]; then ok "a new shell finds douze on PATH ($DOUZE)"; else ko "a new shell finds douze on PATH"; exit 1; fi
fi

version="$("$DOUZE" --version 2>&1)"
if printf '%s' "$version" | grep -qE '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$'; then
    ok "douze --version prints a version: $version"
else
    ko "douze --version prints a version (got: $version)"
fi
echo "$version" > "$WORK/version.txt"
check "douze --help lists serve" sh -c "\"$DOUZE\" --help | grep -q 'douze serve'"

section "Installation page: create your first site"
HELLO="$WORK/hello-douze"
extract "$INSTALL_PAGE" "$HELLO"
check "the page gives _content/index.md" test -f "$HELLO/_content/index.md"
if serve_start "$HELLO"; then
    ok "douze serve answers on http://localhost:$PORT/"
    get / "$WORK/page.html"
    check "the page has the heading" has "$WORK/page.html" "<h1>Hello, Douze</h1>"
    check "the server adds the live reload script" has "$WORK/page.html" '<script src="/__douze/livereload.js"></script>'
    printf '\nEdited while serving.\n' >> "$HELLO/_content/index.md"
    check "an edit is rebuilt and served" wait_for / "Edited while serving."
    check "the output has no live reload script" lacks "$HELLO/_site/index.html" "livereload"
else
    ko "douze serve answers on http://localhost:$PORT/"
fi
serve_stop
check "douze doctor succeeds" sh -c "cd '$HELLO' && '$DOUZE' doctor"

# --- tutorial ---

TUTORIAL="$PAGES/tutorial"
SITE="$WORK/my-blog"
mkdir -p "$SITE"

section "Unit 1: Setup"
extract "$TUTORIAL/1-setup.md" "$SITE"
check "unit 1 builds" build "$SITE"
check "index.html has the heading" has "$SITE/_site/index.html" "<h1>Welcome to my blog</h1>"
check "a second build runs nothing" sh -c "cd '$SITE' && '$DOUZE' --plain | grep -q ' 0 units executed'"
check ".douze is the cache folder" test -d "$SITE/.douze"
check ".gitignore ignores _site and .douze" sh -c "grep -qx '_site/' '$SITE/.gitignore' && grep -qx '.douze/' '$SITE/.gitignore'"
if serve_start "$SITE"; then
    ok "douze serve answers"
    get / "$WORK/page.html"
    check "served page has the live reload script" has "$WORK/page.html" "/__douze/livereload.js"
    sed 's/This is where I write about learning Douze./I changed this paragraph./' "$SITE/_content/index.md" > "$WORK/index.md" && cp "$WORK/index.md" "$SITE/_content/index.md"
    check "the edited paragraph is served" wait_for / "I changed this paragraph."
    check "the rebuild prints one line" sh -c "grep -q '_content/index.md changed: 1 file written' '$WORK/serve.log'"
else
    ko "douze serve answers"
fi
serve_stop

section "Unit 2: Pages"
extract "$TUTORIAL/2-pages.md" "$SITE"
check "unit 2 builds" build "$SITE"
check "about: title from front matter" has "$SITE/_site/about/index.html" "<h1>About me</h1>"
check "about: description" has "$SITE/_site/about/index.html" "<p>Who writes this blog, and why.</p>"
check "about: skills loop" has "$SITE/_site/about/index.html" "<li>Liquid</li>"
check "about: if learning" has "$SITE/_site/about/index.html" "I am still learning Liquid"
check "projects.html is written to projects/index.html" has "$SITE/_site/projects/index.html" "<h1>My projects</h1>"
check "first post: date and author" has "$SITE/_site/posts/first-post/index.html" "Published on September 1, 2026 by Ada."
check "third post: fences are not Liquid" has "$SITE/_site/posts/third-post/index.html" "{{ title }}"
check "home links to the posts" has "$SITE/_site/index.html" '<a href="/posts/third-post/">What I learned about Liquid</a>'
check "404.md is written to 404.html" has "$SITE/_site/404.html" "<h1>Page not found</h1>"
check "no 404/index.html" test ! -e "$SITE/_site/404/index.html"
if serve_start "$SITE"; then
    status="$(curl -s -o "$WORK/page.html" -w '%{http_code}' "http://localhost:$PORT/nothing-here/")"
    check "a missing page is a 404" test "$status" = 404
    check "a missing page shows 404.html" has "$WORK/page.html" "Page not found"
    check "/about redirects or serves /about/" sh -c "curl -sL 'http://localhost:$PORT/about' | grep -q 'About me'"
else
    ko "douze serve answers"
fi
serve_stop

section "Unit 3: Includes and shortcodes"
extract "$TUTORIAL/3-includes-and-shortcodes.md" "$SITE"
check "unit 3 builds" build "$SITE"
check "about: header include" has "$SITE/_site/about/index.html" '<a class="site-title" href="/">My blog</a>'
check "about: footer include with page.date" has "$SITE/_site/about/index.html" "This page changed on"
check "projects: header include" has "$SITE/_site/projects/index.html" "<header>"
check "second post: block shortcode" has "$SITE/_site/posts/second-post/index.html" '<div class="callout callout-tip"><p>A page with <code>permalink: false</code> is not written at all. That is handy for <strong>drafts</strong>.</p>'
check "second post: inline shortcode" has "$SITE/_site/posts/second-post/index.html" '<span class="badge">Updated</span>'
check "includes are not published" test ! -e "$SITE/_site/header.html" -a ! -e "$SITE/_site/header/index.html"

section "Unit 4: Layouts"
extract "$TUTORIAL/4-layouts.md" "$SITE"
check "unit 4 builds" build "$SITE"
check "home: base layout" has "$SITE/_site/index.html" "<!doctype html>"
check "home: header once" sh -c "test \$(grep -c '<header>' '$SITE/_site/index.html') -eq 1"
check "404 has the base layout" has "$SITE/_site/404.html" "<title>Page not found</title>"
check "post: post layout" has "$SITE/_site/posts/first-post/index.html" '<article class="post">'
check "post: chained to the base layout" has "$SITE/_site/posts/first-post/index.html" "<!doctype html>"
check "post: date and author from the layout" has "$SITE/_site/posts/first-post/index.html" '<time datetime="2026-09-01">September 1, 2026</time> by Ada'
check "post: title once" sh -c "test \$(grep -c '<h1>' '$SITE/_site/posts/first-post/index.html') -eq 1"
check "folder data file is not published" test ! -e "$SITE/_site/posts/posts.yml"
check "stylesheet is copied" has "$SITE/_site/styles/global.css" ".callout {"

section "Unit 5: Data and collections"
extract "$TUTORIAL/5-data-and-collections.md" "$SITE"
check "unit 5 builds" build "$SITE"
check "header: site title from data" has "$SITE/_site/about/index.html" "Ada&#x27;s blog</a>"
check "header: navigation from data" has "$SITE/_site/about/index.html" '<a href="/blog/">Blog</a>'
check "header: current page" has "$SITE/_site/about/index.html" '<a href="/about/" aria-current="page">About</a>'
check "base: title with site title" has "$SITE/_site/about/index.html" "<title>About me | Ada&#x27;s blog</title>"
blog="$SITE/_site/blog/index.html"
check "blog: lists the posts" has "$blog" '<a href="/posts/first-post/">My first post</a>'
check "blog: newest first" sh -c "grep -n 'posts/' '$blog' | head -1 | grep -q third-post"
check "blog: topics" has "$blog" '<a href="/tags/liquid/">liquid</a>'
check "home: two latest posts" sh -c "grep -q 'third-post' '$SITE/_site/index.html' && grep -q 'second-post' '$SITE/_site/index.html' && ! grep -q 'first-post' '$SITE/_site/index.html'"
check "tag page douze" has "$SITE/_site/tags/douze/index.html" "<h1>Posts about douze</h1>"
check "tag page douze lists its posts" sh -c "grep -q first-post '$SITE/_site/tags/douze/index.html' && grep -q second-post '$SITE/_site/tags/douze/index.html' && ! grep -q third-post '$SITE/_site/tags/douze/index.html'"
check "tag page liquid" has "$SITE/_site/tags/liquid/index.html" '<a href="/posts/third-post/">What I learned about Liquid</a>'
check "post: tag links" has "$SITE/_site/posts/third-post/index.html" '<a href="/tags/liquid/">#liquid</a>'
check "feed.xml is written" has "$SITE/_site/feed.xml" '<rss version="2.0">'
check "feed: absolute links" has "$SITE/_site/feed.xml" "<link>https://example.com/posts/third-post/</link>"
check "feed: three items" sh -c "test \$(grep -c '<item>' '$SITE/_site/feed.xml') -eq 3"
check "feed: no layout" lacks "$SITE/_site/feed.xml" "<!doctype html>"
check "sitemap.xml is written" has "$SITE/_site/sitemap.xml" "<loc>https://example.com/about/</loc>"
check "sitemap: no 404 and no tags template" sh -c "! grep -qE '404|/tags/' '$SITE/_site/sitemap.xml'"
check "no feed.xml.liquid in the output" test ! -e "$SITE/_site/feed.xml.liquid"
if command -v xmllint >/dev/null 2>&1; then
    check "feed.xml is well-formed XML" xmllint --noout "$SITE/_site/feed.xml"
    check "sitemap.xml is well-formed XML" xmllint --noout "$SITE/_site/sitemap.xml"
fi

section "Unit 6: Workflows"
# The default workflow, printed before douze.yml exists, is the one the page shows.
(cd "$SITE" && "$DOUZE" --print-workflow) > "$WORK/workflow.yml" 2>&1
check "--print-workflow succeeds" test -s "$WORK/workflow.yml"
# The page writes it with one field per line, Douze prints one line per step: the page's YAML (the first untitled
# yaml fence) is printed back by Douze as the workflow file, so that both are compared in the same notation.
awk '/^```yaml$/ { inside = 1; next } inside && /^```/ { exit } inside { print }' "$TUTORIAL/6-workflows.md" > "$WORK/page-workflow.yml"
(cd "$SITE" && "$DOUZE" --print-workflow -w "$WORK/page-workflow.yml") > "$WORK/page-printed.yml" 2>&1
check "the page shows the printed workflow" cmp -s "$WORK/workflow.yml" "$WORK/page-printed.yml"
diff "$WORK/workflow.yml" "$WORK/page-printed.yml" | sed 's/^/    | /'
extract "$TUTORIAL/6-workflows.md" "$SITE"
(cd "$SITE" && "$DOUZE" doctor) > "$WORK/doctor.log" 2>&1
check "douze doctor succeeds" test $? -eq 0
sed 's/^/    | /' "$WORK/doctor.log"
check "douze doctor lists pagefind" has "$WORK/doctor.log" "pagefind"
check "unit 6 builds" build "$SITE" --timeline timeline.html
post="$SITE/_site/posts/third-post/index.html"
check "headings: ids and anchors" has "$post" '<h2 id="variables">Variables<a class="heading-anchor" href="#variables">'
check "headings: table of contents" has "$post" '<li><a href="#loops">Loops</a></li>'
check "headings: no toc without headings" lacks "$SITE/_site/posts/second-post/index.html" 'class="toc"'
check "highlight: tokens" has "$post" 'style="--0:'
check "highlight: code block" has "$post" '<figure class="code'
check "highlight: stylesheet" has "$SITE/_site/styles/code.css" ".code"
check "base links code.css" has "$post" '/styles/code.css'
check "search: pagefind index" test -f "$SITE/_site/pagefind/pagefind-ui.js"
check "search: page" has "$SITE/_site/search/index.html" "new PagefindUI"
check "search: in the navigation" has "$SITE/_site/index.html" '<a href="/search/">Search</a>'
check "--timeline writes timeline.html" test -s "$SITE/timeline.html"
check "deploy workflow file" test -f "$SITE/.github/workflows/deploy.yml"
check "deploy installs as the installation page says" sh -c "grep -qF 'curl -fsSL https://raw.githubusercontent.com/sebastienros/douze/main/install.sh | sh -s -- --preview --no-modify-path' '$SITE/.github/workflows/deploy.yml'"
# The build of the deploy job: CI set, no cache of a previous run.
(cd "$SITE" && CI=true "$DOUZE" --no-cache -o "$WORK/ci-site") > "$WORK/ci.log" 2>&1
check "a clean build (CI, no cache) succeeds" test $? -eq 0
check "the CI build prints the plain view" has "$WORK/ci.log" "Build succeeded"
check "the clean build has the same pages" sh -c "test -f '$WORK/ci-site/tags/liquid/index.html' && test -f '$WORK/ci-site/pagefind/pagefind.js'"
if serve_start "$SITE"; then
    get /posts/third-post/ "$WORK/page.html"
    check "serve: highlighted post" has "$WORK/page.html" 'style="--0:'
    check "serve: live reload script" has "$WORK/page.html" "/__douze/livereload.js"
    get /pagefind/pagefind-ui.js "$WORK/pagefind.js"
    check "serve: pagefind files" test -s "$WORK/pagefind.js"
else
    ko "douze serve answers"
fi
serve_stop

section "The finished site"
missing=0
for f in $(cd "$SITE" && find . -type f ! -path './_site/*' ! -path './.douze/*' ! -name timeline.html | sed 's|^\./||' | sort); do
    name="$(basename "$f")"
    grep -qF -- "$name" "$TUTORIAL/index.md" || { echo "    not in the tree of tutorial/index.md: $f"; missing=1; }
done
check "the tree of tutorial/index.md has every file" test $missing -eq 0

if [ $installed = 1 ]; then
    section "Installation page: update and uninstall"
    sh -c "$command" > "$WORK/update.log" 2>&1
    check "running the install command again succeeds" test $? -eq 0
    check "it keeps the installed version" has "$WORK/update.log" "is already installed"
    uninstall="$(grep -m 1 '^curl -fsSL .*/install\.sh | sh -s -- --uninstall$' "$INSTALL_PAGE")"
    check "the installation page has the uninstall command" test -n "$uninstall"
    sh -c "$uninstall" > "$WORK/uninstall.log" 2>&1
    check "the uninstall command succeeds" test $? -eq 0
    sed 's/^/    | /' "$WORK/uninstall.log"
    check "~/.douze is removed" test ! -e "$HOME/.douze"
    check "a new shell no longer finds douze" sh -c "! bash -lc 'command -v douze'"
fi

printf '\n%s passed, %s failed (douze %s)\n' "$passed" "$failed" "$version"
if [ "$failed" -gt 0 ]; then
    printf 'Failed:%s\n' "$failures"
    exit 1
fi
