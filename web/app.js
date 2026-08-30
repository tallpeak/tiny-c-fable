import { executeWithLimit } from "../dist/Api.js";

const example = `sum int n [
  int i, total;
  i = 1;
  while (i <= n) [
    total = total + i;
    i = i + 1;
  ]
  return total;
]

main [
  println("sum 1..100 =");
  pn(sum(100));
  return sum(100);
]`;

const source = document.querySelector("#source");
const output = document.querySelector("#output");
const status = document.querySelector("#status");
const stepLimit = document.querySelector("#step-limit");

function run() {
  const maxSteps = Number.parseInt(stepLimit.value, 10);
  if (!Number.isSafeInteger(maxSteps) || maxSteps < 1) {
    output.textContent = "Choose a positive integer step limit.";
    status.textContent = "Invalid step limit";
    status.className = "error";
    return;
  }

  const result = executeWithLimit(maxSteps, source.value);
  if (result.tag === 0) {
    const execution = result.fields[0];
    output.textContent = `${execution.Output}\n\nExit value: ${execution.ExitValue}\nSteps: ${execution.Steps}`;
    status.textContent = "Completed";
    status.className = "";
  } else {
    output.textContent = result.fields[0];
    status.textContent = "Program error";
    status.className = "error";
  }
}

document.querySelector("#run").addEventListener("click", run);
document.querySelector("#example").addEventListener("click", () => {
  source.value = example;
  output.textContent = "";
  status.textContent = "Example loaded";
  status.className = "";
});

source.value = example;
