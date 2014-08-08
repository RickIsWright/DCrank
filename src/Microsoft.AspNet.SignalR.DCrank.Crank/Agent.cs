﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNet.SignalR.Client;
using Newtonsoft.Json;

namespace Microsoft.AspNet.SignalR.DCrank.Crank
{
    public class Agent
    {
        private const string _url = "http://localhost:17063";
        private const string _fileName = "crank.exe";
        private const string _hubName = "ControllerHub";
        private readonly ConcurrentDictionary<int, AgentWorker> _workers;
        private readonly string _hostName;
        private HubConnection _connection;
        private IHubProxy _proxy;


        public Agent()
        {
            Trace.Listeners.Add(new ConsoleTraceListener());

            _hostName = Dns.GetHostName();

            _workers = new ConcurrentDictionary<int, AgentWorker>();

            Trace.WriteLine("Agent created");
        }

        public async void Run()
        {
            while (true)
            {
                try
                {
                    using (_connection = new HubConnection(_url))
                    {
                        _proxy = _connection.CreateHubProxy(_hubName);
                        InitializeProxy();

                        Trace.WriteLine("Attempting to connect to TestController");

                        try
                        {
                            await _connection.Start();

                            LogAgent("Agent connected to TestController.", _connection.ConnectionId);

                            while (_connection.State == ConnectionState.Connected)
                            {
                                await InvokeController("agentHeartbeat", new
                                {
                                    HostName = _hostName,
                                    Workers = _workers.Values.Select(worker => new
                                    {
                                        Id = worker.Id,
                                        ConnectedCount = worker.ConnectedCount,
                                        DisconnectedCount = worker.DisconnectedCount,
                                        ReconnectedCount = worker.ReconnectedCount
                                    })
                                });

                                await Task.Delay(1000);
                            }
                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLine(string.Format("Agent failed to connect to server: {0}", ex.Message));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(string.Format("Connection lost: {0}", ex.Message));
                }

                await Task.Delay(10000);
            }
        }

        private AgentWorker CreateWorker()
        {
            var startInfo = new ProcessStartInfo()
            {
                FileName = _fileName,
                Arguments = string.Format("worker {0}", Process.GetCurrentProcess().Id),
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var worker = new AgentWorker(startInfo);

            worker.Start();

            worker.OnMessage += OnMessage;
            worker.OnError += OnError;
            worker.OnExit += OnExit;

            _workers.TryAdd(worker.Id, worker);

            return worker;
        }

        private async Task StartWorker(int id, string targetAddress, int numberOfConnectionsPerWorker)
        {
            AgentWorker worker;
            if (_workers.TryGetValue(id, out worker))
            {
                await worker.Connect(targetAddress, numberOfConnectionsPerWorker);
            }
        }

        private void InitializeProxy()
        {
            _proxy.On<int>("pingAgent", value =>
            {
                LogAgent("Agent received pingAgent with value {0}.", value);
                InvokeController("pongAgent", value);
            });

            _proxy.On<string, int, int>("startWorkers", (targetAddress, numberOfWorkers, numberOfConnectionsPerWorker) =>
            {
                LogAgent("Agent received startWorker command for target {0} with {1} workers and {2} connections per worker.", targetAddress, numberOfWorkers, numberOfConnectionsPerWorker);
                StartWorkers(targetAddress, numberOfWorkers, numberOfConnectionsPerWorker);
            });

            _proxy.On<int>("killWorker", workerId =>
            {
                LogAgent("Agent received killWorker command for Worker {0}.", workerId);

                AgentWorker worker;

                if (_workers.TryGetValue(workerId, out worker))
                {
                    worker.Kill();
                    _workers.TryRemove(workerId, out worker);
                    LogAgent("Agent killed Worker {0}.", workerId);
                }
            });

            _proxy.On<int>("killWorkers", numberOfWorkersToKill =>
            {
                LogAgent("Agent received killWorker command to kill {0} workers.", numberOfWorkersToKill);

                var keys = _workers.Keys.Take(numberOfWorkersToKill).ToList();

                foreach (var key in keys)
                {
                    AgentWorker worker;
                    if (_workers.TryGetValue(key, out worker))
                    {
                        worker.Kill();
                        _workers.TryRemove(key, out worker);
                        LogAgent("Agent killed Worker {0}.", key);
                    }
                }
            });

            _proxy.On<int, int>("pingWorker", (workerId, value) =>
            {
                LogAgent("Agent received pingWorker for Worker {0} with value {1}.", workerId, value);

                AgentWorker worker;

                if (_workers.TryGetValue(workerId, out worker))
                {
                    worker.Send("ping", value);
                    LogAgent("Agent sent ping command to Worker {0} with value {1}.", workerId, value);
                }
                else
                {
                    LogAgent("Agent failed to send ping command, Worker {0} not found.", workerId);
                }
            });

            _proxy.On<int, int>("startTest", (messageSize, messagesPerSecond) =>
            {
                LogAgent("Agent received test information with message size: {0}, and messages sent per second: {1}.", messageSize, messagesPerSecond);

                Task.Run(() =>
                {
                    foreach (var worker in _workers.Values)
                    {
                        worker.StartTest(messageSize, messagesPerSecond);
                    }
                });
            });

            _proxy.On<int>("stopWorker", async workerId =>
            {
                AgentWorker worker;
                await _workers[workerId].Stop();
                _workers.TryRemove(workerId, out worker);
            });
        }

        private void StartWorkers(string targetAddress, int numberOfWorkers, int numberOfConnectionsPerWorker)
        {
            Task.Run(() =>
            {
                Parallel.For(0, numberOfWorkers, async index =>
                {
                    var worker = CreateWorker();

                    await StartWorker(worker.Id, targetAddress, numberOfConnectionsPerWorker);

                    LogAgent("Agent started listening to worker {0} ({1} of {2}).", worker.Id, index, numberOfWorkers);
                });
            });
        }

        private void OnMessage(int id, Message message)
        {
            switch (message.Command.ToLowerInvariant())
            {
                case "ping":
                    LogAgent("Agent received pong message from Worker {0} with value {1}.", id, message.Value);
                    InvokeController("pongWorker", id, message.Value.ToObject<int>());
                    break;

                case "log":
                    LogWorker(id, message.Value.ToObject<string>());
                    break;

                case "status":
                    LogAgent("Agent received status message from Worker {0} with value {1}.", id, message.Value);
                    var worker = _workers[id];
                    worker.ConnectedCount = JsonConvert.DeserializeObject<int>(message.Value["ConnectedCount"].ToString());
                    worker.DisconnectedCount = JsonConvert.DeserializeObject<int>(message.Value["DisconnectedCount"].ToString());
                    worker.ReconnectedCount = JsonConvert.DeserializeObject<int>(message.Value["ReconnectingCount"].ToString());
                    break;
            }
        }

        private void OnError(int workerId, Exception ex)
        {
            AgentWorker worker;
            _workers.TryRemove(workerId, out worker);
            LogWorker(workerId, ex.Message);
        }

        private void OnExit(int workerId)
        {
            AgentWorker worker;
            _workers.TryRemove(workerId, out worker);
        }

        private async void LogWorker(int workerId, string format, params object[] arguments)
        {
            var prefix = string.Format("({0}, {1}) ", _connection.ConnectionId, workerId);
            var message = "[" + DateTime.Now.ToString() + "] " + string.Format(format, arguments);
            Trace.WriteLine(prefix + message);

            try
            {
                await _proxy.Invoke("LogWorker", workerId, message);
            }
            catch (Exception ex)
            {
                Trace.WriteLine(prefix + string.Format("LogWorker threw an exception: {0}", ex.Message));
            }
        }

        private async void LogAgent(string format, params object[] arguments)
        {
            var prefix = string.Format("({0}) ", _connection.ConnectionId, DateTime.Now);
            var message = "[" + DateTime.Now.ToString() + "] " + string.Format(format, arguments);
            Trace.WriteLine(prefix + message);

            try
            {
                await _proxy.Invoke("LogAgent", message);
            }
            catch (Exception ex)
            {
                Trace.WriteLine(prefix + string.Format("LogAgent threw an exception: {0}", ex.Message));
            }
        }

        private async Task InvokeController(string command, params object[] arguments)
        {
            var commandString = command + "(" + string.Join(", ", JsonConvert.SerializeObject(arguments)) + ")";

            try
            {
                await _proxy.Invoke(command, arguments);
                LogAgent("Agent completed call to TestController: {0}", commandString);
            }
            catch (Exception ex)
            {
                Trace.WriteLine(string.Format("Agent attempted call to TestController: {0}. Exception: {1}", command, ex.Message));
            }
        }
    }
}
