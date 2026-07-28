# EF-322 step 3a — release-tag break check

*Run 2026-08-06 against `EF-322-step3a` @ `f343436f` (clean tree, nothing under `src/` modified, nothing
committed). The measurement is **execution against the published NuGet packages**, not source reading: a
throwaway console harness was compiled seven times over one shared `Program.cs` — once per release-line
package, once with the driver version controlled, once at the pre-3a branch base, once at the tier-1 commit
alone, and once against the assembly built from HEAD — and every build was pointed at the same
`mongodb/mongodb-atlas-local` container with byte-identical seed documents.*

**Tagging.** **VERIFIED** = executed this session, both sides measured (commands in §6). **INFERRED** = drawn
from measured/read facts but not itself observed. **UNVERIFIED** = not established.

---

## 0. Answers, up front

| | |
|---|---|
| **Q1 — was the tier-2 revert's inference right?** | **No. REFUTED, VERIFIED.** The revert commit (`f343436f`) states *"since no native path exists at any release tag, a released package folds this count client-side and returns values, so an upgrading consumer would have seen a working query start throwing."* Measured: at **v8.4.2, v9.1.2 and v10.0.2 alike**, `Select(b => b.Posts.Count)` over an owned collection **throws `ArgumentException` at translation time** — it returns no values at all, for any document, ragged or not. The wrapped spelling `Select(b => new { N = b.Posts.Count })` throws the same. The values-returning behaviour the revert protected is a **branch** behaviour (present at the pre-3a base `f4d50b5a`), not a released one. Tier 2 was therefore throw-before / throw-after, which the rubric's exception-type carve-out covers. §2. |
| **Q2 — does anything TIER 1 ships have that property?** | **Yes. One shape, VERIFIED, and it is a real break.** A **bare projection of a required (non-nullable) property whose stored element is absent or explicitly BSON `null`** returned the CLR default at all three release tags and **throws `InvalidOperationException` at HEAD, under the default `Native` mode**. Introduced by `04eda911` (the tier-1 emit gate) — the pre-3a base `f4d50b5a` still returns values. Every other tier-1 shape probed is byte-identical to the tags. §3. |
| **Q3 — `BREAKING-CHANGES.md` entry?** | **Yes — one entry, scoped to the whole class, not to the bare spelling.** The identical change already landed for **wrapped** projections earlier in this unreleased cycle (base throws where the tags return `0`/`null`), so an entry naming only bare projections would under-warn. There is currently **no** entry covering either half. Draft text and mitigation in §4. |

**What this does *not* say.** It does not say tier 1 should be dropped. The break is narrow, has a working
mitigation, makes projections agree with whole-entity reads (which already throw on the same documents at
every released version), and half of it is already in the tree independently of this slice. It says the owner
must decide it knowingly, and that it must be written down.

---

## 1. Baselines — VERIFIED, not assumed

`git fetch --tags` then `gh release list`:

```
gh release list --limit 1 --json tagName,isLatest   ->  [{"isLatest":true,"tagName":"v10.0.2"}]
gh release list --limit 100 --json tagName          ->  v10.0.2, v9.1.2, v8.4.2, v10.0.1, v8.4.1, v9.1.1, ...
```

Latest overall **`v10.0.2`**; latest non-preview per EF line **`v10.0.2` / `v9.1.2` / `v8.4.2`**. Matches what
the prompt expected. All three tags pin `<CSharpDriverVersion>3.9.0</CSharpDriverVersion>`; the provider's
only driver reference is a NuGet **minimum**, so the published dependency is `MongoDB.Driver (>= 3.9.0)` and
driver 3.10.0 (which HEAD pins) is a permitted configuration of the same released assembly. Both were
measured — see the `tag10` and `tag10@3.10` columns, which agree on every probe, so **no result difference in
this report is attributable to the driver version**.

`MongoQueryMode` exists at **none** of the three tags — VERIFIED two independent ways, rather than cited:

- `git ls-tree <tag> src/MongoDB.EntityFrameworkCore/Infrastructure/MongoQueryMode.cs` returns **zero** rows
  for `v8.4.2`, `v9.1.2` and `v10.0.2`.
