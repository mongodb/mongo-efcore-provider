# HANDOFF — native string null propagation (EF-322), 2026-09-29

> **COMPLETED 2026-09-29.** Everything below is history. All string commits (plus member-less-ctor and GroupBy
> case-mapping fixes) are squashed into `6cadb634`; branch = origin + 3; backup `native-EF-322-string-presquash` =
> `230bf718`. `S + T` over a retained pushed-down Select filed as EF-460. Not pushed.

Resume here after `/clear`. Everything below is on local branch `native-EF-322-Native-LINQ-rebased` in
`/Users/arthur.vickers/code/mongo-efcore-provider`. **Nothing is pushed. Do not push until the owner says so.**

## Where the branch is

`origin/EF-322-Native-LINQ-rebased` = `638f92e7`. Local is origin + 11 commits:

| Commit | What | Status |
|---|---|---|
| `dde503cb` | Step 1: native GroupBy over a join scope + aggregation-dialect `<`/`<=` null guard | done, squashed |
| `0693e8db` | Step 2: post-group `Where` via `MongoProjectedAliasScope`, `$push` list, nested-GroupBy fixes | done, squashed |
| `3d0d24c6` | DateTimeKind read-back + left-join non-null-propagating decline | done, squashed — **the string work below gets squashed INTO this commit** |
| `b931d16c` | Null-propagating native string operators (`NullPropagating` guard for `$substrCP`/`$strLenCP`/`$indexOfCP` needle; `$concat` operands `$ifNull: [x, ""]`; `$cmp` missing-as-null) | reviewed |
| `31f5c308` | Follow-ups: coalesce computed leaf; non-nullable Length/IndexOf decline; FirstOrDefault relational guard; release note dropped | reviewed |
| `b57d5df3` | FirstOrDefault/LastOrDefault `==`/`!=` null semantics (pinned test flipped) | reviewed |
| `ef3f352e` | Remaining non-nullable Length gaps; mixed-reader client Length | reviewed |
| `7e3a005b` | Null-guard proof (`ProvenNonNullWhen`) in the non-nullable Length check | reviewed |
| `04f5ad92` | Mixed reader: C# semantics for non-nullable conditionals; null-propagation over document reads; `&&` test | reviewed clean |
| `e6150fd5` | **Root-cause fix**: mixed reader reads ANY client-computed leaf whole (`IsClientComputedLeaf` / `ClientLeafChecker` / `TryBindClientComputedLeaf`, ctor-arg binding for member-less constructors, `NullPropagatingReceiverRewriter`) | reviewed: Needs fixes → fixed in `657e3afd` |
| `657e3afd` | Guard ctor-arg/initializer collisions and retained pushed-down Selects in the mixed reader | **awaiting re-review** |

Safety branches (keep until the owner says otherwise): `native-EF-322-pre-string-null-fix` = `3d0d24c6`
(pre-string-work tip), `native-EF-322-Native-LINQ-rebased-prerebase2` = `82db700f` (before the last rebase),
`native-EF-322-pre-squash-ljfix` = `4c35ac43`, `native-EF-322-datetimekind-presquash` = `492f7cdb`,
`native-EF-322-step2-presquash` = `cb3bf84f`.

## Landed after the handoff was written: `657e3afd` (NOT yet re-reviewed)

`EF-322: guard constructor-arg/initializer collisions and retained pushed-down Selects in the mixed reader`, on
top of `e6150fd5`. Fixes the two Important review findings on `e6150fd5`, both as declines (the shapes now throw on
every row in both modes rather than answer wrong):

