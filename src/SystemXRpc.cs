using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using EasyNetQ;
using EasyNetQ.Topology;

namespace SystemXTransMedExamples
{
    public class SystemXRpc
    {
        private readonly IAdvancedBus _advancedBus;
        
        private readonly ConcurrentDictionary<string, Action<object>> _responseActions = new ConcurrentDictionary<string, Action<object>>();
        private readonly ConcurrentDictionary<Type, byte> _responseQueues = new ConcurrentDictionary<Type, byte>(); // C# does not have ConcurrentHashMap so we use just a dummy byte for value type
        private readonly object _responseQueuesAddLock = new object();
        private readonly string _queueName;

        public SystemXRpc(IAdvancedBus advancedBus)
        {
            _advancedBus = advancedBus;
            _queueName = "transmed8.reply." + Guid.NewGuid();
        }

        /// <summary>
        /// Posts a message on an exchange, and returns a response from the sender.
        /// </summary>
        /// <typeparam name="TRequest">The message type to be sent</typeparam>
        /// <typeparam name="TResponse">The message type expected in return</typeparam>
        /// <param name="request">The message to be sent</param>
        /// <param name="exchange">The exchange to send the message to.</param>
        /// <param name="routingKey">The routing key</param>
        /// <returns></returns>
        public Task<TResponse> RequestAsync<TRequest, TResponse>(TRequest request, IExchange exchange, string routingKey)
            where TRequest : class
            where TResponse : class
        {

            // Get the correlation id to use in this exchange
            var correlationId = Guid.NewGuid();
            
            // Create a task completion source so we can map a event based API into he asynchronous Task Api
            var tcs = new TaskCompletionSource<TResponse>();

            // Create a timeout system
            var timer = new Timer(state =>
            {
                ((Timer)state).Dispose();
                tcs.TrySetException(new TimeoutException($"Request timed out. CorrelationId: {correlationId.ToString()}"));
            });
            timer.Change(TimeSpan.FromSeconds(10),  TimeSpan.FromSeconds(10));

            // Add response action for when we receive the reply message
            _responseActions.TryAdd(correlationId.ToString(), message =>
            {
                timer.Dispose();
                var msg = (IMessage<TResponse>)message;
                tcs.TrySetResult(msg.Body);
            });

            // Add listeners for this type of message
            SubscribeToResponse<TResponse>();
            
            // Publish the message on the given exchange and set the reply-to property to the listening queue.
            RequestPublish(request, exchange, routingKey, _queueName, correlationId);

            // Return the awaitable task 
            return tcs.Task;
        }

        protected virtual void SubscribeToResponse<TResponse>()
            where TResponse : class
        {
            // Checks to see if we already listen to this type of response
            var responseType = typeof(TResponse);

            if (_responseQueues.ContainsKey(responseType))
            {
                return;
            }
            lock (_responseQueuesAddLock)
            {
                if (_responseQueues.ContainsKey(responseType))
                {
                    return;
                }

                // If we haven't listened to this kind of messages before, listen to messages of type TResponse for the queue.

                var queue = _advancedBus.QueueDeclare(_queueName, passive: false,durable: false,exclusive: true,autoDelete: true);

                _advancedBus.Consume<TResponse>(queue, (message, messageReceivedInfo) => Task.Factory.StartNew(() =>
                {
                    // Find the matching responseAction for this correlation id and execute it.
                    Action<object> responseAction;
                    if (_responseActions.TryRemove(message.Properties.CorrelationId, out responseAction))
                    {
                        responseAction(message);
                    }
                    else
                    {
                        // todo: replace with _logger.Warn(...)
                        Console.WriteLine("Got message with no matching correlation id");
                    }
                }));

                _responseQueues.TryAdd(responseType, 0);
            }
        }
        

        protected virtual void RequestPublish<TRequest>(TRequest request, IExchange exchange, string routingKey, string returnQueueName, Guid correlationId)
            where TRequest : class
        {
            var requestMessage = new Message<TRequest>(request)
            {
                Properties =
                {
                    ReplyTo = returnQueueName,
                    CorrelationId = correlationId.ToString(),
                    Expiration = "3500",
                    AppId = "TransMed"
                }
            };

            _advancedBus.Publish(exchange, routingKey, true, requestMessage);
        }

    }
}
