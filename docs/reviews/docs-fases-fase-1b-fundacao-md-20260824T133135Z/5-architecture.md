# Arquitetura

[Voltar ao índice](00-index.md)

## ARC-001: infraestrutura da fase sem fatia de entrega

- `severity`: `HIGH`
- `confidence`: `HIGH`
- `lens`: `Architecture`
- `file`: `docs/fases/fase-1b-fundacao.md`
- `line`: `41`
- `evidence`: A linha 41 reconhece que Terraform completo pertence à fase 1b, mas exclui infraestrutura da decomposição B1 a B16. As pendências 9, 21, 28 e 32 confirmam que filas, tópicos, ACLs, identidades, deployments, S3 WORM e KMS permanecem sem unidade de entrega, embora sejam pré-requisitos de AWS pré-produção. O roadmap do design também inclui Terraform completo na fase 1b.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: O grafo da fase pode declarar o código concluído sem produzir um caminho implantável, seguro e operacionalmente verificável. Controles de durabilidade, autorização, auditoria e recuperação ficam fora dos critérios de conclusão.
- `recommendation`: Acrescentar fatias de infraestrutura com dependências, dono, rollback, permissões, custos e critérios falseáveis para filas, tópicos, ACLs, Entra ID, KMS, WORM, workloads e observabilidade.
- `verification`: Toda entrega Terraform do roadmap deve pertencer a uma fatia explícita. Cada pendência de infraestrutura deve estar resolvida ou vinculada a uma fatia bloqueante validada em pré-produção.
- `source-revision`: `3570c5e7ca761911330cace4b7dfdf1a8c5b2dbb`
- `reviewers`: `dotnet-architect`

## ARC-002: documento aceito depende de ADRs ainda propostas

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `Architecture`
- `file`: `docs/fases/fase-1b-fundacao.md`
- `line`: `81`
- `evidence`: O alvo tem estado `ACCEPTED` e afirma conformidade com ADRs aceitas. Na revisão fixada, ADR-0002, ADR-0003, ADR-0008, ADR-0010 e ADR-0012 têm status `Proposta`; somente ADR-0006 está aceita. Essas propostas sustentam decisões de mensageria, pipeline, idempotência e posse de ContactConsent.
- `evidence-kind`: `executed`
- `introduced-by-diff`: `false`
- `impact`: Decisões estruturais são consumidas como baseline aprovada sem que seus registros demonstrem aceitação. Mudanças futuras não conseguem determinar qual autoridade prevalece nem aplicar a governança de reversão.
- `recommendation`: Formalizar a aceitação de cada ADR estrutural ou alterar o documento para declarar que incorpora propostas condicionais, impedindo seu próprio estado aceito enquanto a condição persistir.
- `verification`: Os status dos ADRs e a qualificação usada na linha 81 devem coincidir, com uma fonte de aceitação verificável para cada decisão incorporada.
- `source-revision`: `3570c5e7ca761911330cace4b7dfdf1a8c5b2dbb`
- `reviewers`: `dotnet-architect`

## ARC-003: catálogo de saída anuncia evento não publicado

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `Architecture`
- `file`: `docs/fases/fase-1b-fundacao.md`
- `line`: `100`
- `evidence`: A linha 100 lista `contact_suppressed` entre os eventos publicados em `notifications.events.v1`. A pendência 55, na linha 299, registra como redação correta que a supressão é detectada e armazenada internamente, mas não anunciada. O design fixado mantém a mesma ressalva e os contratos implementados não expõem esse evento de saída.
- `evidence-kind`: `executed`
- `introduced-by-diff`: `false`
- `impact`: Produtores podem implementar consumo e automação para um evento que a fase não emite, deixando sincronização de supressão silenciosamente incompleta.
- `recommendation`: Remover `contact_suppressed` do catálogo publicado da fase ou marcá-lo no mesmo ponto como evento posterior, sem contrato disponível na 1b.
- `verification`: A lista de eventos da linha 100, o design, os tipos C# publicados e os testes de emissão devem apresentar exatamente o mesmo conjunto.
- `source-revision`: `3570c5e7ca761911330cace4b7dfdf1a8c5b2dbb`
- `reviewers`: `dotnet-specialist`

## ARC-004: entrada de contatos promete device tokens fora do contrato

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `Architecture`
- `file`: `docs/fases/fase-1b-fundacao.md`
- `line`: `101`
- `evidence`: A linha 101 afirma que `contacts.events.v1` transporta contatos, consentimentos e device tokens. O contrato e o aplicador fixados em `Modules.ContactConsent.Integration.V1` modelam declarações de contato e consentimento. Os testes de ingestão rejeitam tipos não suportados, enquanto o cadastro de dispositivos ocorre pela escrita REST em `/v1/recipients/{recipientId}/devices`.
- `evidence-kind`: `executed`
- `introduced-by-diff`: `false`
- `impact`: O produtor do cadastro pode emitir um evento de dispositivo que o consumer recusa e envia para dead letter, apesar de o documento declarar suporte.
- `recommendation`: Corrigir a linha 101 para limitar a entrada aos tipos implementados ou entregar um contrato versionado de device token com aplicador, deduplicação, dead letter e testes de compatibilidade.
- `verification`: Publicar cada tipo declarado em `contacts.events.v1` contra o consumer fixado. Todos os tipos documentados devem ser aceitos e materializados; qualquer tipo não documentado deve ser recusado de forma estável.
- `source-revision`: `3570c5e7ca761911330cace4b7dfdf1a8c5b2dbb`
- `reviewers`: `dotnet-specialist`

## ARC-005: B14 declara oito respostas que a fase deliberadamente não fornece

- `severity`: `HIGH`
- `confidence`: `HIGH`
- `lens`: `Architecture`
- `file`: `docs/fases/fase-1b-fundacao.md`
- `line`: `166`
- `evidence`: B14, na linha 166, e o critério de saída da linha 227 afirmam que `/v1/audit/*` responde às oito perguntas do design. O design fixado declara que a pergunta 7 não é respondida na fase: `deliveryEvents` não existe, `deliveredAt` permanece ausente e `status`, `sentAt` e `providerMessageId` comprovam somente aceitação pelo provedor, nunca entrega. Testes de forma da resposta preservam essa omissão deliberada.
- `evidence-kind`: `executed`
- `introduced-by-diff`: `false`
- `impact`: Auditores e critérios de aceite podem interpretar aceitação do provedor como comprovação de entrega ao destinatário, ampliando indevidamente o valor probatório da API.
- `recommendation`: Redefinir B14 e o critério de saída como sete respostas mais uma lacuna declarada, ou entregar os eventos e carimbos necessários antes de afirmar as oito respostas.
- `verification`: Mapear cada pergunta a campos e fontes concretas da resposta. A pergunta sobre entrega só pode ser marcada como respondida quando houver evidência de entrega, não apenas de aceitação pelo provedor.
- `source-revision`: `3570c5e7ca761911330cace4b7dfdf1a8c5b2dbb`
- `reviewers`: `dotnet-specialist`
