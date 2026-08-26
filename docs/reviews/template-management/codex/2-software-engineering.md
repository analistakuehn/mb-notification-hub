---
language: pt-BR
lens: ENG
lens-name: Engenharia de software
scope: TemplateManagement
source-revision: cc754e5a56dcbe1cae9cf05cf9303f7b0eb189d2
findings: 3
---

# Lente `ENG`: Software Engineering

Correção, coesão, manutenibilidade, tratamento de erro, consistência de
implementação e escopo de mudança.

---

## Achados desta lente

Ordenados por estado: o que ainda exige ação fica no fim.

| Achado | Severidade | Estado | Assunto |
|---|---|---|---|
| `ENG-001` | `MEDIUM` | **PENDENTE** | A migração de jsonb para text pode invalidar hashes existentes |
| `ENG-002` | `MEDIUM` | **PENDENTE** | A pré-visualização e o renderizador publicado executam pipelines... |
| `ENG-003` | `LOW` | **PENDENTE** | HistoricalCatalog pode retornar uma versão draft |

---
## `ENG-001` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `Infrastructure/Persistence/Migrations/20260823013647_CanonicalTextAndPublicationGuards.cs` |
| linha | `21-29` |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-engineer |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado. Não há verificação prévia nem estratégia de reconciliação para hashes de linhas migradas. |

**A migração de jsonb para text pode invalidar hashes existentes.**

Evidência:

    ALTER TABLE templatemanagement.template_version
        ALTER COLUMN variables_schema TYPE text USING variables_schema::text;
    ALTER TABLE templatemanagement.class_policy_version
        ALTER COLUMN definition TYPE text USING definition::text;

A migração não reconcilia content_hash. Antes da conversão, o PostgreSQL já
reescreveu a representação de literais numéricos armazenados como jsonb. O
texto resultante pode, portanto, diferir dos bytes originalmente cobertos pelo
hash e pela aprovação.

Impacto: versões migradas podem falhar na publicação ou no rollback e perder a
correspondência entre conteúdo, hash e aprovação.

Recomendação: criar uma verificação prévia e uma estratégia explícita de
reconciliação ou reaprovação, sem substituir silenciosamente hashes aprovados.

Verificação: migrar a partir da revisão anterior linhas contendo 1e2, -0, 1.50
e 0.1e-3. Confirmar que hash, aprovação e VerifyContentHash permanecem
coerentes ou que a migração recusa o estado com diagnóstico acionável.

---

## `ENG-002` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `Features/Queries/RenderTemplateVersion/RenderTemplateVersion.Handler.cs` |
| linha | `170-176` |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado. O slice de pré-visualização segue com pipeline próprio, sem normalização de SMS e sem a guarda de link em autenticação. Converge com `ENG-001` do relatório paralelo em `../claude/`. |

**A pré-visualização e o renderizador publicado executam pipelines divergentes.**

Evidência:

    return Result.Success(new Response(
        content.Channel.Value,
        requested.Value,
        resolved.Value,
        subject.Value,
        wrappedBody,
        wrappedBodyText));

A pré-visualização devolve o resultado sem a normalização de SMS e sem a guarda
final que recusa links renderizados em SMS de autenticação. PublishedTemplateRenderer
implementa essas duas etapas em um pipeline separado.

Impacto: o autor pode aprovar conteúdo diferente daquele enviado ou obter uma
pré-visualização bem-sucedida para uma mensagem recusada durante o envio.

Recomendação: extrair um único pipeline interno para renderização, layout,
normalização e guardas pós-renderização. Cada superfície deve acrescentar
somente o comportamento que lhe é próprio.

Verificação: para a mesma versão e o mesmo payload, exigir saída e erros
idênticos entre a pré-visualização e o renderizador publicado, inclusive para
SMS e links dinâmicos de autenticação.

---

## `ENG-003` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `LOW` |
| confiança | alta |
| arquivo | `Infrastructure/Integration/HistoricalCatalog.cs` |
| linha | `34-37` |
| tipo-de-evidência | contrato |
| introduzido-por-diff | `false` |
| revisores | dotnet-engineer |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado. A consulta histórica continua filtrando apenas chave e número de versão, sem restringir a published e superseded. |

**HistoricalCatalog pode retornar uma versão draft.**

Evidência:

    TemplateVersion? historical = await dbContext.TemplateVersions
        .AsNoTracking()
        .WhereTemplateKey(key)
        .FirstOrDefaultAsync(candidate => candidate.Version == version, cancellationToken);

O contrato IHistoricalCatalog declara que somente versões published ou
superseded são encontradas. A consulta filtra apenas a chave e o número da
versão.

Impacto: um consumidor pode tratar como evidência histórica uma versão que
nunca foi publicada nem despachada.

Recomendação: restringir a consulta aos estados published e superseded.

Verificação: uma versão draft deve retornar NotFound. A mesma versão deve ser
encontrada depois da publicação e continuar disponível quando superseded.
