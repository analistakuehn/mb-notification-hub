---
language: pt-BR
lens: TST
lens-name: Test
scope: TemplateManagement
source-revision: cc754e5a56dcbe1cae9cf05cf9303f7b0eb189d2
findings: 10
---

# Lente `TST`: Test

Cobertura de comportamento, força do oráculo, isolamento, caminhos de falha,
risco de regressão e dialeto de teste. Um oráculo que não pode falhar é achado
desta lente mesmo com cobertura alta.

O tema que atravessa os dez achados: a suíte do módulo é ampla (32 arquivos
unitários, 27 de integração, 7.570 linhas) e tem exemplares de qualidade
notável, com premissa de falsificabilidade explícita e ordenação deliberada das
asserções. O problema não é volume, é direção: vários oráculos exercitam o
caminho seguro da propriedade que declaram verificar, e continuariam verdes com
o defeito presente.

---

## Achados desta lente

Ordenados por estado: o que ainda exige ação fica no fim.

| Achado | Severidade | Estado | Assunto |
|---|---|---|---|
| `TST-002` | `HIGH` | **RESOLVIDO** | Os testes de sandbox cobrem a família errada de fuga |
| `TST-001` | `HIGH` | **PARCIAL** | O oráculo do mascaramento não cobre o modo de falha que importa |
| `TST-003` | `HIGH` | **OBSOLETO** | O teste de deadline não pode falhar pela propriedade que o nome declara |
| `TST-004` | `MEDIUM` | **PENDENTE** | A regra mais forte do módulo tem oráculo para 2 dos 11 efeitos... |
| `TST-005` | `MEDIUM` | **PENDENTE** | Nenhum oráculo compara as duas superfícies de render |
| `TST-006` | `MEDIUM` | **PENDENTE** | Os testes de catálogo não populam o ponteiro antes da transição |
| `TST-007` | `MEDIUM` | **PENDENTE** | O comportamento de teto e evicção dos dois caches não tem oráculo algum |
| `TST-008` | `MEDIUM` | **PENDENTE** | Teste tautológico: afirma que SHA-256 é determinístico, não que o... |
| `TST-009` | `MEDIUM` | **PENDENTE** | O oráculo deriva a entrada da mesma constante que a produção compara |
| `TST-010` | `LOW` | **PENDENTE** | `ShouldBeOneOf` aceita duas guardas distintas, e não diz qual protegeu |

---
## `TST-001` · PARCIAL

| Campo | Valor |
|---|---|
| severidade | `HIGH` |
| confiança | alta |
| arquivo | `tests/Platform.UnitTests/TemplateManagement/VariableMaskingTests.cs` |
| linha | 9-42 |
| tipo-de-evidência | teste |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist (`HIGH`), dotnet-architect (`MEDIUM`), dotnet-engineer (`MEDIUM`) |
| **estado** | **PARCIAL** |
| nota de estado | O defeito que este oráculo deveria pegar passou a ser bloqueado na publicação, e há teste para isso em `SensitiveVariableValidationTests`. O oráculo pedido aqui, mascaramento sobre payload aninhado, NÃO foi escrito: a lacuna de teste permanece, embora o caminho para ela ficou mais estreito. |

**O oráculo do mascaramento não cobre o modo de falha que importa.**

Evidência, o único teste sobre estrutura aninhada, que exercita o caso seguro:

```csharp
JsonElement masked = VariableMasking.MaskSensitiveVariables(
    Variables("""{ "account": { "number": "12345-6", "holders": ["Ana", "Rui"], "note": null } }"""),
    ["account"])!.Value;
```

O nome sensível é o próprio contêiner e está no topo. O caso perigoso, nome
sensível **dentro** de um contêiner, não existe na suíte.

Impacto: o teste `A_payload_without_the_sensitive_variable_comes_back_unchanged`
afirma o comportamento correto para "a variável não foi enviada", e passa
igualmente no caso "a variável foi enviada aninhada e o mascaramento não
aconteceu", que é `SEC-001` e é um vazamento de dado pessoal. Um teste que passa
nos dois cenários não distingue o correto do incorreto, e é por isso que a
cobertura alta do arquivo não protege nada aqui. Uma mutação de `RequiresMasking`
para `return false` constante não quebra nenhum teste do arquivo.

