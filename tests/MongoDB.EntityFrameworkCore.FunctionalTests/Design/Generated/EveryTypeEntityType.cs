// <auto-generated />
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.ChangeTracking;
using MongoDB.EntityFrameworkCore.Storage;

#pragma warning disable 219, 612, 618
#nullable disable

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Design
{
    internal partial class EveryTypeEntityType
    {
        public static RuntimeEntityType Create(RuntimeModel model, RuntimeEntityType baseEntityType = null)
        {
            var runtimeEntityType = model.AddEntityType(
                "MongoDB.EntityFrameworkCore.FunctionalTests.Design.CompiledModelTests+EveryType",
                typeof(CompiledModelTests.EveryType),
                baseEntityType);

            var id = runtimeEntityType.AddProperty(
                "id",
                typeof(ObjectId),
                propertyInfo: typeof(CompiledModelTests.EveryType).GetProperty("id", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                fieldInfo: typeof(CompiledModelTests.EveryType).GetField("<id>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                valueGenerated: ValueGenerated.OnAdd,
                afterSaveBehavior: PropertySaveBehavior.Throw,
                sentinel: MongoDB.Bson.ObjectId.Parse("000000000000000000000000"));
            id.TypeMapping = MongoTypeMapping.Default.Clone(
                comparer: new ValueComparer<ObjectId>(
                    (ObjectId v1, ObjectId v2) => v1.Equals(v2),
                    (ObjectId v) => v.GetHashCode(),
                    (ObjectId v) => v),
                keyComparer: new ValueComparer<ObjectId>(
                    (ObjectId v1, ObjectId v2) => v1.Equals(v2),
                    (ObjectId v) => v.GetHashCode(),
                    (ObjectId v) => v),
                providerValueComparer: new ValueComparer<ObjectId>(
                    (ObjectId v1, ObjectId v2) => v1.Equals(v2),
                    (ObjectId v) => v.GetHashCode(),
                    (ObjectId v) => v),
                clrType: typeof(ObjectId));
            id.AddAnnotation("Mongo:ElementName", "_id");

            var aDecimal = runtimeEntityType.AddProperty(
                "aDecimal",
                typeof(decimal),
                propertyInfo: typeof(CompiledModelTests.EveryType).GetProperty("aDecimal", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                fieldInfo: typeof(CompiledModelTests.EveryType).GetField("<aDecimal>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                sentinel: 0m);
            aDecimal.TypeMapping = MongoTypeMapping.Default.Clone(
                comparer: new ValueComparer<decimal>(
                    (decimal v1, decimal v2) => v1 == v2,
                    (decimal v) => v.GetHashCode(),
                    (decimal v) => v),
                keyComparer: new ValueComparer<decimal>(
                    (decimal v1, decimal v2) => v1 == v2,
                    (decimal v) => v.GetHashCode(),
                    (decimal v) => v),
                providerValueComparer: new ValueComparer<decimal>(
                    (decimal v1, decimal v2) => v1 == v2,
                    (decimal v) => v.GetHashCode(),
                    (decimal v) => v),
                clrType: typeof(decimal));

            var aDecimal128 = runtimeEntityType.AddProperty(
                "aDecimal128",
                typeof(Decimal128),
                propertyInfo: typeof(CompiledModelTests.EveryType).GetProperty("aDecimal128", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                fieldInfo: typeof(CompiledModelTests.EveryType).GetField("<aDecimal128>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                sentinel: MongoDB.Bson.Decimal128.Parse("0"));
            aDecimal128.TypeMapping = MongoTypeMapping.Default.Clone(
                comparer: new ValueComparer<Decimal128>(
                    (Decimal128 v1, Decimal128 v2) => v1.Equals(v2),
                    (Decimal128 v) => v.GetHashCode(),
                    (Decimal128 v) => v),
                keyComparer: new ValueComparer<Decimal128>(
                    (Decimal128 v1, Decimal128 v2) => v1.Equals(v2),
                    (Decimal128 v) => v.GetHashCode(),
                    (Decimal128 v) => v),
                providerValueComparer: new ValueComparer<Decimal128>(
                    (Decimal128 v1, Decimal128 v2) => v1.Equals(v2),
                    (Decimal128 v) => v.GetHashCode(),
                    (Decimal128 v) => v),
                clrType: typeof(Decimal128));

            var aDouble = runtimeEntityType.AddProperty(
                "aDouble",
                typeof(double),
                propertyInfo: typeof(CompiledModelTests.EveryType).GetProperty("aDouble", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                fieldInfo: typeof(CompiledModelTests.EveryType).GetField("<aDouble>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                sentinel: 0.0);
            aDouble.TypeMapping = MongoTypeMapping.Default.Clone(
                comparer: new ValueComparer<double>(
                    (double v1, double v2) => v1.Equals(v2),
                    (double v) => v.GetHashCode(),
                    (double v) => v),
                keyComparer: new ValueComparer<double>(
                    (double v1, double v2) => v1.Equals(v2),
                    (double v) => v.GetHashCode(),
                    (double v) => v),
                providerValueComparer: new ValueComparer<double>(
                    (double v1, double v2) => v1.Equals(v2),
                    (double v) => v.GetHashCode(),
                    (double v) => v),
                clrType: typeof(double));

            var aFloat = runtimeEntityType.AddProperty(
                "aFloat",
                typeof(float),
                propertyInfo: typeof(CompiledModelTests.EveryType).GetProperty("aFloat", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                fieldInfo: typeof(CompiledModelTests.EveryType).GetField("<aFloat>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                sentinel: 0f);
            aFloat.TypeMapping = MongoTypeMapping.Default.Clone(
                comparer: new ValueComparer<float>(
                    (float v1, float v2) => v1.Equals(v2),
                    (float v) => v.GetHashCode(),
                    (float v) => v),
                keyComparer: new ValueComparer<float>(
                    (float v1, float v2) => v1.Equals(v2),
                    (float v) => v.GetHashCode(),
                    (float v) => v),
                providerValueComparer: new ValueComparer<float>(
                    (float v1, float v2) => v1.Equals(v2),
                    (float v) => v.GetHashCode(),
                    (float v) => v),
                clrType: typeof(float));

            var aGuid = runtimeEntityType.AddProperty(
                "aGuid",
                typeof(Guid),
                propertyInfo: typeof(CompiledModelTests.EveryType).GetProperty("aGuid", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                fieldInfo: typeof(CompiledModelTests.EveryType).GetField("<aGuid>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                sentinel: new Guid("00000000-0000-0000-0000-000000000000"));
            aGuid.TypeMapping = MongoTypeMapping.Default.Clone(
                comparer: new ValueComparer<Guid>(
                    (Guid v1, Guid v2) => v1 == v2,
                    (Guid v) => v.GetHashCode(),
                    (Guid v) => v),
                keyComparer: new ValueComparer<Guid>(
                    (Guid v1, Guid v2) => v1 == v2,
                    (Guid v) => v.GetHashCode(),
                    (Guid v) => v),
                providerValueComparer: new ValueComparer<Guid>(
                    (Guid v1, Guid v2) => v1 == v2,
                    (Guid v) => v.GetHashCode(),
                    (Guid v) => v),
                clrType: typeof(Guid));

            var aLong = runtimeEntityType.AddProperty(
                "aLong",
                typeof(long),
                propertyInfo: typeof(CompiledModelTests.EveryType).GetProperty("aLong", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                fieldInfo: typeof(CompiledModelTests.EveryType).GetField("<aLong>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                sentinel: 0L);
            aLong.TypeMapping = MongoTypeMapping.Default.Clone(
                comparer: new ValueComparer<long>(
                    (long v1, long v2) => v1 == v2,
                    (long v) => v.GetHashCode(),
                    (long v) => v),
                keyComparer: new ValueComparer<long>(
                    (long v1, long v2) => v1 == v2,
                    (long v) => v.GetHashCode(),
                    (long v) => v),
                providerValueComparer: new ValueComparer<long>(
                    (long v1, long v2) => v1 == v2,
                    (long v) => v.GetHashCode(),
                    (long v) => v),
                clrType: typeof(long));

            var aLongRepresentedAsAInt = runtimeEntityType.AddProperty(
                "aLongRepresentedAsAInt",
                typeof(long),
                propertyInfo: typeof(CompiledModelTests.EveryType).GetProperty("aLongRepresentedAsAInt", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                fieldInfo: typeof(CompiledModelTests.EveryType).GetField("<aLongRepresentedAsAInt>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                sentinel: 0L);
            aLongRepresentedAsAInt.TypeMapping = MongoTypeMapping.Default.Clone(
                comparer: new ValueComparer<long>(
                    (long v1, long v2) => v1 == v2,
                    (long v) => v.GetHashCode(),
                    (long v) => v),
                keyComparer: new ValueComparer<long>(
                    (long v1, long v2) => v1 == v2,
                    (long v) => v.GetHashCode(),
                    (long v) => v),
                providerValueComparer: new ValueComparer<long>(
                    (long v1, long v2) => v1 == v2,
                    (long v) => v.GetHashCode(),
                    (long v) => v),
                clrType: typeof(long));
            aLongRepresentedAsAInt.AddAnnotation("Mongo:BsonRepresentation", new Dictionary<string, object> { ["BsonType"] = BsonType.Int32, ["AllowOverflow"] = true, ["AllowTruncation"] = true });

            var aShort = runtimeEntityType.AddProperty(
                "aShort",
                typeof(short),
                propertyInfo: typeof(CompiledModelTests.EveryType).GetProperty("aShort", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                fieldInfo: typeof(CompiledModelTests.EveryType).GetField("<aShort>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                sentinel: (short)0);
            aShort.TypeMapping = MongoTypeMapping.Default.Clone(
                comparer: new ValueComparer<short>(
                    (short v1, short v2) => v1 == v2,
                    (short v) => (int)v,
                    (short v) => v),
                keyComparer: new ValueComparer<short>(
                    (short v1, short v2) => v1 == v2,
                    (short v) => (int)v,
                    (short v) => v),
                providerValueComparer: new ValueComparer<short>(
                    (short v1, short v2) => v1 == v2,
                    (short v) => (int)v,
                    (short v) => v),
                clrType: typeof(short));

            var aString = runtimeEntityType.AddProperty(
                "aString",
                typeof(string),
                propertyInfo: typeof(CompiledModelTests.EveryType).GetProperty("aString", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                fieldInfo: typeof(CompiledModelTests.EveryType).GetField("<aString>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly));
            aString.TypeMapping = MongoTypeMapping.Default.Clone(
                comparer: new ValueComparer<string>(
                    (string v1, string v2) => v1 == v2,
                    (string v) => v.GetHashCode(),
                    (string v) => v),
                keyComparer: new ValueComparer<string>(
                    (string v1, string v2) => v1 == v2,
                    (string v) => v.GetHashCode(),
                    (string v) => v),
                providerValueComparer: new ValueComparer<string>(
                    (string v1, string v2) => v1 == v2,
                    (string v) => v.GetHashCode(),
                    (string v) => v),
                clrType: typeof(string));

            var aStringArray = runtimeEntityType.AddProperty(
                "aStringArray",
                typeof(string[]),
                propertyInfo: typeof(CompiledModelTests.EveryType).GetProperty("aStringArray", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                fieldInfo: typeof(CompiledModelTests.EveryType).GetField("<aStringArray>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly));
            aStringArray.TypeMapping = MongoTypeMapping.Default.Clone(
                comparer: new ListOfReferenceTypesComparer<string[], string>(new ValueComparer<string>(
                    (string v1, string v2) => v1 == v2,
                    (string v) => v.GetHashCode(),
                    (string v) => v)),
                keyComparer: new ValueComparer<string[]>(
                    (String[] v1, String[] v2) => StructuralComparisons.StructuralEqualityComparer.Equals((object)v1, (object)v2),
                    (String[] v) => StructuralComparisons.StructuralEqualityComparer.GetHashCode((object)v),
                    (String[] source) => source.ToArray()),
                providerValueComparer: new ValueComparer<string[]>(
                    (String[] v1, String[] v2) => StructuralComparisons.StructuralEqualityComparer.Equals((object)v1, (object)v2),
                    (String[] v) => StructuralComparisons.StructuralEqualityComparer.GetHashCode((object)v),
                    (String[] source) => source.ToArray()),
                clrType: typeof(string[]));

            var anEnum = runtimeEntityType.AddProperty(
                "anEnum",
                typeof(CompiledModelTests.TestEnum),
                propertyInfo: typeof(CompiledModelTests.EveryType).GetProperty("anEnum", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                fieldInfo: typeof(CompiledModelTests.EveryType).GetField("<anEnum>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                sentinel: CompiledModelTests.TestEnum.A);
            anEnum.TypeMapping = MongoTypeMapping.Default.Clone(
                comparer: new ValueComparer<CompiledModelTests.TestEnum>(
                    (CompiledModelTests.TestEnum v1, CompiledModelTests.TestEnum v2) => object.Equals((object)v1, (object)v2),
                    (CompiledModelTests.TestEnum v) => v.GetHashCode(),
                    (CompiledModelTests.TestEnum v) => v),
                keyComparer: new ValueComparer<CompiledModelTests.TestEnum>(
                    (CompiledModelTests.TestEnum v1, CompiledModelTests.TestEnum v2) => object.Equals((object)v1, (object)v2),
                    (CompiledModelTests.TestEnum v) => v.GetHashCode(),
                    (CompiledModelTests.TestEnum v) => v),
                providerValueComparer: new ValueComparer<CompiledModelTests.TestEnum>(
                    (CompiledModelTests.TestEnum v1, CompiledModelTests.TestEnum v2) => object.Equals((object)v1, (object)v2),
                    (CompiledModelTests.TestEnum v) => v.GetHashCode(),
                    (CompiledModelTests.TestEnum v) => v),
                clrType: typeof(CompiledModelTests.TestEnum));

            var anInt = runtimeEntityType.AddProperty(
                "anInt",
                typeof(int),
                propertyInfo: typeof(CompiledModelTests.EveryType).GetProperty("anInt", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                fieldInfo: typeof(CompiledModelTests.EveryType).GetField("<anInt>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                sentinel: 0);
            anInt.TypeMapping = MongoTypeMapping.Default.Clone(
                comparer: new ValueComparer<int>(
                    (int v1, int v2) => v1 == v2,
                    (int v) => v,
                    (int v) => v),
                keyComparer: new ValueComparer<int>(
                    (int v1, int v2) => v1 == v2,
                    (int v) => v,
                    (int v) => v),
                providerValueComparer: new ValueComparer<int>(
                    (int v1, int v2) => v1 == v2,
                    (int v) => v,
                    (int v) => v),
                clrType: typeof(int));

            var anIntList = runtimeEntityType.AddProperty(
                "anIntList",
                typeof(List<int>),
                propertyInfo: typeof(CompiledModelTests.EveryType).GetProperty("anIntList", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                fieldInfo: typeof(CompiledModelTests.EveryType).GetField("<anIntList>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly));
            anIntList.TypeMapping = MongoTypeMapping.Default.Clone(
                comparer: new ListOfValueTypesComparer<List<int>, int>(new ValueComparer<int>(
                    (int v1, int v2) => v1 == v2,
                    (int v) => v,
                    (int v) => v)),
                keyComparer: new ValueComparer<List<int>>(
                    (List<int> v1, List<int> v2) => object.Equals(v1, v2),
                    (List<int> v) => v.GetHashCode(),
                    (List<int> v) => v),
                providerValueComparer: new ValueComparer<List<int>>(
                    (List<int> v1, List<int> v2) => object.Equals(v1, v2),
                    (List<int> v) => v.GetHashCode(),
                    (List<int> v) => v),
                clrType: typeof(List<int>));

            var anIntRepresentedAsAString = runtimeEntityType.AddProperty(
                "anIntRepresentedAsAString",
                typeof(int),
                propertyInfo: typeof(CompiledModelTests.EveryType).GetProperty("anIntRepresentedAsAString", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                fieldInfo: typeof(CompiledModelTests.EveryType).GetField("<anIntRepresentedAsAString>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                sentinel: 0);
            anIntRepresentedAsAString.TypeMapping = MongoTypeMapping.Default.Clone(
                comparer: new ValueComparer<int>(
                    (int v1, int v2) => v1 == v2,
                    (int v) => v,
                    (int v) => v),
                keyComparer: new ValueComparer<int>(
                    (int v1, int v2) => v1 == v2,
                    (int v) => v,
                    (int v) => v),
                providerValueComparer: new ValueComparer<int>(
                    (int v1, int v2) => v1 == v2,
                    (int v) => v,
                    (int v) => v),
                clrType: typeof(int));
            anIntRepresentedAsAString.AddAnnotation("Mongo:BsonRepresentation", new Dictionary<string, object> { ["BsonType"] = BsonType.String, ["AllowOverflow"] = false, ["AllowTruncation"] = false });

            var key = runtimeEntityType.AddKey(
                new[] { id });
            runtimeEntityType.SetPrimaryKey(key);

            return runtimeEntityType;
        }

        public static void CreateAnnotations(RuntimeEntityType runtimeEntityType)
        {
            runtimeEntityType.AddAnnotation("Mongo:CollectionName", "EveryTypes");

            Customize(runtimeEntityType);
        }

        static partial void Customize(RuntimeEntityType runtimeEntityType);
    }
}
