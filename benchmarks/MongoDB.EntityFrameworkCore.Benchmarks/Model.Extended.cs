using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.Benchmarks;

public class Account                       // principal for Order's reference navigation
{
    public ObjectId Id { get; set; }
    public string Name { get; set; } = "";
    public string Tier { get; set; } = "";
}

public class Order
{
    public ObjectId Id { get; set; }
    public string Code { get; set; } = "";
    public ObjectId AccountId { get; set; }            // FK to Account
    public Account? Account { get; set; }              // reference navigation (Case A Include)
    public ShippingInfo Shipping { get; set; } = new();// owned
    public List<LineItem> Lines { get; set; } = new(); // owned collection
}

public class ShippingInfo                  // owned
{
    public string Carrier { get; set; } = "";
    public OrderAddress Address { get; set; } = new(); // nested owned
}

public class OrderAddress                  // owned (nested under ShippingInfo)
{
    public string Street { get; set; } = "";
    public string City { get; set; } = "";
    public int Zip { get; set; }
}

public class LineItem                      // owned-collection element
{
    public string Sku { get; set; } = "";
    public int Qty { get; set; }
    public ItemMeta Meta { get; set; } = new();          // nested owned (inside collection element)
    public List<Discount> Discounts { get; set; } = new(); // nested owned collection (inside element)
}

public class ItemMeta { public string Category { get; set; } = ""; public double Weight { get; set; } }
public class Discount { public string Kind { get; set; } = ""; public decimal Amount { get; set; } }
