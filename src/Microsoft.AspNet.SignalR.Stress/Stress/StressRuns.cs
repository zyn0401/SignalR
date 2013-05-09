// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.md in the project root for license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNet.SignalR;
using Microsoft.AspNet.SignalR.Client.Http;
using Microsoft.AspNet.SignalR.Client.Hubs;
using Microsoft.AspNet.SignalR.Configuration;
using Microsoft.AspNet.SignalR.Hosting.Memory;
using Microsoft.AspNet.SignalR.Hubs;
using Microsoft.AspNet.SignalR.Infrastructure;
using Microsoft.AspNet.SignalR.Messaging;
using Microsoft.AspNet.SignalR.Samples.Raw;
using Microsoft.AspNet.SignalR.Tests.Infrastructure;
using Newtonsoft.Json.Linq;
using Owin;

namespace Microsoft.AspNet.SignalR.Stress
{
    public static class StressRuns
    {
        public static IDisposable StressGroups(int max = 100)
        {
            var host = new MemoryHost();
            host.Configure(app =>
            {
                var config = new HubConfiguration()
                {
                    Resolver = new DefaultDependencyResolver()
                };

                app.MapHubs(config);

                var configuration = config.Resolver.Resolve<IConfigurationManager>();
                // The below effectively sets the heartbeat interval to five seconds.
                configuration.KeepAlive = TimeSpan.FromSeconds(10);
            });

            var countDown = new CountDownRange<int>(Enumerable.Range(0, max));
            var connection = new Client.Hubs.HubConnection("http://foo");
            var proxy = connection.CreateHubProxy("HubWithGroups");

            proxy.On<int>("Do", i =>
            {
                if (!countDown.Mark(i))
                {
                    Debugger.Break();
                }
            });

            try
            {
                connection.Start(new Client.Transports.LongPollingTransport(host)).Wait();

                proxy.Invoke("Join", "foo").Wait();

                for (int i = 0; i < max; i++)
                {
                    proxy.Invoke("Send", "foo", i).Wait();
                }

                proxy.Invoke("Leave", "foo").Wait();

                for (int i = max + 1; i < max + 50; i++)
                {
                    proxy.Invoke("Send", "foo", i).Wait();
                }

                if (!countDown.Wait(TimeSpan.FromSeconds(10)))
                {
                    Console.WriteLine("Didn't receive " + max + " messages. Got " + (max - countDown.Count) + " missed " + String.Join(",", countDown.Left.Select(i => i.ToString())));
                    Debugger.Break();
                }
            }
            finally
            {
                connection.Stop();
            }

            return host;
        }

        public static IDisposable BrodcastFromServer()
        {
            var host = new MemoryHost();
            IHubContext context = null;

            host.Configure(app =>
            {
                var config = new HubConfiguration()
                {
                    Resolver = new DefaultDependencyResolver()
                };

                app.MapHubs(config);

                var configuration = config.Resolver.Resolve<IConfigurationManager>();
                // The below effectively sets the heartbeat interval to five seconds.
                configuration.KeepAlive = TimeSpan.FromSeconds(10);

                var connectionManager = config.Resolver.Resolve<IConnectionManager>();
                context = connectionManager.GetHubContext("EchoHub");
            });

            var cancellationTokenSource = new CancellationTokenSource();

            var thread = new Thread(() =>
            {
                while (!cancellationTokenSource.IsCancellationRequested)
                {
                    context.Clients.All.echo();
                }
            });

            thread.Start();

            var connection = new Client.Hubs.HubConnection("http://foo");
            var proxy = connection.CreateHubProxy("EchoHub");

            try
            {
                connection.Start(host).Wait();

                Thread.Sleep(1000);
            }
            finally
            {
                connection.Stop();
            }

            return new DisposableAction(() =>
            {
                cancellationTokenSource.Cancel();

                thread.Join();

                host.Dispose();
            });
        }

