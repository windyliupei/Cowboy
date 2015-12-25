﻿using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Cowboy.Buffer;
using Cowboy.Logging;

namespace Cowboy.Sockets
{
    public class AsyncTcpSocketServer
    {
        #region Fields

        private static readonly ILog _log = Logger.Get<AsyncTcpSocketServer>();
        private IBufferManager _bufferManager;
        private TcpListener _listener;
        private readonly ConcurrentDictionary<string, AsyncTcpSocketSession> _sessions = new ConcurrentDictionary<string, AsyncTcpSocketSession>();
        private readonly object _opsLock = new object();
        private readonly AsyncTcpSocketServerConfiguration _configuration;
        private readonly IAsyncTcpSocketServerMessageDispatcher _dispatcher;

        #endregion

        #region Constructors

        public AsyncTcpSocketServer(int listenedPort, IAsyncTcpSocketServerMessageDispatcher dispatcher, AsyncTcpSocketServerConfiguration configuration = null)
            : this(IPAddress.Any, listenedPort, dispatcher, configuration)
        {
        }

        public AsyncTcpSocketServer(IPAddress listenedAddress, int listenedPort, IAsyncTcpSocketServerMessageDispatcher dispatcher, AsyncTcpSocketServerConfiguration configuration = null)
            : this(new IPEndPoint(listenedAddress, listenedPort), dispatcher, configuration)
        {
        }

        public AsyncTcpSocketServer(IPEndPoint listenedEndPoint, IAsyncTcpSocketServerMessageDispatcher dispatcher, AsyncTcpSocketServerConfiguration configuration = null)
        {
            if (listenedEndPoint == null)
                throw new ArgumentNullException("listenedEndPoint");
            if (dispatcher == null)
                throw new ArgumentNullException("dispatcher");

            this.ListenedEndPoint = listenedEndPoint;
            _dispatcher = dispatcher;
            _configuration = configuration ?? new AsyncTcpSocketServerConfiguration();

            Initialize();
        }

        private void Initialize()
        {
            _bufferManager = new GrowingByteBufferManager(_configuration.InitialBufferAllocationCount, _configuration.ReceiveBufferSize);
        }

        #endregion

        #region Properties

        public IPEndPoint ListenedEndPoint { get; private set; }
        public bool Active { get; private set; }
        public int SessionCount { get { return _sessions.Count; } }

        #endregion

        #region Server

        public void Start()
        {
            lock (_opsLock)
            {
                if (Active)
                    return;

                try
                {
                    _listener = new TcpListener(this.ListenedEndPoint);
                    _listener.AllowNatTraversal(_configuration.AllowNatTraversal);
                    _listener.ExclusiveAddressUse = _configuration.ExclusiveAddressUse;

                    _listener.Start(_configuration.PendingConnectionBacklog);
                    Active = true;

                    Task.Run(async () =>
                    {
                        await Accept();
                    })
                    .Forget();
                }
                catch (Exception ex)
                {
                    if (ex is SocketException)
                    {
                        _log.Error(ex.Message, ex);
                    }
                    else throw;
                }
            }
        }

        public void Stop()
        {
            lock (_opsLock)
            {
                if (!Active)
                    return;

                try
                {
                    Active = false;
                    _listener.Stop();
                    _listener = null;
                }
                catch (Exception ex)
                {
                    if (ex is SocketException)
                    {
                        _log.Error(ex.Message, ex);
                    }
                    else throw;
                }
            }
        }

        public bool Pending()
        {
            lock (_opsLock)
            {
                if (!Active)
                    throw new InvalidOperationException("The TCP server is not active.");

                // determine if there are pending connection requests.
                return _listener.Pending();
            }
        }

        private async Task Accept()
        {
            while (Active)
            {
                var tcpClient = await _listener.AcceptTcpClientAsync();
                var session = new AsyncTcpSocketSession(tcpClient, _configuration, _bufferManager, _dispatcher, this);
                Task.Run(async () =>
                {
                    await Process(session);
                })
                .Forget();
            }
        }

        private async Task Process(AsyncTcpSocketSession session)
        {
            string sessionKey = session.RemoteEndPoint.ToString();
            if (_sessions.TryAdd(sessionKey, session))
            {
                try
                {
                    await session.Start();
                }
                finally
                {
                    AsyncTcpSocketSession throwAway;
                    _sessions.TryRemove(sessionKey, out throwAway);
                }
            }
        }

        #endregion

        #region Send

        public async Task SendTo(string sessionKey, byte[] data)
        {
            await SendTo(sessionKey, data, 0, data.Length);
        }

        public async Task SendTo(string sessionKey, byte[] data, int offset, int count)
        {
            AsyncTcpSocketSession sessionFound;
            if (_sessions.TryGetValue(sessionKey, out sessionFound))
            {
                await sessionFound.Send(data, offset, count);
            }
        }

        public async Task SendTo(AsyncTcpSocketSession session, byte[] data)
        {
            await SendTo(session, data, 0, data.Length);
        }

        public async Task SendTo(AsyncTcpSocketSession session, byte[] data, int offset, int count)
        {
            AsyncTcpSocketSession sessionFound;
            if (_sessions.TryGetValue(session.SessionKey, out sessionFound))
            {
                await sessionFound.Send(data, offset, count);
            }
        }

        public async Task Broadcast(byte[] data)
        {
            await Broadcast(data, 0, data.Length);
        }

        public async Task Broadcast(byte[] data, int offset, int count)
        {
            foreach (var session in _sessions.Values)
            {
                await session.Send(data, offset, count);
            }
        }

        #endregion
    }
}