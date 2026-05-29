# EF-117 — Cross-collection `Include` implementation progress

Companion to [`EF-117-include-implementation-plan.md`](EF-117-include-implementation-plan.md).
One section per stage as it lands. Each section records what shipped, how it
was verified, and any design notes that matter for the stages that follow.

---

## Stage 0 — Plumbing, scaffolding, M2M guard

### Changes

- `src/.../Query/Visitors/MongoIncludeCompiler.cs` (new) — scaffold with
  `ClassifyIncludeNavigation(IncludeExpression)` helper that partitions the
  three cases:
  - **Embedded** — returns the navigation; existing path unchanged.
  - **Skip-nav** (`ISkipNavigation`) — throws `InvalidOperationException` with
    the final M2M message citing the EF-117 follow-up.
  - **Cross-collection** — throws `InvalidOperationException` preserving the
    legacy message so the ~500 existing spec-test overrides stay green; the
    navigation name (`<Entity>.<Nav>`) and EF-117 reference are appended.
- `src/.../Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs` —
  both `IncludeExpression` branches now call the shared classifier.
- `src/.../Query/Visitors/MongoProjectionBindingExpressionVisitor.cs` — same,
  for the earlier-running visitor that was missed on the first pass.
- `tests/.../FunctionalTests/Query/IncludeTests.cs` (new) — four Stage 0
  shells:
  - `Include_reference_dependent_to_principal_throws_pending` — asserts the
    current "could not be translated" wrapping; flips in Stage 1.
  - `Include_collection_principal_to_dependents_throws_pending` — asserts
    legacy message + EF-117 ref + navigation name; flips in Stage 2.
  - `ThenInclude_chain_throws_pending` — Customer → Order → Item chain;
    flips in Stage 3.
  - `Include_skip_navigation_throws_not_supported` — asserts the **final**
    M2M message; this is permanent behavior.

### Verification (EF10, `MONGODB_URI` pointing at local docker)

| Suite | Result |
|---|---|
| New `IncludeTests` | 4 / 4 pass |
| `NorthwindInclude*QueryMongoTest` + `NorthwindQueryFilters*` + `NorthwindQueryTagging*` | 518 / 518 pass — no spec-test churn |
| `OwnedEntityTests` | 70 / 70 pass — embedded include path preserved |
| `UnitTests` | 260 / 260 pass |
| `LoggingTests.Vector_query_warning_logged_*` | 4 failures, **pre-existing**; confirmed against a clean stash. Unrelated to Include. |

### Design notes for later stages

- The literal substring `Including navigation 'Navigation' is not supported`
  had to be preserved verbatim — the `'Navigation'` is the EF property name
  (a `nameof` quirk in the original code, not the navigation's actual name)
  and ~500 spec-test overrides match on it. New context (`<Entity>.<Nav>`
  + EF-117 ref) is appended. The full string is replaced by an MQL-assertion
  baseline as each test starts translating.
- The dependent → principal reference test (`Order.Customer`) currently
  fails *before* reaching the classifier — EF's nav-expansion rewrites it
  into a shape `MongoQueryableMethodTranslatingExpressionVisitor` rejects.
  Stage 1 must route this path through the cross-collection include
  machinery so the classifier (and thence the loader) sees it.
- `HasMany().WithMany()` builds cleanly through `MongoModelValidator` — no
  extra guard is needed for the M2M case; the classifier alone is enough.
