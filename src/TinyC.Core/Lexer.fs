namespace TinyC

module Lexer =
    type TokenKind =
        | Identifier of string | Integer of int | CharLiteral of int | StringLiteral of string
        | KwInt | KwChar | KwIf | KwElse | KwWhile | KwReturn | KwBreak
        | LParen | RParen | LBracket | RBracket | Comma | Semicolon
        | Plus | Minus | Star | Slash | Percent | Assign
        | EqEq | BangEq | Lt | LtEq | Gt | GtEq | Eof

    type Token = { Kind: TokenKind; Position: SourcePos }

    let private keyword = function
        | "int" -> KwInt | "char" -> KwChar | "if" -> KwIf | "else" -> KwElse
        | "while" -> KwWhile | "return" -> KwReturn | "break" -> KwBreak
        | name -> Identifier name

    let private escaped = function
        | 'n' -> '\n' | 'r' -> '\r' | 't' -> '\t' | '0' -> '\000'
        | '\\' -> '\\' | '\'' -> '\'' | '"' -> '"' | c -> c

    let tokenize (source: string) : Result<Token list, Diagnostic> =
        let tokens = ResizeArray<Token>()
        let mutable i = 0
        let mutable line = 1
        let mutable column = 1
        let pos () = { Line = line; Column = column }
        let peek n = if i + n < source.Length then Some source[i+n] else None
        let advance () =
            let c = source[i]
            i <- i + 1
            if c = '\n' then line <- line + 1; column <- 1 else column <- column + 1
            c
        let add p kind = tokens.Add { Kind = kind; Position = p }
        let fail p message = Error { Position = p; Message = message }
        let mutable problem: Diagnostic option = None

        while i < source.Length && problem.IsNone do
            let p, c = pos(), source[i]
            if System.Char.IsWhiteSpace c then advance() |> ignore
            elif c = '/' && (peek 1 = Some '/' || peek 1 = Some '*') then
                // Classical Tiny-C treats both comment introducers as comments to EOL.
                while i < source.Length && source[i] <> '\n' do advance() |> ignore
            elif System.Char.IsLetter c || c = '_' then
                let start = i
                while i < source.Length && (System.Char.IsLetterOrDigit source[i] || source[i] = '_') do advance() |> ignore
                add p (keyword source[start..i-1])
            elif System.Char.IsDigit c then
                let start = i
                while i < source.Length && System.Char.IsDigit source[i] do advance() |> ignore
                match System.Int32.TryParse source[start..i-1] with
                | true, n -> add p (Integer n)
                | _ -> problem <- Some { Position = p; Message = "Integer literal is out of range" }
            elif c = '\'' then
                advance() |> ignore
                if i >= source.Length then problem <- Some { Position = p; Message = "Unterminated character literal" }
                else
                    let value =
                        if source[i] = '\\' then
                            advance() |> ignore
                            if i < source.Length then escaped (advance()) else '\000'
                        else
                            advance()
                    if i < source.Length && source[i] = '\'' then
                        advance() |> ignore
                        add p (CharLiteral (int value))
                    else problem <- Some { Position = p; Message = "Unterminated character literal" }
            elif c = '"' then
                advance() |> ignore
                let chars = System.Text.StringBuilder()
                while i < source.Length && source[i] <> '"' && problem.IsNone do
                    if source[i] = '\\' then
                        advance() |> ignore
                        if i < source.Length then chars.Append(escaped (advance())) |> ignore
                        else problem <- Some { Position = p; Message = "Unterminated string literal" }
                    else chars.Append(advance()) |> ignore
                if problem.IsNone then
                    if i < source.Length then
                        advance() |> ignore
                        add p (StringLiteral (chars.ToString()))
                    else problem <- Some { Position = p; Message = "Unterminated string literal" }
            else
                let two a b kind =
                    if c = a && peek 1 = Some b then advance() |> ignore; advance() |> ignore; add p kind; true else false
                if two '=' '=' EqEq || two '!' '=' BangEq || two '<' '=' LtEq || two '>' '=' GtEq then ()
                else
                    advance() |> ignore
                    match c with
                    | '(' -> add p LParen | ')' -> add p RParen | '[' -> add p LBracket | ']' -> add p RBracket
                    | ',' -> add p Comma | ';' -> add p Semicolon | '+' -> add p Plus | '-' -> add p Minus
                    | '*' -> add p Star | '/' -> add p Slash | '%' -> add p Percent | '=' -> add p Assign
                    | '<' -> add p Lt | '>' -> add p Gt
                    | _ -> problem <- Some { Position = p; Message = sprintf "Unexpected character '%c'" c }

        match problem with
        | Some d -> Error d
        | None -> add (pos()) Eof; Ok (List.ofSeq tokens)
