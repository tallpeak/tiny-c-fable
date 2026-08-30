namespace TinyC

open Lexer

module Parser =
    exception private ParseFailure of Diagnostic

    type private State(tokens: Token list) =
        let items = List.toArray tokens
        let mutable index = 0
        member _.Current = items[index]
        member _.Take() = let t = items[index] in index <- index + 1; t
        member this.Error message = raise (ParseFailure { Position = this.Current.Position; Message = message })

    let private same a b =
        match a, b with
        | KwInt,KwInt | KwChar,KwChar | KwIf,KwIf | KwElse,KwElse | KwWhile,KwWhile
        | KwReturn,KwReturn | KwBreak,KwBreak | LParen,LParen | RParen,RParen
        | LBracket,LBracket | RBracket,RBracket | Comma,Comma | Semicolon,Semicolon
        | Plus,Plus | Minus,Minus | Star,Star | Slash,Slash | Percent,Percent
        | Assign,Assign | EqEq,EqEq | BangEq,BangEq | Lt,Lt | LtEq,LtEq
        | Gt,Gt | GtEq,GtEq | Eof,Eof -> true
        | _ -> false

    let private accept (s: State) kind = if same s.Current.Kind kind then s.Take() |> ignore; true else false
    let private expect s kind message = if not (accept s kind) then s.Error message
    let private identifier (s: State) =
        match s.Take() with | { Kind = Identifier x } -> x | t -> raise (ParseFailure { Position=t.Position; Message="Expected a name" })
    let private tcType (s: State) =
        match s.Take() with | { Kind=KwInt } -> IntType | { Kind=KwChar } -> CharType | t -> raise (ParseFailure { Position=t.Position; Message="Expected int or char" })

    let rec private expression s = assignment s
    and private assignment s =
        let left = comparison s
        if accept s Assign then TinyC.Assign(left, assignment s) else left
    and private comparison s =
        let mutable left = additive s
        let mutable looping = true
        while looping do
            let op =
                if accept s EqEq then Some Equal elif accept s BangEq then Some NotEqual
                elif accept s LtEq then Some LessEqual elif accept s GtEq then Some GreaterEqual
                elif accept s Lt then Some Less elif accept s Gt then Some Greater else None
            match op with Some x -> left <- Binary(x,left,additive s) | None -> looping <- false
        left
    and private additive s =
        let mutable left = multiplicative s
        let mutable looping = true
        while looping do
            if accept s Plus then left <- Binary(Add,left,multiplicative s)
            elif accept s Minus then left <- Binary(Subtract,left,multiplicative s)
            else looping <- false
        left
    and private multiplicative s =
        let mutable left = unary s
        let mutable looping = true
        while looping do
            if accept s Star then left <- Binary(Multiply,left,unary s)
            elif accept s Slash then left <- Binary(Divide,left,unary s)
            elif accept s Percent then left <- Binary(Remainder,left,unary s)
            else looping <- false
        left
    and private unary s =
        if accept s Minus then Unary(Negate, unary s)
        elif accept s Plus then Unary(Positive, unary s)
        else primary s
    and private primary (s: State) =
        match s.Take() with
        | { Kind=Integer n } -> Number n
        | { Kind=CharLiteral c } -> Character c
        | { Kind=StringLiteral x } -> Text x
        | { Kind=LParen } -> let e=expression s in expect s RParen "Expected ')'"; e
        | { Kind=Identifier name } ->
            if accept s LParen then
                let args = ResizeArray<Expr>()
                if not (accept s RParen) then
                    args.Add(expression s)
                    while accept s Comma do args.Add(expression s)
                    expect s RParen "Expected ')' after arguments"
                Call(name,List.ofSeq args)
            else Variable name
        | t -> raise (ParseFailure { Position=t.Position; Message="Expected an expression" })

    let private declarators (s: State) typ =
        let one () =
            let name = identifier s
            let length = if accept s LParen then let e=expression s in expect s RParen "Expected ')' after array length"; Some e else None
            { Type=typ; Name=name; Length=length }
        let xs = ResizeArray<Declaration>()
        xs.Add(one())
        while accept s Comma do xs.Add(one())
        List.ofSeq xs

    let rec private statement (s: State) =
        if accept s Semicolon then Empty
        elif accept s LBracket then
            let xs=ResizeArray<Statement>()
            while not (accept s RBracket) do
                if same s.Current.Kind Eof then s.Error "Expected ']'" else xs.Add(statement s)
            Block(List.ofSeq xs)
        elif same s.Current.Kind KwInt || same s.Current.Kind KwChar then
            let ds = declarators s (tcType s)
            accept s Semicolon |> ignore
            Declare ds
        elif accept s KwIf then
            // Parentheses are conventional, but the original Tiny-C accepts
            // them optionally around conditions.
            let parenthesized = accept s LParen
            let condition=expression s
            if parenthesized then expect s RParen "Expected ')' after if condition"
            let yes=statement s
            let no=if accept s KwElse then Some(statement s) else None
            If(condition,yes,no)
        elif accept s KwWhile then
            let parenthesized = accept s LParen
            let condition=expression s
            if parenthesized then expect s RParen "Expected ')' after while condition"
            While(condition,statement s)
        elif accept s KwReturn then
            let value = if same s.Current.Kind Semicolon || same s.Current.Kind RBracket then None else Some(expression s)
            accept s Semicolon |> ignore; Return value
        elif accept s KwBreak then accept s Semicolon |> ignore; Break
        else let e=expression s in accept s Semicolon |> ignore; Expression e

    let parseTokens tokens : Result<Program, Diagnostic> =
        try
            let s=State tokens
            let globals=ResizeArray<Declaration>()
            let functions=ResizeArray<FunctionDef>()
            while not (same s.Current.Kind Eof) do
                if same s.Current.Kind KwInt || same s.Current.Kind KwChar then
                    let typ=tcType s
                    for d in declarators s typ do globals.Add d
                    accept s Semicolon |> ignore
                else
                    let name=identifier s
                    let ps=ResizeArray<Parameter>()
                    while not (same s.Current.Kind LBracket) do
                        if same s.Current.Kind Eof then s.Error "Expected '[' to begin function body"
                        let typ=tcType s
                        ps.Add { Type=typ; Name=identifier s }
                        while accept s Comma do ps.Add { Type=typ; Name=identifier s }
                        accept s Semicolon |> ignore
                    let body=statement s
                    functions.Add { Name=name; Parameters=List.ofSeq ps; Body=body }
            let functionMap = functions |> Seq.map(fun f -> f.Name,f) |> Map.ofSeq
            if functionMap.Count <> functions.Count then s.Error "Duplicate function declaration"
            Ok { Globals=List.ofSeq globals; Functions=functionMap }
        with ParseFailure d -> Error d

    let parse source = Lexer.tokenize source |> Result.bind parseTokens
