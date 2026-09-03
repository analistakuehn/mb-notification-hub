## Parecer do `dotnet-engineer`

### Fatos observados

- A solução brownfield usa .NET 10, Minimal APIs, EF Core/PostgreSQL, Kafka, SQS, Redis, S3, KMS e JWT em monólito modular. O dialeto declarado usa `Result`, FluentValidation, xUnit, NSubstitute, Shouldly, NetArchTest e `WebApplicationFactory` (`.araia/stack-profile.yaml:4-32`; `.araia/runs/SPECIFY-20260831T031955Z/artifacts/docs/SPEC-001/analyses/.staging/01-solution-inspection.md:10-28`).
- Dependências entre módulos só podem alcançar `Integration.V1`; domínio deve permanecer livre de tecnologia, e infraestrutura e host worker não podem depender de módulos (`tests/Platform.ArchTests/ArchitectureTests.cs:23-42`, `:66-96`, `:137-159`, `:201-245`).
- `Notifications` possui ingestão, idempotência, pipeline, tentativa, fallback e estado de entrega. `Dispatch` possui apenas contratos e adaptadores de provedores (`src/Platform.Api/Modules/Notifications/AGENTS.md:9-43`; `src/Platform.Api/Modules/Dispatch/AGENTS.md:5-25`, `:39-72`).
- O comando, o hash canônico, `EmailMessage` e a chamada SendGrid não representam anexos (`RequestNotification.Command.cs:7-36`; `RequestNotification.PayloadHash.cs:13-86`; `RenderedMessage.cs:23-27`; `SendGridMailRequest.cs:10-15`).
- A ingestão aceita uma notificação com notificação, registro idempotente, outbox e auditoria na mesma transação (`src/Platform.Api/Modules/Notifications/AGENTS.md:68-77`; `RequestNotification.Handler.cs:188-230`). O pipeline também sela transição, tentativa, outbox, auditoria e deduplicação em uma transação (`PipelineCommitWriter.cs:15-22`, `:42-88`).
- Mensagens internas usam claim check com identificadores, sem conteúdo (`DispatchMessages.cs:8-19`, `:22-37`). O despacho usa claim otimista por estado, e um envio ao provedor não é repetido após claim ou veredito (`AttemptDispatchWriter.cs:29-37`, `:86-104`; `DispatchMessageProcessor.cs:38-45`, `:119-140`).
- O conteúdo renderizado da tentativa é aberto somente em memória e hoje contém apenas a mensagem do canal (`StoredAttemptContent.cs:7-17`, `:22-49`). `DispatchRequest` é o seam publicado entre `Notifications` e `Dispatch` (`DispatchRequest.cs:11-42`).
- O dialeto de testes observado combina testes unitários de funções puras, integração com `WebApplicationFactory`, PostgreSQL, Redis, LocalStack e Kafka, além de testes contratuais do adaptador contra servidor HTTP falso (`RequestPayloadHashTests.cs:22-117`; `tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj:17-35`; `SendGridProviderContractTests.cs:14-117`).

### Decisões já aceitas

- `AttachmentManagement` possui custódia, validação, referência opaca e ciclo de vida (`docs/SPEC-001/specification.md:133-136`).
- O produtor usa upload gerenciado, aguarda liberação e envia somente referências opacas. S3 arbitrário do cliente e conteúdo inline ficam fora do escopo (`docs/prd-attachment-management.md:97-107`).
- O aceite exige conjunto integralmente liberado, íntegro, pertencente à mesma `application` e usado por principal autorizado (`docs/prd-attachment-management.md:137-144`).
- A primeira produção entrega anexos somente por e-mail. Um fallback incompatível termina explicitamente, sem remover arquivos nem convertê-los em links (`docs/prd-attachment-management.md:146-154`).
- Bytes, localização S3 e capacidades de upload não podem trafegar em Kafka, SQS, outbox, dead-letter, eventos ou logs (`docs/prd-attachment-management.md:156-164`).
- Solicitações sem anexos devem preservar REST, Kafka, idempotência, recusas, eventos e seleção de canal (`docs/prd-attachment-management.md:165-170`).
- Migrações devem ser aditivas, sem apagar objetos ou claims em reversão lógica (`02-architecture-discovery.md:52-59`).

### Direção de implementação recomendada

`AttachmentManagement` deve persistir a autoridade sobre identidade opaca, vínculo com `application`, identidade íntegra dos bytes, validação, liberação, revogação, validade, dependências ativas e elegibilidade de descarte. `Notifications` deve persistir somente o manifesto imutável necessário para idempotência, tentativa e evidência. `Dispatch` não deve conhecer bucket, chave, URL, credencial, estado ou persistência de `AttachmentManagement`.

Modelo conceitual, sem impor enum, tabela ou schema:

