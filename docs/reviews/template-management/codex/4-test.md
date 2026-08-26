---
language: pt-BR
lens: TST
lens-name: Test
scope: TemplateManagement
source-revision: cc754e5a56dcbe1cae9cf05cf9303f7b0eb189d2
findings: 2
---

# Lente `TST`: Test

Cobertura de comportamento, força do oráculo, isolamento, caminhos de falha e
risco de regressão.

A suíte do módulo é ampla e os testes relevantes inspecionados possuem oráculos
independentes. Os dois achados tratam de cenários ausentes entre componentes.

---

## Achados desta lente

Ordenados por estado: o que ainda exige ação fica no fim.

| Achado | Severidade | Estado | Assunto |
|---|---|---|---|
| `TST-001` | `MEDIUM` | **PENDENTE** | Os testes não cobrem transições com o cache aquecido |
| `TST-002` | `MEDIUM` | **PENDENTE** | Os testes não exercitam a cadeia de upgrade da migração canônica |

---
## `TST-001` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `tests/Platform.IntegrationTests/TemplateManagement/PublishedIntegrationContractTests.cs` |
| linha | `65-97` |
| tipo-de-evidência | teste |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect e dotnet-specialist |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado. Converge com `TST-006` do relatório paralelo em `../claude/`. |

**Os testes não cobrem transições com o cache aquecido.**

Evidência:

    await TemplateApi.PublishAsync(publisher, key, version);
    HttpResponseMessage disabled = await publisher.PostAsJsonAsync(
        $"/v1/templates/{key}/disable", new { reason = "conteúdo incorreto em produção" });
    ...
    Result<PublishedTemplateLookup> lookup = await FindTemplateAsync("araia-cambio", key);

A primeira leitura publicada ocorre somente depois da transição. O cenário não
aquece IPublishedCatalog nem IPublishedTemplateRenderer com o estado anterior.
PublishedReadMemoizationTests confirma separadamente que um ponteiro aquecido
permanece válido por 59 segundos.

Impacto: a suíte passa sem detectar que uma instância aquecida pode continuar
localizando e renderizando conteúdo obsoleto.

Recomendação: adicionar contratos com cache aquecido para publicação, rollback,
depreciação e desativação, incluindo duas instâncias do serviço.

Verificação: os novos testes devem falhar na revisão congelada sem avançar o
relógio e passar sem depender da expiração de 60 segundos.

---

## `TST-002` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `tests/Platform.IntegrationTests/TemplateManagement/TemplateManagementApiFixture.cs` |
| linha | `97-107` |
| tipo-de-evidência | teste |
| introduzido-por-diff | `false` |
| revisores | dotnet-engineer |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado. O fixture continua aplicando todas as migrações direto ao estado final, sem exercitar a cadeia de upgrade. |

**Os testes não exercitam a cadeia de upgrade da migração canônica.**

Evidência:

    await scope.ServiceProvider
        .GetRequiredService<TemplateManagementDbContext>()
        .Database.MigrateAsync();

O fixture inicia um PostgreSQL vazio e aplica todas as migrações diretamente ao
estado final. CanonicalSchemaRoundTripTests cria dados somente depois disso.

Impacto: a suíte valida o schema final, mas não detecta perda de integridade na
conversão de linhas jsonb preexistentes.

Recomendação: criar um teste de cadeia de migração que pare na revisão anterior,
grave casos representativos e aplique CanonicalTextAndPublicationGuards.

Verificação: comprovar que hash, aprovação e publicação permanecem coerentes ou
que a migração recusa o estado com diagnóstico acionável.
