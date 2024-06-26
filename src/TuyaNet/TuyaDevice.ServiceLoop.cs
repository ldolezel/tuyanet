﻿using com.clusterrr.TuyaNet.Extensions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace com.clusterrr.TuyaNet
{
	public partial class TuyaDevice
	{
		const int HEART_BEAT_INTERVAL = 30000;
		ConcurrentQueue<Func<Task>> _serviceQueue = new ConcurrentQueue<Func<Task>>();
		bool _ServiceLoopStop = false;
		bool _ServiceLoopStarted = false;
		
		bool IsServiceLoopStarted => _ServiceLoopStarted;

		async Task<TResult> DoQueued<TResult>(Func<Task<TResult>> function)
		{
			var tcs = new TaskCompletionSource<TResult>();
			_serviceQueue.Enqueue(async () =>
			{
				try
				{
					var r = await function();
					tcs.SetResult(r);
				}
				catch (Exception ex) 
				{
					tcs.SetException(ex);
				}
			});
			return await tcs.Task;
		}


		void StartServiceLoop(CancellationToken canceltoken, bool permanentConn)
		{
			Task.Run(async () =>
			{
				_ServiceLoopStarted = true;
				Stopwatch sw = Stopwatch.StartNew();	
				while (!_ServiceLoopStop && !canceltoken.IsCancellationRequested)
				{
					if (_serviceQueue.TryDequeue(out var taskToProcess))
					{
						await taskToProcess();
						sw.Restart();
					}
					else
					{
						await Task.Delay(50, canceltoken);
						if (permanentConn )
						{
							var existing = await ReadExisting(this, canceltoken);
							if (existing!=null && existing.ReturnCode==0)
							{
								//process message - maybe STATUS
								OnAsyncMessageReceived (existing);
							}
						}
					}



					if (permanentConn && sw.ElapsedMilliseconds>= HEART_BEAT_INTERVAL)
					{
					//	await SendHeartBeat(this, canceltoken);
						sw.Restart();
					}
				}
				_ServiceLoopStarted = false;
			});
		}

		void StopServiceLoop()
		{
			_ServiceLoopStop = true;
			_ServiceLoopStarted = false;
		}
	

		async Task<TuyaLocalResponse> SendHeartBeat(TuyaDevice dev, CancellationToken canceltoken)
		{
			try
			{
				var requestQuery = dev.FillJson("", true, true, true, true);

				var command = TuyaCommand.HEART_BEAT;
				_log?.Debug(8, "TUYA", $"Sending Heartbeat JSON {requestQuery}");
				var request = dev.EncodeRequest(command, requestQuery);
				var encryptedResponses = await dev.SendAndReadAsync(command, request, 0, 500,true,true, canceltoken);
				var encryptedResponse = encryptedResponses?.FirstOrDefault();
				var response = dev.DecodeResponse(encryptedResponse);
				_log?.Debug(8, _name, $"Received command {response.Command.GetNames()}, JSON {response?.Json}");
				return response;
			}
			catch  (Exception ex) 
			{
				return await Task.FromResult(new TuyaLocalResponse(TuyaCommand.HEART_BEAT, 1, null, ex.Message, null));
			}
		}

		async Task<TuyaLocalResponse> ReadExisting(TuyaDevice dev, CancellationToken canceltoken)
		{
			try
			{
				var encryptedResponses = await dev.SendAndReadAsync( TuyaCommand.HEART_BEAT /*no matter*/,null, 0, 0,true,false, canceltoken);
				if (encryptedResponses != null)
				{
					var encryptedResponse = encryptedResponses?.FirstOrDefault();
					var response = dev.DecodeResponse(encryptedResponse);
					_log?.Debug(8, _name, $"Received command {response.Command.GetNames()}, JSON {response?.Json}");
					return response;
				}
				return null;
			}
			catch (Exception ex)
			{
				return await Task.FromResult(new TuyaLocalResponse(TuyaCommand.HEART_BEAT, 1, null, ex.Message, null));
			}
		}
	}
}