1. Após registro ou recebimento, a referência permanece inutilizável.
2. Validação conclusiva e íntegra pode liberar; rejeição, indisponibilidade ou conclusão inconclusiva mantêm o gate fechado.
3. Uma liberação pode vencer ou ser revogada, mas seu conteúdo permanece preservado enquanto houver dependência ativa.
4. Claim associa idempotentemente o conjunto inteiro à notificação. Claim parcial não autoriza aceite.
5. Antes de cada tentativa, `Notifications` verifica a validade da liberação e obtém o conteúdo pelo contrato publicado.
6. O conteúdo lido deve conferir com a identidade íntegra do manifesto aceito.
7. O adaptador recebe uma representação neutra de armazenamento e submete o conjunto inteiro uma única vez.
8. Descarte só ocorre após ausência comprovada de dependência ativa; falhas deixam estado recuperável ou descarte conhecido.

Para a ingestão, a coleção de referências e propriedades de entrega deve ser opcional. Ausência deve seguir exatamente o caminho e o hash atuais. Em requisição com anexos, o hash deve ser calculável sem consultar o estado atual do anexo. Assim, replay aceito continua sendo resolvido antes de reavaliar liberação, como exige `docs/prd-attachment-management.md:141-144`. Uma solicitação nova deve obter claim válido imediatamente antes do aceite durável.

### Requisitos de engenharia candidatos

| ID | Requisito candidato | Critério verificável |
|---|---|---|
| `ER-001` | Isolar `AttachmentManagement` como módulo proprietário e publicar somente contratos versionados. | Fitness functions comprovam que módulos irmãos alcançam apenas `AttachmentManagement.Integration.V1`; domínio não depende de EF, AWS ou transporte; `Dispatch` não depende do novo módulo. |
| `ER-002` | Expor registro, upload gerenciado e consulta por referência opaca, sem revelar armazenamento. | Respostas, OpenAPI, erros e logs não contêm bucket, chave, URL reutilizável ou credencial; referência de outra aplicação não revela existência nem estado. |
| `ER-003` | Manter liberação fail-closed. | Conteúdo rejeitado, inconclusivo, não íntegro ou não inspecionável nunca produz referência utilizável nem chamada ao provedor. |
| `ER-004` | Aplicar autorização por `application` em registro, acompanhamento, claim e uso. | Toda combinação cruzada entre duas aplicações é recusada; a matriz de isolamento termina com zero violações, conforme `MET-006` (`docs/prd-attachment-management.md:214`). |
| `ER-005` | Tornar claim e aceite do conjunto atomicamente consistentes no efeito observável. | Falha injetada entre claim, notificação, idempotência, outbox, auditoria e commit nunca deixa notificação aceita sem claim integral; claims órfãos convergem por compensação ou reconciliação. |
| `ER-006` | Incorporar referências e propriedades relevantes ao hash canônico. | Mesmo conjunto e propriedades produzem replay; qualquer diferença relevante produz conflito e nenhum efeito; ausência de anexos produz exatamente os hashes dourados atuais. |
| `ER-007` | Manter bytes e capacidades fora dos transportes. | Varredura de Kafka, SQS, outbox, dead-letter, eventos, logs e auditoria comum encontra zero bytes, base64, localização S3 ou capacidade de upload, conforme `MET-004` (`docs/prd-attachment-management.md:212`). |
| `ER-008` | Congelar na notificação ou tentativa o manifesto aceito e sua identidade íntegra. | Alteração posterior de metadado ou objeto não muda silenciosamente a tentativa; a evidência relaciona referência, identidade validada e conteúdo submetido. |
| `ER-009` | Revalidar liberação e envelope antes de cada chamada ao provedor. | Liberação vencida ou revogada, conjunto incompleto ou envelope excedido produz resultado explícito antes de qualquer requisição SendGrid. |
| `ER-010` | Evoluir o contrato de `Dispatch` com anexos neutros ao provedor e ao armazenamento. | `Dispatch` recebe todos os elementos necessários à chamada, sem tipos AWS ou internos; SendGrid envia conteúdo, nome e tipo liberados sem remover ou reordenar semanticamente o conjunto. |
| `ER-011` | Preservar envio único por tentativa sob concorrência e redelivery. | Claims concorrentes para o mesmo attempt resultam em no máximo uma chamada; falha após chamada sem veredito estaciona em estado incerto e não reenvia cegamente. |
| `ER-012` | Proteger dependências ativas contra descarte e recuperar falhas parciais. | Varredura de abandonados nunca remove anexo reclamado por notificação ativa; falhas entre upload, validação, claim e liberação convergem para estado recuperável ou descarte conhecido. |
| `ER-013` | Fazer evolução e rollback aditivos. | Rollout desabilitado continua aceitando solicitações sem anexos e continua processando anexos já aceitos; reversão lógica não remove dados nem torna tentativa aceita ilegível. |
| `ER-014` | Produzir evidência minimizada e reconstruível. | Todas as tentativas aceitas pelo provedor relacionam manifesto, integridade, validação e submissão, conforme `MET-005`; nenhuma superfície comum contém conteúdo bruto. |

