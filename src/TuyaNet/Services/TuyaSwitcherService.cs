using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using com.clusterrr.TuyaNet.Dps;
using com.clusterrr.TuyaNet.Extensions;
using com.clusterrr.TuyaNet.Log;
using com.clusterrr.TuyaNet.Models;
using Newtonsoft.Json;

namespace com.clusterrr.TuyaNet.Services
{
	public class TuyaSwitcherService
	{
		TuyaDevice _device;
		ILog _log = null;
		TuyaDeviceInfo _deviceInfo;
		bool _permanentConnection;

		public TuyaSwitcherService(TuyaDeviceInfo deviceInfo, bool permanentConnection = false, ILog log = null)
		{
			_log = log;
			_deviceInfo = deviceInfo;
			_permanentConnection = permanentConnection;
		}

		TuyaDevice Device
		{
			get
			{ 
				if (_device==null)
				{
					_device = GetDevice(_deviceInfo, _permanentConnection, _log);
					_device.AsyncMessageReceived += _device_AsyncMessageReceived;
				}
				return _device;
			}
			set
			{ 
				if (_device != null)
				{
					_device.AsyncMessageReceived -= _device_AsyncMessageReceived;
				}
				_device = value;
				if (value!=null)
				{
					value.AsyncMessageReceived += _device_AsyncMessageReceived;
				}
				
			}
		}

		private void _device_AsyncMessageReceived(object sender, TuyaDevice.ReceiveMsg e)
		{
			if (e.Message.Command == TuyaCommand.STATUS)
			{
				var dps = JsonConvert.DeserializeObject<TuayDps>(e.Message.Json);

			}
		}

		public static TuyaDevice GetDevice(TuyaDeviceInfo tuyaDeviceInfo, bool permanentConnection, ILog log = null)
		{
			var allVersions = EnumExtensions.GetTuyaVersionsValues()
					.Select(x => new { ver = x.ToString().Replace("V", ""), value = x });

			var deviceVersion = allVersions.SingleOrDefault(x => x.ver == tuyaDeviceInfo.ApiVer.Replace(".", ""))
					?.value;
			if (deviceVersion is null)
				throw new NotSupportedException($"Not supported version {tuyaDeviceInfo.ApiVer}");

			var dev = new TuyaDevice(name: tuyaDeviceInfo.Name,
					ip: tuyaDeviceInfo.LocalIp,
					port: tuyaDeviceInfo.Port <=0 ? 6668: tuyaDeviceInfo.Port,
					localKey: tuyaDeviceInfo.LocalKey,
					deviceId: tuyaDeviceInfo.DeviceId,
					protocolVersion: deviceVersion.Value,
					log: log);
					
			dev.PermanentConnection = permanentConnection;

			return dev;
		}

		public async Task<bool> GetStatus(string switchNo = "1")
		{
			var statusResp = await GetFullStatus(switchNo);
			var dps = JsonConvert.DeserializeObject<TuayDps>(statusResp.Json);
			var res = dps.TryGetBool(switchNo);
			if (res==null)
			{
				throw new Exception($"switch {switchNo} not found");
			}
			return res.Value;
		}

		public async Task<TuyaLocalResponse> GetFullStatus(string switchNo = "1")
		{
			var statusResp = await GetStatus(Device);
			_log?.Debug(8, Device?.DeviceId, $"GetFullStatus of {switchNo}. Response JSON: {statusResp?.Json}");
			return statusResp;
		}
		public async Task TurnOn(string switchNo = "1")
		{
			var response = await SetStatus(Device, true, switchNo);
			_log?.Debug(8, Device?.DeviceId, $"Turn on {switchNo}. Response JSON: {response?.Json}");
		}

		public async Task Connect()
		{
			await Device.SecureConnectAsync();
			_log?.Debug(5, Device?.DeviceId, $"Success connected.");
		}
		public bool IsConnected()
		{
			return _device != null && _device.IsConnected();
		}


		public void Disconnect()
		{
			var tmp = _device;
			Device = null;
			try
			{
				tmp?.Close();
				_log?.Debug(5, tmp?.DeviceId, $"Success disconnected.");
			}
			catch (Exception ex)
			{
				_log?.Debug(5, tmp?.DeviceId, $"Error disconnected: {ex.Message}");
			}
		}

		public async Task TurnOff(string switchNo = "1")
		{
			var response = await SetStatus(Device, false, switchNo);
			_log?.Debug(8, Device?.DeviceId, $"Turn off {switchNo}. Response JSON: {response?.Json}");
		}

		private async Task<TuyaLocalResponse> GetStatus(TuyaDevice dev)
		{
			/*var requestQuery =
					"{\"gwId\":\"DEVICE_ID\",\"devId\":\"DEVICE_ID\",\"uid\":\"DEVICE_ID\",\"t\":\"CURRENT_TIME\"}";*/

			var requestQuery = dev.FillJson("", true, true, true, true);

			var command = TuyaCommand.DP_QUERY;
			_log?.Debug(8, "TUYA", $"Sending JSON {requestQuery}");
			var request = dev.EncodeRequest(command, requestQuery);
			var encryptedResponses = await dev.SendAsync(command, request);
			var encryptedResponse = encryptedResponses?.FirstOrDefault();
			var response = dev.DecodeResponse(encryptedResponse);
			_log?.Debug(8, "TUYA", $"Received JSON {response?.Json}");
			return response;
		}

		public async Task<TuyaLocalResponse> SetStatus(string switchNo, bool switchStatus)
		{
			var response = await SetStatus(Device, switchStatus, switchNo);
			_log?.Debug(8, Device?.DeviceId, $"SetStatus {switchNo} to {switchStatus}. Response JSON: {response?.Json}");
			return response;
		}
		async Task<TuyaLocalResponse> SetStatus(TuyaDevice dev, bool switchStatus, string switchNo)
		{
			var requestQuery = string.Empty;
			var command = TuyaCommand.CONTROL;

			if (dev.ProtocolVersion == TuyaProtocolVersion.V34)
			{
				command = TuyaCommand.CONTROL_NEW;
				var rawJson = new
				{
					data = new
					{
						ctype = 0,
						devId = dev.DeviceId,
						gwId = dev.DeviceId,
						uid = string.Empty,
						dps = new Dictionary<string, object>()
												{
														{switchNo, switchStatus }
												}
					},
					protocol = 5,
					t = (DateTime.Now - new DateTime(1970, 1, 1)).TotalSeconds.ToString("0")
				};
				requestQuery = JsonConvert.SerializeObject(rawJson);
			}
			else
			{
				requestQuery = dev.FillJson("{\"dps\":{\"" + switchNo + "\":" + switchStatus.ToString().ToLower() + "}}");
			}

			_log?.Debug(8, "TUYA", $"Sending JSON {requestQuery}");
			var request = dev.EncodeRequest(command, requestQuery);
			var encryptedResponses = await dev.SendAsync(command, request);
			var encryptedResponse = encryptedResponses.FirstOrDefault();

			var response = dev.DecodeResponse(encryptedResponse);
			_log?.Debug(8, "TUYA", $"Received JSON {response?.Json}");
			return response;
		}
	}
}