        public static IDisposable ManyUniqueGroups(int concurrency)
        {
            var host = new MemoryHost();
            var threads = new List<Thread>();
            var cancellationTokenSource = new CancellationTokenSource();

            host.Configure(app =>
            {
                var config = new HubConfiguration()
                {
                    Resolver = new DefaultDependencyResolver()
                };
                app.MapHubs(config);
            });

            for (int i = 0; i < concurrency; i++)
            {
                var thread = new Thread(_ =>
                {
                    while (!cancellationTokenSource.IsCancellationRequested)
                    {
                        RunOne(host);
                    }
                });

                threads.Add(thread);
                thread.Start();
            }

            return new DisposableAction(() =>
            {
                cancellationTokenSource.Cancel();

                threads.ForEach(t => t.Join());

                host.Dispose();
            });
        }

        private static void RunOne(MemoryHost host)
        {
            var connection = new Client.Hubs.HubConnection("http://foo");
            var proxy = connection.CreateHubProxy("OnConnectedOnDisconnectedHub");

            try
            {
                connection.Start(host).Wait();

                string guid = Guid.NewGuid().ToString();
                string otherGuid = proxy.Invoke<string>("Echo", guid).Result;

                if (!guid.Equals(otherGuid))
                {
                    throw new InvalidOperationException("Fail!");
                }
            }
            finally
            {
                connection.Stop();
            }
        }


        public static IDisposable RunConnectDisconnect(int connections)
        {
            var host = new MemoryHost();

            host.Configure(app =>
            {
                var config = new HubConfiguration()
                {
                    Resolver = new DefaultDependencyResolver()
                };
                app.MapHubs(config);
            });

            for (int i = 0; i < connections; i++)
            {
                var connection = new Client.Hubs.HubConnection("http://foo");
                var proxy = connection.CreateHubProxy("EchoHub");
                var wh = new ManualResetEventSlim(false);

                proxy.On("echo", _ => wh.Set());

                try
                {
                    connection.Start(host).Wait();

                    proxy.Invoke("Echo", "foo").Wait();

                    if (!wh.Wait(TimeSpan.FromSeconds(10)))
                    {
                        Debugger.Break();
                    }
                }
                finally
                {
                    connection.Stop();
                }
            }

            return host;
        }

        public static void Scaleout(int nodes, int clients, int streams)
        {
            var listener = new CircularTraceListener(1000);
            Trace.Listeners.Add(listener);

            var hosts = new MemoryHost[nodes];
            var random = new Random();
            var eventBus = new EventBus();
            for (var i = 0; i < nodes; ++i)
            {
                var host = new MemoryHost();

                host.Configure(app =>
                {
                    var config = new HubConfiguration()
                    {
                        Resolver = new DefaultDependencyResolver()
                    };

                    var delay = i % 2 == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(1);
                    var bus = new DelayedMessageBus(host.InstanceName, streams, eventBus, config.Resolver, delay);
                    config.Resolver.Register(typeof(IMessageBus), () => bus);

                    var configuration = config.Resolver.Resolve<IConfigurationManager>();
                    configuration.KeepAlive = null;
                    configuration.ConnectionTimeout = TimeSpan.FromSeconds(10);
                    configuration.DefaultMessageBufferSize = 50;

                    app.MapHubs(config);
                });

                hosts[i] = host;
            }

            var client = new LoadBalancer(hosts);
            var wh = new ManualResetEventSlim();

            for (int i = 0; i < clients; i++)
            {
                Task.Run(() => RunLoop(client, wh));
            }

            wh.Wait();
        }

