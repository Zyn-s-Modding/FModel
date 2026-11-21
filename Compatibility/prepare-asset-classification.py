"""Share the original explorer's asset categories and action capabilities."""
import pathlib
import re
import sys

source = pathlib.Path(sys.argv[1]).read_text()
usings = "\n".join(line for line in source.splitlines() if line.startswith("using CUE4Parse"))
start = source.index("(AssetCategory, AssetActions) = dummy switch")
expression = source[start:].split("= ", 1)[1].split("\n            };", 1)[0] + "\n    };"
expression = expression.replace("GameVersion", "game")
extension_source = source[source.index("private Task ResolveByExtensionAsync"):]
cases = []
entries = {}
for line in extension_source.splitlines():
    match = re.match(r'\s*case "([^"]+)":', line)
    if match:
        cases.append(match[1])
    match = re.match(r"\s*AssetCategory = (EAssetCategory\.\w+);", line)
    if match and cases:
        for case in cases:
            entries[case] = [match[1], "EBulkType.None"]
    match = re.match(r"\s*AssetActions = (EBulkType\.\w+);", line)
    if match:
        for case in cases:
            if case in entries:
                entries[case][1] = match[1]
    if "break;" in line or "if (!resolve.HasFlag" in line:
        cases = []
arms = "\n".join(f'        "{extension}" => ({category}, {actions}),' for extension, (category, actions) in entries.items())
output = f'''#nullable disable
{usings}
namespace FModel.Desktop;

internal static class AssetClassification
{{
    internal static (EAssetCategory Category, EBulkType Actions) Classify(UObject dummy, EGame game) => {expression}

    internal static (EAssetCategory Category, EBulkType Actions) Extension(string extension) => extension.ToLowerInvariant() switch
    {{
{arms}
        _ => (EAssetCategory.All, EBulkType.None)
    }};
}}
'''
target = pathlib.Path(sys.argv[2])
target.parent.mkdir(parents=True, exist_ok=True)
target.write_text(output)
