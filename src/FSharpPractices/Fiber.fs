namespace FiberImpl

open System
open System.Threading

[<Sealed; AllowNullLiteral>]
type Cancel(parent: Cancel) = 
    let mutable flag: int = 0
    let mutable children: Cancel list = []
    new() = Cancel(null)

    member _.Cancelled = flag = 1

    member private _.RemoveChild(child) = 
        let rec loop child = 
            let children' = children
            let nval = children' |> List.filter ((<>) child)
            if not (obj.ReferenceEquals(children', Interlocked.CompareExchange(&children, nval, children')))
            then loop child

        if not (List.isEmpty children) then loop child

    member this.AddChild() = 
        let rec loop child = 
            let children' = children
            if (obj.ReferenceEquals(children', Interlocked.CompareExchange(&children, child :: children', children')))
            then child
            else loop child

        loop (Cancel this)

    member this.Cancel() = 
        if Interlocked.Exchange(&flag, 1) = 0 then
            for child in Interlocked.Exchange(&children, []) do child.Cancel()
            if not (isNull parent) then parent.RemoveChild(this)

type FiberResult<'a> = Result<'a, exn> option

[<Interface>]
type IScheduler = 
    abstract Schedule: (unit -> unit) -> unit
    abstract Delay: TimeSpan * (unit -> unit) -> unit

type Fiber<'a> = Fiber of (IScheduler * Cancel -> (FiberResult<'a> -> unit) -> unit)

[<RequireQualifiedAccess>]
module Fiber = 
    let success r = Fiber <| fun (_, c) next -> if c.Cancelled then next None else next (Some (Ok r))
    let fail e = Fiber <| fun (_, c) next -> if c.Cancelled then next None else next (Some (Error e))
    let cancelled<'a> = Fiber <| fun _ next -> next None

    let delay timeout = 
        Fiber <| fun (s, c) next ->
            if c.Cancelled then 
                next None
            else
                s.Delay(timeout, fun () ->
                    if c.Cancelled
                    then next None
                    else next (Some (Ok ()))
                )

    let mapResult fn (Fiber call) = 
        Fiber <| fun (s, c) next ->
            if c.Cancelled then
                next None
            else
                try
                    call (s, c) (fun result ->
                        if c.Cancelled then next None
                        else next (Option.map fn result)
                    )
                with e -> next (Some (Error e))

    let map fn fiber = mapResult (Result.map fn) fiber
    let catch fn fiber = mapResult (function Error e -> fn e | other -> other) fiber

    let bind fn (Fiber call) = 
        Fiber <| fun (s, c) next ->
            if c.Cancelled then next None
            else
                try
                    call (s, c) (fun result ->
                        if c.Cancelled then next None
                        else 
                            match result with
                            | Some (Ok r) ->
                                let (Fiber call') = fn r
                                call' (s, c) next
                            | None -> next None
                            | Some (Error e) -> next (Some (Error e))
                    )
                with e -> next (Some (Error e))

    let race (Fiber left) (Fiber right): Fiber<Choice<'a, 'b>> = 
        Fiber <| fun (s, c) next ->
            if c.Cancelled then next None
            else 
                let mutable flag = 0
                let child = c.AddChild()
                let run fiber choice = 
                    s.Schedule(fun () ->
                        fiber (s, child) (fun result ->
                            if Interlocked.Exchange(&flag, 1) = 0 then
                                child.Cancel()
                                if c.Cancelled then next None
                                else
                                    match result with
                                    | None -> next None
                                    | Some (Ok v) -> next (Some (Ok (choice v)))
                                    | Some (Error e) -> next (Some (Error e))
                        )
                    )

                run left Choice1Of2
                run right Choice2Of2

    let timeout (t: TimeSpan) fiber = 
        Fiber <| fun (s, c) next ->
            let (Fiber call) = race (delay t) fiber
            call (s, c) (fun result ->
                if c.Cancelled then next None
                else
                    match result with
                    | None -> next None
                    | Some (Ok (Choice1Of2 _)) -> next None // timeout
                    | Some (Ok (Choice2Of2 v)) -> next (Some (Ok v))
                    | Some (Error e) -> next (Some (Error e))
            )

    let concurrent fibs = 
        Fiber <| fun (s, c) next ->
            if c.Cancelled then next None
            else
                let mutable remaining = fibs |> Array.length
                let successes = Array.zeroCreate remaining
                let childCancel = c.AddChild()

                fibs |> Array.iteri (fun i (Fiber call) ->
                    s.Schedule(fun () ->
                        call (s, childCancel) (fun result ->
                            match result with
                            | Some (Ok v) ->
                                successes[i] <- v
                                if c.Cancelled && Interlocked.Exchange(&remaining, -1) > 0 then
                                    next None
                                elif Interlocked.Decrement(&remaining) = 0 then
                                    if c.Cancelled then next None
                                    else next (Some (Ok successes))
                            | Some (Error e) ->
                                if Interlocked.Exchange(&remaining, -1) > 0 then
                                    childCancel.Cancel()
                                    if c.Cancelled then next None
                                    else next (Some (Error e))
                            | None ->
                                if Interlocked.Exchange(&remaining, -1) > 0 then
                                    next None
                        )
                    )
                )

    let blocking (s: IScheduler) (cancel: Cancel) (Fiber fn) = 
        use waiter = new ManualResetEventSlim(false)
        let mutable res = None
        s.Schedule(
            fun () -> fn (s, cancel) (fun result -> 
                if not cancel.Cancelled then
                    Interlocked.Exchange(&res, Some result) |> ignore
                waiter.Set()
            )
        )

        waiter.Wait()
        res.Value
        
[<Struct>]
type FiberBuilder = 
    member inline _.Zero() = Fiber.success (Unchecked.defaultof<_>)
    member inline _.ReturnFrom(fib) = fib
    member inline _.Return(v) = Fiber.success v
    member inline _.Bind(m, f) = Fiber.bind f m

[<AutoOpen>]
module FiberBuilder = 
    let fib = FiberBuilder()
                