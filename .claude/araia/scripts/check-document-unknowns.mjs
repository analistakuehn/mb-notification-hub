#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";

const SKIP_DIRECTORIES = new Set([".git", "node_modules", "dist", "build"]);

function usage() {
  return "Usage: node check-document-unknowns.mjs [--json] <file-or-directory> [...]";
}

function parseArgs(argv) {
  const result = { json: false, targets: [] };
  for (const arg of argv) {
    if (arg === "--json") {
      result.json = true;
      continue;
    }
    if (arg === "--help" || arg === "-h") return { help: true };
    if (arg.startsWith("--")) throw new Error(`Unknown option: ${arg}`);
    result.targets.push(arg);
  }
  return result;
}

function collectMarkdown(target) {
  const resolved = path.resolve(target);
  if (!fs.existsSync(resolved)) throw new Error(`Path not found: ${resolved}`);
  const stat = fs.statSync(resolved);
  if (stat.isFile()) {
    if (path.extname(resolved).toLowerCase() !== ".md") {
      throw new Error(`Document must be markdown: ${resolved}`);
    }
    return [resolved];
  }
  if (!stat.isDirectory()) throw new Error(`Unsupported path type: ${resolved}`);

  const files = [];
  const visit = (directory) => {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      if (entry.isDirectory() && SKIP_DIRECTORIES.has(entry.name)) continue;
      const candidate = path.join(directory, entry.name);
      if (entry.isDirectory()) visit(candidate);
      else if (entry.isFile() && path.extname(entry.name).toLowerCase() === ".md") {
        files.push(candidate);
      }
    }
  };
  visit(resolved);
  return files.sort();
}

function visibleLines(text) {
  let insideFence = false;
  return text.split(/\r?\n/).map((line) => {
    if (/^\s*(```|~~~)/.test(line)) {
      insideFence = !insideFence;
      return "";
    }
    return insideFence ? "" : line;
  });
}

const LINE_RULES = [
  {
    code: "UNKNOWN_SENTINEL",
    pattern: /\bUNKNOWN\b/,
    message: "Remove the UNKNOWN sentinel and omit or resolve its containing content.",
  },
  {
    code: "UNRESOLVED_SENTINEL",
    pattern: /\b(?:TBD|TO BE DETERMINED|A DEFINIR|A CONFIRMAR|NOT EVIDENCED)\b/i,
    message: "Remove the unresolved sentinel and omit or resolve its containing content.",
  },
  {
    code: "DANGLING_PLACEHOLDER",
    // Case-sensitive on purpose. `TODO` and `XXX` are uppercase by convention,
    // and matching them case-insensitively flags the ordinary Portuguese word
    // "todo", which appears in normal prose ("todo arquivo", "em todo caso").
    // The other alternatives carry no case, so dropping the flag changes only
    // the sentinel.
    pattern: /\{\{[^}\n]+\}\}|^\s*<[^>\n]{2,160}>\s*$|\b(?:TODO|XXX)\b|\?\?\?/,
    message: "Remove the unresolved template placeholder.",
  },
  {
    code: "UNRESOLVED_SECTION",
    pattern: /^#{1,6}\s+(?:.*\bopen questions?\b|.*\bquestões em aberto\b|unknowns?|evidence gaps?)\s*$/i,
    message: "Remove the unresolved-information section from the durable document.",
  },
  {
    code: "EMPTY_TABLE_VALUE",
    pattern: /\|\s*\|/,
    message: "Remove the empty table value or omit the row.",
  },
];

function auditFile(file) {
  const lines = visibleLines(fs.readFileSync(file, "utf8"));
  const findings = [];
  for (let index = 0; index < lines.length; index += 1) {
    const line = lines[index];
    for (const rule of LINE_RULES) {
      if (!rule.pattern.test(line)) continue;
      findings.push({
        file,
        line: index + 1,
        code: rule.code,
        message: rule.message,
      });
    }
  }
  return findings;
}

function render(findings, files) {
  if (findings.length === 0) {
    return `PASS: ${files.length} document(s) contain no persisted unknown information.`;
  }
  return [
    `FAIL: ${findings.length} persisted unknown-information finding(s).`,
    ...findings.map(
      (finding) =>
        `${finding.file}:${finding.line} [${finding.code}] ${finding.message}`,
    ),
  ].join("\n");
}

function main() {
  let args;
  try {
    args = parseArgs(process.argv.slice(2));
  } catch (error) {
    console.error(`${error.message}\n${usage()}`);
    return 2;
  }

  if (args.help) {
    console.log(usage());
    return 0;
  }
  if (args.targets.length === 0) {
    console.error(usage());
    return 2;
  }

  let files;
  try {
    files = [...new Set(args.targets.flatMap(collectMarkdown))];
  } catch (error) {
    console.error(error.message);
    return 2;
  }

  const findings = files.flatMap(auditFile);
  if (args.json) {
    console.log(JSON.stringify({ files, findings }, null, 2));
  } else {
    console.log(render(findings, files));
  }
  return findings.length === 0 ? 0 : 1;
}

process.exitCode = main();