Recomendação: acrescentar o caso `MaskSensitiveVariables(payload aninhado,
["number"])` e o correspondente `RequiresMasking`, mais um teste de contrato no
renderizador afirmando que a forma mascarada nunca coincide com a completa
quando o payload carrega valor sensível em qualquer profundidade.

Verificação: os testes novos devem falhar contra o código atual. Aplicar a
mutação de falsificação em `RequiresMasking` e confirmar que pelo menos um teste
quebra, o que hoje não acontece.

---

## `TST-002` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `HIGH` |
| confiança | alta |
| arquivo | `tests/Platform.UnitTests/TemplateManagement/ScribanTemplateEngineTests.cs` |
| linha | 32-201 |
| tipo-de-evidência | teste |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist |
| **estado** | **RESOLVIDO** |
| nota de estado | `ScribanSandboxTests` cobre exatamente a família que faltava: reavaliação de string como código, alocação por largura, carregamento de template externo, truncamento silencioso e vazamento de valor em mensagem de erro. Cada caso foi reproduzido contra o engine antes da correção, então nenhum teste ali confirma comportamento atual, todos falsificam uma fuga demonstrada. |

**Os testes de sandbox cobrem a família errada de fuga.**

Evidência: a suíte cobre coleta de variáveis, erro de parse, limite de tamanho,
acesso reflexivo, limite de laço, limite de recursão, deadline de parede, timeout
de expressão regular, teto de saída e variável não declarada. Nenhum teste
menciona `object.eval`, `object.eval_template`, `include`, `include_join` nem as
funções de alocação por largura.

Impacto: os testes modelam apenas as fugas por repetição (laço, recursão,
expressão regular, tamanho de saída). As três fugas realmente disponíveis nesta
versão do Scriban, verificadas na fonte do pacote, são de outra família:
reavaliação de string como código (`SEC-002`), alocação por largura fora do
caminho de saída (`SEC-010`) e carregamento de template externo. A cobertura alta
na dimensão errada é o que faz o sandbox parecer testado.

Recomendação: acrescentar um teste por superfície de builtin que o sandbox
pretende negar, mais um teste de regressão que enumere os membros visíveis ao
template e falhe quando um novo aparecer após atualização do pacote.

Verificação: cada teste novo deve falhar contra o código atual, antes das
correções de `SEC-002` e `SEC-010`.

---

## `TST-003` · OBSOLETO

| Campo | Valor |
|---|---|
| severidade | `HIGH` |
| confiança | alta |
| arquivo | `tests/Platform.UnitTests/TemplateManagement/ScribanTemplateEngineTests.cs` |
| linha | 135-145 |
| tipo-de-evidência | teste |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist |
| **estado** | **OBSOLETO** |
| nota de estado | A propriedade que este achado pede para verificar deixou de existir. A recomendação era observar que o trabalho abandonado para de progredir depois do retorno; com `Task.Run` removido, não há mais tarefa abandonada, e o render para nos checkpoints do próprio engine. O teste continua observando apenas o `Result`, e agora isso está certo, porque não há mais nada para observar. Não implemente a recomendação como escrita. |

**O teste de deadline não pode falhar pela propriedade que o nome declara.**

Evidência:

```csharp
[Fact]
public async Task A_render_over_the_wall_clock_limit_is_discarded()
{
    Result<string> result = await Engine(loopLimit: 10_000_000, timeoutMs: 1).RenderAsync(
        "{{ for i in 1..9000000 }}{{ i }}{{ end }}", variables: null, CancellationToken.None);

    result.IsFailure.ShouldBeTrue();
    result.Error!.ShouldContain("time limit");
}
```

Impacto: o nome do teste e o comentário de produção afirmam que o render em voo é
descartado. O oráculo só observa o `Result` que retorna ao chamador, e esse
`Result` é idêntico quer a thread do pool tenha parado, quer siga queimando CPU.
É o caso exato de oráculo que não pode falhar para a propriedade que declara
verificar, e essa propriedade é o núcleo de `PRF-001`. O teste passa hoje e
continuaria passando se o cancelamento fosse removido por completo.

Recomendação: dar ao oráculo poder de discriminação, usando uma fonte cujo
progresso seja observável (uma função registrada que incremente um contador por
iteração) e afirmando, depois do retorno, que o contador para de crescer dentro
de uma janela.

