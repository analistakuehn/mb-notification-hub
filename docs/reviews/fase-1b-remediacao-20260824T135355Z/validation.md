# Evidência de validação

## Validação estática e suítes locais

| Verificação | Resultado |
| --- | --- |
| `dotnet build MonteBravo.NotificationHub.sln --no-restore -p:TreatWarningsAsErrors=true` | aprovado, 0 avisos e 0 erros |
| `Platform.UnitTests` | 720 de 720 aprovados |
| `Platform.ArchitectureTests` | 8 de 8 aprovados |
| `Platform.SecurityArchitectureTests` | 5 de 5 aprovados |
| `Platform.IntegrationTests` | 432 aprovados, 2 omissões condicionais, 0 falhas, total de 434 |
| `git diff --check` | aprovado |

As duas omissões da suíte de integração são os smoke tests de SendGrid e Twilio.
Eles exigem credenciais reais e produzem efeitos externos, portanto permanecem
condicionados à execução operacional autorizada.

## Regressão da interferência criptográfica

A primeira execução completa após a remediação passou em 410 testes, omitiu os
dois smoke tests e falhou no varredor de retenção. O caso passava isoladamente.
A reprodução mínima demonstrou que
`ApplicationKillSwitchCoreTests` persistia um envelope sintético de um byte na
mesma `CorePipelineFixture`; o varredor global da classe de retenção encontrava
esse dado residual e rejeitava corretamente sua versão de formato.

Após a separação da fixture:

| Verificação | Resultado |
| --- | --- |
| teste de retenção isolado | 1 de 1 aprovado |
| teste contaminador seguido pelo varredor | 2 de 2 aprovados |
| classe completa de retenção | 5 de 5 aprovados |
| build do projeto de integração com avisos como erros | aprovado, 0 avisos e 0 erros |
| suíte completa de integração | 411 aprovados, 2 omissões condicionais e 0 falhas |

Após a última rodada de correções, a suíte integrada completa foi executada
novamente e aprovou 432 testes, com as mesmas 2 omissões condicionais e nenhuma
falha.

## Recibos locais

- Falha reproduzida:
  [`rendered-content-retention-interference-before.trx`](../../../artifacts/test-results/rendered-content-retention-before/rendered-content-retention-interference-before.trx)
- Grupo interferente após a correção:
  [`rendered-content-retention-interference-after.trx`](../../../artifacts/test-results/rendered-content-retention-after/rendered-content-retention-interference-after.trx)
- Suíte completa após a correção:
  [`rendered-content-retention-full-after.trx`](../../../artifacts/test-results/rendered-content-retention-full-after/rendered-content-retention-full-after.trx)
- Unitários finais:
  [`unit-final.trx`](../../../artifacts/test-results/final/unit-final.trx)
- Arquitetura final:
  [`arch-final.trx`](../../../artifacts/test-results/final/arch-final.trx)
- Arquitetura de segurança final:
  [`security-arch-final.trx`](../../../artifacts/test-results/final/security-arch-final.trx)
- Integração final após todos os achados:
  [`integration-final-after-all-findings.trx`](../../../artifacts/test-results/final/integration-final-after-all-findings.trx)

## Limites da evidência local

Os testes locais não substituem os recibos externos de ACL e drift, a execução
da ferramenta de go-live contra o ambiente real, a propagação do kill switch em
múltiplas instâncias nem os smoke tests autorizados dos provedores. Esses gates
continuam explícitos no documento da fase e na resolução dos achados.
