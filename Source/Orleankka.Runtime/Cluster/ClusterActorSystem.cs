﻿using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

using Orleans.Runtime;
using Orleans.Hosting;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Orleans;
using Orleans.CodeGeneration;
using Orleans.Runtime.Configuration;
using Orleans.Storage;

namespace Orleankka.Cluster
{
    using Core;
    using Core.Streams;
    using Utility;

     public class ClusterActorSystem : ActorSystem
    {
        internal readonly ActorInvocationPipeline Pipeline;

        internal ClusterActorSystem(
            ClusterConfiguration configuration,
            Action<ISiloHostBuilder> builder,
            string[] persistentStreamProviders,
            Assembly[] assemblies,
            Action<IServiceCollection> di,
            ActorInvocationPipeline pipeline,
            IActorRefInvoker invoker)
            : base(invoker)
        {
            Pipeline = pipeline;

            var sb = new SiloHostBuilder();
            sb.UseConfiguration(configuration);
            builder?.Invoke(sb);

            using (Trace.Execution("Orleans silo initialization"))
            {
                sb.ConfigureServices(services =>
                {
                    BootStreamSubscriptions(services, persistentStreamProviders);

                    services.AddSingleton<IActorSystem>(this);
                    services.AddSingleton(this);
                    services.TryAddSingleton<IActorActivator>(x => new DefaultActorActivator(x));
                    services.AddSingleton<Func<MethodInfo, InvokeMethodRequest, IGrain, string>>(DashboardIntegration.Format);

                    di?.Invoke(services);
                });

                var parts = new List<Assembly>(assemblies) {Assembly.GetExecutingAssembly()};
                parts.AddRange(ActorType.Registered().Select(x => x.Grain.Assembly).Distinct());

                sb.ConfigureApplicationParts(apm =>
                {
                    apm.AddFrameworkPart(GetType().Assembly);

                    foreach (var part in parts)
                        apm.AddApplicationPart(part);

                    apm.AddFromAppDomain()
                       .WithCodeGeneration();
                });

                Host = sb.Build();
            }

            Silo = Host.Services.GetRequiredService<Silo>();
            Initialize(Host.Services);
        }

         static void BootStreamSubscriptions(IServiceCollection services, string[] persistentStreamProviders)
         {
             const string name = "orlssb";
             services.AddOptions<StreamSubscriptionBootstrapperOptions>(name).Configure(c => c.Providers = persistentStreamProviders);
             services.AddSingletonNamedService(name, StreamSubscriptionBootstrapper.Create);
             services.AddSingletonNamedService(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>) s.GetRequiredServiceByName<IGrainStorage>(n));
         }

         public ISiloHost Host { get; }
        public Silo Silo { get; }

        public async Task Start()
        {
            using (Trace.Execution("Orleans silo startup"))
                await Host.StartAsync();
        }

        public async Task Stop()
        {
            using (Trace.Execution("Orleans silo shutdown"))
                await Host.StopAsync();
        }
    }
}