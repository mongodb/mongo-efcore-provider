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

using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MongoDB.Driver.Search;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Metadata.Search;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Storage;

[XUnitCollection("StorageTests")]
public class SearchIndexExamplesTests(AtlasTemporaryDatabaseFixture database)
    : IClassFixture<AtlasTemporaryDatabaseFixture>
{
    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/define-field-mappings/
    public async Task Enable_dynamic_mapping_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>().HasSearchIndex().IsDynamic();
        });

        var bsonCollection = await PrepareDatabase(db, [new() { Title = "Action Man" }, new() { Title = "G.I. Joe" }]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : true,
                "fields" : { }
              }
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("title", "Action"))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("Action Man", results.Select(e => e["title"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/define-field-mappings/
    public async Task Configure_dynamic_mapping_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>().HasSearchIndex(b =>
            {
                b.IsDynamicWithTypeSet("movieFieldTypes");

                b.AddTypeSet("movieFieldTypes", b =>
                {
                    b.IndexAsNumber();
                    b.IndexAsAutoComplete()
                        .WithMinGrams(4)
                        .WithMaxGrams(10)
                        .UseAnalyzer(BuiltInSearchAnalyzer.LuceneStandard);
                });
            });
        });

        var bsonCollection = await PrepareDatabase(db, [new() { Title = "Action Man" }, new() { Title = "G.I. Joe" }]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : {
                  "typeSet" : "movieFieldTypes"
                },
                "fields" : { }
              },
              "typeSets" : [{
                  "name" : "movieFieldTypes",
                  "types" : [{
                      "type" : "number",
                      "representation" : "double",
                      "indexDoubles" : true,
                      "indexIntegers" : true
                    }, {
                      "type" : "autocomplete",
                      "minGrams" : 4,
                      "maxGrams" : 10,
                      "foldDiacritics" : true,
                      "tokenization" : "edgeGram",
                      "analyzer" : "lucene.standard"
                    }]
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Autocomplete("title", "Actio"))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("Action Man", results.Select(e => e["title"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/define-field-mappings/
    public async Task Multiple_dynamic_mapping_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>().HasSearchIndex(b =>
            {
                b.IsDynamicWithTypeSet("first");
                b.IndexAsEmbedded(e => e.Awards).IsDynamicWithTypeSet("second");

                b.AddTypeSet("first", b =>
                {
                    b.IndexAsToken();
                    b.IndexAsAutoComplete();
                });

                b.AddTypeSet("second", b =>
                {
                    b.IndexAsNumber();
                });
            });
        });

        var bsonCollection = await PrepareDatabase(db,
        [
            new() { Title = "Action Man", Awards = new() { Wins = 3, Text = "Win!", Nominations = 4 } },
            new() { Title = "G.I. Joe", Awards = new() { Wins = 0, Text = "Never gonna win.", Nominations = 10 } }
        ]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : {
                  "typeSet" : "first"
                },
                "fields" : {
                  "awards" : {
                    "type" : "document",
                    "dynamic" : {
                      "typeSet" : "second"
                    },
                    "fields" : { }
                  }
                }
              },
              "typeSets" : [{
                  "name" : "first",
                  "types" : [{
                      "type" : "token"
                    }, {
                      "type" : "autocomplete",
                      "minGrams" : 2,
                      "maxGrams" : 15,
                      "foldDiacritics" : true,
                      "tokenization" : "edgeGram"
                    }]
                }, {
                  "name" : "second",
                  "types" : [{
                      "type" : "number",
                      "representation" : "double",
                      "indexDoubles" : true,
                      "indexIntegers" : true
                    }]
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Autocomplete("title", "Actio"))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("Action Man", results.Select(e => e["title"].AsString));

        var results2 = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Range("awards.wins", SearchRangeV2Builder.Gt(1).Lt(5)))
            .ToListAsync();

        Assert.Single(results2);
        Assert.Contains("Action Man", results2.Select(e => e["title"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/define-field-mappings/
    public async Task Enable_dynamic_mappings_and_configure_dynamic_mappings_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>().HasSearchIndex(b =>
            {
                b.IsDynamic()
                    .UseAnalyzer(BuiltInSearchAnalyzer.LuceneStandard)
                    .UseSearchAnalyzer(BuiltInSearchAnalyzer.LuceneStandard);

                b.IndexAsEmbedded(e => e.Tomatoes)
                    .IsDynamic()
                    .IndexAsEmbedded(e => e.Viewer)
                    .IsDynamicWithTypeSet("numericOnly");

                b.AddTypeSet("numericOnly", b =>
                {
                    b.IndexAsNumber();
                });
            });
        });

        var bsonCollection = await PrepareDatabase(db,
        [
            new() { Title = "Action Man", Tomatoes = new() { Viewer = new() {  Rating = 3.3 } } },
            new() { Title = "G.I. Joe", Tomatoes = new() { Viewer = new() {  Rating = 4.001 } } }
        ]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "analyzer" : "lucene.standard",
              "searchAnalyzer" : "lucene.standard",
              "mappings" : {
                "dynamic" : true,
                "fields" : {
                  "tomatoes" : {
                    "type" : "document",
                    "dynamic" : true,
                    "fields" : {
                      "viewer" : {
                        "type" : "document",
                        "dynamic" : {
                          "typeSet" : "numericOnly"
                        },
                        "fields" : { }
                      }
                    }
                  }
                }
              },
              "typeSets" : [{
                  "name" : "numericOnly",
                  "types" : [{
                      "type" : "number",
                      "representation" : "double",
                      "indexDoubles" : true,
                      "indexIntegers" : true
                    }]
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Range("tomatoes.viewer.rating", SearchRangeV2Builder.Gt(4.0)))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("G.I. Joe", results.Select(e => e["title"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/define-field-mappings/
    public async Task Static_mapping_simple_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>().HasSearchIndex(b =>
            {
                b.UseAnalyzer(BuiltInSearchAnalyzer.LuceneStandard)
                    .UseSearchAnalyzer(BuiltInSearchAnalyzer.LuceneStandard);

                b.IndexAsEmbedded(e => e.Awards, b =>
                {
                    b.IndexAsNumber(e => e.Wins);
                    b.IndexAsNumber(e => e.Nominations).WithRepresentation(SearchNumberRepresentation.Int64);
                    b.IndexAsString(e => e.Text).UseAnalyzer(BuiltInSearchAnalyzer.LuceneEnglish).IgnoreAbove(255);
                });

                b.IndexAsString(e => e.Title)
                    .UseAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace)
                    .AddAlternateAnalyzer("mySecondaryAnalyzer", BuiltInSearchAnalyzer.LuceneFrench);

                b.IndexAsString(e => e.Genres)
                    .UseAnalyzer(BuiltInSearchAnalyzer.LuceneStandard)
                    .AddAlternateAnalyzer("mySecondaryAnalyzer", BuiltInSearchAnalyzer.LuceneFrench);
            });
        });

        var bsonCollection = await PrepareDatabase(db,
        [
            new() { Title = "Action Man", Awards = new() { Wins = 3, Text = "Win!", Nominations = 4 } },
            new() { Title = "G.I. Joe", Awards = new() { Wins = 0, Text = "Never gonna win.", Nominations = 10 } }
        ]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "analyzer" : "lucene.standard",
              "searchAnalyzer" : "lucene.standard",
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "awards" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "wins" : {
                        "type" : "number",
                        "representation" : "double",
                        "indexDoubles" : true,
                        "indexIntegers" : true
                      },
                      "text" : {
                        "type" : "string",
                        "analyzer" : "lucene.english",
                        "ignoreAbove" : 255,
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      },
                      "nominations" : {
                        "type" : "number",
                        "representation" : "int64",
                        "indexDoubles" : true,
                        "indexIntegers" : true
                      }
                    }
                  },
                  "genres" : {
                    "type" : "string",
                    "analyzer" : "lucene.standard",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include",
                    "multi" : {
                      "mySecondaryAnalyzer" : {
                        "type" : "string",
                        "analyzer" : "lucene.french",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  },
                  "title" : {
                    "type" : "string",
                    "analyzer" : "lucene.whitespace",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include",
                    "multi" : {
                      "mySecondaryAnalyzer" : {
                        "type" : "string",
                        "analyzer" : "lucene.french",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  }
                }
              }
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Range("awards.wins", SearchRangeV2Builder.Gt(1).Lt(5)))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("Action Man", results.Select(e => e["title"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/define-field-mappings/
    public async Task Static_mapping_exclude_fields_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>().HasSearchIndex(b =>
            {
                b.IsDynamicWithTypeSet("indexedTypes")
                    .UseAnalyzer(BuiltInSearchAnalyzer.LuceneStandard)
                    .UseSearchAnalyzer(BuiltInSearchAnalyzer.LuceneStandard);

                b.ExcludeFromIndex(e => e.Plot);

                b.AddTypeSet("indexedTypes", b =>
                {
                    b.IndexAsToken();
                    b.IndexAsNumber();
                });
            });
        });

        var bsonCollection = await PrepareDatabase(db,
        [
            new() { Title = "Action Man", Awards = new() { Wins = 3, Text = "Win!", Nominations = 4 } },
            new() { Title = "G.I. Joe", Awards = new() { Wins = 0, Text = "Never gonna win.", Nominations = 10 } }
        ]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "analyzer" : "lucene.standard",
              "searchAnalyzer" : "lucene.standard",
              "mappings" : {
                "dynamic" : {
                  "typeSet" : "indexedTypes"
                },
                "fields" : {
                  "plot" : []
                }
              },
              "typeSets" : [{
                  "name" : "indexedTypes",
                  "types" : [{
                      "type" : "token"
                    }, {
                      "type" : "number",
                      "representation" : "double",
                      "indexDoubles" : true,
                      "indexIntegers" : true
                    }]
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Range("awards.wins", SearchRangeV2Builder.Gt(1).Lt(5)))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("Action Man", results.Select(e => e["title"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/define-field-mappings/
    public async Task Combined_mapping_simple_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>().HasSearchIndex(b =>
            {
                b.IsDynamic(false)
                    .UseAnalyzer(BuiltInSearchAnalyzer.LuceneStandard)
                    .UseSearchAnalyzer(BuiltInSearchAnalyzer.LuceneStandard);

                b.IndexAsString(e => e.Title)
                    .UseAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace)
                    .AddAlternateAnalyzer("mySecondaryAnalyzer", BuiltInSearchAnalyzer.LuceneFrench);

                b.IndexAsString(e => e.Genres).UseAnalyzer(BuiltInSearchAnalyzer.LuceneStandard);

                b.IndexAsEmbedded(e => e.Awards).IsDynamic();
            });
        });

        var bsonCollection = await PrepareDatabase(db,
        [
            new() { Title = "Action Man", Awards = new() { Wins = 3, Text = "Win!", Nominations = 4 } },
            new() { Title = "G.I. Joe", Awards = new() { Wins = 0, Text = "Never gonna win.", Nominations = 10 } }
        ]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "analyzer" : "lucene.standard",
              "searchAnalyzer" : "lucene.standard",
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "genres" : {
                    "type" : "string",
                    "analyzer" : "lucene.standard",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include"
                  },
                  "awards" : {
                    "type" : "document",
                    "dynamic" : true,
                    "fields" : { }
                  },
                  "title" : {
                    "type" : "string",
                    "analyzer" : "lucene.whitespace",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include",
                    "multi" : {
                      "mySecondaryAnalyzer" : {
                        "type" : "string",
                        "analyzer" : "lucene.french",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  }
                }
              }
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Range("awards.wins", SearchRangeV2Builder.Gt(1).Lt(5)))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("Action Man", results.Select(e => e["title"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/define-field-mappings/
    public async Task Combined_mapping_complex_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>().HasSearchIndex(b =>
            {
                b.IsDynamic(false)
                    .UseAnalyzer(BuiltInSearchAnalyzer.LuceneStandard)
                    .UseSearchAnalyzer(BuiltInSearchAnalyzer.LuceneStandard);

                b.IndexAsEmbedded(e => e.Awards).IsDynamicWithTypeSet("movieAwards");

                b.AddTypeSet("movieAwards", b =>
                {
                    b.IndexAsString()
                        .AddAlternateAnalyzer("english", BuiltInSearchAnalyzer.LuceneEnglish)
                        .AddAlternateAnalyzer("french", BuiltInSearchAnalyzer.LuceneFrench);

                    b.IndexAsNumber();

                    b.IndexAsAutoComplete()
                        .UseAnalyzer(BuiltInSearchAnalyzer.LuceneStandard)
                        .WithTokenization(SearchTokenization.EdgeGram)
                        .WithMinGrams(3)
                        .WithMaxGrams(5)
                        .FoldDiacritics(false);
                });
            });
        });

        var bsonCollection = await PrepareDatabase(db,
        [
            new() { Title = "Action Man", Awards = new() { Wins = 3, Text = "Win!", Nominations = 4 } },
            new() { Title = "G.I. Joe", Awards = new() { Wins = 0, Text = "Never gonna win.", Nominations = 10 } }
        ]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "analyzer" : "lucene.standard",
              "searchAnalyzer" : "lucene.standard",
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "awards" : {
                    "type" : "document",
                    "dynamic" : {
                      "typeSet" : "movieAwards"
                    },
                    "fields" : { }
                  }
                }
              },
              "typeSets" : [{
                  "name" : "movieAwards",
                  "types" : [{
                      "type" : "string",
                      "indexOptions" : "offsets",
                      "store" : true,
                      "norms" : "include",
                      "multi" : {
                        "english" : {
                          "type" : "string",
                          "analyzer" : "lucene.english",
                          "indexOptions" : "offsets",
                          "store" : true,
                          "norms" : "include"
                        },
                        "french" : {
                          "type" : "string",
                          "analyzer" : "lucene.french",
                          "indexOptions" : "offsets",
                          "store" : true,
                          "norms" : "include"
                        }
                      }
                    }, {
                      "type" : "number",
                      "representation" : "double",
                      "indexDoubles" : true,
                      "indexIntegers" : true
                    }, {
                      "type" : "autocomplete",
                      "minGrams" : 3,
                      "maxGrams" : 5,
                      "foldDiacritics" : false,
                      "tokenization" : "edgeGram",
                      "analyzer" : "lucene.standard"
                    }]
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Range("awards.wins", SearchRangeV2Builder.Gt(1).Lt(5)))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("Action Man", results.Select(e => e["title"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/define-field-mappings/
    public async Task Multiple_field_type_definition_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>().HasSearchIndex(b =>
            {
                b.IsDynamicWithTypeSet("first")
                    .UseAnalyzer(BuiltInSearchAnalyzer.LuceneStandard)
                    .UseSearchAnalyzer(BuiltInSearchAnalyzer.LuceneStandard);

                b.IndexAsEmbedded(e => e.Awards).IsDynamicWithTypeSet("second");

                b.AddTypeSet("first", b =>
                {
                    b.IndexAsToken();
                    b.IndexAsNumber();
                });

                b.AddTypeSet("second", b =>
                {
                    b.IndexAsAutoComplete();
                });
            });
        });

        var bsonCollection = await PrepareDatabase(db,
        [
            new() { Title = "Action Man", Awards = new() { Wins = 3, Text = "Win!", Nominations = 4 } },
            new() { Title = "G.I. Joe", Awards = new() { Wins = 0, Text = "Never gonna win.", Nominations = 10 } }
        ]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "analyzer" : "lucene.standard",
              "searchAnalyzer" : "lucene.standard",
              "mappings" : {
                "dynamic" : {
                  "typeSet" : "first"
                },
                "fields" : {
                  "awards" : {
                    "type" : "document",
                    "dynamic" : {
                      "typeSet" : "second"
                    },
                    "fields" : { }
                  }
                }
              },
              "typeSets" : [{
                  "name" : "first",
                  "types" : [{
                      "type" : "token"
                    }, {
                      "type" : "number",
                      "representation" : "double",
                      "indexDoubles" : true,
                      "indexIntegers" : true
                    }]
                }, {
                  "name" : "second",
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

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Autocomplete("awards.text", "Never gonn"))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("G.I. Joe", results.Select(e => e["title"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/field-types/array-type/
    public async Task Array_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>()
                .HasSearchIndex()
                .IndexAsString(e => e.Genres);
        });

        var bsonCollection = await PrepareDatabase(db,
        [
            new() { Title = "Bella 1", Genres = ["Comedy", "Drama", "Fishing"] },
            new() { Title = "Bella 2", Genres = ["Comedy", "Fishing"] },
            new() { Title = "Bella 3", Genres = ["Comedy", "Drama"] },
        ]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "genres" : {
                    "type" : "string",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include"
                  }
                }
              }
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("genres", "Drama"))
            .ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.Contains("Bella 1", results.Select(e => e["title"].AsString));
        Assert.Contains("Bella 3", results.Select(e => e["title"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/field-types/autocomplete-type/
    public async Task Autocomplete_simple_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>()
                .HasSearchIndex()
                .IsDynamic(false)
                .IndexAsAutoComplete(e => e.Title)
                .UseAnalyzer(BuiltInSearchAnalyzer.LuceneStandard)
                .WithTokenization(SearchTokenization.EdgeGram)
                .WithMinGrams(3)
                .WithMaxGrams(5)
                .FoldDiacritics(false)
                .UseSimilarity(SearchSimilarityAlgorithm.StableTfl);
        });

        var bsonCollection = await PrepareDatabase(db, [new() { Title = "Action Man" }, new() { Title = "G.I. Joe" }]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "title" : {
                    "type" : "autocomplete",
                    "minGrams" : 3,
                    "maxGrams" : 5,
                    "foldDiacritics" : false,
                    "tokenization" : "edgeGram",
                    "analyzer" : "lucene.standard",
                    "similarity" : {
                      "type" : "stableTfl"
                    }
                  }
                }
              }
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Autocomplete("title", "Acti"))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("Action Man", results.Select(e => e["title"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/field-types/autocomplete-type/
    public async Task Autocomplete_dynamic_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>()
                .HasSearchIndex(b =>
                {
                    b.IsDynamicWithTypeSet("moviesStringIndex")
                        .ExcludeFromIndex(e => e.FullPlot)
                        .ExcludeFromIndex(e => e.Awards);

                    b.AddTypeSet("moviesStringIndex").IndexAsAutoComplete();
                });
        });

        var bsonCollection = await PrepareDatabase(db, [new() { Title = "Action Man" }, new() { Title = "G.I. Joe" }]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : {
                  "typeSet" : "moviesStringIndex"
                },
                "fields" : {
                  "fullplot" : [],
                  "awards" : []
                }
              },
              "typeSets" : [{
                  "name" : "moviesStringIndex",
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

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Autocomplete("title", "Acti"))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("Action Man", results.Select(e => e["title"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/field-types/autocomplete-type/
    public async Task Autocomplete_multiple_types_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>()
                .HasSearchIndex(b =>
                {
                    b.IndexAsAutoComplete(e => e.Title)
                        .UseAnalyzer(BuiltInSearchAnalyzer.LuceneStandard)
                        .WithTokenization(SearchTokenization.EdgeGram)
                        .WithMinGrams(2)
                        .WithMaxGrams(15)
                        .FoldDiacritics(false);

                    b.IndexAsString(e => e.Title);
                });
        });

        var bsonCollection = await PrepareDatabase(db, [new() { Title = "Action Man" }, new() { Title = "G.I. Joe" }]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "title" : [{
                      "type" : "autocomplete",
                      "minGrams" : 2,
                      "maxGrams" : 15,
                      "foldDiacritics" : false,
                      "tokenization" : "edgeGram",
                      "analyzer" : "lucene.standard"
                    }, {
                      "type" : "string",
                      "indexOptions" : "offsets",
                      "store" : true,
                      "norms" : "include"
                    }]
                }
              }
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Autocomplete("title", "Acti"))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("Action Man", results.Select(e => e["title"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/field-types/autocomplete-type/
    public async Task Autocomplete_email_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex()
                .IsDynamic()
                .IndexAsEmbedded(e => e.UpdatedBy)
                .IsDynamic()
                .IndexAsAutoComplete(e => e.Email)
                .UseAnalyzer(BuiltInSearchAnalyzer.LuceneKeyword)
                .WithTokenization(SearchTokenization.NGram)
                .WithMinGrams(2)
                .WithMaxGrams(15)
                .FoldDiacritics(false);
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : true,
                "fields" : {
                  "page_updated_by" : {
                    "type" : "document",
                    "dynamic" : true,
                    "fields" : {
                      "email" : {
                        "type" : "autocomplete",
                        "minGrams" : 2,
                        "maxGrams" : 15,
                        "foldDiacritics" : false,
                        "tokenization" : "nGram",
                        "analyzer" : "lucene.keyword"
                      }
                    }
                  }
                }
              }
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Autocomplete("page_updated_by.email", "lewin"))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("lewinsky@example.com", results.Select(e => e["page_updated_by"]["email"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/field-types/boolean-type/
    public async Task Boolean_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex()
                .IndexAsBoolean(e => e.Active);
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "active" : {
                    "type" : "boolean"
                  }
                }
              }
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Equals("active", true))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains(2, results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/field-types/date-type/
    public async Task Date_basic_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>()
                .HasSearchIndex()
                .IsDynamic(false)
                .IndexAsDate(e => e.Released);
        });

        var bsonCollection = await PrepareDatabase(db,
            [
                new() { Title = "Action Man", Released = new(1976, 5, 4, 3, 2, 1, DateTimeKind.Utc)},
                new() { Title = "G.I. Joe", Released = new(1976, 5, 4, 3, 2, 2, DateTimeKind.Utc)}
            ]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "released" : {
                    "type" : "date"
                  }
                }
              }
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Equals("released", new DateTime(1976, 5, 4, 3, 2, 2, DateTimeKind.Utc)))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("G.I. Joe", results.Select(e => e["title"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/field-types/document-type/
    public async Task Embedded_document_dynamic_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>()
                .HasSearchIndex()
                .IsDynamic(false)
                .IndexAsEmbedded(e => e.Awards)
                .IsDynamic();
        });

        var bsonCollection = await PrepareDatabase(db,
        [
            new() { Title = "Action Man", Awards = new() { Wins = 3, Text = "Win!", Nominations = 4 } },
            new() { Title = "G.I. Joe", Awards = new() { Wins = 0, Text = "Never gonna win.", Nominations = 10 } }
        ]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "awards" : {
                    "type" : "document",
                    "dynamic" : true,
                    "fields" : { }
                  }
                }
              }
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Range("awards.wins", SearchRangeV2Builder.Gt(1).Lt(5)))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("Action Man", results.Select(e => e["title"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/field-types/document-type/
    public async Task Embedded_document_dynamic_with_type_set_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>()
                .HasSearchIndex(b =>
                {
                    b.IsDynamic(false)
                        .IndexAsEmbedded(e => e.Awards)
                        .IsDynamicWithTypeSet("onlyNumbers");

                    b.AddTypeSet("onlyNumbers").IndexAsNumber();
                });
        });

        var bsonCollection = await PrepareDatabase(db,
        [
            new() { Title = "Action Man", Awards = new() { Wins = 3, Text = "Win!", Nominations = 4 } },
            new() { Title = "G.I. Joe", Awards = new() { Wins = 0, Text = "Never gonna win.", Nominations = 10 } }
        ]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "awards" : {
                    "type" : "document",
                    "dynamic" : {
                      "typeSet" : "onlyNumbers"
                    },
                    "fields" : { }
                  }
                }
              },
              "typeSets" : [{
                  "name" : "onlyNumbers",
                  "types" : [{
                      "type" : "number",
                      "representation" : "double",
                      "indexDoubles" : true,
                      "indexIntegers" : true
                    }]
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Range("awards.wins", SearchRangeV2Builder.Gt(1).Lt(5)))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("Action Man", results.Select(e => e["title"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/field-types/embedded-documents-type/
    public async Task Embedded_array_of_documents_basic_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Company>(), modelBuilder =>
        {
            modelBuilder.Entity<Company>()
                .HasSearchIndex().IndexAsEmbeddedArray(e => e.Products).IsDynamic();
        });

        var bsonCollection = await PrepareCompaniesDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "products" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : true,
                    "fields" : { }
                  }
                }
              }
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.EmbeddedDocument(
                "products",
                Builders<BsonDocument>.Search.Text("name", "Marmite")))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("Bookface", results.Select(e => e["name"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/field-types/embedded-documents-type/
    public async Task Embedded_array_of_documents_dynamic_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Company>(), modelBuilder =>
        {
            modelBuilder.Entity<Company>()
                .HasSearchIndex(b =>
                {
                    b.IsDynamic();
                    b.IndexAsEmbeddedArray(e => e.Products).IsDynamic();
                    b.IndexAsToken(e => e.CategoryCode);
                });
        });

        var bsonCollection = await PrepareCompaniesDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : true,
                "fields" : {
                  "category_code" : {
                    "type" : "token"
                  },
                  "products" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : true,
                    "fields" : { }
                  }
                }
              }
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.EmbeddedDocument(
                "products",
                Builders<BsonDocument>.Search.Text("name", "Marmite")))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("Bookface", results.Select(e => e["name"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/field-types/embedded-documents-type/
    public async Task Embedded_array_of_documents_configured_dynamic_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Company>(), modelBuilder =>
        {
            modelBuilder.Entity<Company>()
                .HasSearchIndex(b =>
                {
                    b.IsDynamic(false);
                    b.IndexAsEmbeddedArray(e => e.Relationships, b =>
                    {
                        b.IsDynamicWithTypeSet("stringBooleanIndex");
                        b.IndexAsEmbedded(e => e.Person).IsDynamicWithTypeSet("stringBooleanIndex");
                    });

                    b.AddTypeSet("stringBooleanIndex", b =>
                    {
                        b.IndexAsBoolean();
                        b.IndexAsString();
                    });
                });
        });

        var bsonCollection = await PrepareCompaniesDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "relationships" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : {
                      "typeSet" : "stringBooleanIndex"
                    },
                    "fields" : {
                      "person" : {
                        "type" : "document",
                        "dynamic" : {
                          "typeSet" : "stringBooleanIndex"
                        },
                        "fields" : { }
                      }
                    }
                  }
                }
              },
              "typeSets" : [{
                  "name" : "stringBooleanIndex",
                  "types" : [{
                      "type" : "boolean"
                    }, {
                      "type" : "string",
                      "indexOptions" : "offsets",
                      "store" : true,
                      "norms" : "include"
                    }]
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.EmbeddedDocument(
                "relationships",
                Builders<BsonDocument>.Search.Text("person.first_name", "Arthur")))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("Bookface", results.Select(e => e["name"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/field-types/embedded-documents-type/
    public async Task Embedded_array_of_documents_specified_fields_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Company>(), modelBuilder =>
        {
            modelBuilder.Entity<Company>()
                .HasSearchIndex(b =>
                {
                    b.IndexAsEmbeddedArray(e => e.Offices, b =>
                    {
                        b.IsDynamic(false);
                        b.IndexAsString(e => e.CountryCode);
                        b.IndexAsString(e => e.StateCode);
                    });
                });
        });

        var bsonCollection = await PrepareCompaniesDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "offices" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : false,
                    "fields" : {
                      "country_code" : {
                        "type" : "string",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      },
                      "state_code" : {
                        "type" : "string",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  }
                }
              }
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.EmbeddedDocument(
                "offices",
                Builders<BsonDocument>.Search.Text("country_code", "UK")))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("Bookface", results.Select(e => e["name"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/field-types/embedded-documents-type/
    public async Task Embedded_array_of_documents_stored_source_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Company>(), modelBuilder =>
        {
            modelBuilder.Entity<Company>()
                .HasSearchIndex(b =>
                {
                    b.IsDynamic(false);
                    b.IndexAsEmbeddedArray(e => e.FundingRounds, b =>
                    {
                        b.IsDynamic()
                            .StoreSourceFor(e => e.RoundCode)
                            .StoreSourceFor(e => e.RaisedCurrencyCode)
                            .StoreSourceFor(e => e.RaisedAmount);
                    });
                });
        });

        var bsonCollection = await PrepareCompaniesDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "funding_rounds" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : true,
                    "fields" : { },
                    "storedSource" : {
                      "include" : ["raised_amount", "raised_currency_code", "round_code"]
                    }
                  }
                }
              }
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.EmbeddedDocument(
                "funding_rounds",
                Builders<BsonDocument>.Search.Text("raised_currency_code", "GBP")))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("Recordface", results.Select(e => e["name"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/field-types/embedded-documents-type/
    public async Task Embedded_array_of_documents_multiple_stored_source_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Company>(), modelBuilder =>
        {
            modelBuilder.Entity<Company>()
                .HasSearchIndex(b =>
                {
                    b.IsDynamic(false);
                    b.IndexAsEmbeddedArray(e => e.Products, b =>
                    {
                        b.IsDynamic().StoreAllSource();
                    });

                    b.StoreSourceFor(e => e.Id)
                        .StoreSourceFor(e => e.Name);
                });
        });

        var bsonCollection = await PrepareCompaniesDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "products" : {
                    "type" : "embeddedDocuments",
                    "dynamic" : true,
                    "fields" : { },
                    "storedSource" : true
                  }
                }
              },
              "storedSource" : {
                "include" : ["_id", "name"]
              }
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.EmbeddedDocument(
                "products",
                Builders<BsonDocument>.Search.Text("name", "Vegemite")))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("Recordface", results.Select(e => e["name"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/field-types/number-type/
    public async Task Number_representation_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>()
                .HasSearchIndex()
                .IsDynamic(false)
                .IndexAsNumber(e => e.Year)
                .WithRepresentation(SearchNumberRepresentation.Int64);
        });

        var bsonCollection = await PrepareDatabase(db,
        [
            new() { Title = "Action Man", Year = 1975 },
            new() { Title = "G.I. Joe", Year = 1979 }
        ]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "year" : {
                    "type" : "number",
                    "representation" : "int64",
                    "indexDoubles" : true,
                    "indexIntegers" : true
                  }
                }
              }
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Equals("year", 1979))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("G.I. Joe", results.Select(e => e["title"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/field-types/number-type/
    public async Task Number_index_integers_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>()
                .HasSearchIndex()
                .IsDynamic(false)
                .IndexAsNumber(e => e.Representative)
                .WithRepresentation(SearchNumberRepresentation.Int64)
                .IndexDoubles(false);
        });

        var bsonCollection = await PrepareDatabase(db,
        [
            new() { Title = "Action Man", Year = 1975, Representative = 0.87 },
            new() { Title = "G.I. Joe", Year = 1979, Representative = 0.89  }
        ]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "representative" : {
                    "type" : "number",
                    "representation" : "int64",
                    "indexDoubles" : false,
                    "indexIntegers" : true
                  }
                }
              }
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Range("representative", SearchRangeV2Builder.Gt(0.88).Lt(0.90)))
            .ToListAsync();

        Assert.Empty(results);
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/field-types/number-type/
    public async Task Number_index_doubles_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>()
                .HasSearchIndex()
                .IsDynamic(false)
                .IndexAsNumber(e => e.Representative)
                .IndexIntegers(false);
        });

        var bsonCollection = await PrepareDatabase(db,
        [
            new() { Title = "Action Man", Year = 1975, Representative = 0.87 },
            new() { Title = "G.I. Joe", Year = 1979, Representative = 0.89  }
        ]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "representative" : {
                    "type" : "number",
                    "representation" : "double",
                    "indexDoubles" : true,
                    "indexIntegers" : false
                  }
                }
              }
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Range("representative", SearchRangeV2Builder.Gt(0.88).Lt(0.90)))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("G.I. Joe", results.Select(e => e["title"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/field-types/object-id-type/
    public async Task ObjectId_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>()
                .HasSearchIndex()
                .IsDynamic(false)
                .IndexAsObjectId(e => e.Id);
        });

        var bsonCollection = await PrepareDatabase(db,
        [
            new() { Title = "Action Man", Year = 1975, Representative = 0.87 },
            new() { Title = "G.I. Joe", Year = 1979, Representative = 0.89  }
        ]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "_id" : {
                    "type" : "objectId"
                  }
                }
              }
            }
            """);

        var objectId = (await bsonCollection.FindAsync(Builders<BsonDocument>.Filter.Eq("title", "G.I. Joe")))
            .Single()["_id"].AsObjectId;

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Equals("_id", objectId))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("G.I. Joe", results.Select(e => e["title"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/field-types/string-type/
    public async Task String_basic_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>()
                .HasSearchIndex()
                .IsDynamic(false)
                .IndexAsString(e => e.Title);
        });

        var bsonCollection = await PrepareDatabase(db, [new() { Title = "Action Man" }, new() { Title = "G.I. Joe"  }]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "title" : {
                    "type" : "string",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include"
                  }
                }
              }
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("title", "Action Man"))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("Action Man", results.Select(e => e["title"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/field-types/string-type/
    public async Task String_multi_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>()
                .HasSearchIndex()
                .IsDynamic(false)
                .IndexAsString(e => e.Title)
                .AddAlternateAnalyzer("english", BuiltInSearchAnalyzer.LuceneEnglish)
                .AddAlternateAnalyzer("french", BuiltInSearchAnalyzer.LuceneFrench)
                .AddAlternateSimilarity("stableTfl", SearchSimilarityAlgorithm.StableTfl);
        });

        var bsonCollection = await PrepareDatabase(db, [new() { Title = "Action Man" }, new() { Title = "G.I. Joe"  }]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "title" : {
                    "type" : "string",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include",
                    "multi" : {
                      "stableTfl" : {
                        "type" : "string",
                        "similarity" : {
                          "type" : "stableTfl"
                        },
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      },
                      "english" : {
                        "type" : "string",
                        "analyzer" : "lucene.english",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      },
                      "french" : {
                        "type" : "string",
                        "analyzer" : "lucene.french",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  }
                }
              }
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("title", "Action Man"))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("Action Man", results.Select(e => e["title"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/field-types/token-type/
    public async Task Token_type_only_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>()
                .HasSearchIndex()
                .IsDynamic(false)
                .IndexAsToken(e => e.Title)
                .NormalizeToLowercase();
        });

        var bsonCollection = await PrepareDatabase(db, [new() { Title = "Action Man" }, new() { Title = "G.I. Joe"  }]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "title" : {
                    "type" : "token",
                    "normalizer" : "lowercase"
                  }
                }
              }
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Equals("title", "Action Man"))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("Action Man", results.Select(e => e["title"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/field-types/token-type/
    public async Task Token_multiple_types_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>()
                .HasSearchIndex(b =>
                {
                    b.IsDynamic(false);
                    b.IndexAsString(e => e.Genres);
                    b.IndexAsToken(e => e.Genres);
                });
        });

        var bsonCollection = await PrepareDatabase(db,
        [
            new() { Title = "Bella 1", Genres = ["Comedy", "Drama", "Fishing"] },
            new() { Title = "Bella 2", Genres = ["Comedy", "Fishing"] },
            new() { Title = "Bella 3", Genres = ["Comedy", "Drama"] },
        ]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "genres" : [{
                      "type" : "string",
                      "indexOptions" : "offsets",
                      "store" : true,
                      "norms" : "include"
                    }, {
                      "type" : "token"
                    }]
                }
              }
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Equals("genres", "Drama"))
            .ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.Contains("Bella 1", results.Select(e => e["title"].AsString));
        Assert.Contains("Bella 3", results.Select(e => e["title"].AsString));

        var results2 = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("genres", "Drama"))
            .ToListAsync();

        Assert.Equal(2, results2.Count);
        Assert.Contains("Bella 1", results2.Select(e => e["title"].AsString));
        Assert.Contains("Bella 3", results2.Select(e => e["title"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/standard/
    public async Task Standard_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create<Movie>(database, modelBuilder =>
        {
            modelBuilder.Entity<Movie>()
                .HasSearchIndex()
                .IndexAsString(e => e.Title)
                .UseAnalyzer(BuiltInSearchAnalyzer.LuceneStandard);
        });

        var bsonCollection = await PrepareDatabase(db, [new() { Title = "Action Man" }, new() { Title = "G.I. Joe" }]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "title" : {
                    "type" : "string",
                    "analyzer" : "lucene.standard",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include"
                  }
                }
              }
            }
            """);

        var result = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("title", "action"))
            .SingleAsync();

        Assert.Equal("Action Man", result["title"].AsString);
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/simple/
    public async Task Simple_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>()
                .HasSearchIndex()
                .IndexAsString(e => e.Title)
                .UseAnalyzer(BuiltInSearchAnalyzer.LuceneSimple);
        });

        var bsonCollection = await PrepareDatabase(db, [new() { Title = "The Lion King" }, new() { Title = "Hamlet" }]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "title" : {
                    "type" : "string",
                    "analyzer" : "lucene.simple",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include"
                  }
                }
              }
            }
            """);

        var result = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("title", "lion"))
            .SingleAsync();

        Assert.Equal("The Lion King", result["title"].AsString);
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/whitespace/
    public async Task Whitespace_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>()
                .HasSearchIndex()
                .IndexAsString(e => e.Title)
                .UseAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace)
                .UseSearchAnalyzer(BuiltInSearchAnalyzer.LuceneWhitespace);
        });

        var bsonCollection = await PrepareDatabase(
            db, [new() { Title = "The Lion's Mouth Opens" }, new() { Title = "Lions Mouth Doorknocker" }]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "title" : {
                    "type" : "string",
                    "analyzer" : "lucene.whitespace",
                    "searchAnalyzer" : "lucene.whitespace",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include"
                  }
                }
              }
            }
            """);

        var result = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("title", "Lion's"))
            .SingleAsync();

        Assert.Equal("The Lion's Mouth Opens", result["title"].AsString);
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/keyword/
    public async Task Keyword_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>()
                .HasSearchIndex()
                .IndexAsString(e => e.Title)
                .UseAnalyzer(BuiltInSearchAnalyzer.LuceneKeyword);
        });

        var bsonCollection = await PrepareDatabase(db, [new() { Title = "Class, Action" }, new() { Title = "Class Action" }]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "title" : {
                    "type" : "string",
                    "analyzer" : "lucene.keyword",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include"
                  }
                }
              }
            }
            """);

        var result = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("title", "Class Action"))
            .SingleAsync();

        Assert.Equal("Class Action", result["title"].AsString);
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/language/
    public async Task Built_in_language_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Car>(), modelBuilder =>
        {
            modelBuilder.Entity<Car>()
                .HasSearchIndex()
                .IndexAsEmbedded(e => e.Subject)
                .IndexAsString(e => e.Fr)
                .UseAnalyzer(BuiltInSearchAnalyzer.LuceneFrench);
        });

        var bsonCollection = await PrepareCarsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "subject" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "fr" : {
                        "type" : "string",
                        "analyzer" : "lucene.french",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  }
                }
              }
            }
            """);

        Assert.Empty(await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("subject.fr", "pour"))
            .ToListAsync());

        var result = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("subject.fr", "carburant"))
            .SingleAsync();

        Assert.Equal(
            "Le meilleur moment pour le faire c'est immédiatement après que vous aurez fait le plein de carburant.",
            result["subject"]["fr"].AsString);
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/language/
    public async Task Custom_language_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Car>(), modelBuilder =>
        {
            modelBuilder.Entity<Car>()
                .HasSearchIndex(b =>
                {
                    b.UseAnalyzer(BuiltInSearchAnalyzer.LuceneStandard);

                    b.AddCustomAnalyzer("myHebrewAnalyzer")
                        .UseStandardTokenizer()
                        .WithTokenFilters()
                        .AddIcuFoldingFilter()
                        .AddStopWordFilter(["אן", "שלנו", "זה", "אל"]);

                    b.IndexAsEmbedded(e => e.Subject)
                        .IndexAsString(e => e.He)
                        .UseAnalyzer("myHebrewAnalyzer");
                });
        });

        var bsonCollection = await PrepareCarsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "analyzer" : "lucene.standard",
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "subject" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "he" : {
                        "type" : "string",
                        "analyzer" : "myHebrewAnalyzer",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  }
                }
              },
              "analyzers" : [{
                  "name" : "myHebrewAnalyzer",
                  "tokenizer" : {
                    "type" : "standard"
                  },
                  "tokenFilters" : [{
                      "type" : "icuFolding"
                    }, {
                      "type" : "stopword",
                      "tokens" : ["אן", "שלנו", "זה", "אל"],
                      "ignoreCase" : true
                    }]
                }]
            }
            """);

        var result = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("subject.he", "המכוניות"))
            .SingleAsync();

        Assert.Equal(
            "עדיף לצייד את המכוניות שלנו כדי להבין את הגורמים לתאונה.",
            result["subject"]["he"].AsString);
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/language/
    public async Task Multilingual_search_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>()
                .HasSearchIndex(b =>
                {
                    b.IsDynamic().UseAnalyzer(BuiltInSearchAnalyzer.LuceneStandard);

                    b.IndexAsString(e => e.FullPlot)
                        .UseAnalyzer(BuiltInSearchAnalyzer.LuceneItalian)
                        .AddAlternateAnalyzer("fullplot_english", BuiltInSearchAnalyzer.LuceneEnglish);

                });
        });

        var bsonCollection = await PrepareDatabase(db, [
            new()
            {
                Title = "Bella 1",
                FullPlot = "Bella's movie",
                Released = new DateTime(1985, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Genres = ["Comedy"]
            },
            new()
            {
                Title = "Bella 2",
                FullPlot = "Bella's second movie",
                Released = new DateTime(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Genres = ["Comedy"]
            },
            new()
            {
                Title = "Not Bella",
                FullPlot = "Benny's movie",
                Released = new DateTime(1983, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Genres = ["Comedy"]
            },
            new()
            {
                Title = "Bella 4",
                FullPlot = "Bella's fourth movie",
                Released = new DateTime(1983, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Genres = ["Drama"]
            },
            new()
            {
                Title = "Bella 3",
                FullPlot = "Bella's third movie",
                Released = new DateTime(1983, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Genres = ["Comedy"]
            },
        ]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "analyzer" : "lucene.standard",
              "mappings" : {
                "dynamic" : true,
                "fields" : {
                  "fullplot" : {
                    "type" : "string",
                    "analyzer" : "lucene.italian",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include",
                    "multi" : {
                      "fullplot_english" : {
                        "type" : "string",
                        "analyzer" : "lucene.english",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  }
                }
              }
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(
                Builders<BsonDocument>.Search.Compound()
                    .Must(Builders<BsonDocument>.Search.Text(
                        Builders<BsonDocument>.SearchPath.Analyzer("fullplot", "fullplot_english"), "Bella"))
                    .MustNot(Builders<BsonDocument>.Search.Range(
                        "released",
                        SearchRangeV2Builder
                            .Gt(DateTime.Parse("1984-01-01T00:00:00.000Z"))
                            .Lt(DateTime.Parse("2016-01-01T00:00:00.000Z"))))
                    .Should(Builders<BsonDocument>.Search.Text("genres", "Comedy")))
            .ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.Equal("Bella 3", results[0]["title"]);
        Assert.Equal("Bella 4", results[1]["title"]);
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/multi/
    public async Task Single_field_multi_search_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>()
                .HasSearchIndex()
                .IndexAsString(e => e.Title)
                .UseAnalyzer(BuiltInSearchAnalyzer.LuceneStandard)
                .AddAlternateAnalyzer("frenchAnalyzer", BuiltInSearchAnalyzer.LuceneFrench);
        });

        var bsonCollection = await PrepareDatabase(db, [new() { Title = "è Nous la Libertè" }, new() { Title = "We are Freedom" }]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "title" : {
                    "type" : "string",
                    "analyzer" : "lucene.standard",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include",
                    "multi" : {
                      "frenchAnalyzer" : {
                        "type" : "string",
                        "analyzer" : "lucene.french",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  }
                }
              }
            }
            """);

        var result = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text(
                Builders<BsonDocument>.SearchPath.Analyzer("title", "frenchAnalyzer"), "liberte"))
            .SingleAsync();

        Assert.Equal(
            "è Nous la Libertè",
            result["title"].AsString);
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/multi/
    public async Task Multiple_fields_multi_search_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>().HasSearchIndex(b =>
            {
                b.IndexAsString(e => e.Title)
                    .UseAnalyzer(BuiltInSearchAnalyzer.LuceneStandard)
                    .AddAlternateAnalyzer("frenchAnalyzer", BuiltInSearchAnalyzer.LuceneFrench);

                b.IndexAsString(e => e.Plot)
                    .UseAnalyzer(BuiltInSearchAnalyzer.LuceneStandard)
                    .AddAlternateAnalyzer("frenchAnalyzer", BuiltInSearchAnalyzer.LuceneFrench);
            });
        });

        var bsonCollection = await PrepareDatabase(db, [
            new() { Title = "è Nous la Libertè", Plot = "è Nous la Libertè" },
            new() { Title = "è Nous la Libertè", Plot = "la rèvolution franèaise" },
            new() { Title = "la rèvolution franèaise", Plot = "è Nous la Libertè" },
            new() { Title = "è Nous la Libertè", Plot = "è Nous la Libertè" },
            new() { Title = "è Nous la Libertè", Plot = "Revolution!" },
            new() { Title = "Revolution!", Plot = "è Nous la Libertè" },
            new() { Title = "è Nous la Libertè", Plot = "è Nous la Libertè" },
        ]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "plot" : {
                    "type" : "string",
                    "analyzer" : "lucene.standard",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include",
                    "multi" : {
                      "frenchAnalyzer" : {
                        "type" : "string",
                        "analyzer" : "lucene.french",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  },
                  "title" : {
                    "type" : "string",
                    "analyzer" : "lucene.standard",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include",
                    "multi" : {
                      "frenchAnalyzer" : {
                        "type" : "string",
                        "analyzer" : "lucene.french",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  }
                }
              }
            }
            """);

        // TODO: Replace with builder query once CSHARP-5806 is implemented.
        var results = await bsonCollection.Aggregate(new EmptyPipelineDefinition<BsonDocument>()
            .AppendStage<BsonDocument, BsonDocument, BsonDocument>(
                new BsonDocument
                {
                    {
                        "$search", new BsonDocument
                        {
                            {
                                "text", new BsonDocument
                                {
                                    { "query", "revolution" },
                                    {
                                        "path", new BsonArray
                                        {
                                            "title",
                                            "plot",
                                            new BsonDocument { { "value", "title" }, { "multi", "frenchAnalyzer" } },
                                            new BsonDocument { { "value", "plot" }, { "multi", "frenchAnalyzer" } },
                                        }
                                    }
                                }
                            }
                        }
                    }
                })).ToListAsync();

        Assert.Equal(4, results.Count);
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/character-filters/
    public async Task HtmlStrip_character_filter_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("htmlStrippingAnalyzer")
                        .UseStandardTokenizer()
                        .WithCharacterFilters()
                        .AddHtmlStripFilter(["a"]);

                    b.IndexAsEmbedded(e => e.Text)
                        .IsDynamic()
                        .IndexAsString(e => e.EnUs)
                        .UseAnalyzer("htmlStrippingAnalyzer");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "text" : {
                    "type" : "document",
                    "dynamic" : true,
                    "fields" : {
                      "en_US" : {
                        "type" : "string",
                        "analyzer" : "htmlStrippingAnalyzer",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  }
                }
              },
              "analyzers" : [{
                  "name" : "htmlStrippingAnalyzer",
                  "charFilters" : [{
                      "type" : "htmlStrip",
                      "ignoredTags" : ["a"]
                    }],
                  "tokenizer" : {
                    "type" : "standard"
                  }
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("text.en_US", "head"))
            .ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.Contains(2, results.Select(e => e["_id"].AsInt32));
        Assert.Contains(3, results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/character-filters/
    public async Task IcuNormalize_character_filter_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("normalizingAnalyzer")
                        .UseWhitespaceTokenizer()
                        .WithCharacterFilters()
                        .AddIcuNormalizeFilter();

                    b.IndexAsString(e => e.Message)
                        .UseAnalyzer("normalizingAnalyzer");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "message" : {
                    "type" : "string",
                    "analyzer" : "normalizingAnalyzer",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include"
                  }
                }
              },
              "analyzers" : [{
                  "name" : "normalizingAnalyzer",
                  "charFilters" : [{
                      "type" : "icuNormalize"
                    }],
                  "tokenizer" : {
                    "type" : "whitespace"
                  }
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("message", "no"))
            .ToListAsync();

        Assert.Single(results);
        Assert.Equal([4], results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/character-filters/
    public async Task Mapping_character_filter_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("mappingAnalyzer")
                        .UseKeywordTokenizer()
                        .WithCharacterFilters()
                        .AddMappingFilter([("-", ""), (".", ""), ("(", ""), (")", ""), (" ", "")]);

                    b.IndexAsEmbedded(e => e.UpdatedBy)
                        .IndexAsString(e => e.Phone)
                        .UseAnalyzer("mappingAnalyzer");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "page_updated_by" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "phone" : {
                        "type" : "string",
                        "analyzer" : "mappingAnalyzer",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  }
                }
              },
              "analyzers" : [{
                  "name" : "mappingAnalyzer",
                  "charFilters" : [{
                      "type" : "mapping",
                      "mappings" : {
                        " " : "",
                        "(" : "",
                        ")" : "",
                        "-" : "",
                        "." : ""
                      }
                    }],
                  "tokenizer" : {
                    "type" : "keyword"
                  }
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("page_updated_by.phone", "1234567890"))
            .ToListAsync();

        Assert.Single(results);
        Assert.Equal([1], results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/character-filters/
    public async Task Persian_character_filter_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("persianCharacterIndex")
                        .UseWhitespaceTokenizer()
                        .WithCharacterFilters()
                        .AddPersianFilter();

                    b.IndexAsEmbedded(e => e.Text)
                        .IndexAsString(e => e.FaIr)
                        .UseAnalyzer("persianCharacterIndex");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "text" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "fa_IR" : {
                        "type" : "string",
                        "analyzer" : "persianCharacterIndex",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  }
                }
              },
              "analyzers" : [{
                  "name" : "persianCharacterIndex",
                  "charFilters" : [{
                      "type" : "persian"
                    }],
                  "tokenizer" : {
                    "type" : "whitespace"
                  }
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("text.fa_IR", "صحبت"))
            .ToListAsync();

        Assert.Single(results);
        Assert.Equal([2], results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/tokenizers/
    public async Task EdgeGram_tokenizer_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("edgegramExample").UseEdgeGramTokenizer(2, 7);
                    b.IndexAsString(e => e.Message).UseAnalyzer("edgegramExample");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "message" : {
                    "type" : "string",
                    "analyzer" : "edgegramExample",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include"
                  }
                }
              },
              "analyzers" : [{
                  "name" : "edgegramExample",
                  "tokenizer" : {
                    "type" : "edgeGram",
                    "minGram" : 2,
                    "maxGram" : 7
                  }
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("message", "tr"))
            .ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.Contains(1, results.Select(e => e["_id"].AsInt32));
        Assert.Contains(3, results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/tokenizers/
    public async Task Keyword_tokenizer_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("keywordExample").UseKeywordTokenizer();
                    b.IsDynamic().IndexAsString(e => e.Message).UseAnalyzer("keywordExample");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : true,
                "fields" : {
                  "message" : {
                    "type" : "string",
                    "analyzer" : "keywordExample",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include"
                  }
                }
              },
              "analyzers" : [{
                  "name" : "keywordExample",
                  "tokenizer" : {
                    "type" : "keyword"
                  }
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("message", "try to sign-in"))
            .ToListAsync();

        Assert.Single(results);
        Assert.Equal([3], results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/tokenizers/
    public async Task NGram_tokenizer_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("ngramExample").UseNGramTokenizer(4, 6);
                    b.IsDynamic().IndexAsString(e => e.Title).UseAnalyzer("ngramExample");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : true,
                "fields" : {
                  "title" : {
                    "type" : "string",
                    "analyzer" : "ngramExample",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include"
                  }
                }
              },
              "analyzers" : [{
                  "name" : "ngramExample",
                  "tokenizer" : {
                    "type" : "nGram",
                    "minGram" : 4,
                    "maxGram" : 6
                  }
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("title", "week"), new SearchOptions<BsonDocument> { IndexName = "default" })
            .ToListAsync();

        Assert.Single(results);
        Assert.Equal([1], results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/tokenizers/
    public async Task RegexCaptureGroup_tokenizer_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("phoneNumberExtractor")
                        .UseRegexCaptureGroupTokenizer(@"^\b\d{3}[-]?\d{3}[-]?\d{4}\b$", 0)
                        .WithCharacterFilters()
                        .AddMappingFilter([(" ", "-"), ("(", ""), (")", ""), (".", "-")]);

                    b.IsDynamic()
                        .IndexAsEmbedded(e => e.UpdatedBy)
                        .IndexAsString(e => e.Phone)
                        .UseAnalyzer("phoneNumberExtractor");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : true,
                "fields" : {
                  "page_updated_by" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "phone" : {
                        "type" : "string",
                        "analyzer" : "phoneNumberExtractor",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  }
                }
              },
              "analyzers" : [{
                  "name" : "phoneNumberExtractor",
                  "charFilters" : [{
                      "type" : "mapping",
                      "mappings" : {
                        " " : "-",
                        "(" : "",
                        ")" : "",
                        "." : "-"
                      }
                    }],
                  "tokenizer" : {
                    "type" : "regexCaptureGroup",
                    "pattern" : "^\\b\\d{3}[-]?\\d{3}[-]?\\d{4}\\b$",
                    "group" : 0
                  }
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(
                Builders<BsonDocument>.Search.Text("page_updated_by.phone", "123-456-9870"),
                new SearchOptions<BsonDocument> { IndexName = "default" })
            .ToListAsync();

        Assert.Single(results);
        Assert.Equal([3], results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/tokenizers/
    public async Task RegexSplit_tokenizer_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("dashDotSpaceSplitter")
                        .UseRegexSplitTokenizer("[-. ]+");

                    b.IsDynamic()
                        .IndexAsEmbedded(e => e.UpdatedBy)
                        .IndexAsString(e => e.Phone)
                        .UseAnalyzer("dashDotSpaceSplitter");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : true,
                "fields" : {
                  "page_updated_by" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "phone" : {
                        "type" : "string",
                        "analyzer" : "dashDotSpaceSplitter",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  }
                }
              },
              "analyzers" : [{
                  "name" : "dashDotSpaceSplitter",
                  "tokenizer" : {
                    "type" : "regexSplit",
                    "pattern" : "[-. ]+"
                  }
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(
                Builders<BsonDocument>.Search.Text("page_updated_by.phone", "9870"),
                new SearchOptions<BsonDocument> { IndexName = "default" })
            .ToListAsync();

        Assert.Single(results);
        Assert.Equal([3], results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/tokenizers/
    public async Task UaxUrlEmail_tokenizer_custom_analyzer_basic_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("basicEmailAddressAnalyzer")
                        .UseUaxUrlEmailTokenizer();

                    b.IsDynamic()
                        .IndexAsEmbedded(e => e.UpdatedBy)
                        .IndexAsString(e => e.Email)
                        .UseAnalyzer("basicEmailAddressAnalyzer");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : true,
                "fields" : {
                  "page_updated_by" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "email" : {
                        "type" : "string",
                        "analyzer" : "basicEmailAddressAnalyzer",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  }
                }
              },
              "analyzers" : [{
                  "name" : "basicEmailAddressAnalyzer",
                  "tokenizer" : {
                    "type" : "uaxUrlEmail"
                  }
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("page_updated_by.email", "lewinsky@example.com"))
            .ToListAsync();

        Assert.Single(results);
        Assert.Equal([3], results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/tokenizers/
    public async Task UaxUrlEmail_tokenizer_custom_analyzer_advanced_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("emailAddressAnalyzer")
                        .UseUaxUrlEmailTokenizer();

                    b.IndexAsEmbedded(e => e.UpdatedBy)
                        .IndexAsAutoComplete(e => e.Email)
                        .WithTokenization(SearchTokenization.EdgeGram)
                        .UseAnalyzer("emailAddressAnalyzer");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "page_updated_by" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "email" : {
                        "type" : "autocomplete",
                        "minGrams" : 2,
                        "maxGrams" : 15,
                        "foldDiacritics" : true,
                        "tokenization" : "edgeGram",
                        "analyzer" : "emailAddressAnalyzer"
                      }
                    }
                  }
                }
              },
              "analyzers" : [{
                  "name" : "emailAddressAnalyzer",
                  "tokenizer" : {
                    "type" : "uaxUrlEmail"
                  }
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Autocomplete("page_updated_by.email", "lewinsky@example.com"))
            .ToListAsync();

        Assert.Single(results);
        Assert.Equal([3], results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/tokenizers/
    public async Task Whitespace_tokenizer_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("whitespaceExample")
                        .UseWhitespaceTokenizer();

                    b.IsDynamic()
                        .IndexAsString(e => e.Message)
                        .UseAnalyzer("whitespaceExample");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : true,
                "fields" : {
                  "message" : {
                    "type" : "string",
                    "analyzer" : "whitespaceExample",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include"
                  }
                }
              },
              "analyzers" : [{
                  "name" : "whitespaceExample",
                  "tokenizer" : {
                    "type" : "whitespace"
                  }
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("message", "sign-in"))
            .ToListAsync();

        Assert.Single(results);
        Assert.Equal([3], results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/
    public async Task AsciiFolding_token_filter_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("asciiConverter")
                        .UseStandardTokenizer()
                        .WithTokenFilters()
                        .AddAsciiFoldingFilter();

                    b.IndexAsEmbedded(e => e.UpdatedBy)
                        .IndexAsString(e => e.FirstName)
                        .UseAnalyzer("asciiConverter");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "page_updated_by" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "first_name" : {
                        "type" : "string",
                        "analyzer" : "asciiConverter",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  }
                }
              },
              "analyzers" : [{
                  "name" : "asciiConverter",
                  "tokenizer" : {
                    "type" : "standard"
                  },
                  "tokenFilters" : [{
                      "type" : "asciiFolding",
                      "originalTokens" : "omit"
                    }]
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("page_updated_by.first_name", "Sian"))
            .ToListAsync();

        Assert.Single(results);
        Assert.Equal([1], results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/
    public async Task DaitchMokotoffSoundex_token_filter_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("dmsAnalyzer")
                        .UseStandardTokenizer()
                        .WithTokenFilters()
                        .AddDaitchMokotoffSoundexFilter(includeOriginalTokens: true);

                    b.IndexAsEmbedded(e => e.UpdatedBy)
                        .IndexAsString(e => e.LastName)
                        .UseAnalyzer("dmsAnalyzer");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "page_updated_by" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "last_name" : {
                        "type" : "string",
                        "analyzer" : "dmsAnalyzer",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  }
                }
              },
              "analyzers" : [{
                  "name" : "dmsAnalyzer",
                  "tokenizer" : {
                    "type" : "standard"
                  },
                  "tokenFilters" : [{
                      "type" : "daitchMokotoffSoundex",
                      "originalTokens" : "include"
                    }]
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("page_updated_by.last_name", "AUERBACH"))
            .ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.Contains(1, results.Select(e => e["_id"].AsInt32));
        Assert.Contains(2, results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/
    public async Task EdgeGram_token_filter_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("titleAutocomplete")
                        .UseStandardTokenizer()
                        .WithTokenFilters()
                        .AddIcuFoldingFilter()
                        .AddEdgeGramFilter(4, 7);

                    b.IndexAsString(e => e.Title)
                        .UseAnalyzer("titleAutocomplete");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "title" : {
                    "type" : "string",
                    "analyzer" : "titleAutocomplete",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include"
                  }
                }
              },
              "analyzers" : [{
                  "name" : "titleAutocomplete",
                  "tokenizer" : {
                    "type" : "standard"
                  },
                  "tokenFilters" : [{
                      "type" : "icuFolding"
                    }, {
                      "type" : "edgeGram",
                      "minGram" : 4,
                      "maxGram" : 7,
                      "termNotInBounds" : "omit"
                    }]
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Wildcard("title", "mee*", allowAnalyzedField: true))
            .ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.Contains(1, results.Select(e => e["_id"].AsInt32));
        Assert.Contains(3, results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/
    public async Task EnglishPossessive_token_filter_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("englishPossessiveStemmer")
                        .UseStandardTokenizer()
                        .WithTokenFilters()
                        .AddEnglishPossessiveFilter();

                    b.IndexAsString(e => e.Title)
                        .UseAnalyzer("englishPossessiveStemmer");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "title" : {
                    "type" : "string",
                    "analyzer" : "englishPossessiveStemmer",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include"
                  }
                }
              },
              "analyzers" : [{
                  "name" : "englishPossessiveStemmer",
                  "tokenizer" : {
                    "type" : "standard"
                  },
                  "tokenFilters" : [{
                      "type" : "englishPossessive"
                    }]
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("title", "team"),
                new SearchOptions<BsonDocument> { IndexName = "default" })
            .ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.Contains(1, results.Select(e => e["_id"].AsInt32));
        Assert.Contains(2, results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/
    public async Task Flatten_graph_token_filter_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("wordDelimiterGraphFlatten")
                        .UseWhitespaceTokenizer()
                        .WithTokenFilters()
                        .AddWordDelimiterGraphFilter(
                            new(GenerateWordParts: true, PreserveOriginal: true, IgnoreCaseForProtectedWords: false),
                            protectedWords: ["SIGN_IN"])
                        .AddFlattenGraphFilter();

                    b.IndexAsString(e => e.Message)
                        .UseAnalyzer("wordDelimiterGraphFlatten");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "message" : {
                    "type" : "string",
                    "analyzer" : "wordDelimiterGraphFlatten",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include"
                  }
                }
              },
              "analyzers" : [{
                  "name" : "wordDelimiterGraphFlatten",
                  "tokenizer" : {
                    "type" : "whitespace"
                  },
                  "tokenFilters" : [{
                      "type" : "wordDelimiterGraph",
                      "protectedWords" : {
                        "words" : ["SIGN_IN"],
                        "ignoreCase" : false
                      },
                      "delimiterOptions" : {
                        "generateWordParts" : true,
                        "generateNumberParts" : true,
                        "concatenateWords" : false,
                        "concatenateNumbers" : false,
                        "concatenateAll" : false,
                        "preserveOriginal" : true,
                        "splitOnCaseChange" : true,
                        "splitOnNumerics" : true,
                        "stemEnglishPossessive" : true,
                        "ignoreKeywords" : false
                      }
                    }, {
                      "type" : "flattenGraph"
                    }]
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("message", "sign"),
                new SearchOptions<BsonDocument> { IndexName = "default" })
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains(3, results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/
    public async Task IcuFolding_token_filter_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("diacriticFolder")
                        .UseKeywordTokenizer()
                        .WithTokenFilters()
                        .AddIcuFoldingFilter();

                    b.IndexAsEmbedded(e => e.Text)
                        .IndexAsString(e => e.SvFi)
                        .UseAnalyzer("diacriticFolder");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "text" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "sv_FI" : {
                        "type" : "string",
                        "analyzer" : "diacriticFolder",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  }
                }
              },
              "analyzers" : [{
                  "name" : "diacriticFolder",
                  "tokenizer" : {
                    "type" : "keyword"
                  },
                  "tokenFilters" : [{
                      "type" : "icuFolding"
                    }]
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Wildcard("text.sv_FI", "*avdelning*", allowAnalyzedField: true),
                new SearchOptions<BsonDocument> { IndexName = "default" })
            .ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.Contains(1, results.Select(e => e["_id"].AsInt32));
        Assert.Contains(2, results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/
    public async Task IcuNormalizer_token_filter_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("textNormalizer")
                        .UseWhitespaceTokenizer()
                        .WithTokenFilters()
                        .AddIcuNormalizerFilter(IcuNormalizationForm.Nfkc);

                    b.IndexAsString(e => e.Message)
                        .UseAnalyzer("textNormalizer");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "message" : {
                    "type" : "string",
                    "analyzer" : "textNormalizer",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include"
                  }
                }
              },
              "analyzers" : [{
                  "name" : "textNormalizer",
                  "tokenizer" : {
                    "type" : "whitespace"
                  },
                  "tokenFilters" : [{
                      "type" : "icuNormalizer",
                      "normalizationForm" : "nfkc"
                    }]
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("message", "1"),
                new SearchOptions<BsonDocument> { IndexName = "default" })
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains(2, results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/
    public async Task KeywordRepeat_token_filter_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("keywordStemRemover")
                        .UseWhitespaceTokenizer()
                        .WithTokenFilters()
                        .AddKeywordRepeatFilter()
                        .AddPorterStemmingFilter()
                        .AddRemoveDuplicatesFilter();

                    b.IndexAsString(e => e.Title)
                        .UseAnalyzer("keywordStemRemover");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "title" : {
                    "type" : "string",
                    "analyzer" : "keywordStemRemover",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include"
                  }
                }
              },
              "analyzers" : [{
                  "name" : "keywordStemRemover",
                  "tokenizer" : {
                    "type" : "whitespace"
                  },
                  "tokenFilters" : [{
                      "type" : "keywordRepeat"
                    }, {
                      "type" : "porterStemming"
                    }, {
                      "type" : "removeDuplicates"
                    }]
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Wildcard("title", "mee*", allowAnalyzedField: true))
            .ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.Contains(1, results.Select(e => e["_id"].AsInt32));
        Assert.Contains(3, results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/
    public async Task KStemming_token_filter_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("kStemmer")
                        .UseStandardTokenizer()
                        .WithTokenFilters()
                        .AddLowercaseFilter()
                        .AddKStemmingFilter();

                    b.IsDynamic()
                        .UseAnalyzer("kStemmer");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "analyzer" : "kStemmer",
              "mappings" : {
                "dynamic" : true,
                "fields" : { }
              },
              "analyzers" : [{
                  "name" : "kStemmer",
                  "tokenizer" : {
                    "type" : "standard"
                  },
                  "tokenFilters" : [{
                      "type" : "lowercase"
                    }, {
                      "type" : "kStemming"
                    }]
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("text.en_US", "Meeting"),
                new SearchOptions<BsonDocument> { IndexName = "default" })
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains(1, results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/
    public async Task Length_token_filter_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("longOnly")
                        .UseStandardTokenizer()
                        .WithTokenFilters()
                        .AddIcuFoldingFilter()
                        .AddLengthFilter(min: 20);

                    b.IndexAsEmbedded(e => e.Text)
                        .IsDynamic()
                        .IndexAsString(e => e.SvFi)
                        .UseAnalyzer("longOnly");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "text" : {
                    "type" : "document",
                    "dynamic" : true,
                    "fields" : {
                      "sv_FI" : {
                        "type" : "string",
                        "analyzer" : "longOnly",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  }
                }
              },
              "analyzers" : [{
                  "name" : "longOnly",
                  "tokenizer" : {
                    "type" : "standard"
                  },
                  "tokenFilters" : [{
                      "type" : "icuFolding"
                    }, {
                      "type" : "length",
                      "min" : 20,
                      "max" : 255
                    }]
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("text.sv_FI", "forsaljningsavdelningen"),
                new SearchOptions<BsonDocument> { IndexName = "default" })
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains(2, results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/
    public async Task Lowercase_token_filter_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("keywordLowerer")
                        .UseKeywordTokenizer()
                        .WithTokenFilters()
                        .AddLowercaseFilter();

                    b.IndexAsAutoComplete(e => e.Title)
                        .UseAnalyzer("keywordLowerer")
                        .WithTokenization(SearchTokenization.NGram);
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "title" : {
                    "type" : "autocomplete",
                    "minGrams" : 2,
                    "maxGrams" : 15,
                    "foldDiacritics" : true,
                    "tokenization" : "nGram",
                    "analyzer" : "keywordLowerer"
                  }
                }
              },
              "analyzers" : [{
                  "name" : "keywordLowerer",
                  "tokenizer" : {
                    "type" : "keyword"
                  },
                  "tokenFilters" : [{
                      "type" : "lowercase"
                    }]
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Autocomplete("title", "standup"),
                new SearchOptions<BsonDocument> { IndexName = "default" })
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains(4, results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/
    public async Task NGram_token_filter_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("titleAutocomplete")
                        .UseStandardTokenizer()
                        .WithTokenFilters()
                        .AddEnglishPossessiveFilter()
                        .AddNGramFilter(4, 7);

                    b.IndexAsString(e => e.Title)
                        .UseAnalyzer("titleAutocomplete")
                        .UseSearchAnalyzer(BuiltInSearchAnalyzer.LuceneKeyword);
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "title" : {
                    "type" : "string",
                    "analyzer" : "titleAutocomplete",
                    "searchAnalyzer" : "lucene.keyword",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include"
                  }
                }
              },
              "analyzers" : [{
                  "name" : "titleAutocomplete",
                  "tokenizer" : {
                    "type" : "standard"
                  },
                  "tokenFilters" : [{
                      "type" : "englishPossessive"
                    }, {
                      "type" : "nGram",
                      "minGram" : 4,
                      "maxGram" : 7,
                      "termNotInBounds" : "omit"
                    }]
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Wildcard("title", "meet*", allowAnalyzedField: true),
                new SearchOptions<BsonDocument> { IndexName = "default" })
            .ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.Contains(1, results.Select(e => e["_id"].AsInt32));
        Assert.Contains(3, results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/
    public async Task PorterStemming_token_filter_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("porterStemmer")
                        .UseStandardTokenizer()
                        .WithTokenFilters()
                        .AddLowercaseFilter()
                        .AddPorterStemmingFilter();

                    b.IndexAsString(e => e.Title)
                        .UseAnalyzer("porterStemmer");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "title" : {
                    "type" : "string",
                    "analyzer" : "porterStemmer",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include"
                  }
                }
              },
              "analyzers" : [{
                  "name" : "porterStemmer",
                  "tokenizer" : {
                    "type" : "standard"
                  },
                  "tokenFilters" : [{
                      "type" : "lowercase"
                    }, {
                      "type" : "porterStemming"
                    }]
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("title", "Meet"),
                new SearchOptions<BsonDocument> { IndexName = "default" })
            .ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.Contains(1, results.Select(e => e["_id"].AsInt32));
        Assert.Contains(3, results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/
    public async Task Regex_token_filter_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("emailRedact")
                        .UseKeywordTokenizer()
                        .WithTokenFilters()
                        .AddLowercaseFilter()
                        .AddRegexFilter(
                            @"^([a-z0-9_\.-]+)@([\da-z\.-]+)\.([a-z\.]{2,5})$",
                            "redacted",
                            RegexTokenFilterMatches.All);

                    b.IsDynamic(false)
                        .IndexAsEmbedded(e => e.UpdatedBy)
                        .IndexAsString(e => e.Email)
                        .UseAnalyzer("emailRedact");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "page_updated_by" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "email" : {
                        "type" : "string",
                        "analyzer" : "emailRedact",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  }
                }
              },
              "analyzers" : [{
                  "name" : "emailRedact",
                  "tokenizer" : {
                    "type" : "keyword"
                  },
                  "tokenFilters" : [{
                      "type" : "lowercase"
                    }, {
                      "type" : "regex",
                      "pattern" : "^([a-z0-9_\\.-]+)@([\\da-z\\.-]+)\\.([a-z\\.]{2,5})$",
                      "replacement" : "redacted",
                      "matches" : "all"
                    }]
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Wildcard("page_updated_by.email", "*example.com", allowAnalyzedField: true),
                new SearchOptions<BsonDocument> { IndexName = "default" })
            .Project(Builders<BsonDocument>.Projection.Include("page_updated_by.email"))
            .ToListAsync();

        Assert.Empty(results);
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/
    public async Task RemoveDuplicates_filter_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("duplicateRemover")
                        .UseWhitespaceTokenizer()
                        .WithTokenFilters()
                        .AddKeywordRepeatFilter()
                        .AddRemoveDuplicatesFilter();

                    b.IsDynamic(false)
                        .IndexAsString(e => e.Title)
                        .UseAnalyzer("duplicateRemover");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "title" : {
                    "type" : "string",
                    "analyzer" : "duplicateRemover",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include"
                  }
                }
              },
              "analyzers" : [{
                  "name" : "duplicateRemover",
                  "tokenizer" : {
                    "type" : "whitespace"
                  },
                  "tokenFilters" : [{
                      "type" : "keywordRepeat"
                    }, {
                      "type" : "removeDuplicates"
                    }]
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Wildcard("title", "mee*", allowAnalyzedField: true))
            .ToListAsync();


        Assert.Equal(2, results.Count);
        Assert.Contains(1, results.Select(e => e["_id"].AsInt32));
        Assert.Contains(3, results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/
    public async Task Reverse_filter_custom_analyzer_example()
    {
        // Not quite the example online, since it does a pointless double-reverse filter that is essentially a no-op.

        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("keywordReverse")
                        .UseKeywordTokenizer()
                        .WithTokenFilters()
                        .AddReverseFilter();

                    b.IsDynamic()
                        .UseAnalyzer("keywordReverse")
                        .UseSearchAnalyzer(BuiltInSearchAnalyzer.LuceneKeyword);
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "analyzer" : "keywordReverse",
              "searchAnalyzer" : "lucene.keyword",
              "mappings" : {
                "dynamic" : true,
                "fields" : { }
              },
              "analyzers" : [{
                  "name" : "keywordReverse",
                  "tokenizer" : {
                    "type" : "keyword"
                  },
                  "tokenFilters" : [{
                      "type" : "reverse"
                    }]
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Wildcard("page_updated_by.email", "moc.elpmaxe@*", allowAnalyzedField: true))
            .ToListAsync();

        Assert.Equal(4, results.Count);
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/
    public async Task Shingle_filter_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("emailAutocompleteIndex", b =>
                        {
                            b.UseWhitespaceTokenizer(15);
                            b.WithCharacterFilters().AddMappingFilter([("@", "AT")]);
                            b.WithTokenFilters().AddShingleFilter(2, 3).AddEdgeGramFilter(2, 15);
                        });

                    b.AddCustomAnalyzer("emailAutocompleteSearch", b =>
                        {
                            b.UseWhitespaceTokenizer(15);
                            b.WithCharacterFilters().AddMappingFilter([("@", "AT")]);
                        });

                    b.IsDynamic()
                        .IndexAsEmbedded(e => e.UpdatedBy)
                        .IndexAsString(e => e.Email)
                        .UseAnalyzer("emailAutocompleteIndex")
                        .UseSearchAnalyzer("emailAutocompleteSearch");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : true,
                "fields" : {
                  "page_updated_by" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "email" : {
                        "type" : "string",
                        "analyzer" : "emailAutocompleteIndex",
                        "searchAnalyzer" : "emailAutocompleteSearch",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  }
                }
              },
              "analyzers" : [{
                  "name" : "emailAutocompleteIndex",
                  "charFilters" : [{
                      "type" : "mapping",
                      "mappings" : {
                        "@" : "AT"
                      }
                    }],
                  "tokenizer" : {
                    "type" : "whitespace",
                    "maxTokenLength" : 15
                  },
                  "tokenFilters" : [{
                      "type" : "shingle",
                      "minShingleSize" : 2,
                      "maxShingleSize" : 3
                    }, {
                      "type" : "edgeGram",
                      "minGram" : 2,
                      "maxGram" : 15,
                      "termNotInBounds" : "omit"
                    }]
                }, {
                  "name" : "emailAutocompleteSearch",
                  "charFilters" : [{
                      "type" : "mapping",
                      "mappings" : {
                        "@" : "AT"
                      }
                    }],
                  "tokenizer" : {
                    "type" : "whitespace",
                    "maxTokenLength" : 15
                  }
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("page_updated_by.email", "auerbach@ex"),
                new SearchOptions<BsonDocument> { IndexName = "default" })
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains(1, results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/
    public async Task SnowballStemming_filter_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("frenchStemmer")
                        .UseStandardTokenizer()
                        .WithTokenFilters()
                        .AddLowercaseFilter()
                        .AddSnowballStemmingFilter(SnowballStemmerName.French);

                    b.IndexAsEmbedded(e => e.Text)
                        .IndexAsString(e => e.FrCa)
                        .UseAnalyzer("frenchStemmer");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "text" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "fr_CA" : {
                        "type" : "string",
                        "analyzer" : "frenchStemmer",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  }
                }
              },
              "analyzers" : [{
                  "name" : "frenchStemmer",
                  "tokenizer" : {
                    "type" : "standard"
                  },
                  "tokenFilters" : [{
                      "type" : "lowercase"
                    }, {
                      "type" : "snowballStemming",
                      "stemmerName" : "french"
                    }]
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("text.fr_CA", "réunion"),
                new SearchOptions<BsonDocument> { IndexName = "default" })
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains(1, results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/
    public async Task Stempel_filter_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("stempelAnalyzer")
                        .UseStandardTokenizer()
                        .WithTokenFilters()
                        .AddStempelFilter();

                    b.IndexAsEmbedded(e => e.Text)
                        .IndexAsString(e => e.PlPl)
                        .UseAnalyzer("stempelAnalyzer");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "text" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "pl_PL" : {
                        "type" : "string",
                        "analyzer" : "stempelAnalyzer",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  }
                }
              },
              "analyzers" : [{
                  "name" : "stempelAnalyzer",
                  "tokenizer" : {
                    "type" : "standard"
                  },
                  "tokenFilters" : [{
                      "type" : "stempel"
                    }]
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("text.pl_PL", "punkt"),
                new SearchOptions<BsonDocument> { IndexName = "default" })
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains(4, results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/
    public async Task Stopword_filter_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("stopwordRemover")
                        .UseWhitespaceTokenizer()
                        .WithTokenFilters()
                        .AddStopWordFilter(["is", "the", "at"]);

                    b.IndexAsEmbedded(e => e.Text)
                        .IndexAsString(e => e.EnUs)
                        .UseAnalyzer("stopwordRemover");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "text" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "en_US" : {
                        "type" : "string",
                        "analyzer" : "stopwordRemover",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  }
                }
              },
              "analyzers" : [{
                  "name" : "stopwordRemover",
                  "tokenizer" : {
                    "type" : "whitespace"
                  },
                  "tokenFilters" : [{
                      "type" : "stopword",
                      "tokens" : ["is", "the", "at"],
                      "ignoreCase" : true
                    }]
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Phrase("text.en_US", "head of the sales"))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains(2, results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/
    public async Task Trim_filter_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("tokenTrimmer", b =>
                        {
                            b.WithCharacterFilters().AddHtmlStripFilter(["a"]);
                            b.UseKeywordTokenizer().WithTokenFilters().AddTrimFilter();
                        });

                    b.IndexAsEmbedded(e => e.Text)
                        .IndexAsString(e => e.EnUs)
                        .UseAnalyzer("tokenTrimmer");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "text" : {
                    "type" : "document",
                    "dynamic" : false,
                    "fields" : {
                      "en_US" : {
                        "type" : "string",
                        "analyzer" : "tokenTrimmer",
                        "indexOptions" : "offsets",
                        "store" : true,
                        "norms" : "include"
                      }
                    }
                  }
                }
              },
              "analyzers" : [{
                  "name" : "tokenTrimmer",
                  "charFilters" : [{
                      "type" : "htmlStrip",
                      "ignoredTags" : ["a"]
                    }],
                  "tokenizer" : {
                    "type" : "keyword"
                  },
                  "tokenFilters" : [{
                      "type" : "trim"
                    }]
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Wildcard("text.en_US", "*department meetings*", allowAnalyzedField: true))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains(1, results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/
    public async Task WordDelimiterGraph_filter_custom_analyzer_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Meeting>(), modelBuilder =>
        {
            modelBuilder.Entity<Meeting>()
                .HasSearchIndex(b =>
                {
                    b.AddCustomAnalyzer("wordDelimiterGraphAnalyzer")
                        .UseWhitespaceTokenizer()
                        .WithTokenFilters()
                        .AddWordDelimiterGraphFilter(
                            new(GenerateWordParts: false, SplitOnCaseChange: true, IgnoreCaseForProtectedWords: false),
                            ["is", "the", "at"]);

                    b.IndexAsString(e => e.Title)
                        .UseAnalyzer("wordDelimiterGraphAnalyzer");
                });
        });

        var bsonCollection = await PrepareMeetingsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "title" : {
                    "type" : "string",
                    "analyzer" : "wordDelimiterGraphAnalyzer",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include"
                  }
                }
              },
              "analyzers" : [{
                  "name" : "wordDelimiterGraphAnalyzer",
                  "tokenizer" : {
                    "type" : "whitespace"
                  },
                  "tokenFilters" : [{
                      "type" : "wordDelimiterGraph",
                      "protectedWords" : {
                        "words" : ["is", "the", "at"],
                        "ignoreCase" : false
                      },
                      "delimiterOptions" : {
                        "generateWordParts" : false,
                        "generateNumberParts" : true,
                        "concatenateWords" : false,
                        "concatenateNumbers" : false,
                        "concatenateAll" : false,
                        "preserveOriginal" : false,
                        "splitOnCaseChange" : true,
                        "splitOnNumerics" : true,
                        "stemEnglishPossessive" : true,
                        "ignoreKeywords" : false
                      }
                    }]
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("title", "App2"))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains(4, results.Select(e => e["_id"].AsInt32));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/stored-source-definition/
    public async Task Store_source_include_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>()
                .HasSearchIndex(b =>
                {
                    b.IsDynamic().StoreSourceFor(e => e.Title);
                    b.IndexAsEmbedded(e => e.Awards).IsDynamic().StoreSourceFor(e => e.Wins);
                });
        });

        var bsonCollection = await PrepareDatabase(db,
        [
            new() { Title = "Action Man", Awards = new() { Wins = 3, Text = "Win!", Nominations = 4 } },
            new() { Title = "G.I. Joe", Awards = new() { Wins = 0, Text = "Never gonna win.", Nominations = 10 } }
        ]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : true,
                "fields" : {
                  "awards" : {
                    "type" : "document",
                    "dynamic" : true,
                    "fields" : { }
                  }
                }
              },
              "storedSource" : {
                "include" : ["awards.wins", "title"]
              }
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Range("awards.wins", SearchRangeV2Builder.Gt(1).Lt(5)))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("Action Man", results.Select(e => e["title"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/stored-source-definition/
    public async Task Store_source_exclude_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>()
                .HasSearchIndex(b =>
                {
                    b.IsDynamic().StoreSourceFor(e => e.Title, false);
                    b.IndexAsEmbedded(e => e.Awards).IsDynamic().StoreSourceFor(e => e.Wins, false);
                });
        });

        var bsonCollection = await PrepareDatabase(db,
        [
            new() { Title = "Action Man", Awards = new() { Wins = 3, Text = "Win!", Nominations = 4 } },
            new() { Title = "G.I. Joe", Awards = new() { Wins = 0, Text = "Never gonna win.", Nominations = 10 } }
        ]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : true,
                "fields" : {
                  "awards" : {
                    "type" : "document",
                    "dynamic" : true,
                    "fields" : { }
                  }
                }
              },
              "storedSource" : {
                "exclude" : ["awards.wins", "title"]
              }
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Range("awards.wins", SearchRangeV2Builder.Gt(1).Lt(5)))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("Action Man", results.Select(e => e["title"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/stored-source-definition/
    public async Task Store_source_all_fields_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>()
                .HasSearchIndex("default")
                .IsDynamic()
                .StoreAllSource();
        });

        var bsonCollection = await PrepareDatabase(db,
        [
            new() { Title = "Action Man", Awards = new() { Wins = 3, Text = "Win!", Nominations = 4 } },
            new() { Title = "G.I. Joe", Awards = new() { Wins = 0, Text = "Never gonna win.", Nominations = 10 } }
        ]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : true,
                "fields" : { }
              },
              "storedSource" : true
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Range("awards.wins", SearchRangeV2Builder.Gt(1).Lt(5)))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("Action Man", results.Select(e => e["title"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/synonyms/
    public async Task Synonyms_static_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>()
                .HasSearchIndex(b =>
                {
                    b.IsDynamic(false);
                    b.IndexAsString(e => e.Plot).UseAnalyzer(BuiltInSearchAnalyzer.LuceneEnglish);
                    b.AddSynonyms("my_synonyms", BuiltInSearchAnalyzer.LuceneEnglish, "synonymous_terms");
                });
        });

        var bsonCollection = await PrepareSynonymsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : false,
                "fields" : {
                  "plot" : {
                    "type" : "string",
                    "analyzer" : "lucene.english",
                    "indexOptions" : "offsets",
                    "store" : true,
                    "norms" : "include"
                  }
                }
              },
              "synonyms" : [{
                  "name" : "my_synonyms",
                  "source" : {
                    "collection" : "synonymous_terms"
                  },
                  "analyzer" : "lucene.english"
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("plot", "toilet", new() { Synonyms = "my_synonyms"}))
            .ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.Contains("American Pie", results.Select(e => e["title"].AsString));
        Assert.Contains("British Flan", results.Select(e => e["title"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/synonyms/
    public async Task Synonyms_dynamic_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>()
                .HasSearchIndex(b =>
                {
                    b.IsDynamic();
                    b.UseAnalyzer(BuiltInSearchAnalyzer.LuceneEnglish);
                    b.AddSynonyms("my_synonyms", BuiltInSearchAnalyzer.LuceneEnglish, "synonymous_terms");
                });
        });

        var bsonCollection = await PrepareSynonymsDatabase(db);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "analyzer" : "lucene.english",
              "mappings" : {
                "dynamic" : true,
                "fields" : { }
              },
              "synonyms" : [{
                  "name" : "my_synonyms",
                  "source" : {
                    "collection" : "synonymous_terms"
                  },
                  "analyzer" : "lucene.english"
                }]
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("plot", "toilet", new() { Synonyms = "my_synonyms"}))
            .ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.Contains("American Pie", results.Select(e => e["title"].AsString));
        Assert.Contains("British Flan", results.Select(e => e["title"].AsString));
    }

    [AtlasFact] // https://www.mongodb.com/docs/atlas/atlas-search/index-partition/
    public async Task Partitioned_index_example()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Movie>(), modelBuilder =>
        {
            modelBuilder.Entity<Movie>()
                .HasSearchIndex()
                .IsDynamic()
                .HasPartitions(4);
        });

        var bsonCollection = await PrepareDatabase(db,
        [
            new() { Title = "Action Man", Awards = new() { Wins = 3, Text = "Win!", Nominations = 4 } },
            new() { Title = "G.I. Joe", Awards = new() { Wins = 0, Text = "Never gonna win.", Nominations = 10 } }
        ]);

        SearchIndexTests.ValidateIndex(bsonCollection, expectedDocument:
            """
            {
              "mappings" : {
                "dynamic" : true,
                "fields" : { }
              },
              "numPartitions" : 4
            }
            """);

        var results = await bsonCollection.Aggregate()
            .Search(Builders<BsonDocument>.Search.Text("title", "Action"))
            .ToListAsync();

        Assert.Single(results);
        Assert.Contains("Action Man", results.Select(e => e["title"].AsString));
    }

    private async Task<IMongoCollection<BsonDocument>> PrepareSynonymsDatabase(SingleEntityDbContext<Movie> db)
    {
        var bsonCollection = await PrepareDatabase(db, async () =>
        {
            db.AddRange(
                new Movie { Title = "American Pie", Plot = "A bunch of chicks go to the restroom together." },
                new Movie { Title = "British Flan", Plot = "Some lasses go to the bogs together." });

            var client = db.GetService<IMongoClientWrapper>();
            var synonymsCollection = client.GetCollection<BsonDocument>("synonymous_terms");

            await synonymsCollection.InsertManyAsync([
                BsonDocument.Parse(
                    """
                    {
                      "mappingType": "equivalent",
                      "synonyms": ["toilet", "bog", "restroom"]
                    }
                    """)
            ]);
        });
        return bsonCollection;
    }

    private async Task<IMongoCollection<BsonDocument>> PrepareCarsDatabase(SingleEntityDbContext<Car> db)
        => await PrepareDatabase(db,
        [
            new()
            {
                Id = 1,
                Subject = new()
                {
                    En = "It is better to equip our cars to understand the causes of the accident.",
                    Fr = "Mieux équiper nos voitures pour comprendre les causes d'un accident.",
                    He = "עדיף לצייד את המכוניות שלנו כדי להבין את הגורמים לתאונה."
                }
            },
            new()
            {
                Id = 2,
                Subject = new()
                {
                    En = "The best time to do this is immediately after you've filled up with fuel",
                    Fr = "Le meilleur moment pour le faire c'est immédiatement après que vous aurez fait le plein de carburant.",
                    He = "הזמן הטוב ביותר לעשות זאת הוא מיד לאחר שמילאת דלק."
                }
            }
        ]);

    private async Task<IMongoCollection<BsonDocument>> PrepareMeetingsDatabase(SingleEntityDbContext<Meeting> db)
        => await PrepareDatabase(db,
        [
            new()
            {
                Id = 1,
                UpdatedBy =
                    new()
                    {
                        LastName = "AUERBACH", FirstName = "Siân", Email = "auerbach@example.com", Phone = "(123)-456-7890"
                    },
                Title = "The team's weekly meeting",
                Message = "try to siGn-In",
                Text = new()
                {
                    EnUs = "<head> This page deals with department meetings.</head>",
                    SvFi = "Den här sidan behandlar avdelningsmöten",
                    FrCa = "Cette page traite des réunions de département"
                },
                Active = false,
            },
            new()
            {
                Id = 2,
                UpdatedBy =
                    new() { LastName = "OHRBACH", FirstName = "Noël", Email = "ohrbach@example.com", Phone = "(123)-456-0987" },
                Title = "The check-in with sales team",
                Message = "do not forget to SIGN-IN. See ① for details.",
                Text = new()
                {
                    EnUs = "The head of the sales department spoke first.",
                    FaIr = "ابتدا رئیس بخش فروش صحبت کرد",
                    SvFi = "Först talade chefen för försäljningsavdelningen"
                },
                Active = true,
            },
            new()
            {
                Id = 3,
                UpdatedBy = new()
                {
                    LastName = "LEWINSKY", FirstName = "Brièle", Email = "lewinsky@example.com", Phone = "(123).456.9870"
                },
                Title = "The regular board meeting",
                Message = "try to sign-in",
                Text = new() { EnUs = "<body>We'll head out to the conference room by noon.</body>" }
            },
            new()
            {
                Id = 4,
                UpdatedBy =
                    new()
                    {
                        LastName = "LEVINSKI",
                        FirstName = "François",
                        Email = "levinski@example.com",
                        Phone = "(123).456.8907"
                    },
                Title = "The daily huddle on tHe StandUpApp2",
                Message = "write down your signature or phone №",
                Text = new()
                {
                    EnUs = "<body>This page has been updated with the items on the agenda.</body>",
                    EsMx = "La página ha sido actualizada con los puntos de la agenda.",
                    PlPl = "Strona została zaktualizowana o punkty porządku obrad."
                }
            }
        ]);

    private async Task<IMongoCollection<BsonDocument>> PrepareCompaniesDatabase(SingleEntityDbContext<Company> db)
        => await PrepareDatabase(db,
        [
            new()
            {
                Name = "Bookface",
                CategoryCode = "social",
                Relationships =
                {
                    new() { Title = "One", IsPast = false, Person = new() { FirstName = "Arthur", Permalink = "A" }},
                    new() { Title = "Two", IsPast = false, Person = new() { FirstName = "Damien", Permalink = "D" } }
                },
                FundingRounds =
                {
                    new() { RoundCode = "1", RaisedAmount = 1.0m, RaisedCurrencyCode = "USD" },
                    new() { RoundCode = "2", RaisedAmount = 2.0m, RaisedCurrencyCode = "USD" }
                },
                Offices =
                {
                    new() { CountryCode = "UK", StateCode = "NO" }, new() { CountryCode = "UK", StateCode = "NO" }
                },
                Products =
                {
                    new() { Name = "Marmite", Permalink = "M" }, new() { Name = "Bovril", Permalink = "B" }
                }
            },
            new()
            {
                Name = "Recordface",
                CategoryCode = "social",
                Relationships =
                {
                    new() { Title = "Three", IsPast = false, Person = new() { FirstName = "Boris", Permalink = "B" }},
                    new() { Title = "Four", IsPast = false, Person = new() { FirstName = "Robert", Permalink = "R" } }
                },
                FundingRounds =
                {
                    new() { RoundCode = "3", RaisedAmount = 1.0m, RaisedCurrencyCode = "GBP" },
                    new() { RoundCode = "4", RaisedAmount = 2.0m, RaisedCurrencyCode = "GBP" }
                },
                Offices =
                {
                    new() { CountryCode = "US", StateCode = "IA" }, new() { CountryCode = "US", StateCode = "WA" }
                },
                Products =
                {
                    new() { Name = "Vegemite", Permalink = "V" }, new() { Name = "Bovril", Permalink = "B" }
                }
            },
        ]);

    private Task<IMongoCollection<BsonDocument>> PrepareDatabase<TEntity>(
        SingleEntityDbContext<TEntity> db, IEnumerable<TEntity> entities)
        where TEntity : class
        => PrepareDatabase(db, () =>
        {
            db.AddRange(entities);
            return Task.CompletedTask;
        });

    private Task<IMongoCollection<BsonDocument>> PrepareDatabase<TEntity>(
        SingleEntityDbContext<TEntity> db, Func<Task> seed)
        where TEntity : class
        => PrepareDatabase(database, db, seed);

    internal static async Task<IMongoCollection<BsonDocument>> PrepareDatabase<TEntity>(
        AtlasTemporaryDatabaseFixture fixture, SingleEntityDbContext<TEntity> db, Func<Task> seed)
        where TEntity : class
    {
        await db.Database.EnsureCreatedAsync(new MongoDatabaseCreationOptions(CreateMissingSearchIndexes: false));
        await seed();
        await db.SaveChangesAsync();

        await db.Database.CreateMissingSearchIndexesAsync();
        await db.Database.WaitForSearchIndexesAsync();

        return fixture.GetCollection<BsonDocument>(db.CollectionNamespace);
    }

    public class Movie
    {
        public ObjectId Id { get; set; }

        [BsonElement("title")] public string Title { get; set; }
        [BsonElement("plot")] public string Plot { get; set; }
        [BsonElement("fullplot")] public string FullPlot { get; set; }
        [BsonElement("genres")] public string[] Genres { get; set; }
        [BsonElement("released")] public DateTime Released { get; set; }
        [BsonElement("year")] public int Year { get; set; }
        [BsonElement("representative")] public double Representative { get; set; }
        [BsonElement("awards")] public EmbeddedAwards? Awards { get; set; }
        [BsonElement("tomatoes")] public EmbeddedTomatoes Tomatoes { get; set; }

        public class EmbeddedAwards
        {
            [BsonElement("wins")] public int Wins { get; set; }
            [BsonElement("nominations")] public int Nominations { get; set; }
            [BsonElement("text")] public string Text { get; set; }
        }

        public class EmbeddedTomatoes
        {
            [BsonElement("lastUpdated")] public DateTime LastUpdated { get; set; }
            [BsonElement("dvd")] public DateTime Dvd { get; set; }
            [BsonElement("viewer")] public EmbeddedViewer Viewer { get; set; }

            public class EmbeddedViewer
            {
                [BsonElement("rating")] public double Rating { get; set; }
                [BsonElement("numReviews")] public int Reviews { get; set; }
                [BsonElement("meter")] public int Meter { get; set; }
            }
        }
    }

    public class Car
    {
        public int Id { get; set; }

        [BsonElement("subject")] public EmbeddedSubject Subject { get; set; }

        public class EmbeddedSubject
        {
            [BsonElement("en")] public string En { get; set; }
            [BsonElement("fr")] public string Fr { get; set; }
            [BsonElement("he")] public string He { get; set; }
        }
    }

    public class Meeting
    {
        public int Id { get; set; }

        [BsonElement("page_updated_by")] public EmbeddedContact UpdatedBy { get; set; }
        [BsonElement("title")] public string Title { get; set; }
        [BsonElement("message")] public string Message { get; set; }
        [BsonElement("text")] public EmbeddedText Text { get; set; }
        [BsonElement("active")] public bool? Active { get; set; }

        public class EmbeddedContact
        {
            [BsonElement("last_name")] public string LastName { get; set; }
            [BsonElement("first_name")] public string FirstName { get; set; }
            [BsonElement("email")] public string Email { get; set; }
            [BsonElement("phone")] public string Phone { get; set; }
        }

        public class EmbeddedText
        {
            [BsonElement("en_US")] public string? EnUs { get; set; }
            [BsonElement("fa_IR")] public string? FaIr { get; set; }
            [BsonElement("sv_FI")] public string? SvFi { get; set; }
            [BsonElement("fr_CA")] public string? FrCa { get; set; }
            [BsonElement("es_MX")] public string? EsMx { get; set; }
            [BsonElement("pl_PL")] public string? PlPl { get; set; }
        }
    }

    public class Company
    {
        public ObjectId Id { get; set; }

        [BsonElement("name")] public string Name { get; set; }
        [BsonElement("category_code")] public string CategoryCode { get; set; }
        [BsonElement("products")] public List<EmbeddedProduct> Products { get; } = new();
        [BsonElement("relationships")] public List<EmbeddedRelationship> Relationships { get; } = new();
        [BsonElement("offices")] public List<EmbeddedOffice> Offices { get; } = new();
        [BsonElement("funding_rounds")] public List<EmbeddedFundingRound> FundingRounds { get; } = new();

        public class EmbeddedProduct
        {
            [BsonElement("name")] public string Name { get; set; }
            [BsonElement("permalink")] public string Permalink { get; set; }
        }

        public class EmbeddedRelationship
        {
            [BsonElement("is_past")] public bool IsPast { get; set; }
            [BsonElement("title")] public string Title { get; set; }
            [BsonElement("person")] public EmbeddedPerson Person { get; set; }

            public class EmbeddedPerson
            {
                [BsonElement("first_name")] public string FirstName { get; set; }
                [BsonElement("last_name")] public string LastName { get; set; }
                [BsonElement("permalink")] public string Permalink { get; set; }
            }
        }

        public class EmbeddedOffice
        {
            [BsonElement("country_code")] public string CountryCode { get; set; }
            [BsonElement("state_code")] public string StateCode { get; set; }
        }

        public class EmbeddedFundingRound
        {
            [BsonElement("round_code")] public string RoundCode { get; set; }
            [BsonElement("raised_currency_code")] public string RaisedCurrencyCode { get; set; }
            [BsonElement("raised_amount")] public decimal RaisedAmount { get; set; }
        }
    }
}
