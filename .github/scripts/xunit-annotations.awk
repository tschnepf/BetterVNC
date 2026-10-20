# Turns the console output of xunit.v3 test executables (default reporter, -noColor) into workflow annotations, so a
# run's results can be read on its summary page and through the checks API:
#   - an error per failed test, with the assertion message, at the first stack frame in tests/
#   - an error for a test log without a summary (the process crashed or hung)
#   - a warning per long-running test (-longRunning), a notice with each assembly's totals
#
#   awk -v ws="$GITHUB_WORKSPACE" -v os="$RUNNER_OS" -f .github/scripts/xunit-annotations.awk test-logs/*.log
#
# POSIX awk only: it runs on the Ubuntu, macOS (BSD awk) and Windows (Git Bash) runners.

function esc(s) { gsub(/%/, "%25", s); gsub(/\r/, "", s); return s }
function prop(s) { s = esc(s); gsub(/:/, "%3A", s); gsub(/,/, "%2C", s); return s }
function relpath(p) {
    gsub(/\\/, "/", p)
    if (root != "" && index(p, root) == 1) p = substr(p, length(root) + 1)
    sub(/^\/_\//, "", p)
    return p
}
function flush() {
    if (test == "") return
    printf "::error %stitle=%s::%s\n", (file != "" ? "file=" prop(file) ",line=" lineno "," : ""), prop(prefix test), msg
    test = ""
}
function endfile(   name, i, lines) {
    flush()
    if (current == "" || summary) return
    name = current; sub(/^.*[\/\\]/, "", name); sub(/\.log$/, "", name)
    lines = ""
    for (i = (ntail > 12 ? ntail - 12 : 0); i < ntail; i++) lines = lines (lines == "" ? "" : "%0A") esc(tail[i % 12])
    printf "::error title=%s::%s\n", prop(prefix name " did not finish"), "No test summary; the last lines were:%0A" lines
}

BEGIN {
    root = ws; gsub(/\\/, "/", root)
    if (root != "" && root !~ /\/$/) root = root "/"
    prefix = (os != "" ? os ": " : "")
}
FNR == 1 { endfile(); current = FILENAME; summary = 0; ntail = 0 }
{ tail[ntail % 12] = $0; ntail++ }

# "    Namespace.Class.Method(args) [FAIL]" starts a failure; its message follows, then "      Stack Trace:".
/ \[FAIL\]\r?$/ {
    flush()
    test = $0; sub(/^[ \t]+/, "", test); sub(/ \[FAIL\]\r?$/, "", test)
    msg = ""; file = ""; lineno = ""; stack = 0
    next
}
test != "" && /^  [^ ]/ { flush() }   # "  Finished: ..." closes the last one
test != "" && /^      Stack Trace:/ { stack = 1; next }
test != "" && /^      Output:/ { stack = 2; next }
test != "" && stack == 1 && file == "" && match($0, /[^ \t]+\.cs\([0-9]+,[0-9]+\)/) {
    frame = substr($0, RSTART, RLENGTH)
    p = frame; sub(/\([0-9]+,[0-9]+\)$/, "", p); p = relpath(p)
    if (p ~ /^tests\//) { file = p; lineno = frame; sub(/^.*\(/, "", lineno); sub(/,.*$/, "", lineno) }
    next
}
test != "" && stack == 0 { l = $0; sub(/^      /, "", l); msg = msg (msg == "" ? "" : "%0A") esc(l) }

/\[Long Running Test\]/ { l = $0; sub(/^[ \t]+/, "", l); printf "::warning title=%s::%s\n", prop(prefix "Long-running test"), esc(l) }
/ Total: [0-9]+, Errors: / {
    flush(); summary = 1
    l = $0; sub(/^[ \t]+/, "", l)
    printf "::notice title=%s::%s\n", prop(prefix "Tests"), esc(l)
}

END { endfile() }
