// Copyright (C) by Vesa Karvonen

namespace Hopac

open System
open System.Collections.Generic
open System.Threading
open Hopac.Infixes
open Hopac.Job.Infixes
open Hopac.Alt.Infixes
open Hopac.Promise.Infixes
open Hopac.Extensions
open Timer.Global

module Stream =
  let inline memo x = Promise.Now.delay x
  let inline start x = Job.Global.start x
  let inline tryIn u2v vK eK =
    let mutable e = null
    let v = try u2v () with e' -> e <- e' ; Unchecked.defaultof<_>
    match e with
     | null -> vK v
     | e -> eK e
  let inline (|Nothing|Just|) (b, x) = if b then Just x else Nothing

  type Cons<'x> =
    | Cons of Value: 'x * Next: Promise<Cons<'x>>
    | Nil

  type Stream<'x> = Promise<Cons<'x>>

  type Src<'x> = {mutable src: IVar<Cons<'x>>}

  module Src =
    let create () = {src = IVar<_> ()}
    let rec value s x = Job.delay <| fun () ->
      let w = IVar<_> ()
      let v = s.src
      if IVar.Now.isFull v then raise <| Exception ("Src closed")
      let v' = Interlocked.CompareExchange (&s.src, w, v)
      if LanguagePrimitives.PhysicalEquality v' v
      then v <-= Cons (x, w)
      else value s x
    let error s e = Job.delay <| fun () -> s.src <-=! e // delay required
    let close s = Job.delay <| fun () -> s.src <-= Nil // delay required
    let tap s = s.src :> Promise<_>

  type Var<'x> = {mutable var: Cons<'x>}

  module Var =
    let create x = {var = Cons (x, IVar<_> ())}
    let get v = match v.var with Cons (x, _) -> x | Nil -> failwith "Impossible"
    let set v x = Job.delay <| fun () ->
      let c = Cons (x, IVar<_> ())
      match Interlocked.Exchange (&v.var, c) with
       | Cons (_, i) -> (i :?> IVar<_>) <-= c
       | Nil -> failwith "Impossible"
    let tap v = Promise.Now.withValue v.var

  let nilj<'x> = Job.result Nil :> Job<Cons<'x>>
  let nila<'x> = Alt.always Nil :> Alt<Cons<'x>>
  let inline nil<'x> = Promise.Now.withValue Nil :> Stream<'x>
  let inline consj x xs = Job.result (Cons (x, xs))
  let inline consa x xs = Alt.always (Cons (x, xs))
  let inline cons x xs = Promise.Now.withValue (Cons (x, xs))
  let inline error e = Promise.Now.withFailure e :> Stream<_>
  let one x = cons x nil
  let inline delay (u2xs: _ -> #Job<Cons<_>>) = memo (Job.delay u2xs)

  let fix (xs2xs: Stream<'x> -> #Stream<'x>) =
    let xs = Promise<_> () // XXX publish interface for this?
    xs.Readers <- Promise<_>.Fulfill (xs, Job.delay <| fun () -> xs2xs xs)
    xs :> Stream<_>

  let inline never<'x> = Promise.Now.never () :> Stream<'x>

  let rec ofEnum (xs: IEnumerator<'x>) = memo << Job.thunk <| fun () ->
    if xs.MoveNext () then Cons (xs.Current, ofEnum xs) else xs.Dispose () ; Nil

  let ofSeq (xs: seq<_>) = delay (ofEnum << xs.GetEnumerator)

  let rec onCloseJob (uJ: Job<unit>) xs =
    Job.tryIn xs
       <| function Cons (x, xs) -> consj x (onCloseJob uJ xs)
                 | Nil -> uJ >>% Nil
       <| fun e -> uJ >>! e
    |> memo
  let onCloseFun u2u xs = onCloseJob (Job.thunk u2u) xs

  type Subscribe<'x> (src: IVar<Cons<'x>>) =
    let mutable src = src
    interface IObserver<'x> with
     override t.OnCompleted () = src <-= Nil |> start
     override t.OnError (e) = src <-=! e |> start
     override t.OnNext (x) =
       let nxt = IVar<_> ()
       src <-= Cons (x, nxt) |> start
       src <- nxt

  let subscribeDuring (xs2ys: Stream<'x> -> #Stream<'y>) (xs: IObservable<'x>) =
    delay <| fun () ->
    let src = IVar<_> ()
    xs2ys src |> onCloseFun (xs.Subscribe (Subscribe (src))).Dispose

  let subscribeOnFirst (xs: IObservable<'x>) = delay <| fun () ->
    let src = IVar<_> () in xs.Subscribe (Subscribe (src)) |> ignore ; src

  let subscribingTo (xs: IObservable<'x>) (xs2yJ: Stream<'x> -> #Job<'y>) =
    Job.delay <| fun () ->
    let src = IVar<_> ()
    Job.using (xs.Subscribe (Subscribe (src))) <| fun _ -> xs2yJ src :> Job<_>

  let inline post (ctxt: SynchronizationContext) op =
    match ctxt with null -> op () | ctxt -> ctxt.Post ((fun _ -> op ()), null)

  type Subscriber<'x> (src: IVar<Cons<'x>>, ctxt: SynchronizationContext) =
    let mutable src = src
    // Initial = 0, Subscribed = 1, Disposed = 2
    [<DefaultValue>] val mutable State: int
    [<DefaultValue>] val mutable disp: IDisposable
    member this.Dispose () =
      if 2 <> this.State then
        this.State <- 2
        match this.disp with
         | null -> ()
         | disp -> post ctxt disp.Dispose
    member this.Subscribe (xO: IObservable<'x>) =
      if 0 = this.State then
        post ctxt <| fun () ->
          this.disp <- xO.Subscribe this
          if 0 <> Interlocked.CompareExchange (&this.State, 1, 0) then
            this.disp.Dispose ()
    interface IObserver<'x> with
     override t.OnCompleted () = t.State <- 2; src <-= Nil |> start
     override t.OnError (e) = t.State <- 2; src <-=! e |> start
     override t.OnNext (x) =
       let nxt = IVar<_> ()
       src <-= Cons (x, nxt) |> start
       src <- nxt

  type Guard<'x> (subr: Subscriber<'x>) =
    override this.Finalize () = subr.Dispose ()

  let rec guard g xs = xs |>>* function Cons (x, xs) -> Cons (x, guard g xs)
                                      | Nil -> GC.SuppressFinalize g; Nil

  let ofObservableOn ctxt (xO: IObservable<'x>) : Stream<'x> =
    let xs = IVar ()
    let sub = Subscriber (xs, ctxt)
    post ctxt <| fun () -> sub.Subscribe xO
    guard (Guard (sub)) xs
  let ofObservableOnMain xO = ofObservableOn (Async.getMain ()) xO
  let ofObservable xO = ofObservableOn null xO

  let toObservable xs =
    // XXX Use a better approach than naive locking.
    let subs = HashSet<IObserver<_>>()
    let inline iter f =
      Array.iter f << lock subs <| fun () ->
      let xs = Array.zeroCreate subs.Count
      subs.CopyTo xs
      xs
    let rec loop xs =
      Job.tryIn xs
       <| function Cons (x, xs) -> iter (fun xS -> xS.OnNext x); loop xs
                 | Nil -> Job.unit << iter <| fun xS -> xS.OnCompleted ()
       <| fun e -> Job.unit << iter <| fun xS -> xS.OnError e
    loop xs |> start
    {new IObservable<'x> with
      override this.Subscribe xS =
       lock subs <| fun () -> subs.Add xS |> ignore
       {new IDisposable with
         override this.Dispose () =
          lock subs <| fun () -> subs.Remove xS |> ignore}}

  let rec indefinitely xJ = xJ |>>* fun x -> Cons (x, indefinitely xJ)

  let once xJ = xJ |>>* fun x -> Cons (x, nil)

  let inline mapfC c = function Cons (x, xs) -> c x xs | Nil -> Nil 
  let inline mapfc c xs = xs |>>? mapfC c
  let inline mapfcm c xs = mapfc c xs |> memo

  let inline mapnc (n: #Job<_>) (c: _ -> _ -> #Job<_>) xs =
    xs >>=? function Cons (x, xs) -> c x xs :> Job<_> | _ -> n :> Job<_>
  let inline mapC (c: _ -> _ -> #Job<_>) =
    function Cons (x, xs) -> c x xs :> Job<_> | Nil -> nila :> Job<_>
  let inline mapc c xs = xs >>=? mapC c
  let inline mapcm c xs = mapc c xs |> memo
  let inline mapncm n c xs = mapnc n c xs |> memo

  let rec chooseJob' x2yOJ x xs =
    x2yOJ x >>= function None -> mapc (chooseJob' x2yOJ) xs
                       | Some y -> consa y (chooseJob x2yOJ xs)
  and chooseJob x2yOJ xs = mapcm (chooseJob' x2yOJ) xs
  let rec chooseFun' x2yO x xs =
    match x2yO x with None -> mapc (chooseFun' x2yO) xs
                    | Some y -> consa y (chooseFun x2yO xs)
  and chooseFun x2yO xs = mapcm (chooseFun' x2yO) xs
  let rec choose' xO xOs =
    match xO with None -> mapc choose' xOs | Some x -> consa x (choose xOs)
  and choose xOs = mapcm choose' xOs

  let rec fj' p x xs =
    p x >>= fun b -> if b then consa x (filterJob p xs) else mapc (fj' p) xs
  and filterJob p xs = mapcm (fj' p) xs
  let rec filterFun' p xs =
    xs >>= function Cons (x, xs) ->
                    if p x then consj x (filterFun p xs) else filterFun' p xs
                  | Nil -> nilj
  and filterFun p xs = filterFun' p xs |> memo

  let rec mapJob x2yJ xs =
    mapcm (fun x xs -> x2yJ x |>> fun y -> Cons (y, mapJob x2yJ xs)) xs
  let rec mapFun x2y xs =
    xs |>>* function Cons (x, xs) -> Cons (x2y x, mapFun x2y xs) | Nil -> Nil

  let amb (ls: Stream<_>) (rs: Stream<_>) = ls <|>* rs

  let rec mergeSwap ls rs = mapnc rs (fun l ls -> consj l (merge rs ls)) ls
  and merge ls rs = mergeSwap ls rs <|>* mergeSwap rs ls

  let rec append l (r: Stream<_>) = mapncm r (fun x l -> consj x (append l r)) l

  let rec switch (ls: Stream<_>) (rs: Stream<_>) =
    rs <|>* mapnc rs (fun l ls -> consj l (switch ls rs)) ls

  let rec joinWith (join: Stream<'x> -> Stream<'y> -> #Stream<'y>)
                   (xxs: Stream<#Stream<'x>>) =
    mapcm (fun xs xxs -> join xs (joinWith join xxs)) xxs

  let rec mapJoin (join: Stream<'y> -> Stream<'z> -> #Stream<'z>)
                  (x2ys: 'x -> #Stream<'y>) (xs: Stream<'x>) : Stream<'z> =
    mapcm (fun x xs -> join (x2ys x) (mapJoin join x2ys xs)) xs

  let ambMap x2ys xs = mapJoin amb x2ys xs
  let mergeMap x2ys xs = mapJoin merge x2ys xs
  let appendMap x2ys xs = mapJoin append x2ys xs
  let switchMap x2ys xs = mapJoin switch x2ys xs

  let rec taker evt skipper xs =
    (evt >>=? fun _ -> Job.start (xs >>= fun t -> skipper <-= t) >>% Nil) <|>*
    (xs >>=? function Nil -> skipper <-= Nil >>% Nil
                    | Cons (x, xs) -> consj x (taker evt skipper xs))
  let takeAndSkipUntil evt xs =
    let skipper = IVar () in (taker evt skipper xs, skipper :> Stream<_>)

  let rec skipUntil evt xs =
    (evt >>=? fun _ -> xs) <|>* mapc (fun _ -> skipUntil evt) xs

  let switchTo rs ls = switch ls rs
  let takeUntil (evt: Alt<_>) xs = switch xs (evt >>%* Nil)

  let rec catch (e2xs: _ -> #Stream<_>) (xs: Stream<_>) =
    Job.tryIn xs (mapC (fun x xs -> consj x (catch e2xs xs))) e2xs |> memo

  // Sampler.regular
  // Sampler.debounce
  // Sampler.throttle
  // Sampler.fromStream
  // Sampler.fromJob

  type Sampler = {Sampler: unit -> Alt<unit>}
  let sampler u2uA = {Sampler = u2uA}
  // debounce
  let restart (timeSpan: TimeSpan) : Sampler =
    let uA = timeOut timeSpan
    {Sampler = fun () -> uA}
  // throttle
  let retain (timeSpan: TimeSpan) : Sampler =
    let timeout = timeOut timeSpan
    {Sampler = fun () ->
      let state = ref None
      let timeout = timeout |>> fun () -> state := None
      Alt.delay <| fun () ->
      match !state with
       | None -> let timeout = memo timeout in state := Some timeout; timeout
       | Some timeout -> timeout}

    // sample
//    let periodic (timeSpan: TimeSpan) : Alt<_> =

  let rec smpl0 t xs = mapcm (smpl1 t) xs
  and smpl1 t x xs = (t |>>? fun _ -> Cons (x, smpl0 t xs)) <|>? mapc (smpl1 t) xs
  let sampling sampler xs = smpl0 (sampler.Sampler ()) xs

  let rec sampleGot0 ts xs =
    mapc (fun _ ts -> sampleGot0 ts xs) ts <|>? mapc (sampleGot1 ts) xs
  and sampleGot1 ts x xs =
    mapfc (fun _ ts -> Cons (x, sample ts xs)) ts <|>? mapc (sampleGot1 ts) xs
  and sample ts xs = sampleGot0 ts xs |> memo

  let rec debounceGot1 timeout x xs =
    (timeout |>>? fun _ -> Cons (x, debounce timeout xs)) <|>?
    (xs >>=? function Cons (x, xs) -> debounceGot1 timeout x xs | Nil -> one x :> Alt<_>)
  and debounce timeout xs = mapcm (debounceGot1 timeout) xs

  let rec throttleGot1 timeout timer x xs =
    (timer |>>? fun _ -> Cons (x, throttle timeout xs)) <|>?
    (mapc (throttleGot1 timeout timer) xs)
  and throttle timeout xs = mapcm (throttleGot1 timeout (memo timeout)) xs

  let rec ysxxs1 ys x xs =
    mapfc (fun y ys -> Cons ((x, y), xsyys1 xs y ys <|>* ysxxs1 ys x xs)) ys
  and xsyys1 xs y ys =
    mapfc (fun x xs -> Cons ((x, y), ysxxs1 ys x xs <|>* xsyys1 xs y ys)) xs
  let rec ysxs0 ys xs = mapc (xsyys0 xs) ys
  and xsyys0 xs y ys = xsyys1 xs y ys <|>? ysxs0 ys xs
  let rec xsys0 xs ys = mapc (ysxxs0 ys) xs
  and ysxxs0 ys x xs = ysxxs1 ys x xs <|>? xsys0 xs ys
  let combineLatest xs ys = xsys0 xs ys <|>* ysxs0 ys xs

  let rec zipXY x y xs ys = Cons ((x, y), zip xs ys)
  and zipX ys x xs = mapfc (fun y ys -> zipXY x y xs ys) ys
  and zipY xs y ys = mapfc (fun x xs -> zipXY x y xs ys) xs
  and zip xs ys = mapcm (zipX ys) xs //<|>* mapc (zipY xs) ys

  let rec zipwfXY f x y xs ys = Cons (f x y, zipWithFun f xs ys)
  and zipwfX f ys x xs =
    ys |>> function Cons (y, ys) -> zipwfXY f x y xs ys | Nil -> Nil
  and zipwfY f xs y ys =
    xs |>> function Cons (x, xs) -> zipwfXY f x y xs ys | Nil -> Nil
  and zipWithFun f xs ys =
    xs >>=* function Cons (x, xs) -> zipwfX f ys x xs | Nil -> nilj

  let rec sj f s x xs = f s x |>> fun s -> Cons (s, mapcm (sj f s) xs)
  let scanJob f s (xs: Stream<_>) = cons s (mapcm (sj f s) xs)
  let rec sf f s x xs = let s = f s x in Cons (s, mapfcm (sf f s) xs)
  let scanFun f s (xs: Stream<_>) = cons s (mapfcm (sf f s) xs)
  let scanFromJob s f xs = scanJob f s xs
  let scanFromFun s f xs = scanFun f s xs

  let rec foldJob f s xs =
    xs >>= function Cons (x, xs) -> f s x >>= fun s -> foldJob f s xs
                  | Nil -> Job.result s
  let rec foldFun f s xs =
    xs >>= function Cons (x, xs) -> foldFun f (f s x) xs | Nil -> Job.result s
  let foldFromJob s f xs = foldJob f s xs
  let foldFromFun s f xs = foldFun f s xs

  let count xs = foldFun (fun s _ -> s+1L) 0L xs

  let rec iterJob (f: _ -> #Job<unit>) xs =
    xs >>= function Cons (x, xs) -> f x >>. iterJob f xs | Nil -> Job.unit ()
  let rec iterFun f xs =
    xs >>= function Cons (x, xs) -> f x ; iterFun f xs | Nil -> Job.unit ()
  let rec iter (xs: Stream<_>) : Job<unit> =
    xs >>= function Cons (_, xs) -> iter xs | Nil -> Job.unit ()

  let toSeq xs = Job.delay <| fun () ->
    let ys = ResizeArray<_>()
    iterFun ys.Add xs >>% ys

  type Evt<'x> =
    | Value of 'x
    | Completed
    | Error of exn

  let pull xs onValue onCompleted onError =
    let cmd = Ch<int> ()
    let rec off xs = cmd >>= on xs
    and on xs n =
      (cmd >>=? fun d -> let n = n+d in if n = 0 then off xs else on xs n) <|>?
      (Alt.tryIn xs <| function Cons (x, xs) -> onValue x >>. on xs n
                              | Nil -> onCompleted () >>. Job.foreverIgnore cmd
                    <| fun e -> onError e >>. Job.foreverIgnore cmd) :> Job<_>
    off xs |> start
    (cmd <-- 1, cmd <-- -1)

  let shift t (xs: Stream<'x>) : Stream<'x> =
    let es = Mailbox<Alt<Evt<'x>>> ()
    let (inc, dec) =
      pull xs <| fun x -> t >>% Value x |> Promise.start >>= fun p -> es <<-+ upcast p
              <| fun () -> es <<-+ Alt.always Completed
              <| fun e -> es <<-+ Alt.always (Error e)
    let es = inc >>. es
    let rec ds () =
      es >>=* fun evt ->
      Job.tryIn evt
       <| function Value x -> dec >>. consj x (ds ())
                 | Completed -> nilj
                 | Error e -> error e :> Job<_>
       <| fun e -> dec >>. error e
    ds ()

  let delayEach job xs = mapJob (fun x -> job >>% x) xs

  let rec afterEach' yJ x xs =
    Promise.queue yJ |>> fun pre -> Cons (x, pre >>.* mapc (afterEach' yJ) xs)
  let afterEach yJ (xs: Stream<_>) = mapcm (afterEach' yJ) xs
  let rec beforeEach yJ xs =
    memo (yJ >>. mapfc (fun x xs -> Cons (x, beforeEach yJ xs)) xs)

  let distinctByJob x2kJ xs = filterJob (x2kJ >> Job.map (HashSet<_>().Add)) xs
  let distinctByFun x2k xs = filterFun (x2k >> HashSet<_>().Add) xs

  let rec ducwj eqJ x' x xs =
    let t = mapc (ducwj eqJ x) xs
    eqJ x' x >>= function true -> t | false -> consa x (memo t)
  let distinctUntilChangedWithJob eqJ (xs: Stream<_>) =
    mapfcm (fun x xs -> Cons (x, mapcm (ducwj eqJ x) xs)) xs

  let rec ducwf eq x' x xs =
    let t = mapc (ducwf eq x) xs in if eq x' x then t else consa x (memo t)
  let distinctUntilChangedWithFun eq (xs: Stream<_>) =
    mapfcm (fun x xs -> Cons (x, mapcm (ducwf eq x) xs)) xs

  let rec ducbj x2kJ k' x xs =
    x2kJ x >>= fun k ->
    let t = mapc (ducbj x2kJ k) xs in if k = k' then t else consa x (memo t)
  let distinctUntilChangedByJob x2kJ (xs: Stream<_>) =
    mapcm (fun x xs -> x2kJ x >>= fun k -> consj x (mapcm (ducbj x2kJ k) xs)) xs

  let rec ducbf x2k k' x xs =
    let k = x2k x
    let t = mapc (ducbf x2k k) xs in if k = k' then t else consa x (memo t)
  let distinctUntilChangedByFun x2k (xs: Stream<_>) =
    mapfcm (fun x xs -> Cons (x, mapcm (ducbf x2k (x2k x)) xs)) xs

  let rec duc x' x xs =
    let t = mapc (duc x) xs in if x' = x then t else consa x (memo t)
  let distinctUntilChanged (xs: Stream<_>) =
    mapfcm (fun x xs -> Cons (x, mapcm (duc x) xs)) xs

  type Group<'k, 'x> = {key: 'k; mutable var: IVar<Cons<'x>>}
  let groupByJob (keyOf: 'x -> #Job<'k>) ss =
    let key2br = Dictionary<'k, Group<'k, 'x>>()
    let main = ref (IVar<_> ())
    let baton = MVar<_>(ss)
    let closes = Ch ()
    let raised e =
      key2br.Values
      |> Seq.iterJob (fun g -> g.var <-=! e) >>. (!main <-=! e) >>! e
    let rec wrap self xs newK oldK oldC =
      (mapfc (fun x xs -> Cons (x, self xs)) xs) <|>*
      (let rec serve ss =
         (closes >>=? fun g ->
            match key2br.TryGetValue g.key with
             | Just g' when obj.ReferenceEquals (g', g) ->
               key2br.Remove g.key |> ignore
               g.var <-= Nil >>. oldC serve ss g.key
             | _ -> serve ss) <|>?
         (Alt.tryIn ss
           <| function
               | Cons (s, ss) ->
                 Job.tryInDelay <| fun () -> keyOf s
                  <| fun k ->
                       match key2br.TryGetValue k with
                        | Just g ->
                          let i = g.var
                          let n = IVar<_> ()
                          g.var <- n
                          i <-= Cons (s, n) >>. oldK serve ss k s n
                        | Nothing ->
                          let i' = IVar<_> ()
                          let i = Alt.always (Cons (s, i'))
                          let g = {key = k; var = i'}
                          key2br.Add (k, g)
                          let i' = IVar<_> ()
                          let m = !main
                          main := i'
                          let close = closes <-+ g
                          let ki = (k, close, wrapBr k i)
                          m <-= Cons (ki, i') >>. newK serve ss ki i'
                  <| raised
               | Nil ->
                 key2br.Values
                 |> Seq.iterJob (fun g -> g.var <-= Nil) >>.
                 (!main <-= Nil) >>% Nil
           <| raised) :> Job<_>
       baton >>=? serve)
    and wrapBr k xs =
      wrap (wrapBr k) xs
       <| fun serve ss _ _ -> serve ss
       <| fun serve ss k' x xs ->
            if k = k' then baton <<-= ss >>% Cons (x, wrapBr k xs) else serve ss
       <| fun serve ss k' ->
            if k = k' then baton <<-= ss >>% Nil else serve ss
    let rec wrapMain xs =
      wrap wrapMain xs
       <| fun _ ss ki i -> baton <<-= ss >>% Cons (ki, wrapMain i)
       <| fun serve ss _ _ _ -> serve ss
       <| fun serve ss _ -> serve ss
    wrapMain (!main)
  let groupByFun keyOf ss = groupByJob (Job.lift keyOf) ss

  let rec skip' n xs =
    if 0L < n
    then xs >>= function Cons (_, xs) -> skip' (n-1L) xs | Nil -> nilj
    else xs :> Job<_>
  let skip n xs = if n < 0L then failwith "skip: n < 0L" else skip' n xs |> memo

  let rec take' n xs =
    if 0L < n then mapfcm (fun x xs -> Cons (x, take' (n-1L) xs)) xs else nil
  let take n xs = if n < 0L then failwith "take: n < 0L" else take' n xs

  let head (xs: Stream<_>) = mapfcm (fun x _ -> Cons (x, nil)) xs
  let tail (s: Stream<_>) = memo (s >>= function Cons (_, t) -> t | Nil -> nil)
  let rec ts' = function Cons (_, xs) -> Cons (xs, xs |>>* ts') | Nil -> Nil
  let tails xs = cons xs (xs |>>* ts')

  let last (s: Stream<_>) = memo << Job.delay <| fun () ->
    let rec lp (r: Job<_>) s =
      Job.tryIn s (function Cons (_, t) -> lp s t | Nil -> r) (fun _ -> r)
    lp s s
  let init (s: Stream<_>) = memo << Job.delay <| fun () ->
    let rec lp x s =
      Job.tryIn s
       (function Cons (h, t) -> consj x (lp h t |> memo) | Nil -> nilj)
       (fun _ -> nilj)
    s >>= function Nil -> nilj | Cons (x, s) -> lp x s
  let inits xs = scanFun (fun n _ -> n+1L) 0L xs |> mapFun (fun n -> take n xs)

  let rec unfoldJob f s =
    f s |>>* function None -> Nil | Some (x, s) -> Cons (x, unfoldJob f s)
  let rec unfoldFun f s = memo << Job.thunk <| fun () ->
    match f s with None -> Nil | Some (x, s) -> Cons (x, unfoldFun f s)

  let rec ij f x = f x |>>* fun x -> Cons (x, ij f x)
  let iterateJob x2xJ x = cons x (ij x2xJ x)

  let rec it f x = memo << Job.thunk <| fun _ -> let x = f x in Cons (x, it f x)
  let iterateFun f x = cons x (it f x)

  let repeat x = fix (cons x)
  let cycle xs = fix (append xs)

  let atDateTimeOffsets dtos =
    dtos
    |> mapJob (fun dto ->
       let ts = dto - DateTimeOffset.Now
       if ts.Ticks <= 0L then Job.result dto else timeOut ts >>% dto)
  let atDateTimeOffset dto = atDateTimeOffsets (one dto)

  let afterTimeSpan ts = once (timeOut ts)

  let values (xs: Stream<'x>) : Alt<'x> =
    let vs = Ch<_>()
    let (inc, dec) =
      pull xs (Choice1Of2 >> Ch.send vs) Job.unit (Choice2Of2 >> Ch.send vs)
    Alt.wrapAbortJob dec
     (Alt.guard (inc >>% vs) >>=? function Choice1Of2 x -> dec >>% x
                                         | Choice2Of2 e -> raise e)

  let rec sumWhileFun plus zero u2b xs = delay <| fun () ->
    if u2b () then plus (xs, sumWhileFun plus zero u2b xs) else zero ()

  type [<AbstractClass>] Builder () =
    member inline this.Bind (xs, x2ys: _ -> Stream<_>) =
      mapJoin (fun x y -> this.Plus (x, y)) x2ys xs
    member inline this.Combine (xs1, xs2) = this.Plus (xs1, xs2)
    member inline this.Delay (u2xs: unit -> Stream<'x>) = delay u2xs
    abstract Zero: unit -> Stream<'x>
    member inline this.For (xs, x2ys: _ -> Stream<_>) =
      this.Bind (ofSeq xs, x2ys)
    member inline this.TryWith (xs, e2xs: _ -> Stream<_>) = catch e2xs xs
    member this.While (u2b, xs) = sumWhileFun this.Plus this.Zero u2b xs
    member inline this.Yield (x) = one x
    member inline this.YieldFrom (xs: Stream<_>) = xs
    abstract Plus: Stream<'x> * Stream<'x> -> Stream<'x>

  let appended = {new Builder () with
    member this.Zero () = nil
    member this.Plus (xs, ys) = append xs ys}
  let merged = {new Builder () with
    member this.Zero () = nil
    member this.Plus (xs, ys) = merge xs ys}

  let ambed = {new Builder () with
    member this.Zero () = never
    member this.Plus (xs, ys) = amb xs ys}
  let switched = {new Builder () with
    member this.Zero () = never
    member this.Plus (xs, ys) = switch xs ys}
