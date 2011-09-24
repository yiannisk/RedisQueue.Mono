using System;

namespace RedisQueue.Mono.Exceptions
{
	/// <summary>
	/// An operation requiring a reserved task was asked of the RedisQueue client, like Succeed() or Fail(...),
	/// without a task having been reserved first.
	/// </summary>
	public class NoTaskReservedException : Exception
	{
		public NoTaskReservedException(string message) : base(message) {}
	}
}