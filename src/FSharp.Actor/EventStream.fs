﻿namespace FSharp.Actor

open System
open System.Threading
open System.Collections.Generic
#if INTERACTIVE
open FSharp.Actor
#endif

type Event = {
    Payload : obj
    PayloadType : string
}

type IEventStream = 
    inherit IDisposable
    abstract Publish : 'a -> unit
    abstract Publish : string * 'a -> unit
    abstract Subscribe<'a> : ('a -> unit) -> unit
    abstract Subscribe : string * (Event -> unit) -> unit
    abstract Unsubscribe<'a> : unit -> unit
    abstract Unsubscribe : string -> unit

type DefaultEventStream(id, logger:Log.ILogger) = 
    let cts = new CancellationTokenSource()
    let metricContext = Metrics.createContext (sprintf "eventstream/%s" id)
    let subscriberCount = Metrics.createCounter metricContext (metricContext.Key + "/subscribers")
    let logger = new Log.Logger("eventStream", logger)   
    let mutable mailbox = new DefaultMailbox<Event>(metricContext.Key + "/mailbox") :> IMailbox<_>
    let mutable subscriptions = new Dictionary<string, (Event -> unit)>()
    let rec worker() =
        async {
            let! event = mailbox.Receive(Timeout.Infinite)
            match subscriptions.TryGetValue(event.PayloadType) with
            | true, f -> 
                try 
                    f(event) 
                with e -> logger.Error(sprintf "Error occured handling event %A" event, exn = e)
            | false, _ -> ()
            return! worker()
        }

    let addSubscription typ f = 
        subscriberCount(1L)
        subscriptions.Add(typ, f)

    let removeSubscription typ = 
        subscriberCount(-1L)
        subscriptions.Remove(typ) |> ignore

    let publish typ (payload:'a) = 
        if (box payload) <> null 
        then
            mailbox.Post({PayloadType = typ; Payload = payload })
    do
        Async.Start(worker(), cts.Token)

    interface IEventStream with
        member x.Publish(typ, payload : 'a) = publish typ payload
        member x.Publish(payload : 'a) = publish (typeof<'a>.FullName) payload
        member x.Subscribe(typ, callback) = addSubscription typ callback
        member x.Subscribe<'a>(callback) = addSubscription (typeof<'a>.FullName) (fun event -> event.Payload |> unbox<'a> |> callback)
        member x.Unsubscribe(typ) = removeSubscription typ
        member x.Unsubscribe<'a>() = removeSubscription (typeof<'a>.FullName)
        member x.Dispose() = 
            cts.Cancel()
            if mailbox <> Unchecked.defaultof<_>
            then
                mailbox.Dispose()
                mailbox <- Unchecked.defaultof<_>;
            if subscriptions <> null
            then
                subscriptions.Clear()
                subscriptions <- null



        