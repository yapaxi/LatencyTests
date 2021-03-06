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
        private static readonly int MAX_CONNECTIONS_PER_SERVER = int.MaxValue;

        private static readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

#if NETSTANDARD2_0
        private static readonly HttpClient _httpClient = new HttpClient(new HttpClientHandler()
        {
            MaxConnectionsPerServer  = MAX_CONNECTIONS_PER_SERVER
        });
#else 
        private static readonly HttpClient _httpClient = new HttpClient();
#endif

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
            ServicePointManager.DefaultConnectionLimit = MAX_CONNECTIONS_PER_SERVER;

            Console.CancelKeyPress += Console_CancelKeyPress;

            var settings = ResolveSettings(args);

            Console.WriteLine($"URL: {settings.Url}");

            Warmup(settings.WithTimeout(TimeSpan.FromSeconds(5)));

            Console.WriteLine($"Settings: run for {settings.Timeout} using {settings.ConcurrentCalls} threads with {settings.Delay} delay between calls and repeat everything {settings.Repeat} times");

            const string SEP = "-------";
            var headerSep = $"{SEP}\t{SEP}\t{SEP}\t{SEP}\t{SEP}\t{SEP}";
            Console.WriteLine(headerSep);
            Console.WriteLine($"#\tCode\tCount\tSTDev\tAvg\tPercentiles ({string.Join(",", settings.PerncentilesToCalculate)})");
            Console.WriteLine(headerSep);

            var stage = 0;

            var sw = Stopwatch.StartNew();
            while (++stage <= settings.Repeat)
            {
                if (!_cancellationTokenSource.IsCancellationRequested)
                {
                    var list = Run(settings);

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

                        var percentilesResult = string.Join(
                            ", ",
                            settings.PerncentilesToCalculate.Select(e => GetPercentile(orderedDurations, e))
                        );

                        Console.WriteLine($"{stage}\t{httpCode}\t{count}\t{stdev}\t{avg}\t{percentilesResult}");
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine($"done; total-time: {sw.Elapsed}");
        }

        private static Settings ResolveSettings(string[] args)
        {
            //0
            var url = args.Length > 0 ? args[0] : throw new Exception("Url is required as a first argument");

            //1
            var concurrentCalls = args.Length > 1 ? int.Parse(args[1]) : 2;

            //2
            var seconds = args.Length > 2 ? int.Parse(args[2]) : 60;

            //3
            var repeat = args.Length > 3 ? int.Parse(args[3]) : 1;

            //4
            var delay = TimeSpan.FromMilliseconds(args.Length > 4 ? int.Parse(args[4]) : 0);

            //5
            var auth = args.Length > 5 ? args[5]?.Trim() : null;

            //6
            var percentilesDefault = new[] { 5, 25, 50, 75, 95, 99 };
            var percentilesToCalculate = (args.Length > 6
                ? args[6].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray()
                : percentilesDefault
            )
            .Where(e => e >= 0 && e <= 100)
            .ToArray();

            var settings = new Settings()
            {
                Repeat = repeat,
                AuthorizationHeader = auth,
                ConcurrentCalls = concurrentCalls,
                Delay = delay,
                Limit = null,
                Timeout = TimeSpan.FromSeconds(seconds),
                Url = url,
                PerncentilesToCalculate = percentilesToCalculate
            };
            return settings;
        }

        private static void Warmup(Settings settings)
        {
            Console.Write("Warmup: ");
            Console.WriteLine($"run for {settings.Timeout}");
            var z = Run(settings);
        }

        private static double GetPercentile(TimeSpan[] durations, int p)
        {
            var mul = p * 0.01;

            return Math.Round((durations.Length > 0
                        ? durations[(int)(durations.Length * mul)]
                        : TimeSpan.Zero).TotalMilliseconds, 2);
        }

        private static List<IReadOnlyDictionary<HttpStatusCode, Record>> Run(Settings settings)
        {
            var timeoutTokenSource = new CancellationTokenSource();
            var timeCheckTask = TimeCheck(settings.Timeout, timeoutTokenSource);

            var list = new List<IReadOnlyDictionary<HttpStatusCode, Record>>();

            var threads = Enumerable.Range(0, settings.ConcurrentCalls).Select(e =>
            {
                var t = new Thread(() =>
                {
                    var dict = Loop(settings, timeoutTokenSource).GetAwaiter().GetResult();
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

        private static async Task<IReadOnlyDictionary<HttpStatusCode, Record>> Loop(Settings settings, CancellationTokenSource timeoutTokenSource)
        {
            var dict = new Dictionary<HttpStatusCode, Record>();
            var maxCalls = settings.Limit ?? Int32.MaxValue;

            try
            {
                while (
                    !_cancellationTokenSource.IsCancellationRequested 
                    && !timeoutTokenSource.IsCancellationRequested
                    && --maxCalls > 0)
                {
                    var message = new HttpRequestMessage(HttpMethod.Get, settings.Url);
                    if (!string.IsNullOrWhiteSpace(settings.AuthorizationHeader))
                    {
                        message.Headers.Add("Authorization", settings.AuthorizationHeader);
                    }
                    var sw = Stopwatch.StartNew();
                    var response = await _httpClient.SendAsync(
                        message,
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
                    
                    if (settings.Delay >= TimeSpan.Zero)
                    {
                        await Task.Delay(settings.Delay);
                    }
                }
            }
            catch (OperationCanceledException)
            {

            }

            return dict;
        }

        private class Settings
        {
            public int Repeat { get; set; }
            public TimeSpan Timeout { get; set; }
            public string Url { get; set; }
            public int? Limit { get; set; }
            public TimeSpan Delay { get; set; }
            public string AuthorizationHeader { get; set; }
            public int ConcurrentCalls { get; set; }
            public int[] PerncentilesToCalculate { get; set; }

            public Settings WithTimeout(TimeSpan timeSpan)
            {
                return new Settings()
                {
                    Timeout = timeSpan,
                    AuthorizationHeader = this.AuthorizationHeader,
                    ConcurrentCalls = this.ConcurrentCalls,
                    Delay = this.Delay,
                    Limit = this.Limit,
                    Url = this.Url,
                    PerncentilesToCalculate = this.PerncentilesToCalculate,
                    Repeat = this.Repeat
                };
            }
        }

        private class Record
        {
            public int Count { get; set; }
            public List<TimeSpan> Durations { get; set; }
        }
    }
}
