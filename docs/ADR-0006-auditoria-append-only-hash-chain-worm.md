---
language: pt-BR
---

# ADR-0006: Auditoria em banco, append-only, com hash chain e export WORM

| | |
|---|---|
| **Status** | Aceita (com errata de 2026-08-23) |
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
3. **Export WORM.** Um motor com dois gatilhos: o export diário recorta o dia por partição e o export de fechamento reafirma a partição inteira, autoritativo. Cada export grava o segmento da cadeia como NDJSON com o texto `canonical` byte a byte, um manifest assinado que cobre o intervalo `[seq_min, seq_max]` e uma attestation com o keyId e o algoritmo, em bucket S3 com Object Lock em modo **Compliance** e retenção igual ao prazo legal. A continuidade entre partições é dada pelo **encadeamento de manifests no WORM**: cada manifest referencia a chave e o hash de cauda do anterior, e a chave pública de assinatura é arquivada no próprio bucket, de modo que a verificação independente dispensa banco, plataforma e provedor de chaves (ver a errata de 2026-08-23).

Conteúdo renderizado fica cifrado (chave KMS por `application`) com `content_hash` em claro; para templates com variáveis sensíveis, o conteúdo é armazenado mascarado com hash duplo: `content_hash_full` (calculado sobre o conteúdo completo antes do mascaramento, para confronto com evidência externa) e `content_hash_masked` (sobre o que foi armazenado, verificado pelo endpoint de auditoria); a verificação criptográfica do conteúdo completo não é possível após o mascaramento (§10.2, A4). Leitura de conteúdo ou contato só por `/v1/audit/*`, gerando `audit.read`.

### Errata de 2026-08-23: continuidade entre partições

O texto original desta ADR previa que a partição seguinte iniciasse com `prev_hash` igual ao hash final ancorado da anterior. A implementação da cadeia mostrou que isso acopla o começo de um mês ao fechamento do anterior, que ocorre dias depois: no instante em que o primeiro evento de um mês é gravado, o hash final do mês anterior ainda não é definitivo, porque eventos atrasados continuam chegando dentro da janela de estabilização. Amarrar o primeiro elo a um valor ainda em movimento produziria uma cadeia que só fecha retroativamente, ou um bloqueio de escrita no começo de todo mês.

A errata corrige a regra: **cada partição mensal é uma cadeia autocontida**, iniciada na âncora determinística `SHA-256("notification-hub:audit-chain:{partição}:anchor")`, e a continuidade entre partições passa a ser garantida pelo **encadeamento de manifests no WORM**. Cada manifest exportado referencia a chave e o hash de cauda do manifest anterior, inclusive atravessando a fronteira de partição, de modo que a remoção de um recorte deixa de ser uma ausência silenciosa e vira uma referência que não resolve. A verificação de longo prazo compara o hash de cauda do banco com o do manifest de fechamento e percorre as referências para trás.

A propriedade que a regra original buscava, tornar detectável o descarte de uma partição inteira, continua garantida, e por um caminho mais forte: a âncora é reconstruível a partir do nome da partição por qualquer verificador, e o manifest de fechamento é assinado e imutável. O código commitado (`Domain/AuditChain.cs`) já implementava a âncora determinística; a errata alinha o documento ao que a cadeia faz.

### Errata de 2026-08-23: gatilho do plano B e forma reservada para ele

O texto original tratava o plano B como consequência de um teste de carga que validaria "o p99 de ingestão", sem dizer o que reprovaria, e nomeava uma única forma de sub-cadeia. Esta errata fixa as duas coisas, sem mudar a decisão.

**Gatilho, em escada.** Duas regras precisam valer ao mesmo tempo, medidas na taxa de append projetada:

1. **Sub-orçamento**: espera pelo lock mais posse do lock cabem em 10 ms no p99, ou seja 20 % dos 50 ms que o design dá ao aceite REST inteiro (§11.2).
2. **Regra de capacidade**, indicador antecedente: o teto implícito por partição, `1` dividido pelo p50 da posse, fica em pelo menos 2× a demanda sustentada de append. A fila explode antes de a média saturar, então esperar a média saturar é esperar demais.

Falhando qualquer uma delas, a ordem de resposta é fixa: aplicar primeiro o índice de cauda e o colapso de round trips sob o lock, remedir, e só então considerar o plano B, porque só ele muda a forma da cadeia, a verificação e o manifest. Nenhuma dessas duas correções mexe na cadeia; ambas são mais baratas que o plano B e podem torná-lo desnecessário.

