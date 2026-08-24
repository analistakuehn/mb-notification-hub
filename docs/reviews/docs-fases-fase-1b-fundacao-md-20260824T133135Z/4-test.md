# Teste

[Voltar ao índice](00-index.md)

## TST-001: portão operacional sem oráculo executável

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `Test`
- `file`: `docs/fases/fase-1b-fundacao.md`
- `line`: `237`
- `evidence`: A linha 237 torna bloqueantes a ausência de template `operational` publicado e a ausência de concessão de `Notifications.Send.Operational`. A linha 302 registra o modo de falha: uma notificação pode permanecer `deferred` indefinidamente, sem envio, falha ou alarme. O documento não define comando, teste, fonte de dados, responsável pela execução, evidência persistida nem comportamento de reprovação para esse gate.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: Um gate manual e sem oráculo falseável pode liberar a classe enquanto não existe liberador de adiamento, deixando notificações silenciosamente estacionadas.
- `recommendation`: Definir uma verificação executável de pré-go-live que consulte versões publicadas e atribuições de app role, com dono, comando, receipt persistido e reprovação obrigatória.
- `verification`: A verificação deve falhar quando existir template `operational` publicado ou qualquer concessão de `Notifications.Send.Operational`, e passar somente quando ambas as condições estiverem ausentes.
- `source-revision`: `3570c5e7ca761911330cace4b7dfdf1a8c5b2dbb`
- `reviewers`: `dotnet-architect`
