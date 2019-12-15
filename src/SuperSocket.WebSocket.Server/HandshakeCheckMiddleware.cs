using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace SuperSocket.WebSocket.Server
{
    class HandshakeCheckMiddleware : MiddlewareBase
    {
        private ConcurrentQueue<WebSocketSession> _openHandshakePendingQueue = new ConcurrentQueue<WebSocketSession>();

        private ConcurrentQueue<WebSocketSession> _closeHandshakePendingQueue = new ConcurrentQueue<WebSocketSession>();
        
        private Timer _checkingTimer;

        private readonly HandshakeOptions _options;

        public HandshakeCheckMiddleware(IOptions<HandshakeOptions> handshakeOptions)
        {
            var options = handshakeOptions.Value;

            if (options == null)
                options = new HandshakeOptions();

            _options = options;        
        }

        public override void Register(IServer server)
        {
            _checkingTimer = new Timer(HandshakePendingQueueCheckingCallback, null, _options.CheckingInterval * 1000, _options.CheckingInterval * 1000); // hardcode to 1 minute for now
        }

        public override void Shutdown(IServer server)
        {
            _checkingTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        public override ValueTask<bool> HandleSession(IAppSession session)
        {
            var websocketSession = session as WebSocketSession;
            _openHandshakePendingQueue.Enqueue(websocketSession);
            websocketSession.CloseHandshakeStarted += OnCloseHandshakeStarted;            
            return new ValueTask<bool>(true);
        }

        private void OnCloseHandshakeStarted(object sender, EventArgs e)
        {
            var session = sender as WebSocketSession;
            session.CloseHandshakeStarted -= OnCloseHandshakeStarted;
            _closeHandshakePendingQueue.Enqueue(session);
        }

        private void HandshakePendingQueueCheckingCallback(object state)
        {
            _checkingTimer.Change(Timeout.Infinite, Timeout.Infinite);

            while (true)
            {
                WebSocketSession session;

                if (!_openHandshakePendingQueue.TryPeek(out session))
                    break;

                if (session.Handshaked || session.State == SessionState.Closed)
                {
                    //Handshaked or not connected
                    _openHandshakePendingQueue.TryDequeue(out session);
                    continue;
                }

                if (DateTime.Now < session.StartTime.AddSeconds(_options.OpenHandshakeTimeOut))
                    break;

                //Timeout, dequeue and then close
                _openHandshakePendingQueue.TryDequeue(out session);
                session.CloseWithoutHandshake();
            }

            while (true)
            {
                WebSocketSession session;

                if (!_closeHandshakePendingQueue.TryPeek(out session))
                    break;

                if (session.State == SessionState.Closed)
                {
                    //the session has been closed
                    _closeHandshakePendingQueue.TryDequeue(out session);
                    continue;
                }

                if (DateTime.Now < session.CloseHandshakeStartTime.AddSeconds(_options.CloseHandshakeTimeOut))
                    break;

                //Timeout, dequeue and then close
                _closeHandshakePendingQueue.TryDequeue(out session);
                //Needn't send closing handshake again
                session.CloseWithoutHandshake();
            }

            _checkingTimer.Change(_options.CheckingInterval * 1000, _options.CheckingInterval * 1000);
        }        
    }
}