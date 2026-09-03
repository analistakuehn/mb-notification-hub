RESULT: INCONCLUSIVE  
MECHANISM: as restrições brownfield e os invariantes de segurança estão confirmados, mas a estratégia de transferência, o envelope efetivo e os limites operacionais não possuem medição suficiente para escolher entre buffer, streaming ou spool.  
CONFIDENCE: ALTA para fatos locais e intenções aceitas; MÉDIA para as recomendações candidatas.  
FIX_BRIEF: `dotnet-architect` deve decidir os itens do registro de decisões. `dotnet-engineer` deve implementar somente após a definição dos contratos, limites e experimentos.  
VALIDATION: matriz de falhas injetadas, testes de isolamento, captura do payload SendGrid, varredura de vazamento e sonda de carga com métricas de runtime.  
RESIDUAL_UNCERTAINTY: limites de tamanho e quantidade, orçamento de memória e concorrência, scanner, validade da liberação, envelope contratado, retenção, semântica de revogação durante envio e tratamento de submissão ambígua.

## Síntese para autoria

- **Fato local**: a solução usa .NET 10, Server GC, S3, KMS, Kafka, SQS e JWT. O SDK selecionado é `10.0.100` com avanço para minor mais recente, e o host local resolveu `10.0.302` (`global.json:3-5`, `src/Platform.Api/Platform.Api.csproj:4-8`, `.araia/stack-profile.yaml:23-31`).
- **Fato local**: a capacidade ainda não existe. Os contratos de ingresso, e-mail e SendGrid não representam anexos (`RequestNotification.Command.cs:7-36`, `RenderedMessage.cs:23-27`, `SendGridMailRequest.cs:10-15`).
- **Intenção aceita**: somente anexos liberados, íntegros, pertencentes à mesma `application` e autorizados podem ser aceitos. O conjunto completo deve ser submetido ou falhar explicitamente (`docs/prd-attachment-management.md:128-154`).
- **Fato local**: o REST atual autoriza por classe, mas não vincula o principal à `application` recebida no corpo (`RequestNotification.Endpoint.cs:45-54`, `ProducerAuthorization.cs:36-52`). O ingresso Kafka já avalia a tripla principal, `application` e classe (`ProducerAuthorization.cs:64-81`, `IProducerRegistry.cs:27-35`).
- **Fato local**: o padrão S3 existente do módulo Audit lê e escreve apenas por chave e conserva um SHA-256 como metadado, sem fixar uma versão imutável na identidade lida (`S3WormObjectStore.cs:22-37`, `:44-53`, `:61-80`). Ele comprova SDK e configuração, não resolve a identidade de anexos.
- **Fato local**: o uso atual de KMS assina um digest de evidência. Ele não demonstra proteção criptográfica de anexos (`KmsAttestationSigner.cs:7-12`, `:28-41`).
- **Recomendação**: a identidade interna liberada deve fixar uma geração imutável do objeto, digest criptográfico e comprimento. Chave S3 ou ETag isolados não devem servir como prova de identidade.
- **Fato local**: o registro Kafka tem limite local de 256 KB e não pode transportar anexos ou dados de contato (`docs/guia-integracao-produtor.md:210-216`).
- **Fato local**: `telemetry: none` está declarado no perfil (`.araia/stack-profile.yaml:30`). Existem filtros de logging e fitness functions contra dados pessoais em templates, mas não há telemetria específica nem varredura dinâmica de bytes, chaves S3, URLs ou capacidades de upload (`SecurityArchitectureTests.cs:97-118`, `:141-172`).

## Restrições candidatas de engenharia

