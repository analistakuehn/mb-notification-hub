# Qualidade do .NET

[Voltar ao índice](00-index.md)

## STK-001: Stack Profile descrito com eixo de mensageria obsoleto

- `severity`: `LOW`
- `confidence`: `HIGH`
- `lens`: `.NET Quality`
- `file`: `docs/fases/fase-1b-fundacao.md`
- `line`: `219`
- `evidence`: A linha 219 afirma que o Stack Profile contém `messaging: [sqs]` e que Kafka ainda será materializado. Na revisão fixada, [`.araia/stack-profile.yaml`](../../../.araia/stack-profile.yaml) contém `messaging: [sqs, kafka]`. A linha 162 marca B10 como concluída e a linha 251 registra que o perfil já foi atualizado.
- `evidence-kind`: `executed`
- `introduced-by-diff`: `false`
- `impact`: Ferramentas e pessoas que usem o documento em vez do perfil podem selecionar validações, dependências ou especialistas incompatíveis com o eixo Kafka já adotado.
- `recommendation`: Substituir o snapshot obsoleto pelo valor fixado ou remover a duplicação e apontar o Stack Profile como fonte da verdade.
- `verification`: `git show HEAD:.araia/stack-profile.yaml` e o documento devem apresentar o mesmo conjunto de mensageria.
- `source-revision`: `3570c5e7ca761911330cace4b7dfdf1a8c5b2dbb`
- `reviewers`: `dotnet-engineer`, `dotnet-specialist`
- `dissent`: O `dotnet-architect` agrupou esta evidência em um achado médio de deriva de engenharia. A consolidação preserva a severidade baixa atribuída pelos dois revisores que a classificaram na lente da stack.
