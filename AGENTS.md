# MStats Agent Guidance

## Project constraints

- Keep the plugin at version 2.1.0 and `DatabaseSchemaVersion` at 1 unless the user explicitly asks to change them. Schema changes require user approval; do not add migrations or backward-compatibility paths by default.
- The same `MStats.cs` must run on Oxide/uMod and Carbon. Use APIs implemented by Carbon's Oxide compatibility layer; do not add framework-specific dependencies.
- Keep all database work asynchronous through `Oxide.Core.MySql`. Never block the server thread, and bind all data values as SQL parameters.
- Preserve the plugin's database-only role. Do not add a UI, website, or unrelated features.

## Validation

- Before adding or changing a Rust hook, verify its exact signature against the current Oxide.Rust hook definitions and Carbon's hook manifest, including overloads and whether Carbon marks it Oxide-compatible.
- Compile against the current Rust Dedicated Server managed assemblies and Oxide references after code changes. A successful Oxide build does not replace checking Carbon hook compatibility.
- Keep the README's Rust protocol, Oxide version, hook matrix, and schema details aligned with verified code. State clearly when something has not been live-tested on both frameworks.

## Local conventions

- Use `MySqlLibrary` as the alias for `Oxide.Core.MySql.Libraries.MySql`; the bare `MySql` name collides with Carbon's `Oxide.MySql` namespace inside `Oxide.Plugins`.
- Reuse existing tables and logging categories where possible. New event logging should use existing schema v1 tables unless the user authorizes a schema change.
- Keep hook handlers defensive about null entities/players and ensure NPCs are not recorded as real players.