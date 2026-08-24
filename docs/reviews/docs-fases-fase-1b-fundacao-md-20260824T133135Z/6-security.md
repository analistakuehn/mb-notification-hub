# Segurança

[Voltar ao índice](00-index.md)

## SEC-001: identidade auto-declarada permite impersonação entre produtores Kafka

- `severity`: `HIGH`
- `confidence`: `HIGH`
- `lens`: `Security`
- `file`: `docs/fases/fase-1b-fundacao.md`
- `line`: `297`
- `evidence`: A pendência 53 registra que `producer` e `source` são auto-declarados, que o consumer não recebe a identidade autenticada do emissor e que qualquer principal com ACL de escrita pode assumir a identidade lógica mais privilegiada do `PRODUCER_REGISTRY`. Apesar disso, a linha 209 ainda resume ACL e registro como duas camadas de autorização, e B10 está concluída.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: Um produtor Kafka autorizado a escrever no tópico pode se declarar como outro produtor e obter permissões de aplicação ou classe que não lhe pertencem. Isso invalida o isolamento por produtor no barramento.
- `recommendation`: Antes do go-live Kafka, vincular a autorização a uma identidade autenticada propagada pelo broker ou isolar produtores por tópico e ACL. Como contenção transitória, permitir múltiplos escritores somente quando todos tiverem exatamente o mesmo teto de privilégios e transformar essa igualdade em gate verificável.
- `verification`: Com dois principais Kafka, uma mensagem do principal A declarando identidade, aplicação ou classe exclusiva de B deve ser recusada antes de qualquer efeito. A identidade própria de A deve ser aceita.
- `source-revision`: `3570c5e7ca761911330cace4b7dfdf1a8c5b2dbb`
- `reviewers`: `dotnet-architect`

## SEC-002: kill switch crítico sem entrega planejada

- `severity`: `HIGH`
- `confidence`: `HIGH`
- `lens`: `Security`
- `file`: `docs/fases/fase-1b-fundacao.md`
- `line`: `249`
- `evidence`: A linha 249 afirma que `KILL_SWITCH` integra o modelo de dados e a mecânica de segurança, mas nenhuma fase possui sua implementação. O design usa esse controle como contenção de produtor comprometido e como compensação para rate limit fail-open. No Kafka, o limite por principal não rejeita tráfego, deixando o kill switch e a revogação de ACL como mecanismos de parada.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: Um produtor comprometido pode continuar gerando tráfego, custo e mensagens abusivas até uma alteração externa de ACL. O design aceito depende de um controle de emergência sem entrega, validação ou prazo.
- `recommendation`: Tornar o kill switch uma fatia bloqueante anterior ao go-live Kafka, incluindo escopos, autorização, dupla confirmação quando aplicável, cache, auditoria, comportamento em falha e runbook de revogação de ACL.
- `verification`: Um teste de integração deve ativar o bloqueio de produtor, aplicação e canal, impedir novos despachos, preservar trabalho aceito para retomada e registrar cada transição na trilha. O roadmap deve marcar a entrega como concluída antes do go-live.
- `source-revision`: `3570c5e7ca761911330cace4b7dfdf1a8c5b2dbb`
- `reviewers`: `dotnet-architect`
