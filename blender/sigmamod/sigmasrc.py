# SigmaMod's own sources, read rather than retyped: the parts library (lua/sigma/parts.lua: each part's signature and
# the comment above it), the calls SigmaMod's shipped scripts make (the templates "Add a part" fills in), and the kit
# the Sigma's script is divided into ("-- ##" sections of sigma_assets/apocasigma.lua). From the installed SigmaMod,
# so they match the game's version; in the SigmaMod repo, from the repo.
import os
import re
from pathlib import Path

from . import gamedata, paths

KIT_SCRIPT = "apocasigma"


def _first(*cands):
    for c in cands:
        if c and os.path.isfile(c):
            return c
    return ""


def repo_dir():
    d = Path(__file__).resolve().parent.parent.parent
    return str(d) if (d / "lua" / "sigma" / "parts.lua").is_file() else ""


def parts_lua():
    sm, repo = paths.sigmamod_dir(), repo_dir()
    return _first(sm and os.path.join(sm, "lua", "sigma", "parts.lua"), repo and os.path.join(repo, "lua", "sigma", "parts.lua"))


def asset_dirs():
    out = []
    sm, repo = paths.sigmamod_dir(), repo_dir()
    for d in (sm and os.path.join(sm, "sigma_assets"), repo and os.path.join(repo, "assets")):
        if d and os.path.isdir(d):
            out.append(d)
    return out


def kit_files():
    for d in asset_dirs():
        lua, glb = os.path.join(d, KIT_SCRIPT + ".lua"), os.path.join(d, KIT_SCRIPT + ".glb")
        if os.path.isfile(lua) and os.path.isfile(glb):
            return lua, glb
    return "", ""


_cache = {}


def _cached(key, path, build):
    try:
        mtime = os.path.getmtime(path)
    except OSError:
        return None
    hit = _cache.get(key)
    if hit and hit[0] == (path, mtime):
        return hit[1]
    val = build(path)
    _cache[key] = ((path, mtime), val)
    return val


def code_of(line):
    # a line without its comment (a "--" outside quotes)
    q = None
    for i, ch in enumerate(line):
        if q:
            if ch == q:
                q = None
        elif ch in "\"'":
            q = ch
        elif line.startswith("--", i):
            return line[:i]
    return line


# ---- the parts library

class Part:
    def __init__(self, name, params, doc):
        self.name, self.params, self.doc = name, params, doc
        first = doc.split(". ")[0].split(": ")[0] if doc else ""
        self.summary = (first[:1].upper() + first[1:]).rstrip(".") if first else name


def _parse_parts(path):
    lines = open(path, encoding="utf-8").read().splitlines()
    out = []
    for i, line in enumerate(lines):
        m = re.match(r"function parts\.(\w+)\(([^)]*)\)", line)
        if not m:
            continue
        doc, j = [], i - 1
        while j >= 0 and lines[j].startswith("--"):
            doc.insert(0, lines[j].lstrip("-").strip())
            j -= 1
        out.append(Part(m.group(1), [p.strip() for p in m.group(2).split(",") if p.strip()], " ".join(doc)))
    return out


def _parse_made(path):
    # names parts.lua makes out of an option's value: o.label .. " Pivot", name .. " Hub", o.tank .. " Hose End"
    out = {}
    for m in re.finditer(r'\b(?:o\.)?(\w+) \.\. "([^"]+)"', open(path, encoding="utf-8").read()):
        if m.group(2).startswith(" "):   # an object's name; ":Component" and the like aren't
            out.setdefault(m.group(1), set()).add(m.group(2))
    return out


def made_names():
    """option -> the suffixes parts.lua adds to its value to name an object it makes."""
    p = parts_lua()
    return (_cached("made", p, _parse_made) or {}) if p else {}


def parts_catalog():
    p = parts_lua()
    return (_cached("parts", p, _parse_parts) or []) if p else []


# ---- calls in SigmaMod's shipped scripts

