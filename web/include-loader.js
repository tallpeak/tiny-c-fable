function includeName(text) {
  const trimmed = text.trim();
  if (trimmed.length >= 2) {
    const first = trimmed[0];
    const last = trimmed[trimmed.length - 1];
    if ((first === '"' && last === '"') || (first === "'" && last === "'")) {
      return trimmed.slice(1, -1);
    }
  }
  return trimmed;
}

async function fetchSource(url) {
  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(`${response.status} ${response.statusText}`);
  }
  return response.text();
}

async function resolveInclude(path, sourceUrl) {
  const source = new URL(sourceUrl, window.location.href);
  const candidates = [];

  if (path.startsWith("/")) {
    candidates.push(new URL(path, source.origin));
  } else {
    let directory = new URL(".", source);
    while (directory.origin === source.origin) {
      candidates.push(new URL(path, directory));
      const parent = new URL("..", directory);
      if (parent.href === directory.href) break;
      directory = parent;
    }
  }

  for (const candidate of candidates) {
    try {
      return { url: candidate, text: await fetchSource(candidate) };
    } catch (error) {
      // A missing candidate may exist relative to an ancestor directory.
      if (!String(error.message).startsWith("404 ")) throw error;
    }
  }

  throw new Error(`Unable to resolve include '${path}' from '${source.pathname}'`);
}

export async function expandSourceWithIncludes(source, sourceUrl, loading = new Set()) {
  const canonicalUrl = new URL(sourceUrl, window.location.href).href;
  if (loading.has(canonicalUrl)) {
    throw new Error(`Recursive include detected: ${new URL(canonicalUrl).pathname}`);
  }

  loading.add(canonicalUrl);
  try {
    const expanded = [];
    for (const line of source.split(/\r?\n/)) {
      const match = line.match(/^\s*#include\s+(.+?)\s*$/);
      if (!match) {
        expanded.push(line);
        continue;
      }

      const included = await resolveInclude(includeName(match[1]), canonicalUrl);
      expanded.push(await expandSourceWithIncludes(included.text, included.url, loading));
    }
    return expanded.join("\n");
  } finally {
    loading.delete(canonicalUrl);
  }
}

export async function loadSourceWithIncludes(sourceUrl) {
  const url = new URL(sourceUrl, window.location.href);
  return expandSourceWithIncludes(await fetchSource(url), url);
}
