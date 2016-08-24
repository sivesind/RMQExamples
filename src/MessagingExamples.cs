using System;
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
                "host=larse3,les-laptop;virtualhost=systemx;username=systemx;password=systemx;publisherConfirms=true;product=systemxtransmedtest;persistentMessages=false;timeout=35");

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
            eventToPublish.Properties.AppId = "TransMed"; //tilsvarer muligens "sender"

            //publish med routingkey
            _advancedBus.Publish(_systemXExchange, $"event.{nameof(PatientHaveReceivedTreatmentAndCanBeInvoicedA97Event)}", true, eventToPublish);

            _waitForMessage.WaitOne(3000);
        }


        [Test]
        public void TransMedRequestsEPJ()
        {
            //SystemX lytter på command og svarer
            _advancedBus.Consume(_systemXQueue,
                (IMessage<GetEPJSummaryCommand> requestEPJMsg, MessageReceivedInfo info) =>
                {
                    Console.WriteLine($"SystemX mottok forespørsel om pasientjournal: {JsonConvert.SerializeObject(requestEPJMsg.Body)}");
                    var responseToTransMed = new Message<GetEPJSummaryCommandResponse>(new GetEPJSummaryCommandResponse
                    {
                        NIN = requestEPJMsg.Body.NIN,
                        PatientData1 = "Journaldata som må spesifiseres av prosjektet 1",
                        PatientData2 = "Journaldata som må spesifiseres av prosjektet 2",
                        PatientData3 = "Journaldata som må spesifiseres av prosjektet 3"

                    });
                    responseToTransMed.Properties.CorrelationId = requestEPJMsg.Properties.CorrelationId;
                    responseToTransMed.Properties.AppId = "SystemX";
                    responseToTransMed.Properties.Expiration = "5000";

                    //svarer på reply-to, som er en exclusive kø som transmed lytter midlertidig på
                    _advancedBus.Publish(Exchange.GetDefault(), requestEPJMsg.Properties.ReplyTo, false, responseToTransMed);

                });

            //TransMed gjør forespørsel og forventer svar på temporærkø
            //lytt på temporær svarkø
            var responseQueue = _advancedBus.QueueDeclare();
            try
            {
                _advancedBus.Consume(responseQueue,
                    (IMessage<GetEPJSummaryCommandResponse> epjMsg, MessageReceivedInfo info) =>
                    {
                        Console.WriteLine($"TransMed fikk pasientjournal: {JsonConvert.SerializeObject(epjMsg.Body)}");
                        _waitForMessage.Set();
                    });


                var requestEPJCommand =
                    new Message<GetEPJSummaryCommand>(new GetEPJSummaryCommand
                    {
                        NIN = "12345678901"
                    });
                //sett AMQP properties (correlationid settes av easynetq)
                requestEPJCommand.Properties.Expiration = "5000";
                requestEPJCommand.Properties.AppId = "TransMed"; //tilsvarer muligens "sender"
                requestEPJCommand.Properties.ReplyTo = responseQueue.Name; //sett reply-to så systemx kan svare

                //publish med routingkey
                _advancedBus.Publish(_systemXExchange, $"command.{nameof(GetEPJSummaryCommand)}", true, requestEPJCommand);

                _waitForMessage.WaitOne(10000);
            }
            finally
            {
                _advancedBus.QueueDelete(responseQueue);
            }
        }


        #region Framework

        private readonly AutoResetEvent _waitForMessage = new AutoResetEvent(false);

        [SetUp]
        public void EachTest()
        {
            _waitForMessage.Reset();
        }

        [OneTimeSetUp]
        public void InitOnce()
        {
            try
            {
                //connect
                _advancedBus = RabbitHutch.CreateBus(
                    _connectionConfiguration,
                    new AdvancedBusEventHandlers(
                        connected: OnRabbitConnectionStateChanged,
                        disconnected: OnRabbitConnectionStateChanged),
                    RegisterServices
                    ).Advanced;

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

        [OneTimeTearDown]
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