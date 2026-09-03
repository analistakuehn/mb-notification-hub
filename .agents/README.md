# Bundle Gemini Antigravity (.agents)

Este diretório contém a integração local do Araia e do ecossistema .NET para o Google Antigravity / Gemini.

## Estrutura

- `agents/*.md`: Definição de agentes e subagentes locais (`dotnet-architect`, `dotnet-engineer`, `dotnet-specialist`), configurados para ativação direta ou via `invoke_subagent`.
- `skills/<nome>/SKILL.md`: 12 skills especializadas em desenvolvimento, inspeção, arquitetura e testes .NET.
- `araia/`: Referências de padrões de código C#, convenções arquiteturais e protocolos compartilhados.
- `hooks/`: Scripts Node.js para validação de escrita (PT-BR/EN) e checagem de higiene Git.
- `hooks.json`: Configuração de ganchos de ciclo de vida do Antigravity.
