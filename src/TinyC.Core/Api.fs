namespace TinyC

open System
open System.Collections.Generic
open System.IO
open System.Text

module Api =
    type Execution = { Output: string; ExitValue: int; Steps: int; CanvasCommands: string }

#if !FABLE_COMPILER
    let private stripIncludePath (text: string) =
        let trimmed = text.Trim()
        if trimmed.Length >= 2 then
            let last = trimmed.Length - 1
            if (trimmed[0] = '"' && trimmed[last] = '"') || (trimmed[0] = '\'' && trimmed[last] = '\'') then
                trimmed.Substring(1, trimmed.Length - 2)
            else
                trimmed
        else
            trimmed

    let private ancestorDirs (path: string) =
        seq {
            let mutable current =
                let dir = if Directory.Exists path then path else Path.GetDirectoryName path
                if isNull dir then None else Some (Path.GetFullPath dir)
            while current.IsSome do
                let dir = current.Value
                yield dir
                let parent = Directory.GetParent dir
                current <- if isNull parent then None else Some parent.FullName
        }

    let private resolveInclude (baseDir: string) (includePath: string) =
        if Path.IsPathRooted includePath then
            if File.Exists includePath then Some (Path.GetFullPath includePath) else None
        else
            ancestorDirs baseDir
            |> Seq.map (fun dir -> Path.GetFullPath(Path.Combine(dir, includePath)))
            |> Seq.tryFind File.Exists

    let private expandIncludes (sourcePath: string) =
        let loading = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let rec expandFile (path: string) : string =
            let fullPath = Path.GetFullPath path
            if not (loading.Add fullPath) then
                failwithf "Recursive include detected: %s" fullPath
            try
                let baseDir =
                    match Path.GetDirectoryName fullPath with
                    | null -> Directory.GetCurrentDirectory()
                    | dir -> dir
                let builder = StringBuilder()
                for line in File.ReadAllLines fullPath do
                    let trimmed = line.TrimStart()
                    if trimmed.StartsWith("#include ", StringComparison.Ordinal) then
                        let includeText = trimmed.Substring(9) |> stripIncludePath
                        match resolveInclude baseDir includeText with
                        | Some includedPath -> builder.Append(expandFile includedPath : string) |> ignore
                        | None -> failwithf "Unable to resolve include '%s' from '%s'" includeText fullPath
                    else
                        builder.AppendLine(line) |> ignore
                builder.ToString()
            finally
                loading.Remove fullPath |> ignore
        expandFile sourcePath
