---
language: pt-BR
---

# Pragmatic Modular Architecture

This solution organizes code by business capability first and technology second. Bounded contexts come from domain discovery, and a vertical slice is the unit of change inside a context.

## Foundation

| Path | Responsibility |
|---|---|
| `src/Platform.Api/` | host |
| `tests/Platform.ArchTests/` | arch tests |
| `tests/Platform.SecurityArchTests/` | security arch tests |
| `tests/Platform.UnitTests/` | unit tests |
| `tests/Platform.IntegrationTests/` | integration tests |
| `tests/Platform.PerformanceTests/` | performance benchmarks |

- Keep one bounded context per module folder under `src/Platform.Api/Modules/`.
- Discover module boundaries from domain evidence; do not infer them from technical layers, tables, or screens.
- Keep the shared kernel limited to true universals and enforce its size with an architecture test.
- Keep architecture, security, unit, integration, and performance validation separated by purpose under `tests/`.

## Module contract

Each bounded context owns its domain model, its slices, its versioned public contracts, and its technology. Domain Events stay internal to the producing context. Integration Events are distinct, immutable, versioned public contracts. Cross-context asynchronous delivery uses an outbox in the producer's store and an idempotent inbox at the consumer.

## Change contract

Keep a slice's request, structural validation, handler, source-generated logger, response, and transport mapping together. Put invariants in aggregates or value objects. Introduce repositories, specifications, policies, ports, CQRS projections, or separate services only in response to demonstrated complexity or operational pressure.

## Enforced dependency rules

| Rule | Scope | Forbidden |
|---|---|---|
| `module-domain-must-stay-technology-free` | Api.Modules.*.Domain | Microsoft.AspNetCore, Microsoft.EntityFrameworkCore, MongoDB.Driver, RabbitMQ.Client, Confluent.Kafka, StackExchange.Redis |
| `shared-kernel-must-stay-technology-free` | SharedKernel | Microsoft.AspNetCore, Microsoft.EntityFrameworkCore, MongoDB.Driver, RabbitMQ.Client, Confluent.Kafka, StackExchange.Redis |

`Platform.Api` is the composition root. Cross-context isolation, the error axis, and the shared-kernel budget are enforced by the same architecture test project.

## Guardrails

The default posture is fail-closed: authenticated fallback authorization, explicit anonymous exceptions, rate limiting for state-changing or upstream-cost endpoints, sanitized problem details, no domain binding from request bodies, no interpolated raw SQL, and no personal data in logs.

Runtime performance is not inferred from folder structure. Establish endpoint and workload baselines before setting latency, throughput, allocation, garbage-collection, eventual-consistency, or AI-runtime thresholds.

## Infraestrutura de plataforma (nota da fase 1b, 2026-08-23)

Outbox, Outbox Relay e `processed_messages` são infraestrutura de plataforma, não módulos de negócio: implementam os padrões transversais das ADRs 0002 e 0008 para todos os módulos. O schema Postgres `platform` é a convenção oficial dessa infraestrutura, com history table de migração própria. O provisionamento de partições mensais é mecânica de plataforma parametrizada por esquema e tabela; a semântica de fechamento (revoke de escrita, retenção WORM) permanece nos módulos donos. A infraestrutura de plataforma é consumida pelos dois hosts (`Platform.Api` e `Platform.Worker`) via assembly do Api; a extração de um projeto `Platform.Infrastructure` fica registrada como opção diferida, com gatilho definido: um host precisar excluir código de módulo por conformidade ou por tamanho de imagem. Contratos de escrita transacionais de plataforma seguem o padrão do `IAuditTrail`: o chamador entrega a `DbTransaction`.
