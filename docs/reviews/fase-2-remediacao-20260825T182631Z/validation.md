---
language: pt-BR
---

# Evidência de validação

[Voltar ao índice](00-index.md)

## Suítes locais

| Verificação | Resultado |
|---|---|
| `dotnet build MonteBravo.NotificationHub.sln` | aprovado, 0 avisos e 0 erros |
| `Platform.UnitTests` | 967 de 967 aprovados |
| `Platform.ArchTests` | 8 de 8 aprovados |
| `Platform.SecurityArchTests` | 5 de 5 aprovados |
| `Platform.IntegrationTests` | 558 aprovados, 2 omissões condicionais, 0 falhas, total de 560 |

O build roda com `TreatWarningsAsErrors` e `EnforceCodeStyleInBuild` ligados por
`Directory.Build.props`, de modo que um aviso de estilo reprova a compilação. As
duas omissões da suíte de integração são os smoke tests de SendGrid e Twilio, que
exigem credenciais reais e produzem efeito externo, e permanecem condicionados a
execução operacional autorizada.

A execução de integração acima é posterior ao último build do código: ela cobre a
poda de partição acrescentada à varredura de supressões pendentes, que foi a
última mudança de comportamento desta remediação.

## Oráculos novos

| Oráculo | O que ele derruba |
|---|---|
| `The_worst_case_fallback_to_sms_fits_the_accepted_window` | intervalo de varredura ou timeout de provedor que, sozinho, torna o aceite de 35 s aritmeticamente impossível |
| `The_dedupe_retention_covers_the_window_an_attempt_can_still_be_resolved_in` | retenção da marca de deduplicação menor que a janela em que um attempt ainda é resolvível, que é o intervalo em que um replay volta a gravar evidência |
| `The_attempt_window_covers_the_longest_notification_the_hub_accepts` | janela de resolução abaixo do TTL máximo, que descartaria em silêncio a confirmação de uma entrega que ainda importa |
| `The_planner_answers_the_candidate_selection_with_the_index_and_walks_the_table_without_it` | seleção da reconciliação que deixa de implicar o índice parcial, com falsificação por remoção do índice |
| `The_candidate_selection_bounds_the_join_by_the_partition_key` | junção da reconciliação sem a janela de criação |
| `--mode delivery`, rodada do scheduler | rodada que passa a varrer sequencialmente ou cujo percentil sobe com o volume da tabela |
| `--mode delivery`, custo do callback | crescimento por evento acima do orçamento, e a diferença entre transação por evento e por lote |
| `A_pinned_vector_from_an_independent_signer_verifies_without_signing_anything_here` | mudança simultânea no verificador e no auxiliar de assinatura, que os testes que assinam em tempo de teste não pegam |
| `The_pinned_vector_is_refused_when_one_byte_of_the_body_changes` | verificador que aceita corpo alterado |
| `The_pinned_vector_is_refused_when_the_signed_timestamp_changes` | verificador que aceita instante alterado dentro da janela |
| `A_suppression_the_ledger_never_received_is_reported_by_the_drain` | sinal de supressão perdido por falha transitória do módulo de contatos |
| `A_drained_report_of_an_already_reported_event_counts_no_second_refusal` | relato repetido contando uma segunda recusa que nunca houve |
| `A_callback_over_the_event_ceiling_is_refused_whole_and_stores_nothing` | lote acima do teto aceito, com tempo de resposta escolhido por quem chama |
| `A_provider_without_a_later_lookup_is_left_out_of_the_batch_and_stays_parked` | lote da reconciliação ocupado permanentemente por linhas que nenhuma pergunta resolve |
| `A_run_that_requires_docker_refuses_to_report_success_without_it` | suíte verde num ambiente onde os cenários que provam os critérios nunca executaram |
| `The_round_interval_is_configuration_and_defaults_to_two_seconds` | padrão do intervalo fora do orçamento do fallback |

## Ajuste de execução da suíte