- The harness reflects over the loaded provider assembly for the type and for
  `MongoDbContextOptionsBuilder.UseQueryMode`; against the released package it prints
  `### NOMODE MongoQueryMode does not exist in this provider assembly`, against HEAD `### MODE DriverLinq applied`.

So every mode-conditional statement about the slice is vacuous at the published baseline, and the only
baseline behaviour that exists is "whatever the released package does by default".

**Public surface — VERIFIED, no change.** All seven `src/` files 3a touches declare `internal` types
(`MongoQueryExpression`, `MongoSelectDefinition` + `ProjectionAliasTier`, `NativeGroupByBinder`,
`NativeProjectionBinder`, `MongoProjectionBindingExpressionVisitor`,
`MongoQueryableMethodTranslatingExpressionVisitor`, `MongoShapedQueryCompilingExpressionVisitor`), and
`git diff f4d50b5a..f343436f -- src/ | grep -E "^[+-]\s*(public|protected)"` returns **nothing**. No
annotation keys, no `IMongoClientWrapper`/`IMongoDatabaseCreator`/`IMongoTransactionManager` change, no
stored-document-shape change from this slice. **The break in §3 is purely behavioural.**

---

## 2. Q1 — the tier-2 inference, REFUTED

**Method: direct execution against the released packages.** Not source reading, and not the cheaper option
the prompt allowed — the shape turned out to hinge on a provider-side rewrite that a source trace would
plausibly have got wrong in the same direction the revert did.

Ragged fixture, identical to `NativeBareProjectionTests`' own (populated / empty / **element omitted** /
explicit **BSON null** / populated):

| probe | v8.4.2 | v9.1.2 | v10.0.2 | v10.0.2 @ driver 3.10 | base `f4d50b5a` | HEAD `f343436f` |
|---|---|---|---|---|---|---|
| `Select(b => b.Posts.Count)` | **THROW** `ArgumentException` | **THROW** | **THROW** | **THROW** | `2;0;0;0;1` | `2;0;0;0;1` |
| `Select(b => b.Posts.Count())` | **THROW** `ArgumentException` | **THROW** | **THROW** | **THROW** | `2;0;0;0;1` | `2;0;0;0;1` |
| `Select(b => b.Posts.Count(p => …))` | **THROW** `InvalidOperationException` | **THROW** | **THROW** | **THROW** | `2;0;0;0;1` | `2;0;0;0;1` |
| `Select(b => new { N = b.Posts.Count })` (wrapped) | **THROW** `ArgumentException` | **THROW** | **THROW** | **THROW** | `2;0;0;0;1` | `2;0;0;0;1` |
| `Select(b => b.Tags.Count)` (primitive collection) | **THROW** `MongoCommandException` | **THROW** | **THROW** | **THROW** | **THROW** | **THROW** |

The tag exception, in full:

