// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Originally from EFCore.Cosmos CollectionShaperExpression.

using Microsoft.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

internal sealed class CollectionShaperExpression : Expression, IPrintableExpression
{
    public CollectionShaperExpression(
        Expression projection,
        Expression innerShaper,
        INavigationBase? navigation,
        Type elementType)
    {
        Projection = projection;
        InnerShaper = innerShaper;
        Navigation = navigation;
        ElementType = elementType;
    }

    public Expression Projection { get; }

    public Expression InnerShaper { get; }

    public INavigationBase? Navigation { get; }

    public Type ElementType { get; }

    public override ExpressionType NodeType
        => ExpressionType.Extension;

    public override Type Type
        => Navigation?.ClrType ?? typeof(List<>).MakeGenericType(ElementType);

    protected override Expression VisitChildren(ExpressionVisitor visitor)
    {
        var projection = visitor.Visit(Projection);
        var innerShaper = visitor.Visit(InnerShaper);

        return Update(projection, innerShaper);
    }

    public CollectionShaperExpression Update(
        Expression projection,
        Expression innerShaper)
    {
        return projection != Projection || innerShaper != InnerShaper
            ? new CollectionShaperExpression(projection, innerShaper, Navigation, ElementType)
            : this;
    }

    void IPrintableExpression.Print(ExpressionPrinter expressionPrinter)
    {
        expressionPrinter.AppendLine("CollectionShaper:");
        using (expressionPrinter.Indent())
        {
            expressionPrinter.Append("(");
            expressionPrinter.Visit(Projection);
            expressionPrinter.Append(", ");
            expressionPrinter.Visit(InnerShaper);
            expressionPrinter.AppendLine($", {Navigation?.Name})");
        }
    }
}
