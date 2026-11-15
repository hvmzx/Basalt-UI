"""Cross-check every settings-catalog scope against what serve/config.py actually accepts.

Ground truth, read from the install (read-only):
  * the server's own argparse options in serve/config.py
  * MODEL_OPTIONS / ENTRY_OPTIONS in the same file
  * the engine flags, taken from the engine binary with the very regex serve/config.py
    ENGINE_FLAGS_OF uses, so we agree with what the server will accept at start
Then: server block accepts server options minus model/entry options; engine block accepts
model options and engine flags; a model entry accepts model+entry options and engine flags.
"""
import json
import re
from pathlib import Path

BASALT = Path(r"C:\Users\neur0\Documents\basalt")
CATALOG = Path(r"C:\Users\neur0\Documents\Projects\basalt-ui\design\settings-catalog.json")

cfg = (BASALT / "serve" / "config.py").read_text(encoding="utf-8")

server_opts: set[str] = set()
for m in re.finditer(r'add_argument\(([^)]*)\)', cfg, re.S):
    for opt in re.findall(r'"--([a-z0-9][a-z0-9-]*)"', m.group(1)):
        server_opts.add(opt)

def frozenset_of(name: str) -> set[str]:
    m = re.search(rf'{name} = frozenset\(\{{(.*?)\}}\)', cfg, re.S)
    return set(re.findall(r'"([a-z0-9-]+)"', m.group(1))) if m else set()

model_opts = frozenset_of("MODEL_OPTIONS")
entry_opts = frozenset_of("ENTRY_OPTIONS")

print(f"server options: {len(server_opts)}  model options: {len(model_opts)}  entry options: {len(entry_opts)}")
print(f"model options = {sorted(model_opts)}")
print(f"entry options = {sorted(entry_opts)}")

blob = (BASALT / "bin" / "basalt.exe").read_bytes()
engine_flags = {m.decode() for m in re.findall(rb"(?<![\x21-\x7e])--[a-z0-9][a-z0-9-]*(?![\x21-\x7e])", blob)}
print(f"engine flags read from bin/basalt.exe: {len(engine_flags)}   (--spec present: {'--spec' in engine_flags})")

no_variants = {n for n in engine_flags if n.startswith("--no-")}
flags = {f[2:] for f in engine_flags}          # every word the engine binary mentions, --no-* included
legal = {
    "server": (server_opts - model_opts - entry_opts - {"engine-bin", "help", "config"}) | {"api-key"},
    "engine": (model_opts | flags) - entry_opts,
    "entry": entry_opts | model_opts | flags,
}

cat = json.loads(CATALOG.read_text(encoding="utf-8"))
bad = 0
for d in cat["settings"]:
    key = d["key"]
    want = {block for block in ("server", "engine", "entry") if key in legal[block]}
    if "command_line_only" in d["scope"]:
        want = {"command_line_only"}
    got = set(d["scope"])
    if got != want:
        bad += 1
        why = {b: ("yes" if key in legal[b] else "no") for b in ("server", "engine", "entry")}
        print(f"  {key:<28} catalog={sorted(got)!s:<32} basalt={sorted(want)!s:<32} {why}")
print(f"\nscope mismatches: {bad} of {len(cat['settings'])}")

uncovered = sorted(flags - {d["key"] for d in cat["settings"]} - {"engine-bin"})
uncovered = [n for n in uncovered if len(n) > 1 and not n.isdigit()]
print(f"\nengine words basalt accepts that the catalog does not offer ({len(uncovered)}):")
for n in uncovered:
    print(f"  {n}")
