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
| M11 | Filtered `Include` with a parameterised `Take(n)`/`Skip(n)` drops the paging stage | `Include(c => c.Orders.OrderBy(o => o.Total).Take(n))` | all orders → n per customer | **Confirmed** (Task 0.4, EF10): `Take(n)` n=1 → customer 1 gets all 3 orders, unordered; `Skip(s)` s=1 likewise drops the stage; constant `Take(1)` is correct | Same |
| M13 | Default `TimeSpan` storage (string) compares/sorts lexicographically | `Where(x => x.Duration > TimeSpan.FromHours(10))` with 1 day | excluded → included | Code reading | Same (Min/Max: see plan decision D-TS) |
| M14 | Set operations mixing Local-kind and default-kind `DateTime` read every row with one kind | `Select(x => x.LocalWhen).Union(Select(x => x.When))` | wrong `Kind` on half the rows | Test comments (`NativeDateTimeKindReadbackTests.cs:409,501-509`) | Native declines |
| M15 | Date parts / `AddX` / `.Date` / `TimeOfDay` over Local-kind `DateTime` computed in UTC (EF-459) | `Select(x => x.LocalWhen.Hour)` | off by machine TZ offset | Test comments | Branch refuses in every mode |
| M16 | Stored-ordering over converted/represented properties (EF-337): enum-as-string sorts alphabetically; int-as-string `>`/`Sum`/`Max` wrong | `OrderBy(x => x.StatusAsString)`, `Sum(x => x.IntAsString)` | alphabetical / `$sum`=0 → CLR order / real sum | Test comments (`NativeDistinctTests`, `NativeGateRoutingTests.cs:192`, `NativeSelectorlessAggregateTests.cs:176`) | Branch refuses in every mode |
| M17 | Grouped case mapping uses ASCII-only `$toUpper` | `GroupBy(x => x.Name.ToUpper())` with "école" | "éCOLE" → "ÉCOLE" | Test comments (`NativeGroupByKeyCaseMappingTests`) | Branch refuses |
| M18 | `GroupBy` followed by `Join` returns empty joined entities | `Orders.GroupBy(...).Select(...).Join(Regions, ...)` | empty inner → matching rows | Test comments (`NativeGroupByTests.cs:297`, `NativeJoinInnerDeclineTests.cs:107`) | Branch hard-declines in Native/NativeOnly |
| M19 | Bridge owned-entity equality ignores nested owned navigations of the compared type | `Where(x => x.Location == loc)` where Location owns `City` | matches when nested part differs | Code reading (g3) | Native: per-property over non-shadow props |
| M20 | Derived-target Include narrowing ignores a converted/represented discriminator and includes `null` for abstract types | `Include(c => c.PriorityOrders)` with converted `_t` | empty include / extra rows | Code reading (g1) | Same until g1 Task 4 |
| M21 | `OfType<AbstractMid>()` emits `$in:[null,...]`, matching documents with no discriminator | `Animals.OfType<AbstractMid>()` | extra rows → none | Code reading (g1) | Same until g1 Task 1 |
| M22 | EF10: a filtered join inner with `Include` loses its inner `Where` | spec `Perform_identity_resolution_reuses_same_instances_across_joins` (`OrderID < 10500`) | unfiltered inner rows → filtered | **Confirmed** (Task 0.4, EF10 hand probe of the spec shape): `Orders.Where(o => o.Id < 10500).Include(o => o.Customer)` as join inner returns orders `[10400, 10450, 10600]` → `[10400, 10450]` (tracking and AsNoTracking). Without the `Include` the same join throws on main | Native declines; fallback same |
| M23 | `DateTime.ToString()` pushed down as `$toString` (ISO), not .NET format | `new Order { ShipName = o.OrderDate.Value.ToString() }` | ISO string → .NET format | Code reading (g7) | Native declines today |
| M24 | `Random.Next`/`Guid.NewGuid()`/`EF.Functions.Random()` evaluated once, not per row (EF-255) | `Where(o => new Random().Next() > 0)` | one value baked → per-row | BREAKING-CHANGES / tests | Branch refuses |
| M25 | Narrowing-converter equality wraps an out-of-range constant | `Where(e => e.Big == 3_000_000_000L)` (long stored as int) | matches stored -1294967296 → no match | **Confirmed** (Task 0.4, EF10): constant and parameter both return the row stored as int `-1294967296` (`[1]`) → `[]` | Same |
| M26 | Converted-value `Distinct` composition: `Distinct().Where(v % 2 == 1)` / `.Contains(Shipped)` / `PlainStatus.Distinct().Union(Status.Distinct())` | see `pins1.tsv` DI:720/758/885/911 | `[]`/false/5 rows → `[2021,999]`/true/3 rows | Test comments; **Confirmed** (Task 0.4) for DI:911 `Select(o => new { S = o.AltStatus }).Union(Select(o => new { S = o.UpperStatus }))` (two `ValueConverter<OrderStatus,string>`s, lower vs upper case): main `[New, New, Shipped, Cancelled, Cancelled]` → `[Cancelled, New, Shipped]` | Native declines |
| M27 | Left-join `GroupBy` over the unmatched side invents values (composite key with non-nullable part, conditional accumulators, `Math.Max`/`Sign`/`ToUpper` keys) | see `pins1.tsv` GJ:252/320/935 | e.g. `(Dora,0)` / count 1 / merged keys → C# semantics | Test comments | Native declines |
| M28 | `TimeOfDay` (ms) unioned with a `TimeSpan` (string) property doesn't dedup / misreads | `Select(x => new{T=x.When.TimeOfDay}).Union(Select(x => new{T=x.Duration}))` | 6 values / misread → 5 | **Confirmed** (Task 0.4, EF10): TimeOfDay-first returns 6 values with `12:00:00.001` twice; Duration-first returns `00:00:04.32…`, `00:00:08.28…` etc. (milliseconds read as ticks) | Native declines |
| M29 | Dictionary indexer on a missing key reads null where C# throws `KeyNotFoundException` | `Where(x => x.Dict["k"] == null)` | matches missing → C# throws | Code reading (g5) | Parity planned (accepted divergence) |
| M30 | Boxed `DateTimeOffset.ToString()` is pushed down as `$toString` over the stored sub-document (EF-217) | `Select(e => ((object)e.Dto).ToString())`, also inside `new { … }` and over a nullable `Dto` | `{"DateTime":"2024-03-15T18:45:30.250Z","Ticks":…,"Offset":-300}` → `15/03/2024 13:45:30 -05:00` | Confirmed (Task 0.4; `DateTimeOffsetMemberProjectionTests.cs:266/268/271`) | Branch: client-evaluated (Native), NativeOnly declines |
| M31 | `string.IsNullOrWhiteSpace` misclassifies a whitespace string containing a tab | `Where(x => string.IsNullOrWhiteSpace(x.Text))` / `!…` with `Text = "   \t "` | positive: row missing; negated: `["whitespace","x"]` → `["x"]` | Confirmed (Task 0.4; `NativeStringIsNullOrTests.cs:97,106`) | Positive native correct; negated declines |
| M32 | `Equals(term, StringComparison.OrdinalIgnoreCase)` doesn't fold non-ASCII | `Posts.Any(p => p.Title.Equals("éCOLE", OrdinalIgnoreCase))` over `"École"` (constant and parameter) | `[]` → `["match"]` | Confirmed (Task 0.4; `NativeOwnedCollectionCorrelatedTests.cs:137,168`) | Constant native correct; parameter declines |
| M33 | Terminal `Contains(null)` over an array-field projection matches arrays that contain null | `Select(r => r.Tags).Contains(null)` with a row `Tags = ["x", null]` | `true` → `false` | Confirmed (Task 0.4; `NativeContainsTerminalTests.cs:193`) | Tolerant test (native declines or answers false) |
| M34 | `Union` of bare owned-collection projections materializes a stored `[]` as `null` | `Where(b => b.Rank <= 3).Select(b => b.Posts).Union(Where(b => b.Rank >= 3).Select(b => b.Posts))`, every `Posts` stored `[]` | `[null]` → `[[]]` | Confirmed (Task 0.4; `NativeBareProjectionTests.cs:481`) | Native declines |
| M35 | Projected `db.Set.Where(correlation).Count()` binds the single collection navigation whatever the correlation compares | `Customers.Select(c => db.Orders.Where(o => o.CustomerId == c.Code).Count())` (`Code != Id`; FK `Orders.CustomerId` → `Customer.Id`) | key-correlated counts `[3, 1, 0]` → `[1, 0, 3]` | Code reading (`ResolveCollectionNavigation` identical on `upstream/main`) + observed under branch DriverLinq (final-fix probe, EF8/EF10); **confirmed on a `main` build** (`dec7e26f`, EF8/EF9/EF10, Task 1.11 oracle) | Native now declines (Task 1.11), so Native falls back to this; pinned by `NativeNonKeyCorrelationTests.Bare_projected_non_key_count_declines_natively_and_falls_back_to_main_behavior` |
| M36 | Nullable `Min`/`Max` reduce `{_v: value}` documents, so a MISSING or null row wins `Min` over every number (LINQ skips nulls) | `Min(x => x.Score)` (`int?`) over Score 5 / missing / null; also `Min(x => (int?)x.Rank)` | `null` → `5` | Confirmed (Task 1.15 probe, `dec7e26f`, EF10) | Native correct (bare `$min`); pinned by `NativeMalformedAggregateAndDistinctTests.Nullable_aggregate_or_distinct_where_main_is_wrong_stays_correct` |
| M37 | Nullable `Min`/`Max` over an empty sequence throw "Sequence contains no elements" (LINQ and EF answer `null`) | `Where(x => x.Title == "none").Min(x => x.Score)`, `.Max(x => (int?)x.Rank)` | throws → `null` | Confirmed (Task 1.15 probe, `dec7e26f`, EF10) | Native correct (`null`) |
| M38 | Projected `Distinct` over a non-nullable `bool` returns `false` twice when one row lacks the field and another stores null | `Select(x => x.Flag).Distinct()` over a MISSING-Flag row and a null-Flag row | `[False, False]` → `[False]` | Confirmed (Task 1.15 probe, `dec7e26f`, EF10): `$$ROOT` grouping keeps `{}` and `{_v: null}` apart and the `BooleanSerializer` reads both as false | Native returns `[False]` (a lone `$group` key merges MISSING and null; it still keeps a stored `false` apart from a null-as-false, as main does); pinned by `NativeMalformedAggregateAndDistinctTests.Nullable_aggregate_or_distinct_where_main_is_wrong_stays_correct("distinct_flag")` |
| M39 | A null-guarded conditional over a nullable field throws when the field is MISSING: the `!= null` test renders as a bare aggregation `$ne: ["$Score", null]`, true for MISSING, so the true branch's `$add` answers null and the `Int32` deserializer throws (valid C# fails on the released path) | `Select(x => x.Score != null ? x.Score.Value + 1 : 0)` (`int?` Score) over a document that omits `Score` | throws `FormatException` ("Cannot deserialize a 'Int32' from BsonType 'Null'") → `0` | Confirmed (final-fix-wave probe on `dec7e26f`, EF10) | Native correct (`0`: the native test is null-safe for MISSING); pinned by `NativeNullableMemberTests.Non_nullable_computed_leaf_over_a_proven_or_coalesced_operand_is_unchanged` (`driverKnownWrong: true`) |

