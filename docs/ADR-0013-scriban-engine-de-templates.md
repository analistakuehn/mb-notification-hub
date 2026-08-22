# ADR-0013: Scriban como engine de templates

| | |
|---|---|
| **Status** | Proposta |
| **Data** | 2026-08-22 |
| **Decisores** | Arquitetura, Segurança da Informação |
| **Consultados** | Produto, Compliance |
| **Relacionadas** | ADR-0005 (templates como dados) |
| **Documento-mãe** | Design de Sistema, §4.3 "Template Management" |

## Contexto e problema

A ADR-0005 definiu templates como dados geridos pelo hub: o texto é gerido via API pelos seus donos (Produto e Compliance), validado no `validate` e no `publish` e renderizado pelo runtime com a mesma engine. Isso torna a engine de templates uma fronteira de segurança: quem escreve o template é usuário privilegiado, mas não é desenvolvedor, e o conteúdo renderiza no caminho quente do pipeline com as variáveis da notificação. Um template não pode alcançar tipos .NET (SSTI), não pode travar o worker (laço infinito, recursão descontrolada) e precisa de uma sintaxe que Produto e Compliance escrevam sem depender de engenharia.

## Fatores de decisão

- **Sandbox nativo**: o template só enxerga dados; nenhum tipo .NET exposto.
- **Limites de execução nativos**: laço (`LoopLimit`) e recursão contidos pela própria engine.
- **Sintaxe acessível a não desenvolvedores**: os donos do texto são Produto e Compliance (ADR-0005).
- **Performance**: o render roda no caminho quente do Core e precisa caber no orçamento do estágio.

## Opções consideradas

1. **Scriban** (escolhida).
2. Fluid (dialeto Liquid).
3. Handlebars.Net.
4. Razor.

## Decisão

Adotar o Scriban como engine única de templates, na validação (ADR-0005) e no runtime.

- **Sandbox**: o render recebe somente um `ScriptObject` populado com dados (variáveis da notificação e campos permitidos do contexto). Nenhum tipo .NET é exposto ao template.
- **Limites nativos**: `LoopLimit` e limite de recursão configurados na engine.
- **Timeout de parede**: não é nativo do Scriban e é imposto externamente: o render executa em task com timeout e, estourado o prazo, o resultado é descartado. Complemento na validação: limite de tamanho de template.
- **Pendência registrada**: esta ADR não fixa a versão do pacote Scriban nem os valores numéricos dos limites; ambos serão verificados e fixados na aceitação da ADR.

### Consequências

**Positivas**
- SSTI mitigada por construção: sem tipos .NET no escopo do template, a classe de ataque perde a superfície principal.
- Template patológico (laço infinito, recursão profunda) é contido pela própria engine, sem infraestrutura adicional.
- Sintaxe de chaves duplas com condicionais e filtros, legível para Produto e Compliance.

**Negativas**
- **Lock-in de sintaxe nos templates governados**: todo o catálogo fica escrito em Scriban; trocar de engine exigiria migrar e reaprovar cada versão publicada (a aprovação é sobre `content_hash`, ADR-0005), custo que cresce com o catálogo.
- **Timeout de parede não nativo**: o controle de tempo total é responsabilidade do hub (render em task com timeout e descarte do resultado). O descarte não interrompe a execução em andamento; quem garante terminação são os limites nativos e o limite de tamanho de template.

## Prós e contras das opções

### Opção 1: Scriban
- Prós: sandbox por exposição explícita de dados via `ScriptObject`; `LoopLimit` e limite de recursão nativos; sintaxe simples para não desenvolvedores; desempenho compatível com o caminho quente, a confirmar no teste de carga (critério abaixo).
- Contras: timeout de parede externo; lock-in de sintaxe.

### Opção 2: Fluid (dialeto Liquid)
- Prós: dialeto Liquid difundido (autores vindos de plataformas de e-commerce o reconhecem); modelo de acesso restrito por registro explícito de tipos.
- Contras: linguagem deliberadamente mais limitada; transformações além do conjunto Liquid tendem a virar filtro registrado em código, deslocando apresentação para deploy, exatamente o que a ADR-0005 quer evitar.

### Opção 3: Handlebars.Net
- Prós: sintaxe mustache amplamente conhecida; o estilo logic-less reduz o que um autor pode errar.
- Contras: logic-less empurra condicionais e formatações para helpers registrados em código (mesmo problema da opção 2, agravado); controles equivalentes a limite de laço e de recursão teriam de ser construídos fora da engine.

### Opção 4: Razor
- Prós: expressividade máxima; familiar a qualquer desenvolvedor .NET.
- Contras: o template é C# com acesso ao runtime .NET, SSTI por construção quando o autor não é tecnicamente confiável; sandbox exigiria isolamento de processo; sintaxe hostil a não desenvolvedores; compilação por template. Eliminada pelos dois primeiros fatores de decisão.

## Como saberemos que foi a decisão certa

- O teste de SSTI do catálogo de segurança passa: payloads de template maliciosos não alcançam tipos .NET nem derrubam o worker (laço e recursão interrompidos pelos limites da engine).
- p95 de render dentro do orçamento do estágio, medido no teste de carga.
- Autores publicam template novo sem deploy e sem intervenção de Engenharia (proxy do fator sintaxe, observado na retrospectiva de fase).

## Referências

- Design de Sistema, §4.3 "Template Management".
- ADR-0005 (templates, layouts e políticas como dados geridos pelo hub).
