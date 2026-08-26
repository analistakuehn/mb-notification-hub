---
language: pt-BR
lens: PRF
lens-name: Performance
scope: TemplateManagement
source-revision: cc754e5a56dcbe1cae9cf05cf9303f7b0eb189d2
findings: 1
---

# Lente `PRF`: Performance

Caminhos quentes, alocações, I/O, concorrência, latência e crescimento de
consultas.

Não houve benchmark, perfil ou telemetria de produção. O achado demonstra um
mecanismo de crescimento, mas preserva severidade LOW e confiança média.

---

## Achados desta lente

Ordenados por estado: o que ainda exige ação fica no fim.

| Achado | Severidade | Estado | Assunto |
|---|---|---|---|
| `PRF-001` | `LOW` | **RESOLVIDO** | As consultas administrativas materializam todo o histórico de versões |

---
## `PRF-001` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `LOW` |
| confiança | média |
| arquivo | `Features/Queries/GetTemplate/GetTemplate.Handler.cs` |
| linha | `32-44` |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect e dotnet-engineer |
| dissenso | dotnet-specialist registrou NO-FINDING por ausência de medição |
| **estado** | **RESOLVIDO** |
| nota de estado | Fechado em duas etapas. Primeira: o custo dominante foi eliminado, mas por um mecanismo diferente do que o achado descreve. A verificação mostrou que `Contents` é `OwnsMany` e o EF carrega a coleção owned junto com a entidade, então para cinco colunas escalares por versão a consulta puxava todo o conteúdo autorado de todas as versões, com 512.000 caracteres por entrada de canal e locale. O peso não estava na contagem de linhas, estava na carga por linha. Corrigido por projeção no banco. Segunda: o teto de linhas entrou como janela **descendente** de 200 com continuação por cursor, decidido em mesa técnica com medição executada. O dissenso do specialist, que emitiu `NO-FINDING` por ausência de medição, foi tratado pela própria medição e está resolvido na ficha abaixo. |

**As consultas administrativas materializam todo o histórico de versões.**

Evidência:

    List<TemplateVersion> versions = await dbContext.TemplateVersions
        .AsNoTracking()
        .WhereTemplateKey(templateKey.Value!)
        .OrderBy(version => version.Version)
        .ToListAsync(cancellationToken);

GetLayout.Handler.cs repete o mesmo padrão. Não existe limite, cursor ou teto
de versões, e versões superseded são preservadas.

Impacto: o custo da consulta, da alocação e do payload cresce linearmente com o
histórico da identidade. O código demonstra o mecanismo, mas o snapshot não
fornece cardinalidade de produção nem medição de latência.

Recomendação: paginar os resumos por chave, com limite máximo explícito e cursor
estável pelo número da versão.

Verificação: popular 10.000 versões e confirmar que a consulta contém LIMIT,
que cada resposta respeita o teto e que a paginação não repete nem omite
versões.

### Como foi fechado, e onde o achado errou

A recomendação acima foi aplicada, com uma correção que a medição impôs. O
achado pede cursor pelo número da versão, mas não diz o sentido, e o handler
ordenava de forma crescente. Aplicar o teto sobre essa ordenação produz uma
resposta **errada**: a numeração é monotônica e o rollback clona para um número
maior, então `draft` e `published` vivem na cauda. Medido: `Take(51)` sobre a
ordenação crescente em 10.000 versões devolve as versões 1 a 51, sem nenhuma
publicada. A janela entregue é descendente, de 200, revertida em memória para
manter o array crescente, com `versionsTruncated` e `versionsNextCursor`
aditivos e o parâmetro de query `versionsCursor` para continuar.

O dissenso registrado nesta ficha está resolvido pela via que ele pedia. O
`NO-FINDING` do specialist estava **certo sobre o custo** e a medição o confirma:
o delta de latência fica dentro do ruído até 250 versões e só sai da banda em 500.
Estava **errado sobre a inexistência de mecanismo**, e é isso que sustenta a
correção: sem teto, a mesma consulta produziu três famílias de plano conforme a
população, incluindo `Sort` sobre `Bitmap Heap Scan` já em N=1; com teto, `Limit`
sobre `Index Scan` em todos os cenários. Some-se a travessia do Large Object Heap
na versão 8.193.

O detalhamento completo, com a mesa técnica, os números e o dissenso preservado
do engineer a favor de aceitar sem teto, está na ficha `PRF-008` da revisão
paralela em `docs/reviews/template-management/claude/1-performance.md`.
