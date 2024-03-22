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
using System.Diagnostics;
using System.Xml.Linq;

namespace com.clusterrr.TuyaNet
{
	/// <summary>
	/// Connection with Tuya device.
	/// </summary>
	public partial class TuyaDevice : IDisposable
	{
		private readonly TuyaParser parser;
		ILog _log;
		string _name;
		/// <summary>
		/// Creates a new instance of the TuyaDevice class.
		/// </summary>
		/// <param name="name">optional name</param>
		/// <param name="ip">IP address of device.</param>
		/// <param name="localKey">Local key of device (obtained via API).</param>
		/// <param name="deviceId">Device ID.</param>
		/// <param name="protocolVersion">Protocol version.</param>
		/// <param name="port">TCP port of device.</param>
		/// <param name="receiveTimeout">Receive timeout (msec).</param>
		public TuyaDevice(string name,
				string ip, string localKey, string deviceId, ILog log = null ,
				TuyaProtocolVersion protocolVersion = TuyaProtocolVersion.V33,
				int port = 6668,
				int receiveTimeout = 1500)
		{
			_name = name ?? "TUYADEV";
			_log = log;
			IP = ip;
			LocalKey = localKey;
			this.accessId = null;
			this.apiSecret = null;
			DeviceId = deviceId;
			ProtocolVersion = protocolVersion;
			Port = port<=0 ? 6668:port;
			ReceiveTimeout = receiveTimeout;
			parser = new TuyaParser(localKey, protocolVersion);
		}

