using DependencyInjection.Example.API;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Security.Authentication;

var builder = WebApplication.CreateBuilder(args);

const string ConnectionStringKey = "MyDatabase";
const string DatabaseName = "MyDatabase";

var nosqlConnectionString = builder.Configuration.GetConnectionString(ConnectionStringKey)!;
var mongoUrl = new MongoUrl(nosqlConnectionString);
var settings = MongoClientSettings.FromUrl(mongoUrl);
settings.SslSettings = new SslSettings() { EnabledSslProtocols = SslProtocols.Tls12 };
var mongoClient = new MongoClient(settings);
builder.Services.AddSingleton<IMongoClient>(mongoClient);
builder.Services.AddDbContext<CatalogDbContext>((provider, options) =>
{
    var client = provider.GetRequiredService<IMongoClient>();
    options.UseMongoDB(client, DatabaseName);
});

var app = builder.Build();

app.MapGet("/products", async ([FromServices] CatalogDbContext context) =>
{
    var products = await context.Products.ToListAsync();
    return Results.Ok(products);
})
.WithName("GetProducts");

app.MapPost("/products", async ([FromServices] CatalogDbContext context) =>
{
    var product = new Product
    {
        Title = $"Product - {Guid.NewGuid()}",
        Brand = "MongoDB",
        Id = ObjectId.GenerateNewId().ToString(),
        Tags = [],
    };
    await context.Products.AddAsync(product);
    await context.SaveChangesAsync();
    return Results.Ok(product);
})
.WithName("AddProduct");

app.Run();
