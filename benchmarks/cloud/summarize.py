#!/usr/bin/env python3
"""Summarise a cloud benchmark run: for every measurement and variant, the fastest of the passes, the
spread between fastest and slowest, and the difference to the first variant.

    python3 benchmarks/cloud/summarize.py <results-dir>

Reads <results-dir>/pass<N>-<variant>.md, the Markdown the benchmark projects print. In every table it
takes the time column ("ms", or "Time" in seconds) as the value, the columns before it plus the
nearest heading above as the measurement's name, and keeps the allocation and peak columns of the
fastest pass. Variants are ordered as remote.log announces them in pass 1.
"""
import collections
import glob
import os
import re
import sys


def milliseconds(cell):
    cell = cell.replace("*", "").replace(",", "").strip()
    m = re.fullmatch(r"([0-9.]+)\s*(ms|s)?", cell)
    if not m:
        return None
    value = float(m.group(1))
    return value * 1000 if m.group(2) == "s" else value


def tables(path):
    heading, header = "", None
    for line in open(path, encoding="utf-8"):
        line = line.rstrip("\n")
        if line.startswith("#"):
            heading, header = line.lstrip("#").strip(), None
            continue
        if not line.startswith("|"):
            header = None
            continue
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if header is None:
            header = [c.lower() for c in cells]
            continue
        if set("".join(cells)) <= set("-: "):
            continue
        yield heading, header, cells


def main(folder):
    times = collections.defaultdict(list)
    extra = {}
    variants, names = [], []
    files = sorted(glob.glob(os.path.join(folder, "pass*-*.md")),
                   key=lambda p: int(re.search(r"pass(\d+)-", os.path.basename(p)).group(1)))
    for path in files:
        variant = re.search(r"pass\d+-(.+)\.md$", os.path.basename(path)).group(1)
        if variant not in variants:
            variants.append(variant)
        for heading, header, cells in tables(path):
            column = next((i for i, h in enumerate(header) if h in ("ms", "time")), None)
            if column is None or column >= len(cells):
                continue
            value = milliseconds(cells[column])
            if value is None:
                continue
            name = " / ".join(([heading] if heading else []) + [c.replace("**", "") for c in cells[:column]])
            if name not in names:
                names.append(name)
            times[(name, variant)].append(value)
            if value == min(times[(name, variant)]):
                extra[(name, variant)] = {h: cells[i] for i, h in enumerate(header)
                                          if i < len(cells) and ("alloc" in h or "peak" in h)}

    if not variants:
        sys.exit(f"no pass*-*.md files in {folder}")

    # The order the run used, as remote.log announces it ("== <time> pass 1 <variant>").
    log = os.path.join(folder, "remote.log")
    if os.path.exists(log):
        announced = re.findall(r"^== \S+ pass 1 (\S+)$", open(log, encoding="utf-8").read(), re.M)
        variants.sort(key=lambda v: announced.index(v) if v in announced else len(announced))

    first = variants[0]
    print(f"Fastest of {max(len(v) for v in times.values())} passes; spread = (slowest - fastest) / fastest;"
          f" difference against {first}.\n")
    head = "| measurement | " + " | ".join(
        f"{v} | spread" + ("" if v == first else f" | vs {first}") for v in variants) + " | allocated, peak |"
    print(head)
    print("|" + "---|" + "".join("--:|--:|" + ("" if v == first else "--:|") for v in variants) + "---|")
    for name in names:
        row = [name]
        for v in variants:
            values = times.get((name, v))
            if not values:
                row += ["–", "–"] + ([] if v == first else ["–"])
                continue
            fastest = min(values)
            row += [f"{fastest:,.0f} ms", f"{(max(values) - fastest) / fastest * 100:.1f}%"]
            if v != first:
                base = times.get((name, first))
                row.append(f"{(fastest / min(base) - 1) * 100:+.1f}%" if base else "–")
        details = " · ".join(f"{v}: " + ", ".join(extra.get((name, v), {}).values()) for v in variants if extra.get((name, v)))
        row.append(details)
        print("| " + " | ".join(row) + " |")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else ".")
