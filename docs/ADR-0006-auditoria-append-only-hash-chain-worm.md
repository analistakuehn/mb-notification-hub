# ADR-0006: Auditoria em banco, append-only, com hash chain e export WORM

| | |
|---|---|
| **Status** | Proposta |
| **Data** | 2026-08-22 |
| **Decisores** | Arquitetura, Compliance, Segurança da Informação, Auditoria Interna |
| **Consultados** | Jurídico (retenção), SRE |
| **Relacionadas** | ADR-0003 (trilha por estágio), ADR-0004, ADR-0005, ADR-0007 (`audit.read`) |
| **Documento-mãe** | Design de Sistema, §9.3–§9.6, §10.2 ameaça A11 |

## Contexto e problema

O hub precisa provar, por anos, para o BCB, para a ANPD e para auditoria interna: o que foi enviado, para quem, quando, sob qual base legal, com qual texto exato, aprovado por quem, entregue por qual provedor com qual resposta, e quem consultou essa informação depois. A prova precisa ser completa (não amostrada), transacional (registrada junto com o efeito) e íntegra (alteração ou remoção detectável).

Logs operacionais não servem: são amostráveis, têm retenção curta, não são transacionais e não devem carregar PII.

## Fatores de decisão

- **Completude e transacionalidade**: nenhum efeito sem seu registro.
- **Integridade verificável**: adulteração por DBA, por bug ou por atacante deve ser detectável.
- **Retenção longa** (≥ 5 anos, a confirmar) com custo controlado.
- **Acesso controlado e auditado**: ler a auditoria também é evento auditável.
- **Independência de terceiros** para demonstrar conformidade.
- **Desempenho**: não pode transformar cada insert numa seção crítica global.

## Opções consideradas

1. **Tabela `audit_event` append-only no Postgres + hash chain + export diário para S3 Object Lock (modo Compliance)** (escolhida).
2. Apenas logs estruturados em plataforma de observabilidade.
3. Apenas export para S3 Object Lock (sem trilha transacional no banco).
4. Ledger gerenciado (ex.: banco de dados de ledger de nuvem) ou blockchain privada.

## Decisão

Adotar a opção 1, em três camadas que cobrem a falha uma da outra:

1. **Banco, append-only por construção.** `audit_event` (e `consent`, `approval`, `template_version`, `class_policy_version`, `delivery_event`) com role de aplicação que só tem `INSERT`/`SELECT`; trigger `BEFORE UPDATE OR DELETE` que lança exceção; partições antigas protegidas por `REVOKE` de escrita + trigger de bloqueio (PostgreSQL não tem modo read-only por tabela); `pgaudit` ativo. O `audit_event` é gravado **na mesma transação** do efeito que registra (aceite, decisão de política, tentativa, entrega, consentimento, publicação, configuração, leitura de auditoria).
2. **Hash chain por partição mensal.** O escopo da cadeia é a partição mensal de `AUDIT_EVENT` (chave de partição: `occurred_at`); não existe sequência global. Cada evento tem `seq` (`bigserial`), `prev_hash` e `hash = SHA-256(prev_hash ‖ canonical)`, onde `canonical` é a coluna que guarda os bytes exatos (UTF-8) da serialização canônica RFC 8785 (JCS) que foram hasheados; a verificação usa `canonical`, nunca reserializa o `jsonb`. Sob concorrência, o `prev_hash` é obtido com `pg_advisory_xact_lock` sobre a partição corrente, na mesma transação do efeito; a consequência de serialização é reconhecida, com gate no teste de carga da fase 1b (p99 de ingestão) e plano B já previsto: sub-cadeias por `application` dentro da partição. O job horário de verificação varre por `seq` com watermark de estabilização (só verifica eventos com `occurred_at < now() - 5 min`) e tolera buracos de `seq` (transações abortadas consomem valor; ordem de atribuição não é ordem de commit); o alarme de segurança dispara só para elo cujo `prev_hash` não corresponde ao `hash` do elo anterior presente.
3. **Export WORM.** Job diário exporta os eventos do dia com um manifest assinado (KMS) que cobre o intervalo `[seq_min, seq_max]` do dia por partição, para bucket S3 com Object Lock em modo **Compliance** e retenção igual ao prazo legal. No encerramento da partição mensal, antes do ciclo `DETACH` + export + drop, o hash final da partição é ancorado no manifest WORM; a partição seguinte inicia com `prev_hash` igual ao hash final ancorado da anterior (continuidade entre partições). Verificação de longo prazo compara o hash final no banco com o manifest.

Conteúdo renderizado fica cifrado (chave KMS por `application`) com `content_hash` em claro; para templates com variáveis sensíveis, o conteúdo é armazenado mascarado com hash duplo: `content_hash_full` (calculado sobre o conteúdo completo antes do mascaramento, para confronto com evidência externa) e `content_hash_masked` (sobre o que foi armazenado, verificado pelo endpoint de auditoria); a verificação criptográfica do conteúdo completo não é possível após o mascaramento (§10.2, A4). Leitura de conteúdo ou contato só por `/v1/audit/*`, gerando `audit.read`.

### Consequências

**Positivas**
- Responde às oito perguntas de reconstrução (§9.5) com uma chamada.
- Adulteração é detectável em até uma hora; remoção silenciosa é impossível após o export.
- Sem terceiro na cadeia de prova; o S3 com Object Lock é infraestrutura já sob o contrato AWS existente.
- Auditoria de acesso à auditoria.

**Negativas**
- Volume: `audit_event` cresce mais rápido que qualquer outra tabela. Mitigado por particionamento, export + drop por partição e JSON compacto em `details`.
- A cadeia por partição serializa inserts dentro da partição (advisory lock); o gate é o teste de carga da fase 1b (p99 de ingestão) e o plano B é a sub-cadeia por `application` dentro da partição (§16, risco 7).
- Retenção ≥ 5 anos em WORM tem custo de armazenamento, baixo em S3 Glacier-class, mas irreversível por definição: erro no export é permanente. Mitigado por validação do manifest antes do lock e por ambiente de teste com retenção curta.

## Prós e contras das opções

### Opção 1 — Banco append-only + hash chain + WORM
- Prós: completa, transacional, íntegra, verificável, independente.
- Contras: volume; custo de export.

### Opção 2 — Só logs
- Prós: já existe.
- Contras: amostragem, retenção curta, não transacional, PII em log, sem integridade.

### Opção 3 — Só WORM
- Prós: imutável no destino.
- Contras: janela entre o efeito e o export sem proteção; sem consulta transacional; sem trilha de leitura.

### Opção 4 — Ledger gerenciado / blockchain
- Prós: integridade "de fábrica".
- Contras: mais um sistema para provar conformidade; custo; lock-in; a hash chain em Postgres dá a mesma propriedade com ferramentas que o time domina.

## Como saberemos que foi a decisão certa

- Verificação de hash chain e export WORM rodando sem falha por 90 dias consecutivos antes do go-live.
- Uma auditoria simulada (interna) reconstrói uma notificação de 6 meses atrás sem ajuda de engenharia.
- Nenhum `audit.read` sem identidade Entra.

## Referências

- Design de Sistema — §9.3 a §9.6, §10.2 (A6, A7, A11), §16 risco 7.
- Res. CMN 4.893/2021 — rastreabilidade e registro; LGPD art. 37 (registro das operações).
