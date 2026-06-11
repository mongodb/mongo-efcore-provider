/* Copyright 2023-present MongoDB Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.SpecificationTests;

public class MongoComplianceTest : ComplianceTestBase
{
    // Test bases from Microsoft.EntityFrameworkCore.Specification.Tests that have not yet been
    // overridden for the MongoDB provider. As tests are implemented they should be removed from
    // this list so that the compliance test enforces continued coverage of newly added
    // specification tests.
    protected override ICollection<Type> IgnoredTestBases { get; } =
    [
        // Provider-wide non-query test bases (present across EF8/EF9/EF10).
        typeof(BadDataJsonDeserializationTestBase),
        typeof(ComplexTypesTrackingTestBase<>),
        typeof(CompositeKeyEndToEndTestBase<>),
        typeof(ConcurrencyDetectorDisabledTestBase<>),
        typeof(ConcurrencyDetectorEnabledTestBase<>),
        typeof(ConcurrencyDetectorTestBase<>),
        typeof(ConferencePlannerTestBase<>),
        typeof(ConvertToProviderTypesTestBase<>),
        typeof(CustomConvertersTestBase<>),
        typeof(DataAnnotationTestBase<>),
        typeof(DataBindingTestBase<>),
        typeof(EntityFrameworkServiceCollectionExtensionsTestBase),
        typeof(FieldMappingTestBase<>),
        typeof(FieldsOnlyLoadTestBase<>),
        typeof(FindTestBase<>),
        typeof(GraphUpdatesTestBase<>),
        typeof(InterceptionTestBase),
        typeof(JsonTypesTestBase),
        typeof(KeysWithConvertersTestBase<>),
        typeof(LazyLoadProxyTestBase<>),
        typeof(LoadTestBase<>),
        typeof(LoggingTestBase),
        typeof(ManyToManyFieldsLoadTestBase<>),
        typeof(ManyToManyLoadTestBase<>),
        typeof(ManyToManyTrackingTestBase<>),
        typeof(MaterializationInterceptionTestBase<>),
        typeof(ModelBuilding101TestBase),
        typeof(MonsterFixupTestBase<>),
        typeof(MusicStoreTestBase<>),
        typeof(NotificationEntitiesTestBase<>),
        typeof(OptimisticConcurrencyTestBase<,>),
        typeof(OverzealousInitializationTestBase<>),
        typeof(OwnedEntityQueryTestBase),
        typeof(PropertyValuesTestBase<>),
        typeof(ProxyGraphUpdatesTestBase<>),
        typeof(QueryExpressionInterceptionTestBase),
        typeof(SaveChangesInterceptionTestBase),
        typeof(SeedingTestBase),
        typeof(SerializationTestBase<>),
        typeof(SharedTypeQueryTestBase),
        typeof(SingletonInterceptorsTestBase<>),
        typeof(SpatialTestBase<>),
        typeof(StoreGeneratedFixupTestBase<>),
        typeof(StoreGeneratedTestBase<>),
        typeof(ValueConvertersEndToEndTestBase<>),
        typeof(WithConstructorsTestBase<>),

        // Query test bases (present across EF8/EF9/EF10).
        typeof(ComplexNavigationsCollectionsQueryTestBase<>),
        typeof(ComplexNavigationsCollectionsSharedTypeQueryTestBase<>),
        typeof(ComplexNavigationsQueryTestBase<>),
        typeof(ComplexNavigationsSharedTypeQueryTestBase<>),
        typeof(ComplexTypeQueryTestBase<>),
        typeof(CompositeKeysQueryTestBase<>),
        typeof(Ef6GroupByTestBase<>),
        typeof(FilteredQueryTestBase<>),
        typeof(FiltersInheritanceQueryTestBase<>),
        typeof(FunkyDataQueryTestBase<>),
        typeof(GearsOfWarQueryTestBase<>),
        typeof(IncludeOneToOneTestBase<>),
        typeof(InheritanceQueryTestBase<>),
        typeof(InheritanceRelationshipsQueryTestBase<>),
        typeof(ManyToManyNoTrackingQueryTestBase<>),
        typeof(ManyToManyQueryTestBase<>),
        typeof(NonSharedPrimitiveCollectionsQueryTestBase),
        typeof(NullKeysTestBase<>),
        typeof(OwnedQueryTestBase<>),
        typeof(PrimitiveCollectionsQueryTestBase<>),
        typeof(QueryFilterFuncletizationTestBase<>),
        typeof(QueryTestBase<>),
        typeof(SpatialQueryTestBase<>),
        typeof(AdHocAdvancedMappingsQueryTestBase),
        typeof(AdHocComplexTypeQueryTestBase),

#if EF8
        // EF8-only test bases.
        typeof(ManyToManyHeterogeneousQueryTestBase),
        typeof(SimpleQueryTestBase),
        typeof(UpdatesTestBase<>),
#endif

#if !EF8
        // Test bases added in EF9+.
        typeof(AdHocManyToManyQueryTestBase),
        typeof(AdHocMiscellaneousQueryTestBase),
        typeof(AdHocNavigationsQueryTestBase),
        typeof(AdHocQueryFiltersQueryTestBase),
        // Bulk ExecuteUpdate/ExecuteDelete ARE supported (EF9+), but only for single-collection,
        // Where-scoped queries with scalar setters. These EF conformance suites target owned-collection,
        // table-sharing, cross-collection/navigation, and join scenarios outside that supported subset,
        // so they remain ignored here; behavioral coverage lives in
        // FunctionalTests/Query/ExecuteDeleteTests.cs and ExecuteUpdateTests.cs.
        typeof(Microsoft.EntityFrameworkCore.BulkUpdates.BulkUpdatesTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.BulkUpdates.FiltersInheritanceBulkUpdatesTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.BulkUpdates.InheritanceBulkUpdatesTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.BulkUpdates.NonSharedModelBulkUpdatesTestBase),
        // NorthwindBulkUpdatesTestBase is now implemented by NorthwindBulkUpdatesMongoTest: the supported
        // single-collection Where-scoped scalar cases, as well as OrderBy/Skip/Take/Distinct-scoped cases
        // (executed via the two-phase _id-projection path), call base and pass; everything outside that
        // subset (joins, set ops, GroupBy, SelectMany, navigations, non-entity projections,
        // multiple-collection updates) is not skipped there — each runs and asserts its actual failure
        // mode (translation failure or cross-DbSet rejection), tagged with a // Fails: <reason> comment.
        typeof(JsonQueryTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.ModelBuilding.ModelBuilderTest.ComplexTypeTestBase),
        typeof(Microsoft.EntityFrameworkCore.ModelBuilding.ModelBuilderTest.InheritanceTestBase),
        typeof(Microsoft.EntityFrameworkCore.ModelBuilding.ModelBuilderTest.ManyToManyTestBase),
        typeof(Microsoft.EntityFrameworkCore.ModelBuilding.ModelBuilderTest.ManyToOneTestBase),
        typeof(Microsoft.EntityFrameworkCore.ModelBuilding.ModelBuilderTest.ModelBuilderTestBase),
        typeof(Microsoft.EntityFrameworkCore.ModelBuilding.ModelBuilderTest.NonRelationshipTestBase),
        typeof(Microsoft.EntityFrameworkCore.ModelBuilding.ModelBuilderTest.OneToManyTestBase),
        typeof(Microsoft.EntityFrameworkCore.ModelBuilding.ModelBuilderTest.OneToOneTestBase),
        typeof(Microsoft.EntityFrameworkCore.ModelBuilding.ModelBuilderTest.OwnedTypesTestBase),
        typeof(Microsoft.EntityFrameworkCore.Scaffolding.CompiledModelTestBase),
        typeof(Microsoft.EntityFrameworkCore.Update.UpdatesTestBase<>),
#endif

#if EF9
        // Test bases that existed only in EF9.
        // Bulk ExecuteUpdate/ExecuteDelete ARE supported (EF9+), but only for single-collection,
        // Where-scoped queries with scalar setters. This complex-type conformance suite targets
        // scenarios outside that supported subset, so it remains ignored here; behavioral coverage
        // lives in FunctionalTests/Query/ExecuteDeleteTests.cs and ExecuteUpdateTests.cs.
        typeof(Microsoft.EntityFrameworkCore.BulkUpdates.ComplexTypeBulkUpdatesTestBase<>),
#endif

#if !EF8 && !EF9
        // Test bases added in EF10+.
        typeof(AdHocJsonQueryTestBase),
        // NorthwindFunctionsQueryMongoTest exists only for EF8 and EF9 — the EF10 build deliberately omits it.
        typeof(NorthwindFunctionsQueryTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.ModelBuilding.ModelBuilderTest.ComplexCollectionTestBase),
        typeof(Microsoft.EntityFrameworkCore.Query.Associations.AssociationsBulkUpdateTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Associations.AssociationsCollectionTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Associations.AssociationsMiscellaneousTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Associations.AssociationsPrimitiveCollectionTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Associations.AssociationsProjectionTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Associations.AssociationsSetOperationsTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Associations.AssociationsStructuralEqualityTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Associations.ComplexProperties.ComplexPropertiesBulkUpdateTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Associations.ComplexProperties.ComplexPropertiesCollectionTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Associations.ComplexProperties.ComplexPropertiesMiscellaneousTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Associations.ComplexProperties.ComplexPropertiesPrimitiveCollectionTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Associations.ComplexProperties.ComplexPropertiesProjectionTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Associations.ComplexProperties.ComplexPropertiesSetOperationsTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Associations.ComplexProperties.ComplexPropertiesStructuralEqualityTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Associations.Navigations.NavigationsCollectionTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Associations.Navigations.NavigationsIncludeTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Associations.Navigations.NavigationsMiscellaneousTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Associations.Navigations.NavigationsPrimitiveCollectionTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Associations.Navigations.NavigationsProjectionTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Associations.Navigations.NavigationsSetOperationsTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Associations.Navigations.NavigationsStructuralEqualityTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Associations.OwnedNavigations.OwnedNavigationsCollectionTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Associations.OwnedNavigations.OwnedNavigationsMiscellaneousTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Associations.OwnedNavigations.OwnedNavigationsPrimitiveCollectionTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Associations.OwnedNavigations.OwnedNavigationsProjectionTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Associations.OwnedNavigations.OwnedNavigationsSetOperationsTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Associations.OwnedNavigations.OwnedNavigationsStructuralEqualityTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Translations.ByteArrayTranslationsTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Translations.EnumTranslationsTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Translations.GuidTranslationsTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Translations.MathTranslationsTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Translations.MiscellaneousTranslationsTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Translations.Operators.ArithmeticOperatorTranslationsTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Translations.Operators.BitwiseOperatorTranslationsTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Translations.Operators.ComparisonOperatorTranslationsTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Translations.Operators.LogicalOperatorTranslationsTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Translations.Operators.MiscellaneousOperatorTranslationsTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Translations.StringTranslationsTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Translations.Temporal.DateOnlyTranslationsTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Translations.Temporal.DateTimeOffsetTranslationsTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Translations.Temporal.DateTimeTranslationsTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Translations.Temporal.TimeOnlyTranslationsTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Query.Translations.Temporal.TimeSpanTranslationsTestBase<>),
        typeof(Microsoft.EntityFrameworkCore.Types.TypeTestBase<,>),
#endif
    ];

    protected override Assembly TargetAssembly
        => typeof(MongoComplianceTest).Assembly;
}
