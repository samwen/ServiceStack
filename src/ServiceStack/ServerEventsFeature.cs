﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using ServiceStack.Auth;
using ServiceStack.Host.Handlers;
using ServiceStack.Logging;
using ServiceStack.Web;

namespace ServiceStack
{
    public class ServerEventsFeature : IPlugin
    {
        public string StreamPath { get; set; }
        public string HeartbeatPath { get; set; }
        public string SubscriptionsPath { get; set; }

        public TimeSpan Timeout { get; set; }
        public TimeSpan HeartbeatInterval { get; set; }

        public Action<IEventSubscription, IRequest> OnCreated { get; set; }
        public Action<IEventSubscription> OnSubscribe { get; set; }
        public Action<IEventSubscription> OnUnsubscribe { get; set; }
        public bool NotifyChannelOfSubscriptions { get; set; }

        public ServerEventsFeature()
        {
            StreamPath = "/event-stream";
            HeartbeatPath = "/event-heartbeat";
            SubscriptionsPath = "/event-subscribers";

            Timeout = TimeSpan.FromSeconds(30);
            HeartbeatInterval = TimeSpan.FromSeconds(10);

            NotifyChannelOfSubscriptions = true;
        }

        public void Register(IAppHost appHost)
        {
            var broker = new MemoryServerEvents {
                Timeout = Timeout,
                OnSubscribe = OnSubscribe,
                OnUnsubscribe = OnUnsubscribe,
                NotifyChannelOfSubscriptions = NotifyChannelOfSubscriptions,
            };
            var container = appHost.GetContainer();

            if (container.TryResolve<IServerEvents>() == null)
                container.Register<IServerEvents>(broker);

            appHost.RawHttpHandlers.Add(httpReq => 
                httpReq.PathInfo.EndsWith(StreamPath)
                    ? (IHttpHandler) new ServerEventsHandler()
                    : httpReq.PathInfo.EndsWith(HeartbeatPath)
                      ? new ServerEventsHeartbeatHandler() 
                      : null);

            appHost.RegisterService(typeof(ServerEventsService), SubscriptionsPath);
        }
    }

    public class ServerEventsHandler : HttpAsyncTaskHandler
    {
        static long anonUserId;

        public override bool RunAsAsync()
        {
            return true;
        }

        public override Task ProcessRequestAsync(IRequest req, IResponse res, string operationName)
        {
            res.ContentType = MimeTypes.ServerSentEvents;
            res.AddHeader(HttpHeaders.CacheControl, "no-cache");
            res.KeepAlive = true;
            res.Flush();

            var session = req.GetSession();
            var userAuthId = session != null ? session.UserAuthId : null;
            var userId = userAuthId ?? ("-" + anonUserId);
            var displayName = (session != null ? session.DisplayName : null) 
                ?? "User" + Interlocked.Increment(ref anonUserId);

            var feature = HostContext.GetPlugin<ServerEventsFeature>();

            var now = DateTime.UtcNow;
            var subscriptionId = SessionExtensions.CreateRandomSessionId();
            var subscription = new EventSubscription(res) 
            {
                CreatedAt = now,
                LastPulseAt = now,
                Channel = req.QueryString["channel"] ?? req.OperationName,
                SubscriptionId = subscriptionId,
                UserId = userId,
                UserName = session != null ? session.UserName : null,
                DisplayName = displayName,
                SessionId = req.GetPermanentSessionId(),
                IsAuthenticated = session != null && session.IsAuthenticated,
                Meta = {
                    { "userId", userId },
                    { "displayName", displayName },
                    { AuthMetadataProvider.ProfileUrlKey, session.GetProfileUrl() ?? AuthMetadataProvider.DefaultNoProfileImgUrl },
                }
            };
            if (feature.OnCreated != null)
                feature.OnCreated(subscription, req);

            req.TryResolve<IServerEvents>().Register(subscription);

            var heartbeatUrl = req.ResolveAbsoluteUrl("~/".CombineWith(feature.HeartbeatPath))
                .AddQueryParam("from", subscriptionId);
            var privateArgs = new Dictionary<string, string>(subscription.Meta) {
                {"id", subscriptionId },
                {"heartbeatUrl", heartbeatUrl},
                {"heartbeatIntervalMs", ((long)feature.HeartbeatInterval.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) }};
            subscription.Publish("cmd.onConnect", privateArgs);

            var tcs = new TaskCompletionSource<bool>();

            subscription.OnDispose = _ => {
                try {
                    res.EndHttpHandlerRequest(skipHeaders: true);
                } catch {} 
                tcs.SetResult(true);
            };

            return tcs.Task;
        }
    }

