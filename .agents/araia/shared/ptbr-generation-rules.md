---
language: pt-BR
---

# Regras para geração de artefatos em PT-BR

Restrições aplicadas **durante** a geração. Passagem única, sem revisão completa posterior (uma autoverificação direcionada ao final; consulte Diacríticos).

Escopo: qualquer artefato gerado em português brasileiro (documentos, relatórios, especificações de Delivery Slice, mensagens de commit, nomes de testes, documentação XML, logs e mensagens de erro).

## Consistência integral do idioma

Todo o conteúdo narrativo deve estar em português brasileiro: título do
documento, cabeçalhos H1-H6, rótulos de metadados, cabeçalhos de tabelas,
legendas, chamadas, itens de navegação, listas, instruções e parágrafos. Não
preserve títulos ou trechos em inglês apenas porque vieram de um modelo.

Traduza o significado completo e mantenha a terminologia técnica permitida
neste arquivo. Preserve sem tradução somente identificadores, caminhos,
comandos, campos de `frontmatter`, nomes de estágios, valores de `lifecycle`,
valores de vocabulário controlado, siglas e termos técnicos explicitamente
mantidos em inglês. Uma frase predominantemente em inglês com alguns conectivos
em português continua sendo uma violação.

Valor de vocabulário controlado é aquele que um gate, um validador ou outro
artefato compara literalmente. O caso mais comum é a linha de status de um
artefato: escreva `**Status**: PROPOSED` e nunca `**Status**: PROPOSTO`, porque
a regra de status do G2 faz substituição literal de `PROPOSED` e `DRAFT` por
`APPROVED`. Um status traduzido não casa, a substituição atualiza zero arquivos
e o artefato aprovado permanece marcado como proposto. A mesma preservação vale
para `ACCEPTED`, `REJECTED`, `SUPERSEDED`, `DEPRECATED` e `IN REVIEW`. O rótulo
ao redor do valor continua em português; só o token comparado permanece
canônico.

Exemplos de localização obrigatória:

| Inglês no modelo | PT-BR no documento |
|---|---|
| Executive Summary | Sumário executivo |
| Decision Drivers | Direcionadores da decisão |
| Alternatives Considered | Alternativas consideradas |
| Rollout Strategy | Estratégia de rollout |
| Approval Record | Registro de aprovação |
| Change History | Histórico de alterações |

## Diacríticos (obrigatórios)

Use todos os diacríticos em cada palavra que os exija segundo o Acordo Ortográfico de 1990 (em vigor no Brasil desde 2009): ç, ã, õ, á, é, í, ó, ú, â, ê, ô, à.

**A regra é genérica**: toda palavra que contenha um diacrítico na ortografia padrão do português brasileiro DEVE aparecer com esse diacrítico. A lista abaixo é um conjunto não exaustivo de palavras com alta incidência de erros; a ausência na lista NÃO isenta uma palavra da regra.

Palavras com alta incidência de erros que devem ser escritas corretamente na primeira vez:

- Substantivos com -ção/-são/-são: ação, função, operação, configuração, requisição, validação, integração, autenticação, autorização, execução, implementação, definição, descrição, correção, exceção, conexão, remoção, decisão, cotação, persistência, manutenção, referência, dependência, transação, sessão, versão, revisão, inclusão, exclusão, alteração, redução
- Substantivos proparoxítonos com acento: código, número, único, próximo, próprio, página, diário, índice, métrica, lógica, fórmula, método, cenário, canônico, automático, estático, dinâmico, semântico, sintático, parâmetro, critério, requisito, histórico, periódico
- Substantivos com -ão/-ã/-õe: padrão, mão, razão, mensagem, ordenação, padrão, opção, versão
- Acentos diferenciais e oxítonas: após, três, será, está, é, há, já, só, pôr, pré, pós, têm, vêm
- Outros frequentes: análise, usuário, preço, força, serviço, espaço, exercício, início, ínicio (não), início, série, território, sistêmico, Resolução, hipótese, hipóteses, característica, característico

**Autoverificação direcionada** antes da entrega: examine o artefato em busca de qualquer palavra que deva conter um diacrítico segundo a ortografia padrão (não apenas as palavras listadas acima). Corrija todas as ocorrências sem acento. A lista é um auxílio à memória, não uma lista de permissão exaustiva.

**Dois padrões detectam a maioria das omissões; aplique-os durante a escrita, não depois:**

1. **Regra dos sufixos (quase sem exceções)**: todo substantivo terminado por elementos da família `-ção` / `-ções` / `-são` / `-sões` SEMPRE recebe acento. Uma palavra prestes a ser escrita com a terminação `-cao`, `-coes`, `-sao` ou `-soes` está errada: `configuracao` -> `configuração`, `versao` -> `versão`, `opcoes` -> `opções`, `permissoes` -> `permissões`. As únicas terminações `-ao` sem acento são palavras nativas curtas que, por sua vez, são acentuadas (`mao`/`pao`/`chao` -> `mão`/`pão`/`chão`).
2. **Regra das proparoxítonas**: palavras cuja sílaba tônica é a antepenúltima SEMPRE recebem acento: `codigo` -> `código`, `parametro` -> `parâmetro`, `automatico` -> `automático`, `unico` -> `único`, `pagina` -> `página`.

Essa regra é aplicada de forma determinística; portanto, escreva com acento desde a primeira vez. Um hook `PostToolUse` executa `framework/scripts/check-writing-rules.py` em cada arquivo escrito: nos arquivos de código-fonte, ele corrige automaticamente e no próprio local os diacríticos de alta confiança em comentários e literais de string (sem nunca alterar identificadores) e relata o que não pôde corrigir com segurança; em Markdown, relata as ocorrências com os números das linhas. O hook é a rede de segurança, não um substituto para a disciplina da passagem única.

