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

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.TestUtilities;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

#if EF9

public class AdHocJsonQueryMongoTest : AdHocJsonQueryTestBase
{
    [ConditionalFact]
    public virtual void Check_all_tests_overridden()
        => TestHelpers.AssertAllMethodsOverridden(GetType());

    public override async Task Project_root_with_missing_scalars(bool async)
    {
        // Fails: Missing property values issue EF-164
        Assert.Contains(
            "Document element is missing for required non-nullable property 'Number'",
            (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                base.Project_root_with_missing_scalars(async)))
            .Message);

        AssertMql(
            """
            Entities.{ "$match" : { "_id" : { "$lt" : 4 } } }
            """);
    }

    public override async Task Project_top_level_json_entity_with_missing_scalars(bool async)
    {
        // Fails: Projections issue EF-76
        Assert.Contains(
            "Argument type 'System.Collections.Generic.IEnumerable`1[Microsoft.EntityFrameworkCore.Query.AdHocJsonQueryTestBase+Context21006+JsonEntity]' does not match",
            (await Assert.ThrowsAsync<ArgumentException>(
                () => base.Project_top_level_json_entity_with_missing_scalars(async)))
            .Message);

        AssertMql();
    }

    public override async Task Project_nested_json_entity_with_missing_scalars(bool async)
    {
        // Fails: Projections issue EF-76
        Assert.Contains(
            "An item with the same key has already been added.",
            (await Assert.ThrowsAsync<ArgumentException>(() =>
                base.Project_nested_json_entity_with_missing_scalars(async)))
            .Message);

        AssertMql();
    }

    public override async Task Project_top_level_entity_with_null_value_required_scalars(bool async)
    {
        // Fails: Projections issue EF-76
        Assert.Contains(
            "An error occurred while deserializing the RequiredReference property",
            (await Assert.ThrowsAsync<FormatException>(() =>
                base.Project_top_level_entity_with_null_value_required_scalars(async)))
            .Message);

        AssertMql(
            """
            Entities.{ "$match" : { "_id" : 4 } }, { "$project" : { "_id" : "$_id", "RequiredReference" : "$RequiredReference" } }
            """);
    }

    public override async Task Project_root_entity_with_missing_required_navigation(bool async)
    {
        // Fails: Missing property values issue EF-164
        Assert.Contains(
            "Field 'RequiredReference' required but not present",
            (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                base.Project_root_entity_with_missing_required_navigation(async)))
            .Message);

        AssertMql(
            """
            Entities.{ "$match" : { "_id" : 5 } }
            """);
    }

    public override async Task Project_missing_required_navigation(bool async)
    {
        await base.Project_missing_required_navigation(async);

        AssertMql(
            """
            Entities.{ "$match" : { "_id" : 5 } }, { "$project" : { "_v" : "$RequiredReference.NestedRequiredReference", "_id" : 0 } }
            """);
    }

    public override async Task Project_root_entity_with_null_required_navigation(bool async)
    {
        // Fails: Projections issue EF-164
        Assert.Contains(
            "Field 'NestedRequiredReference' required but not present",
            (await Assert.ThrowsAsync<InvalidOperationException>(
                () => base.Project_root_entity_with_null_required_navigation(async)))
            .Message);

        AssertMql(
            """
            Entities.{ "$match" : { "_id" : 6 } }
            """);
    }

    public override async Task Project_null_required_navigation(bool async)
    {
        // Fails: Projections issue EF-76
        Assert.Contains(
            "The method or operation is not implemented.",
            (await Assert.ThrowsAsync<NotImplementedException>(
                () => base.Project_null_required_navigation(async)))
            .Message);

        AssertMql(
            """
            Entities.{ "$match" : { "_id" : 6 } }, { "$project" : { "_v" : "$RequiredReference", "_id" : 0 } }
            """);
    }

    public override async Task Project_missing_required_scalar(bool async)
    {
        await base.Project_missing_required_scalar(async);

        AssertMql(
            """
            Entities.{ "$match" : { "_id" : 2 } }, { "$project" : { "_id" : "$_id", "Number" : "$RequiredReference.Number" } }
            """);
    }

    public override async Task Project_null_required_scalar(bool async)
    {
        await base.Project_null_required_scalar(async);

        AssertMql(
            """
            Entities.{ "$match" : { "_id" : 4 } }, { "$project" : { "_id" : "$_id", "Number" : "$RequiredReference.Number" } }
            """);
    }

    protected override void OnModelCreating21006(ModelBuilder modelBuilder)
    {
        base.OnModelCreating21006(modelBuilder);

        modelBuilder.Entity<Context21006.Entity>().ToCollection("Entities");
    }

    protected override async Task Seed21006(Context21006 context)
    {
        await base.Seed21006(context);

        var wrapper = (MongoClientWrapper)context.GetService<IMongoClientWrapper>();
        var collection = wrapper.GetCollection<BsonDocument>("Entities");

        var missingTopLevel = new BsonDocument
        {
            ["_id"] = 2,
            ["Name"] = "e2",
            ["Collection"] =
                new BsonArray
                {
                    new BsonDocument
                    {
                        ["Text"] = "e2 c1",
                        ["NestedCollection"] =
                            new BsonArray
                            {
                                new BsonDocument
                                {
                                    ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                    ["Text"] = "e2 c1 c1"
                                },
                                new BsonDocument
                                {
                                    ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                    ["Text"] = "e2 c1 c2"
                                }
                            },
                        ["NestedOptionalReference"] =
                            new BsonDocument
                            {
                                ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                ["Text"] = "e2 c1 nor"
                            },
                        ["NestedRequiredReference"] =
                            new BsonDocument
                            {
                                ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                ["Text"] = "e2 c1 nrr"
                            }
                    },
                    new BsonDocument
                    {
                        ["Text"] = "e2 c2",
                        ["NestedCollection"] =
                            new BsonArray
                            {
                                new BsonDocument
                                {
                                    ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                    ["Text"] = "e2 c2 c1"
                                },
                                new BsonDocument
                                {
                                    ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                    ["Text"] = "e2 c2 c2"
                                }
                            },
                        ["NestedOptionalReference"] =
                            new BsonDocument
                            {
                                ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                ["Text"] = "e2 c2 nor"
                            },
                        ["NestedRequiredReference"] =
                            new BsonDocument
                            {
                                ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                ["Text"] = "e2 c2 nrr"
                            }
                    }
                },
            ["OptionalReference"] = new BsonDocument
            {
                ["Text"] = "e2 or",
                ["NestedCollection"] =
                    new BsonArray
                    {
                        new BsonDocument
                        {
                            ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                            ["Text"] = "e2 or c1"
                        },
                        new BsonDocument
                        {
                            ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                            ["Text"] = "e2 or c2"
                        }
                    },
                ["NestedOptionalReference"] =
                    new BsonDocument
                    {
                        ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                        ["Text"] = "e2 or nor"
                    },
                ["NestedRequiredReference"] =
                    new BsonDocument
                    {
                        ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                        ["Text"] = "e2 or nrr"
                    }
            },
            ["RequiredReference"] = new BsonDocument
            {
                ["Text"] = "e2 rr",
                ["NestedCollection"] =
                    new BsonArray
                    {
                        new BsonDocument
                        {
                            ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                            ["Text"] = "e2 rr c1"
                        },
                        new BsonDocument
                        {
                            ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                            ["Text"] = "e2 rr c2"
                        }
                    },
                ["NestedOptionalReference"] =
                    new BsonDocument
                    {
                        ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                        ["Text"] = "e2 rr nor"
                    },
                ["NestedRequiredReference"] = new BsonDocument
                {
                    ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    ["Text"] = "e2 rr nrr"
                }
            }
        };

        var missingNested = new BsonDocument
        {
            ["_id"] = 3,
            ["Name"] = "e3",
            ["Collection"] =
                new BsonArray
                {
                    new BsonDocument
                    {
                        ["Number"] = 7.0,
                        ["Text"] = "e3 c1",
                        ["NestedCollection"] =
                            new BsonArray
                            {
                                new BsonDocument { ["Text"] = "e3 c1 c1" },
                                new BsonDocument { ["Text"] = "e3 c1 c2" }
                            },
                        ["NestedOptionalReference"] = new BsonDocument { ["Text"] = "e3 c1 nor" },
                        ["NestedRequiredReference"] = new BsonDocument { ["Text"] = "e3 c1 nrr" }
                    },
                    new BsonDocument
                    {
                        ["Number"] = 7.0,
                        ["Text"] = "e3 c2",
                        ["NestedCollection"] =
                            new BsonArray
                            {
                                new BsonDocument { ["Text"] = "e3 c2 c1" },
                                new BsonDocument { ["Text"] = "e3 c2 c2" }
                            },
                        ["NestedOptionalReference"] = new BsonDocument { ["Text"] = "e3 c2 nor" },
                        ["NestedRequiredReference"] = new BsonDocument { ["Text"] = "e3 c2 nrr" }
                    }
                },
            ["OptionalReference"] = new BsonDocument
            {
                ["Number"] = 7.0,
                ["Text"] = "e3 or",
                ["NestedCollection"] =
                    new BsonArray
                    {
                        new BsonDocument { ["Text"] = "e3 or c1" },
                        new BsonDocument { ["Text"] = "e3 or c2" }
                    },
                ["NestedOptionalReference"] = new BsonDocument { ["Text"] = "e3 or nor" },
                ["NestedRequiredReference"] = new BsonDocument { ["Text"] = "e3 or nrr" }
            },
            ["RequiredReference"] = new BsonDocument
            {
                ["Number"] = 7.0,
                ["Text"] = "e3 rr",
                ["NestedCollection"] =
                    new BsonArray
                    {
                        new BsonDocument { ["Text"] = "e3 rr c1" },
                        new BsonDocument { ["Text"] = "e3 rr c2" }
                    },
                ["NestedOptionalReference"] = new BsonDocument { ["Text"] = "e3 rr nor" },
                ["NestedRequiredReference"] = new BsonDocument { ["Text"] = "e3 rr nrr" }
            }
        };

        var nullTopLevel = new BsonDocument
        {
            ["_id"] = 4,
            ["Name"] = "e4",
            ["Collection"] =
                new BsonArray
                {
                    new BsonDocument
                    {
                        ["Number"] = BsonNull.Value,
                        ["Text"] = "e4 c1",
                        ["NestedCollection"] =
                            new BsonArray
                            {
                                new BsonDocument
                                {
                                    ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                    ["Text"] = "e4 c1 c1"
                                },
                                new BsonDocument
                                {
                                    ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                    ["Text"] = "e4 c1 c2"
                                }
                            },
                        ["NestedOptionalReference"] =
                            new BsonDocument
                            {
                                ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                ["Text"] = "e4 c1 nor"
                            },
                        ["NestedRequiredReference"] =
                            new BsonDocument
                            {
                                ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                ["Text"] = "e4 c1 nrr"
                            }
                    },
                    new BsonDocument
                    {
                        ["Number"] = BsonNull.Value,
                        ["Text"] = "e4 c2",
                        ["NestedCollection"] =
                            new BsonArray
                            {
                                new BsonDocument
                                {
                                    ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                    ["Text"] = "e4 c2 c1"
                                },
                                new BsonDocument
                                {
                                    ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                    ["Text"] = "e4 c2 c2"
                                }
                            },
                        ["NestedOptionalReference"] =
                            new BsonDocument
                            {
                                ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                ["Text"] = "e4 c2 nor"
                            },
                        ["NestedRequiredReference"] =
                            new BsonDocument
                            {
                                ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                ["Text"] = "e4 c2 nrr"
                            }
                    }
                },
            ["OptionalReference"] = new BsonDocument
            {
                ["Number"] = BsonNull.Value,
                ["Text"] = "e4 or",
                ["NestedCollection"] =
                    new BsonArray
                    {
                        new BsonDocument
                        {
                            ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                            ["Text"] = "e4 or c1"
                        },
                        new BsonDocument
                        {
                            ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                            ["Text"] = "e4 or c2"
                        }
                    },
                ["NestedOptionalReference"] =
                    new BsonDocument
                    {
                        ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                        ["Text"] = "e4 or nor"
                    },
                ["NestedRequiredReference"] =
                    new BsonDocument
                    {
                        ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                        ["Text"] = "e4 or nrr"
                    }
            },
            ["RequiredReference"] = new BsonDocument
            {
                ["Number"] = BsonNull.Value,
                ["Text"] = "e4 rr",
                ["NestedCollection"] =
                    new BsonArray
                    {
                        new BsonDocument
                        {
                            ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                            ["Text"] = "e4 rr c1"
                        },
                        new BsonDocument
                        {
                            ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                            ["Text"] = "e4 rr c2"
                        }
                    },
                ["NestedOptionalReference"] =
                    new BsonDocument
                    {
                        ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                        ["Text"] = "e4 rr nor"
                    },
                ["NestedRequiredReference"] = new BsonDocument
                {
                    ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    ["Text"] = "e4 rr nrr"
                }
            }
        };

        var missingRequiredNav = new BsonDocument
        {
            ["_id"] = 5,
            ["Name"] = "e5",
            ["Collection"] =
                new BsonArray
                {
                    new BsonDocument
                    {
                        ["Number"] = 7.0,
                        ["Text"] = "e5 c1",
                        ["NestedCollection"] =
                            new BsonArray
                            {
                                new BsonDocument
                                {
                                    ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                    ["Text"] = "e5 c1 c1"
                                },
                                new BsonDocument
                                {
                                    ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                    ["Text"] = "e5 c1 c2"
                                }
                            },
                        ["NestedOptionalReference"] =
                            new BsonDocument
                            {
                                ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                ["Text"] = "e5 c1 nor"
                            }
                    },
                    new BsonDocument
                    {
                        ["Number"] = 7.0,
                        ["Text"] = "e5 c2",
                        ["NestedCollection"] =
                            new BsonArray
                            {
                                new BsonDocument
                                {
                                    ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                    ["Text"] = "e5 c2 c1"
                                },
                                new BsonDocument
                                {
                                    ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                    ["Text"] = "e5 c2 c2"
                                }
                            },
                        ["NestedOptionalReference"] =
                            new BsonDocument
                            {
                                ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                ["Text"] = "e5 c2 nor"
                            }
                    }
                },
            ["OptionalReference"] = new BsonDocument
            {
                ["Number"] = 7.0,
                ["Text"] = "e5 or",
                ["NestedCollection"] =
                    new BsonArray
                    {
                        new BsonDocument
                        {
                            ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                            ["Text"] = "e5 or c1"
                        },
                        new BsonDocument
                        {
                            ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                            ["Text"] = "e5 or c2"
                        }
                    },
                ["NestedOptionalReference"] =
                    new BsonDocument
                    {
                        ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                        ["Text"] = "e5 or nor"
                    }
            }
        };

        var nullRequiredNav = new BsonDocument
        {
            ["_id"] = 6,
            ["Name"] = "e6",
            ["Collection"] =
                new BsonArray
                {
                    new BsonDocument
                    {
                        ["Number"] = 7.0,
                        ["Text"] = "e6 c1",
                        ["NestedCollection"] =
                            new BsonArray
                            {
                                new BsonDocument
                                {
                                    ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                    ["Text"] = "e6 c1 c1"
                                },
                                new BsonDocument
                                {
                                    ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                    ["Text"] = "e6 c1 c2"
                                }
                            },
                        ["NestedOptionalReference"] =
                            new BsonDocument
                            {
                                ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                ["Text"] = "e6 c1 nor"
                            },
                        ["NestedRequiredReference"] = BsonNull.Value
                    },
                    new BsonDocument
                    {
                        ["Number"] = 7.0,
                        ["Text"] = "e6 c2",
                        ["NestedCollection"] =
                            new BsonArray
                            {
                                new BsonDocument
                                {
                                    ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                    ["Text"] = "e6 c2 c1"
                                },
                                new BsonDocument
                                {
                                    ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                    ["Text"] = "e6 c2 c2"
                                }
                            },
                        ["NestedOptionalReference"] =
                            new BsonDocument
                            {
                                ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                ["Text"] = "e6 c2 nor"
                            },
                        ["NestedRequiredReference"] = BsonNull.Value
                    }
                },
            ["OptionalReference"] = new BsonDocument
            {
                ["Number"] = 7.0,
                ["Text"] = "e6 or",
                ["NestedCollection"] =
                    new BsonArray
                    {
                        new BsonDocument
                        {
                            ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                            ["Text"] = "e6 or c1"
                        },
                        new BsonDocument
                        {
                            ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                            ["Text"] = "e6 or c2"
                        }
                    },
                ["NestedOptionalReference"] =
                    new BsonDocument
                    {
                        ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                        ["Text"] = "e6 or nor"
                    },
                ["NestedRequiredReference"] = BsonNull.Value
            },
            ["RequiredReference"] = new BsonDocument
            {
                ["Number"] = 7.0,
                ["Text"] = "e6 rr",
                ["NestedCollection"] =
                    new BsonArray
                    {
                        new BsonDocument
                        {
                            ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                            ["Text"] = "e6 rr c1"
                        },
                        new BsonDocument
                        {
                            ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                            ["Text"] = "e6 rr c2"
                        }
                    },
                ["NestedOptionalReference"] =
                    new BsonDocument
                    {
                        ["DoB"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                        ["Text"] = "e6 rr nor"
                    },
                ["NestedRequiredReference"] = BsonNull.Value
            }
        };

        await collection.InsertManyAsync([
            missingTopLevel,
            missingNested,
            nullTopLevel,
            missingRequiredNav,
            nullRequiredNav]).ConfigureAwait(false);
    }

    private TestMqlLoggerFactory TestMqlLoggerFactory
        => (TestMqlLoggerFactory)ListLoggerFactory;

    private void AssertMql(params string[] expected)
        => TestMqlLoggerFactory.AssertBaseline(expected);

    protected override ITestStoreFactory TestStoreFactory
        => MongoTestStoreFactory.Instance;
}

#endif
