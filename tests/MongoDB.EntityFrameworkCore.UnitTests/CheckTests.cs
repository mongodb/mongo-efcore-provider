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

using System;

namespace MongoDB.EntityFrameworkCore.UnitTests;

public class CheckTests
{
    [Fact]
    public void NotEmptyButCanBeNull_returns_null_for_null()
    {
        Assert.Null(Check.NotEmptyButCanBeNull(null));
    }

    [Fact]
    public void NotEmptyButCanBeNull_returns_value_for_non_empty()
    {
        Assert.Equal("hello", Check.NotEmptyButCanBeNull("hello"));
    }

    [Fact]
    public void NotEmptyButCanBeNull_throws_for_empty_string()
    {
        Assert.Throws<ArgumentException>(() => Check.NotEmptyButCanBeNull(""));
    }
}
