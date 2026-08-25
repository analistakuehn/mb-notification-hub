---
language: pt-BR
---

# Teste

[Voltar ao índice](00-index.md)

## Resultado

`FINDINGS`

## TST-001: a assinatura SendGrid é testada com vetor gerado pela própria implementação

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `Test`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `203`
- `evidence`: A conclusão declara as fatias validadas, e a decomposição exige vetor fixo de assinatura de provedor. `SendGridWebhookInterpreterTests` gera sua própria chave ECDSA e assina o payload com as mesmas premissas que o código sob teste. Esse oráculo prova consistência interna, não interoperabilidade com um vetor independente do SendGrid.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: Uma divergência em formato de chave, codificação de assinatura ou composição dos bytes pode manter a suíte verde e recusar callbacks válidos em produção.
- `recommendation`: Adicionar vetor fixo independente, com chave pública, timestamp, corpo e assinatura fornecidos por fonte oficial ou ferramenta independente do helper testado.
- `verification`: O vetor fixo deve passar sem chamar o helper de assinatura do teste; alterar um byte do corpo ou do timestamp deve produzir `signature-invalid`.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-engineer`

## Verificado sem achado adicional nesta lente

Os testes versionados de fallback, scheduler, supressão, reconciliação e janela de silêncio possuem oráculos sobre estado e efeitos. A lacuna de cenário para circuito aberto foi consolidada na verificação de `ENG-001`, pois compartilha a mesma causa e localização.