Verificação: mutação deliberada removendo o cancelamento deve fazer o teste
falhar. Hoje não faz.

---

## `TST-004` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `tests/Platform.IntegrationTests/TemplateManagement/AuditTrailGuaranteesTests.cs` |
| linha | 24 |
| tipo-de-evidência | teste |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado. Segue com oráculo para 2 dos 11 efeitos governados e sem imposição determinística. |

**A regra mais forte do módulo tem oráculo para 2 dos 11 efeitos governados.**

Evidência: o arquivo tem 5 fatos. Dois cobrem a atomicidade da trilha
(publicação de versão e publicação de política); os outros três cobrem os
gatilhos append-only da tabela, que são garantia do módulo `Audit`, não deste. O
`AGENTS.md` estende a regra a onze efeitos, e a busca por `BeginTransactionAsync`
confirma que os onze existem: criação de layout e de template, depreciação e
desativação de ambos, as três publicações e os dois rollbacks. Os testes de
arquitetura têm 8 fatos e nenhum deles trata de transação, auditoria ou ordem de
chamadas.

Impacto: a regra que sustenta o não repúdio da governança inteira tem oráculo
comportamental para 2 dos 11 caminhos e nenhuma imposição determinística. Os nove
restantes estão corretos hoje, verificados por leitura, mas nada impede a
regressão: um caso de uso novo, uma refatoração que mova o `SaveChangesAsync`
para fora da transação, ou que insira trabalho entre o append e o commit (o que
segura o lock da cadeia da partição por mais tempo), passa por toda a suíte
verde. Isto é distinto de um achado de arquitetura porque o código está certo: o
que falta é o oráculo que garanta que continue certo.

Recomendação: duas camadas. Primeira, um teste de arquitetura que percorra os
handlers de efeito governado e afirme uma única transação com o commit como
última chamada depois do append. Segunda, generalizar o teste de atomicidade
existente em teoria parametrizada sobre os onze efeitos, reaproveitando o mesmo
relógio congelado que já é o mecanismo de falha realista escolhido.

Verificação: a teoria de atomicidade lista onze casos e todos passam. O teste de
arquitetura falha ao se introduzir deliberadamente um `SaveChangesAsync` fora da
transação em qualquer handler.

---

## `TST-005` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `tests/Platform.IntegrationTests/TemplateManagement/SmsRenderNormalizationContractTests.cs` |
| linha | 46 |
| tipo-de-evidência | teste |
| introduzido-por-diff | `false` |
| revisores | dotnet-engineer |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado, e amarrado a `ENG-001`: o oráculo de equivalência só faz sentido depois que as duas superfícies compartilharem implementação. |

**Nenhum oráculo compara as duas superfícies de render.**

Evidência: o único teste de normalização de SMS atravessa apenas o renderizador
publicado, resolvendo `IPublishedTemplateRenderer` do container. Do lado do
preview, a suíte de endpoint cobre substituição, cadeia de locale, ausência de
conteúdo, versão inexistente, allowlist de URL e variável ausente, e nenhum de
seus nove casos usa conteúdo com caractere de controle, quebra de linha ou
acento decomposto, nem propósito de autenticação. O caso mais próximo usa texto
que já está na forma normalizada e portanto não distingue os dois caminhos.

Impacto: a divergência de `ENG-001` existe justamente porque nenhum oráculo
compara as duas superfícies. O teste de normalização é exemplar no seu escopo,
inclusive com premissa de falsificabilidade explícita, mas esse escopo termina no
contrato publicado. Como consequência, a diferença de comportamento entre preview
e produção pode crescer indefinidamente sem que teste algum reprove, e o autor
não tem como saber qual das duas respostas é a verdadeira.

Recomendação: acrescentar um teste de contrato que renderize a mesma versão pelas
duas superfícies com a mesma fonte e afirme igualdade dos campos rendidos. Um
teste desse formato ancora a equivalência como propriedade, em vez de duplicar
asserções sobre cada caminho, e passa a reprovar qualquer divergência futura.

Verificação: o teste deve falhar antes da unificação, com a diferença exata do
caractere de largura zero e da quebra de linha, e passar depois.

---

## `TST-006` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `tests/Platform.IntegrationTests/TemplateManagement/PublishedIntegrationContractTests.cs` |
| linha | 64-90 |
| tipo-de-evidência | teste |
| introduzido-por-diff | `false` |
| revisores | dotnet-engineer |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado. |

