﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using BeetleX.Buffers;
using BeetleX.EventArgs;

namespace BeetleX
{
    class TcpServer : IServer

    {

        public TcpServer(NetConfig config)
        {
            Config = config;
            Name = "TCP-SERVER-" + Guid.NewGuid().ToString("N");
        }

        private LRUDetector mSessionDetector = new LRUDetector();

        private System.Diagnostics.Stopwatch mWatch = new System.Diagnostics.Stopwatch();

        private Socket mSocket;

        private Dispatchs.DispatchCenter<SocketAsyncEventArgsX> mReceiveDispatchCenter = null;

        private Dispatchs.DispatchCenter<ISession> mSendDispatchCenter = null;

        private long mVersion = 0;

        private bool mInitialized = false;

        private Queue<SocketAsyncEventArgs> mSEAES = new Queue<SocketAsyncEventArgs>();

        private Dispatchs.Dispatcher<Socket> mAcceptSockets = null;

        private Buffers.BufferPool mBufferPool;

        private Buffers.BufferPool mReceiveBufferPool;

        private Dictionary<long, ISession> mSessions;

        private long mReceivBytes;

        private long mSendQuantity;

        private long mReceiveQuantity;

        private long mSendBytes;

        public long ReceivBytes
        {
            get
            {
                return mReceivBytes;
            }
        }

        public long SendBytes
        {
            get
            {
                return mSendBytes;
            }
        }

        public long SendQuantity
        {
            get
            {
                return mSendQuantity;
            }
        }

        public long ReceiveQuantity
        {
            get
            {
                return mReceiveQuantity;
            }
        }

        public NetConfig Config
        {
            get;
            set;
        }

        public string Name
        {
            get;
            set;
        }

        public IPacket Packet
        {
            get;
            set;
        }

        public ServerStatus Status
        {
            get;
            set;
        }

        public long Version
        {
            get
            {
                return mVersion;
            }
        }

        public IBufferPool BufferPool
        {
            get
            {
                return mBufferPool;
            }
        }

        public IServerHandler Handler
        {
            get;
            set;
        }

        public int Count
        {
            get
            {
                if (mSessions == null)
                    return 0;
                return mSessions.Count;
            }
        }

        private void AddSession(ISession session)
        {
            lock (mSessions)
            {
                mSessions[session.ID] = session;
            }
            System.Threading.Interlocked.Increment(ref mVersion);
        }

        private void ClearSession()
        {
            ISession[] sessions = GetOnlines();
            foreach (ISession item in sessions)
                item.Dispose();
        }

        private void RemoveSession(ISession session)
        {
            lock (mSessions)
            {
                if (mSessions.ContainsKey(session.ID))
                {
                    mSessions.Remove(session.ID);
                }
            }
            System.Threading.Interlocked.Increment(ref mVersion);
        }

        #region server init start stop

        private void ToInitialize()
        {
            if (!mInitialized)
            {
                if (mAcceptSockets == null)
                {
                    mAcceptSockets = new Dispatchs.Dispatcher<Socket>(AcceptProcess);
                    mAcceptSockets.Start();
                }
                if (Config.ReceiveQueueEnabled)
                {
                    mReceiveDispatchCenter = new Dispatchs.DispatchCenter<SocketAsyncEventArgsX>(ProcessReceiveArgs, Config.ReceiveQueues);
                    mReceiveDispatchCenter.Start();
                }
                if (Config.SendQueueEnabled)
                {
                    mSendDispatchCenter = new Dispatchs.DispatchCenter<ISession>(SessionSendData, Config.SendQueues);
                    mSendDispatchCenter.Start();
                }
                mBufferPool = new BufferPool(Config.BufferSize, 1024, IO_Completed);
                mReceiveBufferPool = new BufferPool(Config.BufferSize, 1024 * 10, IO_Completed);
                mSessions = new Dictionary<long, ISession>(Config.MaxConnections * 2);
                mInitialized = true;
                mWatch.Restart();
                mSessionDetector.Timeout = OnSessionDetection;
                mSessionDetector.Server = this;

            }
        }

        public bool Open()
        {
            try
            {
                mSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                System.Net.IPAddress address = string.IsNullOrEmpty(Config.Host) ? System.Net.IPAddress.Any : System.Net.IPAddress.Parse(Config.Host);
                System.Net.IPEndPoint point = new System.Net.IPEndPoint(address, Config.Port);
                mSocket.Bind(point);
                mSocket.Listen(100);
                ToInitialize();
                Status = ServerStatus.Start;
                BeginAccept();
                Log(LogType.Info, null, "server start@{0}:{1}", Config.Host, Config.Port);
                return true;

            }
            catch (Exception e_)
            {
                Status = ServerStatus.StartError;
                Error(e_, null, "server start error!");
            }
            return false;
        }