    public class ServerEventsHeartbeatHandler : HttpAsyncTaskHandler
    {
        public override bool RunAsAsync() { return true; }

        public override Task ProcessRequestAsync(IRequest req, IResponse res, string operationName)
        {
            req.TryResolve<IServerEvents>().Pulse(req.QueryString["from"]);
            res.EndHttpHandlerRequest(skipHeaders:true);
            return EmptyTask;
        }
    }

    public class GetEventSubscribers : IReturn<List<Dictionary<string,string>>>
    {
        public string Channel { get; set; }
    }

    [DefaultRequest(typeof(GetEventSubscribers))]
    [Restrict(VisibilityTo = RequestAttributes.None)]
    public class ServerEventsService : Service
    {
        public IServerEvents ServerEvents { get; set; }

        public object Any(GetEventSubscribers request)
        {
            return ServerEvents.GetSubscriptions(request.Channel);
        }
    }

    /*
    cmd.showPopup message
    cmd.toggle$h1:first-child

    trigger.animateBox$#boxid {"opacity":".5","padding":"-=20px"}
    trigger.animateBox$.boxclass {"marginTop":"+=20px", "padding":"+=20"}

    css.color #0C0
    css.color$h1 black
    css.backgroundColor #f1f1f1
    css.backgroundColor$h1 yellow
    css.backgroundColor$#boxid red
    css.backgroundColor$.boxclass purple
    css.color$#boxid,.boxclass white
    
    document.title Hello World
    window.location http://google.com
    */

    public class EventSubscription : IEventSubscription
    {
        private static ILog Log = LogManager.GetLogger(typeof(EventSubscription));

        private readonly IResponse response;
        private long msgId;

        public EventSubscription(IResponse response)
        {
            this.response = response;
            this.Meta = new Dictionary<string, string>();
        }

        public DateTime CreatedAt { get; set; }
        public DateTime LastPulseAt { get; set; }
        public string Channel { get; set; }
        public string SubscriptionId { get; set; }
        public string UserId { get; set; }
        public string UserName { get; set; }
        public string DisplayName { get; set; }
        public string SessionId { get; set; }
        public bool IsAuthenticated { get; set; }

        public Action<IEventSubscription> OnUnsubscribe { get; set; }
        public Action<IEventSubscription> OnDispose { get; set; }

