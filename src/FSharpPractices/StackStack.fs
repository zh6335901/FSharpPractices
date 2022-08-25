module StackStack

#nowarn "9"

open System.Runtime.CompilerServices
open FSharp.NativeInterop
open System

let inline stackalloc<'a when 'a: unmanaged> (length: int): Span<'a> = 
    let p = NativePtr.stackalloc<'a> length |> NativePtr.toVoidPtr
    Span<'a>(p, length)

[<Struct; IsByRefLike>]
type StackStack<'T>(values: Span<'T>) = 
    [<DefaultValue>] val mutable private _count: int

    member s.Push(v) = 
        if s._count < values.Length then
            values[s._count] <- v
            s._count <- s._count + 1
        else
            failwith "Exceeded capacity of StackStack"

    member s.Pop() = 
        if s._count > 0 then
            s._count <- s._count - 1
            values[s._count]
        else
            failwith "Empty StackStack"

    member s.Count = s._count

    member s.ToArray() = 
        let newArray = GC.AllocateUninitializedArray s._count
        for i in 0 .. newArray.Length - 1 do
            newArray[i] <- values[i]
        newArray

module StackStack = 
    let inline create length = 
        StackStack (stackalloc<_> length)