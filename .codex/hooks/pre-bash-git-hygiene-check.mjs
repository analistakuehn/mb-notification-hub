#!/usr/bin/env node
// PreToolUse hook: before a Bash call runs, ask for confirmation when the
// command contains a git-hygiene "content rule" flag (one that can appear
// anywhere in the argument list, so no permissions.ask prefix pattern can
// catch it reliably). See shared/command-policy.md for the full design and
// its documented limitations (text scan, not a shell parse; conservative).
//
// Layered design (parallel to post-write-language-check.mjs):
//   - The rule source is single-sourced in shared/command-policy.json
//     (content-rules only; prefix-rules are enforced separately via
//     .claude/settings.json permissions.ask, generated at project-install
//     time). Edit the JSON, not this file, to change which flags trigger a
//     confirmation.
//   - Claude Code receives "ask", preserving the explicit override path.
//     Codex currently does not support permissionDecision:"ask" in PreToolUse,
//     so it receives "deny" before execution and the user can run the command
//     manually outside the agent after reviewing the reason.
//   - It fails closed for git: an unreadable or malformed policy requests
//     confirmation instead of silently dropping protection.
//
// Contract: read the PreToolUse JSON payload on stdin (hook_event_name,
// tool_name, tool_input.command), optionally print
// { hookSpecificOutput: { hookEventName: "PreToolUse", permissionDecision:
// "ask", permissionDecisionReason } } to stdout, always exit 0.

import { readFileSync, existsSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

const POLICY_FILE = join(homedir(), '.araia', 'framework', 'shared', 'command-policy.json');

// Loads and compiles the content-rules once per invocation. Returns null on a
// policy integrity failure so the caller can apply the fail-closed fallback.
export function loadContentRules(policyFile = POLICY_FILE) {
  if (!existsSync(policyFile)) return null;
  let parsed;
  try {
    parsed = JSON.parse(readFileSync(policyFile, 'utf8'));
  } catch {
    return null;
  }
  const rules = Array.isArray(parsed?.['content-rules']) ? parsed['content-rules'] : null;
  if (!rules) return null;
  const compiled = [];
  for (const rule of rules) {
    if (!rule || typeof rule.match !== 'string' || typeof rule.id !== 'string'
        || rule.decision !== 'ask') return null;
    try {
      compiled.push({
        id: rule.id,
        rule: rule.rule || '',
        justification: rule.justification || '',
        regex: new RegExp(rule.match, rule['match-flags'] || ''),
      });
    } catch {
      return null;
    }
  }
  return compiled;
}

// Pure decision function, exported for unit testing without spawning a
// process. `command` is the raw Bash command string from tool_input.command.
// Returns null (defer) or { reason } (ask).
export function decide(command, compiledRules) {
  if (typeof command !== 'string' || !command) return null;
  if (!Array.isArray(compiledRules) || compiledRules.length === 0) return null;
  // Gate on the literal word "git" first: every content-rule targets a git
  // invocation, and skipping non-git commands avoids scanning strings that
  // can never match. This is a text scan, not a shell parse (see
  // shared/command-policy.md "Known limitations"): it does not track quoting
  // or split compound (&&/;/|) commands, so a match anywhere in a chained
  // command triggers the decision for the whole chain, and a quoted string
  // that happens to contain a flag's literal text produces an unnecessary
  // (safe-side) ask rather than a missed (unsafe-side) one.
  if (!/\bgit\b/.test(command)) return null;

  const hits = [];
  for (const rule of compiledRules) {
    if (rule.regex.test(command)) hits.push(rule);
  }
  if (hits.length === 0) return null;

  const reason = hits
    .map((h) => `${h.id}${h.rule ? ` (${h.rule})` : ''}: ${h.justification}`)
    .join(' ');
  return { reason: `git-hygiene command policy: ${reason}` };
}

export function formatDecision(reason, payload) {
  const codex = typeof payload?.turn_id === 'string' || typeof payload?.tool_use_id === 'string';
  return {
    hookSpecificOutput: {
      hookEventName: 'PreToolUse',
      permissionDecision: codex ? 'deny' : 'ask',
      permissionDecisionReason: reason,
    },
  };
}

function emit(reason, payload) {
  process.stdout.write(JSON.stringify(formatDecision(reason, payload)));
}

function main() {
  let raw = '';
  try {
    raw = readFileSync(0, 'utf8');
  } catch {
    return; // no stdin -> defer
  }
  let payload;
  try {
    payload = JSON.parse(raw);
  } catch {
    return; // malformed payload -> defer, never block
  }

  if (payload.hook_event_name !== 'PreToolUse') return;
  if (payload.tool_name !== 'Bash') return;

  const command = payload.tool_input?.command;
  const compiledRules = loadContentRules();
  if (!compiledRules) {
    if (typeof command === 'string' && /\bgit\b/.test(command)) {
      emit('git-hygiene command policy is unavailable or invalid; explicit confirmation is required before any git command can run. Repair shared/command-policy.json and run araia doctor.', payload);
    }
    return;
  }

  const decision = decide(command, compiledRules);
  if (!decision) return;

  emit(decision.reason, payload);
}

// Only auto-run when invoked directly (node pre-bash-git-hygiene-check.mjs),
// not when imported for unit testing.
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    main();
  } catch {
    /* a hook must never throw into the user's workflow */
  }
  process.exit(0);
}
