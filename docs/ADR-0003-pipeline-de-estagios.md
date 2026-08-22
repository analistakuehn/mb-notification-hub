# ADR-0003: Pipeline de estágios com resultado explícito

| | |
|---|---|
| **Status** | Proposta |
| **Data** | 2026-08-22 |
| **Decisores** | Arquitetura, Engenharia de Plataforma |
| **Consultados** | — |
| **Relacionadas** | ADR-0002 (retry na fila), ADR-0006 (auditoria), ADR-0011 (política) |
| **Documento-mãe** | Design de Sistema, §4.3 "Core Worker" |

## Contexto e problema

O Core Worker processa cada notificação em etapas sequenciais: validar, resolver contatos, aplicar política, renderizar, rotear, gravar. Cada etapa pode permitir continuar, rejeitar a notificação (resultado de negócio válido e auditável) ou adiá-la (janela de silêncio). Erros inesperados (banco fora, serviço indisponível) devem voltar para o mecanismo de retry da fila, não ser "tratados" como resultado.

Precisamos de uma estrutura que torne a ordem das etapas explícita, que registre a decisão de cada uma para auditoria e que seja legível para quem entra no time, sem adotar um estilo funcional (Railway-oriented, `Result<T, E>` encadeado) que o time decidiu não usar neste projeto.

## Fatores de decisão

- **Legibilidade** para engenheiros sem familiaridade com programação funcional.
- **Auditoria**: o resultado de cada estágio precisa ir para `audit_event` / `POLICY_EVALUATION`.
- **Separação entre resultado de negócio e falha técnica**: rejeição não é exceção; indisponibilidade não é resultado.
- **Extensibilidade**: novos estágios (ex.: novas regras de política) sem reescrever o fluxo.
- **Testabilidade** de cada estágio isoladamente.

## Opções consideradas

1. **Lista ordenada de `INotificationStage` com contexto mutável e `StageOutcome { Continue, Reject, Defer }`** (escolhida).
2. Railway-oriented: cada estágio devolve `Result<Context, Rejection>` e o pipeline faz `Bind` encadeado.
3. Método único sequencial no Core Worker (sem abstração de estágio).
4. Pipeline de middleware estilo ASP.NET (`next()` explícito).

## Decisão

Adotar a opção 1.

```csharp
public enum StageOutcome { Continue, Reject, Defer }

public interface INotificationStage
{
    string Name { get; }
    Task<StageOutcome> ExecuteAsync(NotificationContext ctx, CancellationToken ct);
}

public sealed class NotificationPipeline(IReadOnlyList<INotificationStage> stages)
{
    public async Task RunAsync(NotificationContext ctx, CancellationToken ct)
    {
        foreach (var stage in stages)
        {
            var outcome = await stage.ExecuteAsync(ctx, ct);
            ctx.Trace.Add(stage.Name, outcome, ctx.LastReason);
            if (outcome != StageOutcome.Continue) break;
        }
        await ctx.CommitAsync(ct);   // notification + attempts + outbox + audit_event, uma transação
    }
}
```

Regras:
- `Reject` e `Defer` são explícitos e carregam `reason`; nunca são expressos por exceção.
- Exceções **não são capturadas** pelo pipeline: propagam, a mensagem volta à fila com backoff (ADR-0002) e, após `maxReceiveCount`, vai à DLQ. Isso garante que falha técnica nunca vira `rejected` na auditoria.
- O contrato mínimo de `NotificationContext` define `LastReason`: string com o motivo da última decisão de estágio, consumida pela trilha (`ctx.Trace.Add`). Este snippet, com construtor sem `IAuditWriter`, é o canônico do pipeline.
- `ctx.Trace` é gravado como parte do `audit_event` do commit: a trilha por estágio é subproduto da estrutura.
- O estágio *Policy* é, internamente, outra lista ordenada (`IPolicyRule`, ADR-0011), seguindo o mesmo padrão.
- A ordem dos estágios é composta no *startup* (DI), não descoberta por reflexão.

### Consequências

**Positivas**
- Código linear; o fluxo é a lista de estágios, lida de cima para baixo.
- Cada estágio é uma classe com um teste.
- Auditoria por estágio sem código adicional.
- Adicionar estágio = nova classe + uma linha na composição.

**Negativas**
- Contexto mutável exige disciplina: um estágio não deve ler campos que outro ainda não preencheu. Mitigado por propriedades com `null` explícito e por testes de contrato da ordem.
- Sem o encadeamento tipado do estilo funcional, erros de ordem são detectados em teste, não em compilação.

## Prós e contras das opções

### Opção 1 — Lista de estágios + `StageOutcome`
- Prós: legível, auditável, extensível, sem dependência conceitual.
- Contras: contexto mutável.

### Opção 2 — Railway-oriented
- Prós: tipagem forte do fluxo de erro; composição elegante.
- Contras: curva de aprendizado; tende a misturar falha técnica e rejeição de negócio no mesmo `Result`; decisão explícita do time de não adotar.

### Opção 3 — Método sequencial único
- Prós: zero abstração.
- Contras: cresce até ficar ilegível; auditoria por etapa exige código manual; testes só de ponta a ponta.

### Opção 4 — Middleware com `next()`
- Prós: padrão conhecido do ASP.NET.
- Contras: `next()` permite que um estágio execute código *depois* dos seguintes, o que obscurece a ordem e dificulta a trilha; não precisamos desse poder.

## Como saberemos que foi a decisão certa

- O tempo de onboarding de engenheiros no fluxo do Core é medido e registrado na retrospectiva de cada fase.
- Nenhum `rejected` na auditoria tem origem em exceção técnica.

## Referências

- Design de Sistema — §4.3 Core Worker, §9.3.
