using com.clusterrr.SemaphoreLock;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using com.clusterrr.TuyaNet.Extensions;
using com.clusterrr.TuyaNet.Log;
using System.Runtime.InteropServices.ComTypes;

namespace com.clusterrr.TuyaNet
{
	/// <summary>
	/// Connection with Tuya device.
	/// </summary>
	public class TuyaDevice : IDisposable
	{
		private readonly TuyaParser parser;
		ILog _log;
		/// <summary>
		/// Creates a new instance of the TuyaDevice class.
		/// </summary>
		/// <param name="ip">IP address of device.</param>
		/// <param name="localKey">Local key of device (obtained via API).</param>
		/// <param name="deviceId">Device ID.</param>
		/// <param name="protocolVersion">Protocol version.</param>
		/// <param name="port">TCP port of device.</param>
		/// <param name="receiveTimeout">Receive timeout (msec).</param>
		public TuyaDevice(
				string ip, string localKey, string deviceId, ILog log = null ,
				TuyaProtocolVersion protocolVersion = TuyaProtocolVersion.V33,
				int port = 6668,
				int receiveTimeout = 1500)
		{
			_log= log;
			IP = ip;
			LocalKey = localKey;
			this.accessId = null;
			this.apiSecret = null;
			DeviceId = deviceId;
			ProtocolVersion = protocolVersion;
			Port = port;
			ReceiveTimeout = receiveTimeout;
			parser = new TuyaParser(localKey, protocolVersion);
		}

		/// <summary>
		/// Creates a new instance of the TuyaDevice class.
		/// </summary>
		/// <param name="ip">IP address of device.</param>
		/// <param name="region">Region to access Cloud API.</param>
		/// <param name="accessId">Access ID to access Cloud API.</param>
		/// <param name="apiSecret">API secret to access Cloud API.</param>
		/// <param name="deviceId">Device ID.</param>
		/// <param name="protocolVersion">Protocol version.</param>
		/// <param name="port">TCP port of device.</param>
		/// <param name="receiveTimeout">Receive timeout (msec).</param> 
		public TuyaDevice(string ip, TuyaApi.Region region, string accessId, string apiSecret, string deviceId,
			 ILog log = null ,TuyaProtocolVersion protocolVersion = TuyaProtocolVersion.V33, int port = 6668, int receiveTimeout = 250)
		{
			_log = log;
			IP = ip;
			LocalKey = null;
			this.region = region;
			this.accessId = accessId;
			this.apiSecret = apiSecret;
			DeviceId = deviceId;
			ProtocolVersion = protocolVersion;
			Port = port;
			ReceiveTimeout = receiveTimeout;
		}

		/// <summary>
		/// IP address of device.
		/// </summary>
		public string IP { get; private set; }
		/// <summary>
		/// Local key of device.
		/// </summary>
		public string LocalKey { get; set; }
		/// <summary>
		/// Device ID.
		/// </summary>
		public string DeviceId { get; private set; }
		/// <summary>
		/// TCP port of device.
		/// </summary>
		public int Port { get; private set; } = 6668;
		/// <summary>
		/// Protocol version.
		/// </summary>
		public TuyaProtocolVersion ProtocolVersion { get; set; }
		/// <summary>
		/// Connection timeout.
		/// </summary>
		public int ConnectionTimeout { get; set; } = 500;
		/// <summary>
		/// Receive timeout.
		/// </summary>
		public int ReceiveTimeout { get; set; }
		/// <summary>
		/// Network error retry interval (msec)
		/// </summary>
		public int NetworkErrorRetriesInterval { get; set; } = 100;
		/// <summary>
		/// Empty responce retry interval (msec)
		/// </summary>
		public int NullRetriesInterval { get; set; } = 0;
		/// <summary>
		/// Permanent connection (connect and stay connected).
		/// </summary>
		public bool PermanentConnection { get; set; } = false;