        private static void RunLoop(IHttpClient client, ManualResetEventSlim wh)
        {
            var connection = new Client.Hubs.HubConnection("http://foo");
            var proxy = connection.CreateHubProxy("EchoHub");
            connection.TraceLevel = Client.TraceLevels.All;
            var dict = new Dictionary<string, List<int>>();

            proxy.On<int, string>("send", (next, connectionId) =>
            {
                if (wh.IsSet)
                {
                    return;
                }

                List<int> values;
                if (dict.TryGetValue(connectionId, out values))
                {
                    if (values.Contains(next))
                    {
                        Trace.WriteLine(String.Format("{0}: Dupes! => {1}, {2}", connection.ConnectionId, next, connectionId));
                        Trace.Flush();
                        wh.Set();
                        return;
                    }
                }
                else
                {
                    values = new List<int>();
                    dict[connectionId] = values;
                }

                values.Add(next);
                if (connectionId == connection.ConnectionId)
                {
                    proxy.Invoke("send", next + 1).Wait();
                }
            });

            connection.Start(new Client.Transports.ServerSentEventsTransport(client)).Wait();

            proxy.Invoke("send", 0).Wait();
        }

        public static void SendLoop()
        {
            var host = new MemoryHost();

            host.Configure(app =>
            {
                var config = new HubConfiguration()
                {
                    Resolver = new DefaultDependencyResolver()
                };

                config.Resolver.Resolve<IConfigurationManager>().ConnectionTimeout = TimeSpan.FromDays(1);
                app.MapHubs(config);
            });


            var connection = new Client.Hubs.HubConnection("http://foo");
            var proxy = connection.CreateHubProxy("EchoHub");
            var wh = new ManualResetEventSlim(false);

            proxy.On("echo", _ => wh.Set());

            try
            {
                connection.Start(new Client.Transports.LongPollingTransport(host)).Wait();

                while (true)
                {
                    proxy.Invoke("Echo", "foo").Wait();

                    if (!wh.Wait(TimeSpan.FromSeconds(10)))
                    {
                        Debugger.Break();
                    }

                    wh.Reset();
                }
            }
            catch
            {
                connection.Stop();
            }
        }

        public static IDisposable ClientGroupsSyncWithServerGroupsOnReconnectLongPolling()
        {
            var host = new MemoryHost();

            host.Configure(app =>
            {
                var config = new ConnectionConfiguration()
                {
                    Resolver = new DefaultDependencyResolver()
                };

                app.MapConnection<MyRejoinGroupConnection>("/groups", config);

                var configuration = config.Resolver.Resolve<IConfigurationManager>();
                configuration.KeepAlive = null;
                configuration.ConnectionTimeout = TimeSpan.FromSeconds(1);
            });

            var connection = new Client.Connection("http://foo/groups");
            var inGroupOnReconnect = new List<bool>();
            var wh = new ManualResetEventSlim();

            connection.Received += message =>
            {
                Console.WriteLine(message);
                wh.Set();
            };

            connection.Reconnected += () =>
            {
                connection.Send(new { type = 3, group = "test", message = "Reconnected" }).Wait();
            };

            connection.Start(new Client.Transports.LongPollingTransport(host)).Wait();

            // Join the group 
            connection.Send(new { type = 1, group = "test" }).Wait();

            Thread.Sleep(TimeSpan.FromSeconds(10));

            if (!wh.Wait(TimeSpan.FromSeconds(10)))
            {
                Debugger.Break();
            }

            Console.WriteLine(inGroupOnReconnect.Count > 0);
            Console.WriteLine(String.Join(", ", inGroupOnReconnect.Select(b => b.ToString())));

            connection.Stop();

            return host;
        }

        public static IDisposable Connect_Broadcast5msg_AndDisconnect(int concurrency)
        {
            var host = new MemoryHost();
            var threads = new List<Thread>();
            var cancellationTokenSource = new CancellationTokenSource();

            host.Configure(app =>
            {
                var config = new ConnectionConfiguration
                {
                    Resolver = new DefaultDependencyResolver()
                };
                app.MapConnection<RawConnection>("/Raw-connection", config);
            });

            for (int i = 0; i < concurrency; i++)
            {
                var thread = new Thread(_ =>
                {
                    while (!cancellationTokenSource.IsCancellationRequested)
                    {
                        BroadcastFive(host);
                    }
                });

                threads.Add(thread);
                thread.Start();
            }

            return new DisposableAction(() =>
            {
                cancellationTokenSource.Cancel();

                threads.ForEach(t => t.Join());

                host.Dispose();
            });
        }

