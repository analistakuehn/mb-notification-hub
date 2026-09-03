# Diretrizes do Projeto para Gemini Antigravity

Este arquivo governa sessões no Google Antigravity / Gemini para o projeto `MonteBravo.NotificationHub`.

## Padrões de Escrita e Convenções
- **Idioma de interação**: Comunicar-se no idioma do usuário (PT-BR). Obrigatório uso de acentuação e diacríticos; não utilizar travessão (em dash `—` ou `–` ou `--`) como pontuação (utilizar vírgulas, parênteses, dois-pontos ou pontos finais).
- **Informações verificáveis**: Omitir informações desconhecidas ou placeholders (TBD, UNKNOWN) em documentos definitivos.
- **Isolamento de especificações**: Não incluir referências a IDs de especificação (ACs, Delivery Slices, ADR IDs) no código de produção ou nomes de testes.

## Dialeto de Despacho no Gemini Antigravity
- **Skills**: As skills do projeto residem em `.agents/skills/`. Ative-as lendo o respectivo `SKILL.md` sob demanda conforme as necessidades da tarefa.
- **Subagentes**: Os perfis arquiteturais e de engenharia residem em `.agents/agents/` (`dotnet-architect`, `dotnet-engineer`, `dotnet-specialist`). Podem ser instanciados como subagentes através da ferramenta `invoke_subagent`.
- **Padrões de Código .NET**: Diretrizes de estilo C# e arquitetura limpa/modular estão documentadas em `.agents/araia/adapters/dotnet/code-style.md`.
