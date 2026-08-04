namespace Dotnet.PackageExplorer.Terminal.IntegrationTests

open System
open Dotnet.PackageExplorer.Application
open Dotnet.PackageExplorer.RpcClient
open Dotnet.PackageExplorer.Terminal
open Dotnet.PackageExplorer.Terminal.UnitTests.TestData
open FsUnit.Xunit
open Terminal.Gui.App
open Terminal.Gui.Drivers
open Terminal.Gui.Drawing
open Terminal.Gui.Input
open Xunit

[<RequireQualifiedAccess>]
module private Driver =
    let private required (driver: IDriver | null) =
        driver
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failwith "The ANSI driver is unavailable.")

    let private cell (driver: IDriver | null) row column =
        let active = required driver

        let contents =
            active.Contents
            |> Option.ofObj
            |> Option.defaultWith (fun () -> failwith "The ANSI screen buffer is unavailable.")

        if contents.GetLength 0 = active.Rows then
            contents[row, column]
        else
            contents[column, row]

    let rows (driver: IDriver | null) =
        let active = required driver

        [ for row in 0 .. active.Rows - 1 do
              yield
                  [ for column in 0 .. active.Cols - 1 do
                        yield (cell driver row column).Grapheme ]
                  |> String.concat "" ]

    let text driver =
        rows driver |> String.concat Environment.NewLine

    let cells (driver: IDriver | null) =
        let active = required driver

        Array.init active.Rows (fun row ->
            Array.init active.Cols (fun column ->
                let value = cell driver row column
                let attribute = value.Attribute.GetValueOrDefault()

                value.Grapheme, attribute.Foreground.ToString(), attribute.Background.ToString()))

