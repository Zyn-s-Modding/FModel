#!/usr/bin/env python3
"""Expose an immutable queue snapshot without changing the upstream checkout."""
from pathlib import Path
import sys

source, destination = map(Path, sys.argv[1:])
text = source.read_text()
anchor = "    public bool HasQueuedItems => TotalQueued > 0;"
if text.count(anchor) != 1:
    raise SystemExit("The upstream export queue has changed. Update the macOS adapter.")
text = text.replace(anchor, anchor + "\n\n    public IReadOnlyList<IExporter> QueuedItems => _roots.Where(item => _paths.TryGetValue(item.ObjectPath, out var current) && ReferenceEquals(current, item)).Cast<IExporter>().ToArray();")
# Removing and re-adding a path must discard the previous exporter instance.
# Otherwise its old queue entry becomes live again and writes the same output twice.
replacements = {
    "ConcurrentDictionary<string, byte> _paths": "ConcurrentDictionary<string, ExporterBase> _paths",
    "_paths.TryAdd(exporter.ObjectPath, 0)": "_paths.TryAdd(exporter.ObjectPath, exporter)",
    "if (_paths.ContainsKey(exporter.ObjectPath))": "if (_paths.TryGetValue(exporter.ObjectPath, out var current) && ReferenceEquals(current, exporter))",
}
for before, after in replacements.items():
    if text.count(before) != 1:
        raise SystemExit("The upstream export queue has changed. Update the macOS adapter.")
    text = text.replace(before, after)
destination.parent.mkdir(parents=True, exist_ok=True)
destination.write_text(text)
