# Engenharia de software

[Voltar ao índice](00-index.md)

## ENG-001: estados de implementação incompatíveis

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `Software Engineering`
- `file`: `docs/fases/fase-1b-fundacao.md`
- `line`: `71`
- `evidence`: A linha 71 afirma que `Platform.Worker` ainda não existe, embora o projeto esteja na solução fixada. A linha 93 mantém B3 em implementação, enquanto a linha 155 a registra como concluída. A linha 167 mantém B15 em implementação e a nota da linha 174 diz que ela não possui commit, apesar de as linhas 193 a 199 e 229 a 233 descreverem sonda, linha de base, resultado e critério cumprido, materializados em `tests/Platform.PerformanceTests`.
- `evidence-kind`: `executed`
- `introduced-by-diff`: `false`
- `impact`: O documento aceito apresenta fotografias incompatíveis sobre hosts, contratos e entregas. Isso pode provocar trabalho duplicado, avaliação errada de dependências e repasse baseado em estado obsoleto.
- `recommendation`: Separar decisões duráveis de fotografias de implementação. Atualizar os estados para a revisão fixada e datar explicitamente qualquer fotografia histórica que deva permanecer.
- `verification`: Conferir o documento contra a solução, os projetos, os testes e o histórico fixado. Cada componente ou fatia deve ter um único estado atual, sem contradição entre a decomposição, as notas e os critérios de saída.
- `source-revision`: `3570c5e7ca761911330cace4b7dfdf1a8c5b2dbb`
- `reviewers`: `dotnet-architect`, `dotnet-engineer`, `dotnet-specialist`

## ENG-002: fechamento da C4 não alcança a documentação do catálogo

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `Software Engineering`
- `file`: `docs/fases/fase-1b-fundacao.md`
- `line`: `172`
- `evidence`: A linha 172 marca C4 como concluída e descreve o estreitamento da promessa de vocabulário único. O design fixado declara que o catálogo canônico vale somente para `rejected.reason` e que `failed.reason` é vocabulário aberto do provedor. Entretanto, a documentação XML pública de [`NotificationRejectionReasons.cs`](../../../src/Platform.Api/Modules/Notifications/Integration/V1/NotificationRejectionReasons.cs) ainda afirma que toda razão recebida em evento de rejeição ou falha pertence ao conjunto fechado.
- `evidence-kind`: `executed`
- `introduced-by-diff`: `false`
- `impact`: Consumidores e ferramentas podem validar `failed.reason` contra uma enumeração fechada, rejeitando códigos novos de provedor e produzindo painéis ou alarmes incorretos.
- `recommendation`: Corrigir a documentação pública para limitar o catálogo a rejeições e documentar separadamente a cardinalidade aberta das falhas. Adicionar um teste de contrato que impeça a reintrodução dessa promessa.
- `verification`: A documentação do tipo e os testes devem afirmar que `rejected.reason` usa `NotificationRejectionReasons.All` e que `failed.reason` aceita valores fora desse conjunto.
- `source-revision`: `3570c5e7ca761911330cace4b7dfdf1a8c5b2dbb`
- `reviewers`: `dotnet-engineer`
- `dissent`: O revisor atribuiu o achado ao diff porque a linha 172 passou a citar um commit concreto. A consolidação registra `introduced-by-diff: false`, pois o pai já dizia `Concluída (commit pendente)` e a alteração não criou a divergência do contrato.
