using System;

namespace RedisQueue.Mono.Exceptions
{
	/// <summary>
	/// Thrown by <c>RedisQueue.Reserve()</c> when a task has already been reserved.
	/// </summary>
	public class TaskAlreadyReservedException : Exception
	{
		public TaskAlreadyReservedException(string message) : base(message) {}
	}
}