[<Sealed>]
type AnsiDriverTests() =
    let longPackage =
        package
            "Microsoft.Extensions.DependencyInjection"
            (Some Direct)
            (Some "1.2.3-alpha.1")
            (Some "10.0.0-rc.2")

    let dependencyInjection =
        package
            "Microsoft.Extensions.DependencyInjection"
            (Some Direct)
            (Some "8.0.0")
            (Some "9.0.0")

    let solutionPersistence =
        package
            "Microsoft.VisualStudio.SolutionPersistence"
            (Some Central)
            (Some "1.0.0")
            (Some "1.1.0")

    let releasePackages =
        [ package "Humanizer.Core" (Some Direct) (Some "2.8.26") (Some "3.0.10")
          package "Newtonsoft.Json" (Some Direct) (Some "12.0.1") (Some "13.0.4")
          package "Serilog.AspNetCore" (Some Central) (Some "8.0.3") (Some "9.0.0")
          package "FSharp.Core" (Some Framework) (Some "10.1.302") (Some "10.1.302")
          package
              "Microsoft.Extensions.DependencyInjection"
              (Some Transitive)
              (Some "9.0.7")
              (Some "10.0.0")
          package "Terminal.Gui" (Some Direct) (Some "2.4.18") (Some "2.5.0")
          package "Markdig" (Some Transitive) (Some "0.41.3") (Some "0.42.0")
          package "MessagePack" (Some Central) (Some "3.1.8") (Some "3.2.0")
          package "FsToolkit.ErrorHandling" (Some Direct) (Some "4.18.0") (Some "5.0.0")
          package
              "Microsoft.VisualStudio.SolutionPersistence"
              (Some Transitive)
              (Some "1.0.52")
              (Some "1.1.0")
          package "NuGet.Protocol" (Some Central) (Some "7.0.0") (Some "7.1.0")
          package "Microsoft.Testing.Platform" (Some Transitive) (Some "1.7.3") (Some "2.0.0") ]

    let releasePreview =
        { preview with
            Projects =
                [ { Project = ProjectId "Application"
                    Framework = Some(TargetFramework "net10.0")
                    Before = Some(PackageVersion "2.8.26")
                    After = Some(PackageVersion "3.0.10") }
                  { Project = ProjectId "RpcClient"
                    Framework = Some(TargetFramework "net9.0")
                    Before = Some(PackageVersion "12.0.1")
                    After = Some(PackageVersion "13.0.4") }
                  { Project = ProjectId "Terminal"
                    Framework = Some(TargetFramework "net10.0")
                    Before = Some(PackageVersion "2.8.26")
                    After = Some(PackageVersion "3.0.10") }
                  { Project = ProjectId "Tests.Integration"
                    Framework = Some(TargetFramework "net8.0")
                    Before = Some(PackageVersion "12.0.1")
                    After = Some(PackageVersion "13.0.4") } ] }

    let renderBufferWithKeys profile width height initial keys =
        use application = Application.Create().Init "ansi"

        application.Driver
        |> Option.ofObj
        |> Option.iter (fun driver -> driver.SetScreenSize(width, height))

        application.StopAfterFirstIteration <- true
        use window = new ExplorerWindow(initial, ignore, application.RequestStop, profile)
        use _keyboard = window.BindKeyboard application.Keyboard

        keys
        |> List.iter (fun key -> application.Keyboard.RaiseKeyDownEvent key |> ignore)

        application.Run window |> ignore
        Driver.rows application.Driver, Driver.cells application.Driver

    let renderWithKeys width height initial keys =
        renderBufferWithKeys Ansi16 width height initial keys |> fst

    let renderTextWithKeys width height initial keys =
        renderWithKeys width height initial keys |> String.concat Environment.NewLine

    let renderTextAfter width height initial keys states =
        use application = Application.Create().Init "ansi"

        application.Driver
        |> Option.ofObj
        |> Option.iter (fun driver -> driver.SetScreenSize(width, height))

        application.StopAfterFirstIteration <- true
        use window = new ExplorerWindow(initial, ignore, application.RequestStop, Ansi16)
        use _keyboard = window.BindKeyboard application.Keyboard

        keys
        |> List.iter (fun key -> application.Keyboard.RaiseKeyDownEvent key |> ignore)

        states |> List.iter window.Render
        application.Run window |> ignore
        Driver.text application.Driver

    let render width initial = renderTextWithKeys width 35 initial []

    let contextViewport width height initial scrollRows =
        use application = Application.Create().Init "ansi"

        application.Driver
        |> Option.ofObj
        |> Option.iter (fun driver -> driver.SetScreenSize(width, height))

        use window = new ExplorerWindow(initial, ignore, application.RequestStop, Ansi16)

        use _keyboard = window.BindKeyboard application.Keyboard
        let mutable frame = Drawing.Rectangle.Empty
        let mutable viewport = Drawing.Rectangle.Empty
        let mutable parentViewport = Drawing.Rectangle.Empty
        let mutable scrolled = false

        application.Invoke(
            Action(fun () ->
                application.Keyboard.RaiseKeyDownEvent Key.L.WithCtrl |> ignore

                let active =
                    window.MostFocused
                    |> Option.ofObj
                    |> Option.defaultWith (fun () -> failwith "Context did not receive focus.")

                frame <- active.Frame
                viewport <- active.Viewport

                parentViewport <-
                    active.SuperView
                    |> Option.ofObj
                    |> Option.map _.Viewport
                    |> Option.defaultValue Drawing.Rectangle.Empty

                if scrollRows > 0 then
                    scrolled <-
                        active.ScrollVertical(scrollRows).GetValueOrDefault()
                        && active.Viewport.Y > 0

                application.RequestStop())
        )

        application.Run window |> ignore
        frame, viewport, parentViewport, scrolled

    let contextScrollbar width height initial =
        use application = Application.Create().Init "ansi"

        application.Driver
        |> Option.ofObj
        |> Option.iter (fun driver -> driver.SetScreenSize(width, height))

        application.StopAfterFirstIteration <- true
        use window = new ExplorerWindow(initial, ignore, application.RequestStop, Ansi16)
        use _keyboard = window.BindKeyboard application.Keyboard

        application.Keyboard.RaiseKeyDownEvent Key.L.WithCtrl |> ignore
        application.Run window |> ignore

        let active =
            window.MostFocused
            |> Option.ofObj
            |> Option.defaultWith (fun () -> failwith "Context did not receive focus.")

        active.FrameToScreen(), active.Viewport, Driver.cells application.Driver

    [<Fact>]
    member _.``Terminal Gui v2 instance host renders one bounded ANSI driver iteration``() =
        use application = Application.Create().Init "ansi"

        application.Driver
        |> Option.ofObj
        |> Option.iter (fun driver -> driver.SetScreenSize(120, 35))

        application.StopAfterFirstIteration <- true
        use window = new ExplorerWindow(model (), ignore, application.RequestStop, Ansi16)

        application.Run window |> ignore

        let screen = Driver.text application.Driver
        screen.Contains("Direct.Package", StringComparison.Ordinal) |> should equal true

        screen.Contains("Central.Package", StringComparison.Ordinal)
        |> should equal true

    [<Fact>]
    member _.``wide ANSI layout renders the package list beside its context``() =
        let details =
            { model () with
                Route = Content(PackageDetails direct.Id) }

        let wide = render 160 details
        wide.Contains("Central.Package", StringComparison.Ordinal) |> should equal true
        wide.Contains("Versions", StringComparison.Ordinal) |> should equal true

    [<Fact>]
    member _.``short content is centered wide and starts below tabs compact``() =
        let centeredRoutes =
            [ { model () with
                  Route = Content(PackageDetails direct.Id) }
              { model () with
                  Route = Content(OperationPreview(preview, Summary)) }
              { model () with
                  Route = Content(OperationPreview(preview, Dependencies)) } ]

        centeredRoutes
        |> List.iter (fun initial ->
            let wideFrame, wideViewport, wideParent, _ = contextViewport 126 34 initial 0

            let leftMargin = wideFrame.X
            let rightMargin = wideParent.Width - wideFrame.X - wideFrame.Width

            abs (leftMargin - rightMargin) |> should be (lessThanOrEqualTo 1)
            wideFrame.Width |> should equal (min 64 wideParent.Width)
            wideFrame.Y |> should be (greaterThan 2)
            wideFrame.Height |> should equal 12
            wideViewport.Width |> should be (lessThanOrEqualTo 64)
            wideViewport.Height |> should be (lessThanOrEqualTo 12)

            let compactFrame, compactViewport, compactParent, _ =
                contextViewport 90 30 initial 0

            compactFrame.X |> should equal 0
            compactFrame.Y |> should be (greaterThanOrEqualTo 2)
            compactFrame.Y |> should be (lessThanOrEqualTo 3)
            compactFrame.Width |> should equal compactParent.Width
            compactViewport.Width |> should be (greaterThan 64)
            compactViewport.Height |> should be (greaterThanOrEqualTo 12))

        let details =
            { model () with
                Route = Content(PackageDetails direct.Id) }

        let _, _, _, wideScrolled = contextViewport 126 34 details 4
        let _, _, _, compactScrolled = contextViewport 90 30 details 4
        wideScrolled |> should equal true
        compactScrolled |> should equal true

    [<Fact>]
    member _.``README targets and preview files retain their full content pane``() =
        [ { model () with
              Route = Content(PackageReadme direct.Id) }
          { model () with
              Route = Content(PackageTargeting direct.Id) }
          { model () with
              Route = Content(OperationPreview(preview, Files)) } ]
        |> List.iter (fun initial ->
            let wideFrame, wideViewport, wideParent, _ = contextViewport 126 34 initial 0

            wideFrame.X |> should equal 0
            wideFrame.Width |> should equal wideParent.Width
            wideViewport.Width |> should be (greaterThan 40)

            let compactFrame, compactViewport, compactParent, _ =
                contextViewport 90 30 initial 0

            compactFrame.X |> should equal 0
            compactFrame.Width |> should equal compactParent.Width
            compactViewport.Width |> should be (greaterThan 64))

    [<Fact>]
    member _.``overflow uses blank tracks with visible controls and short content has no scrollbar``
        ()
        =
        let longReadme =
            [ 1..40 ]
            |> List.map (fun line -> $"## Section {line}\n\nContent for section {line}.")
            |> String.concat "\n\n"

        let readme =
            { model () with
                Route = Content(PackageReadme direct.Id)
                Readmes =
                    Map.ofList
                        [ direct.Id,
                          { Package = direct.Id
                            CommonMark = longReadme } ] }

        let projects =
            [ 1..12 ]
            |> List.map (fun index ->
                project ("Project" + string index) [ "net10.0"; "net9.0"; "net8.0" ])

        let targets =
            { model () with
                Target = Solution("Example.slnx", projects)
                Route = Content(PackageTargeting direct.Id)
                Focus = ProjectRow projects.Head.Id }

        [ 126, 34; 90, 30 ]
        |> List.iter (fun (width, height) ->
            [ readme; targets ]
            |> List.iter (fun initial ->
                let frame, viewport, cells = contextScrollbar width height initial
                let scrollbarColumn = frame.Right - 1
                let topRow = frame.Top
                let bottomRow = frame.Bottom - 1
                let up, upForeground, upBackground = cells[topRow][scrollbarColumn]
                let down, downForeground, downBackground = cells[bottomRow][scrollbarColumn]

                up |> should not' (equal " ")
                down |> should not' (equal " ")
                upForeground |> should not' (equal upBackground)
                downForeground |> should not' (equal downBackground)

                let track =
                    cells[topRow + 1 .. bottomRow - 1]
                    |> Array.map (fun row -> row[scrollbarColumn])

                track |> Array.exists (fun (glyph, _, _) -> glyph = " ") |> should equal true

                track
                |> Array.exists (fun (glyph, foreground, background) ->
                    glyph <> " " && foreground <> background)
                |> should equal true

                viewport.Width |> should equal (frame.Width - 1)

                let _, _, _, scrolled = contextViewport width height initial 4
                scrolled |> should equal true)

            let shortPreview =
                { preview with
                    Summary = [ "Update Direct.Package." ] }

            let shortContent =
                { model () with
                    Route = Content(OperationPreview(shortPreview, Summary)) }

            let frame, viewport, cells = contextScrollbar width height shortContent

            let visibleGlyphs =
                cells[frame.Top .. frame.Bottom - 1]
                |> Array.collect (fun row -> row[frame.Left .. frame.Right - 1])
                |> Array.map (fun (glyph, _, _) -> glyph)

            visibleGlyphs |> should not' (contain (Glyphs.UpArrow.ToString()))
            visibleGlyphs |> should not' (contain (Glyphs.DownArrow.ToString()))
            viewport.Width |> should equal frame.Width)

    [<Fact>]
    member _.``preview projects balance comparison columns without clipping identity``() =
        let projects =
            { model () with
                Route = Content(OperationPreview(releasePreview, Projects)) }

        [ 126, 34; 90, 30 ]
        |> List.iter (fun (width, height) ->
            let rows = renderWithKeys width height projects [ Key.L.WithCtrl ]

            let header =
                rows
                |> List.find (fun row ->
                    [ "Project"; "Framework"; "Current"; "Proposed" ]
                    |> List.forall (fun heading ->
                        row.Contains(heading, StringComparison.Ordinal)))

            let projectStart = header.IndexOf("Project", StringComparison.Ordinal)
            let frameworkStart = header.IndexOf("Framework", StringComparison.Ordinal)
            let currentStart = header.IndexOf("Current", StringComparison.Ordinal)
            let proposedStart = header.IndexOf("Proposed", StringComparison.Ordinal)
            let contextRight = width - 2
            let projectWidth = frameworkStart - projectStart - 1
            let currentWidth = proposedStart - currentStart - 1
            let proposedWidth = contextRight - proposedStart

            currentWidth |> should equal proposedWidth
            proposedWidth |> should be (lessThanOrEqualTo projectWidth)

            let screen = String.concat Environment.NewLine rows

            [ "Tests.Integration"; "net8.0"; "12.0.1"; "13.0.4" ]
            |> List.iter (fun value ->
                screen.Contains(value, StringComparison.Ordinal) |> should equal true)

            let focusedRows, focusedCells =
                renderBufferWithKeys MidnightViolet width height projects [ Key.L.WithCtrl ]

            let focusedHeaderRow =
                focusedRows
                |> List.findIndex (fun row ->
                    [ "Project"; "Framework"; "Current"; "Proposed" ]
                    |> List.forall (fun heading ->
                        row.Contains(heading, StringComparison.Ordinal)))

            let focusedHeader = focusedRows[focusedHeaderRow]
            let focusedProject = focusedHeader.IndexOf("Project", StringComparison.Ordinal)

            let _, headerForeground, headerBackground =
                focusedCells[focusedHeaderRow][focusedProject]

            headerForeground |> should equal "White"
            headerBackground |> should equal "Black"

            focusedCells[focusedHeaderRow]
            |> Array.skip focusedProject
            |> Array.take (width - 2 - focusedProject)
            |> Array.iter (fun (_, _, background) -> background |> should equal "Black")

            let applicationRow =
                focusedRows
                |> List.findIndex (_.Contains("Application", StringComparison.Ordinal))

            let applicationColumn =
                focusedRows[applicationRow].IndexOf("Application", StringComparison.Ordinal)

            let _, applicationForeground, applicationBackground =
                focusedCells[applicationRow][applicationColumn]

            applicationForeground |> should equal "White"
            applicationBackground |> should equal "Black")

    [<Fact>]
    member _.``wide retained context keeps the full release package identities``() =
        let routes =
            [ PackageDetails direct.Id
              OperationPreview(preview, Summary)
              OperationPreview(preview, Dependencies)
              OperationPreview(preview, Projects) ]

        routes
        |> List.iter (fun route ->
            let initial =
                { model () with
                    Packages = releasePackages
                    ActivePackage = Some releasePackages.Head.Id
                    Route = Content route }

            let screen = renderTextWithKeys 126 34 initial []

            [ dependencyInjection.DisplayName; solutionPersistence.DisplayName ]
            |> List.iter (fun packageId ->
                if not (screen.Contains(packageId, StringComparison.Ordinal)) then
                    failwith
                        $"Retained {route} context clipped {packageId}.{Environment.NewLine}{screen}"))

    [<Fact>]
    member _.``compact routes reserve one guidance row and one combined status route row``() =
        let failure =
            { Scope = BackendSessionFailure
              Kind = BackendUnavailable
              Message = "Workspace Explorer is unavailable." }

        let routes =
            [ model (), "Enter details", "Ready", "Packages / List"
              { model () with
                  Route = Content(OperationConfirmation preview)
                  Pending.Apply = Some(RequestToken 20L) },
              "? Help",
              "Applying package changes",
              "Packages / Applying"
              { model () with
                  Route = Failure(PackageList, BackendSessionFailure)
                  Failures = Map.ofList [ BackendSessionFailure, failure ] },
              "Esc dismiss",
              "Action required",
              "Packages / Failure" ]

        routes
        |> List.iter (fun (initial, expectedAction, expectedStatus, expectedRoute) ->
            let rows = renderWithKeys 90 30 initial []

            let guidanceRow =
                rows |> List.findIndex (_.Contains(expectedAction, StringComparison.Ordinal))

            let statusRow =
                rows
                |> List.findIndex (fun row ->
                    row.Contains(expectedStatus, StringComparison.Ordinal)
                    && row.Contains(expectedRoute, StringComparison.Ordinal))

            guidanceRow |> should equal (rows.Length - 3)
            statusRow |> should equal (rows.Length - 2)
            guidanceRow |> should equal (statusRow - 1)
            rows[guidanceRow].Contains("? Help") |> should equal true)

    [<Fact>]
    member _.``Help is a complete masked surface at wide and compact sizes``() =
        [ 126, 34; 90, 30 ]
        |> List.iter (fun (width, height) ->
            let help = renderTextWithKeys width height (model ()) [ Key '?' ]

            [ "Current actions"
              "Tab / Shift-Tab"
              "1-4"
              "j/k or Down/Up"
              "h/l or Left/Right"
              "Ctrl-h / Ctrl-l"
              "s"
              "/"
              "Space"
              "Enter"
              "p"
              "r"
              "Esc              Back or cancel outside Help"
              "? / Esc"
              "q" ]
            |> List.iter (fun binding ->
                help.Contains(binding, StringComparison.Ordinal) |> should equal true)

            help.Contains("Enter details") |> should equal true
            help.Contains("Direct.Package") |> should equal false)

    [<Fact>]
    member _.``sort is a focused masked surface with local guidance``() =
        [ 126, 34; 90, 30 ]
        |> List.iter (fun (width, height) ->
            let field = renderTextWithKeys width height (model ()) [ Key.S ]

            let initialDirection =
                match (model ()).Sort.Direction with
                | Ascending -> "Ascending"
                | Descending -> "Descending"

            field.Contains("[s] Sort packages") |> should equal true
            field.Contains("> Field") |> should equal true
            field.Contains($"Direction   {initialDirection}") |> should equal true
            field.Contains("Enter Apply") |> should equal true
            field.Contains("Esc Close") |> should equal true
            field.Contains("? Help") |> should equal true
            field.Contains("Direct.Package") |> should equal false
            field.Contains("Ready") |> should equal false
            field.Contains("j/k move") |> should equal false

            let direction = renderTextWithKeys width height (model ()) [ Key.S; Key.J ]

            direction.Contains("> Direction") |> should equal true

            let help = renderTextWithKeys width height (model ()) [ Key.S; Key '?' ]

            help.Contains("Current actions") |> should equal true
            help.Contains("h/l field") |> should equal true
            help.Contains("[s] Sort packages") |> should equal false)

    [<Fact>]
    member _.``wide and compact lists show focus semantics and responsive version deltas``() =
        [ 126, 34; 90, 30 ]
        |> List.iter (fun (width, height) ->
            let initial =
                { model () with
                    Mode = Updates
                    Packages = [ longPackage; central ]
                    ActivePackage = Some longPackage.Id
                    SelectedPackages = Set.singleton central.Id }

            let screen = renderTextWithKeys width height initial []
            screen.Contains("Current") |> should equal true
            screen.Contains("Latest") |> should equal true
            screen.Contains("Kind") |> should equal true
            screen.Contains("> [ ]") |> should equal true
            screen.Contains("  [x]") |> should equal true
            screen.Contains("1.2.3-alpha.1") |> should equal true
            screen.Contains("10.0.0-rc.2") |> should equal true

            if width = 90 then
                screen.Contains(longPackage.DisplayName) |> should equal true)

    [<Fact>]
    member _.``wide and compact ANSI lists retain audited identities and comparison columns``() =
        [ Browse, [ "Version"; "Source" ]
          Installed, [ "Version"; "Kind" ]
          Updates, [ "Current"; "Latest"; "Kind" ]
          Consolidate, [ "Current"; "Latest"; "Kind" ] ]
        |> List.iter (fun (mode, headings) ->
            [ 126, 34; 90, 30 ]
            |> List.iter (fun (width, height) ->
                let initial =
                    { model () with
                        Mode = mode
                        Packages = releasePackages
                        ActivePackage = Some releasePackages.Head.Id }

                let screen = renderTextWithKeys width height initial []

                [ dependencyInjection.DisplayName; solutionPersistence.DisplayName ]
                |> List.iter (fun packageId ->
                    if not (screen.Contains(packageId, StringComparison.Ordinal)) then
                        failwith
                            $"{mode} at {width} columns clipped {packageId}.{Environment.NewLine}{screen}")

                headings
                |> List.iter (fun heading ->
                    screen.Contains(heading, StringComparison.Ordinal) |> should equal true)

                match mode with
                | Updates
                | Consolidate ->
                    [ "9.0.7"; "10.0.0"; "1.0.52"; "1.1.0"; "Transitive" ]
                    |> List.iter (fun comparison ->
                        screen.Contains(comparison, StringComparison.Ordinal)
                        |> should equal true)
                | Browse
                | Installed -> ()

                if width = 126 then
                    screen.Contains("Loading package details...", StringComparison.Ordinal)
                    |> should equal true))

    [<Fact>]
    member _.``list sort status and affordance match interactive empty and loading state``() =
        let pending mode =
            match mode with
            | Browse ->
                { model () with
                    Mode = mode
                    Pending.Search = Some(RequestToken 50L) }
            | Installed ->
                { model () with
                    Mode = mode
                    Pending.Refresh = Some(RequestToken 51L) }
            | Updates ->
                { model () with
                    Mode = mode
                    Pending.Updates = Some(RequestToken 52L) }
            | Consolidate ->
                { model () with
                    Mode = mode
                    Pending.Consolidation = Some(RequestToken 53L) }

        let sorted (initial: Model) =
            { initial with
                Sort = { Field = Name; Direction = Descending } }

        [ Browse; Installed; Updates; Consolidate ]
        |> List.iter (fun mode ->
            [ 126, 34; 90, 30 ]
            |> List.iter (fun (width, height) ->
                let normal =
                    { model () with Mode = mode }
                    |> sorted
                    |> fun initial -> renderTextWithKeys width height initial []

                normal.Contains("Sort: Package, descending [s]", StringComparison.Ordinal)
                |> should equal true

                let empty =
                    { model () with
                        Mode = mode
                        Packages = []
                        ActivePackage = None }
                    |> sorted
                    |> fun initial -> renderTextWithKeys width height initial []

                empty.Contains("Sort: Package, descending", StringComparison.Ordinal)
                |> should equal true

                empty.Contains("[s]", StringComparison.Ordinal) |> should equal false

                let loading =
                    pending mode
                    |> sorted
                    |> fun initial -> renderTextWithKeys width height initial []

                loading.Contains("Sort: Package, descending", StringComparison.Ordinal)
                |> should equal true

                loading.Contains("[s]", StringComparison.Ordinal) |> should equal false
                loading.Contains(">~", StringComparison.Ordinal) |> should equal true
                loading.Contains(" ~", StringComparison.Ordinal) |> should equal true
                loading.Contains("~~", StringComparison.Ordinal) |> should equal false))

    [<Fact>]
    member _.``empty and loading lists keep footer and Help actions truthful and identical``() =
        let gatedActions =
            [ "j/k move"; "Space select"; "Enter details"; "p preview"; "s sort" ]

        let actionLine (expected: string) (rows: string list) =
            rows |> List.find (fun row -> row.Contains(expected, StringComparison.Ordinal))

        let verify
            width
            height
            (initial: Model)
            (expectedActions: string)
            (expectedNotice: string)
            =
            let projection = Presentation.project width initial
            projection.Actions |> should equal expectedActions

            let footerRows = renderWithKeys width height initial []
            let footerActions = actionLine expectedActions footerRows

            gatedActions
            |> List.iter (fun action ->
                footerActions.Contains(action, StringComparison.Ordinal) |> should equal false)

            let footer = String.concat Environment.NewLine footerRows
            footer.Contains(expectedNotice, StringComparison.Ordinal) |> should equal true

            let helpRows = renderWithKeys width height initial [ Key '?' ]
            let helpActions = actionLine expectedActions helpRows

            gatedActions
            |> List.iter (fun action ->
                helpActions.Contains(action, StringComparison.Ordinal) |> should equal false)

            let help = String.concat Environment.NewLine helpRows
            help.Contains("Current actions", StringComparison.Ordinal) |> should equal true

            help.Contains("Esc              Back or cancel outside Help")
            |> should equal true

            help.Contains("? / Esc          Close help") |> should equal true

        let empty =
            [ { model () with
                  Mode = Browse
                  Query.Text = "missing"
                  Packages = []
                  ActivePackage = None },
              "Tab/1-4 modes | / search | ? Help",
              "No packages found."
              { model () with
                  Mode = Installed
                  Packages = []
                  ActivePackage = None },
              "1 Browse | r refresh | ? Help",
              "No installed packages."
              { model () with
                  Mode = Updates
                  Packages = []
                  ActivePackage = None },
              "Tab/1-4 modes | / filters | r refresh | ? Help",
              "No updates found."
              { model () with
                  Mode = Consolidate
                  Packages = []
                  ActivePackage = None },
              "Tab/1-4 modes | / filters | r refresh | ? Help",
              "No version differences found." ]

        let loading =
            [ { model () with
                  Mode = Browse
                  Pending.Search = Some(RequestToken 40L) },
              "Searching packages..."
              { model () with
                  Pending.Preview = Some(RequestToken 41L) },
              "Building preview..."
              { model () with
                  Pending.Refresh = Some(RequestToken 42L) },
              "Refreshing installed packages..."
              { model () with
                  Mode = Updates
                  Pending.Updates = Some(RequestToken 43L) },
              "Finding package updates..."
              { model () with
                  Mode = Consolidate
                  Pending.Consolidation = Some(RequestToken 44L) },
              "Finding version differences..." ]

        [ 126, 34; 90, 30 ]
        |> List.iter (fun (width, height) ->
            empty
            |> List.iter (fun (initial, actions, notice) ->
                verify width height initial actions notice)

            loading
            |> List.iter (fun (initial, notice) ->
                verify width height initial "Tab/1-4 modes | Esc cancel | ? Help" notice))

    [<Fact>]
    member _.``wide and compact operation surfaces retain their complete lifecycle content``() =
        let progress =
            { Preview = preview.Id
              Operation = OperationId "operation-1"
              Completed = 3
              Total = 4
              Status = "Restoring projects" }

        let failure =
            { Scope = BackendSessionFailure
              Kind = BackendUnavailable
              Message = "Workspace Explorer is unavailable. Check the installed tool." }

        let operationFailure =
            { Scope = OperationFailure(Some preview.Id)
              Kind = Rejected "The requested operation was rejected."
              Message = "The approved package operation could not be completed." }

        let confirmationPreview =
            { preview with
                Summary =
                    [ "Update Direct.Package from 1.0.0 to 2.0.0."
                      "Update Central.Package from 1.0.0 to 2.0.0."
                      "Restore two affected projects."
                      "No known vulnerabilities remain after the update." ] }

        let confirmationSummary = confirmationPreview.Summary

        [ 126, 34; 90, 30 ]
        |> List.iter (fun (width, height) ->
            let confirmation =
                { model () with
                    Route = Content(OperationPreview(confirmationPreview, Summary)) }

            let confirmationScreen =
                renderTextWithKeys width height confirmation [ Key.L.WithCtrl; Key.Enter ]

            confirmationScreen.Contains("Apply package changes?") |> should equal true
            confirmationScreen.Contains("Impact summary") |> should equal true

            confirmationSummary
            |> List.iter (fun summary -> confirmationScreen.Contains(summary) |> should equal true)

            confirmationScreen.Contains("[Enter] Apply") |> should equal true
            confirmationScreen.Contains("[Esc] Cancel") |> should equal true
            confirmationScreen.Contains("[?] Help") |> should equal true
            confirmationScreen.Contains("Search and filters") |> should equal false

            let cancelled =
                renderTextWithKeys width height confirmation [ Key.L.WithCtrl; Key.Enter; Key.Esc ]

            cancelled.Contains("Apply package changes?") |> should equal false

            cancelled.Contains(confirmationSummary.Head) |> should equal true

            cancelled.Contains("Search and filters") |> should equal true

            let accepted =
                renderTextWithKeys
                    width
                    height
                    confirmation
                    [ Key.L.WithCtrl; Key.Enter; Key.Enter ]

            accepted.Contains("Apply package changes?") |> should equal false

            accepted.Contains(confirmationSummary.Head) |> should equal true

            accepted.Contains("Applying package changes...") |> should equal true

            accepted.Contains("Search and filters") |> should equal true

            let help =
                renderTextWithKeys width height confirmation [ Key.L.WithCtrl; Key.Enter; Key '?' ]

            help.Contains("Current actions") |> should equal true
            help.Contains("Enter apply | Esc cancel | ? Help") |> should equal true
            help.Contains("Apply package changes?") |> should equal false
            help.Contains("Search and filters") |> should equal false

            let restoredConfirmation =
                renderTextWithKeys
                    width
                    height
                    confirmation
                    [ Key.L.WithCtrl; Key.Enter; Key '?'; Key.Esc ]

            restoredConfirmation.Contains("Apply package changes?") |> should equal true
            restoredConfirmation.Contains("Impact summary") |> should equal true

            confirmationSummary
            |> List.iter (fun summary ->
                restoredConfirmation.Contains(summary) |> should equal true)

            restoredConfirmation.Contains("Search and filters") |> should equal false

            let applyingState =
                { model () with
                    Route = Content(OperationConfirmation preview)
                    Pending.Apply = Some(RequestToken 20L) }

            let applying = renderTextWithKeys width height applyingState []

            applying.Contains("Applying changes for 2 packages.") |> should equal true
            applying.Contains("The approved preview is being applied.") |> should equal true
            applying.Contains("Waiting for progress...") |> should equal true
            applying.Contains("? Help") |> should equal true
            applying.Contains("Direct.Package") |> should equal false

            let applyingRows = applying.Split Environment.NewLine

            applyingRows
            |> Array.findIndex (_.Contains("Applying changes for 2 packages."))
            |> should be (greaterThan (height / 4))

            applyingRows
            |> Array.find (_.Contains("Applying changes for 2 packages."))
            |> _.IndexOf("Applying changes for 2 packages.", StringComparison.Ordinal)
            |> should be (greaterThan 5)

            let progressing =
                { model () with
                    Route = Content(OperationProgress progress)
                    Pending.Apply = Some(RequestToken 20L) }
                |> renderTextWithKeys width height
                <| []

            progressing.Contains("Current step: Restoring projects") |> should equal true
            progressing.Contains("75%") |> should equal true
            progressing.Contains("Completed 3 of 4 steps.") |> should equal true

            progressing.Split Environment.NewLine
            |> Array.findIndex (_.Contains("Current step: Restoring projects"))
            |> should be (greaterThan (height / 4))

            [ failure.Scope, failure, "Workspace Explorer connection failed"
              operationFailure.Scope, operationFailure, "Package operation failed" ]
            |> List.iter (fun (scope, problem, heading) ->
                let failed =
                    { model () with
                        Route = Failure(PackageList, scope)
                        Failures = Map.ofList [ scope, problem ] }
                    |> renderTextWithKeys width height
                    <| []

                failed.Contains(heading) |> should equal true
                failed.Contains(problem.Message) |> should equal true
                failed.Contains("Press Esc to dismiss this message.") |> should equal true
                failed.Contains("Esc dismiss | ? Help") |> should equal true
                failed.Contains("Ready") |> should equal false
                failed.Contains("Direct.Package") |> should equal false

                failed.Split Environment.NewLine
                |> Array.findIndex (_.Contains(heading))
                |> should be (greaterThan (height / 4))))

    [<Fact>]
    member _.``owned failure takeover clears the fully masked confirmation surface``() =
        let confirming =
            { model () with
                Route = Content(OperationPreview(preview, Summary)) }

        let failure =
            { Scope = BackendSessionFailure
              Kind = BackendUnavailable
              Message = "Workspace Explorer is unavailable." }

        let failed =
            { confirming with
                Route = Failure(OperationPreview(preview, Summary), BackendSessionFailure)
                Failures = Map.ofList [ BackendSessionFailure, failure ] }

        [ 126, 34; 90, 30 ]
        |> List.iter (fun (width, height) ->
            let takeover =
                renderTextAfter width height confirming [ Key.L.WithCtrl; Key.Enter ] [ failed ]

            takeover.Contains(failure.Message) |> should equal true
            takeover.Contains("Apply package changes?") |> should equal false
            takeover.Contains("Update two packages.") |> should equal false

            let restoredRoute =
                renderTextAfter
                    width
                    height
                    confirming
                    [ Key.L.WithCtrl; Key.Enter ]
                    [ failed; confirming ]

            restoredRoute.Contains("Apply package changes?") |> should equal false
            restoredRoute.Contains("Update two packages.") |> should equal true

            if width = 126 then
                restoredRoute.Contains("Direct.Package") |> should equal true)

    [<Fact>]
    member _.``contextual failures keep their route content and owned package actions``() =
        let source = PackageSource "private-feed"

        let cases =
            [ SourceFailure source, "Sign in to private-feed."
              PackageFailure direct.Id, "Package metadata could not be loaded."
              ProjectFailure(ProjectId "Web"), "The Web project could not be inspected." ]

        [ 126, 34; 90, 30 ]
        |> List.iter (fun (width, height) ->
            cases
            |> List.iter (fun (scope, message) ->
                let problem =
                    { Scope = scope
                      Kind = Rejected "The requested package action was rejected."
                      Message = message }

                let failed =
                    { model () with
                        Route = Failure(PackageDetails direct.Id, scope)
                        Failures = Map.ofList [ scope, problem ] }

                let screen = renderTextWithKeys width height failed [ Key.L.WithCtrl ]

                screen.Contains(message) |> should equal true
                screen.Contains("Esc dismiss | h/l tabs") |> should equal true
                screen.Contains("Packages / Failure") |> should equal false
                screen.Contains("Applying package changes") |> should equal false))

    [<Fact>]
    member _.``wide and compact content keeps target guidance ranges and impact meaning visible``
        ()
        =
        [ 126, 34; 90, 30 ]
        |> List.iter (fun (width, height) ->
            let target =
                { model () with
                    Route = Content(PackageTargeting direct.Id)
                    Focus = ProjectRow(ProjectId "Web")
                    TargetSelection =
                        { Projects = Set.singleton (ProjectId "Web")
                          Frameworks =
                            Map.ofList
                                [ ProjectId "Web",
                                  Set.ofList
                                      [ TargetFramework "net10.0"; TargetFramework "net9.0" ] ] } }
                |> renderTextWithKeys width height
                <| [ Key.L.WithCtrl ]

            target.Contains("Workspace Explorer / Packages") |> should equal true
            target.Contains("[>] [x] Web") |> should equal true
            target.Contains("[x] net10.0") |> should equal true
            target.Contains("j/k move") |> should equal true
            target.Contains("Space select project and all frameworks") |> should equal true
            target.Contains("p preview") |> should equal true

            let targetRows = target.Split Environment.NewLine

            let projectRow = targetRows |> Array.findIndex (_.Contains("[>] [x] Web"))

            let frameworkRow = targetRows |> Array.findIndex (_.Contains("[x] net10.0"))

            frameworkRow |> should be (greaterThan projectRow)

            targetRows[frameworkRow].IndexOf("[x] net10.0", StringComparison.Ordinal)
            |> should
                be
                (greaterThan (
                    targetRows[projectRow].IndexOf("[>] [x] Web", StringComparison.Ordinal)
                ))

            let dependencyPreview =
                { model () with
                    Route = Content(OperationPreview(preview, Dependencies)) }
                |> renderTextWithKeys width height
                <| [ Key.L.WithCtrl ]

            dependencyPreview.Contains("Dependency [4.3.0,)") |> should equal true
            dependencyPreview.Contains("Dependency impact") |> should equal false

            let filePreview =
                { model () with
                    Route = Content(OperationPreview(preview, Files)) }
                |> renderTextWithKeys width height
                <| [ Key.L.WithCtrl ]

            filePreview.Contains("Changed: src/Web/Web.fsproj") |> should equal true
            filePreview.Contains("Changed: src/Worker/Worker.fsproj") |> should equal true)

    [<Fact>]
    member _.``wide and compact README retain trailing prose and fenced code content``() =
        let readme =
            """# Direct.Package

This README prose is intentionally long enough to wrap without losing its trailing prose-tail.

```console
dotnet add package Direct.Package --source https://api.nuget.org/v3/index.json --framework net10.0 --property command-tail
```"""

        let initial =
            { model () with
                Route = Content(PackageReadme direct.Id)
                Readmes =
                    Map.ofList
                        [ direct.Id,
                          { Package = direct.Id
                            CommonMark = readme } ] }

        [ 126, 34; 90, 30 ]
        |> List.iter (fun (width, height) ->
            let screen = renderTextWithKeys width height initial [ Key.L.WithCtrl ]
            screen.Contains("prose-tail") |> should equal true
            screen.Contains("command-tail") |> should equal true)

    [<Fact>]
    member _.``live key events move focus and dispatch through the fixed window bindings``() =
        use application = Application.Create().Init "ansi"

        application.Driver
        |> Option.ofObj
        |> Option.iter (fun driver -> driver.SetScreenSize(120, 35))

        application.StopAfterFirstIteration <- true
        let messages = ResizeArray<Message>()

        use window =
            new ExplorerWindow(model (), messages.Add, application.RequestStop, Ansi16)

        use _keyboard = window.BindKeyboard application.Keyboard

        application.Invoke(
            Action(fun () ->
                application.Keyboard.RaiseKeyDownEvent(Key '/') |> ignore

                let searchFocus =
                    window.MostFocused
                    |> Option.ofObj
                    |> Option.defaultWith (fun () -> failwith "Search did not receive focus.")

                application.Keyboard.RaiseKeyDownEvent Key.L.WithCtrl |> ignore

                let contextFocus =
                    window.MostFocused
                    |> Option.ofObj
                    |> Option.defaultWith (fun () -> failwith "Context did not receive focus.")

                Object.ReferenceEquals(searchFocus, contextFocus) |> should equal false

                application.Keyboard.RaiseKeyDownEvent Key.H.WithCtrl |> ignore

                let listFocus =
                    window.MostFocused
                    |> Option.ofObj
                    |> Option.defaultWith (fun () ->
                        failwith "Package list did not receive focus.")

                Object.ReferenceEquals(contextFocus, listFocus) |> should equal false

                application.Keyboard.RaiseKeyDownEvent Key.D1 |> ignore
                application.Keyboard.RaiseKeyDownEvent Key.R |> ignore
                application.RequestStop())
        )

        application.Run window |> ignore

        messages |> Seq.toList |> should contain (ChangeMode Browse)
        messages |> Seq.toList |> should contain Refresh

    [<Fact>]
    member _.``Markdown viewport survives an in-place status refresh``() =
        use application = Application.Create().Init "ansi"

        application.Driver
        |> Option.ofObj
        |> Option.iter (fun driver -> driver.SetScreenSize(120, 35))

        let longReadme =
            [ 1..80 ]
            |> List.map (fun line -> $"## Section {line}\n\nContent for section {line}.")
            |> String.concat "\n\n"

        let initial =
            { model () with
                Route = Content(PackageReadme direct.Id)
                Readmes =
                    Map.ofList
                        [ direct.Id,
                          { Package = direct.Id
                            CommonMark = longReadme } ] }

        use window = new ExplorerWindow(initial, ignore, application.RequestStop, Ansi16)
        use _keyboard = window.BindKeyboard application.Keyboard

        application.Invoke(
            Action(fun () ->
                application.Keyboard.RaiseKeyDownEvent Key.L.WithCtrl |> ignore

                let context =
                    window.MostFocused
                    |> Option.ofObj
                    |> Option.defaultWith (fun () -> failwith "Context did not receive focus.")

                context.ScrollVertical 4 |> should equal true
                let scrolled = context.Viewport.Y
                scrolled |> should be (greaterThan 0)

                window.Render
                    { initial with
                        Pending.Refresh = Some(RequestToken 88L) }

                context.Viewport.Y |> should equal scrolled
                application.RequestStop())
        )

        application.Run window |> ignore

[<Sealed>]
type ConnectedRuntimeTests() =
    [<Fact>]
    member _.``connected composition closes the client and subscription once after an ANSI run``() =
        let mutable closeCount = 0
        let mutable disposeCount = 0

        let subscription =
            { new IDisposable with
                member _.Dispose() = disposeCount <- disposeCount + 1 }

        let packageClient =
            client (fun () -> async { closeCount <- closeCount + 1 }) (fun _ -> subscription)

        let connection =
            { Client = packageClient
              Capabilities = Set.empty
              ServerCapabilities = Set.empty }

        let createApplication () =
            let application = Application.Create()
            application.StopAfterFirstIteration <- true
            application

        Runtime.runConnected
            createApplication
            "ansi"
            Ansi16
            (Runtime.target "App.fsproj")
            connection
        |> should equal 0

        closeCount |> should equal 1
        disposeCount |> should equal 1
