namespace MonoGameTest.Core

open System
open System.Diagnostics
open System.Threading
open Microsoft.Xna.Framework
open Microsoft.Xna.Framework.Graphics
open Microsoft.Xna.Framework.Input

/// Dispatch - feed new message into the processing loop
type Dispatch<'msg> = 'msg -> unit

/// Subscription - return immediately, but may schedule dispatch of a message at any time
type Sub<'msg> = Dispatch<'msg> -> unit

/// Cmd - container for subscriptions that may produce messages
type Cmd<'msg> = Sub<'msg> list

/// Cmd module for creating and manipulating commands
[<RequireQualifiedAccess>]
module Cmd =
    /// None - no commands, also known as `[]`
    let none : Cmd<'msg> =
        []

    /// Command to issue a specific message
    let ofMsg (msg:'msg) : Cmd<'msg> =
        [fun dispatch -> dispatch msg]

    /// Command to issue a specific message, only when Option.IsSome = true
    let ofMsgOption (msg:'msg option) : Cmd<'msg> =
        [fun dispatch -> match msg with None -> () | Some msg -> dispatch msg]

    /// When emitting the message, map to another type
    let map (f: 'a -> 'msg) (cmd: Cmd<'a>) : Cmd<'msg> =
        cmd |> List.map (fun g -> (fun dispatch -> f >> dispatch) >> g)

    /// Aggregate multiple commands
    let batch (cmds: #seq<Cmd<'msg>>) : Cmd<'msg> =
        cmds |> List.concat

    /// Command to call the subscriber
    let ofSub (sub: Sub<'msg>) : Cmd<'msg> =
        [sub]

    let dispatch d (cmd: Cmd<_>) = for sub in cmd do sub d

    /// Command to issue a message at the end of an asynchronous task
    let ofAsyncMsg (p: Async<'msg>) : Cmd<'msg> =
        [ fun dispatch -> async { let! msg = p in dispatch msg } |> Async.StartImmediate ]

    /// Command to issue a message at the end of an asynchronous task, only when Option.IsSome = true
    let ofAsyncMsgOption (p: Async<'msg option>) : Cmd<'msg> =
        [ fun dispatch -> async { 
            let! msg = p
            match msg with
            | None -> ()
            | Some msg -> dispatch msg } |> Async.StartImmediate ]

/// Program type captures various aspects of program behavior
type Program<'model, 'msg, 'view> =
    {   init : unit -> ('model * Cmd<'msg>)
        update : 'msg -> 'model -> ('model * Cmd<'msg>)
        subscribe : 'model -> Cmd<'msg>
        view : 'view
        debug : bool
        onError : string * exn -> unit }

/// We store the current dispatch function for the running Elmish program as a
/// static-global thunk because we want old view elements stored in the `dependsOn` global table
/// to be recyclable on resumption (when a new ProgramRunner gets created).
type internal ProgramDispatch<'msg>() =
    static let mutable dispatchImpl =
        (fun (_msg : 'msg) ->
        failwith "do not call dispatch during initialization" : unit)
    static let dispatch = id (fun msg -> dispatchImpl msg)
    static member DispatchViaThunk = dispatch
    static member SetDispatchThunk v = dispatchImpl <- v


        
type ElmishGame(?height, ?width) as this =
    inherit Game()
    let width = defaultArg width 1280
    let height = defaultArg height 720
    let graphics =
        new GraphicsDeviceManager(  this,
                                    PreferredBackBufferWidth = width,
                                    PreferredBackBufferHeight = height)

    let draw = Event<GameTime>()
    let update = Event<GameTime>()
    let init = Event<_>()
    
    [<CLIEvent>] member this.DrawEvent = draw.Publish
    [<CLIEvent>] member this.UpdateEvent = update.Publish
    [<CLIEvent>] member this.InitEvent = init.Publish

    override this.Draw(gt) =
        base.Draw(gt)
        draw.Trigger gt

    override this.Update(gt) =
        base.Update(gt)
        update.Trigger gt

    override this.Initialize() =
        base.Initialize()
        init.Trigger()

/// Starts the Elmish dispatch loop for the page with the given Elmish program
type ProgramRunner<'model, 'msg>(app : ElmishGame, program : Program<'model, 'msg, 'model -> ('msg -> unit) -> unit>) =
    do Debug.WriteLine "run: computing initial model"
    
    let mutable alternativeRunner : ProgramRunner<obj, obj> option = None
    let mutable lastModel = Unchecked.defaultof<_>
    let dispatch = ProgramDispatch<'msg>.DispatchViaThunk
    let mutable reset = (fun () -> ())

    // If the view is dynamic, create the initial view
    //this cant be called before init
    //do program.view initialModel dispatch

    // Start Elmish dispatch loop
    let rec processMsg msg =
        try
            let (updatedModel, newCommands) = program.update msg lastModel
            lastModel <- updatedModel

            for sub in newCommands do
                try
                    sub dispatch
                with ex -> program.onError ("Error executing commands:", ex)
        with ex -> program.onError ("Unable to process a message:", ex)

    and updateView updatedModel =
        try
            program.view updatedModel dispatch 
            
        with ex ->
            program.onError ("Unable to evaluate view:", ex)

    let startLoop() =
        let (initialModel, cmd) = program.init()
        lastModel <- initialModel
        // Set up the global dispatch function
        ProgramDispatch<'msg>.SetDispatchThunk processMsg

        reset <- (fun () ->
                        async {
                            //suppress this?
                            //do updateView lastModel 
                            ()
                            }
                        |> Async.StartImmediate)


        Debug.WriteLine "dispatching initial commands"
        for sub in (program.subscribe initialModel @ cmd) do
            try
                sub dispatch
            with ex -> program.onError ("Error executing commands:", ex)

    do 
        app.InitEvent.Add (fun () -> startLoop() )
        app.DrawEvent.Add (fun gt -> updateView lastModel )


    member __.UpdateView() = updateView lastModel
    //member __.InitialMainPage = mainPage
    member __.CurrentModel = lastModel
    member __.Dispatch(msg) = dispatch msg

    member runner.ChangeProgram(newProgram: Program<obj,obj,(obj -> (obj -> unit) -> unit)>)  : unit =
        async {
            // TODO: transmogrify the model
            do alternativeRunner <- Some (ProgramRunner<obj, obj>(app, newProgram))
        } |> Async.StartImmediate

    member __.ResetView() : unit =
        async {
            match alternativeRunner with
            | Some r -> r.ResetView()
            | None -> reset() } |> Async.StartImmediate


    /// Set the current model, e.g. on resume
    member __.SetCurrentModel(model, cmd : Cmd<_>) =
        async {
            match alternativeRunner with
            | Some _ ->
                // TODO: transmogrify the resurrected model
                printfn
                    "SetCurrentModel: ignoring (can't the model after ChangeProgram has been called)"
            | None ->
                Debug.WriteLine "updating the view after setting the model"
                lastModel <- model
                //updateView model
                for sub in program.subscribe model @ cmd do
                    sub dispatch
        } |> Async.StartImmediate

/// Program module - functions to manipulate program instances
[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Program =
    let internal onError (text: string, ex: exn) = 
        Console.WriteLine (sprintf "%s: %A" text ex)

    /// Typical program, new commands are produced by `init` and `update` along with the new state.
    let mkProgram init update view =
        {   init = init
            update = update
            view = view
            subscribe = fun _model -> Cmd.none
            debug = false
            onError = onError }

    /// Simple program that produces only new state with `init` and `update`.
    let mkSimple init update view = 
        mkProgram (fun arg -> init arg, Cmd.none) (fun msg model -> update msg model, Cmd.none) view

    /// Subscribe to external source of events.
    /// The subscription is called once - with the initial (or resumed) model, but can dispatch new messages at any time.
    let withSubscription (subscribe : 'model -> Cmd<'msg>) (program: Program<'model, 'msg, 'view>) =
        let sub model =
            Cmd.batch [ program.subscribe model
                        subscribe model ]
        { program with subscribe = sub }

    /// Trace all the updates to the console
    let withConsoleTrace (program: Program<'model, 'msg, _>) =
        let traceInit () =
            try 
                let initModel,cmd = program.init ()
                Console.WriteLine (sprintf "Initial model: %0A" initModel)
                initModel,cmd
            with e -> 
                Console.WriteLine (sprintf "Error in init function: %0A" e)
                reraise ()

        let traceUpdate msg model =
            Console.WriteLine (sprintf "traceUpdate: msg: %0A" msg)
            try 
                let newModel, cmd = program.update msg model
                Console.WriteLine (sprintf "traceUpdate: Updated model: %0A, cmd: %0A" newModel cmd)
                newModel,cmd
            with e -> 
                Console.WriteLine (sprintf "traceUpdate: Error in model function: %0A" e)
                reraise ()

        let traceView model dispatch =
            Console.WriteLine (sprintf "traceView: View, model = %0A" model)
            try 
                let info = program.view model dispatch
                Console.WriteLine (sprintf "traceView: View result: %0A" info)
                info
            with e -> 
                Console.WriteLine (sprintf "traceView: Error in view function: %0A" e)
                reraise ()
                
        { program with
            init = traceInit 
            update = traceUpdate
            view = traceView }


    /// Trace all the messages as they update the model
    let withTrace trace (program: Program<'model, 'msg, 'view>) =
        { program
            with update = fun msg model -> trace msg model; program.update msg model}

    /// Handle dispatch loop exceptions
    let withErrorHandler onError (program: Program<'model, 'msg, 'view>) =
        { program
            with onError = onError }

    /// Set debugging to true
    let withDebug program = 
        { program with debug = true }

    /// Run the app with dynamic views for a specific application
    let runWithDynamicView (app : ElmishGame) (program: Program<'model, 'msg, _>) = 
        ProgramRunner(app, program)