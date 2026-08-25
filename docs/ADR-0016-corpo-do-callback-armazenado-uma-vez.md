---
language: pt-BR
---

# ADR-0016: Corpo do callback armazenado uma vez e referenciado pela evidência

| | |
|---|---|
| **Status** | Aceita |
| **Data** | 2026-08-25 |
| **Decisores** | Arquitetura, Engenharia de Plataforma |
| **Consultados** | Compliance (superfície de divulgação), SRE |
| **Relacionadas** | ADR-0006 (auditoria append-only), ADR-0014 (confirmação de entrega) |
| **Documento-mãe** | Design de Sistema, §4.3, §6, §11.3; Fase 2: decomposição em fatias, F2-2 |

## Contexto e problema

A rota de callback de provedor é a única rota pública do hub, e quem decide o ritmo e o tamanho do lote é o provedor. O handler selava o corpo verificado uma vez e o escritor gravava esse mesmo envelope na coluna `payload_enc` de **cada** linha de `delivery_event` do lote.

O corpo de um callback cresce com a quantidade de eventos que ele carrega. Um lote de N eventos gravava N cópias de um corpo de tamanho proporcional a N: escrita quadrática em N, com o corpo como termo dominante. A cerca de 420 bytes por evento, isso dá cerca de 1 MiB num lote de cinquenta, 16 MiB em duzentos e 100 MiB em quinhentos, por requisição.

A amplificação também estava na leitura. `DeliveryEventMessageProcessor` carrega a entidade inteira por mensagem de fila, e o corpo vinha junto. Era leitura gratuita: `RebuildEvent` não usa o payload, e nenhum caminho de produção deste repositório descriptografa essa coluna.

**A remediação da fase 2 já tinha achado a propriedade e declarado o que faltava.** Ela introduziu `ProviderWebhookIngestionOptions.MaxEventsPerCallback`, hoje em 200, com recusa inteira e `413` acima do teto, e registrou o valor como julgamento explícito. O comentário daquele teto diz, com todas as letras, que o que removeria a questão em vez de limitá-la é guardar o corpo uma vez e referenciá-lo das linhas de evento, e que isso é mudança do modelo de evidência, fora do escopo daquela rodada. Esta ADR toma essa decisão.

A propriedade que o desenho anterior protegia é legítima e não está em disputa: **a evidência de um evento do lote é o lote**. O que estava em disputa é a implementação dela por replicação.

## Fatores de decisão

- **A evidência de um evento continua sendo o lote inteiro que o carregou**, e não um recorte reconstruído por este hub.
- **A rota pública não pode ter custo quadrático numa variável escolhida por quem chama.**
- **Minimização de dado pessoal.** O corpo bruto é o único lugar em repouso onde o destino aparece em claro, ainda que selado. Guardá-lo N vezes multiplica por N a superfície do vazamento que a cifra de envelope existe para conter.
- **Retenção por descarte de partição**, que é o que a torna barata neste schema.
- **Sem chave estrangeira física neste módulo**, coerente com o que já vale para `attempt_id` e `notification_id`.

## Opções consideradas

1. **Guardar o corpo selado uma vez, em linha própria de `delivery_payload`, referenciada por `delivery_event`** (escolhida).
2. Manter a replicação e viver com o teto de eventos como única resposta.
3. Guardar o corpo fora do banco, em objeto S3 sob a mesma governança WORM da trilha.
4. Selar por evento, cada linha guardando apenas o recorte do próprio evento.

## Decisão

### 1. A tabela `delivery_payload`

Tabela-mãe particionada por mês em `received_at`, nos mesmos limites de `delivery_event`:

```sql
CREATE TABLE notifications.delivery_payload (
    id           uuid NOT NULL,
    received_at  timestamp with time zone NOT NULL,
    provider_key character varying(50) NOT NULL,
    source       character varying(20) NOT NULL,
    payload_enc  bytea NOT NULL,
    CONSTRAINT "PK_delivery_payload" PRIMARY KEY (id, received_at)
) PARTITION BY RANGE (received_at);
```