```
System.ArgumentException: Expression of type 'System.Collections.Generic.List`1[Post]' cannot be used for
parameter of type 'System.Linq.IQueryable`1[Post]' of method 'Int32 Count[Post](System.Linq.IQueryable`1[Post])'
(Parameter 'arg0')
```

It is thrown during translation, before any command is sent, so it is **data-independent** — no document
shape makes it return values.

**Consequences, stated plainly.**

1. The revert's factual premise is false. `Select(b => b.Posts.Count)` is **unsupported** at every released
   version. Tier 2's measured cost (a `MongoCommandException` on a missing/null array when the native factory
   declines late) would have been **throw-before / throw-after**, differing only in exception type on an
   unsupported operation — which `AGENTS.md` carves out explicitly as *not* a break.
2. Tier 2 was therefore **not** required to be reverted on breaking-change grounds. That does **not**
   automatically mean it should return: the revert commit also records a genuine engineering objection (the
   late-fallback path inherits the driver's bare `$size` instead of emitting `$ifNull`, which is a real
   defect regardless of whether it is a *break*), and the values-returning behaviour it protects is one an
   *unreleased* branch already has, so the branch would regress against itself. **That is an owner call, not
   mine.** What is settled is the evidence: the release-tag premise is refuted.
3. The general lesson from `2026-07-31-groupby-join-uncorrelated-inner-decline-design.md` §2.7 repeats
   exactly. The revert measured `2;0;0;0;1` on the **branch** and attributed it to the **released package**.
   The branch has EF-357's rewrite of a collection-navigation `.Count`; no released package does.

---

## 3. Q2 — what tier 1 ships, shape by shape

Seven builds, one shared program, one container, identical seeds. `tier1only` = `04eda911`, i.e. the tier-1
emit gate with the tier-2 commit and its revert both absent — included specifically to attribute the break.

### 3.1 Everything that is unchanged — VERIFIED

Byte-identical across **all seven** builds:

| shape | result everywhere |
|---|---|
| bare non-nullable `string` `Select(b => b.Title)` | `p1_two;p2_empty;p3_missing;p4_null;p5_one` |
| bare nullable `string` `Select(b => b.Note)` (element **omitted** on one row) | `n1;<null>;n3;<null>;n5` |
| bare nullable `int?` `Select(b => b.Score)` (element **omitted** on one row) | `10;<null>;30;<null>;50` |
| bare non-nullable `int` `Select(b => b.Rank)` | `1;2;3;4;5` |
| bare primary key `Select(b => b.Id)` | 5 ObjectIds |
| bare primitive array `Select(b => b.Tags)`, all four array states | `t1\|t2;<empty>;<null>;<null>;t9` |
| the six above behind a **parameterized** `Where(b => b.Title.StartsWith(prefix))` | identical to the plain form |
| bare **renamed** element (`HasElementName("rn")`), plain and parameterized | `r1;r2;r3` |
| bare `Guid` / `DateTime` / `decimal` / **value-converted** enum (`HasConversion<string>`) | correct in every build |
| bare `Distinct()` (§7.3 narrowing) | `p2;q0;r_mid;s_hi` |
| bare **operand** `Union` / `Concat` (§7.2 narrowing) | `p2;q0;r_mid;s_hi` / `p2;p2;q0;r_mid;r_mid;s_hi` |
| bare **operand** `Intersect` / `Except` | throws `InvalidOperationException` in **all seven** (pre-existing, no oracle) |
| **trailing** bare projection after `Union` / `Concat` (§7.1) | `p2;p2;q0;r_mid;s_hi` / `p2;p2;q0;r_mid;r_mid;s_hi` |
| bare then `Count()` / `First()` / `Where`+`OrderBy`+`Skip`+`Take` | `5` / `p1_two` / `p3_missing;p4_null` |
| bare owned-hop scalar `Select(b => b.Home.City)` (declines at HEAD) | `Bristol;Cardiff` |
| bare entity `Select(x => x)` | `a;b;c` |

Both narrowings behave at HEAD exactly as at the tags, and the set-op probes are non-vacuous: the operand
form (dedup over the projected value → 4 rows) and the trailing form (dedup over whole entities → 5 rows)
give **different** answers, so a probe cannot pass by landing on the wrong one.

### 3.2 The break — VERIFIED

A **required (non-nullable) CLR property whose stored element is absent, or present but explicitly BSON
`null`**, projected bare:

| shape (stored state) | v8.4.2 | v9.1.2 | v10.0.2 | v10.0.2 @ 3.10 | base | **tier1 only** | **HEAD** | break? |
|---|---|---|---|---|---|---|---|---|
| `Select(b => b.Missing)` — required `int`, element **OMITTED** | `0` | `0` | `0` | `0` | `0` | **THROW** | **THROW** | **YES** |
| `Select(b => b.MissingStr)` — required `string`, element **OMITTED** | `<null>` | `<null>` | `<null>` | `<null>` | `<null>` | **THROW** | **THROW** | **YES** |
| `Select(b => b.MissingStr)` — required `string`, element **BSON NULL** | `<null>` | `<null>` | `<null>` | `<null>` | `<null>` | **THROW** | **THROW** | **YES** |
| `Select(b => b.Missing)` — required `int`, element **BSON NULL** | THROW `FormatException` | THROW | THROW | THROW | THROW | `0` | `0` | no — throw → works |

The HEAD exception:

```
System.InvalidOperationException: Document element 'Missing' is missing for required non-nullable property 'Missing'.
```

**Attribution: `04eda911`, the tier-1 emit gate.** The pre-3a base returns values; the tier-1 commit alone
throws; HEAD throws. Neither the tier-2 commit nor its revert is involved.

**Mode: the DEFAULT one.** `MongoOptionsExtension.QueryMode` defaults to `MongoQueryMode.Native`
(`Infrastructure/MongoOptionsExtension.cs:233`), and the harness configures nothing but `UseMongoDB`, so this
is what an upgrading consumer gets with no code change.

**Not an artefact of composition.** It reproduces with no `Where`, no `OrderBy` (`F15`), behind a constant
`Where` (`F6a`/`F7a`/`F7b`), and behind a parameterized `Where` (`F9`) — i.e. on both the native path and the
late-native-factory-decline fallback path.

**Mechanism — VERIFIED by captured MQL, both sides.** The stage differs only in the alias, but the alias
decides *who deserializes the value*:

```
tag v10.0.2 / base :  aggregate([{ "$match": {"Title":"f3"} }, { "$project": { "_v": "$Missing", "_id": 0 } }])
HEAD               :  aggregate([{ "$match": {"Title":"f3"} }, { "$project": { "Missing": "$Missing", "_id": 0 } }])
```

MongoDB emits **no** key at all for a `$project` of a path the document lacks, in both cases. At the tag the
result document is handed to the **driver's** serializer for the projected type, which is lenient and yields
the CLR default. At HEAD the tier-1 document-path alias routes the read through the provider's own
alias-addressed DOM shaper (`BsonBinding`), which enforces required-property presence and throws. So this is
a **direct consequence of the tier-1 alias scheme**, not an incidental regression: the entire point of tier 1
is that the shaper, not the driver, reads the value.

### 3.3 The same break already exists for wrapped projections — and predates this slice

| shape (element **OMITTED**) | v8.4.2 | v9.1.2 | v10.0.2 | base `f4d50b5a` | tier1 only | HEAD |
|---|---|---|---|---|---|---|
| `Select(b => new { b.Missing })` — required `int` | `0` | `0` | `0` | **THROW** | THROW | THROW |
| `Select(b => new { b.MissingStr })` — required `string` | `<null>` | `<null>` | `<null>` | **THROW** | THROW | THROW |

**The pre-3a base already throws.** So the class of break — *a native projection of a required property whose
element is absent now throws instead of yielding the default* — is already in the unreleased cycle, and 3a
extends it from wrapped projections to bare ones. Any `BREAKING-CHANGES.md` entry must therefore cover both;
one scoped to bare projections would leave the wrapped half undocumented and would misdescribe the change as
new in this slice.

**Control, and the reason this is defensible.** A **whole-entity** read of the same documents —
`context.Blogs.ToList()` and `Select(x => x)` — throws `InvalidOperationException` at **every** version
measured, tags included. The released packages are internally inconsistent: `ToList()` refuses the document
while `Select(b => b.Missing)` quietly answers `0`. HEAD makes them agree.

**Mitigation — VERIFIED, and it works.** `new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.DriverLinq)`
at HEAD restores `0` / `<null>` for every case above. (It is *not* a mitigation an upgrader could have used
before, since the API does not exist at any tag — but it exists in the version that introduces the change,
which is what a `BREAKING-CHANGES.md` mitigation needs.)

**Coverage gap in the slice's own tests — VERIFIED by reading the fixture.**
`NativeBareProjectionTests.SeedRagged` makes `Note` and `Score` **nullable** and always writes `Title` and
`Rank`, so no row exercises a *required* property with an absent element. The slice's functional net cannot
catch this shape. A regression test belongs with whichever fix or acceptance the owner chooses.

### 3.4 Other tag-vs-HEAD differences found — all pre-existing, none 3a's

| shape | tags | base | HEAD | disposition |
|---|---|---|---|---|
| `Select(b => b.Posts)` (owned collection), element omitted / BSON null | `<null>` | `<empty>` | `<empty>` | pre-existing; **already documented** — `BREAKING-CHANGES.md`, *"A missing or `null` embedded array now materializes as an empty collection, not `null`"*, whose text names this exact projection |
| `Select(b => b.Posts.Count)` and friends | THROW | values | values | pre-existing (EF-357); throw → works, not a break. §2 |
| wrapped projection of a required absent property | values | THROW | THROW | pre-existing on the branch; **undocumented**. §3.3 |
| combined-row probes spanning both absent states (`F6`, `F9`, `F15`) | THROW `FormatException` | THROW `FormatException` | THROW `InvalidOperationException` | exception **type** change on a shape that already throws — carved out by the rubric |

---

## 4. Q3 — the `BREAKING-CHANGES.md` recommendation

**Add one entry**, under the existing `## Breaking changes in 8.5.0 / 9.2.0 / 10.1.0` heading, alongside the
embedded-array entry it sits naturally beside. Scope it to the class, not to this slice:

