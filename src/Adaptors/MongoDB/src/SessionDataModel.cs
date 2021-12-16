﻿// This file is part of the ArmoniK project
// 
// Copyright (C) ANEO, 2021-2021. All rights reserved.
//   W. Kirschenmann   <wkirschenmann@aneo.fr>
//   J. Gurhem         <jgurhem@aneo.fr>
//   D. Dubuc          <ddubuc@aneo.fr>
//   L. Ziane Khodja   <lzianekhodja@aneo.fr>
//   F. Lemaitre       <flemaitre@aneo.fr>
//   S. Djebbar        <sdjebbar@aneo.fr>
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published
// by the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
// 
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System.Threading.Tasks;

using ArmoniK.Core.gRPC.V1;

using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace ArmoniK.Adapters.MongoDB
{
  public class SessionDataModel : IMongoDataModel<SessionDataModel>
  {
    [BsonIgnore]
    public string IdTag { get; set; }

    [BsonElement]
    [BsonRequired]
    public string SessionId { get; set; }

    [BsonId(IdGenerator = typeof(SessionIdGenerator))]
    public string SubSessionId { get; set; }

    [BsonElement]
    public List<ParentId> ParentsId { get; set; }

    [BsonElement]
    public bool IsClosed { get; set; }

    [BsonElement]
    public bool IsCancelled { get; set; }

    [BsonElement]
    [BsonRequired]
    public TaskOptions Options { get; set; }

    /// <inheritdoc />
    public string CollectionName { get; } = "SessionData";

    /// <inheritdoc />
    public Task InitializeIndexesAsync(IClientSessionHandle sessionHandle, IMongoCollection<SessionDataModel> collection)
    {
      var sessionIndex    = Builders<SessionDataModel>.IndexKeys.Text(model => model.SessionId);
      var subSessionIndex = Builders<SessionDataModel>.IndexKeys.Text(model => model.SubSessionId);
      var parentsIndex    = Builders<SessionDataModel>.IndexKeys.Text("ParentsId.Id");
      var sessionSubSessionIndex = Builders<SessionDataModel>.IndexKeys.Combine(sessionIndex,
                                                                                subSessionIndex);
      var sessionParentIndex = Builders<SessionDataModel>.IndexKeys.Combine(sessionIndex,
                                                                            parentsIndex);

      var indexModels = new CreateIndexModel<SessionDataModel>[]
                        {
                          new(sessionIndex,
                              new CreateIndexOptions { Name = nameof(sessionIndex) }),
                          new(sessionSubSessionIndex,
                              new CreateIndexOptions { Name = nameof(sessionSubSessionIndex), Unique = true }),
                          new(sessionParentIndex,
                              new CreateIndexOptions { Name = nameof(sessionParentIndex) }),
                        };

      return collection.Indexes.CreateManyAsync(sessionHandle,
                                                indexModels);
    }

    public class ParentId
    {
      [BsonElement]
      public string Id { get; set; }
    }
  }
}