using System;
using System.Collections.Generic;
using System.Linq;
using log4net;
using RedisQueue.Mono.Entities;
using RedisQueue.Mono.Exceptions;
using RedisQueue.Mono.Properties;
using ServiceStack.Redis;
using ServiceStack.Redis.Generic;

namespace RedisQueue.Mono.Client
{
	/// <summary>
	/// Redis client that presents a task management interface. Consumers can get and dispatch tasks.
	/// </summary>
	public class RedisQueueClient : IDisposable
	{
		/// <summary>
		/// Log4Net logger.
		/// </summary>
		private static readonly ILog _log = LogManager.GetLogger(typeof (RedisQueueClient));

		/// <summary>
		/// Typewise Redis client. Used in Queue operations.
		/// </summary>
		private readonly IRedisTypedClient<TaskMessage> _taskMessageClient;

		/// <summary>
		/// Generic Redis Client. Used in message subscriptions.
		/// </summary>
		private readonly RedisClient _client;

		/// <summary>
		/// The internal state of the client. Either Ready or TaskReserved.
		/// If Ready, can reserve and enqueue tasks.
		/// If TaskReserved, can only enqueue tasks.
		/// </summary>
		private RedisQueueState _state;

		/// <summary>
		/// The task currently reserved, if any.
		/// </summary>
		public TaskMessage CurrentTask { get; private set; }

		/// <summary>
		/// Ctor. Obtains and stores a reference to a typed Redis client.
		/// </summary>
		public RedisQueueClient()
		{
			_client = new RedisClient(Settings.Default.RedisHost, Settings.Default.RedisPort);
			_taskMessageClient = _client.GetTypedClient<TaskMessage>();

			_log.Info("Connected to Redis.");
			_log.DebugFormat("Connection Properties: {0}:{1}",
				Settings.Default.RedisHost, Settings.Default.RedisPort);
		}

		/// <summary>
		/// Adds a task to the queue specified in the task itself and sends a message to
		/// the associated channel, notifying any listeners.
		/// </summary>
		/// <param name="t"></param>
		public void Enqueue(TaskMessage t)
		{
			if (string.IsNullOrEmpty(t.Queue))
				throw new NoQueueSpecifiedException(	
					"TaskMessage.Queue is empty or null. Cannot append task to queue.");

			var queue = new QueueName(t.Queue);
			
			_taskMessageClient.Lists[queue.NameWhenPending].Add(t);
			SendMessage(QueueSystemMessages.TaskAvailable.ToString(), queue);

			_log.Info("New task in [" + queue.NameWhenPending + "]");
			_log.DebugFormat("Task Parameters: {0}", t.Parameters);
		}

		/// <summary>
		/// Obtains the first available task from the specified queue, and changes the client's
		/// state into TaskReserved. In that state, no new tasks can be reserved until the 
		/// task's outcome is evident.
		/// </summary>
		/// <param name="queue"></param>
		/// <returns></returns>
		public TaskMessage Reserve(QueueName queue)
		{
			if (_state == RedisQueueState.TaskReserved)
				throw new TaskAlreadyReservedException("Cannot reserve multiple tasks at once.");

			if (string.IsNullOrEmpty(queue.ToString()))
				throw new NoQueueSpecifiedException(
					"Parameter <queue> is empty or null. Cannot retrieve task for no queue.");

			if (_taskMessageClient.Lists[queue.NameWhenPending].Count == 0)
				throw new QueueIsEmptyException("No tasks available in specified queue.");

			CurrentTask = _taskMessageClient.Lists[queue.NameWhenPending].RemoveStart();
			_state = RedisQueueState.TaskReserved;

			_log.Info("Reserved task from [" + queue.NameWhenPending + "]");
			_log.DebugFormat("Task Parameters: {0}", CurrentTask.Parameters);

			return CurrentTask;
		}

		/// <summary>
		/// Obtains the first available task from the specified queue, and changes the client's
		/// state into TaskReserved. In that state, no new tasks can be reserved until the 
		/// task's outcome is evident.
		/// </summary>
		/// <param name="queue"></param>
		/// <returns></returns>
		public TaskMessage Reserve(string queue) { return Reserve(new QueueName(queue)); }

		/// <summary>
		/// Disposable impl. Handles cleanup and marks any uncompleted tasks as failed.
		/// </summary>
		public void Dispose()
		{
			if (_state == RedisQueueState.TaskReserved)
				// the consumer did not assert the task was succesfully completed,
				// or something sinister has happened. Add the task to the failed tasks list.
				Fail("RedisClient disposed prior to the task being completed.");

			_taskMessageClient.Dispose();
			_client.Dispose();

			_log.Info("Disconnected from Redis.");
			_log.DebugFormat("Client state on disconnect: {0}", _state);
		}

