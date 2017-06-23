﻿using BenchmarkDotNet.Attributes;
using Orleans;
using Orleans.Runtime;
using OrleansDashboard;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PerformanceTests
{
    [MemoryDiagnoser]
    public class DashboardGrainBenchmark
    {
        private MembershipEntry[] _silos;
        private List<string> _grainTypes;
        private DashboardGrain _dashboardGrain;
        private SimpleGrainStatistic[] _simpleGrainStatistics;
        private int _totalActivationCount;

        [Params(3)]
        public int SiloCount { get; set; }

        [Params(50, 100, 200)]
        public int GrainTypeCount { get; set; }

        [Params(10)]
        public int GrainMethodCount { get; set; }

        [Params(100)]
        public int GrainActivationPerSiloCount { get; set; }

        [Params(1000)]
        public int GrainCallsPerActivationCount { get; set; }

        [Setup]
        public void Setup()
        {
            SetupAsync().Wait();
        }

        private async Task SetupAsync()
        {

            _silos = Enumerable.Range(0, SiloCount)
                .Select(i => new MembershipEntry
                {
                    SiloAddress = SiloAddress.NewLocalAddress(i)
                }).ToArray();

            _grainTypes = new List<string>();
            for (int i = 0; i < GrainTypeCount; i++)
            {
                _grainTypes.Add("Grain" + Guid.NewGuid());
            }

            _simpleGrainStatistics =
            (
                from silo in _silos
                from grainType in _grainTypes
                select new SimpleGrainStatistic
                {
                    ActivationCount = GrainActivationPerSiloCount,
                    GrainType = grainType,
                    SiloAddress = silo.SiloAddress
                }).ToArray();

            _totalActivationCount = _simpleGrainStatistics.Sum(s => s.ActivationCount);

            _dashboardGrain = new TestDashboardGrain();

            var grainMethods = new List<string>();
            for (var i = 0; i < GrainMethodCount; i++)
            {
                grainMethods.Add("Method" + Guid.NewGuid());
            }

            await _dashboardGrain.OnActivateAsync().ConfigureAwait(false);
            var now = DateTime.UtcNow;
            foreach (var silo in _silos)
            {
                var grainTracings = new List<GrainTraceEntry>();
                foreach (var grainType in _grainTypes)
                {
                    foreach (var grainMethod in grainMethods)
                    {
                        grainTracings.Add(new GrainTraceEntry
                        {
                            Count = GrainCallsPerActivationCount * GrainActivationPerSiloCount,
                            ElapsedTime = 50,
                            Grain = grainType,
                            Method = grainMethod,
                            Period = now,
                            SiloAddress = silo.SiloAddress.ToParsableString()
                        });
                    }
                }
                await _dashboardGrain.SubmitTracing(silo.SiloAddress.ToParsableString(), grainTracings.ToArray())
                    .ConfigureAwait(false);
            }
        }

        [Benchmark]
        public void Recalculate()
        {
            _dashboardGrain.RecalculateCounters(_totalActivationCount, _silos, _simpleGrainStatistics);
        }
    }
}