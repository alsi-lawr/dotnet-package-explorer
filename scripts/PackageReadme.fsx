open System
open System.IO

let input, output =
    match fsi.CommandLineArgs |> Array.skip 1 with
    | [| input; output |] -> input, output
    | _ -> failwith "Expected an input README and an output path."

let lines = File.ReadAllLines input

let headerEnd =
    lines
    |> Array.tryFindIndex (fun line -> line.Trim() = "</div>")
    |> Option.defaultWith (fun () -> failwith "The GitHub README header is incomplete.")

let packageHeader =
    lines[..headerEnd]
    |> Array.filter (fun line ->
        let trimmed = line.Trim()
        trimmed = "" || trimmed.StartsWith "# " || trimmed.StartsWith "**")
    |> Array.skipWhile String.IsNullOrWhiteSpace

let packageBody =
    lines[headerEnd + 1 ..]
    |> Array.fold
        (fun (divDepth, outputLines) line ->
            let trimmed = line.Trim()

            if trimmed.StartsWith "<div" then
                divDepth + 1, outputLines
            elif trimmed = "</div>" then
                max 0 (divDepth - 1), outputLines
            elif divDepth > 0 then
                divDepth, outputLines
            else
                divDepth, line :: outputLines)
        (0, [])
    |> snd
    |> List.rev
    |> List.map (fun line ->
        line
            .Replace(
                "[CONTRIBUTING.md](CONTRIBUTING.md)",
                "[CONTRIBUTING.md](https://github.com/alsi-lawr/dotnet-package-explorer/blob/master/CONTRIBUTING.md)"
            )
            .Replace(
                "[MIT](LICENSE)",
                "[MIT](https://github.com/alsi-lawr/dotnet-package-explorer/blob/master/LICENSE)"
            ))
    |> List.toArray

let commonMark =
    Array.append packageHeader packageBody
    |> Array.fold
        (fun (previousBlank, outputLines) line ->
            let blank = String.IsNullOrWhiteSpace line

            if blank && previousBlank then
                true, outputLines
            else
                blank, line :: outputLines)
        (false, [])
    |> snd
    |> List.rev

Directory.CreateDirectory(Path.GetDirectoryName output) |> ignore
File.WriteAllLines(output, commonMark)
