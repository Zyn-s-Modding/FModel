"""Adapt the texture decoder ABI without modifying the CUE4Parse submodule."""
from pathlib import Path
import sys

source, destination = map(Path, sys.argv[1:])
text = source.read_text()
old = 'private static extern unsafe void DeswizzleBlockLinearX64('
assert text.count(old) == 1, 'Upstream Tegra ABI changed; review the macOS adapter'
text = text.replace(old, 'private static extern unsafe int DeswizzleBlockLinearX64(')
old = '            DeswizzleBlockLinearX64((ulong) width, (ulong) height, (ulong) depth, ptr, (ulong) data.Length, output, (ulong) output.Length, (ulong) blockHeight, (ulong) formatInfo.BlockBytes);'
assert text.count(old) == 1, 'Upstream Tegra call changed; review the macOS adapter'
text = text.replace(old, old.replace('DeswizzleBlockLinearX64(', 'if (DeswizzleBlockLinearX64(').replace(');', ') == 0)\n                throw new InvalidDataException("Invalid or truncated Tegra block-linear texture.");'))
destination.parent.mkdir(parents=True, exist_ok=True)
destination.write_text(text)
