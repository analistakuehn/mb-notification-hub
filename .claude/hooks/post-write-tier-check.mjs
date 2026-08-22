#!/usr/bin/env node
// PostToolUse hook: after a Write/Edit/MultiEdit to an agent or worker definition,
// nudge the model to verify it against the Agent and Worker Tier Taxonomy in
// framework/CLAUDE.md.
//
// Design (parallel to post-write-language-check.mjs):
//   - Covers main-agent and subagent writes. Runtime hooks are the common
//     enforcement point; no definition relies on an agent remembering to lint.
//   - NUDGES, never blocks. The deterministic verdict is single-sourced in
//     scripts/lint-agent-tiers.py (also wired into CI as a blocking gate), so the
//     hook points the model at that linter rather than re-implementing the rules.
//
// Contract: read the PostToolUse JSON payload on stdin, optionally print a JSON
// object with hookSpecificOutput.additionalContext to stdout, always exit 0.
// A Codex TOML agent must omit the `model` field so the session model is inherited.
// Therefore, never write `model = "inherit"`; `inherit` is not a model ID.

import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

// Resolved by the model (which expands ~), never read by this script.
const LINTER = '~/.araia/framework/scripts/lint-agent-tiers.py';
const TAXONOMY = '~/.araia/framework/CLAUDE.md';

const DEBOUNCE_MS = 20_000; // skip re-nudging the same file within this window
const PRUNE_MS = 3_600_000; // drop debounce entries older than 1h

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

// Roots that hold generated state rather than authored source. `.araia/` is the
// control plane: run state, staging, worktrees, and ledgers. The portfolio
// scheduler writes per-SPEC briefs to `.araia/runs/**/workers/{SPEC-ID}/`, whose
// `workers` segment would otherwise read as a profile directory and demand
// frontmatter from a mission brief.
const NON_SOURCE_ROOTS = ['.araia', 'node_modules', '.git'];
const PROFILE_DIRS = ['agents', 'workers'];

// A canonical profile is Markdown directly inside `agents/` or `workers/`;
// generated harness profiles may be Markdown or TOML under a runtime `agents/`
// directory. Matching the immediate parent, rather than any ancestor segment,
// keeps unrelated trees that merely pass through such a segment out of scope.
function isProfileFile(path) {
  const segments = path.split(/[\\/]/);
  const base = (segments.pop() || '').toLowerCase();
  if (!base.endsWith('.md') && !base.endsWith('.toml')) return false;
  const lowered = segments.map((s) => s.toLowerCase());
  if (lowered.some((s) => NON_SOURCE_ROOTS.includes(s))) return false;
  return PROFILE_DIRS.includes(lowered[lowered.length - 1]);
}

function isCodexTomlAgent(file) {
  const normalized = file.replace(/\\/g, '/').toLowerCase();
  return (normalized.includes('/.codex/agents/') || normalized.startsWith('.codex/agents/')) &&
    normalized.endsWith('.toml');
}

function isKimiMarkdownAgent(file) {
  const normalized = file.replace(/\\/g, '/').toLowerCase();
  return (normalized.includes('/.kimi-code/agents/') || normalized.startsWith('.kimi-code/agents/')) &&
    normalized.endsWith('.md');
}

// Debounce (best-effort; failures never block) --------------------------------
function debounced(sessionId, path) {
  const stateFile = join(tmpdir(), `claude-postwrite-tier-${sessionId || 'default'}.json`);
  const now = Date.now();
  let state = {};
  try {
    state = JSON.parse(readFileSync(stateFile, 'utf8'));
  } catch {
    state = {};
  }
  const last = state[path];
  if (typeof last === 'number' && now - last < DEBOUNCE_MS) return true;
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

function nudge(file) {
  if (isCodexTomlAgent(file)) {
    const name = (file.split(/[\\/]/).pop() || '').replace(/\.toml$/i, '');
    const canonical = `.claude/agents/${name}.md`;
    return `Post-write tier check: you just edited ${file} (a Codex agent definition). Before doing anything else, ` +
      `verify it mirrors the Agent and Worker Tier Taxonomy in ${TAXONOMY}: check the canonical source when present with ` +
      `\`python ${LINTER} ${canonical}\`, then confirm the Codex TOML keeps the equivalent tier posture ` +
      `(\`sandbox_mode = "read-only"\` for mechanical reviewers / validators; \`workspace-write\` only for agents that ` +
      `are allowed to edit). The TOML must omit the \`model\` field to inherit the session model; never write ` +
      `\`model = "inherit"\`, because Codex treats \`inherit\` as a literal model identifier. Fix drift if found; ` +
      `if it is already clean, take no action and continue.`;
  }

  if (isKimiMarkdownAgent(file)) {
    const name = (file.split(/[\\/]/).pop() || '').replace(/\.md$/i, '');
    const canonical = `.claude/agents/${name}.md`;
    return `Post-write tier check: you just edited ${file} (a Kimi Code agent definition). Before doing anything else, ` +
      `verify it mirrors the Agent and Worker Tier Taxonomy in ${TAXONOMY}: check the canonical source when present with ` +
      `\`python ${LINTER} ${canonical}\`, then confirm the Kimi Markdown preserves the equivalent \`tools:\` allowlist, ` +
      `uses \`model_preference: primary\`, and contains a self-contained handoff instruction. Fix drift if found; ` +
      `if it is already clean, take no action and continue.`;
  }

  return `Post-write tier check: you just edited ${file} (an agent or worker definition). Before doing anything else, ` +
    `verify it conforms to the Agent and Worker Tier Taxonomy in ${TAXONOMY}: it must declare \`model: inherit\`, an explicit ` +
    `\`tools:\` list from the known vocabulary, and, if it is a Tier-3 mechanical profile (reviewer / formatter / ` +
    `validator), keep tools within Read, Edit, Glob, Grep (no Write, no Bash). Run \`python ${LINTER} ${file}\` and ` +
    `fix any reported violation; if it is already clean, take no action and continue.`;
}

// Harness payload normalization (Claude Code + Codex + Kimi Code) ------------
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

  const files = resolveEditedFiles(payload).filter(isProfileFile);
  if (!files.length) return;
  const sessionId = payload.session_id || '';
  const contexts = files
    .filter((file) => !debounced(sessionId, file))
    .map(nudge);
  if (contexts.length) emit(contexts.join('\n\n'));
}

try {
  main();
} catch {
  /* a hook must never throw into the user's workflow */
}
process.exit(0);
