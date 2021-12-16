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

using System;
using System.Linq;
using System.Threading.Tasks;

using ArmoniK.Core;
using ArmoniK.Core.gRPC;
using ArmoniK.Core.gRPC.V1;
using ArmoniK.Core.Storage;
using ArmoniK.Core.Utils;

using Google.Protobuf;

using Grpc.Core;

using Microsoft.Extensions.Logging;

using KeyNotFoundException = ArmoniK.Core.Exceptions.KeyNotFoundException;
using TaskStatus = ArmoniK.Core.gRPC.V1.TaskStatus;

namespace ArmoniK.Control.Services
{
  public class ClientService : Core.gRPC.V1.ClientService.ClientServiceBase
  {
    private readonly IQueueStorage                         lockedQueueStorage_;
    private readonly ILogger<ClientService>                logger_;
    private readonly ITableStorage                         tableStorage_;
    private readonly KeyValueStorage<TaskId, Payload>      taskPayloadStorage_;
    private readonly KeyValueStorage<TaskId, ComputeReply> taskResultStorage_;

    public ClientService(ITableStorage                         tableStorage,
                         IQueueStorage                         lockedQueueStorage,
                         KeyValueStorage<TaskId, ComputeReply> taskResultStorage,
                         KeyValueStorage<TaskId, Payload>      taskPayloadStorage,
                         ILogger<ClientService>                logger)
    {
      tableStorage_       = tableStorage;
      taskResultStorage_  = taskResultStorage;
      taskPayloadStorage_ = taskPayloadStorage;
      logger_             = logger;
      lockedQueueStorage_ = lockedQueueStorage;
    }

    public override async Task<Empty> CancelSession(SessionId request, ServerCallContext context)
    {
      using var _ = logger_.LogFunction();
      try
      {
        await tableStorage_.CancelSessionAsync(request,
                                               context.CancellationToken);
      }
      catch (KeyNotFoundException e)
      {
        throw new RpcException(new Status(StatusCode.FailedPrecondition,
                                          e.Message));
      }
      catch (Exception e)
      {
        throw new RpcException(new Status(StatusCode.Unknown,
                                          e.Message));
      }

      return new Empty();
    }

    public override async Task<Empty> CancelTask(TaskFilter request, ServerCallContext context)
    {
      using var _ = logger_.LogFunction();
      try
      {
        await tableStorage_.CancelTask(request,
                                       context.CancellationToken);
      }
      catch (KeyNotFoundException e)
      {
        throw new RpcException(new Status(StatusCode.FailedPrecondition,
                                          e.Message));
      }
      catch (Exception e)
      {
        throw new RpcException(new Status(StatusCode.Unknown,
                                          e.Message));
      }

      return new Empty();
    }

    public override async Task<Empty> CloseSession(SessionId request, ServerCallContext context)
    {
      using var _ = logger_.LogFunction();
      try
      {
        await tableStorage_.CloseSessionAsync(request,
                                              context.CancellationToken);
      }
      catch (KeyNotFoundException e)
      {
        throw new RpcException(new Status(StatusCode.FailedPrecondition,
                                          e.Message));
      }
      catch (Exception e)
      {
        throw new RpcException(new Status(StatusCode.Unknown,
                                          e.Message));
      }

      return new Empty();
    }

    public override Task<SessionId> CreateSession(SessionOptions request, ServerCallContext context)
    {
      using var _ = logger_.LogFunction();
      return tableStorage_.CreateSessionAsync(request,
                                              context.CancellationToken);
    }

    public override async Task<CreateTaskReply> CreateTask(CreateTaskRequest request, ServerCallContext context)
    {
      using var _ = logger_.LogFunction();

      var options = request.TaskOptions ??
                    await tableStorage_.GetDefaultTaskOption(request.SessionId,
                                                             context
                                                               .CancellationToken);

      var inits = await tableStorage_.InitializeTaskCreation(request.SessionId,
                                                             options,
                                                             request.TaskRequests,
                                                             context.CancellationToken)
                                     .ToListAsync();

      await using var finalizer = AsyncDisposable.Create(async () => await tableStorage_.FinalizeTaskCreation(new TaskFilter
                                                                                                              {
                                                                                                                SessionId    = request.SessionId.Session,
                                                                                                                SubSessionId = request.SessionId.SubSession,
                                                                                                                IncludedTaskIds =
                                                                                                                {
                                                                                                                  inits.Select(tuple => tuple.id.Task),
                                                                                                                },
                                                                                                              },
                                                                                                              context.CancellationToken));

      var payloadsUpdateTask = inits.Where(tuple => !tuple.HasPayload)
                                    .Select(tuple => taskPayloadStorage_.AddOrUpdateAsync(tuple.id,
                                                                                          new Payload { Data = ByteString.CopyFrom(tuple.Payload) },
                                                                                          context.CancellationToken))
                                    .WhenAll();


      var enqueueTask = lockedQueueStorage_.EnqueueMessagesAsync(inits.Select(tuple => tuple.id),
                                                                 options.Priority,
                                                                 context.CancellationToken);

      await Task.WhenAll(enqueueTask,
                         payloadsUpdateTask);

      CreateTaskReply reply = new();
      reply.TaskIds.Add(inits.Select(tuple => tuple.id));
      return reply;
    }

