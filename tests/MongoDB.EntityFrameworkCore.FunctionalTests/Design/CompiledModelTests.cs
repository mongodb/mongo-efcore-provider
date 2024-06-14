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

using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Design;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Design;

[XUnitCollection("DesignTests")]
public class CompiledModelTests(TemporaryDatabaseFixture tempDatabase)
    : IClassFixture<TemporaryDatabaseFixture>
{
    public class EveryType
    {
        public ObjectId id { get; set; }

        public Decimal128 aDecimal128 { get; set; }

        public string aString { get; set; }
        public Guid aGuid { get; set; }

        public int anInt { get; set; }
        public short aShort { get; set; }
        public long aLong { get; set; }

        public decimal aDecimal { get; set; }
        public float aFloat { get; set; }
        public double aDouble { get; set; }

        public string[] aStringArray { get; set; }
        public List<int> anIntList { get; set; }

        public List<OwnedEntity> ownedEntities { get; set; }
    }

    public class OwnedEntity
    {
        public string name { get; set; }
    }

    public class SimpleContext(DbContextOptions options)
        : DbContext(options)
    {
        public DbSet<EveryType> EveryTypes { get; set; }
    }

    [Fact]
    public void Can_generate_code_for_model()
    {
        var (db, scope) = GetDesignTimeConfigured<SimpleContext>();
        var files = GenerateModel(db);
        scope.Dispose();

        Assert.Single(files, f => f.Path == nameof(SimpleContext) + "Model.cs");
        Assert.Single(files, f => f.Path == nameof(SimpleContext) + "ModelBuilder.cs");
        Assert.Single(files, f => f.Path == nameof(EveryType) + "EntityType.cs");
        Assert.Single(files, f => f.Path == nameof(OwnedEntity) + "EntityType.cs");
    }

    [Fact]
    public void Regenerate_model_code_for_roundtrip_tests()
    {
        // This will re-generate local code-generated model files for SimpleContext
        // If they change you should run the tests again and check them in.
        var (db, scope) = GetDesignTimeConfigured<SimpleContext>();
        foreach (var file in GenerateModel(db))
        {
            WriteScaffoldedFile(file);
        }
        scope.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Can_roundtrip(bool useCompiledModel)
    {
        var expected = new EveryType
        {
            id = ObjectId.GenerateNewId(),
            aDecimal = 123.45m,
            aDecimal128 = new Decimal128(456.78m),
            aDouble = 789.10,
            aFloat = 11.12f,
            aGuid = Guid.NewGuid(),
            aLong = 678901,
            anInt = 23456,
            aShort = 129,
            aString = "Hello, World!",
            anIntList = [1, 2, 3],
            aStringArray = ["a", "b", "c"],
            ownedEntities = [new OwnedEntity {name = "Owned"}]
        };

        Action<DbContextOptionsBuilder>? configOptionsBuilder = useCompiledModel
            ? b => b.UseModel(SimpleContextModel.Instance)
            : null;

        {
            var (db, scope) = GetDesignTimeConfigured<SimpleContext>(configOptionsBuilder);
            db.EveryTypes.Add(expected);
            db.SaveChanges();
            scope.Dispose();
        }

        {
            var (db, scope) = GetDesignTimeConfigured<SimpleContext>(configOptionsBuilder);
            var actual = db.EveryTypes.First(e => e.id == expected.id);
            scope.Dispose();
            Assert.Equivalent(expected, actual);
        }
    }

    private static IReadOnlyCollection<ScaffoldedFile> GenerateModel(SimpleContext context)
    {
        var designTimeModel = context.GetService<IDesignTimeModel>();
        var codeGenerator = context.GetService<ICompiledModelCodeGenerator>();
        var options = new CompiledModelCodeGenerationOptions
        {
            Language = "C#", ModelNamespace = context.GetType().Namespace!, ContextType = context.GetType()
        };

        return codeGenerator.GenerateModel(designTimeModel.Model, options);
    }

    private (T, IServiceScope) GetDesignTimeConfigured<T>(Action<DbContextOptionsBuilder>? configOptionsBuilder = null) where T : DbContext
    {
        var serviceCollection = new ServiceCollection()
            .AddEntityFrameworkMongoDB()
            .AddEntityFrameworkDesignTimeServices()
            .AddDbContext<T>((p, b) =>
            {
                b.UseMongoDB(tempDatabase.Client, tempDatabase.MongoDatabase.DatabaseNamespace.DatabaseName)
                    .UseInternalServiceProvider(p);

                configOptionsBuilder?.Invoke(b);
            });

        new MongoDesignTimeServices().ConfigureDesignTimeServices(serviceCollection);

        var serviceProvider = serviceCollection.BuildServiceProvider(validateScopes: true);
        var scope = serviceProvider.CreateScope();

        return (scope.ServiceProvider.GetRequiredService<T>(), scope);
    }

    private static void WriteScaffoldedFile(ScaffoldedFile file, [CallerFilePath] string callerFilePath = "")
    {
        var callerDirectory = Path.GetDirectoryName(callerFilePath);
        Assert.NotNull(callerDirectory);

        var generatedCodePath = Path.Combine(callerDirectory, "Generated");
        Directory.CreateDirectory(generatedCodePath);

        var fileName = Path.Combine(generatedCodePath, file.Path);
        File.WriteAllText(fileName, file.Code);
    }
}
