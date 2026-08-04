namespace Dotnet.PackageExplorer.Terminal.UnitTests

open Dotnet.PackageExplorer.Application
open Dotnet.PackageExplorer.Terminal
open FsUnit.Xunit
open Terminal.Gui.Input
open Xunit

[<Sealed>]
type KeyboardTests() =
    [<Fact>]
    member _.``the fixed keyboard contract maps every discoverable package explorer key``() =
        [ Key.Tab, NextMode
          Key.Tab.WithShift, PreviousMode
          Key.D1, SelectMode Browse
          Key.D2, SelectMode Installed
          Key.D3, SelectMode Updates
          Key.D4, SelectMode Consolidate
          Key.J, MoveRow 1
          Key.K, MoveRow -1
          Key.H, MoveHorizontal -1
          Key.L, MoveHorizontal 1
          Key.H.WithCtrl, MovePane -1
          Key.L.WithCtrl, MovePane 1
          Key.S, OpenSort
          Key('/'), FocusSearch
          Key.Space, ToggleSelection
          Key.Enter, Activate
          Key.P, Preview
          Key.R, TerminalAction.Refresh
          Key.Esc, Back
          Key.Q, Quit ]
        |> List.iter (fun (key, expected) -> Keyboard.action key |> should equal (Some expected))

    [<Fact>]
    member _.``unassigned keys remain available to focused Terminal Gui controls``() =
        Keyboard.action Key.F1 |> should equal None
