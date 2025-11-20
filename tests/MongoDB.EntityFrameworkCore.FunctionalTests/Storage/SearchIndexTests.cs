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
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Metadata.Search;
using MongoDB.EntityFrameworkCore.Metadata.Search.Builders;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Storage;

[XUnitCollection("StorageTests")]
public class SearchIndexTests(AtlasTemporaryDatabaseFixture database)
    : IClassFixture<AtlasTemporaryDatabaseFixture>
{
    [AtlasTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Create_search_index_in_EnsureCreated(bool async, bool useOptions)
    {
        var collection = database.CreateCollection<Cat>(values: [async, useOptions]);
        await using var db = SingleEntityDbContext.Create(
            collection,
            b => b.Entity<Cat>().HasSearchIndex().IsDynamic());

        if (useOptions)
        {
            var options = new MongoDatabaseCreationOptions();
            _ = async ? await db.Database.EnsureCreatedAsync(options) : db.Database.EnsureCreated(options);
        }
        else
        {
            _ = async ? await db.Database.EnsureCreatedAsync() : db.Database.EnsureCreated();
        }

        ValidateIndex(database, collection.CollectionNamespace);
    }

    [AtlasTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_and_wait_for_search_index(bool async)
    {
        var collection = database.CreateCollection<Cat>(values: async);
        await using var db = SingleEntityDbContext.Create(
            collection,
            b => b.Entity<Cat>().HasSearchIndex().IsDynamic());

        var options = new MongoDatabaseCreationOptions(CreateMissingSearchIndexes: false, WaitForSearchIndexes: false);

        if (async)
        {
            await db.Database.EnsureCreatedAsync(options);

            Assert.Equal(0, GetSearchIndexCount(collection));

            await db.Database.CreateMissingSearchIndexesAsync();
            await db.Database.WaitForSearchIndexesAsync();
        }
        else
        {
            db.Database.EnsureCreated(options);

            Assert.Equal(0, GetSearchIndexCount(collection));

            db.Database.CreateMissingSearchIndexes();
            db.Database.WaitForSearchIndexes();
        }

        ValidateIndex(database, collection.CollectionNamespace);
    }

    [AtlasFact]
    public async Task Create_default_dynamic_search_index()
    {
        var collection = database.CreateCollection<Cat>();
        await using var db = SingleEntityDbContext.Create(
            collection,
            b => b.Entity<Cat>().HasSearchIndex().IsDynamic());

        await db.Database.EnsureCreatedAsync();

        ValidateIndex(database, collection.CollectionNamespace, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : true,
                "fields" : { }
              }
            }
            """);
    }

    [AtlasFact]
    public async Task Create_dynamic_search_index_with_name()
    {
        var collection = database.CreateCollection<Cat>();
        await using var db = SingleEntityDbContext.Create(
            collection,
            b => b.Entity<Cat>().HasSearchIndex("MySearchIndex").IsDynamic());

        await db.Database.EnsureCreatedAsync();

        ValidateIndex(database, collection.CollectionNamespace, "MySearchIndex", expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : true,
                "fields" : { }
              }
            }
            """);
    }

    [AtlasTheory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Create_dynamic_search_index_with_embedded_documents_type_set(bool nestedBuilders, bool strings, bool generic)
    {
        var collection = database.CreateCollection<Cat>(values: [nestedBuilders, strings]);

        Action<ModelBuilder> modelBuilderAction = strings
            ? nestedBuilders
                ? generic
                    ? b => b.Entity<Cat>().HasSearchIndex((SearchIndexBuilder<Cat> b) =>
                    {
                        b.IsDynamic()
                            .IndexAsEmbedded("Coat", (NestedSearchIndexBuilder<Cat> b)
                                => b.IsDynamic().IndexAsEmbeddedArray("Colors").IsDynamic())
                            .IndexAsEmbeddedArray("Friends").IsDynamic();
                    })
                    : b => b.Entity(typeof(Cat)).HasSearchIndex(b =>
                    {
                        b.IsDynamic()
                            .IndexAsEmbedded("Coat", b => b.IsDynamic().IndexAsEmbeddedArray("Colors").IsDynamic())
                            .IndexAsEmbeddedArray("Friends").IsDynamic();
                    })
                : generic
                    ? b => b.Entity<Cat>(b =>
                    {
                        b.HasSearchIndex().IsDynamic().IndexAsEmbedded("Coat").IsDynamic();
                        b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbeddedArray("Colors").IsDynamic();
                        b.HasSearchIndex().IndexAsEmbeddedArray("Friends").IsDynamic();
                    })
                    : b => b.Entity(typeof(Cat), b =>
                    {
                        b.HasSearchIndex().IsDynamic().IndexAsEmbedded("Coat").IsDynamic();
                        b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbeddedArray("Colors").IsDynamic();
                        b.HasSearchIndex().IndexAsEmbeddedArray("Friends").IsDynamic();
                    })
            : nestedBuilders
                ? b => b.Entity<Cat>().HasSearchIndex(b =>
                {
                    b.IsDynamic()
                        .IndexAsEmbedded(e => e.Coat, b => b.IsDynamic().IndexAsEmbeddedArray(e => e.Colors).IsDynamic())
                        .IndexAsEmbeddedArray(e => e.Friends).IsDynamic();
                })
                : b => b.Entity<Cat>(b =>
                {
                    b.HasSearchIndex().IsDynamic().IndexAsEmbedded(e => e.Coat).IsDynamic();
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbeddedArray(e => e.Colors).IsDynamic();
                    b.HasSearchIndex().IndexAsEmbeddedArray(e => e.Friends).IsDynamic();
                });

        await using var db = SingleEntityDbContext.Create(collection, modelBuilderAction);
        await db.Database.EnsureCreatedAsync();

        ValidateIndex(database, collection.CollectionNamespace, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : true,
                "fields" : {
                  "bCoat" : {
                    "type" : "document",
                    "dynamic" : true,
                    "fields" : {
                      "bColors" : {
                        "type" : "embeddedDocuments",
                        "dynamic" : true,
                        "fields" : { }
                      }
                    }
                  },
                  "bFriends" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : true,
                    "fields" : { }
                  }
                }
              }
            }
            """);
    }

    [AtlasTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Create_static_autocomplete_search_index(bool nestedBuilders, bool strings)
    {
        var collection = database.CreateCollection<Cat>(values: [nestedBuilders, strings]);

        Action<ModelBuilder> modelBuilderAction = nestedBuilders
            ? strings
                ? b => b.Entity(typeof(Cat)).HasSearchIndex(b =>
                {
                    b.IndexAsAutoComplete("Name", b =>
                        {
                            b.UseAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace)
                                .WithMinGrams(4)
                                .WithMaxGrams(16)
                                .WithTokenization(SearchTokenization.NGram)
                                .FoldDiacritics(true)
                                .UseSimilarity(SearchSimilarityAlgorithm.Bm25);
                        })
                        .IndexAsAutoComplete("Comments", b => { })
                        .IndexAsEmbedded("Coat", b =>
                        {
                            b.IndexAsAutoComplete("Comments", b => { })
                                .IndexAsEmbeddedArray("Colors", b =>
                                {
                                    b.IndexAsAutoComplete("Color", b => { })
                                        .IndexAsAutoComplete("Comments", b =>
                                        {
                                            b.UseAnalyzer("lucene.simple")
                                                .WithMinGrams(5)
                                                .WithMaxGrams(20)
                                                .WithTokenization(SearchTokenization.EdgeGram)
                                                .FoldDiacritics(false)
                                                .UseSimilarity(SearchSimilarityAlgorithm.Boolean);
                                        });
                                })
                                .IndexAsEmbedded("Grooming", b =>
                                {
                                    b.IndexAsAutoComplete("GroomerName", b => { })
                                        .IndexAsAutoComplete("Comments", b => { });
                                });
                        })
                        .IndexAsEmbeddedArray("Friends", b => { b.IndexAsAutoComplete("Name", b => { }); });
                })
                : b => b.Entity<Cat>().HasSearchIndex(b =>
                {
                    b.IndexAsAutoComplete(e => e.Name, b =>
                        {
                            b.UseAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace)
                                .WithMinGrams(4)
                                .WithMaxGrams(16)
                                .WithTokenization(SearchTokenization.NGram)
                                .FoldDiacritics(true)
                                .UseSimilarity(SearchSimilarityAlgorithm.Bm25);
                        })
                        .IndexAsAutoComplete(e => e.Comments, b => { })
                        .IndexAsEmbedded(e => e.Coat, b =>
                        {
                            b.IndexAsAutoComplete(e => e.Comments, b => { })
                                .IndexAsEmbeddedArray(e => e.Colors, b =>
                                {
                                    b.IndexAsAutoComplete(e => e.Color, b => { })
                                        .IndexAsAutoComplete(e => e.Comments, b =>
                                        {
                                            b.UseAnalyzer("lucene.simple")
                                                .WithMinGrams(5)
                                                .WithMaxGrams(20)
                                                .WithTokenization(SearchTokenization.EdgeGram)
                                                .FoldDiacritics(false)
                                                .UseSimilarity(SearchSimilarityAlgorithm.Boolean);
                                        });
                                })
                                .IndexAsEmbedded(e => e.Grooming, b =>
                                {
                                    b.IndexAsAutoComplete(e => e.GroomerName, b => { })
                                        .IndexAsAutoComplete(e => e.Comments, b => { });
                                });
                        });
                    b.IndexAsEmbeddedArray(e => e.Friends, b => { b.IndexAsAutoComplete(e => e.Name, b => { }); });
                })
            : strings
                ? b => b.Entity(typeof(Cat), b =>
                {
                    b.HasSearchIndex()
                        .IndexAsAutoComplete("Name")
                        .UseAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace)
                        .WithMinGrams(4)
                        .WithMaxGrams(16)
                        .WithTokenization(SearchTokenization.NGram)
                        .FoldDiacritics(true)
                        .UseSimilarity(SearchSimilarityAlgorithm.Bm25);

                    b.HasSearchIndex().IndexAsAutoComplete("Comments");
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsAutoComplete("Comments");
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbeddedArray("Colors").IndexAsAutoComplete("Color");

                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbeddedArray("Colors")
                        .IndexAsAutoComplete("Comments")
                        .UseAnalyzer("lucene.simple")
                        .WithMinGrams(5)
                        .WithMaxGrams(20)
                        .WithTokenization(SearchTokenization.EdgeGram)
                        .UseSimilarity(SearchSimilarityAlgorithm.Boolean)
                        .FoldDiacritics(false);

                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbedded("Grooming").IndexAsAutoComplete("GroomerName");
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbedded("Grooming").IndexAsAutoComplete("Comments");
                    b.HasSearchIndex().IndexAsEmbeddedArray("Friends").IndexAsAutoComplete("Name");
                })
                : b => b.Entity<Cat>(b =>
                {
                    b.HasSearchIndex()
                        .IndexAsAutoComplete(e => e.Name)
                        .UseSimilarity(SearchSimilarityAlgorithm.Bm25)
                        .UseAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace)
                        .WithMinGrams(4)
                        .WithMaxGrams(16)
                        .WithTokenization(SearchTokenization.NGram)
                        .FoldDiacritics(true);

                    b.HasSearchIndex().IndexAsAutoComplete(e => e.Comments);
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsAutoComplete(e => e.Comments);
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbeddedArray(e => e.Colors)
                        .IndexAsAutoComplete(e => e.Color);

                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbeddedArray(e => e.Colors)
                        .IndexAsAutoComplete(e => e.Comments)
                        .UseAnalyzer("lucene.simple")
                        .WithMinGrams(5)
                        .WithMaxGrams(20)
                        .WithTokenization(SearchTokenization.EdgeGram)
                        .FoldDiacritics(false)
                        .UseSimilarity(SearchSimilarityAlgorithm.Boolean);

                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbedded(e => e.Grooming)
                        .IndexAsAutoComplete(e => e.GroomerName);
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbedded(e => e.Grooming)
                        .IndexAsAutoComplete(e => e.Comments);
                    b.HasSearchIndex().IndexAsEmbeddedArray(e => e.Friends).IndexAsAutoComplete(e => e.Name);
                });

        await using var db = SingleEntityDbContext.Create(collection, modelBuilderAction);
        await db.Database.EnsureCreatedAsync();

        ValidateIndex(database, collection.CollectionNamespace, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "bComments" : {
                    "type" : "autocomplete",
                    "minGrams" : 2,
                    "maxGrams" : 15,
                    "foldDiacritics" : true,
                    "tokenization" : "edgeGram"
                  },
                  "bName" : {
                    "type" : "autocomplete",
                    "minGrams" : 4,
                    "maxGrams" : 16,
                    "foldDiacritics" : true,
                    "tokenization" : "nGram",
                    "analyzer" : "lucene.whitespace",
                    "similarity" : {
                      "type" : "bm25"
                    }
                  },
                  "bCoat" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "bColors" : {
                        "type" : "embeddedDocuments",
                        "dynamic" : false,
                        "fields" : {
                          "bbbComments" : {
                            "type" : "autocomplete",
                            "minGrams" : 5,
                            "maxGrams" : 20,
                            "foldDiacritics" : false,
                            "tokenization" : "edgeGram",
                            "analyzer" : "lucene.simple",
                            "similarity" : {
                              "type" : "boolean"
                            }
                          },
                          "bColor" : {
                            "type" : "autocomplete",
                            "minGrams" : 2,
                            "maxGrams" : 15,
                            "foldDiacritics" : true,
                            "tokenization" : "edgeGram"
                          }
                        }
                      },
                      "bbComments" : {
                        "type" : "autocomplete",
                        "minGrams" : 2,
                        "maxGrams" : 15,
                        "foldDiacritics" : true,
                        "tokenization" : "edgeGram"
                      },
                      "bGrooming" : {
                        "type" : "document",
                        "dynamic" : false,
                        "fields" : {
                          "bGroomerName" : {
                            "type" : "autocomplete",
                            "minGrams" : 2,
                            "maxGrams" : 15,
                            "foldDiacritics" : true,
                            "tokenization" : "edgeGram"
                          },
                          "bbbbComments" : {
                            "type" : "autocomplete",
                            "minGrams" : 2,
                            "maxGrams" : 15,
                            "foldDiacritics" : true,
                            "tokenization" : "edgeGram"
                          }
                        }
                      }
                    }
                  },
                  "bFriends" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : false,
                    "fields" : {
                      "bName" : {
                        "type" : "autocomplete",
                        "minGrams" : 2,
                        "maxGrams" : 15,
                        "foldDiacritics" : true,
                        "tokenization" : "edgeGram"
                      }
                    }
                  }
                }
              }
            }
            """);
    }

    [AtlasTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_dynamic_autocomplete_search_index(bool nestedBuilders)
    {
        var collection = database.CreateCollection<Cat>(values: [nestedBuilders]);

        Action<ModelBuilder> modelBuilderAction = nestedBuilders
            ? b => b.Entity<Cat>().HasSearchIndex(b =>
            {
                b.IsDynamicWithTypeSet("TypeSet1")
                    .IndexAsEmbedded(e => e.Coat, b =>
                    {
                        b.IsDynamicWithTypeSet("TypeSet2")
                            .IndexAsEmbeddedArray(e => e.Colors, b => { b.IsDynamicWithTypeSet("TypeSet3"); })
                            .IndexAsEmbedded(e => e.Grooming, b => { b.IsDynamicWithTypeSet("TypeSet4"); });
                    })
                    .IndexAsEmbeddedArray(e => e.Friends, b => { b.IsDynamicWithTypeSet("TypeSet5"); })
                    .AddTypeSet("TypeSet1", b =>
                    {
                        b.IndexAsAutoComplete(b =>
                        {
                            b.UseAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace)
                                .WithMinGrams(4)
                                .WithMaxGrams(16)
                                .WithTokenization(SearchTokenization.NGram)
                                .FoldDiacritics(true)
                                .UseSimilarity(SearchSimilarityAlgorithm.Bm25);
                        });
                    })
                    .AddTypeSet("TypeSet2", b => { b.IndexAsAutoComplete(b => { }); })
                    .AddTypeSet("TypeSet3", b =>
                    {
                        b.IndexAsAutoComplete(b =>
                        {
                            b.UseAnalyzer("lucene.simple")
                                .WithMinGrams(5)
                                .WithMaxGrams(20)
                                .WithTokenization(SearchTokenization.EdgeGram)
                                .FoldDiacritics(false)
                                .UseSimilarity(SearchSimilarityAlgorithm.Boolean);
                        });
                    })
                    .AddTypeSet("TypeSet4", b => { b.IndexAsAutoComplete(b => { }); })
                    .AddTypeSet("TypeSet5", b => { b.IndexAsAutoComplete(b => { }); });
            })
            : b => b.Entity<Cat>(b =>
            {
                b.HasSearchIndex().IsDynamicWithTypeSet("TypeSet1");
                b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IsDynamicWithTypeSet("TypeSet2");
                b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbeddedArray(e => e.Colors)
                    .IsDynamicWithTypeSet("TypeSet3");
                b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbedded(e => e.Grooming).IsDynamicWithTypeSet("TypeSet4");
                b.HasSearchIndex().IndexAsEmbeddedArray(e => e.Friends).IsDynamicWithTypeSet("TypeSet5");

                b.HasSearchIndex().AddTypeSet("TypeSet1")
                    .IndexAsAutoComplete()
                    .UseAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace)
                    .WithMinGrams(4)
                    .WithMaxGrams(16)
                    .WithTokenization(SearchTokenization.NGram)
                    .FoldDiacritics(true)
                    .UseSimilarity(SearchSimilarityAlgorithm.Bm25);

                b.HasSearchIndex().AddTypeSet("TypeSet2")
                    .IndexAsAutoComplete();

                b.HasSearchIndex().AddTypeSet("TypeSet3")
                    .IndexAsAutoComplete()
                    .UseAnalyzer("lucene.simple")
                    .WithMinGrams(5)
                    .WithMaxGrams(20)
                    .WithTokenization(SearchTokenization.EdgeGram)
                    .FoldDiacritics(false)
                    .UseSimilarity(SearchSimilarityAlgorithm.Boolean);

                b.HasSearchIndex().AddTypeSet("TypeSet4")
                    .IndexAsAutoComplete();
                b.HasSearchIndex().AddTypeSet("TypeSet5")
                    .IndexAsAutoComplete();
            });

        await using var db = SingleEntityDbContext.Create(collection, modelBuilderAction);
        await db.Database.EnsureCreatedAsync();

        ValidateIndex(database, collection.CollectionNamespace, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : {
                  "typeSet" : "TypeSet1"
                },
                "fields" : {
                  "bCoat" : {
                    "type" : "document",
                    "dynamic" : {
                      "typeSet" : "TypeSet2"
                    },
                    "fields" : {
                      "bColors" : {
                        "type" : "embeddedDocuments",
                        "dynamic" : {
                          "typeSet" : "TypeSet3"
                        },
                        "fields" : { }
                      },
                      "bGrooming" : {
                        "type" : "document",
                        "dynamic" : {
                          "typeSet" : "TypeSet4"
                        },
                        "fields" : { }
                      }
                    }
                  },
                  "bFriends" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : {
                      "typeSet" : "TypeSet5"
                    },
                    "fields" : { }
                  }
                }
              },
              "typeSets" : [{
                  "name" : "TypeSet1",
                  "types" : [{
                      "type" : "autocomplete",
                      "minGrams" : 4,
                      "maxGrams" : 16,
                      "foldDiacritics" : true,
                      "tokenization" : "nGram",
                      "analyzer" : "lucene.whitespace",
                      "similarity" : {
                        "type" : "bm25"
                      }
                    }]
                }, {
                  "name" : "TypeSet2",
                  "types" : [{
                      "type" : "autocomplete",
                      "minGrams" : 2,
                      "maxGrams" : 15,
                      "foldDiacritics" : true,
                      "tokenization" : "edgeGram"
                    }]
                }, {
                  "name" : "TypeSet3",
                  "types" : [{
                      "type" : "autocomplete",
                      "minGrams" : 5,
                      "maxGrams" : 20,
                      "foldDiacritics" : false,
                      "tokenization" : "edgeGram",
                      "analyzer" : "lucene.simple",
                      "similarity" : {
                        "type" : "boolean"
                      }
                    }]
                }, {
                  "name" : "TypeSet4",
                  "types" : [{
                      "type" : "autocomplete",
                      "minGrams" : 2,
                      "maxGrams" : 15,
                      "foldDiacritics" : true,
                      "tokenization" : "edgeGram"
                    }]
                }, {
                  "name" : "TypeSet5",
                  "types" : [{
                      "type" : "autocomplete",
                      "minGrams" : 2,
                      "maxGrams" : 15,
                      "foldDiacritics" : true,
                      "tokenization" : "edgeGram"
                    }]
                }]
            }
            """);
    }

    [AtlasTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Create_static_boolean_search_index(bool nestedBuilders, bool strings)
    {
        var collection = database.CreateCollection<Cat>(values: [nestedBuilders, strings]);

        Action<ModelBuilder> modelBuilderAction = nestedBuilders
            ? strings
                ? b => b.Entity(typeof(Cat)).HasSearchIndex(b =>
                {
                    b.IndexAsBoolean("Current", b => { })
                        .IndexAsEmbedded("Coat", b =>
                        {
                            b.IndexAsBoolean("Short", b => { })
                                .IndexAsEmbeddedArray("Colors", b => { })
                                .IndexAsEmbedded("Grooming", b => { b.IndexAsBoolean("Tangles", b => { }); });
                        })
                        .IndexAsEmbeddedArray("Friends", b => { b.IndexAsBoolean("Feline"); });
                })
                : b => b.Entity<Cat>().HasSearchIndex(b =>
                {
                    b.IndexAsBoolean(e => e.Current, b => { })
                        .IndexAsEmbedded(e => e.Coat, b =>
                        {
                            b.IndexAsBoolean(e => e.Short, b => { })
                                .IndexAsEmbeddedArray(e => e.Colors, b => { })
                                .IndexAsEmbedded(e => e.Grooming, b => { b.IndexAsBoolean(e => e.Tangles, b => { }); });
                        });
                    b.IndexAsEmbeddedArray(e => e.Friends, b => { b.IndexAsBoolean(e => e.Feline); });
                })
            : strings
                ? b => b.Entity(typeof(Cat), b =>
                {
                    b.HasSearchIndex().IndexAsBoolean("Current");
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsBoolean("Short");
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbeddedArray("Colors");
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbedded("Grooming").IndexAsBoolean("Tangles");
                    b.HasSearchIndex().IndexAsEmbeddedArray("Friends").IndexAsBoolean("Feline");
                })
                : b => b.Entity<Cat>(b =>
                {
                    b.HasSearchIndex().IndexAsBoolean(e => e.Current);
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsBoolean(e => e.Short);
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbeddedArray(e => e.Colors);
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbedded(e => e.Grooming).IndexAsBoolean(e => e.Tangles);
                    b.HasSearchIndex().IndexAsEmbeddedArray(e => e.Friends).IndexAsBoolean(e => e.Feline);
                });

        await using var db = SingleEntityDbContext.Create(collection, modelBuilderAction);
        await db.Database.EnsureCreatedAsync();

        ValidateIndex(database, collection.CollectionNamespace, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "bCurrent" : {
                    "type" : "boolean"
                  },
                  "bCoat" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "bColors" : {
                        "type" : "embeddedDocuments",
                        "dynamic" : false,
                        "fields" : { }
                      },
                      "bShort" : {
                        "type" : "boolean"
                      },
                      "bGrooming" : {
                        "type" : "document",
                        "dynamic" : false,
                        "fields" : {
                          "bTangles" : {
                            "type" : "boolean"
                          }
                        }
                      }
                    }
                  },
                  "bFriends" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : false,
                    "fields" : {
                      "bFeline" : {
                        "type" : "boolean"
                      }
                    }
                  }
                }
              }
            }
            """);
    }

    [AtlasTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_dynamic_boolean_search_index(bool nestedBuilders)
    {
        var collection = database.CreateCollection<Cat>(values: [nestedBuilders]);

        Action<ModelBuilder> modelBuilderAction = nestedBuilders
            ? b => b.Entity<Cat>().HasSearchIndex(b =>
            {
                b.IsDynamicWithTypeSet("TypeSet1")
                    .IndexAsEmbedded(e => e.Coat, b =>
                    {
                        b.IsDynamicWithTypeSet("TypeSet2")
                            .IndexAsEmbeddedArray(e => e.Colors, b => { b.IsDynamicWithTypeSet("TypeSet3"); })
                            .IndexAsEmbedded(e => e.Grooming, b => { b.IsDynamicWithTypeSet("TypeSet4"); });
                    })
                    .IndexAsEmbeddedArray(e => e.Friends, b => { b.IsDynamicWithTypeSet("TypeSet5"); })
                    .AddTypeSet("TypeSet1", b => { b.IndexAsBoolean(b => { }); })
                    .AddTypeSet("TypeSet2", b => { b.IndexAsBoolean(b => { }); })
                    .AddTypeSet("TypeSet3", b => { b.IndexAsBoolean(b => { }); })
                    .AddTypeSet("TypeSet4", b => { b.IndexAsBoolean(b => { }); })
                    .AddTypeSet("TypeSet5", b => { b.IndexAsBoolean(b => { }); });
            })
            : b => b.Entity<Cat>(b =>
            {
                b.HasSearchIndex().IsDynamicWithTypeSet("TypeSet1");
                b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IsDynamicWithTypeSet("TypeSet2");
                b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbeddedArray(e => e.Colors)
                    .IsDynamicWithTypeSet("TypeSet3");
                b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbedded(e => e.Grooming).IsDynamicWithTypeSet("TypeSet4");
                b.HasSearchIndex().IndexAsEmbeddedArray(e => e.Friends).IsDynamicWithTypeSet("TypeSet5");

                b.HasSearchIndex().AddTypeSet("TypeSet1").IndexAsBoolean();
                b.HasSearchIndex().AddTypeSet("TypeSet2").IndexAsBoolean();
                b.HasSearchIndex().AddTypeSet("TypeSet3").IndexAsBoolean();
                b.HasSearchIndex().AddTypeSet("TypeSet4").IndexAsBoolean();
                b.HasSearchIndex().AddTypeSet("TypeSet5").IndexAsBoolean();
            });

        await using var db = SingleEntityDbContext.Create(collection, modelBuilderAction);
        await db.Database.EnsureCreatedAsync();

        ValidateIndex(database, collection.CollectionNamespace, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : {
                  "typeSet" : "TypeSet1"
                },
                "fields" : {
                  "bCoat" : {
                    "type" : "document",
                    "dynamic" : {
                      "typeSet" : "TypeSet2"
                    },
                    "fields" : {
                      "bColors" : {
                        "type" : "embeddedDocuments",
                        "dynamic" : {
                          "typeSet" : "TypeSet3"
                        },
                        "fields" : { }
                      },
                      "bGrooming" : {
                        "type" : "document",
                        "dynamic" : {
                          "typeSet" : "TypeSet4"
                        },
                        "fields" : { }
                      }
                    }
                  },
                  "bFriends" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : {
                      "typeSet" : "TypeSet5"
                    },
                    "fields" : { }
                  }
                }
              },
              "typeSets" : [{
                  "name" : "TypeSet1",
                  "types" : [{
                      "type" : "boolean"
                    }]
                }, {
                  "name" : "TypeSet2",
                  "types" : [{
                      "type" : "boolean"
                    }]
                }, {
                  "name" : "TypeSet3",
                  "types" : [{
                      "type" : "boolean"
                    }]
                }, {
                  "name" : "TypeSet4",
                  "types" : [{
                      "type" : "boolean"
                    }]
                }, {
                  "name" : "TypeSet5",
                  "types" : [{
                      "type" : "boolean"
                    }]
                }]
            }
            """);
    }

    [AtlasTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Create_static_date_search_index(bool nestedBuilders, bool strings)
    {
        var collection = database.CreateCollection<Cat>(values: [nestedBuilders, strings]);

        Action<ModelBuilder> modelBuilderAction = nestedBuilders
            ? strings
                ? b => b.Entity(typeof(Cat)).HasSearchIndex(b =>
                {
                    b.IndexAsEmbedded("Coat", b =>
                        {
                            b.IndexAsEmbeddedArray("Colors", b => { })
                                .IndexAsEmbedded("Grooming", b => { b.IndexAsDate("Date", b => { }); });
                        })
                        .IndexAsEmbeddedArray("Friends", b => { b.IndexAsDate("Birthday", b => { }); });
                })
                : b => b.Entity<Cat>().HasSearchIndex(b =>
                {
                    b.IndexAsEmbedded(e => e.Coat, b =>
                    {
                        b.IndexAsEmbeddedArray(e => e.Colors, b => { })
                            .IndexAsEmbedded(e => e.Grooming, b => { b.IndexAsDate(e => e.Date, b => { }); });
                    });
                    b.IndexAsEmbeddedArray(e => e.Friends, b => { b.IndexAsDate(e => e.Birthday, b => { }); });
                })
            : strings
                ? b => b.Entity(typeof(Cat), b =>
                {
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbeddedArray("Colors");
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbedded("Grooming").IndexAsDate("Date");
                    b.HasSearchIndex().IndexAsEmbeddedArray("Friends").IndexAsDate("Birthday");
                })
                : b => b.Entity<Cat>(b =>
                {
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbeddedArray(e => e.Colors);
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbedded(e => e.Grooming).IndexAsDate(e => e.Date);
                    b.HasSearchIndex().IndexAsEmbeddedArray(e => e.Friends).IndexAsDate(e => e.Birthday);
                });

        await using var db = SingleEntityDbContext.Create(collection, modelBuilderAction);
        await db.Database.EnsureCreatedAsync();

        ValidateIndex(database, collection.CollectionNamespace, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "bCoat" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "bColors" : {
                        "type" : "embeddedDocuments",
                        "dynamic" : false,
                        "fields" : { }
                      },
                      "bGrooming" : {
                        "type" : "document",
                        "dynamic" : false,
                        "fields" : {
                          "bDate" : {
                            "type" : "date"
                          }
                        }
                      }
                    }
                  },
                  "bFriends" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : false,
                    "fields" : {
                      "bBirthday" : {
                        "type" : "date"
                      }
                    }
                  }
                }
              }
            }
            """);
    }

    [AtlasTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_dynamic_date_search_index(bool nestedBuilders)
    {
        var collection = database.CreateCollection<Cat>(values: [nestedBuilders]);

        Action<ModelBuilder> modelBuilderAction = nestedBuilders
            ? b => b.Entity<Cat>().HasSearchIndex(b =>
            {
                b.IsDynamicWithTypeSet("TypeSet1")
                    .IndexAsEmbedded(e => e.Coat, b =>
                    {
                        b.IsDynamicWithTypeSet("TypeSet2")
                            .IndexAsEmbeddedArray(e => e.Colors, b => { b.IsDynamicWithTypeSet("TypeSet3"); })
                            .IndexAsEmbedded(e => e.Grooming, b => { b.IsDynamicWithTypeSet("TypeSet4"); });
                    })
                    .IndexAsEmbeddedArray(e => e.Friends, b => { b.IsDynamicWithTypeSet("TypeSet5"); })
                    .AddTypeSet("TypeSet1", b => { b.IndexAsDate(b => { }); })
                    .AddTypeSet("TypeSet2", b => { b.IndexAsDate(b => { }); })
                    .AddTypeSet("TypeSet3", b => { b.IndexAsDate(b => { }); })
                    .AddTypeSet("TypeSet4", b => { b.IndexAsDate(b => { }); })
                    .AddTypeSet("TypeSet5", b => { b.IndexAsDate(b => { }); });
            })
            : b => b.Entity<Cat>(b =>
            {
                b.HasSearchIndex().IsDynamicWithTypeSet("TypeSet1");
                b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IsDynamicWithTypeSet("TypeSet2");
                b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbeddedArray(e => e.Colors)
                    .IsDynamicWithTypeSet("TypeSet3");
                b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbedded(e => e.Grooming).IsDynamicWithTypeSet("TypeSet4");
                b.HasSearchIndex().IndexAsEmbeddedArray(e => e.Friends).IsDynamicWithTypeSet("TypeSet5");

                b.HasSearchIndex().AddTypeSet("TypeSet1").IndexAsDate();
                b.HasSearchIndex().AddTypeSet("TypeSet2").IndexAsDate();
                b.HasSearchIndex().AddTypeSet("TypeSet3").IndexAsDate();
                b.HasSearchIndex().AddTypeSet("TypeSet4").IndexAsDate();
                b.HasSearchIndex().AddTypeSet("TypeSet5").IndexAsDate();
            });

        await using var db = SingleEntityDbContext.Create(collection, modelBuilderAction);
        await db.Database.EnsureCreatedAsync();

        ValidateIndex(database, collection.CollectionNamespace, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : {
                  "typeSet" : "TypeSet1"
                },
                "fields" : {
                  "bCoat" : {
                    "type" : "document",
                    "dynamic" : {
                      "typeSet" : "TypeSet2"
                    },
                    "fields" : {
                      "bColors" : {
                        "type" : "embeddedDocuments",
                        "dynamic" : {
                          "typeSet" : "TypeSet3"
                        },
                        "fields" : { }
                      },
                      "bGrooming" : {
                        "type" : "document",
                        "dynamic" : {
                          "typeSet" : "TypeSet4"
                        },
                        "fields" : { }
                      }
                    }
                  },
                  "bFriends" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : {
                      "typeSet" : "TypeSet5"
                    },
                    "fields" : { }
                  }
                }
              },
              "typeSets" : [{
                  "name" : "TypeSet1",
                  "types" : [{
                      "type" : "date"
                    }]
                }, {
                  "name" : "TypeSet2",
                  "types" : [{
                      "type" : "date"
                    }]
                }, {
                  "name" : "TypeSet3",
                  "types" : [{
                      "type" : "date"
                    }]
                }, {
                  "name" : "TypeSet4",
                  "types" : [{
                      "type" : "date"
                    }]
                }, {
                  "name" : "TypeSet5",
                  "types" : [{
                      "type" : "date"
                    }]
                }]
            }
            """);
    }

    // Note that this creates geo indexes on non-geo properties, which will not be valid if the index is used.
    // Geo properties can be used when we support them.
    [AtlasTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Create_static_geo_search_index(bool nestedBuilders, bool strings)
    {
        var collection = database.CreateCollection<Cat>(values: [nestedBuilders, strings]);

        Action<ModelBuilder> modelBuilderAction = nestedBuilders
            ? strings
                ? b => b.Entity(typeof(Cat)).HasSearchIndex(b =>
                {
                    b.IndexAsGeo("Name", b => { b.IndexShapes(); })
                        .IndexAsEmbedded("Coat", b =>
                        {
                            b.IndexAsEmbeddedArray("Colors", b => { b.IndexAsGeo("Color", b => { }); })
                                .IndexAsEmbedded("Grooming", b => { b.IndexAsGeo("GroomerName", b => { b.IndexShapes(false); }); });
                        })
                        .IndexAsEmbeddedArray("Friends", b => { b.IndexAsGeo("Name", b => { }); });
                })
                : b => b.Entity<Cat>().HasSearchIndex(b =>
                {
                    b.IndexAsGeo(e => e.Name, b => { b.IndexShapes(); })
                        .IndexAsEmbedded(e => e.Coat, b =>
                        {
                            b.IndexAsEmbeddedArray(e => e.Colors, b => { b.IndexAsGeo(e => e.Color, b => { }); })
                                .IndexAsEmbedded(e => e.Grooming,
                                    b => { b.IndexAsGeo(e => e.GroomerName, b => { b.IndexShapes(false); }); });
                        });
                    b.IndexAsEmbeddedArray(e => e.Friends, b => { b.IndexAsGeo(e => e.Name, b => { }); });
                })
            : strings
                ? b => b.Entity(typeof(Cat), b =>
                {
                    b.HasSearchIndex().IndexAsGeo("Name").IndexShapes();
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbeddedArray("Colors").IndexAsGeo("Color");
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbedded("Grooming").IndexAsGeo("GroomerName")
                        .IndexShapes(false);
                    b.HasSearchIndex().IndexAsEmbeddedArray("Friends").IndexAsGeo("Name");
                })
                : b => b.Entity<Cat>(b =>
                {
                    b.HasSearchIndex().IndexAsGeo(e => e.Name).IndexShapes();
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbeddedArray(e => e.Colors).IndexAsGeo(e => e.Color);
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbedded(e => e.Grooming).IndexAsGeo(e => e.GroomerName)
                        .IndexShapes(false);
                    b.HasSearchIndex().IndexAsEmbeddedArray(e => e.Friends).IndexAsGeo(e => e.Name);
                });

        await using var db = SingleEntityDbContext.Create(collection, modelBuilderAction);
        await db.Database.EnsureCreatedAsync();

        ValidateIndex(database, collection.CollectionNamespace, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "bName" : {
                    "type" : "geo",
                    "indexShapes" : true
                  },
                  "bCoat" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "bColors" : {
                        "type" : "embeddedDocuments",
                        "dynamic" : false,
                        "fields" : {
                          "bColor" : {
                            "type" : "geo",
                            "indexShapes" : false
                          }
                        }
                      },
                      "bGrooming" : {
                        "type" : "document",
                        "dynamic" : false,
                        "fields" : {
                          "bGroomerName" : {
                            "type" : "geo",
                            "indexShapes" : false
                          }
                        }
                      }
                    }
                  },
                  "bFriends" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : false,
                    "fields" : {
                      "bName" : {
                        "type" : "geo",
                        "indexShapes" : false
                      }
                    }
                  }
                }
              }
            }
            """);
    }

    [AtlasTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_dynamic_geo_search_index(bool nestedBuilders)
    {
        var collection = database.CreateCollection<Cat>(values: [nestedBuilders]);

        Action<ModelBuilder> modelBuilderAction = nestedBuilders
            ? b => b.Entity<Cat>().HasSearchIndex(b =>
            {
                b.IsDynamicWithTypeSet("TypeSet1")
                    .IndexAsEmbedded(e => e.Coat, b =>
                    {
                        b.IsDynamicWithTypeSet("TypeSet2")
                            .IndexAsEmbeddedArray(e => e.Colors, b => { b.IsDynamicWithTypeSet("TypeSet3"); })
                            .IndexAsEmbedded(e => e.Grooming, b => { b.IsDynamicWithTypeSet("TypeSet4"); });
                    })
                    .IndexAsEmbeddedArray(e => e.Friends, b => { b.IsDynamicWithTypeSet("TypeSet5"); })
                    .AddTypeSet("TypeSet1", b => { b.IndexAsGeo(b => { }); })
                    .AddTypeSet("TypeSet2", b => { b.IndexAsGeo(b => { b.IndexShapes(); }); })
                    .AddTypeSet("TypeSet3", b => { b.IndexAsGeo(b => { b.IndexShapes(false); }); })
                    .AddTypeSet("TypeSet4", b => { b.IndexAsGeo(b => { b.IndexShapes(true); }); })
                    .AddTypeSet("TypeSet5", b => { b.IndexAsGeo(b => { }); });
            })
            : b => b.Entity<Cat>(b =>
            {
                b.HasSearchIndex().IsDynamicWithTypeSet("TypeSet1");
                b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IsDynamicWithTypeSet("TypeSet2");
                b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbeddedArray(e => e.Colors)
                    .IsDynamicWithTypeSet("TypeSet3");
                b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbedded(e => e.Grooming).IsDynamicWithTypeSet("TypeSet4");
                b.HasSearchIndex().IndexAsEmbeddedArray(e => e.Friends).IsDynamicWithTypeSet("TypeSet5");

                b.HasSearchIndex().AddTypeSet("TypeSet1").IndexAsGeo();
                b.HasSearchIndex().AddTypeSet("TypeSet2").IndexAsGeo().IndexShapes();
                b.HasSearchIndex().AddTypeSet("TypeSet3").IndexAsGeo().IndexShapes(false);
                b.HasSearchIndex().AddTypeSet("TypeSet4").IndexAsGeo().IndexShapes(true);
                b.HasSearchIndex().AddTypeSet("TypeSet5").IndexAsGeo();
            });

        await using var db = SingleEntityDbContext.Create(collection, modelBuilderAction);
        await db.Database.EnsureCreatedAsync();

        ValidateIndex(database, collection.CollectionNamespace, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : {
                  "typeSet" : "TypeSet1"
                },
                "fields" : {
                  "bCoat" : {
                    "type" : "document",
                    "dynamic" : {
                      "typeSet" : "TypeSet2"
                    },
                    "fields" : {
                      "bColors" : {
                        "type" : "embeddedDocuments",
                        "dynamic" : {
                          "typeSet" : "TypeSet3"
                        },
                        "fields" : { }
                      },
                      "bGrooming" : {
                        "type" : "document",
                        "dynamic" : {
                          "typeSet" : "TypeSet4"
                        },
                        "fields" : { }
                      }
                    }
                  },
                  "bFriends" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : {
                      "typeSet" : "TypeSet5"
                    },
                    "fields" : { }
                  }
                }
              },
              "typeSets" : [{
                  "name" : "TypeSet1",
                  "types" : [{
                      "type" : "geo",
                      "indexShapes" : false
                    }]
                }, {
                  "name" : "TypeSet2",
                  "types" : [{
                      "type" : "geo",
                      "indexShapes" : true
                    }]
                }, {
                  "name" : "TypeSet3",
                  "types" : [{
                      "type" : "geo",
                      "indexShapes" : false
                    }]
                }, {
                  "name" : "TypeSet4",
                  "types" : [{
                      "type" : "geo",
                      "indexShapes" : true
                    }]
                }, {
                  "name" : "TypeSet5",
                  "types" : [{
                      "type" : "geo",
                      "indexShapes" : false
                    }]
                }]
            }
            """);
    }

    [AtlasTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Create_static_number_search_index(bool nestedBuilders, bool strings)
    {
        var collection = database.CreateCollection<Cat>(values: [nestedBuilders, strings]);

        Action<ModelBuilder> modelBuilderAction = nestedBuilders
            ? strings
                ? b => b.Entity(typeof(Cat)).HasSearchIndex(b =>
                {
                    b.IndexAsNumber("Cost", b => { b.WithRepresentation(SearchNumberRepresentation.Double); })
                        .IndexAsNumber("Rating", b => { b.WithRepresentation(SearchNumberRepresentation.Double); })
                        .IndexAsNumber("Moles", b => { b.WithRepresentation(SearchNumberRepresentation.Int64); })
                        .IndexAsEmbedded("Coat", b =>
                        {
                            b.IndexAsEmbeddedArray("Colors",
                                    b =>
                                    {
                                        b.IndexAsNumber("Percent",
                                            b => { b.WithRepresentation(SearchNumberRepresentation.Double); });
                                    })
                                .IndexAsEmbedded("Grooming", b =>
                                {
                                    b.IndexAsNumber("Cost", b => { b.IndexIntegers().IndexDoubles(false); })
                                        .IndexAsNumber("Rating", b => { b.IndexIntegers(false).IndexDoubles(); });
                                });
                        })
                        .IndexAsEmbeddedArray("Friends", b =>
                        {
                            b.IndexAsNumber("Cost", b => { })
                                .IndexAsNumber("Rating", b => { })
                                .IndexAsNumber("Moles", b => { });
                        });
                })
                : b => b.Entity<Cat>().HasSearchIndex(b =>
                {
                    b.IndexAsNumber(e => e.Cost, b => { b.WithRepresentation(SearchNumberRepresentation.Double); })
                        .IndexAsNumber(e => e.Rating, b => { b.WithRepresentation(SearchNumberRepresentation.Double); })
                        .IndexAsNumber(e => e.Moles, b => { b.WithRepresentation(SearchNumberRepresentation.Int64); })
                        .IndexAsEmbedded(e => e.Coat, b =>
                        {
                            b.IndexAsEmbeddedArray(e => e.Colors, b =>
                                {
                                    b.IndexAsNumber(e => e.Percent,
                                        b => { b.WithRepresentation(SearchNumberRepresentation.Double); });
                                })
                                .IndexAsEmbedded(e => e.Grooming, b =>
                                {
                                    b.IndexAsNumber(e => e.Cost, b => { b.IndexIntegers().IndexDoubles(false); })
                                        .IndexAsNumber(e => e.Rating, b => { b.IndexIntegers(false).IndexDoubles(); });
                                });
                        });
                    b.IndexAsEmbeddedArray(e => e.Friends, b =>
                    {
                        b.IndexAsNumber(e => e.Cost, b => { })
                            .IndexAsNumber(e => e.Rating, b => { })
                            .IndexAsNumber(e => e.Moles, b => { });
                    });
                })
            : strings
                ? b => b.Entity(typeof(Cat), b =>
                {
                    b.HasSearchIndex().IndexAsNumber("Cost").WithRepresentation(SearchNumberRepresentation.Double);
                    b.HasSearchIndex().IndexAsNumber("Rating").WithRepresentation(SearchNumberRepresentation.Double);
                    b.HasSearchIndex().IndexAsNumber("Moles").WithRepresentation(SearchNumberRepresentation.Int64);
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbeddedArray("Colors").IndexAsNumber("Percent")
                        .WithRepresentation(SearchNumberRepresentation.Double);
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbedded("Grooming").IndexAsNumber("Cost").IndexIntegers()
                        .IndexDoubles(false);
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbedded("Grooming").IndexAsNumber("Rating")
                        .IndexIntegers(false).IndexDoubles();
                    b.HasSearchIndex().IndexAsEmbeddedArray("Friends").IndexAsNumber("Cost");
                    b.HasSearchIndex().IndexAsEmbeddedArray("Friends").IndexAsNumber("Rating");
                    b.HasSearchIndex().IndexAsEmbeddedArray("Friends").IndexAsNumber("Moles");
                })
                : b => b.Entity<Cat>(b =>
                {
                    b.HasSearchIndex().IndexAsNumber(e => e.Cost).WithRepresentation(SearchNumberRepresentation.Double);
                    b.HasSearchIndex().IndexAsNumber(e => e.Rating).WithRepresentation(SearchNumberRepresentation.Double);
                    b.HasSearchIndex().IndexAsNumber(e => e.Moles).WithRepresentation(SearchNumberRepresentation.Int64);
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbeddedArray(e => e.Colors)
                        .IndexAsNumber(e => e.Percent).WithRepresentation(SearchNumberRepresentation.Double);
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbedded(e => e.Grooming).IndexAsNumber(e => e.Cost)
                        .IndexIntegers().IndexDoubles(false);
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbedded(e => e.Grooming).IndexAsNumber(e => e.Rating)
                        .IndexIntegers(false).IndexDoubles();
                    b.HasSearchIndex().IndexAsEmbeddedArray(e => e.Friends).IndexAsNumber(e => e.Cost);
                    b.HasSearchIndex().IndexAsEmbeddedArray(e => e.Friends).IndexAsNumber(e => e.Rating);
                    b.HasSearchIndex().IndexAsEmbeddedArray(e => e.Friends).IndexAsNumber(e => e.Moles);
                });

        await using var db = SingleEntityDbContext.Create(collection, modelBuilderAction);
        await db.Database.EnsureCreatedAsync();

        ValidateIndex(database, collection.CollectionNamespace, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "bRating" : {
                    "type" : "number",
                    "representation" : "double",
                    "indexDoubles" : true,
                    "indexIntegers" : true
                  },
                  "bCoat" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "bColors" : {
                        "type" : "embeddedDocuments",
                        "dynamic" : false,
                        "fields" : {
                          "bPercent" : {
                            "type" : "number",
                            "representation" : "double",
                            "indexDoubles" : true,
                            "indexIntegers" : true
                          }
                        }
                      },
                      "bGrooming" : {
                        "type" : "document",
                        "dynamic" : false,
                        "fields" : {
                          "bRating" : {
                            "type" : "number",
                            "representation" : "double",
                            "indexDoubles" : true,
                            "indexIntegers" : false
                          },
                          "bCost" : {
                            "type" : "number",
                            "representation" : "double",
                            "indexDoubles" : false,
                            "indexIntegers" : true
                          }
                        }
                      }
                    }
                  },
                  "bFriends" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : false,
                    "fields" : {
                      "bRating" : {
                        "type" : "number",
                        "representation" : "double",
                        "indexDoubles" : true,
                        "indexIntegers" : true
                      },
                      "bCost" : {
                        "type" : "number",
                        "representation" : "double",
                        "indexDoubles" : true,
                        "indexIntegers" : true
                      },
                      "bMoles" : {
                        "type" : "number",
                        "representation" : "double",
                        "indexDoubles" : true,
                        "indexIntegers" : true
                      }
                    }
                  },
                  "bCost" : {
                    "type" : "number",
                    "representation" : "double",
                    "indexDoubles" : true,
                    "indexIntegers" : true
                  },
                  "bMoles" : {
                    "type" : "number",
                    "representation" : "int64",
                    "indexDoubles" : true,
                    "indexIntegers" : true
                  }
                }
              }
            }
            """);
    }

    [AtlasTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_dynamic_number_search_index(bool nestedBuilders)
    {
        var collection = database.CreateCollection<Cat>(values: [nestedBuilders]);

        Action<ModelBuilder> modelBuilderAction = nestedBuilders
            ? b => b.Entity<Cat>().HasSearchIndex(b =>
            {
                b.IsDynamicWithTypeSet("TypeSet1")
                    .IndexAsEmbedded(e => e.Coat, b =>
                    {
                        b.IsDynamicWithTypeSet("TypeSet2")
                            .IndexAsEmbeddedArray(e => e.Colors, b => { b.IsDynamicWithTypeSet("TypeSet3"); })
                            .IndexAsEmbedded(e => e.Grooming, b => { b.IsDynamicWithTypeSet("TypeSet4"); });
                    })
                    .IndexAsEmbeddedArray(e => e.Friends, b => { b.IsDynamicWithTypeSet("TypeSet5"); })
                    .AddTypeSet("TypeSet1",
                        b => { b.IndexAsNumber(b => { b.WithRepresentation(SearchNumberRepresentation.Double); }); })
                    .AddTypeSet("TypeSet2",
                        b => { b.IndexAsNumber(b => { b.WithRepresentation(SearchNumberRepresentation.Int64); }); })
                    .AddTypeSet("TypeSet3", b =>
                    {
                        b.IndexAsNumber(b =>
                        {
                            b.IndexDoubles();
                            b.IndexIntegers();
                        });
                    })
                    .AddTypeSet("TypeSet4", b =>
                    {
                        b.IndexAsNumber(b =>
                        {
                            b.IndexDoubles(false);
                            b.IndexIntegers(true);
                        });
                    })
                    .AddTypeSet("TypeSet5", b =>
                    {
                        b.IndexAsNumber(b =>
                        {
                            b.IndexIntegers(false);
                            b.IndexDoubles(true);

                        });
                    });
            })
            : b => b.Entity<Cat>(b =>
            {
                b.HasSearchIndex().IsDynamicWithTypeSet("TypeSet1");
                b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IsDynamicWithTypeSet("TypeSet2");
                b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbeddedArray(e => e.Colors)
                    .IsDynamicWithTypeSet("TypeSet3");
                b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbedded(e => e.Grooming).IsDynamicWithTypeSet("TypeSet4");
                b.HasSearchIndex().IndexAsEmbeddedArray(e => e.Friends).IsDynamicWithTypeSet("TypeSet5");

                b.HasSearchIndex().AddTypeSet("TypeSet1").IndexAsNumber().WithRepresentation(SearchNumberRepresentation.Double);
                b.HasSearchIndex().AddTypeSet("TypeSet2").IndexAsNumber().WithRepresentation(SearchNumberRepresentation.Int64);
                b.HasSearchIndex().AddTypeSet("TypeSet3").IndexAsNumber().IndexDoubles().IndexIntegers();
                b.HasSearchIndex().AddTypeSet("TypeSet4").IndexAsNumber().IndexDoubles(false).IndexIntegers(true);
                b.HasSearchIndex().AddTypeSet("TypeSet5").IndexAsNumber().IndexIntegers(false).IndexDoubles(true);
            });

        await using var db = SingleEntityDbContext.Create(collection, modelBuilderAction);
        await db.Database.EnsureCreatedAsync();

        ValidateIndex(database, collection.CollectionNamespace, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : {
                  "typeSet" : "TypeSet1"
                },
                "fields" : {
                  "bCoat" : {
                    "type" : "document",
                    "dynamic" : {
                      "typeSet" : "TypeSet2"
                    },
                    "fields" : {
                      "bColors" : {
                        "type" : "embeddedDocuments",
                        "dynamic" : {
                          "typeSet" : "TypeSet3"
                        },
                        "fields" : { }
                      },
                      "bGrooming" : {
                        "type" : "document",
                        "dynamic" : {
                          "typeSet" : "TypeSet4"
                        },
                        "fields" : { }
                      }
                    }
                  },
                  "bFriends" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : {
                      "typeSet" : "TypeSet5"
                    },
                    "fields" : { }
                  }
                }
              },
              "typeSets" : [{
                  "name" : "TypeSet1",
                  "types" : [{
                      "type" : "number",
                      "representation" : "double",
                      "indexDoubles" : true,
                      "indexIntegers" : true
                    }]
                }, {
                  "name" : "TypeSet2",
                  "types" : [{
                      "type" : "number",
                      "representation" : "int64",
                      "indexDoubles" : true,
                      "indexIntegers" : true
                    }]
                }, {
                  "name" : "TypeSet3",
                  "types" : [{
                      "type" : "number",
                      "representation" : "double",
                      "indexDoubles" : true,
                      "indexIntegers" : true
                    }]
                }, {
                  "name" : "TypeSet4",
                  "types" : [{
                      "type" : "number",
                      "representation" : "double",
                      "indexDoubles" : false,
                      "indexIntegers" : true
                    }]
                }, {
                  "name" : "TypeSet5",
                  "types" : [{
                      "type" : "number",
                      "representation" : "double",
                      "indexDoubles" : true,
                      "indexIntegers" : false
                    }]
                }]
            }
            """);
    }

    [AtlasTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Create_static_objectId_search_index(bool nestedBuilders, bool strings)
    {
        var collection = database.CreateCollection<Cat>(values: [nestedBuilders, strings]);

        Action<ModelBuilder> modelBuilderAction = nestedBuilders
            ? strings
                ? b => b.Entity(typeof(Cat)).HasSearchIndex(b =>
                {
                    b.IndexAsObjectId("Id", b => { })
                        .IndexAsEmbedded("Coat", b =>
                        {
                            b.IndexAsEmbeddedArray("Colors", b => { })
                                .IndexAsEmbedded("Grooming", b => { b.IndexAsObjectId("CatCode", b => { }); });
                        })
                        .IndexAsEmbeddedArray("Friends", b => { b.IndexAsObjectId("CatCode", b => { }); });
                })
                : b => b.Entity<Cat>().HasSearchIndex(b =>
                {
                    b.IndexAsObjectId(e => e.Id, b => { })
                        .IndexAsEmbedded(e => e.Coat, b =>
                        {
                            b.IndexAsEmbeddedArray(e => e.Colors, b => { })
                                .IndexAsEmbedded(e => e.Grooming, b => { b.IndexAsObjectId(e => e.CatCode, b => { }); });
                        });
                    b.IndexAsEmbeddedArray(e => e.Friends, b => { b.IndexAsObjectId(e => e.CatCode, b => { }); });
                })
            : strings
                ? b => b.Entity(typeof(Cat), b =>
                {
                    b.HasSearchIndex().IndexAsObjectId("Id");
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbeddedArray("Colors");
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbedded("Grooming").IndexAsObjectId("CatCode");
                    b.HasSearchIndex().IndexAsEmbeddedArray("Friends").IndexAsObjectId("CatCode");
                })
                : b => b.Entity<Cat>(b =>
                {
                    b.HasSearchIndex().IndexAsObjectId(e => e.Id);
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbeddedArray(e => e.Colors);
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbedded(e => e.Grooming)
                        .IndexAsObjectId(e => e.CatCode);
                    b.HasSearchIndex().IndexAsEmbeddedArray(e => e.Friends).IndexAsObjectId(e => e.CatCode);
                });

        await using var db = SingleEntityDbContext.Create(collection, modelBuilderAction);
        await db.Database.EnsureCreatedAsync();

        ValidateIndex(database, collection.CollectionNamespace, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "bCoat" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "bColors" : {
                        "type" : "embeddedDocuments",
                        "dynamic" : false,
                        "fields" : { }
                      },
                      "bGrooming" : {
                        "type" : "document",
                        "dynamic" : false,
                        "fields" : {
                          "bCatCode" : {
                            "type" : "objectId"
                          }
                        }
                      }
                    }
                  },
                  "bFriends" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : false,
                    "fields" : {
                      "bCatCode" : {
                        "type" : "objectId"
                      }
                    }
                  },
                  "_id" : {
                    "type" : "objectId"
                  }
                }
              }
            }
            """);
    }

    [AtlasTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_dynamic_objectId_search_index(bool nestedBuilders)
    {
        var collection = database.CreateCollection<Cat>(values: [nestedBuilders]);

        Action<ModelBuilder> modelBuilderAction = nestedBuilders
            ? b => b.Entity<Cat>().HasSearchIndex(b =>
            {
                b.IsDynamicWithTypeSet("TypeSet1")
                    .IndexAsEmbedded(e => e.Coat, b =>
                    {
                        b.IsDynamicWithTypeSet("TypeSet2")
                            .IndexAsEmbeddedArray(e => e.Colors, b => { b.IsDynamicWithTypeSet("TypeSet3"); })
                            .IndexAsEmbedded(e => e.Grooming, b => { b.IsDynamicWithTypeSet("TypeSet4"); });
                    })
                    .IndexAsEmbeddedArray(e => e.Friends, b => { b.IsDynamicWithTypeSet("TypeSet5"); })
                    .AddTypeSet("TypeSet1", b => { b.IndexAsObjectId(b => { }); })
                    .AddTypeSet("TypeSet2", b => { b.IndexAsObjectId(b => { }); })
                    .AddTypeSet("TypeSet3", b => { b.IndexAsObjectId(b => { }); })
                    .AddTypeSet("TypeSet4", b => { b.IndexAsObjectId(b => { }); })
                    .AddTypeSet("TypeSet5", b => { b.IndexAsObjectId(b => { }); });
            })
            : b => b.Entity<Cat>(b =>
            {
                b.HasSearchIndex().IsDynamicWithTypeSet("TypeSet1");
                b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IsDynamicWithTypeSet("TypeSet2");
                b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbeddedArray(e => e.Colors)
                    .IsDynamicWithTypeSet("TypeSet3");
                b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbedded(e => e.Grooming).IsDynamicWithTypeSet("TypeSet4");
                b.HasSearchIndex().IndexAsEmbeddedArray(e => e.Friends).IsDynamicWithTypeSet("TypeSet5");

                b.HasSearchIndex().AddTypeSet("TypeSet1").IndexAsObjectId();
                b.HasSearchIndex().AddTypeSet("TypeSet2").IndexAsObjectId();

                b.HasSearchIndex().AddTypeSet("TypeSet3").IndexAsObjectId();
                b.HasSearchIndex().AddTypeSet("TypeSet4").IndexAsObjectId();
                b.HasSearchIndex().AddTypeSet("TypeSet5").IndexAsObjectId();
            });

        await using var db = SingleEntityDbContext.Create(collection, modelBuilderAction);
        await db.Database.EnsureCreatedAsync();

        ValidateIndex(database, collection.CollectionNamespace, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : {
                  "typeSet" : "TypeSet1"
                },
                "fields" : {
                  "bCoat" : {
                    "type" : "document",
                    "dynamic" : {
                      "typeSet" : "TypeSet2"
                    },
                    "fields" : {
                      "bColors" : {
                        "type" : "embeddedDocuments",
                        "dynamic" : {
                          "typeSet" : "TypeSet3"
                        },
                        "fields" : { }
                      },
                      "bGrooming" : {
                        "type" : "document",
                        "dynamic" : {
                          "typeSet" : "TypeSet4"
                        },
                        "fields" : { }
                      }
                    }
                  },
                  "bFriends" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : {
                      "typeSet" : "TypeSet5"
                    },
                    "fields" : { }
                  }
                }
              },
              "typeSets" : [{
                  "name" : "TypeSet1",
                  "types" : [{
                      "type" : "objectId"
                    }]
                }, {
                  "name" : "TypeSet2",
                  "types" : [{
                      "type" : "objectId"
                    }]
                }, {
                  "name" : "TypeSet3",
                  "types" : [{
                      "type" : "objectId"
                    }]
                }, {
                  "name" : "TypeSet4",
                  "types" : [{
                      "type" : "objectId"
                    }]
                }, {
                  "name" : "TypeSet5",
                  "types" : [{
                      "type" : "objectId"
                    }]
                }]
            }
            """);
    }

    [AtlasTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Create_static_string_search_index(bool nestedBuilders, bool strings)
    {
        var collection = database.CreateCollection<Cat>(values: [nestedBuilders, strings]);

        Action<ModelBuilder> modelBuilderAction = nestedBuilders
            ? strings
                ? b => b.Entity(typeof(Cat)).HasSearchIndex(b =>
                {
                    b.IndexAsString("Name", b =>
                        {
                            b.UseAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace)
                                .UseSearchAnalyzer("lucene.standard")
                                .WithIndexAmount(StringSearchIndexAmount.Positions)
                                .StoreDocumentText()
                                .IgnoreAbove(2048)
                                .UseSimilarity(SearchSimilarityAlgorithm.Bm25)
                                .AddAlternateAnalyzer("English", BuiltInSearchAnalyzer.LuceneEnglish)
                                .AddAlternateAnalyzer("French", "lucene.french")
                                .AddAlternateSimilarity("StableSimilarity", SearchSimilarityAlgorithm.StableTfl)
                                .IncludeFieldLength();
                        })
                        .IndexAsString("Comments", b =>
                        {
                            b.WithIndexAmount(StringSearchIndexAmount.Freqs)
                                .StoreDocumentText(false)
                                .UseSimilarity(SearchSimilarityAlgorithm.Boolean)
                                .IncludeFieldLength(false);
                        })
                        .IndexAsEmbedded("Coat", b =>
                        {
                            b.IndexAsString("Comments", b => { })
                                .IndexAsEmbeddedArray("Colors", b =>
                                {
                                    b.IndexAsString("Color", b =>
                                        {
                                            b.UseAnalyzer("lucene.whitespace")
                                                .UseSearchAnalyzer("lucene.standard")
                                                .IgnoreAbove(2048)
                                                .UseSimilarity(SearchSimilarityAlgorithm.Bm25)
                                                .AddAlternateAnalyzer("English", "lucene.english")
                                                .AddAlternateAnalyzer("French", BuiltInSearchAnalyzer.LuceneFrench)
                                                .AddAlternateSimilarity("StableSimilarity", SearchSimilarityAlgorithm.StableTfl);
                                        })
                                        .IndexAsString("Comments", b =>
                                        {
                                            b.UseAnalyzer("lucene.simple")
                                                .WithIndexAmount(StringSearchIndexAmount.Freqs)
                                                .AddAlternateSimilarity("StableSimilarity", SearchSimilarityAlgorithm.StableTfl)
                                                .IncludeFieldLength();
                                        });
                                })
                                .IndexAsEmbedded("Grooming", b =>
                                {
                                    b.IndexAsString("GroomerName", b =>
                                        {
                                            b.UseAnalyzer("lucene.simple")
                                                .WithIndexAmount(StringSearchIndexAmount.Freqs)
                                                .AddAlternateSimilarity("StableSimilarity", SearchSimilarityAlgorithm.StableTfl)
                                                .IncludeFieldLength();
                                        })
                                        .IndexAsString("Comments", b =>
                                        {
                                            b.UseAnalyzer("lucene.simple")
                                                .WithIndexAmount(StringSearchIndexAmount.Freqs)
                                                .AddAlternateSimilarity("StableSimilarity", SearchSimilarityAlgorithm.StableTfl)
                                                .IncludeFieldLength();
                                        });
                                });
                        })
                        .IndexAsEmbeddedArray("Friends", b =>
                        {
                            b.IndexAsString("Name", b =>
                            {
                                b.UseAnalyzer("lucene.simple")
                                    .WithIndexAmount(StringSearchIndexAmount.Docs)
                                    .IncludeFieldLength();
                            });
                        });
                })
                : b => b.Entity<Cat>().HasSearchIndex(b =>
                {
                    b.IndexAsString(e => e.Name, b =>
                        {
                            b.UseAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace)
                                .UseSearchAnalyzer("lucene.standard")
                                .WithIndexAmount(StringSearchIndexAmount.Positions)
                                .StoreDocumentText()
                                .IgnoreAbove(2048)
                                .UseSimilarity(SearchSimilarityAlgorithm.Bm25)
                                .AddAlternateAnalyzer("English", "lucene.english")
                                .AddAlternateAnalyzer("French", "lucene.french")
                                .AddAlternateSimilarity("StableSimilarity", SearchSimilarityAlgorithm.StableTfl)
                                .IncludeFieldLength();
                        })
                        .IndexAsString(e => e.Comments, b =>
                        {
                            b.WithIndexAmount(StringSearchIndexAmount.Freqs)
                                .StoreDocumentText(false)
                                .UseSimilarity(SearchSimilarityAlgorithm.Boolean)
                                .IncludeFieldLength(false);
                        })
                        .IndexAsEmbedded(e => e.Coat, b =>
                        {
                            b.IndexAsString(e => e.Comments, b => { })
                                .IndexAsEmbeddedArray(e => e.Colors, b =>
                                {
                                    b.IndexAsString(e => e.Color, b =>
                                        {
                                            b.UseAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace)
                                                .UseSearchAnalyzer("lucene.standard")
                                                .IgnoreAbove(2048)
                                                .UseSimilarity(SearchSimilarityAlgorithm.Bm25)
                                                .AddAlternateAnalyzer("English", "lucene.english")
                                                .AddAlternateAnalyzer("French", "lucene.french")
                                                .AddAlternateSimilarity("StableSimilarity", SearchSimilarityAlgorithm.StableTfl);
                                        })
                                        .IndexAsString(e => e.Comments, b =>
                                        {
                                            b.UseAnalyzer("lucene.simple")
                                                .WithIndexAmount(StringSearchIndexAmount.Freqs)
                                                .AddAlternateSimilarity("StableSimilarity", SearchSimilarityAlgorithm.StableTfl)
                                                .IncludeFieldLength();
                                        });
                                })
                                .IndexAsEmbedded(e => e.Grooming, b =>
                                {
                                    b.IndexAsString(e => e.GroomerName, b =>
                                        {
                                            b.UseAnalyzer("lucene.simple")
                                                .WithIndexAmount(StringSearchIndexAmount.Freqs)
                                                .AddAlternateSimilarity("StableSimilarity", SearchSimilarityAlgorithm.StableTfl)
                                                .IncludeFieldLength();
                                        })
                                        .IndexAsString(e => e.Comments, b =>
                                        {
                                            b.UseAnalyzer("lucene.simple")
                                                .WithIndexAmount(StringSearchIndexAmount.Freqs)
                                                .AddAlternateSimilarity("StableSimilarity", SearchSimilarityAlgorithm.StableTfl)
                                                .IncludeFieldLength();
                                        });
                                });
                        });
                    b.IndexAsEmbeddedArray(e => e.Friends, b =>
                    {
                        b.IndexAsString(e => e.Name, b =>
                        {
                            b.UseAnalyzer("lucene.simple")
                                .WithIndexAmount(StringSearchIndexAmount.Docs)
                                .IncludeFieldLength();
                        });
                    });
                })
            : strings
                ? b => b.Entity(typeof(Cat), b =>
                {
                    b.HasSearchIndex()
                        .IndexAsString("Name")
                        .UseAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace)
                        .UseSearchAnalyzer(BuiltInSearchAnalyzer.LuceneStandard)
                        .WithIndexAmount(StringSearchIndexAmount.Positions)
                        .StoreDocumentText()
                        .IgnoreAbove(2048)
                        .UseSimilarity(SearchSimilarityAlgorithm.Bm25)
                        .AddAlternateAnalyzer("English", "lucene.english")
                        .AddAlternateAnalyzer("French", "lucene.french")
                        .AddAlternateSimilarity("StableSimilarity", SearchSimilarityAlgorithm.StableTfl)
                        .IncludeFieldLength();

                    b.HasSearchIndex().IndexAsString("Comments")
                        .WithIndexAmount(StringSearchIndexAmount.Freqs)
                        .StoreDocumentText(false)
                        .UseSimilarity(SearchSimilarityAlgorithm.Boolean)
                        .IncludeFieldLength(false);

                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsString("Comments");
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbeddedArray("Colors").IndexAsString("Color")
                        .UseAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace)
                        .UseSearchAnalyzer("lucene.standard")
                        .IgnoreAbove(2048)
                        .UseSimilarity(SearchSimilarityAlgorithm.Bm25)
                        .AddAlternateAnalyzer("English", "lucene.english")
                        .AddAlternateAnalyzer("French", "lucene.french")
                        .AddAlternateSimilarity("StableSimilarity", SearchSimilarityAlgorithm.StableTfl);


                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbeddedArray("Colors").IndexAsString("Comments")
                        .UseAnalyzer("lucene.simple")
                        .WithIndexAmount(StringSearchIndexAmount.Freqs)
                        .AddAlternateSimilarity("StableSimilarity", SearchSimilarityAlgorithm.StableTfl)
                        .IncludeFieldLength();

                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbedded("Grooming").IndexAsString("GroomerName")
                        .UseAnalyzer("lucene.simple")
                        .WithIndexAmount(StringSearchIndexAmount.Freqs)
                        .AddAlternateSimilarity("StableSimilarity", SearchSimilarityAlgorithm.StableTfl)
                        .IncludeFieldLength();

                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbedded("Grooming").IndexAsString("Comments")
                        .UseAnalyzer("lucene.simple")
                        .WithIndexAmount(StringSearchIndexAmount.Freqs)
                        .AddAlternateSimilarity("StableSimilarity", SearchSimilarityAlgorithm.StableTfl)
                        .IncludeFieldLength();

                    b.HasSearchIndex().IndexAsEmbeddedArray("Friends").IndexAsString("Name")
                        .UseAnalyzer("lucene.simple")
                        .WithIndexAmount(StringSearchIndexAmount.Docs)
                        .IncludeFieldLength();

                })
                : b => b.Entity<Cat>(b =>
                {
                    b.HasSearchIndex()
                        .IndexAsString(e => e.Name)
                        .UseAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace)
                        .UseSearchAnalyzer("lucene.standard")
                        .WithIndexAmount(StringSearchIndexAmount.Positions)
                        .StoreDocumentText()
                        .IgnoreAbove(2048)
                        .UseSimilarity(SearchSimilarityAlgorithm.Bm25)
                        .AddAlternateAnalyzer("English", "lucene.english")
                        .AddAlternateAnalyzer("French", "lucene.french")
                        .AddAlternateSimilarity("StableSimilarity", SearchSimilarityAlgorithm.StableTfl)
                        .IncludeFieldLength();

                    b.HasSearchIndex().IndexAsString(e => e.Comments)
                        .WithIndexAmount(StringSearchIndexAmount.Freqs)
                        .StoreDocumentText(false)
                        .UseSimilarity(SearchSimilarityAlgorithm.Boolean)
                        .IncludeFieldLength(false);

                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsString(e => e.Comments);
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbeddedArray(e => e.Colors).IndexAsString(e => e.Color)
                        .UseAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace)
                        .UseSearchAnalyzer("lucene.standard")
                        .IgnoreAbove(2048)
                        .UseSimilarity(SearchSimilarityAlgorithm.Bm25)
                        .AddAlternateAnalyzer("English", "lucene.english")
                        .AddAlternateAnalyzer("French", "lucene.french")
                        .AddAlternateSimilarity("StableSimilarity", SearchSimilarityAlgorithm.StableTfl);


                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbeddedArray(e => e.Colors)
                        .IndexAsString(e => e.Comments)
                        .UseAnalyzer("lucene.simple")
                        .WithIndexAmount(StringSearchIndexAmount.Freqs)
                        .AddAlternateSimilarity("StableSimilarity", SearchSimilarityAlgorithm.StableTfl)
                        .IncludeFieldLength();

                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbedded(e => e.Grooming)
                        .IndexAsString(e => e.GroomerName)
                        .UseAnalyzer("lucene.simple")
                        .WithIndexAmount(StringSearchIndexAmount.Freqs)
                        .AddAlternateSimilarity("StableSimilarity", SearchSimilarityAlgorithm.StableTfl)
                        .IncludeFieldLength();

                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbedded(e => e.Grooming).IndexAsString(e => e.Comments)
                        .UseAnalyzer("lucene.simple")
                        .WithIndexAmount(StringSearchIndexAmount.Freqs)
                        .AddAlternateSimilarity("StableSimilarity", SearchSimilarityAlgorithm.StableTfl)
                        .IncludeFieldLength();

                    b.HasSearchIndex().IndexAsEmbeddedArray(e => e.Friends).IndexAsString(e => e.Name)
                        .UseAnalyzer("lucene.simple")
                        .WithIndexAmount(StringSearchIndexAmount.Docs)
                        .IncludeFieldLength();

                });

        await using var db = SingleEntityDbContext.Create(collection, modelBuilderAction);
        await db.Database.EnsureCreatedAsync();

        ValidateIndex(database, collection.CollectionNamespace, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "bComments" : {
                    "type" : "string",
                    "similarity" : {
                      "type" : "boolean"
                    },
                    "indexOptions" : "freqs",
                    "store" : false,
                    "norms" : "omit"
                  },
                  "bName" : {
                    "type" : "string",
                    "analyzer" : "lucene.whitespace",
                    "searchAnalyzer" : "lucene.standard",
                    "similarity" : {
                      "type" : "bm25"
                    },
                    "ignoreAbove" : 2048,
                    "indexOptions" : "positions",
                    "store" : true,
                    "norms" : "include",
                    "multi" : {
                      "English" : {
                        "type" : "string",
                        "analyzer" : "lucene.english",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      },
                      "StableSimilarity" : {
                        "type" : "string",
                        "similarity" : {
                          "type" : "stableTfl"
                        },
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      },
                      "French" : {
                        "type" : "string",
                        "analyzer" : "lucene.french",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  },
                  "bCoat" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "bColors" : {
                        "type" : "embeddedDocuments",
                        "dynamic" : false,
                        "fields" : {
                          "bbbComments" : {
                            "type" : "string",
                            "analyzer" : "lucene.simple",
                            "indexOptions" : "freqs",
                            "store" : true,
                            "norms" : "include",
                            "multi" : {
                              "StableSimilarity" : {
                                "type" : "string",
                                "similarity" : {
                                  "type" : "stableTfl"
                                },
                                "indexOptions" : "offsets",
                                "store" : true,
                                "norms" : "include"
                              }
                            }
                          },
                          "bColor" : {
                            "type" : "string",
                            "analyzer" : "lucene.whitespace",
                            "searchAnalyzer" : "lucene.standard",
                            "similarity" : {
                              "type" : "bm25"
                            },
                            "ignoreAbove" : 2048,
                            "indexOptions" : "offsets",
                            "store" : true,
                            "norms" : "include",
                            "multi" : {
                              "English" : {
                                "type" : "string",
                                "analyzer" : "lucene.english",
                                "indexOptions" : "offsets",
                                "store" : true,
                                "norms" : "include"
                              },
                              "StableSimilarity" : {
                                "type" : "string",
                                "similarity" : {
                                  "type" : "stableTfl"
                                },
                                "indexOptions" : "offsets",
                                "store" : true,
                                "norms" : "include"
                              },
                              "French" : {
                                "type" : "string",
                                "analyzer" : "lucene.french",
                                "indexOptions" : "offsets",
                                "store" : true,
                                "norms" : "include"
                              }
                            }
                          }
                        }
                      },
                      "bbComments" : {
                        "type" : "string",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      },
                      "bGrooming" : {
                        "type" : "document",
                        "dynamic" : false,
                        "fields" : {
                          "bGroomerName" : {
                            "type" : "string",
                            "analyzer" : "lucene.simple",
                            "indexOptions" : "freqs",
                            "store" : true,
                            "norms" : "include",
                            "multi" : {
                              "StableSimilarity" : {
                                "type" : "string",
                                "similarity" : {
                                  "type" : "stableTfl"
                                },
                                "indexOptions" : "offsets",
                                "store" : true,
                                "norms" : "include"
                              }
                            }
                          },
                          "bbbbComments" : {
                            "type" : "string",
                            "analyzer" : "lucene.simple",
                            "indexOptions" : "freqs",
                            "store" : true,
                            "norms" : "include",
                            "multi" : {
                              "StableSimilarity" : {
                                "type" : "string",
                                "similarity" : {
                                  "type" : "stableTfl"
                                },
                                "indexOptions" : "offsets",
                                "store" : true,
                                "norms" : "include"
                              }
                            }
                          }
                        }
                      }
                    }
                  },
                  "bFriends" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : false,
                    "fields" : {
                      "bName" : {
                        "type" : "string",
                        "analyzer" : "lucene.simple",
                        "indexOptions" : "docs",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  }
                }
              }
            }
            """);
    }

    [AtlasTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_dynamic_string_search_index(bool nestedBuilders)
    {
        var collection = database.CreateCollection<Cat>(values: [nestedBuilders]);

        Action<ModelBuilder> modelBuilderAction = nestedBuilders
            ? b => b.Entity<Cat>().HasSearchIndex(b =>
            {
                b.IsDynamicWithTypeSet("TypeSet1")
                    .IndexAsEmbedded(e => e.Coat, b =>
                    {
                        b.IsDynamicWithTypeSet("TypeSet2")
                            .IndexAsEmbeddedArray(e => e.Colors, b => { b.IsDynamicWithTypeSet("TypeSet3"); })
                            .IndexAsEmbedded(e => e.Grooming, b => { b.IsDynamicWithTypeSet("TypeSet4"); });
                    })
                    .IndexAsEmbeddedArray(e => e.Friends, b => { b.IsDynamicWithTypeSet("TypeSet5"); })
                    .AddTypeSet("TypeSet1", b =>
                    {
                        b.IndexAsString(b =>
                        {
                            b.UseAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace)
                                .UseSearchAnalyzer("lucene.standard")
                                .WithIndexAmount(StringSearchIndexAmount.Positions)
                                .StoreDocumentText()
                                .IgnoreAbove(2048)
                                .UseSimilarity(SearchSimilarityAlgorithm.Bm25)
                                .AddAlternateAnalyzer("English", "lucene.english")
                                .AddAlternateAnalyzer("French", "lucene.french")
                                .AddAlternateSimilarity("StableSimilarity", SearchSimilarityAlgorithm.StableTfl)
                                .IncludeFieldLength();
                        });
                    })
                    .AddTypeSet("TypeSet2", b => { b.IndexAsString(b => { }); })
                    .AddTypeSet("TypeSet3", b =>
                    {
                        b.IndexAsString(b =>
                        {
                            b.WithIndexAmount(StringSearchIndexAmount.Freqs)
                                .StoreDocumentText(false)
                                .UseSimilarity(SearchSimilarityAlgorithm.Boolean)
                                .IncludeFieldLength(false);
                        });
                    })
                    .AddTypeSet("TypeSet4", b =>
                    {
                        b.IndexAsString(b =>
                        {
                            b.UseAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace)
                                .UseSearchAnalyzer("lucene.standard")
                                .IgnoreAbove(2048)
                                .UseSimilarity(SearchSimilarityAlgorithm.Bm25)
                                .AddAlternateAnalyzer("English", "lucene.english")
                                .AddAlternateAnalyzer("French", "lucene.french")
                                .AddAlternateSimilarity("StableSimilarity", SearchSimilarityAlgorithm.StableTfl);
                        });
                    })
                    .AddTypeSet("TypeSet5", b =>
                    {
                        b.IndexAsString(b =>
                        {
                            b.UseAnalyzer("lucene.simple")
                                .WithIndexAmount(StringSearchIndexAmount.Freqs)
                                .AddAlternateSimilarity("StableSimilarity", SearchSimilarityAlgorithm.StableTfl)
                                .IncludeFieldLength();
                        });
                    });
            })
            : b => b.Entity<Cat>(b =>
            {
                b.HasSearchIndex().IsDynamicWithTypeSet("TypeSet1");
                b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IsDynamicWithTypeSet("TypeSet2");
                b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbeddedArray(e => e.Colors)
                    .IsDynamicWithTypeSet("TypeSet3");
                b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbedded(e => e.Grooming).IsDynamicWithTypeSet("TypeSet4");
                b.HasSearchIndex().IndexAsEmbeddedArray(e => e.Friends).IsDynamicWithTypeSet("TypeSet5");

                b.HasSearchIndex().AddTypeSet("TypeSet1")
                    .IndexAsString()
                    .UseAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace)
                    .UseSearchAnalyzer("lucene.standard")
                    .WithIndexAmount(StringSearchIndexAmount.Positions)
                    .StoreDocumentText()
                    .IgnoreAbove(2048)
                    .UseSimilarity(SearchSimilarityAlgorithm.Bm25)
                    .AddAlternateAnalyzer("English", "lucene.english")
                    .AddAlternateAnalyzer("French", "lucene.french")
                    .AddAlternateSimilarity("StableSimilarity", SearchSimilarityAlgorithm.StableTfl)
                    .IncludeFieldLength();

                b.HasSearchIndex().AddTypeSet("TypeSet2")
                    .IndexAsString();

                b.HasSearchIndex().AddTypeSet("TypeSet3")
                    .IndexAsString()
                    .WithIndexAmount(StringSearchIndexAmount.Freqs)
                    .StoreDocumentText(false)
                    .UseSimilarity(SearchSimilarityAlgorithm.Boolean)
                    .IncludeFieldLength(false);

                b.HasSearchIndex().AddTypeSet("TypeSet4")
                    .IndexAsString()
                    .UseAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace)
                    .UseSearchAnalyzer("lucene.standard")
                    .IgnoreAbove(2048)
                    .UseSimilarity(SearchSimilarityAlgorithm.Bm25)
                    .AddAlternateAnalyzer("English", "lucene.english")
                    .AddAlternateAnalyzer("French", "lucene.french")
                    .AddAlternateSimilarity("StableSimilarity", SearchSimilarityAlgorithm.StableTfl);

                b.HasSearchIndex().AddTypeSet("TypeSet5")
                    .IndexAsString()
                    .UseAnalyzer("lucene.simple")
                    .WithIndexAmount(StringSearchIndexAmount.Freqs)
                    .AddAlternateSimilarity("StableSimilarity", SearchSimilarityAlgorithm.StableTfl)
                    .IncludeFieldLength();

            });

        await using var db = SingleEntityDbContext.Create(collection, modelBuilderAction);
        await db.Database.EnsureCreatedAsync();

        ValidateIndex(database, collection.CollectionNamespace, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : {
                  "typeSet" : "TypeSet1"
                },
                "fields" : {
                  "bCoat" : {
                    "type" : "document",
                    "dynamic" : {
                      "typeSet" : "TypeSet2"
                    },
                    "fields" : {
                      "bColors" : {
                        "type" : "embeddedDocuments",
                        "dynamic" : {
                          "typeSet" : "TypeSet3"
                        },
                        "fields" : { }
                      },
                      "bGrooming" : {
                        "type" : "document",
                        "dynamic" : {
                          "typeSet" : "TypeSet4"
                        },
                        "fields" : { }
                      }
                    }
                  },
                  "bFriends" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : {
                      "typeSet" : "TypeSet5"
                    },
                    "fields" : { }
                  }
                }
              },
              "typeSets" : [{
                  "name" : "TypeSet1",
                  "types" : [{
                      "type" : "string",
                      "analyzer" : "lucene.whitespace",
                      "searchAnalyzer" : "lucene.standard",
                      "similarity" : {
                        "type" : "bm25"
                      },
                      "ignoreAbove" : 2048,
                      "indexOptions" : "positions",
                      "store" : true,
                      "norms" : "include",
                      "multi" : {
                        "English" : {
                          "type" : "string",
                          "analyzer" : "lucene.english",
                          "indexOptions" : "offsets",
                          "store" : true,
                          "norms" : "include"
                        },
                        "StableSimilarity" : {
                          "type" : "string",
                          "similarity" : {
                            "type" : "stableTfl"
                          },
                          "indexOptions" : "offsets",
                          "store" : true,
                          "norms" : "include"
                        },
                        "French" : {
                          "type" : "string",
                          "analyzer" : "lucene.french",
                          "indexOptions" : "offsets",
                          "store" : true,
                          "norms" : "include"
                        }
                      }
                    }]
                }, {
                  "name" : "TypeSet2",
                  "types" : [{
                      "type" : "string",
                      "indexOptions" : "offsets",
                      "store" : true,
                      "norms" : "include"
                    }]
                }, {
                  "name" : "TypeSet3",
                  "types" : [{
                      "type" : "string",
                      "similarity" : {
                        "type" : "boolean"
                      },
                      "indexOptions" : "freqs",
                      "store" : false,
                      "norms" : "omit"
                    }]
                }, {
                  "name" : "TypeSet4",
                  "types" : [{
                      "type" : "string",
                      "analyzer" : "lucene.whitespace",
                      "searchAnalyzer" : "lucene.standard",
                      "similarity" : {
                        "type" : "bm25"
                      },
                      "ignoreAbove" : 2048,
                      "indexOptions" : "offsets",
                      "store" : true,
                      "norms" : "include",
                      "multi" : {
                        "English" : {
                          "type" : "string",
                          "analyzer" : "lucene.english",
                          "indexOptions" : "offsets",
                          "store" : true,
                          "norms" : "include"
                        },
                        "StableSimilarity" : {
                          "type" : "string",
                          "similarity" : {
                            "type" : "stableTfl"
                          },
                          "indexOptions" : "offsets",
                          "store" : true,
                          "norms" : "include"
                        },
                        "French" : {
                          "type" : "string",
                          "analyzer" : "lucene.french",
                          "indexOptions" : "offsets",
                          "store" : true,
                          "norms" : "include"
                        }
                      }
                    }]
                }, {
                  "name" : "TypeSet5",
                  "types" : [{
                      "type" : "string",
                      "analyzer" : "lucene.simple",
                      "indexOptions" : "freqs",
                      "store" : true,
                      "norms" : "include",
                      "multi" : {
                        "StableSimilarity" : {
                          "type" : "string",
                          "similarity" : {
                            "type" : "stableTfl"
                          },
                          "indexOptions" : "offsets",
                          "store" : true,
                          "norms" : "include"
                        }
                      }
                    }]
                }]
            }
            """);
    }

    [AtlasTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Create_static_token_search_index(bool nestedBuilders, bool strings)
    {
        var collection = database.CreateCollection<Cat>(values: [nestedBuilders, strings]);

        Action<ModelBuilder> modelBuilderAction = nestedBuilders
            ? strings
                ? b => b.Entity(typeof(Cat)).HasSearchIndex(b =>
                {
                    b.IndexAsToken("Name", b => { b.NormalizeToLowercase(); })
                        .IndexAsToken("Comments", b => { })
                        .IndexAsEmbedded("Coat", b =>
                        {
                            b.IndexAsToken("Comments", b => { })
                                .IndexAsEmbeddedArray("Colors", b =>
                                {
                                    b.IndexAsToken("Color", b => { })
                                        .IndexAsToken("Comments", b => { b.NormalizeToLowercase(false); });
                                })
                                .IndexAsEmbedded("Grooming", b =>
                                {
                                    b.IndexAsToken("GroomerName", b => { })
                                        .IndexAsToken("Comments", b => { });
                                });
                        })
                        .IndexAsEmbeddedArray("Friends", b => { b.IndexAsToken("Name", b => { }); });
                })
                : b => b.Entity<Cat>().HasSearchIndex(b =>
                {
                    b.IndexAsToken(e => e.Name, b => { b.NormalizeToLowercase(); })
                        .IndexAsToken(e => e.Comments, b => { })
                        .IndexAsEmbedded(e => e.Coat, b =>
                        {
                            b.IndexAsToken(e => e.Comments, b => { })
                                .IndexAsEmbeddedArray(e => e.Colors, b =>
                                {
                                    b.IndexAsToken(e => e.Color, b => { })
                                        .IndexAsToken(e => e.Comments, b => { b.NormalizeToLowercase(false); });
                                })
                                .IndexAsEmbedded(e => e.Grooming, b =>
                                {
                                    b.IndexAsToken(e => e.GroomerName, b => { })
                                        .IndexAsToken(e => e.Comments, b => { });
                                });
                        });
                    b.IndexAsEmbeddedArray(e => e.Friends, b => { b.IndexAsToken(e => e.Name, b => { }); });
                })
            : strings
                ? b => b.Entity(typeof(Cat), b =>
                {
                    b.HasSearchIndex().IndexAsToken("Name").NormalizeToLowercase();
                    b.HasSearchIndex().IndexAsToken("Comments");
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsToken("Comments");
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbeddedArray("Colors").IndexAsToken("Color");
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbeddedArray("Colors").IndexAsToken("Comments")
                        .NormalizeToLowercase(false);
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbedded("Grooming").IndexAsToken("GroomerName");
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbedded("Grooming").IndexAsToken("Comments");
                    b.HasSearchIndex().IndexAsEmbeddedArray("Friends").IndexAsToken("Name");
                })
                : b => b.Entity<Cat>(b =>
                {
                    b.HasSearchIndex().IndexAsToken(e => e.Name).NormalizeToLowercase();
                    b.HasSearchIndex().IndexAsToken(e => e.Comments);
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsToken(e => e.Comments);
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbeddedArray(e => e.Colors).IndexAsToken(e => e.Color);
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbeddedArray(e => e.Colors)
                        .IndexAsToken(e => e.Comments).NormalizeToLowercase(false);
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbedded(e => e.Grooming)
                        .IndexAsToken(e => e.GroomerName);
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbedded(e => e.Grooming).IndexAsToken(e => e.Comments);
                    b.HasSearchIndex().IndexAsEmbeddedArray(e => e.Friends).IndexAsToken(e => e.Name);
                });

        await using var db = SingleEntityDbContext.Create(collection, modelBuilderAction);
        await db.Database.EnsureCreatedAsync();

        ValidateIndex(database, collection.CollectionNamespace, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "bComments" : {
                    "type" : "token"
                  },
                  "bName" : {
                    "type" : "token",
                    "normalizer" : "lowercase"
                  },
                  "bCoat" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "bColors" : {
                        "type" : "embeddedDocuments",
                        "dynamic" : false,
                        "fields" : {
                          "bbbComments" : {
                            "type" : "token",
                            "normalizer" : "none"
                          },
                          "bColor" : {
                            "type" : "token"
                          }
                        }
                      },
                      "bbComments" : {
                        "type" : "token"
                      },
                      "bGrooming" : {
                        "type" : "document",
                        "dynamic" : false,
                        "fields" : {
                          "bGroomerName" : {
                            "type" : "token"
                          },
                          "bbbbComments" : {
                            "type" : "token"
                          }
                        }
                      }
                    }
                  },
                  "bFriends" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : false,
                    "fields" : {
                      "bName" : {
                        "type" : "token"
                      }
                    }
                  }
                }
              }
            }
            """);
    }

    [AtlasTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_dynamic_token_search_index(bool nestedBuilders)
    {
        var collection = database.CreateCollection<Cat>(values: [nestedBuilders]);

        Action<ModelBuilder> modelBuilderAction = nestedBuilders
            ? b => b.Entity<Cat>().HasSearchIndex(b =>
            {
                b.IsDynamicWithTypeSet("TypeSet1")
                    .IndexAsEmbedded(e => e.Coat, b =>
                    {
                        b.IsDynamicWithTypeSet("TypeSet2")
                            .IndexAsEmbeddedArray(e => e.Colors, b => { b.IsDynamicWithTypeSet("TypeSet3"); })
                            .IndexAsEmbedded(e => e.Grooming, b => { b.IsDynamicWithTypeSet("TypeSet4"); });
                    })
                    .IndexAsEmbeddedArray(e => e.Friends, b => { b.IsDynamicWithTypeSet("TypeSet5"); })
                    .AddTypeSet("TypeSet1", b => { b.IndexAsToken(b => { b.NormalizeToLowercase(); }); })
                    .AddTypeSet("TypeSet2", b => { b.IndexAsToken(b => { }); })
                    .AddTypeSet("TypeSet3", b => { b.IndexAsToken(b => { b.NormalizeToLowercase(false); }); })
                    .AddTypeSet("TypeSet4", b => { b.IndexAsToken(b => { }); })
                    .AddTypeSet("TypeSet5", b => { b.IndexAsToken(b => { }); });
            })
            : b => b.Entity<Cat>(b =>
            {
                b.HasSearchIndex().IsDynamicWithTypeSet("TypeSet1");
                b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IsDynamicWithTypeSet("TypeSet2");
                b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbeddedArray(e => e.Colors)
                    .IsDynamicWithTypeSet("TypeSet3");
                b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbedded(e => e.Grooming).IsDynamicWithTypeSet("TypeSet4");
                b.HasSearchIndex().IndexAsEmbeddedArray(e => e.Friends).IsDynamicWithTypeSet("TypeSet5");

                b.HasSearchIndex().AddTypeSet("TypeSet1").IndexAsToken().NormalizeToLowercase();
                b.HasSearchIndex().AddTypeSet("TypeSet2").IndexAsToken();
                b.HasSearchIndex().AddTypeSet("TypeSet3").IndexAsToken().NormalizeToLowercase(false);
                b.HasSearchIndex().AddTypeSet("TypeSet4").IndexAsToken();
                b.HasSearchIndex().AddTypeSet("TypeSet5").IndexAsToken();
            });

        await using var db = SingleEntityDbContext.Create(collection, modelBuilderAction);
        await db.Database.EnsureCreatedAsync();

        ValidateIndex(database, collection.CollectionNamespace, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : {
                  "typeSet" : "TypeSet1"
                },
                "fields" : {
                  "bCoat" : {
                    "type" : "document",
                    "dynamic" : {
                      "typeSet" : "TypeSet2"
                    },
                    "fields" : {
                      "bColors" : {
                        "type" : "embeddedDocuments",
                        "dynamic" : {
                          "typeSet" : "TypeSet3"
                        },
                        "fields" : { }
                      },
                      "bGrooming" : {
                        "type" : "document",
                        "dynamic" : {
                          "typeSet" : "TypeSet4"
                        },
                        "fields" : { }
                      }
                    }
                  },
                  "bFriends" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : {
                      "typeSet" : "TypeSet5"
                    },
                    "fields" : { }
                  }
                }
              },
              "typeSets" : [{
                  "name" : "TypeSet1",
                  "types" : [{
                      "type" : "token",
                      "normalizer" : "lowercase"
                    }]
                }, {
                  "name" : "TypeSet2",
                  "types" : [{
                      "type" : "token"
                    }]
                }, {
                  "name" : "TypeSet3",
                  "types" : [{
                      "type" : "token",
                      "normalizer" : "none"
                    }]
                }, {
                  "name" : "TypeSet4",
                  "types" : [{
                      "type" : "token"
                    }]
                }, {
                  "name" : "TypeSet5",
                  "types" : [{
                      "type" : "token"
                    }]
                }]
            }
            """);
    }

    [AtlasTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Create_static_uuid_search_index(bool nestedBuilders, bool strings)
    {
        var collection = database.CreateCollection<Cat>(values: [nestedBuilders, strings]);

        Action<ModelBuilder> modelBuilderAction = nestedBuilders
            ? strings
                ? b => b.Entity(typeof(Cat)).HasSearchIndex(b =>
                {
                    b.IndexAsUuid("AlternateId", b => { })
                        .IndexAsEmbedded("Coat", b =>
                        {
                            b.IndexAsEmbeddedArray("Colors", b => { })
                                .IndexAsEmbedded("Grooming", b => { b.IndexAsUuid("LegacyCatCode", b => { }); });
                        })
                        .IndexAsEmbeddedArray("Friends", b => { b.IndexAsUuid("LegacyCatCode", b => { }); });
                })
                : b => b.Entity<Cat>().HasSearchIndex(b =>
                {
                    b.IndexAsUuid(e => e.AlternateId, b => { })
                        .IndexAsEmbedded(e => e.Coat, b =>
                        {
                            b.IndexAsEmbeddedArray(e => e.Colors, b => { })
                                .IndexAsEmbedded(e => e.Grooming, b => { b.IndexAsUuid(e => e.LegacyCatCode, b => { }); });
                        });
                    b.IndexAsEmbeddedArray(e => e.Friends, b => { b.IndexAsUuid(e => e.LegacyCatCode, b => { }); });
                })
            : strings
                ? b => b.Entity(typeof(Cat), b =>
                {
                    b.HasSearchIndex().IndexAsUuid("AlternateId");
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbeddedArray("Colors");
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbedded("Grooming").IndexAsUuid("LegacyCatCode");
                    b.HasSearchIndex().IndexAsEmbeddedArray("Friends").IndexAsUuid("LegacyCatCode");
                })
                : b => b.Entity<Cat>(b =>
                {
                    b.HasSearchIndex().IndexAsUuid(e => e.AlternateId);
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbeddedArray(e => e.Colors);
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbedded(e => e.Grooming)
                        .IndexAsUuid(e => e.LegacyCatCode);
                    b.HasSearchIndex().IndexAsEmbeddedArray(e => e.Friends).IndexAsUuid(e => e.LegacyCatCode);
                });

        await using var db = SingleEntityDbContext.Create(collection, modelBuilderAction);
        await db.Database.EnsureCreatedAsync();

        ValidateIndex(database, collection.CollectionNamespace, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "bAlternateId" : {
                    "type" : "uuid"
                  },
                  "bCoat" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "bColors" : {
                        "type" : "embeddedDocuments",
                        "dynamic" : false,
                        "fields" : { }
                      },
                      "bGrooming" : {
                        "type" : "document",
                        "dynamic" : false,
                        "fields" : {
                          "bLegacyCatCode" : {
                            "type" : "uuid"
                          }
                        }
                      }
                    }
                  },
                  "bFriends" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : false,
                    "fields" : {
                      "bLegacyCatCode" : {
                        "type" : "uuid"
                      }
                    }
                  }
                }
              }
            }
            """);
    }

    [AtlasTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_dynamic_uuid_search_index(bool nestedBuilders)
    {
        var collection = database.CreateCollection<Cat>(values: [nestedBuilders]);

        Action<ModelBuilder> modelBuilderAction = nestedBuilders
            ? b => b.Entity<Cat>().HasSearchIndex(b =>
            {
                b.IsDynamicWithTypeSet("TypeSet1")
                    .IndexAsEmbedded(e => e.Coat, b =>
                    {
                        b.IsDynamicWithTypeSet("TypeSet2")
                            .IndexAsEmbeddedArray(e => e.Colors, b => { b.IsDynamicWithTypeSet("TypeSet3"); })
                            .IndexAsEmbedded(e => e.Grooming, b => { b.IsDynamicWithTypeSet("TypeSet4"); });
                    })
                    .IndexAsEmbeddedArray(e => e.Friends, b => { b.IsDynamicWithTypeSet("TypeSet5"); })
                    .AddTypeSet("TypeSet1", b => { b.IndexAsUuid(b => { }); })
                    .AddTypeSet("TypeSet2", b => { b.IndexAsUuid(b => { }); })
                    .AddTypeSet("TypeSet3", b => { b.IndexAsUuid(b => { }); })
                    .AddTypeSet("TypeSet4", b => { b.IndexAsUuid(b => { }); })
                    .AddTypeSet("TypeSet5", b => { b.IndexAsUuid(b => { }); });
            })
            : b => b.Entity<Cat>(b =>
            {
                b.HasSearchIndex().IsDynamicWithTypeSet("TypeSet1");
                b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IsDynamicWithTypeSet("TypeSet2");
                b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbeddedArray(e => e.Colors)
                    .IsDynamicWithTypeSet("TypeSet3");
                b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbedded(e => e.Grooming).IsDynamicWithTypeSet("TypeSet4");
                b.HasSearchIndex().IndexAsEmbeddedArray(e => e.Friends).IsDynamicWithTypeSet("TypeSet5");

                b.HasSearchIndex().AddTypeSet("TypeSet1").IndexAsUuid();
                b.HasSearchIndex().AddTypeSet("TypeSet2").IndexAsUuid();

                b.HasSearchIndex().AddTypeSet("TypeSet3").IndexAsUuid();
                b.HasSearchIndex().AddTypeSet("TypeSet4").IndexAsUuid();
                b.HasSearchIndex().AddTypeSet("TypeSet5").IndexAsUuid();
            });

        await using var db = SingleEntityDbContext.Create(collection, modelBuilderAction);
        await db.Database.EnsureCreatedAsync();

        ValidateIndex(database, collection.CollectionNamespace, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : {
                  "typeSet" : "TypeSet1"
                },
                "fields" : {
                  "bCoat" : {
                    "type" : "document",
                    "dynamic" : {
                      "typeSet" : "TypeSet2"
                    },
                    "fields" : {
                      "bColors" : {
                        "type" : "embeddedDocuments",
                        "dynamic" : {
                          "typeSet" : "TypeSet3"
                        },
                        "fields" : { }
                      },
                      "bGrooming" : {
                        "type" : "document",
                        "dynamic" : {
                          "typeSet" : "TypeSet4"
                        },
                        "fields" : { }
                      }
                    }
                  },
                  "bFriends" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : {
                      "typeSet" : "TypeSet5"
                    },
                    "fields" : { }
                  }
                }
              },
              "typeSets" : [{
                  "name" : "TypeSet1",
                  "types" : [{
                      "type" : "uuid"
                    }]
                }, {
                  "name" : "TypeSet2",
                  "types" : [{
                      "type" : "uuid"
                    }]
                }, {
                  "name" : "TypeSet3",
                  "types" : [{
                      "type" : "uuid"
                    }]
                }, {
                  "name" : "TypeSet4",
                  "types" : [{
                      "type" : "uuid"
                    }]
                }, {
                  "name" : "TypeSet5",
                  "types" : [{
                      "type" : "uuid"
                    }]
                }]
            }
            """);
    }

    [AtlasTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Create_dynamic_search_indexes_with_exclusions(bool nestedBuilders, bool strings)
    {
        var collection = database.CreateCollection<Cat>(values: [nestedBuilders, strings]);

        Action<ModelBuilder> modelBuilderAction = nestedBuilders
            ? strings
                ? b => b.Entity(typeof(Cat)).HasSearchIndex(b =>
                {
                    b.IsDynamic()
                        .ExcludeFromIndex("Name")
                        .ExcludeFromIndex("Breed")
                        .ExcludeFromIndex("Comments")
                        .ExcludeFromIndex("Current")
                        .IndexAsEmbedded("Coat", b =>
                        {
                            b.IsDynamic()
                                .ExcludeFromIndex("Short")
                                .ExcludeFromIndex("Comments")
                                .IndexAsEmbeddedArray("Colors", b =>
                                {
                                    b.IsDynamic()
                                        .ExcludeFromIndex("Color")
                                        .ExcludeFromIndex("Comments")
                                        .ExcludeFromIndex("Percent");
                                })
                                .IndexAsEmbedded("Grooming", b =>
                                {
                                    b.IsDynamic()
                                        .ExcludeFromIndex("Comments")
                                        .ExcludeFromIndex("Date")
                                        .ExcludeFromIndex("Duration")
                                        .ExcludeFromIndex("Tangles");
                                });
                        })
                        .IndexAsEmbeddedArray("Friends", b =>
                        {
                            b.IsDynamic()
                                .ExcludeFromIndex("Name");
                        });
                })
                : b => b.Entity<Cat>().HasSearchIndex(b =>
                {
                    b.IsDynamic()
                        .ExcludeFromIndex(e => e.Name)
                        .ExcludeFromIndex(e => e.Breed)
                        .ExcludeFromIndex(e => e.Comments)
                        .ExcludeFromIndex(e => e.Current)
                        .IndexAsEmbedded(e => e.Coat, b =>
                        {
                            b.IsDynamic()
                                .ExcludeFromIndex(e => e.Short)
                                .ExcludeFromIndex(e => e.Comments)
                                .IndexAsEmbeddedArray(e => e.Colors, b =>
                                {
                                    b.IsDynamic()
                                        .ExcludeFromIndex(e => e.Comments)
                                        .ExcludeFromIndex(e => e.Percent)
                                        .ExcludeFromIndex(e => e.Color);
                                })
                                .IndexAsEmbedded(e => e.Grooming, b =>
                                {
                                    b.IsDynamic()
                                        .ExcludeFromIndex(e => e.Comments)
                                        .ExcludeFromIndex(e => e.Date)
                                        .ExcludeFromIndex(e => e.Duration)
                                        .ExcludeFromIndex(e => e.Tangles);
                                });
                        })
                        .IndexAsEmbeddedArray(e => e.Friends, b =>
                        {
                            b.IsDynamic()
                                .ExcludeFromIndex(e => e.Name);
                        });
                })
            : strings
                ? b => b.Entity(typeof(Cat), b =>
                {
                    b.HasSearchIndex()
                        .IsDynamic()
                        .ExcludeFromIndex("Name")
                        .ExcludeFromIndex("Breed")
                        .ExcludeFromIndex("Comments")
                        .ExcludeFromIndex("Current");
                    b.HasSearchIndex().IndexAsEmbedded("Coat")
                        .IsDynamic()
                        .ExcludeFromIndex("Short")
                        .ExcludeFromIndex("Comments");
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbeddedArray("Colors")
                        .IsDynamic()
                        .ExcludeFromIndex("Color")
                        .ExcludeFromIndex("Comments")
                        .ExcludeFromIndex("Percent");
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbedded("Grooming")
                        .IsDynamic()
                        .ExcludeFromIndex("Comments")
                        .ExcludeFromIndex("Date")
                        .ExcludeFromIndex("Duration")
                        .ExcludeFromIndex("Tangles");
                    b.HasSearchIndex().IndexAsEmbeddedArray("Friends")
                        .IsDynamic()
                        .ExcludeFromIndex("Name");
                })
                : b => b.Entity<Cat>(b =>
                {
                    b.HasSearchIndex()
                        .IsDynamic()
                        .ExcludeFromIndex(e => e.Name)
                        .ExcludeFromIndex(e => e.Breed)
                        .ExcludeFromIndex(e => e.Comments)
                        .ExcludeFromIndex(e => e.Current);
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat)
                        .IsDynamic()
                        .ExcludeFromIndex(e => e.Short)
                        .ExcludeFromIndex(e => e.Comments);
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbeddedArray(e => e.Colors)
                        .IsDynamic()
                        .ExcludeFromIndex(e => e.Color)
                        .ExcludeFromIndex(e => e.Comments)
                        .ExcludeFromIndex(e => e.Percent);
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbedded(e => e.Grooming)
                        .IsDynamic()
                        .ExcludeFromIndex(e => e.Comments)
                        .ExcludeFromIndex(e => e.Date)
                        .ExcludeFromIndex(e => e.Duration)
                        .ExcludeFromIndex(e => e.Tangles);
                    b.HasSearchIndex().IndexAsEmbeddedArray(e => e.Friends)
                        .IsDynamic()
                        .ExcludeFromIndex(e => e.Name);
                });

        await using var db = SingleEntityDbContext.Create(collection, modelBuilderAction);
        await db.Database.EnsureCreatedAsync();

        ValidateIndex(database, collection.CollectionNamespace, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : true,
                "fields" : {
                  "bBreed" : [],
                  "bCurrent" : [],
                  "bComments" : [],
                  "bName" : [],
                  "bCoat" : {
                    "type" : "document",
                    "dynamic" : true,
                    "fields" : {
                      "bColors" : {
                        "type" : "embeddedDocuments",
                        "dynamic" : true,
                        "fields" : {
                          "bPercent" : [],
                          "bbbComments" : [],
                          "bColor" : []
                        }
                      },
                      "bbComments" : [],
                      "bShort" : [],
                      "bGrooming" : {
                        "type" : "document",
                        "dynamic" : true,
                        "fields" : {
                          "bTangles" : [],
                          "bDate" : [],
                          "bDuration" : [],
                          "bbbbComments" : []
                        }
                      }
                    }
                  },
                  "bFriends" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : true,
                    "fields" : {
                      "bName" : []
                    }
                  }
                }
              }
            }
            """);
    }

    [AtlasTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Create_static_search_index_with_top_level_options(bool nestedBuilders, bool strings)
    {
        var collection = database.CreateCollection<Cat>(values: [nestedBuilders, strings]);

        Action<ModelBuilder> modelBuilderAction = nestedBuilders
            ? strings
                ? b => b.Entity(typeof(Cat)).HasSearchIndex(b =>
                {
                    b.HasPartitions(4)
                        .UseAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace)
                        .UseSearchAnalyzer("lucene.simple")
                        .IndexAsEmbedded("Coat", b =>
                        {
                            b.IndexAsEmbeddedArray("Colors", b => { })
                                .IndexAsEmbedded("Grooming", b => { });
                        })
                        .IndexAsEmbeddedArray("Friends", b => { });
                })
                : b => b.Entity<Cat>().HasSearchIndex(b =>
                {
                    b.UseAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace)
                        .HasPartitions(4)
                        .UseSearchAnalyzer("lucene.simple")
                        .IndexAsEmbedded(e => e.Coat, b =>
                        {
                            b.IndexAsEmbeddedArray(e => e.Colors, b => { })
                                .IndexAsEmbedded(e => e.Grooming, b => { });
                        })
                        .IndexAsEmbeddedArray(e => e.Friends, b => { });
                })
            : strings
                ? b => b.Entity(typeof(Cat), b =>
                {
                    b.HasSearchIndex()
                        .UseAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace)
                        .UseSearchAnalyzer("lucene.simple")
                        .HasPartitions(4);

                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbeddedArray("Colors");
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbedded("Grooming");
                    b.HasSearchIndex().IndexAsEmbeddedArray("Friends");
                })
                : b => b.Entity<Cat>(b =>
                {
                    b.HasSearchIndex()
                        .HasPartitions(4)
                        .UseAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace)
                        .UseSearchAnalyzer("lucene.simple");

                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbeddedArray(e => e.Colors);
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbedded(e => e.Grooming);
                    b.HasSearchIndex().IndexAsEmbeddedArray(e => e.Friends);
                });

        await using var db = SingleEntityDbContext.Create(collection, modelBuilderAction);
        await db.Database.EnsureCreatedAsync();

        ValidateIndex(database, collection.CollectionNamespace, expectedDocument:
            """
            {
              "analyzer" : "lucene.whitespace",
              "searchAnalyzer" : "lucene.simple",
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "bCoat" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "bColors" : {
                        "type" : "embeddedDocuments",
                        "dynamic" : false,
                        "fields" : { }
                      },
                      "bGrooming" : {
                        "type" : "document",
                        "dynamic" : false,
                        "fields" : { }
                      }
                    }
                  },
                  "bFriends" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : false,
                    "fields" : { }
                  }
                }
              },
              "numPartitions" : 4
            }
            """);
    }

    [AtlasTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Create_dynamic_search_index_with_top_level_options(bool nestedBuilders, bool strings)
    {
        var collection = database.CreateCollection<Cat>(values: [nestedBuilders, strings]);

        Action<ModelBuilder> modelBuilderAction = nestedBuilders
            ? strings
                ? b => b.Entity(typeof(Cat)).HasSearchIndex(b =>
                {
                    b.IsDynamic()
                        .HasPartitions(4)
                        .UseAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace)
                        .UseSearchAnalyzer("lucene.simple")
                        .IndexAsEmbedded("Coat", b =>
                        {
                            b.IsDynamic().IndexAsEmbeddedArray("Colors", b => { b.IsDynamic(); })
                                .IndexAsEmbedded("Grooming", b => { b.IsDynamic(); });
                        })
                        .IndexAsEmbeddedArray("Friends", b => { b.IsDynamic(); });
                })
                : b => b.Entity<Cat>().HasSearchIndex(b =>
                {
                    b.IsDynamic()
                        .HasPartitions(4)
                        .UseAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace)
                        .UseSearchAnalyzer("lucene.simple")
                        .IndexAsEmbedded(e => e.Coat, b =>
                        {
                            b.IsDynamic().IndexAsEmbeddedArray(e => e.Colors, b => { b.IsDynamic(); })
                                .IndexAsEmbedded(e => e.Grooming, b => { b.IsDynamic(); });
                        })
                        .IndexAsEmbeddedArray(e => e.Friends, b => { b.IsDynamic(); });
                })
            : strings
                ? b => b.Entity(typeof(Cat), b =>
                {
                    b.HasSearchIndex().IsDynamic()
                        .HasPartitions(4)
                        .UseAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace)
                        .UseSearchAnalyzer("lucene.simple");

                    b.HasSearchIndex().IndexAsEmbedded("Coat").IsDynamic();
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbeddedArray("Colors").IsDynamic();
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbedded("Grooming").IsDynamic();
                    b.HasSearchIndex().IndexAsEmbeddedArray("Friends").IsDynamic();
                })
                : b => b.Entity<Cat>(b =>
                {
                    b.HasSearchIndex().IsDynamic()
                        .UseSearchAnalyzer("lucene.simple")
                        .HasPartitions(4)
                        .UseAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace);

                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IsDynamic();
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbeddedArray(e => e.Colors).IsDynamic();
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbedded(e => e.Grooming).IsDynamic();
                    b.HasSearchIndex().IndexAsEmbeddedArray(e => e.Friends).IsDynamic();
                });

        await using var db = SingleEntityDbContext.Create(collection, modelBuilderAction);
        await db.Database.EnsureCreatedAsync();

        ValidateIndex(database, collection.CollectionNamespace, expectedDocument:
            """
            {
              "analyzer" : "lucene.whitespace",
              "searchAnalyzer" : "lucene.simple",
              "mappings" : {
                "dynamic" : true,
                "fields" : {
                  "bCoat" : {
                    "type" : "document",
                    "dynamic" : true,
                    "fields" : {
                      "bColors" : {
                        "type" : "embeddedDocuments",
                        "dynamic" : true,
                        "fields" : { }
                      },
                      "bGrooming" : {
                        "type" : "document",
                        "dynamic" : true,
                        "fields" : { }
                      }
                    }
                  },
                  "bFriends" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : true,
                    "fields" : { }
                  }
                }
              },
              "numPartitions" : 4
            }
            """);
    }

    [AtlasTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Create_static_search_index_with_custom_analyzers_and_synonyms(bool nestedBuilders, bool strings)
    {
        var collection = database.CreateCollection<Cat>(values: [nestedBuilders, strings]);

        Action<ModelBuilder> modelBuilderAction = nestedBuilders
            ? strings
                ? b => b.Entity(typeof(Cat)).HasSearchIndex(b =>
                {
                    b.HasPartitions(4)
                        .UseAnalyzer("CustomAnalyzer1")
                        .UseSearchAnalyzer("CustomAnalyzer2")
                        .IndexAsEmbedded("Coat", b =>
                        {
                            b.IndexAsEmbeddedArray("Colors", b => { })
                                .IndexAsEmbedded("Grooming", b => { });
                        })
                        .IndexAsEmbeddedArray("Friends", b => { })
                        .AddCustomAnalyzer("CustomAnalyzer1", b =>
                        {
                            b.UseKeywordTokenizer()
                                .WithTokenFilters(b =>
                                {
                                    b.AddAsciiFoldingFilter(true)
                                        .AddDaitchMokotoffSoundexFilter(false)
                                        .AddEdgeGramFilter(2, 4, true)
                                        .AddEnglishPossessiveFilter()
                                        .AddFlattenGraphFilter()
                                        .AddIcuFoldingFilter()
                                        .AddIcuNormalizerFilter(IcuNormalizationForm.Nfkc)
                                        .AddKeywordRepeatFilter()
                                        .AddKStemmingFilter()
                                        .AddLengthFilter(10, 20)
                                        .AddLowercaseFilter()
                                        .AddNGramFilter(3, 9, false)
                                        .AddPorterStemmingFilter()
                                        .AddRegexFilter(@"^(?!\$)\w+", "", RegexTokenFilterMatches.All)
                                        .AddRemoveDuplicatesFilter()
                                        .AddReverseFilter()
                                        .AddShingleFilter(11, 22)
                                        .AddSnowballStemmingFilter(SnowballStemmerName.Catalan)
                                        .AddSpanishPluralStemmingFilter()
                                        .AddStempelFilter()
                                        .AddStopWordFilter(["Yes", "No"], false)
                                        .AddTrimFilter()
                                        .AddWordDelimiterGraphFilter(
                                            new WordDelimiterOptions
                                            {
                                                ConcatenateAll = false,
                                                ConcatenateWords = true,
                                                ConcatenateNumbers = true,
                                                GenerateNumberParts = true,
                                                GenerateWordParts = true,
                                                PreserveOriginal = true,
                                                SplitOnCaseChange = true,
                                                SplitOnNumerics = true,
                                                StemEnglishPossessive = true,
                                                IgnoreKeywords = true,
                                                IgnoreCaseForProtectedWords = true
                                            }, ["Aa", "Bb"]);
                                })
                                .WithCharacterFilters(b =>
                                {
                                    b.AddIcuNormalizeFilter();
                                    b.AddMappingFilter([("-", ""), (".", ""), ("(", ""), (")", "")]);
                                    b.AddPersianFilter();
                                    b.AddHtmlStripFilter(["a"]);
                                });
                        })
                        .AddCustomAnalyzer("CustomAnalyzer2", b => { b.UseEdgeGramTokenizer(2, 7); })
                        .AddCustomAnalyzer("CustomAnalyzer3", b => { b.UseNGramTokenizer(2, 7); })
                        .AddCustomAnalyzer("CustomAnalyzer4",
                            b => { b.UseRegexCaptureGroupTokenizer(@"^\\b\\d{3}[-]?\\d{3}[-]?\\d{4}\\b$", 0); })
                        .AddCustomAnalyzer("CustomAnalyzer5", b => { b.UseRegexSplitTokenizer("[-. ]+"); })
                        .AddCustomAnalyzer("CustomAnalyzer6", b => { b.UseStandardTokenizer(100); })
                        .AddCustomAnalyzer("CustomAnalyzer7", b => { b.UseUaxUrlEmailTokenizer(100); })
                        .AddCustomAnalyzer("CustomAnalyzer8", b => { b.UseWhitespaceTokenizer(100); })
                        .AddSynonyms("Synonyms1", BuiltInSearchAnalyzer.LuceneStandard, "MySynonyms1")
                        .AddSynonyms("Synonyms2", "lucene.standard", "MySynonyms2")
                        .StoreAllSource();
                })
                : b => b.Entity<Cat>().HasSearchIndex(b =>
                {
                    b.UseAnalyzer("CustomAnalyzer1")
                        .HasPartitions(4)
                        .UseSearchAnalyzer("CustomAnalyzer2")
                        .IndexAsEmbedded(e => e.Coat, b =>
                        {
                            b.IndexAsEmbeddedArray(e => e.Colors, b => { })
                                .IndexAsEmbedded(e => e.Grooming, b => { });
                        })
                        .IndexAsEmbeddedArray(e => e.Friends, b => { })
                        .AddCustomAnalyzer("CustomAnalyzer1", b =>
                        {
                            b.UseKeywordTokenizer()
                                .WithTokenFilters(b =>
                                {
                                    b.AddAsciiFoldingFilter(true)
                                        .AddDaitchMokotoffSoundexFilter(false)
                                        .AddEdgeGramFilter(2, 4, true)
                                        .AddEnglishPossessiveFilter()
                                        .AddFlattenGraphFilter()
                                        .AddIcuFoldingFilter()
                                        .AddIcuNormalizerFilter(IcuNormalizationForm.Nfkc)
                                        .AddKeywordRepeatFilter()
                                        .AddKStemmingFilter()
                                        .AddLengthFilter(10, 20)
                                        .AddLowercaseFilter()
                                        .AddNGramFilter(3, 9, false)
                                        .AddPorterStemmingFilter()
                                        .AddRegexFilter(@"^(?!\$)\w+", "", RegexTokenFilterMatches.All)
                                        .AddRemoveDuplicatesFilter()
                                        .AddReverseFilter()
                                        .AddShingleFilter(11, 22)
                                        .AddSnowballStemmingFilter(SnowballStemmerName.Catalan)
                                        .AddSpanishPluralStemmingFilter()
                                        .AddStempelFilter()
                                        .AddStopWordFilter(["Yes", "No"], false)
                                        .AddTrimFilter()
                                        .AddWordDelimiterGraphFilter(
                                            new WordDelimiterOptions
                                            {
                                                ConcatenateAll = false,
                                                ConcatenateWords = true,
                                                ConcatenateNumbers = true,
                                                GenerateNumberParts = true,
                                                GenerateWordParts = true,
                                                PreserveOriginal = true,
                                                SplitOnCaseChange = true,
                                                SplitOnNumerics = true,
                                                StemEnglishPossessive = true,
                                                IgnoreKeywords = true,
                                                IgnoreCaseForProtectedWords = true
                                            }, ["Aa", "Bb"]);
                                })
                                .WithCharacterFilters(b =>
                                {
                                    b.AddIcuNormalizeFilter()
                                        .AddMappingFilter([("-", ""), (".", ""), ("(", ""), (")", "")])
                                        .AddPersianFilter()
                                        .AddHtmlStripFilter(["a"]);
                                });
                        })
                        .AddCustomAnalyzer("CustomAnalyzer2", b => { b.UseEdgeGramTokenizer(2, 7); })
                        .AddCustomAnalyzer("CustomAnalyzer3", b => { b.UseNGramTokenizer(2, 7); })
                        .AddCustomAnalyzer("CustomAnalyzer4",
                            b => { b.UseRegexCaptureGroupTokenizer(@"^\\b\\d{3}[-]?\\d{3}[-]?\\d{4}\\b$", 0); })
                        .AddCustomAnalyzer("CustomAnalyzer5", b => { b.UseRegexSplitTokenizer("[-. ]+"); })
                        .AddCustomAnalyzer("CustomAnalyzer6", b => { b.UseStandardTokenizer(100); })
                        .AddCustomAnalyzer("CustomAnalyzer7", b => { b.UseUaxUrlEmailTokenizer(100); })
                        .AddCustomAnalyzer("CustomAnalyzer8", b => { b.UseWhitespaceTokenizer(100); })
                        .AddSynonyms("Synonyms1", "lucene.standard", "MySynonyms1")
                        .AddSynonyms("Synonyms2", "lucene.standard", "MySynonyms2")
                        .StoreAllSource();
                })
            : strings
                ? b => b.Entity(typeof(Cat), b =>
                {
                    b.HasSearchIndex().AddCustomAnalyzer("CustomAnalyzer1").UseKeywordTokenizer().WithCharacterFilters()
                        .AddIcuNormalizeFilter()
                        .AddMappingFilter([("-", ""), (".", ""), ("(", ""), (")", "")])
                        .AddPersianFilter().AddHtmlStripFilter(["a"]);
                    b.HasSearchIndex().AddCustomAnalyzer("CustomAnalyzer1").UseKeywordTokenizer().WithTokenFilters(b =>
                    {
                        b.AddAsciiFoldingFilter(true)
                            .AddDaitchMokotoffSoundexFilter(false)
                            .AddEdgeGramFilter(2, 4, true)
                            .AddEnglishPossessiveFilter()
                            .AddFlattenGraphFilter()
                            .AddIcuFoldingFilter()
                            .AddIcuNormalizerFilter(IcuNormalizationForm.Nfkc)
                            .AddKeywordRepeatFilter()
                            .AddKStemmingFilter()
                            .AddLengthFilter(10, 20)
                            .AddLowercaseFilter()
                            .AddNGramFilter(3, 9, false)
                            .AddPorterStemmingFilter()
                            .AddRegexFilter(@"^(?!\$)\w+", "", RegexTokenFilterMatches.All)
                            .AddRemoveDuplicatesFilter()
                            .AddReverseFilter()
                            .AddShingleFilter(11, 22)
                            .AddSnowballStemmingFilter(SnowballStemmerName.Catalan)
                            .AddSpanishPluralStemmingFilter()
                            .AddStempelFilter()
                            .AddStopWordFilter(["Yes", "No"], false)
                            .AddTrimFilter()
                            .AddWordDelimiterGraphFilter(
                                new WordDelimiterOptions
                                {
                                    ConcatenateAll = false,
                                    ConcatenateWords = true,
                                    ConcatenateNumbers = true,
                                    GenerateNumberParts = true,
                                    GenerateWordParts = true,
                                    PreserveOriginal = true,
                                    SplitOnCaseChange = true,
                                    SplitOnNumerics = true,
                                    StemEnglishPossessive = true,
                                    IgnoreKeywords = true,
                                    IgnoreCaseForProtectedWords = true
                                }, ["Aa", "Bb"]);
                    });
                    b.HasSearchIndex().AddCustomAnalyzer("CustomAnalyzer2").UseEdgeGramTokenizer(2, 7);
                    b.HasSearchIndex().AddCustomAnalyzer("CustomAnalyzer3").UseNGramTokenizer(2, 7);
                    b.HasSearchIndex().AddCustomAnalyzer("CustomAnalyzer4")
                        .UseRegexCaptureGroupTokenizer(@"^\\b\\d{3}[-]?\\d{3}[-]?\\d{4}\\b$", 0);
                    b.HasSearchIndex().AddCustomAnalyzer("CustomAnalyzer5").UseRegexSplitTokenizer("[-. ]+");
                    b.HasSearchIndex().AddCustomAnalyzer("CustomAnalyzer6", b => { b.UseStandardTokenizer(100); });
                    b.HasSearchIndex().AddCustomAnalyzer("CustomAnalyzer7", b => { b.UseUaxUrlEmailTokenizer(100); });
                    b.HasSearchIndex().AddCustomAnalyzer("CustomAnalyzer8", b => { b.UseWhitespaceTokenizer(100); });

                    b.HasSearchIndex()
                        .UseAnalyzer("CustomAnalyzer1")
                        .UseSearchAnalyzer("CustomAnalyzer2")
                        .AddSynonyms("Synonyms1", BuiltInSearchAnalyzer.LuceneStandard, "MySynonyms1")
                        .AddSynonyms("Synonyms2", "lucene.standard", "MySynonyms2")
                        .HasPartitions(4)
                        .StoreAllSource();

                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbeddedArray("Colors");
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbedded("Grooming");
                    b.HasSearchIndex().IndexAsEmbeddedArray("Friends");
                })
                : b => b.Entity<Cat>(b =>
                {
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbeddedArray(e => e.Colors);
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbedded(e => e.Grooming);
                    b.HasSearchIndex().IndexAsEmbeddedArray(e => e.Friends);

                    b.HasSearchIndex()
                        .HasPartitions(4)
                        .UseAnalyzer("CustomAnalyzer1")
                        .AddSynonyms("Synonyms1", "lucene.standard", "MySynonyms1")
                        .AddSynonyms("Synonyms2", "lucene.standard", "MySynonyms2")
                        .UseSearchAnalyzer("CustomAnalyzer2")
                        .StoreAllSource();

                    b.HasSearchIndex().AddCustomAnalyzer("CustomAnalyzer1").UseKeywordTokenizer().WithCharacterFilters()
                        .AddIcuNormalizeFilter()
                        .AddMappingFilter([("-", ""), (".", ""), ("(", ""), (")", "")])
                        .AddPersianFilter().AddHtmlStripFilter(["a"]);
                    b.HasSearchIndex().AddCustomAnalyzer("CustomAnalyzer1").UseKeywordTokenizer().WithTokenFilters(b =>
                    {
                        b.AddAsciiFoldingFilter(true)
                            .AddDaitchMokotoffSoundexFilter(false)
                            .AddEdgeGramFilter(2, 4, true)
                            .AddEnglishPossessiveFilter()
                            .AddFlattenGraphFilter()
                            .AddIcuFoldingFilter()
                            .AddIcuNormalizerFilter(IcuNormalizationForm.Nfkc)
                            .AddKeywordRepeatFilter()
                            .AddKStemmingFilter()
                            .AddLengthFilter(10, 20)
                            .AddLowercaseFilter()
                            .AddNGramFilter(3, 9, false)
                            .AddPorterStemmingFilter()
                            .AddRegexFilter(@"^(?!\$)\w+", "", RegexTokenFilterMatches.All)
                            .AddRemoveDuplicatesFilter()
                            .AddReverseFilter()
                            .AddShingleFilter(11, 22)
                            .AddSnowballStemmingFilter(SnowballStemmerName.Catalan)
                            .AddSpanishPluralStemmingFilter()
                            .AddStempelFilter()
                            .AddStopWordFilter(["Yes", "No"], false)
                            .AddTrimFilter()
                            .AddWordDelimiterGraphFilter(
                                new WordDelimiterOptions
                                {
                                    ConcatenateAll = false,
                                    ConcatenateWords = true,
                                    ConcatenateNumbers = true,
                                    GenerateNumberParts = true,
                                    GenerateWordParts = true,
                                    PreserveOriginal = true,
                                    SplitOnCaseChange = true,
                                    SplitOnNumerics = true,
                                    StemEnglishPossessive = true,
                                    IgnoreKeywords = true,
                                    IgnoreCaseForProtectedWords = true
                                }, ["Aa", "Bb"]);
                    });
                    b.HasSearchIndex().AddCustomAnalyzer("CustomAnalyzer2").UseEdgeGramTokenizer(2, 7);
                    b.HasSearchIndex().AddCustomAnalyzer("CustomAnalyzer3").UseNGramTokenizer(2, 7);
                    b.HasSearchIndex().AddCustomAnalyzer("CustomAnalyzer4")
                        .UseRegexCaptureGroupTokenizer(@"^\\b\\d{3}[-]?\\d{3}[-]?\\d{4}\\b$", 0);
                    b.HasSearchIndex().AddCustomAnalyzer("CustomAnalyzer5").UseRegexSplitTokenizer("[-. ]+");
                    b.HasSearchIndex().AddCustomAnalyzer("CustomAnalyzer6", b => { b.UseStandardTokenizer(100); });
                    b.HasSearchIndex().AddCustomAnalyzer("CustomAnalyzer7", b => { b.UseUaxUrlEmailTokenizer(100); });
                    b.HasSearchIndex().AddCustomAnalyzer("CustomAnalyzer8", b => { b.UseWhitespaceTokenizer(100); });
                });

        await using var db = SingleEntityDbContext.Create(collection, modelBuilderAction);
        await db.Database.EnsureCreatedAsync();

        ValidateIndex(database, collection.CollectionNamespace, expectedDocument:
            """
            {
              "analyzer" : "CustomAnalyzer1",
              "searchAnalyzer" : "CustomAnalyzer2",
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "bCoat" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "bColors" : {
                        "type" : "embeddedDocuments",
                        "dynamic" : false,
                        "fields" : { }
                      },
                      "bGrooming" : {
                        "type" : "document",
                        "dynamic" : false,
                        "fields" : { }
                      }
                    }
                  },
                  "bFriends" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : false,
                    "fields" : { }
                  }
                }
              },
              "analyzers" : [{
                  "name" : "CustomAnalyzer1",
                  "charFilters" : [{
                      "type" : "icuNormalize"
                    }, {
                      "type" : "mapping",
                      "mappings" : {
                        "(" : "",
                        ")" : "",
                        "-" : "",
                        "." : ""
                      }
                    }, {
                      "type" : "persian"
                    }, {
                      "type" : "htmlStrip",
                      "ignoredTags" : ["a"]
                    }],
                  "tokenizer" : {
                    "type" : "keyword"
                  },
                  "tokenFilters" : [{
                      "type" : "asciiFolding",
                      "originalTokens" : "include"
                    }, {
                      "type" : "daitchMokotoffSoundex",
                      "originalTokens" : "omit"
                    }, {
                      "type" : "edgeGram",
                      "minGram" : 2,
                      "maxGram" : 4,
                      "termNotInBounds" : "include"
                    }, {
                      "type" : "englishPossessive"
                    }, {
                      "type" : "flattenGraph"
                    }, {
                      "type" : "icuFolding"
                    }, {
                      "type" : "icuNormalizer",
                      "normalizationForm" : "nfkc"
                    }, {
                      "type" : "keywordRepeat"
                    }, {
                      "type" : "kStemming"
                    }, {
                      "type" : "length",
                      "min" : 10,
                      "max" : 20
                    }, {
                      "type" : "lowercase"
                    }, {
                      "type" : "nGram",
                      "minGram" : 3,
                      "maxGram" : 9,
                      "termNotInBounds" : "omit"
                    }, {
                      "type" : "porterStemming"
                    }, {
                      "type" : "regex",
                      "pattern" : "^(?!\\$)\\w+",
                      "replacement" : "",
                      "matches" : "all"
                    }, {
                      "type" : "removeDuplicates"
                    }, {
                      "type" : "reverse"
                    }, {
                      "type" : "shingle",
                      "minShingleSize" : 11,
                      "maxShingleSize" : 22
                    }, {
                      "type" : "snowballStemming",
                      "stemmerName" : "catalan"
                    }, {
                      "type" : "spanishPluralStemming"
                    }, {
                      "type" : "stempel"
                    }, {
                      "type" : "stopword",
                      "tokens" : ["Yes", "No"],
                      "ignoreCase" : false
                    }, {
                      "type" : "trim"
                    }, {
                      "type" : "wordDelimiterGraph",
                      "protectedWords" : {
                        "words" : ["Aa", "Bb"],
                        "ignoreCase" : true
                      },
                      "delimiterOptions" : {
                        "generateWordParts" : true,
                        "generateNumberParts" : true,
                        "concatenateWords" : true,
                        "concatenateNumbers" : true,
                        "concatenateAll" : false,
                        "preserveOriginal" : true,
                        "splitOnCaseChange" : true,
                        "splitOnNumerics" : true,
                        "stemEnglishPossessive" : true,
                        "ignoreKeywords" : true
                      }
                    }]
                }, {
                  "name" : "CustomAnalyzer2",
                  "tokenizer" : {
                    "type" : "edgeGram",
                    "minGram" : 2,
                    "maxGram" : 7
                  }
                }, {
                  "name" : "CustomAnalyzer3",
                  "tokenizer" : {
                    "type" : "nGram",
                    "minGram" : 2,
                    "maxGram" : 7
                  }
                }, {
                  "name" : "CustomAnalyzer4",
                  "tokenizer" : {
                    "type" : "regexCaptureGroup",
                    "pattern" : "^\\\\b\\\\d{3}[-]?\\\\d{3}[-]?\\\\d{4}\\\\b$",
                    "group" : 0
                  }
                }, {
                  "name" : "CustomAnalyzer5",
                  "tokenizer" : {
                    "type" : "regexSplit",
                    "pattern" : "[-. ]+"
                  }
                }, {
                  "name" : "CustomAnalyzer6",
                  "tokenizer" : {
                    "type" : "standard",
                    "maxTokenLength" : 100
                  }
                }, {
                  "name" : "CustomAnalyzer7",
                  "tokenizer" : {
                    "type" : "uaxUrlEmail",
                    "maxTokenLength" : 100
                  }
                }, {
                  "name" : "CustomAnalyzer8",
                  "tokenizer" : {
                    "type" : "whitespace",
                    "maxTokenLength" : 100
                  }
                }],
              "storedSource" : true,
              "synonyms" : [{
                  "name" : "Synonyms1",
                  "source" : {
                    "collection" : "MySynonyms1"
                  },
                  "analyzer" : "lucene.standard"
                }, {
                  "name" : "Synonyms2",
                  "source" : {
                    "collection" : "MySynonyms2"
                  },
                  "analyzer" : "lucene.standard"
                }],
              "numPartitions" : 4
            }
            """);
    }

    [AtlasTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Create_static_search_index_with_included_stored_source_fields(bool nestedBuilders, bool strings)
    {
        var collection = database.CreateCollection<Cat>(values: [nestedBuilders, strings]);

        await StoredSourceTest(nestedBuilders, strings, collection, store: true);

        ValidateIndex(database, collection.CollectionNamespace, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "bCoat" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "bColors" : {
                        "type" : "embeddedDocuments",
                        "dynamic" : false,
                        "fields" : { },
                        "storedSource" : {
                          "include" : ["bColor", "bPercent", "bbbComments"]
                        }
                      },
                      "bGrooming" : {
                        "type" : "document",
                        "dynamic" : false,
                        "fields" : { }
                      }
                    }
                  },
                  "bFriends" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : false,
                    "fields" : { },
                    "storedSource" : {
                      "include" : ["bName"]
                    }
                  }
                }
              },
              "storedSource" : {
                "include" : ["bBreed", "bCoat.bGrooming.bDate", "bCoat.bGrooming.bDuration", "bCoat.bGrooming.bGroomerName", "bCoat.bGrooming.bTangles", "bCoat.bGrooming.bbbbComments", "bCoat.bShort", "bCoat.bbComments", "bCurrent", "bName"]
              }
            }
            """);
    }

    [AtlasTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Create_static_search_index_with_excluded_stored_source_fields(bool nestedBuilders, bool strings)
    {
        var collection = database.CreateCollection<Cat>(values: [nestedBuilders, strings]);

        await StoredSourceTest(nestedBuilders, strings, collection, store: false);

        ValidateIndex(database, collection.CollectionNamespace, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "bCoat" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "bColors" : {
                        "type" : "embeddedDocuments",
                        "dynamic" : false,
                        "fields" : { },
                        "storedSource" : {
                          "exclude" : ["bColor", "bPercent", "bbbComments"]
                        }
                      },
                      "bGrooming" : {
                        "type" : "document",
                        "dynamic" : false,
                        "fields" : { }
                      }
                    }
                  },
                  "bFriends" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : false,
                    "fields" : { },
                    "storedSource" : {
                      "exclude" : ["bName"]
                    }
                  }
                }
              },
              "storedSource" : {
                "exclude" : ["bBreed", "bCoat.bGrooming.bDate", "bCoat.bGrooming.bDuration", "bCoat.bGrooming.bGroomerName", "bCoat.bGrooming.bTangles", "bCoat.bGrooming.bbbbComments", "bCoat.bShort", "bCoat.bbComments", "bCurrent", "bName"]
              }
            }
            """);
    }

    private static async Task StoredSourceTest(bool nestedBuilders, bool strings, IMongoCollection<Cat> collection, bool store)
    {
        Action<ModelBuilder> modelBuilderAction = nestedBuilders
            ? strings
                ? b => b.Entity(typeof(Cat)).HasSearchIndex(b =>
                {
                    b.IndexAsEmbedded("Coat", b =>
                        {
                            b.IndexAsEmbeddedArray("Colors", b =>
                                {
                                    b.StoreSourceFor("Color", store)
                                        .StoreSourceFor("Comments", store)
                                        .StoreSourceFor("Percent", store);
                                })
                                .IndexAsEmbedded("Grooming", b =>
                                {
                                    b.StoreSourceFor("Comments", store)
                                        .StoreSourceFor("Date", store)
                                        .StoreSourceFor("Tangles", store)
                                        .StoreSourceFor("Duration", store)
                                        .StoreSourceFor("GroomerName", store);
                                })
                                .StoreSourceFor("Comments", store)
                                .StoreSourceFor("Short", store);
                        })
                        .IndexAsEmbeddedArray("Friends", b => { b.StoreSourceFor("Name", store); })
                        .StoreSourceFor("Name", store)
                        .StoreSourceFor("Breed", store)
                        .StoreSourceFor("Current", store);
                })
                : b => b.Entity<Cat>().HasSearchIndex(b =>
                {
                    b.IndexAsEmbedded(e => e.Coat, b =>
                        {
                            b.IndexAsEmbeddedArray(e => e.Colors, b =>
                                {
                                    b.StoreSourceFor(e => e.Color, store)
                                        .StoreSourceFor(e => e.Comments, store)
                                        .StoreSourceFor(e => e.Percent, store);
                                })
                                .IndexAsEmbedded(e => e.Grooming, b =>
                                {
                                    b.StoreSourceFor(e => e.Comments, store)
                                        .StoreSourceFor(e => e.Date, store)
                                        .StoreSourceFor(e => e.Tangles, store)
                                        .StoreSourceFor(e => e.Duration, store)
                                        .StoreSourceFor(e => e.GroomerName, store);
                                })
                                .StoreSourceFor(e => e.Comments, store)
                                .StoreSourceFor(e => e.Short, store);
                        })
                        .IndexAsEmbeddedArray(e => e.Friends, b => { b.StoreSourceFor(e => e.Name, store); })
                        .StoreSourceFor(e => e.Name, store)
                        .StoreSourceFor(e => e.Breed, store)
                        .StoreSourceFor(e => e.Current, store);
                })
            : strings
                ? b => b.Entity(typeof(Cat), b =>
                {
                    b.HasSearchIndex().IndexAsEmbedded("Coat")
                        .StoreSourceFor("Comments", store)
                        .StoreSourceFor("Short", store);
                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbeddedArray("Colors")
                        .StoreSourceFor("Color", store)
                        .StoreSourceFor("Comments", store)
                        .StoreSourceFor("Percent", store);

                    b.HasSearchIndex().IndexAsEmbedded("Coat").IndexAsEmbedded("Grooming")
                        .StoreSourceFor("Comments", store)
                        .StoreSourceFor("Date", store)
                        .StoreSourceFor("Tangles", store)
                        .StoreSourceFor("Duration", store)
                        .StoreSourceFor("GroomerName", store);
                    b.HasSearchIndex().IndexAsEmbeddedArray("Friends").StoreSourceFor("Name", store);
                    b.HasSearchIndex()
                        .StoreSourceFor("Name", store)
                        .StoreSourceFor("Breed", store)
                        .StoreSourceFor("Current", store);
                })
                : b => b.Entity<Cat>(b =>
                {
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat)
                        .StoreSourceFor(e => e.Comments, store)
                        .StoreSourceFor(e => e.Short, store);
                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbeddedArray(e => e.Colors)
                        .StoreSourceFor(e => e.Color, store)
                        .StoreSourceFor(e => e.Comments, store)
                        .StoreSourceFor(e => e.Percent, store);

                    b.HasSearchIndex().IndexAsEmbedded(e => e.Coat).IndexAsEmbedded(e => e.Grooming)
                        .StoreSourceFor(e => e.Comments, store)
                        .StoreSourceFor(e => e.Date, store)
                        .StoreSourceFor(e => e.Tangles, store)
                        .StoreSourceFor(e => e.Duration, store)
                        .StoreSourceFor(e => e.GroomerName, store);
                    b.HasSearchIndex().IndexAsEmbeddedArray(e => e.Friends).StoreSourceFor(e => e.Name, store);
                    b.HasSearchIndex()
                        .StoreSourceFor(e => e.Name, store)
                        .StoreSourceFor(e => e.Breed, store)
                        .StoreSourceFor(e => e.Current, store);

                });

        await using var db = SingleEntityDbContext.Create(collection, modelBuilderAction);
        await db.Database.EnsureCreatedAsync();
    }

    private int GetSearchIndexCount(IMongoCollection<Cat> collection)
        => database.GetCollection<BsonDocument>(collection.CollectionNamespace).SearchIndexes.List().ToList().Count;

    internal static BsonDocument ValidateIndex(AtlasTemporaryDatabaseFixture fixture,
        CollectionNamespace collectionNamespace,
        string indexName = "default",
        string? expectedDocument = null)
        => ValidateIndex(fixture.GetCollection<BsonDocument>(collectionNamespace), indexName, expectedDocument);

    internal static BsonDocument ValidateIndex(IMongoCollection<BsonDocument> collection,
        string indexName = "default",
        string? expectedDocument = null)
    {
        var indexes = collection.SearchIndexes.List().ToList();
        var index = indexes.Single(i => i["name"].AsString == indexName);

        Assert.Equal("search", index["type"].AsString);
        Assert.Equal("READY", index["status"].AsString);
        Assert.True(index["queryable"].AsBoolean);
        Assert.Equal(0, index["latestVersion"].AsInt32);

        if (expectedDocument != null)
        {
            var document = index["latestDefinition"].ToJson(new JsonWriterSettings { Indent = true });
            Assert.Equal(expectedDocument, document, ignoreLineEndingDifferences: true);
        }

        return index;
    }

    private class Cat
    {
        public ObjectId Id { get; set; }
        [BsonElement("bAlternateId")] public Guid AlternateId { get; set; }
        [BsonElement("bName")] public string Name { get; set; }
        [BsonElement("bBreed")] public string Breed { get; set; }
        [BsonElement("bComments")] public string[] Comments { get; set; }
        [BsonElement("bCurrent")] public bool Current { get; set; }
        [BsonElement("bCoat")] public Coat Coat { get; set; }
        [BsonElement("bFriends")] public List<Friend> Friends { get; set; }
        [BsonElement("bRating")] public double Rating { get; set; }
        [BsonElement("bCost")] public decimal Cost { get; set; }
        [BsonElement("bMoles")] public long Moles { get; set; }
    }

    private class Coat
    {
        [BsonElement("bShort")] public bool Short { get; set; }
        [BsonElement("bColors")] public ColorPart[] Colors { get; set; }
        [BsonElement("bGrooming")] public Grooming Grooming { get; set; }
        [BsonElement("bbComments")] public string[] Comments { get; set; }
    }

    private class ColorPart
    {
        [BsonElement("bColor")] public string Color { get; set; }
        [BsonElement("bPercent")] public int Percent { get; set; }
        [BsonElement("bbbComments")] public string[] Comments { get; set; }
    }

    private class Grooming
    {
        [BsonElement("bDate")] public DateTime Date { get; set; }
        [BsonElement("bTangles")] public bool Tangles { get; set; }
        [BsonElement("bDuration")] public double Duration { get; set; }
        [BsonElement("bGroomerName")] public string GroomerName { get; set; }
        [BsonElement("bbbbComments")] public List<string> Comments { get; set; }
        [BsonElement("bRating")] public double Rating { get; set; }
        [BsonElement("bCost")] public decimal Cost { get; set; }
        [BsonElement("bCatCode")] public ObjectId CatCode { get; set; }
        [BsonElement("bLegacyCatCode")] public Guid LegacyCatCode { get; set; }
    }

    private class Friend
    {
        [BsonElement("bName")] public string Name { get; set; }
        [BsonElement("bFeline")] public bool Feline { get; set; }
        [BsonElement("bBirthday")] public DateOnly Birthday { get; set; }
        [BsonElement("bRating")] public double Rating { get; set; }
        [BsonElement("bCost")] public decimal Cost { get; set; }
        [BsonElement("bMoles")] public long Moles { get; set; }
        [BsonElement("bCatCode")] public ObjectId CatCode { get; set; }
        [BsonElement("bLegacyCatCode")] public Guid LegacyCatCode { get; set; }
    }
}