		private TcpClient client = null;
		private TuyaApi.Region region;
		private string accessId;
		private string apiSecret;
		private SemaphoreSlim sem = new SemaphoreSlim(1);
		CancellationTokenSource _cancelIdleRead;
		private SemaphoreSlim _sendLock = new SemaphoreSlim(1);

		/// <summary>
		/// Fills JSON string with base fields required by most commands.
		/// </summary>
		/// <param name="json">JSON string</param>
		/// <param name="addGwId">Add "gwId" field with device ID.</param>
		/// <param name="addDevId">Add "devId" field with device ID.</param>
		/// <param name="addUid">Add "uid" field with device ID.</param>
		/// <param name="addTime">Add "time" field with current timestamp.</param>
		/// <returns>JSON string with added fields.</returns>
		public string FillJson(string json, bool addGwId = true, bool addDevId = true, bool addUid = true, bool addTime = true)
		{
			if (string.IsNullOrEmpty(json))
				json = "{}";
			var root = JObject.Parse(json);
			if ((addGwId || addDevId || addUid) && string.IsNullOrWhiteSpace(DeviceId))
				throw new ArgumentNullException("deviceId", "Device ID can't be null.");
			if (addTime && !root.ContainsKey("t"))
				root.AddFirst(new JProperty("t", (DateTime.Now - new DateTime(1970, 1, 1)).TotalSeconds.ToString("0")));
			if (addUid && !root.ContainsKey("uid"))
				root.AddFirst(new JProperty("uid", DeviceId));
			if (addDevId && !root.ContainsKey("devId"))
				root.AddFirst(new JProperty("devId", DeviceId));
			if (addGwId && !root.ContainsKey("gwId"))
				root.AddFirst(new JProperty("gwId", DeviceId));
			return root.ToString();
		}

		/// <summary>
		/// Creates encoded and encrypted payload data from JSON string.
		/// </summary>
		/// <param name="command">Tuya command ID.</param>
		/// <param name="json">String with JSON to send.</param>
		/// <returns>Raw data.</returns>
		public byte[] EncodeRequest(TuyaCommand command, string json)
		{
			if (string.IsNullOrEmpty(LocalKey)) throw new ArgumentException("LocalKey is not specified", "LocalKey");
			return parser.EncodeRequest(command, json, Encoding.UTF8.GetBytes(LocalKey), ProtocolVersion);
		}

		/// <summary>
		/// Parses and decrypts payload data from received bytes.
		/// </summary>
		/// <param name="data">Raw data to parse and decrypt.</param>
		/// <returns>Instance of TuyaLocalResponse.</returns>
		public TuyaLocalResponse DecodeResponse(byte[] data)
		{
			if (string.IsNullOrEmpty(LocalKey)) throw new ArgumentException("LocalKey is not specified", "LocalKey");
			return parser.DecodeResponse(data);
		}

		/// <summary>
		/// Sends JSON string to device and reads response.
		/// </summary>
		/// <param name="command">Tuya command ID.</param>
		/// <param name="json">JSON string.</param>
		/// <param name="retries">Number of retries in case of network error (default - 2).</param>
		/// <param name="nullRetries">Number of retries in case of empty answer (default - 1).</param>
		/// <param name="overrideRecvTimeout">Override receive timeout (default - ReceiveTimeout property).</param>
		/// <param name="cancellationToken">Cancellation token.</param>
		/// <returns>Parsed and decrypred received data as instance of TuyaLocalResponse.</returns>
		public async Task<TuyaLocalResponse> SendAsync(TuyaCommand command, string json, int retries = 2, int nullRetries = 1, int? overrideRecvTimeout = null, CancellationToken cancellationToken = default)
				=> DecodeResponse(await SendAsync(command, EncodeRequest(command, json), retries, nullRetries, overrideRecvTimeout, cancellationToken));


		public bool IsConnected()
		{
			return client?.Connected == true;
		}

