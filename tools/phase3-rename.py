#!/usr/bin/env python3
"""CB-256 phase 3, commit 2: the mechanical ClaudeBuddy -> Orbweaver rename.

Run from anywhere: it works on the tree that contains it (the parent of tools/).

    python tools/phase3-rename.py           # rewrite in place, print a summary
    python tools/phase3-rename.py --residue # also list every ClaudeBuddy left in .cs

It is committed together with its own output so a reviewer checks the commit
rather than reading 2500 changed lines: check out the commit before it, run
this, and the diff against the commit must be empty. Running it twice is a
no-op. It is deleted in the PR's last commit and is not a tool to run again.

Rules, all by syntactic position and never by "every occurrence":

* .cs files: a small C# lexer separates code from comments and from string
  literals (regular, verbatim, interpolated, raw, char). Only CODE is
  rewritten, and that includes the code inside an interpolation hole, so
  $"...{ClaudeBuddySettings.OrbSize}..." follows while "ClaudeBuddySettings"
  as quoted text does not. In code:
    - `namespace ClaudeBuddy[.X]`, `using [static] ClaudeBuddy[.X]`,
      `global::ClaudeBuddy`, and a qualified `ClaudeBuddy.<Name>` -> Orbweaver
    - the identifier ClaudeBuddySettings -> OrbweaverSettings (whole word)
  ClaudeBuddySpeech, ClaudeBuddyHook and every other longer identifier are
  whole-word misses by construction. Strings and comments are human decisions
  for the unit that owns the file; the --residue list is that worklist.
  Three string literals are code in disguise and are named one by one in
  STRING_EDITS below: Brand.AssemblyName (must equal <AssemblyName>), the
  BrandTests row that pins it, and one reflection lookup by type name.
* .axaml: x:Class="ClaudeBuddy.*" and xmlns using:ClaudeBuddy.
* .csproj (not the speech engine's own, which moves with its assembly name in
  another unit): AssemblyName, RootNamespace, the four test InternalsVisibleTo
  entries, and ProjectReference/Compile Include text naming a renamed file.
* .sln: project names and paths for the renamed projects.
* Brand.cs: AssemblyName flips; Legacy.Executable is added once.

Never touched: anything under tools/ClaudeBuddySpeech/ other than the app's
own namespace in the .cs files compiled into the app; any CLAUDE_BUDDY_* or
CLAUDEBUDDY_* token (case-sensitive patterns cannot match them); the bundle id
(lower case); every shell, Inno, PowerShell, YAML, Python and Markdown file.

Bytes in, bytes out: no decoding, no newline translation, so CRLF and LF files
and any BOM come back exactly as they went in apart from the rewritten names.
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SKIP_DIRS = {".git", ".claude", "bin", "obj", "TestResults", "node_modules", "dist", "publish"}
SPEECH_DIR = os.path.join("tools", "ClaudeBuddySpeech")
TEST_ASSEMBLIES = ("UnitTests", "IntegrationTests", "UiTests", "UiScreenshots")

# ---------------------------------------------------------------- C# lexer


def _skip_string(src, i):
    """i is at the start of a string or char literal (possibly with $/@
    prefixes). Returns (end, holes) where holes are (start, end) spans of code
    inside interpolation holes, already resolved recursively by the caller."""
    n = len(src)
    j = i
    dollars = 0
    verbatim = False
    while j < n and src[j] in b"$@":
        if src[j] == ord("$"):
            dollars += 1
        else:
            verbatim = True
        j += 1
    if src[j] == ord("'"):
        j += 1
        while j < n:
            c = src[j]
            if c == ord("\\"):
                j += 2
                continue
            if c == ord("'"):
                return j + 1, []
            j += 1
        return n, []
    # src[j] == '"'
    q = j
    while q < n and src[q] == ord('"'):
        q += 1
    quotes = q - j
    holes = []
    if quotes >= 3:
        # Raw string literal: ends at the same run of quotes. Holes open with
        # `dollars` braces (a single $ means one brace).
        k = q
        while k < n:
            if src[k] == ord('"') and src[k:k + quotes] == b'"' * quotes:
                return k + quotes, holes
            if dollars and src[k] == ord("{") and src[k:k + dollars] == b"{" * dollars:
                # More braces than `dollars` in a row: the extra are content.
                run = k
                while run < n and src[run] == ord("{"):
                    run += 1
                start = run
                end = _skip_code(src, start, stop_at_brace=True)
                holes.append((start, end))
                k = end + 1
                continue
            k += 1
        return n, holes
    if quotes == 2:
        # Empty string "" (or @"" / $"").
        return j + 2, []
    k = j + 1
    while k < n:
        c = src[k]
        if verbatim:
            if c == ord('"'):
                if k + 1 < n and src[k + 1] == ord('"'):
                    k += 2
                    continue
                return k + 1, holes
        else:
            if c == ord("\\"):
                k += 2
                continue
            if c == ord('"'):
                return k + 1, holes
            if c == ord("\n"):
                return k, holes  # unterminated; don't run away
        if dollars and c == ord("{"):
            if k + 1 < n and src[k + 1] == ord("{"):
                k += 2
                continue
            start = k + 1
            end = _skip_code(src, start, stop_at_brace=True)
            holes.append((start, end))
            k = end + 1
            continue
        k += 1
    return n, holes


_code_spans = []


def _skip_code(src, i, stop_at_brace=False):
    """Scan code from i. Record code spans in _code_spans. When stop_at_brace,
    stop at the '}' that closes an interpolation hole and return its index."""
    n = len(src)
    depth = 0
    span_start = i
    k = i
    while k < n:
        c = src[k]
        if c == ord("/") and k + 1 < n and src[k + 1] == ord("/"):
            _code_spans.append((span_start, k))
            e = src.find(b"\n", k)
            k = n if e < 0 else e
            span_start = k
            continue
        if c == ord("/") and k + 1 < n and src[k + 1] == ord("*"):
            _code_spans.append((span_start, k))
            e = src.find(b"*/", k + 2)
            k = n if e < 0 else e + 2
            span_start = k
            continue
        is_str = c in (ord('"'), ord("'"))
        if c in b"$@":
            m = k
            while m < n and src[m] in b"$@":
                m += 1
            is_str = m < n and src[m] == ord('"') and (k == 0 or not _ident(src[k - 1]))
        if is_str and c == ord("'") and k > 0 and _ident(src[k - 1]):
            is_str = False  # never true in C#, but keeps the lexer honest
        if is_str:
            _code_spans.append((span_start, k))
            end, holes = _skip_string(src, k)
            k = end
            span_start = k
            continue
        if stop_at_brace:
            if c == ord("{"):
                depth += 1
            elif c == ord("}"):
                if depth == 0:
                    _code_spans.append((span_start, k))
                    return k
                depth -= 1
        k += 1
    _code_spans.append((span_start, n))
    return n


def _ident(b):
    return b == ord("_") or 48 <= b <= 57 or 65 <= b <= 90 or 97 <= b <= 122


def code_spans(src):
    _code_spans.clear()
    _skip_code(src, 0)
    return sorted(s for s in _code_spans if s[1] > s[0])


CS_RULES = [
    # namespace / using / using static / alias target, then a qualified name.
    (re.compile(rb"\b((?:namespace|using)\s+(?:static\s+)?(?:\w+\s*=\s*)?)ClaudeBuddy\b"), rb"\1Orbweaver"),
    (re.compile(rb"\bglobal::ClaudeBuddy\b"), rb"global::Orbweaver"),
    (re.compile(rb"(?<![\w.])ClaudeBuddy(?=\s*\.\s*[A-Z])"), rb"Orbweaver"),
    (re.compile(rb"\bClaudeBuddySettings\b"), rb"OrbweaverSettings"),
]
CS_ANY = re.compile(rb"ClaudeBuddy\w*")

# String literals that are code in disguise, by file and exact bytes.
STRING_EDITS = {
    "Brand.cs": [
        (b'internal const string AssemblyName = "ClaudeBuddy";',
         b'internal const string AssemblyName = "Orbweaver";'),
    ],
    os.path.join("tests", "UnitTests", "BrandTests.cs"): [
        (b'[InlineData(nameof(Brand.AssemblyName), Brand.AssemblyName, "ClaudeBuddy")]',
         b'[InlineData(nameof(Brand.AssemblyName), Brand.AssemblyName, "Orbweaver")]'),
    ],
    os.path.join("tests", "UiTests", "RemoteScanTests.cs"): [
        (b'.Assembly.GetType("ClaudeBuddy.TeamLinks")', b'.Assembly.GetType("Orbweaver.TeamLinks")'),
    ],
}


def _eol(src, at):
    return b"\r\n" if src.find(b"\r\n", at) == src.find(b"\n", at) - 1 else b"\n"


def brand_extras(rel, src):
    """Legacy.Executable in Brand.cs and its BrandTests pin, added once. It gets
    no EachLegacyNameDiffersFromTheCurrentOne row: ("ClaudeBuddy", "Orbweaver")
    is byte for byte the DataDirName row already there, and xUnit folds a
    duplicate theory row into the existing case, so it would test nothing."""
    if rel == "Brand.cs" and b"internal const string Executable" not in src:
        anchor = b'            internal const string SingleInstanceMutexName = "ClaudeBuddy_SingleInstance_Mutex";'
        at = src.index(anchor) + len(anchor)
        nl = _eol(src, at)
        add = (nl + b"            // The executable every build before phase 3 shipped as: ClaudeBuddy.exe"
               + nl + b"            // on Windows, Contents/MacOS/ClaudeBuddy in the macOS bundle."
               + nl + b'            internal const string Executable = "ClaudeBuddy";')
        src = src[:at] + add + src[at:]
    if rel == os.path.join("tests", "UnitTests", "BrandTests.cs") and b"Brand.Legacy.Executable" not in src:
        a1 = (b'    [InlineData(nameof(Brand.Legacy.SingleInstanceMutexName), Brand.Legacy.SingleInstanceMutexName,'
              b' "ClaudeBuddy_SingleInstance_Mutex")]')
        at = src.index(a1) + len(a1)
        nl = _eol(src, at)
        src = (src[:at] + nl + b'    [InlineData(nameof(Brand.Legacy.Executable), Brand.Legacy.Executable, "ClaudeBuddy")]'
               + src[at:])
    return src


def rewrite_cs(rel, src, residue):
    out = []
    last = 0
    for s, e in code_spans(src):
        out.append(src[last:s])
        chunk = src[s:e]
        for rx, rep in CS_RULES:
            chunk = rx.sub(rep, chunk)
        out.append(chunk)
        last = e
    out.append(src[last:])
    new = b"".join(out)
    for old, rep in STRING_EDITS.get(rel, []):
        if old in new:
            new = new.replace(old, rep, 1)
    new = brand_extras(rel, new)
    if residue is not None:
        spans = code_spans(new)
        for m in CS_ANY.finditer(new):
            in_code = any(s <= m.start() < e for s, e in spans)
            line = new.count(b"\n", 0, m.start()) + 1
            residue.append((rel, line, "code" if in_code else "text", m.group().decode()))
    return new


# ---------------------------------------------------------------- the rest

AXAML_RULES = [
    (re.compile(rb'(x:Class=")ClaudeBuddy(\.)'), rb"\1Orbweaver\2"),
    (re.compile(rb'("using:)ClaudeBuddy(["\.])'), rb"\1Orbweaver\2"),
]
TESTS = rb"(?:" + rb"|".join(t.encode() for t in TEST_ASSEMBLIES) + rb")"
CSPROJ_RULES = [
    (re.compile(rb"(<AssemblyName>)ClaudeBuddy((?:\." + TESTS + rb")?</AssemblyName>)"), rb"\1Orbweaver\2"),
    (re.compile(rb"(<RootNamespace>)ClaudeBuddy((?:\.Tests)?</RootNamespace>)"), rb"\1Orbweaver\2"),
    (re.compile(rb'(<InternalsVisibleTo Include=")ClaudeBuddy(\.' + TESTS + rb'" />)'), rb"\1Orbweaver\2"),
    (re.compile(rb'(Include="[^"]*?)ClaudeBuddy((?:\.' + TESTS + rb')?\.csproj")'), rb"\1Orbweaver\2"),
    (re.compile(rb'(Include="[^"]*?)ClaudeBuddySettings(\.cs")'), rb"\1OrbweaverSettings\2"),
]
SLN_RULES = [
    (re.compile(rb'(Project\("\{[0-9A-Fa-f-]+\}"\) = ")ClaudeBuddy((?:\.' + TESTS + rb')?",)'), rb"\1Orbweaver\2"),
    (re.compile(rb'([\\"/])ClaudeBuddy((?:\.' + TESTS + rb')?\.csproj")'), rb"\1Orbweaver\2"),
]


def apply(rules, src):
    for rx, rep in rules:
        src = rx.sub(rep, src)
    return src


def files():
    for dirpath, dirnames, filenames in os.walk(ROOT):
        dirnames[:] = sorted(d for d in dirnames if d not in SKIP_DIRS)
        for f in sorted(filenames):
            yield os.path.relpath(os.path.join(dirpath, f), ROOT)


def check_internals_visible_to():
    """The four InternalsVisibleTo entries must equal the four test assembly
    names byte for byte: a miss is 500 compile errors, so say it here first."""
    app = open(os.path.join(ROOT, "Orbweaver.csproj"), "rb").read()
    granted = set(re.findall(rb'<InternalsVisibleTo Include="([^"]+)" />', app))
    for t in TEST_ASSEMBLIES:
        p = os.path.join(ROOT, "tests", t, "Orbweaver." + t + ".csproj")
        name = re.search(rb"<AssemblyName>([^<]+)</AssemblyName>", open(p, "rb").read()).group(1)
        if name not in granted:
            sys.exit("InternalsVisibleTo does not grant %s (%s)" % (name.decode(), p))


def main():
    show = "--residue" in sys.argv
    residue = [] if show else None
    changed = 0
    for rel in files():
        ext = os.path.splitext(rel)[1].lower()
        in_speech = rel.startswith(SPEECH_DIR + os.sep)
        if ext == ".cs":
            fn = lambda r, s: rewrite_cs(r, s, residue)
        elif ext == ".axaml" and not in_speech:
            fn = lambda r, s: apply(AXAML_RULES, s)
        elif ext == ".csproj" and not in_speech:
            fn = lambda r, s: apply(CSPROJ_RULES, s)
        elif ext == ".sln":
            fn = lambda r, s: apply(SLN_RULES, s)
        else:
            continue
        path = os.path.join(ROOT, rel)
        src = open(path, "rb").read()
        new = fn(rel, src)
        if new != src:
            with open(path, "wb") as f:
                f.write(new)
            changed += 1
    check_internals_visible_to()
    print("phase3-rename: %d file(s) rewritten" % changed)
    if show:
        for rel, line, where, tok in residue:
            print("%s:%d\t%s\t%s" % (rel.replace(os.sep, "/"), line, where, tok))


if __name__ == "__main__":
    main()
