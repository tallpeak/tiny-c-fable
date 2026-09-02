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
const canvas = document.querySelector("#canvas");
const context = canvas.getContext("2d");
const status = document.querySelector("#status");
const stepLimit = document.querySelector("#step-limit");
const runButton = document.querySelector("#run");
const mathLibButton = document.querySelector("#mathlib-example");
const colorButton = document.querySelector("#color-example");
const sampleUrl = (name) => new URL(`../reference/tiny-c/SamplePrograms/${name}`, import.meta.url);
let sourceUrl = sampleUrl("editor.tc");

function drawCanvas(commands) {
  let x = 0;
  let y = 0;
  for (const command of commands.split(/\r?\n/)) {
    if (!command) continue;
    const parts = command.split("|");
    switch (parts[0]) {
      case "clear":
        canvas.width = Number(parts[1]);
        canvas.height = Number(parts[2]);
        context.clearRect(0, 0, canvas.width, canvas.height);
        context.fillStyle = "rgb(0, 0, 0)";
        context.strokeStyle = "rgb(0, 0, 0)";
        break;
      case "rgb":
        context.fillStyle = `rgb(${parts[1]}, ${parts[2]}, ${parts[3]})`;
        context.strokeStyle = context.fillStyle;
        break;
      case "fontsize":
        context.font = `${Number(parts[1])}px sans-serif`;
        break;
      case "rectangle":
        context.beginPath();
        context.rect(Number(parts[1]), Number(parts[2]), Number(parts[3]), Number(parts[4]));
        break;
      case "fill":
        context.fill();
        break;
      case "stroke":
        context.stroke();
        break;
      case "moveto":
        x = Number(parts[1]);
        y = Number(parts[2]);
        break;
      case "text":
        context.fillText(parts.slice(1).join("|"), x, y);
        break;
      default:
        throw new Error(`Unknown canvas command '${parts[0]}'`);
    }
  }
}

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
      drawCanvas(execution.CanvasCommands);
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
  sourceUrl = sampleUrl("editor.tc");
  output.textContent = "";
  status.textContent = "Example loaded";
  status.className = "";
});
mathLibButton.addEventListener("click", async () => {
  const url = sampleUrl("testMathLib-lrb.tc");
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
colorButton.addEventListener("click", async () => {
  const url = sampleUrl("color.tc");
  colorButton.disabled = true;
  status.textContent = "Loading color.tc…";
  status.className = "";
  try {
    const response = await fetch(url);
    if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
    source.value = await response.text();
    sourceUrl = url;
    output.textContent = "";
    status.textContent = "color.tc loaded";
  } catch (error) {
    output.textContent = error instanceof Error ? error.message : String(error);
    status.textContent = "Load error";
    status.className = "error";
  } finally {
    colorButton.disabled = false;
  }
});

source.value = example;