		public async Task SecureConnectAsync(CancellationToken cancellationToken = default)
		{
			_cancelIdleRead?.Cancel();
			_cancelIdleRead = null;
			await SecureConnectAsyncInternal(cancellationToken);
			if (PermanentConnection) StartReadLoop();
			return;
		}

		async Task SecureConnectAsyncInternal(CancellationToken cancellationToken)
		{
			if (client == null)
				client = new TcpClient();
			if (!client.ConnectAsync(IP, Port).Wait(ConnectionTimeout))
			{
				_log?.Error("TUYADEV", $"Connection to {IP}:{Port} timeout");
				throw new IOException($"Connection to {IP}:{Port} timeout");
			}
			_log?.Debug(6, "TUYADEV", $"Connection to {IP}:{Port} opened");
			

			if (ProtocolVersion == TuyaProtocolVersion.V34)
			{
				var random = new Random();
				var tmpLocalKey = new byte[16];
				random.NextBytes(tmpLocalKey);
				var key = Encoding.UTF8.GetBytes(LocalKey);
				var request = parser.EncodeRequest34(
						TuyaCommand.SESS_KEY_NEG_START,
						tmpLocalKey,
						key
						);

				var networkClientStream = client.GetStream();
				await networkClientStream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
				var receivedBytes = await ReceiveAsync(networkClientStream, 1, null, cancellationToken);
				var response = parser.DecodeResponse(receivedBytes);
				if ((int)response.Command == (int)TuyaCommand.SESS_KEY_NEG_RES)
				{
					var remoteKeySize = 16;
					var hashSize = 32;
					var tmpRemoteKey = response.Payload.Take(remoteKeySize).ToArray();
					var hash = parser.GetHashSha256(tmpLocalKey);
					var expectedHash = response.Payload.Skip(remoteKeySize).Take(hashSize).ToArray();
					if (!expectedHash.SequenceEqual(hash))
						throw new Exception("HMAC mismatch");

					var requestFinish = parser.EncodeRequest34(
							TuyaCommand.SESS_KEY_NEG_FINISH,
							parser.GetHashSha256(tmpRemoteKey),
							key
					);
					await networkClientStream.WriteAsync(requestFinish, cancellationToken).ConfigureAwait(false);

					var sessionKey = new byte[tmpLocalKey.Length];
					for (var i = 0; i < tmpLocalKey.Length; i++)
					{
						var value = (byte)(tmpLocalKey[i] ^ tmpRemoteKey[i]);
						sessionKey[i] = value;
					}
					var encryptedSessionKey = parser.Encrypt34(sessionKey);
					parser.SetupSessionKey(encryptedSessionKey);
				}
			}

		}

		void StartReadLoop()
		{
			_cancelIdleRead = new CancellationTokenSource();

			//start reading idles
			_ = Task.Run(async () =>
			{
				return;
				//start reading idles
				while (!_cancelIdleRead.IsCancellationRequested)
				{
					var inter = Math.Max(1000, NetworkErrorRetriesInterval);
					using (await _sendLock.WaitDisposableAsync(inter, _cancelIdleRead.Token))
					{
						try
						{
							if (IsConnected())
							{
								_log?.Debug(8, "TUYADEV", $"SendAsync from periodic loop");
								_ = await SendAsyncInternal(TuyaCommand.UDP, (byte[])null, 1, 0, 100, _cancelIdleRead.Token, true);
							}
						}
						catch { }
					}
					await Task.Delay(inter, _cancelIdleRead.Token);
				}
			});

		}

		void CloseSocket()
		{
			try
			{
				var c = client;
				client = null;
				c?.Close();
				c?.Dispose();
				
			}
			catch { }
		}

