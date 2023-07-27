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

using MongoDB.Bson;

#nullable disable

internal class Planet
{
    public ObjectId _id { get; set; }
    public string name { get; set; }
    public int orderFromSun { get; set; }
    public bool hasRings { get; set; }
    public string[] mainAtmosphere { get; set; }
    // public MeanTemperature meanTemperature { get; set; }
}

internal class MeanTemperature
{
    public double min { get; set;  }
    public double max { get; set;  }
    public double mean { get; set;  }
}
