# Agent Plans — historical archive

This folder holds **historical** design documents and implementation plans produced by AI agents
(via the brainstorming / planning workflow) while building features. They are a record of *what was
done, and why,* at a point in time.

**They are NOT living documentation and NOT authoritative for current behavior.** They are
intentionally **not** updated after the work ships, so they go stale as the code evolves. Read them
only when you need historical context — the original intent, trade-offs, or task sequence behind an
already-implemented feature. Do not rely on them to describe how the provider behaves today.

For the current, authoritative picture, use instead:
- the code itself;
- `README.md` ("What is supported");
- `docs/failing-spec-tests.md` (the maintained list of known gaps, each with a ticket);
- the per-area `AGENTS.md` files under `src/`.

## Layout

```
docs/agentplans/<yyyy-mm-dd>/<feature-name>/<feature-name>.<type>.md
```

- `<yyyy-mm-dd>` — the date the document was authored.
- `<feature-name>` — kebab-case feature slug. A feature's design and implementation share one folder.
- `<type>` — `design` for a design document, `implementation` for an implementation plan.

Example:
`docs/agentplans/2026-06-11/bulk-two-phase-execute-update-delete/bulk-two-phase-execute-update-delete.design.md`

New agent-authored designs and plans should be written here following this convention.
