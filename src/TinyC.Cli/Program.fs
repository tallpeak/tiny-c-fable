open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open TinyC

let private usage () =
    eprintfn "Usage: tinyc [--max-steps N] <program.tc>"
    eprintfn "       tinyc --serve [port]"

let private contentType (path: string) =
    match Path.GetExtension(path).ToLowerInvariant() with
    | ".html" -> "text/html; charset=utf-8"
    | ".js" -> "text/javascript; charset=utf-8"
    | ".css" -> "text/css; charset=utf-8"
    | ".json" -> "application/json; charset=utf-8"
    | ".svg" -> "image/svg+xml"
    | _ -> "application/octet-stream"

let private serve port =
    let root = Path.GetFullPath(Directory.GetCurrentDirectory())
    let rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + string Path.DirectorySeparatorChar
    use listener = new TcpListener(IPAddress.Loopback, port)
    listener.Start()
    printfn "Tiny-C playground: http://localhost:%d/web/" port
    printfn "Serving %s (press Ctrl+C to stop)" root
    while true do
        use client = listener.AcceptTcpClient()
        use stream = client.GetStream()
        use reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true)
        let requestLine = reader.ReadLine()
        let mutable header = reader.ReadLine()
        while not (String.IsNullOrEmpty header) do header <- reader.ReadLine()
        let send status reason mime (bytes: byte array) =
            let headers = sprintf "HTTP/1.1 %d %s\r\nContent-Type: %s\r\nContent-Length: %d\r\nConnection: close\r\n\r\n" status reason mime bytes.Length
            let headerBytes = Encoding.ASCII.GetBytes headers
            stream.Write(headerBytes, 0, headerBytes.Length)
            stream.Write(bytes, 0, bytes.Length)
        let parts = if isNull requestLine then [||] else requestLine.Split(' ')
        if parts.Length < 2 || parts[0] <> "GET" then
            send 405 "Method Not Allowed" "text/plain; charset=utf-8" (Encoding.UTF8.GetBytes "Only GET is supported.")
        else
            let requestPath = parts[1].Split('?')[0] |> Uri.UnescapeDataString
            let relative = requestPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)
            let requested = if String.IsNullOrWhiteSpace relative then "web/index.html" else relative
            let candidate = Path.GetFullPath(Path.Combine(root, requested))
            let file = if Directory.Exists candidate then Path.Combine(candidate, "index.html") else candidate
            if not (file.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)) || not (File.Exists file) then
                send 404 "Not Found" "text/plain; charset=utf-8" (Encoding.UTF8.GetBytes "Not found.")
            else
                send 200 "OK" (contentType file) (File.ReadAllBytes file)

let private runProgram maxSteps path =
    if not (File.Exists path) then
        eprintfn "Tiny-C source file not found: %s" path
        2
    else
        match Api.executeFileWithLimit maxSteps path with
        | Ok result ->
            Console.Write(result.Output)
            eprintfn "\nExit value: %d (%d steps)" result.ExitValue result.Steps
            0
        | Error message ->
            eprintfn "Tiny-C error: %s" message
            1

[<EntryPoint>]
let main args =
    match args with
    | [| "--serve" |] -> serve 8080; 0
    | [| "--serve"; port |] ->
        match Int32.TryParse port with
        | true, value when value > 0 && value <= 65535 -> serve value; 0
        | _ -> eprintfn "Port must be between 1 and 65535."; 2
    | [| path |] -> runProgram 10_000_000 path
    | [| "--max-steps"; limit; path |] ->
        match Int32.TryParse limit with
        | true, value when value > 0 -> runProgram value path
        | _ -> eprintfn "Step limit must be a positive integer."; 2
    | _ -> usage (); 2
