using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Timers;
using Rebus.Bus;
using Rebus.Log4Net;
using Rebus.Logging;
using Rebus.Messages;
using Rebus.Newtonsoft.JsonNET;
using Rebus.Transports.Msmq;
using log4net;
using System.Linq;
using ILog = log4net.ILog;

namespace Rebus.Timeout
{
    public class TimeoutService : IHandleMessages<TimeoutRequest>, IActivateHandlers
    {
        static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        const string InputQueueName = "rebus.timeout";
        
        readonly IBus bus;
        readonly object listLock = new object();
        readonly List<Timeout> timeouts = new List<Timeout>();
        readonly Timer timer = new Timer();
        readonly RebusBus rebusBus;

        public TimeoutService()
        {
            var msmqMessageQueue = new MsmqMessageQueue(InputQueueName);

            RebusLoggerFactory.Current = new Log4NetLoggerFactory();
            rebusBus = new RebusBus(this, msmqMessageQueue, msmqMessageQueue, null, null, null, new JsonMessageSerializer(), new TrivialPipelineInspector());
            bus = rebusBus;

            timer.Interval = 300;
            timer.Elapsed += CheckCallbacks;
        }

        public string InputQueue
        {
            get { return InputQueueName; }
        }

        public IEnumerable<IHandleMessages<T>> GetHandlerInstancesFor<T>()
        {
            if (typeof(T) == typeof(TimeoutRequest))
            {
                return new[] {(IHandleMessages<T>) this};
            }

            if (typeof(T) == typeof(object))
            {
                return new IHandleMessages<T>[0];
            }

            throw new InvalidOperationException(string.Format("Someone took the chance and sent a message of type {0} to me.", typeof(T)));
        }

        public IEnumerable<IMessageModule> GetMessageModules()
        {
            return new IMessageModule[0];
        }

        public void Release(IEnumerable handlerInstances)
        {
        }

        public void Start()
        {
            Log.Info("Starting bus");
            rebusBus.Start(1);
            Log.Info("Starting inner timer");
            timer.Start();
        }

        public void Stop()
        {
            Log.Info("Stopping inner timer");
            timer.Stop();
            Log.Info("Disposing bus");
            rebusBus.Dispose();
        }

        public void Handle(TimeoutRequest message)
        {
            var currentMessageContext = MessageContext.GetCurrent();

            lock (listLock)
            {
                var newTimeout = new Timeout
                                     {
                                         CorrelationId = message.CorrelationId,
                                         ReplyTo = currentMessageContext.ReturnAddress,
                                         TimeToReturn = DateTime.UtcNow + message.Timeout,
                                     };

                timeouts.Add(newTimeout);

                Log.InfoFormat("Added new timeout: {0}", newTimeout);
            }
        }

        void CheckCallbacks(object sender, ElapsedEventArgs e)
        {
            lock(listLock)
            {
                var dueTimeouts = timeouts.ToList().Where(t => t.TimeToReturn >= DateTime.UtcNow);

                foreach (var timeout in dueTimeouts)
                {
                    Log.InfoFormat("Timeout!: {0} -> {1}", timeout.CorrelationId, timeout.ReplyTo);

                    bus.Send(timeout.ReplyTo,
                             new TimeoutReply
                                 {
                                     CorrelationId = timeout.CorrelationId,
                                     DueTime = timeout.TimeToReturn
                                 });

                    timeouts.Remove(timeout);
                }
            }
        }
    }
}