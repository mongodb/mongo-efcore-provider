using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.Benchmarks;

// A small blog-shaped model for Include shapes the Review/Product and Order/Account models cannot express: a collection navigation
// (Category.Articles), several reference navigations on one entity (Article.Category, Article.Author) and a second level
// (Category.Articles -> Article.Author). Kept apart from the other models so their documents, and so their numbers, do not change.
public class Category
{
    public ObjectId Id { get; set; }
    public int Rank { get; set; }
    public string Name { get; set; } = "";
    public List<Article> Articles { get; set; } = new();
}

public class Author
{
    public ObjectId Id { get; set; }
    public string Name { get; set; } = "";
}

public class Article
{
    public ObjectId Id { get; set; }
    public string Title { get; set; } = "";
    public int Views { get; set; }
    public ObjectId CategoryId { get; set; }
    public Category? Category { get; set; }
    public ObjectId AuthorId { get; set; }
    public Author? Author { get; set; }
}
