using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Text;

namespace com.clusterrr.TuyaNet.Log
{
	public interface ILog
	{
		void Error(string tag, string message);
		void Info(string tag, string message);
		void Debug(int level,string tag, string message);
	}
}