		/// <summary>
		/// Creates a new instance of the TuyaDevice class.
		/// </summary>
		/// <param name="name">optional name</param>
		/// <param name="ip">IP address of device.</param>
		/// <param name="region">Region to access Cloud API.</param>
		/// <param name="accessId">Access ID to access Cloud API.</param>
		/// <param name="apiSecret">API secret to access Cloud API.</param>
		/// <param name="deviceId">Device ID.</param>
		/// <param name="protocolVersion">Protocol version.</param>
		/// <param name="port">TCP port of device.</param>
		/// <param name="receiveTimeout">Receive timeout (msec).</param> 
		public TuyaDevice(string name, string ip, TuyaApi.Region region, string accessId, string apiSecret, string deviceId,
			 ILog log = null ,TuyaProtocolVersion protocolVersion = TuyaProtocolVersion.V33, int port = 6668, int receiveTimeout = 250)
		{
			_name = name ?? "TUYADEV"; ;
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
		public int ConnectionTimeout { get; set; } = 1000;
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
			return root.ToString( Formatting.None);
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
		/// Parses and decrypts payload data from received bytes.
		/// </summary>
		/// <param name="data">Raw data to parse and decrypt.</param>
		/// <returns>Instance of TuyaLocalResponse.</returns>
		public IEnumerable<TuyaLocalResponse> DecodeResponses(IEnumerable<byte[]> data)
		{
			if (string.IsNullOrEmpty(LocalKey)) throw new ArgumentException("LocalKey is not specified", "LocalKey");
			foreach (byte[] onedata in data)
			{
				yield return parser.DecodeResponse(onedata);
			}
		}
		/// <summary>
		/// Sends JSON string to device and reads response.
		/// </summary>
		/// <param name="command">Tuya command ID.</param>
		/// <param name="json">JSON string.</param>
		/// <param name="nullRetries">Number of retries in case of empty answer (default - 1).</param>
		/// <param name="overrideRecvTimeout">Override receive timeout (default - ReceiveTimeout property).</param>
		/// <param name="cancellationToken">Cancellation token.</param>
		/// <returns>Parsed and decrypred received data as instance of TuyaLocalResponse.</returns>
		public async Task<IEnumerable<TuyaLocalResponse>> SendAsync(TuyaCommand command, string json, int nullRetries = 1, int? overrideRecvTimeout = null, bool waitForAnswer = true, CancellationToken cancellationToken = default)
		{
			if (IsServiceLoopStarted)
			{
				var r = await DoQueued<IEnumerable<byte[]>>(() => SendAndReadAsync(command, EncodeRequest(command, json), nullRetries, overrideRecvTimeout, waitForAnswer, cancellationToken));
				return DecodeResponses(r);
			}
			else
			{
				return await Task.Run(() => DecodeResponses(SendAndRead(command, EncodeRequest(command, json), nullRetries, overrideRecvTimeout, cancellationToken)));
			}
		}


		public bool IsConnected()
		{
			return client?.Connected == true;
		}

		public async Task SecureConnectAsync(CancellationToken cancellationToken = default)
		{
			_cancelIdleRead?.Cancel();
			_cancelIdleRead = null;
			await SecureConnectAsyncInternal(cancellationToken);
			StartServiceLoop(cancellationToken, PermanentConnection);
			return;
		}

		async Task SecureConnectAsyncInternal(CancellationToken cancellationToken)
		{
			if (client == null)
				client = new TcpClient();
			if (!client.ConnectAsync(IP, Port).Wait(ConnectionTimeout))
			{
				_log?.Error(_name, $"Connection to {IP}:{Port} timeout");
				throw new IOException($"Connection to {IP}:{Port} timeout");
			}
			_log?.Debug(6, _name, $"Connection to {IP}:{Port} opened");
			

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

				client.Client.Send(request);
				var receivedBytes = Receive(client, 1, null, cancellationToken);
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
					client.Client.Send(requestFinish);

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
			StopServiceLoop();
			CloseSocket();
			_log?.Debug(6, _name, $"Device closed");
		}

		/*
		 * 	Some TUYA devices immediately close the TCP connection after sending the response 
		 * 	and the original async implementation can't read it and raises exception because of closed socket 
		 * 	NetworkStream data is also unavaiable at this time.
		 * 	So I decided to rewrite it but it didnt help
		 * 	
		 * 	Many Tuya devices do not handle multiple commands sent in quick succession. 
		 * 	Some will reboot, possibly changing state in the process, 
		 * 	others will go offline for 30s to a few minutes if you overload them. 
		 * 	There is some rate limiting to try to avoid this, but it is not sufficient for many devices, 
		 * 	and may not work across entities where you are sending commands to multiple entities on the same device. 
		 * 	The rate limiting also combines commands, which not all devices can handle. 
		 * 	If you are sending commands from an automation, it is best to add delays between commands - if your automation is for multiple devices, it might be enough to send commands to other devices first before coming back to send a second command to the first one, or you may still need a delay after that. The exact timing depends on the device, 
		 * 	so you may need to experiment to find the minimum delay that gives reliable results.
				Some devices can handle multiple commands in a single message, so for entity platforms that support it (eg climate set_temperature can include presets, lights pretty much everything is set through turn_on) multiple settings are sent at once. But some devices do not like this and require all commands to set only a single dp at a time, so you may need to experiment with your automations to see whether a single command or multiple commands (with delays, see above) work best with your devices.
			https://github.com/make-all/tuya-local
		 */

		public async Task<IEnumerable<byte[]>> SendAsync(TuyaCommand command, byte[] data, int nullRetries = 1, int? overrideRecvTimeout = null, bool waitForAnswer = true,
			CancellationToken cancellationToken = default)
		{
			if (IsServiceLoopStarted)
			{
				return await DoQueued<IEnumerable<byte[]>>(() => SendAndReadAsync(command, data, nullRetries, overrideRecvTimeout, waitForAnswer, cancellationToken));
			}else
			{
				return await Task.Run(() => SendAndRead(command, data, nullRetries, overrideRecvTimeout, cancellationToken));
			}
		}

		//static int ct = 1;

		/// <summary>
		/// Some TYUA devices
		/// </summary>
		/// <param name="command"></param>
		/// <param name="data"></param>
		/// <param name="nullRetries"></param>
		/// <param name="overrideRecvTimeout"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		/// <exception cref="Exception"></exception>
		IEnumerable<byte[]> SendAndRead(TuyaCommand command, byte[] data, int nullRetries = 1, int? overrideRecvTimeout = null,
			CancellationToken cancellationToken = default)
		{
			if (!IsConnected()) throw new Exception("Not connected");
			List<byte[]> result = new List<byte[]>();	
			Exception lastException = null;
			try
			{
				try
				{
					byte[] buffer = new byte[1024];
					if (data != null)
					{
						_log?.Debug(9, _name, $"Sending: lenght={data.Length}, data={BitConverter.ToString(data)}");
						client.Client.Send(data);
					}

					while (result.Count<=0)
					{
						var responseRaw = Receive(client, nullRetries, overrideRecvTimeout, cancellationToken);
						/*System.IO.File.WriteAllBytes(@"C:\tmp\tuyalog\responseRaw" + _name + ct.ToString() + ".bin", responseRaw);
						ct++;*/


						var responsesParsed = parser.DecodeResponses(responseRaw).ToArray();
						foreach (var item in responsesParsed)
						{
							_log?.Debug(8, _name, $"Received json: {item.Json}");
							_log?.Debug(8, _name, $"Received command: {item.Command.GetNames()}");

							//remove empty
							if (item.Payload != null && item.Payload.Length>0)
							{
								result.Add(item.OriginalPacket);

								//for protocol v3.4 need wait response by command
								if (item.Command == command /*||
										ProtocolVersion != TuyaProtocolVersion.V34*/)
								{
									return new[] { item.OriginalPacket }; //found exact
								}
							}
						}
					}
					return result;


				}
				catch (Exception ex)
				{
					lastException = ex;
					_log?.Error(_name, ex.Message);
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
		}

		async Task<IEnumerable<byte[]>> SendAndReadAsync(TuyaCommand command, byte[] data, int nullRetries = 1, int? overrideRecvTimeout = null,
				bool waitForAnswer = true,CancellationToken cancellationToken = default)
		{
			if (!IsConnected() && PermanentConnection)
			{
				CloseSocket();
				await SecureConnectAsyncInternal(cancellationToken);
			}

			if (!IsConnected()) throw new Exception("Not connected");

				
			
			List<byte[]> result = new List<byte[]>();
			Exception lastException = null;
			try
			{
				try
				{
					byte[] buffer = new byte[1024];
					if (data != null)
					{
						_log?.Debug(9, _name, $"Sending: lenght={data.Length}, data={BitConverter.ToString(data)}");
						await client.Client.SendAsync(new ReadOnlyMemory<byte>(data), SocketFlags.None);
					}

					while (waitForAnswer && result.Count <= 0)
					{
						var responseRaw = await ReceiveAsync(client, nullRetries, overrideRecvTimeout, cancellationToken);
						/*System.IO.File.WriteAllBytes(@"C:\tmp\tuyalog\responseRaw" + _name + ct.ToString() + ".bin", responseRaw);
						ct++;*/


						var responsesParsed = parser.DecodeResponses(responseRaw).ToArray();
						foreach (var item in responsesParsed)
						{
							_log?.Debug(8, _name, $"Received json: {item.Json}");
							_log?.Debug(8, _name, $"Received command: {item.Command.GetNames()}");

							//remove empty
							if (item.Payload != null && item.Payload.Length > 0)
							{
								result.Add(item.OriginalPacket);

								//for protocol v3.4 need wait response by command
								if (item.Command == command /*||
										ProtocolVersion != TuyaProtocolVersion.V34*/)
								{
									return new[] { item.OriginalPacket }; //found exact
								}
							}
						}
					}
					return result;


				}
				catch (Exception ex)
				{
					lastException = ex;
					_log?.Error(_name, ex.Message);
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
		}



		byte[] Receive(TcpClient client, int nullRetries = 1, int? overrideRecvTimeout = null, CancellationToken cancellationToken = default)
		{
			var isEnding = false;
			byte[] result;
			byte[] buffer = new byte[512];
			using (var ms = new MemoryStream())
			{
				int length = buffer.Length;
				int timeout = overrideRecvTimeout ?? ReceiveTimeout;
				client.Client.ReceiveTimeout = timeout;
				Stopwatch sw = Stopwatch.StartNew();
				while (!isEnding)
				{
					var timeoutCancellationTokenSource = new CancellationTokenSource();
					var bytes = client.Client.Receive(buffer, 0, length, SocketFlags.None);
					if (bytes > 0)
					{
						ms.Write(buffer, 0, bytes);
						_log?.Debug(9, _name, $"Received: lenght={bytes}, data={BitConverter.ToString(buffer?.Take(bytes)?.ToArray())}");
					}
					var receivedArray = ms.ToArray();
					if (receivedArray.Length > 4)
					{
						var packetEnding = receivedArray.Skip(receivedArray.Length - 4).Take(4).ToArray();
						isEnding = TuyaParser.SUFFIX.SequenceEqual(packetEnding);
					}
					/*	else
						{
							isEnding = true;
						}*/
					if (sw.ElapsedMilliseconds > timeout)
					{
						throw new TimeoutException();
					}
				}
				result = ms.ToArray();
			}

			if ((result.Length <= 28) && (nullRetries > 0)) // empty response
			{
				Task.Delay(NullRetriesInterval, cancellationToken).Wait();
				result = Receive(client, nullRetries - 1, overrideRecvTimeout: overrideRecvTimeout, cancellationToken);
			}
			return result;
		}

		async Task<byte[]> ReceiveAsync(TcpClient client, int nullRetries = 1, int? overrideRecvTimeout = null, CancellationToken cancellationToken = default)
		{
			var isEnding = false;
			byte[] result;
			byte[] buffer = new byte[512];
			using (var ms = new MemoryStream())
			{
				int length = buffer.Length;
				int timeout = overrideRecvTimeout ?? ReceiveTimeout;
				client.Client.ReceiveTimeout = timeout;
				Stopwatch sw = Stopwatch.StartNew();
				while (!isEnding)
				{
					var timeoutCancellationTokenSource = new CancellationTokenSource();
					var bytes = await client.Client.ReceiveAsync(new Memory<byte>(buffer, 0, length), SocketFlags.None);
					if (bytes > 0)
					{
						await ms.WriteAsync(buffer, 0, bytes);
						_log?.Debug(9, _name, $"Received: lenght={bytes}, data={BitConverter.ToString(buffer?.Take(bytes)?.ToArray())}");
					}
					var receivedArray = ms.ToArray();
					if (receivedArray.Length > 4)
					{
						var packetEnding = receivedArray.Skip(receivedArray.Length - 4).Take(4).ToArray();
						isEnding = TuyaParser.SUFFIX.SequenceEqual(packetEnding);
					}
					/*	else
						{
							isEnding = true;
						}*/
					if (sw.ElapsedMilliseconds > timeout)
					{
						throw new TimeoutException();
					}
				}
				result = ms.ToArray();
			}

			if ((result.Length <= 28) && (nullRetries > 0)) // empty response
			{
				await Task.Delay(NullRetriesInterval, cancellationToken);
				result = await ReceiveAsync(client, nullRetries - 1, overrideRecvTimeout: overrideRecvTimeout, cancellationToken);
			}
			return result;
		}

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
			var responses = await SendAsync(TuyaCommand.DP_QUERY, requestJson, nullRetries, overrideRecvTimeout,true, cancellationToken);
			var response = responses.FirstOrDefault();

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
		/// <param name="nullRetries">Number of retries in case of empty answer (default - 1).</param>
		/// <param name="overrideRecvTimeout">Override receive timeout (default - ReceiveTimeout property).</param>
		/// <param name="allowEmptyResponse">Do not throw exception on empty Response</param>
		/// <param name="cancellationToken">Cancellation token.</param>
		/// <returns>Dictionary of DP numbers and values.</returns>
		public async Task<Dictionary<int, object>> SetDpAsync(int dp, object value,  int nullRetries = 1, int? overrideRecvTimeout = null, bool allowEmptyResponse = false, CancellationToken cancellationToken = default)
				=> await SetDpsAsync(new Dictionary<int, object> { { dp, value } }, nullRetries, overrideRecvTimeout, allowEmptyResponse, cancellationToken);

		/// <summary>
		/// Sets DPs to specified value.
		/// </summary>
		/// <param name="dps">Dictionary of DP numbers and values to set.</param>
		/// <param name="nullRetries">Number of retries in case of empty answer (default - 1).</param>
		/// <param name="overrideRecvTimeout">Override receive timeout (default - ReceiveTimeout property).</param>
		/// <param name="allowEmptyResponse">Do not throw exception on empty Response</param>
		/// <param name="cancellationToken">Cancellation token.</param>
		/// <returns>Dictionary of DP numbers and values.</returns>
		public async Task<Dictionary<int, object>> SetDpsAsync(Dictionary<int, object> dps, int nullRetries = 1, int? overrideRecvTimeout = null, bool allowEmptyResponse = false, CancellationToken cancellationToken = default)
		{
			var cmd = new Dictionary<string, object>
						{
								{ "dps",  dps }
						};
			string requestJson = JsonConvert.SerializeObject(cmd);
			requestJson = FillJson(requestJson);
			var responses = await SendAsync(TuyaCommand.CONTROL, requestJson, nullRetries, overrideRecvTimeout,true, cancellationToken);
			var response = responses.FirstOrDefault();
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
		/// <param name="nullRetries">Number of retries in case of empty answer (default - 1).</param>
		/// <param name="overrideRecvTimeout">Override receive timeout (default - ReceiveTimeout property).</param>
		/// <param name="cancellationToken">Cancellation token.</param>
		/// <returns>Dictionary of DP numbers and values.</returns>
		public async Task<Dictionary<int, object>> UpdateDpsAsync(IEnumerable<int> dpIds, int nullRetries = 1, int? overrideRecvTimeout = null, CancellationToken cancellationToken = default)
		{
			var cmd = new Dictionary<string, object>
						{
								{ "dpId",  dpIds.ToArray() }
						};
			string requestJson = JsonConvert.SerializeObject(cmd);
			requestJson = FillJson(requestJson);
			var responses = await SendAsync(TuyaCommand.UPDATE_DPS, requestJson,  nullRetries, overrideRecvTimeout, true, cancellationToken);
			var response = responses.FirstOrDefault();
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
