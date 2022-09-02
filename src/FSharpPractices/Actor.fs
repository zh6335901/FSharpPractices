module Actor

open System.Collections.Concurrent
open System.Threading
open System

[<Struct>]
type SystemMessage =
    | Die
    | Restart of exn

module Status = 
    let [<Literal>] Idle = 0
    let [<Literal>] Occupied = 1
    let [<Literal>] Stopped = 2

type Actor<'state, 'msg>(initState: 'state, handler: 'state -> 'msg -> 'state) = 
    let mutable status: int = Status.Idle
    let systemMessages = ConcurrentQueue<SystemMessage>()
    let userMessages = ConcurrentQueue<'msg>()
    let mutable state = initState

    static let deadLetters = Event<'msg>()

    static let callback: WaitCallback = new WaitCallback(
        fun o ->
            let actor = o :?> Actor<'state, 'msg>
            actor.Execute()
    )

    let stop () = 
        Interlocked.Exchange(&status, Status.Stopped) |> ignore
        for msg in userMessages do
            deadLetters.Trigger msg

    interface IDisposable with
        member _.Dispose() = stop()

    member this.Post(systemMessage: SystemMessage) = 
        systemMessages.Enqueue systemMessage
        this.Schedule()

    member this.Post(msg: 'msg) = 
        userMessages.Enqueue msg
        this.Schedule()

    member private this.Schedule() = 
        if Interlocked.CompareExchange(&status, Status.Occupied, Status.Idle) = Status.Idle
        then ThreadPool.QueueUserWorkItem(callback, this) |> ignore

    member private this.Execute() = 
        let rec loop iterations = 
            if Volatile.Read(&status) <> Status.Stopped then
                if iterations <> 0 then
                    let ok, sysMsg = systemMessages.TryDequeue()
                    if ok then
                        match sysMsg with
                        | Die ->
                            stop()
                            Status.Stopped
                        | Restart error ->
                            state <- initState
                            loop (iterations - 1)
                    else
                        let ok, msg = userMessages.TryDequeue()
                        if ok then
                            state <- handler state msg
                            loop (iterations - 1)
                        else Status.Idle
                else Status.Idle
            else Status.Stopped

        try
            let status' = loop 300

            if status' <> Status.Stopped
            then Interlocked.Exchange(&status, Status.Idle) |> ignore

            if systemMessages.Count <> 0 || userMessages.Count <> 0
            then this.Schedule()
        with err ->
            Interlocked.Exchange(&status, Status.Idle) |> ignore
            this.Post(Restart err)
