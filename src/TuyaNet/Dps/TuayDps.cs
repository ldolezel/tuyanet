﻿using Newtonsoft.Json;
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
		public double? TryGetDouble(string id)
		{
			try
			{
				if (dps.TryGetValue(id, out var value))
				{
					return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
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
					if (dps.TryGetValue(id, out var value))
					{
						return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
					}
				}
				return null;
			}
			catch
			{ return null; }
		}

	}


}