		public void Close()
		{
			_cancelIdleRead?.Cancel();
			_cancelIdleRead?.Dispose();
			_cancelIdleRead = null;
			CloseSocket();
			_log?.Debug(6,"TUYADEV", $"Device closed");
		}
		/// <summary>
		/// Sends raw data over to device and read response.
		/// </summary>
		/// <param name="command">Command type</param>
		/// <param name="data">Raw data to send.</param>
		/// <param name="retries">Number of retries in case of network error (default - 2).</param>
		/// <param name="nullRetries">Number of retries in case of empty answer (default - 1).</param>
		/// <param name="overrideRecvTimeout">Override receive timeout (default - ReceiveTimeout property).</param>
		/// <param name="cancellationToken">Cancellation token.</param>
		/// <returns>Received data (raw).</returns>
		public async Task<byte[]> SendAsync(TuyaCommand command, byte[] data, int retries = 2, int nullRetries = 1, int? overrideRecvTimeout = null, 
			CancellationToken cancellationToken = default)
		{
			using (await _sendLock.WaitDisposableAsync(cancellationToken))
			{
				if (!IsConnected())
				{
					await SecureConnectAsyncInternal(_cancelIdleRead.Token);
				}
				_log?.Debug(8, "TUYADEV", $"SendAsync from external call: lenght={data.Length}");
				return await SendAsyncInternal(command, data, retries, nullRetries, overrideRecvTimeout, cancellationToken, false);
			}
		}

		async Task<byte[]> ClearReadExisting(NetworkStream networkClientStream, CancellationToken cancellationToken)
		{
			try
			{
				if (networkClientStream == null) return null;
				byte[] buffer = new byte[256];
				MemoryStream stream = new MemoryStream();
				while (networkClientStream.DataAvailable)
				{
					var len = await networkClientStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
					if (len>0)
					{
						stream.Write(buffer, 0, len);
					}
				}
				return stream.ToArray();
			}
			catch { return null; }
		}


		async Task<byte[]> SendAsyncInternal(TuyaCommand command, byte[] data, int retries , int nullRetries , 
			int? overrideRecvTimeout,
			CancellationToken cancellationToken, bool ignoreCanceledOrTimeout)
		{
			Exception lastException = null;

			var networkClientStream = client?.GetStream();
			if (networkClientStream == null)
			{
				throw new Exception("Need invoke ConnectAsync before sendig.");
			}

			//Existing Empty data
			_ = ClearReadExisting(networkClientStream, cancellationToken);


			while (retries-- > 0)
			{
				if (client?.Connected == false)
				{
					CloseSocket();
				}
				try
				{
					using (await sem.WaitDisposableAsync(cancellationToken))
					{


						if (data != null)
						{
							_log?.Debug(9, "TUYADEV", $"Sending: lenght={data.Length}, data={BitConverter.ToString(data)}");
							await networkClientStream.WriteAsync(data, 0, data.Length, cancellationToken).ConfigureAwait(false);
						}
						byte[] response = null;
						while (response == null)
						{
							var responseRaw = await ReceiveAsync(networkClientStream, nullRetries, overrideRecvTimeout, cancellationToken);



							var responseParsed = parser.DecodeResponse(responseRaw);
							//Console.WriteLine($"Command: {responseParsed.Command.GetNames()}");
							//Console.WriteLine($"Json: {responseParsed.Json}");
							//Console.WriteLine($"Size payload: {responseParsed.Payload?.Length}");

							_log?.Debug(8, "TUYADEV", $"Received command: {responseParsed.Command.GetNames()}");
							_log?.Debug(8, "TUYADEV", $"Received json: {responseParsed.Json}");

							//for protocol v3.4 need wait response by command
							if (responseParsed.Command == command ||
									ProtocolVersion != TuyaProtocolVersion.V34)
								response = responseRaw;
						}
						return response;
					}
				}
				catch (Exception ex) when (ex is IOException or TimeoutException or SocketException)
				{
					// sockets sometimes drop the connection unexpectedly, so let's 
					// retry at least once
					if (!ignoreCanceledOrTimeout || (ignoreCanceledOrTimeout && ex is not TimeoutException))
					{
						lastException = ex;
					}
				}
				catch (Exception ex)
				{
					if (!ignoreCanceledOrTimeout || (ignoreCanceledOrTimeout && ex is not OperationCanceledException))
					{
						_log?.Error("TUYADEV", ex.Message);
						throw ex;
					}
				}
				finally
				{

					if (!IsConnected())
					{
						CloseSocket();
						throw lastException ?? new Exception("Not connected");
					}
					if (!PermanentConnection) CloseSocket();
					if (lastException != null)
					{
						//CloseSocket();
						throw lastException;
					}
				}
				await Task.Delay(NetworkErrorRetriesInterval, cancellationToken);
			}
			throw lastException;
		}


