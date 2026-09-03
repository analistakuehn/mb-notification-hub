# Preparação do Stack Profile

**Resultado**: perfil preservado  
**SPEC**: `SPEC-001`  
**Escopo**: reutilização do perfil técnico durante o estágio SPECIFY

## Decisão aplicada

O arquivo `.araia/stack-profile.yaml` contém o marcador `manually-edited: true`. O usuário escolheu preservar esse conteúdo e prosseguir com descoberta somente leitura. Nenhuma detecção automática foi promovida ao perfil.

## Eixos reutilizados

O estágio utilizará os eixos declarados para .NET 10, monólito modular, Entity Framework, PostgreSQL, SQS, Kafka, Redis, S3, KMS e autenticação JWT. A inspeção mecânica encontrou dependências correspondentes no repositório, conforme registrado em `01-solution-inspection.md`.

Os eixos adicionais de armazenamento de objetos em S3, gestão de chaves com KMS e bloqueios distribuídos com advisory locks do PostgreSQL permanecem como decisões locais do perfil manual. Esta contribuição não os recalcula nem os substitui.

## Divergência preservada

O valor `messaging-consumer-pattern: none` permanece inalterado, embora a solução use Kafka e SQS diretamente. A especificação tratará essa diferença como evidência de revisão técnica, sem presumir a correção do valor nem bloquear a autoria dos requisitos.

## Recibo

`STACK_PROFILE_PREPARATION: PASS`

O Stack Profile manual foi preservado e está disponível como restrição técnica para as contribuições seguintes.
