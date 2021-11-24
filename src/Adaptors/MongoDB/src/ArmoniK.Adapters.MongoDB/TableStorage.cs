﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;


using MongoDB.Driver;
using MongoDB.Driver.Linq;

using ArmoniK.Core.Exceptions;
using ArmoniK.Core.gRPC.V1;
using ArmoniK.Core.Storage;
using TaskStatus = ArmoniK.Core.gRPC.V1.TaskStatus;

using JetBrains.Annotations;


namespace ArmoniK.Adapters.MongoDB
{
  [PublicAPI]
  //TODO : wrap all exceptions into ArmoniKExceptions
  public class TableStorage : ITableStorage
  {
    private readonly IMongoCollection<SessionDataModel> sessionCollection_;
    private readonly IMongoCollection<TaskDataModel>    taskCollection_;
    private readonly IClientSessionHandle               sessionHandle_;
    public           TimeSpan                           PollingDelay { get; }

    public TableStorage(IMongoCollection<SessionDataModel> sessionCollection,
                        IMongoCollection<TaskDataModel>    taskCollection,
                        IClientSessionHandle               sessionHandle,
                        TimeSpan                           pollingDelay)
    {
      sessionCollection_ = sessionCollection;
      taskCollection_    = taskCollection;
      sessionHandle_     = sessionHandle;
      PollingDelay       = pollingDelay;




      Task.WaitAll(sessionCollection_.InitializeIndexesAsync(sessionHandle_),
                   taskCollection_.InitializeIndexesAsync(sessionHandle_));
    }


    public async Task CancelSessionAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
      Expression<Func<SessionDataModel, bool>> filterDefinition = sdm => sessionId.Session == sdm.SessionId &&
                                                                         (sessionId.SubSession == sdm.SubSessionId ||
                                                                          sdm.ParentsId.Any(id => id.Id == sessionId.SubSession) &&
                                                                          !sdm.IsClosed);

      var definitionBuilder = new UpdateDefinitionBuilder<SessionDataModel>();

      var updateDefinition = definitionBuilder.Combine(definitionBuilder.Set(model => model.IsCancelled, true),
                                                       definitionBuilder.Set(model => model.IsClosed, true));

      var res = await sessionCollection_.UpdateManyAsync(sessionHandle_, filterDefinition, updateDefinition,
                                                         cancellationToken: cancellationToken);
      if (res.MatchedCount < 1)
        throw new InvalidOperationException("No open session found. Was the session closed?");
    }

    public async Task CloseSessionAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
      Expression<Func<SessionDataModel, bool>> filterDefinition = sdm => sessionId.Session == sdm.SessionId &&
                                                                         (sessionId.SubSession == sdm.SubSessionId ||
                                                                          sdm.ParentsId.Any(id => id.Id == sessionId.SubSession));

      var definitionBuilder = new UpdateDefinitionBuilder<SessionDataModel>();

      var updateDefinition = definitionBuilder.Set(model => model.IsClosed, true);

