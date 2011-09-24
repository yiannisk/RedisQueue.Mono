using System;
using System.Threading;
using log4net;
using RedisQueue.Mono.Client;
using RedisQueue.Mono.Properties;
using ServiceStack.Redis;

namespace RedisQueue.Mono.Worker
{
	public class QueueSubscriber
	{
		/// <summary>
		/// Log4Net logger.
		/// </summary>
		private static readonly ILog _log = LogManager.GetLogger(typeof(QueueSubscriber));

		private Thread _thread;
		public RedisQueueClient Client { get; private set; }

		public QueueName Queue { get; protected set; }
		protected IRedisSubscription Subscription;

		public virtual Action<string> MessageReceived { get; set; }

		public QueueSubscriber()
		{
			Queue = new QueueName(Settings.Default.Queue);
			Client = new RedisQueueClient();
		}

		public void Start(Action callback)
		{
			Start();
			callback();
		}

		public void Start()
		{
			try
			{
				if (_thread != null 
						&& (_thread.ThreadState == ThreadState.Running
							|| _thread.ThreadState == ThreadState.WaitSleepJoin)) return;

				Subscription = Client.GetSubscription();
				Subscription.OnMessage = (x, y) =>
				{
					_log.DebugFormat("Message received from [{0}]: {1}", x, y);

					if (MessageReceived == null)
					{
						_log.Debug("No recipients for message.");
						return;
					}

					_log.Debug("Forwarding message.");
					MessageReceived(y);
				};

				Subscription.OnSubscribe = x =>
				{
					_log.InfoFormat("Subscribed to [{0}].", x);
				};

				Subscription.OnUnSubscribe = x =>
				{
					_log.InfoFormat("UnSubscribed from [{0}].", x);
				};

				_thread = new Thread(Run) { Name = "QueueSubscriber_Run" };
				_thread.Start();

				_log.Info("Started subscriber thread.");
				_log.DebugFormat("Thread Identifier: {0}", _thread.Name);
			}
			catch (Exception exception)
			{
				_log.Fatal("Failed to start subscriber thread.", exception);
			}
		}

		public void Run()
		{
			try
			{
				_log.Info("Waiting for messages...");
				Subscription.SubscribeToChannels(new QueueName(Settings.Default.Queue).ChannelName);
			}
			catch {}
		}

		public void Stop(Action callback)
		{
			Stop();
			callback();
		}

		public void Stop()
		{
			try
			{
				Client.Dispose();
				Client = new RedisQueueClient();
				_log.Info("Stopped subscriber thread.");
				_log.DebugFormat("Thread Identifier: {0}", _thread.Name);
			}
			catch (Exception exception)
			{
				_log.Error("Failed to stop subscriber thread.", exception);
			}
		}
	}
}