`source` distingue `webhook` de `reconciliation`. A distinção já existia de fato e era indecifrável a partir da linha: no callback os bytes são o corpo assinado pelo provedor, na reconciliação são o evento canônico serializado por este hub. São coisas diferentes sob um nome só, e quem periciar a evidência anos depois precisa saber qual está lendo.

`delivery_event` perde `payload_enc` e ganha `payload_id uuid NOT NULL`. A junção é `ON e.payload_id = p.id AND e.received_at = p.received_at`, que poda partição dos dois lados. A referência é lógica, sem chave estrangeira física, pela mesma razão que as outras deste módulo e por uma a mais: uma FK para tabela particionada impediria o descarte da partição referenciada.

### 2. O instante da recepção é do callback, não do evento

O escritor deixa de carimbar `now` por evento e passa a carimbar uma vez por callback, propagando o mesmo instante a todos os eventos dele. Isso é mais fiel ao que a coluna já dizia significar, e torna estrutural o alinhamento: todo evento de um lote cai na mesma partição do payload que o carregou, sem depender de sorte na virada de mês.

### 3. A ordem de escrita é o que preserva a propriedade

Por evento, em uma transação ou nenhuma: reivindicar a identidade em `provider_event_dedupe`; gravar os bytes em `delivery_payload` **apenas se este é o primeiro evento que reivindica**; inserir a linha de `delivery_event` referenciando `payload_id`; acrescentar a mensagem de outbox; confirmar.

O selo continua sendo um por callback. O que passa a ser compartilhado é uma alça, criada pelo handler e entregue a cada chamada do escritor, marcada como gravada **depois** do commit e nunca antes.

A propriedade da evidência passa a ser: **toda linha de `delivery_event` referencia uma linha de payload que guarda os bytes exatos do callback que carregou aquele evento, gravada na mesma transação do evento ou em transação anterior do mesmo callback.** Ela é garantida pela ordem, não por transação compartilhada, e é isso que permite manter uma transação por evento.

Três consequências dessa ordem, todas desejadas:

- Um lote inteiramente reentregue não reivindica nada e não grava nada, nem evento nem payload. Gravar o payload antes do laço seria mais simples e deixaria lixo a cada reentrega.
- Se o processo morrer no meio do lote, a retentativa sela de novo e os eventos restantes apontam para uma segunda linha de payload, com o mesmo texto claro. Duas linhas, um lote, e a invariante continua verdadeira para cada evento.
- O texto cifrado trafega uma vez por callback, não uma vez por evento.

### 4. Um teto de bytes, e o teto de eventos permanece

`MaxBodyBytes` existia duplicado, com o mesmo valor, em `ProviderWebhookIngestionOptions` e em `ProviderSignatureOptions`, aplicado em duas camadas da mesma rota. Fica um: as options de ingestão são donas do valor, e o esquema de assinatura passa a lê-lo de lá.

**`MaxEventsPerCallback` permanece, com a justificativa corrigida.** O argumento de armazenamento quadrático que fixou o valor em 200 deixa de valer, mas o teto não era só isso: cada evento custa uma transação, então sem teto o número de transações de uma requisição é escolhido por quem chama. A medição confirma: um lote de 500 eventos leva cerca de 700 ms a 800 ms, e um de 200 leva cerca de 270 ms a 455 ms. O valor certo pertence ao gate de carga com corpos reais, não a esta ADR; o que ela fixa é que a razão do teto passou a ser latência e não volume.

### 5. Sem backfill

`payload_id` entra `NOT NULL` sem default. Uma tabela que já guarde evidência recusa a migração em vez de inventar um callback para linhas cujos bytes não sabe atribuir. É projeto novo e não há dado a preservar; a recusa é alta e explícita em vez de silenciosa.

## Consequências

**A linha de `delivery_event` deixa de ser autocontida.** Ler a evidência bruta passa a exigir uma junção. É o preço direto, e vale porque nenhum caminho de produção faz essa leitura.

