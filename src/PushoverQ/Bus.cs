﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Practices.TransientFaultHandling;
using Microsoft.ServiceBus;
using Microsoft.ServiceBus.Messaging;
using PushoverQ.Configuration;
using PushoverQ.ContextConfiguration;
using PushoverQ.SendConfiguration;

namespace PushoverQ
{
    public sealed class Bus : IBus, IDisposable
    {
        private readonly BusSettings _settings;
        private readonly NamespaceManager _namespaceManager;
        private readonly MessagingFactory _messagingFactory;
        private readonly SemaphoreSlim _publishSemaphore;
        private static readonly RetryPolicy RetryPolicy = new RetryPolicy<TransientErrorDetectionStrategy>(
            new ExponentialBackoff("Retry exponentially", int.MaxValue, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(30), true));

        public static IBus CreateBus(Action<BusConfigurator> configure)
        {
            var configurator = new BusConfigurator();
            configure(configurator);

            return new Bus(configurator.Settings);
        }

        private Bus(BusSettings settings)
        {
            _settings = settings;

            _messagingFactory = MessagingFactory.CreateFromConnectionString(settings.ConnectionString);
            _namespaceManager = NamespaceManager.CreateFromConnectionString(settings.ConnectionString);
            _publishSemaphore = new SemaphoreSlim(settings.MaxMessagesInFlight);
        }

        #region Publish

        #region Publish wrapper overloads
        public Task Publish(object message)
        {
            return Publish(message, Timeout.InfiniteTimeSpan, CancellationToken.None);
        }

        public Task Publish(object message, TimeSpan timeout)
        {
            return Publish(message, timeout, CancellationToken.None);
        }

        public Task Publish(object message, CancellationToken token)
        {
            return Publish(message, Timeout.InfiniteTimeSpan, token);
        }
        #endregion

        public Task Publish(object message, TimeSpan timeout, CancellationToken token)
        {
            return Publish(message, null, timeout, token);
        }

        #region Publish w/ configure wrapper overloads
        public Task Publish(object message, Action<ISendConfigurator> configure)
        {
            return Publish(message, configure, Timeout.InfiniteTimeSpan, CancellationToken.None);
        }

        public Task Publish(object message, Action<ISendConfigurator> configure, TimeSpan timeout)
        {
            return Publish(message, configure, timeout, CancellationToken.None);
        }

        public Task Publish(object message, Action<ISendConfigurator> configure, CancellationToken token)
        {
            return Publish(message, configure, Timeout.InfiniteTimeSpan, token);
        }
        #endregion

        public async Task Publish(object message, Action<ISendConfigurator> configure, TimeSpan timeout, CancellationToken token)
        {
            var configurator = new SendConfigurator();
            configurator.ToTopic(_settings.TypeToTopicName(message.GetType()));
            if(configure != null) configure(configurator);
            var sendSettings = configurator.SendSettings;

            if(sendSettings.NeedsConfirmation)
                throw new NotImplementedException();

            await _publishSemaphore.WaitAsync(timeout, token);

            try
            {
                var sender = await RetryPolicy.ExecuteAsync(() => Task<MessageSender>.Factory.FromAsync(_messagingFactory.BeginCreateMessageSender, _messagingFactory.EndCreateMessageSender, sendSettings.Topic, null)
                       .NaiveTimeoutAndCancellation(timeout, token));

                await RetryPolicy.ExecuteAsync(async () =>
                                                         {
                                                             using (var ms = new MemoryStream())
                                                             {
                                                                 _settings.Serializer.Serialize(message, ms);

                                                                 ms.Seek(0, SeekOrigin.Begin);
                                                                 var brokeredMessage = new BrokeredMessage(ms, false);
                                                                 if (sendSettings.VisibleAfter != null)
                                                                     brokeredMessage.ScheduledEnqueueTimeUtc = sendSettings.VisibleAfter.Value;
                                                                 if (sendSettings.Expiration != null)
                                                                     brokeredMessage.TimeToLive = sendSettings.Expiration.Value;

                                                                 await Task.Factory.FromAsync(sender.BeginSend, sender.EndSend, brokeredMessage, null)
                                                                     .NaiveTimeoutAndCancellation(timeout, token);
                                                             }
                                                         }, token);

                await RetryPolicy.ExecuteAsync(() => Task.Factory.FromAsync(sender.BeginClose, sender.EndClose, null));
            }
            finally
            {
                _publishSemaphore.Release();
            }
        }

        #region Publish<T> wrapper overloads
        public Task<T> Publish<T>(object message)
        {
            return Publish<T>(message, Timeout.InfiniteTimeSpan, CancellationToken.None);            
        }

