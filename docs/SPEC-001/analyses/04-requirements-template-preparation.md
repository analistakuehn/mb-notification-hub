# Preparação dos modelos de requisitos

**Resultado**: modelos preparados  
**SPEC**: `SPEC-001`  
**Perfil de entrega**: `standard`

## Conjunto canônico

O estágio produzirá exatamente os três artefatos centrais previstos pelo fluxo SPECIFY:

1. `requirements/core/01-development-specification.md`, com problema, objetivos, jornadas, capacidades, rastreabilidade, estado atual e alvo, requisitos de engenharia, aplicabilidade, decisões e riscos.
2. `requirements/core/02-implementation-map.md`, com sementes orientadas a resultado, dependências, ondas e pré-requisitos externos, sem estimativas nem decomposição de tarefas.
3. `requirements/core/03-verification-plan.md`, com cobertura, ferramentas, efetividade dos testes, Definition of Done global e gate de primeira produção.

Os modelos compartilhados do framework são a estrutura canônica. O modelo .NET de requisitos orienta vocabulário, conteúdo do adapter e cobertura de segurança, privacidade e observabilidade, sem criar um quarto artefato central.

## Disposição de famílias condicionais

A invocação não solicitou documentos adicionais por `--include`. As famílias condicionais aplicáveis serão registradas inline na Development Specification. Uma separação posterior dependerá do checkpoint de conteúdo do estágio e não será presumida nesta preparação.

## Regras aplicadas

- Os identificadores do PRD serão preservados como fontes de rastreabilidade.
- Os requisitos de engenharia receberão identificadores estáveis e critérios verificáveis.
- O Implementation Map permanecerá acima do nível de Delivery Slice e não antecipará o estágio PLAN.
- Valores operacionais sem evidência não serão persistidos como placeholders.
- As superfícies .NET respeitarão o Stack Profile manual e as fronteiras observadas do monólito modular.

## Recibo

`REQUIREMENTS_TEMPLATE_PREPARATION: PASS`

Os modelos estão resolvidos para a consolidação das contribuições do painel técnico.