1. Ctor-arg / initializer collision (`new CDto4(x.S.ToUpper(), ...) { Name = x.T }` gave `Len` = T's length):
   `VisitMemberInit` records binding member names; `TryGetConstructorArgumentMembers` returns null on a clash.
2. Retained pushed-down Select (`Select(x => new { R = x.O, V = x.S.Substring(1) }).Distinct()`/`Union` gave nulls):
   `TryBindClientComputedLeaf` declines when `_pushedDownSelectRetained`.

Tests: `MixedReaderClientLeafTests.Constructor_argument_and_initializer_naming_the_same_member` and
`Computed_leaf_under_a_retained_pushed_down_Select` (DriverLinq + Native; helper `AssertRowsCorrectOrThrowing` —
each row must equal the oracle or throw). Both failed with the reported wrong values before the fix. Suites 0
failures EF8/EF9/EF10; EF10 NativeOnly by name vs `e6150fd5`: no change (104 → 104).

**First thing on resume:** dispatch a scoped opus re-review of `e6150fd5..657e3afd` against those two findings.

**Open, needs an owner ruling:** `S + T` (and the owned `R` beside it) over a retained pushed-down Select is still
silently wrong via the mixed reader's older arithmetic path — pre-existing (root predates the branch). The
implementer tried the same decline there and reverted it for fear of breaking arithmetic whose operand names match
projected member names. Ask the owner: fix here, or leave.

## Next task (owner said "Fix it")

**Native mode silently drops case mapping inside member-less constructions.** Pre-existing on the branch since at
least `3d0d24c6`; a regression vs released v10.0.4 (which returns `"ABC"`). Probed shapes, all return `"Abc"`
instead of `"ABC"` in Native / NativeOnly:
`new KeyValuePair<string,string?>(S.ToUpper(), T)`, `new Tuple<string,string?>(S.ToUpper(), T)`, a constructor DTO
`new CDto(S.ToUpper(), N * 2)`, a positional record. Cause per the reviewer: the native projection binder doesn't
handle member-less constructors, so the client-side case mapping is lost. Fix it (correct natively, or decline so
the mixed/driver path — now correct after `e6150fd5` — handles it); tests in both modes with a hand oracle;
mutation-prove; full three-version suites; NativeOnly delta by name.

## Then

Squash **all string commits** (`b931d16c` .. new HEAD) **into `3d0d24c6`** so the branch is origin + 3 commits
(one per slice). Procedure used for every squash so far: `git branch -f <backup> HEAD`; take the `3d0d24c6`
message (`git log -1 --format=%B 3d0d24c6`) and append a paragraph describing the string null-propagation work;
`git reset --soft 0693e8db` (the commit before `3d0d24c6`); `git commit -F <msg>`; verify
`git diff --quiet <backup> HEAD`.
**Do not push.**

## Owner rulings (this work) — do not relitigate

- Fix string null propagation for the **native** path in this branch; **no Jira ticket**; driver-LINQ behaviour not
  changed deliberately.
- **No `BREAKING-CHANGES.md` entry** for native-vs-driver null-semantics corrections (also ruled for the earlier
  null-ordering change).
- First/LastOrDefault over a null/missing string: **null semantics in every comparison, incl. `==`/`!=`**; bare
  projection still reads `'\0'`.
- Fix the mixed-reader operand-aliasing **root cause** (done in `e6150fd5`).
- Fix the member-less-construction case-mapping drop (next task).
- Controller ruling (flag to owner if asked): `S.Replace("b", T)` with `T` null answers `"Ac"` (C#) in the mixed
  reader, matching the native renderer's own `$ifNull: [replacement, ""]` (87f185f1); EF/v10.0.4 give null.

## Known, accepted / out of scope (don't fix unless asked)

- Over-declines: `NorthwindSelect.Select_anonymous_constant_in_expression` and
  `Select_non_matching_value_types_from_length_introduces_explicit_cast` decline under NativeOnly (non-nullable
  Length over never-null strings; `MayBeNull` is CLR-type based and can't see scope). They pass in default mode.
- `$sum` over non-nullable Length stays native (EF relational SUM semantics).
- Pre-existing retained-pushed-down-Select mixed read (owned R reads null, `S + T` leaf wrong) — root predates the
  branch; only the new Substring/Trim extension is being guarded.
- `S.Contains(...)` as a non-nullable bool over a null S: NRE in the DriverLinq mixed reader, `False` in Native.
- `Substring` start beyond length returns `""` (C# throws) — pre-existing.
- Through the driver-LINQ fallback: value-converted string `Length` returns the stored length; `enum.ToString()`
  returns the number.
- EF-459 (filed): server-side date semantics over `HasDateTimeKind(Local)` (date parts, AddDays), plus a comment on
  `DateTimeOffset.LocalDateTime`/`.DateTime`.

## Process notes

- Subagent-driven: fresh implementer per task, opus reviewer per commit, re-review after each fix, mutation proofs,
  full EF8/EF9/EF10 suites + EF10 NativeOnly by-name delta before squashing. Per-task stop gate is **off**.
- Ledger for this work: `.superpowers/sdd/2026-09-29-native-string-null-propagation/progress.md` (git-ignored) —
  briefs and packaged review diffs live alongside it.
- Implementer/reviewer subagents can't write report files outside the repo (harness blocks it) — ask for reports
  inline; keep scratch under `/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/0b03de6a-225d-4390-bd26-17a062c748bb/scratchpad/`.
- zsh: avoid `==` in unquoted `echo` (use heredocs for ledger writes).
