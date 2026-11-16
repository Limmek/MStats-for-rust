---
applyTo: "MStats.cs"
description: "Rust plugin implementation guidance for MStats, including Oxide and Carbon hook/API compatibility."
---

# MStats.cs Instructions

- Follow the repository's `AGENTS.md`; keep plugin version 2.1.0 and database schema v1 unless explicitly asked otherwise.
- Verify every Rust hook's current parameter types and overloads against Oxide.Rust and Carbon's manifest before implementing it. Carbon compatibility must be marked Oxide-compatible.
- Preserve the `MySqlLibrary` alias. Do not use the unqualified `MySql` type in `namespace Oxide.Plugins`.
- Database queries must remain asynchronous, parameterized, and safe to enqueue from hooks. Avoid expensive world scans and synchronous database operations.
- Reuse existing schema tables and config logging switches when adding statistics. Update README logging and limitations when observable behavior changes.
- Compile against the current Rust managed assemblies after edits; report if a live Carbon check was not performed.