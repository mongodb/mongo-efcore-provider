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

using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MongoDB.EntityFrameworkCore.Diagnostics;

/// <summary>
/// MongoDB-specific logging definitions.
/// </summary>
internal class MongoLoggingDefinitions : LoggingDefinitions
{
    public EventDefinitionBase? LogExecutedMqlQuery;

    public EventDefinitionBase? LogExecutingBulkWrite;
    public EventDefinitionBase? LogExecutedBulkWrite;

    public EventDefinitionBase? LogBeginningTransaction;
    public EventDefinitionBase? LogBeganTransaction;

    public EventDefinitionBase? LogCommittingTransaction;
    public EventDefinitionBase? LogCommittedTransaction;

    public EventDefinitionBase? LogRollingBackTransaction;
    public EventDefinitionBase? LogRolledBackTransaction;

    public EventDefinitionBase? LogTransactionError;

    public EventDefinitionBase? LogRecommendedMinMaxRangeMissing;
    public EventDefinitionBase? LogEncryptedNullablePropertyEncountered;
}
