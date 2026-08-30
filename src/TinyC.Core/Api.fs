namespace TinyC

module Api =
    type Execution = { Output: string; ExitValue: int; Steps: int }

    let executeWithLimit maxSteps source : Result<Execution,string> =
        let output=System.Text.StringBuilder()
        let display = function Runtime.NumberValue n -> string n | Runtime.TextValue s -> s
        let hosts : Map<string,Runtime.HostFunction> =
            [ "print", fun xs -> xs |> List.iter(fun x -> output.Append(display x) |> ignore); Ok(Runtime.NumberValue 0)
              "println", fun xs -> xs |> List.iter(fun x -> output.Append(display x) |> ignore); output.AppendLine() |> ignore; Ok(Runtime.NumberValue 0)
              "pn", fun xs -> match xs with [Runtime.NumberValue n] -> output.Append(n) |> ignore; Ok(Runtime.NumberValue n) | _ -> Error "pn expects one integer"
              "pc", fun xs -> match xs with [Runtime.NumberValue n] -> output.Append(char n) |> ignore; Ok(Runtime.NumberValue n) | _ -> Error "pc expects one character" ] |> Map.ofList
        Parser.parse source
        |> Result.mapError(fun d -> sprintf "%d:%d: %s" d.Position.Line d.Position.Column d.Message)
        |> Result.bind(Runtime.run { MaxSteps=maxSteps; EntryPoint="main"; HostFunctions=hosts })
        |> Result.map(fun r ->
            { Output = output.ToString()
              ExitValue = (match r.Value with Runtime.NumberValue n -> n | _ -> 0)
              Steps = r.Steps })

    let execute source = executeWithLimit 1_000_000 source