**Os testes de catálogo não populam o ponteiro antes da transição.**

Evidência: em ambos os testes de depreciação e desativação, o template é criado,
publicado e transicionado, e só então consultado pela primeira vez:

```csharp
disabled.EnsureSuccessStatusCode();

Result<PublishedTemplateLookup> lookup = await FindTemplateAsync("araia-cambio", key);
```

Nenhuma consulta antecede a transição, e a chave é sempre nova.

Impacto: por nunca popular o ponteiro antes da transição, o par de testes passa
de forma idêntica com invalidação de cache e sem ela, portanto não é oráculo da
propriedade que o nome sugere. A janela de até 60 segundos descrita em `SEC-011`
fica sem qualquer teste que a fixe, em qualquer direção: nem um teste que prove a
invalidação, nem um que documente e trave a obsolescência aceita. Uma regressão
que aumentasse o tempo de vida do ponteiro, ou uma correção que introduzisse
invalidação, passariam ambas despercebidas.

Recomendação: acrescentar um teste que consulte o catálogo antes da transição,
execute a desativação e consulte de novo, afirmando rejeição. Se a decisão de
negócio for manter a obsolescência, escrever o teste no sentido oposto, com
relógio controlado, afirmando o estado publicado dentro da janela e a rejeição
depois dela, para que a janela seja contrato explícito e não efeito colateral.

Verificação: o novo teste deve falhar no código atual. Confirmar que os dois
testes existentes continuam passando, o que mostra que cobrem outra coisa (a
leitura a frio) e devem ser mantidos.

---

## `TST-007` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `tests/Platform.UnitTests/TemplateManagement/PublishedReadMemoizationTests.cs` |
| linha | 18-76 |
| tipo-de-evidência | teste |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist, dotnet-architect |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado. |

**O comportamento de teto e evicção dos dois caches não tem oráculo algum.**

Evidência: a suíte cobre acerto de parse, não cacheamento de fonte com erro,
janela do ponteiro (59 segundos dentro, 61 fora) e não expiração da entrada
imutável. Nenhum teste exercita o limite de entradas nem a limpeza total, em
nenhum dos dois caches.

Impacto: o comportamento de teto é decisão de projeto declarada em comentário nos
dois caches e não tem oráculo. A limpeza total é justamente o que produz o efeito
manada de `PRF-007` e o despejo do conjunto quente publicado de `SEC-013`.
Somando-se a `TST-006`, nem o teto nem a janela de cegueira do interruptor de
desativação foram confrontados com a intenção declarada.

Recomendação: testar o teto e o despejo em ambos os caches, e acrescentar o caso
de chave com espaços em volta descrito em `PRF-003`.

Verificação: os testes de teto devem falhar se o limite de entradas for alterado
sem intenção.

---

## `TST-008` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `tests/Platform.UnitTests/TemplateManagement/VariableMaskingTests.cs` |
| linha | 138 |
| tipo-de-evidência | teste |
| introduzido-por-diff | `false` |
| revisores | dotnet-engineer |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado. O teste tautológico continua verde e continua sem exercitar `MaskSensitiveVariables`. |

**Teste tautológico: afirma que SHA-256 é determinístico, não que o mascaramento funciona.**

Evidência:

```csharp
public void Masking_changes_the_canonical_hash_of_the_rendered_fields()
{
    var fullHash = CanonicalHash.OfFields("Acesso", "Código 998877", null);
    var maskedHash = CanonicalHash.OfFields("Acesso", $"Código {VariableMasking.MaskedValue}", null);
    var repeatedFullHash = CanonicalHash.OfFields("Acesso", "Código 998877", null);

    maskedHash.ShouldNotBe(fullHash);
    repeatedFullHash.ShouldBe(fullHash);
}
```

Impacto: o teste não chama `MaskSensitiveVariables` nem `RequiresMasking`.
Compara duas cadeias literais escritas à mão, ou seja, afirma que SHA-256
distingue entradas distintas e é determinístico para entradas iguais. Isso é
propriedade da função de hash, não do mascaramento, e o teste continua verde com
qualquer defeito em `VariableMasking`, inclusive o de `SEC-001` ou uma
implementação que devolvesse o payload intacto. O nome promete cobertura que a
asserção não entrega, o que é pior do que ausência de teste, porque cria a
aparência de que a propriedade está ancorada em nível unitário. A propriedade
real só é verificada num teste de integração que depende de Docker e portanto não
roda no laço rápido.