As três classes que leem plano de execução provisionam cada uma o próprio banco
em contêiner e derrubam índices, então nenhuma pode compartilhar fixture. Elas
passaram a compartilhar uma coleção sem fixture, que é a forma de serializá-las
entre si. Em paralelo, sobre os contêineres que o resto da suíte já mantém, elas
produziam falha de provisionamento, que não é asserção vermelha e sim contêiner
que nunca fica pronto: a suíte reportava centenas de falhas sem defeito de
código. Duas execuções completas foram descartadas por essa causa antes do
ajuste, e a serialização é a correção.

## Números desta bancada

Comando:

```bash
dotnet run --project tests/Platform.PerformanceTests -c Release -- --mode delivery --delivery-volumes 50000,300000
```

Contêiner PostgreSQL 17 descartável, sem concorrência de produção. Ida trivial ao banco nesta bancada: 0,538 ms (p50), e
cada evento custa cinco idas, o que põe o piso por evento em cerca de 2,7 ms só
de ida e volta.

### Rodada do scheduler no caminho de fallback

| Volume | Statement | p50 | p99 | Reivindicadas | Por linha | Plano |
|---:|---|---:|---:|---:|---:|---|
| 50 mil | prazo vencido (`queued`/`sent`) | 1,50 ms | 10,71 ms | 99 | 15 us | índice |
| 50 mil | prazo vencido (`unknown`) | 1,33 ms | 2,60 ms | 13 | 102 us | índice |
| 300 mil | prazo vencido (`queued`/`sent`) | 3,63 ms | 8,48 ms | 200 | 18 us | índice |
| 300 mil | prazo vencido (`unknown`) | 2,52 ms | 3,31 ms | 76 | 33 us | índice |

Esta é a série confiável. O plano é atendido por índice nos quatro casos, e o
custo por linha reivindicada fica plano enquanto a tabela cresce seis vezes: a
rodada cresce com o que ela reivindica, não com o tamanho da tabela. É
exatamente a propriedade que o prazo até o SMS de fallback depende de ter, e a
que ninguém tinha medido.

### Custo de ingestão de um callback

| Forma | Eventos | Callback p50 | Por evento p50 |
|---|---:|---:|---:|
| por evento | 1 | 1,861 ms | 1,861 ms |
| por lote | 1 | 1,775 ms | 1,775 ms |
| por evento | 10 | 18,778 ms | 1,878 ms |
| por lote | 10 | 14,557 ms | 1,456 ms |
| por evento | 50 | 2 215 ms | 44,301 ms |
| por lote | 50 | 3 422 ms | 68,436 ms |
| por evento | 200 | 1 374 ms | 6,868 ms |
| por lote | 200 | 1 349 ms | 6,743 ms |
| por evento | 500 | 17 249 ms | 34,499 ms |
| por lote | 500 | 17 358 ms | 34,717 ms |

**As duas primeiras células são confiáveis; as três últimas não são, e a razão
importa mais do que os números.** Em lotes de um e de dez o resultado bate com o
modelo e com o piso de ida e volta: o custo por evento fica plano na forma por
evento, como se espera de N transações independentes, e cai na forma por lote,
como se espera de um commit amortizado.

De cinquenta para cima a série deixa de ser ordenável. Um lote de cinquenta
aparece mais caro por evento do que um de duzentos, e a forma por lote aparece
mais lenta que a por evento no mesmo tamanho. As duas coisas são impossíveis
para um caminho linear, e continuaram impossíveis depois de a disciplina de
`CHECKPOINT` entre células ser acrescentada. A conclusão honesta é que esta
bancada, com disco virtual e centenas de megabytes de escrita por célula, não
sustenta número absoluto nessa faixa. O que a série fez foi apontar para a causa
estrutural, que é derivável do código sem medir: a escrita é quadrática no
tamanho do lote. Essa parte está afirmada por leitura e aritmética, não por esta
tabela.

O aceite em percentil sobre carga real continua sendo do gate de carga. O que
esta fase entrega é o instrumento, a série pequena que confere com o modelo e a
propriedade quadrática nomeada.

