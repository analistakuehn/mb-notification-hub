# Recibo do recheck final

## Resultado

`RESOLVED`

`dotnet-architect`, `dotnet-engineer` e `dotnet-specialist` verificaram de forma
independente os mesmos 17 critérios. Cada revisor retornou 17 resultados
`PASS`, sem critério interno pendente.

## Identidade

- Manifesto: [`resolved-target-manifest.txt`](resolved-target-manifest.txt)
- Revisão-fonte: `3570c5e7ca761911330cace4b7dfdf1a8c5b2dbb`
- Arquivos: 104
- Divergências individuais de SHA-256: 0
- Digest do objeto:
  `e222c5fbbd103cb6be553a982a4291a8686069a250ab714c0620f1351c7e1ddf`
- SHA-256 do próprio manifesto:
  `ea8a498cedf769fe9dde2c4e257c35f97c078ad6e48aa541ff1e4c5e071c68b2`

Os três revisores recalcularam os 104 hashes e o digest com
`SHA256(UTF8(join(file_hash_lines, LF)))`.

## Critérios

| # | Resultado | Evidência principal |
| ---: | :---: | --- |
| 1 | `PASS` | DLT pré-confiança por lista de campos permitidos e sentinelas em key, corpo, headers e logs. |
| 2 | `PASS` | Chave Kafka rejeita whitespace e mais de 200 caracteres; 200 permanece válido e a partição avança. |
| 3 | `PASS` | Binder Kafka separa ausência e `null` de tipo ou formato inválido nos seis campos opcionais. |
| 4 | `PASS` | Replay REST consulta PostgreSQL antes do kill switch em miss do Redis. |
| 5 | `PASS` | `ProducerDisabled` produz uma trilha e um evento, sem notificação nem idempotência. |
| 6 | `PASS` | `KillSwitchCache` recusa a primeira carga quando ela própria consome o TTL. |
| 7 | `PASS` | `CachedProducerRegistry` inclui a consulta na idade absoluta de 60 segundos. |
| 8 | `PASS` | Os dois caches compartilham falha e backoff entre 32 chamadores sem estender autoridade. |
| 9 | `PASS` | Holds inválidos e órfãos são terminalizados sem retomada e não bloqueiam o lote. |
| 10 | `PASS` | Graph exige identidade coerente, role canônica única e recibo sem token. |
| 11 | `PASS` | Dispatch vincula `attemptId` a `notificationId` antes de qualquer gate ou efeito. |
| 12 | `PASS` | `SettleVerdictAsync` possui 3 parâmetros e `RequestedEvent` possui 6. |
| 13 | `PASS` | Nenhum tipo de `System.Globalization` ou `System.Text.Json` permanece totalmente qualificado em corpos corrigidos. |
| 14 | `PASS` | Os dois documentos enumeram exatamente os três tipos HTTP exclusivos. |
| 15 | `PASS` | O teste de `producer-disabled` não afirma inalcançabilidade. |
| 16 | `PASS` | O teste de cache declara duas instâncias locais, não processos independentes. |
| 17 | `PASS` | Oráculos Kafka pré-confiança exigem ausência dos dados sanitizados. |

## Evidência dinâmica

- Build integrado: 0 avisos e 0 erros.
- Testes unitários: 720 de 720 aprovados.
- Testes de arquitetura: 8 de 8 aprovados.
- Testes de arquitetura de segurança: 5 de 5 aprovados.
- Testes de integração: 432 aprovados, 2 omissões condicionais e 0 falhas.
- `git diff --check`: aprovado.

Os recibos executáveis estão relacionados em [`validation.md`](validation.md).

## Pontos cegos externos

O resultado `RESOLVED` cobre os achados locais e não substitui:

- recibo real de ACL exclusiva por tópico Kafka e de drift;
- execução da ferramenta de go-live contra PostgreSQL e Microsoft Graph reais;
- ensaio multi-instância do kill switch com zero efeitos após `t0 + 10 s`;
- smoke tests reais de SendGrid e Twilio.

Esses itens permanecem gates operacionais explícitos, não achados internos em
aberto.