**Forma do índice de cauda, corrigida pela medição de 2026-08-23.** A prescrição original desta errata era um índice parcial em `(occurred_at, seq DESC)`, e ela estava errada. Dentro de uma partição, a poda já satisfez o predicado de tempo, então `occurred_at` na frente é prefixo inútil; e como o predicado restante é faixa e não igualdade, a composta não fornece ordenação por `seq` dentro da faixa. O plano de execução confirma: sobre partição de dois milhões de linhas, sem índice a consulta custa 330 ms lendo 181.230 buffers, com a composta o planejador ignora o índice e repete a varredura em 371 ms, e com índice parcial em `(seq DESC)` custa 0,174 ms lendo 4 buffers. A forma adotada é **`(seq DESC) WHERE hash IS NOT NULL`**. Duas consequências: o predicado parcial precisa aparecer **literalmente** na consulta para o planejador casar o índice, e ele aparece hoje; e o mesmo índice serve o verificador horário e o export por faixa de `seq`, que também percorrem a partição por `seq`, de modo que o ganho não é só do caminho quente.

**Colapso de round trips: quatro para três, não para dois.** Lock e `nextval` cabem num statement só, porque nenhum dos dois lê snapshot da tabela, e `nextval` na projeção sobre a expressão travada é avaliado depois da concessão, o que mantém ordem de sequência igual a ordem de cadeia. A leitura do `prev_hash` **não** entra nesse statement. A razão é o nível de isolamento e não o planejador: sob READ COMMITTED o statement tira seu snapshot ao iniciar, antes de bloquear no lock, então um statement que espera e depois lê a trilha lê estado anterior ao commit de quem ele esperou, recebe um elo obsoleto e bifurca a cadeia. A medição de 2026-08-23 registrou **6.707 elos bifurcados em 8.711 linhas** com a leitura dobrada, e nenhum com a leitura em statement próprio. O erro corrigido aqui é **de correção, não de desempenho**: a versão anterior desta errata embarcaria um escritor que forka a cadeia, que é exatamente o dano que a cadeia existe para impedir.

**Dependência declarada do nível de isolamento.** O quatro para três só é correto porque o chamador está em READ COMMITTED, em que cada statement tira snapshot novo, já com o lock na mão. Um chamador em REPEATABLE READ ou SERIALIZABLE tira o snapshot no primeiro statement da transação, antes do lock, e a leitura obsoleta volta mesmo com os statements separados. Hoje o padrão do driver salva por acidente, não por desenho. A guarda no escritor, conferir o nível de isolamento e recusar o que não for READ COMMITTED com a razão registrada, é entrega da fatia corretiva; a dependência fica declarada aqui desde já, porque a próxima pessoa que escolher isolamento mais forte por segurança quebra a cadeia sem que nenhum teste avise.

### Errata de 2026-08-24: o que a aplicação mediu, e a nota que ela derrubou

O índice, o colapso e a guarda entraram no código em 2026-08-24. Três registros, porque a aplicação confirmou duas coisas e falsificou uma.

**Confirmado, a forma do índice.** `ix_audit_event_chain_tail`, `(seq DESC) WHERE hash IS NOT NULL`, criado **na partição-mãe**, de onde o PostgreSQL o propaga a toda partição existente e a toda partição que o provisionador criar depois. Criar por partição teria deixado o mês seguinte nascer sem índice, que é o defeito de volta. Medido com o schema das migrações, a cauda passa a ser `Index Scan` sobre o índice propagado e custa 0,040 ms lendo 3 buffers com dez mil linhas, 0,042 ms lendo 4 com quinhentas mil e 0,046 ms lendo 4 com dois milhões: plana com o volume, contra 330 ms e 181.230 buffers sem índice. Consequência operacional que viaja com a decisão: criar índice na partição-mãe toma lock sobre ela e constrói em cada partição anexada, então em banco que já carrega anos de trilha esta migração é janela de manutenção, e a construção concorrente que evitaria isso não existe para tabela particionada nem dentro da transação de migração.

**Confirmado, o colapso e a dependência de isolamento.** O append segura o lock por três round trips, e o escritor recusa transação que não seja READ COMMITTED, conferindo tanto o nível declarado pelo chamador, antes de qualquer statement, quanto o nível que o servidor reporta para a transação em curso, que é o que um default de servidor, banco ou papel muda sem ninguém dizer no ponto de chamada. O ganho do quatro para três não aparece na mediana da bancada e aparece onde importa, que é a cauda sob contenção: com dois milhões de linhas na partição, janela p99 de **10,7 ms contra 100,0 ms** e **426,4 contra 234,8 appends por segundo**. É exatamente o p99 que o §11.2 protege.