		/// <summary>
		/// Marks the currently reserved task as failed.
		/// </summary>
		/// <param name="reason"></param>
		/// <exception cref="NoTaskReservedException">No task has been reserved. Future failure not yet 
		/// supported.</exception>
		/// <exception cref="InvalidStateException">CurrentTask is null. Something's seriously off.</exception>
		public void Fail(string reason)
		{
			if (_state != RedisQueueState.TaskReserved)
				throw new NoTaskReservedException("No task has been reserved. Future failure not yet supported.");

			if (CurrentTask == null)
				throw new InvalidStateException("CurrentTask is null. Something's seriously off.");

			CurrentTask.Status = TaskStatus.Failed;
			CurrentTask.Reason = reason;
			CurrentTask.UpdatedOn = DateTime.Now;

			var queue = new QueueName(CurrentTask.Queue);
			var targetQueue = queue.NameWhenFailed;

			// If we support task cycling and the task should indeed be recycled, 
			// place it to the end of the pending queue again.
			if (Settings.Default.TaskRecycling)
				if (Settings.Default.MaxTaskRetries == 0 || CurrentTask.Retries < Settings.Default.MaxTaskRetries)
				{
					targetQueue = queue.NameWhenPending;

					// If we support cycling, and we have a limit to how many times tasks
					// can be recycled, keep track of the number of retries. Do not remove this
					// lightly, since it can produce arithmetic overflows (<TaskMessage>.Retries is
					// an int, after all).
					if (CurrentTask.Retries < Settings.Default.MaxTaskRetries) CurrentTask.Retries++;
				}

			_taskMessageClient.Lists[targetQueue].Add(CurrentTask);

			_log.Info("A task has failed. Moving to [" + targetQueue + "].");
			_log.DebugFormat("Task Parameters: {0}", CurrentTask.Parameters);

			_state = RedisQueueState.Ready;
		}

		/// <summary>
		/// Marks the current task as failed, and regardless of the cycling setting, places it 
		/// in the :failed queue.
		/// </summary>
		/// <param name="reason"></param>
		public void CriticalFail(string reason)
		{
			if (_state != RedisQueueState.TaskReserved)
				throw new NoTaskReservedException("No task has been reserved. Future failure not yet supported.");

			if (CurrentTask == null)
				throw new InvalidStateException("CurrentTask is null. Something's seriously off.");

			CurrentTask.Status = TaskStatus.Failed;
			CurrentTask.Reason = reason;
			CurrentTask.UpdatedOn = DateTime.Now;

			var targetQueue = new QueueName(CurrentTask.Queue).NameWhenFailed;

			_taskMessageClient.Lists[targetQueue].Add(CurrentTask);

			_log.Info("A task has failed. Moving to [" + targetQueue + "].");
			_log.DebugFormat("Task Parameters: {0}", CurrentTask.Parameters);

			_state = RedisQueueState.Ready;
		}

		/// <summary>
		/// Marks the currently reserved task as succeeded.
		/// </summary>
		/// <exception cref="InvalidStateException">CurrentTask is null. Something's seriously off.</exception>
		/// <exception cref="NoTaskReservedException">No task has been reserved. Future success not yet 
		/// supported.</exception>
		public void Succeed()
		{
			if (_state != RedisQueueState.TaskReserved)
				throw new NoTaskReservedException(
					"No task has been reserved. Future success not yet supported.");

			if (CurrentTask == null)
				throw new InvalidStateException("CurrentTask is null. Something's seriously off.");

			CurrentTask.Status = TaskStatus.Succeeded;
			CurrentTask.UpdatedOn = DateTime.Now;

			if (!Settings.Default.PurgeSuccessfulTasks)
			{
				var queue = new QueueName(CurrentTask.Queue);
				_taskMessageClient.Lists[queue.NameWhenSucceeded].Add(CurrentTask);
				_log.Info("A task has succeeded. Moving to [" + queue.NameWhenSucceeded + "].");
			}
			else
			{
				_log.Info("A task has succeeded and will be dropped.");
			}

			_log.DebugFormat("Task Parameters: {0}", CurrentTask.Parameters);

			_state = RedisQueueState.Ready;
		}

		/// <summary>
		/// Returns all pending tasks.
		/// </summary>
		/// <param name="queue"></param>
		/// <returns></returns>
		[Obsolete("Driven out by inconsistent naming. Use PendingTasks(...) instead.")]
		public IList<TaskMessage> All(QueueName queue)
		{
			if (string.IsNullOrEmpty(queue.ToString()))
				throw new NoQueueSpecifiedException(
					"Parameter <queue> is empty or null. Cannot retrieve task for no queue.");

			return _taskMessageClient.Lists[queue.NameWhenPending];
		}


		/// <summary>
		/// Returns all pending tasks.
		/// </summary>
		/// <param name="queue"></param>
		/// <returns></returns>
		[Obsolete("Driven out by inconsistent naming. Use PendingTasks(...) instead.")]
		public IList<TaskMessage> All(string queue) { return All(new QueueName(queue)); }