        public void Resume()
        {
            Status = ServerStatus.Start;
        }

        public bool Pause()
        {
            Status = ServerStatus.Stop;
            return true;
        }

        #endregion

        #region session accept
        private void AcceptProcess(System.Net.Sockets.Socket e)
        {
            try
            {
                EventArgs.ConnectingEventArgs cea = new EventArgs.ConnectingEventArgs();
                cea.Server = this;
                cea.Socket = e;
                OnConnecting(cea);
                if (cea.Cancel)
                {
                    CloseSocket(e);
                }
                else
                {
                    TcpSession session = new TcpSession();
                    session.Initialization(this, null);
                    session.Socket = e;
                    session.LittleEndian = Config.LittleEndian;
                    session.RemoteEndPoint = e.RemoteEndPoint;
                    if (this.Packet != null)
                    {
                        session.Packet = this.Packet.Clone();
                        session.Packet.Completed = OnPacketDecodeCompleted;
                    }
                    if (Config.ReceiveQueueEnabled)
                    {
                        session.ReceiveDispatcher = mReceiveDispatchCenter.Next();
                    }
                    if (Config.SendQueueEnabled)
                    {
                        session.SendDispatcher = mSendDispatchCenter.Next();
                    }
                    EventArgs.ConnectedEventArgs cead = new EventArgs.ConnectedEventArgs();
                    cead.Server = this;
                    cead.Session = session;
                    AddSession(session);
                    BeginReceive(session);
                    OnConnected(cead);

                }
            }
            catch (Exception e_)
            {
                Error(e_, null, "accept socket process error");
            }

        }

        private void BeginAccept()
        {

            System.Threading.ThreadPool.QueueUserWorkItem(o =>
            {
                try
                {
                    while (true)
                    {
                        if (Status == ServerStatus.Stop)
                        {
                            System.Threading.Thread.Sleep(100);
                            continue;
                        }
                        if (Status == ServerStatus.Closed)
                        {
                            break;
                        }
                        Socket socket = mSocket.Accept();
                        mAcceptSockets.Enqueue(socket);
                    }
                }
                catch (Exception e_)
                {
                    Error(e_, null, "server accept error!");
                    Status = ServerStatus.AcceptError;
                }
            });
        }

        #endregion

        #region session send and receive
        private void SessionSendData(ISession session)
        {
            ((TcpSession)session).ProcessSendMessages();
        }

        private void SessionReceivePacket(ISession session)
        {
            TcpSession tcpsession = (TcpSession)session;

            try
            {
                if (Handler != null)
                {

                    if (!tcpsession.IsDisposed)
                        Handler.SessionPacketDecodeCompleted(this, tcpsession.GetDecodeCompletedArgs());
                }
            }
            catch (Exception e_)
            {
                Error(e_, null, "{0} session packet process message event error !", session.RemoteEndPoint);
            }
        }

        private void BeginReceive(ISession session)
        {
            if (session.IsDisposed)
                return;
            Buffers.Buffer buffer = (Buffers.Buffer)mReceiveBufferPool.Pop();
            try
            {

                buffer.AsyncFrom(session);
            }
            catch (Exception e_)
            {
                buffer.Free();
                Error(e_, session, "session receive data error!");
            }
        }

        private void IO_Completed(object sender, System.Net.Sockets.SocketAsyncEventArgs e)
        {
            SocketAsyncEventArgsX ex = (SocketAsyncEventArgsX)e;
            if (ex.IsReceive)
            {
                ReceiveCompleted(e);

            }
            else
            {
                SendCompleted(e);

            }
        }

        private void ProcessReceiveArgs(SocketAsyncEventArgs e)
        {
            SocketAsyncEventArgsX ex = (SocketAsyncEventArgsX)e;
            ISession session = ex.Session;
            if (this.Config.Statistical)
            {
                System.Threading.Interlocked.Increment(ref mReceiveQuantity);
                System.Threading.Interlocked.Add(ref mReceivBytes, e.BytesTransferred);
            }
            ex.BufferX.Postion = 0;
            ex.BufferX.SetLength(e.BytesTransferred);
            session.Receive(ex.BufferX);
            if (session.SocketProcessHandler != null)
                session.SocketProcessHandler.ReceiveCompleted(session, e);
        }