    public override async Task<Count> GetTasksCount(TaskFilter request, ServerCallContext context)
    {
      using var _ = logger_.LogFunction();
      var count = await tableStorage_.CountTasksAsync(request,
                                                      context.CancellationToken);
      return new Count { Value = count };
    }

    public override async Task<TaskIdList> ListTask(TaskFilter        request,
                                                    ServerCallContext context)
    {
      using var _ = logger_.LogFunction();

      var list = await tableStorage_.ListTasksAsync(request,
                                                context.CancellationToken).ToListAsync(context.CancellationToken);

      var output = new TaskIdList();
      output.TaskIds.Add(list);
      return output;
    }

    /// <inheritdoc />
    public override async Task<TaskIdList> ListSubTasks(TaskFilter request, ServerCallContext context)
    {
      using var _ = logger_.LogFunction();

      TaskIdList wholeList = new();

      var listAsync = tableStorage_.ListTasksAsync(request,
                                                   context.CancellationToken);

      await foreach (var tid in listAsync)
      {
        var localFilter = new TaskFilter(request) { SubSessionId = tid.Task };
        localFilter.IncludedTaskIds.Clear();
        var localList = await ListSubTasks(localFilter,
                                     context);
        wholeList.TaskIds.Add(tid);
        wholeList.TaskIds.Add(localList.TaskIds);
      }

      return wholeList;
    }

    /// <inheritdoc />
    public override async Task<Count> GetSubTasksCount(TaskFilter request, ServerCallContext context) 

    {
      using var _ = logger_.LogFunction();


      var listAsync = tableStorage_.ListTasksAsync(request,
                                                   context.CancellationToken);

      var wholeCount = await listAsync.SelectAwait(async id =>
                                             {
                                               var localFilter = new TaskFilter(request) { SubSessionId = id.Task };
                                               localFilter.IncludedTaskIds.Clear();
                                               return (await ListSubTasks(localFilter,
                                                                          context)).TaskIds.Count + 1;
                                             }).SumAsync(i => i);

      return new Count{ Value = wholeCount };
    }

    public override async Task<MultiplePayloadReply> TryGetResult(TaskFilter                              request,
                                                                  ServerCallContext                       context)
    {
      using var            _                    = logger_.LogFunction();
      MultiplePayloadReply multiplePayloadReply = new();
      await foreach (var taskId in tableStorage_.ListTasksAsync(request,
                                                                context.CancellationToken)
                                                .WithCancellation(context.CancellationToken))
      {
        var result = await taskResultStorage_.TryGetValuesAsync(taskId,
                                                                context.CancellationToken);
        var reply = new SinglePayloadReply { TaskId = taskId, Data = new Payload { Data = result.Result } };
        multiplePayloadReply.Payloads.Add(reply);
      }
      return multiplePayloadReply;
    }

    public override async Task<Empty> WaitForCompletion(TaskFilter request, ServerCallContext context)
    {
      using var _ = logger_.LogFunction();

      // TODO: optimize by filtering based on the task statuses
      // TODO: optimize by filtering based on the number of retries
      var taskIds = tableStorage_.ListTasksAsync(request,
                                                 context.CancellationToken);
      await foreach (var taskId in taskIds)
      {
        await WaitForSingleTaskCompletion(taskId, context);
      }

      return new Empty();
    }

    private async Task WaitForSingleTaskCompletion(TaskId taskId, ServerCallContext context)
    {
      using var _ = logger_.LogFunction(taskId.ToPrintableId());
      bool completed;
      do
      {
        var taskData = await tableStorage_.ReadTaskAsync(taskId,
                                                      context.CancellationToken);
        logger_.LogInformation("Task {id} has status {status}, retry : {retry}, max {max}",
                               taskId,
                               taskData.Status,
                               taskData.Retries,
                               taskData.Options.MaxRetries);
        completed = taskData.Status == TaskStatus.Completed ||
                    taskData.Status == TaskStatus.Canceled;
        if (!completed)
        {
          logger_.LogInformation("Task {id} is not completed. Will wait",
                                 taskId);
          await Task.Delay(tableStorage_.PollingDelay);
        }
      } while (!completed);

      logger_.LogInformation("Task {id} has been completed",
                             taskId);
    }

    /// <inheritdoc />
    public override async Task<Empty> WaitForSubTasksCompletion(TaskFilter request, ServerCallContext context)
    {
      using var _ = logger_.LogFunction();
      var taskIds = tableStorage_.ListTasksAsync(request,
                                                 context.CancellationToken);

      await foreach (var id in taskIds)
      {
        await WaitForSingleTaskCompletion(id,
                                          context);
        var localFilter = new TaskFilter(request) { SubSessionId = id.Task };
        localFilter.IncludedTaskIds.Clear();
        logger_.LogDebug("localFilter: {localFilter}",
                         localFilter);

        await WaitForSubTasksCompletion(localFilter,
                                        context);
      }

      return new Empty();
    }
  }
}