		private async Task<byte[]> ReceiveAsync(NetworkStream stream, int nullRetries = 1, int? overrideRecvTimeout = null, CancellationToken cancellationToken = default)
		{
			var isEnding = false;
			byte[] result;
			byte[] buffer = new byte[1024];
			using (var ms = new MemoryStream())
			{
				int length = buffer.Length;
				while (!isEnding /*&& !stream.DataAvailable*/)
				{
					var timeoutCancellationTokenSource = new CancellationTokenSource();
					var readTask = stream.ReadAsync(buffer, 0, length, cancellationToken: cancellationToken);
					var timeoutTask = Task.Delay(overrideRecvTimeout ?? ReceiveTimeout, cancellationToken: timeoutCancellationTokenSource.Token);
					var t = await Task.WhenAny(readTask, timeoutTask).ConfigureAwait(false);
					timeoutCancellationTokenSource.Cancel();
					int bytes = 0;
					if (t == timeoutTask)
					{
						if (stream.DataAvailable)
						{
							bytes = await stream.ReadAsync(buffer, 0, length, cancellationToken);
						}
						else
							throw new TimeoutException();
					}
					else if (t == readTask)
					{
						bytes = await readTask.ConfigureAwait(false);
					}
					_log?.Debug(9, "TUYADEV", $"Received: lenght={bytes}, data={BitConverter.ToString(buffer?.Take(bytes)?.ToArray())}");
					ms.Write(buffer, 0, bytes);
					var receivedArray = ms.ToArray();
					if (receivedArray.Length > 4)
					{
						var packetEnding = receivedArray.Skip(receivedArray.Length - 4).Take(4).ToArray();
						isEnding = TuyaParser.SUFFIX.SequenceEqual(packetEnding);
					}
					else
					{
						isEnding = true;
					}
				}
				result = ms.ToArray();
			}
			if ((result.Length <= 28) && (nullRetries > 0)) // empty response
			{
				await Task.Delay(NullRetriesInterval, cancellationToken);
				result = await ReceiveAsync(stream, nullRetries - 1, overrideRecvTimeout: overrideRecvTimeout, cancellationToken);
				_log?.Debug(9, "TUYADEV", $"Received: lenght={result.Length}, data={BitConverter.ToString(result)}");
			}
			return result;
		}
		/*private async Task<byte[]> ReceiveAsync(NetworkStream stream, int nullRetries = 1, int? overrideRecvTimeout = null, CancellationToken cancellationToken = default)
		{
			var isEnding = false;
			byte[] result;
			byte[] buffer = new byte[1024];
			using (var ms = new MemoryStream())
			{
				int length = buffer.Length;
				while (!isEnding )// && !stream.DataAvailable
				{
					int bytes=0;
					try
					{
						try
						{
							// Create a CancellationTokenSource
							CancellationTokenSource cts = new CancellationTokenSource();

							// Start reading asynchronously
							Task<int> readTask = stream.ReadAsync(buffer, 0, buffer.Length);

							// Simulate a cancellation after a certain delay (e.g., 5000 milliseconds)
							Task.Delay(overrideRecvTimeout ?? ReceiveTimeout).ContinueWith(_ => cts.Cancel());

							try
							{
								// Wait for either the read to complete or the cancellation token to be triggered
								var bytes= await Task.WhenAny(readTask, Task.Delay(-1, cts.Token));

								if (bytes > 0)
								{
									_log?.Debug(9, "TUYADEV", $"Received: lenght={bytes}, data={BitConverter.ToString(buffer?.Take(bytes)?.ToArray())}");
								}
								else
								{
									_log?.Debug(9, "TUYADEV", $"Received: lenght={bytes}");
								}
							}



							bytes = await stream.ReadAsync(buffer, 0, length, linkedCts.Token);
							
						}
						catch (OperationCanceledException)
						{
							_log?.Debug(9, "TUYADEV", $"Received: timeout");
							throw new TimeoutException();
						}
					}
					finally
					{
							// close alll CancellationTokenSource
							timeoutCancellationTokenSource?.Dispose();
							linkedCts?.Dispose();
					}
					
					ms.Write(buffer, 0, bytes);
					var receivedArray = ms.ToArray();
					if (receivedArray.Length > 4)
					{
						var packetEnding = receivedArray.Skip(receivedArray.Length - 4).Take(4).ToArray();
						isEnding = TuyaParser.SUFFIX.SequenceEqual(packetEnding);
					}
					else
					{
						isEnding = true;
					}
					}
				result = ms.ToArray();
			}
			if ((result.Length <= 28) && (nullRetries > 0)) // empty response
			{
				await Task.Delay(NullRetriesInterval, cancellationToken);
				result = await ReceiveAsync(stream, nullRetries - 1, overrideRecvTimeout: overrideRecvTimeout, cancellationToken);
				_log?.Debug(9, "TUYADEV", $"Received: lenght={result.Length}, data={BitConverter.ToString(result)}");
			}
			return result;
		}*/