> ### Projecting a required property whose stored element is absent now throws
>
> **Old behavior.** Projecting a non-nullable property — `Select(b => b.Rank)` or
> `Select(b => new { b.Rank })` — returned the CLR default (`0`, `null`, …) for documents in which that
> element was absent or explicitly BSON `null`, because the projected value was deserialized by the driver.
> A whole-entity query over the same documents already threw
> `InvalidOperationException: Document element is missing for required non-nullable property '…'`.
>
> **New behavior.** Projections read the value through the provider's own materializer and apply the same
> required-property rule as a whole-entity read, so the projection throws
> `InvalidOperationException: Document element '…' is missing for required non-nullable property '…'` for
> those documents. Nullable properties (`int?`, `string?`) are unaffected and still read back as `null`.
>
> **Why.** Two read paths over the same document disagreed: `ToList()` refused it, a projection silently
> substituted a default. Only one of those can be right for a property the model declares required.
>
> **Mitigations.** Make the property nullable in the model if the element is genuinely optional — this is the
> recommended fix and it makes the intent explicit. Otherwise backfill the documents
> (`collection.UpdateMany(Builders<BsonDocument>.Filter.Exists("Rank", false), Builders<BsonDocument>.Update.Set("Rank", 0))`).
> As a temporary measure `optionsBuilder.UseMongoDB(…, o => o.UseQueryMode(MongoQueryMode.DriverLinq))`
> restores the previous values.