        private static void BroadcastFive(MemoryHost host)
        {
            var connection = new Client.Connection("http://samples/Raw-connection");

            connection.Error += e =>
            {
                Console.Error.WriteLine("========ERROR==========");
                Console.Error.WriteLine(e.GetBaseException().ToString());
                Console.Error.WriteLine("=======================");
            };

            connection.Start(new Client.Transports.ServerSentEventsTransport(host)).Wait();

            try
            {
                for (int i = 0; i < 5; i++)
                {
                    var payload = new
                    {
                        type = MessageType.Broadcast,
                        value = "message " + i.ToString()
                    };

                    connection.Send(payload).Wait();
                }

            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("========ERROR==========");
                Console.Error.WriteLine(ex.GetBaseException().ToString());
                Console.Error.WriteLine("=======================");
            }
            finally
            {
                connection.Stop();
            }
        }

        enum MessageType
        {
            Send,
            Broadcast,
            Join,
            PrivateMessage,
            AddToGroup,
            RemoveFromGroup,
            SendToGroup,
            BroadcastExceptMe,
        }

        public class MyGroupConnection : PersistentConnection
        {
            protected override Task OnReceived(IRequest request, string connectionId, string data)
            {
                JObject operation = JObject.Parse(data);
                int type = operation.Value<int>("type");
                string group = operation.Value<string>("group");

                if (type == 1)
                {
                    return Groups.Add(connectionId, group);
                }
                else if (type == 2)
                {
                    return Groups.Remove(connectionId, group);
                }
                else if (type == 3)
                {
                    return Groups.Send(group, operation.Value<string>("message"));
                }

                return base.OnReceived(request, connectionId, data);
            }
        }

        public class MyRejoinGroupConnection : MyGroupConnection
        {
        }

        private class LoadBalancer : SignalR.Client.Http.IHttpClient
        {
            private int _counter;
            private readonly SignalR.Client.Http.IHttpClient[] _servers;
            private Random _random = new Random();

            public LoadBalancer(params SignalR.Client.Http.IHttpClient[] servers)
            {
                _servers = servers;
            }

            public Task<Client.Http.IResponse> Get(string url, Action<Client.Http.IRequest> prepareRequest)
            {
                int index = _random.Next(0, _servers.Length);

                Trace.WriteLine(String.Format("{0}: GET {1}", index, new Uri(url).LocalPath));

                _counter = (_counter + 1) % _servers.Length;
                return _servers[index].Get(url, prepareRequest);
            }

            public Task<Client.Http.IResponse> Post(string url, Action<Client.Http.IRequest> prepareRequest, IDictionary<string, string> postData)
            {
                int index = _random.Next(0, _servers.Length);

                Trace.WriteLine(String.Format("{0}: POST {1}", index, new Uri(url).LocalPath));

                _counter = (_counter + 1) % _servers.Length;
                return _servers[index].Post(url, prepareRequest, postData);
            }
        }

        private class EventBus
        {
            private long id;
            public event EventHandler<EventMessage> Received;

            public void Publish(int streamIndex, ScaleoutMessage message)
            {
                if (Received != null)
                {
                    lock (this)
                    {
                        Received(this, new EventMessage
                        {
                            Id = (ulong)id,
                            Message = message,
                            StreamIndex = streamIndex
                        });

                        id++;
                    }
                }
            }
        }

        private class EventMessage
        {
            public int StreamIndex { get; set; }
            public ulong Id { get; set; }
            public ScaleoutMessage Message { get; set; }
        }

        private class DelayedMessageBus : ScaleoutMessageBus
        {
            private readonly TimeSpan _delay;
            private readonly EventBus _bus;
            private readonly string _serverName;
            private TaskQueue _queue = new TaskQueue();
            private readonly int _streamCount;

