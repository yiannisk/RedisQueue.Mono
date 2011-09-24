using System;

namespace RedisQueue.Mono.Exceptions
{
	/// <summary>
	/// Thrown by <c>RedisQueue.Reserve()</c> when the specified queue is empty.
	/// </summary>
	public class QueueIsEmptyException : Exception
	{
		public QueueIsEmptyException(string message) : base (message) {}
	}
}