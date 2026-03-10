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
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Entities.Guides;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection(nameof(ReadOnlySampleGuidesFixture))]
public class ProjectionTests(ReadOnlySampleGuidesFixture database)
    : IDisposable, IAsyncDisposable
{
    private readonly GuidesDbContext _db = GuidesDbContext.Create(database.MongoDatabase);

    [Fact]
    public void Select_projection_no_op()
    {
        var results = _db.Planets.Take(10).Select(p => p).ToArray();
        Assert.Equal(8, results.Length);
        Assert.All(results, r =>
        {
            Assert.NotNull(r.name);
            Assert.InRange(r.orderFromSun, 1, 8);
        });
    }

    [Fact]
    public void Select_projection_to_anonymous()
    {
        var results = _db.Planets.Take(10).Select(p => new {Name = p.name, Order = p.orderFromSun});
        Assert.All(results, r =>
        {
            Assert.NotNull(r.Name);
            Assert.InRange(r.Order, 1, 8);
        });
    }

    [Fact]
    public void Select_projection_calculated()
    {
        var results = _db.Planets.Select(p => new { Total = p.orderFromSun + p.orderFromSun }).ToList();

        Assert.Equal(8, results.Count);
        Assert.All(results, r => Assert.InRange(r.Total, 2, 16));
        Assert.Contains(results, r => r.Total == 6); // Earth: 3 + 3
    }

    [Fact]
    public void Select_projection_to_anonymous_via_mql_field()
    {
        var results = _db.Planets.Take(10).Select(p => new
        {
            Name = Mql.Field(p, "name", StringSerializer.Instance),
            Order = Mql.Field(p, "orderFromSun", Int32Serializer.Instance)
        });
        Assert.All(results, r =>
        {
            Assert.NotNull(r.Name);
            Assert.InRange(r.Order, 1, 8);
        });
    }

    [Fact]
    public void Select_projection_to_tuple()
    {
        var results = _db.Planets.Take(10).Select(p => Tuple.Create(p.name, p.orderFromSun, p.hasRings));
        Assert.All(results, r =>
        {
            Assert.NotNull(r.Item1);
            Assert.InRange(r.Item2, 1, 8);
        });
    }

    [Fact]
    public void Select_projection_to_constructor_initializer()
    {
        var results = _db.Planets.Take(10).Select(p => new NamedContainer<Planet> {Name = p.name, Item = p});
        Assert.All(results, r => { Assert.Equal(r.Name, r.Item?.name); });
    }

    [Fact]
    public void Select_projection_to_constructor_params()
    {
        var results = _db.Planets.Take(10).Select(p => new NamedContainer<Planet>(p, p.name));
        Assert.All(results, r => { Assert.Equal(r.Name, r.Item?.name); });
    }

    [Fact]
    public void Select_projection_to_constructor_params_and_initializer()
    {
        var results = _db.Planets.Take(10).Select(p => new NamedContainer<Planet>(p) {Name = p.name});
        Assert.All(results, r => { Assert.Equal(r.Name, r.Item?.name); });
    }

    [Fact]
    public void Select_projection_entity_and_scalar_field()
    {
        var results = _db.Planets.Take(10).Select(p => new { Planet = p, Name = p.name }).ToList();
        Assert.Equal(8, results.Count);
        Assert.All(results, r =>
        {
            Assert.NotNull(r.Planet);
            Assert.Equal(r.Name, r.Planet.name);
            Assert.InRange(r.Planet.orderFromSun, 1, 8);
        });
    }

    [Fact]
    public void Select_projection_entity_and_multiple_scalar_fields()
    {
        var results = _db.Planets.Take(10).Select(p => new { Planet = p, p.name, p.orderFromSun }).ToList();
        Assert.Equal(8, results.Count);
        Assert.All(results, r =>
        {
            Assert.NotNull(r.Planet);
            Assert.Equal(r.name, r.Planet.name);
            Assert.Equal(r.orderFromSun, r.Planet.orderFromSun);
        });
    }

    [Fact]
    public void Select_projection_multiple_scalar_fields()
    {
        var results = _db.Planets.Select(p => new { p.name, p.orderFromSun, p.hasRings }).ToList();
        Assert.Equal(8, results.Count);
        Assert.All(results, r =>
        {
            Assert.NotNull(r.name);
            Assert.InRange(r.orderFromSun, 1, 8);
        });
        Assert.Contains(results, r => r.hasRings);
        Assert.Contains(results, r => !r.hasRings);
    }

    [Fact]
    public void Select_projection_single_scalar_field()
    {
        var results = _db.Planets.Select(p => new { p.name }).ToList();
        Assert.Equal(8, results.Count);
        Assert.All(results, r => Assert.NotNull(r.name));
        Assert.Contains(results, r => r.name == "Earth");
    }

    [Fact]
    public void Select_projection_with_constant()
    {
        var results = _db.Planets.Take(10).Select(p => new { p.name, Source = "SolarSystem" }).ToList();
        Assert.Equal(8, results.Count);
        Assert.All(results, r =>
        {
            Assert.NotNull(r.name);
            Assert.Equal("SolarSystem", r.Source);
        });
    }

    [Fact]
    public void Select_projection_calculated_and_direct()
    {
        var results = _db.Planets.Select(p => new { p.name, DoubleOrder = p.orderFromSun * 2 }).ToList();
        Assert.Equal(8, results.Count);
        Assert.All(results, r =>
        {
            Assert.NotNull(r.name);
            Assert.InRange(r.DoubleOrder, 2, 16);
        });
        Assert.Contains(results, r => r.name == "Earth" && r.DoubleOrder == 6);
    }

    [Fact]
    public void Select_projection_entity_to_named_container_with_scalar()
    {
        var results = _db.Planets.Take(10)
            .Select(p => new NamedContainer<Planet> { Name = p.name, Item = p })
            .ToList();
        Assert.Equal(8, results.Count);
        Assert.All(results, r =>
        {
            Assert.NotNull(r.Item);
            Assert.Equal(r.Name, r.Item!.name);
            Assert.InRange(r.Item.orderFromSun, 1, 8);
        });
    }

    [Fact]
    public void Select_projection_complex_combination_nested()
    {
        var results = _db.Planets.Take(10).Select(p => new
        {
            Name = p.name,
            Order = p.orderFromSun,
            Planet1 = p,
            Planet2 = p,
            DoubleOrder = p.orderFromSun * 2,
            RingScore = p.hasRings ? 10 : 0,
            OrderSquared = p.orderFromSun * p.orderFromSun,
            OrderPlusTen = p.orderFromSun + 10,
            Sub = new
            {
                SubName = p.name,
                SubPlanet = p,
                SubCalc = p.orderFromSun * 3,
                SubRingScore = p.hasRings ? 100 : 0
            }
        }).ToList();

        Assert.Equal(8, results.Count);
        Assert.All(results, r =>
        {
            Assert.NotNull(r.Name);
            Assert.InRange(r.Order, 1, 8);
            Assert.NotNull(r.Planet1);
            Assert.NotNull(r.Planet2);
            Assert.Equal(r.Name, r.Planet1.name);
            Assert.Equal(r.Name, r.Planet2.name);
            Assert.Equal(r.Order * 2, r.DoubleOrder);
            Assert.Equal(r.Order * r.Order, r.OrderSquared);
            Assert.Equal(r.Order + 10, r.OrderPlusTen);
            Assert.True(r.RingScore == 0 || r.RingScore == 10);
            Assert.Equal(r.Name, r.Sub.SubName);
            Assert.Equal(r.Name, r.Sub.SubPlanet.name);
            Assert.Equal(r.Order * 3, r.Sub.SubCalc);
            Assert.True(r.Sub.SubRingScore == 0 || r.Sub.SubRingScore == 100);
        });

        var earth = results.Single(r => r.Name == "Earth");
        Assert.Equal(3, earth.Order);
        Assert.Equal(6, earth.DoubleOrder);
        Assert.Equal(9, earth.OrderSquared);
        Assert.Equal(0, earth.RingScore);
    }

    [Fact]
    public void Select_projection_nested_entity()
    {
        var results = _db.Planets.Take(10).Select(p => new
        {
            Planet = p,
            Sub = new
            {
                SubPlanet = p,
            }
        }).ToList();

        Assert.Equal(8, results.Count);
        Assert.All(results, r =>
        {
            Assert.NotNull(r.Planet);
            Assert.NotNull(r.Sub.SubPlanet);
            Assert.Equal(r.Planet.name, r.Sub.SubPlanet.name);
        });
    }

    [Fact]
    public void Select_projection_nested_entity_and_scalar()
    {
        var results = _db.Planets.Take(10).Select(p => new
        {
            Planet = p,
            DoubleOrder = p.orderFromSun * 2,
            Sub = new
            {
                SubPlanet = p,
                SubDoubleOrder = p.orderFromSun * 2
            }
        }).ToList();

        Assert.Equal(8, results.Count);
        Assert.All(results, r =>
        {
            Assert.NotNull(r.Planet);
            Assert.Equal(r.Planet.orderFromSun * 2, r.DoubleOrder);
            Assert.NotNull(r.Sub.SubPlanet);
            Assert.Equal(r.Planet.name, r.Sub.SubPlanet.name);
            Assert.Equal(r.DoubleOrder, r.Sub.SubDoubleOrder);
        });
    }

    [Fact]
    public void Select_projection_complex_combination_flat()
    {
        var results = _db.Planets.Take(10).Select(p => new
        {
            Name = p.name,
            Order = p.orderFromSun,
            Planet1 = p,
            Planet2 = p,
            DoubleOrder = p.orderFromSun * 2,
            RingScore = p.hasRings ? 10 : 0,
            OrderSquared = p.orderFromSun * p.orderFromSun,
            OrderPlusTen = p.orderFromSun + 10
        }).ToList();

        Assert.Equal(8, results.Count);
        Assert.All(results, r =>
        {
            Assert.NotNull(r.Name);
            Assert.InRange(r.Order, 1, 8);
            Assert.NotNull(r.Planet1);
            Assert.NotNull(r.Planet2);
            Assert.Equal(r.Name, r.Planet1.name);
            Assert.Equal(r.Name, r.Planet2.name);
            Assert.Equal(r.Order * 2, r.DoubleOrder);
            Assert.Equal(r.Order * r.Order, r.OrderSquared);
            Assert.Equal(r.Order + 10, r.OrderPlusTen);
            Assert.True(r.RingScore == 0 || r.RingScore == 10);
        });

        var earth = results.Single(r => r.Name == "Earth");
        Assert.Equal(3, earth.Order);
        Assert.Equal(6, earth.DoubleOrder);
        Assert.Equal(9, earth.OrderSquared);
        Assert.Equal(0, earth.RingScore);
    }

    [Fact]
    public void Select_projection_mixed_ef_property_and_mql_field()
    {
        var results = _db.Planets.Select(p => new
        {
            Name = Mql.Field(p, "name", StringSerializer.Instance),
            Order = EF.Property<int>(p, "orderFromSun"),
            HasRings = Mql.Field(p, "hasRings", BooleanSerializer.Instance),
            DirectName = p.name
        }).ToList();

        Assert.Equal(8, results.Count);
        Assert.All(results, r =>
        {
            Assert.NotNull(r.Name);
            Assert.Equal(r.Name, r.DirectName);
            Assert.InRange(r.Order, 1, 8);
        });
        Assert.Contains(results, r => r.Name == "Earth" && r.Order == 3 && !r.HasRings);
    }

    [Fact]
    public void Select_projection_calculated_from_ef_property_and_mql_field()
    {
        var results = _db.Planets.Select(p => new
        {
            Sum = EF.Property<int>(p, "orderFromSun") + p.orderFromSun,
            Label = Mql.Field(p, "hasRings", BooleanSerializer.Instance) ? "Ringed" : "Plain"
        }).ToList();

        Assert.Equal(8, results.Count);
        Assert.All(results, r =>
        {
            Assert.InRange(r.Sum, 2, 16);
            Assert.True(r.Label == "Ringed" || r.Label == "Plain");
        });
        Assert.Contains(results, r => r.Sum == 6 && r.Label == "Plain"); // Earth: 3+3
        Assert.Contains(results, r => r.Label == "Ringed");
    }

    [Fact]
    public void Select_projection_standalone_ef_property()
    {
        var results = _db.Planets.Select(p => EF.Property<int>(p, "orderFromSun")).ToList();
        Assert.Equal(8, results.Count);
        Assert.Contains(3, results); // Earth
        Assert.Contains(1, results); // Mercury
        Assert.Contains(8, results); // Neptune
    }

    [Fact]
    public void Sum_with_ef_property_and_field()
    {
        // Sum of (orderFromSun + orderFromSun) across all 8 planets: (1+2+3+4+5+6+7+8)*2 = 72
        var result = _db.Planets.Sum(p => EF.Property<int>(p, "orderFromSun") + p.orderFromSun);
        Assert.Equal(72, result);
    }

    [Fact]
    public void Sum_with_mql_field_and_field()
    {
        // Sum of (orderFromSun + orderFromSun) across all 8 planets: (1+2+3+4+5+6+7+8)*2 = 72
        var result = _db.Planets.Sum(p => Mql.Field(p, "orderFromSun", Int32Serializer.Instance) + p.orderFromSun);
        Assert.Equal(72, result);
    }

    [Fact]
    public void Select_projection_entity_and_mql_field()
    {
        var results = _db.Planets.Take(10).Select(p => new
        {
            Planet = p,
            Name = Mql.Field(p, "name", StringSerializer.Instance)
        }).ToList();

        Assert.Equal(8, results.Count);
        Assert.All(results, r =>
        {
            Assert.NotNull(r.Planet);
            Assert.NotNull(r.Name);
            Assert.Equal(r.Name, r.Planet.name);
            Assert.InRange(r.Planet.orderFromSun, 1, 8);
        });
    }

    [Fact]
    public void Select_projection_entity_and_ef_property()
    {
        var results = _db.Planets.Take(10).Select(p => new
        {
            Planet = p,
            Name = EF.Property<string>(p, "name")
        }).ToList();

        Assert.Equal(8, results.Count);
        Assert.All(results, r =>
        {
            Assert.NotNull(r.Planet);
            Assert.NotNull(r.Name);
            Assert.Equal(r.Name, r.Planet.name);
            Assert.InRange(r.Planet.orderFromSun, 1, 8);
        });
    }

    [Fact]
    public void Select_projection_nested_anonymous_with_mql_field()
    {
        var results = _db.Planets
            .Where(p => p.name == "Earth")
            .Select(p => new
            {
                p.name,
                Sub = new
                {
                    Order = Mql.Field(p, "orderFromSun", Int32Serializer.Instance)
                }
            })
            .ToList();

        Assert.Single(results);
        Assert.Equal("Earth", results[0].name);
        Assert.Equal(3, results[0].Sub.Order);
    }

    [Fact]
    public void Select_projection_nested_anonymous_with_ef_property()
    {
        var results = _db.Planets
            .Where(p => p.name == "Earth")
            .Select(p => new
            {
                p.name,
                Sub = new
                {
                    Order = EF.Property<int>(p, "orderFromSun")
                }
            })
            .ToList();

        Assert.Single(results);
        Assert.Equal("Earth", results[0].name);
        Assert.Equal(3, results[0].Sub.Order);
    }

    [Fact]
    public void Select_projection_nested_anonymous_with_calculated_fields()
    {
        var results = _db.Planets
            .Where(p => p.name == "Earth")
            .Select(p => new
            {
                p.name,
                Sub = new
                {
                    DoubleOrder = p.orderFromSun * 2,
                    NameUpper = p.name.ToUpper()
                }
            })
            .ToList();

        Assert.Single(results);
        Assert.Equal("Earth", results[0].name);
        Assert.Equal(6, results[0].Sub.DoubleOrder);
        Assert.Equal("EARTH", results[0].Sub.NameUpper);
    }

    [Fact]
    public void Select_scalar_property()
    {
        var results = _db.Planets.Select(p => p.name).ToList();
        Assert.Equal(8, results.Count);
        Assert.All(results, name => Assert.NotNull(name));
        Assert.Contains(results, name => name == "Earth");
    }

    [Fact]
    public void Select_scalar_property_with_distinct()
    {
        var results = _db.Planets.Select(p => p.hasRings).Distinct().ToList();
        Assert.Equal(2, results.Count);
        Assert.Contains(true, results);
        Assert.Contains(false, results);
    }

    [Fact]
    public void Select_scalar_property_with_orderby_distinct()
    {
        var results = _db.Planets.OrderBy(p => p.orderFromSun).Select(p => p.name).Distinct().ToList();
        Assert.Equal(8, results.Count);
        Assert.All(results, name => Assert.NotNull(name));
        Assert.Contains("Earth", results);
        Assert.Contains("Mars", results);
    }

    private class PlanetWithLongOrder
    {
        public ObjectId _id { get; set; }
        public string name { get; set; } = null!;
        public long orderFromSun { get; set; }
        public bool hasRings { get; set; }
    }

    private SingleEntityDbContext<PlanetWithLongOrder> CreateLongOrderContext()
    {
        var collection = database.MongoDatabase.GetCollection<PlanetWithLongOrder>("planets");
        return SingleEntityDbContext.Create(collection, mb =>
            mb.Entity<PlanetWithLongOrder>().Property(e => e.orderFromSun).HasConversion<int>());
    }

    [Fact]
    public void Sum_with_value_converter()
    {
        using var db = CreateLongOrderContext();
        var result = db.Entities.Sum(p => p.orderFromSun);
        Assert.Equal(36L, result);
    }

    [Fact]
    public void Min_with_value_converter()
    {
        using var db = CreateLongOrderContext();
        var result = db.Entities.Min(p => p.orderFromSun);
        Assert.Equal(1L, result);
    }

    [Fact]
    public void Max_with_value_converter()
    {
        using var db = CreateLongOrderContext();
        var result = db.Entities.Max(p => p.orderFromSun);
        Assert.Equal(8L, result);
    }

    [Fact]
    public void Average_with_value_converter()
    {
        using var db = CreateLongOrderContext();
        var result = db.Entities.Average(p => p.orderFromSun);
        Assert.Equal(4.5, result);
    }

    [Fact]
    public void Count_with_value_converter_in_predicate()
    {
        using var db = CreateLongOrderContext();
        var result = db.Entities.Count(p => p.orderFromSun > 4L);
        Assert.Equal(4, result);
    }

    [Fact]
    public void Sum_with_value_converter_and_arithmetic_throws()
    {
        using var db = CreateLongOrderContext();
        Assert.ThrowsAny<Exception>(() => db.Entities.Sum(p => p.orderFromSun * 2));
    }

    [Fact]
    public void Sum_with_value_converter_via_ef_property()
    {
        using var db = CreateLongOrderContext();
        var result = db.Entities.Sum(p => EF.Property<long>(p, "orderFromSun"));
        Assert.Equal(36L, result);
    }

    [Fact]
    public void Select_projection_flat_with_value_converter()
    {
        using var db = CreateLongOrderContext();
        var results = db.Entities.Select(p => new { p.name, p.orderFromSun }).ToList();

        Assert.Equal(8, results.Count);
        Assert.All(results, r =>
        {
            Assert.NotNull(r.name);
            Assert.InRange(r.orderFromSun, 1L, 8L);
        });
        Assert.Contains(results, r => r.name == "Earth" && r.orderFromSun == 3L);
    }

    [Fact]
    public void Select_projection_calculated_with_value_converter_throws()
    {
        using var db = CreateLongOrderContext();
        Assert.ThrowsAny<Exception>(
            () => db.Entities.Select(p => new { p.name, Double = p.orderFromSun * 2 }).ToList());
    }

    [Fact]
    public void Select_projection_nested_with_value_converter()
    {
        using var db = CreateLongOrderContext();
        var results = db.Entities
            .Where(p => p.name == "Earth")
            .Select(p => new
            {
                p.name,
                Sub = new { p.orderFromSun }
            })
            .ToList();

        Assert.Single(results);
        Assert.Equal("Earth", results[0].name);
        Assert.Equal(3L, results[0].Sub.orderFromSun);
    }

    [Fact]
    public void Select_projection_nested_calculated_with_value_converter_throws()
    {
        using var db = CreateLongOrderContext();
        Assert.ThrowsAny<Exception>(
            () => db.Entities
                .Where(p => p.name == "Earth")
                .Select(p => new
                {
                    p.name,
                    Sub = new
                    {
                        Double = p.orderFromSun * 2,
                        NameUpper = p.name.ToUpper()
                    }
                })
                .ToList());
    }

    [Fact]
    public void Select_projection_with_value_converter_ef_property()
    {
        using var db = CreateLongOrderContext();
        var results = db.Entities.Select(p => new
        {
            p.name,
            Order = EF.Property<long>(p, "orderFromSun")
        }).ToList();

        Assert.Equal(8, results.Count);
        Assert.All(results, r =>
        {
            Assert.NotNull(r.name);
            Assert.InRange(r.Order, 1L, 8L);
        });
        Assert.Contains(results, r => r.name == "Earth" && r.Order == 3L);
    }

    [Fact]
    public void Select_projection_nested_with_value_converter_ef_property()
    {
        using var db = CreateLongOrderContext();
        var results = db.Entities
            .Where(p => p.name == "Earth")
            .Select(p => new
            {
                p.name,
                Sub = new
                {
                    Order = EF.Property<long>(p, "orderFromSun")
                }
            })
            .ToList();

        Assert.Single(results);
        Assert.Equal("Earth", results[0].name);
        Assert.Equal(3L, results[0].Sub.Order);
    }

    [Fact]
    public void Select_projection_entity_and_value_converter_scalar()
    {
        using var db = CreateLongOrderContext();
        var results = db.Entities.Take(10).Select(p => new
        {
            Planet = p,
            p.orderFromSun
        }).ToList();

        Assert.Equal(8, results.Count);
        Assert.All(results, r =>
        {
            Assert.NotNull(r.Planet);
            Assert.Equal(r.Planet.orderFromSun, r.orderFromSun);
            Assert.InRange(r.orderFromSun, 1L, 8L);
        });
    }

    [Fact]
    public void Select_projection_entity_and_value_converter_via_ef_property()
    {
        using var db = CreateLongOrderContext();
        var results = db.Entities.Take(10).Select(p => new
        {
            Planet = p,
            Order = EF.Property<long>(p, "orderFromSun")
        }).ToList();

        Assert.Equal(8, results.Count);
        Assert.All(results, r =>
        {
            Assert.NotNull(r.Planet);
            Assert.Equal(r.Planet.orderFromSun, r.Order);
            Assert.InRange(r.Order, 1L, 8L);
        });
    }

    [Fact]
    public void Select_projection_with_server_side_method_calls()
    {
        var results = _db.Planets.Select(p => new
        {
            Upper = p.name.ToUpper(),
            Lower = p.name.ToLower(),
            Len = p.name.Length
        }).ToList();

        Assert.Equal(8, results.Count);
        var earth = results.Single(r => r.Upper == "EARTH");
        Assert.Equal("earth", earth.Lower);
        Assert.Equal(5, earth.Len);
    }

    [Fact]
    public void Select_projection_with_conditional_expression()
    {
        var results = _db.Planets.Select(p => new
        {
            p.name,
            RingScore = p.hasRings ? 10 : 0
        }).ToList();

        Assert.Equal(8, results.Count);
        Assert.All(results, r => Assert.True(r.RingScore == 0 || r.RingScore == 10));
        Assert.Contains(results, r => r.name == "Saturn" && r.RingScore == 10);
        Assert.Contains(results, r => r.name == "Earth" && r.RingScore == 0);
    }

    [Fact]
    public void Select_projection_nested_anonymous_scalars_only()
    {
        var results = _db.Planets
            .Where(p => p.name == "Earth")
            .Select(p => new
            {
                p.name,
                Sub = new { p.orderFromSun, p.hasRings }
            })
            .ToList();

        Assert.Single(results);
        Assert.Equal("Earth", results[0].name);
        Assert.Equal(3, results[0].Sub.orderFromSun);
        Assert.False(results[0].Sub.hasRings);
    }

    [Fact]
    public void Select_projection_deeply_nested_anonymous()
    {
        var results = _db.Planets
            .Where(p => p.name == "Earth")
            .Select(p => new
            {
                p.name,
                L1 = new
                {
                    p.orderFromSun,
                    L2 = new
                    {
                        p.hasRings,
                        L3 = new { DoubleOrder = p.orderFromSun * 2 }
                    }
                }
            })
            .ToList();

        Assert.Single(results);
        Assert.Equal("Earth", results[0].name);
        Assert.Equal(3, results[0].L1.orderFromSun);
        Assert.False(results[0].L1.L2.hasRings);
        Assert.Equal(6, results[0].L1.L2.L3.DoubleOrder);
    }

    [Fact]
    public void Select_projection_to_value_tuple()
    {
        var results = _db.Planets.Take(10)
            .Select(p => new ValueTuple<string, int>(p.name, p.orderFromSun))
            .ToList();

        Assert.Equal(8, results.Count);
        Assert.All(results, r =>
        {
            Assert.NotNull(r.Item1);
            Assert.InRange(r.Item2, 1, 8);
        });
        Assert.Contains(results, r => r.Item1 == "Earth" && r.Item2 == 3);
    }

    [Fact]
    public void Select_scalar_aggregate_sum()
    {
        var result = _db.Planets.Sum(p => p.orderFromSun);
        Assert.Equal(36, result); // 1+2+3+4+5+6+7+8
    }

    [Fact]
    public void Select_scalar_aggregate_count()
    {
        var result = _db.Planets.Count();
        Assert.Equal(8, result);
    }

    [Fact]
    public void Select_scalar_aggregate_average()
    {
        var result = _db.Planets.Average(p => p.orderFromSun);
        Assert.Equal(4.5, result);
    }

    [Fact]
    public void Select_scalar_aggregate_min()
    {
        var result = _db.Planets.Min(p => p.orderFromSun);
        Assert.Equal(1, result);
    }

    [Fact]
    public void Select_scalar_aggregate_max()
    {
        var result = _db.Planets.Max(p => p.orderFromSun);
        Assert.Equal(8, result);
    }

    [Fact]
    public void Select_scalar_aggregate_any()
    {
        var result = _db.Planets.Any(p => p.hasRings);
        Assert.True(result);
    }

    [Fact]
    public void Select_scalar_aggregate_all()
    {
        var result = _db.Planets.All(p => p.orderFromSun > 0);
        Assert.True(result);
    }

    [Fact]
    public void Select_projection_with_subquery_count()
    {
        var results = _db.Planets
            .Where(p => p.name == "Earth")
            .Select(p => new
            {
                p.name,
                AtmosphereCount = p.mainAtmosphere.Count()
            })
            .ToList();

        Assert.Single(results);
        Assert.Equal("Earth", results[0].name);
        Assert.Equal(3, results[0].AtmosphereCount); // ["N", "O2", "Ar"]
    }

    [Fact]
    public void Select_projection_property_aggregate()
    {
        var results = _db.Planets
            .Where(p => p.name == "Earth")
            .Select(p => new
            {
                p.name,
                NameLength = p.name.Length
            })
            .ToList();

        Assert.Single(results);
        Assert.Equal("Earth", results[0].name);
        Assert.Equal(5, results[0].NameLength);
    }

    [Fact]
    public void Select_projection_complex_conditional_logic()
    {
        var results = _db.Planets.Select(p => new
        {
            p.name,
            Classification = p.orderFromSun <= 4 ? "Inner" : "Outer",
            RingDescription = p.hasRings ? "Has rings" : "No rings"
        }).ToList();

        Assert.Equal(8, results.Count);
        Assert.Contains(results, r => r.name == "Earth" && r.Classification == "Inner" && r.RingDescription == "No rings");
        Assert.Contains(results, r => r.name == "Saturn" && r.Classification == "Outer" && r.RingDescription == "Has rings");
    }

    [Fact]
    public void Select_projection_null_coalescing()
    {
        var results = _db.Moons.Select(m => new
        {
            m.name,
            Year = m.yearOfDiscovery ?? 0
        }).ToList();

        Assert.Equal(5, results.Count);
        var theMoon = results.Single(r => r.name == "The Moon");
        Assert.Equal(0, theMoon.Year);
        var triton = results.Single(r => r.name == "Triton");
        Assert.Equal(1846, triton.Year);
    }

    [Fact]
    public void Select_projection_type_cast()
    {
        var results = _db.Planets
            .Where(p => p.name == "Earth")
            .Select(p => new
            {
                p.name,
                OrderAsDouble = (double)p.orderFromSun
            })
            .ToList();

        Assert.Single(results);
        Assert.Equal("Earth", results[0].name);
        Assert.Equal(3.0, results[0].OrderAsDouble);
    }

    [Fact]
    public void Select_projection_after_where_orderby_skip_take()
    {
        var results = _db.Planets
            .Where(p => p.hasRings)
            .OrderBy(p => p.orderFromSun)
            .Skip(1)
            .Take(2)
            .Select(p => new { p.name, p.orderFromSun })
            .ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal("Saturn", results[0].name);
        Assert.Equal(6, results[0].orderFromSun);
        Assert.Equal("Uranus", results[1].name);
        Assert.Equal(7, results[1].orderFromSun);
    }

    private static string FormatLabel(string name) => $"[{name}]";

    [Fact]
    public void Select_projection_client_side_method_not_supported()
    {
        Assert.ThrowsAny<Exception>(() =>
            _db.Planets.Select(p => new { Label = FormatLabel(p.name) }).ToList());
    }

    [Fact]
    public void Select_projection_mixed_server_client_not_supported()
    {
        Assert.ThrowsAny<Exception>(() =>
            _db.Planets.Select(p => new
            {
                Upper = p.name.ToUpper(),
                Label = FormatLabel(p.name)
            }).ToList());
    }

    [Fact]
    public void Select_many_not_supported()
    {
        Assert.ThrowsAny<Exception>(() =>
            _db.Planets
                .SelectMany(p => p.mainAtmosphere, (p, a) => new { p.name, Atmosphere = a })
                .ToList());
    }

    [Fact]
    public void Select_projection_nested_collection_to_list()
    {
        // Sub-query filtering within projections is not currently supported
        Assert.ThrowsAny<Exception>(() =>
            _db.Planets.Select(p => new
            {
                p.name,
                Gases = p.mainAtmosphere.Where(a => a.Length > 1).ToList()
            }).ToList());
    }

    [Fact]
    public void Select_projection_group_by_not_supported()
    {
        Assert.ThrowsAny<Exception>(() =>
            _db.Planets
                .GroupBy(p => p.hasRings)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToList());
    }

    private class OrderWithDates
    {
        public ObjectId _id { get; set; }
        public string name { get; set; } = null!;
        public DateTime createdAt { get; set; }
        public DateTime? shippedAt { get; set; }
    }

    private SingleEntityDbContext<OrderWithDates> CreateDateTimeContext()
    {
        var collection = database.MongoDatabase.GetCollection<OrderWithDates>("projection_test_dates");
        if (collection.CountDocuments(FilterDefinition<OrderWithDates>.Empty) == 0)
        {
            collection.InsertMany(
            [
                new()
                {
                    _id = ObjectId.GenerateNewId(), name = "Order1",
                    createdAt = new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc),
                    shippedAt = new DateTime(2024, 7, 20, 14, 0, 0, DateTimeKind.Utc)
                },
                new()
                {
                    _id = ObjectId.GenerateNewId(), name = "Order2",
                    createdAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    shippedAt = null
                }
            ]);
        }

        return SingleEntityDbContext.Create(collection);
    }

    [Fact]
    public void Select_projection_datetime_component()
    {
        using var db = CreateDateTimeContext();
        var results = db.Entities
            .Where(e => e.name == "Order1")
            .Select(e => new { e.name, Year = e.createdAt.Year, Month = e.createdAt.Month })
            .ToList();

        Assert.Single(results);
        Assert.Equal(2024, results[0].Year);
        Assert.Equal(6, results[0].Month);
    }

    [Fact]
    public void Select_projection_datetime_arithmetic_not_supported()
    {
        using var db = CreateDateTimeContext();
        Assert.ThrowsAny<Exception>(() =>
            db.Entities
                .Where(e => e.name == "Order1")
                .Select(e => new { Duration = e.shippedAt!.Value - e.createdAt })
                .ToList());
    }

    [Fact]
    public void Select_projection_object_array()
    {
        // Heterogeneous object arrays are not supported by LINQ V3 (items need same serializer)
        Assert.ThrowsAny<Exception>(() =>
            _db.Planets.Select(p => new object[] { p.name, p.orderFromSun }).ToList());
    }

    [Fact]
    public void Select_projection_chained_selects()
    {
        var results = _db.Planets
            .Select(p => new { p.name, IsOuter = p.orderFromSun > 4 })
            .Select(x => new { x.name, Label = x.IsOuter ? "Outer" : "Inner" })
            .ToList();

        Assert.Equal(8, results.Count);
        Assert.Contains(results, r => r.name == "Earth" && r.Label == "Inner");
        Assert.Contains(results, r => r.name == "Jupiter" && r.Label == "Outer");
    }

    private enum PlanetCategory { Terrestrial, GasGiant, IceGiant }

    private class CategorizedPlanet
    {
        public ObjectId _id { get; set; }
        public string name { get; set; } = null!;
        public PlanetCategory category { get; set; }
    }

    private SingleEntityDbContext<CategorizedPlanet> CreateEnumContext()
    {
        var collection = database.MongoDatabase.GetCollection<CategorizedPlanet>("projection_test_enums");
        if (collection.CountDocuments(FilterDefinition<CategorizedPlanet>.Empty) == 0)
        {
            collection.InsertMany(
            [
                new() { _id = ObjectId.GenerateNewId(), name = "Earth", category = PlanetCategory.Terrestrial },
                new() { _id = ObjectId.GenerateNewId(), name = "Jupiter", category = PlanetCategory.GasGiant },
                new() { _id = ObjectId.GenerateNewId(), name = "Neptune", category = PlanetCategory.IceGiant }
            ]);
        }

        return SingleEntityDbContext.Create(collection);
    }

    [Fact]
    public void Select_projection_enum()
    {
        using var db = CreateEnumContext();
        var results = db.Entities.Select(e => new
        {
            e.name,
            e.category,
            IsGasGiant = e.category == PlanetCategory.GasGiant
        }).ToList();

        Assert.Equal(3, results.Count);
        Assert.Contains(results, r =>
            r.name == "Earth" && r.category == PlanetCategory.Terrestrial && !r.IsGasGiant);
        Assert.Contains(results, r =>
            r.name == "Jupiter" && r.category == PlanetCategory.GasGiant && r.IsGasGiant);
    }

    [Fact]
    public void Select_projection_owned_type()
    {
        // Projecting owned entities without the owner requires AsNoTracking
        var results = _db.Planets.AsNoTracking().Select(p => new { p.name, Car = p.parkingCar }).ToList();

        Assert.Equal(8, results.Count);
        Assert.All(results, r => Assert.NotNull(r.name));
    }

    [Fact]
    public void Select_projection_ternary_null_check_with_first_or_default()
    {
        var result = _db.Planets
            .OrderBy(p => p.orderFromSun)
            .Where(p => (p != null ? p.name : null) != null)
            .Select(p => p != null ? p.name : null)
            .FirstOrDefault();

        Assert.NotNull(result);
        Assert.Equal("Mercury", result);
    }

    public void Dispose()
        => _db.Dispose();

    public async ValueTask DisposeAsync()
        => await _db.DisposeAsync();
}
