---
language: pt-BR
---

# Segurança

[Voltar ao índice](00-index.md)

## Resultado

`FINDINGS`

## SEC-001: a correlação SendGrid pode usar identificadores de query não assinados

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `Security`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `49`
- `evidence`: O documento define correlação SendGrid em `custom_args`, dentro do corpo assinado. O endpoint também aceita `notificationId` e `attemptId` pela query para qualquer provedor. A assinatura SendGrid cobre timestamp e corpo, não a URL; quando o corpo não contém correlação, o handler usa a query. A resolução por correlação valida o par de IDs, mas não valida `ProviderKey` ou `ProviderMessageId` contra o attempt.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: Um callback SendGrid autenticamente assinado e sem `custom_args` pode ser associado a um attempt diferente por meio da query, alterando estado, fallback e efeitos de supressão.
- `recommendation`: Aceitar correlação de rota apenas para provedores cuja assinatura cubra a URL. Para SendGrid, exigir correlação no corpo e validar provedor e identidade da mensagem ao resolver o attempt.
- `verification`: Enviar corpo SendGrid validamente assinado sem `custom_args`, com query apontando para outro attempt. O callback deve ser recusado sem mudança de estado.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-architect`

## SEC-002: o callback Twilio não possui a janela temporal de replay declarada

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `Security`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `47`
- `evidence`: O documento declara proteção contra replay por `provider_event_id` mais janela de timestamp. `TwilioWebhookOptions` registra que notificações de status por callback não carregam timestamp, e `TwilioWebhookInterpreter` aplica a janela somente quando o campo existe. A deduplicação por evento é removida depois de 30 dias.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: Um callback Twilio assinado e capturado não possui prova de frescor na aplicação e pode voltar a ser aceito depois da retenção da marca. A máquina de estados reduz alguns efeitos, mas esse não é o controle declarado.
- `recommendation`: Documentar a limitação específica da Twilio e o risco residual. Manter a identidade de replay pelo horizonte do risco ou introduzir token de callback não reutilizável e vinculado ao attempt.
- `verification`: Reutilizar um callback assinado sem timestamp antes e depois da expiração da deduplicação. Ele deve permanecer incapaz de gravar nova evidência ou novo efeito.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-architect`, `dotnet-engineer`, `dotnet-specialist`

## SEC-003: o relatório mensal concluído omite as ativações de PIM prometidas

- `severity`: `HIGH`
- `confidence`: `HIGH`
- `lens`: `Security`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `86`
- `evidence`: A linha 86 inclui DLQs, falhas de provedor e ativações de PIM no relatório. A linha 176 marca a entrega como concluída, e a linha 201 reconhece apenas as duas primeiras ausências. `MonthlyEvidenceComposition` define `DeadLetterQueues`, `ProviderFailures` e `PrivilegedAccessActivations` como `null`; os testes confirmam a ausência das três, e a unidade I2 não declara uma fonte de PIM.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: Engenharia e Compliance podem aceitar como concluído um pacote mensal sem evidência de acesso privilegiado, comprometendo completude de auditoria e investigação de mudanças administrativas.
- `recommendation`: Integrar uma fonte oficial de ativações de PIM e arquivar a evidência de forma imutável, ou marcar o relatório como parcial e bloquear o estado de conclusão até a dependência existir.
- `verification`: Gerar um relatório com ativações controladas e validar identidade, aprovador, janela temporal e integridade no armazenamento WORM. Alternativamente, o documento precisa declarar explicitamente o estado parcial e as três seções ausentes.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-architect`, `dotnet-engineer`, `dotnet-specialist`
- `dissent`: O `dotnet-architect` classificou a causa como `HIGH` em Segurança; o `dotnet-engineer` usou `MEDIUM` em Engenharia; o `dotnet-specialist` usou `MEDIUM` em Arquitetura. A consolidação preservou a maior severidade sustentada e a lente do impacto dominante sobre evidência de acesso privilegiado.
