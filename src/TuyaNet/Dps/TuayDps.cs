using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;

namespace com.clusterrr.TuyaNet.Dps
{
	public class TuayDps
	{
		[JsonInclude()]
		public Dictionary<string, object> dps { get; set; }

		public bool? TryGetBool(string id)
		{
			try
			{
				if (dps.TryGetValue(id, out var value))
				{
					return Convert.ToBoolean(value);
				}
				return null;
			}
			catch
			{ return null; }
		}
		public void TrySetBool(string id, bool value)
		{
			try
			{
				if (dps.TryGetValue(id, out _))
				{
					dps[id] = value;
				}
			}
			catch
			{ }
		}

		public double? TryGetDouble(string id)
		{
			try
			{
				if (string.IsNullOrWhiteSpace(id)) return null;
				if (dps.TryGetValue(id, out var value))
				{
					if (double.TryParse(value?.ToString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var res))
					{
						return res;
					}
				}
				return null;
			}
			catch
			{ return null; }
		}
		public double? TryGetDouble(string [] ids)
		{
			try
			{
				if (ids==null) return null;
				foreach (var id in ids)
				{
					if (string.IsNullOrWhiteSpace(id)) continue;
					if (dps.TryGetValue(id, out var value))
					{
						if (double.TryParse (value?.ToString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture,out var res))
						{
							return res;
						}
						
					}
				}
				return null;
			}
			catch
			{ return null; }
		}

	}


}
