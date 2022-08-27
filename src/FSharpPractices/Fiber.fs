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