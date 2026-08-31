# Tiny-C Fable Core

A from-scratch F# implementation of the classical Tiny-C interpreter represented
by Tom Gibson's 1977 8080 version and later C rewrite. It parses source once to
an AST and evaluates that AST, instead of reparsing source text during every loop
iteration. The core uses APIs supported by Fable and can compile to JavaScript.

## Implemented

- `int` and `char` scalar variables
- integer and character literals, plus strings for browser host calls
- classical `[` / `]` blocks
- arithmetic, comparisons, assignment, `if`/`else`, `while`, `break`, `return`
- functions with classical declarations such as `double int x [ ... ]`
- integer/character arrays using classical `a(10)` syntax
- browser-safe host functions: `print`, `println`, `pn`, and `pc`
- execution step limit for stopping runaway programs
- line/column lexer and parser diagnostics

This milestone intentionally omits source includes, old pointer arithmetic,
file/system calls, dynamic native plugins, debugger commands, varargs, and the
numbered `MC` interface. Those need explicit browser-safe designs rather than a
literal port. Classical calls currently require parentheses.

## Build and test

Install .NET 8 or newer, then:

```sh
dotnet run --project tests/TinyC.Tests
dotnet run --project src/TinyC.Cli -- reference/tiny-c/SamplePrograms/lee.tc
dotnet tool restore
npm run build
```

Fable writes ES modules into `dist/`. `TinyC.Api.execute` and
`TinyC.Api.executeWithLimit` are the main embedding entry points.

## Playground

Serve the repository root with the included local server. It rebuilds the Fable
output first; opening the HTML file directly will prevent browser module imports:

```sh
npm run serve
```

Open `http://localhost:8080/web/` to use the textarea-based Tiny-C playground.

## Example

```text
sum int n [
    int i, total;
    i = 1;
    while (i <= n) [
        total = total + i;
        i = i + 1;
    ]
    return total;
]

main [
    println("sum = ");
    pn(sum(100));
    return sum(100);
]
```

## Design

The project is intentionally split into `Ast`, `Lexer`, `Parser`, `Runtime`,
and `Api`. `Runtime.HostFunction` is the capability boundary for a future web
worker and canvas/console UI. Keeping host functions explicit prevents Tiny-C
programs from gaining ambient browser or operating-system access.

## License and attribution

GPL-3.0-or-later, matching the supplied Tiny-C source distribution. Original
Tiny-C and the 1977 8080 implementation are credited to Tom Gibson.