**Corrigida, a nota sobre quantos caminhos o índice serve: é um.** A errata de 2026-08-23 afirmava que o índice de cauda serviria também o verificador horário e o export por faixa de `seq`. Aquilo era inferência a partir da forma do índice, não leitura de plano, e o método é o mesmo que já custou uma rodada de medição. O índice de cauda serve **um** caminho, a cauda do append, onde entrega 0,046 ms contra 330 ms. A causa fica registrada junto para que a inferência não se repita: **índice parcial só casa com statement que carregue o predicado dele**, e o leitor compartilhado não pode carregar `hash IS NOT NULL` porque precisa devolver também as linhas pré-cadeia, que não têm hash.

**Decisão de 2026-08-24: separar o leitor, com escopo apertado.** A leitura compartilhada passa a ser duas consultas, encadeadas e pré-cadeia, cada uma carregando o seu predicado e atendida pelo seu índice parcial, mescladas por `seq` fora do banco; e passa a andar por **paginação por chave** em blocos, nunca a partição inteira numa tacada. Quatro razões, em ordem de peso. Primeira, direção do custo: um índice não parcial cobraria manutenção em todo insert da tabela mais quente do sistema, para sempre, para servir dois trabalhos de retaguarda que rodam de hora em hora e uma vez por dia. Segunda, o acoplamento é acidental e o desacoplamento já existe a jusante, porque o export já grava as pré-cadeia em objeto separado; a separação alinha a leitura a uma fronteira que os consumidores já respeitam. Terceira, o lado pré-cadeia custa zero no caminho quente: pré-cadeia é conjunto fechado, então um índice parcial `WHERE hash IS NULL` nunca recebe inserção, e "não há pré-cadeia nesta partição" vira resposta de uma busca em vez de varredura para provar vazio. Quarta, o que some não é só a varredura, é a ordenação, que carregava o texto canônico de cada linha por uma intercalação em disco. A paginação por chave vem junto e não é opcional: torna a passagem integral interrompível e retomável, não estoura `work_mem` e não segura transação longa, e o modelo já a suportava porque o checkpoint da verificação guarda `last_seq`. O `MAX(seq)` do plano do export passa a carregar os predicados nas duas metades, casando com os mesmos índices parciais. **Nada muda nos bytes exportados, nos arquivos, no manifest, nem na tolerância a buracos de `seq`**: muda apenas como as linhas são buscadas.

**Falseabilidade declarada.** Depois da separação, a ordenação em disco tem que sumir do plano da faixa de `seq`. Se não sumir, a separação falhou, e o recurso é o índice não parcial **na forma de índice único, com o parcial removido, nunca os dois** (um índice não parcial ainda serve a cauda do append por varredura para trás, porque as pré-cadeia são as de `seq` mais baixo e a primeira linha encontrada já tem hash, ao custo de a garantia do caminho quente passar a depender de distribuição de dados em vez de estrutura). É recurso, não empate, e exige nova ratificação antes de ser aplicado.

**Limite conhecido do plano B.** Sub-cadeias por `application` só distribuem contenção se o tráfego se espalhar por aplicações. O critério de saída da fase 1b migra templates de um produtor dominante, e com uma aplicação concentrando o volume o plano B rende quase nada. Por isso o discriminador de sub-cadeia é reservado como **string opaca** (`chainKey`), e não como o valor de `application`: assim o plano B e um plano por bucket de hash com número fixo de sub-cadeias, que funciona mesmo com produtor único, compartilham formato de manifest, gramática de âncora e implementação de verificação.

**Ativação só em fronteira de partição.** Uma partição nasce com cadeia única ou multi-cadeia e nunca troca no meio. A partição M continua cadeia única, a M+1 nasce multi-cadeia, e a gramática de âncora distingue as duas: `notification-hub:audit-chain:{partição}:anchor` para a cadeia única, e `notification-hub:audit-chain:{partição}:{chainKey}:anchor` para um segmento. Não existe backfill, e nenhuma linha já gravada é reinterpretada.

**Manifest com lista de segmentos.** O manifest passa a declarar os segmentos de cadeia como lista, hoje com exatamente um segmento, identificado pela âncora da partição. O `formatVersion` sobe uma vez só: o mesmo incremento carrega a lista de segmentos e a semântica da janela do manifest que está pendente de esclarecimento. Nada foi exportado em produção ainda, e depois do primeiro export o formato vira corpus imutável, então as duas mudanças precisam viajar juntas.

**Nada de coluna nova em `audit_event` agora.** Uma coluna `chain_key` com valor único numa tabela append-only é ambiguidade retroativa sem contrapartida: as linhas já gravadas ficariam com um valor que ninguém escolheu. A coluna entra, se entrar, na mesma mudança que ativa o plano B numa fronteira de partição.

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
