open System
open System.IO
open TinyC

let rec private findSampleProgram (dir: string) =
    let candidate = Path.Combine(dir, "reference", "tiny-c", "SamplePrograms", "testMathLib-lrb.tc")
    if File.Exists candidate then candidate
    else
        let parent = Directory.GetParent dir
        if isNull parent then failwith "Could not locate reference/tiny-c/SamplePrograms/testMathLib-lrb.tc"
        else findSampleProgram parent.FullName

let sampleProgram = findSampleProgram AppContext.BaseDirectory

let cases =
    [ "arithmetic", "main [ return 2 + 3 * 4; ]", 14, ""
      "function", "double int x [ return x*2; ] main [ return double(21); ]", 42, ""
      "loop", "main [ int i, total; i=1; while(i<=10) [ total=total+i; i=i+1; ] return total; ]", 55, ""
      "array", "main [ int a(3); a(0)=7; a(1)=8; a(2)=9; return a(0)+a(1)+a(2); ]", 24, ""
      "array shadows function", "f int x [ return x+100; ] main [ int f(1); f(0)=7; return f(0); ]", 7, ""
      "integer array reference", "set int p(1) [ p(0)=20; p(1)=22; ] main [ int a(1); set a; return a(0)+a(1); ]", 42, ""
      "integer pointer offset", "set int p(1) [ p(0)=42; ] main [ int a(3); set a+2*4; return a(2); ]", 42, ""
      "nested scope", "main [ int x; x=3; [ int x; x=9; ] return x; ]", 3, ""
      "later function replaces earlier", "value [ return 1; ] value [ return 2; ] main [ return value(); ]", 2, ""
      "char storage", "main [ char c; c=300; return c; ]", 44, ""
      "classical char buffer", "main [ char line(2); line(0)='A'; line(1)='B'; line(2)=0; pl line+1; ]", 0, "B\n"
      "break", "main [ int i; while(1) [ i=i+1; if(i==3) break; ] return i; ]", 3, ""
      "return through loop", "main [ while(1) [ return 42; ] ]", 42, ""
      "readme example", "sum int n [ int i, total; i=1; while(i<=n) [ total=total+i; i=i+1; ] return total; ] main [ println(\"sum = \" ); pn(sum(100)); return sum(100); ]", 5050, "sum = \n5050"
      "output", "main [ println(\"hello from tiny-c\"); return 0; ]", 0, "hello from tiny-c\n" ]

let mutable failures=0
for name,source,wantValue,wantOutput in cases do
    match Api.execute source with
    | Ok result when result.ExitValue=wantValue && result.Output.Replace("\r\n","\n")=wantOutput -> printfn "PASS %s (%d steps)" name result.Steps
    | Ok result -> failures<-failures+1; eprintfn "FAIL %s: got value=%d output=%A" name result.ExitValue result.Output
    | Error e -> failures<-failures+1; eprintfn "FAIL %s: %s" name e
if failures>0 then failwithf "%d test(s) failed" failures

let failuresBeforeErrors = failures
let errorCases =
    [ "assignment target", "main [ 1=2; ]", "Left side of assignment is not assignable"
      "array range", "main [ int a(1); return a(2); ]", "Array index out of range"
      "step limit", "main [ while(1) [ ] ]", "Execution step limit exceeded" ]

for name, source, expected in errorCases do
    let result = if name = "step limit" then Api.executeWithLimit 25 source else Api.execute source
    match result with
    | Error message when message.Contains expected -> printfn "PASS %s" name
    | Error message -> failures <- failures + 1; eprintfn "FAIL %s: got %s" name message
    | Ok value -> failures <- failures + 1; eprintfn "FAIL %s: unexpectedly succeeded with %A" name value

if failures > failuresBeforeErrors then failwithf "%d error test(s) failed" (failures - failuresBeforeErrors)

match Api.executeFileWithLimit 10_000_000 sampleProgram with
| Ok result when result.ExitValue = 0 && result.Output.Contains "testMathLib.tc - 1/11/19" -> printfn "PASS sample includes (%d steps)" result.Steps
| Ok result -> failwithf "sample includes failed: exit=%d output prefix=%A" result.ExitValue (result.Output.Substring(0, min result.Output.Length 80))
| Error message -> failwithf "sample includes failed: %s" message
