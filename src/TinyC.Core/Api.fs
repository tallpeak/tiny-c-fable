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
                    elif trimmed.StartsWith("#loadMC ", StringComparison.Ordinal) then
                        // Plugin loading is a host concern; browser-safe MC calls are built in.
                        ()
                    else
                        builder.AppendLine(line) |> ignore
                builder.ToString()
            finally
                loading.Remove fullPath |> ignore
        expandFile sourcePath
#endif

    let private executeTextWithLimit maxSteps input source : Result<Execution,string> =
        let output = StringBuilder()
        let canvasCommands = StringBuilder()
        let canvasCommand (command: string) = canvasCommands.AppendLine(command) |> ignore
        let input = Queue<int>(input |> Seq.map int)

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
                        | 'x' -> (match value with Runtime.NumberValue n -> n.ToString("x") | _ -> failwith "printf %x expects an integer")
                        | other -> failwithf "Unsupported printf format specifier '%%%c'" other
                    i <- i + 1
                    if width > 0 && rendered.Length < width then
                        let pad = String(Array.create (width - rendered.Length) (if zeroPad then '0' else ' '))
                        sb.Append(pad).Append(rendered) |> ignore
                    else
                        sb.Append(rendered) |> ignore
            if argIndex <> args.Length then failwith "printf received too many arguments"
            sb.ToString()
        let numeric = function
            | Runtime.NumberValue n -> Ok n
            | _ -> Error "Machine call expects an integer"
        let characterArray = function
            | Runtime.CharacterArrayValue(values, offset) when offset >= 0 && offset <= values.Length -> Ok(values, offset)
            | Runtime.CharacterArrayValue _ -> Error "Character-array pointer is out of range"
            | _ -> Error "Machine call expects a character-array pointer"
        let integerArray = function
            | Runtime.IntegerArrayValue(values, offset) when offset >= 0 && offset < values.Length -> Ok(values, offset)
            | Runtime.IntegerArrayValue _ -> Error "Integer-array pointer is out of range"
            | _ -> Error "Machine call expects an integer-array pointer"
        let pointerRange first last =
            match first, last with
            | Runtime.CharacterArrayValue(firstValues, firstOffset), Runtime.CharacterArrayValue(lastValues, lastOffset)
                when obj.ReferenceEquals(firstValues, lastValues) && firstOffset >= 0 && lastOffset >= firstOffset && lastOffset < firstValues.Length ->
                    Ok(firstValues, firstOffset, lastOffset)
            | Runtime.IntegerArrayValue(firstValues, firstOffset), Runtime.IntegerArrayValue(lastValues, lastOffset)
                when obj.ReferenceEquals(firstValues, lastValues) && firstOffset >= 0 && lastOffset >= firstOffset && lastOffset < firstValues.Length ->
                    Ok(firstValues, firstOffset, lastOffset)
            | _ -> Error "Machine call range must be within one array"
        let copyText (destination: int array) destinationOffset (text: string) =
            let chars = text |> Seq.map int |> Seq.truncate (max 0 (destination.Length - destinationOffset)) |> Seq.toArray
            Array.blit chars 0 destination destinationOffset chars.Length
            if destinationOffset + chars.Length < destination.Length then destination[destinationOffset + chars.Length] <- 0
            chars.Length
        let machineCall : Runtime.HostFunction = fun values ->
            let fail message = Error message
            match List.rev values with
            | Runtime.NumberValue mcno :: reversedArgs ->
                let args = List.rev reversedArgs
                match mcno, args with
                | 1, [value] -> numeric value |> Result.map(fun n -> output.Append(char n) |> ignore; Runtime.NumberValue n)
                | 2, [] ->
                    let value = if input.Count = 0 then 10 else input.Dequeue()
                    output.Append(char value) |> ignore
                    Ok(Runtime.NumberValue value)
                | 7, [first; last; distance] ->
                    match first, last, distance with
                    | Runtime.CharacterArrayValue(source, sourceOffset), Runtime.CharacterArrayValue(lastValues, lastOffset), Runtime.NumberValue dist
                        when obj.ReferenceEquals(source, lastValues) && sourceOffset >= 0 && lastOffset >= sourceOffset && lastOffset < source.Length ->
                        let destinationOffset = sourceOffset + dist
                        let length = lastOffset - sourceOffset + 1
                        if destinationOffset < 0 || destinationOffset + length > source.Length then fail "Machine call move is out of range"
                        else
                            let copy = Array.sub source sourceOffset length
                            Array.blit copy 0 source destinationOffset length
                            Ok(Runtime.NumberValue 0)
                    | Runtime.IntegerArrayValue(source, sourceOffset), Runtime.IntegerArrayValue(lastValues, lastOffset), Runtime.NumberValue dist
                        when obj.ReferenceEquals(source, lastValues) && dist % 4 = 0 && sourceOffset >= 0 && lastOffset >= sourceOffset && lastOffset < source.Length ->
                        // Integer-array pointers use byte distances in the original C runtime.
                        let destinationOffset = sourceOffset + dist / 4
                        let length = lastOffset - sourceOffset + 1
                        if destinationOffset < 0 || destinationOffset + length > source.Length then fail "Machine call move is out of range"
                        else
                            let copy = Array.sub source sourceOffset length
                            Array.blit copy 0 source destinationOffset length
                            Ok(Runtime.NumberValue 0)
                    | _ -> fail "Machine call move requires matching array pointers"
                | 8, [first; last; character] ->
                    match pointerRange first last, numeric character with
                    | Ok(values, firstOffset, lastOffset), Ok c ->
                        Ok(Runtime.NumberValue([firstOffset..lastOffset] |> List.sumBy(fun i -> if values[i] = c then 1 else 0)))
                    | Error e, _ | _, Error e -> fail e
                | 9, [first; last; character; count] ->
                    match pointerRange first last, numeric character, integerArray count with
                    | Ok(values, firstOffset, lastOffset), Ok c, Ok(countValues, countOffset) ->
                        let mutable remaining = countValues[countOffset]
                        let mutable result = lastOffset - firstOffset
                        let mutable stop = false
                        let mutable i = firstOffset
                        while i <= lastOffset && not stop do
                            if values[i] = c then
                                remaining <- remaining - 1
                                result <- i - firstOffset
                                if remaining <= 0 then stop <- true
                            i <- i + 1
                        countValues[countOffset] <- remaining
                        Ok(Runtime.NumberValue result)
                    | Error e, _, _ | _, Error e, _ | _, _, Error e -> fail e
                | 12, [] -> Ok(Runtime.NumberValue(if input.Count = 0 then 0 else 1))
                | 13, [first; last] ->
                    match pointerRange first last with
                    | Ok(values, firstOffset, lastOffset) ->
                        for i in firstOffset..lastOffset do output.Append(char values[i]) |> ignore
                        Ok(Runtime.NumberValue 0)
                    | Error e -> fail e
                | 14, [value] -> numeric value |> Result.map(fun n -> output.Append(n) |> ignore; Runtime.NumberValue 0)
                | 101, format :: formatArgs ->
                    let format =
                        match format with
                        | Runtime.TextValue text -> Ok text
                        | Runtime.CharacterArrayValue(array, offset) ->
                            Ok(array |> Seq.skip offset |> Seq.takeWhile ((<>) 0) |> Seq.map char |> String.Concat)
                        | _ -> Error "Machine call 101 expects a format string"
                    match format with
                    | Ok text ->
                        try output.Append(formatPrintf text formatArgs) |> ignore; Ok(Runtime.NumberValue 0)
                        with ex -> fail ex.Message
                    | Error e -> fail e
                | 104, [value] ->
                    match value with
                    | Runtime.TextValue text -> Ok(Runtime.NumberValue text.Length)
                    | Runtime.CharacterArrayValue(values, offset) when offset >= 0 && offset <= values.Length ->
                        Ok(Runtime.NumberValue(values |> Seq.skip offset |> Seq.takeWhile ((<>) 0) |> Seq.length))
                    | Runtime.CharacterArrayValue _ -> fail "Character-array pointer is out of range"
                    | _ -> fail "Machine call 104 expects a string"
                | 105, [destination; source] | 106, [destination; source] ->
                    match characterArray destination with
                    | Error e -> fail e
                    | Ok(values, offset) ->
                        let text =
                            match source with
                            | Runtime.TextValue text -> text
                            | Runtime.CharacterArrayValue(sourceValues, sourceOffset) ->
                                sourceValues |> Seq.skip sourceOffset |> Seq.takeWhile ((<>) 0) |> Seq.map char |> String.Concat
                            | _ -> ""
                        if mcno = 105 then
                            let currentLength = values |> Seq.skip offset |> Seq.takeWhile ((<>) 0) |> Seq.length
                            let start = offset + currentLength
                            if start >= values.Length then fail "Machine call string operation is out of range"
                            else Ok(Runtime.NumberValue(copyText values start text))
                        else
                            Ok(Runtime.NumberValue(copyText values offset text))
                | 102, [_] -> Ok(Runtime.NumberValue 0)
                | 108, [] -> Ok(Runtime.NumberValue 0)
                | 109, [] -> Ok(Runtime.NumberValue 0)
                // Pica Graphics compatibility calls. Coordinates use the original
                // 80-by-80 pica space; the browser maps them onto the canvas.
                | 1001, [] -> canvasCommand "pigra-blank"; Ok(Runtime.NumberValue 0)
                | 1002, [] -> canvasCommand "pigra-show"; Ok(Runtime.NumberValue 0)
                | 1003, [x; y] ->
                    match numeric x, numeric y with
                    | Ok x, Ok y -> canvasCommand (sprintf "pigra-plot|%d|%d" x y); Ok(Runtime.NumberValue 0)
                    | Error e, _ | _, Error e -> fail e
                | 1004, [x0; y0; x1; y1] ->
                    match numeric x0, numeric y0, numeric x1, numeric y1 with
                    | Ok x0, Ok y0, Ok x1, Ok y1 -> canvasCommand (sprintf "pigra-line|%d|%d|%d|%d" x0 y0 x1 y1); Ok(Runtime.NumberValue 0)
                    | Error e, _, _, _ | _, Error e, _, _ | _, _, Error e, _ | _, _, _, Error e -> fail e
                | 1005, [x; y; radius; points] ->
                    match numeric x, numeric y, numeric radius, numeric points with
                    | Ok x, Ok y, Ok radius, Ok points -> canvasCommand (sprintf "pigra-circle|%d|%d|%d|%d" x y radius points); Ok(Runtime.NumberValue 0)
                    | Error e, _, _, _ | _, Error e, _, _ | _, _, Error e, _ | _, _, _, Error e -> fail e
                | 1006, [x; y; radius; points] ->
                    match numeric x, numeric y, numeric radius, numeric points with
                    | Ok x, Ok y, Ok radius, Ok points -> canvasCommand (sprintf "pigra-star|%d|%d|%d|%d" x y radius points); Ok(Runtime.NumberValue 0)
                    | Error e, _, _, _ | _, Error e, _, _ | _, _, Error e, _ | _, _, _, Error e -> fail e
                | 1007, [x; y; text] ->
                    match numeric x, numeric y with
                    | Ok x, Ok y -> canvasCommand (sprintf "pigra-text|%d|%d|%s" x y ((textOf text).Replace("|", " ").Replace("\r", "").Replace("\n", " "))); Ok(Runtime.NumberValue 0)
                    | Error e, _ | _, Error e -> fail e
                | 1008, [_] -> Ok(Runtime.NumberValue 0)
                | 1009, [x0; y0; x1; y1; x2; y2] ->
                    match numeric x0, numeric y0, numeric x1, numeric y1, numeric x2, numeric y2 with
                    | Ok x0, Ok y0, Ok x1, Ok y1, Ok x2, Ok y2 -> canvasCommand (sprintf "pigra-triangle|%d|%d|%d|%d|%d|%d" x0 y0 x1 y1 x2 y2); Ok(Runtime.NumberValue 0)
                    | Error e, _, _, _, _, _ | _, Error e, _, _, _, _ | _, _, Error e, _, _, _ | _, _, _, Error e, _, _ | _, _, _, _, Error e, _ | _, _, _, _, _, Error e -> fail e
                | _, _ -> fail (sprintf "Machine call %d is not implemented" mcno)
            | _ -> fail "Machine call expects its number as the final argument"
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
              "MC", machineCall
              "strcpy", fun xs ->
                    match xs with
                    | [Runtime.CharacterArrayValue(destination, destinationOffset); source] ->
                        let text = textOf source
                        let chars = text |> Seq.map int |> Seq.truncate (max 0 (destination.Length - destinationOffset)) |> Seq.toArray
                        Array.blit chars 0 destination destinationOffset chars.Length
                        if destinationOffset + chars.Length < destination.Length then destination[destinationOffset + chars.Length] <- 0
                        Ok(Runtime.NumberValue chars.Length)
                    | _ -> Error "strcpy expects a destination array and source text"
              "strcat", fun xs ->
                    match xs with
                    | [Runtime.CharacterArrayValue(destination, destinationOffset); source] ->
                        let start = destinationOffset + (destination |> Seq.skip destinationOffset |> Seq.takeWhile ((<>) 0) |> Seq.length)
                        let chars = textOf source |> Seq.map int |> Seq.truncate (max 0 (destination.Length - start)) |> Seq.toArray
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
        executeTextWithLimit maxSteps "" source

    let executeWithInputWithLimit maxSteps input source : Result<Execution,string> =
        executeTextWithLimit maxSteps input source

#if !FABLE_COMPILER
    let executeFileWithLimit maxSteps sourcePath : Result<Execution,string> =
        try
            expandIncludes sourcePath |> executeTextWithLimit maxSteps ""
        with ex ->
            Error ex.Message

    let executeFileWithInputWithLimit maxSteps input sourcePath : Result<Execution,string> =
        try
            expandIncludes sourcePath |> executeTextWithLimit maxSteps input
        with ex ->
            Error ex.Message
#endif

    let execute source = executeWithLimit 1_000_000 source
