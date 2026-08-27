# Bundle Codex do Araia

Este diretório contem a integração local do Araia para o Codex. Ele não e a fonte canonica do framework: os arquivos são gerados a partir de `~/.araia/framework` e reconciliados por `$araia sync --harness codex`.

## Estrutura

- `config.toml`: configuração local do Codex para skills e subagents do projeto.
- `hooks.json`: registro dos hooks `PostToolUse` de escrita e `PreToolUse` de comando.
- `hooks/*.mjs`: scripts Node chamados depois de escrita ou antes de comandos Bash sensíveis.
- `rules/araia.rules`: confirmação nativa para prefixos Git sensíveis.
- `agents/*.toml`: subagents Codex. O tier do agente vira `sandbox_mode`.
- `skills/{nome}/`: bundle completo de cada skill local, com `SKILL.md` e os recursos que ela cita por caminho relativo (`references/`, `flows/`, `scripts/`, `templates/`).
- `araia/`: cópia dos arquivos do framework que o bundle cita, para que o projeto funcione sem `~/.araia/framework` instalado. As referências dentro do bundle apontam para cá.
- `.araia-managed-files.json`: inventário dos arquivos de bundle gerenciados, com o hash de cada um. Ele autoriza a poda e a desinstalação; um arquivo editado no projeto é preservado.

## Como ativar

O Codex só carrega hooks e skills de projeto confiavel. Depois de gerar ou sincronizar este bundle:

1. Abra o Codex na raiz do projeto.
2. Rode `/hooks`.
3. Confie nos três hooks do Araia.
4. Confirme em `~/.codex/config.toml` que este projeto esta como confiavel. A chave e o caminho absoluto do projeto nesta máquina, que `git rev-parse --show-toplevel` imprime:

```toml
[projects."<caminho absoluto do projeto nesta máquina>"]
trust_level = "trusted"
```

Se o projeto não estiver confiavel, o Codex pode ignorar `.codex/hooks.json` e `.codex/skills` sem erro visivel.

## Como os hooks rodam

Não execute o diretório `.codex/hooks` diretamente. Cada hook é um script Node que espera receber um payload JSON no stdin, no formato do evento correspondente.

O registro em `hooks.json` executa as cópias deste bundle, em `.codex/hooks/`:

- `.codex/hooks/post-write-language-check.mjs`
- `.codex/hooks/post-write-tier-check.mjs`
- `.codex/hooks/pre-bash-git-hygiene-check.mjs`

O Codex não substitui variáveis no comando do hook e o executa com o `cwd` da sessão, que nem sempre é a raiz do repositório. Por isso cada comando é um lançador Node de uma linha que resolve a raiz pelo `git rev-parse --show-toplevel`, cai para o diretório atual fora de um checkout git e então chama o hook. Assim `hooks.json` não guarda nenhum caminho da máquina que o gerou e continua funcionando a partir de um subdiretório. A impressão digital faz a definição confiada mudar quando o conteúdo do script muda; após uma atualização, revise e confie novamente nos hooks em `/hooks`.

Os matchers cobrem `apply_patch|Edit|Write` em `PostToolUse` e `Bash` em `PreToolUse`. Para `apply_patch`, os hooks extraem todos os arquivos dos cabeçalhos presentes em `tool_input.command`.

## Smoke test no PowerShell

Use estes comandos a partir da raiz do projeto para validar que os scripts executam:

```powershell
'{"session_id":"codex-readme-language","tool_input":{"command":"*** Begin Patch\n*** Update File: AGENTS.md\n*** End Patch"}}' | node .codex\hooks\post-write-language-check.mjs
```

```powershell
'{"session_id":"codex-readme-tier","tool_input":{"file_path":".codex/agents/sample-reviewer.toml"}}' | node .codex\hooks\post-write-tier-check.mjs
```

```powershell
'{"hook_event_name":"PreToolUse","turn_id":"codex-readme-policy","tool_name":"Bash","tool_input":{"command":"git -C . push --force"}}' | node .codex\hooks\pre-bash-git-hygiene-check.mjs
```

Saída esperada: um JSON com `hookSpecificOutput.additionalContext`, ou silencio quando o arquivo não se aplica ao hook.

## Problemas comuns

- `node` não encontrado: instale ou exponha Node.js no `PATH`.
- O hook não dispara durante edições: rode `/hooks`, confie nos três hooks e reinicie a sessão do Codex se necessário.
- O hook de idioma só emite um lembrete genérico: `python` não está no `PATH`; nesse caso ele não consegue chamar `./.codex/araia/scripts/check-writing-rules.py` e degrada sem bloquear o fluxo.
- O hook de tier em `.codex/agents/*.toml` valida a postura Codex: revisores e validadores mecanicos usam `sandbox_mode = "read-only"`; agentes que podem editar usam `workspace-write`.