		/// <summary>
		/// Returns all pending tasks.
		/// </summary>
		/// <param name="queue"></param>
		/// <returns></returns>
		public IList<TaskMessage> PendingTasks(QueueName queue)
		{
			if (string.IsNullOrEmpty(queue.ToString()))
				throw new NoQueueSpecifiedException(
					"Parameter <queue> is empty or null. Cannot retrieve task for no queue.");

			return _taskMessageClient.Lists[queue.NameWhenPending];
		}

		/// <summary>
		/// Returns all pending tasks.
		/// </summary>
		/// <param name="queue"></param>
		/// <returns></returns>
		public IList<TaskMessage> PendingTasks(string queue)
		{
			return PendingTasks(new QueueName(queue));
		}

		/// <summary>
		/// Returns all failed tasks.
		/// </summary>
		/// <param name="queue"></param>
		/// <returns></returns>
		public IList<TaskMessage> FailedTasks(QueueName queue)
		{
			if (string.IsNullOrEmpty(queue.ToString()))
				throw new NoQueueSpecifiedException(
					"Parameter <queue> is empty or null. Cannot retrieve task for no queue.");

			return _taskMessageClient.Lists[queue.NameWhenFailed];
		}

		/// <summary>
		/// Returns all failed tasks.
		/// </summary>
		/// <param name="queue"></param>
		/// <returns></returns>
		public IList<TaskMessage> FailedTasks(string queue)
		{
			return FailedTasks(new QueueName(queue));
		}

		/// <summary>
		/// Returns all tasks.
		/// </summary>
		/// <param name="queue"></param>
		/// <returns></returns>
		public IList<TaskMessage> AllTasks(QueueName queue)
		{
			if (string.IsNullOrEmpty(queue.ToString()))
				throw new NoQueueSpecifiedException(
					"Parameter <queue> is empty or null. Cannot retrieve task for no queue.");

			return _taskMessageClient.Lists[queue.NameWhenFailed]
				.Union(_taskMessageClient.Lists[queue.NameWhenSucceeded])
				.Union(_taskMessageClient.Lists[queue.NameWhenPending])
				.ToList();
		}

		/// <summary>
		/// Returns all tasks.
		/// </summary>
		/// <param name="queue"></param>
		/// <returns></returns>
		public IList<TaskMessage> AllTasks(string queue)
		{
			return AllTasks(new QueueName(queue));
		}

		/// <summary>
		/// Returns all succeeded tasks.
		/// </summary>
		/// <param name="queue"></param>
		/// <returns></returns>
		public IList<TaskMessage> SucceededTasks(QueueName queue)
		{
			if (string.IsNullOrEmpty(queue.ToString()))
				throw new NoQueueSpecifiedException(
					"Parameter <queue> is empty or null. Cannot retrieve task for no queue.");

			return _taskMessageClient.Lists[queue.NameWhenSucceeded];
		}

		/// <summary>
		/// Returns all succeeded tasks.
		/// </summary>
		/// <param name="queue"></param>
		/// <returns></returns>
		public IList<TaskMessage> SucceededTasks(string queue)
		{
			return SucceededTasks(new QueueName(queue));
		}

		/// <summary>
		/// Returns a list of all queues found.
		/// </summary>
		/// <returns></returns>
		public IList<string> AllQueues()
		{
			var allKeys = _taskMessageClient.GetAllKeys().Where(x => x.StartsWith("queue:"));
			
			var queues = new HashSet<string>();

			foreach (var key in allKeys)
				queues.Add(key.Split(':')[1]);

			return queues.ToList();
		}

		/// <summary>
		/// Returns an empty channel subscription.
		/// </summary>
		/// <returns></returns>
		public IRedisSubscription GetSubscription()
		{
			return _client.CreateSubscription();
		}

		/// <summary>
		/// Sends a message to the channel associated with a specific queue.
		/// </summary>
		/// <param name="message"></param>
		/// <param name="queue"></param>
		public void SendMessage(string message, QueueName queue)
		{
			_client.PublishMessage(queue.ChannelName, message);
		}

		/// <summary>
		/// Sends a message to the channel associated with a specific queue.
		/// </summary>
		/// <param name="message"></param>
		/// <param name="queue"></param>
		public void SendMessage(string message, string queue) 
		{
			SendMessage(message, new QueueName(queue));
		}

		/// <summary>
		/// Removes a task with a specific task identifier.
		/// </summary>
		/// <param name="task"></param>
		public void RemoveTask(TaskMessage task)
		{
			var queueName = new QueueName(task.Queue);
			removeTask(queueName.NameWhenPending, task);
			removeTask(queueName.NameWhenFailed, task);
			removeTask(queueName.NameWhenSucceeded, task);
		}

		private void removeTask(string queue, TaskMessage task)
		{
			var results = new List<TaskMessage>();
			TaskMessage t;
			while((t = _taskMessageClient.Lists[queue].RemoveStart()) != null)
			{
				if (t.Equals(task)) continue;
				results.Add(t);
			}

			if (results.Count > 0)
				_taskMessageClient.Lists[queue].AddRange(results);
		}
	}
}