#endif

    let private executeTextWithLimit maxSteps source : Result<Execution,string> =
        let output = StringBuilder()
        let canvasCommands = StringBuilder()
        let canvasCommand (command: string) = canvasCommands.AppendLine(command) |> ignore

        let textOf = function
            | Runtime.TextValue s -> s
            | Runtime.CharacterArrayValue(values, offset) ->
                if offset < 0 || offset > values.Length then ""
                else values |> Seq.skip offset |> Seq.takeWhile (fun value -> value <> 0) |> Seq.map char |> String.Concat
            | Runtime.NumberValue n -> string n
            | Runtime.IntegerArrayValue _ -> ""
        let display = function
            | Runtime.NumberValue n -> string n
            | Runtime.TextValue s -> s
            | Runtime.CharacterArrayValue(values, offset) ->
                if offset < 0 || offset > values.Length then ""
                else
                    values
                    |> Seq.skip offset
                    |> Seq.takeWhile (fun value -> value <> 0)
                    |> Seq.map char
                    |> String.Concat
            | Runtime.IntegerArrayValue _ -> ""
        let stringOf = function
            | Runtime.TextValue s -> s
            | Runtime.NumberValue n -> string n
            | Runtime.CharacterArrayValue(values, offset) ->
                if offset < 0 || offset > values.Length then ""
                else
                    values
                    |> Seq.skip offset
                    |> Seq.takeWhile (fun value -> value <> 0)
                    |> Seq.map char
                    |> String.Concat
            | Runtime.IntegerArrayValue _ -> ""
        let charOf = function
            | Runtime.NumberValue n -> char n
            | Runtime.TextValue s -> if String.IsNullOrEmpty s then '\000' else s[0]
            | Runtime.CharacterArrayValue(values, offset) ->
                if offset < 0 || offset >= values.Length then '\000' else char values[offset]
            | Runtime.IntegerArrayValue _ -> '\000'
        let formatPrintf (format: string) (args: Runtime.Value list) =
            let sb = StringBuilder()
            let mutable i = 0
            let mutable argIndex = 0
            let nextArg () =
                if argIndex >= args.Length then failwith "printf expects more arguments"
                let value = args[argIndex]
                argIndex <- argIndex + 1
                value
            while i < format.Length do
                if format[i] <> '%' then
                    sb.Append(format[i]) |> ignore
                    i <- i + 1
                elif i + 1 < format.Length && format[i + 1] = '%' then
                    sb.Append('%') |> ignore
                    i <- i + 2
                else
                    i <- i + 1
                    let mutable zeroPad = false
                    if i < format.Length && format[i] = '0' then
                        zeroPad <- true
                        i <- i + 1
                    let mutable width = 0
                    while i < format.Length && Char.IsDigit format[i] do
                        width <- width * 10 + int format[i] - int '0'
                        i <- i + 1
                    if i >= format.Length then failwith "Malformed printf format string"
                    let value = nextArg()
                    let rendered =
                        match format[i] with
                        | 'c' -> string (charOf value)
                        | 's' -> stringOf value
                        | 'd' -> string (match value with Runtime.NumberValue n -> n | _ -> failwith "printf %d expects an integer")
                        | other -> failwithf "Unsupported printf format specifier '%%%c'" other
                    i <- i + 1
                    if width > 0 && rendered.Length < width then
                        let pad = String(Array.create (width - rendered.Length) (if zeroPad then '0' else ' '))
                        sb.Append(pad).Append(rendered) |> ignore
                    else
                        sb.Append(rendered) |> ignore
            if argIndex <> args.Length then failwith "printf received too many arguments"
            sb.ToString()
        let hosts : Map<string,Runtime.HostFunction> =
            [ "print", fun xs -> xs |> List.iter(fun x -> output.Append(display x) |> ignore); Ok(Runtime.NumberValue 0)
              "println", fun xs -> xs |> List.iter(fun x -> output.Append(display x) |> ignore); output.AppendLine() |> ignore; Ok(Runtime.NumberValue 0)
              "pl", fun xs -> xs |> List.iter(fun x -> output.Append(display x) |> ignore); output.AppendLine() |> ignore; Ok(Runtime.NumberValue 0)
              "ps", fun xs -> xs |> List.iter(fun x -> output.Append(display x) |> ignore); Ok(Runtime.NumberValue 0)
              "printf", fun xs ->
                    match xs with
                    | Runtime.TextValue fmt :: rest -> output.Append(formatPrintf fmt rest) |> ignore; Ok(Runtime.NumberValue 0)
                    | Runtime.CharacterArrayValue(values, offset) :: rest ->
                        let fmt = stringOf (Runtime.CharacterArrayValue(values, offset))
                        output.Append(formatPrintf fmt rest) |> ignore
                        Ok(Runtime.NumberValue 0)
                    | _ -> Error "printf expects a format string"
              "putchar", fun xs -> match xs with [Runtime.NumberValue n] -> output.Append(char n) |> ignore; Ok(Runtime.NumberValue n) | _ -> Error "putchar expects one character"
              "pn", fun xs -> match xs with [Runtime.NumberValue n] -> output.Append(n) |> ignore; Ok(Runtime.NumberValue n) | _ -> Error "pn expects one integer"
              "pc", fun xs -> match xs with [Runtime.NumberValue n] -> output.Append(char n) |> ignore; Ok(Runtime.NumberValue n) | _ -> Error "pc expects one character"
              // Graphics are recorded as data so the browser host can replay them on canvas.
              "start", fun xs -> match xs with [_; Runtime.NumberValue width; Runtime.NumberValue height] -> canvasCommand (sprintf "clear|%d|%d" width height); Ok(Runtime.NumberValue 0) | _ -> Error "start expects a name, width, and height"
              "rectangle", fun xs -> match xs with [Runtime.NumberValue x; Runtime.NumberValue y; Runtime.NumberValue width; Runtime.NumberValue height] -> canvasCommand (sprintf "rectangle|%d|%d|%d|%d" x y width height); Ok(Runtime.NumberValue 0) | _ -> Error "rectangle expects four integers"
              "setrgb", fun xs -> match xs with [Runtime.NumberValue r; Runtime.NumberValue g; Runtime.NumberValue b] -> canvasCommand (sprintf "rgb|%d|%d|%d" r g b); Ok(Runtime.NumberValue 0) | _ -> Error "setrgb expects three integers"
              // Unparenthesized Tiny-C calls make a no-argument call followed by
              // another identifier parse as a nested call. Ignore such values;
              // evaluating them still preserves the following graphics operation.
              "fill", fun _ -> canvasCommand "fill"; Ok(Runtime.NumberValue 0)
              "stroke", fun _ -> canvasCommand "stroke"; Ok(Runtime.NumberValue 0)
              "setfontsize", fun xs -> match xs with [Runtime.NumberValue size] -> canvasCommand (sprintf "fontsize|%d" size); Ok(Runtime.NumberValue 0) | _ -> Error "setfontsize expects one integer"
              "moveto", fun xs -> match xs with [Runtime.NumberValue x; Runtime.NumberValue y] -> canvasCommand (sprintf "moveto|%d|%d" x y); Ok(Runtime.NumberValue 0) | _ -> Error "moveto expects two integers"
              "lineto", fun xs -> match xs with [Runtime.NumberValue x; Runtime.NumberValue y] -> canvasCommand (sprintf "lineto|%d|%d" x y); Ok(Runtime.NumberValue 0) | _ -> Error "lineto expects two integers"
              "arc", fun xs -> match xs with [Runtime.NumberValue x; Runtime.NumberValue y; Runtime.NumberValue radius; Runtime.NumberValue startAngle; Runtime.NumberValue endAngle] -> canvasCommand (sprintf "arc|%d|%d|%d|%d|%d" x y radius startAngle endAngle); Ok(Runtime.NumberValue 0) | _ -> Error "arc expects five integers"
              "arcneg", fun xs -> match xs with [Runtime.NumberValue x; Runtime.NumberValue y; Runtime.NumberValue radius; Runtime.NumberValue startAngle; Runtime.NumberValue endAngle] -> canvasCommand (sprintf "arcneg|%d|%d|%d|%d|%d" x y radius startAngle endAngle); Ok(Runtime.NumberValue 0) | _ -> Error "arcneg expects five integers"
              "dot", fun xs -> match xs with [Runtime.NumberValue x; Runtime.NumberValue y] -> canvasCommand (sprintf "moveto|%d|%d" x y); canvasCommand (sprintf "lineto|%d|%d" (x + 1) (y + 1)); Ok(Runtime.NumberValue 0) | _ -> Error "dot expects two integers"
              "next", fun _ -> canvasCommand "next"; Ok(Runtime.NumberValue 0)
              "setdash", fun xs -> match xs with [Runtime.NumberValue dash; Runtime.NumberValue offset] -> canvasCommand (sprintf "setdash|%d|%d" dash offset); Ok(Runtime.NumberValue 0) | _ -> Error "setdash expects a dash length and offset"
              "setdash2", fun xs -> match xs with [Runtime.NumberValue dash1; Runtime.NumberValue dash2; Runtime.NumberValue offset] -> canvasCommand (sprintf "setdash2|%d|%d|%d" dash1 dash2 offset); Ok(Runtime.NumberValue 0) | _ -> Error "setdash2 expects two dash lengths and an offset"
              "showtext", fun xs -> match xs with [value] -> canvasCommand (sprintf "text|%s" ((textOf value).Replace("|", " ").Replace("\r", "").Replace("\n", " "))); Ok(Runtime.NumberValue 0) | _ -> Error "showtext expects one string"
              "strcpy", fun xs ->
                    match xs with
                    | [Runtime.CharacterArrayValue(destination, destinationOffset); source] ->
                        let text = textOf source
                        let chars = text |> Seq.map int |> Seq.truncate (destination.Length - destinationOffset - 1) |> Seq.toArray
                        Array.blit chars 0 destination destinationOffset chars.Length
                        if destinationOffset + chars.Length < destination.Length then destination[destinationOffset + chars.Length] <- 0
                        Ok(Runtime.NumberValue chars.Length)
                    | _ -> Error "strcpy expects a destination array and source text"
              "strcat", fun xs ->
                    match xs with
                    | [Runtime.CharacterArrayValue(destination, destinationOffset); source] ->
                        let start = destinationOffset + (destination |> Seq.skip destinationOffset |> Seq.takeWhile ((<>) 0) |> Seq.length)
                        let chars = textOf source |> Seq.map int |> Seq.truncate (destination.Length - start - 1) |> Seq.toArray
                        Array.blit chars 0 destination start chars.Length
                        if start + chars.Length < destination.Length then destination[start + chars.Length] <- 0
                        Ok(Runtime.NumberValue (start + chars.Length - destinationOffset))
                    | _ -> Error "strcat expects a destination array and source text"
              "show", fun _ -> Ok(Runtime.NumberValue 0) ] |> Map.ofList
        Parser.parse source
        |> Result.mapError(fun d -> sprintf "%d:%d: %s" d.Position.Line d.Position.Column d.Message)
        |> Result.bind(Runtime.run { MaxSteps=maxSteps; EntryPoint="main"; HostFunctions=hosts })
        |> Result.map(fun r ->
            { Output = output.ToString()
              ExitValue = (match r.Value with Runtime.NumberValue n -> n | _ -> 0)
              Steps = r.Steps
              CanvasCommands = canvasCommands.ToString() })

    let executeWithLimit maxSteps source : Result<Execution,string> =
        executeTextWithLimit maxSteps source

#if !FABLE_COMPILER
    let executeFileWithLimit maxSteps sourcePath : Result<Execution,string> =
        try
            expandIncludes sourcePath |> executeTextWithLimit maxSteps
        with ex ->
            Error ex.Message
#endif

    let execute source = executeWithLimit 1_000_000 source
