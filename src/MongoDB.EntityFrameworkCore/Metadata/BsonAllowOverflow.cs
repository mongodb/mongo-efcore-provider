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

namespace MongoDB.EntityFrameworkCore.Metadata;

public enum BsonAllowOverflow
{
    Unspecified,
    False,
    True
}

public static class BsonAllowOverflowHelper
{
    public static bool? ToNullableBool(this BsonAllowOverflow value)
        => value switch
        {
            BsonAllowOverflow.False => false,
            BsonAllowOverflow.True => true,
            _ => null,
        };

    public static BsonAllowOverflow FromNullableBool(bool? value)
        => value switch
        {
            false => BsonAllowOverflow.False,
            true => BsonAllowOverflow.True,
            _ => BsonAllowOverflow.Unspecified,
        };
}
