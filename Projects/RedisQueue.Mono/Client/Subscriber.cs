using System;
using ServiceStack.Redis;

namespace RedisQueue.Mono.Client
{
	public class Subscriber : IDisposable
	{
		protected readonly IRedisSubscription Subscription;
		protected readonly string Channel;

		public virtual Action<string> MessageReceived { get; set; }
		public virtual Action Subscribed { get; set; }
		public virtual Action UnSubscribed { get; set; }

		public Subscriber(IRedisSubscription subscription, string channel)
		{
			Subscription = subscription;
			Channel = channel;
		}

		public void Subscribe()
		{
			Subscription.OnMessage = (x, y) => MessageReceived(y);
			Subscription.OnSubscribe = x => Subscribed();
			Subscription.OnUnSubscribe = x => UnSubscribed();

			Subscription.SubscribeToChannels(Channel);
		}

		public void UnSubscribe()
		{
			Subscription.UnSubscribeFromChannels(Channel);
		}

		public void Dispose()
		{
			Subscription.UnSubscribeFromAllChannels();
			Subscription.Dispose();
		}
	}
}