### Sementes de implementação

| Semente | Resultado | Requisitos | Dependências | Onda | Dono técnico |
|---|---|---|---|---|---|
| `IM-01` | Módulo e contratos básicos de identidade opaca, autorização e estado observável | `ER-001`, `ER-002`, `ER-004` | Decisão do contrato externo | 1 | `AttachmentManagement` |
| `IM-02` | Upload sob custódia do hub com integridade verificável | `ER-002`, `ER-003` | `IM-01`; decisões S3/KMS/IAM | 1 | `AttachmentManagement`, adaptador S3 |
| `IM-03` | Validação, liberação e rejeição fail-closed com repetição segura | `ER-003`, `ER-012` | `IM-02`; decisão do mecanismo de validação | 2 | `AttachmentManagement`, adaptador de validação |
| `IM-04` | Contrato publicado de manifesto e claim integral | `ER-001`, `ER-004`, `ER-005`, `ER-008` | `IM-03`; decisão de consistência | 3 | `AttachmentManagement.Integration`, `Notifications` |
| `IM-05` | Ingresso REST e Kafka com referências opcionais e hash compatível | `ER-006`, `ER-007`, `ER-013` | `IM-04`; decisão V1 ou coexistência | 3 | `Notifications/Ingress`, adaptadores REST e Kafka |
| `IM-06` | Snapshot imutável do conjunto no ciclo de tentativa e mensagens internas somente por referência | `ER-007`, `ER-008` | `IM-04`, `IM-05` | 4 | `Notifications/Pipeline` e persistência do módulo |
| `IM-07` | Verificação de validade, integridade e envelope antes da chamada | `ER-009`, `ER-011` | `IM-06`; limites aprovados | 4 | `Notifications/Dispatching`, contrato de `AttachmentManagement` |
| `IM-08` | Contrato neutro de envio e submissão integral no SendGrid | `ER-010`, `ER-011`, `ER-014` | `IM-06`, `IM-07`; decisão buffer ou streaming | 4 | `Dispatch.Integration`, adaptador SendGrid |
| `IM-09` | Falha explícita e fallback sem degradação do manifesto | `ER-009`, `ER-011`, `ER-013` | `IM-07`, `IM-08`; decisão de roteamento | 4 | `Notifications` |
| `IM-10` | Reconciliação, proteção contra descarte e limpeza de abandonados | `ER-012`, `ER-013` | `IM-04`, `IM-09`; regra de retenção | 5 | `AttachmentManagement`, workers de manutenção |
| `IM-11` | Evidência operacional minimizada e métricas do ciclo completo | `ER-007`, `ER-014` | `IM-03`, `IM-08`, `IM-10` | 5 | `AttachmentManagement`, `Notifications`, contrato publicado de `Audit` |
| `IM-12` | Migrações aditivas, controles de rollout e baseline de compatibilidade | `ER-006`, `ER-013` | Evolui junto das sementes anteriores | 1 a 5 | Persistência de cada módulo e composição da plataforma |

### Matriz de verificação

| Camada | Verificações obrigatórias | Resultado mensurável |
|---|---|---|
| Unitária | Transições do ciclo de vida; liberação fail-closed; canonicalização do manifesto; ordem e duplicatas após decisão contratual; hash dourado sem anexos; replay e conflito; cálculo de envelope; mapeamento integral SendGrid | Todos os casos positivos e negativos exercitam a regra e preservam os vetores dourados atuais |
| Integração | S3/LocalStack, PostgreSQL e concorrência; upload, validação, liberação, claim, tentativa e descarte; substituição de objeto após validação; consulta autorizada; falhas entre cada persistência | Nenhuma notificação aceita sem claim; nenhum anexo ativo descartado; nenhum estado utilizável sem validação |
| Contrato | REST, Kafka, OpenAPI, schema, dead-letter, motivos de recusa, eventos, contrato `AttachmentManagement.Integration` e contrato de `Dispatch`; clientes sem o novo membro | Consumidores antigos continuam desserializando ou V1 e V2 coexistem; ausência do membro mantém as respostas vigentes |
| Arquitetura | Dependências entre módulos, domínio livre de tecnologia, host e infraestrutura sem dependência reversa, `Dispatch` sem S3 ou persistência de anexos | Todas as fitness functions passam; nenhuma dependência fora da superfície publicada |
| Segurança | Matriz entre aplicações, enumeração de referências, troca de conteúdo, tipo divergente, metadado hostil, conteúdo protegido, liberação revogada e varredura de vazamento | Zero violações de gate, isolamento e exposição, conforme `MET-002`, `MET-004` e `MET-006` |
| Resiliência | Indisponibilidade de S3, KMS, validação e provedor; timeout após upload; crash após claim; crash antes e depois da chamada; redelivery e reconciliação de órfãos | Nenhum avanço incorreto, duplicação ou degradação; cada falha converge para estado recuperável, explícito ou descarte conhecido |
| Compatibilidade | Suíte vigente REST e Kafka sem anexos; hashes dourados; ordem de recusas; eventos; seleção de canal; rollout e rollback | Zero divergências no baseline sem anexos, conforme `MET-007` (`docs/prd-attachment-management.md:215`) |

