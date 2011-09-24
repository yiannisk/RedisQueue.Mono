using System.Collections.Generic;

namespace RedisQueue.Mono.Worker
{
	public interface IPerform
	{
		void Perform(string args);
		IPerformResult Status { get; }
		IDictionary<string, string> TaskStorage { get; set; }
	}
}