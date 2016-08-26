using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using SystemXTransMedExamples.SystemXAPI;
using EasyNetQ;
using EasyNetQ.ConnectionString;
using EasyNetQ.Topology;
using Newtonsoft.Json;
using NUnit.Framework;
using ExchangeType = RabbitMQ.Client.ExchangeType;

namespace SystemXTransMedExamples
{
    [TestFixture]
    public class MessagingExamples
    {
        private IAdvancedBus _advancedBus;

        private IExchange _systemXExchange;
        private IQueue _systemXQueue;

        private IExchange _transMedExchange;
        private IQueue _transMedQueue;

        private readonly ConnectionConfiguration _connectionConfiguration =
            new ConnectionStringParser().Parse(
                "host=WIN-O8ULS03I5IB;virtualhost=systemx;username=systemx;password=systemx;publisherConfirms=true;product=systemxtransmedtest;persistentMessages=false;timeout=35");

        [Test]
        public void TransMedPublishesEvent()
        {
            //SystemX lytter
            _advancedBus.Consume(_systemXQueue,
                (IMessage<PatientHaveReceivedTreatmentAndCanBeInvoicedA97Event> msg, MessageReceivedInfo info) =>
                {
                    Console.WriteLine($"SystemX mottok melding med routingkey {info.RoutingKey}: {JsonConvert.SerializeObject(msg.Body)}");
                    _waitForMessage.Set();
                });

            //TransMed publiserer event
            var eventToPublish =
                new Message<PatientHaveReceivedTreatmentAndCanBeInvoicedA97Event>(new PatientHaveReceivedTreatmentAndCanBeInvoicedA97Event
                {
                    NIN = "12345678901",
                    TreatmentDescription = "Stivkrampevaksine",
                    UserId = "les-123"
                });

            //sett AMQP properties (correlationid settes av easynetq, trenger ikke reply-to siden vi ikke skal ha svar)
            eventToPublish.Properties.Expiration =
                TimeSpan.FromDays(2).TotalMilliseconds.ToString(CultureInfo.InvariantCulture); //ttl
            eventToPublish.Properties.AppId = "TransMed"; //tilsvarer sanns. "sender"

            //publish
            _advancedBus.Publish(_systemXExchange, $"event.{nameof(PatientHaveReceivedTreatmentAndCanBeInvoicedA97Event)}", true, eventToPublish);

            _waitForMessage.WaitOne(3000);
        }


        [Test]
        public void TransMedRequestsEPJ()
        {
            //SystemX lytter på request og har kode for å sende reply
            _advancedBus.Consume(_systemXQueue,
                (IMessage<GetEPJSummaryRequest> requestEPJMsg, MessageReceivedInfo info) =>
                {
                    Console.WriteLine($"SystemX mottok forespørsel om pasientjournal: {JsonConvert.SerializeObject(requestEPJMsg.Body)}");
                    var responseToTransMed = CreateResponseToTransMed(requestEPJMsg);
                    //svarer på reply-to, som er en exclusive kø som TransMed lytter midlertidig på
                    _advancedBus.Publish(Exchange.GetDefault(), requestEPJMsg.Properties.ReplyTo, false, responseToTransMed);

                });

            //TransMed gjør forespørsel og forventer svar på temporærkø
            //lag temporær exclusive svarkø
            var replyQueue = _advancedBus.QueueDeclare();
            try
            {
                //lytt temporært på svarkø
                _advancedBus.Consume(replyQueue,
                    (IMessage<GetEPJSummaryReply> epjMsg, MessageReceivedInfo info) =>
                    {
                        Console.WriteLine($"TransMed fikk pasientjournal: {JsonConvert.SerializeObject(epjMsg.Body)}");
                        _waitForMessage.Set();
                    });

                //lag request
                var epjRequest = CreateEpjRequest(replyQueue);

                //publiser request
                _advancedBus.Publish(_systemXExchange, $"command.{nameof(GetEPJSummaryRequest)}", true, epjRequest);

                Console.WriteLine(_waitForMessage.WaitOne(3000)
                    ? "Fikk svar innen valgt timeout"
                    : "Fikk ikke svar innen valgt timeout, kan velge å prøve igjen om ønskelig");
            }
            finally
            {
                _advancedBus.QueueDelete(replyQueue); //sannsynligvis ikke nødvendig
            }
        }

