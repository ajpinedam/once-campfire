"""Summarizes compare.sh results: medians across rounds, ratios to Rails, and the README's ratios.

    python3 summarize.py RESULTS_DIR [CLIENT]
"""
import glob
import json
import os
import statistics
import sys

WORKLOADS = [("room", "Room page"), ("messages", "Messages page"), ("sidebar", "Sidebar"), ("search", "Search"), ("post", "Post a message")]
LABELS = {"dotnet": ".NET", "rails-1": "Rails, 1 worker", "rails-4": "Rails, 4 workers"}

# The top-level README's "Other implementations" table: req/s on a Ryzen AI MAX+ 395, 4 hardware threads each.
PUBLISHED = {
    "Rails": [241, 413, 552, 435, 273],
    "Django": [170, 196, 615, 315, 154],
    "Laravel": [164, 175, 715, 305, 137],
    "Express": [559, 777, 4125, 1294, 256],
    "Elixir": [722, 1053, 1275, 1156, 801],
    "Go": [3860, 5573, 19753, 7053, 4767],
    "Rust": [36260, 40872, 34672, 33299, 6896],
}

results_dir = sys.argv[1]
client = sys.argv[2] if len(sys.argv) > 2 else "fast"

runs = {}
for path in sorted(glob.glob(os.path.join(results_dir, "*.json"))):
    config = os.path.basename(path).rsplit("-", 1)[0]
    runs.setdefault(config, []).append(json.load(open(path)))

configs = [config for config in ["dotnet", "rails-1", "rails-4"] if config in runs] + sorted(set(runs) - set(LABELS))
median = lambda config, key, field: statistics.median(run[key][field] for run in runs[config])
reference = "rails-4" if "rails-4" in runs else "rails-1" if "rails-1" in runs else None

rounds = ", ".join(f"{LABELS.get(config, config)} ×{len(runs[config])}" for config in configs)
print(f"\nMedian req/s ({rounds}; {client} client)\n")
header = "| Workload | " + " | ".join(LABELS.get(config, config) for config in configs) + " |"
if "dotnet" in runs and reference:
    header += f" .NET ÷ {LABELS[reference]} | p99 {LABELS[reference]} | p99 .NET |"
print(header)
print("|---|" + "---:|" * (header.count("|") - 2))
for key, label in WORKLOADS:
    cells = [f"{median(config, key, 'rps'):,.0f}" for config in configs]
    if "dotnet" in runs and reference:
        cells.append(f"{median('dotnet', key, 'rps') / median(reference, key, 'rps'):,.1f}×")
        cells.append(f"{median(reference, key, 'p99'):,.0f} ms")
        cells.append(f"{median('dotnet', key, 'p99'):,.1f} ms")
    print(f"| {label} | " + " | ".join(cells) + " |")

if "dotnet" in runs and reference:
    print(f"\nThroughput relative to Rails: the README's implementations vs .NET here (÷ {LABELS[reference]})\n")
    names = list(PUBLISHED) + [".NET (here)"]
    print("| Workload | " + " | ".join(names) + " |")
    print("|---|" + "---:|" * len(names))
    for index, (key, label) in enumerate(WORKLOADS):
        published = [f"{PUBLISHED[name][index] / PUBLISHED['Rails'][index]:,.1f}×" for name in PUBLISHED]
        ours = median("dotnet", key, "rps") / median(reference, key, "rps")
        print(f"| {label} | " + " | ".join(published) + f" | **{ours:,.1f}×** |")
