﻿using Orleans;
using Orleans.Concurrency;
using Orleans.Placement;
using Orleans.Runtime;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OrleansDashboard
{
    [Reentrant]
    [PreferLocalPlacement]
    public class DashboardGrain : Grain, IDashboardGrain
    {
        private DashboardCounters Counters { get; set; }
        private DateTime StartTime { get; set; }
        private List<GrainTraceEntry> history = new List<GrainTraceEntry>();

        private ArrayPool<SimpleGrainStatisticCounter> grainStatsCounterPool =
            ArrayPool<SimpleGrainStatisticCounter>.Shared;

        private async Task Callback(object _)
        {
            var metricsGrain = this.GrainFactory.GetGrain<IManagementGrain>(0);
            var activationCountTask = metricsGrain.GetTotalActivationCount();
            var hostsTask = metricsGrain.GetDetailedHosts(true);
            var simpleGrainStatsTask = metricsGrain.GetSimpleGrainStatistics();

            await Task.WhenAll(activationCountTask, hostsTask, simpleGrainStatsTask);

            RecalculateCounters(activationCountTask.Result, hostsTask.Result, simpleGrainStatsTask.Result);
        }

        internal void RecalculateCounters(int activationCount, MembershipEntry[] hosts,
            SimpleGrainStatistic[] simpleGrainStatistics)
        {
            this.Counters.TotalActivationCount = activationCount;

            this.Counters.TotalActiveHostCount = hosts.Count(x => x.Status == SiloStatus.Active);
            this.Counters.TotalActivationCountHistory.Enqueue(activationCount);
            this.Counters.TotalActiveHostCountHistory.Enqueue(this.Counters.TotalActiveHostCount);

            while (this.Counters.TotalActivationCountHistory.Count > Dashboard.HistoryLength)
            {
                this.Counters.TotalActivationCountHistory.Dequeue();
            }
            while (this.Counters.TotalActiveHostCountHistory.Count > Dashboard.HistoryLength)
            {
                this.Counters.TotalActiveHostCountHistory.Dequeue();
            }

            // TODO - whatever max elapsed time
            var elapsedTime = Math.Min((DateTime.UtcNow - this.StartTime).TotalSeconds, 100);

            this.Counters.Hosts = hosts.Select(x => new SiloDetails
            {
                FaultZone = x.FaultZone,
                HostName = x.HostName,
                IAmAliveTime = x.IAmAliveTime.ToString("o"),
                ProxyPort = x.ProxyPort,
                RoleName = x.RoleName,
                SiloAddress = x.SiloAddress.ToParsableString(),
                SiloName = x.SiloName,
                StartTime = x.StartTime.ToString("o"),
                Status = x.Status.ToString(),
                UpdateZone = x.UpdateZone
            }).ToArray();

          
            //retur prev buffer
            if (this.Counters.SimpleGrainStats != null)
            {
                var copy = this.Counters.SimpleGrainStats;
                this.Counters.SimpleGrainStats = null;
                grainStatsCounterPool.Return(copy);
            }


            var historyByGrain = this.history
                .ToLookup(x => new GrainSiloKey(x.Grain, x.SiloAddress));
            var dict = new Dictionary<GrainSiloKey, AggregatedGrainTotals>();

            var stats = grainStatsCounterPool.Rent(simpleGrainStatistics.Length);
            for (int i = 0; i < simpleGrainStatistics.Length; i++)
            {
                var x = simpleGrainStatistics[i];
                var grainName = TypeFormatter.Parse(x.GrainType);
                var siloAddress = x.SiloAddress.ToParsableString();
                var key = new GrainSiloKey(grainName, siloAddress);
                AggregatedGrainTotals totals;

                // ensure entry in totals dictionary
                if (!dict.TryGetValue(key, out totals))
                {
                    totals = new AggregatedGrainTotals()
                    {
                        TotalSeconds = elapsedTime
                    };
                    dict.Add(key, totals);
                }

                // aggregate totals
                // only loop entries once
                foreach (var entry in historyByGrain[key])
                {
                    totals.TotalAwaitTime += entry.ElapsedTime;
                    totals.TotalCalls += entry.Count;
                    totals.TotalExceptions += entry.ExceptionCount;
                }

                stats[i] = new SimpleGrainStatisticCounter(totals)
                {
                    ActivationCount = x.ActivationCount,
                    GrainType = grainName,
                    SiloAddress = x.SiloAddress.ToParsableString()
                };
            }

            this.Counters.SimpleGrainStats = stats;
        }

        public override Task OnActivateAsync()
        {
            this.Counters = new DashboardCounters();
            this.RegisterTimer(this.Callback, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            this.StartTime = DateTime.UtcNow;
            return base.OnActivateAsync();
        }

        public Task<DashboardCounters> GetCounters()
        {
            return Task.FromResult(this.Counters);
        }

        public Task<Dictionary<string, Dictionary<string, GrainTraceEntry>>> GetGrainTracing(string grain)
        {
            var results = new Dictionary<string, Dictionary<string, GrainTraceEntry>>();

            foreach (var historicValue in this.history.Where(x => x.Grain == grain))
            {
                var grainMethodKey = $"{grain}.{historicValue.Method}";
                if (!results.ContainsKey(grainMethodKey))
                {
                    results.Add(grainMethodKey, new Dictionary<string, GrainTraceEntry>());
                }
                var grainResults = results[grainMethodKey];

                var key = historicValue.Period.ToPeriodString();
                if (!grainResults.ContainsKey(key)) grainResults.Add(key, new GrainTraceEntry
                {
                    Grain = historicValue.Grain,
                    Method = historicValue.Method,
                    Period = historicValue.Period
                });
                var value = grainResults[key];
                value.Count += historicValue.Count;
                value.ElapsedTime += historicValue.ElapsedTime;
                value.ExceptionCount += historicValue.ExceptionCount;
            }

            return Task.FromResult(results);
        }

        public Task<Dictionary<string, GrainTraceEntry>> GetClusterTracing()
        {
            var results = new Dictionary<string, GrainTraceEntry>();

            foreach (var historicValue in this.history)
            {
                var key = historicValue.Period.ToPeriodString();
                GrainTraceEntry value;
                if (!results.TryGetValue(key, out value))
                {
                    value = new GrainTraceEntry
                    {
                        Period = historicValue.Period,
                    };

                    results[key] = value;
                }

                value.Count += historicValue.Count;
                value.ElapsedTime += historicValue.ElapsedTime;
                value.ExceptionCount += historicValue.ExceptionCount;
            }

            return Task.FromResult(results);
        }

        public Task<Dictionary<string, GrainTraceEntry>> GetSiloTracing(string address)
        {
            var results = new Dictionary<string, GrainTraceEntry>();

            foreach (var historicValue in this.history.Where(x => x.SiloAddress == address))
            {
                var key = historicValue.Period.ToPeriodString();
                if (!results.ContainsKey(key)) results.Add(key, new GrainTraceEntry
                {
                    Period = historicValue.Period,
                });
                var value = results[key];
                value.Count += historicValue.Count;
                value.ElapsedTime += historicValue.ElapsedTime;
                value.ExceptionCount += historicValue.ExceptionCount;
            }

            return Task.FromResult(results);
        }

        public Task Init()
        {
            // just used to activate the grain
            return TaskDone.Done;
        }

        public Task SubmitTracing(string siloIdentity, GrainTraceEntry[] grainTrace)
        {
            var now = DateTime.UtcNow;
            foreach (var entry in grainTrace)
            {
                // sync clocks
                entry.Period = now;
            }

            // fill in any previously captured methods which aren't in this reporting window
            var allGrainTrace = new List<GrainTraceEntry>(grainTrace);
            var values = this.history
                .Where(x => x.SiloAddress == siloIdentity)
                .GroupBy(x => x.GrainAndMethod)
                .Select(x => x.First());


            foreach (var value in values)
            {
                if (!grainTrace.Any(x => x.GrainAndMethod == value.GrainAndMethod))
                {
                    allGrainTrace.Add(new GrainTraceEntry
                    {
                        Count = 0,
                        ElapsedTime = 0,
                        Grain = value.Grain,
                        Method = value.Method,
                        Period = now,
                        SiloAddress = siloIdentity
                    });
                }
            }

            var retirementWindow = DateTime.UtcNow.AddSeconds(-100);
            history.AddRange(allGrainTrace);
            history.RemoveAll(x => x.Period < retirementWindow);

            return TaskDone.Done;
        }
    }
}