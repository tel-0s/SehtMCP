"""Exercise the real stdio MCP transport using only the Python standard library.

Run after dotnet build. --local additionally inspects installed game data and NIFs.
No game/modlist files are written. Artifacts are confined to artifacts/smoke-*.
"""
import argparse
import base64
import json
from pathlib import Path
import queue
import subprocess
import threading
import time

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--local", action="store_true")
    parser.add_argument("--server", type=Path, help="Published executable to verify instead of the Debug DLL")
    options = parser.parse_args()
    output = ROOT / "artifacts" / f"smoke-{time.time_ns()}"
    output.mkdir(parents=True)
    config = json.loads((ROOT / "seht.local.json").read_text()) if options.local else {}
    config["workspace"] = str(output)
    settings = output / "config.json"
    settings.write_text(json.dumps(config))
    server = ROOT / "src/SehtMcp/bin/Debug/net10.0/SehtMcp.dll"
    command = [str(options.server.resolve())] if options.server else ["dotnet", str(server)]
    process = subprocess.Popen(command + ["--config", str(settings)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
    messages = queue.Queue()
    stderr = []

    def read_stdout():
        for line in process.stdout:
            try:
                messages.put(json.loads(line))
            except Exception as exc:
                messages.put(exc)
        messages.put(RuntimeError("Server exited: " + "".join(stderr)[-4000:]))

    threading.Thread(target=read_stdout, daemon=True).start()
    threading.Thread(target=lambda: stderr.extend(process.stderr.readlines()), daemon=True).start()
    next_id = 0

    def request(method, params=None):
        nonlocal next_id
        next_id += 1
        process.stdin.write(json.dumps({"jsonrpc": "2.0", "id": next_id, "method": method, "params": params or {}}) + "\n")
        process.stdin.flush()
        while True:
            message = messages.get(timeout=180)
            if isinstance(message, Exception):
                raise message
            if message.get("id") == next_id:
                assert "error" not in message, message
                return message["result"]

    def call(tool_name, **arguments):
        result = request("tools/call", {"name": tool_name, "arguments": arguments})
        assert not result.get("isError"), (tool_name, result)
        data = result.get("structuredContent")
        if data is None:
            data = json.loads(result["content"][0]["text"])
        return data

    try:
        initialized = request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "seht-smoke", "version": "1"}})
        assert initialized["serverInfo"]["name"] == "SehtMCP"
        process.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        process.stdin.flush()
        tools = request("tools/list")["tools"]
        (output / "tools.json").write_text(json.dumps(tools, indent=2))
        assert len(tools) >= 35
        assert {"navmesh_generate", "navmesh_generate_from_cell", "navmesh_create", "navmesh_get", "navmesh_nearest", "navmesh_link_door", "navmesh_preview"} <= {t["name"] for t in tools}
        assert request("resources/list")["resources"]
        assert request("prompts/list")["prompts"]
        status = call("seht_status")
        types = call("record_types")["items"]
        print(f"MCP initialized: {len(tools)} tools, {len(types)} record types", flush=True)
        for name in ["Weapon", "Npc", "Quest", "Spell", "ConstructibleObject", "Cell", "ScriptEntry"]:
            schema = call("record_schema", type=name, depth=2)
            (output / f"schema-{name}.json").write_text(json.dumps(schema, indent=2))
        masters = ["Skyrim.esm"] if options.local else []
        session = call("plugin_create", filename="SehtSmoke.esp", kind="esp-fe", author="SehtMCP", masters=masters)["session"]
        batch = [{"op": "create", "type": "Keyword", "editorId": "SehtSmokeKeyword", "alias": "kw"}, {"op": "create", "type": "Weapon", "editorId": "SehtSmokeBlade", "fields": {"Name": "Seht Smoke Blade", "Keywords": ["$kw"], "BasicStats": {"Damage": 12, "Value": 120, "Weight": 8}, "Data": {"AnimationType": "OneHandSword", "Speed": 1, "Reach": 1}}}]
        call("plugin_batch", session=session, expectedRevision=0, operations=batch)
        data = call("record_get", session=session, formKey="000801:SehtSmoke.esp")
        assert data["record"]["BasicStats"]["Damage"] == 12
        valid = call("plugin_validate", session=session)
        assert valid["valid"], valid
        saved = call("plugin_save", session=session, expectedRevision=1, relativePath="SehtSmoke.esp")
        assert Path(saved["path"]).read_bytes()[:4] == b"TES4"
        assert call("plugin_open", path=saved["path"])
        assert call("plugin_manifest", session=session)
        package = call("plugin_package", session=session, output="SehtSmoke.zip")
        assert Path(package["path"]).exists()
        print("Authored, validated, saved, and reopened an ESL-flagged ESP", flush=True)
        nav_session = call("plugin_create", filename="SehtNavSmoke.esp", kind="esp")["session"]
        nav_cell = call("record_create", session=nav_session, expectedRevision=0, type="Cell", editorId="SehtNavRoom", fields={})["result"]["formKey"]
        geometry = {"vertices": [[-256,-256,0],[256,-256,0],[256,256,0],[-256,256,0]], "triangles": [[0,1,2],[0,2,3]]}
        call("navmesh_generate", session=nav_session, expectedRevision=1, cell=nav_cell, dryRun=True, **geometry)
        baked = call("navmesh_generate", session=nav_session, expectedRevision=1, cell=nav_cell, settings={"agentRadius":16}, walkableSeeds=[[0,0,0]], **geometry)
        nav_key = baked["result"]["components"][0]["formKey"]
        assert call("navmesh_get", session=nav_session, navmesh=nav_key)["triangleCount"] > 0
        assert call("navmesh_nearest", session=nav_session, cell=nav_cell, x=0, y=0, z=0)["distance"] <= 4
        assert call("plugin_validate", session=nav_session)["valid"]
        nav_saved = call("plugin_save", session=nav_session, expectedRevision=2, relativePath="SehtNavSmoke.esp")
        assert call("plugin_open", path=nav_saved["path"])
        nav_preview = request("tools/call", {"name":"navmesh_preview", "arguments":{"session":nav_session, "cell":nav_cell, "pitch":90}})
        assert not nav_preview.get("isError"), nav_preview
        (output / "navmesh-preview.png").write_bytes(base64.b64decode(next(c["data"] for c in nav_preview["content"] if c["type"] == "image")))
        (output / "navmesh-report.json").write_text(json.dumps({"baked":baked, "saved":nav_saved}, indent=2))
        print("Baked, inspected, previewed, validated, saved, and reopened interior NAVM/NAVI", flush=True)
        if options.local:
            source = str(Path(config["gameDirectory"]) / "Data" / "Skyrim - Meshes0.bsa")
            meshes = call("archive_search", archive=source, query="longsword.nif", limit=5)
            if not meshes["items"]:
                source = str(Path(config["gameDirectory"]) / "Data" / "Skyrim - Meshes1.bsa")
                meshes = call("archive_search", archive=source, query="longsword.nif", limit=5)
            assert meshes["items"], "No sword NIF found"
            mesh = meshes["items"][0]["path"]
            inspected = call("nif_inspect", path=mesh, archive=source)
            (output / "nif-inspect.json").write_text(json.dumps(inspected, indent=2))
            preview = request("tools/call", {"name": "nif_preview", "arguments": {"path": mesh, "archive": source}})
            assert not preview.get("isError"), preview
            image = next(c for c in preview["content"] if c["type"] == "image")
            (output / "nif-preview.png").write_bytes(base64.b64decode(image["data"]))
            print(f"Read and rendered installed NIF: {mesh}", flush=True)
            scene_session = call("plugin_create", filename="SehtNavScene.esp", kind="esp", masters=["Skyrim.esm"])["session"]
            scene_cell = call("record_create", session=scene_session, expectedRevision=0, type="Cell", editorId="SehtNavScene", fields={})["result"]["formKey"]
            call("cell_place", session=scene_session, expectedRevision=1, cell=scene_cell, baseFormKey="04EFCF:Skyrim.esm", editorId="SehtFloor", x=1024, y=512, z=64, rz=1.57079632679)
            archives = [str(p) for p in (Path(config["gameDirectory"]) / "Data").glob("Skyrim - Meshes*.bsa")]
            scene = call("navmesh_generate_from_cell", session=scene_session, expectedRevision=2, cell=scene_cell, archives=archives)
            (output / "navmesh-scene.json").write_text(json.dumps(scene, indent=2))
            assert call("plugin_validate", session=scene_session)["valid"]
            call("plugin_save", session=scene_session, expectedRevision=3, relativePath="SehtNavScene.esp")
            scene_preview = request("tools/call", {"name":"navmesh_preview", "arguments":{"session":scene_session,"cell":scene_cell}})
            assert not scene_preview.get("isError"), scene_preview
            (output / "navmesh-scene.png").write_bytes(base64.b64decode(next(c["data"] for c in scene_preview["content"] if c["type"] == "image")))
            print("Baked navmesh from a transformed vanilla Dwemer floor NIF read from BSA", flush=True)
            call("script_write", name="SehtSmoke", source="Scriptname SehtSmoke\n\nInt Function Add(Int a, Int b) Global\n  Return a + b\nEndFunction\n")
            compilation = call("script_compile", name="SehtSmoke")
            assert compilation["success"], compilation
            print("Compiled Papyrus with installed Creation Kit compiler", flush=True)
        (output / "report.json").write_text(json.dumps({"tools": len(tools), "types": len(types), "status": status, "saved": saved, "local": options.local}, indent=2))
        print(f"PASS: {output}", flush=True)
    finally:
        process.stdin.close()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
        (output / "stderr.log").write_text("".join(stderr))


if __name__ == "__main__":
    main()
