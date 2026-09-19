"""Generate runtime dependency notices from restored NuGet metadata (no network)."""
import json
from pathlib import Path
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
assets = json.loads((root / "src/SehtMcp/obj/project.assets.json").read_text())
folders = [Path(p) for p in assets["packageFolders"]]
lines = ["# Third-party notices", "", "SehtMCP is GPL-3.0-only. Runtime dependencies retain their own licenses.", "", "This inventory is generated from pinned, restored NuGet package metadata. It records source repositories for obtaining the corresponding dependency source. Distributors must include applicable notices and provide corresponding source as required by those licenses. No Bethesda executables, game plugins, scripts, or assets are bundled.", "", "| Package | Version | License | Source |", "| --- | --- | --- | --- |"]
for key, library in sorted(assets["libraries"].items()):
    if library["type"] != "package":
        continue
    name, version = key.split("/")
    package = next(p / library["path"] for p in folders if (p / library["path"]).exists())
    tree = ET.parse(next(package.glob("*.nuspec")))
    def find(tag):
        return tree.find(f".//{{*}}{tag}")
    license = find("license")
    license_text = license.text if license is not None else "See package metadata"
    repository = find("repository")
    source = repository.get("url") if repository is not None else ""
    commit = repository.get("commit") if repository is not None else ""
    if source and commit and "github.com" in source:
        source = source.removesuffix(".git") + "/tree/" + commit
    source_link = f"[source]({source})" if source else f"[package](https://www.nuget.org/packages/{name}/{version})"
    lines.append(f"| {name} | {version} | {license_text} | {source_link} |")
lines += ["", "The read-only NIF parser was implemented from the [niftools/nifxml format specification](https://github.com/niftools/nifxml). The specification is not bundled. The MCP implementation uses the [official C# SDK](https://github.com/modelcontextprotocol/csharp-sdk).", ""]
(root / "THIRD_PARTY_NOTICES.md").write_text("\n".join(lines), encoding="utf-8")
