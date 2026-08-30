namespace TinyC

type SourcePos = { Line: int; Column: int }

type TcType = IntType | CharType

type UnaryOp = Negate | Positive

type BinaryOp =
    | Add | Subtract | Multiply | Divide | Remainder
    | Equal | NotEqual | Less | LessEqual | Greater | GreaterEqual

type Expr =
    | Number of int
    | Character of int
    | Text of string
    | Variable of string
    | Index of string * Expr
    | Call of string * Expr list
    | Unary of UnaryOp * Expr
    | Binary of BinaryOp * Expr * Expr
    | Assign of Expr * Expr

type Declaration = { Type: TcType; Name: string; Length: Expr option }

type Statement =
    | Empty
    | Block of Statement list
    | Declare of Declaration list
    | Expression of Expr
    | If of Expr * Statement * Statement option
    | While of Expr * Statement
    | Return of Expr option
    | Break

type Parameter = { Type: TcType; Name: string }

type FunctionDef = { Name: string; Parameters: Parameter list; Body: Statement }

type Program = {
    Globals: Declaration list
    Functions: Map<string, FunctionDef>
}

type Diagnostic = { Position: SourcePos; Message: string }
