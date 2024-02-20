using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using com.clusterrr.TuyaNet.Dps;
using com.clusterrr.TuyaNet.Extensions;
using com.clusterrr.TuyaNet.Models;
using Newtonsoft.Json;

namespace com.clusterrr.TuyaNet.Services
{
    public class TuyaSwitcherService
    {
        private readonly TuyaDevice device;

        public TuyaSwitcherService(TuyaDeviceInfo deviceInfo)
        {
            this.device = GetDevice(deviceInfo);
        }

        private TuyaDevice GetDevice(TuyaDeviceInfo tuyaDeviceInfo)
        {
            var allVersions = EnumExtensions.GetTuyaVersionsValues()
                .Select(x => new { ver = x.ToString().Replace("V", ""), value = x });

            var deviceVersion = allVersions.SingleOrDefault(x => x.ver == tuyaDeviceInfo.ApiVer.Replace(".", ""))
                ?.value;
            if (deviceVersion is null)
                throw new NotSupportedException($"Not supported version {tuyaDeviceInfo.ApiVer}");

            var dev = new TuyaDevice(
                ip: tuyaDeviceInfo.LocalIp,
                localKey: tuyaDeviceInfo.LocalKey,
                deviceId: tuyaDeviceInfo.DeviceId,
                protocolVersion: deviceVersion.Value);

            dev.PermanentConnection = true;

            return dev;
        }

        public async Task<bool> GetStatus(string switchNo = "1")
        {
            var statusResp = await GetStatus(device);
            Console.WriteLine($"Check status info. Response JSON: {statusResp.Json}");
						var dps = JsonConvert.DeserializeObject<TuayDps>(statusResp.Json);
						if (!dps.dps.TryGetValue(switchNo, out var status))
						{
							throw new Exception($"switch {switchNo} not found");
						}
						return Convert.ToBoolean(status);
        }

        public async Task TurnOn(string switchNo = "1")
        {
            var response = await SetStatus(device, true, switchNo);
            Console.WriteLine($"Set status info. Response JSON: {response.Json}");
        }

        public async Task Connect()
        {
            await device.SecureConnectAsync();
            Console.WriteLine($"Success connected.");
        }
		public void Disconnect()
		{
			device.Close();
			Console.WriteLine($"Success disconnected.");
		}

		public async Task TurnOff(string switchNo = "1")
        {
            var response = await SetStatus(device, false, switchNo);
            Console.WriteLine($"Set status info. Response JSON: {response.Json}");
        }

        private async Task<TuyaLocalResponse> GetStatus(TuyaDevice dev)
        {
            var requestQuery =
                "{\"gwId\":\"DEVICE_ID\",\"devId\":\"DEVICE_ID\",\"uid\":\"DEVICE_ID\",\"t\":\"CURRENT_TIME\"}";
            var command = TuyaCommand.DP_QUERY;
            var request = dev.EncodeRequest(command, requestQuery);
            var encryptedResponse = await dev.SendAsync(command, request);
            var response = dev.DecodeResponse(encryptedResponse);
            return response;
        }

        private async Task<TuyaLocalResponse> SetStatus(TuyaDevice dev, bool switchStatus, string switchNo)
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
                requestQuery = dev.FillJson("{\"dps\":{\""+ switchNo + "\":" + switchStatus.ToString().ToLower() + "}}");
            }

            var request = dev.EncodeRequest(command, requestQuery);
            var encryptedResponse = await dev.SendAsync(command, request);
            var response = dev.DecodeResponse(encryptedResponse);
            return response;
        }
    }
}