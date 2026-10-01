# Phase 3 scope confirmed against canonical `main` (Task 0.4)

**Canonical main used:** `upstream/main` = `dec7e26fcc87dec0d31fd704a7ffef1ccd90d7f0` (fetched 2026-10-01), exported
with `git archive` (no worktree). All runs are EF10 (`Debug EF10`), `MONGODB_URI`/`ATLAS_URI` unset (per-process
Atlas-local container). No family is EF-version-specific enough to need EF8/EF9.

**Verdicts.** Per pin: **WORKED** = main returned the correct answer (from the test's hand-written expectation, an
in-memory LINQ oracle in the test, or hand-computed from the seed); **THREW** = exception on main; **WRONG** = ran but
returned wrong data (logged in `main-issues.md`). Per family: **IN SCOPE** if at least one representative WORKED;
**OPTIONAL** if none worked (main threw); **OUT** if wrong on main. Inside an in-scope family, only the pins marked
WORKED are parity obligations; a THREW pin there is optional coverage.

## Headline findings

- **Main has no `GroupBy` support** (README: "Not supported: … GroupBy"). Every pin whose query starts with a
  `GroupBy` (or has one as a set-op's *first* operand) threw `InvalidOperationException: … could not be translated`.
  That makes A, B, C, D, E, G, J, K, M and R optional, plus the GroupBy pins inside F, I, L, TimeOfDay, doc
  construction and set-op constant. **Exception:** a `GroupBy` as a set op's *second* operand runs on main (the
  driver lowers it inside `$unionWith`), so family O and NSOP:282 call 1 WORKED.
- Also threw on main: count predicates over reference collections ("Unsupported cross-DbSet query", ref-count family),
  `Skip`/`Take` between two joins (`NotSupportedException`, NCJP + Ef373), bare/arithmetic `$size` projections over
  owned collections (`ArgumentException: Expression of type List<Post> …`, the whole `$size` family and two
  array-leaf computed-sibling pins), and owned-leaf set ops/Distinct.
- **TimeOfDay worked on main** even without the EF-218 bridge rewrite: projection, Distinct, Max, OrderBy, filter,
  boxed, nested and Concat-with-TimeSpan all matched the tests' hand oracles. Families 3.25 and 3.26 are in scope
  (except the GroupBy pin NCDT:409 and the `DateTimeOffset.TimeOfDay` pin NCDT:425, which threw).
- New wrong-on-main results: M30 (boxed `DateTimeOffset.ToString()`, the DTO:266/268/271 pins), M31
  (`IsNullOrWhiteSpace` with a tab), M32 (`Equals(…, OrdinalIgnoreCase)` non-ASCII), M33 (`Select(r => r.Tags)
  .Contains(null)`), M34 (Union of bare owned-collection projections reads `[]` as `null`). M26 is now confirmed for
  DI:911, M28 is confirmed.

**Summary: 37 families. 24 IN SCOPE, 13 OPTIONAL, 0 wholly OUT** (4 individual pins OUT: DTO:266/268/271,
BareProj:481). No family left UNVERIFIED.

## Families

Pin abbreviations as in the plan's Phase 3 table. "Repr." = every listed pin was run (all of them, not a sample), via
the shim below.

