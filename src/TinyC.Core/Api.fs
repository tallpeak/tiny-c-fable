namespace TinyC

open System
open System.Collections.Generic
open System.IO
open System.Text

module Api =
    type Execution = { Output: string; ExitValue: int; Steps: int }

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
        let charOf = function
            | Runtime.NumberValue n -> char n
            | Runtime.TextValue s -> if String.IsNullOrEmpty s then '\000' else s[0]
            | Runtime.CharacterArrayValue(values, offset) ->
                if offset < 0 || offset >= values.Length then '\000' else char values[offset]
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
              "pc", fun xs -> match xs with [Runtime.NumberValue n] -> output.Append(char n) |> ignore; Ok(Runtime.NumberValue n) | _ -> Error "pc expects one character" ] |> Map.ofList
        Parser.parse source
        |> Result.mapError(fun d -> sprintf "%d:%d: %s" d.Position.Line d.Position.Column d.Message)
        |> Result.bind(Runtime.run { MaxSteps=maxSteps; EntryPoint="main"; HostFunctions=hosts })
        |> Result.map(fun r ->
            { Output = output.ToString()
              ExitValue = (match r.Value with Runtime.NumberValue n -> n | _ -> 0)
              Steps = r.Steps })

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