        public Task<T> Publish<T>(object message, TimeSpan timeout)
        {
            return Publish<T>(message, timeout, CancellationToken.None);
        }

        public Task<T> Publish<T>(object message, CancellationToken token)
        {
            return Publish<T>(message, Timeout.InfiniteTimeSpan, token);
        }
        #endregion

        public Task<T> Publish<T>(object message, TimeSpan timeout, CancellationToken token)
        {
            return Publish<T>(message, null, timeout, token);
        }

        public Task<T> Publish<T>(object message, Action<ISendConfigurator> configure)
        {
            return Publish<T>(message, configure, Timeout.InfiniteTimeSpan, CancellationToken.None);
        }

        public Task<T> Publish<T>(object message, Action<ISendConfigurator> configure, TimeSpan timeout)
        {
            return Publish<T>(message, configure, timeout, CancellationToken.None);
        }

        public Task<T> Publish<T>(object message, Action<ISendConfigurator> configure, CancellationToken token)
        {
            return Publish<T>(message, configure, Timeout.InfiniteTimeSpan, token);
        }

        public Task<T> Publish<T>(object message, Action<ISendConfigurator> configure, TimeSpan timeout, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        #endregion

        #region Subscribe

        public Task<IDisposable> Subscribe<T>(Func<T, Task> handler)
        {
            return Subscribe(_settings.CompeteSubscriptionName, handler);
        }

        public Task<IDisposable> Subscribe<T>(string subscription, Func<T, Task> handler)
        {
            return Subscribe(_settings.TypeToTopicName(typeof(T)), subscription, handler);
        }

        public Task<IDisposable> Subscribe<T>(string topic, string subscription, Func<T, Task> handler)
        {
            throw new NotImplementedException();
        }

        public Task<IDisposable> Subscribe<T>(Func<T, ISendSettings, Task> handler)
        {
            return Subscribe(_settings.CompeteSubscriptionName, handler);
        }

        public Task<IDisposable> Subscribe<T>(string subscription, Func<T, ISendSettings, Task> handler)
        {
            return Subscribe(_settings.TypeToTopicName(typeof(T)), subscription, handler);
        }

        public Task<IDisposable> Subscribe<T>(string topic, string subscription, Func<T, ISendSettings, Task> handler)
        {
            throw new NotImplementedException();
        }

        public Task<IDisposable> Subscribe<T>(Consumes<T>.All consumer)
        {
            return Subscribe(_settings.CompeteSubscriptionName, consumer);
        }

        public Task<IDisposable> Subscribe<T>(string subscription, Consumes<T>.All consumer)
        {
            return Subscribe(_settings.TypeToTopicName(typeof(T)), subscription, consumer);
        }

        public Task<IDisposable> Subscribe<T>(string topic, string subscription, Consumes<T>.All consumer)
        {
            throw new NotImplementedException();
        }

        public Task<IDisposable> Subscribe<T>(Consumes<T>.Envelope consumer)
        {
            return Subscribe(_settings.CompeteSubscriptionName, consumer);
        }

        public Task<IDisposable> Subscribe<T>(string subscription, Consumes<T>.Envelope consumer)
        {
            return Subscribe(_settings.TypeToTopicName(typeof(T)), subscription, consumer);
        }

        public Task<IDisposable> Subscribe<T>(string topic, string subscription, Consumes<T>.Envelope consumer)
        {
            throw new NotImplementedException();
        }

        public Task<IDisposable> Subscribe<T>(Func<Consumes<T>.All> consumerFactory)
        {
            return Subscribe(_settings.CompeteSubscriptionName, consumerFactory);
        }

        public Task<IDisposable> Subscribe<T>(string subscription, Func<Consumes<T>.All> consumerFactory)
        {
            return Subscribe(_settings.TypeToTopicName(typeof(T)), subscription, consumerFactory);
        }

        public Task<IDisposable> Subscribe<T>(string topic, string subscription, Func<Consumes<T>.All> consumerFactory)
        {
            throw new NotImplementedException();
        }

        public Task<IDisposable> Subscribe<T>(Func<Consumes<T>.Envelope> consumerFactory)
        {
            return Subscribe(_settings.CompeteSubscriptionName, consumerFactory);
        }

        public Task<IDisposable> Subscribe<T>(string subscription, Func<Consumes<T>.Envelope> consumerFactory)
        {
            return Subscribe(_settings.TypeToTopicName(typeof (T)), subscription, consumerFactory);
        }

        public Task<IDisposable> Subscribe<T>(string topic, string subscription, Func<Consumes<T>.Envelope> consumerFactory)
        {
            throw new NotImplementedException();
        }

        #endregion

        public void Dispose(bool disposing)
        {
            
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~Bus()
        {
            Dispose(false);            
        }
    }
}
