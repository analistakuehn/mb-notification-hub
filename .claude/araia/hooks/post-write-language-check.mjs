#!/usr/bin/env node
// PostToolUse hook: after a Write/Edit/MultiEdit, enforce the framework writing
// rules on the natural-language text just produced.
//
// Layered design (see framework/shared/post-write-language-enforcement.md):
//   - The deterministic verdict is single-sourced in
//     scripts/check-writing-rules.py (also wired into CI). This hook RUNS that
//     script and reports its concrete, line-anchored findings back to the model
//     so the correction is a fix-list ("line 83: term -> corrected term"),
//     not a vague "review the prose" nudge.
//   - It covers BOTH direct (main-agent) AND subagent writes. Subagents were the
//     blind spot: the prior version stayed silent for them on the assumption the
//     agent wiring enforced rules inline, which empirically it did not, so whole
//     files of PT-BR violations shipped. The deterministic scan closes that gap
//     for every writer.
//   - It NEVER blocks. A finding becomes additionalContext the model acts on;
//     a clean scan is silent; any internal failure degrades to deterministic guidance
//     (or silence) and always exits 0.
//
// Contract: read the PostToolUse JSON payload on stdin, optionally print a JSON
// object with hookSpecificOutput.additionalContext to stdout, always exit 0.

