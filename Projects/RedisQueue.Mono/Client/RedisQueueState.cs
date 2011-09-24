namespace RedisQueue.Mono.Client
{
	/// <summary>
	/// Represents the internal state of the RedisQueue client.
	/// </summary>
	internal enum RedisQueueState
	{
		/// <summary>
		/// Client can reserve and enqueue tasks.
		/// </summary>
		Ready = 0,

		/// <summary>
		/// Client can only enqueue tasks, having already reserved a single task.
		/// </summary>
		TaskReserved = 1
	}
}