def statements(src):
    """[(first line number, text)] of the script's statements: lines joined until their brackets close."""
    out, buf, depth, start = [], [], 0, 0
    for n, line in enumerate(src.splitlines(), 1):
        code = code_of(line)
        if not buf and not code.strip():
            continue
        if not buf:
            start = n
        buf.append(line)
        depth += sum(code.count(c) for c in "({[") - sum(code.count(c) for c in ")}]")
        if depth <= 0:
            out.append((start, "\n".join(buf)))
            buf, depth = [], 0
    if buf:
        out.append((start, "\n".join(buf)))
    return out


def node_names(glb, under=None):
    """The names of a .glb's objects; with under, only that object and what is below it."""
    js = gamedata.glb_json(glb) or {}
    nodes = js.get("nodes", [])
    if under is None:
        return {n.get("name") for n in nodes if n.get("name")}
    top = next((i for i, n in enumerate(nodes) if n.get("name") == under), None)
    out, todo = set(), [top] if top is not None else []
    while todo:
        n = nodes[todo.pop()]
        out.add(n.get("name"))
        todo += n.get("children", [])
    return out


def _parse_examples(d):
    found = {}
    files = sorted(f for f in os.listdir(d) if f.endswith(".lua"))
    files.sort(key=lambda f: f != KIT_SCRIPT + ".lua")   # the Sigma's first: it is the car
    for f in files:
        glb = os.path.join(d, f[:-4] + ".glb")
        names = node_names(glb) if os.path.isfile(glb) else set()
        try:
            src = open(os.path.join(d, f), encoding="utf-8").read()
        except OSError:
            continue
        for _, st in statements(src):
            m = re.match(r"\s*parts\.(\w+)\s*\(", st)
            if m and st.count("\n") < 4:   # a call, not a loop or a page of table
                found.setdefault(m.group(1), []).append((st.strip(), names))
    return found


def examples():
    dirs = asset_dirs()
    return (_cached("examples", dirs[0], _parse_examples) or {}) if dirs else {}


def names_in(code, names):
    """The object names a piece of script uses, in order: strings, table keys and g.nodes.X fields that are in names."""
    out = []
    for line in code.splitlines():
        c = code_of(line)
        for m in re.finditer(r'"([^"]*)"|\b(\w+)\s*=(?!=)|g\.nodes\.(\w+)', c):
            s = m.group(1) if m.group(1) is not None else (m.group(2) or m.group(3))
            if s in names and s not in out:
                out.append(s)
    return out


def rename(code, mapping):
    """The code with object names changed (old -> new): strings, table keys, g.nodes.X fields."""
    if not mapping:
        return code
    out = []
    for line in code.splitlines():
        c = code_of(line)
        rest = line[len(c):]

        def sub(m):
            if m.group(1) is not None:
                return '"' + mapping.get(m.group(1), m.group(1)) + '"'
            if m.group(2) is not None:
                n = m.group(2)
                return f'["{mapping[n]}"] =' if n in mapping else m.group(0)
            n = m.group(3)
            return f'g.nodes["{mapping[n]}"]' if n in mapping else m.group(0)
        out.append(re.sub(r'"([^"]*)"|\b(\w+)\s*=(?!=)|g\.nodes\.(\w+)', sub, c) + rest)
    return "\n".join(out)


# ---- the kit: the Sigma's script in sections, each made of roles (the objects it needs, by their names in the Sigma)

class Role:
    def __init__(self, name, flags, kind, text):
        self.name, self.optional, self.multi, self.kind = name, "?" in flags, "*" in flags, kind or "mesh"
        label, _, rest = text.partition(". ")
        self.label, self.help = label.strip().rstrip("."), rest.strip()


class Section:
    def __init__(self, title, required):
        self.title, self.required = title, required
        self.desc, self.roles, self.code = [], [], []
        self.needs = []    # sections whose roles its lines name

    def role(self, name):
        return next((r for r in self.roles if r.name == name), None)


HEADER = re.compile(r"^-- ## (.+?)(?: \[(required)\])?\s*$")
ROLE = re.compile(r"^-- - (.+?)([?*]*)(?: \[(\w+)\])?: (.+)$")
LEGEND = re.compile(r"^-- \[(\w+)\]: (.+)$")