| ID | Origem | Restrição candidata e critério verificável |
|---|---|---|
| `ER-001` | Recomendação sustentada por `PAC-003`, `PAC-013` | Cada liberação deve fixar referência opaca, geração imutável interna, SHA-256 e comprimento. Um teste que substitua o objeto sob a mesma chave não pode provocar chamada ao provedor; no caminho válido, o hash dos bytes decodificados do payload capturado deve igualar o digest liberado. |
| `ER-002` | Intenção aceita | A referência pública não pode conter bucket, chave, versão, URL assinada ou credencial. Enumeração e consulta cruzada devem produzir resposta indistinguível de referência inexistente e trilha segura (`docs/prd-attachment-management.md:176-182`, `:297`). |
| `ER-003` | Lacuna local confirmada | Registro, acompanhamento, uso, revogação e operação devem autorizar o principal para a `application` do recurso. Testes devem cobrir toda combinação principal A/B, aplicação A/B e referência A/B, com zero acesso cruzado. |
| `ER-004` | Recomendação de segurança | Indisponibilidade ou ausência de evidência atual de autorização deve fechar o gate. O cache atual preserva snapshot antigo após falha de atualização (`IProducerRegistry.cs:40-43`); a validade e a revogação desse snapshot exigem política própria antes de reutilização na capacidade. |
| `ER-005` | Intenção aceita | Nenhum estado pode avançar para `released` sem resultado final aprovado de integridade, tipo e antimalware. Scanner indisponível, inconclusivo, incapaz de inspecionar ou conteúdo protegido mantém o gate fechado e impede a chamada ao provedor (`docs/prd-attachment-management.md:128-135`, `:180-181`). |
| `ER-006` | Intenção aceita | O tipo real deve ser obtido do conteúdo e comparado ao tipo declarado. Metadados hostis e estruturas não inspecionáveis devem ser recusados. A suíte deve usar fixtures com assinatura divergente, metadado hostil, estrutura inválida e resultado inconclusivo (`docs/prd-attachment-management.md:133-135`, `:232`). |
| `ER-007` | Recomendação contra TOCTOU | O aceite deve reivindicar um snapshot liberado, e cada tentativa deve verificar estado e identidade imediatamente antes do ponto irreversível da submissão. Barreiras de teste devem intercalar liberação, claim, revogação, descarte e início da chamada, sem envio após estado inválido. |
| `ER-008` | Intenção aceita e lacuna de desenho | Aceite da notificação e claim do manifesto devem ser atômicos ou compensáveis com reconciliação determinística. Falhas injetadas entre claim, `notification`, idempotência, outbox, auditoria e commit não podem deixar notificação aceita sem claim nem claim órfão permanente. A transação atual confirma quatro escritas juntas (`Notifications/AGENTS.md:68-77`). |
| `ER-009` | Intenção aceita | Referências e propriedades que alteram entrega integram a forma canônica idempotente. Ausência de anexos deve preservar exatamente o hash atual. Golden tests devem comprovar o hash vigente e a matriz de ordem, duplicatas, replay e conflito (`RequestNotification.PayloadHash.cs:13-32`, `docs/prd-attachment-management.md:139-144`). |
| `ER-010` | Intenção aceita | O envelope deve ser calculado sobre o payload UTF-8 exato submetido ao provedor, incluindo corpo, metadados JSON e base64. Um conjunto acima do limite configurado deve falhar antes da chamada, com motivo estável e sem remoção de arquivo. Nenhum valor numérico está sustentado. |
| `ER-011` | Recomendação de integração | `Dispatch` deve receber uma representação neutra de provedor, sem chave S3 ou dependência de `AttachmentManagement`. O adaptador SendGrid deve produzir a forma de wire e provar, por captura e decodificação, nome, tipo, conjunto e igualdade byte a byte. A fronteira atual proíbe acesso ao armazenamento de outro contexto (`Dispatch/AGENTS.md:5-25`). |
| `ER-012` | Recomendação de runtime | Nenhuma implementação pode ser aprovada sem orçamento medido de heap, working set, CPU, I/O e concorrência. Cancelamento e falha não podem reter buffers, streams ou arquivos temporários. O host usa Server GC (`Platform.Api.csproj:7`), portanto concorrência e tamanho do envelope afetam diretamente a pressão de memória por instância. |
| `ER-013` | Restrição local | Kafka, SQS, outbox, dead-letter e eventos carregam somente referências opacas e estados mínimos. Bytes, base64, nome, chave S3, URL e capacidade de upload permanecem fora. A forma serializada completa deve passar pelo limite próprio de cada transporte; somente o limite Kafka de 256 KB está sustentado. |
| `ER-014` | Intenção aceita | Falhas de S3, KMS, scanner ou autorização não podem produzir liberação nem envio. Falha do provedor mantém o conjunto exato e nunca converte para link ou outro canal (`docs/prd-attachment-management.md:184-189`). |
| `ER-015` | Intenção aceita | Logs, erros, traces, métricas e auditoria comum não podem conter conteúdo, nome desnecessário, digest, localização ou capacidade de acesso. A verificação deve semear sentinelas e varrer todas as superfícies coletadas, atendendo `MET-004` (`docs/prd-attachment-management.md:209-215`). |
| `ER-016` | Intenção aceita | O caminho sem anexos deve preservar REST, Kafka, idempotência, recusas, eventos e seleção de canal. Contract tests devem executar o baseline antes e depois da mudança (`docs/prd-attachment-management.md:165-170`). |

## Experimento para buffer, streaming ou spool

