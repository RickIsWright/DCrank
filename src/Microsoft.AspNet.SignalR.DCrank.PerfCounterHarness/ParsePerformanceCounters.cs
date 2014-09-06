﻿using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace Microsoft.AspNet.SignalR.DCrank.PerfCounterHarness
{
    public class ParsePerformanceCounters
    {
        public static List<DCrankPerformanceCounter> ParseCounters(List<PerformanceCounterSample> counterSamples)
        {
            var counterList = new List<DCrankPerformanceCounter>();

            var latestCounterSample = counterSamples.ElementAt(0);
            var previousCounterSample = counterSamples.ElementAt(1);

            if (latestCounterSample != null && previousCounterSample != null)
            {
                var dummyObject = new { definitions = new List<PerformanceCounterDefinition>(), values = new List<PerformanceCounterValues>() };

                var latestSmapleCounterValues = JsonConvert.DeserializeAnonymousType(latestCounterSample.PerformanceCounterJsonBlob, dummyObject);
                var previousSmapleCounterValues = JsonConvert.DeserializeAnonymousType(previousCounterSample.PerformanceCounterJsonBlob, dummyObject);

                // Creating dictionary to link perf counters to values
                var latestPerfCounterValueDictionary = new Dictionary<int, long>();
                var previousPerfCounterValueDictionary = new Dictionary<int, long>();

                // Adding current and previous perf counter sample values to dictionaries
                foreach (var perfCounterValue in latestSmapleCounterValues.values)
                {
                    latestPerfCounterValueDictionary.Add(perfCounterValue.ValueId, perfCounterValue.Value);
                }

                foreach (var perfCounterValue in previousSmapleCounterValues.values)
                {
                    previousPerfCounterValueDictionary.Add(perfCounterValue.ValueId, perfCounterValue.Value);
                }

                foreach (var perfCounterDefinition in latestSmapleCounterValues.definitions)
                {
                    var perfCounter = new DCrankPerformanceCounter(perfCounterDefinition.Name);

                    if (perfCounterDefinition.Type == PerfCounterHarness.PerformanceCounterType.Total)
                    {
                        long value;
                        latestPerfCounterValueDictionary.TryGetValue(perfCounterDefinition.ValueId, out value);

                        perfCounter.RawValue = value;
                    }
                    else if (perfCounterDefinition.Type == PerfCounterHarness.PerformanceCounterType.PerSecRate)
                    {
                        long latestValue, previousValue;
                        latestPerfCounterValueDictionary.TryGetValue(perfCounterDefinition.ValueId, out latestValue);
                        previousPerfCounterValueDictionary.TryGetValue(perfCounterDefinition.ValueId, out previousValue);

                        perfCounter.RawValue = (latestValue - previousValue) / latestCounterSample.Timestamp.Subtract(previousCounterSample.Timestamp).Seconds;
                    }

                    counterList.Add(perfCounter);
                }
            }

            return counterList;
        }
    }
}