		/// <summary>
		/// Requests current DPs status.
		/// </summary>
		/// <param name="retries">Number of retries in case of network error (default - 2).</param>
		/// <param name="nullRetries">Number of retries in case of empty answer (default - 1).</param>
		/// <param name="overrideRecvTimeout">Override receive timeout (default - ReceiveTimeout property).</param>
		/// <param name="cancellationToken">Cancellation token.</param>
		/// <returns>Dictionary of DP numbers and values.</returns>
		public async Task<Dictionary<int, object>> GetDpsAsync(int retries = 5, int nullRetries = 1, int? overrideRecvTimeout = null, CancellationToken cancellationToken = default)
		{
			var requestJson = FillJson(null);
			var response = await SendAsync(TuyaCommand.DP_QUERY, requestJson, retries, nullRetries, overrideRecvTimeout, cancellationToken);
			if (string.IsNullOrEmpty(response.Json))
				throw new InvalidDataException("Response is empty");
			var root = JObject.Parse(response.Json);
			var dps = JsonConvert.DeserializeObject<Dictionary<string, object>>(root.GetValue("dps").ToString());
			return dps.ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);
		}

		/// <summary>
		/// Sets single DP to specified value.
		/// </summary>
		/// <param name="dp">DP number.</param>
		/// <param name="value">Value.</param>
		/// <param name="retries">Number of retries in case of network error (default - 2).</param>
		/// <param name="nullRetries">Number of retries in case of empty answer (default - 1).</param>
		/// <param name="overrideRecvTimeout">Override receive timeout (default - ReceiveTimeout property).</param>
		/// <param name="allowEmptyResponse">Do not throw exception on empty Response</param>
		/// <param name="cancellationToken">Cancellation token.</param>
		/// <returns>Dictionary of DP numbers and values.</returns>
		public async Task<Dictionary<int, object>> SetDpAsync(int dp, object value, int retries = 2, int nullRetries = 1, int? overrideRecvTimeout = null, bool allowEmptyResponse = false, CancellationToken cancellationToken = default)
				=> await SetDpsAsync(new Dictionary<int, object> { { dp, value } }, retries, nullRetries, overrideRecvTimeout, allowEmptyResponse, cancellationToken);