| Family | Pins (file:line) → main outcome | Verdict |
|---|---|---|
| A post-group OrderBy/Skip/Take | GB:2502 THREW, GB:3333 THREW, GJ:583 THREW (GroupBy untranslatable) | OPTIONAL |
| B ops after nested GroupBy | GB:3366 THREW, GB:3385 THREW | OPTIONAL |
| C >2 grouping levels | GB:2145 THREW, GB:3917 ×3 THREW | OPTIONAL |
| D group paging before terminal | GB:1275 THREW, GB:1309 THREW | OPTIONAL |
| E post-group Where alias shapes | GB:3435 THREW, GB:3469 ×2 THREW, GB:3748 THREW | OPTIONAL |
| F GetType over a non-entity row | GB:3508 ×2 THREW, GB:3526 ×2 THREW; **GB:3543 WORKED** (`[]`, Distinct row) | IN SCOPE (GB:3543 only) |
| G Distinct over grouped output | DI:200 THREW | OPTIONAL |
| H one-member wrapper equality | DI:217 WORKED (`{S=4042}`), DI:235 WORKED (`{US}`), DI:250 WORKED (`(US)`) | IN SCOPE |
| I non-default key serialization | GB:268 THREW; DI:732 WORKED, DI:744 WORKED, DI:1260 WORKED | IN SCOPE (DI pins) |
| J `$push` over non-default element | GB:3789 ×2 THREW, GB:3798 THREW | OPTIONAL |
| K group accumulator/key gaps | GB:465, 718, 1334, 1438, 1821, 1858, 2447, 2561 all THREW | OPTIONAL |
| L ops after projected-operand set op | SO:1636, 1662, 1688, 1692, 1728, 1780, 1811, 1838 WORKED; SO:1753, 2339 THREW (GroupBy after Union) | IN SCOPE (8 pins) |
| M grouped set-op operand | SO:1213, 1241, 1268 THREW (GroupBy first operand) | OPTIONAL |
| N ops after whole-entity set op | SO:765 WORKED, SO:1062 WORKED | IN SCOPE |
| O join-scope set-op operands | GJ:1237 WORKED `[1,3,7]`, GJ:1246 WORKED `[1,1,3,7,7]`, GJ:1281 WORKED `[1,10,20,30]` | IN SCOPE |
| P case mapping over computed receiver | DI:1176 WORKED (`[{US}]`), DI:1386 WORKED (`1`), both hand-computed from the seed | IN SCOPE |
| Q Join over projected Distinct | DI:2004 WORKED (`FR/EU, UK/EU, US/NA`) | IN SCOPE |
| R paging before grouped join | GJ:625 THREW, GJ:641 THREW | OPTIONAL |
| owned-quantifier / quantifier `$expr` | NCT:371, NOCP:423 (well-formed seed), OwnedCount:849, OwnedCount:864, OwnedAll:430, OwnedAll:441, FilteredCount:420 (`[]`, hand-computed), Cardinality:400 all WORKED | IN SCOPE |
| primitive collections | OwnedCount:833 WORKED, OwnedAll:512 WORKED (all `Tags` empty, so all 5 rows is correct), FilteredCount:405 WORKED | IN SCOPE |
| array-leaf set-op operand | NAP:871, 910, 938 WORKED; NAP:1001 THREW (`Document element is missing for … 'Id'`, shadow-key element) | IN SCOPE |
| array-leaf computed sibling | NAP:620, 649 WORKED; NAP:593, 1228 THREW (`ArgumentException`, `Posts.Count` beside an array leaf) | IN SCOPE |
| array-leaf / nav-leaf alias | NAP:287, 531, OwnedRef:813, 856, BareProj:399 WORKED; Ef362:185, 213 WORKED (see note); CtorOnly:186 THREW (tracking query projects an owned entity without its owner) | IN SCOPE |
| array-leaf nested owned element | NAP:382 WORKED | IN SCOPE |
| list-init container | NAP:1814 WORKED | IN SCOPE |
| TimeOfDay downstream / predicate / shape | NCDT:303, 317, 376, 395, 594, 452 WORKED (hand oracles); NCDT:409 THREW (GroupBy), NCDT:425 THREW (`DateTimeOffset.TimeOfDay` unsupported by serializer) | IN SCOPE |
| TOD set-op Concat | NCDT:360 WORKED | IN SCOPE |
| doc construction / client ternary / class-map ternary | NDCP:257, 478, 552, 647, 675, 750, 769, 789, 816 WORKED; NDCP:723 THREW (GroupBy) | IN SCOPE |
| client-evaluated leaves | NCEL:343, 365, 388, 414, 435, 477, 492, 509, 524 all WORKED | IN SCOPE |
| set-op constant / construction / realias | NSOP:213, 462, 469, 476, 485, 493, 571, 584, 592, 600 WORKED; NSOP:512, 544 THREW (GroupBy first operand) | IN SCOPE |
| chained-join paging / reducers | NJT:491 WORKED, NJT:715 WORKED, NJT:721 WORKED; NCJP:69, 83, 98, 111 THREW and Ef373:197 ×6 THREW (`Skip`/`Take` between two joins not supported) | IN SCOPE (NJT pins only) |
| join misc | NJT:827[Split] WORKED (in-memory oracle), NJT:1797 WORKED, NJT:2060 ×2 WORKED, JoinCond:348, 371 ×2 WORKED, JoinNested:150, 165, 180 WORKED, Chained:122 WORKED | IN SCOPE |
| bare computed over `$size` | CompBare:535, 641, 957, 1209 THREW (`ArgumentException`), CompBare:1001 THREW, ProjReducer:196 THREW | OPTIONAL |
| ref-count composition | RefCount:146, 189, 234, 267: every fallback sibling threw "Unsupported cross-DbSet query" (267's threw `BsonSerializationException`) | OPTIONAL |
| DateTimeOffset ToString client | DTO:338 WORKED (`"a15/03/2024 13:45:30 -05:00"`); DTO:266, 268, 271 **WRONG** (BSON JSON, M30) | IN SCOPE (DTO:338); 266/268/271 OUT |
| owned-leaf set-op / Distinct | OwnedRef:934 THREW, OwnedRef:947 THREW (`InvalidCastException`); BareProj:481 **WRONG** (`[null]` for stored `[]`, M34) | OPTIONAL (481 OUT) |
| small singles | Sort:465 WORKED (all keys tie; 4 rows), Sort:562 WORKED, Ef382:360 WORKED (`[B,A,C]`), Bool:161, 197 WORKED, LocalColl:161, 207 WORKED, ClientMethod:137 WORKED, OfType:185 WORKED, ContainsTerminal:121 WORKED, CompProj:470 WORKED, NVST:299, 327, 518 WORKED, Ef425:209 WORKED; Ef382:408 THREW, GroupByCtor:205 THREW | IN SCOPE per item (Ef382:408, GroupByCtor:205 optional) |

Notes:
- **Ef362:185/213:** the populated and stored-`[]` rows read correctly on main. Stored-`null`/missing `Home.Notes` read
  as `null` (main) where the branch's tests expect empty: that's the EF-358 behaviour change, not a main bug. Parity
  should be judged on well-formed rows (or under the EF-358 ruling).
- **NDCP:789/816:** `IdName` is a plain class, so LINQ-to-objects `Union`/`Distinct` would dedup by reference. Main
  dedups structurally (server-side), which is the provider's established set-op semantics. Counted as WORKED.
- **Covered-by-Phase-2 families** (primitive collections → 2.5.1, alias → 2.4.4, nested owned element → 2.3.5) each
  have WORKED representatives.

## UNSURE pins resolved

| Pin | Main outcome | Verdict |
|---|---|---|
| GB:281 subquery in group projection | `InvalidOperationException` (GroupBy) | THREW |
| GB:1558 empty-key `Where(g => g.Key == sentinel)` | GroupBy untranslatable | THREW |
| GB:1615 empty-key Key vs sentinel in accumulator | GroupBy untranslatable | THREW |
| GB:3741 `$push` of owned entities | GroupBy untranslatable | THREW |
| DI:911 Union over two lower/upper-case converters | `[New, New, Shipped, Cancelled, Cancelled]` → correct `[Cancelled, New, Shipped]` | WRONG (M26) |
| GJ:351 key-only accumulator over left join | GroupJoin/GroupBy untranslatable | THREW |
| GJ:727 group key condition over unmatched side | GroupJoin/GroupBy untranslatable | THREW |
| NCT:635 widening cast over converted property | `NotSupportedException: ValueConverterSerializer … does not implement IHasRepresentationSerializer` | THREW |
| NCT:2252 field-to-field over re-encoding converter | `ExpressionNotSupportedException` | THREW |
| NDCP:750 OrderBy over client ternary | `[PAY\|Alpha, REC\|Receive ×3]` (hand-computed) | WORKED |
| NDCP:769 Where over client ternary | `[PAY\|Alpha]` | WORKED |
| NDCP:789 Union over client ternary | `[PAY\|Pay, REC\|Receive]` | WORKED |
| NDCP:816 Distinct over client ternary | `[PAY\|Pay, REC\|Receive]` (structural dedup) | WORKED |
| NSOP:282 `Select(Id).Union(GroupBy(N).Select(Count(Code > 5)))` | `{2,1,0}` = correct `{1,2} ∪ {0,1}` | WORKED |
| NSOP:284 same with grouped operand first | GroupBy untranslatable | THREW |
| NJT:388 `Orders.Skip(1).Take(2).Where(r => r.Owner.Name != "Alice")` | `[Total 30]` = correct (natural order 10, 20, 30) | WORKED |
| NRI:54 `Orders.Join(Buyers, …, (o, b) => new { o, b })` | `5:Alice, 15:Bob, 35:Alice` (dangling O3 dropped) = correct | WORKED |
| RefCount:98 filtered `Where(…).Count() > 0` over reference collection | "Unsupported cross-DbSet query" | THREW |
| Correlated:168 `Equals(@term, OrdinalIgnoreCase)` non-ASCII | `[]` → correct `["match"]` | WRONG (M32) |
| CtorOnly:413 `new object[] { p }` Union | `Document element is missing for … 'Id'` | THREW |
| ClientMethod:180 client method Union | `ExpressionNotSupportedException: ClientMethod(c)` | THREW |
| IsNullOr:106 `!IsNullOrWhiteSpace(Text)` | `["whitespace","x"]` → correct `["x"]` | WRONG (M31) |
| ContainsTerminal:175 `Select(n => n.Parent).Contains(leaf)` | `false` = correct | WORKED |
| ContainsTerminal:193 `Select(r => r.Tags).Contains(null)` | `true` → correct `false` | WRONG (M33) |

24 UNSURE pins (the plan says 8 + 10 + 7, but `pins1.tsv` has 7 UNSURE rows): 8 WORKED (NDCP:750/769/789/816,
NSOP:282, NJT:388, NRI:54, ContainsTerminal:175), 12 THREW, 4 WRONG. The 8 WORKED ones become in-scope parity
obligations.

## Step 5 results (also recorded in `main-issues.md`)

| Item | Probe (EF10, hand-written, raw seed) | Main result | Evidence |
|---|---|---|---|
| M22 | `Customers.Where(Name starts "A")` join `Orders.Where(o => o.Id < 10500).Include(o => o.Customer)` (orders 10400, 10450, 10600 for that customer) | `[10400, 10450, 10600]`, so the inner `Where` is lost (tracking and AsNoTracking). Without the `Include` the join throws | **Confirmed** |
| M25 | long `Value` with `HasConversion<int>()`, stored ints `-1294967296` and `5`; `Where(e => e.Value == 3_000_000_000L)` | `[1]` for both the constant and the parameter (correct: `[]`) | **Confirmed** |
| M11 | `Include(c => c.Orders.OrderBy(o => o.Total).Take(n))`, n=1 | customer 1 gets all 3 orders (unordered); `Skip(s)` also dropped; constant `Take(1)` correct | **Confirmed** |
| M12 | `Customers.Select(c => new { c.Id, N = db.Orders.Count(o => o.CustomerId == c.Code) })` | Throws `The LINQ expression 'DbSet<Order>()' could not be translated`; the key-correlated control throws too | **Refuted** for main (no wrong data; main can't run it) |
| D-F10 | `Rank` missing on one row: `Select(x => new { x.Rank })` and `new { x.Title, x.Rank }` | `[1, 2, 0]`, the same as bare `Select(x => x.Rank)`; entity read throws | Recorded as a probe note |

## Method

A compatibility shim, not hand probes, for the pin sweep. The 54 branch test files holding pins were copied into the
`upstream/main` export and compiled against main with:

- `MongoQueryMode`, a no-op `UseQueryMode` extension on `MongoDbContextOptionsBuilder`, an internal
  `NativeTranslationNotSupportedException`, and `TestQueryMode` pinned to DriverLinq.
- A `NativeModeAssert` whose `NativeAndParity`/`DeclinesCleanly`/`NativeAndExpected`/`TwiceWithDifferentValues` run
  only the DriverLinq leg and record its result or exception.
- Every `Assert.Throws<NativeTranslationNotSupportedException>(…)` rewritten to `ParityOracle.Throws(…)`, which runs
  the pinned query on main's driver path and records the outcome.
- No-op `AssertExecutedMqlContains`/`GetLogMessagesByEventId` and a `StreamingEligibility` stub.

Each pin was then judged against the test's own hand-written expectation, in-memory oracle, or a value hand-computed
from the seed, never against branch driver output. Pins with no oracle in the test were computed by hand: DI:1176/1386,
FilteredCount:405/420, OwnedAll:512, NJT:388, NSOP:282, NDCP:750–816, NRI:54, IsNullOr:106, Correlated:168. Two tests
were edited in the export only to make results readable: Ef362 null-guards the array, and NRI:54 projects to strings.
Step 5 used hand-written probes in the `ReviewProbeTests` pattern.

Scratch (not committed), under
`/private/tmp/claude-502/-Users-arthur-vickers-code-provider3/d776b2a4-0860-4e01-8e78-46b0c77f6ba6/scratchpad/sdd-task-0.4/`:
- Shim: `main-oracle/tests/MongoDB.EntityFrameworkCore.FunctionalTests/Utilities/ParityShim.cs`
- Step 5 probes: `main-oracle/tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ZzParityOracleTests.cs`
- Raw outcomes: `oracle-run1.txt` (per-pin recorded driver results), `run1-results.tsv` / `run1.trx` (per-test
  pass/fail on main), `view1.txt` (per-family join of the two), `zz.txt` (Step 5), `oracle-run3.txt` (NRI:54 recheck)
