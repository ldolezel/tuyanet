using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;

namespace com.clusterrr.TuyaNet.Dps
{
	internal class TuayDps
	{
		[JsonInclude()]
		public Dictionary<string, object> dps { get; set; }
	}


}
