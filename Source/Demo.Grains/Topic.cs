﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Orleankka;

namespace Demo
{
    public class Topic : Actor, ITopic
    {
        readonly ITimerService timers;
        readonly IReminderService reminders;
        readonly ITopicStorage storage;

        const int MaxRetries = 3;
        static readonly TimeSpan RetryPeriod = TimeSpan.FromSeconds(5);
        readonly IDictionary<string, int> retrying = new Dictionary<string, int>();

        internal int total;
        internal string query;

        public Topic()
        {
            timers = new TimerService(this);
            reminders = new ReminderService(this);
            storage = TopicStorage.Instance;
        }

        public Topic(
            string id, 
            IActorSystem system, 
            ITimerService timers, 
            IReminderService reminders, 
            ITopicStorage storage)
            : base(id, system)
        {
            this.timers = timers;
            this.reminders = reminders;
            this.storage = storage;
        }

        public override async Task ActivateAsync()
        {
            total = await storage.ReadTotalAsync(Id);
        }

        public override Task OnTell(object message)
        {
            return Handle((dynamic)message);
        }

        public async Task Handle(CreateTopic cmd)
        {
            query = cmd.Query;

            foreach (var entry in cmd.Schedule)
                await reminders.Register(entry.Key, TimeSpan.Zero, entry.Value);
        }

        public override async Task OnReminder(string api)
        {
            try
            {
                if (!IsRetrying(api))
                    await Search(api);
            }
            catch (ApiUnavailableException)
            {
                ScheduleRetries(api);
            }
        }

        bool IsRetrying(string api)
        {
            return retrying.ContainsKey(api);
        }

        public void ScheduleRetries(string api)
        {
            retrying.Add(api, 0);
            timers.Register(api, RetryPeriod, RetryPeriod, api, RetrySearch);
        }

        public async Task RetrySearch(object state)
        {
            var api = (string)state;
            
            try
            {
                await Search(api);
                CancelRetries(api);
            }
            catch (ApiUnavailableException)
            {
                RecordFailedRetry(api);

                if (MaxRetriesReached(api))
                {
                    DisableSearch(api);
                    CancelRetries(api);                   
                }
            }
        }

        void RecordFailedRetry(string api)
        {
            Log.Message(ConsoleColor.DarkRed, "[{0}] failed to obtain results from {1} ...", Id, api);
            retrying[api] += 1;
        }

        bool MaxRetriesReached(string api)
        {
            return retrying[api] == MaxRetries;
        }

        void CancelRetries(string api)
        {
            timers.Unregister(api);
            retrying.Remove(api);
        }

        async Task Search(string api)
        {
            var provider = System.ActorOf<IApi>(api);

            total += await provider.Query(new Search(query));
            Log.Message(ConsoleColor.DarkGray, "[{0}] succesfully obtained results from {1} ...", Id, api);

            await storage.WriteTotalAsync(Id, total);
        }

        void DisableSearch(string api)
        {
            reminders.Unregister(api);
        }
    }
}
