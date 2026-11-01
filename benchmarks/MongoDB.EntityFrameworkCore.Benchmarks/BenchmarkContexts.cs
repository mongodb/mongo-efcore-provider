using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace MongoDB.EntityFrameworkCore.Benchmarks;

internal static class BenchmarkContexts
{
    // The pool behind AddDbContextPool: AddPooledDbContextFactory over a private service collection, configured with the extensions of an
    // existing options object so each config keeps its own settings (query mode, warnings). Disposing a context the factory hands out
    // returns it to the pool.
    public static IDbContextFactory<BenchmarkDbContext> Pool(DbContextOptions<BenchmarkDbContext> options)
        => new ServiceCollection()
            .AddPooledDbContextFactory<BenchmarkDbContext>(builder =>
            {
                foreach (var extension in options.Extensions)
                {
                    ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(extension);
                }
            })
            .BuildServiceProvider()
            .GetRequiredService<IDbContextFactory<BenchmarkDbContext>>();
}
