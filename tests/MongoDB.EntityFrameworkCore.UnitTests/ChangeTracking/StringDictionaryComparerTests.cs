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
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using MongoDB.EntityFrameworkCore.ChangeTracking;

namespace MongoDB.EntityFrameworkCore.UnitTests.ChangeTracking;

#if !EF8 && !EF9
public class StringDictionaryComparerTests
{
    private static StringDictionaryComparer<Dictionary<string, int>, int> CreateIntComparer()
        => new(new ValueComparer<int>(false));

    private static StringDictionaryComparer<Dictionary<string, string>, string> CreateStringComparer()
        => new(new ValueComparer<string>(false));

    [Fact]
    public void Equals_returns_true_for_same_reference()
    {
        var comparer = CreateIntComparer();
        var dict = (object)new Dictionary<string, int> { ["a"] = 1 };

        Assert.True(comparer.Equals(dict, dict));
    }

    [Fact]
    public void Equals_returns_true_for_both_null()
    {
        var comparer = CreateIntComparer();

        Assert.True(comparer.Equals(null, null));
    }

    [Fact]
    public void Equals_returns_false_for_null_and_non_null()
    {
        var comparer = CreateIntComparer();
        var dict = (object)new Dictionary<string, int> { ["a"] = 1 };

        Assert.False(comparer.Equals(null, dict));
        Assert.False(comparer.Equals(dict, null));
    }

    [Fact]
    public void Equals_returns_true_for_equal_dictionaries()
    {
        var comparer = CreateIntComparer();
        var a = (object)new Dictionary<string, int> { ["x"] = 1, ["y"] = 2 };
        var b = (object)new Dictionary<string, int> { ["x"] = 1, ["y"] = 2 };

        Assert.True(comparer.Equals(a, b));
    }

    [Fact]
    public void Equals_returns_false_for_different_values()
    {
        var comparer = CreateIntComparer();
        var a = (object)new Dictionary<string, int> { ["x"] = 1 };
        var b = (object)new Dictionary<string, int> { ["x"] = 99 };

        Assert.False(comparer.Equals(a, b));
    }

    [Fact]
    public void Equals_returns_false_for_different_keys()
    {
        var comparer = CreateIntComparer();
        var a = (object)new Dictionary<string, int> { ["x"] = 1 };
        var b = (object)new Dictionary<string, int> { ["y"] = 1 };

        Assert.False(comparer.Equals(a, b));
    }

    [Fact]
    public void Equals_returns_false_for_different_counts()
    {
        var comparer = CreateIntComparer();
        var a = (object)new Dictionary<string, int> { ["x"] = 1 };
        var b = (object)new Dictionary<string, int> { ["x"] = 1, ["y"] = 2 };

        Assert.False(comparer.Equals(a, b));
    }

    [Fact]
    public void GetHashCode_returns_same_value_for_equal_dictionaries()
    {
        var comparer = CreateIntComparer();
        var a = (object)new Dictionary<string, int> { ["x"] = 1, ["y"] = 2 };
        var b = (object)new Dictionary<string, int> { ["x"] = 1, ["y"] = 2 };

        Assert.Equal(comparer.GetHashCode(a), comparer.GetHashCode(b));
    }

    [Fact]
    public void GetHashCode_returns_different_values_for_different_dictionaries()
    {
        var comparer = CreateIntComparer();
        var a = (object)new Dictionary<string, int> { ["x"] = 1 };
        var b = (object)new Dictionary<string, int> { ["y"] = 99 };

        Assert.NotEqual(comparer.GetHashCode(a), comparer.GetHashCode(b));
    }

    [Fact]
    public void Snapshot_creates_independent_copy()
    {
        var comparer = CreateStringComparer();
        var original = new Dictionary<string, string> { ["a"] = "one", ["b"] = "two" };

        var snapshot = comparer.Snapshot(original);
        original["a"] = "changed";

        Assert.NotSame(original, snapshot);
        var snapshotDict = (IReadOnlyDictionary<string, string?>)snapshot;
        Assert.Equal("one", snapshotDict["a"]);
    }

