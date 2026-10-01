# Issues that also exist on `main` (track for main, not branch-blocking)

Canonical main for this list: `upstream/main` = `dec7e26f` (2026-10-01). "Confirmed" = observed in a probe run on
`dec7e26f` (see `probes-main.txt`, `ReviewProbeTests.cs.txt`); "test comment" = stated by an existing branch test;
"code reading" = inferred, needs a probe on main before filing. Native branch behaviour is noted so the fix on main can
match it.

Jira: not yet filed. Each row is a candidate EF ticket; draft text, show the owner, then file (Jira MCP: create then
update for Markdown formatting).

## Wrong data on main

| # | Issue | Repro (LINQ) | main result → correct | Evidence | Branch (native) |
|---|---|---|---|---|---|
| M1 | Navigation-less join on a nullable non-key property matches null↔null and missing↔null | `Shipments.Join(Rates, s => s.Region, r => r.Region, ...)` | `[1-11, 2-12, 2-13, 3-12, 3-13]` → `[1-11]` | Confirmed | Same wrong result |
| M2 | Join to a derived TPH `DbSet<Dog>` returns sibling (`Cat`) rows | `Owners.Join(db.Dogs, o => o.Id, d => d.OwnerId, ...)` | `[o1-rex, o1-tom]` → `[o1-rex]` | Confirmed | Same wrong result |
| M3 | String methods / case mapping on a value-converted string compare the stored form | `Docs.Where(d => d.Code.StartsWith("ab"))` (stored `"p_abc"`) | `[]` → `[abc]` | Confirmed | Same |
| M4 | `Distinct`/`Union` treat a missing field and explicit null as different values | `Rows.Select(x => x.Name).Distinct()` | `[null, null, x]` → `[null, x]` | Confirmed | Native correct; `Union` still wrong natively |
| M5 | `== null` projected as a value ignores missing elements | `Select(x => x.Score == null)`, `Select(x => x.Name == null ? 1 : 2)` | `[F,T,F]` → `[F,T,T]` | Confirmed | Native correct |
| M6 | String concatenation with a null operand returns null | `Select(x => x.Title + x.Name)` | `"b"+null → null` → `"b"` | Confirmed | Native correct |
| M7 | Lifted relational comparison projected as a value is true for null/missing | `Select(x => x.Score < 5)` (`int?`) | `[F,T,T]` → `[F,F,F]` | Confirmed | Native declines; fallback same wrong result |
| M8 | Non-nullable property compared with a null parameter matches rows missing the field | `int? p = null; Where(x => x.Rank == p)` / `!=` | `[c]` / `[a,b]` → `[]` / all rows | Confirmed | Native throws NRE today (F7); after fix: parity with main |
| M9 | `HasValue` projected over a missing element answers True | `Select(x => x.Score.HasValue)` | True → False | Test comment (`NativeNullableMemberTests.cs:347`) | Native declines |
| M10 | Converted-bool truthiness: predicates over a `"Y"/"N"`-stored bool | `Posts.Count(p => !p.Flag)` | 0 per row → actual count | Test comment (`NativeOwnedCollectionFilteredCountTests.cs:688`); Any/All variants code reading | Native declines |
| M11 | Filtered `Include` with a parameterised `Take(n)`/`Skip(n)` drops the paging stage | `Include(c => c.Orders.OrderBy(o => o.Total).Take(n))` | all orders → n per customer | Code reading (identical code on main) | Same |
| M12 | Correlated-collection matcher never checks the outer key | `Customers.Select(c => db.Orders.Count(o => o.CustomerId == c.Region))` | binds as `Customer.Orders` → correct correlation | Code reading | Same |
| M13 | Default `TimeSpan` storage (string) compares/sorts lexicographically | `Where(x => x.Duration > TimeSpan.FromHours(10))` with 1 day | excluded → included | Code reading | Same (Min/Max: see plan decision D-TS) |
| M14 | Set operations mixing Local-kind and default-kind `DateTime` read every row with one kind | `Select(x => x.LocalWhen).Union(Select(x => x.When))` | wrong `Kind` on half the rows | Test comments (`NativeDateTimeKindReadbackTests.cs:409,501-509`) | Native declines |
| M15 | Date parts / `AddX` / `.Date` / `TimeOfDay` over Local-kind `DateTime` computed in UTC (EF-459) | `Select(x => x.LocalWhen.Hour)` | off by machine TZ offset | Test comments | Branch refuses in every mode |
| M16 | Stored-ordering over converted/represented properties (EF-337): enum-as-string sorts alphabetically; int-as-string `>`/`Sum`/`Max` wrong | `OrderBy(x => x.StatusAsString)`, `Sum(x => x.IntAsString)` | alphabetical / `$sum`=0 → CLR order / real sum | Test comments (`NativeDistinctTests`, `NativeGateRoutingTests.cs:192`, `NativeSelectorlessAggregateTests.cs:176`) | Branch refuses in every mode |
| M17 | Grouped case mapping uses ASCII-only `$toUpper` | `GroupBy(x => x.Name.ToUpper())` with "école" | "éCOLE" → "ÉCOLE" | Test comments (`NativeGroupByKeyCaseMappingTests`) | Branch refuses |
| M18 | `GroupBy` followed by `Join` returns empty joined entities | `Orders.GroupBy(...).Select(...).Join(Regions, ...)` | empty inner → matching rows | Test comments (`NativeGroupByTests.cs:297`, `NativeJoinInnerDeclineTests.cs:107`) | Branch hard-declines in Native/NativeOnly |
| M19 | Bridge owned-entity equality ignores nested owned navigations of the compared type | `Where(x => x.Location == loc)` where Location owns `City` | matches when nested part differs | Code reading (g3) | Native: per-property over non-shadow props |
| M20 | Derived-target Include narrowing ignores a converted/represented discriminator and includes `null` for abstract types | `Include(c => c.PriorityOrders)` with converted `_t` | empty include / extra rows | Code reading (g1) | Same until g1 Task 4 |
| M21 | `OfType<AbstractMid>()` emits `$in:[null,...]`, matching documents with no discriminator | `Animals.OfType<AbstractMid>()` | extra rows → none | Code reading (g1) | Same until g1 Task 1 |
| M22 | EF10: a filtered join inner with `Include` loses its inner `Where` | spec `Perform_identity_resolution_reuses_same_instances_across_joins` (`OrderID < 10500`) | unfiltered inner rows → filtered | Code reading (g6; spec only asserts `Assert.Same`) | Native declines; fallback same |
| M23 | `DateTime.ToString()` pushed down as `$toString` (ISO), not .NET format | `new Order { ShipName = o.OrderDate.Value.ToString() }` | ISO string → .NET format | Code reading (g7) | Native declines today |
| M24 | `Random.Next`/`Guid.NewGuid()`/`EF.Functions.Random()` evaluated once, not per row (EF-255) | `Where(o => new Random().Next() > 0)` | one value baked → per-row | BREAKING-CHANGES / tests | Branch refuses |
| M25 | Narrowing-converter equality wraps an out-of-range constant | `Where(e => e.Big == 3_000_000_000L)` (long stored as int) | matches stored -1294967296 → no match | Code reading (g2) — **probe first** | Same |
| M26 | Converted-value `Distinct` composition: `Distinct().Where(v % 2 == 1)` / `.Contains(Shipped)` / `PlainStatus.Distinct().Union(Status.Distinct())` | see `pins1.tsv` DI:720/758/885/911 | `[]`/false/5 rows → `[2021,999]`/true/3 rows | Test comments | Native declines |
| M27 | Left-join `GroupBy` over the unmatched side invents values (composite key with non-nullable part, conditional accumulators, `Math.Max`/`Sign`/`ToUpper` keys) | see `pins1.tsv` GJ:252/320/935 | e.g. `(Dora,0)` / count 1 / merged keys → C# semantics | Test comments | Native declines |
| M28 | `TimeOfDay` (ms) unioned with a `TimeSpan` (string) property doesn't dedup / misreads | `Select(x => new{T=x.When.TimeOfDay}).Union(Select(x => new{T=x.Duration}))` | 6 values / misread → 5 | Test comments (`NativeConditionalAndDateTimeTests.cs:501,516`); **main applicability unverified** (EF-218 rewrite not on main) | Native declines |
| M29 | Dictionary indexer on a missing key reads null where C# throws `KeyNotFoundException` | `Where(x => x.Dict["k"] == null)` | matches missing → C# throws | Code reading (g5) | Parity planned (accepted divergence) |

## Accepted divergences (document; no fix planned)
- Culture-sensitive `StartsWith/EndsWith/Contains(…, CurrentCulture[IgnoreCase])` evaluated ordinally (main and native).
- `DateTime.Now` comparisons are instant comparisons (Local converted to UTC), not tick comparisons.
- `EndsWith` renders `$` (matches before a trailing `\n`) — same on main and native.

## Branch-only doc gaps
- EF-217 (DateTimeOffset `ToString`/concatenation now refused) has no `BREAKING-CHANGES.md` entry.
- EF-358 (null/missing owned collection now materializes as empty) — confirm it's accepted as a break.
