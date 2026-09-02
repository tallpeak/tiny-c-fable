import { executeWithLimit } from "../dist/Api.js";
import { expandSourceWithIncludes } from "./include-loader.js";

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
const runButton = document.querySelector("#run");
const mathLibButton = document.querySelector("#mathlib-example");
let sourceUrl = new URL("../reference/tiny-c/SamplePrograms/editor.tc", window.location.href);

async function run() {
  const maxSteps = Number.parseInt(stepLimit.value, 10);
  if (!Number.isSafeInteger(maxSteps) || maxSteps < 1) {
    output.textContent = "Choose a positive integer step limit.";
    status.textContent = "Invalid step limit";
    status.className = "error";
    return;
  }

  runButton.disabled = true;
  status.textContent = "Loading includes…";
  status.className = "";
  try {
    const expandedSource = await expandSourceWithIncludes(source.value, sourceUrl);
    status.textContent = "Running…";
    const result = executeWithLimit(maxSteps, expandedSource);
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
  } catch (error) {
    output.textContent = error instanceof Error ? error.message : String(error);
    status.textContent = "Include error";
    status.className = "error";
  } finally {
    runButton.disabled = false;
  }
}

runButton.addEventListener("click", run);
document.querySelector("#example").addEventListener("click", () => {
  source.value = example;
  sourceUrl = new URL("../reference/tiny-c/SamplePrograms/editor.tc", window.location.href);
  output.textContent = "";
  status.textContent = "Example loaded";
  status.className = "";
});
mathLibButton.addEventListener("click", async () => {
  const url = new URL("../reference/tiny-c/SamplePrograms/testMathLib-lrb.tc", window.location.href);
  mathLibButton.disabled = true;
  status.textContent = "Loading Lee MathLib…";
  status.className = "";
  try {
    const response = await fetch(url);
    if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
    source.value = await response.text();
    sourceUrl = url;
    output.textContent = "";
    status.textContent = "Lee MathLib loaded";
  } catch (error) {
    output.textContent = error instanceof Error ? error.message : String(error);
    status.textContent = "Load error";
    status.className = "error";
  } finally {
    mathLibButton.disabled = false;
  }
});

source.value = example;
