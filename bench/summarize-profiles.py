"""
Compares profile runs of several saves side by side: cost per tick, where it goes, and how many of each kind of thing.

  python bench/summarize-profiles.py            (all results labelled profile-*, newest per save)
  python bench/summarize-profiles.py a.txt b.txt
"""
import glob
import os
import re
import sys

ROW = re.compile(r"^\s{2}(.+?)\s{2,}([\d.]+) ms\s+([\d.]+)%\s+([\d.]+) calls/tick")


def parse(path):
    text = open(path, encoding="utf-8", errors="replace").read()
    r = {"file": os.path.basename(path), "sections": {}}
    m = re.search(r"^label:\s+(.*)$", text, re.M)
    r["label"] = m.group(1).strip() if m else r["file"]
    m = re.search(r"^mean:\s+([\d.]+) ms", text, re.M)
    r["mean"] = float(m.group(1)) if m else None
    m = re.search(r"background load:\s+([\d,.]+) cores", text)
    r["load"] = m.group(1).replace(",", ".") if m else "?"
    section = None
    for line in text.splitlines():
        if line.startswith("-- "):
            section = line[3:].strip()
            r["sections"][section] = []
            continue
        if section:
            m = ROW.match(line)
            if m:
                r["sections"][section].append((m.group(1).strip(), float(m.group(2)), float(m.group(3)), float(m.group(4))))
            elif line.strip() == "":
                section = None
    return r


def rows(r, section):
    for key in r["sections"]:
        if key.startswith(section):
            return r["sections"][key]
    return []


def main():
    paths = sys.argv[1:]
    if not paths:
        here = os.path.join(os.path.dirname(os.path.abspath(__file__)), "results")
        latest = {}
        for p in sorted(glob.glob(os.path.join(here, "*-profile-*.txt"))):
            label = re.sub(r"^\d{8}-\d{6}-", "", os.path.basename(p))
            latest[label] = p
        paths = list(latest.values())
    runs = [parse(p) for p in paths]
    runs.sort(key=lambda r: r["mean"] or 0)

    print("== Cost per tick (uninstrumented) and background load")
    for r in runs:
        print(f"  {r['label']:<55} {r['mean'] or 0:7.2f} ms/tick   load {r['load']} cores")

    for section, title in [("Thing ticks by category", "Share of instrumented tick by thing category (count per tick)"),
                           ("Tick sections", "Share by tick section"),
                           ("World tick", "World tick parts"),
                           ("Pawn work: every-tick vs interval", "Pawn work by category")]:
        names = []
        for r in runs:
            for name, *_ in rows(r, section):
                if name not in names:
                    names.append(name)
        if not names:
            continue
        print(f"\n== {title}")
        print("  " + " " * 34 + "".join(f"{r['label'][8:22]:>16}" for r in runs))
        for name in names:
            cells = []
            for r in runs:
                hit = next((x for x in rows(r, section) if x[0] == name), None)
                cells.append(f"{hit[2]:6.1f}% ({hit[3]:5.0f})" if hit else " " * 15)
            print(f"  {name[:34]:<34}" + "".join(f"{c:>16}" for c in cells))

    print("\n== Top thing defs per save")
    for r in runs:
        top = rows(r, "Top 25 thing defs")[:8]
        print(f"  {r['label']}: " + ", ".join(f"{n} {p:.1f}% (x{c:.0f})" for n, _, p, c in top))


if __name__ == "__main__":
    main()