        private static Message<GetEPJSummaryRequest> CreateEpjRequest(IQueue responseQueue)
        {
            var requestEPJCommand =
                new Message<GetEPJSummaryRequest>(new GetEPJSummaryRequest
                {
                    NIN = "12345678901"
                });
            //sett AMQP properties (correlationid settes av easynetq)
            requestEPJCommand.Properties.Expiration = "3500";
            requestEPJCommand.Properties.AppId = "TransMed"; //tilsvarer muligens "sender"
            requestEPJCommand.Properties.ReplyTo = responseQueue.Name; //sett reply-to så systemx kan svare
            return requestEPJCommand;
        }

        private static Message<GetEPJSummaryReply> CreateResponseToTransMed(IMessage<GetEPJSummaryRequest> requestEPJMsg)
        {
            var replyToTransMed = new Message<GetEPJSummaryReply>(new GetEPJSummaryReply
            {
                NIN = requestEPJMsg.Body.NIN,
                PatientData1 = "Journaldata som må spesifiseres av prosjektet 1",
                PatientData2 = "Journaldata som må spesifiseres av prosjektet 2",
                PatientData3 = "Journaldata som må spesifiseres av prosjektet 3"
            });
            replyToTransMed.Properties.CorrelationId = requestEPJMsg.Properties.CorrelationId;
            replyToTransMed.Properties.AppId = "SystemX";
            replyToTransMed.Properties.Expiration = "10000";
            return replyToTransMed;
        }

        #region Framework

        private readonly AutoResetEvent _waitForMessage = new AutoResetEvent(false);

        [SetUp]
        public void Init()
        {
            try
            {
                _waitForMessage.Reset();
                
                //connect
                Stopwatch w = new Stopwatch();
                w.Start();
                _advancedBus = RabbitHutch.CreateBus(
                    _connectionConfiguration,
                    new AdvancedBusEventHandlers(
                        connected: OnRabbitConnectionStateChanged,
                        disconnected: OnRabbitConnectionStateChanged),
                    RegisterServices
                    ).Advanced;
                w.Stop();
                Console.WriteLine($"Oppkobling: {w.ElapsedMilliseconds} ms");

                //SystemX
                _systemXExchange = GetExchange("exchange.to.SystemX.from.TransMed");
                _systemXQueue = GetQueue("queue.to.SystemX.from.TransMed");
                _advancedBus.Bind(_systemXExchange, _systemXQueue, "#");

                _transMedExchange = GetExchange("exchange.to.TransMed.from.SystemX");
                _transMedQueue = GetQueue("queue.to.TransMed.from.SystemX");
                _advancedBus.Bind(_transMedExchange, _transMedQueue, "#");
            }
            catch (Exception e)
            {
                Console.WriteLine("Error on init: " + e);
                throw;
            }
        }

        [TearDown]
        public void Dispose()
        {
            _advancedBus.QueueDelete(_systemXQueue);
            _advancedBus.QueueDelete(_transMedQueue);
            _advancedBus.ExchangeDelete(_transMedExchange);
            _advancedBus.ExchangeDelete(_systemXExchange);
            _advancedBus.Dispose();
        }

        /// <summary>
        /// Returns the exchange or creates one if it does not exist
        /// </summary>
        /// <param name="exchangeName">The exchange name on the form exchange.[to|from].[AMKNAME]</param>
        private IExchange GetExchange(string exchangeName)
        {
            try
            {
                return _advancedBus.ExchangeDeclare(exchangeName, ExchangeType.Topic, passive: true);
            }
            catch (Exception)
            {
                Console.WriteLine("Exchange not declared, creating...");
                return _advancedBus.ExchangeDeclare(exchangeName, ExchangeType.Topic, durable: true);
            }
        }

        private IQueue GetQueue(string queueName)
        {
            try
            {
                return _advancedBus.QueueDeclare(queueName, passive: true);
            }
            catch (Exception)
            {
                Console.WriteLine($"Queue {queueName} not declared, creating it...");
                return _advancedBus.QueueDeclare(queueName);
            }
        }

        private void OnRabbitConnectionStateChanged(object sender, EventArgs e)
        {
            var bus = (IAdvancedBus) sender;
            Console.WriteLine("Connection state: " + bus.IsConnected);
        }

        private void RegisterServices(IServiceRegister obj)
        {
        }

        #endregion
    }
}