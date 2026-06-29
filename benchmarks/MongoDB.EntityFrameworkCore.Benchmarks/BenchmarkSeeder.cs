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

    public static void SeedExtended(BenchmarkDbContext ctx, int accountCount, int orderCount, int wideCount)
    {
        ctx.Database.EnsureCreated();

        var accounts = new List<Account>(accountCount);
        for (var i = 0; i < accountCount; i++)
        {
            var a = new Account { Name = "account-" + i, Tier = (i % 3) == 0 ? "gold" : "standard" };
            accounts.Add(a);
            ctx.Accounts.Add(a);
        }
        ctx.SaveChanges();

        for (var i = 0; i < orderCount; i++)
        {
            var order = new Order
            {
                Code = "order-" + i,
                AccountId = accounts[i % accounts.Count].Id,
                Shipping = new ShippingInfo
                {
                    Carrier = (i % 2) == 0 ? "air" : "ground",
                    Address = new OrderAddress { Street = "street-" + i, City = "city-" + (i % 50), Zip = 10_000 + (i % 2000) }
                }
            };
            for (var j = 0; j < 3; j++)
            {
                var line = new LineItem
                {
                    Sku = $"sku-{i}-{j}",
                    Qty = j + 1,
                    Meta = new ItemMeta { Category = "cat-" + (j % 4), Weight = (j + 1) * 0.25 }
                };
                for (var k = 0; k < 2; k++)
                {
                    line.Discounts.Add(new Discount { Kind = "k" + k, Amount = 0.5m * (k + 1) });
                }
                order.Lines.Add(line);
            }
            ctx.Orders.Add(order);
        }

        for (var i = 0; i < wideCount; i++)
        {
            ctx.Wides.Add(new WideEntity().Fill(i));
        }
        ctx.SaveChanges();
    }
}