    [Fact]
    public void Snapshot_handles_null_values_in_string_dictionary()
    {
        var comparer = CreateStringComparer();
        var original = new Dictionary<string, string> { ["a"] = "hello", ["b"] = null! };

        var snapshot = comparer.Snapshot(original);

        var snapshotDict = (IReadOnlyDictionary<string, string?>)snapshot;
        Assert.Equal("hello", snapshotDict["a"]);
        Assert.Null(snapshotDict["b"]);
    }

    [Fact]
    public void GetHashCode_handles_null_values()
    {
        var comparer = CreateStringComparer();
        var dict = (object)new Dictionary<string, string> { ["a"] = "hello", ["b"] = null! };

        var hash = comparer.GetHashCode(dict);
        Assert.NotEqual(0, hash);
    }

    [Fact]
    public void ElementComparer_returns_provided_comparer()
    {
        var elementComparer = new ValueComparer<int>(false);
        var comparer = new StringDictionaryComparer<Dictionary<string, int>, int>(elementComparer);

        Assert.Same(elementComparer, comparer.ElementComparer);
    }

    [Fact]
    public void Equals_throws_for_non_dictionary_types()
    {
        var comparer = CreateIntComparer();

        Assert.Throws<InvalidOperationException>(() =>
            comparer.Equals((object)"not a dict", (object)"also not"));
    }
}

#else

public class StringDictionaryComparerLegacyTests
{
    private static StringDictionaryComparer<int, Dictionary<string, int>> CreateIntComparer()
        => new(new ValueComparer<int>(false), readOnly: false);

    [Fact]
    public void Equals_returns_true_for_same_reference()
    {
        var comparer = CreateIntComparer();
        var dict = new Dictionary<string, int> { ["a"] = 1 };

        Assert.True(comparer.Equals(dict, dict));
    }

    [Fact]
    public void Equals_returns_true_for_both_null()
    {
        var comparer = CreateIntComparer();

        Assert.True(comparer.Equals(null, null));
    }

    [Fact]
    public void Equals_returns_false_for_null_and_non_null()
    {
        var comparer = CreateIntComparer();
        var dict = new Dictionary<string, int> { ["a"] = 1 };

        Assert.False(comparer.Equals(null, dict));
        Assert.False(comparer.Equals(dict, null));
    }

    [Fact]
    public void Equals_returns_true_for_equal_dictionaries()
    {
        var comparer = CreateIntComparer();
        var a = new Dictionary<string, int> { ["x"] = 1, ["y"] = 2 };
        var b = new Dictionary<string, int> { ["x"] = 1, ["y"] = 2 };

        Assert.True(comparer.Equals(a, b));
    }

    [Fact]
    public void Equals_returns_false_for_different_values()
    {
        var comparer = CreateIntComparer();
        var a = new Dictionary<string, int> { ["x"] = 1 };
        var b = new Dictionary<string, int> { ["x"] = 99 };

        Assert.False(comparer.Equals(a, b));
    }

    [Fact]
    public void Equals_returns_false_for_different_keys()
    {
        var comparer = CreateIntComparer();
        var a = new Dictionary<string, int> { ["x"] = 1 };
        var b = new Dictionary<string, int> { ["y"] = 1 };

        Assert.False(comparer.Equals(a, b));
    }

    [Fact]
    public void Equals_returns_false_for_different_counts()
    {
        var comparer = CreateIntComparer();
        var a = new Dictionary<string, int> { ["x"] = 1 };
        var b = new Dictionary<string, int> { ["x"] = 1, ["y"] = 2 };

        Assert.False(comparer.Equals(a, b));
    }

    [Fact]
    public void GetHashCode_returns_same_value_for_equal_dictionaries()
    {
        var comparer = CreateIntComparer();
        var a = new Dictionary<string, int> { ["x"] = 1, ["y"] = 2 };
        var b = new Dictionary<string, int> { ["x"] = 1, ["y"] = 2 };

        Assert.Equal(comparer.GetHashCode(a), comparer.GetHashCode(b));
    }

    [Fact]
    public void GetHashCode_returns_different_values_for_different_dictionaries()
    {
        var comparer = CreateIntComparer();
        var a = new Dictionary<string, int> { ["x"] = 1 };
        var b = new Dictionary<string, int> { ["y"] = 99 };

        Assert.NotEqual(comparer.GetHashCode(a), comparer.GetHashCode(b));
    }