import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { tmpdir, homedir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

// Referenced by the model (which resolves ~) in deterministic guidance only.
const RULES_DIR = './.claude/araia/shared';
const EN_RULES = `${RULES_DIR}/en-generation-rules.md`;
const PTBR_RULES = `${RULES_DIR}/ptbr-generation-rules.md`;
const LANG_DETECT = `${RULES_DIR}/language-detection.md`;

// Real path to the deterministic checker, resolved for spawn (not for the model).
// An installed bundle commits the checker next to this hook, so a clone that
// never installed the framework still runs it. The home copy stays the
// fallback for a hook invoked from the framework itself.
const VENDORED_CHECKER = fileURLToPath(new URL('../araia/scripts/check-writing-rules.py', import.meta.url));
const CHECKER = existsSync(VENDORED_CHECKER)
  ? VENDORED_CHECKER
  : join(homedir(), '.araia', 'framework', 'scripts', 'check-writing-rules.py');

const DEBOUNCE_MS = 20_000; // skip re-scanning the same file within this window
const PRUNE_MS = 3_600_000; // drop debounce entries older than 1h
const MAX_FINDINGS = 25; // cap the reported list so context stays bounded
const SPACED_DOUBLE_HYPHEN = ` ${'-'.repeat(2)} `;
const debug = (...values) => {
  if (process.env.ARAIA_HOOK_DEBUG === '1') process.stderr.write(`[araia-language-hook] ${values.join(' ')}\n`);
};

function projectLedgerRequiredByCommand() {
  const prefix = '--require-ledger=';
  const arg = process.argv.slice(2).find((value) => value.startsWith(prefix));
  if (!arg) return null;
  const name = arg.slice(prefix.length);
  return /^[A-Za-z0-9._-]+$/.test(name) ? name : '';
}

function isEnabledForProject(payload) {
  const ledger = projectLedgerRequiredByCommand();
  if (ledger === null) return true;
  if (!ledger) return false;
  const cwd = typeof payload.cwd === 'string' && payload.cwd ? payload.cwd : process.cwd();
  return existsSync(join(cwd, '.araia', ledger));
}

// File classification ---------------------------------------------------------
const PROSE_EXT = new Set(['md', 'mdx', 'markdown', 'txt', 'rst', 'adoc']);
const SOURCE_EXT = new Set([
  'cs', 'ts', 'tsx', 'js', 'jsx', 'mjs', 'cjs', 'py', 'go', 'java', 'kt', 'kts',
  'swift', 'dart', 'rb', 'php', 'rs', 'scala', 'vue', 'svelte', 'sql', 'cpp',
  'cc', 'c', 'h', 'hpp', 'm', 'mm',
]);
// Whole-file extensions that are config/data/binary; never carry prose we own.
const IGNORE_EXT = new Set([
  'json', 'yaml', 'yml', 'toml', 'ini', 'env', 'cfg', 'conf', 'lock', 'xml',
  'csproj', 'props', 'targets', 'sln', 'csv', 'tsv', 'map',
  'png', 'jpg', 'jpeg', 'gif', 'svg', 'ico', 'webp', 'pdf', 'zip', 'gz', 'tar',
  'exe', 'dll', 'bin', 'woff', 'woff2', 'ttf', 'eot', 'mp4', 'mov', 'lockb',
]);
const IGNORE_SEGMENTS = new Set([
  'node_modules', 'dist', 'build', 'bin', 'obj', 'out', 'coverage',
  '.git', '.next', '.nuxt', '.svelte-kit', 'vendor', '__pycache__', '.venv',
  'venv', '.cache',
]);

function ext(path) {
  const base = path.split(/[\\/]/).pop() || '';
  const dot = base.lastIndexOf('.');
  return dot > 0 ? base.slice(dot + 1).toLowerCase() : '';
}

function isMinified(path) {
  const base = (path.split(/[\\/]/).pop() || '').toLowerCase();
  return base.endsWith('.min.js') || base.endsWith('.min.css') ||
    base.endsWith('-lock.json') || base.endsWith('.lock.json');
}

function classify(path) {
  const segments = path.split(/[\\/]/).map((s) => s.toLowerCase());
  if (segments.some((s) => IGNORE_SEGMENTS.has(s))) return 'ignore';
  if (isMinified(path)) return 'ignore';
  const e = ext(path);
  if (IGNORE_EXT.has(e)) return 'ignore';
  if (PROSE_EXT.has(e)) return 'prose';
  if (SOURCE_EXT.has(e)) return 'source';
  return 'ignore';
}

// Debounce (best-effort; failures never block) --------------------------------
function debounced(sessionId, path) {
  const stateFile = join(tmpdir(), `araia-postwrite-lang-${sessionId || 'default'}.json`);
  const now = Date.now();
  let state = {};
  try {
    state = JSON.parse(readFileSync(stateFile, 'utf8'));
  } catch {
    state = {};
  }
  const last = state[path];
  if (typeof last === 'number' && now - last < DEBOUNCE_MS) return true;
  // record this scan and prune stale entries
  state[path] = now;
  for (const [k, v] of Object.entries(state)) {
    if (typeof v !== 'number' || now - v > PRUNE_MS) delete state[k];
  }
  try {
    writeFileSync(stateFile, JSON.stringify(state), 'utf8');
  } catch {
    /* ignore */
  }
  return false;
}

// Deterministic scan + conservative auto-fix ---------------------------------
// Run check-writing-rules.py over the file and return its finding lines, or
// null when the checker could not run (missing python / missing script) so the
// caller can emit deterministic guidance. For source files the run passes
// --fix, so high-confidence diacritics are corrected in place automatically and
// only what could not be auto-fixed (dash glyphs, ambiguous cases) comes back as
// findings the model must resolve.
function runChecker(file, kind) {
  if (!existsSync(CHECKER)) return null;
  const mode = kind === 'source' ? 'source' : 'markdown';
  // Auto-fix is source-only: it edits comments and string literals, never code
  // or markdown prose structure. Markdown stays detect-only here.
  const args = mode === 'source'
    ? [CHECKER, '--mode', 'source', '--fix', '--strict', file]
    : [CHECKER, '--mode', 'markdown', '--strict', file];
  const env = { ...process.env, PYTHONIOENCODING: 'utf-8', PYTHONUTF8: '1' };
  for (const py of ['python3', 'python']) {
    let res;
    try {
      res = spawnSync(py, args, { encoding: 'utf8', env, timeout: 10_000 });
    } catch {
      continue;
    }
    if (res.error || res.status === null) continue; // interpreter not found
    // Windows Store app-execution aliases can exist but return 9009 without
    // running Python. Only the checker's documented exit codes are terminal;
    // otherwise try the next interpreter candidate.
    if (![0, 1, 2].includes(res.status)) continue;
    // With --fix the script rewrites diacritics, then re-reports what remains.
    // exit 0 => clean (possibly after auto-fix); 2 => errors remain; 1 => warns.
    const fixed = /^FIXED: /m.test(String(res.stderr || ''));
    if (res.status === 0) {
      // Surface a short note when the auto-fix changed the file so the model
      // knows its working tree differs from what it wrote (no action needed).
      return fixed ? { findings: [], autofixed: true } : { findings: [], autofixed: false };
    }
    const lines = String(res.stdout || '')
      .split('\n')
      .map((l) => l.trim())
      .filter((l) => l.includes('[error]') || l.includes('[warning]'));
    return { findings: lines, autofixed: fixed };
  }
  return null; // no interpreter
}

// Guidance text when the checker cannot run ----------------------------------
function proseNudge(file) {
  return `Post-write language check: you just wrote ${file}. Before doing anything else, ` +
    `apply the framework generation rules to the prose you just added or changed: read ` +
    `${EN_RULES} (English) or ${PTBR_RULES} (PT-BR), choosing the language per ${LANG_DETECT}. ` +
    `Localize the complete narrative surface (title, headings, metadata and table labels, captions, lists, and body), ` +
    `then fix genuine rule violations (PT-BR diacritics, punctuation, voice, hedging, technical-term casing). ` +
    `In particular, replace any literal em dash, en dash, or spaced "${SPACED_DOUBLE_HYPHEN}" with a comma, parentheses, a colon, or a period (PT-BR keeps an em dash only to open dialogue). ` +
    `If it is already compliant, take no action and continue.`;
}

function sourceNudge(file) {
  return `Post-write language check: you just modified ${file}. Before doing anything else, review ONLY ` +
    `the natural-language text you just added or changed in it (XML doc comments / JSDoc / docstrings, log ` +
    `messages, exception and error messages, user-facing strings, and code comments) against the framework ` +
    `generation rules: read ${EN_RULES} (English) or ${PTBR_RULES} (PT-BR) per ${LANG_DETECT}. Replace any literal ` +
    `em dash, en dash, or spaced "${SPACED_DOUBLE_HYPHEN}" in that text with a comma, parentheses, a colon, or a period. Do NOT touch ` +
    `identifiers, code logic, imports, or fenced examples. If the text is already compliant, take no action and continue.`;
}

// Concrete finding report (when the checker ran and found violations) ---------
function findingsReport(file, kind, findings, autofixed) {
  const shown = findings.slice(0, MAX_FINDINGS);
  const extra = findings.length > shown.length
    ? `\n(+${findings.length - shown.length} more; fix these and re-check.)` : '';
  const surface = kind === 'source'
    ? 'comments, doc comments, and string literals (NOT identifiers or code logic)'
    : 'the prose';
  const fixedNote = autofixed
    ? `High-confidence diacritics were already auto-corrected in place; the findings below ` +
      `are what the auto-fix could NOT resolve safely (e.g. dash glyphs, ambiguous words). `
    : '';
  return `Post-write language check for ${file}: ${findings.length} writing-rule ` +
    `violation(s) remain in ${surface}. ${fixedNote}Fix each one now, before continuing, applying the ` +
    `PT-BR/EN generation rules (one narrative language across titles, headings, labels, and body; mandatory ` +
    `diacritics; no literal em/en dash). Findings:\n` +
    shown.join('\n') + extra +
    `\nAfter fixing, the same scan must pass: ` +
    `\`python ./.claude/araia/scripts/check-writing-rules.py --mode ${kind === 'source' ? 'source' : 'markdown'} --strict ${file}\`.`;
}

// Harness payload normalization (Claude Code + Codex + Kimi Code) ------------
// Resolve every edited path across harness payload shapes. Codex apply_patch
// sends the entire patch in tool_input.command, so parse its canonical file
// headers in addition to the direct Claude/Kimi path fields.
function resolveEditedFiles(payload) {
  const found = [];
  const add = (value) => {
    if (typeof value === 'string' && value && !found.includes(value) && found.length < 32) found.push(value);
  };
  const containers = [payload, payload.tool_input, payload.input, payload.arguments, payload.params, payload.tool_response];
  const keys = ['file_path', 'path', 'notebook_path', 'filePath', 'file', 'target'];
  for (const c of containers) {
    if (!c || typeof c !== 'object') continue;
    for (const k of keys) add(c[k]);
    if (typeof c.command === 'string') {
      for (const match of c.command.matchAll(/^\*\*\* (?:Update|Add|Delete) File:[ \t]*([^\r\n]+?)[ \t]*\r?$/gm)) add(match[1]);
    }
    for (const arrKey of ['changes', 'files', 'edits']) {
      const arr = c[arrKey];
      if (!Array.isArray(arr)) continue;
      for (const item of arr) {
        if (typeof item === 'string') add(item);
        if (item && typeof item === 'object') {
          for (const k of keys) add(item[k]);
        }
      }
    }
  }
  return found;
}

// Main ------------------------------------------------------------------------
function emit(additionalContext) {
  process.stdout.write(JSON.stringify({
    hookSpecificOutput: { hookEventName: 'PostToolUse', additionalContext },
  }));
}

function main() {
  let raw = '';
  try {
    raw = readFileSync(0, 'utf8');
  } catch {
    return; // no stdin → nothing to do
  }
  let payload;
  try {
    payload = JSON.parse(raw);
  } catch {
    return; // malformed payload → never block
  }
  if (!isEnabledForProject(payload)) return;

  const files = resolveEditedFiles(payload);
  debug('resolved', JSON.stringify(files));
  if (!files.length) return;
  const sessionId = payload.session_id || '';
  const contexts = [];
  for (const file of files) {
    const kind = classify(file);
    debug('checking', JSON.stringify(file), kind);
    if (kind === 'ignore' || debounced(sessionId, file)) continue;
    const result = runChecker(file, kind);
    debug('result', JSON.stringify(result));
    if (result === null) {
      contexts.push(kind === 'prose' ? proseNudge(file) : sourceNudge(file));
      continue;
    }
    const { findings, autofixed } = result;
    if (findings.length === 0) {
      if (autofixed) {
        contexts.push(`Post-write language check: auto-corrected PT-BR diacritics in ${file} ` +
          `(comments and string literals only; code untouched). The file on disk is now ` +
          `compliant and differs from what you wrote. No action needed; re-read before further edits.`);
      }
      continue;
    }
    contexts.push(findingsReport(file, kind, findings, autofixed));
  }
  if (contexts.length) emit(contexts.join('\n\n'));
}

try {
  main();
} catch {
  /* a hook must never throw into the user's workflow */
}
process.exit(0);
