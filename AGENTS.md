# Implementation TODOs

## Machine calls

The browser-safe runtime intentionally does not expose the process, filesystem, or
native-plugin capabilities used by these reference `machineCall.c` entries:

- `MC 0`: debugger breakpoint/variable dump (`_mzero`); the evaluator has no
  debugger or symbol-address API.
- `MC 3`–`MC 6`, `MC 10`–`MC 11`, and `MC 15`: reserved/undefined entries in
  the reference `origList` (`naf`), so their historical semantics are unknown.
- `MC 102`: sleeping synchronously would block the browser and the evaluator has
  no asynchronous host-call contract.
- `MC 103` and `MC 107`: file read/write; these need an explicit, sandboxed file
  service and a defined buffer/file representation.
- `MC 108` and `MC 109`: process termination; the host function API currently
  cannot signal evaluator termination separately from returning a value.
- `MC 111`–`MC 114`: stream open/write/read/close; these need a portable file
  handle service and lifecycle owned by the execution context.
- `MC 115`: property-file lookup; this needs a host-provided resource/property
  service.
- `MC 116`: process/system execution; intentionally unavailable in the browser
  sandbox.
- `MC 117`: writing an integer to an open file; depends on the missing stream
  handle service.
- `MC 120`–`MC 125`: reserved/undefined entries in the reference `newList`
  (`naf`).
- `MC 201`–`MC 210`: reserved/undefined user machine-call slots in the reference
  `userList` (`naf`).
- `MC >= 300`: dynamically loaded native/plugin calls; the Fable/browser runtime
  has no native dynamic-loader boundary.

The portable calls that do not require those capabilities are implemented in
`src/TinyC.Core/Api.fs`, including character/string operations, scanning, formatted
output, date formatting, and the integer square-root/arctangent calls.
