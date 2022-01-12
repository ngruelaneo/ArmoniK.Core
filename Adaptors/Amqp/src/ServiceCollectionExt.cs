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

using Amqp;

using ArmoniK.Core;
using ArmoniK.Core.Injection;
using ArmoniK.Core.Injection.Options;
using ArmoniK.Core.Storage;

using JetBrains.Annotations;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArmoniK.Adapters.Amqp
{
  public static class ServiceCollectionExt
  {
    [PublicAPI]
    public static IServiceCollection AddAmqp(
      this IServiceCollection serviceCollection,
      ConfigurationManager    configuration,
      ILogger logger
    )
    {
      logger.LogInformation("Configure Amqp client");

      var components = configuration.GetSection(Components.SettingSection);

      if (components["QueueStorage"] == "ArmoniK.Adapters.Amqp.QueueStorage")
      {
        var amqpOptions = configuration.GetRequiredValue<Options.Amqp>(Options.Amqp.SettingSection);

        using var _ = logger.BeginNamedScope("AMQP configuration",
                                             ("host", amqpOptions.Host),
                                             ("port", amqpOptions.Port));

        if (!string.IsNullOrEmpty(amqpOptions.CredentialsPath))
        {
          configuration.AddJsonFile(amqpOptions.CredentialsPath);
          logger.LogTrace("Loaded amqp credentials from file {path}",
                                amqpOptions.CredentialsPath);
        }
        else
        {
          logger.LogTrace("No credential path provided");
        }

        serviceCollection.AddOption(configuration,
                                    Options.Amqp.SettingSection,
                                    out amqpOptions);

        var sessionProvider = new SessionProvider(amqpOptions);

        serviceCollection.AddSingleton(sessionProvider);

        serviceCollection.AddSingleton<IQueueStorage, QueueStorage>();

        serviceCollection.AddHealthChecks()
                         .AddAsyncCheck("AmqpHealthCheck",
                                        async () =>
                                        {
                                          var t = await sessionProvider.GetAsync();
                                          return t.SessionState == SessionState.Opened ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy();
                                        });

        logger.LogInformation("Amqp configuration complete");
      }
      else
      {
        logger.LogInformation("Nothing to configure");
      }

      return serviceCollection;
    }
  }
}