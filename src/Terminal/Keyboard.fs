namespace Dotnet.PackageExplorer.Terminal

open Dotnet.PackageExplorer.Application
open Terminal.Gui.Input

type TerminalAction =
    | NextMode
    | PreviousMode
    | SelectMode of PackageMode
    | MoveRow of int
    | MoveHorizontal of int
    | MovePane of int
    | OpenSort
    | FocusSearch
    | ToggleSelection
    | Activate
    | Preview
    | RefreshPackages
    | ShowHelp
    | Back
    | Quit

[<RequireQualifiedAccess>]
module internal Keyboard =
    let private matches (expected: Key) (actual: Key) = expected.KeyCode = actual.KeyCode

    let action (key: Key) =
        if matches Key.Tab.WithShift key then Some PreviousMode
        elif matches Key.Tab key then Some NextMode
        elif matches Key.D1 key then Some(SelectMode Browse)
        elif matches Key.D2 key then Some(SelectMode Installed)
        elif matches Key.D3 key then Some(SelectMode Updates)
        elif matches Key.D4 key then Some(SelectMode Consolidate)
        elif matches Key.J key then Some(MoveRow 1)
        elif matches Key.K key then Some(MoveRow -1)
        elif matches Key.H.WithCtrl key then Some(MovePane -1)
        elif matches Key.L.WithCtrl key then Some(MovePane 1)
        elif matches Key.H key then Some(MoveHorizontal -1)
        elif matches Key.L key then Some(MoveHorizontal 1)
        elif matches Key.S key then Some OpenSort
        elif matches (Key '/') key then Some FocusSearch
        elif matches Key.Space key then Some ToggleSelection
        elif matches Key.Enter key then Some Activate
        elif matches Key.P key then Some Preview
        elif matches Key.R key then Some RefreshPackages
        elif matches (Key '?') key then Some ShowHelp
        elif matches Key.Esc key then Some Back
        elif matches Key.Q key then Some Quit
        else None