        private void ReceiveCompleted(SocketAsyncEventArgs e)
        {
            SocketAsyncEventArgsX ex = (SocketAsyncEventArgsX)e;
            ISession session = ex.Session;
            try
            {
                if (e.SocketError == System.Net.Sockets.SocketError.Success && e.BytesTransferred > 0)
                {
                    if (session.Server.Config.ReceiveQueueEnabled)
                    {
                        ((TcpSession)session).ReceiveDispatcher.Enqueue(ex);
                    }
                    else
                    {
                        ProcessReceiveArgs(e);
                    }
                    BeginReceive(session);

                }
                else
                {
                    session.Dispose();

                    ex.BufferX.Free();
                }

            }
            catch (Exception e_)
            {
                Error(e_, ex.Session, "receive data completed SocketError {0}!", e.SocketError);
            }

        }

        private void SendCompleted(SocketAsyncEventArgs e)
        {
            SocketAsyncEventArgsX ex = (SocketAsyncEventArgsX)e;
            Buffers.Buffer buffer = (Buffers.Buffer)ex.BufferX;
            ISession session = ex.Session;
            try
            {
                if (e.SocketError == SocketError.IOPending || e.SocketError == SocketError.Success)
                {
                    if (this.Config.Statistical)
                    {
                        System.Threading.Interlocked.Increment(ref mSendQuantity);
                        System.Threading.Interlocked.Add(ref mSendBytes, e.BytesTransferred);
                    }
                    if (e.BytesTransferred < e.Count)
                    {
                        buffer.Postion = (buffer.Postion + e.BytesTransferred);
                        buffer.SetLength(buffer.Length - e.BytesTransferred);
                        buffer.AsyncTo(session);
                    }
                    else
                    {
                        IBuffer nextbuf = buffer.Next;
                        buffer.Free();
                        if (nextbuf != null)
                        {
                            ((TcpSession)session).CommitBuffer(nextbuf);
                        }
                        else
                        {
                            ((TcpSession)session).SendCompleted();
                        }
                    }
                }
                else
                {
                    Buffers.Buffer.Free(buffer);
                    session.Dispose();
                }
                if (session.SocketProcessHandler != null)
                    session.SocketProcessHandler.ReceiveCompleted(session, e);
            }
            catch (Exception e_)
            {
                Error(e_, ex.Session, "send data completed SocketError {0}!", e.SocketError);
            }

        }
        #endregion

        #region server and session event

        private void OnSessionDetection(IList<IDetectorItem> items)
        {
            try
            {
                if (Handler != null)
                    Handler.SessionDetection(this, new EventArgs.SessionDetectionEventArgs { Server = this, Sesions = items });
            }
            catch (Exception e_)
            {
                Error(e_, null, "session detection process error");
            }
        }

        public void SessionReceive(SessionReceiveEventArgs e)
        {

            if (e.Session.Packet != null)
            {
                try
                {

                    e.Session.Packet.Decode(e.Session, e.Reader);
                }
                catch (Exception e_)
                {
                    Error(e_, e.Session, "{0} session  buffer decoding error! ", e.Session.RemoteEndPoint);
                    e.Session.Dispose();
                }
            }
            else
            {
                try
                {
                    if (Handler != null)
                        Handler.SessionReceive(e.Server, e);
                }
                catch (Exception e_)
                {
                    Error(e_, e.Session, "{0} session  buffer process error! ", e.Session.RemoteEndPoint);
                }
            }

        }

        private void OnPacketDecodeCompleted(object server, PacketDecodeCompletedEventArgs e)
        {
            try
            {
                if (Handler != null)
                {
                    Handler.SessionPacketDecodeCompleted(this, e);
                }
            }
            catch (Exception e_)
            {
                Error(e_, null, "{0} session packet  message process error !", e.Session.RemoteEndPoint);
            }
        }

        private void OnConnecting(ConnectingEventArgs e)
        {
            try
            {
                if (Handler != null)
                    Handler.Connecting(this, e);
            }
            catch (Exception e_)
            {
                Error(e_, null, "Server session connecting process error!");
            }
        }

        private void OnConnected(ConnectedEventArgs e)
        {
            try
            {
                if (Handler != null)
                    Handler.Connected(this, e);
            }
            catch (Exception e_)
            {
                Error(e_, e.Session, "Server session connected process error!");
            }
        }

        public void Log(LogType type, ISession session, string message)
        {
            try
            {
                if (Handler != null)
                    Handler.Log(this, new ServerLogEventArgs() { Message = message, Server = this, Type = type, Session = session });
            }
            catch (Exception e_)
            {
                Error(e_, session, "Server log process error!");
            }

        }

        public void Log(LogType type, ISession session, string message, params object[] parameters)
        {
            Log(type, session, string.Format(message, parameters));

        }

        public void Error(Exception error, ISession session, string message)
        {
            try
            {
                if (Handler != null)
                    Handler.Error(this, new ServerErrorEventArgs() { Message = message, Server = this, Error = error, Session = session });
            }
            catch
            {
            }
        }