## Pontuação

- Travessão `—`: permitido SOMENTE para iniciar uma fala em uma linha de diálogo (`— Bom dia, disse ela.`). Nunca o use para intercalar uma oração ou separar orações; nunca use a meia-risca `–` nem a barra horizontal `―`. Para trechos intercalados, use vírgulas ou parênteses; para uma ruptura forte, use ponto-final, dois-pontos ou ponto e vírgula.
- ` -- ` (hífen duplo com espaços) e ` - ` (hífen isolado com espaços): nunca os use. Nenhum deles substitui o travessão nem qualquer outro sinal de pontuação.
- Vírgulas: separam itens de listas; aparecem após advérbios de transição no início da frase ("Portanto,", "Além disso,", "No entanto,"); isolam apostos. **Nunca** aparecem entre o sujeito e o verbo.
- Pontos-finais encerram frases declarativas; mantenha a consistência nas listas (todos os itens com ponto ou nenhum com ponto).
- Dois-pontos introduzem listas, explicações e exemplos; use letra minúscula depois deles, exceto para nomes próprios.
- Pontos e vírgulas separam itens de listas complexas (itens que contêm vírgulas) ou orações independentes relacionadas.
- Aspas: "duplas" para citações diretas, 'simples' para citações aninhadas.
- Reticências: use exatamente três pontos (...), sem espaço antes e com moderação.

## Termos técnicos em inglês

Muitos termos de engenharia de software são empréstimos linguísticos sem equivalente natural em português brasileiro. Por padrão, mantenha-os em inglês.

**Mantenha em inglês** (não traduza):

- Controle de versão: commit, push, pull, merge, branch, fork, rebase, stash, tag, release
- Infraestrutura e entrega: deploy, pipeline, build, rollback, cluster, container, pod, namespace
- Arquitetura: backend, frontend, middleware, payload, endpoint, gateway, cache, proxy, handler, hook, wrapper, scaffold
- Camadas da arquitetura (nomes de camadas na base de código): Domain, Infra, Infrastructure, Service, Api, Application, Presentation, Core, Shared. Nunca traduza esses nomes quando se referirem a camadas ou namespaces do projeto.
- Dados e mensageria: stream, queue, topic, offset, consumer, producer, broker
- Testes: mock, stub, fixture, assertion, coverage, snapshot
- Processo: sprint, backlog, ticket, roadmap, stakeholder, checklist
- **Identificadores de código** (nomes de classes, métodos, arquivos, interfaces e propriedades) que aparecem em linha no texto: preserve cada byte, mesmo quando a palavra existir em português brasileiro. Não adicione diacríticos aos identificadores (`ListDictionary` permanece `ListDictionary`, nunca `ListDicionário`).

**Traduza** quando houver um equivalente claro e natural em português brasileiro:

| Inglês | PT-BR |
|---|---|
| user | usuário |
| account | conta |
| password | senha |
| settings | configurações |
| dashboard | painel |
| report | relatório |
| invoice | fatura / nota fiscal |
| order | pedido |

**Formatação em textos em português brasileiro**:

- Escreva termos em inglês com letra minúscula, exceto nomes próprios e siglas (deploy, pipeline, OAuth, REST).
- Não use itálico em termos em inglês (exceto na primeira apresentação da definição).
- Não flexione termos em inglês segundo as regras do português brasileiro; reformule a frase. Exceção: termos totalmente incorporados ao vernáculo ("o build quebrou", "o commit foi revertido").

## Linguagem técnica natural (evite calques literais)

Traduza o conceito, não os morfemas. Uma palavra construída parte por parte a partir de um termo em inglês pode ser gramaticalmente válida em português brasileiro e, ainda assim, estar errada, pois nenhum engenheiro se expressa dessa forma. Esse é o defeito mais frequente em textos gerados para revisão de código: o texto passa pelas verificações de diacríticos e pontuação, mas ainda parece produzido por uma máquina. A regra aplica-se igualmente aos apontamentos do revisor, ao relatório consolidado e aos comentários do PR.

Prefira o verbo idiomático que um engenheiro brasileiro realmente usa:

| Conceito (EN) | Evite (calque) | Prefira |
|---|---|---|
| re-wrap an exception | reembrulhar | capturar e relançar; reencapsular; converter ... em |
| let an exception through | deixar passar | propagar; repassar |
| collapse a 424 into a 500 | colapsar | converter; rebaixar |
| swallow an exception | (varia) | suprimir; descartar silenciosamente |

A tabela é ilustrativa, não exaustiva. Heurística: leia a frase em voz alta; se você não a formularia dessa maneira em uma discussão de PR ou em um documento de projeto técnico, reescreva-a com o verbo que usaria. Mantenha o substantivo em inglês entre crases quando ele for um identificador de código ou um empréstimo linguístico incorporado (`wrapper`, `handler`); a regra se aplica a verbos inventados por calque, não a empréstimos linguísticos legítimos.

## Por que usar passagem única

A maioria das restrições (pontuação, seleção de termos e voz) envolve decisões tomadas no momento da escrita. Uma revisão posterior apenas produz alterações desnecessárias. A única exceção são os diacríticos: empiricamente, essa é a restrição com maior incidência de erros, por isso se justifica uma pequena autoverificação direcionada à lista de palavras com alta incidência de erros apresentada acima. Não estenda a autoverificação ao artefato inteiro.

A passagem única é a disciplina, não a única proteção. Uma verificação por unidade logo após cada escrita, junto com o hook do mecanismo `Write`/`Edit`, detecta deslizes de forma cirúrgica e restrita à unidade recém-escrita (a autoverificação de diacríticos é uma dessas verificações), sem nunca reler o artefato inteiro. Consulte `post-write-language-enforcement.md`.