            public DelayedMessageBus(string serverName, int streamCount, EventBus bus, IDependencyResolver resolver, TimeSpan delay)
                : base(resolver, new ScaleoutConfiguration() { InstanceName = serverName })
            {
                _serverName = serverName;
                _streamCount = streamCount;
                _bus = bus;
                _delay = delay;

                _bus.Received += (sender, e) =>
                {
                    _queue.Enqueue(state =>
                    {
                        var eventMessage = (EventMessage)state;
                        return Task.Run(() => OnReceived(eventMessage.StreamIndex, eventMessage.Id, eventMessage.Message));
                    },
                    e);
                };

                for (int i = 0; i < StreamCount; i++)
                {
                    Open(i);
                }
            }

            protected override int StreamCount
            {
                get
                {
                    return _streamCount;
                }
            }

            protected override Task Send(int streamIndex, IList<Message> messages)
            {
                _bus.Publish(streamIndex, new ScaleoutMessage(messages));

                return TaskAsyncHelper.Empty;
            }

            protected override void OnReceived(int streamIndex, ulong id, ScaleoutMessage message)
            {
                if (_delay != TimeSpan.Zero)
                {
                    Thread.Sleep(_delay);
                }

                base.OnReceived(streamIndex, id, message);
            }
        }

        public class CircularTraceListener : TraceListener
        {
            private readonly MessageStore<TraceMessage> _store;

            public CircularTraceListener(int size)
            {
                _store = new MessageStore<TraceMessage>((uint)size);
            }

            public override void Write(string message)
            {
                _store.Add(new TraceMessage(message));
            }

            public override void WriteLine(string message)
            {
                _store.Add(new TraceMessage(message) { HasNewLine = true });
            }

            public override void Flush()
            {
                var e = new StoreEnumerator<TraceMessage>(_store);

                var path = "log_" + Guid.NewGuid().ToString().Substring(0, 4) + ".log";
                using (var sw = new StreamWriter(path))
                {
                    while (e.MoveNext())
                    {
                        if (e.Current.HasNewLine)
                        {
                            sw.WriteLine(e.Current.Message);
                        }
                        else
                        {
                            sw.Write(e.Current.Message);
                        }
                    }
                }
                Console.WriteLine("File {0} written.", path);
            }

            private struct StoreEnumerator<T> : IEnumerator<T>, IEnumerator where T : class
            {
                private readonly WeakReference _storeReference;
                private MessageStoreResult<T> _result;
                private int _offset;
                private int _length;
                private ulong _nextId;

                public StoreEnumerator(MessageStore<T> store)
                    : this()
                {
                    _storeReference = new WeakReference(store);
                    var result = store.GetMessages(0, 100);
                    Initialize(result);
                }

                public T Current
                {
                    get
                    {
                        return _result.Messages.Array[_offset];
                    }
                }

                public void Dispose()
                {

                }

                object IEnumerator.Current
                {
                    get { return Current; }
                }

                public bool MoveNext()
                {
                    _offset++;

                    if (_offset < _length)
                    {
                        return true;
                    }

                    if (!_result.HasMoreData)
                    {
                        return false;
                    }

                    // If the store falls out of scope
                    var store = (MessageStore<T>)_storeReference.Target;

                    if (store == null)
                    {
                        return false;
                    }

                    // Get the next result
                    MessageStoreResult<T> result = store.GetMessages(_nextId, 100);
                    Initialize(result);

                    _offset++;

                    return _offset < _length;
                }

                public void Reset()
                {
                    throw new NotSupportedException();
                }

                private void Initialize(MessageStoreResult<T> result)
                {
                    _result = result;
                    _offset = _result.Messages.Offset - 1;
                    _length = _result.Messages.Offset + _result.Messages.Count;
                    _nextId = _result.FirstMessageId + (ulong)_result.Messages.Count;
                }
            }

        }

        public class TraceMessage
        {
            public TraceMessage(string message)
            {
                Message = message;
            }

            public string Message { get; private set; }
            public bool HasNewLine { get; set; }
        }
    }
}
