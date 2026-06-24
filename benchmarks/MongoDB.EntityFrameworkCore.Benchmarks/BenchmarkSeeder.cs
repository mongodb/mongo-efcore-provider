namespace MongoDB.EntityFrameworkCore.Benchmarks;

public static class BenchmarkSeeder
{
    // Deterministic: same inputs → same documents, so DriverOnly and EF read identical data.
    public static void Seed(BenchmarkDbContext ctx, int flatCount, int productCount, int reviewCount)
    {
        ctx.Database.EnsureCreated();

        for (var i = 0; i < flatCount; i++)
        {
            ctx.FlatItems.Add(new FlatItem
            {
                Count = i,
                Big = 1_000_000_000L + i,
                Name = "flat-" + i,
                Active = (i % 2) == 0,
                Rate = i * 0.5
            });
        }
        ctx.SaveChanges();

        var products = new List<Product>(productCount);
        for (var i = 0; i < productCount; i++)
        {
            var product = new Product { Title = "product-" + i };
            products.Add(product);
            ctx.Products.Add(product);
        }
        ctx.SaveChanges();

        for (var i = 0; i < reviewCount; i++)
        {
            ctx.Reviews.Add(new Review
            {
                Stars = (i % 5) + 1,
                ProductId = products[i % products.Count].Id
            });
        }
        ctx.SaveChanges();
    }
}