        public void Publish(string selector, object message)
        {
            try
            {
                var msg = (message != null ? message.ToJson() : "");
                var frame = "id: " + Interlocked.Increment(ref msgId) + "\n"
                          + "data: " + selector + " " + msg + "\n\n";

                lock (response)
                {
                    response.OutputStream.Write(frame);
                    response.Flush();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Error publishing notification to: " + selector, ex);
                Unsubscribe();
            }
        }

        public void Pulse()
        {
            LastPulseAt = DateTime.UtcNow;
        }

        public void Unsubscribe()
        {
            if (OnUnsubscribe != null)
                OnUnsubscribe(this);
        }

        public void Dispose()
        {
            OnUnsubscribe = null;
            try
            {
                lock (response)
                {
                    response.EndHttpHandlerRequest(skipHeaders: true);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Error ending subscription response", ex);
            }

            if (OnDispose != null)
                OnDispose(this);
        }

        public Dictionary<string, string> Meta { get; set; }
    }

    public interface IEventSubscription : IMeta, IDisposable
    {
        DateTime CreatedAt { get; set; }
        DateTime LastPulseAt { get; set; }

        string Channel { get; }
        string UserId { get; }
        string UserName { get; }
        string DisplayName { get; }
        string SessionId { get; }
        string SubscriptionId { get; }
        bool IsAuthenticated { get; set; }

        Action<IEventSubscription> OnUnsubscribe { get; set; }
        void Unsubscribe();

        void Publish(string selector, object message);
        void Pulse();
    }

    public class MemoryServerEvents : IServerEvents
    {
        private static ILog Log = LogManager.GetLogger(typeof(MemoryServerEvents));

        public static int DefaultArraySize = 2;
        public static int ReSizeMultiplier = 2;
        public static int ReSizeBuffer = 20;
        const string UnknownChannel = "*";

        public TimeSpan Timeout { get; set; }

        public Action<IEventSubscription> OnSubscribe { get; set; }
        public Action<IEventSubscription> OnUnsubscribe { get; set; }
        public bool NotifyChannelOfSubscriptions { get; set; }


        public ConcurrentDictionary<string, IEventSubscription[]> Subcriptions =
           new ConcurrentDictionary<string, IEventSubscription[]>();
        public ConcurrentDictionary<string, IEventSubscription[]> ChannelSubcriptions =
           new ConcurrentDictionary<string, IEventSubscription[]>();
        public ConcurrentDictionary<string, IEventSubscription[]> UserIdSubcriptions =
           new ConcurrentDictionary<string, IEventSubscription[]>();
        public ConcurrentDictionary<string, IEventSubscription[]> UserNameSubcriptions =
           new ConcurrentDictionary<string, IEventSubscription[]>();
        public ConcurrentDictionary<string, IEventSubscription[]> SessionSubcriptions =
           new ConcurrentDictionary<string, IEventSubscription[]>();

        public void NotifyAll(string selector, object message)
        {
            foreach (var entry in Subcriptions)
            {
                foreach (var sub in entry.Value)
                {
                    if (sub != null)
                        sub.Publish(selector, message);
                }
            }
        }

        public void NotifySubscription(string subscriptionId, string selector, object message, string channel = null)
        {
            Notify(Subcriptions, subscriptionId, selector, message, channel);
        }

        public void NotifyChannel(string channel, string selector, object message)
        {
            Notify(ChannelSubcriptions, channel, selector, message, channel);
        }

        public void NotifyUserId(string userId, string selector, object message, string channel = null)
        {
            Notify(UserIdSubcriptions, userId, selector, message, channel);
        }

        public void NotifyUserName(string userName, string selector, object message, string channel = null)
        {
            Notify(UserNameSubcriptions, userName, selector, message, channel);
        }

        public void NotifySession(string sspid, string selector, object message, string channel = null)
        {
            Notify(SessionSubcriptions, sspid, selector, message, channel);
        }

        void Notify(ConcurrentDictionary<string, IEventSubscription[]> map, string key,
            string selector, object message, string channel = null)
        {
            IEventSubscription[] subs;
            if (!map.TryGetValue(key, out subs)) return;

            var expired = new List<IEventSubscription>();
            var now = DateTime.UtcNow;

            foreach (var subscription in subs)
            {
                if (subscription != null && (channel == null || subscription.Channel == channel))
                {
                    if (now - subscription.LastPulseAt > Timeout)
                    {
                        expired.Add(subscription);
                    }
                    subscription.Publish(selector, message);
                }
            }

            foreach (var sub in expired)
            {
                sub.Unsubscribe();
            }
        }

        public void Pulse(string id)
        {
            var sub = GetSubscription(id);
            if (sub == null) return;
            sub.Pulse();
        }

        public IEventSubscription GetSubscription(string id)
        {
            if (id == null) return null;
            foreach (var subs in Subcriptions.Values)
            {
                foreach (var sub in subs)
                {
                    if (sub != null && sub.SubscriptionId == id)
                        return sub;
                }
            }
            return null;
        }

        public List<Dictionary<string, string>> GetSubscriptions(string channel=null)
        {
            var ret = new List<Dictionary<string, string>>();
            foreach (var subs in Subcriptions.Values)
            {
                foreach (var sub in subs)
                {
                    if (sub != null && (channel == null || sub.Channel == channel))
                        ret.Add(sub.Meta);
                }
            }
            return ret;
        }

        public void Register(IEventSubscription subscription)
        {
            try
            {
                lock (subscription)
                {
                    subscription.OnUnsubscribe = HandleUnsubscription;
                    RegisterSubscription(subscription, subscription.Channel ?? UnknownChannel, ChannelSubcriptions);
                    RegisterSubscription(subscription, subscription.SubscriptionId, Subcriptions);
                    RegisterSubscription(subscription, subscription.UserId, UserIdSubcriptions);
                    RegisterSubscription(subscription, subscription.UserName, UserNameSubcriptions);
                    RegisterSubscription(subscription, subscription.SessionId, SessionSubcriptions);

                    if (OnSubscribe != null)
                        OnSubscribe(subscription);
                }

                if (NotifyChannelOfSubscriptions && subscription.Channel != null)
                    NotifyChannel(subscription.Channel, "cmd.onJoin", subscription.Meta);
            }
            catch (Exception ex)
            {
                Log.Error("Register: " + ex.Message, ex);
                throw;
            }
        }

        void RegisterSubscription(IEventSubscription subscription, string key,
            ConcurrentDictionary<string, IEventSubscription[]> map)
        {
            if (key == null)
                return;

            IEventSubscription[] subs;
            if (!map.TryGetValue(key, out subs))
            {
                subs = new IEventSubscription[DefaultArraySize];
                subs[0] = subscription;
                if (map.TryAdd(key, subs))
                    return;
            }

            while (!map.TryGetValue(key, out subs));
            if (!TryAdd(subs, subscription))
            {
                IEventSubscription[] snapshot, newArray;
                do
                {
                    while (!map.TryGetValue(key, out snapshot));
                    newArray = new IEventSubscription[subs.Length * ReSizeMultiplier + ReSizeBuffer];
                    Array.Copy(snapshot, 0, newArray, 0, snapshot.Length);
                    if (!TryAdd(newArray, subscription, startIndex:snapshot.Length))
                        snapshot = null;
                } while (!map.TryUpdate(key, newArray, snapshot));
            }
        }

        private static bool TryAdd(IEventSubscription[] subs, IEventSubscription subscription, int startIndex=0)
        {
            for (int i = startIndex; i < subs.Length; i++)
            {
                if (subs[i] != null) continue;
                lock (subs)
                {
                    if (subs[i] != null) continue;
                    subs[i] = subscription;
                    return true;
                }
            }
            return false;
        }

        void UnRegisterSubscription(IEventSubscription subscription, string key,
            ConcurrentDictionary<string, IEventSubscription[]> map)
        {
            if (key == null)
                return;

            try
            {
                IEventSubscription[] subs;
                if (!map.TryGetValue(key, out subs)) return;

                for (int i = 0; i < subs.Length; i++)
                {
                    if (subs[i] != subscription) continue;
                    lock (subs)
                    {
                        if (subs[i] == subscription)
                        {
                            subs[i] = null;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("UnRegister: " + ex.Message, ex);
                throw;
            }
        }

        void HandleUnsubscription(IEventSubscription subscription)
        {
            lock (subscription)
            {
                UnRegisterSubscription(subscription, subscription.Channel ?? UnknownChannel, ChannelSubcriptions);
                UnRegisterSubscription(subscription, subscription.SubscriptionId, Subcriptions);
                UnRegisterSubscription(subscription, subscription.UserId, UserIdSubcriptions);
                UnRegisterSubscription(subscription, subscription.UserName, UserNameSubcriptions);
                UnRegisterSubscription(subscription, subscription.SessionId, SessionSubcriptions);

                if (OnUnsubscribe != null)
                    OnUnsubscribe(subscription);

                subscription.Dispose();
            }

            if (NotifyChannelOfSubscriptions && subscription.Channel != null)
                NotifyChannel(subscription.Channel, "cmd.onLeave", subscription.Meta);
        }
    }

    public interface IServerEvents
    {
        void Pulse(string id);

        IEventSubscription GetSubscription(string id);

        List<Dictionary<string, string>> GetSubscriptions(string channel = null);

        void Register(IEventSubscription subscription);

        void NotifyAll(string selector, object message);

        void NotifyChannel(string channel, string selector, object message);

        void NotifySubscription(string subscriptionId, string selector, object message, string channel = null);

        void NotifyUserId(string userId, string selector, object message, string channel = null);

        void NotifyUserName(string userName, string selector, object message, string channel = null);

        void NotifySession(string sspid, string selector, object message, string channel = null);
    }
}