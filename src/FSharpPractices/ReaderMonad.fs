module ReaderMonad

[<Struct>] type Effect<'env, 'out> = Effect of ('env -> 'out) 

[<RequireQualifiedAccess>]
module Effect = 
    let inline run env (Effect fn) = 
        fn env

    let ask = Effect id

    let inline bind f m = 
        Effect (
            fun env ->
                let out = run env m
                run env (f out)
        )

    let inline value v = Effect (fun _ -> v)
    let inline apply (fn: 'env -> 'out) = Effect fn

[<Struct>]
type EffectBuilder =
    member inline _.Bind(m, f) = Effect.bind f m  
    member inline _.Zero() = Effect.value (Unchecked.defaultof<_>)
    member inline _.ReturnFrom(m) = m
    member inline _.Return(m) = Effect.value m

[<AutoOpen>]
module EffectBuilder = 
    let effect = EffectBuilder()

module ReaderMonadSamples = 
    [<Interface>]
    type ILogger = 
        abstract Debug: string -> unit
        abstract Info: string -> unit

    [<Interface>] type ILog = abstract Logger: ILogger

    module Log = 
        let debug msg (env: #ILog) = env.Logger.Debug msg
        let info msg (env: #ILog) = env.Logger.Info msg 

    [<Interface>]
    type IDatabase =
        abstract Query: string * 'i -> 'o
        abstract Execute: string * 'i -> unit
        
    [<Interface>] type IDb = abstract Database: IDatabase

    type User = { Id: string; Password: string; Name: string }

    module Db = 
        let fetchUser (userId: string) (env: #IDb): User = env.Database.Query("test", {| userId = userId |})
        let updateUser (user: User) (env: #IDb) = env.Database.Execute("test", user)

    let changePass userId newPassword = effect {
        let! user = Effect.apply (Db.fetchUser userId)
        do! Effect.apply (Db.updateUser { user with Password = newPassword })
        do! Effect.apply (Log.info "Change password succeeded")

        return Ok ()
    }
