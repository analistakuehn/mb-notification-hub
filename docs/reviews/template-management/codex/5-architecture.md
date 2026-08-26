---
language: pt-BR
lens: ARC
lens-name: Arquitetura
scope: TemplateManagement
source-revision: cc754e5a56dcbe1cae9cf05cf9303f7b0eb189d2
findings: 3
---

# Lente `ARC`: Architecture

Fronteiras, contratos, consistência distribuída, imutabilidade e decisões
aceitas.

---

## Achados desta lente

Ordenados por estado: o que ainda exige ação fica no fim.

| Achado | Severidade | Estado | Assunto |
|---|---|---|---|
| `ARC-001` | `HIGH` | **PENDENTE** | Ponteiros publicados permanecem obsoletos por até 60 segundos |
| `ARC-002` | `HIGH` | **PENDENTE** | Versões aprovadas não possuem a proteção append-only exigida pela... |
| `ARC-003` | `MEDIUM` | **PENDENTE** | O DTO publicado expõe uma coleção concretamente mutável |

---
## `ARC-001` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `HIGH` |
| confiança | alta |
| arquivo | `Infrastructure/Integration/PublishedReadCache.cs` |
| linha | `5-15, 30-55` |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect, dotnet-engineer e dotnet-specialist |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado. Converge com `SEC-011` e `ARC-006` do relatório paralelo em `../claude/`. |

**Ponteiros publicados permanecem obsoletos por até 60 segundos.**

Evidência:

    internal static readonly TimeSpan PointerLifetime = TimeSpan.FromSeconds(60);

    if (_pointers.TryGetValue(key, out PointerEntry? entry)
        && entry.ExpiresAt > timeProvider.GetUtcNow()
        && entry.Value is T typed)
    {
        value = typed;
        return true;
    }

A cache é local ao processo e não expõe invalidação. PublishedCatalog e
PublishedTemplateRenderer devolvem o valor armazenado antes de consultar o
banco. Os handlers de publicação, rollback, depreciação e desativação não
coordenam essa cache.

Impacto: depois de uma transição confirmada, instâncias aquecidas podem renderizar
conteúdo antigo ou já desabilitado. Novas publicações e rollbacks também podem
divergir entre processos durante a janela.

Recomendação: invalidar ou versionar os ponteiros somente depois do commit, com
propagação entre instâncias. Como alternativa, retirar da cache a verificação
do estado terminal.

Verificação: aquecer catálogo, renderer e política, confirmar o commit de cada
transição e exigir efeito imediato na mesma instância e em outra. Confirmar
também que uma transação revertida não invalida o ponteiro vigente.

---

## `ARC-002` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `HIGH` |
| confiança | alta |
| arquivo | `Infrastructure/Persistence/Migrations/20260822225539_TemplateLifecycleAudit.cs` |
| linha | `83-101` |
| tipo-de-evidência | decisão-aceita e leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-engineer |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado, e é o achado de maior consequência ainda aberto neste relatório: a proteção append-only que a decisão aceita exige não existe para versões de template, de layout nem de política. |

**Versões aprovadas não possuem a proteção append-only exigida pela decisão aceita.**

Evidência:

    CREATE TRIGGER trg_audit_event_append_only
        BEFORE UPDATE OR DELETE ON templatemanagement.audit_event
        FOR EACH ROW EXECUTE FUNCTION templatemanagement.reject_append_only_mutation();

    CREATE TRIGGER trg_approval_append_only
        BEFORE UPDATE OR DELETE ON templatemanagement.approval
        FOR EACH ROW EXECUTE FUNCTION templatemanagement.reject_append_only_mutation();

A ADR-0006 aceita inclui template_version e class_policy_version entre as
tabelas append-only. Nenhuma migração do módulo cria proteção equivalente para
essas tabelas, para layout_version ou para o conteúdo associado. As leituras
publicadas também não executam VerifyContentHash.

Impacto: SQL direto, credencial excessiva ou defeito de persistência pode alterar
conteúdo aprovado, e o renderer pode distribuí-lo sem detectar adulteração.

Recomendação: impor no banco a imutabilidade do conteúdo e das definições após a
saída de draft, aplicar privilégio mínimo à role da aplicação e validar o hash
antes do consumo publicado.

Verificação: publicar template, layout e política, tentar UPDATE e DELETE
diretos e exigir rejeição. Adulterar uma cópia de teste e exigir falha por
incompatibilidade de hash no catálogo e no renderer.

---

## `ARC-003` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `Infrastructure/Integration/PublishedCatalog.cs` |
| linha | `82-86` |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado. |

**O DTO publicado expõe uma coleção concretamente mutável.**

Evidência:

    ChannelsWithContent = version.Contents
        .Select(content => content.Channel)
        .Distinct()
        .ToList(),

A propriedade pública usa IReadOnlyList, mas a instância concreta continua
sendo List e integra um DTO compartilhado pela cache.

Impacto: um consumidor pode converter a coleção para o tipo concreto, alterá-la
e afetar leituras posteriores dentro do processo.

Recomendação: usar uma coleção realmente imutável ou criar uma cópia defensiva
em cada projeção publicada.

Verificação: tentar modificar a primeira resposta e confirmar que a operação
não é possível e que leituras posteriores permanecem inalteradas.
