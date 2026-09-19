"""Generate reference documentation from an actual MCP tools/list response artifact."""
import argparse
import json
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("catalog", type=Path, help="tools.json emitted by verify_mcp.py")
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
tools = json.loads(args.catalog.read_text(encoding="utf-8"))
lines = ["# SehtMCP tool catalog", "", f"Generated from the running 0.1.0 server: **{len(tools)} tools**. See [machine-readable schemas](tools.json) for complete JSON Schema definitions. Tool results use structured JSON plus text; NIF previews also contain PNG image blocks.", ""]
for tool in sorted(tools, key=lambda t: t["name"]):
    lines.extend([f"## `{tool['name']}`", "", tool.get("description", ""), ""])
    schema = tool["inputSchema"]
    required = schema.get("required", [])
    lines.extend(["| Argument | Required | Schema / default |", "| --- | --- | --- |"])
    for name, prop in schema.get("properties", {}).items():
        kind = prop.get("type", prop.get("$ref", "see JSON Schema"))
        if isinstance(kind, list):
            kind = "/".join(kind)
        default = f"; default `{json.dumps(prop['default'])}`" if "default" in prop else ""
        lines.append(f"| `{name}` | {'yes' if name in required else 'no'} | {kind}{default} |")
    lines.append("")
(root / "docs/TOOLS.md").write_text("\n".join(lines), encoding="utf-8")
(root / "docs/tools.json").write_text(json.dumps(tools, indent=2) + "\n", encoding="utf-8")
