# Benchmarks on a rented server

A laptop that is also compiling, browsing and running other jobs is a poor stopwatch: the same
benchmark on it varied by over 40% between runs. These scripts rent a Hetzner Cloud server with
dedicated cores for the length of one run, measure there, bring the results back and delete the
server. Run them when a change may touch speed — the read or write path, a runtime or dependency
update — not on every pull request.

```bash
# A change against main, reading the synthetic fixtures (push the branch first):
HEAD=my-branch benchmarks/cloud/hetzner.sh benchmarks/cloud/remote/refs.sh

# The same for writing:
SUITE=write HEAD=my-branch benchmarks/cloud/hetzner.sh benchmarks/cloud/remote/refs.sh

# One commit on the .NET 10 runtime, on .NET 11, and compiled for net11.0:
REF=main benchmarks/cloud/hetzner.sh benchmarks/cloud/remote/runtimes.sh
```

Results land in `artifacts/cloud-bench/<server-name>/` (git-ignored): each pass's Markdown, the
machine (`machine.txt`), the commits, the run's log, and `summary.md` — for every measurement the
fastest pass per variant, the spread between fastest and slowest, and the difference against the
first variant. `python3 benchmarks/cloud/summarize.py <folder>` prints it again.

## What it needs

- The `hcloud` CLI (`brew install hcloud`) and a Hetzner Cloud API token with read and write access,
  in `HCLOUD_TOKEN` or in `.env` at the repository root (`HCLOUD_TOKEN=…` or `HETZNER_API_KEY=…`).
  `.env` is git-ignored; never commit the token.
- The commits to measure pushed to the repository: the server clones it, it never sees your working tree.

## How a run is set up, and why

- **Dedicated cores only** — `TYPE=ccx33` by default (8 dedicated AMD EPYC vCPU, 32 GB, about
  €0.26 an hour; a run takes 10 to 40 minutes). The shared-core types are as noisy as a busy laptop.
- **What is compared runs on one server, in alternating passes** (`PASSES`, default 5). Two servers of
  the same type can sit on different hardware, so a number from one is never compared with a number
  from another. Independent runs — reading and writing, say — can go on separate servers at once, as
  far as the project's limit on dedicated cores allows (a new project's admits one ccx33; a second
  server fails with `dedicated core limit exceeded` and is cleaned up — run it after the first).
- **The fastest pass is the figure**; the spread says how far to trust a difference. A difference
  smaller than the spread of either side is noise.
- **Synthetic data only.** `TriasDev.Tabular.FixtureGenerator` writes the fixtures on the server, the
  same shape as the published ones. No local file leaves the machine; a final check on real files
  stays a local run.
- **Nothing is left behind.** The server and its SSH key are deleted on every exit path, error and
  Ctrl-C included; both carry the label `purpose=tabular-bench`, so
  `hcloud server list -l purpose=tabular-bench` shows a leftover if the script itself was killed.

## Scripts

| Script | Measures |
|---|---|
| `hetzner.sh <remote-script> [results-dir]` | Creates the server, runs the script, fetches `/root/out`, deletes everything. `TYPE`, `LOCATION` (default `nbg1`) and the variables below. |
| `remote/refs.sh` | `BASE` (default `main`) against `HEAD`. `SUITE=read` (default): `TriasDev.Tabular.Benchmarks` over the four synthetic fixtures; `SUITE=write`: `TriasDev.Tabular.WriteComparison`, TriasDev.Tabular's writers only. |
| `remote/runtimes.sh` | `REF` as built (net10.0) on .NET 10 and on .NET 11, and compiled for net11.0 on .NET 11. Same `SUITE`. |
| `remote/setup.sh` | Sourced by both: .NET SDK and runtimes, the machine's description, cloning, fixtures. |
| `summarize.py` | The summary table from a results folder. |

`FIXTURE_SCALE` (below 1) shrinks the fixtures to try a script quickly; `TABULAR_FILES` picks
fixtures; `TABULAR_RUNS` sets the write comparison's runs inside each pass.