      var res = await sessionCollection_.UpdateManyAsync(sessionHandle_,
                                                         filterDefinition,
                                                         updateDefinition,
                                                         cancellationToken: cancellationToken);
      if (res.MatchedCount < 1)
        throw new InvalidOperationException("No open session found. Was the session already closed?");
    }

    public Task<int> CountTasksAsync(TaskFilter filter, CancellationToken cancellationToken = default)
      => taskCollection_.FilterQueryAsync(sessionHandle_, filter)
                        .CountAsync(cancellationToken);

    public async Task<SessionId> CreateSessionAsync(SessionOptions sessionOptions, CancellationToken cancellationToken = default)
    {
      bool                            subSession = false;
      List<SessionDataModel.ParentId> parents    = null;
      if (sessionOptions.ParentSession != null)
      {
        if (!string.IsNullOrEmpty(sessionOptions.ParentSession.Session))
        {
          subSession = true;

          if (!string.IsNullOrEmpty(sessionOptions.ParentSession.SubSession))
          {
            var t = await sessionCollection_.AsQueryable(sessionHandle_)
                                            .Where(x => x.SessionId == sessionOptions.ParentSession.Session &&
                                                        x.SubSessionId == sessionOptions.ParentSession.SubSession)
                                            .SingleAsync(cancellationToken);
            parents = t.ParentsId;
            parents.Add(new SessionDataModel.ParentId() { Id = sessionOptions.ParentSession.SubSession });
          }
        }
      }

      var data = new SessionDataModel
                 {
                   IdTag       = sessionOptions.IdTag,
                   IsCancelled = false,
                   IsClosed    = false,
                   Options     = sessionOptions.DefaultTaskOption,
                   ParentsId   = parents,
                 };
      if (subSession)
      {
        data.SessionId = sessionOptions.ParentSession.Session;
      }
      else
      {
        data.SessionId = data.SubSessionId;
      }

      await sessionCollection_.InsertOneAsync(data, cancellationToken: cancellationToken);
      return new SessionId { Session = data.SessionId, SubSession = data.SubSessionId };
    }

    public Task DeleteTaskAsync(TaskId id, CancellationToken cancellationToken = default)
    {
      return taskCollection_.DeleteOneAsync(sessionHandle_, tdm => tdm.SessionId == id.Task &&
                                                       tdm.SubSessionId == id.SubSession &&
                                                       tdm.TaskId == id.Task, 
                                     cancellationToken: cancellationToken);
    }
    
    /// <inheritdoc />
    public async Task<int> UpdateTaskStatusAsync(TaskFilter filter, TaskStatus status, CancellationToken cancellationToken = default)
    {
      var updateDefinition = new UpdateDefinitionBuilder<TaskDataModel>().Set(tdm => tdm.Status, status);

      var result = await taskCollection_.UpdateManyAsync(sessionHandle_, x => x.SessionId == filter.SessionId &&
                                                           x.SubSessionId == filter.SubSessionId &&
                                                           filter.IncludedStatuses.Any(s => s == x.Status) &&
                                                           filter.ExcludedStatuses.All(s => s != x.Status) &&
                                                           filter.IncludedTaskIds.Any(tId => tId == x.TaskId) &&
                                                           filter.ExcludedTaskIds.All(tId => tId != x.TaskId) &&
                                                           x.Status != TaskStatus.Completed &&
                                                           x.Status != TaskStatus.Canceled, 
                                                         updateDefinition, 
                                                         cancellationToken: cancellationToken);
      return (int)result.MatchedCount;
    }

    public async Task IncreaseRetryCounterAsync(TaskId id, CancellationToken cancellationToken = default)
    {
      var updateDefinition = new UpdateDefinitionBuilder<TaskDataModel>().Inc(tdm => tdm.Retries, 1);

      var res = await taskCollection_.UpdateManyAsync(sessionHandle_, tdm => tdm.SessionId == id.Session &&
                                                                   tdm.SubSessionId == id.SubSession &&
                                                                   tdm.TaskId == id.Task,
                                            updateDefinition,
                                            cancellationToken: cancellationToken);
      switch (res.MatchedCount)
      {
        case 0:
          throw new ArmoniKException("Task not found");
        case > 1:
          throw new ArmoniKException("Multiple tasks modified");
      }
    }

    public async Task<(TaskId id, bool isPayloadStored)> InitializeTaskCreation(SessionId session, TaskOptions options, Payload payload, CancellationToken cancellationToken = default)
    {
      var isPayloadStored = payload.CalculateSize() < 12000000;

      var tdm = new TaskDataModel
                {
                  HasPayload   = isPayloadStored,
                  Options      = options,
                  Retries      = 0,
                  SessionId    = session.Session,
                  SubSessionId = session.SubSession,
                  Status       = TaskStatus.Creating,
                };
      if (isPayloadStored)
      {
        tdm.Payload = payload;
      }

      await taskCollection_.InsertOneAsync(sessionHandle_, tdm, cancellationToken: cancellationToken);
      return (tdm.GetTaskId(), isPayloadStored);
    }

    public async Task<bool> IsSessionCancelledAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
      var session = await sessionCollection_.AsQueryable(sessionHandle_)
                                            .Where(x => x.SessionId == sessionId.Session &&
                                                        x.SubSessionId == (sessionId.SubSession ?? "") &&
                                                        string.IsNullOrEmpty(sessionId.SubSession) || "" == x.SubSessionId)
                                            .SingleAsync(cancellationToken);
      
      return session.IsCancelled;
    }

    public async Task<bool> IsSessionClosedAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
      var session = await sessionCollection_.AsQueryable(sessionHandle_)
                                            .Where(x => x.SessionId == sessionId.Session &&
                                                        x.SubSessionId == (sessionId.SubSession ?? "") &&
                                                        string.IsNullOrEmpty(sessionId.SubSession) || "" == x.SubSessionId)
                                            .SingleAsync(cancellationToken);
      return session.IsClosed;
    }

    /// <inheritdoc />
    public Task DeleteSessionAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
      throw new NotImplementedException();
    }

    public IAsyncEnumerable<TaskId> ListTasksAsync(TaskFilter filter, CancellationToken cancellationToken = default)
    {
      return taskCollection_.FilterQueryAsync(sessionHandle_, filter)
                            .Select(x => new TaskId
                                         {
                                           Session = x.SessionId, SubSession = x.SubSessionId,
                                           Task    = x.TaskId
                                         })
                            .ToAsyncEnumerable();
    }

    public async Task<TaskData> ReadTaskAsync(TaskId id, CancellationToken cancellationToken = default)
    {
      var res = await taskCollection_.AsQueryable(sessionHandle_)
                                     .Where(tdm => tdm.SessionId == id.Session &&
                                                    tdm.SubSessionId == id.SubSession &&
                                                    tdm.TaskId == id.Task)
                                     .SingleAsync(cancellationToken);
      return res.ToTaskData();
    }

    public async Task UpdateTaskStatusAsync(TaskId id, Core.gRPC.V1.TaskStatus status, CancellationToken cancellationToken = default)
    {
      var updateDefinition = new UpdateDefinitionBuilder<TaskDataModel>().Set(tdm => tdm.Status, status);

      var res = await taskCollection_.UpdateManyAsync(sessionHandle_, x => x.SessionId == id.Session &&
                                                                 x.SubSessionId == id.SubSession &&
                                                                 x.TaskId == id.Task &&
                                                                 x.Status != TaskStatus.Completed &&
                                                                 x.Status != TaskStatus.Canceled,
                                            updateDefinition,
                                            cancellationToken: cancellationToken);

      switch (res.MatchedCount)
      {
        case 0:
          throw new ArmoniKException("Task not found");
        case > 1:
          throw new ArmoniKException("Multiple tasks modified");
      }
    }

    public async Task<TaskOptions> GetDefaultTaskOption(SessionId sessionId, CancellationToken cancellationToken)
    {
      return await sessionCollection_.AsQueryable(sessionHandle_)
                                     .Where(sdm => sdm.SessionId == sessionId.Session && sdm.SubSessionId == sessionId.SubSession)
                                     .Select(sdm => sdm.Options)
                                     .SingleAsync(cancellationToken);
    }
  }
}