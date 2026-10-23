# Writes the files that a documentation page shows: every code fence whose info string has a title="<path>" meta,
# such as ```html title="_includes/base.html", is written to <dest>/<path>, in the order of the page, so that a later
# fence of the same file replaces it. Fences without a title, or whose title has a space (a tab label, a command), are
# skipped. Prints the paths it wrote.
#
#   awk -v dest=/tmp/my-blog -f extract-files.awk docs/tutorial/2-pages.md

function flush() {
    if (path == "") return
    file = dest "/" path
    dir = file
    sub(/\/[^\/]*$/, "", dir)
    system("mkdir -p '" dir "'")
    printf "%s", body > file
    close(file)
    print path
}

BEGIN {
    if (dest == "") { print "extract-files.awk: pass -v dest=<folder>" > "/dev/stderr"; exit 2 }
    infence = 0
}

{
    line = $0
    sub(/\r$/, "", line)
    if (!infence) {
        if (match(line, /^[ ]*(```+|~~~+)/)) {
            indent = line
            sub(/[^ ].*$/, "", indent)
            rest = substr(line, length(indent) + 1)
            marker = rest
            sub(/[^`~].*$/, "", marker)
            info = substr(rest, length(marker) + 1)
            infence = 1
            body = ""
            path = ""
            if (match(info, /title="[^"]*"/)) {
                candidate = substr(info, RSTART + 7, RLENGTH - 8)
                if (candidate != "" && candidate !~ / /) path = candidate
            }
        }
        next
    }
    # A closing fence: the same character, at least as many, nothing else.
    probe = line
    sub(/^[ ]*/, "", probe)
    if (substr(probe, 1, length(marker)) == marker) {
        tail = substr(probe, length(marker) + 1)
        if (tail ~ /^[`~]*[ ]*$/ && substr(marker, 1, 1) == substr(probe, 1, 1)) {
            flush()
            infence = 0
            next
        }
    }
    # The fence's indentation is not part of the file.
    if (indent != "" && substr(line, 1, length(indent)) == indent) line = substr(line, length(indent) + 1)
    body = body line "\n"
}
