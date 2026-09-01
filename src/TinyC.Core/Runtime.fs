namespace TinyC

open System.Collections.Generic

module Runtime =
    type Value = NumberValue of int | TextValue of string | CharacterArrayValue of int array * int
    type HostFunction = Value list -> Result<Value, string>
    type RunOptions = { MaxSteps: int; EntryPoint: string; HostFunctions: Map<string,HostFunction> }
    type RunResult = { Value: Value; Steps: int }

    // The declared type lives with storage so char assignment can retain the
    // classical one-byte value semantics without exposing pointer operations.
    type private Slot = Scalar of TcType * int ref | Array of TcType * int array | ValueSlot of TcType * Value ref
    type private Environment = Dictionary<string,Slot> list
    type private Flow = Continue | Returned of Value | Broken
    exception private RuntimeFailure of string

    let private number = function
        | NumberValue n -> n
        | TextValue _ | CharacterArrayValue _ -> raise(RuntimeFailure "Expected a numeric value")
    let private truth n = n <> 0

    let run (options: RunOptions) (program: Program) : Result<RunResult,string> =
        try
            let globals=Dictionary<string,Slot>()
            let mutable steps=0
            let tick () = steps <- steps+1; if steps > options.MaxSteps then raise(RuntimeFailure "Execution step limit exceeded")
            let allocate (frame: Dictionary<string,Slot>) (d: Declaration) (length: int option) =
                if frame.ContainsKey d.Name then raise(RuntimeFailure(sprintf "Duplicate variable '%s'" d.Name))
                if length |> Option.exists (fun n -> n < 0) then raise(RuntimeFailure "Array length cannot be negative")
                // The classical Tiny-C declaration a(n) reserves indices 0..n.
                frame[d.Name] <- match length with Some n -> Array(d.Type, Array.zeroCreate (n + 1)) | None -> Scalar(d.Type, ref 0)
            for d in program.Globals do
                match d.Length with None -> allocate globals d None | Some(Number n) -> allocate globals d (Some n) | Some _ -> raise(RuntimeFailure "Global array length must be constant")

            let rec invoke (name: string) (args: Value list) =
                tick()
                match Map.tryFind name options.HostFunctions with
                | Some host -> match host args with Ok v -> v | Error e -> raise(RuntimeFailure e)
                | None ->
                    match Map.tryFind name program.Functions with
                    | None -> raise(RuntimeFailure(sprintf "Unknown function '%s'" name))
                    | Some fn ->
                        if args.Length <> fn.Parameters.Length then raise(RuntimeFailure(sprintf "Function '%s' expects %d argument(s)" name fn.Parameters.Length))
                        let locals=Dictionary<string,Slot>()
                        List.zip fn.Parameters args |> List.iter(fun (p,v) ->
                            if locals.ContainsKey p.Name then raise(RuntimeFailure(sprintf "Duplicate parameter '%s'" p.Name))
                            locals[p.Name] <-
                                if p.IsArray then ValueSlot(p.Type, ref v)
                                else Scalar(p.Type, ref(number v)))
                        match exec [locals] fn.Body with Returned v -> v | _ -> NumberValue 0
            and findSlot (env: Environment) (name: string) =
                let rec find (frames: Environment) =
                    match frames with
                    | [] -> match globals.TryGetValue name with true,v -> v | _ -> raise(RuntimeFailure(sprintf "Unknown variable '%s'" name))
                    | frame::rest -> match frame.TryGetValue name with true,v -> v | _ -> find rest
                find env
            and tryFindSlot (env: Environment) (name: string) =
                let rec find (frames: Environment) =
                    match frames with
                    | [] -> match globals.TryGetValue name with true,v -> Some v | _ -> None
                    | frame::rest -> match frame.TryGetValue name with true,v -> Some v | _ -> find rest
                find env
            and eval (env: Environment) (expr: Expr) =
                tick()
                match expr with
                | Number n | Character n -> NumberValue n
                | Text x -> TextValue x
                | Variable name ->
                    match findSlot env name with
                    | Scalar(_, r) -> NumberValue r.Value
                    | Array(CharType, values) -> CharacterArrayValue(values, 0)
                    | Array _ -> raise(RuntimeFailure(sprintf "Array '%s' requires an index" name))
                    | ValueSlot(_, v) -> v.Value
                | Index(name,index) ->
                    match findSlot env name with
                    | Array(_, a) -> let i=eval env index |> number in if i<0 || i>=a.Length then raise(RuntimeFailure "Array index out of range") else NumberValue a[i]
                    | ValueSlot(_, v) ->
                        match v.Value with
                        | CharacterArrayValue(values, offset) ->
                            let i = eval env index |> number
                            let actual = offset + i
                            if i < 0 || actual < 0 || actual >= values.Length then raise(RuntimeFailure "Array index out of range")
                            NumberValue values[actual]
                        | _ -> raise(RuntimeFailure(sprintf "'%s' is not an array" name))
                    | _ -> raise(RuntimeFailure(sprintf "'%s' is not an array" name))
                | Call(name,[index]) ->
                    match tryFindSlot env name with
                    | Some(Array(_, a)) -> let i=eval env index |> number in if i<0 || i>=a.Length then raise(RuntimeFailure "Array index out of range") else NumberValue a[i]
                    | Some(Scalar _) -> raise(RuntimeFailure(sprintf "'%s' is not an array" name))
                    | Some(ValueSlot(_, v)) ->
                        match v.Value with
                        | CharacterArrayValue(values, offset) ->
                            let i = eval env index |> number
                            let actual = offset + i
                            if i < 0 || actual < 0 || actual >= values.Length then raise(RuntimeFailure "Array index out of range")
                            NumberValue values[actual]
                        | _ -> raise(RuntimeFailure(sprintf "'%s' is not an array" name))
                    | None -> invoke name [eval env index]
                | Call(name,args) -> invoke name (args |> List.map(eval env))
                | Unary(op,x) -> let n=eval env x |> number in NumberValue(if op=Negate then -n else n)
                | Binary(op,a,b) ->
                    let left, right = eval env a, eval env b
                    match op, left, right with
                    | Add, CharacterArrayValue(values, offset), NumberValue n -> CharacterArrayValue(values, offset + n)
                    | Add, NumberValue x, NumberValue y -> NumberValue(x+y)
                    | Add, _, _ -> raise(RuntimeFailure "Only a character array can be offset for output")
                    | _, _, _ ->
                        let x, y = number left, number right
                        let yes v=NumberValue(if v then 1 else 0)
                        match op with
                        | Subtract -> NumberValue(x-y) | Multiply -> NumberValue(x*y)
                        | Divide -> if y=0 then raise(RuntimeFailure "Division by zero") else NumberValue(x/y)
                        | Remainder -> if y=0 then raise(RuntimeFailure "Division by zero") else NumberValue(x%y)
                        | Equal -> yes(x=y) | NotEqual -> yes(x<>y) | Less -> yes(x<y) | LessEqual -> yes(x<=y)
                        | Greater -> yes(x>y) | GreaterEqual -> yes(x>=y)
                        | Add -> raise(RuntimeFailure "Unreachable addition case")
                | Assign(target,value) -> assign env target value
            and assign (env: Environment) (target: Expr) (value: Expr) =
                let store typ value = if typ = CharType then value &&& 0xff else value
                match target with
                | Variable name ->
                    let assigned = eval env value |> number
                    match findSlot env name with
                    | Scalar(typ, r) -> r.Value <- store typ assigned; NumberValue r.Value
                    | Array _ -> raise(RuntimeFailure "Cannot assign an array")
                    | ValueSlot(typ, r) -> r.Value <- NumberValue(store typ assigned); r.Value
                | Index(name,index) ->
                    match findSlot env name with
                    | Array(typ, a) ->
                        let i=eval env index |> number
                        if i<0 || i>=a.Length then raise(RuntimeFailure "Array index out of range")
                        let assigned = eval env value |> number
                        a[i] <- store typ assigned
                        NumberValue a[i]
                    | ValueSlot(typ, r) ->
                        match r.Value with
                        | CharacterArrayValue(values, offset) ->
                            let i = eval env index |> number
                            let actual = offset + i
                            if i < 0 || actual < 0 || actual >= values.Length then raise(RuntimeFailure "Array index out of range")
                            let assigned = eval env value |> number
                            values[actual] <- store typ assigned
                            NumberValue values[actual]
                        | _ -> raise(RuntimeFailure(sprintf "'%s' is not an array" name))
                    | _ -> raise(RuntimeFailure(sprintf "'%s' is not an array" name))
                | Call(name,[index]) ->
                    match findSlot env name with
                    | Array(typ, a) ->
                        let i=eval env index |> number
                        if i<0 || i>=a.Length then raise(RuntimeFailure "Array index out of range")
                        let assigned = eval env value |> number
                        a[i] <- store typ assigned
                        NumberValue a[i]
                    | _ -> raise(RuntimeFailure(sprintf "'%s' is not an array" name))
                | _ -> raise(RuntimeFailure "Left side of assignment is not assignable")
            and exec (env: Environment) (statement: Statement) =
                tick()
                match statement with
                | Empty -> Continue
                | Expression e -> eval env e |> ignore; Continue
                | Declare ds ->
                    let frame = List.head env
                    for d in ds do
                        let len = d.Length |> Option.map (fun e -> eval env e |> number)
                        allocate frame d len
                    Continue
                | Return x -> Returned(match x with Some e -> eval env e | None -> NumberValue 0)
                | Break -> Broken
                | If(c,y,n) -> if eval env c |> number |> truth then exec env y else n |> Option.map(exec env) |> Option.defaultValue Continue
                | Block xs ->
                    let blockFrame = Dictionary<string,Slot>()
                    let mutable flow=Continue
                    for x in xs do if flow=Continue then flow <- exec (blockFrame::env) x
                    flow
                | While(c,body) ->
                    let mutable flow=Continue
                    while flow=Continue && (eval env c |> number |> truth) do
                        match exec env body with Broken -> flow <- Broken | Returned v -> flow <- Returned v | Continue -> ()
                    if flow=Broken then Continue else flow

            let value=invoke options.EntryPoint []
            Ok { Value=value; Steps=steps }
        with RuntimeFailure e -> Error e