Two things that entry must **not** do, both learned from `…-groupby-join-uncorrelated-inner-decline-design.md`
§2.7:

- **Do not scope it to bare projections.** The wrapped half already shipped into this cycle and is the half a
  reader is more likely to hit.
- **Do not add an entry for tier 2.** There is nothing to warn about: the shape throws at every released
  version (§2). An entry telling upgraders a working query started throwing would be exactly the mis-warning
  that got the EF-366 entry deleted.

**Also do not add** an entry for the owned-collection `null` → empty change (already documented) or for the
count shape becoming supported (throw → works is not a break).

---

## 5. Confidence, and what would falsify this

| claim | tag | strength |
|---|---|---|
| Baselines are `v10.0.2` / `v9.1.2` / `v8.4.2` | VERIFIED | `gh release list`, this session |
| `MongoQueryMode` exists at no tag | VERIFIED | `git ls-tree` **and** runtime reflection over the released assembly |
| 3a changes no public/protected surface | VERIFIED | all touched types `internal`; zero `public`/`protected` diff lines |
| Q1: bare `.Count` throws at all three tags | VERIFIED | executed against the published packages, 4 spellings, both driver versions |
| Q2: required-absent bare projection is a break | VERIFIED | executed both sides; attributed to `04eda911` by bisecting the three 3a commits; mechanism confirmed by captured MQL on both sides |
| Every other tier-1 shape is unchanged | VERIFIED **for the shapes probed** | 56 probes × 7 builds |
| The break's mitigation works | VERIFIED | executed at HEAD |
| No *other* tier-1 shape breaks | **INFERRED** | the probe set is the design's own shape inventory plus renamed/converted/representation-sensitive leaves and both absent states, but it is a probe set, not a proof. The nearest un-probed relatives are a `[BsonRepresentation]`-configured property, a shadow property, an enum stored as `int`, and the VectorSearch `__score` leaf (unobservable at any tag, so it cannot be a break) |
| Whether tier 2 should return | **not answered** | out of scope; §2 settles only the release-tag premise |

**What would falsify the headline.** Q1 falls if `Select(b => b.Posts.Count)` can be made to return values on
a released package by some model configuration my fixture did not use (a `HasKey`-less owned collection, a
reference collection). Q2 falls if the `F6a`/`F7a`/`F7b` divergence turns out to be an artefact of the
console harness rather than the provider — it is not, because base and HEAD differ under an otherwise
identical build, and the captured MQL shows exactly the alias change tier 1 introduces.

