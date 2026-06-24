using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.Benchmarks;

public class FlatItem
{
    public ObjectId Id { get; set; }
    public int Count { get; set; }
    public long Big { get; set; }
    public string Name { get; set; } = "";
    public bool Active { get; set; }
    public double Rate { get; set; }
}

public class Review
{
    public ObjectId Id { get; set; }
    public int Stars { get; set; }
    public ObjectId ProductId { get; set; }   // FK on the dependent (Review)
    public Product? Product { get; set; }      // reference navigation to the principal
}

public class Product
{
    public ObjectId Id { get; set; }
    public string Title { get; set; } = "";
}

public class BenchmarkDbContext : DbContext
{
    public BenchmarkDbContext(DbContextOptions options) : base(options)
    {
    }

    public DbSet<FlatItem> FlatItems => Set<FlatItem>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<WideEntity> Wides => Set<WideEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FlatItem>();
        modelBuilder.Entity<Review>().HasOne(r => r.Product).WithMany().HasForeignKey(r => r.ProductId);

        modelBuilder.Entity<Account>();
        modelBuilder.Entity<WideEntity>();
        modelBuilder.Entity<Order>(b =>
        {
            b.HasOne(o => o.Account).WithMany().HasForeignKey(o => o.AccountId);
            b.OwnsOne(o => o.Shipping, s => s.OwnsOne(si => si.Address));
            b.OwnsMany(o => o.Lines, l =>
            {
                l.OwnsOne(li => li.Meta);
                l.OwnsMany(li => li.Discounts);
            });
        });
    }
}
