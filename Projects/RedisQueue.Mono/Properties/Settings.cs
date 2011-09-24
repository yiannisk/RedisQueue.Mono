using System;
using Newtonsoft.Json;
using System.IO;

namespace RedisQueue.Mono.Properties
{
	public class Settings
	{
		#region Default Settings
		const string DefaultFileName = "config.json";
		
		private static Settings _defaultSettings;
		
		public static Settings ReadSettings() 
		{
			return JsonConvert.DeserializeObject<Settings>(File.ReadAllText(DefaultFileName)); 
		}
		
		public static Settings Default
		{
			get 
			{ 
				return _defaultSettings ?? (_defaultSettings = ReadSettings());
			}
		}
		#endregion
		
		public virtual string RedisHost { get; set; }
		public virtual int RedisPort { get; set; }
		public virtual bool TaskRecycling { get; set; }
		public virtual int MaxTaskRetries { get; set; }
		public virtual bool PurgeSuccessfulTasks { get; set; }
		public virtual string Queue { get; set; }
		public virtual int MonitorSleepIntervalInMilliseconds { get; set; }
		public virtual int RetriesToUnloadAppDomain { get; set; }
		public virtual string WorkerAssemblyPath { get; set; }
		public virtual string WorkerClassName { get; set; }
	}
}