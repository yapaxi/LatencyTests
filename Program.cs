﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LatencyTests
{
    class Program
    {
        private static readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private static readonly HttpClient _httpClient = new HttpClient(new HttpClientHandler()
        {
           
        });

        static async Task TimeCheck(TimeSpan maxTime, CancellationTokenSource timeoutTokenSource)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    if (sw.Elapsed >= maxTime)
                    {
                        timeoutTokenSource.Cancel();
                        return;
                    }

                    await Task.Delay(1000, _cancellationTokenSource.Token);
                }
            }
            catch (OperationCanceledException)
            {
                
            }
        }

        static void Main(string[] args)
        {
            Console.CancelKeyPress += Console_CancelKeyPress;
            ServicePointManager.DefaultConnectionLimit = 100500;
            var url = args.Length > 0 ? args[0] : throw new Exception("Url is required as a first argument");
            var concurrentCalls = args.Length > 1 ? int.Parse(args[1]) : 2;
            var seconds = args.Length > 2 ? int.Parse(args[2]) : 60;
            var repeat = args.Length > 3 ? int.Parse(args[3]) : 1;
            var delay = TimeSpan.FromMilliseconds(args.Length > 4 ? int.Parse(args[4]) : 0);

            Warmup(url, concurrentCalls);

            var loadTimeout = TimeSpan.FromSeconds(seconds);
            Console.Write("Start... ");
            Console.WriteLine($"Running for {loadTimeout} using {concurrentCalls} threads and repeating {repeat} times with {delay} delay");


            const string SEP = "-------";
            var headerSep = $"{SEP}\t{SEP}\t{SEP}\t{SEP}\t{SEP}";
            Console.WriteLine(headerSep);
            Console.WriteLine("Code\tCount\tSTDev\tAvg\tPercentiles (5,25,50,75,95,99)");
            Console.WriteLine(headerSep);

            var rem = repeat;

            var sw = Stopwatch.StartNew();
            while (--rem >= 0)
            {
                if (!_cancellationTokenSource.IsCancellationRequested)
                {
                    var list = Run(url, concurrentCalls, loadTimeout, delay);

                    var httpsCodes = list.SelectMany(e => e).Select(e => e.Key).Distinct().ToArray();
                    foreach (var httpCode in httpsCodes)
                    {
                        var codeStatistics = list
                            .SelectMany(e => e)
                            .Where(q => q.Key == httpCode)
                            .ToArray();

                        var orderedDurations = codeStatistics.SelectMany(e => e.Value.Durations).OrderBy(e => e).ToArray();
                        var count = codeStatistics.Sum(e => e.Value.Count);

                        var avg = Math.Round((orderedDurations.Length > 0
                                    ? TimeSpan.FromMilliseconds(orderedDurations.Average(e => e.TotalMilliseconds))
                                    : TimeSpan.Zero).TotalMilliseconds, 2);

                        var stdev = Math.Round(orderedDurations.Length > 1
                                    ? Math.Sqrt(orderedDurations.Select(e => Math.Pow(e.TotalMilliseconds - avg, 2)).Sum() / (orderedDurations.Length - 1.0))
                                    : 0, 2);

                        var p5 = GetPercentile(orderedDurations, 5);
                        var p25 = GetPercentile(orderedDurations, 25);
                        var p50 = GetPercentile(orderedDurations, 50);
                        var p75 = GetPercentile(orderedDurations, 75);
                        var p95 = GetPercentile(orderedDurations, 95);
                        var p99 = GetPercentile(orderedDurations, 99);

                        Console.WriteLine($"{httpCode}\t{count}\t{stdev}\t{avg}\t{p5}, {p25}, {p50}, {p75}, {p95}, {p99}");
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine($"done; total-time: {sw.Elapsed}");
        }

        private static void Warmup(string url, int concurrentCalls)
        {
            var testTimeout = TimeSpan.FromSeconds(5);
            Console.Write("Warmup... ");
            Console.WriteLine($"Running for {testTimeout}");
            var z = Run(url, concurrentCalls, testTimeout, TimeSpan.Zero);
        }

        private static double GetPercentile(TimeSpan[] durations, int p)
        {
            var mul = p * 0.01;

            return Math.Round((durations.Length > 0
                        ? durations[(int)(durations.Length * mul)]
                        : TimeSpan.Zero).TotalMilliseconds, 2);
        }

        private static List<IReadOnlyDictionary<HttpStatusCode, Record>> Run(
            string url, 
            int concurrentCalls, 
            TimeSpan timeout,
            TimeSpan delay)
        {
            var timeoutTokenSource = new CancellationTokenSource();
            var timeCheckTask = TimeCheck(timeout, timeoutTokenSource);

            var list = new List<IReadOnlyDictionary<HttpStatusCode, Record>>();

            var threads = Enumerable.Range(0, concurrentCalls).Select(e =>
            {
                var t = new Thread(() =>
                {
                    var dict = Loop(url, null, timeoutTokenSource, delay).GetAwaiter().GetResult();
                    lock (list)
                    {
                        list.Add(dict);
                    }
                });
                return t;
            }).ToList();

            threads.ForEach(e => e.Start());
            threads.ForEach(e => e.Join());
            timeCheckTask.GetAwaiter().GetResult();

            return list;
        }

        private static void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            Console.WriteLine("Cancelling...");
            e.Cancel = true;
            _cancellationTokenSource.Cancel();
        }

        private static async Task<IReadOnlyDictionary<HttpStatusCode, Record>> Loop(string url, int? limit, CancellationTokenSource timeoutTokenSource, TimeSpan delay)
        {
            var dict = new Dictionary<HttpStatusCode, Record>();
            var maxCalls = limit ?? Int32.MaxValue;

            try
            {
                while (
                    !_cancellationTokenSource.IsCancellationRequested 
                    && !timeoutTokenSource.IsCancellationRequested
                    && --maxCalls > 0)
                {
                    var sw = Stopwatch.StartNew();
                    var response = await _httpClient.SendAsync(
                        new HttpRequestMessage(HttpMethod.Get, url),
                        _cancellationTokenSource.Token
                    );
                    await response.Content.ReadAsStringAsync();
                    
                    var el = sw.Elapsed;

                    dict.TryGetValue(response.StatusCode, out var val);
                    if (val == null)
                    {
                        val = new Record()
                        {
                            Durations = new List<TimeSpan>()
                        };
                    }
                    val.Count += 1;
                    val.Durations.Add(el);
                    dict[response.StatusCode] = val;
                    
                    if (delay >= TimeSpan.Zero)
                    {
                        await Task.Delay(delay);
                    }
                }
            }
            catch (OperationCanceledException)
            {

            }

            return dict;
        }

        private class Record
        {
            public int Count { get; set; }
            public List<TimeSpan> Durations { get; set; }
        }
    }
}
