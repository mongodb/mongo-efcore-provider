﻿/* Copyright 2023-present MongoDB Inc.
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

namespace MongoDB.EntityFrameworkCore.UnitTests.Metadata.Attributes;

public static class CollectionAttributeTests
{
    [Theory]
    [InlineData("CollectionNameSet")]
    public static void Constructor_name_sets_property(string name)
    {
        var attribute = new CollectionAttribute(name);
        Assert.Equal(name, attribute.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public static void Constructor_throws_argument_exception_when_empty(string? name)
    {
        Assert.Throws<ArgumentException>(() => new CollectionAttribute(name!));
    }
}