        public void Error(Exception error, ISession session, string message, params object[] parameters)
        {
            Error(error, session, string.Format(message, parameters));
        }

        public void CloseSession(ISession session)
        {
            try
            {
                RemoveSession(session);
                if (Handler != null)
                    Handler.Disconnect(this, new SessionEventArgs { Server = this, Session = session });
            }
            catch (Exception e_)
            {
                Error(e_, session, "close session error!");
            }
            finally
            {
                CloseSocket(session.Socket);
            }
        }

        //释放Socket对象
        internal static void CloseSocket(Socket socket)
        {
            try
            {
                socket.Shutdown(SocketShutdown.Both);
            }
            catch
            {
            }
            try
            {
                socket.Dispose();

            }
            catch
            {

            }

        }
        #endregion

        public void Dispose()
        {
            ClearSession();
            mInitialized = false;

            Status = ServerStatus.Closed;
            CloseSocket(mSocket);
            if (mReceiveDispatchCenter != null)
                mReceiveDispatchCenter.Dispose();
            if (mAcceptSockets != null)
                mAcceptSockets.Dispose();
            mSessionDetector.Server = null;
            mSessionDetector.Dispose();

        }

        public bool Send(object data, ISession session)
        {
            if (data == null)
                return false;
            return session.Send(data);
        }

        public bool[] Send(object message, params ISession[] sessions)
        {
            return Send(message, new ArraySegment<ISession>(sessions, 0, sessions.Length));

        }

        private bool[] OnSend(object message, System.ArraySegment<ISession> sessions)
        {
            bool[] result = new bool[sessions.Count];
            for (int i = 0; i < sessions.Count; i++)
            {
                result[i] = Send(message, sessions.Array[sessions.Offset + i]);
            }
            return result;
        }

        public bool[] Send(object message, System.ArraySegment<ISession> sessions)
        {
            bool[] result = new bool[sessions.Count];
            if (message is byte[] || message is System.ArraySegment<byte> || message is IBuffer[] || message is IBuffer)
            {
                return OnSend(message, sessions);
            }
            else
            {
                if (this.Packet == null)
                {
                    Error(new BXException("server message formater is null!"), null, "server message formater is null!");
                }
                else
                {
                    if (Config.Combined > 0 && sessions.Count >= Config.Combined)
                    {
                        byte[] data = Packet.Encode(message, this);
                        return OnSend(data, sessions);
                    }
                    else
                    {
                        return OnSend(message, sessions);
                    }
                }
            }
            return result;
        }

        public long GetRunTime()
        {
            return mWatch.ElapsedMilliseconds;
        }

        public void DetectionSession(int timeout)
        {
            mSessionDetector.Detection(timeout);
        }

        public void UpdateSession(ISession session)
        {
            mSessionDetector.Update(session);
        }

        class OnlineSegment
        {

            public OnlineSegment()
            {
                Arrays = new ISession[0];
            }

            public ISession[] Arrays { get; set; }

            public long Version
            {
                get; set;
            }

        }

        private OnlineSegment mOnlines = new OnlineSegment();

        public ISession[] GetOnlines()
        {
            lock (mSessions)
            {
                if (mOnlines.Version != this.Version)
                {
                    mOnlines.Arrays = mSessions.Values.ToArray();
                    mOnlines.Version = this.Version;
                }

            }
            return mOnlines.Arrays;
        }

        private long mLastSend;

        private long mLastReceive;

        public override string ToString()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendFormat("{0} Listen {1}:{2}\r\n", Name, string.IsNullOrEmpty(this.Config.Host) ? "0.0.0.0" : this.Config.Host, this.Config.Port);
            sb.AppendFormat("Connections:{0}     Buffers:{1}\r\n",
                Count.ToString("###,###,##0").PadLeft(15),
                BufferPool.Count.ToString("###,###,##0").PadLeft(7));

            sb.AppendFormat("IO  Receive:{0}/s   Send:{1}/s\r\n",
            (ReceiveQuantity - mLastReceive).ToString("###,###,##0").PadLeft(15),
            (SendQuantity - mLastSend).ToString("###,###,##0").PadLeft(15));
            mLastReceive = ReceiveQuantity;
            mLastSend = SendQuantity;

            sb.AppendFormat("IO  Receive:{0}     Send:{1}\r\n",
                ReceiveQuantity.ToString("###,###,##0").PadLeft(15),
                SendQuantity.ToString("###,###,##0").PadLeft(15));


            sb.AppendFormat("BW  Receive:{0}KB   Send:{1}KB\r\n",
                (ReceivBytes / 1024).ToString("###,###,##0").PadLeft(15),
                (SendBytes / 1024).ToString("###,###,##0").PadLeft(15));
            sb.AppendLine("");
            return sb.ToString();
        }
    }
}
