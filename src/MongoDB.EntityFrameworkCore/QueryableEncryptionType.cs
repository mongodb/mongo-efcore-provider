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

namespace MongoDB.EntityFrameworkCore;

/// <summary>
/// Specifies the type of query that can be performed on an encrypted field.
/// </summary>
public enum QueryableEncryptionType
{
    /// <summary>
    /// The field is encrypted and can not be directly queried against. (IsEncrypted)
    /// </summary>
    NotQueryable,
    /// <summary>
    /// The field is encrypted and can be queried for equality. (IsEncryptedForEquality)
    /// </summary>
    Equality,
    /// <summary>
    /// The field is encrypted and can be queried for range queries. (IsEncryptedForRange)
    /// </summary>
    Range,
}