## Probe notes (Task 0.4, not bugs on main)
- M12 (refuted for main; moved out of the wrong-data table): "correlated-collection matcher never checks the outer
  key", `Customers.Select(c => db.Orders.Count(o => o.CustomerId == c.Code))`. **Main throws; branch native returns
  wrong data → plan Task 1.11 (F11).** Main (Task 0.4, EF10) can't translate the correlated `db.Orders.Count(pred)`
  subquery at all (`The LINQ expression 'DbSet<Order>()' could not be translated`, also for the key-correlated
  control). Branch probe (final-fix wave, EF8 + EF10, `Code != Id`): the `Count(pred)` spelling throws the same in all
  three modes, but the native `SelectMany` (`from c … from o in db.Orders.Where(o => o.CustomerId == c.Code)`) and the
  correlated `FirstOrDefault` reducer bind `Customer.Orders` and return key-correlated rows under NativeOnly and
  Native, where DriverLinq throws; `Where(c => db.Orders.Where(o => o.CustomerId == c.Code).Count() > 0)` returns the
  key-correlated `[1, 2]` (correct `[1, 3]`) in all three modes on the branch, DriverLinq included, because the native
  slot populator registers the count `$lookup` before routing (main's RefCount family threw for this shape). Related **main** bug, same shape: `Select(c => db.Orders.Where(o => o.CustomerId ==
  c.Code).Count())` returns the key-correlated count in **all** modes, including DriverLinq, because the driver path's
  `MongoProjectionBindingExpressionVisitor.ResolveCollectionNavigation` ignores the predicate when there is a single
  candidate navigation (same code on `upstream/main`). Tracked as M35.
  **Confirmed by a `main` build (Task 1.11, `dec7e26f`, EF8/EF9/EF10):** every SelectMany (incl. reversed, conjunct,
  nested, nullable-FK), correlated-reducer and `Where(… Count() > 0)` spelling throws `InvalidOperationException` on
  main, key-correlated or not ("could not be translated" / "Unsupported cross-DbSet query"); only the bare projected
  count returns data (M35). Fixed on the branch by Task 1.11: native declines, and the branch DriverLinq count
  predicate (same matcher via the slot populator) throws as main does.
- D-F10: with `Rank` (non-nullable `int`) missing from a document, main returns `0` for the bare spelling
  `Select(x => x.Rank)` **and** for the anonymous spellings `Select(x => new { x.Rank })` /
  `Select(x => new { x.Title, x.Rank })` (`[1, 2, 0]`); only an entity read throws `Document element is missing`.
- EF-358: main reads a stored-`null`/missing owned *hop* collection (`b.Home.Notes`) as `null` and a stored `[]` as
  empty (Ef362 pins). That's main's released behaviour; the branch's change is the EF-358 break, not a main bug.

## Accepted divergences (document; no fix planned)
- Culture-sensitive `StartsWith/EndsWith/Contains(…, CurrentCulture[IgnoreCase])` evaluated ordinally (main and native).
- `DateTime.Now` comparisons are instant comparisons (Local converted to UTC), not tick comparisons.
- `EndsWith` renders `$` (matches before a trailing `\n`) — same on main and native.

## Branch-only doc gaps
- EF-217 (DateTimeOffset `ToString`/concatenation now refused) has no `BREAKING-CHANGES.md` entry.
- EF-358 (null/missing owned collection now materializes as empty) — confirm it's accepted as a break.