### Validações que bloqueiam escolhas

1. **Consistência do claim**: implementar uma prova com falhas em cada ponto entre reserva, aceite, outbox e commit para comparar transação compartilhada contra reserva idempotente com compensação. Escolher somente a alternativa que impeça aceite sem claim e demonstre convergência de órfãos.
2. **Compatibilidade V1 ou V2**: executar contract tests contra serializadores, schemas Kafka, OpenAPI e consumidores existentes. Se qualquer consumidor não tolerar membro opcional, manter versões coexistentes.
3. **Semântica canônica do conjunto**: aprovar, por corpus de contrato, o significado de ordem, duplicatas, nome de exibição, tipo e propriedades que alteram a entrega. Somente depois congelar vetores dourados do hash.
4. **Proteção do objeto**: provar em S3 que troca, sobrescrita ou leitura de versão diferente após validação é detectada ou impossível. A prova deve preceder a escolha de versionamento, IAM e KMS.
5. **Validação de segurança**: avaliar mecanismo e indisponibilidade com arquivos hostis, protegidos, inconclusivos e de tipo divergente. O experimento não pode presumir provedor, tipos permitidos nem validade da liberação.
6. **Envelope e transferência**: medir, no envelope máximo aprovado, memória, latência, cancelamento, falha parcial e igualdade byte a byte para buffer e streaming. Sem limites de tamanho e quantidade, não há evidência para escolher.
7. **Autorização REST por aplicação**: demonstrar que os claims atuais vinculam o principal à `application`. A evidência disponível confirma autorização por classe, mas não prova esse vínculo (`02-architecture-discovery.md:63-65`).
8. **Retenção e descarte**: obter regra sustentada de produto, privacidade ou compliance e validar uma simulação com notificação ativa, terminal, retry, fallback e investigação. Não selecionar período por conveniência técnica.
9. **Roteamento de notificação com anexos**: validar com Produto se o plano deve ser recusado quando o primeiro canal elegível não for e-mail, restringido a e-mail ou transformado de outra forma. O produtor não escolhe o canal hoje (`docs/guia-integracao-produtor.md:333-344`), e a direção aceita não autoriza Engenharia a sobrescrever a política.
10. **Metas operacionais**: medir throughput, latência, memória e backlog com carga representativa antes de decidir topologia de workers e capacidade. A evidência atual não define metas quantitativas (`02-architecture-discovery.md:74-78`).

### Dissensos e trade-offs a preservar

- Transação compartilhada simplifica atomicidade, mas aumenta acoplamento entre módulos. Reserva e compensação preservam autonomia, mas exigem reconciliação comprovada.
- Campo opcional em V1 reduz superfície operacional, mas só é válido se todos os consumidores tolerarem a adição. V2 reduz risco contratual e exige coexistência; `Dispatch.Integration.V2` também exige evoluir a fitness function hoje fixada em `Integration.V1`.
- Buffer simplifica o adaptador e a comprovação do payload, mas pressiona memória. Streaming limita memória e aumenta a complexidade de hash, cancelamento e ciclo de vida.
- Snapshot no aceite estabiliza idempotência e investigação. Consulta ao estado vivo antes da tentativa continua necessária para revogação e validade, criando dependência de disponibilidade.
- Anexos dentro de `EmailMessage` mantêm o manifesto como parte do conteúdo final. Colocá-los em `DispatchRequest` reduz mudança na hierarquia renderizada, mas pode separar dados que precisam participar da mesma evidência. A escolha depende do modelo de hash e transferência.
- O padrão S3 do módulo `Audit` comprova disponibilidade do SDK, não autoriza reutilizar sua implementação nem acessar seu armazenamento (`01-solution-inspection.md:35`).

Validação realizada: inspeção estritamente somente leitura. Nenhum build ou teste foi executado, e nenhum arquivo ou manifesto foi alterado.

`DOTNET_ENGINEER_SPECIFICATION: PASS`

Justificativa: há evidência suficiente para declarar exequibilidade, requisitos mensuráveis, seams, ondas, estratégia de verificação e riscos. As escolhas sem suporte foram isoladas como validações bloqueadoras, sem inventar schema, limites, retenção, scanner, tipos permitidos ou arquitetura final.
