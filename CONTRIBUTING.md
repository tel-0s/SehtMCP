# Contributing

Use .NET 10 and the pinned NuGet dependencies. Run `dotnet test SehtMCP.sln` and `python scripts/verify_mcp.py` before submitting a change. Use `--local` only with your own configured Skyrim installation. Never commit game assets, compiled Bethesda binaries, personal paths/configuration, or smoke artifacts.

For a new field adapter, add a meaningful fixture that writes and reads an actual plugin and exercises invalid input. For a new nested-record operation, verify the correct parent hierarchy and that unrelated sibling records are not overridden. For NIF parsing, include a synthetic bounds test and document supported versions. Do not advertise CK bridge operations until a concrete implementation and version/capability checks exist.

Maintain revision and rollback guarantees. Keep tool descriptions clear about units, replacement/merge behavior, and output side effects. Prefer a bounded purpose-specific tool over arbitrary code execution. Preserve MCP stdout for protocol data and put diagnostics on stderr.

Dependencies are locked. For updates, review upstream format/API changes, regenerate lockfiles with a normal restore, run all checks, and regenerate `THIRD_PARTY_NOTICES.md` using `python scripts/third_party_notices.py`. Keep license/source notices when distributing runtime binaries. Do not claim an in-game or editor integration test was performed when only binary validation ran.
