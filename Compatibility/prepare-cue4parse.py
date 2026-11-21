"""Generate narrowly scoped compatibility overrides without editing a submodule."""
from pathlib import Path
import sys

source = Path(sys.argv[1])
destination = Path(sys.argv[2])
original = source.read_text(encoding="utf-8-sig")
needle = "if (Ar.Game >= GAME_UE5_2) NormalPrecision = Ar.Read<int>();"
replacement = '''if (Ar.Versions.Options.TryGetValue("Nanite.HasNormalPrecision", out var hasNormalPrecision)
                ? hasNormalPrecision : Ar.Game >= GAME_UE5_2) NormalPrecision = Ar.Read<int>();'''
if original.count(needle) != 1:
    raise SystemExit("CUE4Parse Nanite source changed; review the compatibility override.")
destination.parent.mkdir(parents=True, exist_ok=True)
destination.write_text(original.replace(needle, replacement), encoding="utf-8")
