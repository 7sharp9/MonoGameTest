﻿namespace MonoGameTest

open System
open Microsoft.Xna.Framework
open Microsoft.Xna.Framework.Graphics
open Microsoft.Xna.Framework.Input
open MonoGameTest.Core

module App =

    type Sprite =
        {   position: Vector2; speed: float32; texture: Texture2D; size: Point; offset: Point }
        member this.Draw(spriteBatch: SpriteBatch) =
            let sourceRect = Rectangle(this.offset, this.size)
            spriteBatch.Draw(this.texture, this.position, Nullable.op_Implicit sourceRect, Color.White)

    type Model =
        {   Player : Sprite
            GameTime : GameTime
            Game: Game
            SpriteBatch: SpriteBatch }

    type Msg = 
        | Tick of GameTime
        | MovePlayer of Vector2

    let initPlayer (game: Game) =
        let playerSpriteSheet = game.Content.Load<Texture2D>("skeleton")
        {   position = Vector2.Zero
            speed= 166.f
            texture = playerSpriteSheet
            size = Point(64, 64)
            offset = Point(0,128) }

    let initModel game =
        {   Player = initPlayer game
            GameTime = GameTime()
            Game = game
            SpriteBatch = new SpriteBatch(game.GraphicsDevice) }

    let init game () = 
        initModel game, Cmd.none

    let (|KeyDown|_|) k (state: KeyboardState) =
        if state.IsKeyDown k then Some() else None

    let getMovementVector = function
        | KeyDown Keys.W & KeyDown Keys.A -> Vector2(-1.f, -1.f)
        | KeyDown Keys.W & KeyDown Keys.D -> Vector2(1.f, -1.f)
        | KeyDown Keys.S & KeyDown Keys.A -> Vector2(-1.f, 1.f)
        | KeyDown Keys.S & KeyDown Keys.D -> Vector2(1.f, 1.f)
        | KeyDown Keys.W -> Vector2(0.f, -1.f)
        | KeyDown Keys.S -> Vector2(0.f, 1.f)
        | KeyDown Keys.A -> Vector2(-1.f, 0.f)
        | KeyDown Keys.D -> Vector2(1.f, -0.f)
        | _ -> Vector2.Zero

    let update msg model =
        match msg with
        | Tick gt ->
            let nextCmd =
                let motion = Keyboard.GetState() |> getMovementVector
                if motion = Vector2.Zero then Cmd.none
                else motion |> Vector2.Normalize |> MovePlayer |> Cmd.ofMsg
            { model with GameTime = gt}, nextCmd

        | MovePlayer movementVector ->
            let newPosition =
                let newPos =
                    model.Player.position + movementVector * model.Player.speed * float32 model.GameTime.ElapsedGameTime.TotalSeconds
                
                let playerSize = model.Player.size.ToVector2()
                
                let minClamp =  Vector2.Zero - playerSize * 0.5f
                
                let maxClamp =
                    Vector2(float32 model.Game.GraphicsDevice.Viewport.Width,
                            float32 model.Game.GraphicsDevice.Viewport.Height) - playerSize * 0.5f
                
                Vector2.Clamp(newPos, minClamp, maxClamp)

            { model with
                Player = {model.Player with position = newPosition}}, Cmd.none

    let view (model: Model) dispatch =
        model.Game.GraphicsDevice.Clear Color.CornflowerBlue
        model.SpriteBatch.Begin()
        model.Player.Draw model.SpriteBatch
        model.SpriteBatch.End()


type GameApp () as this = 
    inherit ElmishGame ()
    do  this.Content.RootDirectory <- "Content"
        this.IsMouseVisible <- true

    let mapEventSubscription initial =
        let sub dispatch =
            let msgSender msg = msg |> App.Tick |> dispatch
            this.UpdateEvent.Add(msgSender)
        Cmd.ofSub sub

    //example of subscriptions
    // let subscription model =
    //     Cmd.batch [ Cmd.map HourMsg (Hour.subscribe model.hours)
    //                 Cmd.map SecondMsg (Second.subscribe model.seconds) ]
    
    let runner =
            Program.mkProgram (App.init this) App.update App.view
            |> Program.withSubscription mapEventSubscription
            |> Program.withConsoleTrace
            |> Program.runWithDynamicView this

module Program =
    [<EntryPoint>]
    let main argv =
        use game = new GameApp()
        game.Run()
        0 // return an integer exit code
