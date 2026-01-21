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

using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using MongoDB.Bson.Serialization.Attributes;

namespace MongoDB.EntityFrameworkCore.Metadata.Conventions.BsonAttributes;

/// <summary>
/// Recognize <see cref="BsonIgnoreIfDefaultAttribute"/> when applied to properties of an entity
/// to ensure the model will throw as the attribute is not supported in the EF Core provider.
/// </summary>
/// <param name="dependencies">The <see cref="ProviderConventionSetBuilderDependencies"/> conventions depend upon.</param>
public sealed class BsonIgnoreIfDefaultAttributePropertyAttributeConvention(ProviderConventionSetBuilderDependencies dependencies)
    : NotSupportedPropertyAttributeConvention<BsonIgnoreIfDefaultAttribute>(dependencies);
