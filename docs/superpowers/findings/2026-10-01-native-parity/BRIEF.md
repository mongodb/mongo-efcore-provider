# Research brief: native-LINQ parity on branch EF-322c (repo /Users/arthur.vickers/code/provider3)

GOAL (owner's definition of done): every test case that produced correct results on `main` via driver LINQ must
produce correct results on EF-322c via NATIVE LINQ (MongoQueryMode.NativeOnly green). Pinning a mode in a test to
exercise one implementation on purpose is fine. "Worked" = correct results, not "test passed" (a test passing by
asserting a translation failure is not a working case). Cases where the driver threw or returned wrong data on main
are out of scope for the native branch — but wrong-on-main ones must be recorded so they can be fixed on main.

RULES FOR ALL RESEARCH AGENTS: read-only. Do NOT run `dotnet build`/`dotnet test`, do NOT edit repo files, do NOT
touch git state. Use git/grep/read only. Write any scratch to your own subdir under
/private/tmp/claude-502/-Users-arthur-vickers-code-provider3/d776b2a4-0860-4e01-8e78-46b0c77f6ba6/scratchpad/plan/<your-agent-name>/.
Read src/MongoDB.EntityFrameworkCore/Query/AGENTS.md first.

KEY CODE FACTS
- Native decline surfaces at two throw sites in Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs:
  line ~321 ("Query projects a non-entity result" — projected/scalar path, VisitProjectedQuery) and ~784
  ("Query is not natively representable" — entity path). Under Native mode these fall back to driver LINQ via
  Query/Visitors/MongoEFToLinqTranslatingExpressionVisitor*.cs. Declines originate earlier (translator returns
  false/null, IsNatively* flags on MongoSelectDefinition / MongoQueryExpression, NativeProjectionBinder,
  NativeSlotPopulator, NativeCardinalityBinder, NativeGroupByBinder, MongoExpressionTranslator*).
- Spec tests: tests/MongoDB.EntityFrameworkCore.SpecificationTests; MONGODB_EF_NATIVE_ONLY=1 flips spec contexts to
  NativeOnly (Utilities/MongoTestStore.cs); MongoSpecTestHelpers.IsNativeOnly branches expectations.
- Functional tests do NOT honor MONGODB_EF_NATIVE_ONLY today; Native* tests use explicit modes and
  Utilities/NativeModeAssert.cs (NativeAndParity / DeclinesCleanly).

INPUT DATA (in this dir)
- nativeonly-gaps.tsv: 129 test methods that PASS when every context is forced to DriverLinq but FAIL when forced to
  NativeOnly (same assertions both runs; MQL assertions disabled). Columns: test, EF versions, NativeOnly failure.
- probes-branch.txt / probes-main.txt / ZzReviewProbeTests.cs: probe results (DriverLinq/Native/NativeOnly on
  branch; driver on main) behind the confirmed findings below.

CONFIRMED REVIEW FINDINGS (EF10 probes)
Silent wrong (in scope, must fix on branch):
 F1 Principal-side reference Include (1:1, FK on dependent, required) drops principals without dependent in ALL modes
    incl. forced DriverLinq; main correct. MongoQueryableMethodTranslatingExpressionVisitor.cs:1527
    PreserveNullAndEmptyArrays = !navigation.ForeignKey.IsRequired (ignores IsOnDependent).
 F2 DateTime.UtcNow/Now/Today(.AddX) receiver evaluated once at compile time (MongoExpressionTranslator.cs:357,
    TryEvaluateClosedSubtree 385-415) → stale cached plan; Now relabeled UTC via SpecifyKind (406) → off by TZ offset.
 F3 Non-nullable computed projection over null/missing reads 0 (driver FormatException): Score!.Value+1, *2, /2,
    Math.Abs, conditional, Rank+1 with Rank missing. Alias read BsonBinding.TryReadElementValue returns default.
 F4 Sum(int) overflow wraps (driver OverflowException): MongoShapedQueryCompilingExpressionVisitor.cs:925-926.
 F5 string StartsWith/EndsWith/Contains on [BsonRepresentation(ObjectId)] string Id → [] (driver: server error).
    Regex arm in MongoExpressionTranslator gates only on ClrType==string.
 F6 Math.Sign(nullable.Value) in predicate over null matches (driver: unsupported).
Loud in DEFAULT mode (worked on main, now throws even with fallback available):
 F7 null parameter vs non-nullable property (==, !=) and Contains with null in list → NullReferenceException
    (MongoPipelineFactory.cs:766-767 SerializeParameter / SerializeThroughWriter).
 F8 Min/Max over enum, TimeSpan, DateOnly → InvalidCastException (MongoShapedQueryCompilingExpressionVisitor.cs:928).
 F9 Tuple equality with Guid element → ArgumentException (MongoValueRenderer.cs:45 BsonValue.Create).
 F10 bare Select(x => x.Rank) with Rank missing → throws "Document element ... missing" (main returned 0).
Pre-existing on main (track for main, not branch-blocking): nullable non-key Join matches null/missing to null;
 Join to derived TPH DbSet (db.Dogs) returns sibling Cat rows; StartsWith/ToLower on value-converted string matches
 nothing; Union/Distinct missing-vs-null (driver wrong, native right); filtered-Include Take(n)/Skip(n) with a
 parameter silently dropped (identical on main, per code reading); correlated-collection matcher doesn't check the
 outer key (NativeCorrelationMatcher.cs:55-75, same hole on main per code reading); TimeSpan default string
 storage compares/sorts lexicographically.
Branch refusals of queries that worked on main (EF-337 stored-ordering, EF-459 local-kind date parts):
 e.g. FunctionalTests ProjectionTests.Count_with_value_converter_in_predicate_is_refused (main asserted Count==4).
