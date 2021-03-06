#nowarn "40"
namespace Kafunk

// TODO: https://github.com/fsprojects/FSharpx.Async

open System
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic
open System.Collections.Concurrent

open Kafunk


[<AutoOpen>]
module AsyncEx =

  let empty : Async<unit> = async.Return()

  let never : Async<unit> = 
    Async.Sleep Timeout.Infinite

  let awaitTaskUnit (t:Task) =
    Async.FromContinuations <| fun (ok,err,cnc) ->
      t.ContinueWith(fun t ->
        if t.IsFaulted then err(t.Exception)
        elif t.IsCanceled then cnc(OperationCanceledException("Task wrapped with Async.AwaitTask has been cancelled.",  t.Exception))
        elif t.IsCompleted then ok()
        else failwith "invalid Task state!") |> ignore

  let awaitTaskCancellationAsError (t:Task<'a>) : Async<'a> =
    Async.FromContinuations <| fun (ok,err,_) ->
      t.ContinueWith (fun (t:Task<'a>) ->
        if t.IsFaulted then err t.Exception
        elif t.IsCanceled then err (OperationCanceledException("Task wrapped with Async has been cancelled."))
        elif t.IsCompleted then ok t.Result
        else failwith "invalid Task state!") |> ignore

  let awaitTaskUnitCancellationAsError (t:Task) : Async<unit> =
    Async.FromContinuations <| fun (ok,err,_) ->
      t.ContinueWith (fun (t:Task) ->
        if t.IsFaulted then err t.Exception
        elif t.IsCanceled then err (OperationCanceledException("Task wrapped with Async has been cancelled."))
        elif t.IsCompleted then ok ()
        else failwith "invalid Task state!") |> ignore

  type Async with

    /// An async computation which does nothing and completes immediately.
    static member inline empty = empty

    /// An async computation which does nothing and never completes.
    static member inline never = never

    static member map (f:'a -> 'b) (a:Async<'a>) : Async<'b> = async.Bind(a, f >> async.Return)

    static member inline bind (f:'a -> Async<'b>) (a:Async<'a>) : Async<'b> = async.Bind(a, f)

    static member inline join (a:Async<Async<'a>>) : Async<'a> = Async.bind id a
   
    static member inline tryFinally (compensation:unit -> unit) (a:Async<'a>) : Async<'a> =
      async.TryFinally(a, compensation)

    static member inline tryFinallyDispose (d:#IDisposable) (a:Async<'a>) : Async<'a> =
      Async.tryFinally (fun () -> d.Dispose()) a

    static member inline tryFinallyDisposeAll (ds:#IDisposable seq) (a:Async<'a>) : Async<'a> =
      Async.tryFinally (fun () -> ds |> Seq.iter (fun d -> d.Dispose())) a

    static member inline tryCancelled comp a = Async.TryCancelled(a, comp)

    static member inline tryWith h a = async.TryWith(a, h)

    /// Returns an async computation which will wait for the given task to complete.
    static member inline AwaitTask (t:Task) = awaitTaskUnit t

    /// Returns an async computation which will wait for the given task to complete and returns its result.
    /// Task cancellations are propagated as exceptions so that they can be trapped.
    static member inline AwaitTaskCancellationAsError (t:Task<'a>) : Async<'a> = 
      awaitTaskCancellationAsError t

    /// Returns an async computation which will wait for the given task to complete and returns its result.
    /// Task cancellations are propagated as exceptions so that they can be trapped.
    static member inline AwaitTaskCancellationAsError (t:Task) : Async<unit> = 
      awaitTaskUnitCancellationAsError t

    /// Like Async.StartWithContinuations but starts the computation on a ThreadPool thread.
    static member StartThreadPoolWithContinuations (a:Async<'a>, ok:'a -> unit, err:exn -> unit, cnc:OperationCanceledException -> unit, ?ct:CancellationToken) =
      let a = Async.SwitchToThreadPool () |> Async.bind (fun _ -> a)
      Async.StartWithContinuations (a, ok, err, cnc, defaultArg ct CancellationToken.None)

    static member Parallel (c1, c2) : Async<'a * 'b> = async {
      let! c1 = c1 |> Async.StartChild
      let! c2 = c2 |> Async.StartChild
      let! c1 = c1
      let! c2 = c2
      return c1,c2 }

    static member Parallel (c1, c2, c3) : Async<'a * 'b * 'c> = async {
      let! c1 = c1 |> Async.StartChild
      let! c2 = c2 |> Async.StartChild
      let! c3 = c3 |> Async.StartChild
      let! c1 = c1
      let! c2 = c2
      let! c3 = c3
      return c1,c2,c3 }

    static member Parallel (c1, c2, c3, c4) : Async<'a * 'b * 'c * 'd> = async {
      let! c1 = c1 |> Async.StartChild
      let! c2 = c2 |> Async.StartChild
      let! c3 = c3 |> Async.StartChild
      let! c4 = c4 |> Async.StartChild
      let! c1 = c1
      let! c2 = c2
      let! c3 = c3
      let! c4 = c4
      return c1,c2,c3,c4 }

    /// Creates an async computation which runs the provided sequence of computations and completes
    /// when all computations in the sequence complete. Up to parallelism computations will
    /// be in-flight at any given point in time. Error or cancellation of any computation in
    /// the sequence causes the resulting computation to error or cancel, respectively.
    static member ParallelIgnoreCT (ct:CancellationToken) (parallelism:int) (xs:seq<Async<_>>) = async {
      let sm = new SemaphoreSlim(parallelism)
      let cde = new CountdownEvent(1)
      let tcs = new TaskCompletionSource<unit>()
      ct.Register(Action(fun () -> tcs.TrySetCanceled() |> ignore)) |> ignore
      let inline tryComplete () =
        if cde.Signal() then
          tcs.SetResult(())
      let inline ok _ =
        sm.Release() |> ignore
        tryComplete ()
      let inline err (ex:exn) =
        tcs.TrySetException ex |> ignore
        sm.Release() |> ignore
      let inline cnc (_:OperationCanceledException) =
        tcs.TrySetCanceled() |> ignore
        sm.Release() |> ignore
      try
        use en = xs.GetEnumerator()
        while not (tcs.Task.IsCompleted) && en.MoveNext() do
          sm.Wait()
          cde.AddCount(1)
          Async.StartWithContinuations (en.Current, ok, err, cnc, ct)
        tryComplete ()
        do! tcs.Task |> Async.AwaitTask
      finally
        cde.Dispose()
        sm.Dispose() }

    /// Creates an async computation which runs the provided sequence of computations and completes
    /// when all computations in the sequence complete. Up to parallelism computations will
    /// be in-flight at any given point in time. Error or cancellation of any computation in
    /// the sequence causes the resulting computation to error or cancel, respectively.
    static member ParallelIgnore (parallelism:int) (xs:seq<Async<_>>) =
      Async.ParallelIgnoreCT CancellationToken.None parallelism xs

    /// Creates an async computation which runs the provided sequence of computations and completes
    /// when all computations in the sequence complete. Up to parallelism computations will
    /// be in-flight at any given point in time. Error or cancellation of any computation in
    /// the sequence causes the resulting computation to error or cancel, respectively.
    /// Like Async.Parallel but with support for throttling.
    /// Note that an array is allocated to contain the results of all computations.
    static member ParallelThrottled (parallelism:int) (xs:seq<Async<'a>>) : Async<'a[]> = async {
      let rec comps  = xs |> Seq.toArray |> Array.mapi (fun i -> Async.map (fun a -> Array.set results i a))
      and results = Array.zeroCreate comps.Length
      do! Async.ParallelIgnore parallelism comps
      return results }

    /// Starts the specified operation using a new CancellationToken and returns
    /// IDisposable object that cancels the computation. This method can be used
    /// when implementing the Subscribe method of IObservable interface.
    static member StartDisposable (op:Async<unit>) =
      let ct = new System.Threading.CancellationTokenSource()
      Async.Start(op, ct.Token)
      { new IDisposable with member x.Dispose() = ct.Cancel() }

//    /// Returns an async computation which runs the argument computation but raises an exception if it doesn't complete
//    /// by the specified timeout.
//    static member timeoutAfter (timeout:TimeSpan) (c:Async<'a>) = async {
//      let! r = Async.StartChild(c, (int)timeout.TotalMilliseconds)
//      return! r }

//    static member timeoutAfter (timeout:TimeSpan) (c:Async<'a>) =
//      Async.FromContinuations <| fun (ok,err,cnc) ->
//        let cts = new CancellationTokenSource()
//        cts.CancelAfter timeout
//        //cts.Token.Register (fun () -> printfn "cancelled!") |> ignore
//        //let rec t = new Timer(cnc', null, int timeout.TotalMilliseconds, -1)
////        and cnc' _ = 
////          printfn "timeout!"
////          cnc (OperationCanceledException())
////          cts.Cancel()
////          t.Dispose()          
////        let ok a = 
////          ok a
////          t.Dispose()             
////        let err e =           
////          err e
////          t.Dispose()
//        let ok a = ok a ; cts.Dispose()
//        let err e = err e ; cts.Dispose()
//        let cnc e = cnc e ; cts.Dispose()
//        Async.StartWithContinuations (c, ok, err, cnc, cts.Token)

    static member timeoutAfter (timeout:TimeSpan) (c:Async<'a>) =
      let timeout = async {
        do! Async.Sleep (int timeout.TotalMilliseconds)
        return raise (OperationCanceledException()) }
      Async.choose c timeout

    /// Creates a computation which returns the result of the first computation that
    /// produces a value as well as a handle to the other computation. The other
    /// computation will be memoized.
    static member chooseBoth (a:Async<'a>) (b:Async<'a>) : Async<'a * Async<'a>> =
      Async.FromContinuations <| fun (ok,err,cnc) ->
        let state = ref 0
        let iv = new TaskCompletionSource<_>()
        let inline ok a =
          if (Interlocked.CompareExchange(state, 1, 0) = 0) then
            ok (a, iv.Task |> Async.AwaitTask)
          else
            iv.SetResult a
        let inline err (ex:exn) =
          if (Interlocked.CompareExchange(state, 1, 0) = 0) then err ex
          else iv.SetException ex
        let inline cnc ex =
          if (Interlocked.CompareExchange(state, 1, 0) = 0) then cnc ex
          else iv.SetCanceled ()
        Async.StartThreadPoolWithContinuations (a, ok, err, cnc)
        Async.StartThreadPoolWithContinuations (b, ok, err, cnc)

    static member chooseTasks (a:Task<'a>) (b:Task<'a>) : Async<'a * Task<'a>> = async {
      let! ct = Async.CancellationToken
      let i = Task.WaitAny([| (a :> Task) ; (b :> Task) |], ct)
      if i = 0 then return (a.Result, b)
      elif i = 1 then return (b.Result, a)
      else return! failwith (sprintf "unreachable, i = %d" i) }

    /// Creates a computation which produces a tuple consiting of the value produces by the first
    /// argument computation to complete and a handle to the other computation. The second computation
    /// to complete is memoized.
    static member internal chooseBothAny (a:Async<'a>) (b:Async<'b>) : Async<Choice<'a * Async<'b>, 'b * Async<'a>>> =
      Async.chooseBoth (a |> Async.map Choice1Of2) (b |> Async.map Choice2Of2)
      |> Async.map (fun (first,second) ->
        match first with
        | Choice1Of2 a -> (a,(second |> Async.map (function Choice2Of2 b -> b | _ -> failwith "invalid state"))) |> Choice1Of2
        | Choice2Of2 b -> (b,(second |> Async.map (function Choice1Of2 a -> a | _ -> failwith "invalid state"))) |> Choice2Of2)

    /// Creates an async computation which completes when any of the argument computations completes.
    /// The other argument computation is cancelled.
    static member choose (a:Async<'a>) (b:Async<'a>) : Async<'a> =
      Async.FromContinuations <| fun (ok,err,cnc) ->
        let state = ref 0
        let cts = new CancellationTokenSource()
        let inline cancel () =
          cts.Cancel()
          cts.Dispose()
        let inline ok a =
          if (Interlocked.CompareExchange(state, 1, 0) = 0) then 
            cancel ()
            ok a
        let inline err (ex:exn) =
          if (Interlocked.CompareExchange(state, 1, 0) = 0) then 
            cancel ()
            err ex
        let inline cnc ex =
          if (Interlocked.CompareExchange(state, 1, 0) = 0) then 
            cancel ()
            cnc ex                
        Async.StartThreadPoolWithContinuations (a, ok, err, cnc, cts.Token)
        Async.StartThreadPoolWithContinuations (b, ok, err, cnc, cts.Token)

    /// Associates an async computation to a cancellation token.
    static member withCancellationToken (ct:CancellationToken) (a:Async<'a>) : Async<'a> =
      Async.FromContinuations (fun (ok,err,cnc) -> Async.StartThreadPoolWithContinuations(a, ok, err, cnc, ct))

    static member Throw (a:Async<Choice<'a, exn>>) : Async<'a> =
      async {
        let! r = a
        match r with
        | Choice1Of2 a -> return a
        | Choice2Of2 e -> return raise e }




type Mb<'a> = MailboxProcessor<'a>

/// Operations on unbounded FIFO mailboxes.
module Mb =

  /// Creates a new unbounded mailbox.
  let create () : Mb<'a> = 
    MailboxProcessor.Start (fun _ -> async.Return())

  /// Puts a message into a mailbox, no waiting.
  let inline put (a:'a) (mb:Mb<'a>) = mb.Post a

  /// Creates an async computation that completes when a message is available in a mailbox.
  let inline take (mb:Mb<'a>) = mb.Receive()




type private MVarReq<'a> =
  | PutAsync of Async<'a> * TaskCompletionSource<'a>
  | UpdateAsync of cond:('a -> bool) * update:('a -> Async<'a>) * TaskCompletionSource<'a>
  | PutOrUpdateAsync of update:('a option -> Async<'a>) * TaskCompletionSource<'a>
  | Get of TaskCompletionSource<'a>
  | Take of cond:('a -> bool) * TaskCompletionSource<'a>

/// A serialized variable.
type MVar<'a> internal (?a:'a) =

  let [<VolatileField>] mutable state : 'a = Unchecked.defaultof<_>

  let mbp = MailboxProcessor.Start (fun mbp -> async {
    let rec init (v:int) = async {
      state <- Unchecked.defaultof<_>
      return! mbp.Scan (function
        | PutAsync (a,rep) ->          
          Some (async {
            try            
              let! a = a
              state <- a
              rep.SetResult a
              return! loop (a, v + 1)
            with ex ->
              rep.SetException ex
              return! init (v + 1) })
        | PutOrUpdateAsync (update,rep) ->          
          Some (async {
            try
              let! a = update None
              rep.SetResult a  
              return! loop (a, v + 1)
            with ex ->
              rep.SetException ex
              return! init v })
        | _ ->
          None) }
    and loop (a:'a, v:int) = async {
      let! msg = mbp.Receive()
      match msg with
      | PutAsync (a',rep) ->
        try
          let! a = a'
          state <- a
          rep.SetResult a
          return! loop (a, v + 1)
        with ex ->
          rep.SetException ex
          return! loop (a, v)
      | PutOrUpdateAsync (update,rep) ->
        try
          let! a = update (Some a)
          state <- a
          rep.SetResult a
          return! loop (a, v + 1)
        with ex ->
          rep.SetException ex
          return! loop (a, v)
      | Get rep ->
        rep.SetResult a
        return! loop (a, v + 1)
      | Take (cond,rep) ->
        if cond a then        
          rep.SetResult a
          return! init (v + 1)
        else
          rep.SetResult a
          return! loop (a, v)
      | UpdateAsync (cond,f,rep) ->
        try
          if cond a then
            let! a = f a
            state <- a
            rep.SetResult a
            return! loop (a, v + 1)
          else
            rep.SetResult a
            return! loop (a, v)
        with ex ->
          rep.SetException ex
          return! loop (a, v) }
    match a with
    | Some a ->
      state <- a
      return! loop (a, 1)
    | None -> 
      return! init 0 })

  do mbp.Error.Add (fun x -> printfn "|MVar|ERROR|%O" x) // shouldn't happen
  
  let postAndAsyncReply f = 
    let tcs = new TaskCompletionSource<'a>()    
    mbp.Post (f tcs)
    tcs.Task |> Async.AwaitTask 

  member __.Get () : Async<'a> =
    postAndAsyncReply (Get)

  member __.TakeIf (cond:'a -> bool) : Async<'a> =
    postAndAsyncReply (fun tcs -> Take(cond, tcs))

  member __.Take () : Async<'a> =
    __.TakeIf (konst true)

  member __.GetFast () : 'a =
    state

  member __.Put (a:'a) : Async<'a> =
    __.PutAsync (async.Return a)

  member __.PutAsync (a:Async<'a>) : Async<'a> =
    postAndAsyncReply (fun ch -> PutAsync (a,ch))

  member __.Update (f:'a -> 'a) : Async<'a> =
    __.UpdateAsync (f >> async.Return)

  member __.UpdateIfAsync (cond:'a -> bool, update:'a -> Async<'a>) : Async<'a> =
    postAndAsyncReply (fun ch -> UpdateAsync (cond, update, ch))

  member __.UpdateAsync (update:'a -> Async<'a>) : Async<'a> =
    __.UpdateIfAsync (konst true, update)

  member __.PutOrUpdateAsync (update:'a option -> Async<'a>) : Async<'a> =
    postAndAsyncReply (fun ch -> PutOrUpdateAsync (update,ch))

  interface IDisposable with
    member __.Dispose () = (mbp :> IDisposable).Dispose()

/// Operations on serialized variables.
module MVar =
  
  /// Creates an empty MVar.
  let create () : MVar<'a> =
    new MVar<_>()

  /// Creates a full MVar.
  let createFull (a:'a) : MVar<'a> =
    new MVar<_>(a)

  /// Gets the value of the MVar.
  let get (c:MVar<'a>) : Async<'a> =
    c.Get ()

  /// Takes an item from an MVar if the item satisfied the condition.
  /// If the item doesn't satisfy the condition, it is still returned, but
  /// remains in the MVar.
  let takeIf (cond:'a -> bool) (c:MVar<'a>) : Async<'a> =
    c.TakeIf (cond)

  /// Takes an item from the MVar.
  let take (c:MVar<'a>) : Async<'a> =
    c.Take ()
  
  /// Returns the last known value, if any, without serialization.
  /// NB: unsafe because the value may be null, but helpful for supporting overlapping
  /// operations.
  let getFastUnsafe (c:MVar<'a>) : 'a =
    c.GetFast ()

  /// Puts an item into the MVar, returning the item that was put.
  /// Returns if the MVar is either empty or full.
  let put (a:'a) (c:MVar<'a>) : Async<'a> =
    c.Put a

  /// Puts an item into the MVar, returning the item that was put.
  /// Returns if the MVar is either empty or full.
  let putAsync (a:Async<'a>) (c:MVar<'a>) : Async<'a> =
    c.PutAsync a

  /// Puts a new value into an MVar or updates an existing value.
  /// Returns the value that was put or the updated value.
  let putOrUpdateAsync (update:'a option -> Async<'a>) (c:MVar<'a>) : Async<'a> =
    c.PutOrUpdateAsync (update)

  /// Updates an item in the MVar.
  /// Returns when an item is available to update.
  let update (update:'a -> 'a) (c:MVar<'a>) : Async<'a> =
    c.Update update

  /// Updates an item in the MVar.
  /// Returns when an item is available to update.
  let updateAsync (update:'a -> Async<'a>) (c:MVar<'a>) : Async<'a> =
    c.UpdateAsync update

  /// Updates an item in the MVar if it satisfies the condition.
  /// Returns when an item is available to update.
  /// If the item doesn't satisfy the condition, it is not updated, but it is returned.
  let updateIfAsync (cond:'a -> bool) (update:'a -> Async<'a>) (c:MVar<'a>) : Async<'a> =
    c.UpdateIfAsync (cond, update)



// operations on resource monitors.
module Resource =

  /// Resource recovery action
  type Recovery =
      
    /// The resource should be re-created.
    | Recreate

    /// The error should be escalated, notifying dependent
    /// resources.
    | Escalate     


  type Epoch<'r> = {
    resource : 'r
    closed : TaskCompletionSource<unit>
  }
     
  /// <summary>
  /// Recoverable resource supporting the creation recoverable operations.
  /// - create - used to create the resource initially and upon recovery. Overlapped inocations
  ///   of this function are queued and given the instance being created when creation is complete.
  /// - handle - called when an exception is raised by an resource-dependent computation created
  ///   using this resrouce. If this function throws an exception, it is escalated.
  /// </summary>
  /// <notes>
  /// A resource is an entity which undergoes state changes and is used by operations.
  /// Resources can form supervision hierarchy through a message passing and reaction system.
  /// Supervision hierarchies can be used to re-cycle chains of dependent resources.
  /// </notes>
  type Resource<'r> internal (create:Async<'r>, handle:('r * exn) -> Async<Recovery>) =
      
    //let Log = Log.create "Resource"
    
    let cell : MVar<Epoch<'r>> = MVar.create ()
   
    let create = async {
      //Log.info "creating_resource"
      let! r = create
      //Log.info "created_resource"
      let closed = new TaskCompletionSource<unit>()
      return { resource = r ; closed = closed } }

    let recover ex ep = async {
      //Log.info "recovering_resource"
      let! recovery = handle (ep.resource,ex)
      match recovery with
      | Escalate -> 
        //Log.info "recovery_escalating"
        let edi = Runtime.ExceptionServices.ExceptionDispatchInfo.Capture ex
        edi.Throw ()
        return failwith ""              
      | Recreate ->
        //Log.info "recovery_restarting"
        let! ep' = create
        //Log.info "recovery_restarted"
        return ep' }

    member internal __.Create () = async {
      return! cell |> MVar.putAsync create }

    member __.Recover (ep':Epoch<'r>, ex:exn) =
      cell |> MVar.updateAsync (recover ex)
// TODO: review
//      if ep'.closed.TrySetException ex then
//        cell |> MVar.updateAsync (recover ex)
//      else
//        cell |> MVar.get
        
    member __.Inject<'a, 'b> (op:'r -> ('a -> Async<'b>)) : Async<'a -> Async<'b>> = async {      
      let! ep = MVar.get cell
      let epoch = ref ep
      let rec go a = async {
        //Log.trace "performing_operation"
        //let! ep = MVar.get cell
        let ep = !epoch
        try
          return! op ep.resource a
        with ex ->
          //Log.info "caught_exception_on_injected_operation|input=%A error=%O" a ex
          let! epoch' = __.Recover (ep, ex)
          epoch := epoch'
          //Log.info "recovery_complete"
          return! go a }
      return go }

    interface IDisposable with
      member __.Dispose () = ()
    
  let recoverableRecreate (create:Async<'r>) (handleError:('r * exn) -> Async<Recovery>) = async {      
    let r = new Resource<_>(create, handleError)
    let! _ = r.Create()
    return r }

  let inject (op:'r -> ('a -> Async<'b>)) (r:Resource<'r>) : Async<'a -> Async<'b>> =
    r.Inject op
   

module AsyncFunc =
  
  let dimap (g:'c -> 'a) (h:'b -> 'd) (f:'a -> Async<'b>) : 'c -> Async<'d> =
    g >> f >> Async.map h

  let doBeforeAfter (before:'a -> unit) (after:'a * 'b -> unit) (f:'a -> Async<'b>) : 'a -> Async<'b> =
    fun a -> async {
      do before a
      let! b = f a
      do after (a,b)
      return b }

  let doBeforeAfterError (before:'a -> unit) (after:'a * 'b -> unit) (error:'a * exn -> unit) (f:'a -> Async<'b>) : 'a -> Async<'b> =
    fun a -> async {
      do before a
      try
        let! b = f a
        do after (a,b)
        return b
      with ex ->
        let edi = Runtime.ExceptionServices.ExceptionDispatchInfo.Capture ex
        error (a,edi.SourceException)
        edi.Throw ()
        return failwith "undefined" }



         