## Dois defeitos do próprio instrumento, corrigidos antes de publicar número

A sonda de entrega errou duas vezes, e as duas correções são parte da evidência
porque as duas produziriam conclusão errada.

**Falso alarme de varredura sequencial.** A primeira versão procurava as palavras
`Seq Scan` em qualquer lugar do plano e concluía que o statement varria a tabela.
Uma tabela particionada carrega sempre as partições vazias dos meses à frente, e
o planejador lê uma partição vazia sequencialmente porque não existe nada mais
barato do que ler nada. A sonda reprovava um schema perfeitamente indexado, que é
exatamente como um instrumento ganha a fama que o faz ser ignorado. É a mesma
armadilha que os testes de plano da suíte de integração já documentam. Corrigida
para perguntar se alguma partição com linhas está sendo varrida, o veredito virou
o correto: atendido por índice, nos dois volumes e nos dois statements.

**Ordenação impossível na série de ingestão.** A primeira série dizia que um lote
de cinquenta custava cinco vezes mais por evento do que um lote de duzentos, e
que a forma por lote era mais lenta que a por evento no mesmo tamanho. As duas
coisas são impossíveis para um caminho linear no número de eventos. A causa era
disciplina de bancada: cada célula escreve milhares de linhas, e a célula que
roda enquanto o checkpointer descarrega a escrita da célula anterior carrega uma
cauda que pertence à máquina. Os braços de contenção deste mesmo projeto já
mantêm um `CHECKPOINT` entre células por essa razão, e a primeira versão desta
não mantinha. Uma medição que produz ordenação impossível está medindo a
bancada, e publicá-la seria pior do que não medir.

**Barreira que faltava.** O modo `delivery` escreve linhas no outbox endereçadas
ao rastreador de entrega, e linha de outbox não é inerte: um relay apontado para
aquele banco publica. Os outros modos escrevem linhas que ficam paradas, e por
isso a autorização existente (`--allow-trail-writes`) bastava para eles. Este
modo passou a recusar qualquer alvo que não seja o contêiner descartável, sem
flag que abra a porta.

## O que continua sem prova local

- **Aceite em percentil sob carga de produção.** As duas séries que o modo
  `delivery` produz são medidas nesta bancada, com PostgreSQL em contêiner e sem
  concorrência de produção. Elas dizem como o custo cresce e onde ele está; o
  número que vira aceite pertence ao gate de carga contra ambiente real.
- **TLS, assinatura e pipeline HTTP.** Ficam fora das duas medições por decisão:
  não crescem com volume nem com lote, e medi-los exige host e cliente.
- **Caos.** Nenhum artefato, runbook ou recibo. Declarado como pendência
  atribuída.
- **Infraestrutura declarativa.** WAF, allowlist de borda, `maxReceiveCount` das
  filas, bucket WORM, sender BR, Messaging Service, URL de callback pública e
  fonte de métricas operacionais pertencem à unidade I2 e não existem neste
  repositório.
- **Fonte de ativações de PIM.** A elevação acontece no provedor de identidade,
  fora deste hub.
- **Vetor de assinatura do próprio provedor.** O vetor fixo do SendGrid é
  independente deste repositório, não do provedor: ele prova o enquadramento dos
  bytes, o formato de chave, o hash e a codificação da assinatura contra uma
  implementação que não compartilha código com o verificador. Só o SendGrid pode
  fornecer um vetor que também prove que o esquema foi lido corretamente da
  documentação dele.
- **Twilio com retorno de entrega real.** `RequireDeliveryFeedback` reprova a
  configuração errada no start, mas nenhum teste local fala com a Twilio.

## Migração

`AddDeliverySuppressionReportState` foi aplicada pelas suítes de integração, que
executam `Database.MigrateAsync` contra PostgreSQL 17 em contêiner, incluindo o
índice de expressão criado por SQL. `AddNotificationAdmittedPlan` já vinha sendo
aplicada pelo mesmo caminho.
