using System;
using System.IO;
using System.Threading;
using log4net;
using RedisQueue.Mono.Client;
using RedisQueue.Mono.Entities;
using RedisQueue.Mono.Exceptions;
using RedisQueue.Mono.Properties;

namespace RedisQueue.Mono.Worker
{
	public class QueueMonitor
	{
		/// <summary>
		/// Log4Net logger.
		/// </summary>
		private static readonly ILog _log = LogManager.GetLogger(typeof(QueueMonitor));

		private bool _keepWalking;
		private Thread _thread;

		private AppDomain _domain;
		private Performer _marshalledRef;

		private readonly RedisQueueClient _client;

		public bool Waiting { get; private set; }

		public QueueMonitor(RedisQueueClient client)
		{
			_client = client;	
		}

		public QueueMonitor() { _client = new RedisQueueClient(); }

		public void Start()
		{
			try
			{
				if (_thread != null
						&& (_thread.ThreadState == ThreadState.Running
							|| _thread.ThreadState == ThreadState.WaitSleepJoin)) return;

				_thread = new Thread(Run) { Name = "QueueMonitor_Run" };

				_keepWalking = true;

				_thread.Start();

				_log.Info("Started monitor thread.");
				_log.DebugFormat("Thread Identifier: {0}", _thread.Name);
			}
			catch (Exception exception)
			{
				_log.Fatal("Failed to start monitor thread.", exception);
			}
		}

		public void Stop()
		{
			try
			{
				lock(this)
				{
					// Set the flag, telling the thread it's gotta wrap up.
					_keepWalking = false;

					// In case it 's wainting, signal it to stop.
					Monitor.Pulse(this);
				}

				// Join it for 20 millis.
				_thread.Join(20);

				// if it's not done by now, abort it.
				_thread.Abort();

				// fail the current task.
				_client.Fail("Service is stopping.");

				// Attempt to release the application domain, if it 's there.
				releaseAppDomain();

				_log.Info("Stopped monitor thread.");
				_log.DebugFormat("Thread Identifier: {0}", _thread.Name);

				_thread = null;
			}
			catch (Exception exception)
			{
				_log.Error("Failed to stop monitor thread.", exception);
			}

		}