Recomendação: reescrever partindo do payload e passando pelo mascaramento: montar
as duas formas a partir do resultado real de `MaskSensitiveVariables` e comparar
os hashes. Manter a asserção de determinismo, mas sobre valores produzidos pelo
código sob teste.

Verificação: aplicar a mutação que faz `MaskSensitiveVariables` devolver o
payload sem alteração. O teste reescrito deve falhar; o atual passa.

---

## `TST-009` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `tests/Platform.UnitTests/TemplateManagement/AuthenticationSmsLinkValidationTests.cs` |
| linha | 150 |
| tipo-de-evidência | teste |
| introduzido-por-diff | `false` |
| revisores | dotnet-engineer |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado, e amarrado a `SEC-007`: a teoria sobre variação de caixa só reprova depois que `purpose` for canonizado. |

**O oráculo deriva a entrada da mesma constante que a produção compara.**

Evidência:

```csharp
private static Template Authentication(IReadOnlyList<string>? linkDomains = null)
    => Template.Create(
        Key,
        Metadata(TemplateValidation.AuthenticationPurpose, linkDomains ?? [])).Value!;
```

Todos os casos positivos derivam da própria constante, e o único caso de
falsificação usa outra palavra, não outra caixa. A busca por `purpose` nos testes
retorna somente a forma minúscula.

Impacto: o oráculo é insensível por construção à classe de defeito descrita em
`SEC-007`. Como o valor de entrada e o valor comparado saem da mesma constante, a
comparação ordinal nunca é exercitada contra um valor que dela difira apenas na
caixa, e o conjunto inteiro de testes continuaria verde com o controle desligado
para toda variação de caixa. O teste que carrega o comentário "Falsification: the
purpose is what triggers the rule" falsifica somente a hipótese "palavra
diferente" e não a hipótese "mesma palavra, caixa diferente", que é a que está
aberta.

Recomendação: converter os casos de gatilho em `[Theory]` com as variações de
caixa e com espaços em volta, afirmando que todas reprovam a publicação de um SMS
com link. Escrever o teste antes da correção de `SEC-007`, para que ele nomeie o
dano em vez de confirmar o comportamento atual.

Verificação: rodar a nova `[Theory]` no código atual. As variações de caixa devem
falhar. Depois da canonização do propósito, todas passam.

---

## `TST-010` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `LOW` |
| confiança | alta |
| arquivo | `tests/Platform.IntegrationTests/TemplateManagement/ConcurrentLifecycleConflictTests.cs` |
| linha | 59 |
| tipo-de-evidência | teste |
| introduzido-por-diff | `false` |
| revisores | dotnet-engineer |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado. |

**`ShouldBeOneOf` aceita duas guardas distintas, e não diz qual protegeu.**

Evidência:

```csharp
DomainError.Describe(result.Error, result.ErrorKind).Code.ShouldBeOneOf(
    ErrorCodes.PreconditionFailed,
    ErrorCodes.PublicationConflict);
```

No teste irmão, o mesmo oráculo é preciso, com `ShouldBe` sobre um código único.

Impacto: a asserção aceita duas guardas distintas do handler: o token de
concorrência da linha do template e o índice único parcial de versão publicada.
Uma regressão que desativasse a primeira, por exemplo removendo a marcação
explícita de propriedade modificada, deslocaria o resultado para a segunda guarda
e o teste continuaria verde. Severidade `LOW` porque a asserção de estado que
segue é forte e específica (exatamente um publicado, número de versão exato,
origem do rollback exata) e capturaria a maior parte das regressões de
comportamento. O que se perde é a capacidade de dizer qual guarda protegeu, que é
o que o teste se propõe a demonstrar.

Recomendação: fixar o código esperado para o interleaving que o interceptor força
de modo determinístico e, se as duas guardas forem de fato alcançáveis nesse
ponto, dividir em dois testes com marcadores de lote distintos, cada um afirmando
um código único. O teste irmão mostra que o interleaving já é escolhido com essa
precisão.

Verificação: aplicar a mutação que remove a marcação de propriedade modificada no
rollback e confirmar que o teste dividido reprova. Hoje ele passa.
