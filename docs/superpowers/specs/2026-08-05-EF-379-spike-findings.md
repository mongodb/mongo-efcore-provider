# EF-379 spike findings — root-vs-transitive hop classification in `RebindInnerShaperToOuterQuery`

Date: 2026-08-05. Branch: `NativeQueryOngoing`, clean at `2a544b7e`. EF10 only (`Debug EF10`).
Method: a throwaway functional probe class (`Ef379SpikeProbeTests`, since deleted) plus temporary
instrumentation inside `MongoQueryableMethodTranslatingExpressionVisitor.RebindInnerShaperToOuterQuery`
(since reverted) that dumped, per join: the `outerKeySelector.Body` tree, its receiver, the resolved
`fkPropertyName`, which navigation-resolution tier matched, the resolved navigation, the resulting
`localField`, and `outer.ShaperExpression`. Every claim below is labelled **MEASURED** (observed in a live
run against a real server) or **INFERRED** (derived by reading code / the dumped trees, not executed).

Nine scenarios were probed. Raw dumps are in the session scratchpad
(`.../scratchpad/ef379-spike/dump{,2,3}.txt`); the load-bearing excerpts are inlined below.

---

## Headline: two of the three prior claims held, one is REFUTED, and the defect is BROADER than filed

| Prior claim | Verdict |
|---|---|
| "Prefer the FK-name match" (the fix as **filed in the ticket**) is refuted — the FK-name tier misfires on a name collision | **CONFIRMED (MEASURED).** And *worse*: see the next row. |
| Member-chain **depth** is not the discriminator, because a genuine root hop can arrive as `Property(d.Outer.Outer, …)` | **CONFIRMED (MEASURED)** — scenario D2/E3/I all produce exactly `ti.Outer.Outer` for a genuine root hop, at the same depth as a transitive `ti.Outer.Inner` (scenario J). |
| Resolving the receiver **needs `outer.ShaperExpression`**, threaded as a new argument | **REFUTED as stated (MEASURED).** The root-vs-transitive *decision* is fully decidable from `outerKeySelector.Body` alone, for all nine scenarios including the self-referencing one. `outer.ShaperExpression` is *not* needed for the decision. It is still the best source for one *secondary* value (the intermediate `IEntityType`) — see Q3. **A simpler fix than the one I planned is available.** |

**And a finding not in the ticket at all (MEASURED, scenario B2): renaming the root's FK does NOT fix the
defect.** The ticket's repro relies on a property-*name* collision (`PRoot.LeafId` / `PMid.LeafId`), and
that is what tier 1 (`TIER1-FKNAME`) misfires on. But with the root's FK renamed to `SideLeafId` — no name
collision whatsoever — **tier 2 (the target-entity-type-only fallback) misfires instead**, resolves
`RRoot.SideLeaf`, and emits `localField: "SideLeafId"` with alias `_lookup_SideLeaf`. Same symptom, same
null navigation. So the trigger condition is not "the FK names collide" but the much broader **"the ROOT
carries any reference navigation to the transitive hop's target entity type"**. Any fix scoped to the FK-name
tier is insufficient by construction.

---

## Q1 — Live reproduction of the defect

Model (per the ticket; `PRoot` and `PMid` both declare a property named `LeafId`):

```
PLeaf { Id, Label }
PMid  { Id, Label, LeafId -> PLeaf }
PRoot { Id, Name, MidId -> PMid, LeafId -> PLeaf }
```

Seed makes the two paths disagree: root's `LeafId` → leaf labelled `"WRONG"`; mid's `LeafId` → leaf
labelled `"RIGHT"`. Query: `db.PRoots.Include(r => r.Mid).ThenInclude(m => m.Leaf)`.
Correct answer: `rows=1, mid="M1", Mid.Leaf.Label == "RIGHT"`.

**MEASURED, all three modes:**

| `MongoQueryMode` | rows | `Mid.Label` | `Mid.Leaf.Label` | verdict |
|---|---|---|---|---|
| `Native` (default) | 1 | `M1` | **`null`** | WRONG (should be `"RIGHT"`) |
| `DriverLinq` (explicit) | 1 | `M1` | **`null`** | WRONG (should be `"RIGHT"`) |
| `NativeOnly` | — | — | — | throws `NativeTranslationNotSupportedException` ("Query is not natively representable and MongoQueryMode.NativeOnly forbids the driver-LINQ fallback") |

Emitted MQL, **byte-identical under `Native` and `DriverLinq`** (collection-name suffixes differ per run):