A codificação base64 produz `4 × ceil(bytes/3)` caracteres antes do overhead JSON. Isso não define o limite do provedor, mas demonstra que o tamanho bruto não basta para o preflight.

Executar os três braços com o mesmo corpus e o mesmo fake HTTP capturando o corpo integral:

| Braço | Mecanismo avaliado | Risco principal |
|---|---|---|
| Buffer | S3 para `byte[]`, base64 completa e modelo JSON | Retenção simultânea de bytes, string base64 e objetos do serializador por envio concorrente |
| Streaming | S3 para hash/codificador base64 incremental e JSON de saída | Ciclo de vida, cancelamento, cálculo prévio do envelope e impossibilidade de descobrir divergência tarde demais |
| Spool | Materialização privada e protegida do corpo ou arquivo antes da chamada | Exposição local, quota de disco, limpeza após crash e custo adicional de I/O |

A matriz deve usar arquivos válidos nas classes pequena, intermediária e próxima ao máximo configurado, combinações próximas ao envelope total e concorrência unitária, sustentada e de pico. Os valores devem vir de orçamento aprovado, não deste parecer.

Métricas obrigatórias:

- tamanho bruto, caracteres base64 e bytes UTF-8 do payload final;
- working set, private bytes, GC heap, taxa de alocação, coleções por geração, tempo em GC e pausas;
- CPU de leitura, hash, base64 e serialização;
- latência para primeiro byte, duração total e percentis da tentativa;
- streams, conexões, fila do thread pool e envios simultâneos;
- bytes e duração de spool, quota ocupada e resíduos após cancelamento ou crash;
- latência e erros de S3, KMS e scanner;
- throughput concluído, backlog e taxa de falha;
- igualdade entre digest liberado e digest do conteúdo decodificado da requisição capturada.

Critério de decisão: escolher o braço que respeitar o orçamento aprovado de memória, CPU, latência e throughput, preservar igualdade byte a byte e limpar todos os recursos. Sem esses orçamentos, a escolha permanece inconclusiva. A infraestrutura de sonda existente já registra ambiente e relatório JSON (`tests/Platform.PerformanceTests/Program.cs:13-18`, `:86-113`, `:320-344`), mas precisa de um cenário específico para anexos.

## Matriz de modos de falha

| Falha injetada | Comportamento fail closed | Evidência verificável |
|---|---|---|
| Upload S3 interrompido ou objeto ausente | Não liberar; manter estado recuperável ou descarte conhecido | Estado final, ausência de claim e zero chamadas ao fake SendGrid |
| Objeto substituído após validação | Invalidar a operação; não ler por chave mutável nem enviar | Troca sob a mesma chave, geração divergente e zero chamadas |
| KMS negado, indisponível ou chave desabilitada | Não liberar, descriptografar nem enviar; sem fallback local fora de Development | Falha de KMS simulada, estado explícito e zero chamadas. O host já recusa chave local de desenvolvimento fora de Development (`Program.cs:64-75`) |
| Scanner indisponível ou inconclusivo | Permanecer pendente ou rejeitar conforme catálogo aprovado; nunca liberar | Resultado do fake scanner, transição registrada e zero chamadas |
| Scanner detecta conteúdo hostil | Rejeitar e impedir uso posterior | Repetição da referência continua recusada; nenhuma chamada |
| Revogação ou vencimento antes da tentativa | Falhar explicitamente antes do ponto de submissão | Barreira concorrente e contador de chamadas igual a zero |
| Falha entre claim e commit da notificação | Rollback total ou compensação idempotente | Falha em cada ponto, consulta de claims órfãos e reconciliação concluída |
| Envelope efetivo excedido | Rejeitar antes da chamada | Corpo contado pelo mesmo serializador, zero requisições ao fake |
| Timeout, rede, circuito aberto, 429 ou 5xx do provedor | Manter o mesmo snapshot e retornar resultado transitório, sem remover anexos | A forma atual já classifica esses resultados (`SendGridChannelProvider.cs:46-65`, `:101-121`); ampliar testes para conferir o manifesto |
| 4xx definitivo do provedor | Falha permanente explícita, conjunto preservado em evidência | Resposta capturada, nenhum fallback degradado |
| Cancelamento ou crash durante transferência | Fechar stream, liberar buffer e remover spool recuperável | Verificação de handles, arquivos temporários e memória após reinício |
| Falha de publicação em broker/outbox | Não perder aceite nem publicar conteúdo bruto | Reprocessamento idempotente e varredura das mensagens produzidas |

O rate limiter de `Dispatch` falha aberto quando Redis cai (`Dispatch/AGENTS.md:264-270`). Essa postura não deve ser copiada para autorização, KMS, identidade imutável ou scanner, pois estes são gates de segurança.

