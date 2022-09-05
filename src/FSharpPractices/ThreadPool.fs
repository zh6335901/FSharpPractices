module ThreadPool

open System.Collections.Concurrent
open System.Threading
open System

type WorkAgent(shared: ConcurrentQueue<IThreadPoolWorkItem>) = 
    let personal = ConcurrentQueue<IThreadPoolWorkItem>()
    let restEvent = new ManualResetEventSlim(false)

    let swap (l: byref<'t>, r: byref<'t>) = 
        let tmp = l
        l <- r
        r <- tmp

    let loop() = 
        let mutable first = personal
        let mutable second = shared
        let mutable counter = 0

        while true do
            let mutable item = null
            if first.TryDequeue(&item) || second.TryDequeue(&item)
            then item.Execute()
            else
                restEvent.Wait()
                restEvent.Reset()

            counter <- (counter + 1) % 32
            if counter = 0 then swap(&first, &second)
        
    let thread = new Thread(loop)
        
    member _.Start() = thread.Start()

    member this.WakeUp() = 
        if not restEvent.IsSet then restEvent.Set()
   
    member this.Schedule(item) = 
        personal.Enqueue(item)
        this.WakeUp()

    member _.Dispose() = 
        restEvent.Dispose()
        thread.Abort()

    interface IDisposable with member this.Dispose() = this.Dispose()

type ThreadPool(size: int) = 
    let mutable i: int = 0
    let sharedQ = ConcurrentQueue<IThreadPoolWorkItem>()
    let agents = Array.init size (fun _ -> new WorkAgent(sharedQ))
    do
        for a in agents do a.Start()

    static let shared = lazy (new ThreadPool(Environment.ProcessorCount))
    static member Global with get() = shared.Value

    member _.UnsafeQueueUserWorkItem(item) = 
        sharedQ.Enqueue item
        i <- Interlocked.Increment(&i) % size
        agents[i].WakeUp()

    member this.UnsafeQueueUserWorkItem(affinityId, item) = 
        agents[affinityId % size].Schedule(item)

    member this.Queue(fn: unit -> unit) = 
        this.UnsafeQueueUserWorkItem { new IThreadPoolWorkItem with member _.Execute() = fn () }

    member this.Queue(affinityId, fn: unit -> unit) = 
        this.UnsafeQueueUserWorkItem(affinityId, 
            { new IThreadPoolWorkItem with member _.Execute() = fn () })

    member _.Dispose() = for a in agents do a.Dispose()

    interface IDisposable with member this.Dispose() = this.Dispose()

module ThreadPoolSamples = 
    let call () = 
        let threadId = Thread.CurrentThread.ManagedThreadId
        printfn "Calling from thread %i" threadId

    let run () = 
        let affinityId = 1
        for i in 0 .. 100 do 
            ThreadPool.Global.Queue(affinityId, call)

        for i in 0 .. 100 do
            ThreadPool.Global.Queue(call)