def _parse_kit(path):
    lua, glb = path, os.path.splitext(path)[0] + ".glb"
    sections, cur, model, kinds = [], None, None, {}
    for line in open(lua, encoding="utf-8").read().splitlines():
        lg = LEGEND.match(line)
        if lg and cur is None:
            kinds[lg.group(1)] = lg.group(2).strip()
            continue
        m = HEADER.match(line)
        if m:
            cur = Section(m.group(1).strip(), bool(m.group(2)))
            sections.append(cur)
            continue
        mm = re.match(r'\s*g\.model\s*=\s*"([^"]+)"', line)
        if mm:
            model = mm.group(1)
        if re.match(r'\s*template\s*"', line):
            cur = None
        if cur is None:
            continue
        r = ROLE.match(line)
        if r and not cur.code:
            cur.roles.append(Role(r.group(1).strip(), r.group(2), r.group(3), r.group(4)))
        elif line.startswith("-- ") and not cur.code and not cur.roles:
            cur.desc.append(line[3:])
        else:
            cur.code.append(line)
    owner = {r.name: s for s in sections for r in s.roles}
    for s in sections:
        s.desc = " ".join(s.desc)
        s.code = "\n".join(s.code).strip("\n")
        for n in names_in(s.code, set(owner)):
            if owner[n] is not s and owner[n] not in s.needs:
                s.needs.append(owner[n])
    return {"sections": sections, "model": model, "glb": glb, "lua": lua, "roles": owner, "kinds": kinds}


def kit():
    lua, glb = kit_files()
    return _cached("kit", lua, _parse_kit) if lua else None


def sigma_list(code, name):
    """The names in the list literal of the code that holds name (a role with any number of objects)."""
    m = re.search(r'\{[^{}]*"' + re.escape(name) + r'"[^{}]*\}', code)
    return re.findall(r'"([^"]+)"', m.group(0)) if m else [name]


def _drop(st, name):
    """The statement without name as a list item, a keyed table's entry, or an option its line marks optional
    ("-- optional: a, b"); None when the line can't do without it (it's what the line is about)."""
    q = re.escape(name)
    optional = {o.strip() for m in re.finditer(r"--\s*optional:\s*([\w ,]+)", st) for o in m.group(1).split(",")}
    m = re.search(r'(\w+)\s*=\s*"' + q + r'"', code_of(st.replace("\n", " ")))
    if m and m.group(1) in optional:
        pats = [r'\s*\b' + m.group(1) + r'\s*=\s*"' + q + r'"\s*,', r',\s*\b' + m.group(1) + r'\s*=\s*"' + q + r'"']
    elif m:
        return None   # an option the line needs
    else:
        pats = [r'\s*"' + q + r'"\s*,(?=[^{}]*\})', r',\s*"' + q + r'"(?=[^{}]*\})',      # an item in a list
                r'\s*\b' + q + r'\s*=\s*"[^"]*"\s*,', r',\s*\b' + q + r'\s*=\s*"[^"]*"']   # a table keyed by names
    for pat in pats:
        new, n = re.subn(pat, "", st, count=1)
        if n:
            return new
    return None


def section_code(sec, picks, roles):
    """The section's lines for the objects picked: role name -> [object names] ([] for none). A single object picked
    renames it; a list role's list becomes the objects picked; a role left out takes its option, entry or list item
    with it, or its whole line when the line is about it."""
    out = []
    for _, st in statements(sec.code):
        keep = True
        for name in names_in(st, set(roles)):
            role = roles[name].role(name)
            got = picks.get(name, [])
            if role.multi:
                if not got:
                    keep = False
                    break
                st = re.sub(r'\{[^{}]*"' + re.escape(name) + r'"[^{}]*\}',
                            lambda m: "{ " + ", ".join(f'"{n}"' for n in got) + " }", st, count=1)
            elif not got:
                new = _drop(st, name)
                if new is None:
                    keep = False
                    break
                st = new
        if keep:
            out.append(st)
    single = {n: got[0] for n, got in picks.items() if got and n in roles and not roles[n].role(n).multi and got[0] != n}
    return rename("\n".join(out), single)


def kit_parents(glb):
    """name -> parent name in the Sigma's .glb (None at the top)."""
    js = gamedata.glb_json(glb) or {}
    nodes = js.get("nodes", [])
    parent = {}
    for n in nodes:
        for c in n.get("children", []):
            parent[nodes[c].get("name")] = n.get("name")
    return parent