## Mapa de implementação candidato

| Proprietário | Superfícies | Responsabilidade |
|---|---|---|
| `AttachmentManagement` | API externa, domínio, persistência, S3/KMS/scanner, `Integration.V1` | identidade opaca, upload, identidade imutável, validação, liberação, claim, revogação, preservação, reconciliação e descarte |
| `Notifications` | comando REST/Kafka, hash, aceite, tentativa, fallback | referências e propriedades no hash, claim atômico, snapshot da tentativa, rechecagem da liberação e falha explícita |
| `Dispatch.Integration.*` | contrato publicado versionado | representação neutra do conteúdo e contrato explícito de ownership/disposal, sem S3, URL assinada ou estado de anexo |
| `Dispatch/SendGrid` | wire model, codificação e chamada HTTP | coleção de anexos, base64, cálculo do envelope, captura de resultado e sanitização |
| Validação transversal | Unit, Integration, SecurityArch, Performance, probes | regressão, isolamento, TOCTOU, falhas, vazamento, contrato do provedor e orçamento de runtime |

## Plano de verificação

1. Golden tests do hash vigente sem anexos e matriz canônica com anexos.
2. Testes de autorização REST e Kafka cruzando principal, `application` e referência.
3. Testes de máquina de estados e scanner com resultados limpo, hostil, indisponível, inconclusivo e não inspecionável.
4. Testes de corrida determinísticos com barreiras em liberação, claim, revogação, descarte e submissão.
5. Integração S3 em ambiente descartável, incluindo substituição sob a mesma chave, geração fixa, cancelamento e falha de leitura.
6. Integração KMS simulada ou descartável para negação, indisponibilidade e ausência de fallback.
7. Fake SendGrid capturando JSON, conjunto, nome, tipo e base64. Decodificar cada item e comparar digest e comprimento.
8. Matriz de 202, 4xx, 429, 5xx, timeout, rede e circuito, preservando o mesmo manifesto. Os testes atuais já cobrem forma, autenticação e classificação básica (`SendGridProviderContractTests.cs:14-43`, `:45-117`).
9. Varredura de Kafka, SQS, outbox, dead-letter, logs, traces, métricas, respostas e auditoria com sentinelas.
10. Sonda comparativa buffer, streaming e spool com falha e cancelamento em cada fase.
11. Suíte completa do baseline sem anexos e fitness functions de dependência modular.

## Decisões que exigem ADR ou desenho inline

- Identidade imutável S3: versionamento, Object Lock, chave content-addressed ou combinação, incluindo o ponto de cálculo do digest.
- KMS, IAM, política de chave, contexto criptográfico e comportamento de rotação.
- Scanner, tipos aceitos, tratamento de estruturas protegidas, política de validade e revalidação.
- Autorização por `application` no REST e semântica de freshness/revogação do registro Kafka.
- Transação compartilhada contra reserva idempotente com compensação.
- Contrato `Dispatch` aditivo em V1 contra V2 coexistente. A fitness function atual reconhece apenas `Integration.V1` (`ArchitectureTests.cs:23`, `:95-134`).
- Ownership e descarte de stream ou spool entre módulos.
- Buffer, streaming ou spool, após medição.
- Fonte contratual e configuração do envelope SendGrid.
- Ponto linearizável da revogação durante uma chamada já iniciada.
- Submissão ambígua após timeout, pois o adaptador não repete diretamente um POST não idempotente (`Dispatch/AGENTS.md:242-248`).
- Limites de tamanho, quantidade, concorrência, backlog e armazenamento.
- Retenção e descarte, inclusive evidência suficiente sem preservar conteúdo além do necessário.
- SLOs, alertas e orçamento de capacidade.

## Registro de diagnóstico

- `dotnet --version`, Windows local, `C:\projects\montebravo\mb-notification-hub`, exit `0`, saída `10.0.302`, sem artefato.
- Inspeções `rg -n` e `Get-Content` somente leitura, sem artefatos.
- Uma consulta auxiliar de contagem falhou com `ParserError` de PowerShell e foi repetida com sintaxe corrigida; não afetou a evidência.
- Nenhum build, restore, perfil, trace, dump, stress ou acesso de rede foi executado.

`DOTNET_SPECIALIST_SPECIFICATION: PARTIAL`

Justificativa: os invariantes de segurança, fronteiras, lacunas do código atual, falhas obrigatórias e plano de verificação estão sustentados. A escolha de transferência e os requisitos quantitativos permanecem sem envelope contratual, limites operacionais e medições, portanto não admitem confirmação técnica ainda.
