# ArchSet
Read docs/SPEC.md before any work. It is the product definition (source: the "ArchSet for Rhino" Claude Doc).

## Layout
- schema/        JSON Schemas (source of truth) and operations.json (the shared operation contract)
- library/       seed office library: materials/, templates/, assemblies/ (known), pens.json, rules/,
                 volume-rules/ (faces.json, openings.json: what volume faces and cuts become)
- core/          C# logic, no RhinoCommon: models, schema validation, generator, operation engine,
                 Volumes.cs (volumes -> elements by rules), Sessions.cs (session record and decks)
- core.tests/    xUnit tests; run `dotnet test` from this folder

## How to work
- Write the test first, then the code. A task is done only when `dotnet test` passes.
- core/ must never reference RhinoCommon. plugin/ (later) holds only Rhino-specific code.
- Store lengths in millimeters. Convert only at display.
- Every schema change updates schema/, the C# models, the TS types (once ui/ exists), and tests.
- Never invent code-compliance values (fire ratings, STC, span tables). Leave them null; a null
  `ratings.source` means TODO-SOURCE. Generated variants get ratings only by matching a known assembly.

## One operation set, two callers
Every edit is an operation in schema/operations.json. The panel calls them over HTTP; Claude calls
the same operations as MCP tools. Each mutating operation is one undo step, logged with its author
("person" or "claude"). Adding a feature means adding an operation, not a panel-only code path.

## Volumes are the architecture
Closed volumes are the source of truth. The host reports each volume's faces (normal, size, adjacency,
tag, openings); core/Volumes.cs derives walls, roofs, floors, slabs, soffits and openings from them by
rules in library/volume-rules. Elements are never stored, only derived, so editing a volume or a rule
rebuilds everything. Every element carries the rule that made it, the rules it overrode, and whether
it is an assumed default. Rule precedence: face_tag > volume_type > adjacency > orientation.

## Session record
Every mutating operation is recorded with time, author, session and the request text that led to it.
A pause over 2 hours or a new day starts a new session. deck.make builds slides from starred changes,
each traced back to its change. Undone changes never appear in a deck.
