using System;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;

namespace GraphQL.Client.Http.Websocket {
	internal class GraphQLHttpWebSocket : IDisposable {
		private readonly Uri webSocketUri;
		private readonly GraphQLHttpClientOptions _options;
		private readonly ArraySegment<byte> buffer;
		private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

		private Subject<WebsocketResponseWrapper> _responseSubject;
		private readonly Subject<GraphQLWebSocketRequest> _requestSubject = new Subject<GraphQLWebSocketRequest>();
		private readonly Subject<Exception> _exceptionSubject = new Subject<Exception>();
		private IDisposable _requestSubscription;

		public WebSocketState WebSocketState => clientWebSocket?.State ?? WebSocketState.None;

		private WebSocket clientWebSocket = null;
		private int _connectionAttempt = 0;

		public GraphQLHttpWebSocket(Uri webSocketUri, GraphQLHttpClientOptions options) {
			this.webSocketUri = webSocketUri;
			_options = options;
			buffer = new ArraySegment<byte>(new byte[8192]);
			_responseStream = _createResponseStream();

			_requestSubscription = _requestSubject.Select(request => Observable.FromAsync(() => _sendWebSocketRequest(request))).Concat().Subscribe();
		}

		public IObservable<Exception> ReceiveErrors => _exceptionSubject.AsObservable();

		public IObservable<WebsocketResponseWrapper> ResponseStream => _responseStream;
		public readonly IObservable<WebsocketResponseWrapper> _responseStream;

		public Task SendWebSocketRequest(GraphQLWebSocketRequest request) {
			_requestSubject.OnNext(request);
			return request.SendTask();
		}

		private async Task _sendWebSocketRequest(GraphQLWebSocketRequest request) {
			try {
				if (_cancellationTokenSource.Token.IsCancellationRequested) {
					request.SendCanceled();
					return;
				}

				await InitializeWebSocket().ConfigureAwait(false);
				var requestBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request, _options.JsonSerializerOptions);
				await this.clientWebSocket.SendAsync(
					new ArraySegment<byte>(requestBytes),
					WebSocketMessageType.Text,
					true,
					_cancellationTokenSource.Token).ConfigureAwait(false);
				request.SendCompleted();
			}
			catch (Exception e) {
				request.SendFailed(e);
			}
		}

		public Task InitializeWebSocketTask { get; private set; } = Task.CompletedTask;

		private readonly object _initializeLock = new object();

		#region Private Methods

		private Task _backOff() {
			_connectionAttempt++;

			if (_connectionAttempt == 1) return Task.CompletedTask;

			var delay = _options.BackOffStrategy(_connectionAttempt - 1);
			Debug.WriteLine($"connection attempt #{_connectionAttempt}, backing off for {delay.TotalSeconds} s");
			return Task.Delay(delay);
		}

		public Task InitializeWebSocket() {
			// do not attempt to initialize if cancellation is requested
			if (_disposed != null)
				throw new OperationCanceledException();

			lock (_initializeLock) {
				// if an initialization task is already running, return that
				if (InitializeWebSocketTask != null &&
				   !InitializeWebSocketTask.IsFaulted &&
				   !InitializeWebSocketTask.IsCompleted)
					return InitializeWebSocketTask;

				// if the websocket is open, return a completed task
				if (clientWebSocket != null && clientWebSocket.State == WebSocketState.Open)
					return Task.CompletedTask;

				// else (re-)create websocket and connect
				//_responseStreamConnection?.Dispose();
				clientWebSocket?.Dispose();

				// fix websocket not supported on win 7 using
				// https://github.com/PingmanTools/System.Net.WebSockets.Client.Managed
				clientWebSocket = SystemClientWebSocket.CreateClientWebSocket();
				switch (clientWebSocket) {
					case ClientWebSocket nativeWebSocket:
						nativeWebSocket.Options.AddSubProtocol("graphql-ws");
						break;
					case System.Net.WebSockets.Managed.ClientWebSocket managedWebSocket:
						managedWebSocket.Options.AddSubProtocol("graphql-ws");
						break;
					default:
						throw new NotSupportedException($"unknown websocket type {clientWebSocket.GetType().Name}");
				}

				return InitializeWebSocketTask = _connectAsync(_cancellationTokenSource.Token);
			}
		}

		private IObservable<WebsocketResponseWrapper> _createResponseStream() {
			return Observable.Create<WebsocketResponseWrapper>(_createResultStream)
				// complete sequence on OperationCanceledException, this is triggered by the cancellation token on disposal
				.Catch<WebsocketResponseWrapper, OperationCanceledException>(exception =>
					Observable.Empty<WebsocketResponseWrapper>());
		}