		/// <summary>
		/// Sets DPs to specified value.
		/// </summary>
		/// <param name="dps">Dictionary of DP numbers and values to set.</param>
		/// <param name="retries">Number of retries in case of network error (default - 2).</param>
		/// <param name="nullRetries">Number of retries in case of empty answer (default - 1).</param>
		/// <param name="overrideRecvTimeout">Override receive timeout (default - ReceiveTimeout property).</param>
		/// <param name="allowEmptyResponse">Do not throw exception on empty Response</param>
		/// <param name="cancellationToken">Cancellation token.</param>
		/// <returns>Dictionary of DP numbers and values.</returns>
		public async Task<Dictionary<int, object>> SetDpsAsync(Dictionary<int, object> dps, int retries = 2, int nullRetries = 1, int? overrideRecvTimeout = null, bool allowEmptyResponse = false, CancellationToken cancellationToken = default)
		{
			var cmd = new Dictionary<string, object>
						{
								{ "dps",  dps }
						};
			string requestJson = JsonConvert.SerializeObject(cmd);
			requestJson = FillJson(requestJson);
			var response = await SendAsync(TuyaCommand.CONTROL, requestJson, retries, nullRetries, overrideRecvTimeout, cancellationToken);
			if (string.IsNullOrEmpty(response.Json))
			{
				if (!allowEmptyResponse)
					throw new InvalidDataException("Response is empty");
				else
					return null;
			}
			var root = JObject.Parse(response.Json);
			var newDps = JsonConvert.DeserializeObject<Dictionary<string, object>>(root.GetValue("dps").ToString());
			return newDps.ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);
		}

		/// <summary>
		/// Update DP values.
		/// </summary>
		/// <param name="dpIds">DP identificators to update (can be empty for some devices).</param>
		/// <param name="retries">Number of retries in case of network error (default - 2).</param>
		/// <param name="nullRetries">Number of retries in case of empty answer (default - 1).</param>
		/// <param name="overrideRecvTimeout">Override receive timeout (default - ReceiveTimeout property).</param>
		/// <param name="cancellationToken">Cancellation token.</param>
		/// <returns>Dictionary of DP numbers and values.</returns>
		public async Task<Dictionary<int, object>> UpdateDpsAsync(IEnumerable<int> dpIds, int retries = 5, int nullRetries = 1, int? overrideRecvTimeout = null, CancellationToken cancellationToken = default)
		{
			var cmd = new Dictionary<string, object>
						{
								{ "dpId",  dpIds.ToArray() }
						};
			string requestJson = JsonConvert.SerializeObject(cmd);
			requestJson = FillJson(requestJson);
			var response = await SendAsync(TuyaCommand.UPDATE_DPS, requestJson, retries, nullRetries, overrideRecvTimeout, cancellationToken);
			if (string.IsNullOrEmpty(response.Json))
				return new Dictionary<int, object>();
			var root = JObject.Parse(response.Json);
			var newDps = JsonConvert.DeserializeObject<Dictionary<string, object>>(root.GetValue("dps").ToString());
			return newDps.ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);
		}

		/// <summary>
		/// Get current local key from Tuya Cloud API
		/// </summary>
		/// <param name="forceTokenRefresh">Refresh access token even it's not expired.</param>
		/// <param name="cancellationToken">Cancellation token.</param>
		public async Task RefreshLocalKeyAsync(bool forceTokenRefresh = false, CancellationToken cancellationToken = default)
		{
			if (string.IsNullOrEmpty(accessId)) throw new ArgumentException("Access ID is not specified", "accessId");
			if (string.IsNullOrEmpty(apiSecret)) throw new ArgumentException("API secret is not specified", "apiSecret");
			var api = new TuyaApi(region, accessId, apiSecret);
			var deviceInfo = await api.GetDeviceInfoAsync(DeviceId, forceTokenRefresh: forceTokenRefresh, cancellationToken);
			LocalKey = deviceInfo.LocalKey;
		}

		/// <summary>
		/// Disposes object.
		/// </summary>
		public void Dispose()
		{
			CloseSocket();
		}
	}
}
