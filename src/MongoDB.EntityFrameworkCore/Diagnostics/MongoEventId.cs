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

using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MongoDB.EntityFrameworkCore.Diagnostics;

/// <summary>
/// Event IDs for MongoDB events that correspond to messages logged to an <see cref="ILogger" />
/// and events sent to a <see cref="DiagnosticSource" />.
/// </summary>
/// <remarks>
/// These IDs are also used with <see cref="WarningsConfigurationBuilder" /> to configure the behavior of warnings.
/// </remarks>
public static class MongoEventId
{
    // Warning: These values must not change between releases.
    // Only add new values to the end of sections, never in the middle.
    private enum Id
    {
        ExecutedMqlQuery = CoreEventId.ProviderDesignBaseId
    }

    /// <summary>
    /// An MQL query has been executed.
    /// </summary>
    /// <remarks>
    /// <para>This event is in the <see cref="DbLoggerCategory.Database.Command" /> category.</para>
    /// <para>
    /// This event uses the <see cref="MongoQueryEventData" /> payload when used with a <see cref="DiagnosticSource" />.
    /// </para>
    /// </remarks>
    public static readonly EventId ExecutedMqlQuery = MakeEventId(Id.ExecutedMqlQuery);

    private static readonly string EventPrefix = DbLoggerCategory.Database.Command.Name + ".";

    private static EventId MakeEventId(Id id)
        => new((int)id, EventPrefix + id);
}