    [Fact]
    public void Snapshot_creates_independent_copy()
    {
        var comparer = CreateIntComparer();
        var original = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 };

        var snapshot = (Dictionary<string, int>)comparer.Snapshot(original);
        original["a"] = 999;

        Assert.Equal(1, snapshot["a"]);
    }

    [Fact]
    public void Snapshot_returns_same_instance_when_read_only()
    {
        var readOnlyComparer = new StringDictionaryComparer<int, Dictionary<string, int>>(
            new ValueComparer<int>(false), readOnly: true);
        var original = new Dictionary<string, int> { ["a"] = 1 };

        var snapshot = readOnlyComparer.Snapshot(original);

        Assert.Same(original, snapshot);
    }

    [Fact]
    public void Type_returns_collection_type()
    {
        var comparer = CreateIntComparer();

        Assert.Equal(typeof(Dictionary<string, int>), comparer.Type);
    }
}

public class NullableStringDictionaryComparerTests
{
    private static NullableStringDictionaryComparer<int, Dictionary<string, int?>> CreateComparer()
        => new(new ValueComparer<int>(false), readOnly: false);

    [Fact]
    public void Equals_returns_true_for_equal_dictionaries()
    {
        var comparer = CreateComparer();
        var a = new Dictionary<string, int?> { ["x"] = 1, ["y"] = null };
        var b = new Dictionary<string, int?> { ["x"] = 1, ["y"] = null };

        Assert.True(comparer.Equals(a, b));
    }

    [Fact]
    public void Equals_returns_false_for_null_vs_value()
    {
        var comparer = CreateComparer();
        var a = new Dictionary<string, int?> { ["x"] = null };
        var b = new Dictionary<string, int?> { ["x"] = 1 };

        Assert.False(comparer.Equals(a, b));
    }

    [Fact]
    public void Equals_returns_false_for_value_vs_null()
    {
        var comparer = CreateComparer();
        var a = new Dictionary<string, int?> { ["x"] = 1 };
        var b = new Dictionary<string, int?> { ["x"] = null };

        Assert.False(comparer.Equals(a, b));
    }

    [Fact]
    public void Equals_returns_true_for_both_null()
    {
        var comparer = CreateComparer();

        Assert.True(comparer.Equals(null, null));
    }

    [Fact]
    public void Equals_returns_true_for_same_reference()
    {
        var comparer = CreateComparer();
        var dict = new Dictionary<string, int?> { ["a"] = 1 };

        Assert.True(comparer.Equals(dict, dict));
    }

    [Fact]
    public void GetHashCode_returns_same_value_for_equal_dictionaries()
    {
        var comparer = CreateComparer();
        var a = new Dictionary<string, int?> { ["x"] = 1, ["y"] = null };
        var b = new Dictionary<string, int?> { ["x"] = 1, ["y"] = null };

        Assert.Equal(comparer.GetHashCode(a), comparer.GetHashCode(b));
    }

    [Fact]
    public void Snapshot_creates_independent_copy()
    {
        var comparer = CreateComparer();
        var original = new Dictionary<string, int?> { ["a"] = 1, ["b"] = null };

        var snapshot = (Dictionary<string, int?>)comparer.Snapshot(original);
        original["a"] = 999;

        Assert.Equal(1, snapshot["a"]);
        Assert.Null(snapshot["b"]);
    }

    [Fact]
    public void Snapshot_returns_same_instance_when_read_only()
    {
        var readOnlyComparer = new NullableStringDictionaryComparer<int, Dictionary<string, int?>>(
            new ValueComparer<int>(false), readOnly: true);
        var original = new Dictionary<string, int?> { ["a"] = 1 };

        var snapshot = readOnlyComparer.Snapshot(original);

        Assert.Same(original, snapshot);
    }

    [Fact]
    public void Type_returns_collection_type()
    {
        var comparer = CreateComparer();

        Assert.Equal(typeof(Dictionary<string, int?>), comparer.Type);
    }
}
#endif