		private async Task<IDisposable> _createResultStream(IObserver<WebsocketResponseWrapper> observer, CancellationToken token) {
			if (_responseSubject == null || _responseSubject.IsDisposed) {
				_responseSubject = new Subject<WebsocketResponseWrapper>();
				var observable = await _getReceiveResultStream().ConfigureAwait(false);
				observable.Subscribe(_responseSubject);

				_responseSubject.Subscribe(_ => { }, ex => {
					_exceptionSubject.OnNext(ex);
					_responseSubject?.Dispose();
					_responseSubject = null;
				},
				() => {
					_responseSubject?.Dispose();
					_responseSubject = null;
				});
			}

			return new CompositeDisposable
			(
				_responseSubject.Subscribe(observer),
				Disposable.Create(() => {
					Debug.WriteLine("response stream disposed");
				})
			);
		}

		private async Task<IObservable<WebsocketResponseWrapper>> _getReceiveResultStream() {
			await InitializeWebSocket().ConfigureAwait(false);
			return Observable.Defer(() => _getReceiveTask().ToObservable()).Repeat();
		}

		private async Task _connectAsync(CancellationToken token) {
			try {
				await _backOff().ConfigureAwait(false);
				Debug.WriteLine($"opening websocket {clientWebSocket.GetHashCode()}");
				await clientWebSocket.ConnectAsync(webSocketUri, token).ConfigureAwait(false);
				Debug.WriteLine($"connection established on websocket {clientWebSocket.GetHashCode()}");
				_connectionAttempt = 1;
			}
			catch (Exception e) {
				_exceptionSubject.OnNext(e);
				throw;
			}
		}


		private Task<WebsocketResponseWrapper> _receiveAsyncTask = null;
		private readonly object _receiveTaskLocker = new object();
		/// <summary>
		/// wrapper method to pick up the existing request task if already running
		/// </summary>
		/// <returns></returns>
		private Task<WebsocketResponseWrapper> _getReceiveTask() {
			lock (_receiveTaskLocker) {
				if (_receiveAsyncTask == null ||
					_receiveAsyncTask.IsFaulted ||
					_receiveAsyncTask.IsCompleted)
					_receiveAsyncTask = _receiveResultAsync();
			}

			return _receiveAsyncTask;
		}

		private async Task<WebsocketResponseWrapper> _receiveResultAsync() {
			try {
				Debug.WriteLine($"receiving data on websocket {clientWebSocket.GetHashCode()} ...");

				using (var ms = new MemoryStream()) {
					WebSocketReceiveResult webSocketReceiveResult = null;
					do {
						_cancellationTokenSource.Token.ThrowIfCancellationRequested();
						webSocketReceiveResult = await clientWebSocket.ReceiveAsync(buffer, CancellationToken.None);
						ms.Write(buffer.Array, buffer.Offset, webSocketReceiveResult.Count);
					}
					while (!webSocketReceiveResult.EndOfMessage);

					_cancellationTokenSource.Token.ThrowIfCancellationRequested();
					ms.Seek(0, SeekOrigin.Begin);

					if (webSocketReceiveResult.MessageType == WebSocketMessageType.Text) {
						var bytes = ms.ToArray();
						var response =
							System.Text.Json.JsonSerializer.Deserialize<WebsocketResponseWrapper>(bytes,
								_options.JsonSerializerOptions);
						response.MessageBytes = bytes;

						return response;
					}
					else {
						throw new NotSupportedException("binary websocket messages are not supported");
					}
				}
			}
			catch (Exception e) {
				Debug.WriteLine($"exception thrown while receiving websocket data: {e}");
				throw;
			}
		}

		private async Task _closeAsync(CancellationToken cancellationToken = default) {
			if (clientWebSocket == null)
				return;

			// don't attempt to close the websocket if it is in a failed state
			if (this.clientWebSocket.State != WebSocketState.Open &&
				this.clientWebSocket.State != WebSocketState.CloseReceived &&
				this.clientWebSocket.State != WebSocketState.CloseSent) {
				Debug.WriteLine($"websocket {clientWebSocket.GetHashCode()} state = {this.clientWebSocket.State}");
				return;
			}

			Debug.WriteLine($"closing websocket {clientWebSocket.GetHashCode()}");
			await this.clientWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cancellationToken).ConfigureAwait(false);
		}

		#endregion

		#region IDisposable

		private Task _disposed;
		private object _disposedLocker = new object();
		public void Dispose() {
			// Async disposal as recommended by Stephen Cleary (https://blog.stephencleary.com/2013/03/async-oop-6-disposal.html)
			lock (_disposedLocker) {
				if (_disposed == null) _disposed = DisposeAsync();
			}
		}

		private async Task DisposeAsync() {
			Debug.WriteLine($"disposing websocket {clientWebSocket.GetHashCode()}...");
			if (!_cancellationTokenSource.IsCancellationRequested)
				_cancellationTokenSource.Cancel();
			await _closeAsync().ConfigureAwait(false);
			clientWebSocket?.Dispose();
			_cancellationTokenSource.Dispose();
			Debug.WriteLine($"websocket {clientWebSocket.GetHashCode()} disposed");
		}
		#endregion
	}
}
