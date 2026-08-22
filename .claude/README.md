# Bundle Claude Code do Araia

Este diretório contém a integração local do Araia para o Claude Code. Ele não é a fonte canônica do framework: os arquivos são gerados a partir de `~/.araia/framework` e reconciliados por `/araia sync --harness claude-code`.

## Estrutura

- `settings.json`: permissões e registro dos hooks `PostToolUse` e `PreToolUse`.
- `hooks/*.mjs`: scripts Node chamados depois de escrita/edição ou antes de comandos Bash sensíveis.
- `agents/*.md`: agentes Claude Code em Markdown com frontmatter.
- `skills/*/SKILL.md`: skills locais disponíveis no Claude Code.

## Como os hooks rodam

O Claude Code lê `settings.json` e chama os hooks `PostToolUse` depois de `Write`, `Edit` ou `MultiEdit`, e o hook `PreToolUse` antes de cada chamada `Bash`.

O registro atual chama:

- `node ${CLAUDE_PROJECT_DIR}/.claude/hooks/post-write-language-check.mjs` (`PostToolUse`)
- `node ${CLAUDE_PROJECT_DIR}/.claude/hooks/post-write-tier-check.mjs` (`PostToolUse`)
- `node ${CLAUDE_PROJECT_DIR}/.claude/hooks/pre-bash-git-hygiene-check.mjs` (`PreToolUse`, matcher `Bash`)

Os dois primeiros recebem um payload JSON pelo stdin e são não bloqueantes: quando encontram algo, devolvem `hookSpecificOutput.additionalContext`; quando não há nada a fazer, ficam silenciosos. O terceiro também lê um payload JSON pelo stdin, mas pode pedir confirmação (`permissionDecision: "ask"`) para um comando `git` que contenha uma flag de bypass como `--no-verify`, sempre que essa flag puder aparecer em qualquer posição do comando; ver `~/.araia/framework/shared/command-policy.md`.

## Permissions.ask

`settings.json` também pode conter entradas `permissions.ask` geradas a partir de `~/.araia/framework/shared/command-policy.json` (as `prefix-rules`, cuja flag aparece sempre logo após o verbo git, como `git push --force` ou `git add -A`). Essas entradas pedem confirmação antes do comando rodar; elas nunca bloqueiam (`deny`) porque o `git-hygiene-protocol.md` reserva uma exceção explícita e pedida na sessão para cada uma.

## Smoke test no PowerShell

Use estes comandos a partir da raiz do projeto para validar que os scripts executam:

```powershell
'{"session_id":"claude-readme-language","tool_input":{"file_path":"AGENTS.md"}}' | node .claude\hooks\post-write-language-check.mjs
```

```powershell
'{"session_id":"claude-readme-tier","tool_input":{"file_path":".claude/agents/sample-reviewer.md"}}' | node .claude\hooks\post-write-tier-check.mjs
```

```powershell
'{"hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"git commit -m \"wip\" --no-verify"}}' | node .claude\hooks\pre-bash-git-hygiene-check.mjs
```

Saída esperada: um JSON com `hookSpecificOutput.additionalContext` (nos dois primeiros) ou `hookSpecificOutput.permissionDecision` (no terceiro), ou silêncio quando o arquivo ou comando não se aplica ao hook.

## Problemas comuns

- `node` não encontrado: instale ou exponha Node.js no `PATH`.
- O hook não dispara em edição real: confira se `settings.json` preserva o bloco `hooks.PostToolUse`.
- O hook de idioma só emite um lembrete genérico: `python` não está no `PATH`; nesse caso ele não consegue chamar `~/.araia/framework/scripts/check-writing-rules.py` e degrada sem bloquear o fluxo.
- O hook de tier valida agentes Markdown: `model: inherit`, `tools:` explícito e restrições de Tier 3 conforme `~/.araia/framework/docs/authoring-standards.md`.
