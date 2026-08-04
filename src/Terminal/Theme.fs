namespace Dotnet.PackageExplorer.Terminal

open System
open Terminal.Gui.Drawing
open Terminal.Gui.ViewBase

type ColorProfile =
    | MidnightViolet
    | Ansi16

type TerminalSchemes =
    { Canvas: Scheme
      Section: Scheme
      Muted: Scheme
      Information: Scheme
      Success: Scheme
      Warning: Scheme
      Failure: Scheme
      Direct: Terminal.Gui.Drawing.Attribute
      Central: Terminal.Gui.Drawing.Attribute }

[<RequireQualifiedAccess>]
module internal Theme =
    let private color16 (name: ColorName16) =
        let mutable value = name
        Color(&value)

    let private attribute (foreground: Color) (background: Color) =
        let mutable foregroundValue = foreground
        let mutable backgroundValue = background
        Terminal.Gui.Drawing.Attribute(&foregroundValue, &backgroundValue)

    let private scheme normal focus =
        Scheme(Normal = normal, Focus = focus, Active = focus, Editable = normal)

    let schemes profile =
        let black = color16 Color.Black
        let white = color16 Color.White

        let purple, blue, selection, muted, direct, success, warning, failure =
            match profile with
            | MidnightViolet ->
                Color(168, 85, 247),
                Color(96, 165, 250),
                Color(30, 64, 112),
                Color(148, 163, 184),
                Color(45, 212, 191),
                Color(74, 222, 128),
                Color(250, 204, 21),
                Color(248, 113, 113)
            | Ansi16 ->
                color16 Color.BrightMagenta,
                color16 Color.BrightBlue,
                color16 Color.Blue,
                color16 Color.DarkGray,
                color16 Color.BrightCyan,
                color16 Color.BrightGreen,
                color16 Color.BrightYellow,
                color16 Color.BrightRed

        { Canvas = scheme (attribute white black) (attribute white selection)
          Section = scheme (attribute purple black) (attribute white selection)
          Muted = scheme (attribute muted black) (attribute white selection)
          Information = scheme (attribute blue black) (attribute white selection)
          Success = scheme (attribute success black) (attribute white selection)
          Warning = scheme (attribute warning black) (attribute white selection)
          Failure = scheme (attribute failure black) (attribute white selection)
          Direct = attribute direct black
          Central = attribute purple black }

    let detect (getEnvironmentVariable: string -> string) =
        let colorTerm = getEnvironmentVariable "COLORTERM"
        let term = getEnvironmentVariable "TERM"

        if
            colorTerm.Contains("truecolor", StringComparison.OrdinalIgnoreCase)
            || colorTerm.Contains("24bit", StringComparison.OrdinalIgnoreCase)
            || term.Contains("direct", StringComparison.OrdinalIgnoreCase)
        then
            MidnightViolet
        else
            Ansi16

    let apply (scheme: Scheme) (view: View) = view.SetScheme scheme |> ignore
