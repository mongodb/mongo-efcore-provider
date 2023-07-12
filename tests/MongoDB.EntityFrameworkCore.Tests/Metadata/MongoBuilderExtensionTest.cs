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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Tests.Metadata;

public static class MongoBuilderExtensionTest
{
    [Fact]
    public static void Can_set_collection_name()
    {
        var typeBuilder = CreateBuilder().Entity(typeof(SampleEntity), ConfigurationSource.Convention)!;

        Assert.NotNull(typeBuilder.ToCollection("First"));
        Assert.Equal("First", typeBuilder.Metadata.GetCollectionName());

        Assert.NotNull(typeBuilder.ToCollection("Second", fromDataAnnotation: true));
        Assert.Equal("Second", typeBuilder.Metadata.GetCollectionName());

        Assert.Null(typeBuilder.ToCollection("Third"));
        Assert.Equal("Second", typeBuilder.Metadata.GetCollectionName());
    }

    [Fact]
    public static void Can_set_element_name()
    {
        var typeBuilder = CreateBuilder().Entity(typeof(SampleEntity), ConfigurationSource.Convention)!;
        var propertyBuilder = typeBuilder.Property(typeof(string), "SampleString", ConfigurationSource.Convention)!;

        Assert.NotNull(propertyBuilder.ToElement("First"));
        Assert.Equal("First", propertyBuilder.Metadata.GetElementName());

        Assert.NotNull(propertyBuilder.ToElement("Second", fromDataAnnotation: true));
        Assert.Equal("Second", propertyBuilder.Metadata.GetElementName());

        Assert.Null(propertyBuilder.ToElement("Third"));
        Assert.Equal("Second", propertyBuilder.Metadata.GetElementName());
    }

    private static ModelBuilder CreateConventionModelBuilder()
        => MongoTestHelpers.Instance.CreateConventionBuilder();

    private static InternalModelBuilder CreateBuilder()
        => (InternalModelBuilder)CreateConventionModelBuilder().GetInfrastructure();

    private class SampleEntity
    {
        public string SampleString { get; set; }
    }
}