```
aggregate([
  { "$lookup" : { "from" : "…_PL…", "localField" : "LeafId",  "foreignField" : "_id", "as" : "_lookup_Leaf" } },
  { "$unwind" : { "path" : "$_lookup_Leaf", "preserveNullAndEmptyArrays" : false } },
  { "$lookup" : { "from" : "…_PM…", "localField" : "MidId",   "foreignField" : "_id", "as" : "_lookup_Mid"  } },
  { "$unwind" : { "path" : "$_lookup_Mid",  "preserveNullAndEmptyArrays" : false } }
])
```

Two things to read off that pipeline, both MEASURED:

1. The leaf `$lookup` comes **FIRST** and its `localField` is the **unprefixed root field `LeafId`** — the
   root's own FK, i.e. the `"WRONG"` leaf. The correct second stage would be
   `localField: "_lookup_Mid.LeafId"` (exactly what the non-colliding control in scenario B1 emits, below).
2. `MongoQueryMode` is irrelevant to the defect. The misclassification happens at **translation** time,
   before the gate reads the mode, so `UseQueryMode(DriverLinq)` is **not** an escape hatch. Same class of
   mode-independence as the EF-368 query-filter finding already recorded in `Query/AGENTS.md`.

**Precision on the symptom, because the naive statement is stronger than the measurement.** The
materialized `Mid.Leaf` came back **`null`**, not `"WRONG"` — the pipeline *does* unwind the wrong leaf
document into `_lookup_Leaf`, but the shaper (which expects to read the mid's leaf) does not pick that
document up. So what is MEASURED is `null` where `"RIGHT"` is correct. Whether some other model shape can
make the same misclassification produce a *wrong non-null value* is **NOT MEASURED** here — do not claim it.
This is still silent wrong data: no exception, a row returned, a navigation the user asked for is empty.

The three-valued assertion (`"RIGHT"` / `"WRONG"` / `null`) was used deliberately rather than `!= null`:
the `Ef372DeepReferenceIncludeTests` header already records that EF's change-tracker identity fix-up can
repair the object graph while the `$lookup` matched the wrong field, so an existence-only assertion is not a
discriminator. (In this particular fixture the graph was *not* repaired — the collision is on the leaf, and
the correct leaf was never fetched, so there was nothing to fix up from.)

The instrumented tier for the offending join:

```
--- JOIN (hop 2 of scenario A) ---
  root(outerEntityType)   = PRoot
  innerCollectionType     = PLeaf
  InnerCollections(before)= [PMid, PLeaf]
  outerKeySelector.Body   = [MethodCallExpression2/Call] Property(p.Inner, "LeafId")
  outerKS.receiver        = EF.Property(arg0) => [FieldExpression/MemberAccess/type=PMid] p.Inner
  fkPropertyName          = LeafId
  TIER MATCHED            = TIER1-FKNAME          <-- misfire
  resolvedNavigation      = PRoot.Leaf -> PLeaf   <-- the ROOT's navigation, not PMid.Leaf
  throughPrefix           = <null>
  lookupLocalField        = LeafId
```

`TryResolveIntermediateLookupPrefix` is never reached, so EF-372's prefix-or-decline guarantee never applies
— exactly as the ticket states. **CONFIRMED (MEASURED).**

---

## Q2 — Instrumented dump, per scenario

Nine scenarios. The receiver column is the load-bearing one.

| # | Shape | Hop | `outerKeySelector.Body` | Receiver (node kind / type) | Tier matched | `localField` | Correct? |
|---|---|---|---|---|---|---|---|
| **A** | colliding 2-hop `ThenInclude` (`PRoot.Mid` → `PMid.Leaf`; root also has `PRoot.LeafId`/`Leaf`) | 1 | `Property(p, "MidId")` | bare `ParameterExpression` `p` : `PRoot` | `TIER1-FKNAME` → `PRoot.Mid` | `MidId` | ✔ |
| | | 2 | `Property(p.Inner, "LeafId")` | `MemberExpression` `p.Inner` : **`PMid`** | `TIER1-FKNAME` → **`PRoot.Leaf`** | `LeafId` | ✘ **BUG** |
| **B1** | control: 2-hop `ThenInclude`, root has **no** navigation to the leaf type | 1 | `Property(q, "MidId")` | bare param `q` : `QRoot` | `TIER1-FKNAME` → `QRoot.Mid` | `MidId` | ✔ |
| | | 2 | `Property(q.Inner, "LeafId")` | `q.Inner` : **`QMid`** | `TIER3-TRANSITIVE` (intermediate `QMid`, prefix `_lookup_Mid`) → `QMid.Leaf` | `_lookup_Mid.LeafId` | ✔ |
| **B2** | root's FK **renamed** (`SideLeafId`) — **no name collision** — but root still navigates to the leaf type | 1 | `Property(r, "MidId")` | bare param `r` : `RRoot` | `TIER1-FKNAME` → `RRoot.Mid` | `MidId` | ✔ |
| | | 2 | `Property(r.Inner, "LeafId")` | `r.Inner` : **`RMid`** | **`TIER2-TYPEONLY`** → **`RRoot.SideLeaf`** | `SideLeafId` | ✘ **BUG (new finding)** |
| **C** | 3-hop chain (the EF-372 shape) | 1 | `Property(q, "MidId")` | bare param `q` : `QRoot` | `TIER1-FKNAME` → `QRoot.Mid` | `MidId` | ✔ |
| | | 2 | `Property(q.Inner, "LeafId")` | `q.Inner` : `QMid` | `TIER3-TRANSITIVE` (`QMid`, `_lookup_Mid`) | `_lookup_Mid.LeafId` | ✔ |
| | | 3 | `Property(q.Inner, "TipId")` | `q.Inner` : **`QLeaf`** | `TIER3-TRANSITIVE` (`QLeaf`, `_lookup_Leaf`) | `_lookup_Leaf.TipId` | ✔ |
| **D** | two sibling root reference `Include`s, **different** target types | 1 | `Property(s, "AlphaId")` | bare param `s` : `SRoot` | `TIER1-FKNAME` → `SRoot.Alpha` | `AlphaId` | ✔ |
| | | 2 | `Property(s.Outer, "BetaId")` | **`s.Outer`** : **`SRoot`** | `TIER1-FKNAME` → `SRoot.Beta` | `BetaId` | ✔ |
| **D2** | **three** sibling root reference `Include`s | 3 | `Property(s.Outer.Outer, "GammaId")` | **`s.Outer.Outer`** : **`SRoot`** | `TIER1-FKNAME` → `SRoot.Gamma` | `GammaId` | ✔ |
| **E** | user-authored 2-level chained `Join`, 2nd key off the joined mid | 2 | `Convert(ti.Inner.LeafId, Object)` | `ti.Inner` : `QMid` | `TIER3-TRANSITIVE` (`QMid`, `_lookup_Mid`) | `_lookup_Mid.LeafId` | ✔ |
| **E2** | user-authored chained `Join`, 2nd key off the **root** (`x.r.BetaId`) | 2 | `Convert(ti.Outer.BetaId, Object)` | **`ti.Outer`** : **`SRoot`** | `TIER1-FKNAME` → `SRoot.Beta` | `BetaId` | ✔ |
| **E3** | user-authored **3-level** chained `Join`, all keys off the root | 3 | `Convert(ti0.Outer.Outer.GammaId, Object)` | **`ti0.Outer.Outer`** : **`SRoot`** | `TIER1-FKNAME` → `SRoot.Gamma` | `GammaId` | ✔ |
| **F** | **self-referencing** 2-hop chain (`FNode.Parent` → `FNode.Parent`) | 2 | `Property(f.Inner, "ParentId")` | `f.Inner` : **`FNode`** (== root CLR type!) | `TIER1-FKNAME` → `FNode.Parent` | `ParentId` | ✘ (pre-existing; see below) |
| **I** | **mixed**: root sibling `Include` + transitive `ThenInclude` | 2 | `Property(m.Inner, "LeafId")` | `m.Inner` : `MMid` | `TIER3-TRANSITIVE` (`MMid`, `_lookup_Mid`) | `_lookup_Mid.LeafId` | ✔ |
| | | 3 | `Property(m.Outer.Outer, "SideId")` | **`m.Outer.Outer`** : **`MRoot`** | `TIER1-FKNAME` → `MRoot.Side` | `SideId` | ✔ |
| **J** | two `ThenInclude`s off the **same** intermediate | 2 | `Property(j.Inner, "LeafId")` | `j.Inner` : `JMid` | `TIER3-TRANSITIVE` (`JMid`, `_lookup_Mid`) | `_lookup_Mid.LeafId` | ✔ |
| | | 3 | `Property(j.Outer.Inner, "AltId")` | **`j.Outer.Inner`** : **`JMid`** | `TIER3-TRANSITIVE` (`JMid`, `_lookup_Mid`) → `JMid.Alt` | `_lookup_Mid.AltId` | ✔ |

Data outcomes (MEASURED):

| Scenario | `Native` | `DriverLinq` | `NativeOnly` |
|---|---|---|---|
| A (colliding) | `rows=1, mid=M1, midLeaf=null` (**wrong**) | identical (**wrong**) | `NativeTranslationNotSupportedException` |
| B1 (control) | `rows=1, mid=M1, midLeaf=RIGHT` ✔ | not run | `NativeTranslationNotSupportedException` |
| B2 (renamed FK) | `rows=1, mid=M1, midLeaf=null` (**wrong**) | not run | `NativeTranslationNotSupportedException` |
| C (3-hop) | `rows=1, mid=M1, midLeaf=RIGHT, tip=T1` ✔ | not run | `NativeTranslationNotSupportedException` |
| D | `rows=1, alpha=A1, beta=B1` ✔ | not run | `NativeTranslationNotSupportedException` |
| D2 | `rows=1, alpha=A1, beta=B1, gamma=G1` ✔ | not run | not run |
| E / E2 / E3 | `R1\|RIGHT` / `R1\|A1\|B1` / `R1\|A1\|B1\|G1` ✔ | not run | not run |
| F (self-ref) | `InvalidOperationException: Document element is missing for required non-nullable property 'Id'` | **identical exception** | not run |
| I (mixed) | `rows=1, side=S1, mid=M1, midLeaf=RIGHT` ✔ | not run | not run |
| J (fork) | `rows=1, mid=M1, midLeaf=L1, midAlt=A1` ✔ | identical ✔ | not run |

Three structural facts worth extracting, all MEASURED:

1. **Across the nine `Include`/`Join` scenarios probed above, the receiver took one of exactly two shapes**:
   a bare `ParameterExpression`, or a chain of `MemberExpression`s named `Outer`/`Inner` rooted on the
   lambda's own parameter. No third shape was observed **in that scenario set** — which is `Include`,
   `ThenInclude` and user-authored `Join` shapes only. The `Body` itself is either
   `EF.Property(receiver, "<Fk>")` (nav-expanded `Include`/`ThenInclude`) or
   `Convert(receiver.<Fk>, object)` over a plain `MemberExpression` (user-authored `Join`) — `RemoveConvert`
   + `TryGetSimplePropertyName` already normalize both.

   **Do NOT re-attach the join-ordinal labels an earlier version of this line carried** — it read "a bare
   `ParameterExpression` (first join)" and "…rooted on the lambda's own parameter (second and later joins)".
   Those parentheticals are FALSE, and they were the premise behind a decline the EF-379 slice shipped and
   then **withdrew as a measured regression**. They look true here only because every scenario in the table
   above is an `Include`/`Join` chain, where a transparent identifier can only have come from a prior join.
   **A transparent identifier is not produced only by a prior JOIN — an owned `SelectMany` produces one
   too**, so a TI-chain receiver occurs at the **FIRST** join. Counter-example, measured:
   `from o in db.Orders from t in o.Tags join p in db.Products on t.ProductId equals p.Id select new { o.Total, p.Name }`,
   where `Tag` is an owned element carrying a bare `ObjectId` FK **property** and **no** navigation, reaches
   the very first `TranslateJoinCore` call with a `ti.Inner`-rooted receiver and therefore classifies as
   `TransitiveHop` at join #1. Pinned by
   `Ef379RootNavigationMisroutingTests.Owned_SelectMany_then_join_off_the_unwound_element_still_works`; the
   withdrawal itself is recorded in the EF-379 note in
   `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`. Receiver SHAPE says which SCOPE the key is read from,
   never which join ORDINAL it is.
2. **`outer.ShaperExpression` mirrors the receiver's TI structure exactly.** Where the receiver is
   `ti.Outer.Inner`, the shaper is `new TI(Outer = new TI(Outer = shaper, Inner = shaper), Inner = shaper)`
   and `shaper.Outer.Inner` is the corresponding `StructuralTypeShaperExpression`. Verified for every
   scenario in the table; a bare parameter always pairs with a bare `StructuralTypeShaperExpression`.
3. **Depth is genuinely useless as a discriminator.** `s.Outer.Outer` (D2, E3, I — genuine root hop) and
   `j.Outer.Inner` (J — transitive hop) are both two-member chains at the same nesting depth.

`NativeOnly` throwing for every multi-hop shape is expected and unrelated: per `Query/AGENTS.md`,
`ThenInclude`/multi-level and sibling reference `Include`s are deferred and decline to the driver-LINQ
fallback. It is a useful control that the defect is *not* native-path-specific.

---

## Q3 — Is there a total, decidable rule?

**Yes (MEASURED across all nine scenarios), and it needs only `outerKeySelector.Body`.**

The discriminator is the **member names of the receiver's transparent-identifier access chain**:

- a chain of only `Outer` hops (or an empty chain — a bare parameter) resolves to the **query root**;
- a chain containing **any** `Inner` hop resolves to a **previously-joined** entity, i.e. the hop is
  transitive, and the receiver's own type/scope names the intermediate.

Pseudocode against the actually-observed node shapes:

```csharp
enum HopKind { RootHop, TransitiveHop, Unclassifiable }

// MEASURED shapes only:
//   Body     : EF.Property(receiver, "<Fk>")  |  Convert(receiver.<Fk>, object)
//   receiver : ParameterExpression p          |  MemberExpression chain of "Outer"/"Inner" rooted on p
static HopKind ClassifyHop(LambdaExpression outerKeySelector)
{
    var body = outerKeySelector.Body.RemoveConvert();          // strips the object Convert (user Join form)

    Expression? receiver = body switch
    {
        MemberExpression m                                            => m.Expression,
        MethodCallExpression c when c.Method.IsEFPropertyMethod()
                                    && c.Arguments.Count == 2         => c.Arguments[0],
        _                                                             => null   // composite key, etc.
    };
    if (receiver == null) return HopKind.Unclassifiable;

    // Peel the transparent-identifier access chain back to the parameter.
    var sawInner = false;
    var node = receiver;
    while (node is MemberExpression { Member.Name: "Outer" or "Inner" } ti)
    {
        sawInner |= ti.Member.Name == "Inner";
        node = ti.Expression!;
    }

    if (node != outerKeySelector.Parameters[0]) return HopKind.Unclassifiable;   // reference equality

    return sawInner ? HopKind.TransitiveHop : HopKind.RootHop;
}
```

and how it slots into `RebindInnerShaperToOuterQuery` (the shape of the fix, **not** implemented here):

```csharp
switch (ClassifyHop(outerKeySelector))
{
    case RootHop:
        // UNCHANGED from today: tier 1 (FK-name off the root), then tier 2 (target-type-only off the root).
        // Still needed — the classification says WHICH SCOPE, the FK-name match says WHICH NAVIGATION
        // within that scope (e.g. Doc.Author vs Doc.Editor, both targeting Buyer).
        navigation    = Tier1(rootEntityType, fkPropertyName) ?? Tier2(rootEntityType, innerEntityType);
        throughPrefix = null;
        break;

    case TransitiveHop:
        // NEVER consult rootEntityType.GetNavigations() — that is the entire EF-379 defect,
        // and it must be closed for BOTH tiers, not just tier 1 (see the B2 measurement).
        intermediate = ResolveIntermediateEntityType(receiver, outer.ShaperExpression);   // see below
        navigation   = intermediate?.GetNavigations()
                          .FirstOrDefault(n => n.TargetEntityType == innerEntityType
                                            && n.ForeignKey.Properties.Any(p => p.Name == fkPropertyName));
        if (navigation == null) return null;                                    // decline
        if (!TryResolveIntermediateLookupPrefix(outerQ, rootEntityType, intermediate, out throughPrefix))
            return null;                                                        // EF-372's guarantee applies
        break;

    case Unclassifiable:
        // Keep today's behaviour, or decline. Not exercised by any measured scenario.
        break;
}
```

### On the three prior claims

- **"Prefer the FK-name match" is refuted** — **CONFIRMED (MEASURED)**, scenario A: tier 1 is precisely
  what misfires there. And the refutation is stronger than filed: scenario B2 shows tier 2 misfires
  independently, so *both* root tiers must be gated behind the classification.
- **"Depth is not the discriminator, because a genuine root hop can arrive as `Property(d.Outer.Outer, …)`"**
  — **CONFIRMED (MEASURED)**, scenarios D2, E3 and I all produce exactly that, and scenario J produces a
  same-depth transitive `ti.Outer.Inner`. The receiver *chain composition* separates them; the depth does not.
- **"Resolving the receiver needs `outer.ShaperExpression`, threaded as one new argument"** —
  **REFUTED as stated (MEASURED).** The root-vs-transitive decision above uses only `outerKeySelector`
  and is correct for all nine scenarios, **including the self-referencing scenario F** where the root and
  intermediate share a CLR type. **Say so plainly: the simpler fix works, and no new parameter is needed
  for the decision.**

  Two related things that are *not* refuted, stated narrowly:

  1. A rule based on the receiver's **CLR TYPE** rather than its member names would be **wrong** —
     scenario F (MEASURED) has `f.Inner` typed `FNode`, the same CLR type as the root, so a
     `receiver.Type == rootEntityType.ClrType` test misclassifies it as a root hop. If anyone proposes the
     type-based shortcut, this is the counter-example.
  2. `outer.ShaperExpression` remains the **most robust source for the intermediate `IEntityType`**
     (`ResolveIntermediateEntityType` above): walking the `hops` list against the shaper's `NewExpression`
     tree lands on the exact `StructuralTypeShaperExpression` for that scope, which survives shared CLR
     types and TPH-derived targets in a way `receiver.Type` + `FindEntityType` does not. This is
     **INFERRED** from fact 2 in Q2 (the mirroring is MEASURED; the walk itself was not executed).
     `receiver.Type` + `model.FindEntityType(...)` would also give the right answer for all nine measured
     scenarios — so threading the shaper is a robustness choice, not a necessity.

  Hardcoding the member names `"Outer"`/`"Inner"` is consistent with existing convention, not a new
  liberty: `MongoQueryableMethodTranslatingExpressionVisitor` (lines ~331, ~440, ~470, ~503, ~656),
  `MongoEFToLinqTranslatingExpressionVisitor.LeftJoin.cs` (~560, ~1116, ~1195, ~1251) and
  `NativeSelectManyBinder.cs` (~353, ~499) all already resolve transparent-identifier structure by those
  literal names. (MEASURED by grep.)

### Two scope limits the rule does NOT fix — record these before implementing

1. **Scenario F (self-referencing chain) is NOT fixed by the classification alone.** MEASURED: with the
   classification corrected, hop 2 becomes a `TransitiveHop` with intermediate `FNode` — but the existing
   transitive branch **skips** any `priorInnerEntityType == innerEntityType` (source line
   `if (priorInnerEntityType == innerEntityType) continue;`), so a self-referencing intermediate can never
   be selected, and `LookupExpression.GetLookupAlias` is navigation-name-only so both hops would derive the
   same `_lookup_Parent` alias and `AddLookup` would de-duplicate one away — the same blocker
   `Query/AGENTS.md` already documents for the sibling-`ThenInclude` case ("that shape needs a path-scoped
   alias scheme, not a better prefix resolution"). Today scenario F throws
   `InvalidOperationException: Document element is missing for required non-nullable property 'Id'` in
   `Native` and `DriverLinq` alike (MEASURED) — i.e. it is *already* a loud failure, not silent wrong data,
   so it is not a regression risk. **Expect the classification fix to turn it into a clean `null`
   decline** (EF Core's translation-failure path with `AddTranslationErrorDetails`) rather than into working
   data. That is an improvement, but it *is* an observable disposition change and should be pinned by a test.
2. **The intermediate search in the transitive branch is itself imprecise and the receiver fixes it for
   free.** Today the branch iterates `outerQueryExpression.InnerCollections.Keys` and takes the *first*
   prior entity type carrying an FK-name-matching navigation. The receiver names the intermediate exactly,
   so passing it in removes a second guess. (INFERRED from source; no shape was found where the two
   disagree.)

---

## Q4 — User-authored chained `Join` (EF-375 / EF-377 neighbourhood)

**MEASURED, three forms, all at 2+ levels:**

| Form | 2nd/3rd hop `Body` | Receiver | Classification under the proposed rule | Today's tier | Agrees? |
|---|---|---|---|---|---|
| E: `.Join(Mids, r=>r.MidId, …, (r,m)=>new{r,m}).Join(Leaves, x=>x.m.LeafId, …)` | `Convert(ti.Inner.LeafId, Object)` | `ti.Inner` : `QMid` | `TransitiveHop` | `TIER3-TRANSITIVE` | ✔ same |
| E2: `.Join(Alphas, r=>r.AlphaId, …, (r,a)=>new{r,a}).Join(Betas, x=>x.r.BetaId, …)` | `Convert(ti.Outer.BetaId, Object)` | `ti.Outer` : `SRoot` | `RootHop` | `TIER1-FKNAME` | ✔ same |
| E3: three-level chain, every key off the root | `Convert(ti0.Outer.Outer.GammaId, Object)` | `ti0.Outer.Outer` : `SRoot` | `RootHop` | `TIER1-FKNAME` | ✔ same |

Findings:

- The user-authored `Join` receiver has **the same structure** as the nav-expanded `Include` receiver. The
  only differences are cosmetic and already normalized: the leaf is a plain `MemberExpression` under a
  `Convert(…, object)` rather than an `EF.Property` call. Note the user wrote `x => x.m.LeafId` and
  nav-expansion re-emitted it as `ti.Inner.LeafId` — EF Core normalizes the user's own anonymous-type
  member names into its own `Outer`/`Inner` transparent identifier, consistent with the
  `Query/AGENTS.md` EF-373 note ("nav-expansion re-emits every join with its OWN TransparentIdentifier
  result selector"). (MEASURED.)
- **The proposed rule reproduces today's classification for every user-authored `Join` measured, so no
  EF-375/EF-377 disposition changes.** (MEASURED for these three; see Q5 for the committed-test exposure.)
- E2/E3 are the important controls: a chained `Join` **can** legitimately reach back to the root at hop 2/3
  (`ti.Outer`, `ti.Outer.Outer`), and those must keep taking the root tiers. The rule keeps them there.

---

## Q5 — Blast radius: which committed tests exercise the changed path

Read: `Ef372DeepReferenceIncludeTests.cs`, `Ef373InterleavedPagingTests.cs`,
`NativeReferenceIncludeTests.cs`, `CrossCollectionIncludeTests.cs`.
**Baseline control (MEASURED): all four classes, EF10, clean tree — 70 passed / 0 failed.**

Every second-or-later join in every one of these files enters the changed classification. Classified below
by whether the *outcome* should change. All classifications are **INFERRED** from the receiver shapes
measured in Q2 plus the models/queries as written — none of the four files was re-run under a modified
`src/`, because the fix was deliberately not implemented.

### Exercises the changed code, outcome should be UNCHANGED (the regression surface to protect)

`Ef372DeepReferenceIncludeTests` — the densest exposure; every test here has a 2nd-or-later join:

- `Three_hop_reference_ThenInclude_returns_every_row`, `..._prefixes_the_third_localField`,
  `Two_hop_reference_ThenInclude_prefixes_only_the_second_localField`,
  `Four_hop_reference_ThenInclude_prefixes_the_fourth_localField`,
  `Three_hop_OPTIONAL_reference_ThenInclude_prefixes_the_third_localField` (the `LeftJoin` route),
  `Three_hop_chain_localField_alias_comes_from_the_navigation_name` — all are pure `Root→Mid→Leaf→Tip`
  chains whose roots carry **no** navigation to the deeper types, so today they already take
  `TIER3-TRANSITIVE`. Under the rule they take the same branch by a different test. **The `localField` MQL
  pins in these tests are the strongest available guard on the fix.**
- `Three_level_user_authored_chained_Join_returns_every_row` — the Q4 form E, `ti.Inner` → transitive,
  unchanged.
- `Two_same_typed_navigations_single_branch_ThenInclude_returns_correct_rows` — `AmbRoot` has *no*
  navigation to `AmbLeaf`, so the hop is transitive today and stays transitive. Its teeth (leaf `"A"` vs
  `"B"`) are exactly the sort of value discrimination the fix must not disturb.
- `Two_same_typed_navigations_sibling_ThenIncludes_decline_cleanly`,
  `Two_same_typed_navigations_second_branch_only_declines_cleanly`,
  `Optional_two_same_typed_navigations_sibling_ThenIncludes_decline_cleanly` — these decline **inside**
  `TryResolveIntermediateLookupPrefix`, i.e. downstream of the classification, which the rule still routes
  them into. Their message assertions (`"reaches it THROUGH a previously-joined entity type"`, `AmbLeaf`,
  `LeafId`) should be unaffected. **Verify explicitly** — a classification change that stopped routing them
  into the transitive branch would silently turn a clean decline back into wrong data.
- `Join_chain_with_no_model_navigation_declines_rather_than_dropping_every_row` — `NoNavRoot` has an FK
  property but no navigation, so hop 1 resolves `navigation == null` and hop 2 (`ti.Inner`) declines at
  tier 2 of the prefix resolver. Classification unchanged; decline unchanged.
- `One_hop_reference_Include_localField_is_unprefixed` — single hop, bare parameter → `RootHop`. This is the
  control that the rule does not *over*-classify.

`Ef373InterleavedPagingTests` — `db.Roots.Join(Mids, r=>r.MidId, …, (r,m)=>r).…Join(Others, r=>r.OtherId, …)`.
Both keys are off the **root**, so the second join's receiver is `ti.Outer` (the Q4 form E2 shape) →
`RootHop`, same as today's `TIER1-FKNAME`. Affected tests:
`Paging_between_two_joins_pages_the_rows_the_first_join_produced`,
`Paging_between_two_joins_emits_the_second_lookup_above_the_paging`,
`Paging_below_all_joins_still_emits_both_lookups_above_the_paging`,
`Interleaved_Skip_without_Take_pages_the_rows_the_first_join_produced`,
`Sort_between_two_joins_orders_the_rows_a_one_to_many_second_join_expands` (whose second join is 1:N off
`r._id`). **These are the tests most likely to catch an over-broad `TransitiveHop` classification**: they
pin exact `$lookup` stage ORDER and paged row content, so a spurious prefix would show up as a wrong page
rather than as a null navigation.

`NativeReferenceIncludeTests`:
- `Declined_shapes_throw_under_NativeOnly_and_match_DriverLinq_under_Native` rows
  `"sibling reference Includes"` (`Lines.Include(l=>l.Order).Include(l=>l.Product)`) and
  `"same-target sibling Includes"` (`Docs.Include(d=>d.Author).Include(d=>d.Editor)`) — both are genuine
  root second hops (`ti.Outer`), so `RootHop`. **The `Docs` row is the one to watch**: `Author` and `Editor`
  both target `Buyer`, so it proves the root tiers must be *kept*, not replaced — the classification says
  "root scope", and only tier 1's FK-name match distinguishes `EditorId` → `Doc.Editor`. A fix that removed
  tier 1 in favour of the receiver alone would break this test.
- `Declined_shapes…` row `"ThenInclude / transitive"` (`Lines.Include(l=>l.Order).ThenInclude(o=>o.Buyer)`) —
  `Line` has no navigation to `Buyer`, so this is `TIER3-TRANSITIVE` today and `TransitiveHop` under the
  rule. Unchanged.
- `Two_joins_onto_the_same_target_stay_declined`
  (`Orders.Include(o=>o.Buyer).Join(db.Buyers, o=>o.BuyerId, …)`) — second hop off the root → `RootHop`,
  and `Buyer` is already in `InnerCollections`. Unchanged.
- `A_real_ThenInclude_nested_underneath_an_embedded_hop_still_declines`
  (`Orders.Include(o=>o.Buyer).ThenInclude(b=>b.Address).ThenInclude(a=>a.Region)`) — reaches the multi-hop
  machinery; classification applies.
- Every **single-hop** test in the file (`Required_reference_Include_goes_native_with_an_inner_unwind`,
  `Optional_reference_Include_goes_native_with_a_left_outer_unwind`,
  `Composed_Where_stays_ahead_of_the_lookup`, the streaming-materializer tests, the three query-filter
  decline tests, `Composite_FK_and_PK_still_declines…`, and the reducer tests) has a **bare-parameter**
  receiver → `RootHop`, trivially unchanged. They are the "did you break the common case" net.

`CrossCollectionIncludeTests`:
- `Include_multi_level_materializes_nested_entities`
  (`Orders.Include(o=>o.Customer).ThenInclude(c=>c.Orders)`) — the second hop is a **collection**
  navigation, whose `$lookup` is registered by `MongoProjectionBindingExpressionVisitor` rather than by
  `TranslateJoinCore`, so it may not reach the changed code at all. **This one is worth actually measuring
  rather than reasoning about** — it is the only test in the four files whose exposure I could not settle by
  inspection.
- `Include_self_join_materializes_related_entity` (`Staff.Include(s=>s.Manager)`) — single hop, bare
  parameter → `RootHop`. Relevant because it is the *shallow* self-reference: it must keep working while the
  2-hop self-reference (scenario F) declines. Related: `Include_multiple_navigations_on_same_entity`,
  `Filtered_collection_include_*`, `Query_filter_on_collection_include_target_*` are single-join or
  collection-Include shapes and should be untouched.

### Not exercised

Everything in the four files with exactly one cross-collection join and a bare-parameter key selector
reaches the classification but can only ever be `RootHop`, so it cannot change. That is the majority of
`CrossCollectionIncludeTests` and `NativeReferenceIncludeTests`.

### No committed test covers the defect

**MEASURED (by construction — the probe had to build its own models): none of the four files contains a
model where the ROOT carries a reference navigation to a transitive hop's target entity type.** That is
exactly why EF-379 is a silent, uncaught hole. The fix needs new fixtures on both doorways:

- the **name-collision** shape (scenario A — `PRoot.LeafId` / `PMid.LeafId`), and
- the **renamed-FK / type-only** shape (scenario B2 — `RRoot.SideLeafId` with `RRoot.SideLeaf` still
  targeting `RLeaf`), which the ticket does not mention and which a tier-1-only fix would leave broken.

Both must assert on the **value** (`"RIGHT"` vs `"WRONG"` vs `null`), not on `!= null`, and both should pin
the `localField` MQL (`_lookup_Mid.LeafId`).

---

## Recommended next step (not implemented here)

A single-conjunct change, no new method parameter: gate **both** root navigation tiers in
`RebindInnerShaperToOuterQuery` behind `ClassifyHop(outerKeySelector) == RootHop`, and route
`TransitiveHop` into the existing transitive branch (which already ends in EF-372's
prefix-or-decline). Pass the receiver-resolved intermediate entity type into that branch if the
imprecision noted in Q3 is worth closing at the same time; threading `outer.ShaperExpression` is
optional robustness, not a requirement. Expect scenario F (self-referencing 2-hop) to move from a
`"Document element is missing"` crash to a clean translation decline, and pin that.