---

## 6. Reproduction

```bash
# 0. baselines
git fetch --tags
gh release list --limit 1   --json tagName,isLatest
gh release list --limit 100 --json tagName --jq '.[].tagName'
for t in v8.4.2 v9.1.2 v10.0.2; do
  git ls-tree $t src/MongoDB.EntityFrameworkCore/Infrastructure/MongoQueryMode.cs   # zero rows at all three
done
git diff f4d50b5a..f343436f -- src/ | grep -E "^[+-]\s*(public|protected)"          # empty

# 1. a server of one's own
docker run -d --name step3a-breakcheck-mongo -p 57345:27017 mongodb/mongodb-atlas-local:8

# 2. the harness (scratchpad; a single shared Program.cs compiled seven ways)
#      tag8 / tag9 / tag10        -> PackageReference MongoDB.EntityFrameworkCore 8.4.2 / 9.1.2 / 10.0.2
#      tag10d310                  -> the same 10.0.2 package + an explicit MongoDB.Driver 3.10.0
#      base / t1 / head           -> <Reference> to the provider DLL built from
#                                    f4d50b5a (pre-3a) / 04eda911 (tier 1 only) / f343436f (HEAD)
#    the two branch worktrees:
git worktree add <scratch>/wt-base f4d50b5a --detach
git worktree add <scratch>/wt-t1   04eda911 --detach
(cd <scratch>/wt-base && dotnet build src/MongoDB.EntityFrameworkCore/MongoDB.EntityFrameworkCore.csproj -c "Debug EF10")
(cd <scratch>/wt-t1   && dotnet build src/MongoDB.EntityFrameworkCore/MongoDB.EntityFrameworkCore.csproj -c "Debug EF10")
dotnet build src/MongoDB.EntityFrameworkCore/MongoDB.EntityFrameworkCore.csproj -c "Debug EF10"   # HEAD

# 3. rebuild EVERY harness project before EVERY measurement round, then run
for p in tag8 tag9 tag10 tag10d310 base t1 head; do dotnet build $p/$p.csproj -c Release; done
for p in tag8 tag9 tag10 tag10d310 base t1 head; do
  dotnet run --project $p/$p.csproj -c Release --no-build -- \
    "mongodb://localhost:57345/?directConnection=true" "final_$p" "$p" > out-$p.txt
done
python3 diff.py > FINAL-DIFF.txt        # 19 differing probes of 56

# 4. MQL for the break (needs EnableSensitiveDataLogging; otherwise the pipeline logs as `aggregate([?])`)
MQL=1 dotnet run --project tag10/tag10.csproj -c Release --no-build -- ... && grep '### MQL' outmql-tag10.txt
MQL=1 dotnet run --project head/head.csproj   -c Release --no-build -- ... && grep '### MQL' outmql-head.txt

# 5. cleanup
git worktree remove --force <scratch>/wt-base <scratch>/wt-t1 && git worktree list
docker rm -f step3a-breakcheck-mongo
```

**Two traps this run actually fell into, recorded so the next one does not.**

1. **Stale binaries.** A round was measured after rebuilding only three of seven harness projects; the four
   stale ones ran an older `Program.cs` against a *newer* seed and produced a driver-version difference that
   does not exist. Rebuild all, every round.
2. **EF's model cache is keyed by context TYPE.** Two `BlogContext` instances configured with different
   `ToCollection` names silently share the first-built model, so every set-op probe was querying the wrong
   collection and returning plausible-looking wrong data. The harness now uses a distinct context class per
   collection.

**Fixture note.** The ragged fixture mirrors `NativeBareProjectionTests.SeedRagged` exactly (populated /
empty / omitted / explicit BSON null / populated) and self-checks the four stored states before probing. The
`fancy` fixture is seeded through **EF itself** so that `Guid`, `decimal` and value-converted values carry
whatever representation the provider under test writes, then two rows are edited with the raw driver to
produce the omitted and BSON-null states.

**Cleanliness.** Both worktrees removed and `git worktree list` re-checked (only the three pre-existing
`.claude/worktrees/agent-*` belonging to other sessions remain, untouched); container removed; nothing under
`src/` or `tests/` modified; nothing committed. This file is the only change to the tree.
