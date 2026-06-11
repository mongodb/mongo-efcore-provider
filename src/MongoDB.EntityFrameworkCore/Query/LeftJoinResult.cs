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

namespace MongoDB.EntityFrameworkCore.Query;

/// <summary>
/// Result type for LeftJoin that matches the MongoDB driver's join field naming convention.
/// The driver's Join/LeftJoin translators produce documents with "_outer" and "_inner" fields.
/// Using matching property names ensures the driver's $project stage preserves these field names.
/// </summary>
internal class LeftJoinResult<TOuter, TInner>
{
    // ReSharper disable InconsistentNaming
    public TOuter _outer { get; set; }
    public TInner _inner { get; set; }
    // ReSharper restore InconsistentNaming

    public LeftJoinResult(TOuter _outer, TInner _inner)
    {
        this._outer = _outer;
        this._inner = _inner;
    }
}
