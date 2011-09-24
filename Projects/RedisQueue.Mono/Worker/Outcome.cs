namespace RedisQueue.Mono.Worker
{
	public enum Outcome
	{
		NotStarted,
		Success,
		Failure,
		CriticalFailure
	}
}