**A superfície de divulgação não muda.** `NotificationEvidenceReader` projeta colunas explicitamente e nunca selecionou o corpo; `DeliveryEventEvidence` não tem membro para ele, por decisão registrada no próprio contrato. A rota de reconstrução que o Compliance lê não sabia que a coluna existia, e continua não sabendo.

**O consumidor assíncrono para de reler o corpo**, sem mudança de comportamento, porque já não o usava.

**Uma tabela particionada a mais**, provisionada pelo mesmo agendador e com verificação de integridade própria (`notifications-delivery-payload-partitions`), pela mesma razão que `delivery_event` tem a sua.

**A ADR-0006 acolhe a tabela nova** na lista de tabelas governadas append-only. `delivery_payload` nasce estritamente imutável: nada, em nenhum caminho, reescreve um payload gravado.

**Retenções independentes passam a ser possíveis.** O alinhamento de partição permite descartar o corpo bruto antes da evidência estruturada, que é o que a minimização quer. As janelas e o mecanismo de descarte pertencem à fatia que implementar retenção, e nada aqui as antecipa: hoje nada descarta partição alguma, e esta decisão não muda isso.

## Opções descartadas

### Opção 2: viver com o teto de eventos

- Prós: nenhuma mudança de esquema nem de modelo de evidência.
- Contras: não corrige, apenas escolhe onde o custo quadrático para de crescer. Um teto de 200 ainda admitia cerca de 16 MiB por requisição, e o teto teria de ser recalculado toda vez que o formato de um provedor mudasse de tamanho. A própria remediação que o introduziu o declarou paliativo.

### Opção 3: corpo em S3 com governança WORM

- Prós: tira o volume do banco e coloca a evidência bruta sob o mesmo Object Lock da trilha.
- Contras: põe rede a serviço externo dentro da transação da rota mais sensível a latência do hub, ou quebra a atomicidade entre a evidência e o objeto. Continua sendo a evolução natural quando a retenção do corpo justificar, e a linha de payload é exatamente o ponto onde essa indireção entraria depois.

### Opção 4: selar por evento

- Prós: linha autocontida e escrita linear, sem tabela nova.
- Contras: troca a evidência pelo resumo. O que o provedor assinou foi o lote inteiro, e um recorte por evento é reconstrução deste hub sobre bytes que ele não assinou: a assinatura deixa de ser verificável contra o que está guardado, que é o ponto de guardar. E paga uma chamada de cifra por evento, sendo a cifra o passo mais caro da requisição.

## Como saberemos que foi a decisão certa

- A amplificação medida pelo modo `delivery` da sonda, que é o WAL por callback dividido pelo corpo que chegou, fica estável com o tamanho do lote em vez de acompanhá-lo. **Medido em 2026-08-25**: 4,2x em lote de 1, 3,7x em 10, 3,7x em 50, 3,6x a 4,6x em 200 e 3,7x em 500. É o coeficiente, e não o fator de redução, que prova que o termo do corpo saiu.
- O custo por evento também fica estável, entre cerca de 1,3 ms e 2,5 ms de lote 1 a lote 500, que é o que sustenta o teto de eventos ser questão de latência e não de volume.
- A resposta da rota de reconstrução do Compliance é idêntica antes e depois. Se mudar, a mudança vazou para uma superfície de divulgação.
- Um lote inteiramente reentregue continua não gravando linha alguma, payload incluído.
- Se em seis meses alguém precisar do corpo bruto num caminho de produção, a projeção que hoje o omite estava errada e o contrato de divulgação pede revisão, não uma leitura direta da coluna.

## Referências

- Design de Sistema, §4.3 (rastreamento de entrega), §6 (esquema e unicidade fora das tabelas particionadas), §11.3 (o endpoint de webhook faz apenas validar, inserir e enfileirar).
- ADR-0006: lista de tabelas governadas append-only.
- ADR-0014: confirmação de entrega, e por que o feedback é aplicado fora da requisição.
- Remediação das revisões da fase 2, que fixou o teto de eventos e deixou o modelo de evidência declarado como pergunta aberta.