		/// <summary>
		/// The main monitor run method. What it does is try to reserve a task and dispatch it in a separate 
		/// application domain. A couple of notes follow:
		/// 
		/// AppDomain load / unload policy
		/// 
		/// The current policy is pretty straightforward. As long as the monitor 
		/// finds tasks in the queue, it will re-use the application domain. As soon as the queue is empty, 
		/// it will attempt to unload the application domain.
		/// 
		/// Stuck tasks
		/// 
		/// ...will currently hang the service. Probable TODO: Monitor the AppDomain and kill / fail stuck tasks.
		/// </summary>
		protected void Run()
		{
			lock(this)
			{
				while (_keepWalking)
				{
					// attempt to get a task.
					try
					{
						// Get the current task count and keep an index to avoid
						// re-iterating any failed tasks that may be enqueued.
						var taskCount = _client.PendingTasks(Settings.Default.Queue).Count;

						for (var idx = 0; idx < taskCount; idx++)
						{
							TaskMessage task = _client.Reserve(Settings.Default.Queue);
							_log.Info("Reserved task from queue [queue:" + Settings.Default.Queue + "]");
							_log.DebugFormat("Task Parameters: {0}", task.Parameters);
							_log.DebugFormat("Task Index: {0}", idx);

							// if we have a task, then we need an AppDomain to work on. Create it if it's
							// not already there and then create the worker interface on it.
							if (_domain == null) initAppDomain();

							// Set the task's internal storage.
							_marshalledRef.TaskStorage = task.Storage;

							// Dispatch the task and collect its status.
							_marshalledRef.Perform(task.Parameters);
							var taskResult = _marshalledRef.Status;

							// Collect the task's internal storage.
							task.Storage = _marshalledRef.TaskStorage;

							// handle the different outcomes available.
							switch (taskResult.Outcome)
							{
								case Outcome.Success:
									_client.Succeed();
									_log.Info("Task succeeded.");
									break;

								case Outcome.Failure:
									_client.Fail(taskResult.Reason);
									_log.InfoFormat("Task failed. Reason: {0}", taskResult.Reason);

									if (taskResult.Data is string)
										_log.InfoFormat("Additional Information: {0}", taskResult.Data);
									break;

								case Outcome.CriticalFailure:
									_client.CriticalFail(taskResult.Reason);
									_log.InfoFormat("Task failed critically. Reason: {0}", taskResult.Reason);

									if (taskResult.Data is string)
										_log.InfoFormat("Additional Information: {0}", taskResult.Data);
									break;
							}

							// give the client some time to process any outstanding messages.
							Monitor.Wait(this, 10);
						} // end for
					} // end try
					catch (QueueIsEmptyException)
					{
						_log.DebugFormat("No tasks in queue [{0}]", new QueueName(Settings.Default.Queue).NameWhenPending);

						// The queue specified has run out of tasks. Unload the application domain to
						// conserve system resources.
						if (!releaseAppDomain())
						{
							_log.Fatal("Could not release application domain. No more tasks will be executed.");
							return;
						}
					}
					catch (Exception exception)
					{
						_log.Fatal("Fatal exception in monitor thread.", exception);
						return;
					}

					// It's been signalled to stop, so stop it.
					if (!_keepWalking) continue;

					// Sleep for a while.
					Waiting = true;
					var waitStart = DateTime.Now;
					_log.DebugFormat("Waiting for: {0} millis.", Settings.Default.MonitorSleepIntervalInMilliseconds);
					Monitor.Wait(this, Settings.Default.MonitorSleepIntervalInMilliseconds);
					var intervalWaited = DateTime.Now - waitStart;
					Waiting = false;

					// Log the reason for resuming...
					_log.Debug(
						(intervalWaited.TotalMilliseconds + 100) < Settings.Default.MonitorSleepIntervalInMilliseconds
							? "Stopped waiting. Pulse received."
							: "Stopped waiting. Interval Elapsed.");
				} //end while	
			}
		}

		private bool releaseAppDomain()
		{
			if (_domain == null) return true;

			var retries = 0;
			while(retries++ <= Settings.Default.RetriesToUnloadAppDomain)
			{
				try
				{
					_log.Info("Attempting to release application domain.");

					if (_domain != null)
					{
						_log.DebugFormat("Domain Name: {0}", _domain.FriendlyName);
						AppDomain.Unload(_domain);
					}

					_marshalledRef = null;
					_domain = null;

					_log.Info("Succeeded.");

					return true;
				}
				catch (AppDomainUnloadedException appDomainUnloadedException)
				{
					_log.InfoFormat("Could not release domain. Retrying {0} out of {1}...",
						retries, Settings.Default.RetriesToUnloadAppDomain);

					_log.Debug("Exception Information", appDomainUnloadedException);
				}
			}
				
			return false;
		}

		private void initAppDomain()
		{
			var pathToAssembly = resolvePath(Settings.Default.WorkerAssemblyPath);

			if (String.IsNullOrEmpty(pathToAssembly))
				throw new FileNotFoundException("Could not locate the file to load.", 
					Settings.Default.WorkerAssemblyPath);

			var domainSetup = new AppDomainSetup { PrivateBinPath = pathToAssembly };

			_domain = AppDomain.CreateDomain("Domain_" + Guid.NewGuid(), null, domainSetup);

			_marshalledRef = (Performer)_domain.CreateInstanceFromAndUnwrap(
				pathToAssembly, Settings.Default.WorkerClassName);

			_log.Info("Initialized application domain.");
			_log.DebugFormat("AppDomain Name: {0}", _domain.FriendlyName);
		}

		private static string resolvePath(string workerAssemblyPath)
		{
			if (File.Exists(workerAssemblyPath)) return workerAssemblyPath;

			return File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, workerAssemblyPath)) 
				? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, workerAssemblyPath) 
				: string.Empty;
		}
	}
}