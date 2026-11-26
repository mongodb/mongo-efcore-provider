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

global using Xunit;
#if EF11
// EF11's test base classes use plain xUnit v3 [Fact]/[Theory]; its [ConditionalFact]/[ConditionalTheory] infrastructure is gone.
global using ConditionalFactAttribute = Xunit.FactAttribute;
global using ConditionalTheoryAttribute = Xunit.TheoryAttribute;
// xUnit v3's IAsyncLifetime (and EF11's fixtures) use ValueTask where xUnit v2 uses Task.
global using LifetimeTask = System.Threading.Tasks.ValueTask;
#else
global using LifetimeTask = System.Threading.Tasks.Task;
#endif
global using XUnitCollection = Xunit.CollectionAttribute;
global using MongoDB.EntityFrameworkCore.SpecificationTests.Utilities;

#if EF11
// CollectionBehaviorAttribute.DisableTestParallelization is an error in xUnit v3 4.x.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
#else
[assembly:CollectionBehavior(DisableTestParallelization = true)]
#endif
