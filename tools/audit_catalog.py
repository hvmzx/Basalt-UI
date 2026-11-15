"""Compare design/settings-catalog.json with everything basalt itself says.

Per key we search the whole install (python sources, docs) plus the help text captured from
basalt.exe for "--key", "key" (the config spelling) or the bare word; short common words are
therefore weakly checked, dashed names are checked exactly. The other direction lists dashed
names basalt uses that the catalog does not offer.
"""
import json
import re
import sys
from pathlib import Path

BASALT = Path(r"C:\Users\neur0\Documents\basalt")
HERE = Path(r"C:\Users\neur0\Documents\Projects\basalt-ui")
CATALOG = HERE / "design" / "settings-catalog.json"
HELP = ["tools/serve-help.txt", "tools/engine-help.txt", "tools/engine-help2.txt"]

corpus_parts: list[tuple[str, str]] = []
for py in sorted(BASALT.rglob("*.py")):
    try:
        corpus_parts.append((py.relative_to(BASALT).as_posix(), py.read_text(encoding="utf-8", errors="replace")))
    except OSError as exc:
        print("skip", py, exc, file=sys.stderr)
for md in sorted(list(BASALT.rglob("*.md")) + list(BASALT.rglob("*.txt"))):
    try:
        corpus_parts.append((md.relative_to(BASALT).as_posix(), md.read_text(encoding="utf-8", errors="replace")))
    except OSError as exc:
        print("skip", md, exc, file=sys.stderr)
for name in HELP:
    p = HERE / name
    if p.exists():
        corpus_parts.append((p.name, p.read_text(encoding="utf-8", errors="replace")))

corpus = "\n".join(t for _, t in corpus_parts)
where = {name: text for name, text in corpus_parts}
flag_names = set(re.compile(r"--([a-z][a-z0-9]*(?:-[a-z0-9]+)*)").findall(corpus))
quoted = set(re.compile(r"[\"'`(]([a-z][a-z0-9]+(?:-[a-z0-9]+)+)[\"'`)]").findall("\n".join(t for n, t in corpus_parts if n.endswith((".md", ".txt")))))

cat = json.loads(CATALOG.read_text(encoding="utf-8"))
defs = cat["settings"]
catalog_keys = {d["key"] for d in defs}

print(f"corpus: {len(corpus_parts)} files, {len(corpus)} chars; catalog {len(catalog_keys)} keys")

missing: list[tuple[str, str]] = []
for d in defs:
    k = d["key"]
    hit = None
    for name, text in corpus_parts:
        if f"--{k}" in text or f'"{k}"' in text or f"`{k}`" in text or f"'{k}'" in text:
            hit = name
            break
    if hit is None and re.search(rf"(?<![A-Za-z0-9_-]){re.escape(k)}(?![A-Za-z0-9_-])", corpus):
        hit = "(bare word)"
    if hit is None:
        missing.append((k, "/".join(d["scope"])))

print(f"\ncatalog keys never mentioned by basalt ({len(missing)}):")
for k, scope in missing:
    d = next(x for x in defs if x["key"] == k)
    print(f"  {k:<28} scope={scope:<14} type={d['type']:<7} model={d.get('model')}")

skip = ("v1", "api", "chat", "http", "json", "sse", "openai")
extras = sorted(n for n in flag_names | quoted if n not in catalog_keys and not n.startswith(skip))
print(f"\ndashed names basalt uses that the catalog does not have ({len(extras)}):")
for n in extras:
    src = next((name for name, text in corpus_parts if f"--{n}" in text or f'"{n}"' in text), "?")
    print(f"  {n:<28} {src}")
